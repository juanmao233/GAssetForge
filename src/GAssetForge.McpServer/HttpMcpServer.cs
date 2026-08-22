using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GAssetForge.McpServer;

internal sealed class HttpMcpServer(
    ServerOptions options,
    McpRequestDispatcher dispatcher,
    TimeProvider? timeProvider = null,
    TimeSpan? sessionTtl = null)
{
    private const long MaximumRequestBytes = 4L * 1024L * 1024L;
    private const string SessionHeaderName = "Mcp-Session-Id";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly McpSessionRegistry _sessions = new(timeProvider, sessionTtl);
    private readonly JsonRpcMessageHandler _messageHandler = new(dispatcher);
    private readonly object _healthSync = new();
    private readonly DateTimeOffset _startedAtUtc =
        (timeProvider ?? TimeProvider.System).GetUtcNow().ToUniversalTime();

    private long _totalHandshakes;
    private DateTimeOffset? _lastHandshakeUtc;
    private string? _lastClientName;
    private string? _lastClientVersion;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ApplicationName = typeof(HttpMcpServer).Assembly.FullName
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = MaximumRequestBytes;
            kestrel.Listen(options.HostAddress, options.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
            });
        });

        await using var app = builder.Build();
        app.MapGet("/health", HandleHealthAsync);
        app.MapPost("/mcp", HandlePostAsync);
        app.MapDelete("/mcp", HandleDeleteAsync);

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        Console.Error.WriteLine(
            $"[GAssetForge MCP] HTTP server started: {options.Endpoint} authentication=bearer expectedAgent={options.ExpectedAgent}");

        try
        {
            await app.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(stopTimeout.Token).ConfigureAwait(false);
            Console.Error.WriteLine("[GAssetForge MCP] HTTP server stopped.");
        }
    }

    private async Task HandleHealthAsync(HttpContext context)
    {
        if (!EnsureTrustedLocalRequest(context))
        {
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        PruneExpiredSessions();

        long totalHandshakes;
        DateTimeOffset? lastHandshakeUtc;
        string? lastClientName;
        string? lastClientVersion;
        lock (_healthSync)
        {
            totalHandshakes = _totalHandshakes;
            lastHandshakeUtc = _lastHandshakeUtc;
            lastClientName = _lastClientName;
            lastClientVersion = _lastClientVersion;
        }

        var activeSessions = _sessions.GetActiveSessions();
        var hasConnectedSession = activeSessions.Any(session => session.IsInitialized);
        await WriteJsonAsync(
                context,
                new
                {
                    status = hasConnectedSession ? "connected" : "waiting",
                    endpoint = options.Endpoint,
                    activeSessions = activeSessions.Length,
                    totalHandshakes,
                    lastHandshakeUtc,
                    lastClientName,
                    lastClientVersion,
                    expectedAgent = options.ExpectedAgent,
                    startedAtUtc = _startedAtUtc
                },
                StatusCodes.Status200OK)
            .ConfigureAwait(false);
    }

    private async Task HandlePostAsync(HttpContext context)
    {
        if (!EnsureTrustedLocalRequest(context))
        {
            return;
        }

        if (!EnsureAuthenticatedMcpRequest(context))
        {
            return;
        }

        PruneExpiredSessions();

        if (!IsJsonContentType(context.Request.ContentType))
        {
            await WriteJsonAsync(
                    context,
                    JsonRpcMessageHandler.ErrorResponse(
                        null,
                        JsonRpcErrorCodes.InvalidRequest,
                        "POST /mcp 需要 application/json 请求体。"),
                    StatusCodes.Status415UnsupportedMediaType)
                .ConfigureAwait(false);
            return;
        }

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(
                    context.Request.Body,
                    cancellationToken: context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine($"[GAssetForge MCP] HTTP JSON parse error: {exception.Message}");
            await WriteJsonAsync(
                    context,
                    JsonRpcMessageHandler.ErrorResponse(
                        null,
                        JsonRpcErrorCodes.ParseError,
                        "JSON 解析失败。"),
                    StatusCodes.Status400BadRequest)
                .ConfigureAwait(false);
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            var method = TryReadMethod(root);
            var suppliedSessionId = ReadSessionId(context.Request);

            if (string.Equals(method, "initialize", StringComparison.Ordinal))
            {
                if (suppliedSessionId is not null)
                {
                    await WriteJsonAsync(
                            context,
                            JsonRpcMessageHandler.ErrorResponse(
                                GetIdOrNull(root),
                                JsonRpcErrorCodes.InvalidRequest,
                                "initialize 请求不能携带已有的 Mcp-Session-Id。"),
                            StatusCodes.Status400BadRequest)
                        .ConfigureAwait(false);
                    return;
                }

                await HandleInitializeAsync(context, root).ConfigureAwait(false);
                return;
            }

            if (suppliedSessionId is null)
            {
                await WriteJsonAsync(
                        context,
                        JsonRpcMessageHandler.ErrorResponse(
                            GetIdOrNull(root),
                            JsonRpcErrorCodes.InvalidRequest,
                            "后续 MCP 请求必须携带 Mcp-Session-Id。"),
                        StatusCodes.Status400BadRequest)
                    .ConfigureAwait(false);
                return;
            }

            if (!_sessions.TryGetActive(suppliedSessionId, out var session))
            {
                await WriteJsonAsync(
                        context,
                        JsonRpcMessageHandler.ErrorResponse(
                            GetIdOrNull(root),
                            JsonRpcErrorCodes.InvalidRequest,
                            "MCP 会话不存在或已经结束。"),
                        StatusCodes.Status404NotFound)
                    .ConfigureAwait(false);
                return;
            }

            var result = await _messageHandler.HandleAsync(
                    root,
                    session!,
                    options.ExpectedAgent,
                    context.RequestAborted)
                .ConfigureAwait(false);

            if (result.Succeeded && session!.TryMarkHandshakeReported())
            {
                RecordHandshake(suppliedSessionId, session.Snapshot());
            }

            await WriteMessageResultAsync(context, result).ConfigureAwait(false);
        }
    }

    private async Task HandleInitializeAsync(HttpContext context, JsonElement root)
    {
        var session = _sessions.CreateSession();
        var result = await _messageHandler.HandleAsync(
                root,
                session,
                options.ExpectedAgent,
                context.RequestAborted)
            .ConfigureAwait(false);

        if (result.Succeeded && result.HasResponse)
        {
            var sessionId = _sessions.Add(session);

            context.Response.Headers[SessionHeaderName] = sessionId;
            var snapshot = session.Snapshot();
            Console.Error.WriteLine(
                $"[GAssetForge MCP] initialize accepted: session={sessionId} client={snapshot.ClientName ?? "unknown"} version={snapshot.ClientVersion ?? "unknown"}");
        }

        await WriteMessageResultAsync(context, result).ConfigureAwait(false);
    }

    private async Task HandleDeleteAsync(HttpContext context)
    {
        if (!EnsureTrustedLocalRequest(context))
        {
            return;
        }

        if (!EnsureAuthenticatedMcpRequest(context))
        {
            return;
        }

        PruneExpiredSessions();

        var sessionId = ReadSessionId(context.Request);
        if (sessionId is null)
        {
            await WriteJsonAsync(
                    context,
                    JsonRpcMessageHandler.ErrorResponse(
                        null,
                        JsonRpcErrorCodes.InvalidRequest,
                        "DELETE /mcp 必须携带 Mcp-Session-Id。"),
                    StatusCodes.Status400BadRequest)
                .ConfigureAwait(false);
            return;
        }

        if (!_sessions.Remove(sessionId))
        {
            await WriteJsonAsync(
                    context,
                    JsonRpcMessageHandler.ErrorResponse(
                        null,
                        JsonRpcErrorCodes.InvalidRequest,
                        "MCP 会话不存在或已经结束。"),
                    StatusCodes.Status404NotFound)
                .ConfigureAwait(false);
            return;
        }

        Console.Error.WriteLine($"[GAssetForge MCP] session closed: session={sessionId}");
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private void RecordHandshake(string sessionId, McpSessionSnapshot snapshot)
    {
        var handshakeUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        lock (_healthSync)
        {
            _totalHandshakes++;
            _lastHandshakeUtc = handshakeUtc;
            _lastClientName = snapshot.ClientName;
            _lastClientVersion = snapshot.ClientVersion;
        }

        Console.Error.WriteLine(
            $"[GAssetForge MCP] handshake completed: session={sessionId} client={snapshot.ClientName ?? "unknown"} version={snapshot.ClientVersion ?? "unknown"} protocol={snapshot.ProtocolVersion ?? "unknown"}");
    }

    private void PruneExpiredSessions()
    {
        var removed = _sessions.RemoveExpiredSessions();
        if (removed > 0)
        {
            Console.Error.WriteLine($"[GAssetForge MCP] expired inactive sessions removed: count={removed}");
        }
    }

    private static async Task WriteMessageResultAsync(HttpContext context, JsonRpcMessageResult result)
    {
        if (!result.HasResponse)
        {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }

        await WriteJsonAsync(context, result.Response!, StatusCodes.Status200OK).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(HttpContext context, object payload, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
                context.Response.Body,
                payload,
                payload.GetType(),
                ProtocolJson.Options,
                context.RequestAborted)
            .ConfigureAwait(false);
    }

    private static bool EnsureTrustedLocalRequest(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        var isLoopback = address is not null &&
                         (IPAddress.IsLoopback(address) ||
                          address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4()));
        if (isLoopback)
        {
            var host = context.Request.Host.Host;
            if (!IsLoopbackHost(host))
            {
                Console.Error.WriteLine($"[GAssetForge MCP] rejected non-loopback Host: {host}");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return false;
            }

            if (!context.Request.Headers.TryGetValue("Origin", out var origins))
            {
                return true;
            }

            var origin = origins.Count == 1 ? origins[0] : null;
            if (origin is not null &&
                Uri.TryCreate(origin, UriKind.Absolute, out var originUri) &&
                originUri.Scheme is "http" or "https" &&
                IsLoopbackHost(originUri.Host))
            {
                return true;
            }

            Console.Error.WriteLine($"[GAssetForge MCP] rejected non-loopback Origin: {origin ?? "<invalid>"}");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return false;
        }

        Console.Error.WriteLine($"[GAssetForge MCP] rejected non-loopback client: {address}");
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return false;
    }

    private bool EnsureAuthenticatedMcpRequest(HttpContext context)
    {
        var expectedToken = options.BearerToken;
        if (string.IsNullOrEmpty(expectedToken) ||
            !context.Request.Headers.TryGetValue("Authorization", out var values) ||
            values.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(values[0], out var authorization) ||
            !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(authorization.Parameter) ||
            authorization.Parameter.Any(char.IsWhiteSpace) ||
            !TokensEqual(expectedToken, authorization.Parameter))
        {
            context.Response.Headers["WWW-Authenticate"] = "Bearer";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return false;
        }

        return true;
    }

    private static bool TokensEqual(string expected, string supplied)
    {
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }

    private static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var candidate = host.Trim().Trim('[', ']');
        return IPAddress.TryParse(candidate, out var address) && IPAddress.IsLoopback(address);
    }

    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var mediaType = contentType.Split(';', 2)[0].Trim();
        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
               mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadSessionId(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(SessionHeaderName, out var values) || values.Count != 1)
        {
            return null;
        }

        var value = values[0]?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? TryReadMethod(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("method", out var method) &&
        method.ValueKind == JsonValueKind.String
            ? method.GetString()
            : null;

    private static JsonElement? GetIdOrNull(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var id)
            ? id.Clone()
            : null;
}

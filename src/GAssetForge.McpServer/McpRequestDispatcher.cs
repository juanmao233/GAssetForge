using System.Text.Json;

namespace GAssetForge.McpServer;

internal sealed class McpSessionState
{
    private readonly object _sync = new();
    private DateTimeOffset _lastActivityUtc;
    private bool _expired;
    private bool _initializeReceived;
    private bool _initialized;
    private string? _clientName;
    private string? _clientVersion;
    private string? _protocolVersion;
    private bool _handshakeReported;

    public McpSessionState(DateTimeOffset? initialActivityUtc = null)
    {
        _lastActivityUtc = (initialActivityUtc ?? TimeProvider.System.GetUtcNow()).ToUniversalTime();
    }

    public SemaphoreSlim RequestGate { get; } = new(1, 1);

    public bool IsInitialized
    {
        get
        {
            lock (_sync)
            {
                return _initialized;
            }
        }
    }

    public McpSessionSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new McpSessionSnapshot(
                _initializeReceived,
                _initialized,
                _clientName,
                _clientVersion,
                _protocolVersion);
        }
    }

    public void RecordInitialize(string protocolVersion, string? clientName, string? clientVersion)
    {
        lock (_sync)
        {
            if (_initializeReceived)
            {
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidRequest, "当前 MCP 会话已经执行过 initialize。");
            }

            _initializeReceived = true;
            _protocolVersion = protocolVersion;
            _clientName = clientName;
            _clientVersion = clientVersion;
        }
    }

    public bool CompleteInitialization()
    {
        lock (_sync)
        {
            if (!_initializeReceived)
            {
                throw new JsonRpcException(
                    JsonRpcErrorCodes.InvalidRequest,
                    "notifications/initialized 必须在 initialize 成功后发送。");
            }

            if (_initialized)
            {
                return false;
            }

            _initialized = true;
            return true;
        }
    }

    public bool TryMarkHandshakeReported()
    {
        lock (_sync)
        {
            if (!_initialized || _handshakeReported)
            {
                return false;
            }

            _handshakeReported = true;
            return true;
        }
    }

    public void RecordActivity(DateTimeOffset activityUtc)
    {
        lock (_sync)
        {
            if (_expired)
            {
                throw new InvalidOperationException("已经过期的 MCP 会话不能重新激活。");
            }

            var normalized = activityUtc.ToUniversalTime();
            if (normalized > _lastActivityUtc)
            {
                _lastActivityUtc = normalized;
            }
        }
    }

    public bool TryRecordActivity(DateTimeOffset activityUtc, TimeSpan ttl)
    {
        lock (_sync)
        {
            var normalized = activityUtc.ToUniversalTime();
            if (_expired || HasExpired(normalized, ttl))
            {
                _expired = true;
                return false;
            }

            if (normalized > _lastActivityUtc)
            {
                _lastActivityUtc = normalized;
            }

            return true;
        }
    }

    public bool TryExpire(DateTimeOffset nowUtc, TimeSpan ttl)
    {
        lock (_sync)
        {
            if (_expired)
            {
                return true;
            }

            if (!HasExpired(nowUtc.ToUniversalTime(), ttl))
            {
                return false;
            }

            _expired = true;
            return true;
        }
    }

    private bool HasExpired(DateTimeOffset nowUtc, TimeSpan ttl)
    {
        DateTimeOffset expiresAtUtc;
        try
        {
            expiresAtUtc = _lastActivityUtc.Add(ttl);
        }
        catch (ArgumentOutOfRangeException)
        {
            expiresAtUtc = DateTimeOffset.MaxValue;
        }

        return nowUtc >= expiresAtUtc;
    }
}

internal sealed record McpSessionSnapshot(
    bool InitializeReceived,
    bool IsInitialized,
    string? ClientName,
    string? ClientVersion,
    string? ProtocolVersion);

internal sealed class McpRequestDispatcher(McpToolCatalog toolCatalog)
{
    private const string LatestProtocolVersion = "2025-06-18";

    private static readonly HashSet<string> SupportedProtocolVersions =
    [
        "2024-11-05",
        "2025-03-26",
        LatestProtocolVersion
    ];

    public async Task<object?> DispatchAsync(
        string method,
        JsonElement parameters,
        McpSessionState session,
        string? expectedAgent,
        CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialize":
                var requestedVersion = ReadProtocolVersion(parameters);
                var negotiatedVersion = SupportedProtocolVersions.Contains(requestedVersion)
                    ? requestedVersion
                    : LatestProtocolVersion;
                var (clientName, clientVersion) = ReadClientInfo(parameters);
                ReportExpectedAgentDiagnostic(expectedAgent, clientName);
                session.RecordInitialize(negotiatedVersion, clientName, clientVersion);
                return new
                {
                    protocolVersion = negotiatedVersion,
                    capabilities = new
                    {
                        tools = new { listChanged = false }
                    },
                    serverInfo = new
                    {
                        name = "GAssetForge",
                        title = "GAssetForge AI Art Generation Queue",
                        version = "1.0.0"
                    },
                    instructions = "读取 get_current_context 获取桌面工作流约束；领取生成任务后，必须把产物保存到返回的有效 Assets 根目录内，再调用 complete_generation_task。"
                };

            case "notifications/initialized":
                session.CompleteInitialization();
                return null;

            case "ping":
                return new { };

            case "tools/list":
                EnsureInitialized(session);
                return toolCatalog.DescribeTools();

            case "tools/call":
                EnsureInitialized(session);
                if (parameters.ValueKind != JsonValueKind.Object ||
                    !parameters.TryGetProperty("name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(nameElement.GetString()))
                {
                    throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "tools/call 缺少工具名称。");
                }

                var arguments = parameters.TryGetProperty("arguments", out var argumentsElement)
                    ? argumentsElement
                    : JsonSerializer.SerializeToElement(new { });
                if (arguments.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "tools/call arguments 必须是对象。");
                }

                return await toolCatalog.CallAsync(nameElement.GetString()!, arguments, cancellationToken)
                    .ConfigureAwait(false);

            default:
                if (method.StartsWith("notifications/", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine($"[GAssetForge MCP] Ignored notification: {method}");
                    return null;
                }

                throw new JsonRpcException(JsonRpcErrorCodes.MethodNotFound, $"不支持的方法：{method}");
        }
    }

    private static void EnsureInitialized(McpSessionState session)
    {
        if (!session.IsInitialized)
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidRequest, "请先完成 initialize 与 notifications/initialized 握手。");
        }
    }

    private static string ReadProtocolVersion(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("protocolVersion", out var version) ||
            version.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(version.GetString()))
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "initialize 缺少 protocolVersion。");
        }

        return version.GetString()!;
    }

    private static (string? Name, string? Version) ReadClientInfo(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("clientInfo", out var clientInfo) ||
            clientInfo.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return (null, null);
        }

        if (clientInfo.ValueKind != JsonValueKind.Object)
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "initialize.clientInfo 必须是对象。");
        }

        var name = ReadOptionalClientInfoString(clientInfo, "name");
        var version = ReadOptionalClientInfoString(clientInfo, "version");
        return (name, version);
    }

    private static string? ReadOptionalClientInfoString(JsonElement clientInfo, string propertyName)
    {
        if (!clientInfo.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonRpcException(
                JsonRpcErrorCodes.InvalidParams,
                $"initialize.clientInfo.{propertyName} 必须是字符串。");
        }

        var result = value.GetString()?.Trim();
        return string.IsNullOrEmpty(result) ? null : result;
    }

    private static void ReportExpectedAgentDiagnostic(string? expectedAgent, string? clientName)
    {
        if (expectedAgent is null)
        {
            return;
        }

        if (clientName is null)
        {
            Console.Error.WriteLine(
                $"[GAssetForge MCP] expected-agent diagnostic: expected={expectedAgent} received=<missing>");
            return;
        }

        var normalizedExpected = NormalizeAgentName(expectedAgent);
        var normalizedClient = NormalizeAgentName(clientName);
        if (!normalizedClient.Contains(normalizedExpected, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[GAssetForge MCP] expected-agent diagnostic mismatch: expected={expectedAgent} received={clientName}");
        }
    }

    private static string NormalizeAgentName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

internal sealed record JsonRpcMessageResult(
    bool HasResponse,
    object? Response,
    string? Method,
    bool Succeeded);

internal sealed class JsonRpcMessageHandler(McpRequestDispatcher dispatcher)
{
    public async Task<JsonRpcMessageResult> HandleAsync(
        JsonElement root,
        McpSessionState session,
        string? expectedAgent,
        CancellationToken cancellationToken)
    {
        var id = GetIdOrNull(root);
        var hasId = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out _);
        string? method = null;

        try
        {
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("jsonrpc", out var jsonRpc) ||
                jsonRpc.ValueKind != JsonValueKind.String ||
                !string.Equals(jsonRpc.GetString(), "2.0", StringComparison.Ordinal) ||
                !root.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(methodElement.GetString()))
            {
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidRequest, "无效的 JSON-RPC 2.0 请求。");
            }

            method = methodElement.GetString()!;
            if (string.Equals(method, "initialize", StringComparison.Ordinal) && !hasId)
            {
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidRequest, "initialize 必须是带 id 的 JSON-RPC 请求。");
            }

            var parameters = root.TryGetProperty("params", out var paramsElement)
                ? paramsElement
                : default;

            await session.RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            object? result;
            try
            {
                result = await dispatcher.DispatchAsync(
                        method,
                        parameters,
                        session,
                        expectedAgent,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                session.RequestGate.Release();
            }

            return hasId
                ? new JsonRpcMessageResult(true, ResultResponse(id, result), method, true)
                : new JsonRpcMessageResult(false, null, method, true);
        }
        catch (JsonRpcException exception)
        {
            if (!hasId && method is not null)
            {
                Console.Error.WriteLine($"[GAssetForge MCP] Notification {method} rejected: {exception.Message}");
                return new JsonRpcMessageResult(false, null, method, false);
            }

            return new JsonRpcMessageResult(
                true,
                ErrorResponse(id, exception.Code, exception.Message, exception.ErrorData),
                method,
                false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[GAssetForge MCP] Internal error in {method ?? "unknown"}: {exception}");
            if (!hasId && method is not null)
            {
                return new JsonRpcMessageResult(false, null, method, false);
            }

            return new JsonRpcMessageResult(
                true,
                ErrorResponse(id, JsonRpcErrorCodes.InternalError, "服务器内部错误。"),
                method,
                false);
        }
    }

    public static object ErrorResponse(JsonElement? id, int code, string message, object? data = null) => new
    {
        jsonrpc = "2.0",
        id,
        error = new { code, message, data }
    };

    private static object ResultResponse(JsonElement? id, object? result) => new
    {
        jsonrpc = "2.0",
        id,
        result
    };

    private static JsonElement? GetIdOrNull(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var id)
            ? id.Clone()
            : null;
}

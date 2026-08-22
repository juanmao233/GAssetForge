using GAssetForge.App.Services;
using GAssetForge.McpServer;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GAssetForge.SmokeTests;

internal static class McpBearerAuthenticationSmokeTests
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "mcp-bearer-auth",
            Guid.NewGuid().ToString("N"));
        var paths = new WorkspacePaths(testRoot);
        var originalParentToken = Environment.GetEnvironmentVariable("GASSETFORGE_MCP_BEARER_TOKEN");

        try
        {
            VerifyServerOptionsReadNamedEnvironmentVariable(paths);

            using var service = new McpServerProcessService(paths);
            using var secondService = new McpServerProcessService(paths);
            Equal("GASSETFORGE_MCP_BEARER_TOKEN", service.TokenEnvironmentVariableName,
                "临时 Token 环境变量名");
            Equal(service.TokenEnvironmentVariableValue, service.BearerToken,
                "AppServer transport 可读取当前 Bearer Token");
            True(service.BearerToken.Length == 43 && service.BearerToken.All(IsBase64UrlCharacter),
                "Bearer Token 必须是 256 bit 高熵 Base64Url 值");
            False(string.Equals(service.BearerToken, secondService.BearerToken, StringComparison.Ordinal),
                "每个 WPF 进程服务实例必须生成独立 Token");
            Equal(originalParentToken, Environment.GetEnvironmentVariable(service.TokenEnvironmentVariableName),
                "临时 Token 不得写入 WPF 进程全局环境");

            var logs = new ConcurrentQueue<McpServerLogEventArgs>();
            service.LogReceived += (_, args) => logs.Enqueue(args);
            var port = GetAvailableLoopbackPort();
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp", UriKind.Absolute);
            var healthEndpoint = new Uri($"http://127.0.0.1:{port}/health", UriKind.Absolute);
            using var anonymousClient = CreateClient();
            using var wrongTokenClient = CreateClient(new string('x', 43));
            using var authorizedClient = CreateClient(service.BearerToken);

            try
            {
                await service.StartAsync(port, "codex");
                Equal(endpoint, service.Endpoint, "MCP ProcessService 当前 Endpoint");

                using (var health = await WaitForHealthAsync(anonymousClient, healthEndpoint))
                {
                    var rawHealth = health.RootElement.GetRawText();
                    False(rawHealth.Contains(service.BearerToken, StringComparison.Ordinal),
                        "/health 不得泄露 Bearer Token");
                    False(rawHealth.Contains(service.TokenEnvironmentVariableName, StringComparison.Ordinal),
                        "/health 不得泄露 Token 环境变量名");
                }

                using (var invalidHostRequest = new HttpRequestMessage(HttpMethod.Get, healthEndpoint))
                {
                    invalidHostRequest.Headers.Host = "example.com";
                    using var invalidHostResponse = await anonymousClient.SendAsync(invalidHostRequest);
                    Equal(HttpStatusCode.Forbidden, invalidHostResponse.StatusCode,
                        "/health 必须拒绝非 loopback Host");
                }

                var initializeMessage = new
                {
                    jsonrpc = "2.0",
                    id = 7001,
                    method = "initialize",
                    @params = new
                    {
                        protocolVersion = "2025-06-18",
                        capabilities = new { },
                        clientInfo = new { name = "Neutral Bearer SmokeTests", version = "1.0.0" }
                    }
                };

                using (var missingToken = await SendMcpAsync(anonymousClient, endpoint, initializeMessage))
                {
                    AssertUnauthorized(missingToken, "POST /mcp 缺少 Token");
                }

                using (var wrongToken = await SendMcpAsync(wrongTokenClient, endpoint, initializeMessage))
                {
                    AssertUnauthorized(wrongToken, "POST /mcp 错误 Token");
                }

                string sessionId;
                using (var initialize = await SendMcpAsync(authorizedClient, endpoint, initializeMessage))
                {
                    Equal(HttpStatusCode.OK, initialize.StatusCode,
                        "正确 Bearer Token 应允许 initialize");
                    sessionId = initialize.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues)
                        ? sessionValues.SingleOrDefault()
                          ?? throw new InvalidOperationException("initialize 缺少 Mcp-Session-Id。")
                        : throw new InvalidOperationException("initialize 缺少 Mcp-Session-Id。");
                }

                using (var initialized = await SendMcpAsync(
                           authorizedClient,
                           endpoint,
                           new
                           {
                               jsonrpc = "2.0",
                               method = "notifications/initialized",
                               @params = new { }
                           },
                           sessionId))
                {
                    Equal(HttpStatusCode.Accepted, initialized.StatusCode,
                        "正确 Bearer Token 应允许 initialized 通知");
                }

                await WaitUntilAsync(
                    () => logs.Any(entry => entry.Message.Contains(
                        "expected-agent diagnostic mismatch",
                        StringComparison.Ordinal)),
                    TimeSpan.FromSeconds(5),
                    "expected-agent 不匹配应只记录诊断而不得拒绝握手。");

                using (var missingDelete = await SendDeleteAsync(anonymousClient, endpoint, sessionId))
                {
                    AssertUnauthorized(missingDelete, "DELETE /mcp 缺少 Token");
                }

                using (var wrongDelete = await SendDeleteAsync(wrongTokenClient, endpoint, sessionId))
                {
                    AssertUnauthorized(wrongDelete, "DELETE /mcp 错误 Token");
                }

                using (var deleted = await SendDeleteAsync(authorizedClient, endpoint, sessionId))
                {
                    Equal(HttpStatusCode.NoContent, deleted.StatusCode,
                        "正确 Bearer Token 应允许 DELETE /mcp");
                }
            }
            finally
            {
                await service.StopAsync();
            }

            False(logs.Any(entry => entry.Message.Contains(service.BearerToken, StringComparison.Ordinal)),
                "MCP 日志不得包含 Bearer Token");
            foreach (var filePath in Directory.EnumerateFiles(paths.RootDirectory, "*", SearchOption.AllDirectories))
            {
                False(File.ReadAllText(filePath).Contains(service.BearerToken, StringComparison.Ordinal),
                    $"Bearer Token 不得落盘：{filePath}");
            }
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void VerifyServerOptionsReadNamedEnvironmentVariable(WorkspacePaths paths)
    {
        var variableName = $"GASSETFORGE_MCP_TEST_TOKEN_{Guid.NewGuid():N}";
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        try
        {
            Environment.SetEnvironmentVariable(variableName, token);
            var options = ServerOptions.Parse(
            [
                "--transport", "http",
                "--host", "127.0.0.1",
                "--port", GetAvailableLoopbackPort().ToString(),
                "--expected-agent", "codex",
                "--token-env", variableName,
                "--data-root", paths.RootDirectory
            ]);
            Equal(variableName, options.BearerTokenEnvironmentVariableName,
                "ServerOptions Token 环境变量名");
            Equal(token, options.BearerToken, "ServerOptions 应从指定环境变量读取 Token");

            Environment.SetEnvironmentVariable(variableName, null);
            Throws<InvalidOperationException>(
                () => ServerOptions.Parse(
                [
                    "--transport", "http",
                    "--port", GetAvailableLoopbackPort().ToString(),
                    "--token-env", variableName,
                    "--data-root", paths.RootDirectory
                ]),
                "HTTP 模式缺少 Token 环境变量值必须 fail closed");

            var stdio = ServerOptions.Parse(["--transport", "stdio", "--data-root", paths.RootDirectory]);
            Equal<string?>(null, stdio.BearerToken, "stdio 不要求 Bearer Token");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    private static HttpClient CreateClient(string? bearerToken = null)
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false
        })
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
        if (!string.IsNullOrEmpty(bearerToken))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return client;
    }

    private static async Task<HttpResponseMessage> SendMcpAsync(
        HttpClient client,
        Uri endpoint,
        object message,
        string? sessionId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(message), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(sessionId))
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendDeleteAsync(
        HttpClient client,
        Uri endpoint,
        string sessionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);
        request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        return await client.SendAsync(request);
    }

    private static async Task<JsonDocument> WaitForHealthAsync(HttpClient client, Uri endpoint)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(12);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            try
            {
                using var response = await client.GetAsync(endpoint);
                if (response.IsSuccessStatusCode)
                {
                    return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // The child process may still be binding its loopback listener.
            }

            await Task.Delay(75);
        }

        throw new InvalidOperationException("等待 MCP /health 就绪超时。");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string message)
    {
        var timeoutAt = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        if (!condition())
        {
            throw new InvalidOperationException(message);
        }
    }

    private static int GetAvailableLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void AssertUnauthorized(HttpResponseMessage response, string message)
    {
        Equal(HttpStatusCode.Unauthorized, response.StatusCode, message);
        True(response.Headers.WwwAuthenticate.Any(challenge =>
                string.Equals(challenge.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)),
            $"{message} 应返回 WWW-Authenticate: Bearer");
    }

    private static bool IsBase64UrlCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_';

    private static void Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool condition, string message) => True(!condition, message);

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }
}

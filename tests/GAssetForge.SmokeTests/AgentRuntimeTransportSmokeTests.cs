using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GAssetForge.App.Services;
using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.Core;

namespace GAssetForge.SmokeTests;

internal static class AgentRuntimeTransportSmokeTests
{
    private const string TestTokenVariable = "GASSETFORGE_AGENT_RUNTIME_SMOKE_TOKEN";

    public static async Task RunAllAsync()
    {
        await JsonLineRpcRoutesConcurrentResponsesByIdAsync();
        await JsonLineRpcAcceptsMultipleMessagesOnOnePhysicalLineAsync();
        await JsonLineRpcRecoversValidMessageAfterNonJsonPrefixAsync();
        await JsonLineRpcRecoversValidMessageAfterNonJsonMiddleSegmentAsync();
        await JsonLineRpcDoesNotResynchronizeAtNestedObjectsInsideMalformedEnvelopeAsync();
        await JsonLineRpcDispatchesValidMessageAndRedactsNonJsonTailAsync();
        await JsonLineRpcRejectsJsonScalarsWithoutTerminatingTheConnectionAsync();
        await JsonLineRpcKeepsFullyNonJsonInputOutOfAgentNotificationsAsync();
        await JsonLineRpcBoundsAndSummarizesRepeatedInvalidInputAsync();
        await JsonLineRpcFailureFailsEveryPendingRequestAsync();
        await JsonLineRpcDispatchesInboundServerRequestsAsync();
        await CodexAppServerUsesExpectedInitializationShapeAsync();
        await CodexAppServerKeepsImageGenerationNotificationsStructuredAsync();
        await CodexAppServerFreezesWorkspaceWriteSandboxPolicyPerTurnAsync();
        await CodexAppServerFailsActiveTurnWhenProtocolStreamEndsAsync();
        await CodexAppServerScopesGAssetForgeMcpApprovalsToTheActiveBatchAsync();
        await CodexAppServerRejectsPreBaselineVersionAsync();
        CodexProcessLaunchUsesOptionsPathForAutomaticRuntimeResolution();
        CodexExecutableResolverPrefersAppDataNpmShimBeforeOfficialInstallAndWindowsApps();
        CodexExecutableResolverDiscoversOfficialLocalAppDataRuntimeWithoutPath();
        CodexExecutableResolverSkipsWindowsAppsExecutableWithDiagnostic();
        CodexExecutableResolverProbesExplicitScriptAndExecutable();
        CodexExecutableResolverReportsEveryRejectedCandidate();
        CodexExecutableResolverDoesNotCacheFailuresAndCanRefreshSuccesses();
        CodexExecutableResolverPrefersLaunchablePathShimOverPackagedRuntime();
        CodexExecutableResolverSkipsUnlaunchableCompleteRuntime();
        CodexProcessLaunchFallsBackToLegacyPathScript();
        CodexProcessLaunchExplainsMissingCompanionWithoutFallback();
        CodexProcessLaunchKeepsBearerTokenOutOfArguments();
        CodexProcessLaunchScopesImageApiNetworkToWorkspaceWriteSandbox();
        CodexProcessLaunchUsesUtf8NoBomForProtocolStreams();
        await CodexProcessLaunchRoundTripsUtf8ThroughRealChildAsync();
        await CodexExecDeclaresFallbackCapabilitiesAndBuildsArgumentsAsync();
        await CodexExecPromotesActualTurnIdentityForIdlessItemsAsync();
        await CodexExecAcceptsConcatenatedJsonAndRedactsInvalidTailAsync();
        await CodexExecBoundsAndSummarizesRepeatedInvalidInputAsync();
        await CodexExecCancellationKillsTheEntireProcessTreeAsync();
    }

    private static async Task JsonLineRpcRoutesConcurrentResponsesByIdAsync()
    {
        var writer = new RecordingTextWriter();
        await using var connection = new JsonLineRpcConnection(
            new StringReader(string.Empty),
            writer,
            TimeSpan.FromSeconds(3));

        var firstRequest = connection.SendRequestAsync("fixture/first", new { value = 1 });
        var secondRequest = connection.SendRequestAsync("fixture/second", new { value = 2 });
        await WaitUntilAsync(
            () => writer.Lines.Count == 2 && connection.PendingRequestCount == 2,
            TimeSpan.FromSeconds(2),
            "JSON-RPC 并发请求没有全部进入 pending。\n");

        var requestIds = writer.Lines
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return new
                {
                    Method = document.RootElement.GetProperty("method").GetString()!,
                    Id = document.RootElement.GetProperty("id").GetInt64()
                };
            })
            .ToDictionary(item => item.Method, item => item.Id, StringComparer.Ordinal);

        connection.ProcessLine(JsonSerializer.Serialize(new
        {
            id = requestIds["fixture/second"].ToString(),
            result = new { routed = "second" }
        }));
        connection.ProcessLine(JsonSerializer.Serialize(new
        {
            id = requestIds["fixture/first"],
            result = new { routed = "first" }
        }));

        var firstResult = await firstRequest;
        var secondResult = await secondRequest;
        Equal("first", firstResult.GetProperty("routed").GetString(),
            "JSON-RPC 第一请求应按数值 id 路由");
        Equal("second", secondResult.GetProperty("routed").GetString(),
            "JSON-RPC 第二请求应兼容字符串形式的数值 id");
        Equal(0, connection.PendingRequestCount, "JSON-RPC 完成后 pending 数量");
    }

    private static async Task JsonLineRpcAcceptsMultipleMessagesOnOnePhysicalLineAsync()
    {
        var notifications = new List<(string Method, string Text)>();
        var warnings = new List<string>();
        await using var connection = new JsonLineRpcConnection(
            new StringReader(string.Empty),
            new StringWriter(),
            TimeSpan.FromSeconds(3));
        connection.NotificationReceived += (method, parameters) =>
            notifications.Add((method, parameters.GetProperty("text").GetString() ?? string.Empty));
        connection.ProtocolWarning += warnings.Add;

        var first = JsonSerializer.Serialize(new
        {
            method = "item/agentMessage/delta",
            @params = new { text = "正在生成图片" }
        });
        var second = JsonSerializer.Serialize(new
        {
            method = "item/completed",
            @params = new { text = "图片已经生成" }
        });
        connection.ProcessLine(first + " \t " + second + "  ");

        Equal(2, notifications.Count, "同一物理行中的两个完整 JSON-RPC 消息都必须被处理");
        Equal("正在生成图片", notifications[0].Text, "第一条中文通知不得损坏");
        Equal("图片已经生成", notifications[1].Text, "第二条中文通知不得损坏");
        Equal(0, warnings.Count, "两个完整 JSON 值及正常尾随空白不应产生协议警告");
    }

    private static async Task JsonLineRpcRecoversValidMessageAfterNonJsonPrefixAsync()
    {
        var notifications = new List<string>();
        var warnings = new List<string>();
        await using var connection = new JsonLineRpcConnection(
            new StringReader(string.Empty),
            new StringWriter(),
            TimeSpan.FromSeconds(3));
        connection.NotificationReceived += (_, parameters) =>
            notifications.Add(parameters.GetProperty("text").GetString() ?? string.Empty);
        connection.ProtocolWarning += warnings.Add;

        const string sensitivePrefix = "event: private-prefix-token-must-not-be-logged ";
        var validMessage = JsonSerializer.Serialize(new
        {
            method = "item/agentMessage/delta",
            @params = new { text = "前缀后的合法中文状态" }
        });
        connection.ProcessLine(sensitivePrefix + validMessage);

        Equal(1, notifications.Count, "非 JSON 前缀不得吞掉同一物理行后方的完整 JSON-RPC 消息");
        Equal("前缀后的合法中文状态", notifications[0], "恢复后的中文通知不得损坏");
        Equal(1, warnings.Count, "恢复前的非 JSON 前缀必须形成一条脱敏协议诊断");
        True(warnings[0].Contains("offset=0", StringComparison.Ordinal),
            "前缀诊断必须记录无效片段起始偏移");
        True(warnings[0].Contains("segmentBytes=", StringComparison.Ordinal),
            "前缀诊断必须记录无效片段长度");
        True(warnings[0].Contains("sha256=", StringComparison.Ordinal),
            "前缀诊断必须记录不可逆短摘要");
        False(warnings[0].Contains(sensitivePrefix, StringComparison.Ordinal),
            "前缀诊断不得记录 raw 非协议输入");
        False(warnings[0].Contains("private-prefix-token", StringComparison.Ordinal),
            "前缀诊断不得泄露敏感词");
    }

    private static async Task JsonLineRpcRecoversValidMessageAfterNonJsonMiddleSegmentAsync()
    {
        var notifications = new List<string>();
        var warnings = new List<string>();
        await using var connection = new JsonLineRpcConnection(
            new StringReader(string.Empty),
            new StringWriter(),
            TimeSpan.FromSeconds(3));
        connection.NotificationReceived += (_, parameters) =>
            notifications.Add(parameters.GetProperty("text").GetString() ?? string.Empty);
        connection.ProtocolWarning += warnings.Add;

        var first = JsonSerializer.Serialize(new
        {
            method = "item/agentMessage/delta",
            @params = new { text = "第一条合法消息" }
        });
        var second = JsonSerializer.Serialize(new
        {
            method = "item/completed",
            @params = new { text = "第二条合法消息" }
        });
        const string sensitiveMiddle = " event: private-middle-token-must-not-be-logged ";
        connection.ProcessLine(first + sensitiveMiddle + second);

        Equal(2, notifications.Count, "中间非 JSON 片段不得吞掉前后任一完整 JSON-RPC 消息");
        Equal("第一条合法消息", notifications[0], "中间片段前方消息顺序");
        Equal("第二条合法消息", notifications[1], "中间片段后方消息顺序");
        Equal(1, warnings.Count, "中间非 JSON 片段必须形成一条脱敏协议诊断");
        True(warnings[0].Contains("processedMessages=1", StringComparison.Ordinal),
            "中间片段诊断必须记录已成功处理的消息数量");
        True(warnings[0].Contains("segmentBytes=", StringComparison.Ordinal),
            "中间片段诊断必须记录无效片段长度");
        True(warnings[0].Contains("sha256=", StringComparison.Ordinal),
            "中间片段诊断必须记录不可逆短摘要");
        False(warnings[0].Contains(sensitiveMiddle, StringComparison.Ordinal),
            "中间片段诊断不得记录 raw 非协议输入");
        False(warnings[0].Contains("private-middle-token", StringComparison.Ordinal),
            "中间片段诊断不得泄露敏感词");
    }

    private static async Task JsonLineRpcDoesNotResynchronizeAtNestedObjectsInsideMalformedEnvelopeAsync()
    {
        var writer = new RecordingTextWriter();
        var notifications = new List<string>();
        var warnings = new List<string>();
        await using var connection = new JsonLineRpcConnection(
            new StringReader(string.Empty),
            writer,
            TimeSpan.FromSeconds(3));
        connection.NotificationReceived += (_, parameters) =>
            notifications.Add(parameters.GetProperty("text").GetString() ?? string.Empty);
        connection.ProtocolWarning += warnings.Add;

        var request = connection.SendRequestAsync("fixture/top-level-resynchronization", new { });
        await WaitUntilAsync(
            () => writer.Lines.Count == 1 && connection.PendingRequestCount == 1,
            TimeSpan.FromSeconds(2),
            "顶层重同步 fixture 请求没有进入 pending。");
        using var outboundRequest = JsonDocument.Parse(writer.Lines[0]);
        var requestId = outboundRequest.RootElement.GetProperty("id").GetInt64();

        var nestedResponse = JsonSerializer.Serialize(new
        {
            id = requestId,
            result = new { routed = "nested-spoof" }
        });
        var nestedNotification = JsonSerializer.Serialize(new
        {
            method = "item/completed",
            @params = new { text = "nested-spoof" }
        });
        var actualResponse = JsonSerializer.Serialize(new
        {
            id = requestId,
            result = new { routed = "actual-top-level" }
        });
        var actualNotification = JsonSerializer.Serialize(new
        {
            method = "item/completed",
            @params = new { text = "actual-top-level" }
        });
        const string sensitiveJunk = " private-resync-token-must-not-be-logged ";
        var malformedEnvelope =
            "{\"broken\":true \"nestedResponse\":" + nestedResponse +
            ",\"nestedNotification\":" + nestedNotification + "}";

        connection.ProcessLine(
            malformedEnvelope + sensitiveJunk + actualResponse + actualNotification);

        var result = await request;
        Equal("actual-top-level", result.GetProperty("routed").GetString(),
            "损坏外层中的嵌套 response 不得被误认作顶层协议响应");
        SequenceEqual(
            new[] { "actual-top-level" },
            notifications,
            "损坏外层中的嵌套 notification 不得被派发，真正的后续顶层消息仍应恢复");
        Equal(1, warnings.Count,
            "损坏外层及其后方 junk 应作为一个脱敏无效片段报告");
        True(warnings[0].Contains("recoveryOffset=", StringComparison.Ordinal),
            "顶层重同步诊断必须记录恢复偏移");
        True(warnings[0].Contains("sha256=", StringComparison.Ordinal),
            "顶层重同步诊断必须保留不可逆摘要");
        False(warnings[0].Contains(sensitiveJunk, StringComparison.Ordinal),
            "顶层重同步诊断不得记录 raw 非协议输入");
        False(warnings[0].Contains("private-resync-token", StringComparison.Ordinal),
            "顶层重同步诊断不得泄露敏感词");
    }

    private static async Task JsonLineRpcDispatchesValidMessageAndRedactsNonJsonTailAsync()
    {
        var notifications = new List<string>();
        var warnings = new List<string>();
        await using var connection = new JsonLineRpcConnection(
            new StringReader(string.Empty),
            new StringWriter(),
            TimeSpan.FromSeconds(3));
        connection.NotificationReceived += (_, parameters) =>
            notifications.Add(parameters.GetProperty("text").GetString() ?? string.Empty);
        connection.ProtocolWarning += warnings.Add;

        var validMessage = JsonSerializer.Serialize(new
        {
            method = "item/agentMessage/delta",
            @params = new { text = "Agent 中文状态正常" }
        });
        const string sensitiveTail = "event: private-tail-token-must-not-be-logged";
        connection.ProcessLine(validMessage + sensitiveTail);

        Equal(1, notifications.Count, "非 JSON 尾部不得阻止前方完整消息派发");
        Equal("Agent 中文状态正常", notifications[0], "前方完整消息正文不得被尾部污染");
        Equal(1, warnings.Count, "非 JSON 尾部必须单独形成一条协议诊断");
        True(warnings[0].Contains("offset=", StringComparison.Ordinal), "尾部诊断应记录安全字节偏移");
        True(warnings[0].Contains("remainingBytes=", StringComparison.Ordinal), "尾部诊断应记录剩余字节数");
        True(warnings[0].Contains("sha256=", StringComparison.Ordinal), "尾部诊断应记录不可逆短摘要");
        False(warnings[0].Contains(sensitiveTail, StringComparison.Ordinal), "尾部诊断不得记录 raw 尾部");
        False(warnings[0].Contains("private-tail-token", StringComparison.Ordinal), "尾部诊断不得泄露敏感词");
    }

    private static async Task JsonLineRpcRejectsJsonScalarsWithoutTerminatingTheConnectionAsync()
    {
        var notificationCount = 0;
        var warnings = new List<string>();
        await using var connection = new JsonLineRpcConnection(
            new StringReader(string.Empty),
            new StringWriter(),
            TimeSpan.FromSeconds(3));
        connection.NotificationReceived += (_, _) => notificationCount++;
        connection.ProtocolWarning += warnings.Add;

        connection.ProcessLine("42");

        Equal(0, notificationCount, "JSON 标量不得进入 Agent 通知正文");
        Equal(1, warnings.Count, "JSON 标量应产生可诊断的协议警告");
        True(warnings[0].Contains("kind=Number", StringComparison.Ordinal), "协议警告应标识非对象 JSON 类型");
    }

    private static async Task JsonLineRpcKeepsFullyNonJsonInputOutOfAgentNotificationsAsync()
    {
        var notificationCount = 0;
        var warnings = new List<string>();
        await using var connection = new JsonLineRpcConnection(
            new StringReader(string.Empty),
            new StringWriter(),
            TimeSpan.FromSeconds(3));
        connection.NotificationReceived += (_, _) => notificationCount++;
        connection.ProtocolWarning += warnings.Add;

        const string sensitiveLine = "event: fully-private-protocol-line";
        connection.ProcessLine(sensitiveLine);

        Equal(0, notificationCount, "完全非 JSON 输入不得进入 Agent 通知正文");
        Equal(1, warnings.Count, "完全非 JSON 输入应产生一条协议诊断");
        True(warnings[0].Contains("remainingBytes=", StringComparison.Ordinal), "协议诊断应记录剩余字节数");
        True(warnings[0].Contains("sha256=", StringComparison.Ordinal), "协议诊断应记录不可逆摘要");
        False(warnings[0].Contains(sensitiveLine, StringComparison.Ordinal), "协议诊断不得记录 raw 非 JSON 输入");
        False(warnings[0].Contains("fully-private", StringComparison.Ordinal), "协议诊断不得泄露敏感词");
    }

    private static async Task JsonLineRpcBoundsAndSummarizesRepeatedInvalidInputAsync()
    {
        var warnings = new List<string>();
        var connection = new JsonLineRpcConnection(
            new StringReader(string.Empty),
            new StringWriter(),
            TimeSpan.FromSeconds(3));
        connection.ProtocolWarning += warnings.Add;

        const string sensitiveLine = "event: repeated-private-protocol-line";
        for (var index = 0; index < 1_000; index++)
        {
            connection.ProcessLine(sensitiveLine);
        }

        await connection.DisposeAsync();

        True(warnings.Count <= 5,
            "重复非协议输入必须指数限频，不能逐条排队到 UI 与同步日志");
        True(warnings.Any(message =>
                message.Contains("total=1000", StringComparison.Ordinal) &&
                message.Contains("suppressed=999", StringComparison.Ordinal)),
            "重复非协议输入必须回传累计与抑制数量");
        False(warnings.Any(message => message.Contains(sensitiveLine, StringComparison.Ordinal)),
            "限频样本和汇总都不得记录 raw 非协议输入");
        False(warnings.Any(message => message.Contains("repeated-private", StringComparison.Ordinal)),
            "限频诊断不得泄露重复输入中的敏感词");
    }

    private static async Task JsonLineRpcFailureFailsEveryPendingRequestAsync()
    {
        var writer = new RecordingTextWriter();
        await using var connection = new JsonLineRpcConnection(
            new StringReader(string.Empty),
            writer,
            TimeSpan.FromSeconds(3));

        var firstRequest = connection.SendRequestAsync("fixture/pending-one", null);
        var secondRequest = connection.SendRequestAsync("fixture/pending-two", null);
        await WaitUntilAsync(
            () => connection.PendingRequestCount == 2,
            TimeSpan.FromSeconds(2),
            "JSON-RPC failure fixture 没有建立两个 pending 请求。");

        var failure = new IOException("fixture protocol stream terminated");
        connection.Fail(failure);

        var firstFailure = await ThrowsAsync<IOException>(
            () => firstRequest,
            "连接异常后第一条 pending 请求必须失败");
        var secondFailure = await ThrowsAsync<IOException>(
            () => secondRequest,
            "连接异常后第二条 pending 请求必须失败");
        True(ReferenceEquals(failure, firstFailure), "第一条 pending 请求应收到原始连接异常");
        True(ReferenceEquals(failure, secondFailure), "第二条 pending 请求应收到原始连接异常");
        Equal(0, connection.PendingRequestCount, "连接异常后 pending 必须清空");

        await ThrowsAsync<InvalidOperationException>(
            () => connection.SendRequestAsync("fixture/after-failure", null),
            "连接失败后不得接受新请求");
    }

    private static async Task JsonLineRpcDispatchesInboundServerRequestsAsync()
    {
        var writer = new RecordingTextWriter();
        await using var connection = new JsonLineRpcConnection(
            new StringReader(string.Empty),
            writer,
            TimeSpan.FromSeconds(3))
        {
            ServerRequestHandler = (method, parameters, _) => Task.FromResult<object?>(new
            {
                method,
                echoed = parameters.GetProperty("value").GetInt32()
            })
        };

        connection.ProcessLine(JsonSerializer.Serialize(new
        {
            id = 901,
            method = "fixture/server-request",
            @params = new { value = 7 }
        }));
        await WaitUntilAsync(
            () => writer.Lines.Count == 1,
            TimeSpan.FromSeconds(2),
            "JSON-RPC 入站请求没有收到宿主响应。");

        using var response = JsonDocument.Parse(writer.Lines[0]);
        Equal(901, response.RootElement.GetProperty("id").GetInt32(),
            "入站请求响应 id");
        var result = response.RootElement.GetProperty("result");
        Equal("fixture/server-request", result.GetProperty("method").GetString(),
            "入站请求 method 应交给 dispatcher");
        Equal(7, result.GetProperty("echoed").GetInt32(),
            "入站请求 params 应交给 dispatcher");
    }

    private static async Task CodexAppServerUsesExpectedInitializationShapeAsync()
    {
        var standardOutput = new AsyncLineTextReader();
        var standardInput = new RecordingTextWriter(line =>
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var id) &&
                root.TryGetProperty("method", out var method) &&
                string.Equals(method.GetString(), "initialize", StringComparison.Ordinal))
            {
                standardOutput.EnqueueLine(JsonSerializer.Serialize(new
                {
                    id = id.GetInt64(),
                    result = new
                    {
                        codexHome = Path.GetFullPath(Environment.CurrentDirectory),
                        platformFamily = "windows",
                        platformOs = "windows",
                        userAgent = "codex_app_server/0.139.0"
                    }
                }));
            }

            return ValueTask.CompletedTask;
        });
        var process = new FakeAgentChildProcess(
            id: 4101,
            standardInput,
            standardOutput,
            new StringReader(string.Empty),
            onExit: standardOutput.Complete);
        var factory = new FakeAgentProcessFactory((_, _) => process);
        await using var transport = new CodexAppServerTransport(factory);

        await transport.StartAsync(new AgentSessionStartOptions
        {
            ExecutablePath = "codex-fixture",
            WorkingDirectory = Environment.CurrentDirectory,
            SandboxMode = "workspace-write",
            AllowSandboxNetworkAccess = true,
            RequestTimeout = TimeSpan.FromSeconds(3)
        });

        await WaitUntilAsync(
            () => standardInput.Lines.Count == 2,
            TimeSpan.FromSeconds(2),
            "Codex app-server 未发送 initialize/initialized 序列。");
        Equal(AgentSessionTransportState.Ready, transport.State,
            "initialize 完成后 app-server 状态");
        Equal("0.139.0", transport.RuntimeVersion,
            "app-server 应从 initialize.userAgent 解析兼容版本");
        SequenceEqual(
            new[]
            {
                "app-server",
                "--stdio",
                "-c",
                "sandbox_workspace_write.network_access=true"
            },
            factory.StartInfos.Single().ArgumentList,
            "Codex app-server 必须在 workspace-write 内显式配置 Image API 出站网络");

        var protocolLines = standardInput.Lines;
        using (var initializeDocument = JsonDocument.Parse(protocolLines[0]))
        {
            var initialize = initializeDocument.RootElement;
            True(initialize.GetProperty("id").TryGetInt64(out _),
                "initialize 必须使用数值请求 id");
            Equal("initialize", initialize.GetProperty("method").GetString(),
                "首条 app-server 消息");
            var parameters = initialize.GetProperty("params");
            var clientInfo = parameters.GetProperty("clientInfo");
            Equal("GAssetForge", clientInfo.GetProperty("name").GetString(),
                "initialize clientInfo.name");
            Equal("GAssetForge", clientInfo.GetProperty("title").GetString(),
                "initialize clientInfo.title");
            Equal(ProductIdentity.InformationalVersion, clientInfo.GetProperty("version").GetString(),
                "initialize clientInfo.version");
            var capabilities = parameters.GetProperty("capabilities");
            False(capabilities.GetProperty("experimentalApi").GetBoolean(),
                "initialize 不应启用 experimental API");
            False(capabilities.GetProperty("requestAttestation").GetBoolean(),
                "initialize 不应请求 attestation");
        }

        using (var initializedDocument = JsonDocument.Parse(protocolLines[1]))
        {
            var initialized = initializedDocument.RootElement;
            Equal("initialized", initialized.GetProperty("method").GetString(),
                "initialize 响应后必须发送 initialized 通知");
            False(initialized.TryGetProperty("id", out _),
                "initialized 必须是无 id 通知");
            False(initialized.TryGetProperty("params", out _),
                "无参数 initialized 通知不应伪造 params");
        }

        await transport.StopAsync();
        True(process.KillCallCount > 0 && process.LastKillUsedEntireProcessTree,
            "停止 app-server 必须终止整个进程树");
    }

    private static async Task CodexAppServerFreezesWorkspaceWriteSandboxPolicyPerTurnAsync()
    {
        const string threadId = "thread-sandbox-policy-fixture";
        const string turnId = "turn-sandbox-policy-fixture";
        var configuredWorkingDirectory = Path.Combine(Environment.CurrentDirectory, "src", "..");
        var expectedWorkingDirectory = Path.GetFullPath(configuredWorkingDirectory);
        var standardOutput = new AsyncLineTextReader();
        JsonElement? capturedTurnParameters = null;
        var standardInput = new RecordingTextWriter(line =>
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var requestId) ||
                !root.TryGetProperty("method", out var method))
            {
                return ValueTask.CompletedTask;
            }

            switch (method.GetString())
            {
                case "initialize":
                    standardOutput.EnqueueLine(JsonSerializer.Serialize(new
                    {
                        id = requestId.GetInt64(),
                        result = new
                        {
                            codexHome = expectedWorkingDirectory,
                            platformFamily = "windows",
                            platformOs = "windows",
                            userAgent = "codex_app_server/0.139.0"
                        }
                    }));
                    break;
                case "thread/start":
                    standardOutput.EnqueueLine(JsonSerializer.Serialize(new
                    {
                        id = requestId.GetInt64(),
                        result = new { thread = new { id = threadId } }
                    }));
                    break;
                case "turn/start":
                    capturedTurnParameters = root.GetProperty("params").Clone();
                    standardOutput.EnqueueLine(JsonSerializer.Serialize(new
                    {
                        id = requestId.GetInt64(),
                        result = new { turn = new { id = turnId } }
                    }));
                    standardOutput.EnqueueLine(JsonSerializer.Serialize(new
                    {
                        method = "turn/completed",
                        @params = new
                        {
                            threadId,
                            turn = new { id = turnId, status = "completed" }
                        }
                    }));
                    break;
            }

            return ValueTask.CompletedTask;
        });
        var process = new FakeAgentChildProcess(
            4105,
            standardInput,
            standardOutput,
            new StringReader(string.Empty),
            standardOutput.Complete);
        await using var transport = new CodexAppServerTransport(
            new FakeAgentProcessFactory((_, _) => process));

        await transport.StartAsync(new AgentSessionStartOptions
        {
            ExecutablePath = "codex-fixture",
            WorkingDirectory = configuredWorkingDirectory,
            SandboxMode = "workspace-write",
            AllowSandboxNetworkAccess = true,
            RequestTimeout = TimeSpan.FromSeconds(3)
        });
        var result = await transport.StartTurnAsync("exercise the per-turn sandbox policy fixture");

        True(result.IsSuccess, "带显式 sandboxPolicy 的 App Server Turn 应正常完成");
        var turnParameters = capturedTurnParameters ??
                             throw new InvalidOperationException("turn/start 必须发送 params。");
        var sandboxPolicy = turnParameters.GetProperty("sandboxPolicy");
        Equal("workspaceWrite", sandboxPolicy.GetProperty("type").GetString(),
            "turn/start sandboxPolicy.type");
        var writableRoots = sandboxPolicy.GetProperty("writableRoots").EnumerateArray().ToArray();
        Equal(1, writableRoots.Length, "turn/start sandboxPolicy 只应开放当前项目工作目录");
        Equal(expectedWorkingDirectory, writableRoots[0].GetString(),
            "turn/start writableRoots 必须使用规范化工作目录");
        True(sandboxPolicy.GetProperty("networkAccess").GetBoolean(),
            "turn/start 必须显式冻结 Image API 出站网络权限");
    }

    private static async Task CodexAppServerKeepsImageGenerationNotificationsStructuredAsync()
    {
        var standardOutput = new AsyncLineTextReader();
        var standardInput = new RecordingTextWriter(line =>
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var id) &&
                root.TryGetProperty("method", out var method) &&
                string.Equals(method.GetString(), "initialize", StringComparison.Ordinal))
            {
                standardOutput.EnqueueLine(JsonSerializer.Serialize(new
                {
                    id = id.GetInt64(),
                    result = new
                    {
                        codexHome = Path.GetFullPath(Environment.CurrentDirectory),
                        platformFamily = "windows",
                        platformOs = "windows",
                        userAgent = "codex_app_server/0.149.1"
                    }
                }));
            }

            return ValueTask.CompletedTask;
        });
        var process = new FakeAgentChildProcess(
            id: 4106,
            standardInput,
            standardOutput,
            new StringReader(string.Empty),
            onExit: standardOutput.Complete);
        await using var transport = new CodexAppServerTransport(
            new FakeAgentProcessFactory((_, _) => process));
        var notifications = new ConcurrentQueue<AgentNotificationEventArgs>();
        var warnings = new ConcurrentQueue<string>();
        transport.NotificationReceived += (_, eventArgs) => notifications.Enqueue(eventArgs);
        transport.LogReceived += (_, eventArgs) =>
        {
            if (eventArgs.Level == AgentLogLevel.Warning)
            {
                warnings.Enqueue(eventArgs.Message);
            }
        };

        await transport.StartAsync(new AgentSessionStartOptions
        {
            ExecutablePath = "codex-fixture",
            WorkingDirectory = Environment.CurrentDirectory,
            RequestTimeout = TimeSpan.FromSeconds(3)
        });

        const string ordinaryText = "visible image ordinary text must stay outside JSONL notifications";
        var savedPath = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.ImageGenerationTransport",
            "generated.png"));
        standardOutput.EnqueueLine(ordinaryText);
        standardOutput.EnqueueLine(JsonSerializer.Serialize(new
        {
            method = "item/completed",
            @params = new
            {
                threadId = "thread-image-generation",
                turnId = "turn-image-generation",
                item = new
                {
                    id = "image-generation-transport",
                    type = "imageGeneration",
                    status = "completed",
                    result = "visible-result",
                    savedPath
                }
            }
        }));

        await WaitUntilAsync(
            () => notifications.Count == 1 && warnings.Count > 0,
            TimeSpan.FromSeconds(2),
            "App Server 没有独立转发 imageGeneration JSON 通知与非协议文本诊断。");

        var notification = notifications.Single();
        Equal("item/completed", notification.Method,
            "imageGeneration 必须保留正式 item/completed 事件名");
        var item = notification.Parameters.GetProperty("item");
        Equal("imageGeneration", item.GetProperty("type").GetString(),
            "transport 必须保留 imageGeneration item 类型");
        Equal("completed", item.GetProperty("status").GetString(),
            "transport 必须保留 imageGeneration status");
        Equal("visible-result", item.GetProperty("result").GetString(),
            "transport 必须保留结构化 result 字段供解析器判断可视结果");
        Equal(savedPath, item.GetProperty("savedPath").GetString(),
            "transport 必须保留结构化绝对 savedPath");
        True(warnings.All(message => !message.Contains(ordinaryText, StringComparison.Ordinal)),
            "普通非 JSON 文本只能形成脱敏诊断，不能混入通知或日志正文");
    }

    private static async Task CodexAppServerScopesGAssetForgeMcpApprovalsToTheActiveBatchAsync()
    {
        const string threadId = "thread-generation-fixture";
        const string turnId = "turn-generation-fixture";
        const string approvedTaskId = "0123456789abcdef0123456789abcdef";
        const string foreignTaskId = "fedcba9876543210fedcba9876543210";
        var standardOutput = new AsyncLineTextReader();
        var approvalResponses = new ConcurrentDictionary<long, JsonElement>();
        var standardInput = new RecordingTextWriter(line =>
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("method", out var method) &&
                root.TryGetProperty("id", out var requestId))
            {
                switch (method.GetString())
                {
                    case "initialize":
                        standardOutput.EnqueueLine(JsonSerializer.Serialize(new
                        {
                            id = requestId.GetInt64(),
                            result = new
                            {
                                codexHome = Path.GetFullPath(Environment.CurrentDirectory),
                                platformFamily = "windows",
                                platformOs = "windows",
                                userAgent = "codex_app_server/0.139.0"
                            }
                        }));
                        break;
                    case "thread/start":
                        standardOutput.EnqueueLine(JsonSerializer.Serialize(new
                        {
                            id = requestId.GetInt64(),
                            result = new { thread = new { id = threadId } }
                        }));
                        break;
                    case "turn/start":
                        standardOutput.EnqueueLine(JsonSerializer.Serialize(new
                        {
                            id = requestId.GetInt64(),
                            result = new { turn = new { id = turnId } }
                        }));
                        standardOutput.EnqueueLine(CreateMcpApprovalRequest(
                            801,
                            threadId,
                            turnId,
                            "create_generation_task",
                            approvedTaskId));
                        standardOutput.EnqueueLine(CreateMcpApprovalRequest(
                            802,
                            threadId,
                            turnId,
                            "submit_generation_output",
                            foreignTaskId));
                        standardOutput.EnqueueLine(CreateMcpApprovalRequest(
                            803,
                            threadId,
                            turnId,
                            "claim_generation_task",
                            approvedTaskId));
                        standardOutput.EnqueueLine(CreateMcpApprovalRequest(
                            804,
                            threadId,
                            turnId,
                            "submit_generation_output",
                            approvedTaskId));
                        standardOutput.EnqueueLine(CreateMcpApprovalRequest(
                            805,
                            threadId,
                            turnId,
                            "run_image_delivery_fallback",
                            approvedTaskId));
                        standardOutput.EnqueueLine(CreateMcpApprovalRequest(
                            806,
                            threadId,
                            turnId,
                            "run_image_delivery_fallback",
                            taskId: null));
                        break;
                }

                return ValueTask.CompletedTask;
            }

            if (!root.TryGetProperty("id", out var responseId) ||
                !responseId.TryGetInt64(out var numericResponseId) ||
                numericResponseId is < 801 or > 806)
            {
                return ValueTask.CompletedTask;
            }

            approvalResponses[numericResponseId] = root.GetProperty("result").Clone();
            if (numericResponseId == 806)
            {
                standardOutput.EnqueueLine(JsonSerializer.Serialize(new
                {
                    method = "turn/completed",
                    @params = new
                    {
                        threadId,
                        turn = new { id = turnId, status = "completed" }
                    }
                }));
            }

            return ValueTask.CompletedTask;
        });
        var process = new FakeAgentChildProcess(
            4103,
            standardInput,
            standardOutput,
            new StringReader(string.Empty),
            standardOutput.Complete);
        await using var transport = new CodexAppServerTransport(
            new FakeAgentProcessFactory((_, _) => process));

        await transport.StartAsync(new AgentSessionStartOptions
        {
            ExecutablePath = "codex-fixture",
            WorkingDirectory = Environment.CurrentDirectory,
            RequestTimeout = TimeSpan.FromSeconds(3)
        });
        var result = await transport.StartTurnAsync($"""
            执行 GAssetForge 批次。
            - {approvedTaskId}
            """);

        True(result.IsSuccess, "MCP 审批 fixture Turn 应正常完成");
        await WaitUntilAsync(
            () => approvalResponses.Count == 6,
            TimeSpan.FromSeconds(2),
            "宿主没有响应全部 MCP elicitation 请求。");
        Equal("decline", approvalResponses[801].GetProperty("action").GetString(),
            "非生成协议工具必须拒绝");
        Equal("decline", approvalResponses[802].GetProperty("action").GetString(),
            "非当前批次 taskId 的 submit_generation_output 必须拒绝");
        Equal("accept", approvalResponses[803].GetProperty("action").GetString(),
            "当前批次 claim 必须批准");
        Equal("accept", approvalResponses[804].GetProperty("action").GetString(),
            "当前批次 submit_generation_output 必须批准");
        Equal("accept", approvalResponses[805].GetProperty("action").GetString(),
            "当前批次 run_image_delivery_fallback 必须批准");
        Equal("decline", approvalResponses[806].GetProperty("action").GetString(),
            "缺少 taskId 的 run_image_delivery_fallback 必须拒绝");
        Equal(JsonValueKind.Object, approvalResponses[804].GetProperty("content").ValueKind,
            "批准响应 content 必须是空对象");
        Equal(0, approvalResponses[804].GetProperty("content").EnumerateObject().Count(),
            "批准响应 content 不应回填工具参数");
        Equal(JsonValueKind.Null, approvalResponses[804].GetProperty("_meta").ValueKind,
            "批准响应 _meta 默认必须为 null");
    }

    private static async Task CodexAppServerFailsActiveTurnWhenProtocolStreamEndsAsync()
    {
        const string threadId = "thread-protocol-eof-fixture";
        const string turnId = "turn-protocol-eof-fixture";
        var standardOutput = new AsyncLineTextReader();
        var standardInput = new RecordingTextWriter(line =>
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var requestId) ||
                !root.TryGetProperty("method", out var method))
            {
                return ValueTask.CompletedTask;
            }

            switch (method.GetString())
            {
                case "initialize":
                    standardOutput.EnqueueLine(JsonSerializer.Serialize(new
                    {
                        id = requestId.GetInt64(),
                        result = new
                        {
                            codexHome = Path.GetFullPath(Environment.CurrentDirectory),
                            platformFamily = "windows",
                            platformOs = "windows",
                            userAgent = "codex_app_server/0.139.0"
                        }
                    }));
                    break;
                case "thread/start":
                    standardOutput.EnqueueLine(JsonSerializer.Serialize(new
                    {
                        id = requestId.GetInt64(),
                        result = new { thread = new { id = threadId } }
                    }));
                    break;
                case "turn/start":
                    standardOutput.EnqueueLine(JsonSerializer.Serialize(new
                    {
                        id = requestId.GetInt64(),
                        result = new { turn = new { id = turnId } }
                    }));
                    standardOutput.Complete();
                    break;
            }

            return ValueTask.CompletedTask;
        });
        var process = new FakeAgentChildProcess(
            4104,
            standardInput,
            standardOutput,
            new StringReader(string.Empty),
            standardOutput.Complete);
        await using var transport = new CodexAppServerTransport(
            new FakeAgentProcessFactory((_, _) => process));

        await transport.StartAsync(new AgentSessionStartOptions
        {
            ExecutablePath = "codex-fixture",
            WorkingDirectory = Environment.CurrentDirectory,
            RequestTimeout = TimeSpan.FromSeconds(3)
        });
        var turnTask = transport.StartTurnAsync("exercise the protocol EOF fixture");
        False(process.HasExited, "协议 EOF fixture 的子进程必须保持存活");

        await ThrowsAsync<EndOfStreamException>(
            () => turnTask.WaitAsync(TimeSpan.FromSeconds(2)),
            "stdout EOF 后活动 Turn 必须立即收到协议流异常");
        Equal(AgentSessionTransportState.Faulted, transport.State,
            "stdout EOF 后 transport 必须保持 Faulted");
        True(transport.IsRunning,
            "协议流断开不应伪造成子进程已退出");
    }

    private static string CreateMcpApprovalRequest(
        long id,
        string threadId,
        string turnId,
        string toolName,
        string? taskId)
    {
        var toolParameters = new Dictionary<string, object?>
        {
            ["agentId"] = "codex-root"
        };
        if (!string.IsNullOrWhiteSpace(taskId))
        {
            toolParameters["taskId"] = taskId;
        }
        if (string.Equals(toolName, "submit_generation_output", StringComparison.Ordinal))
        {
            toolParameters["outputIndex"] = 0;
            toolParameters["sourcePath"] = Path.Combine(
                Path.GetTempPath(),
                "codex-app-server-submit-fixture.png");
        }

        return JsonSerializer.Serialize(new
        {
            id,
            method = "mcpServer/elicitation/request",
            @params = new
            {
                threadId,
                turnId,
                serverName = "gasset_forge",
                mode = "form",
                message = $"Allow {toolName}?",
                requestedSchema = new { type = "object", properties = new { } },
                _meta = new
                {
                    codex_approval_kind = "mcp_tool_call",
                    tool_name = toolName,
                    tool_params = toolParameters
                }
            }
        });
    }

    private static void CodexProcessLaunchKeepsBearerTokenOutOfArguments()
    {
        var token = $"runtime-smoke-{Guid.NewGuid():N}";
        var endpoint = new Uri("http://127.0.0.1:43119/mcp");
        var options = CreateOptions(endpoint, token);
        var startInfo = CodexProcessLaunch.CreateBaseStartInfo(options);
        CodexProcessLaunch.AddMcpConfiguration(startInfo, options);

        var arguments = startInfo.ArgumentList.ToArray();
        SequenceEqual(
            new[]
            {
                "-c",
                $"mcp_servers.gasset_forge.url={CodexProcessLaunch.TomlString(endpoint.AbsoluteUri)}",
                "-c",
                $"mcp_servers.gasset_forge.bearer_token_env_var={CodexProcessLaunch.TomlString(TestTokenVariable)}",
                "-c",
                "mcp_servers.gasset_forge.required=true",
                "-c",
                $"mcp_servers.gasset_forge.tool_timeout_sec={GenerationMcpToolPolicy.ToolTimeoutSeconds}",
                "-c",
                $"mcp_servers.gasset_forge.enabled_tools={GenerationMcpToolPolicy.BuildTomlToolArray()}",
                "-c",
                $"mcp_servers.gasset_forge.default_tools_approval_mode={CodexProcessLaunch.TomlString(GenerationMcpToolPolicy.DefaultToolsApprovalMode)}"
            },
            arguments,
            "MCP 应通过独立 -c 参数注入");
        False(arguments.Any(argument => argument.Contains(token, StringComparison.Ordinal)),
            "Bearer Token 不得出现在 CLI 参数中");
        Equal(token, startInfo.Environment[TestTokenVariable],
            "Bearer Token 只能通过命名子进程环境变量传递");
        False(
            CodexProcessLaunch.Redact($"stderr leaked {token}", options)
                .Contains(token, StringComparison.Ordinal),
            "日志脱敏不得保留 Bearer Token");
    }

    private static void CodexProcessLaunchScopesImageApiNetworkToWorkspaceWriteSandbox()
    {
        var options = new AgentSessionStartOptions
        {
            ExecutablePath = "codex-fixture",
            WorkingDirectory = Environment.CurrentDirectory,
            SandboxMode = "workspace-write",
            ApprovalPolicy = "never",
            AllowSandboxNetworkAccess = true
        };
        var startInfo = CodexProcessLaunch.CreateBaseStartInfo(options);
        CodexProcessLaunch.AddSandboxConfiguration(startInfo, options);

        SequenceEqual(
            new[] { "-c", "sandbox_workspace_write.network_access=true" },
            startInfo.ArgumentList,
            "Image API 网络权限必须以 workspace-write 精确配置注入");

        var unrestricted = options with { SandboxMode = "danger-full-access" };
        var unrestrictedStartInfo = CodexProcessLaunch.CreateBaseStartInfo(unrestricted);
        Throws<InvalidOperationException>(
            () => CodexProcessLaunch.AddSandboxConfiguration(unrestrictedStartInfo, unrestricted),
            "GAssetForge 不得为图片 API 兜底隐式扩大到 danger-full-access");
    }

    private static void CodexProcessLaunchUsesUtf8NoBomForProtocolStreams()
    {
        var options = CreateOptions(
            new Uri("http://127.0.0.1:43119/mcp"),
            $"runtime-smoke-{Guid.NewGuid():N}");
        var startInfo = CodexProcessLaunch.CreateBaseStartInfo(options);

        AssertUtf8NoBom(startInfo.StandardInputEncoding, "Codex stdin");
        AssertUtf8NoBom(startInfo.StandardOutputEncoding, "Codex stdout");
        AssertUtf8NoBom(startInfo.StandardErrorEncoding, "Codex stderr");

        var outputOnlyProbe = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        CodexProcessLaunch.ConfigureUtf8Redirection(outputOnlyProbe);
        Equal<Encoding?>(null, outputOnlyProbe.StandardInputEncoding,
            "未重定向 stdin 的版本探针不应配置输入编码");
        AssertUtf8NoBom(outputOnlyProbe.StandardOutputEncoding, "Codex --version stdout");
        AssertUtf8NoBom(outputOnlyProbe.StandardErrorEncoding, "Codex --version stderr");
    }

    private static void AssertUtf8NoBom(Encoding? encoding, string channel)
    {
        True(encoding is not null, $"{channel} 必须显式声明 UTF-8，不能继承 Windows 控制台代码页");
        Equal(Encoding.UTF8.CodePage, encoding!.CodePage, $"{channel} 编码代码页");
        Equal(0, encoding.GetPreamble().Length, $"{channel} 不得写入 UTF-8 BOM");
    }

    private static async Task CodexProcessLaunchRoundTripsUtf8ThroughRealChildAsync()
    {
        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        True(File.Exists(powershellPath), "Windows PowerShell 子进程 fixture 必须存在");
        const string script = """
            $utf8 = [System.Text.UTF8Encoding]::new($false)
            [Console]::InputEncoding = $utf8
            [Console]::OutputEncoding = $utf8
            [Console]::Error.WriteLine('子进程错误流中文')
            $inputLine = [Console]::In.ReadLine()
            $payload = @{ method = 'fixture/status'; params = @{ text = ('子进程回显：' + $inputLine) } }
            [Console]::Out.WriteLine(($payload | ConvertTo-Json -Compress))
            """;
        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);
        CodexProcessLaunch.ConfigureUtf8Redirection(startInfo);

        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException("无法启动 UTF-8 子进程 fixture。");
        const string input = "正在生成像素素材";
        await process.StandardInput.WriteLineAsync(input);
        process.StandardInput.Close();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var output = await standardOutput;
        var error = await standardError;

        Equal(0, process.ExitCode, "UTF-8 子进程 fixture 退出码");
        using var document = JsonDocument.Parse(output);
        Equal("子进程回显：正在生成像素素材",
            document.RootElement.GetProperty("params").GetProperty("text").GetString(),
            "真实 stdin/stdout 管道中的中文 JSON 不得损坏");
        True(error.Contains("子进程错误流中文", StringComparison.Ordinal),
            "真实 stderr 管道中的中文不得损坏");
    }

    private static async Task CodexAppServerRejectsPreBaselineVersionAsync()
    {
        var standardOutput = new AsyncLineTextReader();
        var standardInput = new RecordingTextWriter(line =>
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var id) &&
                root.TryGetProperty("method", out var method) &&
                string.Equals(method.GetString(), "initialize", StringComparison.Ordinal))
            {
                standardOutput.EnqueueLine(JsonSerializer.Serialize(new
                {
                    id = id.GetInt64(),
                    result = new
                    {
                        codexHome = Path.GetFullPath(Environment.CurrentDirectory),
                        platformFamily = "windows",
                        platformOs = "windows",
                        userAgent = "codex_app_server/0.138.0"
                    }
                }));
            }

            return ValueTask.CompletedTask;
        });
        var process = new FakeAgentChildProcess(
            4102,
            standardInput,
            standardOutput,
            new StringReader(string.Empty),
            standardOutput.Complete);
        await using var transport = new CodexAppServerTransport(
            new FakeAgentProcessFactory((_, _) => process));

        await ThrowsAsync<NotSupportedException>(
            () => transport.StartAsync(new AgentSessionStartOptions
            {
                ExecutablePath = "codex-fixture",
                WorkingDirectory = Environment.CurrentDirectory,
                RequestTimeout = TimeSpan.FromSeconds(3)
            }),
            "低于 0.139.0 的 Codex app-server 必须触发兼容性降级信号");
        Equal(AgentSessionTransportState.Faulted, transport.State,
            "旧版 app-server 应进入 Faulted");
        True(process.KillCallCount > 0 && process.LastKillUsedEntireProcessTree,
            "拒绝旧版 app-server 时必须清理进程树");
    }

    private static void CodexExecutableResolverPrefersAppDataNpmShimBeforeOfficialInstallAndWindowsApps()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "codex-runtime-appdata-priority",
            Guid.NewGuid().ToString("N"));
        var applicationDirectory = Path.Combine(root, "distribution");
        var applicationDataDirectory = Path.Combine(root, "AppData", "Roaming");
        var npmDirectory = Path.Combine(applicationDataDirectory, "npm");
        var localApplicationDataDirectory = Path.Combine(root, "AppData", "Local");
        var officialBinDirectory = Path.Combine(
            localApplicationDataDirectory,
            "Programs",
            "OpenAI",
            "Codex",
            "bin");
        var windowsAppsDirectory = Path.Combine(
            root,
            "Program Files",
            "WindowsApps",
            "OpenAI.Codex_fixture",
            "app",
            "resources");
        try
        {
            Directory.CreateDirectory(applicationDirectory);
            Directory.CreateDirectory(npmDirectory);
            Directory.CreateDirectory(officialBinDirectory);
            Directory.CreateDirectory(windowsAppsDirectory);
            var expectedShimPath = Path.Combine(npmDirectory, "codex.cmd");
            File.WriteAllText(expectedShimPath, "@echo off\r\nexit /b 0\r\n");
            File.WriteAllBytes(Path.Combine(officialBinDirectory, "codex.exe"), [0x03]);
            var windowsAppsExecutable = Path.Combine(windowsAppsDirectory, "codex.exe");
            File.WriteAllBytes(windowsAppsExecutable, [0x01]);
            File.WriteAllBytes(
                Path.Combine(windowsAppsDirectory, CodexExecutableResolver.CodeModeHostFileName),
                [0x02]);
            var probedCandidates = new List<string>();

            var resolution = CodexExecutableResolver.Resolve(
                "codex",
                applicationDirectory,
                windowsAppsDirectory,
                candidate =>
                {
                    probedCandidates.Add(candidate.CodexExecutablePath);
                    return string.Equals(
                        candidate.CodexExecutablePath,
                        expectedShimPath,
                        StringComparison.OrdinalIgnoreCase);
                },
                applicationDataDirectory,
                localApplicationDataDirectory);

            Equal(Path.GetFullPath(expectedShimPath), resolution.CodexExecutablePath,
                "%APPDATA%\\npm 中的 codex.cmd 必须先于 LOCALAPPDATA 官方安装和 WindowsApps exe");
            SequenceEqual(
                new[] { Path.GetFullPath(expectedShimPath) },
                probedCandidates,
                "命中 AppData npm shim 后不应再探测 LOCALAPPDATA 或 WindowsApps 候选");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void CodexProcessLaunchUsesOptionsPathForAutomaticRuntimeResolution()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "codex-runtime-options-path",
            Guid.NewGuid().ToString("N"));
        var applicationDirectory = Path.Combine(root, "distribution");
        var applicationDataDirectory = Path.Combine(root, "empty-appdata");
        var localApplicationDataDirectory = Path.Combine(root, "empty-localappdata");
        var freshPathDirectory = Path.Combine(root, "fresh-path");
        try
        {
            Directory.CreateDirectory(applicationDirectory);
            Directory.CreateDirectory(applicationDataDirectory);
            Directory.CreateDirectory(localApplicationDataDirectory);
            Directory.CreateDirectory(freshPathDirectory);
            var expectedShimPath = Path.Combine(freshPathDirectory, "codex.cmd");
            const string dependencyName = "gassetforge-codex-runtime-dependency.cmd";
            File.WriteAllText(
                Path.Combine(freshPathDirectory, dependencyName),
                "@echo off\r\nexit /b 0\r\n");
            File.WriteAllText(
                expectedShimPath,
                $"@echo off\r\ncall {dependencyName}\r\nif errorlevel 1 exit /b 23\r\nexit /b 0\r\n");
            var options = new AgentSessionStartOptions
            {
                ExecutablePath = "codex",
                WorkingDirectory = Environment.CurrentDirectory,
                EnvironmentVariables = new Dictionary<string, string?>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["PATH"] = freshPathDirectory
                }
            };

            var startInfo = CodexProcessLaunch.CreateBaseStartInfo(
                options,
                applicationDirectory,
                applicationDataDirectory: applicationDataDirectory,
                localApplicationDataDirectory: localApplicationDataDirectory);

            Equal(
                Environment.GetEnvironmentVariable("ComSpec") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                startInfo.FileName,
                "自动解析必须使用本次 Agent 启动配置中的 fresh PATH");
            SequenceEqual(
                new[] { "/d", "/s", "/c", "call", Path.GetFullPath(expectedShimPath) },
                startInfo.ArgumentList,
                "fresh PATH 中的 codex.cmd 必须成为实际启动目标");
            Equal(freshPathDirectory, startInfo.Environment["PATH"],
                "解析成功后同一 fresh PATH 必须继续传给 Codex 子进程");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void CodexExecutableResolverDiscoversOfficialLocalAppDataRuntimeWithoutPath()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "codex-runtime-localappdata",
            Guid.NewGuid().ToString("N"));
        var applicationDirectory = Path.Combine(root, "distribution");
        var applicationDataDirectory = Path.Combine(root, "empty-appdata");
        var localApplicationDataDirectory = Path.Combine(root, "LocalAppData");
        var officialBinDirectory = Path.Combine(
            localApplicationDataDirectory,
            "Programs",
            "OpenAI",
            "Codex",
            "bin");
        try
        {
            Directory.CreateDirectory(applicationDirectory);
            Directory.CreateDirectory(applicationDataDirectory);
            Directory.CreateDirectory(officialBinDirectory);
            var expectedExecutablePath = Path.Combine(officialBinDirectory, "codex.exe");
            File.WriteAllBytes(expectedExecutablePath, [0x01]);
            var probedCandidates = new List<string>();

            var resolution = CodexExecutableResolver.ResolveWithDiagnostics(
                "codex",
                applicationDirectory,
                pathEnvironment: string.Empty,
                launchProbe: candidate =>
                {
                    probedCandidates.Add(candidate.CodexExecutablePath);
                    return CodexExecutableProbeResult.Successful("fixture 官方安装可启动");
                },
                applicationDataDirectory: applicationDataDirectory,
                localApplicationDataDirectory: localApplicationDataDirectory);

            True(resolution.IsSuccess,
                "PATH 为空时仍应直接检测 LOCALAPPDATA 下的官方 Codex 安装");
            Equal(Path.GetFullPath(expectedExecutablePath), resolution.Launch!.CodexExecutablePath,
                "LOCALAPPDATA 官方安装路径");
            SequenceEqual(
                new[] { Path.GetFullPath(expectedExecutablePath) },
                probedCandidates,
                "无 PATH 时只应探测注入 LOCALAPPDATA 中的官方候选");
            var diagnostic = resolution.Candidates.Single();
            Equal(CodexExecutableCandidateStatus.Accepted, diagnostic.Status,
                "LOCALAPPDATA 官方安装候选状态");
            True(diagnostic.Source.Contains("LOCALAPPDATA", StringComparison.OrdinalIgnoreCase),
                "候选诊断必须明确官方安装来源");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void CodexExecutableResolverSkipsWindowsAppsExecutableWithDiagnostic()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "codex-runtime-windowsapps-skip",
            Guid.NewGuid().ToString("N"));
        var applicationDirectory = Path.Combine(root, "distribution");
        var applicationDataDirectory = Path.Combine(root, "empty-appdata");
        var windowsAppsDirectory = Path.Combine(
            root,
            "Program Files",
            "WindowsApps",
            "OpenAI.Codex_fixture",
            "app",
            "resources");
        try
        {
            Directory.CreateDirectory(applicationDirectory);
            Directory.CreateDirectory(applicationDataDirectory);
            Directory.CreateDirectory(windowsAppsDirectory);
            var windowsAppsExecutable = Path.Combine(windowsAppsDirectory, "codex.exe");
            File.WriteAllBytes(windowsAppsExecutable, [0x01]);
            File.WriteAllBytes(
                Path.Combine(windowsAppsDirectory, CodexExecutableResolver.CodeModeHostFileName),
                [0x02]);
            var probeCount = 0;

            var resolution = CodexExecutableResolver.ResolveWithDiagnostics(
                "codex",
                applicationDirectory,
                windowsAppsDirectory,
                candidate =>
                {
                    probeCount++;
                    return CodexExecutableProbeResult.Successful("fixture 可启动");
                },
                applicationDataDirectory,
                Path.Combine(root, "empty-localappdata"));

            False(resolution.IsSuccess,
                "WindowsApps 包内 codex.exe 不得被当作可用 Runtime");
            Equal(0, probeCount,
                "WindowsApps 包内 codex.exe 应在 Process.Start 之前被明确跳过");
            var diagnostic = resolution.Candidates.Single(candidate =>
                string.Equals(
                    candidate.CandidatePath,
                    windowsAppsExecutable,
                    StringComparison.OrdinalIgnoreCase));
            Equal(CodexExecutableCandidateStatus.Skipped, diagnostic.Status,
                "WindowsApps 候选状态应为 Skipped");
            True(diagnostic.Source.Contains("PATH", StringComparison.OrdinalIgnoreCase),
                "WindowsApps 候选诊断应说明来源");
            True(diagnostic.Reason.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) &&
                 diagnostic.Reason.Contains("拒绝访问", StringComparison.Ordinal),
                "WindowsApps 候选诊断应解释桌面进程直接启动会拒绝访问");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void CodexExecutableResolverProbesExplicitScriptAndExecutable()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "codex-runtime-explicit-probe",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var argumentsPath = Path.Combine(root, "probe-arguments.txt");
            var scriptPath = Path.Combine(root, "configured codex.cmd");
            File.WriteAllText(
                scriptPath,
                $"@echo off\r\n>\"{argumentsPath}\" echo %*\r\nexit /b 0\r\n");

            var scriptResolution = CodexExecutableResolver.Resolve(scriptPath);

            Equal(
                Environment.GetEnvironmentVariable("ComSpec") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                scriptResolution.ProcessFileName,
                "显式配置的 .cmd 必须通过 ComSpec 启动");
            SequenceEqual(
                new[] { "/d", "/s", "/c", "call", Path.GetFullPath(scriptPath) },
                scriptResolution.PrefixArguments,
                "显式配置的 .cmd 应保留脚本启动前缀");
            True(File.Exists(argumentsPath),
                "显式配置的 .cmd 必须先执行启动探测");
            True(File.ReadAllText(argumentsPath).Contains("--version", StringComparison.Ordinal),
                "显式配置的 .cmd 启动探测必须传入 --version");

            var executablePath = Path.Combine(root, "configured-codex.exe");
            File.WriteAllBytes(executablePath, [0x01]);
            var executableProbeCount = 0;
            var executableResolution = CodexExecutableResolver.Resolve(
                executablePath,
                launchProbe: candidate =>
                {
                    executableProbeCount++;
                    return string.Equals(
                        candidate.ProcessFileName,
                        Path.GetFullPath(executablePath),
                        StringComparison.OrdinalIgnoreCase);
                });

            Equal(1, executableProbeCount,
                "显式配置的 .exe 必须执行启动探测");
            Equal(Path.GetFullPath(executablePath), executableResolution.ProcessFileName,
                "显式配置的 .exe 应直接作为进程文件");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void CodexExecutableResolverReportsEveryRejectedCandidate()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "codex-runtime-diagnostics",
            Guid.NewGuid().ToString("N"));
        var applicationDirectory = Path.Combine(root, "distribution");
        var applicationDataDirectory = Path.Combine(root, "AppData", "Roaming");
        var npmDirectory = Path.Combine(applicationDataDirectory, "npm");
        var pathDirectory = Path.Combine(root, "path-cli");
        try
        {
            Directory.CreateDirectory(applicationDirectory);
            Directory.CreateDirectory(npmDirectory);
            Directory.CreateDirectory(pathDirectory);
            var appDataShimPath = Path.Combine(npmDirectory, "codex.cmd");
            var pathShimPath = Path.Combine(pathDirectory, "codex.bat");
            File.WriteAllText(appDataShimPath, "@echo off\r\nexit /b 7\r\n");
            File.WriteAllText(pathShimPath, "@echo off\r\nexit /b 9\r\n");

            CodexExecutableResolutionException? failure = null;
            try
            {
                _ = CodexExecutableResolver.Resolve(
                    "codex",
                    applicationDirectory,
                    pathDirectory,
                    applicationDataDirectory: applicationDataDirectory,
                    localApplicationDataDirectory: Path.Combine(root, "empty-localappdata"));
            }
            catch (CodexExecutableResolutionException exception)
            {
                failure = exception;
            }

            True(failure is not null,
                "所有 Codex 候选探测失败时必须抛出结构化解析异常");
            Equal(2, failure!.Candidates.Count,
                "解析异常必须保留每个已发现候选的诊断");
            True(failure.Message.Contains(appDataShimPath, StringComparison.OrdinalIgnoreCase) &&
                 failure.Message.Contains("APPDATA npm", StringComparison.OrdinalIgnoreCase) &&
                 failure.Message.Contains("退出码 7", StringComparison.Ordinal),
                "异常必须包含 AppData npm 候选的路径、来源和失败原因");
            True(failure.Message.Contains(pathShimPath, StringComparison.OrdinalIgnoreCase) &&
                 failure.Message.Contains("PATH shim", StringComparison.OrdinalIgnoreCase) &&
                 failure.Message.Contains("退出码 9", StringComparison.Ordinal),
                "异常必须包含 PATH 候选的路径、来源和失败原因");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void CodexExecutableResolverDoesNotCacheFailuresAndCanRefreshSuccesses()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "codex-runtime-probe-cache",
            Guid.NewGuid().ToString("N"));
        var scriptPath = Path.Combine(root, "codex.cmd");
        try
        {
            Directory.CreateDirectory(root);
            CodexExecutableResolver.ClearLaunchProbeCache();
            File.WriteAllText(scriptPath, "@echo off\r\nexit /b 17\r\n");

            var initialFailure = CodexExecutableResolver.ResolveWithDiagnostics(scriptPath);
            False(initialFailure.IsSuccess,
                "首次失败的显式 shim 不得解析成功");
            True(initialFailure.Candidates.Single().Reason.Contains("退出码 17", StringComparison.Ordinal),
                "首次失败应保留真实退出码");

            File.WriteAllText(scriptPath, "@echo off\r\nexit /b 0\r\n");
            var recoveredResolution = CodexExecutableResolver.Resolve(scriptPath);
            Equal(Path.GetFullPath(scriptPath), recoveredResolution.CodexExecutablePath,
                "失败探测不得永久缓存，修复 shim 后本进程应立即恢复");

            File.WriteAllText(scriptPath, "@echo off\r\nexit /b 19\r\n");
            var cachedSuccess = CodexExecutableResolver.ResolveWithDiagnostics(scriptPath);
            True(cachedSuccess.IsSuccess,
                "成功探测可以缓存以避免每次启动重复执行 --version");

            CodexExecutableResolver.ClearLaunchProbeCache();
            var refreshedFailure = CodexExecutableResolver.ResolveWithDiagnostics(scriptPath);
            False(refreshedFailure.IsSuccess,
                "手动刷新必须清除成功探测缓存并重新验证 Runtime");
            True(refreshedFailure.Candidates.Single().Reason.Contains("退出码 19", StringComparison.Ordinal),
                "刷新后的诊断应显示最新失败原因");
        }
        finally
        {
            CodexExecutableResolver.ClearLaunchProbeCache();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void CodexExecutableResolverPrefersLaunchablePathShimOverPackagedRuntime()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "codex-runtime-resolution",
            Guid.NewGuid().ToString("N"));
        var applicationDirectory = Path.Combine(root, "distribution");
        var desktopRuntimeDirectory = Path.Combine(root, "official-desktop-runtime");
        var legacyDirectory = Path.Combine(root, "legacy-cli");
        try
        {
            Directory.CreateDirectory(applicationDirectory);
            Directory.CreateDirectory(desktopRuntimeDirectory);
            Directory.CreateDirectory(legacyDirectory);
            File.WriteAllBytes(Path.Combine(applicationDirectory, "codex.exe"), [0x01]);
            var expectedShimPath = Path.Combine(legacyDirectory, "codex.cmd");
            File.WriteAllText(expectedShimPath, "@echo off");
            var packagedCodexPath = Path.Combine(desktopRuntimeDirectory, "codex.exe");
            File.WriteAllBytes(packagedCodexPath, [0x02]);
            File.WriteAllBytes(
                Path.Combine(desktopRuntimeDirectory, "codex-code-mode-host.exe"),
                [0x03]);

            var resolution = CodexExecutableResolver.Resolve(
                "codex",
                applicationDirectory,
                string.Join(Path.PathSeparator, desktopRuntimeDirectory, legacyDirectory),
                candidate => string.Equals(
                    candidate.CodexExecutablePath,
                    expectedShimPath,
                    StringComparison.OrdinalIgnoreCase),
                Path.Combine(root, "empty-appdata"),
                Path.Combine(root, "empty-localappdata"));

            Equal(Path.GetFullPath(expectedShimPath), resolution.CodexExecutablePath,
                "PATH 中可启动的 codex.cmd 应覆盖不可直接启动的打包 Runtime");
            Equal(
                Environment.GetEnvironmentVariable("ComSpec") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                resolution.ProcessFileName,
                "PATH shim 应通过命令处理器启动");
            SequenceEqual(
                new[] { "/d", "/s", "/c", "call", Path.GetFullPath(expectedShimPath) },
                resolution.PrefixArguments,
                "PATH shim 启动前缀");
            False(resolution.HasCodeModeCompanion,
                "shim 自行解析原生资源，不应伪报同目录 companion");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void CodexExecutableResolverSkipsUnlaunchableCompleteRuntime()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "codex-runtime-launch-probe",
            Guid.NewGuid().ToString("N"));
        var applicationDirectory = Path.Combine(root, "distribution");
        var blockedRuntimeDirectory = Path.Combine(root, "packaged-runtime-blocked");
        var usableRuntimeDirectory = Path.Combine(root, "portable-runtime-usable");
        try
        {
            Directory.CreateDirectory(applicationDirectory);
            Directory.CreateDirectory(blockedRuntimeDirectory);
            Directory.CreateDirectory(usableRuntimeDirectory);
            var blockedExecutable = Path.Combine(blockedRuntimeDirectory, "codex.exe");
            var usableExecutable = Path.Combine(usableRuntimeDirectory, "codex.exe");
            File.WriteAllBytes(blockedExecutable, [0x01]);
            File.WriteAllBytes(
                Path.Combine(blockedRuntimeDirectory, CodexExecutableResolver.CodeModeHostFileName),
                [0x02]);
            File.WriteAllBytes(usableExecutable, [0x03]);
            File.WriteAllBytes(
                Path.Combine(usableRuntimeDirectory, CodexExecutableResolver.CodeModeHostFileName),
                [0x04]);
            var probedCandidates = new List<string>();

            var resolution = CodexExecutableResolver.Resolve(
                "codex",
                applicationDirectory,
                string.Join(Path.PathSeparator, blockedRuntimeDirectory, usableRuntimeDirectory),
                candidate =>
                {
                    probedCandidates.Add(candidate.CodexExecutablePath);
                    return string.Equals(
                        candidate.CodexExecutablePath,
                        usableExecutable,
                        StringComparison.OrdinalIgnoreCase);
                },
                Path.Combine(root, "empty-appdata"),
                Path.Combine(root, "empty-localappdata"));

            Equal(Path.GetFullPath(usableExecutable), resolution.CodexExecutablePath,
                "启动探测失败的完整 Runtime 必须被跳过");
            SequenceEqual(
                new[] { Path.GetFullPath(blockedExecutable), Path.GetFullPath(usableExecutable) },
                probedCandidates,
                "完整 Runtime 应按 PATH 顺序逐个执行启动探测");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void CodexProcessLaunchFallsBackToLegacyPathScript()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "codex-runtime-legacy",
            Guid.NewGuid().ToString("N"));
        var applicationDirectory = Path.Combine(root, "distribution");
        var legacyDirectory = Path.Combine(root, "legacy-cli");
        try
        {
            Directory.CreateDirectory(applicationDirectory);
            Directory.CreateDirectory(legacyDirectory);
            File.WriteAllBytes(Path.Combine(applicationDirectory, "codex.exe"), [0x01]);
            var legacyScriptPath = Path.Combine(legacyDirectory, "codex.cmd");
            File.WriteAllText(legacyScriptPath, "@echo off");

            var startInfo = CodexProcessLaunch.CreateBaseStartInfo(
                new AgentSessionStartOptions
                {
                    ExecutablePath = "codex",
                    WorkingDirectory = Environment.CurrentDirectory
                },
                applicationDirectory,
                legacyDirectory,
                Path.Combine(root, "empty-appdata"),
                Path.Combine(root, "empty-localappdata"));

            Equal(
                Environment.GetEnvironmentVariable("ComSpec") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                startInfo.FileName,
                "旧版 PATH 脚本应通过命令处理器启动");
            SequenceEqual(
                new[] { "/d", "/s", "/c", "call", Path.GetFullPath(legacyScriptPath) },
                startInfo.ArgumentList,
                "旧版 Codex CLI 脚本启动前缀");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void CodexProcessLaunchExplainsMissingCompanionWithoutFallback()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "codex-runtime-incomplete",
            Guid.NewGuid().ToString("N"));
        var applicationDirectory = Path.Combine(root, "distribution");
        try
        {
            Directory.CreateDirectory(applicationDirectory);
            var localExecutablePath = Path.Combine(applicationDirectory, "codex.exe");
            var expectedCompanionPath = Path.Combine(
                applicationDirectory,
                CodexExecutableResolver.CodeModeHostFileName);
            File.WriteAllBytes(localExecutablePath, [0x01]);

            InvalidOperationException? failure = null;
            try
            {
                _ = CodexProcessLaunch.CreateBaseStartInfo(
                    new AgentSessionStartOptions
                    {
                        ExecutablePath = "codex",
                        WorkingDirectory = Environment.CurrentDirectory
                    },
                    applicationDirectory,
                    pathEnvironment: string.Empty,
                    applicationDataDirectory: Path.Combine(root, "empty-appdata"),
                    localApplicationDataDirectory: Path.Combine(root, "empty-localappdata"));
            }
            catch (InvalidOperationException exception)
            {
                failure = exception;
            }

            True(failure is not null,
                "仅存在不完整本地 Codex Runtime 时必须在启动前失败");
            True(failure!.Message.Contains(localExecutablePath, StringComparison.OrdinalIgnoreCase),
                "启动失败说明必须指出遮蔽完整安装的本地 codex.exe");
            True(failure.Message.Contains(expectedCompanionPath, StringComparison.OrdinalIgnoreCase),
                "启动失败说明必须给出缺失 companion 的完整路径");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task CodexExecDeclaresFallbackCapabilitiesAndBuildsArgumentsAsync()
    {
        var token = $"runtime-smoke-{Guid.NewGuid():N}";
        var endpoint = new Uri("http://127.0.0.1:43120/mcp");
        var factory = new FakeAgentProcessFactory((_, index) =>
        {
            var threadId = index == 0 ? "thread-created-by-exec" : "thread-to-resume";
            var output = string.Join(
                Environment.NewLine,
                JsonSerializer.Serialize(new { type = "thread.started", thread_id = threadId }),
                JsonSerializer.Serialize(new { type = "turn.started", turn_id = $"turn-{index + 1}" }),
                JsonSerializer.Serialize(new { type = "turn.completed" })) + Environment.NewLine;
            return FakeAgentChildProcess.Exited(
                id: 4200 + index,
                new RecordingTextWriter(),
                new StringReader(output),
                exitCode: 0);
        });
        await using var transport = new CodexExecTransport(factory);

        var expectedCapabilities =
            AgentTransportCapabilities.ThreadResume |
            AgentTransportCapabilities.TurnExecution |
            AgentTransportCapabilities.TurnInterrupt |
            AgentTransportCapabilities.McpConfiguration;
        Equal(expectedCapabilities, transport.Capabilities,
            "Codex exec 必须明确声明精简降级能力");
        False(transport.Capabilities.HasFlag(AgentTransportCapabilities.PersistentProcess),
            "Codex exec 不得伪装成常驻进程");
        False(transport.Capabilities.HasFlag(AgentTransportCapabilities.AccountRead),
            "Codex exec 不支持账号读取");
        False(transport.Capabilities.HasFlag(AgentTransportCapabilities.ChatGptLogin),
            "Codex exec 不支持 ChatGPT 登录");
        False(transport.Capabilities.HasFlag(AgentTransportCapabilities.Logout),
            "Codex exec 不支持登出");
        False(transport.Capabilities.HasFlag(AgentTransportCapabilities.ExplicitThreadStart),
            "Codex exec 的新线程只能延迟到首个 Turn 创建");

        var options = CreateOptions(endpoint, token) with
        {
            Model = "gpt-runtime-fixture",
            SandboxMode = "workspace-write",
            ApprovalPolicy = "never",
            AllowSandboxNetworkAccess = true,
            SkipGitRepositoryCheck = true
        };
        await transport.StartAsync(options);
        await ThrowsAsync<NotSupportedException>(
            () => transport.ReadAccountAsync(),
            "Codex exec 账号读取必须明确返回不支持");

        var deferredThread = await transport.StartThreadAsync();
        True(deferredThread.IsDeferred && deferredThread.ThreadId is null,
            "Codex exec StartThread 应声明延迟创建");
        var freshResult = await transport.StartTurnAsync("create a fixture asset");
        Equal("thread-created-by-exec", freshResult.ThreadId,
            "Codex exec 应从 JSONL 提取新线程 id");
        Equal("turn-1", freshResult.TurnId,
            "Codex exec 应从 JSONL 提取新 Turn id");
        True(freshResult.IsSuccess, "Codex exec 新线程 Turn 应完成");

        await transport.ResumeThreadAsync("thread-to-resume");
        var resumedResult = await transport.StartTurnAsync("resume the fixture task");
        Equal("thread-to-resume", resumedResult.ThreadId,
            "Codex exec 恢复 Turn 应保留线程 id");
        Equal("turn-2", resumedResult.TurnId,
            "Codex exec 恢复 Turn id");
        True(resumedResult.IsSuccess, "Codex exec 恢复 Turn 应完成");

        var approvalConfig = $"approval_policy={CodexProcessLaunch.TomlString(options.ApprovalPolicy)}";
        const string networkConfig = "sandbox_workspace_write.network_access=true";
        var urlConfig = $"mcp_servers.gasset_forge.url={CodexProcessLaunch.TomlString(endpoint.AbsoluteUri)}";
        var tokenVariableConfig =
            $"mcp_servers.gasset_forge.bearer_token_env_var={CodexProcessLaunch.TomlString(TestTokenVariable)}";
        var toolApprovalConfig =
            $"mcp_servers.gasset_forge.default_tools_approval_mode={CodexProcessLaunch.TomlString(GenerationMcpToolPolicy.DefaultToolsApprovalMode)}";
        var toolTimeoutConfig =
            $"mcp_servers.gasset_forge.tool_timeout_sec={GenerationMcpToolPolicy.ToolTimeoutSeconds}";
        var enabledToolsConfig =
            $"mcp_servers.gasset_forge.enabled_tools={GenerationMcpToolPolicy.BuildTomlToolArray()}";
        SequenceEqual(
            new[]
            {
                "exec",
                "--json",
                "--skip-git-repo-check",
                "-m",
                "gpt-runtime-fixture",
                "-s",
                "workspace-write",
                "-c",
                approvalConfig,
                "-c",
                networkConfig,
                "-c",
                urlConfig,
                "-c",
                tokenVariableConfig,
                "-c",
                "mcp_servers.gasset_forge.required=true",
                "-c",
                toolTimeoutConfig,
                "-c",
                enabledToolsConfig,
                "-c",
                toolApprovalConfig,
                "-"
            },
            factory.StartInfos[0].ArgumentList,
            "Codex exec 新线程参数顺序");
        SequenceEqual(
            new[]
            {
                "exec",
                "resume",
                "--json",
                "--skip-git-repo-check",
                "-m",
                "gpt-runtime-fixture",
                "-c",
                approvalConfig,
                "-c",
                networkConfig,
                "-c",
                urlConfig,
                "-c",
                tokenVariableConfig,
                "-c",
                "mcp_servers.gasset_forge.required=true",
                "-c",
                toolTimeoutConfig,
                "-c",
                enabledToolsConfig,
                "-c",
                toolApprovalConfig,
                "thread-to-resume",
                "-"
            },
            factory.StartInfos[1].ArgumentList,
            "Codex exec resume 参数顺序");

        foreach (var startInfo in factory.StartInfos)
        {
            False(startInfo.ArgumentList.Any(argument => argument.Contains(token, StringComparison.Ordinal)),
                "Codex exec 参数不得包含 Bearer Token");
            Equal(token, startInfo.Environment[TestTokenVariable],
                "Codex exec 子进程应从环境变量读取 Bearer Token");
        }

        Equal("create a fixture asset",
            ((RecordingTextWriter)factory.Processes[0].StandardInput).WrittenText,
            "Codex exec 新线程 prompt 应从 stdin 输入");
        Equal("resume the fixture task",
            ((RecordingTextWriter)factory.Processes[1].StandardInput).WrittenText,
            "Codex exec resume prompt 应从 stdin 输入");
    }

    private static async Task CodexExecPromotesActualTurnIdentityForIdlessItemsAsync()
    {
        const string actualTurnId = "turn-promoted-exec";
        var output = string.Join(
            Environment.NewLine,
            JsonSerializer.Serialize(new
            {
                type = "thread.started",
                thread_id = "thread-promoted-exec"
            }),
            JsonSerializer.Serialize(new
            {
                type = "turn.started",
                turn_id = actualTurnId
            }),
            JsonSerializer.Serialize(new
            {
                type = "item.completed",
                item = new
                {
                    id = "message-promoted-exec",
                    type = "agent_message",
                    text = "Exec fallback 正文"
                }
            }),
            JsonSerializer.Serialize(new { type = "turn.completed" })) + Environment.NewLine;
        var factory = new FakeAgentProcessFactory((_, _) => FakeAgentChildProcess.Exited(
            id: 4245,
            new RecordingTextWriter(),
            new StringReader(output),
            exitCode: 0));
        await using var transport = new CodexExecTransport(factory);
        string? activeTurnIdAtAgentMessage = null;
        transport.NotificationReceived += (_, eventArgs) =>
        {
            if (string.Equals(eventArgs.Method, "item.completed", StringComparison.Ordinal))
            {
                activeTurnIdAtAgentMessage = transport.ActiveTurnId;
            }
        };
        await transport.StartAsync(new AgentSessionStartOptions
        {
            ExecutablePath = "codex-fixture",
            WorkingDirectory = Environment.CurrentDirectory,
            RequestTimeout = TimeSpan.FromSeconds(3)
        });

        var result = await transport.StartTurnAsync("promote the actual exec turn id");

        Equal(actualTurnId, result.TurnId,
            "Codex exec 最终结果应返回 turn.started 分配的真实 TurnId");
        Equal(actualTurnId, activeTurnIdAtAgentMessage,
            "缺少 turn_id 的 Exec item 必须继承真实 TurnId，而不是本地 exec-* 标识");
    }

    private static async Task CodexExecAcceptsConcatenatedJsonAndRedactsInvalidTailAsync()
    {
        var threadStarted = JsonSerializer.Serialize(new
        {
            type = "thread.started",
            thread_id = "thread-concatenated-exec"
        });
        var turnStarted = JsonSerializer.Serialize(new
        {
            type = "turn.started",
            turn_id = "turn-concatenated-exec"
        });
        var turnCompleted = JsonSerializer.Serialize(new { type = "turn.completed" });
        const string sensitiveTail = "event: exec-private-tail-must-not-be-logged";
        var output = string.Concat(
            threadStarted,
            " \t ",
            turnStarted,
            turnCompleted,
            sensitiveTail,
            Environment.NewLine);
        var factory = new FakeAgentProcessFactory((_, _) => FakeAgentChildProcess.Exited(
            id: 4250,
            new RecordingTextWriter(),
            new StringReader(output),
            exitCode: 0));
        await using var transport = new CodexExecTransport(factory);
        var notifications = new List<string>();
        var logs = new List<string>();
        transport.NotificationReceived += (_, args) => notifications.Add(args.Method);
        transport.LogReceived += (_, args) => logs.Add(args.Message);
        await transport.StartAsync(new AgentSessionStartOptions
        {
            ExecutablePath = "codex-fixture",
            WorkingDirectory = Environment.CurrentDirectory,
            RequestTimeout = TimeSpan.FromSeconds(3)
        });

        var result = await transport.StartTurnAsync("parse concatenated exec fixture");

        Equal("thread-concatenated-exec", result.ThreadId,
            "Codex exec 必须保留同一物理行第一条 JSON 消息");
        Equal("turn-concatenated-exec", result.TurnId,
            "Codex exec 必须保留同一物理行后续 JSON 消息");
        True(result.IsSuccess, "合法 JSON 后的非协议尾部不得破坏已完成 Turn");
        SequenceEqual(
            new[] { "thread.started", "turn.started", "turn.completed" },
            notifications,
            "非协议尾部不得进入 Agent 正文或阻止前方 JSON 事件派发");
        True(logs.Any(message =>
                message.Contains("sha256=", StringComparison.Ordinal) &&
                message.Contains("remainingBytes=", StringComparison.Ordinal)),
            "Codex exec 非协议尾部应形成脱敏诊断");
        False(logs.Any(message => message.Contains(sensitiveTail, StringComparison.Ordinal)),
            "Codex exec 日志不得记录 raw 非协议尾部");
        False(logs.Any(message => message.Contains("exec-private-tail", StringComparison.Ordinal)),
            "Codex exec 日志不得泄露尾部敏感词");
    }

    private static async Task CodexExecBoundsAndSummarizesRepeatedInvalidInputAsync()
    {
        const string sensitiveLine = "event: repeated-exec-private-protocol-line";
        var outputLines = new List<string>
        {
            JsonSerializer.Serialize(new
            {
                type = "thread.started",
                thread_id = "thread-bounded-exec"
            }),
            JsonSerializer.Serialize(new
            {
                type = "turn.started",
                turn_id = "turn-bounded-exec"
            })
        };
        outputLines.AddRange(Enumerable.Repeat(sensitiveLine, 1_000));
        outputLines.Add(JsonSerializer.Serialize(new { type = "turn.completed" }));
        var output = string.Join(Environment.NewLine, outputLines) + Environment.NewLine;
        var factory = new FakeAgentProcessFactory((_, _) => FakeAgentChildProcess.Exited(
            id: 4260,
            new RecordingTextWriter(),
            new StringReader(output),
            exitCode: 0));
        await using var transport = new CodexExecTransport(factory);
        var logs = new List<string>();
        transport.LogReceived += (_, args) => logs.Add(args.Message);
        await transport.StartAsync(new AgentSessionStartOptions
        {
            ExecutablePath = "codex-fixture",
            WorkingDirectory = Environment.CurrentDirectory,
            RequestTimeout = TimeSpan.FromSeconds(3)
        });

        var result = await transport.StartTurnAsync("bound repeated exec diagnostics");
        var protocolWarnings = logs
            .Where(message => message.Contains("非协议", StringComparison.Ordinal))
            .ToArray();

        Equal("thread-bounded-exec", result.ThreadId,
            "重复非协议行不得阻止 Codex exec 线程事件");
        Equal("turn-bounded-exec", result.TurnId,
            "重复非协议行不得阻止 Codex exec Turn 事件");
        True(result.IsSuccess, "重复非协议行不得破坏后续完成事件");
        True(protocolWarnings.Length <= 5,
            "Codex exec 重复非协议行必须指数限频");
        True(protocolWarnings.Any(message =>
                message.Contains("total=1000", StringComparison.Ordinal) &&
                message.Contains("suppressed=999", StringComparison.Ordinal)),
            "Codex exec 必须汇总重复非协议行数量");
        False(protocolWarnings.Any(message => message.Contains(sensitiveLine, StringComparison.Ordinal)),
            "Codex exec 限频日志不得记录 raw 输入");
        False(protocolWarnings.Any(message => message.Contains("repeated-exec-private", StringComparison.Ordinal)),
            "Codex exec 限频日志不得泄露敏感词");
    }

    private static async Task CodexExecCancellationKillsTheEntireProcessTreeAsync()
    {
        var output = new AsyncLineTextReader();
        var process = new FakeAgentChildProcess(
            id: 4301,
            new RecordingTextWriter(),
            output,
            new StringReader(string.Empty),
            onExit: output.Complete);
        var factory = new FakeAgentProcessFactory((_, _) => process);
        await using var transport = new CodexExecTransport(factory);
        await transport.StartAsync(new AgentSessionStartOptions
        {
            ExecutablePath = "codex-fixture",
            WorkingDirectory = Environment.CurrentDirectory,
            RequestTimeout = TimeSpan.FromSeconds(3)
        });

        using var cancellation = new CancellationTokenSource();
        var turn = transport.StartTurnAsync("cancel this fixture turn", cancellation.Token);
        await WaitUntilAsync(
            () => factory.Processes.Count == 1 &&
                  transport.State == AgentSessionTransportState.RunningTurn &&
                  ((RecordingTextWriter)process.StandardInput).WrittenText.Length > 0,
            TimeSpan.FromSeconds(2),
            "Codex exec cancellation fixture 未进入 RunningTurn。");
        cancellation.Cancel();

        await ThrowsAsync<OperationCanceledException>(
            () => turn.WaitAsync(TimeSpan.FromSeconds(3)),
            "取消 Codex exec Turn 必须传播 OperationCanceledException");
        True(process.KillCallCount > 0, "取消 Codex exec Turn 必须终止子进程");
        True(process.LastKillUsedEntireProcessTree,
            "取消 Codex exec Turn 必须终止整个进程树");
        True(process.Disposed, "取消完成后必须释放 Codex exec 子进程");
        Equal<string?>(null, transport.ActiveTurnId,
            "取消完成后必须清除 ActiveTurnId");
        Equal(AgentSessionTransportState.Ready, transport.State,
            "可继续使用的 exec transport 取消后应回到 Ready");
    }

    private static AgentSessionStartOptions CreateOptions(Uri endpoint, string token) => new()
    {
        ExecutablePath = "codex-fixture",
        WorkingDirectory = Environment.CurrentDirectory,
        McpEndpoint = endpoint,
        McpBearerTokenEnvironmentVariableName = TestTokenVariable,
        McpBearerToken = token,
        RequestTimeout = TimeSpan.FromSeconds(3)
    };

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string message)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        if (!condition())
        {
            throw new InvalidOperationException(message);
        }
    }

    private static TException Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"{message}：预期 {typeof(TException).Name}，实际 {exception.GetType().Name}。",
                exception);
        }

        throw new InvalidOperationException($"{message}：没有抛出 {typeof(TException).Name}。");
    }

    private static async Task<TException> ThrowsAsync<TException>(
        Func<Task> action,
        string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"{message}：预期 {typeof(TException).Name}，实际 {exception.GetType().Name}。",
                exception);
        }

        throw new InvalidOperationException($"{message}：没有抛出 {typeof(TException).Name}。");
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

    private static void SequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string message)
    {
        var expectedItems = expected.ToArray();
        var actualItems = actual.ToArray();
        if (!expectedItems.SequenceEqual(actualItems))
        {
            throw new InvalidOperationException(
                $"{message}：预期 [{string.Join(", ", expectedItems)}]，" +
                $"实际 [{string.Join(", ", actualItems)}]。");
        }
    }

    private sealed class RecordingTextWriter : TextWriter
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];
        private readonly StringBuilder _writtenText = new();
        private readonly Func<string, ValueTask>? _lineWritten;

        internal RecordingTextWriter(Func<string, ValueTask>? lineWritten = null)
        {
            _lineWritten = lineWritten;
        }

        public override Encoding Encoding => Encoding.UTF8;

        internal IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                {
                    return _lines.ToArray();
                }
            }
        }

        internal string WrittenText
        {
            get
            {
                lock (_gate)
                {
                    return _writtenText.ToString();
                }
            }
        }

        public override Task WriteAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _writtenText.Append(buffer.Span);
            }

            return Task.CompletedTask;
        }

        public override Task WriteLineAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = buffer.ToString();
            lock (_gate)
            {
                _lines.Add(line);
                _writtenText.Append(line).Append(NewLine);
            }

            return _lineWritten?.Invoke(line).AsTask() ?? Task.CompletedTask;
        }
    }

    private sealed class AsyncLineTextReader : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        internal void EnqueueLine(string line)
        {
            if (!_lines.Writer.TryWrite(line))
            {
                throw new InvalidOperationException("Fixture stdout 已关闭。");
            }
        }

        internal void Complete() => _lines.Writer.TryComplete();

        public override async ValueTask<string?> ReadLineAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                return await _lines.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Complete();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FakeAgentProcessFactory(
        Func<ProcessStartInfo, int, FakeAgentChildProcess> processBuilder) : IAgentProcessFactory
    {
        private readonly object _gate = new();
        private readonly Func<ProcessStartInfo, int, FakeAgentChildProcess> _processBuilder =
            processBuilder;
        private readonly List<ProcessStartInfo> _startInfos = [];
        private readonly List<FakeAgentChildProcess> _processes = [];

        internal IReadOnlyList<ProcessStartInfo> StartInfos
        {
            get
            {
                lock (_gate)
                {
                    return _startInfos.ToArray();
                }
            }
        }

        internal IReadOnlyList<FakeAgentChildProcess> Processes
        {
            get
            {
                lock (_gate)
                {
                    return _processes.ToArray();
                }
            }
        }

        public IAgentChildProcess Start(ProcessStartInfo startInfo)
        {
            lock (_gate)
            {
                var process = _processBuilder(startInfo, _startInfos.Count);
                _startInfos.Add(startInfo);
                _processes.Add(process);
                return process;
            }
        }
    }

    private sealed class FakeAgentChildProcess : IAgentChildProcess
    {
        private readonly TaskCompletionSource _exit = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Action? _onExit;
        private int _hasExited;
        private int _exitCode;
        private int _killCallCount;
        private int _disposed;

        internal FakeAgentChildProcess(
            int id,
            TextWriter standardInput,
            TextReader standardOutput,
            TextReader standardError,
            Action? onExit = null)
        {
            Id = id;
            StandardInput = standardInput;
            StandardOutput = standardOutput;
            StandardError = standardError;
            _onExit = onExit;
        }

        internal static FakeAgentChildProcess Exited(
            int id,
            TextWriter standardInput,
            TextReader standardOutput,
            int exitCode)
        {
            var process = new FakeAgentChildProcess(
                id,
                standardInput,
                standardOutput,
                new StringReader(string.Empty));
            process.CompleteExit(exitCode);
            return process;
        }

        public int Id { get; }

        public bool HasExited => Volatile.Read(ref _hasExited) != 0;

        public int ExitCode => Volatile.Read(ref _exitCode);

        public TextWriter StandardInput { get; }

        public TextReader StandardOutput { get; }

        public TextReader StandardError { get; }

        internal int KillCallCount => Volatile.Read(ref _killCallCount);

        internal bool LastKillUsedEntireProcessTree { get; private set; }

        internal bool Disposed => Volatile.Read(ref _disposed) != 0;

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            await _exit.Task.WaitAsync(cancellationToken);
        }

        public void Kill(bool entireProcessTree)
        {
            Interlocked.Increment(ref _killCallCount);
            LastKillUsedEntireProcessTree = entireProcessTree;
            CompleteExit(137);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }

        private void CompleteExit(int exitCode)
        {
            Volatile.Write(ref _exitCode, exitCode);
            if (Interlocked.Exchange(ref _hasExited, 1) != 0)
            {
                return;
            }

            _onExit?.Invoke();
            _exit.TrySetResult();
        }
    }
}

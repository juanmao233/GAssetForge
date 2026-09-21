using System.Reflection;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using GAssetForge.App.Services;
using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.Core;

namespace GAssetForge.SmokeTests;

internal static class AgentMcpReadinessSmokeTests
{
    public static void Run()
    {
        FreshThreadWaitsForItsDelayedMcpReady().GetAwaiter().GetResult();
        StartingDoesNotReuseCompletedReadiness().GetAwaiter().GetResult();
        FreshThreadBuffersEarlyReadyAndIgnoresOtherThreads().GetAwaiter().GetResult();
        DuplicateStartingKeepsWaitersAndUnrelatedMcpCannotReleaseThem().GetAwaiter().GetResult();
        FailureAndMcpCancellationKeepDiagnosticIdentity().GetAwaiter().GetResult();
        CallerCancellationAndStopWakeFreshThreadWaiters().GetAwaiter().GetResult();
        CancellationBeforeThreadResponseRequiresExplicitReconnect().GetAwaiter().GetResult();
        TransportFailureWakesCurrentWaiter().GetAwaiter().GetResult();
        TimeoutIsSpecificAndLateReadyCanRecover().GetAwaiter().GetResult();
        InitialWaitBeforeThreadResponseDoesNotBindNull().GetAwaiter().GetResult();
        StopDuringAccountRefreshCannotStartAnotherThread().GetAwaiter().GetResult();
        LateAccountReadCannotOverwriteReplacementSession().GetAwaiter().GetResult();
    }

    private static async Task FreshThreadWaitsForItsDelayedMcpReady()
    {
        var transport = new ReadinessTransport();
        await using var runtime = await CreateReadyRuntimeAsync(transport);
        transport.StartThreadHandler = () =>
        {
            transport.CurrentThreadId = "fresh-thread";
            transport.Report("starting", "fresh-thread");
            return new AgentThreadResult("fresh-thread");
        };

        var fresh = runtime.StartFreshThreadAsync();
        True(!fresh.IsCompleted && !runtime.McpReady,
            "thread/start 返回时 MCP 仍在 starting，批次准备必须继续等待而不是立即失败。");
        transport.Report("ready", "fresh-thread");
        Equal("fresh-thread", (await fresh.WaitAsync(TimeSpan.FromSeconds(2))).ThreadId,
            "新线程 ready 后必须释放原批次准备。");
    }

    private static async Task StartingDoesNotReuseCompletedReadiness()
    {
        var transport = new ReadinessTransport();
        await using var runtime = await CreateReadyRuntimeAsync(transport);
        transport.Report("starting", "old-thread");
        var wait = WaitForReadiness(runtime);
        True(!wait.IsCompleted && !runtime.McpReady,
            "新一轮 starting 不得复用上一轮已经完成的 readiness TCS。");
        transport.Report("ready", "old-thread");
        await wait.WaitAsync(TimeSpan.FromSeconds(2));
        True(runtime.McpReady, "只有当前周期真实 ready 才能完成 readiness 等待。");
    }

    private static async Task FreshThreadBuffersEarlyReadyAndIgnoresOtherThreads()
    {
        var transport = new ReadinessTransport();
        await using var runtime = await CreateReadyRuntimeAsync(transport);
        transport.StartThreadHandler = () =>
        {
            transport.CurrentThreadId = "fresh-thread";
            transport.Report("starting", "fresh-thread");
            transport.Report("ready", "fresh-thread");
            transport.Report("failed", "old-thread", "old-thread failure");
            return new AgentThreadResult("fresh-thread");
        };

        await runtime.StartFreshThreadAsync().WaitAsync(TimeSpan.FromSeconds(2));
        True(runtime.McpReady, "thread/start 响应前到达的当前线程 ready 必须被保留。");
        transport.Report("starting", "old-thread");
        transport.Report("failed", "old-thread", "late old failure");
        transport.Report("failed", "fresh-thread", "unrelated server", "other_mcp");
        True(runtime.McpReady && runtime.LastError is null,
            "旧线程或无关 MCP 的迟到状态不得污染当前线程 readiness 和错误状态。");
    }

    private static async Task DuplicateStartingKeepsWaitersAndUnrelatedMcpCannotReleaseThem()
    {
        var transport = new ReadinessTransport();
        await using var runtime = await CreateReadyRuntimeAsync(transport);
        ConfigureDelayedFreshThread(transport);
        var fresh = runtime.StartFreshThreadAsync();
        var waiter = runtime.WaitForExecutionReadinessAsync();
        transport.Report("starting", "fresh-thread");
        transport.Report("starting", "fresh-thread");
        transport.Report("ready", "old-thread");
        transport.Report("ready", null);
        transport.Report("ready", "fresh-thread", serverName: "other_mcp");
        True(!fresh.IsCompleted && !waiter.IsCompleted && !runtime.McpReady,
            "重复 starting 应共享原 waiter，旧线程、无 threadId、无关 MCP ready 都不得提前释放。");
        transport.Report("ready", "fresh-thread");
        await Task.WhenAll(fresh, waiter).WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task FailureAndMcpCancellationKeepDiagnosticIdentity()
    {
        foreach (var (status, code) in new[]
                 {
                     ("failed", "agent_mcp_failed"),
                     ("cancelled", "agent_mcp_cancelled")
                 })
        {
            var transport = new ReadinessTransport();
            await using var runtime = await CreateReadyRuntimeAsync(transport);
            typeof(CodexAgentRuntimeService)
                .GetField("_sessionSecret", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(runtime, "fixture-mcp-secret");
            ConfigureDelayedFreshThread(transport);
            var fresh = runtime.StartFreshThreadAsync();
            transport.Report(status, "fresh-thread", "fixture-mcp-secret initialization failure");
            var failure = await ExpectReadinessFailure(fresh, code);
            Equal("fresh-thread", failure.ThreadId, "失败必须关联实际新线程。");
            Equal("mcp.readiness", failure.Stage, "MCP 初始化错误不得误标为认证失败。");
            True(!failure.Message.Contains("fixture-mcp-secret", StringComparison.Ordinal),
                "MCP 失败详情中的会话凭据必须脱敏。");
            Equal(CodexAgentAuthenticationState.NotRequired, runtime.AuthenticationState,
                "MCP 初始化失败不应更改账号认证状态。");
        }
    }

    private static async Task CallerCancellationAndStopWakeFreshThreadWaiters()
    {
        var transport = new ReadinessTransport();
        await using var runtime = await CreateReadyRuntimeAsync(transport);
        ConfigureDelayedFreshThread(transport);
        using var cancellation = new CancellationTokenSource();
        var cancelledFresh = runtime.StartFreshThreadAsync(cancellation.Token);
        cancellation.Cancel();
        try
        {
            await cancelledFresh.WaitAsync(TimeSpan.FromSeconds(2));
            throw new InvalidOperationException("调用者取消必须终止当前 readiness 等待。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        transport.Report("ready", "fresh-thread");
        await runtime.WaitForExecutionReadinessAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var fresh = runtime.StartFreshThreadAsync();
        var stop = runtime.StopAsync();
        await ExpectReadinessFailure(fresh, "agent_transport_stopped");
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
        True(!runtime.McpReady && !runtime.IsConnected,
            "Stop 必须立即唤醒持有 session gate 的新线程等待，不可等待 20 秒超时。");
    }

    private static async Task TimeoutIsSpecificAndLateReadyCanRecover()
    {
        var transport = new ReadinessTransport();
        await using var runtime = await CreateReadyRuntimeAsync(transport, TimeSpan.FromMilliseconds(50));
        ConfigureDelayedFreshThread(transport);
        var failure = await ExpectReadinessFailure(runtime.StartFreshThreadAsync(), "agent_mcp_ready_timeout");
        True(failure.Message.Contains("Turn 尚未提交", StringComparison.Ordinal) &&
             failure.OccurredAt != default && failure.StateSummary.Contains("mcp=starting", StringComparison.Ordinal),
            "超时必须说明尚未提交 Turn，并携带事件发生时间和不含密钥的状态摘要。");
        transport.Report("ready", "fresh-thread");
        await runtime.WaitForExecutionReadinessAsync().WaitAsync(TimeSpan.FromSeconds(2));
        True(runtime.McpReady, "超时之后真实 ready 仍可恢复当前原线程，无需创建替代任务。");
    }

    private static async Task CancellationBeforeThreadResponseRequiresExplicitReconnect()
    {
        var transport = new ReadinessTransport();
        await using var runtime = await CreateReadyRuntimeAsync(transport);
        var remoteResponse = new TaskCompletionSource<AgentThreadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.StartThreadAsyncHandler = _ => remoteResponse.Task;
        using var cancellation = new CancellationTokenSource();
        var fresh = runtime.StartFreshThreadAsync(cancellation.Token);
        Equal(1, transport.StartThreadCallCount, "fixture 必须已发出尚未返回的 thread/start。");
        cancellation.Cancel();
        try
        {
            await fresh.WaitAsync(TimeSpan.FromSeconds(2));
            throw new InvalidOperationException("thread/start 未返回时用户取消必须立即结束等待。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        var failure = await ExpectReadinessFailure(runtime.WaitForExecutionReadinessAsync(),
            "agent_thread_start_cancelled");
        True(failure.Message.Contains("结果未知", StringComparison.Ordinal) &&
             failure.RecoveryHint.Contains("重新连接", StringComparison.Ordinal),
            "取消无法确认远端线程创建结果时必须明确要求重连。");
        transport.CurrentThreadId = "unconfirmed-thread";
        transport.Report("ready", "old-thread");
        transport.Report("ready", "unconfirmed-thread");
        remoteResponse.TrySetResult(new AgentThreadResult("unconfirmed-thread"));
        True(!runtime.McpReady, "取消之后迟到的旧/新 ready 或 RPC 响应不可复活不确定会话。");
        await ExpectReadinessFailure(runtime.StartFreshThreadAsync(), "agent_thread_start_cancelled");
        Equal(1, transport.StartThreadCallCount, "必须释放 gate，但不可自动重试或重复发出 thread/start。");
    }

    private static async Task TransportFailureWakesCurrentWaiter()
    {
        var transport = new ReadinessTransport();
        await using var runtime = await CreateReadyRuntimeAsync(transport);
        ConfigureDelayedFreshThread(transport);
        var fresh = runtime.StartFreshThreadAsync();
        transport.Fault();
        var failure = await ExpectReadinessFailure(fresh, "agent_transport_faulted");
        Equal("runtime", failure.Stage, "传输进程故障应保留 runtime 阶段，而不是被归为 MCP 超时。");
        transport.Report("ready", "fresh-thread");
        True(!runtime.McpReady, "已停止或故障的传输不能被迟到 ready 通知重新标记为可执行。");
    }

    private static async Task InitialWaitBeforeThreadResponseDoesNotBindNull()
    {
        var transport = new ReadinessTransport();
        await using var runtime = await CreateReadyRuntimeAsync(transport);
        var tracker = (AgentMcpReadinessTracker)typeof(CodexAgentRuntimeService)
            .GetField("_mcpReadiness", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
        tracker.Begin(allowUnscoped: true);
        transport.CurrentThreadId = null;
        var earlyWaiter = runtime.WaitForExecutionReadinessAsync();
        True(!earlyWaiter.IsCompleted, "首次 thread/start 响应前的 readiness 等待必须保持未绑定。");
        transport.Report("ready", "initial-thread");
        transport.CurrentThreadId = "initial-thread";
        tracker.BindInitialThread(transport.CurrentThreadId);
        await earlyWaiter.WaitAsync(TimeSpan.FromSeconds(2));
        True(runtime.McpReady && tracker.ThreadId == "initial-thread",
            "早调用等待不能把 null 绑定成最终 threadId；真实响应仍必须能绑定并接收早到 ready。");
    }

    private static async Task StopDuringAccountRefreshCannotStartAnotherThread()
    {
        var transport = new ReadinessTransport();
        await using var runtime = await CreateReadyRuntimeAsync(transport);
        var accountResponse = new TaskCompletionSource<AgentAccountState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.ReadAccountHandler = _ => accountResponse.Task;

        // The MCP descriptor only answers existing-process/endpoint property checks.
        // It never starts, stops, disposes, or sends HTTP to the sentinel current process.
        var server = (McpServerProcessService)RuntimeHelpers.GetUninitializedObject(typeof(McpServerProcessService));
        SetField(server, "_stateLock", new object());
        SetField(server, "_process", Process.GetCurrentProcess());
        SetField(server, "_state", McpServerProcessState.WaitingForAgent);
        SetField(server, "_port", 43120);
        var workingDirectory = Environment.CurrentDirectory;
        SetField(runtime, "_sessionWorkingDirectory", workingDirectory);
        SetField(runtime, "_sessionMcpEndpoint", server.Endpoint!);
        SetField(runtime, "_sessionEnvironmentFingerprint",
            ProcessEnvironmentSnapshot.ComputeEnvironmentFingerprint(new Dictionary<string, string?>()));
        typeof(CodexAgentRuntimeService).GetProperty(nameof(runtime.AuthenticationState))!
            .SetValue(runtime, CodexAgentAuthenticationState.Required);

        var ensure = runtime.EnsureSessionAsync(workingDirectory, "another-thread", server);
        Equal(1, transport.ReadAccountCallCount, "fixture 必须正在等待已有会话的账号刷新。");
        var stop = runtime.StopAsync();
        await ExpectReadinessFailure(ensure, "agent_transport_stopped");
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
        accountResponse.TrySetResult(new AgentAccountState(false, true, "fixture", null, null));
        Equal(0, transport.StartThreadCallCount + transport.ResumeThreadCallCount,
            "Stop 之后迟到的账号响应不得继续 SelectThread、开启新readiness周期或启动降级通道。");
        True(!runtime.McpReady && !runtime.IsConnected,
            "Stop 不得被仍持session gate的异步会话准备覆盖。");
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static async Task LateAccountReadCannotOverwriteReplacementSession()
    {
        foreach (var cancelCaller in new[] { false, true })
        {
            var transport = new ReadinessTransport();
            await using var runtime = await CreateReadyRuntimeAsync(transport);
            var oldResponse = new TaskCompletionSource<AgentAccountState>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            transport.ReadAccountHandler = _ => oldResponse.Task;
            using var cancellation = new CancellationTokenSource();
            var oldRead = runtime.RefreshAccountAsync(cancellation.Token);
            await runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

            // A new session can appear before an uncooperative old read finishes.
            // Await the actual old publication task to make this regression deterministic.
            SetField(runtime, "_transport", new ReadinessTransport());
            var replacementAccount = new AgentAccountState(true, false, "replacement", null, null);
            typeof(CodexAgentRuntimeService).GetProperty(nameof(runtime.Account))!
                .SetValue(runtime, replacementAccount);
            typeof(CodexAgentRuntimeService).GetProperty(nameof(runtime.AuthenticationState))!
                .SetValue(runtime, CodexAgentAuthenticationState.Required);
            if (cancelCaller)
            {
                cancellation.Cancel();
            }

            oldResponse.TrySetResult(new AgentAccountState(false, true, "stale-old-session", null, null));
            if (cancelCaller)
            {
                try
                {
                    await oldRead.WaitAsync(TimeSpan.FromSeconds(2));
                    throw new InvalidOperationException("已取消的旧账号读取不得发布状态。");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
            }
            else
            {
                await ExpectReadinessFailure(oldRead, "agent_transport_stopped");
            }

            True(ReferenceEquals(replacementAccount, runtime.Account),
                "迟到的旧账号响应不得覆盖替代会话 Account。");
            Equal(CodexAgentAuthenticationState.Required, runtime.AuthenticationState,
                "迟到的旧账号响应不得把替代会话错误提升为已认证。");
        }
    }

    private static void ConfigureDelayedFreshThread(ReadinessTransport transport) =>
        transport.StartThreadHandler = () =>
        {
            transport.CurrentThreadId = "fresh-thread";
            transport.Report("starting", "fresh-thread");
            return new AgentThreadResult("fresh-thread");
        };

    private static async Task<AgentReadinessException> ExpectReadinessFailure(Task task, string code)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (AgentReadinessException exception)
        {
            Equal(code, exception.Code, "就绪失败必须保留明确错误码。");
            return exception;
        }

        throw new InvalidOperationException($"预期 readiness 错误 {code}，实际成功。");
    }

    // Construct only the already-connected runtime seam. No process, MCP HTTP server,
    // user settings, credentials, filesystem, or external Agent participates in these tests.
    private static async Task<CodexAgentRuntimeService> CreateReadyRuntimeAsync(
        ReadinessTransport transport,
        TimeSpan? timeout = null)
    {
        var runtime = new CodexAgentRuntimeService("fixture-never-launched",
            () => transport,
            () => throw new InvalidOperationException("MCP readiness 不得触发兼容性降级。"),
            pathEnvironmentProvider: static () => string.Empty,
            environmentVariablesProvider: static () => new Dictionary<string, string?>(),
            mcpReadyTimeout: timeout);
        var type = typeof(CodexAgentRuntimeService);
        type.GetField("_transport", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(runtime, transport);
        type.GetProperty(nameof(runtime.TransportMode))!
            .SetValue(runtime, CodexAgentTransportMode.AppServer);
        type.GetProperty(nameof(runtime.AuthenticationState))!
            .SetValue(runtime, CodexAgentAuthenticationState.NotRequired);
        type.GetMethod("AttachTransport", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, [transport]);
        transport.Report("starting", "old-thread");
        var initial = WaitForReadiness(runtime);
        transport.Report("ready", "old-thread");
        await initial.WaitAsync(TimeSpan.FromSeconds(2));
        return runtime;
    }

    private static Task WaitForReadiness(CodexAgentRuntimeService runtime) =>
        (Task)typeof(CodexAgentRuntimeService)
            .GetMethod("WaitForMcpReadyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, [CancellationToken.None])!;

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message) =>
        True(EqualityComparer<T>.Default.Equals(expected, actual),
            $"{message} expected={expected}, actual={actual}");

    private sealed class ReadinessTransport : IAgentSessionTransport
    {
        public string DisplayName => "readiness fixture";
        public AgentTransportCapabilities Capabilities =>
            AgentTransportCapabilities.PersistentProcess |
            AgentTransportCapabilities.ExplicitThreadStart |
            AgentTransportCapabilities.AccountRead |
            AgentTransportCapabilities.TurnExecution |
            AgentTransportCapabilities.McpConfiguration;
        public AgentSessionTransportState State { get; private set; } = AgentSessionTransportState.Ready;
        public bool IsRunning => State == AgentSessionTransportState.Ready;
        public string? RuntimeVersion => "fixture";
        public string? CurrentThreadId { get; set; } = "old-thread";
        public string? ActiveTurnId => null;
        public Func<AgentThreadResult>? StartThreadHandler { get; set; }
        public Func<CancellationToken, Task<AgentThreadResult>>? StartThreadAsyncHandler { get; set; }
        public int StartThreadCallCount { get; private set; }
        public int ResumeThreadCallCount { get; private set; }
        public int ReadAccountCallCount { get; private set; }
        public Func<CancellationToken, Task<AgentAccountState>>? ReadAccountHandler { get; set; }

        public event EventHandler<AgentTransportStateChangedEventArgs>? StateChanged;
        public event EventHandler<AgentTransportLogEventArgs>? LogReceived { add { } remove { } }
        public event EventHandler<AgentNotificationEventArgs>? NotificationReceived { add { } remove { } }
        public event EventHandler<AgentTurnCompletedEventArgs>? TurnCompleted { add { } remove { } }
        public event EventHandler<AgentMcpStatusChangedEventArgs>? McpStatusChanged;

        public void Report(string status, string? threadId, string? error = null,
            string serverName = ProductIdentity.McpServerName) =>
            McpStatusChanged?.Invoke(this,
                new AgentMcpStatusChangedEventArgs(serverName, status, error, threadId));

        public void Fault()
        {
            State = AgentSessionTransportState.Faulted;
            StateChanged?.Invoke(this, new AgentTransportStateChangedEventArgs(State, "fixture transport fault"));
        }

        public Task StartAsync(AgentSessionStartOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("No external process may start in a readiness fixture.");
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            State = AgentSessionTransportState.Stopped;
            StateChanged?.Invoke(this, new AgentTransportStateChangedEventArgs(State, "fixture stopped"));
            return Task.CompletedTask;
        }
        public Task<AgentThreadResult> StartThreadAsync(CancellationToken cancellationToken = default)
        {
            StartThreadCallCount++;
            return StartThreadAsyncHandler?.Invoke(cancellationToken) ??
                Task.FromResult(StartThreadHandler?.Invoke() ?? new AgentThreadResult(CurrentThreadId));
        }
        public Task<AgentThreadResult> ResumeThreadAsync(string threadId, CancellationToken cancellationToken = default)
        {
            ResumeThreadCallCount++;
            throw new NotSupportedException();
        }
        public Task<AgentTurnResult> StartTurnAsync(string prompt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task InterruptTurnAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AgentAccountState> ReadAccountAsync(CancellationToken cancellationToken = default)
        {
            ReadAccountCallCount++;
            return ReadAccountHandler?.Invoke(cancellationToken) ?? throw new NotSupportedException();
        }
        public Task<AgentLoginResult> StartChatGptLoginAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task LogoutAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

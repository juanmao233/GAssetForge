using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using GAssetForge.App.Services;
using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.App.ViewModels;
using GAssetForge.Core;

namespace GAssetForge.SmokeTests;

internal static class AgentConnectionLifecycleSmokeTests
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    public static void RunAll(string temporaryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRoot);

        RuntimeFailureKeepsHealthyMcpAndAllowsRetry(temporaryRoot);
        StandaloneMcpStartsAndStopsWithoutStartingRuntime(temporaryRoot);
        HealthyMcpIsReusedWhenRuntimeRetrySucceeds(temporaryRoot);
        ReconnectSessionReplacesRuntimeAndReusesHealthyMcp(temporaryRoot);
        ReconnectSessionRestartsUnavailableMcp(temporaryRoot);
        ReconnectSessionRecyclesMcpStuckInStartingAfterHealthTimeout(temporaryRoot);
        FaultedRunningTransportIsNotReused(temporaryRoot);
        ReconnectSessionRejectsRunningTurn(temporaryRoot);
        RuntimeSessionReceivesFreshPathOverride(temporaryRoot);
        RuntimeEnvironmentSnapshotCoversImageApiAndProxyVariables();
        EnvironmentFingerprintIsStableAndDoesNotExposeSecrets();
        RuntimeSessionRestartsWhenFreshPathChanges(temporaryRoot);
        RuntimeSessionRestartsWhenApiOrProxyEnvironmentChanges(temporaryRoot);
        RunTurnRefreshesRuntimeWhenFreshPathChanges(temporaryRoot);
        GenerationBatchCanRotateToFreshThread(temporaryRoot);
        DeferredFreshThreadDoesNotResumePreviousBatch(temporaryRoot);
        SlowRuntimeStartupCanBeDisposedWithoutUiDeadlock(temporaryRoot);
    }

    private static void RuntimeFailureKeepsHealthyMcpAndAllowsRetry(string temporaryRoot)
    {
        var fixture = CreateFixture(
            temporaryRoot,
            "runtime-failure-keeps-mcp",
            _ => FakeAgentSessionTransport.Failing("fixture Codex Runtime unavailable"),
            _ => FakeAgentSessionTransport.Failing("fixture codex exec unavailable"));
        using var viewModel = fixture.ViewModel;

        fixture.ViewModel.StartCommand.Execute(null);
        PumpUntil(
            () => fixture.ViewModel.StatusKind == AgentConnectionStatus.Faulted &&
                  fixture.ViewModel.StartCommand.CanExecute(null),
            CommandTimeout,
            "Runtime 启动失败后主启动命令没有回到可重试状态。");

        True(fixture.Server.IsProcessRunning,
            "Runtime 启动失败后，已通过健康检查的 MCP Server 必须继续运行。");
        True(GetBooleanProperty(fixture.ViewModel, "IsMcpServerActive"),
            "Runtime 启动失败后 IsMcpServerActive 应保持为 true。");
        True(GetBooleanProperty(fixture.ViewModel, "IsMcpServerStandalone"),
            "Runtime 启动失败后应进入可诊断的 MCP 独立运行状态。");
        False(GetBooleanProperty(fixture.ViewModel, "IsAgentSessionActive"),
            "Runtime 启动失败后不得误报 Agent 会话已启动。");
        True(fixture.ViewModel.StartCommand.CanExecute(null),
            "健康 MCP 独立运行时，StartCommand 必须允许重试 Runtime。");
        Equal(1, CountMcpLaunches(fixture.ServerLogs),
            "首次会话启动只应拉起一个 MCP 子进程");

        fixture.ViewModel.StartCommand.Execute(null);
        PumpUntil(
            () => fixture.AppServerFactoryCallCount == 2 &&
                  fixture.ViewModel.StatusKind == AgentConnectionStatus.Faulted &&
                  fixture.ViewModel.StartCommand.CanExecute(null),
            CommandTimeout,
            "保留 MCP 后再次点击 StartCommand 没有重试 Codex Runtime。");

        True(fixture.Server.IsProcessRunning,
            "第二次 Runtime 失败后仍应保留同一个健康 MCP Server。");
        Equal(1, CountMcpLaunches(fixture.ServerLogs),
            "重试 Runtime 不得重复启动或替换健康 MCP 子进程");
    }

    private static void StandaloneMcpStartsAndStopsWithoutStartingRuntime(string temporaryRoot)
    {
        var fixture = CreateFixture(
            temporaryRoot,
            "standalone-mcp-lifecycle",
            _ => FakeAgentSessionTransport.Failing("Runtime should not be started"),
            _ => FakeAgentSessionTransport.Failing("Runtime should not be started"));
        using var viewModel = fixture.ViewModel;
        var startMcpCommand = GetCommand(viewModel, "StartMcpServerCommand");
        var stopMcpCommand = GetCommand(viewModel, "StopMcpServerCommand");

        True(GetBooleanProperty(viewModel, "CanStartMcpServer"),
            "停止状态应允许独立启动 MCP Server。");
        startMcpCommand.Execute(null);
        PumpUntil(
            () => fixture.Server.IsProcessRunning &&
                  IsMcpHealthReady(fixture.Server.State) &&
                  GetBooleanProperty(viewModel, "IsMcpServerStandalone") &&
                  GetBooleanProperty(viewModel, "CanStopMcpServer"),
            CommandTimeout,
            "独立 MCP 启动命令没有使真实 Server 进入健康状态。");

        True(GetBooleanProperty(viewModel, "IsMcpServerActive"),
            "独立启动后 IsMcpServerActive 应为 true。");
        True(GetBooleanProperty(viewModel, "CanStopMcpServer"),
            "独立启动后应允许单独停止 MCP Server。");
        False(GetBooleanProperty(viewModel, "IsAgentSessionActive"),
            "仅启动 MCP Server 不得启动或误报 Agent Runtime。");
        Equal(0, fixture.AppServerFactoryCallCount,
            "独立启动 MCP 不得创建 App Server transport");
        Equal(0, fixture.ExecFactoryCallCount,
            "独立启动 MCP 不得创建 exec transport");

        stopMcpCommand.Execute(null);
        PumpUntil(
            () => !fixture.Server.IsProcessRunning &&
                  fixture.Server.State == McpServerProcessState.Stopped &&
                  GetBooleanProperty(viewModel, "CanStartMcpServer"),
            CommandTimeout,
            "独立 MCP 停止命令没有终止真实 Server 子进程。");

        False(GetBooleanProperty(viewModel, "IsMcpServerActive"),
            "独立停止后 IsMcpServerActive 应为 false。");
        True(GetBooleanProperty(viewModel, "CanStartMcpServer"),
            "独立停止后应允许重新启动 MCP Server。");
        Equal(0, fixture.AppServerFactoryCallCount,
            "独立停止 MCP 不得触碰 App Server transport");
        Equal(0, fixture.ExecFactoryCallCount,
            "独立停止 MCP 不得触碰 exec transport");
    }

    private static void HealthyMcpIsReusedWhenRuntimeRetrySucceeds(string temporaryRoot)
    {
        var fixture = CreateFixture(
            temporaryRoot,
            "healthy-mcp-runtime-retry",
            attempt => attempt == 1
                ? FakeAgentSessionTransport.Failing("fixture first App Server failure")
                : FakeAgentSessionTransport.Ready(),
            _ => FakeAgentSessionTransport.Failing("fixture first exec failure"));
        using var viewModel = fixture.ViewModel;
        var startMcpCommand = GetCommand(viewModel, "StartMcpServerCommand");

        startMcpCommand.Execute(null);
        PumpUntil(
            () => fixture.Server.IsProcessRunning &&
                  IsMcpHealthReady(fixture.Server.State) &&
                  GetBooleanProperty(viewModel, "IsMcpServerStandalone") &&
                  viewModel.StartCommand.CanExecute(null),
            CommandTimeout,
            "重试场景的独立 MCP Server 未进入健康状态。");
        Equal(1, CountMcpLaunches(fixture.ServerLogs),
            "fixture 应只启动一次 MCP Server");

        viewModel.StartCommand.Execute(null);
        PumpUntil(
            () => viewModel.StatusKind == AgentConnectionStatus.Faulted &&
                  viewModel.StartCommand.CanExecute(null),
            CommandTimeout,
            "首次 Runtime 失败后没有返回可重试状态。");
        True(fixture.Server.IsProcessRunning,
            "首次 Runtime 失败不得停止预先健康运行的 MCP Server。");
        Equal(1, CountMcpLaunches(fixture.ServerLogs),
            "首次 Runtime 尝试必须复用预先健康的 MCP Server");

        viewModel.StartCommand.Execute(null);
        PumpUntil(
            () => GetBooleanProperty(viewModel, "IsAgentSessionActive") &&
                  viewModel.IsConnected &&
                  viewModel.StatusKind == AgentConnectionStatus.Connected,
            CommandTimeout,
            "第二次 Runtime 尝试没有在保留的 MCP Server 上建立可用会话。");

        Equal(2, fixture.AppServerFactoryCallCount,
            "第二次点击 StartCommand 应重新创建 App Server transport");
        Equal(1, fixture.ExecFactoryCallCount,
            "App Server 第二次成功后不得再次进入 exec fallback");
        Equal(1, CountMcpLaunches(fixture.ServerLogs),
            "Runtime 重试成功前后不得重复启动健康 MCP Server");
        True(fixture.Server.IsProcessRunning,
            "Runtime 重试成功后 MCP Server 应继续运行。");
        False(GetBooleanProperty(viewModel, "IsMcpServerStandalone"),
            "Runtime 就绪后 MCP Server 不应继续标记为独立运行。");
    }

    private static void ReconnectSessionReplacesRuntimeAndReusesHealthyMcp(string temporaryRoot)
    {
        const string resumeThreadId = "fixture-reconnect-thread";
        var transports = new ConcurrentQueue<FakeAgentSessionTransport>();
        var fixture = CreateFixture(
            temporaryRoot,
            "reconnect-runtime-preserves-mcp-and-thread",
            _ =>
            {
                var transport = FakeAgentSessionTransport.Ready();
                transports.Enqueue(transport);
                return transport;
            },
            _ => FakeAgentSessionTransport.Failing("exec fallback should not be used"),
            resumeThreadId: resumeThreadId);
        using var viewModel = fixture.ViewModel;

        viewModel.StartCommand.Execute(null);
        PumpUntil(
            () => viewModel.IsConnected && fixture.AppServerFactoryCallCount == 1,
            CommandTimeout,
            "重连 fixture 的初始 Agent 会话未进入 Ready。");
        var initialEndpoint = fixture.Server.Endpoint;
        Equal(1, CountMcpLaunches(fixture.ServerLogs),
            "初始 Agent 会话只应启动一个 MCP Server");

        var reconnectTask = viewModel.ReconnectSessionAsync();
        PumpUntil(
            () => reconnectTask.IsCompleted,
            CommandTimeout,
            "ReconnectSessionAsync 未在限定时间内完成。");
        reconnectTask.GetAwaiter().GetResult();

        Equal(2, fixture.AppServerFactoryCallCount,
            "强制重连必须创建新的 App Server transport，而不是复用 Ready Runtime");
        Equal(0, fixture.ExecFactoryCallCount,
            "App Server 重连成功时不得进入 exec fallback");
        Equal(1, CountMcpLaunches(fixture.ServerLogs),
            "强制重连必须复用健康 MCP Server，不能重复启动子进程");
        Equal(initialEndpoint, fixture.Server.Endpoint,
            "复用健康 MCP 时 Endpoint 不得改变");
        Equal(fixture.WorkingDirectory, viewModel.WorkingDirectory,
            "重连后必须保留项目工作目录");
        Equal(resumeThreadId, fixture.Runtime.ThreadId,
            "重连后必须恢复原 Codex thread bookmark");
        Equal(2, transports.Count,
            "重连应保留可观察的旧、新两个 transport 实例");
        Equal(1, transports.Last().ResumeThreadCallCount,
            "新的 Runtime 必须显式恢复重连前的 thread bookmark");
        True(viewModel.IsConnected,
            "ReconnectSessionAsync 成功返回前必须验证 Agent 与 MCP 已恢复可执行状态");
    }

    private static void ReconnectSessionRestartsUnavailableMcp(string temporaryRoot)
    {
        var fixture = CreateFixture(
            temporaryRoot,
            "reconnect-restarts-unavailable-mcp",
            _ => FakeAgentSessionTransport.Ready(),
            _ => FakeAgentSessionTransport.Failing("exec fallback should not be used"));
        using var viewModel = fixture.ViewModel;

        viewModel.StartCommand.Execute(null);
        PumpUntil(
            () => viewModel.IsConnected,
            CommandTimeout,
            "MCP 恢复 fixture 的初始 Agent 会话未进入 Ready。");
        var resumeThreadId = fixture.Runtime.ThreadId;
        fixture.Server.StopAsync().GetAwaiter().GetResult();
        PumpUntil(
            () => !fixture.Server.IsProcessRunning && !viewModel.IsConnected,
            CommandTimeout,
            "fixture MCP Server 未停止。");

        var reconnectTask = viewModel.ReconnectSessionAsync();
        PumpUntil(
            () => reconnectTask.IsCompleted,
            CommandTimeout,
            "MCP Server 不可用时 ReconnectSessionAsync 未完成。");
        reconnectTask.GetAwaiter().GetResult();

        Equal(2, CountMcpLaunches(fixture.ServerLogs),
            "MCP Server 已停止时重连必须执行一次新的健康启动");
        Equal(2, fixture.AppServerFactoryCallCount,
            "MCP 恢复后必须重新创建 Runtime transport");
        Equal(resumeThreadId, fixture.Runtime.ThreadId,
            "MCP 与 Runtime 恢复后必须继续使用原 thread bookmark");
        True(viewModel.IsConnected,
            "MCP 恢复并重建 Runtime 后必须重新达到可执行状态");
    }

    private static void ReconnectSessionRecyclesMcpStuckInStartingAfterHealthTimeout(
        string temporaryRoot)
    {
        const string resumeThreadId = "fixture-starting-reconnect-thread";
        var fixture = CreateFixture(
            temporaryRoot,
            "reconnect-recycles-starting-mcp-after-timeout",
            _ => FakeAgentSessionTransport.Ready(),
            _ => FakeAgentSessionTransport.Failing("exec fallback should not be used"),
            resumeThreadId: resumeThreadId,
            mcpHealthReadyTimeout: TimeSpan.FromMilliseconds(250));
        using var viewModel = fixture.ViewModel;

        fixture.Server.StartAsync(viewModel.Port, "codex").GetAwaiter().GetResult();
        PumpUntil(
            () => IsMcpHealthReady(fixture.Server.State),
            CommandTimeout,
            "Starting 超时恢复 fixture 的初始 MCP Server 未进入健康状态。");
        fixture.Server.StopAsync().GetAwaiter().GetResult();
        PumpUntil(
            () => !fixture.Server.IsProcessRunning,
            CommandTimeout,
            "Starting 超时恢复 fixture 未停止初始 MCP Server。");

        var stalledProcess = StartStalledMcpPlaceholderProcess();
        var stalledProcessId = stalledProcess.Id;
        InjectStalledStartingMcpProcess(fixture.Server, stalledProcess, viewModel.Port);
        True(fixture.Server.IsProcessRunning &&
             fixture.Server.State == McpServerProcessState.Starting,
            "回归 fixture 必须模拟进程存活但健康状态长期停在 Starting。");

        try
        {
            var reconnectTask = viewModel.ReconnectSessionAsync();
            PumpUntil(
                () => reconnectTask.IsCompleted,
                CommandTimeout,
                "Starting MCP 健康检查超时后 ReconnectSessionAsync 未完成。");
            reconnectTask.GetAwaiter().GetResult();

            False(IsProcessAlive(stalledProcessId),
                "Starting 健康检查超时后必须回收原占位进程。");
            Equal(2, CountMcpLaunches(fixture.ServerLogs),
                "Starting 超时后只应执行一次新的 MCP Server 健康启动");
            Equal(1, fixture.AppServerFactoryCallCount,
                "回收 Starting MCP 后必须重新创建 Runtime transport");
            Equal(resumeThreadId, fixture.Runtime.ThreadId,
                "回收 Starting MCP 后必须继续使用原 thread bookmark");
            Equal(1, viewModel.Logs.Count(entry =>
                    entry.Contains("健康检查超时", StringComparison.Ordinal) &&
                    entry.Contains("回收", StringComparison.Ordinal)),
                "Starting 超时恢复原因只应记录一次");
            True(viewModel.IsConnected,
                "Starting MCP 被回收并单次重启后必须恢复可执行状态");
        }
        finally
        {
            TryTerminateProcess(stalledProcessId);
        }
    }

    private static void FaultedRunningTransportIsNotReused(string temporaryRoot)
    {
        FakeAgentSessionTransport? firstTransport = null;
        var fixture = CreateFixture(
            temporaryRoot,
            "faulted-running-transport-is-replaced",
            attempt =>
            {
                var transport = attempt == 1
                    ? FakeAgentSessionTransport.Ready(processRemainsRunningWhenFaulted: true)
                    : FakeAgentSessionTransport.Ready();
                firstTransport ??= transport;
                return transport;
            },
            _ => FakeAgentSessionTransport.Failing("exec fallback should not be used"));
        using var viewModel = fixture.ViewModel;

        viewModel.StartCommand.Execute(null);
        PumpUntil(
            () => viewModel.IsConnected && fixture.AppServerFactoryCallCount == 1,
            CommandTimeout,
            "Faulted transport fixture 的初始会话未进入 Ready。");
        var resumeThreadId = fixture.Runtime.ThreadId;
        True(!string.IsNullOrWhiteSpace(resumeThreadId),
            "Faulted transport fixture 必须先建立可恢复 thread");

        firstTransport!.SimulateFaultKeepingProcessAlive("fixture protocol stream faulted");
        PumpUntil(
            () => fixture.Runtime.State == AgentSessionTransportState.Faulted,
            CommandTimeout,
            "fixture transport 未进入进程仍存活的 Faulted 状态。");
        True(firstTransport.IsRunning,
            "该回归场景必须模拟进程仍存活但协议 transport 已 Faulted");

        var ensureTask = fixture.Runtime.EnsureSessionAsync(
            fixture.WorkingDirectory,
            resumeThreadId,
            fixture.Server);
        PumpUntil(
            () => ensureTask.IsCompleted,
            CommandTimeout,
            "Faulted transport 的 EnsureSessionAsync 未完成。");
        ensureTask.GetAwaiter().GetResult();

        Equal(2, fixture.AppServerFactoryCallCount,
            "Faulted transport 即使进程仍存活也必须被替换");
        Equal(AgentSessionTransportState.Ready, fixture.Runtime.State,
            "替换 Faulted transport 后 Runtime 必须恢复 Ready");
        Equal(resumeThreadId, fixture.Runtime.ThreadId,
            "替换 Faulted transport 后必须恢复原 thread bookmark");
    }

    private static void ReconnectSessionRejectsRunningTurn(string temporaryRoot)
    {
        var turnEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = CreateFixture(
            temporaryRoot,
            "reconnect-rejects-running-turn",
            _ => FakeAgentSessionTransport.ReadyWithBlockingTurn(turnEntered),
            _ => FakeAgentSessionTransport.Failing("exec fallback should not be used"));
        using var viewModel = fixture.ViewModel;
        using var turnCancellation = new CancellationTokenSource();

        viewModel.StartCommand.Execute(null);
        PumpUntil(
            () => viewModel.IsConnected,
            CommandTimeout,
            "运行中 Turn fixture 的 Agent 会话未进入 Ready。");
        var turnTask = viewModel.RunTurnAsync(
            "执行运行中 Turn 重连拒绝测试。",
            turnCancellation.Token);
        PumpUntil(
            () => turnEntered.Task.IsCompleted && viewModel.IsTurnRunning,
            CommandTimeout,
            "fixture Turn 未进入 RunningTurn。");

        var reconnectTask = viewModel.ReconnectSessionAsync();
        PumpUntil(
            () => reconnectTask.IsCompleted,
            CommandTimeout,
            "运行中 Turn 的重连拒绝没有及时返回。");
        Exception? reconnectFailure = null;
        try
        {
            reconnectTask.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            reconnectFailure = exception;
        }

        True(reconnectFailure is InvalidOperationException &&
             reconnectFailure.Message.Contains("Turn", StringComparison.Ordinal),
            "运行中 Turn 的重连必须以准确 InvalidOperationException 拒绝");
        Equal(1, fixture.AppServerFactoryCallCount,
            "拒绝运行中 Turn 重连时不得替换 Runtime");
        Equal(1, CountMcpLaunches(fixture.ServerLogs),
            "拒绝运行中 Turn 重连时不得重启 MCP Server");
        True(viewModel.IsTurnRunning,
            "重连拒绝不得中断原本正在执行的 Turn");

        turnCancellation.Cancel();
        PumpUntil(
            () => turnTask.IsCompleted,
            CommandTimeout,
            "清理 fixture Turn 时没有响应取消。");
        try
        {
            turnTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void SlowRuntimeStartupCanBeDisposedWithoutUiDeadlock(string temporaryRoot)
    {
        var startEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = CreateFixture(
            temporaryRoot,
            "slow-runtime-dispose",
            _ => FakeAgentSessionTransport.BlockingUntilCancelled(startEntered),
            _ => FakeAgentSessionTransport.Failing("exec fallback should not start after cancellation"));

        fixture.ViewModel.StartCommand.Execute(null);
        PumpUntil(
            () => startEntered.Task.IsCompleted && fixture.Server.IsProcessRunning,
            CommandTimeout,
            "慢 Runtime fixture 未进入可取消的启动阶段。");

        var disposeTask = fixture.ViewModel.DisposeAsync().AsTask();
        PumpUntil(
            () => disposeTask.IsCompleted,
            CommandTimeout,
            "启动中的 AgentConnectionViewModel 异步关闭超时，可能存在 UI 生命周期死锁。");
        disposeTask.GetAwaiter().GetResult();

        False(fixture.Server.IsProcessRunning,
            "异步关闭完成后不得残留 MCP Server 子进程。");
    }

    private static void RuntimeSessionReceivesFreshPathOverride(string temporaryRoot)
    {
        AgentSessionStartOptions? capturedOptions = null;
        var fixture = CreateFixture(
            temporaryRoot,
            "runtime-fresh-path",
            _ => FakeAgentSessionTransport.Ready(options => capturedOptions = options),
            _ => FakeAgentSessionTransport.Failing("exec fallback should not start"));
        using var viewModel = fixture.ViewModel;

        viewModel.StartCommand.Execute(null);
        PumpUntil(
            () => viewModel.IsConnected && capturedOptions is not null,
            CommandTimeout,
            "Runtime 启动没有把环境快照交给 Codex 子进程。");

        True(capturedOptions!.EnvironmentVariables.TryGetValue("PATH", out var childPath) &&
             !string.IsNullOrWhiteSpace(childPath),
            "Codex 子进程必须收到显式 PATH 覆盖，不能只继承应用启动时的旧 PATH。");
        Equal(
            ProcessEnvironmentSnapshot.BuildFreshPathEnvironment(),
            childPath,
            "Codex 子进程 PATH 必须来自当前进程、Machine 与 User 的最新合并快照");
    }

    private static void RuntimeSessionRestartsWhenFreshPathChanges(string temporaryRoot)
    {
        var firstPath = Path.Combine(temporaryRoot, "runtime-path-first");
        var refreshedPath = Path.Combine(temporaryRoot, "runtime-path-with-python");
        var currentPath = firstPath;
        var capturedPaths = new ConcurrentQueue<string?>();
        var fixture = CreateFixture(
            temporaryRoot,
            "runtime-refreshes-changed-path",
            _ => FakeAgentSessionTransport.Ready(options =>
                capturedPaths.Enqueue(options.EnvironmentVariables["PATH"])),
            _ => FakeAgentSessionTransport.Failing("exec fallback should not start"),
            () => currentPath);
        using var viewModel = fixture.ViewModel;

        viewModel.StartCommand.Execute(null);
        PumpUntil(
            () => viewModel.IsConnected && capturedPaths.Count == 1,
            CommandTimeout,
            "Runtime 首次启动没有捕获 fixture PATH。");

        currentPath = refreshedPath;
        fixture.Runtime.EnsureSessionAsync(
                fixture.WorkingDirectory,
                fixture.Runtime.ThreadId,
                fixture.Server)
            .GetAwaiter()
            .GetResult();

        Equal(2, fixture.AppServerFactoryCallCount,
            "PATH 在 Agent 会话启动后变化时 EnsureSessionAsync 必须重建受管 Runtime");
        True(
            capturedPaths.SequenceEqual(new[] { firstPath, refreshedPath }),
            "重建后的 Codex 子进程必须收到最新 PATH 快照");

        fixture.Runtime.EnsureSessionAsync(
                fixture.WorkingDirectory,
                fixture.Runtime.ThreadId,
                fixture.Server)
            .GetAwaiter()
            .GetResult();
        Equal(2, fixture.AppServerFactoryCallCount,
            "PATH 快照未变化时 EnsureSessionAsync 必须继续复用当前 Runtime");
    }

    private static void EnvironmentFingerprintIsStableAndDoesNotExposeSecrets()
    {
        const string secret = "fixture-secret-must-not-appear";
        var first = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["OPENAI_API_KEY"] = secret,
            ["https_proxy"] = "http://proxy.example.invalid:8080",
            ["OPENAI_ORG_ID"] = "org-fixture",
            ["OPENAI_PROJECT_ID"] = "project-fixture",
            ["SSL_CERT_DIR"] = Path.Combine("fixture", "certs")
        };
        var reordered = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SSL_CERT_DIR"] = Path.Combine("fixture", "certs"),
            ["OPENAI_PROJECT_ID"] = "project-fixture",
            ["OPENAI_ORG_ID"] = "org-fixture",
            ["https_proxy"] = "http://proxy.example.invalid:8080",
            ["OPENAI_API_KEY"] = secret
        };

        var firstFingerprint = ProcessEnvironmentSnapshot.ComputeEnvironmentFingerprint(first);
        var reorderedFingerprint = ProcessEnvironmentSnapshot.ComputeEnvironmentFingerprint(reordered);

        Equal(firstFingerprint, reorderedFingerprint,
            "Runtime 环境指纹不得因 Dictionary 插入顺序变化而触发无意义重启");
        False(firstFingerprint.Contains(secret, StringComparison.Ordinal),
            "Runtime 复用状态只能保存不可逆指纹，不得保存或回显 API 密钥");
        reordered["OPENAI_PROJECT_ID"] = "project-fixture-updated";
        False(string.Equals(
                firstFingerprint,
                ProcessEnvironmentSnapshot.ComputeEnvironmentFingerprint(reordered),
                StringComparison.Ordinal),
            "Image API 组织/项目/代理/证书变量变化必须改变 Runtime 环境指纹");
    }

    private static void RuntimeEnvironmentSnapshotCoversImageApiAndProxyVariables()
    {
        var snapshot = ProcessEnvironmentSnapshot.BuildFreshAgentEnvironmentVariables();
        var expectedNames = new[]
        {
            "PATH",
            "OPENAI_API_KEY",
            "OPENAI_BASE_URL",
            "HTTP_PROXY",
            "HTTPS_PROXY",
            "ALL_PROXY",
            "NO_PROXY",
            "http_proxy",
            "https_proxy",
            "all_proxy",
            "no_proxy",
            "OPENAI_ORG_ID",
            "OPENAI_PROJECT_ID",
            "CODEX_HOME",
            "GASSETFORGE_PYTHON_EXECUTABLE",
            "SSL_CERT_FILE",
            "SSL_CERT_DIR",
            "REQUESTS_CA_BUNDLE"
        };

        True(expectedNames.All(snapshot.ContainsKey),
            "Runtime 环境快照必须覆盖 Harness 使用的 Image API、代理与证书变量集合");
    }

    private static void RunTurnRefreshesRuntimeWhenFreshPathChanges(string temporaryRoot)
    {
        var firstPath = Path.Combine(temporaryRoot, "turn-path-first");
        var refreshedPath = Path.Combine(temporaryRoot, "turn-path-with-python");
        var currentPath = firstPath;
        var capturedPaths = new ConcurrentQueue<string?>();
        var fixture = CreateFixture(
            temporaryRoot,
            "run-turn-refreshes-changed-path",
            _ => FakeAgentSessionTransport.Ready(options =>
                capturedPaths.Enqueue(options.EnvironmentVariables["PATH"])),
            _ => FakeAgentSessionTransport.Failing("exec fallback should not start"),
            () => currentPath);
        using var viewModel = fixture.ViewModel;

        viewModel.StartCommand.Execute(null);
        PumpUntil(
            () => viewModel.IsConnected && capturedPaths.Count == 1,
            CommandTimeout,
            "Runtime 首次启动没有捕获 Turn fixture PATH。");

        currentPath = refreshedPath;
        var turn = viewModel.RunTurnAsync("fixture generation turn");
        PumpUntil(
            () => turn.IsCompleted,
            CommandTimeout,
            "提交生成 Turn 前没有完成 Runtime 环境刷新。");
        var result = turn.GetAwaiter().GetResult();

        True(result.IsSuccess, "环境刷新后生成 Turn 应继续在恢复的 thread 上完成");
        Equal(2, fixture.AppServerFactoryCallCount,
            "生成按钮必须在提交 Turn 前触发 EnsureSessionAsync 并重建旧环境 Runtime");
        True(
            capturedPaths.SequenceEqual(new[] { firstPath, refreshedPath }),
            "生成 Turn 重建后的 Codex 子进程必须收到最新 PATH 快照");
        Equal("fixture-thread", result.ThreadId,
            "环境刷新重启 Runtime 时必须恢复现有 Codex thread");
    }

    private static void GenerationBatchCanRotateToFreshThread(string temporaryRoot)
    {
        var fixture = CreateFixture(
            temporaryRoot,
            "generation-batch-fresh-thread",
            _ => FakeAgentSessionTransport.Ready(),
            _ => FakeAgentSessionTransport.Failing("exec fallback should not start"));
        using var viewModel = fixture.ViewModel;

        viewModel.StartCommand.Execute(null);
        PumpUntil(
            () => viewModel.IsConnected &&
                  string.Equals(fixture.Runtime.ThreadId, "fixture-thread", StringComparison.Ordinal),
            CommandTimeout,
            "Runtime 首次启动没有建立 fixture thread。");

        var rotateTask = viewModel.StartFreshGenerationThreadAsync();
        PumpUntil(
            () => rotateTask.IsCompleted,
            CommandTimeout,
            "生成批次没有完成独立线程轮换。");
        var rotated = rotateTask.GetAwaiter().GetResult();

        Equal("fixture-thread-2", rotated.ThreadId,
            "每个生成批次必须能显式创建与旧上下文隔离的新线程");
        Equal(rotated.ThreadId, fixture.Runtime.ThreadId,
            "Runtime 当前线程必须切换到批次新线程");
    }

    private static void DeferredFreshThreadDoesNotResumePreviousBatch(string temporaryRoot)
    {
        FakeAgentSessionTransport? transport = null;
        var fixture = CreateFixture(
            temporaryRoot,
            "generation-batch-deferred-fresh-thread",
            _ => transport = FakeAgentSessionTransport.ReadyWithDeferredThreadStart(),
            _ => FakeAgentSessionTransport.Failing("exec fallback should not start"),
            resumeThreadId: "previous-batch-thread");
        using var viewModel = fixture.ViewModel;

        viewModel.StartCommand.Execute(null);
        PumpUntil(
            () => viewModel.IsConnected && transport?.ResumeThreadCallCount == 1,
            CommandTimeout,
            "Runtime 没有按 fixture 恢复上一批次线程。");

        var rotated = viewModel.StartFreshGenerationThreadAsync().GetAwaiter().GetResult();
        True(rotated.IsDeferred,
            "fixture 必须返回延迟创建的新线程");

        // Deliberately do not pump the WPF Dispatcher between rotation and RunTurn. This catches
        // stale bookmark races where UI publication is queued but the next exec Turn starts now.
        var turn = viewModel.RunTurnAsync("start the fresh deferred batch thread")
            .GetAwaiter()
            .GetResult();

        Equal("fixture-deferred-thread", turn.ThreadId,
            "延迟创建通道的下一 Turn 必须建立新线程");
        Equal(1, transport!.ResumeThreadCallCount,
            "批次轮换后不得因 UI Dispatcher 延迟而再次恢复上一批次线程");
    }

    private static void RuntimeSessionRestartsWhenApiOrProxyEnvironmentChanges(string temporaryRoot)
    {
        const string firstApiKey = "fixture-api-key-first";
        const string refreshedApiKey = "fixture-api-key-refreshed";
        var currentApiKey = firstApiKey;
        var currentBaseUrl = "https://api-first.example.invalid/v1";
        var currentProxy = "http://proxy-first.example.invalid:8080";
        var currentPython = Path.Combine(temporaryRoot, "Python312", "python.exe");
        var capturedEnvironments = new ConcurrentQueue<IReadOnlyDictionary<string, string?>>();
        IReadOnlyDictionary<string, string?> BuildEnvironment() =>
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["OPENAI_API_KEY"] = currentApiKey,
                ["OPENAI_BASE_URL"] = currentBaseUrl,
                ["HTTPS_PROXY"] = currentProxy,
                ["GASSETFORGE_PYTHON_EXECUTABLE"] = currentPython
            };
        var fixture = CreateFixture(
            temporaryRoot,
            "runtime-refreshes-api-proxy-environment",
            _ => FakeAgentSessionTransport.Ready(options =>
                capturedEnvironments.Enqueue(new Dictionary<string, string?>(
                    options.EnvironmentVariables,
                    StringComparer.OrdinalIgnoreCase))),
            _ => FakeAgentSessionTransport.Failing("exec fallback should not start"),
            () => Path.Combine(temporaryRoot, "stable-runtime-path"),
            BuildEnvironment);
        using var viewModel = fixture.ViewModel;

        viewModel.StartCommand.Execute(null);
        PumpUntil(
            () => viewModel.IsConnected && capturedEnvironments.Count == 1,
            CommandTimeout,
            "Runtime 首次启动没有捕获 API/代理环境快照。");

        currentApiKey = refreshedApiKey;
        currentBaseUrl = "https://api-refreshed.example.invalid/v1";
        currentProxy = "http://proxy-refreshed.example.invalid:8080";
        currentPython = Path.Combine(temporaryRoot, "Python313", "python.exe");
        var resumeThreadId = fixture.Runtime.ThreadId;
        fixture.Runtime.EnsureSessionAsync(
                fixture.WorkingDirectory,
                resumeThreadId,
                fixture.Server)
            .GetAwaiter()
            .GetResult();

        Equal(2, fixture.AppServerFactoryCallCount,
            "API、代理或显式 Python 环境变化时必须重建受管 Runtime");
        var latest = capturedEnvironments.Last();
        Equal(refreshedApiKey, latest["OPENAI_API_KEY"],
            "重建后的 Runtime 必须收到最新 Image API Key");
        Equal(currentBaseUrl, latest["OPENAI_BASE_URL"],
            "重建后的 Runtime 必须收到最新 Image API Base URL");
        Equal(currentProxy, latest["HTTPS_PROXY"],
            "重建后的 Runtime 必须收到最新代理地址");
        Equal(currentPython, latest["GASSETFORGE_PYTHON_EXECUTABLE"],
            "重建后的 Runtime 必须收到最新显式 Python 路径");
        Equal(resumeThreadId, fixture.Runtime.ThreadId,
            "API/代理环境刷新重启 Runtime 时必须恢复原 Codex thread");

        fixture.Runtime.EnsureSessionAsync(
                fixture.WorkingDirectory,
                fixture.Runtime.ThreadId,
                fixture.Server)
            .GetAwaiter()
            .GetResult();
        Equal(2, fixture.AppServerFactoryCallCount,
            "API/代理环境未变化时必须继续复用当前 Runtime");
    }

    private static LifecycleFixture CreateFixture(
        string temporaryRoot,
        string testName,
        Func<int, IAgentSessionTransport> appServerFactory,
        Func<int, IAgentSessionTransport> execFactory,
        Func<string>? pathEnvironmentProvider = null,
        Func<IReadOnlyDictionary<string, string?>>? environmentVariablesProvider = null,
        string? resumeThreadId = null,
        TimeSpan? mcpHealthReadyTimeout = null)
    {
        var root = Path.Combine(temporaryRoot, testName, Guid.NewGuid().ToString("N"));
        var paths = new WorkspacePaths(Path.Combine(root, "app-data"));
        var workingDirectory = Path.Combine(root, "project");
        Directory.CreateDirectory(workingDirectory);
        var server = new McpServerProcessService(paths);
        var serverLogs = new ConcurrentQueue<McpServerLogEventArgs>();
        server.LogReceived += (_, eventArgs) => serverLogs.Enqueue(eventArgs);
        var fixture = new LifecycleFixture(server, serverLogs);
        var runtime = new CodexAgentRuntimeService(
            "codex-fixture",
            () => appServerFactory(fixture.IncrementAppServerFactoryCallCount()),
            () => execFactory(fixture.IncrementExecFactoryCallCount()),
            pathEnvironmentProvider,
            environmentVariablesProvider);
        fixture.Runtime = runtime;
        fixture.WorkingDirectory = workingDirectory;
        var port = GetAvailableLoopbackPort();
        fixture.ViewModel = mcpHealthReadyTimeout is { } timeout
            ? new AgentConnectionViewModel(
                server,
                runtime,
                selectedAgentKey: "codex",
                port: port,
                workingDirectory: workingDirectory,
                resumeThreadId: resumeThreadId,
                workbenchLogSink: null,
                codexExecutablePath: null,
                codexExecutablePicker: null,
                mcpHealthReadyTimeout: timeout)
            : new AgentConnectionViewModel(
                server,
                runtime,
                selectedAgentKey: "codex",
                port: port,
                workingDirectory: workingDirectory,
                resumeThreadId: resumeThreadId);
        return fixture;
    }

    private static Process StartStalledMcpPlaceholderProcess()
    {
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(commandProcessor))
        {
            commandProcessor = "cmd.exe";
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = commandProcessor,
                Arguments = "/d /c \"ping -t 127.0.0.1 > nul\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("无法启动 Starting MCP 占位进程。");
        }

        return process;
    }

    private static void InjectStalledStartingMcpProcess(
        McpServerProcessService server,
        Process process,
        int port)
    {
        var stateLock = GetPrivateField<object>(server, "_stateLock");
        lock (stateLock)
        {
            SetPrivateField(server, "_process", process);
            SetPrivateField(server, "_runCancellation", new CancellationTokenSource());
            SetPrivateField(server, "_port", port);
            SetPrivateField(server, "_state", McpServerProcessState.Starting);
            SetPrivateField(server, "_detail", "fixture MCP health remains Starting");
        }

        var raiseStateChanged = typeof(McpServerProcessService).GetMethod(
            "RaiseStateChanged",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "McpServerProcessService 缺少 RaiseStateChanged 测试缝隙。");
        raiseStateChanged.Invoke(server, null);
    }

    private static T GetPrivateField<T>(object source, string fieldName)
    {
        var field = source.GetType().GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"{source.GetType().Name} 缺少测试字段 {fieldName}。");
        return field.GetValue(source) is T value
            ? value
            : throw new InvalidOperationException(
                $"{source.GetType().Name}.{fieldName} 类型不符合测试预期。");
    }

    private static void SetPrivateField(object source, string fieldName, object? value)
    {
        var field = source.GetType().GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"{source.GetType().Name} 缺少测试字段 {fieldName}。");
        field.SetValue(source, value);
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void TryTerminateProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static ICommand GetCommand(object source, string propertyName)
    {
        var property = source.GetType().GetProperty(propertyName)
            ?? throw new InvalidOperationException(
                $"{source.GetType().Name} 缺少生命周期命令 {propertyName}。");
        return property.GetValue(source) as ICommand
            ?? throw new InvalidOperationException(
                $"{source.GetType().Name}.{propertyName} 未返回 ICommand。");
    }

    private static bool GetBooleanProperty(object source, string propertyName)
    {
        var property = source.GetType().GetProperty(propertyName)
            ?? throw new InvalidOperationException(
                $"{source.GetType().Name} 缺少生命周期状态 {propertyName}。");
        return property.GetValue(source) is bool value
            ? value
            : throw new InvalidOperationException(
                $"{source.GetType().Name}.{propertyName} 未返回 bool。");
    }

    private static int CountMcpLaunches(IEnumerable<McpServerLogEventArgs> logs) =>
        logs.Count(entry => entry.Message.Contains("正在启动 MCP Server", StringComparison.Ordinal));

    private static bool IsMcpHealthReady(McpServerProcessState state) =>
        state is McpServerProcessState.WaitingForAgent or McpServerProcessState.Connected;

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

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout, string failureMessage)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidOperationException(failureMessage);
            }

            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(20),
                DispatcherPriority.Background,
                (_, _) => frame.Continue = false,
                Dispatcher.CurrentDispatcher);
            try
            {
                Dispatcher.PushFrame(frame);
            }
            finally
            {
                timer.Stop();
            }
        }

        // Flush state notifications queued by the final child-process/runtime event.
        var flushFrame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => flushFrame.Continue = false));
        Dispatcher.PushFrame(flushFrame);
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool condition, string message) => True(!condition, message);

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{label}: expected '{expected}', actual '{actual}'.");
        }
    }

    private sealed class LifecycleFixture(
        McpServerProcessService server,
        ConcurrentQueue<McpServerLogEventArgs> serverLogs)
    {
        private int _appServerFactoryCallCount;
        private int _execFactoryCallCount;

        public AgentConnectionViewModel ViewModel { get; set; } = null!;

        public CodexAgentRuntimeService Runtime { get; set; } = null!;

        public string WorkingDirectory { get; set; } = string.Empty;

        public McpServerProcessService Server { get; } = server;

        public ConcurrentQueue<McpServerLogEventArgs> ServerLogs { get; } = serverLogs;

        public int AppServerFactoryCallCount => _appServerFactoryCallCount;

        public int ExecFactoryCallCount => _execFactoryCallCount;

        public int IncrementAppServerFactoryCallCount() =>
            Interlocked.Increment(ref _appServerFactoryCallCount);

        public int IncrementExecFactoryCallCount() =>
            Interlocked.Increment(ref _execFactoryCallCount);
    }

    private sealed class FakeAgentSessionTransport : IAgentSessionTransport
    {
        private readonly string? _startFailure;
        private readonly bool _blockUntilCancelled;
        private readonly TaskCompletionSource<bool>? _startEntered;
        private readonly Action<AgentSessionStartOptions>? _started;
        private readonly bool _deferredThreadStart;
        private readonly bool _processRemainsRunningWhenFaulted;
        private readonly bool _blockTurnUntilCancelled;
        private readonly TaskCompletionSource<bool>? _turnEntered;
        private AgentSessionTransportState _state = AgentSessionTransportState.Stopped;
        private int _threadSequence;

        private FakeAgentSessionTransport(
            string? startFailure,
            bool blockUntilCancelled = false,
            TaskCompletionSource<bool>? startEntered = null,
            Action<AgentSessionStartOptions>? started = null,
            bool deferredThreadStart = false,
            bool processRemainsRunningWhenFaulted = false,
            bool blockTurnUntilCancelled = false,
            TaskCompletionSource<bool>? turnEntered = null)
        {
            _startFailure = startFailure;
            _blockUntilCancelled = blockUntilCancelled;
            _startEntered = startEntered;
            _started = started;
            _deferredThreadStart = deferredThreadStart;
            _processRemainsRunningWhenFaulted = processRemainsRunningWhenFaulted;
            _blockTurnUntilCancelled = blockTurnUntilCancelled;
            _turnEntered = turnEntered;
        }

        public static FakeAgentSessionTransport Failing(string message) => new(message);

        public static FakeAgentSessionTransport Ready(
            Action<AgentSessionStartOptions>? started = null,
            bool processRemainsRunningWhenFaulted = false) =>
            new(
                null,
                started: started,
                processRemainsRunningWhenFaulted: processRemainsRunningWhenFaulted);

        public static FakeAgentSessionTransport ReadyWithDeferredThreadStart() =>
            new(null, deferredThreadStart: true);

        public static FakeAgentSessionTransport ReadyWithBlockingTurn(
            TaskCompletionSource<bool> turnEntered) =>
            new(
                null,
                blockTurnUntilCancelled: true,
                turnEntered: turnEntered);

        public static FakeAgentSessionTransport BlockingUntilCancelled(
            TaskCompletionSource<bool> startEntered) =>
            new(null, blockUntilCancelled: true, startEntered: startEntered);

        public string DisplayName => "Lifecycle fixture transport";

        public AgentTransportCapabilities Capabilities =>
            AgentTransportCapabilities.PersistentProcess |
            AgentTransportCapabilities.AccountRead |
            AgentTransportCapabilities.ExplicitThreadStart |
            AgentTransportCapabilities.ThreadResume |
            AgentTransportCapabilities.TurnExecution |
            AgentTransportCapabilities.TurnInterrupt |
            AgentTransportCapabilities.McpConfiguration;

        public AgentSessionTransportState State => _state;

        public bool IsRunning => _state is
            AgentSessionTransportState.Starting or
            AgentSessionTransportState.Ready or
            AgentSessionTransportState.RunningTurn ||
            (_state == AgentSessionTransportState.Faulted &&
             _processRemainsRunningWhenFaulted);

        public string? RuntimeVersion => _startFailure is null ? "fixture-1.0" : null;

        public string? CurrentThreadId { get; private set; }

        public string? ActiveTurnId => null;

        public int ResumeThreadCallCount { get; private set; }

        public event EventHandler<AgentTransportStateChangedEventArgs>? StateChanged;

        public event EventHandler<AgentTransportLogEventArgs>? LogReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<AgentNotificationEventArgs>? NotificationReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<AgentTurnCompletedEventArgs>? TurnCompleted
        {
            add { }
            remove { }
        }

        public event EventHandler<AgentMcpStatusChangedEventArgs>? McpStatusChanged;

        public async Task StartAsync(
            AgentSessionStartOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _started?.Invoke(options);
            Transition(AgentSessionTransportState.Starting, "fixture starting");
            if (_blockUntilCancelled)
            {
                _startEntered?.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Transition(AgentSessionTransportState.Stopped, "fixture startup cancelled");
                }

                return;
            }

            if (_startFailure is not null)
            {
                Transition(AgentSessionTransportState.Faulted, _startFailure);
                throw new InvalidOperationException(_startFailure);
            }

            Transition(AgentSessionTransportState.Ready, "fixture ready");
            McpStatusChanged?.Invoke(
                this,
                new AgentMcpStatusChangedEventArgs(
                    ProductIdentity.McpServerName,
                    "ready",
                    null,
                    CurrentThreadId));
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_state == AgentSessionTransportState.Stopped)
            {
                return Task.CompletedTask;
            }

            Transition(AgentSessionTransportState.Stopping, "fixture stopping");
            CurrentThreadId = null;
            Transition(AgentSessionTransportState.Stopped, "fixture stopped");
            return Task.CompletedTask;
        }

        public Task<AgentAccountState> ReadAccountAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AgentAccountState(
                RequiresOpenAiAuth: false,
                IsAuthenticated: true,
                AccountType: "fixture",
                Email: null,
                PlanType: null));
        }

        public Task<AgentLoginResult> StartChatGptLoginAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task LogoutAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentThreadResult> StartThreadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_deferredThreadStart)
            {
                CurrentThreadId = null;
                return Task.FromResult(new AgentThreadResult(null, IsDeferred: true));
            }

            var sequence = Interlocked.Increment(ref _threadSequence);
            CurrentThreadId = sequence == 1 ? "fixture-thread" : $"fixture-thread-{sequence}";
            return Task.FromResult(new AgentThreadResult(CurrentThreadId));
        }

        public Task<AgentThreadResult> ResumeThreadAsync(
            string threadId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResumeThreadCallCount++;
            CurrentThreadId = threadId;
            return Task.FromResult(new AgentThreadResult(CurrentThreadId));
        }

        public async Task<AgentTurnResult> StartTurnAsync(
            string prompt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(CurrentThreadId) && _deferredThreadStart)
            {
                CurrentThreadId = "fixture-deferred-thread";
            }

            if (_blockTurnUntilCancelled)
            {
                Transition(AgentSessionTransportState.RunningTurn, "fixture turn running");
                _turnEntered?.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    Transition(AgentSessionTransportState.Ready, "fixture turn canceled");
                }
            }

            return new AgentTurnResult(
                CurrentThreadId,
                "fixture-turn",
                "completed",
                null,
                (JsonElement?)null);
        }

        public Task InterruptTurnAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void SimulateFaultKeepingProcessAlive(string detail)
        {
            if (!_processRemainsRunningWhenFaulted)
            {
                throw new InvalidOperationException(
                    "该 fixture transport 未配置 Faulted 后保持进程存活。");
            }

            Transition(AgentSessionTransportState.Faulted, detail);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
        }

        private void Transition(AgentSessionTransportState state, string detail)
        {
            _state = state;
            StateChanged?.Invoke(this, new AgentTransportStateChangedEventArgs(state, detail));
        }
    }
}

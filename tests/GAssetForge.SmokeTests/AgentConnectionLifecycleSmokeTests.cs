using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        RuntimeSessionReceivesFreshPathOverride(temporaryRoot);
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

    private static LifecycleFixture CreateFixture(
        string temporaryRoot,
        string testName,
        Func<int, IAgentSessionTransport> appServerFactory,
        Func<int, IAgentSessionTransport> execFactory)
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
            () => execFactory(fixture.IncrementExecFactoryCallCount()));
        fixture.ViewModel = new AgentConnectionViewModel(
            server,
            runtime,
            selectedAgentKey: "codex",
            port: GetAvailableLoopbackPort(),
            workingDirectory: workingDirectory);
        return fixture;
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
        private AgentSessionTransportState _state = AgentSessionTransportState.Stopped;

        private FakeAgentSessionTransport(
            string? startFailure,
            bool blockUntilCancelled = false,
            TaskCompletionSource<bool>? startEntered = null,
            Action<AgentSessionStartOptions>? started = null)
        {
            _startFailure = startFailure;
            _blockUntilCancelled = blockUntilCancelled;
            _startEntered = startEntered;
            _started = started;
        }

        public static FakeAgentSessionTransport Failing(string message) => new(message);

        public static FakeAgentSessionTransport Ready(
            Action<AgentSessionStartOptions>? started = null) =>
            new(null, started: started);

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
            AgentSessionTransportState.RunningTurn;

        public string? RuntimeVersion => _startFailure is null ? "fixture-1.0" : null;

        public string? CurrentThreadId { get; private set; }

        public string? ActiveTurnId => null;

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
            CurrentThreadId = "fixture-thread";
            return Task.FromResult(new AgentThreadResult(CurrentThreadId));
        }

        public Task<AgentThreadResult> ResumeThreadAsync(
            string threadId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CurrentThreadId = threadId;
            return Task.FromResult(new AgentThreadResult(CurrentThreadId));
        }

        public Task<AgentTurnResult> StartTurnAsync(
            string prompt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AgentTurnResult(
                CurrentThreadId,
                "fixture-turn",
                "completed",
                null,
                (JsonElement?)null));
        }

        public Task InterruptTurnAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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

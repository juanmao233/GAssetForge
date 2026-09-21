using System.IO;
using System.ComponentModel;
using GAssetForge.Core;

namespace GAssetForge.App.Services.AgentRuntime;

public enum CodexAgentTransportMode
{
    None,
    AppServer,
    ExecFallback
}

public enum CodexAgentAuthenticationState
{
    Unknown,
    NotRequired,
    Required,
    Authenticated
}

public sealed class CodexAgentRuntimeService : IAsyncDisposable
{
    private readonly TimeSpan _mcpReadyTimeout;
    private readonly AgentMcpReadinessTracker _mcpReadiness = new();
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly object _configurationLock = new();
    private readonly object _operationLock = new();
    private CancellationTokenSource _stopSignal = new();
    private int _stopRequests;
    private readonly Func<IAgentSessionTransport> _appServerFactory;
    private readonly Func<IAgentSessionTransport> _execFactory;
    private readonly Func<string> _pathEnvironmentProvider;
    private readonly Func<IReadOnlyDictionary<string, string?>> _environmentVariablesProvider;
    private string _codexExecutablePath;
    private IAgentSessionTransport? _transport;
    private string? _sessionWorkingDirectory;
    private Uri? _sessionMcpEndpoint;
    private string? _sessionEnvironmentFingerprint;
    private string? _sessionSecret;
    private bool _disposed;

    public CodexAgentRuntimeService(string codexExecutablePath = "codex")
        : this(
            codexExecutablePath,
            static () => new CodexAppServerTransport(),
            static () => new CodexExecTransport())
    {
    }

    internal CodexAgentRuntimeService(
        string codexExecutablePath,
        Func<IAgentSessionTransport> appServerFactory,
        Func<IAgentSessionTransport> execFactory,
        Func<string>? pathEnvironmentProvider = null,
        Func<IReadOnlyDictionary<string, string?>>? environmentVariablesProvider = null,
        TimeSpan? mcpReadyTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexExecutablePath);
        _codexExecutablePath = codexExecutablePath;
        _appServerFactory = appServerFactory ?? throw new ArgumentNullException(nameof(appServerFactory));
        _execFactory = execFactory ?? throw new ArgumentNullException(nameof(execFactory));
        _pathEnvironmentProvider = pathEnvironmentProvider ??
                                   ProcessEnvironmentSnapshot.BuildFreshPathEnvironment;
        _environmentVariablesProvider = environmentVariablesProvider ??
                                        ProcessEnvironmentSnapshot.BuildFreshAgentEnvironmentVariables;
        _mcpReadyTimeout = mcpReadyTimeout ?? TimeSpan.FromSeconds(20);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_mcpReadyTimeout, TimeSpan.Zero);
    }

    public CodexAgentTransportMode TransportMode { get; private set; }

    public AgentSessionTransportState State =>
        _transport?.State ?? AgentSessionTransportState.Stopped;

    public bool IsConnected => State is
        AgentSessionTransportState.Ready or AgentSessionTransportState.RunningTurn;

    public bool IsTurnRunning => State == AgentSessionTransportState.RunningTurn;

    public bool McpReady => _mcpReadiness.IsReady;

    public string ReadinessStateSummary =>
        $"transport={State};mode={TransportMode};auth={AuthenticationState};" +
        $"mcp={_mcpReadiness.State};thread={_mcpReadiness.ThreadId ?? ThreadId ?? "none"}";

    public AgentAccountState? Account { get; private set; }

    public CodexAgentAuthenticationState AuthenticationState { get; private set; } =
        CodexAgentAuthenticationState.Unknown;

    public string? ThreadId => _transport?.CurrentThreadId;

    public string? RuntimeVersion => _transport?.RuntimeVersion;

    public string? ActiveTurnId => _transport?.ActiveTurnId;

    public string? LastError { get; private set; }

    public string CodexExecutablePath
    {
        get
        {
            lock (_configurationLock)
            {
                return _codexExecutablePath;
            }
        }
    }

    public event EventHandler<AgentTransportStateChangedEventArgs>? StateChanged;

    public event EventHandler<AgentTransportLogEventArgs>? LogReceived;

    public event EventHandler<AgentNotificationEventArgs>? NotificationReceived;

    public event EventHandler<AgentTurnCompletedEventArgs>? TurnCompleted;

    public event EventHandler<AgentMcpStatusChangedEventArgs>? McpStatusChanged;

    public void ConfigureExecutablePath(string codexExecutablePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(codexExecutablePath);
        if (!_sessionGate.Wait(0))
        {
            throw new InvalidOperationException("Codex Runtime 正在启动、停止或执行配置，暂时不能修改 CLI 路径。");
        }

        try
        {
            if (State is not AgentSessionTransportState.Stopped and not AgentSessionTransportState.Faulted)
            {
                throw new InvalidOperationException("Codex Runtime 运行期间不能修改 CLI 路径。请先停止 Agent 会话。");
            }

            lock (_configurationLock)
            {
                _codexExecutablePath = codexExecutablePath.Trim();
            }

            LastError = null;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task EnsureSessionAsync(
        string workingDirectory,
        string? resumeThreadId,
        McpServerProcessService mcpServer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(mcpServer);
        var normalizedWorkingDirectory = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(normalizedWorkingDirectory))
        {
            throw new DirectoryNotFoundException($"Agent 工作目录不存在：{normalizedWorkingDirectory}");
        }

        if (!mcpServer.IsProcessRunning || mcpServer.Endpoint is null)
        {
            throw new InvalidOperationException("请先启动 GAssetForge MCP Server，再接入 Codex Agent Runtime。");
        }

        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var callerCancellationToken = cancellationToken;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var operationCancellation = CreateSessionOperationCancellation(callerCancellationToken);
            cancellationToken = operationCancellation.Token;
            var environmentVariables = new Dictionary<string, string?>(
                _environmentVariablesProvider(),
                StringComparer.OrdinalIgnoreCase);
            var freshPath = _pathEnvironmentProvider();
            if (!string.IsNullOrWhiteSpace(freshPath))
            {
                environmentVariables["PATH"] = freshPath;
            }

            var environmentFingerprint =
                ProcessEnvironmentSnapshot.ComputeEnvironmentFingerprint(environmentVariables);
            if (CanReuseSession(
                    normalizedWorkingDirectory,
                    mcpServer.Endpoint,
                    environmentFingerprint))
            {
                if (TransportMode == CodexAgentTransportMode.AppServer &&
                    AuthenticationState == CodexAgentAuthenticationState.Required)
                {
                    await RefreshAccountCoreAsync(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (AuthenticationState == CodexAgentAuthenticationState.Required)
                    {
                        return;
                    }
                }

                await EnsureRequestedThreadAsync(resumeThreadId, cancellationToken).ConfigureAwait(false);
                if (TransportMode == CodexAgentTransportMode.AppServer && !McpReady)
                {
                    await WaitForMcpReadyAsync(cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            await StopTransportAsync(CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var options = new AgentSessionStartOptions
            {
                ExecutablePath = CodexExecutablePath,
                WorkingDirectory = normalizedWorkingDirectory,
                McpEndpoint = mcpServer.Endpoint,
                McpBearerTokenEnvironmentVariableName = mcpServer.TokenEnvironmentVariableName,
                McpBearerToken = mcpServer.TokenEnvironmentVariableValue,
                EnvironmentVariables = environmentVariables,
                SandboxMode = "workspace-write",
                ApprovalPolicy = "never",
                AllowSandboxNetworkAccess = true
            };

            LastError = null;
            _sessionSecret = mcpServer.TokenEnvironmentVariableValue;
            WriteFacadeLog(AgentLogLevel.Information,
                CodexProcessLaunch.DescribeNetworkEnvironment(environmentVariables));
            BeginReadinessCycle(allowUnscoped: true, cancellationToken);
            Account = null;
            AuthenticationState = CodexAgentAuthenticationState.Unknown;
            try
            {
                var appServer = _appServerFactory();
                AttachTransport(appServer);
                _transport = appServer;
                await appServer.StartAsync(options, cancellationToken).ConfigureAwait(false);
                TransportMode = CodexAgentTransportMode.AppServer;
                await RefreshAccountCoreAsync(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (AuthenticationState == CodexAgentAuthenticationState.Required)
                {
                    WriteFacadeLog(
                        AgentLogLevel.Warning,
                        "Codex app-server 已连接，但账号需要登录；完成登录后再次建立线程即可。");
                }
                else
                {
                    await EnsureRequestedThreadAsync(resumeThreadId, cancellationToken).ConfigureAwait(false);
                    await WaitForMcpReadyAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (AgentRpcException authenticationException) when (
                IsAuthenticationRequired(authenticationException))
            {
                TransportMode = CodexAgentTransportMode.AppServer;
                Account = new AgentAccountState(
                    RequiresOpenAiAuth: true,
                    IsAuthenticated: false,
                    AccountType: null,
                    Email: null,
                    PlanType: null);
                AuthenticationState = CodexAgentAuthenticationState.Required;
                LastError = null;
                WriteFacadeLog(
                    AgentLogLevel.Warning,
                    $"Codex app-server 已连接，但需要完成账号登录：{authenticationException.Message}");
            }
            catch (Exception appServerException) when (ShouldFallbackFromAppServer(
                       appServerException,
                       cancellationToken))
            {
                LastError = Sanitize(appServerException.Message);
                WriteFacadeLog(
                    AgentLogLevel.Warning,
                    $"Codex app-server 接入失败，将使用 exec JSONL 降级通道：{appServerException.Message}");
                await StopTransportAsync(CancellationToken.None).ConfigureAwait(false);

                var exec = _execFactory();
                _sessionSecret = options.McpBearerToken;
                AttachTransport(exec);
                _transport = exec;
                await exec.StartAsync(options, cancellationToken).ConfigureAwait(false);
                TransportMode = CodexAgentTransportMode.ExecFallback;
                Account = null;
                AuthenticationState = CodexAgentAuthenticationState.Unknown;
                if (!string.IsNullOrWhiteSpace(resumeThreadId))
                {
                    await exec.ResumeThreadAsync(resumeThreadId, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await exec.StartThreadAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            _sessionWorkingDirectory = normalizedWorkingDirectory;
            _sessionMcpEndpoint = mcpServer.Endpoint;
            _sessionEnvironmentFingerprint = environmentFingerprint;
        }
        catch (OperationCanceledException) when (
            !callerCancellationToken.IsCancellationRequested && IsStopRequested())
        {
            throw CreateReadinessException("agent_transport_stopped", "runtime",
                "Agent Runtime 正在停止，会话准备已取消，未继续创建线程或提交 Turn。", ThreadId);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task<AgentTurnResult> RunTurnAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var transport = _transport ??
                        throw new InvalidOperationException("Codex Agent Runtime 尚未接入。");
        try
        {
            var result = await transport.StartTurnAsync(prompt, cancellationToken).ConfigureAwait(false);
            LastError = result.IsSuccess ? null : result.ErrorMessage ?? result.Status;
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastError = Sanitize(exception.Message);
            throw;
        }
    }

    public async Task<AgentThreadResult> StartFreshThreadAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var callerCancellationToken = cancellationToken;
        object? readinessCycle = null;
        var threadBound = false;
        try
        {
            using var operationCancellation = CreateSessionOperationCancellation(callerCancellationToken);
            cancellationToken = operationCancellation.Token;
            var transport = RequireCapability(
                AgentTransportCapabilities.ExplicitThreadStart,
                "创建独立线程");
            if (!IsConnected || transport.State == AgentSessionTransportState.RunningTurn)
            {
                throw new InvalidOperationException(
                    "Codex Agent Runtime 尚未处于可创建独立线程的 Ready 状态。");
            }

            await WaitForExecutionReadinessAsync(cancellationToken).ConfigureAwait(false);
            readinessCycle = TransportMode == CodexAgentTransportMode.AppServer
                ? BeginReadinessCycle(allowUnscoped: false, cancellationToken)
                : null;
            var result = await transport.StartThreadAsync(cancellationToken)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            if (readinessCycle is not null)
            {
                _mcpReadiness.Bind(readinessCycle, result.ThreadId);
                threadBound = true;
                await WaitForMcpReadyAsync(cancellationToken).ConfigureAwait(false);
            }
            LastError = null;
            return result;
        }
        catch (OperationCanceledException) when (
            !callerCancellationToken.IsCancellationRequested && IsStopRequested())
        {
            throw CreateReadinessException("agent_transport_stopped", "runtime",
                "Agent Runtime 正在停止，新线程准备已取消。", ThreadId);
        }
        catch (OperationCanceledException) when (
            callerCancellationToken.IsCancellationRequested && readinessCycle is not null && !threadBound)
        {
            const string message = "新线程创建请求已取消，但 thread/start 尚未返回；远端创建结果未知，必须重新连接 Agent。";
            _mcpReadiness.Stop("agent_thread_start_cancelled", message);
            LastError = message;
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastError = Sanitize(exception.Message);
            throw;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task<AgentAccountState> RefreshAccountAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await RefreshAccountCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentLoginResult> StartChatGptLoginAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var transport = RequireCapability(AgentTransportCapabilities.ChatGptLogin, "ChatGPT 登录");
        var result = await transport.StartChatGptLoginAsync(cancellationToken).ConfigureAwait(false);
        AuthenticationState = CodexAgentAuthenticationState.Required;
        return result;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var transport = RequireCapability(AgentTransportCapabilities.Logout, "账号登出");
        await transport.LogoutAsync(cancellationToken).ConfigureAwait(false);
        await RefreshAccountCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task InterruptTurnAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return (_transport ?? throw new InvalidOperationException("Codex Agent Runtime 尚未接入。"))
            .InterruptTurnAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource stopSignal;
        lock (_operationLock)
        {
            _stopRequests++;
            stopSignal = _stopSignal;
        }

        // A fresh-thread operation holds the session gate while waiting for ready.
        // Wake that waiter before acquiring the gate, otherwise Stop waits for its timeout.
        _mcpReadiness.Stop("agent_transport_stopped", "Agent Runtime 已停止，当前线程不再等待 MCP 初始化。");
        stopSignal.Cancel();
        var gateEntered = false;
        try
        {
            await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            await StopTransportAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (gateEntered)
            {
                _sessionGate.Release();
            }

            lock (_operationLock)
            {
                if (--_stopRequests == 0)
                {
                    _stopSignal = new CancellationTokenSource();
                    stopSignal.Dispose();
                }
            }
        }
    }

    private CancellationTokenSource CreateSessionOperationCancellation(CancellationToken callerToken)
    {
        lock (_operationLock)
        {
            if (_stopRequests != 0)
            {
                throw CreateReadinessException("agent_transport_stopped", "runtime",
                    "Agent Runtime 正在停止，不能启动新的会话准备。", ThreadId);
            }

            return CancellationTokenSource.CreateLinkedTokenSource(callerToken, _stopSignal.Token);
        }
    }

    private bool IsStopRequested()
    {
        lock (_operationLock)
        {
            return _stopRequests != 0;
        }
    }

    private object BeginReadinessCycle(bool allowUnscoped, CancellationToken cancellationToken)
    {
        lock (_operationLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopRequests != 0)
            {
                throw CreateReadinessException("agent_transport_stopped", "runtime",
                    "Agent Runtime 正在停止，不能创建新的 MCP 就绪周期。", ThreadId);
            }

            return _mcpReadiness.Begin(allowUnscoped);
        }
    }

    private bool CanReuseSession(
        string workingDirectory,
        Uri endpoint,
        string environmentFingerprint) =>
        _transport is
            {
                IsRunning: true,
                State: AgentSessionTransportState.Ready or AgentSessionTransportState.RunningTurn
            } &&
        string.Equals(_sessionWorkingDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase) &&
        Equals(_sessionMcpEndpoint, endpoint) &&
        string.Equals(
            _sessionEnvironmentFingerprint,
            environmentFingerprint,
            StringComparison.Ordinal);

    private async Task EnsureRequestedThreadAsync(
        string? resumeThreadId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transport = _transport ??
                        throw new InvalidOperationException("Codex Agent Runtime 尚未接入。");
        if (!string.IsNullOrWhiteSpace(resumeThreadId))
        {
            if (!string.Equals(transport.CurrentThreadId, resumeThreadId, StringComparison.Ordinal))
            {
                try
                {
                    await SelectThreadAsync(
                        () => transport.ResumeThreadAsync(resumeThreadId, cancellationToken), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (AgentRpcException exception)
                {
                    WriteFacadeLog(
                        AgentLogLevel.Warning,
                        $"Codex 线程 {resumeThreadId} 无法恢复，将创建新线程：{exception.Message}");
                    await SelectThreadAsync(() => transport.StartThreadAsync(cancellationToken), cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(transport.CurrentThreadId))
        {
            await SelectThreadAsync(() => transport.StartThreadAsync(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }

        _mcpReadiness.BindInitialThread(transport.CurrentThreadId);
    }

    private async Task SelectThreadAsync(
        Func<Task<AgentThreadResult>> selectThread, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cycle = TransportMode == CodexAgentTransportMode.AppServer &&
                    !string.IsNullOrWhiteSpace(ThreadId)
            ? BeginReadinessCycle(allowUnscoped: false, cancellationToken)
            : null;
        var result = await selectThread().WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (cycle is not null)
        {
            _mcpReadiness.Bind(cycle, result.ThreadId);
        }
        else
        {
            _mcpReadiness.BindInitialThread(result.ThreadId);
        }
    }

    public async Task WaitForExecutionReadinessAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConnected)
        {
            throw CreateReadinessException("agent_transport_stopped", "runtime",
                "Agent Runtime 当前未运行，无法准备生成 Turn。", ThreadId);
        }

        if (TransportMode == CodexAgentTransportMode.AppServer)
        {
            await WaitForMcpReadyAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForMcpReadyAsync(CancellationToken cancellationToken)
    {
        _mcpReadiness.BindInitialThread(ThreadId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_mcpReadyTimeout);
        try
        {
            var failure = await _mcpReadiness.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (failure is not null)
            {
                throw CreateReadinessException(failure.Code,
                    failure.Code == "agent_thread_start_cancelled" ? "thread.start" :
                    failure.Code.StartsWith("agent_transport_", StringComparison.Ordinal)
                        ? "runtime" : "mcp.readiness",
                    failure.Message, failure.ThreadId);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw CreateReadinessException("agent_mcp_ready_timeout", "mcp.readiness",
                $"等待当前线程的 gasset_forge MCP ready 超时（{_mcpReadyTimeout.TotalSeconds:0.##} 秒）；Turn 尚未提交。",
                _mcpReadiness.ThreadId ?? ThreadId);
        }
    }

    private AgentReadinessException CreateReadinessException(
        string code, string stage, string message, string? threadId) =>
        new(code, stage, Sanitize(message), threadId, ReadinessStateSummary,
            code == "agent_thread_start_cancelled"
                ? "请重新连接 Agent 以确认可执行会话，再继续原任务；不要重复创建任务。"
                : "原生成任务保持不变；请检查当前线程的 MCP 初始化日志，连接恢复后继续原任务。");

    private async Task<AgentAccountState> RefreshAccountCoreAsync(CancellationToken cancellationToken)
    {
        var transport = RequireCapability(AgentTransportCapabilities.AccountRead, "读取账号");
        var account = await transport.ReadAccountAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(transport, _transport))
        {
            throw CreateReadinessException("agent_transport_stopped", "runtime",
                "原 Agent Runtime 已结束，忽略其迟到的账号状态。", null);
        }

        Account = account;
        AuthenticationState = Account.IsAuthenticated
            ? CodexAgentAuthenticationState.Authenticated
            : Account.RequiresOpenAiAuth
                ? CodexAgentAuthenticationState.Required
                : CodexAgentAuthenticationState.NotRequired;
        return Account;
    }

    private IAgentSessionTransport RequireCapability(
        AgentTransportCapabilities capability,
        string operation)
    {
        var transport = _transport ??
                        throw new InvalidOperationException("Codex Agent Runtime 尚未接入。");
        if (!transport.Capabilities.HasFlag(capability))
        {
            throw new NotSupportedException($"当前 {transport.DisplayName} 通道不支持{operation}。");
        }

        return transport;
    }

    private void AttachTransport(IAgentSessionTransport transport)
    {
        transport.StateChanged += OnTransportStateChanged;
        transport.LogReceived += OnTransportLogReceived;
        transport.NotificationReceived += OnTransportNotificationReceived;
        transport.TurnCompleted += OnTransportTurnCompleted;
        transport.McpStatusChanged += OnTransportMcpStatusChanged;
    }

    private void DetachTransport(IAgentSessionTransport transport)
    {
        transport.StateChanged -= OnTransportStateChanged;
        transport.LogReceived -= OnTransportLogReceived;
        transport.NotificationReceived -= OnTransportNotificationReceived;
        transport.TurnCompleted -= OnTransportTurnCompleted;
        transport.McpStatusChanged -= OnTransportMcpStatusChanged;
    }

    private async Task StopTransportAsync(CancellationToken cancellationToken)
    {
        _mcpReadiness.Stop("agent_transport_stopped", "Agent Runtime 已停止，当前线程不再等待 MCP 初始化。");
        var transport = _transport;
        _transport = null;
        if (transport is not null)
        {
            try
            {
                await transport.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                DetachTransport(transport);
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }

        TransportMode = CodexAgentTransportMode.None;
        Account = null;
        AuthenticationState = CodexAgentAuthenticationState.Unknown;
        _sessionWorkingDirectory = null;
        _sessionMcpEndpoint = null;
        _sessionEnvironmentFingerprint = null;
        _sessionSecret = null;
    }

    private void OnTransportStateChanged(object? sender, AgentTransportStateChangedEventArgs eventArgs)
    {
        if (eventArgs.State == AgentSessionTransportState.Faulted)
        {
            LastError = eventArgs.Detail is null ? null : Sanitize(eventArgs.Detail);
        }

        if (ReferenceEquals(sender, _transport) && eventArgs.State is
            AgentSessionTransportState.Stopped or AgentSessionTransportState.Faulted)
        {
            _mcpReadiness.Stop(
                eventArgs.State == AgentSessionTransportState.Faulted
                    ? "agent_transport_faulted" : "agent_transport_stopped",
                eventArgs.Detail is null ? "Agent Runtime 已停止。" : Sanitize(eventArgs.Detail));
        }

        StateChanged?.Invoke(this, eventArgs);
    }

    private void OnTransportLogReceived(object? sender, AgentTransportLogEventArgs eventArgs) =>
        LogReceived?.Invoke(this, eventArgs);

    private void OnTransportNotificationReceived(object? sender, AgentNotificationEventArgs eventArgs) =>
        NotificationReceived?.Invoke(this, eventArgs);

    private void OnTransportTurnCompleted(object? sender, AgentTurnCompletedEventArgs eventArgs) =>
        TurnCompleted?.Invoke(this, eventArgs);

    private void OnTransportMcpStatusChanged(object? sender, AgentMcpStatusChangedEventArgs eventArgs)
    {
        if (ReferenceEquals(sender, _transport) &&
            ProductIdentity.IsKnownMcpServerName(eventArgs.ServerName))
        {
            var accepted = _mcpReadiness.Observe(
                eventArgs.ThreadId, eventArgs.Status,
                eventArgs.Error is null ? null : Sanitize(eventArgs.Error));
            if (accepted && eventArgs.Status is "failed" or "cancelled" or "canceled")
            {
                LastError = Sanitize(eventArgs.Error ?? "GAssetForge MCP 初始化失败或被取消。");
            }
        }

        McpStatusChanged?.Invoke(this, eventArgs);
    }

    private static bool ShouldFallbackFromAppServer(
        Exception exception,
        CancellationToken userCancellationToken)
    {
        if (exception is OperationCanceledException && userCancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (exception is AgentReadinessException)
        {
            // MCP readiness is not an authentication or app-server compatibility failure.
            // Preserve its diagnostic identity and the original task instead of starting another transport.
            return false;
        }

        return exception is AgentMcpStartupException or
            AgentRpcException or
            TimeoutException or
            NotSupportedException or
            Win32Exception or
            IOException or
            InvalidDataException or
            InvalidOperationException or
            OperationCanceledException;
    }

    private static bool IsAuthenticationRequired(AgentRpcException exception)
    {
        var message = exception.Message;
        return message.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("login", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("logged in", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("credential", StringComparison.OrdinalIgnoreCase);
    }

    private void WriteFacadeLog(AgentLogLevel level, string message) =>
        LogReceived?.Invoke(
            this,
            new AgentTransportLogEventArgs(DateTimeOffset.Now, level, Sanitize(message)));

    private string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(_sessionSecret))
        {
            return message;
        }

        return message.Replace(_sessionSecret, "[REDACTED]", StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _disposed = true;
        _sessionGate.Dispose();
        _stopSignal.Dispose();
    }
}

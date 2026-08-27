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
    private static readonly TimeSpan McpReadyTimeout = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly object _configurationLock = new();
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
    private TaskCompletionSource<bool>? _mcpReadyCompletion;
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
        Func<IReadOnlyDictionary<string, string?>>? environmentVariablesProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexExecutablePath);
        _codexExecutablePath = codexExecutablePath;
        _appServerFactory = appServerFactory ?? throw new ArgumentNullException(nameof(appServerFactory));
        _execFactory = execFactory ?? throw new ArgumentNullException(nameof(execFactory));
        _pathEnvironmentProvider = pathEnvironmentProvider ??
                                   ProcessEnvironmentSnapshot.BuildFreshPathEnvironment;
        _environmentVariablesProvider = environmentVariablesProvider ??
                                        ProcessEnvironmentSnapshot.BuildFreshAgentEnvironmentVariables;
    }

    public CodexAgentTransportMode TransportMode { get; private set; }

    public AgentSessionTransportState State =>
        _transport?.State ?? AgentSessionTransportState.Stopped;

    public bool IsConnected => State is
        AgentSessionTransportState.Ready or AgentSessionTransportState.RunningTurn;

    public bool IsTurnRunning => State == AgentSessionTransportState.RunningTurn;

    public bool McpReady { get; private set; }

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
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
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
                    await RefreshAccountCoreAsync(cancellationToken).ConfigureAwait(false);
                    if (AuthenticationState == CodexAgentAuthenticationState.Required)
                    {
                        return;
                    }
                }

                await EnsureRequestedThreadAsync(resumeThreadId, cancellationToken).ConfigureAwait(false);
                if (TransportMode == CodexAgentTransportMode.AppServer && !McpReady)
                {
                    _mcpReadyCompletion ??= NewMcpReadyCompletion();
                    await WaitForMcpReadyAsync(cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            await StopTransportAsync(CancellationToken.None).ConfigureAwait(false);
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
            McpReady = false;
            _mcpReadyCompletion = NewMcpReadyCompletion();
            Account = null;
            AuthenticationState = CodexAgentAuthenticationState.Unknown;
            try
            {
                var appServer = _appServerFactory();
                AttachTransport(appServer);
                _transport = appServer;
                await appServer.StartAsync(options, cancellationToken).ConfigureAwait(false);
                TransportMode = CodexAgentTransportMode.AppServer;
                await RefreshAccountCoreAsync(cancellationToken).ConfigureAwait(false);
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
                _mcpReadyCompletion = null;
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
        try
        {
            var transport = RequireCapability(
                AgentTransportCapabilities.ExplicitThreadStart,
                "创建独立线程");
            if (!IsConnected || transport.State == AgentSessionTransportState.RunningTurn)
            {
                throw new InvalidOperationException(
                    "Codex Agent Runtime 尚未处于可创建独立线程的 Ready 状态。");
            }

            var result = await transport.StartThreadAsync(cancellationToken).ConfigureAwait(false);
            LastError = null;
            return result;
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
        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopTransportAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sessionGate.Release();
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
        var transport = _transport ??
                        throw new InvalidOperationException("Codex Agent Runtime 尚未接入。");
        if (!string.IsNullOrWhiteSpace(resumeThreadId))
        {
            if (!string.Equals(transport.CurrentThreadId, resumeThreadId, StringComparison.Ordinal))
            {
                try
                {
                    await transport.ResumeThreadAsync(resumeThreadId, cancellationToken).ConfigureAwait(false);
                }
                catch (AgentRpcException exception)
                {
                    WriteFacadeLog(
                        AgentLogLevel.Warning,
                        $"Codex 线程 {resumeThreadId} 无法恢复，将创建新线程：{exception.Message}");
                    await transport.StartThreadAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(transport.CurrentThreadId))
        {
            await transport.StartThreadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForMcpReadyAsync(CancellationToken cancellationToken)
    {
        if (McpReady)
        {
            return;
        }

        var completion = _mcpReadyCompletion ??= NewMcpReadyCompletion();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(McpReadyTimeout);
        try
        {
            var ready = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (!ready)
            {
                throw new AgentMcpStartupException(
                    LastError ?? "GAssetForge MCP 未能进入 ready 状态。");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgentMcpStartupException(
                $"等待 gasset_forge MCP ready 超时（{McpReadyTimeout.TotalSeconds:0} 秒）。");
        }
    }

    private static TaskCompletionSource<bool> NewMcpReadyCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<AgentAccountState> RefreshAccountCoreAsync(CancellationToken cancellationToken)
    {
        var transport = RequireCapability(AgentTransportCapabilities.AccountRead, "读取账号");
        Account = await transport.ReadAccountAsync(cancellationToken).ConfigureAwait(false);
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
        McpReady = false;
        _mcpReadyCompletion = null;
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
        if (ProductIdentity.IsKnownMcpServerName(eventArgs.ServerName))
        {
            McpReady = string.Equals(eventArgs.Status, "ready", StringComparison.OrdinalIgnoreCase);
            if (McpReady)
            {
                _mcpReadyCompletion?.TrySetResult(true);
            }

            if (string.Equals(eventArgs.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                LastError = Sanitize(eventArgs.Error ?? "GAssetForge MCP 启动失败。");
                _mcpReadyCompletion?.TrySetResult(false);
            }
            else if (string.Equals(eventArgs.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                LastError = Sanitize(eventArgs.Error ?? "GAssetForge MCP 启动已取消。");
                _mcpReadyCompletion?.TrySetResult(false);
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
    }
}

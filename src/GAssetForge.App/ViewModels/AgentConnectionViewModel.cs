using GAssetForge.App.Infrastructure;
using GAssetForge.App.Services;
using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.Core;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Input;
using System.Windows.Threading;

namespace GAssetForge.App.ViewModels;

public enum AgentConnectionStatus
{
    Stopped,
    Starting,
    WaitingForAgent,
    Connected,
    Stopping,
    Faulted
}

public sealed class AgentConnectionSettingsChangedEventArgs : EventArgs
{
    public AgentConnectionSettingsChangedEventArgs(string agentKey, int port)
    {
        AgentKey = agentKey;
        Port = port;
    }

    public string AgentKey { get; }

    public int Port { get; }
}

public sealed class AgentSessionChangedEventArgs : EventArgs
{
    public AgentSessionChangedEventArgs(string? threadId, string workingDirectory)
    {
        ThreadId = threadId;
        WorkingDirectory = workingDirectory;
    }

    public string? ThreadId { get; }

    public string WorkingDirectory { get; }
}

public sealed class AgentConnectionViewModel : ObservableObject, IDisposable
{
    public const int DefaultPort = 43120;
    private const int MaximumLogEntries = 500;
    private static readonly TimeSpan McpHealthReadyTimeout = TimeSpan.FromSeconds(15);

    // Kept for compatibility with the delivery smoke tests and the asset pipeline.
    private readonly McpServerProcessService _serverService;
    private readonly CodexAgentRuntimeService _runtimeService;
    private readonly IWorkbenchLogSink? _workbenchLogSink;
    private readonly IWorkbenchLogRedactionRegistry? _workbenchRedactionRegistry;
    private readonly AgentActivityParser _activityParser = new();
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly AsyncRelayCommand _startCommand;
    private readonly AsyncRelayCommand _stopCommand;
    private readonly AsyncRelayCommand _refreshAuthenticationCommand;
    private readonly AsyncRelayCommand _loginCommand;
    private readonly AsyncRelayCommand _interruptTurnCommand;
    private readonly RelayCommand _copyCodexConnectionPromptCommand;
    private AgentProfile _selectedAgent;
    private int _port;
    private AgentConnectionStatus _statusKind = AgentConnectionStatus.Stopped;
    private string _statusText = "Agent 会话未启动";
    private string _lastHandshakeText = "尚未收到 Agent 握手";
    private string _workingDirectory;
    private string? _resumeThreadId;
    private string? _activeWorkingDirectory;
    private string? _lastPublishedThreadId;
    private string? _lastPublishedWorkingDirectory;
    private string? _faultMessage;
    private string? _lastRuntimeDetail;
    private string _lastTurnStatus = "尚未运行 Turn";
    private string _lastMcpToolStatus = "尚未初始化";
    private string _codexConnectionPromptStatusText = "可复制完整连接检查指令并粘贴到 Codex 聊天；提示只引用令牌环境变量名。";
    private CancellationTokenSource? _startCancellation;
    private bool _isStarting;
    private bool _isStopping;
    private bool _disposed;

    public AgentConnectionViewModel(
        McpServerProcessService serverService,
        string? selectedAgentKey = null,
        int port = DefaultPort)
        : this(
            serverService,
            new CodexAgentRuntimeService(),
            selectedAgentKey,
            port,
            Environment.CurrentDirectory,
            null)
    {
    }

    public AgentConnectionViewModel(
        McpServerProcessService serverService,
        CodexAgentRuntimeService runtimeService,
        string? selectedAgentKey = null,
        int port = DefaultPort,
        string? workingDirectory = null,
        string? resumeThreadId = null,
        IWorkbenchLogSink? workbenchLogSink = null)
    {
        _serverService = serverService ?? throw new ArgumentNullException(nameof(serverService));
        _runtimeService = runtimeService ?? throw new ArgumentNullException(nameof(runtimeService));
        _workbenchLogSink = workbenchLogSink;
        _workbenchRedactionRegistry = workbenchLogSink as IWorkbenchLogRedactionRegistry;
        _workbenchRedactionRegistry?.RegisterSensitiveValue(
            _serverService.TokenEnvironmentVariableValue);
        _dispatcher = Dispatcher.CurrentDispatcher;
        _selectedAgent = AgentProfile.Find(selectedAgentKey);
        _port = IsValidPort(port) ? port : DefaultPort;
        _workingDirectory = NormalizeWorkingDirectory(workingDirectory ?? Environment.CurrentDirectory);
        _resumeThreadId = NormalizeThreadId(resumeThreadId);
        _lastPublishedThreadId = _resumeThreadId;
        _lastPublishedWorkingDirectory = _workingDirectory;

        _startCommand = new AsyncRelayCommand(StartSessionAsync, () => CanStart);
        _stopCommand = new AsyncRelayCommand(StopSessionAsync, () => CanStop);
        _refreshAuthenticationCommand = new AsyncRelayCommand(
            RefreshAuthenticationAsync,
            () => CanRefreshAuthentication);
        _loginCommand = new AsyncRelayCommand(LoginAsync, () => CanLogin);
        _interruptTurnCommand = new AsyncRelayCommand(InterruptTurnAsync, () => CanInterruptTurn);

        StartCommand = _startCommand;
        StopCommand = _stopCommand;
        RefreshAuthenticationCommand = _refreshAuthenticationCommand;
        LoginCommand = _loginCommand;
        InterruptTurnCommand = _interruptTurnCommand;
        ClearLogCommand = new RelayCommand(ClearLogs, () => Logs.Count > 0);
        CopyConfigCommand = new RelayCommand(() => CopyConfigRequested?.Invoke(this, ConfigSnippet));
        _copyCodexConnectionPromptCommand = new RelayCommand(
            () => CopyCodexConnectionPromptRequested?.Invoke(this, CodexConnectionPrompt),
            () => CanCopyCodexConnectionPrompt);
        CopyCodexConnectionPromptCommand = _copyCodexConnectionPromptCommand;

        _serverService.StateChanged += OnServerStateChanged;
        _serverService.LogReceived += OnServerLogReceived;
        _runtimeService.StateChanged += OnRuntimeStateChanged;
        _runtimeService.LogReceived += OnRuntimeLogReceived;
        _runtimeService.NotificationReceived += OnRuntimeNotificationReceived;
        _runtimeService.TurnCompleted += OnRuntimeTurnCompleted;
        _runtimeService.McpStatusChanged += OnRuntimeMcpStatusChanged;

        RefreshState();
    }

    public event EventHandler<AgentConnectionSettingsChangedEventArgs>? SettingsChanged;

    public event EventHandler<AgentSessionChangedEventArgs>? SessionChanged;

    public event EventHandler<AgentActivityEventArgs>? AgentActivityReceived;

    public event EventHandler<string>? CopyConfigRequested;

    public event EventHandler<string>? CopyCodexConnectionPromptRequested;

    public IReadOnlyList<AgentProfile> Agents => AgentProfile.Supported;

    public ObservableCollection<string> Logs { get; } = [];

    public AgentProfile SelectedAgent
    {
        get => _selectedAgent;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _selectedAgent, value))
            {
                _faultMessage = null;
                OnPropertyChanged(nameof(ConfigSnippet));
                NotifySettingsChanged();
                RefreshState();
            }
        }
    }

    public int Port
    {
        get => _port;
        set
        {
            if (SetProperty(ref _port, value))
            {
                _faultMessage = null;
                OnPropertyChanged(nameof(Endpoint));
                OnPropertyChanged(nameof(ConfigSnippet));
                OnPropertyChanged(nameof(CodexConnectionPrompt));
                OnPropertyChanged(nameof(IsPortValid));
                OnPropertyChanged(nameof(CanCopyCodexConnectionPrompt));
                if (IsPortValid)
                {
                    NotifySettingsChanged();
                }

                RefreshState();
            }
        }
    }

    public bool IsPortValid => IsValidPort(Port);

    public string Endpoint => IsPortValid
        ? $"http://127.0.0.1:{Port}/mcp"
        : "端口必须位于 1024 到 65535 之间";

    public string ConfigSnippet
    {
        get
        {
            if (!IsPortValid)
            {
                return "请先输入 1024 到 65535 之间的有效端口。";
            }

            var snippet = SelectedAgent.CreateConfigSnippet(new Uri(Endpoint, UriKind.Absolute));
            return "# 仅供诊断；认证令牌由 GAssetForge 注入进程环境，不在界面中显示。" +
                   Environment.NewLine + snippet;
        }
    }

    public bool CanCopyCodexConnectionPrompt =>
        IsPortValid && Directory.Exists(WorkingDirectory);

    public string CodexConnectionPrompt => CanCopyCodexConnectionPrompt
        ? CodexManualFallbackPromptBuilder.BuildConnectionPrompt(
            WorkingDirectory,
            new Uri(Endpoint, UriKind.Absolute),
            _serverService.TokenEnvironmentVariableName)
        : "请先选择有效项目目录并设置 1024 到 65535 之间的 MCP 端口。";

    public string CodexConnectionPromptStatusText
    {
        get => _codexConnectionPromptStatusText;
        private set => SetProperty(ref _codexConnectionPromptStatusText, value);
    }

    public AgentConnectionStatus StatusKind
    {
        get => _statusKind;
        private set
        {
            if (SetProperty(ref _statusKind, value))
            {
                OnPropertyChanged(nameof(StatusBrushKey));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string StatusBrushKey => StatusKind switch
    {
        AgentConnectionStatus.Connected => "Success",
        AgentConnectionStatus.Faulted => "Error",
        AgentConnectionStatus.Starting or AgentConnectionStatus.Stopping => "Progress",
        AgentConnectionStatus.WaitingForAgent => "Warning",
        _ => "Neutral"
    };

    public bool IsRunning =>
        _isStarting ||
        _isStopping ||
        _serverService.IsProcessRunning ||
        _runtimeService.State is not AgentSessionTransportState.Stopped;

    // Connected means a generation task can be executed, rather than merely that HTTP MCP shook hands.
    public bool IsConnected => IsSessionReady();

    public bool IsTurnRunning => _runtimeService.IsTurnRunning;

    public bool CanEditSettings => !IsRunning;

    public bool CanStart =>
        !_disposed &&
        CanEditSettings &&
        IsPortValid &&
        SelectedAgent.IsRuntimeAvailable &&
        Directory.Exists(WorkingDirectory);

    public bool CanStop => !_disposed && IsRunning;

    public bool CanLogin =>
        !_disposed &&
        !_isStarting &&
        !_isStopping &&
        _runtimeService.TransportMode == CodexAgentTransportMode.AppServer &&
        _runtimeService.AuthenticationState == CodexAgentAuthenticationState.Required;

    public bool CanRefreshAuthentication =>
        !_disposed &&
        !_isStarting &&
        !_isStopping &&
        _runtimeService.TransportMode == CodexAgentTransportMode.AppServer &&
        _runtimeService.State is AgentSessionTransportState.Ready or AgentSessionTransportState.RunningTurn;

    public bool CanInterruptTurn =>
        !_disposed &&
        _runtimeService.IsTurnRunning;

    public string LastHandshakeText
    {
        get => _lastHandshakeText;
        private set => SetProperty(ref _lastHandshakeText, value);
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        private set
        {
            if (!SetProperty(ref _workingDirectory, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CodexConnectionPrompt));
            OnPropertyChanged(nameof(CanCopyCodexConnectionPrompt));
            _copyCodexConnectionPromptCommand?.RaiseCanExecuteChanged();
        }
    }

    public string CodexVersionText => string.IsNullOrWhiteSpace(_runtimeService.RuntimeVersion)
        ? "未检测（启动会话后读取）"
        : _runtimeService.RuntimeVersion!;

    public string AuthenticationText => _runtimeService.AuthenticationState switch
    {
        CodexAgentAuthenticationState.Authenticated => BuildAuthenticatedAccountText(_runtimeService.Account),
        CodexAgentAuthenticationState.NotRequired => "当前运行方式不要求 OpenAI 账号认证",
        CodexAgentAuthenticationState.Required => "需要登录 ChatGPT / OpenAI 账号",
        _ => "尚未检查"
    };

    public string RuntimeModeText => _runtimeService.TransportMode switch
    {
        CodexAgentTransportMode.AppServer => "Codex App Server（持久线程）",
        CodexAgentTransportMode.ExecFallback => "codex exec --json（兼容降级）",
        _ => "未启动"
    };

    public string AppServerStatusText
    {
        get
        {
            if (_runtimeService.TransportMode == CodexAgentTransportMode.ExecFallback)
            {
                return "App Server 不可用，已自动切换 exec JSONL";
            }

            return _runtimeService.State switch
            {
                AgentSessionTransportState.Starting => "正在初始化 App Server",
                AgentSessionTransportState.Ready => _lastRuntimeDetail ?? "App Server 已就绪",
                AgentSessionTransportState.RunningTurn => "正在执行 Turn",
                AgentSessionTransportState.Stopping => "正在停止 Runtime",
                AgentSessionTransportState.Faulted => _runtimeService.LastError ?? _lastRuntimeDetail ?? "Runtime 异常",
                _ => "未启动"
            };
        }
    }

    public string McpServerStatusText => _serverService.State switch
    {
        McpServerProcessState.Starting => "正在启动并执行健康检查",
        McpServerProcessState.WaitingForAgent => "健康检查通过，等待 Agent 工具握手",
        McpServerProcessState.Connected => "运行正常，已收到 Agent 握手",
        McpServerProcessState.Stopping => "正在停止",
        McpServerProcessState.Faulted => "MCP Server 异常",
        _ => "未启动"
    };

    public string McpToolStatusText
    {
        get
        {
            if (_runtimeService.TransportMode == CodexAgentTransportMode.ExecFallback)
            {
                return "exec 降级通道将在每个 Turn 中加载 MCP";
            }

            if (_runtimeService.McpReady)
            {
                return $"{ProductIdentity.McpServerName} 工具已 ready";
            }

            if (_runtimeService.AuthenticationState == CodexAgentAuthenticationState.Required)
            {
                return $"等待登录后初始化 {ProductIdentity.McpServerName} 工具";
            }

            return _lastMcpToolStatus;
        }
    }

    public string ThreadIdText
    {
        get
        {
            var currentThreadId = IsActiveWorkingDirectoryCurrent()
                ? _runtimeService.ThreadId
                : null;
            if (!string.IsNullOrWhiteSpace(currentThreadId))
            {
                return currentThreadId;
            }

            return string.IsNullOrWhiteSpace(_resumeThreadId)
                ? "尚未建立"
                : $"待恢复：{_resumeThreadId}";
        }
    }

    public string TurnStatusText => _runtimeService.IsTurnRunning
        ? string.IsNullOrWhiteSpace(_runtimeService.ActiveTurnId)
            ? "Turn 执行中"
            : $"Turn 执行中：{_runtimeService.ActiveTurnId}"
        : _lastTurnStatus;

    public ICommand StartCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand RefreshAuthenticationCommand { get; }

    public ICommand LoginCommand { get; }

    public ICommand InterruptTurnCommand { get; }

    public ICommand ClearLogCommand { get; }

    public ICommand CopyConfigCommand { get; }

    public ICommand CopyCodexConnectionPromptCommand { get; }

    public void ReportCodexConnectionPromptCopyResult(bool copied, string? errorMessage = null)
    {
        CodexConnectionPromptStatusText = copied
            ? "已复制 Codex 连接指令，可直接粘贴到 Codex 聊天窗口。"
            : $"复制 Codex 连接指令失败：{(string.IsNullOrWhiteSpace(errorMessage) ? "剪贴板不可用" : errorMessage.Trim())}";
    }

    public void ConfigureWorkingDirectory(string workingDirectory, string? resumeThreadId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalized = NormalizeWorkingDirectory(workingDirectory);
        var pathChanged = !PathEquals(WorkingDirectory, normalized);
        WorkingDirectory = normalized;
        _resumeThreadId = NormalizeThreadId(resumeThreadId);
        if (pathChanged)
        {
            _faultMessage = null;
            _lastTurnStatus = "尚未运行 Turn";
        }

        PublishSession(_resumeThreadId, WorkingDirectory);
        RefreshState();
    }

    public async Task ReconfigureWorkingDirectoryAsync(
        string workingDirectory,
        string? resumeThreadId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalized = NormalizeWorkingDirectory(workingDirectory);
        if (!PathEquals(WorkingDirectory, normalized) && IsRunning)
        {
            await StopSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_dispatcher.CheckAccess())
        {
            ConfigureWorkingDirectory(normalized, resumeThreadId);
        }
        else if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
        {
            await _dispatcher.InvokeAsync(
                () => ConfigureWorkingDirectory(normalized, resumeThreadId),
                DispatcherPriority.DataBind);
        }
    }

    public async Task<AgentTurnResult> RunTurnAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException(
                    "Agent 会话尚未达到可执行状态。请先启动会话并完成认证与 MCP 工具初始化。");
            }

            Dispatch(() =>
            {
                _lastTurnStatus = "正在提交 Turn";
                RefreshState();
            });
            var result = await _runtimeService.RunTurnAsync(prompt, cancellationToken).ConfigureAwait(false);
            Dispatch(() =>
            {
                _lastTurnStatus = result.IsSuccess
                    ? "最近 Turn 已完成"
                    : $"最近 Turn：{result.Status} · {result.ErrorMessage ?? "未提供错误信息"}";
                CaptureRuntimeSession();
                RefreshState();
            });
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Dispatch(() =>
            {
                _lastTurnStatus = $"Turn 失败：{Sanitize(exception.Message)}";
                AddLog(DateTimeOffset.Now, _lastTurnStatus, true);
                RefreshState();
            });
            throw;
        }
        finally
        {
            _turnGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _startCancellation?.Cancel();
        try
        {
            StopSessionAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // Shutdown continues so both child processes and handles are released.
        }

        _disposed = true;
        _serverService.StateChanged -= OnServerStateChanged;
        _serverService.LogReceived -= OnServerLogReceived;
        _runtimeService.StateChanged -= OnRuntimeStateChanged;
        _runtimeService.LogReceived -= OnRuntimeLogReceived;
        _runtimeService.NotificationReceived -= OnRuntimeNotificationReceived;
        _runtimeService.TurnCompleted -= OnRuntimeTurnCompleted;
        _runtimeService.McpStatusChanged -= OnRuntimeMcpStatusChanged;
        _runtimeService.DisposeAsync().GetAwaiter().GetResult();
        _serverService.Dispose();
        _startCancellation?.Dispose();
        _lifecycleGate.Dispose();
        _turnGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task StartSessionAsync() => StartSessionAsync(CancellationToken.None);

    private async Task StartSessionAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (!CanStart)
            {
                return;
            }

            _faultMessage = null;
            _isStarting = true;
            _startCancellation?.Dispose();
            _startCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            RefreshState();
            AddLog(DateTimeOffset.Now, $"正在启动 {SelectedAgent.DisplayName} Agent 会话。", false);

            try
            {
                await _serverService.StartAsync(
                    Port,
                    SelectedAgent.Key,
                    _startCancellation.Token).ConfigureAwait(true);
                await WaitForMcpHealthReadyAsync(_startCancellation.Token).ConfigureAwait(true);
                await _runtimeService.EnsureSessionAsync(
                    WorkingDirectory,
                    _resumeThreadId,
                    _serverService,
                    _startCancellation.Token).ConfigureAwait(true);

                _activeWorkingDirectory = WorkingDirectory;
                CaptureRuntimeSession();
                if (_runtimeService.AuthenticationState == CodexAgentAuthenticationState.Required)
                {
                    AddLog(
                        DateTimeOffset.Now,
                        "Codex App Server 已启动，但需要先完成账号登录；登录后点击“刷新认证”。",
                        false);
                }
                else if (_runtimeService.TransportMode == CodexAgentTransportMode.ExecFallback)
                {
                    AddLog(
                        DateTimeOffset.Now,
                        "App Server 不可用，当前使用 codex exec --json 兼容降级通道。",
                        false);
                }
                else
                {
                    AddLog(DateTimeOffset.Now, "Codex App Server 与 MCP 工具均已就绪。", false);
                }
            }
            catch (OperationCanceledException) when (_startCancellation.IsCancellationRequested)
            {
                AddLog(DateTimeOffset.Now, "Agent 会话启动已取消。", false);
            }
            catch (Exception exception)
            {
                _faultMessage = Sanitize(exception.Message);
                AddLog(DateTimeOffset.Now, $"Agent 会话启动失败：{_faultMessage}", true);
                await StopServicesAfterFailedStartAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            _isStarting = false;
            _startCancellation?.Dispose();
            _startCancellation = null;
            RefreshState();
            _lifecycleGate.Release();
        }
    }

    private Task StopSessionAsync() => StopSessionAsync(CancellationToken.None);

    private async Task StopSessionAsync(CancellationToken cancellationToken)
    {
        _startCancellation?.Cancel();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_serverService.IsProcessRunning &&
                _runtimeService.State == AgentSessionTransportState.Stopped &&
                !_isStarting)
            {
                Dispatch(RefreshState);
                return;
            }

            Dispatch(() =>
            {
                _isStopping = true;
                _faultMessage = null;
                CaptureRuntimeSession();
                RefreshState();
            });

            Exception? stopFailure = null;
            try
            {
                await _runtimeService.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                stopFailure = exception;
                Dispatch(() => AddLog(
                    DateTimeOffset.Now,
                    $"停止 Agent Runtime 失败：{Sanitize(exception.Message)}",
                    true));
            }

            try
            {
                await _serverService.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                stopFailure ??= exception;
                Dispatch(() => AddLog(
                    DateTimeOffset.Now,
                    $"停止 MCP Server 失败：{Sanitize(exception.Message)}",
                    true));
            }

            Dispatch(() =>
            {
                _activeWorkingDirectory = null;
                _lastMcpToolStatus = "尚未初始化";
                _lastTurnStatus = "尚未运行 Turn";
                _faultMessage = stopFailure is null ? null : Sanitize(stopFailure.Message);
            });
        }
        finally
        {
            Dispatch(() =>
            {
                _isStopping = false;
                RefreshState();
            });
            _lifecycleGate.Release();
        }
    }

    private async Task StopServicesAfterFailedStartAsync()
    {
        try
        {
            await _runtimeService.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AddLog(DateTimeOffset.Now, $"清理 Agent Runtime 失败：{Sanitize(exception.Message)}", true);
        }

        try
        {
            await _serverService.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AddLog(DateTimeOffset.Now, $"清理 MCP Server 失败：{Sanitize(exception.Message)}", true);
        }

        _activeWorkingDirectory = null;
    }

    private async Task RefreshAuthenticationAsync()
    {
        try
        {
            var account = await _runtimeService.RefreshAccountAsync().ConfigureAwait(true);
            AddLog(
                DateTimeOffset.Now,
                account.IsAuthenticated ? "Codex 账号认证已确认。" : "Codex 账号仍未完成认证。",
                false);

            if (account.IsAuthenticated && _serverService.IsProcessRunning)
            {
                await _runtimeService.EnsureSessionAsync(
                    WorkingDirectory,
                    _resumeThreadId,
                    _serverService).ConfigureAwait(true);
                _activeWorkingDirectory = WorkingDirectory;
                CaptureRuntimeSession();
            }
        }
        catch (Exception exception)
        {
            _faultMessage = Sanitize(exception.Message);
            AddLog(DateTimeOffset.Now, $"刷新认证失败：{_faultMessage}", true);
        }
        finally
        {
            RefreshState();
        }
    }

    private async Task LoginAsync()
    {
        try
        {
            var login = await _runtimeService.StartChatGptLoginAsync().ConfigureAwait(true);
            AddLog(DateTimeOffset.Now, "已启动 Codex ChatGPT 设备登录流程。", false);
            if (login.VerificationUri is not null)
            {
                AddLog(DateTimeOffset.Now, $"登录地址：{login.VerificationUri}", false);
            }

            if (!string.IsNullOrWhiteSpace(login.UserCode))
            {
                _workbenchRedactionRegistry?.RegisterSensitiveValue(login.UserCode);
                AddLog(DateTimeOffset.Now, $"设备验证码：{login.UserCode}", false);
            }

            AddLog(DateTimeOffset.Now, "浏览器完成登录后，请点击“刷新认证”。", false);
        }
        catch (Exception exception)
        {
            _faultMessage = Sanitize(exception.Message);
            AddLog(DateTimeOffset.Now, $"启动登录失败：{_faultMessage}", true);
        }
        finally
        {
            RefreshState();
        }
    }

    private async Task InterruptTurnAsync()
    {
        try
        {
            await _runtimeService.InterruptTurnAsync().ConfigureAwait(true);
            _lastTurnStatus = "已请求中断当前 Turn";
            AddLog(DateTimeOffset.Now, _lastTurnStatus, false);
        }
        catch (Exception exception)
        {
            _lastTurnStatus = $"中断 Turn 失败：{Sanitize(exception.Message)}";
            AddLog(DateTimeOffset.Now, _lastTurnStatus, true);
        }
        finally
        {
            RefreshState();
        }
    }

    private async Task WaitForMcpHealthReadyAsync(CancellationToken cancellationToken)
    {
        if (IsMcpHealthReady(_serverService.State))
        {
            return;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, McpServerStateChangedEventArgs eventArgs)
        {
            if (IsMcpHealthReady(eventArgs.State))
            {
                completion.TrySetResult(true);
            }
            else if (eventArgs.State == McpServerProcessState.Faulted)
            {
                completion.TrySetException(new InvalidOperationException(
                    eventArgs.Detail ?? "MCP Server 健康检查失败。"));
            }
            else if (eventArgs.State == McpServerProcessState.Stopped)
            {
                completion.TrySetException(new InvalidOperationException(
                    "MCP Server 在健康检查完成前停止。"));
            }
        }

        _serverService.StateChanged += Handler;
        try
        {
            if (IsMcpHealthReady(_serverService.State))
            {
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(McpHealthReadyTimeout);
            try
            {
                await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"等待 MCP Server 健康检查超时（{McpHealthReadyTimeout.TotalSeconds:0} 秒）。");
            }
        }
        finally
        {
            _serverService.StateChanged -= Handler;
        }
    }

    private void OnServerStateChanged(object? sender, McpServerStateChangedEventArgs eventArgs) =>
        Dispatch(() =>
        {
            LastHandshakeText = BuildLastHandshakeText(eventArgs);
            if (eventArgs.State == McpServerProcessState.Faulted && !_isStopping)
            {
                _faultMessage = Sanitize(eventArgs.Detail ?? "MCP Server 运行异常");
            }

            RefreshState();
        });

    private void OnServerLogReceived(object? sender, McpServerLogEventArgs eventArgs) =>
        Dispatch(() => AddLog(eventArgs.Timestamp, eventArgs.Message, eventArgs.IsError, "MCP Server"));

    private void OnRuntimeStateChanged(object? sender, AgentTransportStateChangedEventArgs eventArgs) =>
        Dispatch(() =>
        {
            _lastRuntimeDetail = string.IsNullOrWhiteSpace(eventArgs.Detail)
                ? null
                : Sanitize(eventArgs.Detail);
            if (eventArgs.State == AgentSessionTransportState.Faulted && !_isStopping)
            {
                _faultMessage = _lastRuntimeDetail ?? _runtimeService.LastError ?? "Agent Runtime 运行异常";
            }

            if (eventArgs.State is AgentSessionTransportState.Stopped or AgentSessionTransportState.Faulted)
            {
                _activityParser.Reset();
            }

            CaptureRuntimeSession();
            RefreshState();
        });

    private void OnRuntimeLogReceived(object? sender, AgentTransportLogEventArgs eventArgs) =>
        Dispatch(() => AddLog(
            eventArgs.Timestamp,
            eventArgs.Message,
            eventArgs.Level == AgentLogLevel.Error,
            "Agent Runtime"));

    private void OnRuntimeNotificationReceived(object? sender, AgentNotificationEventArgs eventArgs) =>
        Dispatch(() =>
        {
            var activity = _activityParser.Parse(
                eventArgs,
                DateTimeOffset.Now,
                _runtimeService.ThreadId,
                _runtimeService.ActiveTurnId);
            if (activity is not null)
            {
                AgentActivityReceived?.Invoke(
                    this,
                    new AgentActivityEventArgs(
                        activity.Timestamp,
                        activity.Category,
                        Sanitize(activity.Message),
                        activity.IsWarning,
                        activity.ThreadId,
                        activity.TurnId,
                        activity.ItemId,
                        activity.ItemType,
                        activity.TaskId,
                        activity.EventName,
                        activity.IsTerminal));
            }

            if (eventArgs.Method.Contains("login", StringComparison.OrdinalIgnoreCase))
            {
                AddLog(DateTimeOffset.Now, $"Codex 登录事件：{eventArgs.Method}", false, "Codex");
            }
        });

    private void OnRuntimeTurnCompleted(object? sender, AgentTurnCompletedEventArgs eventArgs) =>
        Dispatch(() =>
        {
            _lastTurnStatus = eventArgs.Result.IsSuccess
                ? "最近 Turn 已完成"
                : $"最近 Turn：{eventArgs.Result.Status}";
            CaptureRuntimeSession();
            RefreshState();
        });

    private void OnRuntimeMcpStatusChanged(object? sender, AgentMcpStatusChangedEventArgs eventArgs) =>
        Dispatch(() =>
        {
            _lastMcpToolStatus = ProductIdentity.IsKnownMcpServerName(eventArgs.ServerName)
                ? $"{ProductIdentity.McpServerName}：{eventArgs.Status}"
                : $"{eventArgs.ServerName}：{eventArgs.Status}";
            if (!string.IsNullOrWhiteSpace(eventArgs.Error))
            {
                _lastMcpToolStatus += $" · {Sanitize(eventArgs.Error)}";
            }

            CaptureRuntimeSession();
            RefreshState();
        });

    private void CaptureRuntimeSession()
    {
        if (!IsActiveWorkingDirectoryCurrent() || string.IsNullOrWhiteSpace(_runtimeService.ThreadId))
        {
            return;
        }

        _resumeThreadId = _runtimeService.ThreadId;
        PublishSession(_resumeThreadId, WorkingDirectory);
    }

    private void PublishSession(string? threadId, string workingDirectory)
    {
        if (string.Equals(_lastPublishedThreadId, threadId, StringComparison.Ordinal) &&
            PathEquals(_lastPublishedWorkingDirectory, workingDirectory))
        {
            return;
        }

        _lastPublishedThreadId = threadId;
        _lastPublishedWorkingDirectory = workingDirectory;
        SessionChanged?.Invoke(this, new AgentSessionChangedEventArgs(threadId, workingDirectory));
    }

    private bool IsSessionReady()
    {
        if (!SelectedAgent.IsRuntimeAvailable ||
            !IsActiveWorkingDirectoryCurrent() ||
            !_serverService.IsProcessRunning ||
            _serverService.State is not McpServerProcessState.WaitingForAgent and
                not McpServerProcessState.Connected ||
            _serverService.Endpoint?.Port != Port ||
            !_runtimeService.IsConnected)
        {
            return false;
        }

        return _runtimeService.TransportMode switch
        {
            CodexAgentTransportMode.AppServer =>
                (_runtimeService.AuthenticationState is
                    CodexAgentAuthenticationState.Authenticated or
                    CodexAgentAuthenticationState.NotRequired) &&
                _runtimeService.McpReady,
            CodexAgentTransportMode.ExecFallback => true,
            _ => false
        };
    }

    private bool IsActiveWorkingDirectoryCurrent() =>
        !string.IsNullOrWhiteSpace(_activeWorkingDirectory) &&
        PathEquals(_activeWorkingDirectory, WorkingDirectory);

    private void RefreshState()
    {
        var (status, text) = BuildOverallStatus();
        StatusKind = status;
        StatusText = text;

        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsTurnRunning));
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanLogin));
        OnPropertyChanged(nameof(CanRefreshAuthentication));
        OnPropertyChanged(nameof(CanInterruptTurn));
        OnPropertyChanged(nameof(CodexVersionText));
        OnPropertyChanged(nameof(AuthenticationText));
        OnPropertyChanged(nameof(RuntimeModeText));
        OnPropertyChanged(nameof(AppServerStatusText));
        OnPropertyChanged(nameof(McpServerStatusText));
        OnPropertyChanged(nameof(McpToolStatusText));
        OnPropertyChanged(nameof(ThreadIdText));
        OnPropertyChanged(nameof(TurnStatusText));
        OnPropertyChanged(nameof(CodexConnectionPrompt));
        OnPropertyChanged(nameof(CanCopyCodexConnectionPrompt));
        RaiseCommandStates();
    }

    private (AgentConnectionStatus Status, string Text) BuildOverallStatus()
    {
        if (_isStopping)
        {
            return (AgentConnectionStatus.Stopping, "正在停止 Agent Runtime 与 MCP Server");
        }

        if (_isStarting)
        {
            return IsMcpHealthReady(_serverService.State)
                ? (AgentConnectionStatus.WaitingForAgent, "MCP Server 健康检查通过，正在建立 Agent 会话")
                : (AgentConnectionStatus.Starting, "正在启动 MCP Server 并执行健康检查");
        }

        if (!string.IsNullOrWhiteSpace(_faultMessage))
        {
            return (AgentConnectionStatus.Faulted, _faultMessage);
        }

        if (!SelectedAgent.IsRuntimeAvailable && !IsRunning)
        {
            return (AgentConnectionStatus.Stopped, "OpenCode Adapter 待接入，当前不可启动");
        }

        if (IsRunning && !IsActiveWorkingDirectoryCurrent() &&
            !string.IsNullOrWhiteSpace(_activeWorkingDirectory))
        {
            return (AgentConnectionStatus.WaitingForAgent, "项目工作目录已改变，请停止旧会话后重新启动");
        }

        if (IsConnected)
        {
            return _runtimeService.TransportMode == CodexAgentTransportMode.ExecFallback
                ? (AgentConnectionStatus.Connected, "Agent 可执行（codex exec 兼容降级）")
                : (AgentConnectionStatus.Connected, "Codex App Server 会话已就绪");
        }

        if (_runtimeService.AuthenticationState == CodexAgentAuthenticationState.Required)
        {
            return (AgentConnectionStatus.WaitingForAgent, "Codex App Server 已启动，等待账号登录");
        }

        if (_runtimeService.State == AgentSessionTransportState.Faulted ||
            _serverService.State == McpServerProcessState.Faulted)
        {
            return (
                AgentConnectionStatus.Faulted,
                _runtimeService.LastError ?? _lastRuntimeDetail ?? "Agent 接入链路运行异常");
        }

        if (_serverService.IsProcessRunning ||
            _runtimeService.State is not AgentSessionTransportState.Stopped)
        {
            return (AgentConnectionStatus.WaitingForAgent, "接入链路运行中，等待 Agent 与 MCP 工具就绪");
        }

        return (AgentConnectionStatus.Stopped, "Agent 会话未启动");
    }

    private void AddLog(
        DateTimeOffset timestamp,
        string message,
        bool isError,
        string source = "Agent")
    {
        if (_disposed)
        {
            return;
        }

        var sanitized = Sanitize(message);
        var level = isError ? "ERR" : "INF";
        Logs.Add($"[{timestamp:HH:mm:ss}] [{level}] {sanitized}");
        while (Logs.Count > MaximumLogEntries)
        {
            Logs.RemoveAt(0);
        }

        (ClearLogCommand as RelayCommand)?.RaiseCanExecuteChanged();

        try
        {
            _workbenchLogSink?.Write(
                isError ? WorkbenchLogLevel.Error : WorkbenchLogLevel.Information,
                source,
                sanitized,
                timestamp);
        }
        catch
        {
            // 全局日志总线不可用时仍保留 Agent 面板自己的日志。
        }
    }

    private void ClearLogs()
    {
        Logs.Clear();
        (ClearLogCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private string Sanitize(string message)
    {
        var token = _serverService.TokenEnvironmentVariableValue;
        return string.IsNullOrEmpty(token)
            ? message
            : message.Replace(token, "[REDACTED]", StringComparison.Ordinal);
    }

    private static string BuildAuthenticatedAccountText(AgentAccountState? account)
    {
        if (account is null)
        {
            return "已认证";
        }

        var parts = new List<string> { "已认证" };
        if (!string.IsNullOrWhiteSpace(account.AccountType))
        {
            parts.Add(account.AccountType);
        }

        if (!string.IsNullOrWhiteSpace(account.PlanType))
        {
            parts.Add(account.PlanType);
        }

        return string.Join(" · ", parts);
    }

    private static string BuildLastHandshakeText(McpServerStateChangedEventArgs state)
    {
        if (state.LastHandshakeUtc is null)
        {
            return "尚未收到 Agent 握手";
        }

        var clientParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(state.LastClientName))
        {
            clientParts.Add(state.LastClientName);
        }

        if (!string.IsNullOrWhiteSpace(state.LastClientVersion))
        {
            clientParts.Add(state.LastClientVersion);
        }

        var client = clientParts.Count > 0 ? $" · {string.Join(" ", clientParts)}" : string.Empty;
        return $"最近握手：{state.LastHandshakeUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)}{client}";
    }

    private void NotifySettingsChanged() =>
        SettingsChanged?.Invoke(
            this,
            new AgentConnectionSettingsChangedEventArgs(SelectedAgent.Key, Port));

    private void RaiseCommandStates()
    {
        _startCommand.RaiseCanExecuteChanged();
        _stopCommand.RaiseCanExecuteChanged();
        _refreshAuthenticationCommand.RaiseCanExecuteChanged();
        _loginCommand.RaiseCanExecuteChanged();
        _interruptTurnCommand.RaiseCanExecuteChanged();
        _copyCodexConnectionPromptCommand.RaiseCanExecuteChanged();
    }

    private void Dispatch(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action, DispatcherPriority.DataBind);
        }
    }

    private static bool IsMcpHealthReady(McpServerProcessState state) =>
        state is McpServerProcessState.WaitingForAgent or McpServerProcessState.Connected;

    private static string NormalizeWorkingDirectory(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var normalized = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException($"Agent 工作目录不存在：{normalized}");
        }

        return normalized;
    }

    private static string? NormalizeThreadId(string? threadId) =>
        string.IsNullOrWhiteSpace(threadId) ? null : threadId.Trim();

    private static bool PathEquals(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsValidPort(int port) => port is >= 1024 and <= 65535;
}

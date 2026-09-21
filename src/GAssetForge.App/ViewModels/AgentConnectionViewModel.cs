using GAssetForge.App.Infrastructure;
using GAssetForge.App.Services;
using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.Core;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
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
    public AgentConnectionSettingsChangedEventArgs(
        string agentKey,
        int port,
        string? codexExecutablePath = null)
    {
        AgentKey = agentKey;
        Port = port;
        CodexExecutablePath = codexExecutablePath;
    }

    public string AgentKey { get; }

    public int Port { get; }

    public string? CodexExecutablePath { get; }
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

public sealed class AgentConnectionViewModel : ObservableObject, IDisposable, IAsyncDisposable
{
    public const int DefaultPort = 43120;
    private const int MaximumLogEntries = 500;
    private static readonly TimeSpan McpHealthReadyTimeout = TimeSpan.FromSeconds(15);

    // Kept for compatibility with the delivery smoke tests and the asset pipeline.
    private readonly McpServerProcessService _serverService;
    private readonly CodexAgentRuntimeService _runtimeService;
    private readonly ICodexExecutablePicker _codexExecutablePicker;
    private readonly IWorkbenchLogSink? _workbenchLogSink;
    private readonly IWorkbenchLogRedactionRegistry? _workbenchRedactionRegistry;
    private readonly AgentActivityParser _activityParser = new();
    private readonly Dispatcher _dispatcher;
    private readonly TimeSpan _mcpHealthReadyTimeout;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly object _disposeLock = new();
    private readonly AsyncRelayCommand _startCommand;
    private readonly AsyncRelayCommand _stopCommand;
    private readonly AsyncRelayCommand _startMcpServerCommand;
    private readonly AsyncRelayCommand _stopMcpServerCommand;
    private readonly AsyncRelayCommand _refreshCodexRuntimeDiagnosticsCommand;
    private readonly AsyncRelayCommand _refreshAuthenticationCommand;
    private readonly AsyncRelayCommand _loginCommand;
    private readonly AsyncRelayCommand _interruptTurnCommand;
    private readonly RelayCommand _copyCodexConnectionPromptCommand;
    private readonly RelayCommand _browseCodexExecutableCommand;
    private readonly RelayCommand _useAutomaticCodexExecutableCommand;
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
    private string? _codexExecutablePath;
    private string _codexRuntimeDiagnosticsText = "尚未检测 Codex CLI。点击“重新检测”可查看全部候选路径与结果。";
    private long _codexExecutableRevision;
    private CancellationTokenSource? _startCancellation;
    private bool _isStarting;
    private bool _isStopping;
    private bool _isStartingMcpServer;
    private bool _isStoppingMcpServer;
    private bool _isRefreshingCodexRuntimeDiagnostics;
    private bool _disposed;
    private Task? _disposeTask;

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
        IWorkbenchLogSink? workbenchLogSink = null,
        string? codexExecutablePath = null,
        ICodexExecutablePicker? codexExecutablePicker = null)
        : this(
            serverService,
            runtimeService,
            selectedAgentKey,
            port,
            workingDirectory,
            resumeThreadId,
            workbenchLogSink,
            codexExecutablePath,
            codexExecutablePicker,
            McpHealthReadyTimeout)
    {
    }

    internal AgentConnectionViewModel(
        McpServerProcessService serverService,
        CodexAgentRuntimeService runtimeService,
        string? selectedAgentKey,
        int port,
        string? workingDirectory,
        string? resumeThreadId,
        IWorkbenchLogSink? workbenchLogSink,
        string? codexExecutablePath,
        ICodexExecutablePicker? codexExecutablePicker,
        TimeSpan mcpHealthReadyTimeout)
    {
        if (mcpHealthReadyTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mcpHealthReadyTimeout),
                "MCP 健康检查超时时间必须大于零。");
        }

        _serverService = serverService ?? throw new ArgumentNullException(nameof(serverService));
        _runtimeService = runtimeService ?? throw new ArgumentNullException(nameof(runtimeService));
        _codexExecutablePicker = codexExecutablePicker ?? new DesktopCodexExecutablePicker();
        _workbenchLogSink = workbenchLogSink;
        _workbenchRedactionRegistry = workbenchLogSink as IWorkbenchLogRedactionRegistry;
        _mcpHealthReadyTimeout = mcpHealthReadyTimeout;
        _workbenchRedactionRegistry?.RegisterSensitiveValue(
            _serverService.TokenEnvironmentVariableValue);
        _dispatcher = Dispatcher.CurrentDispatcher;
        _selectedAgent = AgentProfile.Find(selectedAgentKey);
        _port = IsValidPort(port) ? port : DefaultPort;
        _workingDirectory = NormalizeWorkingDirectory(workingDirectory ?? Environment.CurrentDirectory);
        _resumeThreadId = NormalizeThreadId(resumeThreadId);
        _codexExecutablePath = NormalizeConfiguredExecutablePath(codexExecutablePath);
        _runtimeService.ConfigureExecutablePath(EffectiveCodexExecutablePath);
        _lastPublishedThreadId = _resumeThreadId;
        _lastPublishedWorkingDirectory = _workingDirectory;

        _startCommand = new AsyncRelayCommand(StartSessionAsync, () => CanStart);
        _stopCommand = new AsyncRelayCommand(StopSessionAsync, () => CanStop);
        _startMcpServerCommand = new AsyncRelayCommand(StartMcpServerAsync, () => CanStartMcpServer);
        _stopMcpServerCommand = new AsyncRelayCommand(StopMcpServerAsync, () => CanStopMcpServer);
        _refreshCodexRuntimeDiagnosticsCommand = new AsyncRelayCommand(
            RefreshCodexRuntimeDiagnosticsAsync,
            () => CanEditRuntimeSettings);
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
        _browseCodexExecutableCommand = new RelayCommand(
            BrowseCodexExecutable,
            () => CanEditRuntimeSettings);
        _useAutomaticCodexExecutableCommand = new RelayCommand(
            UseAutomaticCodexExecutable,
            () => CanEditRuntimeSettings && !string.IsNullOrWhiteSpace(CodexExecutablePath));
        StartMcpServerCommand = _startMcpServerCommand;
        StopMcpServerCommand = _stopMcpServerCommand;
        RefreshCodexRuntimeDiagnosticsCommand = _refreshCodexRuntimeDiagnosticsCommand;
        BrowseCodexExecutableCommand = _browseCodexExecutableCommand;
        UseAutomaticCodexExecutableCommand = _useAutomaticCodexExecutableCommand;

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
        IsAgentSessionActive ||
        IsMcpServerActive ||
        _runtimeService.State == AgentSessionTransportState.Faulted;

    public bool IsAgentSessionActive =>
        _isStarting ||
        _isStopping ||
        _runtimeService.State is
            AgentSessionTransportState.Starting or
            AgentSessionTransportState.Ready or
            AgentSessionTransportState.RunningTurn or
            AgentSessionTransportState.Stopping;

    public bool IsMcpServerActive =>
        _isStartingMcpServer ||
        _isStoppingMcpServer ||
        _serverService.IsProcessRunning;

    public bool IsMcpServerStandalone => IsMcpServerActive && !IsAgentSessionActive;

    // Connected means a generation task can be executed, rather than merely that HTTP MCP shook hands.
    public bool IsConnected => IsSessionReady();

    public bool IsTurnRunning => _runtimeService.IsTurnRunning;

    public bool CanEditSettings =>
        !_disposed &&
        !IsAgentSessionActive &&
        !IsMcpServerActive;

    public bool CanEditRuntimeSettings =>
        !_disposed &&
        !_isStarting &&
        !_isStopping &&
        !_isRefreshingCodexRuntimeDiagnostics &&
        _runtimeService.State is AgentSessionTransportState.Stopped or AgentSessionTransportState.Faulted;

    public bool CanStart =>
        !_disposed &&
        !_isStarting &&
        !_isStopping &&
        !_isStartingMcpServer &&
        !_isStoppingMcpServer &&
        !_isRefreshingCodexRuntimeDiagnostics &&
        !IsAgentSessionActive &&
        IsPortValid &&
        SelectedAgent.IsRuntimeAvailable &&
        Directory.Exists(WorkingDirectory) &&
        CanReuseCurrentMcpServer();

    public bool CanStop => !_disposed && IsAgentSessionActive;

    public bool CanStartMcpServer =>
        !_disposed &&
        !_isStarting &&
        !_isStopping &&
        !_isStartingMcpServer &&
        !_isStoppingMcpServer &&
        !IsAgentSessionActive &&
        !_serverService.IsProcessRunning &&
        IsPortValid;

    public bool CanStopMcpServer =>
        !_disposed &&
        !_isStarting &&
        !_isStopping &&
        !_isStartingMcpServer &&
        !_isStoppingMcpServer &&
        !IsAgentSessionActive &&
        _serverService.IsProcessRunning;

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

    public string? CodexExecutablePath
    {
        get => _codexExecutablePath;
        set => ConfigureCodexExecutablePath(value);
    }

    public string CodexExecutablePathDisplayText => string.IsNullOrWhiteSpace(CodexExecutablePath)
        ? "自动检测（%APPDATA%\\npm、%LOCALAPPDATA% 官方安装、PATH 与应用目录）"
        : CodexExecutablePath;

    public string CodexRuntimeDiagnosticsText
    {
        get => _codexRuntimeDiagnosticsText;
        private set => SetProperty(ref _codexRuntimeDiagnosticsText, value);
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

    public ICommand StartMcpServerCommand { get; }

    public ICommand StopMcpServerCommand { get; }

    public ICommand BrowseCodexExecutableCommand { get; }

    public ICommand UseAutomaticCodexExecutableCommand { get; }

    public ICommand RefreshCodexRuntimeDiagnosticsCommand { get; }

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

    private string EffectiveCodexExecutablePath =>
        string.IsNullOrWhiteSpace(CodexExecutablePath) ? "codex" : CodexExecutablePath;

    private void BrowseCodexExecutable()
    {
        if (!CanEditRuntimeSettings)
        {
            return;
        }

        var selectedPath = _codexExecutablePicker.SelectExecutable(CodexExecutablePath);
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            ConfigureCodexExecutablePath(selectedPath);
        }
    }

    private void UseAutomaticCodexExecutable()
    {
        if (CanEditRuntimeSettings)
        {
            ConfigureCodexExecutablePath(null);
        }
    }

    private void ConfigureCodexExecutablePath(string? value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanEditRuntimeSettings)
        {
            throw new InvalidOperationException("Codex Runtime 运行期间不能修改 CLI 路径。请先停止 Agent 会话。");
        }

        var normalized = NormalizeConfiguredExecutablePath(value);
        if (string.Equals(_codexExecutablePath, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _runtimeService.ConfigureExecutablePath(
            string.IsNullOrWhiteSpace(normalized) ? "codex" : normalized);
        _codexExecutablePath = normalized;
        _codexExecutableRevision++;
        _faultMessage = null;
        CodexRuntimeDiagnosticsText = "CLI 路径已更新。点击“重新检测”验证候选是否可启动。";
        OnPropertyChanged(nameof(CodexExecutablePath));
        OnPropertyChanged(nameof(CodexExecutablePathDisplayText));
        NotifySettingsChanged();
        RefreshState();
    }

    private async Task RefreshCodexRuntimeDiagnosticsAsync()
    {
        if (!CanEditRuntimeSettings)
        {
            return;
        }

        var revision = _codexExecutableRevision;
        _isRefreshingCodexRuntimeDiagnostics = true;
        CodexRuntimeDiagnosticsText = "正在检测 Codex CLI 候选路径…";
        RefreshState();
        try
        {
            var requestedExecutable = EffectiveCodexExecutablePath;
            var resolution = await Task.Run(() =>
            {
                CodexExecutableResolver.ClearLaunchProbeCache();
                return CodexExecutableResolver.ResolveWithDiagnostics(
                    requestedExecutable,
                    pathEnvironment: ProcessEnvironmentSnapshot.BuildFreshPathEnvironment());
            }).ConfigureAwait(true);
            if (revision != _codexExecutableRevision)
            {
                return;
            }

            CodexRuntimeDiagnosticsText = FormatCodexRuntimeDiagnostics(resolution);
            if (resolution.IsSuccess)
            {
                _faultMessage = null;
                AddLog(
                    DateTimeOffset.Now,
                    $"Codex CLI 检测通过：{resolution.Summary}",
                    false);
            }
            else
            {
                AddLog(DateTimeOffset.Now, $"Codex CLI 检测失败：{resolution.Summary}", true);
            }
        }
        catch (Exception exception)
        {
            if (revision == _codexExecutableRevision)
            {
                CodexRuntimeDiagnosticsText = $"Codex CLI 检测异常：{Sanitize(exception.Message)}";
                AddLog(DateTimeOffset.Now, CodexRuntimeDiagnosticsText, true);
            }
        }
        finally
        {
            _isRefreshingCodexRuntimeDiagnostics = false;
            RefreshState();
        }
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
        var failureStage = "turn.prepare";
        try
        {
            await EnsureExecutionReadinessAsync("turn.prepare", cancellationToken).ConfigureAwait(false);

            Dispatch(() =>
            {
                _lastTurnStatus = "正在刷新 Agent 执行环境";
                RefreshState();
            });
            var resumeThreadId = _runtimeService.ThreadId ?? _resumeThreadId;
            await _runtimeService.EnsureSessionAsync(
                    WorkingDirectory,
                    resumeThreadId,
                    _serverService,
                    cancellationToken)
                .ConfigureAwait(false);
            await EnsureExecutionReadinessAsync("turn.prepare", cancellationToken).ConfigureAwait(false);

            Dispatch(() =>
            {
                _activeWorkingDirectory = WorkingDirectory;
                _lastTurnStatus = "正在提交 Turn";
                CaptureRuntimeSession();
                RefreshState();
            });
            failureStage = "turn.request";
            var result = await _runtimeService.RunTurnAsync(prompt, cancellationToken).ConfigureAwait(false);
            Dispatch(() =>
            {
                _lastTurnStatus = result.IsSuccess
                    ? "最近 Turn 已完成"
                    : $"最近 Turn：{result.Status} · {Sanitize(result.ErrorMessage ?? "未提供错误信息")}";
                CaptureRuntimeSession();
                RefreshState();
            });
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogExecutionFailure(exception, "Agent 请求失败", failureStage, "agent_turn_request_failed");
            throw;
        }
        finally
        {
            _turnGate.Release();
        }
    }

    public async Task ReconnectSessionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        var turnGateEntered = false;
        var reconnectStarted = false;
        try
        {
            if (!_turnGate.Wait(0))
            {
                throw new InvalidOperationException(
                    "当前 Agent Turn 或批次线程准备仍在执行，不能同时重新连接会话。");
            }

            turnGateEntered = true;
            if (_runtimeService.IsTurnRunning)
            {
                throw new InvalidOperationException(
                    "当前 Agent Turn 正在执行，请等待 Turn 结束或先中断后再重新连接会话。");
            }

            if (!SelectedAgent.IsRuntimeAvailable)
            {
                throw new NotSupportedException(
                    $"{SelectedAgent.DisplayName} Runtime 尚未适配，不能重新连接会话。");
            }

            if (!IsPortValid)
            {
                throw new InvalidOperationException("MCP Server 端口无效，不能重新连接 Agent 会话。");
            }

            if (!Directory.Exists(WorkingDirectory))
            {
                throw new DirectoryNotFoundException($"Agent 工作目录不存在：{WorkingDirectory}");
            }

            var resumeThreadId = NormalizeThreadId(_runtimeService.ThreadId) ?? _resumeThreadId;
            _resumeThreadId = resumeThreadId;
            _faultMessage = null;
            _isStarting = true;
            reconnectStarted = true;
            _lastTurnStatus = "正在重新连接 Agent 会话";
            RefreshState();
            AddLog(
                DateTimeOffset.Now,
                "正在强制替换 Codex Runtime；健康的 MCP Server 将保持运行。",
                false);

            try
            {
                await _runtimeService.StopAsync(cancellationToken).ConfigureAwait(true);
                _activeWorkingDirectory = null;
                await EnsureReconnectMcpServerHealthyAsync(cancellationToken).ConfigureAwait(true);
                await _runtimeService.EnsureSessionAsync(
                        WorkingDirectory,
                        resumeThreadId,
                        _serverService,
                        cancellationToken)
                    .ConfigureAwait(true);

                _activeWorkingDirectory = WorkingDirectory;
                _resumeThreadId = NormalizeThreadId(_runtimeService.ThreadId) ?? resumeThreadId;
                CaptureRuntimeSession();
                if (!IsConnected)
                {
                    throw new InvalidOperationException(
                        "Agent Runtime 已重新启动，但认证或 MCP 工具尚未达到可执行状态。" +
                        $" 当前状态：{BuildOverallStatus().Text}");
                }

                _faultMessage = null;
                _lastTurnStatus = "Agent 会话已重新连接，可继续原持久化任务";
                AddLog(
                    DateTimeOffset.Now,
                    "Codex Runtime 已重新连接；工作目录、线程书签与健康 MCP Server 均已保留。",
                    false);
            }
            catch (OperationCanceledException)
            {
                _activeWorkingDirectory = null;
                AddLog(DateTimeOffset.Now, "Agent 会话重新连接已取消。", false);
                throw;
            }
            catch (Exception exception)
            {
                var detail = Sanitize(exception.Message);
                var mcpStateDetail = _serverService.IsProcessRunning &&
                                     IsMcpHealthReady(_serverService.State)
                    ? "MCP Server 已保持健康运行。"
                    : "MCP Server 当前也未达到健康状态，请同时检查端口与服务日志。";
                _faultMessage =
                    $"Agent 会话重新连接失败；{mcpStateDetail}{Environment.NewLine}{detail}";
                CodexRuntimeDiagnosticsText = detail;
                AddLog(DateTimeOffset.Now, _faultMessage, true);
                try
                {
                    await _runtimeService.StopAsync(CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception cleanupException)
                {
                    AddLog(
                        DateTimeOffset.Now,
                        $"清理失败的 Agent Runtime 时发生错误：{Sanitize(cleanupException.Message)}",
                        true);
                }

                _activeWorkingDirectory = null;
                throw new InvalidOperationException(_faultMessage, exception);
            }
        }
        finally
        {
            if (reconnectStarted)
            {
                _isStarting = false;
                RefreshState();
            }

            if (turnGateEntered)
            {
                _turnGate.Release();
            }

            _lifecycleGate.Release();
        }
    }

    public async Task<AgentThreadResult> StartFreshGenerationThreadAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureExecutionReadinessAsync("thread.prepare", cancellationToken).ConfigureAwait(false);

            await _runtimeService.EnsureSessionAsync(
                    WorkingDirectory,
                    _runtimeService.ThreadId ?? _resumeThreadId,
                    _serverService,
                    cancellationToken)
                .ConfigureAwait(false);
            await EnsureExecutionReadinessAsync("thread.prepare", cancellationToken).ConfigureAwait(false);

            Dispatch(() =>
            {
                _lastTurnStatus = "正在为生成批次创建独立线程";
                RefreshState();
            });
            var result = await _runtimeService.StartFreshThreadAsync(cancellationToken)
                .ConfigureAwait(false);
            var freshThreadId = NormalizeThreadId(result.ThreadId);
            _resumeThreadId = freshThreadId;
            Dispatch(() =>
            {
                _activeWorkingDirectory = WorkingDirectory;
                PublishSession(freshThreadId, WorkingDirectory);

                _lastTurnStatus = result.IsDeferred
                    ? "生成批次已切换到新的延迟创建线程"
                    : $"生成批次已切换到独立线程 {result.ThreadId}";
                RefreshState();
            });
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogExecutionFailure(exception, "生成批次线程准备失败", "thread.prepare", "agent_thread_prepare_failed");
            throw;
        }
        finally
        {
            _turnGate.Release();
        }
    }

    public void Dispose()
    {
        var disposeTask = DisposeAsync().AsTask();
        if (_dispatcher.CheckAccess() &&
            !disposeTask.IsCompleted &&
            !_dispatcher.HasShutdownStarted &&
            !_dispatcher.HasShutdownFinished)
        {
            var frame = new DispatcherFrame();
            _ = disposeTask.ContinueWith(
                completedTask =>
                {
                    if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
                    {
                        frame.Continue = false;
                        return;
                    }

                    _ = _dispatcher.BeginInvoke(
                        DispatcherPriority.Send,
                        new Action(() => frame.Continue = false));
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }

        disposeTask.GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _startCancellation?.Cancel();
        try
        {
            await StopSessionAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Shutdown continues so both child processes and handles are released.
        }

        _serverService.StateChanged -= OnServerStateChanged;
        _serverService.LogReceived -= OnServerLogReceived;
        _runtimeService.StateChanged -= OnRuntimeStateChanged;
        _runtimeService.LogReceived -= OnRuntimeLogReceived;
        _runtimeService.NotificationReceived -= OnRuntimeNotificationReceived;
        _runtimeService.TurnCompleted -= OnRuntimeTurnCompleted;
        _runtimeService.McpStatusChanged -= OnRuntimeMcpStatusChanged;
        await _runtimeService.DisposeAsync().ConfigureAwait(false);
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

            var mcpHealthy = false;
            try
            {
                await EnsureMcpServerHealthyAsync(_startCancellation.Token).ConfigureAwait(true);
                mcpHealthy = true;
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
                await CleanupAfterFailedRuntimeStartAsync(mcpHealthy).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                var detail = Sanitize(exception.Message);
                _faultMessage = mcpHealthy
                    ? $"Codex Runtime 启动失败；MCP Server 已保持运行，可修正 CLI 路径后重试。{Environment.NewLine}{detail}"
                    : detail;
                CodexRuntimeDiagnosticsText = detail;
                AddLog(DateTimeOffset.Now, $"Agent 会话启动失败：{_faultMessage}", true);
                await CleanupAfterFailedRuntimeStartAsync(mcpHealthy).ConfigureAwait(true);
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

    private Task StartMcpServerAsync() => StartMcpServerAsync(CancellationToken.None);

    private async Task StartMcpServerAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (!CanStartMcpServer)
            {
                return;
            }

            _isStartingMcpServer = true;
            _faultMessage = null;
            RefreshState();
            AddLog(DateTimeOffset.Now, "正在独立启动 MCP Server。", false);
            try
            {
                await _serverService.StartAsync(Port, SelectedAgent.Key, cancellationToken).ConfigureAwait(true);
                await WaitForMcpHealthReadyAsync(
                        _mcpHealthReadyTimeout,
                        cancellationToken)
                    .ConfigureAwait(true);
                AddLog(DateTimeOffset.Now, "MCP Server 已就绪，可继续启动 Agent Runtime 或独立诊断。", false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _faultMessage = Sanitize(exception.Message);
                AddLog(DateTimeOffset.Now, $"MCP Server 启动失败：{_faultMessage}", true);
                try
                {
                    await _serverService.StopAsync(CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception cleanupException)
                {
                    AddLog(
                        DateTimeOffset.Now,
                        $"清理 MCP Server 失败：{Sanitize(cleanupException.Message)}",
                        true);
                }
            }
        }
        finally
        {
            _isStartingMcpServer = false;
            RefreshState();
            _lifecycleGate.Release();
        }
    }

    private Task StopMcpServerAsync() => StopMcpServerAsync(CancellationToken.None);

    private async Task StopMcpServerAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (!CanStopMcpServer)
            {
                return;
            }

            _isStoppingMcpServer = true;
            _faultMessage = null;
            RefreshState();
            try
            {
                await _serverService.StopAsync(cancellationToken).ConfigureAwait(true);
                _lastMcpToolStatus = "尚未初始化";
                AddLog(DateTimeOffset.Now, "MCP Server 已独立停止。", false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _faultMessage = Sanitize(exception.Message);
                AddLog(DateTimeOffset.Now, $"停止 MCP Server 失败：{_faultMessage}", true);
            }
        }
        finally
        {
            _isStoppingMcpServer = false;
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

    private async Task CleanupAfterFailedRuntimeStartAsync(bool keepHealthyMcpServer)
    {
        try
        {
            await _runtimeService.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AddLog(DateTimeOffset.Now, $"清理 Agent Runtime 失败：{Sanitize(exception.Message)}", true);
        }

        if (!keepHealthyMcpServer)
        {
            try
            {
                await _serverService.StopAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                AddLog(DateTimeOffset.Now, $"清理 MCP Server 失败：{Sanitize(exception.Message)}", true);
            }
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

    private async Task EnsureMcpServerHealthyAsync(
        CancellationToken cancellationToken,
        TimeSpan? healthReadyTimeout = null)
    {
        var effectiveHealthReadyTimeout = healthReadyTimeout ?? _mcpHealthReadyTimeout;
        if (_serverService.IsProcessRunning)
        {
            if (_serverService.Endpoint?.Port != Port)
            {
                throw new InvalidOperationException(
                    $"MCP Server 当前运行在端口 {_serverService.Endpoint?.Port}，与设置端口 {Port} 不一致。" +
                    "请先停止 MCP Server 后再修改端口。");
            }

            await WaitForMcpHealthReadyAsync(
                    effectiveHealthReadyTimeout,
                    cancellationToken)
                .ConfigureAwait(true);
            AddLog(DateTimeOffset.Now, "复用已就绪的 MCP Server，继续启动 Codex Runtime。", false);
            return;
        }

        await _serverService.StartAsync(
            Port,
            SelectedAgent.Key,
            cancellationToken).ConfigureAwait(true);
        await WaitForMcpHealthReadyAsync(
                effectiveHealthReadyTimeout,
                cancellationToken)
            .ConfigureAwait(true);
    }

    private async Task EnsureReconnectMcpServerHealthyAsync(CancellationToken cancellationToken)
    {
        if (_serverService.IsProcessRunning)
        {
            var endpointUnavailable = _serverService.Endpoint is null;
            var endpointPortMismatch = _serverService.Endpoint is { } endpoint &&
                                       endpoint.Port != Port;
            var stateRequiresRestart = _serverService.State is
                McpServerProcessState.Faulted or
                McpServerProcessState.Stopping or
                McpServerProcessState.Stopped;
            if (endpointUnavailable || endpointPortMismatch || stateRequiresRestart)
            {
                var reason = endpointUnavailable
                    ? "MCP Server 进程存在但 Endpoint 尚不可用"
                    : endpointPortMismatch
                    ? $"MCP Server 当前端口 {_serverService.Endpoint?.Port} 与设置端口 {Port} 不一致"
                    : $"MCP Server 当前状态为 {_serverService.State}";
                AddLog(
                    DateTimeOffset.Now,
                    $"{reason}，重新连接将先回收旧服务再执行健康启动。",
                    false);
                await _serverService.StopAsync(cancellationToken).ConfigureAwait(true);
                _lastMcpToolStatus = "尚未初始化";
            }
        }

        try
        {
            await EnsureMcpServerHealthyAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (TimeoutException exception) when (_serverService.IsProcessRunning)
        {
            AddLog(
                DateTimeOffset.Now,
                $"{exception.Message} MCP Server 进程仍在运行且状态为 {_serverService.State}，" +
                "重新连接将回收旧服务并仅重启一次。",
                false);
            await _serverService.StopAsync(cancellationToken).ConfigureAwait(true);
            _lastMcpToolStatus = "尚未初始化";

            // Deliberately do not loop: a second startup/health failure must remain visible
            // instead of turning one broken endpoint into an unbounded restart cycle.
            await EnsureMcpServerHealthyAsync(
                    cancellationToken,
                    McpHealthReadyTimeout)
                .ConfigureAwait(true);
        }
    }

    private async Task WaitForMcpHealthReadyAsync(
        TimeSpan healthReadyTimeout,
        CancellationToken cancellationToken)
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
            timeout.CancelAfter(healthReadyTimeout);
            try
            {
                await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"等待 MCP Server 健康检查超时（{healthReadyTimeout.TotalSeconds:0.###} 秒）。");
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

    private void OnRuntimeNotificationReceived(object? sender, AgentNotificationEventArgs eventArgs)
    {
        var occurredAt = DateTimeOffset.Now;
        var threadId = _runtimeService.ThreadId;
        var turnId = _runtimeService.ActiveTurnId;
        Dispatch(() =>
        {
            var activity = _activityParser.Parse(
                eventArgs,
                occurredAt,
                threadId,
                turnId);
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
                AddLog(occurredAt, $"Codex 登录事件：{eventArgs.Method}", false, "Codex");
            }
        });
    }

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

    internal string? ActiveThreadId => _runtimeService.ThreadId;

    internal string? ActiveTurnId => _runtimeService.ActiveTurnId;

    private async Task EnsureExecutionReadinessAsync(string stage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateExecutionPrerequisites(stage);
        if (_runtimeService.TransportMode == CodexAgentTransportMode.AppServer && !_runtimeService.McpReady)
        {
            var occurredAt = DateTimeOffset.Now;
            Dispatch(() =>
            {
                _lastTurnStatus = "正在等待当前线程的 GAssetForge MCP 初始化；尚未提交 Turn";
                AddLog(occurredAt, _lastTurnStatus, false);
                RefreshState();
            });
        }

        await _runtimeService.WaitForExecutionReadinessAsync(cancellationToken).ConfigureAwait(false);
        // Waiting cannot authorize a stopped runtime, changed project, or expired authentication.
        ValidateExecutionPrerequisites(stage);
        if (_runtimeService.TransportMode == CodexAgentTransportMode.AppServer && !_runtimeService.McpReady)
        {
            throw CreateExecutionReadinessException("agent_mcp_not_ready", stage,
                "当前线程的 MCP 尚未就绪，Turn 尚未提交。",
                "请等待初始化完成；持续失败时重新连接 Agent，再继续原任务。");
        }
    }

    private void ValidateExecutionPrerequisites(string stage)
    {
        if (!_runtimeService.IsConnected)
        {
            throw CreateExecutionReadinessException(
                _runtimeService.State == AgentSessionTransportState.Faulted
                    ? "agent_transport_faulted" : "agent_session_not_running", stage,
                "Agent 会话未运行或连接已经停止，Turn 尚未提交。",
                "请先启动或重新连接 Agent；原任务和产物不会被删除。");
        }
        if (!SelectedAgent.IsRuntimeAvailable)
        {
            throw CreateExecutionReadinessException("agent_runtime_unavailable", stage,
                "当前选择的 Agent 不支持执行。", "请切换到可执行的 Agent Runtime。");
        }
        if (!IsActiveWorkingDirectoryCurrent())
        {
            throw CreateExecutionReadinessException("agent_project_mismatch", stage,
                "Agent 的工作目录与当前项目不一致，已阻止跨项目提交。",
                "请为当前项目重新连接 Agent。");
        }
        if (!_serverService.IsProcessRunning || !IsMcpHealthReady(_serverService.State))
        {
            throw CreateExecutionReadinessException("mcp_server_unavailable", stage,
                "本地 GAssetForge MCP 服务未运行或健康检查未通过。",
                "请检查 MCP 服务状态并重新连接 Agent。");
        }
        if (_serverService.Endpoint?.Port != Port)
        {
            throw CreateExecutionReadinessException("mcp_endpoint_mismatch", stage,
                "MCP 服务端口与当前连接配置不一致。", "请按当前端口重新连接 Agent。");
        }
        if (_runtimeService.TransportMode == CodexAgentTransportMode.AppServer &&
            _runtimeService.AuthenticationState is not CodexAgentAuthenticationState.Authenticated and
                not CodexAgentAuthenticationState.NotRequired)
        {
            throw CreateExecutionReadinessException("agent_auth_required", stage,
                "Agent 账号认证尚未确认，Turn 尚未提交。",
                "请完成账号登录并刷新认证；无需修改绘图指令。");
        }
    }

    private string CaptureExecutionState() =>
        $"runtime={_runtimeService.State}; transport={_runtimeService.TransportMode}; " +
        $"auth={_runtimeService.AuthenticationState}; mcpReady={_runtimeService.McpReady}; " +
        $"server={_serverService.State}; serverRunning={_serverService.IsProcessRunning}; " +
        $"portMatches={_serverService.Endpoint?.Port == Port}; " +
        $"projectMatches={IsActiveWorkingDirectoryCurrent()}";

    private AgentReadinessException CreateExecutionReadinessException(
        string code, string stage, string message, string recoveryHint) =>
        new(code, stage, message, _runtimeService.ThreadId, CaptureExecutionState(), recoveryHint);

    private void LogExecutionFailure(Exception exception, string label, string stage, string fallbackCode)
    {
        var readiness = exception as AgentReadinessException;
        var occurredAt = readiness?.OccurredAt ?? DateTimeOffset.Now;
        var message = $"{label}：{Sanitize(exception.Message)}";
        var context = new WorkbenchLogContext
        {
            AgentThreadId = readiness?.ThreadId ?? _runtimeService.ThreadId,
            AgentTurnId = _runtimeService.ActiveTurnId,
            Stage = readiness?.Stage ?? stage,
            FailureStage = readiness?.Stage ?? stage,
            Status = "failed",
            ErrorCode = readiness?.Code ?? fallbackCode,
            ReadinessState = readiness?.StateSummary ?? CaptureExecutionState(),
            RecoveryHint = readiness?.RecoveryHint ?? "请保留此日志中的错误码与线程编号，检查详细错误后重试原任务。",
            ExceptionType = exception.GetType().FullName
        };
        try
        {
            // Persist at the failure boundary. A delayed UI callback must not change
            // the apparent ordering or lose evidence when the window is closing.
            if (_workbenchLogSink is IWorkbenchStructuredLogSink structured)
            {
                structured.WriteStructured(WorkbenchLogLevel.Error, "Agent", message, context, occurredAt, exception);
            }
            else
            {
                _workbenchLogSink?.Write(WorkbenchLogLevel.Error, "Agent", message, occurredAt, exception);
            }
        }
        catch
        {
            // Diagnostics must never replace the original execution failure.
        }
        Dispatch(() =>
        {
            _lastTurnStatus = message;
            AddLog(occurredAt, message, true, publishToWorkbench: false);
            RefreshState();
        });
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
        OnPropertyChanged(nameof(IsAgentSessionActive));
        OnPropertyChanged(nameof(IsMcpServerActive));
        OnPropertyChanged(nameof(IsMcpServerStandalone));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsTurnRunning));
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(CanEditRuntimeSettings));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanStartMcpServer));
        OnPropertyChanged(nameof(CanStopMcpServer));
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

        if (_isStoppingMcpServer)
        {
            return (AgentConnectionStatus.Stopping, "正在停止 MCP Server");
        }

        if (_isStarting)
        {
            return IsMcpHealthReady(_serverService.State)
                ? (AgentConnectionStatus.WaitingForAgent, "MCP Server 健康检查通过，正在建立 Agent 会话")
                : (AgentConnectionStatus.Starting, "正在启动 MCP Server 并执行健康检查");
        }

        if (_isStartingMcpServer)
        {
            return (AgentConnectionStatus.Starting, "正在独立启动 MCP Server 并执行健康检查");
        }

        if (!string.IsNullOrWhiteSpace(_faultMessage))
        {
            return (AgentConnectionStatus.Faulted, _faultMessage);
        }

        if (IsMcpServerStandalone && IsMcpHealthReady(_serverService.State))
        {
            return (
                AgentConnectionStatus.WaitingForAgent,
                SelectedAgent.IsRuntimeAvailable
                    ? "MCP Server 已就绪；Codex Runtime 尚未启动"
                    : "MCP Server 已就绪；所选 Agent Runtime 尚未适配，可用于独立诊断");
        }

        if (!SelectedAgent.IsRuntimeAvailable && !IsRunning)
        {
            return (AgentConnectionStatus.Stopped, "OpenCode Adapter 待接入，当前不可启动");
        }

        if (IsAgentSessionActive && !IsActiveWorkingDirectoryCurrent() &&
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
        string source = "Agent",
        bool publishToWorkbench = true)
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

        if (!publishToWorkbench)
        {
            return;
        }

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
        var sanitized = string.IsNullOrEmpty(token)
            ? message
            : message.Replace(token, "[REDACTED]", StringComparison.Ordinal);
        return WorkbenchConsoleService.RedactCommonCredentials(sanitized);
    }

    private static string FormatCodexRuntimeDiagnostics(CodexExecutableResolution resolution)
    {
        var builder = new StringBuilder();
        builder.AppendLine(resolution.Summary);
        foreach (var candidate in resolution.Candidates)
        {
            var status = candidate.Status switch
            {
                CodexExecutableCandidateStatus.Accepted => "可用",
                CodexExecutableCandidateStatus.Skipped => "已跳过",
                _ => "失败"
            };
            builder.Append('[')
                .Append(status)
                .Append("] [")
                .Append(candidate.Source)
                .Append("] ")
                .AppendLine(candidate.CandidatePath);
            builder.Append("  ").AppendLine(candidate.Reason);
        }

        return builder.ToString().TrimEnd();
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
            new AgentConnectionSettingsChangedEventArgs(
                SelectedAgent.Key,
                Port,
                CodexExecutablePath));

    private void RaiseCommandStates()
    {
        _startCommand.RaiseCanExecuteChanged();
        _stopCommand.RaiseCanExecuteChanged();
        _startMcpServerCommand.RaiseCanExecuteChanged();
        _stopMcpServerCommand.RaiseCanExecuteChanged();
        _refreshCodexRuntimeDiagnosticsCommand.RaiseCanExecuteChanged();
        _browseCodexExecutableCommand.RaiseCanExecuteChanged();
        _useAutomaticCodexExecutableCommand.RaiseCanExecuteChanged();
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

    private bool CanReuseCurrentMcpServer() =>
        !_serverService.IsProcessRunning ||
        (_serverService.Endpoint?.Port == Port && IsMcpHealthReady(_serverService.State));

    private static string? NormalizeConfiguredExecutablePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) ||
            string.Equals(executablePath.Trim(), "codex", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var trimmed = executablePath.Trim().Trim('"');
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return trimmed;
        }
    }

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

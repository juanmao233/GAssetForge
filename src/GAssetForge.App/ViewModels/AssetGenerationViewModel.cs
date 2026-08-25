using GAssetForge.App.Infrastructure;
using GAssetForge.App.Services;
using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.Core.ArtStyles;
using GAssetForge.Core.Generation;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;

namespace GAssetForge.App.ViewModels;

public sealed record AssetGenerationStepOption(
    string Key,
    string DisplayName,
    string Description);

public enum GeneratedAssetStage
{
    ModelOutput,
    NormalizedInput,
    BackgroundRemoved,
    Final
}

public enum GeneratedAssetVisualState
{
    Processing,
    Success,
    RetryableWarning,
    FinalFailure
}

public sealed class GeneratedAssetViewModel
{
    private readonly GenerationTaskStatus _taskStatus;
    private readonly bool _canRetry;
    private readonly bool _isSuperseded;
    private readonly bool _hasAutomaticRetryRemaining;

    public GeneratedAssetViewModel(
        string assetRoot,
        string relativePath,
        GeneratedAssetStage stage,
        GenerationTaskStatus taskStatus,
        bool canRetry,
        bool isSuperseded,
        bool hasAutomaticRetryRemaining,
        string? failureMessage,
        string taskId,
        GenerationTaskRetryLineage? retryLineage)
    {
        _taskStatus = taskStatus;
        _canRetry = canRetry;
        _isSuperseded = isSuperseded;
        _hasAutomaticRetryRemaining = hasAutomaticRetryRemaining;
        Stage = stage;
        FailureMessage = NormalizeFailureMessage(failureMessage);
        TaskId = taskId;
        AttemptNumber = retryLineage?.AttemptNumber ?? 1;
        MaxAttempts = retryLineage?.MaxAttempts ?? 1;
        RelativePath = relativePath.Replace('\\', '/');
        FullPath = Path.GetFullPath(Path.Combine(assetRoot, RelativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    public string Name => Path.GetFileName(RelativePath);

    public string RelativePath { get; }

    public string FullPath { get; }

    public GeneratedAssetStage Stage { get; }

    public string StageText => Stage switch
    {
        GeneratedAssetStage.ModelOutput => "模型原图",
        GeneratedAssetStage.NormalizedInput => "画布归一化",
        GeneratedAssetStage.BackgroundRemoved => "背景处理",
        GeneratedAssetStage.Final => "最终产物",
        _ => "生成产物"
    };

    public string TaskId { get; }

    public int AttemptNumber { get; }

    public int MaxAttempts { get; }

    public string StageBadgeText => MaxAttempts > 1
        ? $"{StageText} · 尝试 {AttemptNumber}/{MaxAttempts}"
        : StageText;

    public GeneratedAssetVisualState VisualState => _taskStatus switch
    {
        GenerationTaskStatus.Completed => GeneratedAssetVisualState.Success,
        GenerationTaskStatus.Failed when _isSuperseded || _hasAutomaticRetryRemaining =>
            GeneratedAssetVisualState.RetryableWarning,
        GenerationTaskStatus.Failed => GeneratedAssetVisualState.FinalFailure,
        _ => GeneratedAssetVisualState.Processing
    };

    public string FailureMessage { get; }

    public bool HasFailureMessage => FailureMessage.Length > 0;

    public string AvailabilityText
    {
        get
        {
            if (!File.Exists(FullPath))
            {
                return "文件缺失";
            }

            return _taskStatus switch
            {
                GenerationTaskStatus.Completed when Stage == GeneratedAssetStage.Final =>
                    "已生成",
                GenerationTaskStatus.Completed =>
                    $"{StageText}已保留 · 任务已完成",
                GenerationTaskStatus.Failed when _isSuperseded =>
                    $"{StageText}已保留 · 本次尝试未通过，已自动重做",
                GenerationTaskStatus.Failed when _hasAutomaticRetryRemaining =>
                    $"{StageText}已保留 · 本次未通过，正在自动重做",
                GenerationTaskStatus.Failed when _canRetry =>
                    $"{StageText}已保留 · 重做已停止，可重新生成该项",
                GenerationTaskStatus.Failed =>
                    $"{StageText}已保留 · 最终失败，需要人工介入",
                _ when Stage == GeneratedAssetStage.Final =>
                    "最终文件已落盘 · 等待任务完成登记与校验",
                _ => $"{StageText}已落盘 · 后续步骤处理中"
            };
        }
    }

    public string DiagnosticText => HasFailureMessage
        ? $"生成问题：{FailureMessage}"
        : AvailabilityText;

    private static string NormalizeFailureMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = string.Join(
            " ",
            value.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= 240 ? normalized : $"{normalized[..237]}...";
    }
}

public sealed class AssetGenerationSettingsChangedEventArgs(
    string stepKey,
    string negativePrompt,
    int maxParallelism,
    bool autoRetryEnabled,
    int autoRetryCount,
    bool preferUnifiedStylePrompt,
    IReadOnlyList<AssetGenerationDraftRowSnapshot> rows) : EventArgs
{
    public string StepKey { get; } = stepKey;

    public string NegativePrompt { get; } = negativePrompt;

    public int MaxParallelism { get; } = maxParallelism;

    public bool AutoRetryEnabled { get; } = autoRetryEnabled;

    public int AutoRetryCount { get; } = autoRetryCount;

    public bool PreferUnifiedStylePrompt { get; } = preferUnifiedStylePrompt;

    public IReadOnlyList<AssetGenerationDraftRowSnapshot> Rows { get; } = rows;

    public string ContentPrompt => Rows.FirstOrDefault()?.Description ?? string.Empty;

    public string OutputDirectory => Rows.FirstOrDefault()?.OutputDirectory ?? string.Empty;
}

public sealed class GenerationBatchPublishedEventArgs(
    string batchId,
    IReadOnlyList<string> taskIds,
    bool isRetry,
    bool dispatchRequested) : EventArgs
{
    public string BatchId { get; } = batchId;

    public IReadOnlyList<string> TaskIds { get; } = taskIds;

    public bool IsRetry { get; } = isRetry;

    public bool DispatchRequested { get; } = dispatchRequested;
}

internal sealed record AutomaticGenerationRetryResult(
    bool HadEligibleFailures,
    bool Scheduled,
    string? RetryBatchId,
    int TaskCount,
    string Message)
{
    public static AutomaticGenerationRetryResult None(string message) =>
        new(false, false, null, 0, message);

    public static AutomaticGenerationRetryResult Blocked(string message) =>
        new(true, false, null, 0, message);
}

internal sealed record RecoverableGenerationDispatch(
    string BatchId,
    IReadOnlyList<string> TaskIds,
    bool IsRetry);

public sealed class AssetGenerationViewModel : ObservableObject, IDisposable
{
    public const int MaximumDraftRowCount = GenerationInputValidator.MaximumBatchItemCount;
    public const int MaximumBatchOutputCount = GenerationInputValidator.MaximumBatchOutputCount;
    private const int MaximumAgentFeedbackEntries = 60;
    private const int MaximumAutoRetryCount = 10;
    private const int TerminalArtifactPollCount = 10;

    private static readonly IReadOnlyList<AssetGenerationStepOption> SupportedSteps =
    [
        new(
            TilemapConstraintPackageBuilder.WorkflowId,
            "Tilemap 制作",
            "使用当前 Tilemap 网格、画布尺寸、控制角与旋转参数生成单个透明瓦片。")
    ];

    private static readonly IReadOnlyList<int> SupportedAutoRetryCounts =
        Enumerable.Range(0, MaximumAutoRetryCount + 1).ToArray();

    private readonly WorkspacePaths _paths;
    private readonly IGenerationTaskStore _taskStore;
    private readonly GenerationReferenceSnapshotService _snapshotService;
    private readonly GenerationRunWorkspaceService _runWorkspaceService;
    private readonly IAssetGenerationOutputPicker _outputPicker;
    private readonly ArtStyleLibraryViewModel _styleLibrary;
    private readonly TilemapWorkspaceViewModel _tilemap;
    private readonly ProjectBrowserViewModel _projectBrowser;
    private readonly AgentConnectionViewModel _agentConnection;
    private readonly BackgroundRemovalViewModel _backgroundRemoval;
    private readonly AssetRootService _assetRootService;
    private readonly TimeProvider _timeProvider;
    private readonly DispatcherTimer _pollTimer;
    private readonly AsyncRelayCommand _createTaskCommand;
    private readonly AsyncRelayCommand _startGenerationCommand;
    private readonly AsyncRelayCommand _refreshTaskCommand;
    private readonly RelayCommand _openOutputDirectoryCommand;
    private readonly RelayCommand _addDraftRowCommand;
    private readonly RelayCommand<AssetGenerationDraftRowViewModel> _removeDraftRowCommand;
    private readonly RelayCommand<PublishedGenerationTaskViewModel> _retryFailedTaskCommand;
    private readonly AsyncRelayCommand _createManualRetryBatchCommand;
    private ArtStyleGroupViewModel? _selectedStyleGroup;
    private AssetGenerationStepOption _selectedGenerationStep;
    private string _negativePrompt;
    private int _maxParallelism;
    private bool _autoRetryEnabled;
    private int _autoRetryCount;
    private bool _preferUnifiedStylePrompt;
    private AssetGenerationDraftRowViewModel? _selectedDraftRow;
    private string _constraintPreview = string.Empty;
    private string _statusText = "确认风格、批量条目和输出目录后可直接开始生成；未连接时也可仅发布本地队列";
    private GenerationTask? _currentTask;
    private string? _currentBatchId;
    private bool _isBusy;
    private bool _isDispatching;
    private string? _currentDispatchBatchId;
    private GenerationBatchDispatchStage _currentDispatchStage = GenerationBatchDispatchStage.Idle;
    private double _dispatchProgress;
    private string _currentInstruction = "尚未开始调度";
    private string _dispatchStageDetail = "点击开始生成后，这里会显示批次从约束准备到产物校验的实际步骤。";
    private string _dispatchFailureText = string.Empty;
    private string _failureStageText = string.Empty;
    private int _requestedImageCount;
    private int _generatedImageCount;
    private int _verifiedImageCount;
    private double _imageProgress;
    private string _currentImageText = "尚未建立图片交付计划";
    private string _agentFeedbackStatusText = "尚未开始监听 Agent 回传";
    private bool _hasAgentGenerationRisk;
    private string _agentGenerationRiskText = string.Empty;
    private DateTimeOffset? _dispatchStartedAtUtc;
    private DateTimeOffset? _dispatchStageStartedAtUtc;
    private DateTimeOffset? _lastAgentActivityUtc;
    private DateTimeOffset? _lastOutputActivityUtc;
    private string? _lastAgentFeedbackCoalesceKey;
    private string _manualFallbackStatusText =
        "复制连接指令后粘贴到 Codex 聊天；存在 Pending 任务时可继续复制当前批次任务指令。";
    private bool _showCurrentBatchManualPrompt;
    private bool _isPolling;
    private bool _refreshRequested;
    private bool _disposed;
    private int _remainingTerminalArtifactPolls;
    private bool _suppressSettingsNotification = true;
    private readonly Dictionary<string, int> _lastAppliedTaskRevisions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _trackedBatchIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _observedOutputPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _observedArtifactPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<GenerationBatchDispatchStage, TimeSpan> _dispatchStageDurations = [];
    private readonly SemaphoreSlim _automaticRetryGate = new(1, 1);

    public AssetGenerationViewModel(
        WorkspacePaths paths,
        IGenerationTaskStore taskStore,
        ArtStyleLibraryViewModel styleLibrary,
        TilemapWorkspaceViewModel tilemap,
        ProjectBrowserViewModel projectBrowser,
        AgentConnectionViewModel agentConnection,
        IAssetGenerationOutputPicker outputPicker,
        BackgroundRemovalViewModel backgroundRemoval,
        string? selectedStepKey = null,
        string? contentPrompt = null,
        string? negativePrompt = null,
        string? outputDirectory = null,
        IEnumerable<AssetGenerationDraftRowSettings>? draftRows = null,
        int maxParallelism = 2,
        bool autoRetryEnabled = true,
        int autoRetryCount = 3,
        bool preferUnifiedStylePrompt = true,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _taskStore = taskStore ?? throw new ArgumentNullException(nameof(taskStore));
        _styleLibrary = styleLibrary ?? throw new ArgumentNullException(nameof(styleLibrary));
        _tilemap = tilemap ?? throw new ArgumentNullException(nameof(tilemap));
        _projectBrowser = projectBrowser ?? throw new ArgumentNullException(nameof(projectBrowser));
        _agentConnection = agentConnection ?? throw new ArgumentNullException(nameof(agentConnection));
        _outputPicker = outputPicker ?? throw new ArgumentNullException(nameof(outputPicker));
        _backgroundRemoval = backgroundRemoval ?? throw new ArgumentNullException(nameof(backgroundRemoval));
        _snapshotService = new GenerationReferenceSnapshotService(paths);
        _runWorkspaceService = new GenerationRunWorkspaceService();
        _assetRootService = new AssetRootService(paths);

        _selectedGenerationStep = SupportedSteps.FirstOrDefault(step =>
            string.Equals(step.Key, selectedStepKey, StringComparison.OrdinalIgnoreCase)) ?? SupportedSteps[0];
        _negativePrompt = negativePrompt?.Trim() ?? string.Empty;
        _maxParallelism = NormalizeParallelism(maxParallelism);
        _autoRetryEnabled = autoRetryEnabled;
        _autoRetryCount = NormalizeAutoRetryCount(autoRetryCount);
        _preferUnifiedStylePrompt = preferUnifiedStylePrompt;
        _timeProvider = timeProvider ?? TimeProvider.System;
        InitializeDraftRows(draftRows, contentPrompt, outputDirectory);

        _createTaskCommand = new AsyncRelayCommand(
            () => CreateTaskAsync(requestDispatch: false),
            () => CanCreateTask);
        _startGenerationCommand = new AsyncRelayCommand(StartGenerationAsync, () => CanStartGeneration);
        _refreshTaskCommand = new AsyncRelayCommand(RefreshCurrentTaskAsync, () => !IsBusy);
        _openOutputDirectoryCommand = new RelayCommand(OpenOutputDirectory, () => Directory.Exists(OutputDirectory));
        _addDraftRowCommand = new RelayCommand(AddDraftRow, () => CanAddDraftRow);
        _removeDraftRowCommand = new RelayCommand<AssetGenerationDraftRowViewModel>(
            RemoveDraftRow,
            row => row is not null && DraftRows.Count > 1 && DraftRows.Contains(row));
        _retryFailedTaskCommand = new RelayCommand<PublishedGenerationTaskViewModel>(
            RetryFailedTask,
            CanRetryFailedTask);
        _createManualRetryBatchCommand = new AsyncRelayCommand(
            CreateManualRetryBatchAsync,
            () => CanCreateManualRetryBatch);
        CreateTaskCommand = _createTaskCommand;
        StartGenerationCommand = _startGenerationCommand;
        RefreshTaskCommand = _refreshTaskCommand;
        OpenOutputDirectoryCommand = _openOutputDirectoryCommand;
        ChooseOutputDirectoryCommand = new RelayCommand(ChooseOutputDirectory);
        UseProjectCurrentDirectoryCommand = new RelayCommand(UseProjectCurrentDirectory);
        AddDraftRowCommand = _addDraftRowCommand;
        RemoveDraftRowCommand = _removeDraftRowCommand;
        ChooseRowOutputDirectoryCommand = new RelayCommand<AssetGenerationDraftRowViewModel>(ChooseRowOutputDirectory);
        UseProjectCurrentDirectoryForRowCommand = new RelayCommand<AssetGenerationDraftRowViewModel>(
            UseProjectCurrentDirectoryForRow);
        RetryFailedTaskCommand = _retryFailedTaskCommand;
        CreateManualRetryBatchCommand = _createManualRetryBatchCommand;
        ShowStyleLibraryCommand = new RelayCommand(() => StyleLibraryRequested?.Invoke(this, EventArgs.Empty));
        ShowAgentConnectionCommand = new RelayCommand(() => AgentConnectionRequested?.Invoke(this, EventArgs.Empty));
        ShowBackgroundRemovalCommand = new RelayCommand(() => BackgroundRemovalRequested?.Invoke(this, EventArgs.Empty));

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _pollTimer.Tick += OnPollTimerTick;

        _styleLibrary.PropertyChanged += OnStyleLibraryPropertyChanged;
        _tilemap.PropertyChanged += OnTilemapPropertyChanged;
        _agentConnection.PropertyChanged += OnAgentConnectionPropertyChanged;
        _agentConnection.AgentActivityReceived += OnAgentActivityReceived;
        _backgroundRemoval.PropertyChanged += OnBackgroundRemovalPropertyChanged;
        _projectBrowser.CurrentPathChanged += OnProjectCurrentPathChanged;
        _projectBrowser.ConfiguredRootPathChanged += OnProjectRootPathChanged;
        SelectedStyleGroup = _styleLibrary.SelectedGroup ?? _styleLibrary.Groups.FirstOrDefault();
        RefreshDerivedState();
        _suppressSettingsNotification = false;
        _ = RefreshCurrentTaskAsync();
    }

    public event EventHandler<AssetGenerationSettingsChangedEventArgs>? SettingsChanged;

    public event EventHandler? StyleLibraryRequested;

    public event EventHandler? AgentConnectionRequested;

    public event EventHandler? BackgroundRemovalRequested;

    /// <summary>
    /// Publication seam raised only after the queue transaction has committed and the published tasks
    /// have been applied to the UI. DispatchRequested lets Shell/AppServer integrations distinguish
    /// audit-only queue publication from an application-driven execution request.
    /// </summary>
    public event EventHandler<GenerationBatchPublishedEventArgs>? BatchPublished;

    public ObservableCollection<ArtStyleGroupViewModel> StyleGroups => _styleLibrary.Groups;

    public IReadOnlyList<AssetGenerationStepOption> GenerationSteps => SupportedSteps;

    [Obsolete("并行图片数已由 Agent Host 默认调度；仅为旧绑定兼容保留。")]
    public IReadOnlyList<int> ParallelismOptions => [];

    public BackgroundRemovalViewModel BackgroundRemoval => _backgroundRemoval;

    public ObservableCollection<AssetGenerationDraftRowViewModel> DraftRows { get; } = [];

    public ObservableCollection<PublishedGenerationTaskViewModel> PublishedTasks { get; } = [];

    public ObservableCollection<GeneratedAssetViewModel> GeneratedAssets { get; } = [];

    public AssetGenerationDraftRowViewModel? SelectedDraftRow
    {
        get => _selectedDraftRow;
        set
        {
            if (!SetProperty(ref _selectedDraftRow, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ContentPrompt));
            OnPropertyChanged(nameof(OutputDirectory));
            OnPropertyChanged(nameof(OutputDirectoryHint));
            _openOutputDirectoryCommand?.RaiseCanExecuteChanged();
        }
    }

    public int TotalRequestedCount => DraftRows.Sum(row => Math.Max(0, row.Quantity));

    public int DraftRowCount => DraftRows.Count;

    public bool CanAddDraftRow => DraftRows.Count < MaximumDraftRowCount;

    public string BatchSummaryText =>
        $"{DraftRows.Count} 种物品 · 共 {TotalRequestedCount} 张 · 每张独立任务 · 由 Agent Host 默认调度 · 整批最多 {MaximumBatchOutputCount} 张";

    public ArtStyleGroupViewModel? SelectedStyleGroup
    {
        get => _selectedStyleGroup;
        set
        {
            if (ReferenceEquals(_selectedStyleGroup, value))
            {
                return;
            }

            UnsubscribeStyleGroup(_selectedStyleGroup);
            _selectedStyleGroup = value;
            SubscribeStyleGroup(_selectedStyleGroup);
            OnPropertyChanged();
            if (!ReferenceEquals(_styleLibrary.SelectedGroup, value))
            {
                _styleLibrary.SelectedGroup = value;
            }

            RefreshDerivedState();
        }
    }

    public AssetGenerationStepOption SelectedGenerationStep
    {
        get => _selectedGenerationStep;
        set
        {
            if (value is null || !SetProperty(ref _selectedGenerationStep, value))
            {
                return;
            }

            RefreshDerivedState();
            NotifySettingsChanged();
        }
    }

    public string ContentPrompt
    {
        get => PrimaryDraftRow?.Description ?? string.Empty;
        set
        {
            if (PrimaryDraftRow is null)
            {
                return;
            }

            PrimaryDraftRow.Description = value ?? string.Empty;
        }
    }

    public string NegativePrompt
    {
        get => _negativePrompt;
        set
        {
            value ??= string.Empty;
            if (!SetProperty(ref _negativePrompt, value))
            {
                return;
            }

            RefreshDerivedState();
            NotifySettingsChanged();
        }
    }

    public int MaxParallelism
    {
        get => _maxParallelism;
        set
        {
            var normalized = NormalizeParallelism(value);
            if (!SetProperty(ref _maxParallelism, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(BatchSummaryText));
            ConstraintPreview = BuildConstraintPreview();
            NotifySettingsChanged();
        }
    }

    public IReadOnlyList<int> AutoRetryCountOptions => SupportedAutoRetryCounts;

    public bool PreferUnifiedStylePrompt
    {
        get => _preferUnifiedStylePrompt;
        set
        {
            if (!SetProperty(ref _preferUnifiedStylePrompt, value))
            {
                return;
            }

            RefreshDerivedState();
            NotifySettingsChanged();
        }
    }

    public bool IsUsingUnifiedStylePrompt =>
        TryGetFreshUnifiedPrompt(SelectedStyleGroup, out _);

    public string UnifiedStylePromptStatusText
    {
        get
        {
            if (!PreferUnifiedStylePrompt)
            {
                return "统一提示词优先已关闭；本批次保持原有风格描述、人工提示词与参考图链路。";
            }

            if (SelectedStyleGroup is null)
            {
                return "选择风格组后将优先使用其统一提示词样本。";
            }

            if (TryGetFreshUnifiedPrompt(SelectedStyleGroup, out _))
            {
                return "已命中新鲜统一提示词样本；本批次不会携带或读取参考图。";
            }

            return SelectedStyleGroup.UnifiedPromptSample is null
                ? "当前风格组尚无统一提示词样本，将自动回退到风格描述、人工提示词与参考图。"
                : "统一提示词样本已过期（素材或风格定义已变化），将自动回退到风格描述、人工提示词与参考图。";
        }
    }

    public bool AutoRetryEnabled
    {
        get => _autoRetryEnabled;
        set
        {
            if (!SetProperty(ref _autoRetryEnabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(AutoRetrySummaryText));
            OnPropertyChanged(nameof(MaxGenerationAttempts));
            NotifySettingsChanged();
        }
    }

    /// <summary>
    /// Maximum number of redo attempts after the initial generation. A value of three means at most
    /// four total attempts. The dispatch coordinator consumes this setting; the view model persists
    /// and presents it without mutating task lineage itself.
    /// </summary>
    public int AutoRetryCount
    {
        get => _autoRetryCount;
        set
        {
            var normalized = NormalizeAutoRetryCount(value);
            if (!SetProperty(ref _autoRetryCount, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(AutoRetrySummaryText));
            OnPropertyChanged(nameof(MaxGenerationAttempts));
            NotifySettingsChanged();
        }
    }

    public string AutoRetrySummaryText => !AutoRetryEnabled
        ? "自动重做已关闭；问题产物会保留并等待人工处理。"
        : AutoRetryCount == 0
            ? "自动重做已开启，但最多重做次数为 0；失败后仅提示。"
            : $"可恢复问题默认自动重做，初次生成后最多再尝试 {AutoRetryCount} 次；每次中间产物都会保留。";

    public int MaxGenerationAttempts => AutoRetryEnabled
        ? AutoRetryCount + 1
        : 1;

    public string OutputDirectory
    {
        get => SelectedDraftRow?.OutputDirectory ?? PrimaryDraftRow?.OutputDirectory ?? string.Empty;
        private set
        {
            var target = SelectedDraftRow ?? PrimaryDraftRow;
            if (target is null || string.Equals(target.OutputDirectory, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            target.OutputDirectory = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OutputDirectoryHint));
            _openOutputDirectoryCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsAgentConnected => _agentConnection.IsConnected;

    public string ConnectionStatusText => IsAgentConnected
        ? $"已连接 {_agentConnection.SelectedAgent.DisplayName} · {_agentConnection.Endpoint}"
        : $"{_agentConnection.StatusText} · 可离线发布到本地队列，Agent 就绪后再调度";

    public string CanvasSizeText =>
        $"{_tilemap.ArtImageWidth} × {_tilemap.ArtImageHeight} px · PNG · 透明背景";

    public string TilemapGeometryText =>
        $"网格边长 {_tilemap.GridUnitSize:0.##} · {_tilemap.TileShapeText} · " +
        $"A/B 边夹角 {_tilemap.DiamondAngle:0.##}° · A 边方向 {_tilemap.RotationAngle:0.##}°";

    public string OutputDirectoryHint
    {
        get
        {
            var target = SelectedDraftRow ?? PrimaryDraftRow;
            return target?.OutputDirectoryHint ?? "尚未设置输出目录";
        }
    }

    public string ConstraintPreview
    {
        get => _constraintPreview;
        private set => SetProperty(ref _constraintPreview, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanCreateTask));
            OnPropertyChanged(nameof(CanStartGeneration));
            OnPropertyChanged(nameof(CanCreateManualRetryBatch));
            RaiseCommandStates();
        }
    }

    public bool IsDispatching
    {
        get => _isDispatching;
        private set
        {
            if (!SetProperty(ref _isDispatching, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanCreateTask));
            OnPropertyChanged(nameof(CanStartGeneration));
            OnPropertyChanged(nameof(CanCreateManualRetryBatch));
            RaiseCommandStates();
        }
    }

    internal GenerationBatchDispatchStage CurrentDispatchStage => _currentDispatchStage;

    internal string CurrentDispatchBatchId => _currentDispatchBatchId ?? string.Empty;

    public double DispatchProgress
    {
        get => _dispatchProgress;
        private set
        {
            if (SetProperty(ref _dispatchProgress, value))
            {
                OnPropertyChanged(nameof(DispatchProgressText));
            }
        }
    }

    public string DispatchProgressText => $"{DispatchProgress:0}%";

    public int RequestedImageCount
    {
        get => _requestedImageCount;
        private set
        {
            if (SetProperty(ref _requestedImageCount, value))
            {
                OnPropertyChanged(nameof(ImageProgressText));
            }
        }
    }

    public int GeneratedImageCount
    {
        get => _generatedImageCount;
        private set
        {
            if (SetProperty(ref _generatedImageCount, value))
            {
                OnPropertyChanged(nameof(ImageProgressText));
            }
        }
    }

    public int VerifiedImageCount
    {
        get => _verifiedImageCount;
        private set => SetProperty(ref _verifiedImageCount, value);
    }

    public double ImageProgress
    {
        get => _imageProgress;
        private set => SetProperty(ref _imageProgress, value);
    }

    public string ImageProgressText => RequestedImageCount <= 0
        ? "0 / 0 张已落盘"
        : $"{GeneratedImageCount} / {RequestedImageCount} 张已落盘";

    public string CurrentImageText
    {
        get => _currentImageText;
        private set => SetProperty(ref _currentImageText, value);
    }

    public ObservableCollection<GenerationAgentFeedbackEntry> AgentFeedbackEntries { get; } = [];

    public string AgentFeedbackStatusText
    {
        get => _agentFeedbackStatusText;
        private set => SetProperty(ref _agentFeedbackStatusText, value);
    }

    public bool HasAgentGenerationRisk
    {
        get => _hasAgentGenerationRisk;
        private set => SetProperty(ref _hasAgentGenerationRisk, value);
    }

    public string AgentGenerationRiskText
    {
        get => _agentGenerationRiskText;
        private set => SetProperty(ref _agentGenerationRiskText, value);
    }

    public string CurrentDispatchStageText => BuildDispatchStageText(_currentDispatchStage);

    public string CurrentStageElapsedText
    {
        get
        {
            if (_dispatchStageStartedAtUtc is null ||
                _currentDispatchStage is GenerationBatchDispatchStage.Idle)
            {
                return "当前阶段尚未计时";
            }

            var elapsed = GetUtcNow() - _dispatchStageStartedAtUtc.Value;
            return $"当前阶段耗时 {FormatDuration(elapsed)}";
        }
    }

    public string LongestStageText
    {
        get
        {
            var candidates = _dispatchStageDurations.ToDictionary(pair => pair.Key, pair => pair.Value);
            if (_dispatchStageStartedAtUtc is not null &&
                _currentDispatchStage is not GenerationBatchDispatchStage.Idle)
            {
                var currentElapsed = GetUtcNow() - _dispatchStageStartedAtUtc.Value;
                if (!candidates.TryGetValue(_currentDispatchStage, out var previous) ||
                    currentElapsed > previous)
                {
                    candidates[_currentDispatchStage] = currentElapsed;
                }
            }

            if (candidates.Count == 0)
            {
                return "最长步骤：尚无数据";
            }

            var longest = candidates.MaxBy(pair => pair.Value);
            return $"最长步骤：{BuildDispatchStageText(longest.Key)} · {FormatDuration(longest.Value)}";
        }
    }

    public string CurrentInstruction
    {
        get => _currentInstruction;
        private set => SetProperty(ref _currentInstruction, value);
    }

    public string DispatchStageDetail
    {
        get => _dispatchStageDetail;
        private set => SetProperty(ref _dispatchStageDetail, value);
    }

    public string DispatchFailureText
    {
        get => _dispatchFailureText;
        private set
        {
            if (SetProperty(ref _dispatchFailureText, value))
            {
                OnPropertyChanged(nameof(HasDispatchFailure));
            }
        }
    }

    public string FailureStageText
    {
        get => _failureStageText;
        private set => SetProperty(ref _failureStageText, value);
    }

    public bool HasDispatchProgress =>
        !string.IsNullOrWhiteSpace(_currentDispatchBatchId) &&
        _currentDispatchStage != GenerationBatchDispatchStage.Idle;

    public bool HasDispatchFailure => !string.IsNullOrWhiteSpace(DispatchFailureText);

    public bool CanCreateTask => !IsBusy &&
                                  !IsDispatching &&
                                  SelectedStyleGroup is not null &&
                                  HasUsableSelectedStyle(SelectedStyleGroup) &&
                                  (TryGetFreshUnifiedPrompt(SelectedStyleGroup, out _) ||
                                   TryValidateStyleReferences(SelectedStyleGroup, out _)) &&
                                  SelectedGenerationStep.Key == TilemapConstraintPackageBuilder.WorkflowId &&
                                  DraftRows.Count > 0 &&
                                  DraftRows.All(row => row.IsValid) &&
                                  BackgroundRemoval.IsConfigurationValid &&
                                  TotalRequestedCount is >= 1 and <= MaximumBatchOutputCount;

    public bool CanStartGeneration => CanCreateTask && IsAgentConnected;

    public bool HasCurrentTask => _currentTask is not null || PublishedTasks.Count > 0;

    public bool HasPublishedBatch => !string.IsNullOrWhiteSpace(_currentBatchId) && PublishedTasks.Count > 0;

    public string CurrentBatchId => _currentBatchId ?? string.Empty;

    public string CurrentTaskId => _currentTask?.Id ?? string.Empty;

    public string CurrentTaskStatusText => BuildBatchStatusText();

    public string CurrentTaskDetail => BuildBatchProgressText();

    public bool CanCopyCodexConnectionPrompt => _agentConnection.CanCopyCodexConnectionPrompt;

    public string CodexConnectionPrompt => _agentConnection.CodexConnectionPrompt;

    public bool CanCopyCurrentBatchPrompt =>
        CanCopyCodexConnectionPrompt && GetCurrentBatchPendingTasks().Length > 0;

    public bool CanCreateManualRetryBatch
    {
        get
        {
            var currentTasks = GetCurrentBatchActiveTasks();
            return !IsBusy &&
                   !IsDispatching &&
                   currentTasks.Length > 0 &&
                   currentTasks.All(task => task.Status == GenerationTaskStatus.Failed) &&
                   currentTasks.All(task => !task.HasAutomaticRetryRemaining);
        }
    }

    public string CurrentBatchManualPrompt
    {
        get
        {
            var pendingTasks = GetCurrentBatchPendingTasks();
            if (!CanCopyCodexConnectionPrompt ||
                pendingTasks.Length == 0 ||
                string.IsNullOrWhiteSpace(_currentBatchId))
            {
                return string.Empty;
            }

            return CodexManualFallbackPromptBuilder.BuildBatchPrompt(
                _agentConnection.WorkingDirectory,
                new Uri(_agentConnection.Endpoint, UriKind.Absolute),
                McpServerProcessService.DefaultBearerTokenEnvironmentVariableName,
                _currentBatchId,
                pendingTasks.Select(task => task.TaskId).ToArray());
        }
    }

    public string ManualFallbackPromptPreview =>
        _showCurrentBatchManualPrompt && CanCopyCurrentBatchPrompt
            ? CurrentBatchManualPrompt
            : CodexConnectionPrompt;

    public string ManualFallbackAvailabilityText
    {
        get
        {
            if (!HasPublishedBatch)
            {
                return "尚无当前批次；先发布任务，或仅复制 Codex 连接指令检查 MCP。";
            }

            var currentTasks = GetCurrentBatchActiveTasks();
            if (currentTasks.Any(task => task.HasAutomaticRetryRemaining))
            {
                return "当前失败项仍有自动重做额度；软件会先创建新的任务与交付路径，无需人工复制旧 taskId。";
            }

            if (currentTasks.Length > 0 && currentTasks.All(task => task.Status == GenerationTaskStatus.Failed))
            {
                return "当前批次没有可认领的 Pending 任务；旧 Failed taskId 不会被复制，请先创建手动重试批次。";
            }

            var pendingCount = currentTasks.Count(task => task.Status == GenerationTaskStatus.Pending);
            if (pendingCount == 0)
            {
                return "当前批次没有可认领的 Pending 任务，批次任务指令已禁用。";
            }

            if (!CanCopyCodexConnectionPrompt)
            {
                return "当前项目工作目录或 MCP Endpoint 无效；请先在 MCP Agent 接入页修正连接设置。";
            }

            return $"当前批次有 {pendingCount} 个 Pending 任务可交给 Codex 人工认领；只会复制这些 taskId。";
        }
    }

    public string ManualFallbackStatusText
    {
        get => _manualFallbackStatusText;
        private set => SetProperty(ref _manualFallbackStatusText, value);
    }

    public ICommand ShowStyleLibraryCommand { get; }

    public ICommand ShowAgentConnectionCommand { get; }

    public ICommand ChooseOutputDirectoryCommand { get; }

    public ICommand UseProjectCurrentDirectoryCommand { get; }

    public ICommand AddDraftRowCommand { get; }

    public ICommand RemoveDraftRowCommand { get; }

    public ICommand ChooseRowOutputDirectoryCommand { get; }

    public ICommand UseProjectCurrentDirectoryForRowCommand { get; }

    public ICommand RetryFailedTaskCommand { get; }

    public ICommand CreateManualRetryBatchCommand { get; }

    public ICommand CreateTaskCommand { get; }

    public ICommand StartGenerationCommand { get; }

    public ICommand ShowBackgroundRemovalCommand { get; }

    public ICommand RefreshTaskCommand { get; }

    public ICommand OpenOutputDirectoryCommand { get; }

    public void ShowCodexConnectionPromptPreview()
    {
        _showCurrentBatchManualPrompt = false;
        OnPropertyChanged(nameof(ManualFallbackPromptPreview));
    }

    public bool TryShowCurrentBatchPromptPreview()
    {
        if (!CanCopyCurrentBatchPrompt)
        {
            ManualFallbackStatusText = ManualFallbackAvailabilityText;
            return false;
        }

        _showCurrentBatchManualPrompt = true;
        OnPropertyChanged(nameof(ManualFallbackPromptPreview));
        return true;
    }

    public void ReportManualFallbackCopyResult(string label, bool copied, string? errorMessage = null)
    {
        var normalizedLabel = string.IsNullOrWhiteSpace(label) ? "指令" : label.Trim();
        ManualFallbackStatusText = copied
            ? $"已复制“{normalizedLabel}”，可直接粘贴到 Codex 聊天窗口。"
            : $"复制“{normalizedLabel}”失败：{(string.IsNullOrWhiteSpace(errorMessage) ? "剪贴板不可用" : errorMessage.Trim())}";
    }

    public void ReportDispatchQueued(string batchId, string? detail = null)
    {
        EnsureDispatchBatch(batchId);
        ApplyDispatchProgress(
            batchId,
            GenerationBatchDispatchStage.Queued,
            35d,
            "GenerationBatchDispatchCoordinator.TryEnqueue(batchId, taskIds)",
            string.IsNullOrWhiteSpace(detail)
                ? "批次已进入应用内串行调度队列。"
                : detail.Trim());
        StatusText = $"批次 {ShortId(batchId)} 已进入应用内 Agent 调度队列";
    }

    public void ReportDispatchResult(string batchId, string? detail = null)
    {
        if (!IsCurrentDispatchBatch(batchId))
        {
            return;
        }

        ApplyDispatchProgress(
            batchId,
            GenerationBatchDispatchStage.WaitingForTaskClaim,
            Math.Max(55d, DispatchProgress),
            "等待 MCP 任务状态与本地文件校验",
            string.IsNullOrWhiteSpace(detail)
                ? "Agent Turn 已结束，正在根据 MCP 任务状态收束本批次结果。"
                : detail.Trim());
        StatusText = $"批次 {ShortId(batchId)} 的 Agent Turn 已结束，正在核对任务与产物状态";
    }

    public void ReportDispatchFailure(string batchId, string failureMessage)
    {
        var detail = string.IsNullOrWhiteSpace(failureMessage)
            ? "执行器未返回具体原因"
            : failureMessage.Trim();
        EnsureDispatchBatch(batchId);
        GenerationBatchDispatchStage? failedStage = _currentDispatchStage switch
        {
            GenerationBatchDispatchStage.Idle => GenerationBatchDispatchStage.Queued,
            GenerationBatchDispatchStage.Failed when !string.IsNullOrWhiteSpace(FailureStageText) => null,
            _ => _currentDispatchStage
        };
        ApplyDispatchProgress(
            batchId,
            GenerationBatchDispatchStage.Failed,
            DispatchProgress,
            "停止当前批次调度并保留可重试任务",
            detail,
            failedStage,
            detail);
        StatusText = $"批次 {ShortId(batchId)} 已成功发布到本地队列，但自动调度失败：{detail}";
    }

    public void ReportDispatchCanceled(string? batchId, string? detail = null)
    {
        if (!string.IsNullOrWhiteSpace(batchId))
        {
            EnsureDispatchBatch(batchId);
            ApplyDispatchProgress(
                batchId,
                GenerationBatchDispatchStage.Canceled,
                DispatchProgress,
                "取消旧项目的 Agent 调度",
                string.IsNullOrWhiteSpace(detail)
                    ? "项目工作目录已经切换。"
                    : detail.Trim());
        }
        else
        {
            IsDispatching = false;
        }

        var shortId = ShortId(batchId);
        var subject = string.IsNullOrWhiteSpace(shortId)
            ? "旧项目生成批次"
            : $"批次 {shortId}";
        var suffix = string.IsNullOrWhiteSpace(detail)
            ? string.Empty
            : $"：{detail.Trim()}";
        StatusText = $"{subject} 的应用内调度已因项目切换取消；未完成任务仍保留在本地 Pending 队列{suffix}";
    }

    internal void ReportDispatchProgress(GenerationBatchDispatchProgressSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!IsCurrentDispatchBatch(snapshot.Request.BatchId))
        {
            return;
        }

        ApplyDispatchProgress(
            snapshot.Request.BatchId,
            snapshot.Stage,
            snapshot.ProgressPercentage,
            snapshot.Instruction,
            snapshot.Detail,
            snapshot.FailureStage,
            snapshot.FailureMessage);
    }

    internal Task RefreshAfterDispatchAsync() => RefreshCurrentTaskAsync();

    internal async Task<IReadOnlyList<RecoverableGenerationDispatch>>
        GetRecoverablePendingDispatchesAsync()
    {
        var tasks = await _taskStore.ListAsync().ConfigureAwait(true);
        return tasks
            .Where(task =>
                task.Status == GenerationTaskStatus.Pending &&
                task.DispatchIntent?.Requested == true &&
                !string.IsNullOrWhiteSpace(task.BatchId) &&
                IsTaskForCurrentProject(task))
            .GroupBy(task => task.BatchId!, StringComparer.Ordinal)
            .OrderBy(group => group.Min(task => task.CreatedAtUtc))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new RecoverableGenerationDispatch(
                group.Key,
                group.OrderBy(task => task.BatchItemIndex ?? int.MaxValue)
                    .ThenBy(task => task.CreatedAtUtc)
                    .ThenBy(task => task.Id, StringComparer.Ordinal)
                    .Select(task => task.Id)
                    .ToArray(),
                group.Any(task => task.DispatchIntent?.IsRetry == true)))
            .ToArray();
    }

    internal async Task<IReadOnlyList<string>> GetRecoverableAutomaticRetrySourceBatchIdsAsync()
    {
        var tasks = await _taskStore.ListAsync().ConfigureAwait(true);
        return tasks
            .Where(task =>
                task.Status == GenerationTaskStatus.Failed &&
                task.DispatchIntent?.Requested == true &&
                task.FailureRetryable == true &&
                string.IsNullOrWhiteSpace(task.AutomaticRetryBlockedReason) &&
                string.IsNullOrWhiteSpace(task.SupersededByTaskId) &&
                task.RetryLineage is { } lineage &&
                lineage.AttemptNumber < lineage.MaxAttempts &&
                !string.IsNullOrWhiteSpace(task.BatchId) &&
                IsTaskForCurrentProject(task))
            .OrderBy(task => task.CreatedAtUtc)
            .ThenBy(task => task.BatchItemIndex ?? int.MaxValue)
            .ThenBy(task => task.Id, StringComparer.Ordinal)
            .Select(task => task.BatchId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal async Task RepairPersistedDispatchWorkspaceAsync(string batchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        var tasks = (await _taskStore.ListBatchAsync(batchId).ConfigureAwait(true))
            .Where(IsTaskForCurrentProject)
            .OrderBy(task => task.BatchItemIndex ?? int.MaxValue)
            .ThenBy(task => task.CreatedAtUtc)
            .ThenBy(task => task.Id, StringComparer.Ordinal)
            .ToArray();
        if (tasks.Length == 0)
        {
            throw new InvalidDataException($"恢复调度时找不到当前项目批次：{batchId}");
        }

        var deliveries = tasks.Select(task =>
        {
            if (!MaterialGenerationConstraintComposer.TryReadDelivery(task.Constraints, out var delivery) ||
                delivery?.Workspace is null)
            {
                throw new InvalidDataException($"恢复任务 {ShortId(task.Id)} 缺少 project-local workspace。");
            }

            return delivery;
        }).ToArray();
        var assetRoot = deliveries[0].AssetRoot;
        if (deliveries.Any(delivery => !AssetRootService.PathEquals(assetRoot, delivery.AssetRoot)))
        {
            throw new InvalidDataException("恢复批次跨越了不同项目 Assets 根目录。");
        }

        var run = _runWorkspaceService.OpenOrRepair(batchId, assetRoot);
        var manifestFullPath = Path.GetFullPath(Path.Combine(
            assetRoot,
            run.ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var manifestExists = File.Exists(manifestFullPath) && new FileInfo(manifestFullPath).Length > 0;
        var manifestItems = new List<GenerationRunManifestItem>(tasks.Length);
        for (var index = 0; index < tasks.Length; index++)
        {
            var task = tasks[index];
            var delivery = deliveries[index];
            var workspace = delivery.Workspace!;
            if (!string.Equals(workspace.RunId, run.BatchId, StringComparison.Ordinal) ||
                !string.Equals(workspace.RootPath, run.RootRelativePath, StringComparison.Ordinal) ||
                !string.Equals(workspace.ManifestPath, run.ManifestRelativePath, StringComparison.Ordinal) ||
                workspace.ModelOutputPaths is not { Length: 1 } modelPaths ||
                workspace.ProcessedPaths is not { Length: 1 } processedPaths ||
                delivery.StagingPaths is not { Length: 1 } stagingPaths ||
                delivery.TargetPaths is not { Length: 1 } targetPaths)
            {
                throw new InvalidDataException(
                    $"恢复任务 {ShortId(task.Id)} 的冻结 Run 路径与批次 {ShortId(batchId)} 不一致。");
            }

            var promptFullPath = Path.GetFullPath(Path.Combine(
                assetRoot,
                workspace.PromptPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(promptFullPath) || new FileInfo(promptFullPath).Length == 0)
            {
                await _runWorkspaceService.WritePromptAsync(run, workspace, task.Constraints)
                    .ConfigureAwait(true);
            }
            var description = PublishedTasks.FirstOrDefault(item =>
                    string.Equals(item.TaskId, task.Id, StringComparison.Ordinal))?.Description ??
                task.DisplayName;
            manifestItems.Add(new GenerationRunManifestItem(
                task.BatchItemId ?? task.Id,
                task.Id,
                description,
                task.BatchItemIndex ?? index,
                task.RetryLineage is { AttemptNumber: > 1 } lineage
                    ? lineage.AttemptNumber - 1
                    : index,
                tasks.Length,
                modelPaths[0],
                stagingPaths[0],
                processedPaths[0],
                targetPaths[0]));
        }

        if (!manifestExists)
        {
            await _runWorkspaceService.WriteManifestAsync(
                    run,
                    new GenerationRunManifest(
                        1,
                        run.BatchId,
                        run.BatchId,
                        assetRoot,
                        tasks[0].Constraints.WorkflowId,
                        tasks.Min(task => task.CreatedAtUtc),
                        "recovered-dispatch",
                        "从持久化任务合同重建",
                        manifestItems))
                .ConfigureAwait(true);
        }
    }

    internal async Task<AutomaticGenerationRetryResult> TryCreateAutomaticRetryBatchAsync(
        string sourceBatchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBatchId);
        await _automaticRetryGate.WaitAsync().ConfigureAwait(true);
        PreparedGenerationRunWorkspace? runWorkspace = null;
        GenerationTask[] retryableParents = [];
        IReadOnlyList<GenerationTask> committedRetryTasks = [];
        string? retryBatchId = null;
        var retryStoreCommitted = false;
        try
        {
            var sourceTasks = await _taskStore.ListBatchAsync(sourceBatchId).ConfigureAwait(true);
            retryableParents = sourceTasks
                .Where(task =>
                    task.Status == GenerationTaskStatus.Failed &&
                    string.IsNullOrWhiteSpace(task.SupersededByTaskId) &&
                    string.IsNullOrWhiteSpace(task.AutomaticRetryBlockedReason) &&
                    task.DispatchIntent?.Requested == true &&
                    task.FailureRetryable == true &&
                    task.RetryLineage is { } lineage &&
                    lineage.AttemptNumber < lineage.MaxAttempts)
                .OrderBy(task => task.BatchItemIndex)
                .ThenBy(task => task.Id, StringComparer.Ordinal)
                .ToArray();
            if (retryableParents.Length == 0)
            {
                return AutomaticGenerationRetryResult.None("当前批次没有可自动重做的失败项。");
            }

            var deliveries = retryableParents
                .Select(task =>
                    MaterialGenerationConstraintComposer.TryReadDelivery(task.Constraints, out var delivery)
                        ? delivery
                        : null)
                .ToArray();
            if (deliveries.Any(delivery => delivery is null))
            {
                return await BlockAutomaticRetryAsync(
                        retryableParents,
                        "可恢复失败项缺少冻结 delivery 计划，需要人工检查任务数据。")
                    .ConfigureAwait(true);
            }

            var firstDelivery = deliveries[0]!;
            if (deliveries.Skip(1).Any(delivery =>
                    !AssetRootService.PathEquals(firstDelivery.AssetRoot, delivery!.AssetRoot)))
            {
                return await BlockAutomaticRetryAsync(
                        retryableParents,
                        "自动重做批次跨越了不同项目 Assets 根目录，需要人工拆分。")
                    .ConfigureAwait(true);
            }

            retryBatchId = Guid.NewGuid().ToString("N");
            runWorkspace = _runWorkspaceService.Prepare(retryBatchId, firstDelivery.AssetRoot);
            await _runWorkspaceService.MirrorExistingRunReferencesAsync(runWorkspace, firstDelivery)
                .ConfigureAwait(true);
            var retryItems = new List<CreateGenerationRetryBatchItemRequest>(retryableParents.Length);
            var itemDescriptions = new Dictionary<string, string>(StringComparer.Ordinal);
            var manifestDrafts = new List<GenerationRunManifestDraft>(retryableParents.Length);
            for (var index = 0; index < retryableParents.Length; index++)
            {
                var parent = retryableParents[index];
                var previousDelivery = deliveries[index]!;
                if (parent.Constraints.Output.Count != 1)
                {
                    throw new InvalidDataException(
                        $"自动重做只支持一图一任务，任务 {ShortId(parent.Id)} 的 output.count 无效。");
                }

                var attemptNumber = parent.RetryLineage!.AttemptNumber + 1;
                var itemId = Guid.NewGuid().ToString("N");
                var suggestedBaseName =
                    $"tilemap_{retryBatchId[..8]}_attempt_{attemptNumber:D2}_r{index + 1:D2}";
                var finalOutputDirectory = ResolveRetryOutputDirectory(
                    previousDelivery.OutputDirectory,
                    retryBatchId);
                var targetPath = $"{finalOutputDirectory}/{suggestedBaseName}.png";
                var targetFullPath = Path.GetFullPath(Path.Combine(
                    previousDelivery.AssetRoot,
                    targetPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!AssetRootService.IsWithinRoot(previousDelivery.AssetRoot, targetFullPath))
                {
                    throw new InvalidDataException($"自动重做目标越出了原任务 Assets 根目录：{targetPath}");
                }

                if (File.Exists(targetFullPath) || Directory.Exists(targetFullPath))
                {
                    throw new IOException($"自动重做目标已经存在：{targetFullPath}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetFullPath)!);
                var workspacePlan = _runWorkspaceService.CreateOutputPlan(
                    runWorkspace,
                    itemId,
                    suggestedBaseName);
                var normalizedPath = _runWorkspaceService.CreateNormalizedPath(
                    runWorkspace,
                    itemId,
                    suggestedBaseName);
                var retryConstraints = BuildAutomaticRetryConstraints(parent, attemptNumber);
                retryConstraints = MaterialGenerationConstraintComposer.ReplaceDelivery(
                    retryConstraints,
                    new GenerationDeliveryPlan
                    {
                        AssetRoot = previousDelivery.AssetRoot,
                        OutputDirectory = finalOutputDirectory,
                        TargetPaths = [targetPath],
                        StagingPaths = [normalizedPath],
                        SuggestedBaseName = suggestedBaseName,
                        Workspace = workspacePlan
                    });
                await _runWorkspaceService.WritePromptAsync(runWorkspace, workspacePlan, retryConstraints)
                    .ConfigureAwait(true);
                var description = PublishedTasks.FirstOrDefault(item =>
                        string.Equals(item.TaskId, parent.Id, StringComparison.Ordinal))?.Description ??
                    parent.DisplayName;
                itemDescriptions[itemId] = description;
                retryItems.Add(new CreateGenerationRetryBatchItemRequest
                {
                    ParentTaskId = parent.Id,
                    ItemId = itemId,
                    DisplayName = $"{parent.DisplayName} · 自动重做 {attemptNumber}/{parent.RetryLineage.MaxAttempts}",
                    Constraints = retryConstraints
                });
                manifestDrafts.Add(new GenerationRunManifestDraft(
                    itemId,
                    description,
                    index,
                    attemptNumber - 1,
                    parent.RetryLineage.MaxAttempts,
                    workspacePlan.ModelOutputPaths[0],
                    normalizedPath,
                    workspacePlan.ProcessedPaths[0],
                    targetPath));
            }

            var result = await _taskStore.CreateRetryBatchAsync(new CreateGenerationRetryBatchRequest
            {
                BatchId = retryBatchId,
                DisplayName = $"自动重做 · {retryableParents.Length} 项",
                RequestedBy = "GAssetForge Desktop · Automatic Retry",
                DispatchRequested = true,
                Items = retryItems.ToArray()
            }).ConfigureAwait(true);
            if (!result.Created)
            {
                _runWorkspaceService.DeleteIfUnpublished(runWorkspace);
                runWorkspace = null;
                foreach (var batchId in result.Tasks
                             .Select(task => task.BatchId)
                             .Where(batchId => !string.IsNullOrWhiteSpace(batchId)))
                {
                    _trackedBatchIds.Add(batchId!);
                }

                _currentBatchId = result.Tasks.FirstOrDefault()?.BatchId;

                await RefreshCurrentTaskAsync().ConfigureAwait(true);
                if (result.Tasks.FirstOrDefault()?.BatchId is { Length: > 0 } existingBatchId)
                {
                    RaiseBatchPublished(
                        existingBatchId,
                        result.Tasks,
                        isRetry: true,
                        dispatchRequested: true);
                }

                return new AutomaticGenerationRetryResult(
                    true,
                    true,
                    result.Tasks.FirstOrDefault()?.BatchId,
                    result.Tasks.Count,
                    "重试后继任务已经由另一条调度路径创建，已复用既有任务。");
            }

            retryStoreCommitted = true;
            committedRetryTasks = result.Tasks;
            var tasksByItemId = result.Tasks.ToDictionary(task => task.BatchItemId!, StringComparer.Ordinal);
            await _runWorkspaceService.WriteManifestAsync(
                    runWorkspace,
                    new GenerationRunManifest(
                        1,
                        retryBatchId,
                        retryBatchId,
                        firstDelivery.AssetRoot,
                        retryableParents[0].Constraints.WorkflowId,
                        DateTimeOffset.UtcNow,
                        "automatic-retry",
                        "冻结自上一轮结构化失败诊断",
                        manifestDrafts.Select(draft => new GenerationRunManifestItem(
                            draft.ItemId,
                            tasksByItemId[draft.ItemId].Id,
                            draft.Description,
                            draft.RowIndex,
                            draft.VariantIndex,
                            draft.VariantCount,
                            draft.ModelOutputPath,
                            draft.NormalizedPath,
                            draft.ProcessedPath,
                            draft.TargetPath)).ToArray()))
                .ConfigureAwait(true);

            _trackedBatchIds.Add(sourceBatchId);
            _trackedBatchIds.Add(retryBatchId);
            _currentBatchId = retryBatchId;
            var refreshedParents = await _taskStore.ListBatchAsync(sourceBatchId).ConfigureAwait(true);
            ApplyBatch(
                refreshedParents.Concat(result.Tasks).ToArray(),
                itemDescriptions,
                forceStatusMessage: true,
                mergeWithExisting: true);
            _remainingTerminalArtifactPolls = TerminalArtifactPollCount;
            StatusText =
                $"批次 {ShortId(sourceBatchId)} 有 {result.Tasks.Count} 项未达标，已创建自动重做批次 {ShortId(retryBatchId)}";
            RaiseBatchPublished(retryBatchId, result.Tasks, isRetry: true, dispatchRequested: true);
            _pollTimer.Start();
            return new AutomaticGenerationRetryResult(
                true,
                true,
                retryBatchId,
                result.Tasks.Count,
                $"已创建 {result.Tasks.Count} 个新尝试；上一轮中间产物继续保留。");
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            if (retryStoreCommitted &&
                committedRetryTasks.Count > 0 &&
                !string.IsNullOrWhiteSpace(retryBatchId))
            {
                _trackedBatchIds.Add(sourceBatchId);
                _trackedBatchIds.Add(retryBatchId);
                _currentBatchId = retryBatchId;
                var refreshedParents = await _taskStore.ListBatchAsync(sourceBatchId).ConfigureAwait(true);
                ApplyBatch(
                    refreshedParents.Concat(committedRetryTasks).ToArray(),
                    descriptions: null,
                    forceStatusMessage: true,
                    mergeWithExisting: true);
                _remainingTerminalArtifactPolls = TerminalArtifactPollCount;
                StatusText =
                    $"自动重做任务已持久化，但 Run manifest 写入未完成：{exception.Message}；" +
                    "任务仍将派发，产物路径与任务存储继续作为权威恢复依据。";
                RaiseBatchPublished(
                    retryBatchId,
                    committedRetryTasks,
                    isRetry: true,
                    dispatchRequested: true);
                _pollTimer.Start();
                return new AutomaticGenerationRetryResult(
                    true,
                    true,
                    retryBatchId,
                    committedRetryTasks.Count,
                    $"后继任务已持久化并继续派发；Run manifest 需要稍后修复：{exception.Message}");
            }

            if (runWorkspace is not null)
            {
                _runWorkspaceService.DeleteIfUnpublished(runWorkspace);
            }

            return retryableParents.Length == 0
                ? AutomaticGenerationRetryResult.Blocked($"自动重做准备失败：{exception.Message}")
                : await BlockAutomaticRetryAsync(
                        retryableParents,
                        $"自动重做准备失败：{exception.Message}")
                    .ConfigureAwait(true);
        }
        finally
        {
            _automaticRetryGate.Release();
        }
    }

    private async Task<AutomaticGenerationRetryResult> BlockAutomaticRetryAsync(
        IReadOnlyList<GenerationTask> parents,
        string reason)
    {
        try
        {
            var blocked = await _taskStore.BlockAutomaticRetryAsync(
                    parents.Select(parent => parent.Id).ToArray(),
                    reason)
                .ConfigureAwait(true);
            ApplyBatch(
                blocked,
                descriptions: null,
                forceStatusMessage: true,
                mergeWithExisting: true);
            RefreshManualFallbackState();
            return AutomaticGenerationRetryResult.Blocked(
                $"{reason} 已停止自动重做并开放人工重新生成入口。");
        }
        catch (Exception blockException) when (IsExpectedOperationException(blockException))
        {
            return AutomaticGenerationRetryResult.Blocked(
                $"{reason} 同时无法持久化人工接管状态：{blockException.Message}");
        }
    }

    private AssetGenerationDraftRowViewModel? PrimaryDraftRow => DraftRows.FirstOrDefault();

    private void InitializeDraftRows(
        IEnumerable<AssetGenerationDraftRowSettings>? settingsRows,
        string? legacyContentPrompt,
        string? legacyOutputDirectory)
    {
        var fallbackDescription = string.IsNullOrWhiteSpace(legacyContentPrompt)
            ? "单个游戏场景瓦片"
            : legacyContentPrompt.Trim();
        var fallbackOutput = ResolveInitialOutputDirectory(legacyOutputDirectory);
        var restoredRows = settingsRows?
            .Where(settings => settings is not null)
            .Take(MaximumDraftRowCount)
            .Select(settings => new AssetGenerationDraftRowViewModel(
                settings.Id,
                string.IsNullOrWhiteSpace(settings.Description)
                    ? fallbackDescription
                    : settings.Description,
                settings.Quantity is >= 1 and <= GenerationInputValidator.MaximumOutputCount
                    ? settings.Quantity
                    : 1,
                ResolveInitialOutputDirectory(settings.OutputPath ?? fallbackOutput)))
            .ToArray() ?? [];

        if (restoredRows.Length == 0)
        {
            restoredRows =
            [
                new AssetGenerationDraftRowViewModel(
                    Guid.NewGuid().ToString("N"),
                    fallbackDescription,
                    1,
                    fallbackOutput)
            ];
        }

        foreach (var row in restoredRows)
        {
            AttachDraftRow(row);
            DraftRows.Add(row);
        }

        SelectedDraftRow = DraftRows[0];
        RefreshDraftCollectionState(notifySettings: false);
    }

    private void AttachDraftRow(AssetGenerationDraftRowViewModel row)
    {
        row.Changed += OnDraftRowChanged;
        ValidateDraftRow(row);
    }

    private void OnDraftRowChanged(object? sender, EventArgs e)
    {
        if (sender is not AssetGenerationDraftRowViewModel row)
        {
            return;
        }

        ValidateDraftRow(row);
        if (ReferenceEquals(row, SelectedDraftRow) || ReferenceEquals(row, PrimaryDraftRow))
        {
            OnPropertyChanged(nameof(ContentPrompt));
            OnPropertyChanged(nameof(OutputDirectory));
            OnPropertyChanged(nameof(OutputDirectoryHint));
        }

        RefreshDraftCollectionState();
    }

    private void ValidateDraftRow(AssetGenerationDraftRowViewModel row)
    {
        var description = row.Description?.Trim() ?? string.Empty;
        if (description.Length == 0)
        {
            row.ValidationMessage = "物品描述不能为空";
        }
        else if (description.Length > 16_384 || description.Any(character => character == '\0'))
        {
            row.ValidationMessage = "物品描述过长或包含无效字符";
        }
        else if (row.Quantity is < 1 or > GenerationInputValidator.MaximumOutputCount)
        {
            row.ValidationMessage =
                $"单行数量必须位于 1 到 {GenerationInputValidator.MaximumOutputCount} 之间；更多内容请拆成多行";
        }
        else if (!TryValidateOutputDirectory(row.OutputDirectory, out var normalized, out var reason))
        {
            row.ValidationMessage = reason;
            row.OutputDirectoryHint = "输出目录不可用";
            return;
        }
        else
        {
            row.ValidationMessage = string.Empty;
            var relative = Path.GetRelativePath(_projectBrowser.RootPath, normalized).Replace('\\', '/');
            row.OutputDirectoryHint = relative == "." ? "Assets/" : $"Assets/{relative}";
            return;
        }

        row.OutputDirectoryHint = string.Empty;
    }

    private void RefreshDraftCollectionState(bool notifySettings = true)
    {
        foreach (var row in DraftRows)
        {
            ValidateDraftRow(row);
        }

        OnPropertyChanged(nameof(TotalRequestedCount));
        OnPropertyChanged(nameof(DraftRowCount));
        OnPropertyChanged(nameof(CanAddDraftRow));
        OnPropertyChanged(nameof(BatchSummaryText));
        OnPropertyChanged(nameof(CanCreateTask));
        OnPropertyChanged(nameof(CanStartGeneration));
        ConstraintPreview = BuildConstraintPreview();
        RaiseCommandStates();
        if (notifySettings)
        {
            NotifySettingsChanged();
        }
    }

    private string BuildBatchStatusText()
    {
        var active = PublishedTasks.Where(task => !task.IsSuperseded).ToArray();
        if (active.Length == 0)
        {
            return _currentTask is null ? "尚未发布生成批次" : StatusTextForLegacyTask(_currentTask);
        }

        if (active.All(task => task.Status == GenerationTaskStatus.Completed))
        {
            return "批次全部完成";
        }

        if (active.Any(task => task.HasAutomaticRetryRemaining))
        {
            return "批次存在未达标项 · 自动重做中";
        }

        if (active.All(task => task.Status == GenerationTaskStatus.Failed))
        {
            return "批次全部失败";
        }

        if (active.All(task => task.Status is GenerationTaskStatus.Completed or GenerationTaskStatus.Failed))
        {
            return "批次部分完成";
        }

        if (active.Any(task => task.Status == GenerationTaskStatus.Claimed))
        {
            return "批次制作中";
        }

        if (active.Any(task => task.Status is GenerationTaskStatus.Completed or GenerationTaskStatus.Failed))
        {
            return "批次部分完成，仍有任务等待";
        }

        return "批次等待 Agent 调度";
    }

    private string BuildBatchProgressText()
    {
        var active = PublishedTasks.Where(task => !task.IsSuperseded).ToArray();
        if (active.Length == 0)
        {
            return _currentTask is null
                ? "发布后将在这里汇总每一行的 Pending、Claimed、Completed 与 Failed 状态。"
                : DetailTextForLegacyTask(_currentTask);
        }

        var completedRows = active.Count(task => task.Status == GenerationTaskStatus.Completed);
        var retryingRows = active.Count(task => task.HasAutomaticRetryRemaining);
        var failedRows = active.Count(task =>
            task.Status == GenerationTaskStatus.Failed && !task.HasAutomaticRetryRemaining);
        var completedOutputs = active.Sum(task => task.CompletedOutputCount);
        var generatedOutputs = active.Sum(task => task.GeneratedOutputCount);
        var requestedOutputs = active.Sum(task => task.RequestedCount);
        return $"{completedRows}/{active.Length} 行完成 · {generatedOutputs}/{requestedOutputs} 张已落盘 · " +
               $"{completedOutputs}/{requestedOutputs} 张已完成登记" +
               (retryingRows == 0 ? string.Empty : $" · {retryingRows} 行正在自动重做") +
               (failedRows == 0 ? string.Empty : $" · {failedRows} 行失败可单独重试");
    }

    private void BeginDispatchProgress(
        string batchId,
        GenerationBatchDispatchStage stage,
        double progress,
        string instruction,
        string detail)
    {
        EnsureDispatchBatch(batchId);
        ApplyDispatchProgress(batchId, stage, progress, instruction, detail);
    }

    private void EnsureDispatchBatch(string batchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        var normalized = batchId.Trim();
        if (string.Equals(_currentDispatchBatchId, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _currentDispatchBatchId = normalized;
        _currentDispatchStage = GenerationBatchDispatchStage.Idle;
        _dispatchStageStartedAtUtc = null;
        _dispatchStageDurations.Clear();
        _dispatchStartedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        _lastAgentActivityUtc = null;
        _lastOutputActivityUtc = null;
        _lastAgentFeedbackCoalesceKey = null;
        _observedOutputPaths.Clear();
        AgentFeedbackEntries.Clear();
        AgentFeedbackStatusText = "等待 Agent 首次回传";
        HasAgentGenerationRisk = false;
        AgentGenerationRiskText = string.Empty;
        DispatchProgress = 0d;
        CurrentInstruction = "尚未开始调度";
        DispatchStageDetail = "批次已建立，等待进入调度步骤。";
        FailureStageText = string.Empty;
        DispatchFailureText = string.Empty;
        IsDispatching = false;
        OnPropertyChanged(nameof(CurrentDispatchStageText));
        OnPropertyChanged(nameof(HasDispatchProgress));
        UpdateAgentHealthStatus();
    }

    private bool IsCurrentDispatchBatch(string? batchId) =>
        !string.IsNullOrWhiteSpace(batchId) &&
        string.Equals(_currentDispatchBatchId, batchId.Trim(), StringComparison.Ordinal);

    private void ApplyDispatchProgress(
        string batchId,
        GenerationBatchDispatchStage stage,
        double progress,
        string instruction,
        string detail,
        GenerationBatchDispatchStage? failureStage = null,
        string? failureMessage = null)
    {
        if (!IsCurrentDispatchBatch(batchId))
        {
            return;
        }

        var previousStage = _currentDispatchStage;
        var observedAtUtc = GetUtcNow();
        if (stage != previousStage)
        {
            if (_dispatchStageStartedAtUtc is { } stageStartedAtUtc &&
                previousStage is not GenerationBatchDispatchStage.Idle)
            {
                var elapsed = observedAtUtc - stageStartedAtUtc;
                if (!_dispatchStageDurations.TryGetValue(previousStage, out var previousElapsed) ||
                    elapsed > previousElapsed)
                {
                    _dispatchStageDurations[previousStage] = elapsed;
                }
            }

            _dispatchStageStartedAtUtc = observedAtUtc;
        }
        var normalizedProgress = Math.Clamp(progress, 0d, 100d);
        if (stage == GenerationBatchDispatchStage.Completed)
        {
            normalizedProgress = 100d;
        }
        else
        {
            normalizedProgress = Math.Max(DispatchProgress, normalizedProgress);
        }

        _currentDispatchStage = stage;
        DispatchProgress = normalizedProgress;
        CurrentInstruction = string.IsNullOrWhiteSpace(instruction)
            ? "等待下一条调度指令"
            : instruction.Trim();
        DispatchStageDetail = string.IsNullOrWhiteSpace(detail)
            ? "当前步骤未提供补充说明。"
            : detail.Trim();

        var resolvedFailureStage = failureStage ??
                                   (stage == GenerationBatchDispatchStage.Failed
                                       ? previousStage == GenerationBatchDispatchStage.Failed
                                           ? null
                                           : previousStage
                                       : null);
        if (resolvedFailureStage is not null)
        {
            FailureStageText = BuildDispatchStageText(resolvedFailureStage.Value);
        }
        else if (stage != GenerationBatchDispatchStage.Failed ||
                 previousStage != GenerationBatchDispatchStage.Failed)
        {
            FailureStageText = string.Empty;
        }
        DispatchFailureText = string.IsNullOrWhiteSpace(failureMessage)
            ? string.Empty
            : failureMessage.Trim();
        IsDispatching = stage is not GenerationBatchDispatchStage.Idle and
            not GenerationBatchDispatchStage.Completed and
            not GenerationBatchDispatchStage.Failed and
            not GenerationBatchDispatchStage.Canceled;

        OnPropertyChanged(nameof(CurrentDispatchStageText));
        OnPropertyChanged(nameof(CurrentStageElapsedText));
        OnPropertyChanged(nameof(LongestStageText));
        OnPropertyChanged(nameof(HasDispatchProgress));
        UpdateAgentHealthStatus();
    }

    private static string BuildDispatchStageText(GenerationBatchDispatchStage stage) => stage switch
    {
        GenerationBatchDispatchStage.PreparingConstraints => "准备生成约束",
        GenerationBatchDispatchStage.FreezingStyleReferences => "冻结风格参考",
        GenerationBatchDispatchStage.PersistingBatch => "创建本地批次",
        GenerationBatchDispatchStage.Queued => "等待调度",
        GenerationBatchDispatchStage.BuildingAgentInstruction => "构建 Agent 指令",
        GenerationBatchDispatchStage.StartingAgentTurn => "启动 Agent Turn",
        GenerationBatchDispatchStage.WaitingForTaskClaim => "等待 MCP 认领",
        GenerationBatchDispatchStage.GeneratingAssets => "生成美术资产",
        GenerationBatchDispatchStage.VerifyingOutputs => "校验生成产物",
        GenerationBatchDispatchStage.Completed => "批次已完成",
        GenerationBatchDispatchStage.Failed => "批次执行失败",
        GenerationBatchDispatchStage.Canceled => "批次调度已取消",
        _ => "尚未开始"
    };

    private static string StatusTextForLegacyTask(GenerationTask task) => task.Status switch
    {
        GenerationTaskStatus.Pending => "等待 Agent 认领",
        GenerationTaskStatus.Claimed => $"正在由 {task.ClaimedBy} 生成",
        GenerationTaskStatus.Completed => "生成完成",
        GenerationTaskStatus.Failed => "生成失败",
        _ => "尚未提交生成任务"
    };

    private static string DetailTextForLegacyTask(GenerationTask task) => task.Status switch
    {
        GenerationTaskStatus.Pending => "任务已经写入共享队列。",
        GenerationTaskStatus.Claimed => $"认领时间：{task.ClaimedAtUtc?.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
        GenerationTaskStatus.Completed =>
            $"完成时间：{task.CompletedAtUtc?.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {task.RelativeAssetPaths.Length} 个产物",
        GenerationTaskStatus.Failed => task.FailureMessage ?? "Agent 未提供失败原因",
        _ => string.Empty
    };

    private static string ShortId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= 8 ? normalized : normalized[..8];
    }

    private static int NormalizeParallelism(int value) =>
        Math.Clamp(value, 1, Math.Min(8, GenerationInputValidator.MaximumOutputCount));

    private static int NormalizeAutoRetryCount(int value) =>
        Math.Clamp(value, 0, MaximumAutoRetryCount);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pollTimer.Stop();
        _pollTimer.Tick -= OnPollTimerTick;
        UnsubscribeStyleGroup(_selectedStyleGroup);
        _styleLibrary.PropertyChanged -= OnStyleLibraryPropertyChanged;
        _tilemap.PropertyChanged -= OnTilemapPropertyChanged;
        _agentConnection.PropertyChanged -= OnAgentConnectionPropertyChanged;
        _agentConnection.AgentActivityReceived -= OnAgentActivityReceived;
        _backgroundRemoval.PropertyChanged -= OnBackgroundRemovalPropertyChanged;
        _projectBrowser.CurrentPathChanged -= OnProjectCurrentPathChanged;
        _projectBrowser.ConfiguredRootPathChanged -= OnProjectRootPathChanged;
        foreach (var row in DraftRows)
        {
            row.Changed -= OnDraftRowChanged;
        }
    }

    private async Task StartGenerationAsync()
    {
        if (!CanStartGeneration)
        {
            StatusText = BuildStartGenerationPrerequisiteMessage();
            return;
        }

        await CreateTaskAsync(requestDispatch: true).ConfigureAwait(true);
    }

    private async Task CreateTaskAsync(bool requestDispatch)
    {
        var canPublish = requestDispatch ? CanStartGeneration : CanCreateTask;
        if (!canPublish || SelectedStyleGroup is null)
        {
            StatusText = requestDispatch
                ? BuildStartGenerationPrerequisiteMessage()
                : BuildPrerequisiteMessage();
            return;
        }

        IsBusy = true;
        GenerationReferenceSnapshot? snapshot = null;
        PreparedGenerationRunWorkspace? runWorkspace = null;
        var batchCreated = false;
        string? dispatchBatchId = null;
        try
        {
            var batchId = Guid.NewGuid().ToString("N");
            dispatchBatchId = requestDispatch ? batchId : null;
            if (requestDispatch)
            {
                BeginDispatchProgress(
                    batchId,
                    GenerationBatchDispatchStage.PreparingConstraints,
                    5d,
                    "读取 Tilemap 参数并校验批量描述、数量与输出目录",
                    $"正在准备 {DraftRows.Count} 个任务、{TotalRequestedCount} 个预期产物的冻结约束。");
            }

            var useUnifiedPrompt = TryGetFreshUnifiedPrompt(
                SelectedStyleGroup,
                out var unifiedPromptSample);
            var sourceReferences = useUnifiedPrompt
                ? []
                : SelectedStyleGroup.Assets
                    .Where(asset => !asset.IsMissing)
                    .Select(asset => new GenerationReferenceSource(
                        asset.Id,
                        asset.Name,
                        asset.FullPath,
                        asset.Sha256,
                        asset.MediaType))
                    .ToArray();
            if (requestDispatch)
            {
                ApplyDispatchProgress(
                    batchId,
                    GenerationBatchDispatchStage.FreezingStyleReferences,
                    15d,
                    "GenerationReferenceSnapshotService.CaptureAsync(batchId, references)",
                    useUnifiedPrompt
                        ? "已冻结新鲜统一提示词样本；本批次明确跳过参考图校验与携带。"
                        : $"正在复制并校验 {sourceReferences.Length} 张风格参考图快照。");
            }

            snapshot = await _snapshotService.CaptureAsync(batchId, sourceReferences)
                .ConfigureAwait(true);
            var styleSelection = new MaterialGenerationStyleSelection
            {
                GroupId = SelectedStyleGroup.Id,
                Name = SelectedStyleGroup.Name,
                Description = useUnifiedPrompt ? string.Empty : SelectedStyleGroup.Description,
                Prompt = useUnifiedPrompt
                    ? unifiedPromptSample!.Prompt
                    : SelectedStyleGroup.StylePrompt,
                SourceMode = useUnifiedPrompt
                    ? MaterialGenerationConstraintComposer.UnifiedPromptOnlySourceMode
                    : null,
                SourceFingerprint = useUnifiedPrompt
                    ? unifiedPromptSample!.SourceFingerprint
                    : null,
                GeneratedAtUtc = useUnifiedPrompt
                    ? unifiedPromptSample!.GeneratedAtUtc
                    : null,
                References = snapshot.References.ToArray()
            };
            var stylePrompt = useUnifiedPrompt
                ? unifiedPromptSample!.Prompt.Trim()
                : BuildLegacyStylePrompt(SelectedStyleGroup);
            var backgroundRemoval = BackgroundRemoval.CreateGenerationSpec();
            var assetRoot = Path.GetFullPath(_projectBrowser.RootPath);
            runWorkspace = _runWorkspaceService.Prepare(batchId, assetRoot);
            await _runWorkspaceService.MirrorStyleReferencesAsync(runWorkspace, snapshot)
                .ConfigureAwait(true);
            var reservedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var itemDescriptions = new Dictionary<string, string>(StringComparer.Ordinal);
            var items = new List<CreateGenerationBatchItemRequest>(TotalRequestedCount);
            var manifestDrafts = new List<GenerationRunManifestDraft>(TotalRequestedCount);

            for (var rowIndex = 0; rowIndex < DraftRows.Count; rowIndex++)
            {
                var row = DraftRows[rowIndex];
                if (!TryValidateOutputDirectory(row.OutputDirectory, out var normalizedOutput, out var outputReason))
                {
                    throw new InvalidOperationException($"第 {rowIndex + 1} 行：{outputReason}");
                }

                var relativeOutputDirectory = Path.GetRelativePath(assetRoot, normalizedOutput)
                    .Replace('\\', '/');
                if (relativeOutputDirectory == ".")
                {
                    relativeOutputDirectory = string.Empty;
                }

                var finalOutputDirectory = string.IsNullOrEmpty(relativeOutputDirectory)
                    ? $"Generated/Tilemap/{batchId}/Final"
                    : $"{relativeOutputDirectory}/Tilemap/{batchId}/Final";
                var finalOutputFullPath = Path.GetFullPath(Path.Combine(
                    assetRoot,
                    finalOutputDirectory.Replace('/', Path.DirectorySeparatorChar)));
                if (!AssetRootService.IsWithinRoot(assetRoot, finalOutputFullPath))
                {
                    throw new InvalidDataException($"第 {rowIndex + 1} 行最终分类目录越出了当前 Assets：{finalOutputDirectory}");
                }

                Directory.CreateDirectory(finalOutputFullPath);
                for (var variantIndex = 0; variantIndex < row.Quantity; variantIndex++)
                {
                    var itemId = Guid.NewGuid().ToString("N");
                    var suggestedBaseName =
                        $"tilemap_{batchId[..8]}_r{rowIndex + 1:D2}_v{variantIndex + 1:D3}";
                    var targetPath = $"{finalOutputDirectory}/{suggestedBaseName}.png";
                    if (!reservedTargets.Add(targetPath))
                    {
                        throw new InvalidDataException($"批次中出现重复目标路径：{targetPath}");
                    }

                    var targetFullPath = Path.GetFullPath(Path.Combine(
                        assetRoot,
                        targetPath.Replace('/', Path.DirectorySeparatorChar)));
                    if (!AssetRootService.IsWithinRoot(assetRoot, targetFullPath))
                    {
                        throw new InvalidDataException($"生成目标路径越出了当前 Assets 根目录：{targetPath}");
                    }

                    if (File.Exists(targetFullPath) || Directory.Exists(targetFullPath))
                    {
                        throw new IOException($"生成目标已经存在：{targetFullPath}");
                    }

                    var basePackage = _tilemap.BuildGenerationConstraints(
                        row.Description.Trim(),
                        stylePrompt,
                        string.IsNullOrWhiteSpace(NegativePrompt) ? null : NegativePrompt.Trim(),
                        1,
                        backgroundRemoval);
                    basePackage = basePackage with
                    {
                        Output = basePackage.Output with { MaxParallelism = 1 }
                    };
                    var workspacePlan = _runWorkspaceService.CreateOutputPlan(
                        runWorkspace,
                        itemId,
                        suggestedBaseName);
                    var normalizedPath = _runWorkspaceService.CreateNormalizedPath(
                        runWorkspace,
                        itemId,
                        suggestedBaseName);
                    var constraints = MaterialGenerationConstraintComposer.Compose(
                        basePackage,
                        styleSelection,
                        new GenerationDeliveryPlan
                        {
                            AssetRoot = assetRoot,
                            OutputDirectory = finalOutputDirectory,
                            TargetPaths = [targetPath],
                            StagingPaths = [normalizedPath],
                            SuggestedBaseName = suggestedBaseName,
                            Workspace = workspacePlan
                        });
                    await _runWorkspaceService.WritePromptAsync(
                            runWorkspace,
                            workspacePlan,
                            constraints)
                        .ConfigureAwait(true);
                    itemDescriptions[itemId] = row.Description.Trim();
                    items.Add(new CreateGenerationBatchItemRequest
                    {
                        ItemId = itemId,
                        DisplayName = $"{BuildDisplayName(row.Description)} · {variantIndex + 1}/{row.Quantity}",
                        MaxAttempts = MaxGenerationAttempts,
                        Constraints = constraints
                    });
                    manifestDrafts.Add(new GenerationRunManifestDraft(
                        itemId,
                        row.Description.Trim(),
                        rowIndex,
                        variantIndex,
                        row.Quantity,
                        workspacePlan.ModelOutputPaths[0],
                        normalizedPath,
                        workspacePlan.ProcessedPaths[0],
                        targetPath));
                }
            }

            if (requestDispatch)
            {
                ApplyDispatchProgress(
                    batchId,
                    GenerationBatchDispatchStage.PersistingBatch,
                    25d,
                    "IGenerationTaskStore.CreateBatchAsync(request)",
                    $"正在把 {items.Count} 个单图任务及固定项目内运行路径原子写入本地队列。");
            }

            var tasks = await _taskStore.CreateBatchAsync(new CreateGenerationBatchRequest
            {
                BatchId = batchId,
                DisplayName = $"Tilemap 批次 · {DraftRows.Count} 种 · {TotalRequestedCount} 张",
                RequestedBy = "GAssetForge Desktop",
                DispatchRequested = requestDispatch,
                Items = items.ToArray()
            }).ConfigureAwait(true);
            batchCreated = true;
            var tasksByItemId = tasks.ToDictionary(task => task.BatchItemId!, StringComparer.Ordinal);
            await _runWorkspaceService.WriteManifestAsync(
                    runWorkspace,
                    new GenerationRunManifest(
                        1,
                        batchId,
                        batchId,
                        assetRoot,
                        SelectedGenerationStep.Key,
                        DateTimeOffset.UtcNow,
                        SelectedStyleGroup.Id,
                        SelectedStyleGroup.Name,
                        manifestDrafts.Select(draft => new GenerationRunManifestItem(
                            draft.ItemId,
                            tasksByItemId[draft.ItemId].Id,
                            draft.Description,
                            draft.RowIndex,
                            draft.VariantIndex,
                            draft.VariantCount,
                            draft.ModelOutputPath,
                            draft.NormalizedPath,
                            draft.ProcessedPath,
                            draft.TargetPath)).ToArray()))
                .ConfigureAwait(true);
            _trackedBatchIds.Clear();
            _trackedBatchIds.Add(batchId);
            _lastAppliedTaskRevisions.Clear();
            PublishedTasks.Clear();
            GeneratedAssets.Clear();
            _observedOutputPaths.Clear();
            _observedArtifactPaths.Clear();
            _remainingTerminalArtifactPolls = TerminalArtifactPollCount;
            _currentBatchId = null;
            ApplyBatch(tasks, itemDescriptions, forceStatusMessage: true, mergeWithExisting: false);
            if (requestDispatch)
            {
                StatusText =
                    $"批次 {batchId[..8]} 已发布，共 {tasks.Count} 个任务、{TotalRequestedCount} 个产物；正在请求应用内调度";
                RaiseBatchPublished(batchId, tasks, isRetry: false, dispatchRequested: true);
            }
            else
            {
                StatusText =
                    $"批次 {batchId[..8]} 已仅发布到本地队列，共 {tasks.Count} 个任务、{TotalRequestedCount} 个产物";
                RaiseBatchPublished(batchId, tasks, isRetry: false, dispatchRequested: false);
            }

            _pollTimer.Start();
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            if (!batchCreated && snapshot is not null)
            {
                try
                {
                    await _snapshotService.DeleteAsync(snapshot).ConfigureAwait(true);
                }
                catch
                {
                    // The original task creation failure is more actionable than cleanup failure.
                }
            }

            if (!batchCreated && runWorkspace is not null)
            {
                _runWorkspaceService.DeleteIfUnpublished(runWorkspace);
            }

            StatusText = $"无法发布批量生成任务：{exception.Message}";
            if (requestDispatch && !string.IsNullOrWhiteSpace(dispatchBatchId))
            {
                ReportDispatchFailure(dispatchBatchId, $"批次准备或落盘失败：{exception.Message}");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async void RetryFailedTask(PublishedGenerationTaskViewModel? failedItem)
    {
        if (failedItem?.CanRetry != true || IsBusy || IsDispatching)
        {
            return;
        }

        if (!IsAgentConnected)
        {
            StatusText = "Agent 链路尚未达到可执行状态，失败项未创建重试任务；请连接完成后重试";
            return;
        }

        IsBusy = true;
        string? retryDispatchBatchId = null;
        PreparedGenerationRunWorkspace? runWorkspace = null;
        var batchCreated = false;
        try
        {
            var failedTask = failedItem.Task;
            if (!MaterialGenerationConstraintComposer.TryReadDelivery(
                    failedTask.Constraints,
                    out var previousDelivery) ||
                previousDelivery is null)
            {
                throw new InvalidDataException("失败任务缺少可重试的固定交付计划。");
            }

            var retryBatchId = Guid.NewGuid().ToString("N");
            retryDispatchBatchId = retryBatchId;
            runWorkspace = _runWorkspaceService.Prepare(retryBatchId, previousDelivery.AssetRoot);
            await _runWorkspaceService.MirrorExistingRunReferencesAsync(runWorkspace, previousDelivery)
                .ConfigureAwait(true);
            BeginDispatchProgress(
                retryBatchId,
                GenerationBatchDispatchStage.PreparingConstraints,
                5d,
                "读取失败任务的冻结约束并创建独立重试计划",
                $"正在为失败任务 {ShortId(failedTask.Id)} 分配新的交付路径。");
            var itemDescriptions = new Dictionary<string, string>(StringComparer.Ordinal);
            var retryItems = new List<CreateGenerationBatchItemRequest>(failedTask.Constraints.Output.Count);
            var manifestDrafts = new List<GenerationRunManifestDraft>(failedTask.Constraints.Output.Count);
            for (var outputIndex = 0; outputIndex < failedTask.Constraints.Output.Count; outputIndex++)
            {
                var itemId = Guid.NewGuid().ToString("N");
                var suggestedBaseName =
                    $"tilemap_{retryBatchId[..8]}_retry_v{outputIndex + 1:D3}";
                var targetPath = string.IsNullOrEmpty(previousDelivery.OutputDirectory)
                    ? $"Generated/Tilemap/{retryBatchId}/Final/{suggestedBaseName}.png"
                    : $"{previousDelivery.OutputDirectory}/{suggestedBaseName}.png";
                var fullPath = Path.GetFullPath(Path.Combine(
                    previousDelivery.AssetRoot,
                    targetPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!AssetRootService.IsWithinRoot(previousDelivery.AssetRoot, fullPath))
                {
                    throw new InvalidDataException($"重试目标越出了原任务 Assets 根目录：{targetPath}");
                }

                if (File.Exists(fullPath) || Directory.Exists(fullPath))
                {
                    throw new IOException($"重试目标已经存在：{fullPath}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                var singleOutputConstraints = failedTask.Constraints with
                {
                    Output = failedTask.Constraints.Output with { Count = 1, MaxParallelism = 1 }
                };
                var workspacePlan = _runWorkspaceService.CreateOutputPlan(
                    runWorkspace,
                    itemId,
                    suggestedBaseName);
                var normalizedPath = _runWorkspaceService.CreateNormalizedPath(
                    runWorkspace,
                    itemId,
                    suggestedBaseName);
                var outputDirectory = Path.GetDirectoryName(targetPath.Replace('/', Path.DirectorySeparatorChar))
                    ?.Replace('\\', '/') ?? string.Empty;
                var constraints = MaterialGenerationConstraintComposer.ReplaceDelivery(
                    singleOutputConstraints,
                    new GenerationDeliveryPlan
                    {
                        AssetRoot = previousDelivery.AssetRoot,
                        OutputDirectory = outputDirectory,
                        TargetPaths = [targetPath],
                        StagingPaths = [normalizedPath],
                        SuggestedBaseName = suggestedBaseName,
                        Workspace = workspacePlan
                    });
                await _runWorkspaceService.WritePromptAsync(runWorkspace, workspacePlan, constraints)
                    .ConfigureAwait(true);
                itemDescriptions[itemId] = failedItem.Description;
                retryItems.Add(new CreateGenerationBatchItemRequest
                {
                    ItemId = itemId,
                    DisplayName = $"{failedTask.DisplayName} · 重试 {outputIndex + 1}/{failedTask.Constraints.Output.Count}",
                    MaxAttempts = MaxGenerationAttempts,
                    Constraints = constraints
                });
                manifestDrafts.Add(new GenerationRunManifestDraft(
                    itemId,
                    failedItem.Description,
                    0,
                    outputIndex,
                    failedTask.Constraints.Output.Count,
                    workspacePlan.ModelOutputPaths[0],
                    normalizedPath,
                    workspacePlan.ProcessedPaths[0],
                    targetPath));
            }

            ApplyDispatchProgress(
                retryBatchId,
                GenerationBatchDispatchStage.PersistingBatch,
                25d,
                "IGenerationTaskStore.CreateBatchAsync(retryRequest)",
                $"正在持久化 {retryItems.Count} 个单图重试任务；原失败任务继续保留用于审计。");
            var tasks = await _taskStore.CreateBatchAsync(new CreateGenerationBatchRequest
            {
                BatchId = retryBatchId,
                DisplayName = $"重试 · {failedItem.Description}",
                RequestedBy = "GAssetForge Desktop",
                DispatchRequested = true,
                IsRetryDispatch = true,
                Items = retryItems.ToArray()
            }).ConfigureAwait(true);
            batchCreated = true;
            var tasksByItemId = tasks.ToDictionary(task => task.BatchItemId!, StringComparer.Ordinal);
            await _runWorkspaceService.WriteManifestAsync(
                    runWorkspace,
                    new GenerationRunManifest(
                        1,
                        retryBatchId,
                        retryBatchId,
                        previousDelivery.AssetRoot,
                        failedTask.Constraints.WorkflowId,
                        DateTimeOffset.UtcNow,
                        "retry",
                        "冻结自原失败任务",
                        manifestDrafts.Select(draft => new GenerationRunManifestItem(
                            draft.ItemId,
                            tasksByItemId[draft.ItemId].Id,
                            draft.Description,
                            draft.RowIndex,
                            draft.VariantIndex,
                            draft.VariantCount,
                            draft.ModelOutputPath,
                            draft.NormalizedPath,
                            draft.ProcessedPath,
                            draft.TargetPath)).ToArray()))
                .ConfigureAwait(true);

            failedItem.MarkSuperseded();
            _trackedBatchIds.Add(retryBatchId);
            ApplyBatch(
                tasks,
                itemDescriptions,
                forceStatusMessage: true,
                mergeWithExisting: true);
            StatusText =
                $"失败项已作为新批次 {retryBatchId[..8]} 重新发布；正在请求应用内调度，原失败任务保留用于审计";
            RaiseBatchPublished(retryBatchId, tasks, isRetry: true, dispatchRequested: true);
            _remainingTerminalArtifactPolls = TerminalArtifactPollCount;
            _pollTimer.Start();
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            if (!batchCreated && runWorkspace is not null)
            {
                _runWorkspaceService.DeleteIfUnpublished(runWorkspace);
            }

            StatusText = $"无法重试失败项：{exception.Message}";
            if (!string.IsNullOrWhiteSpace(retryDispatchBatchId))
            {
                ReportDispatchFailure(retryDispatchBatchId, $"重试任务准备或落盘失败：{exception.Message}");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CreateManualRetryBatchAsync()
    {
        var failedItems = GetCurrentBatchActiveTasks();
        if (!CanCreateManualRetryBatch ||
            failedItems.Length == 0 ||
            failedItems.Any(item => item.Status != GenerationTaskStatus.Failed))
        {
            ManualFallbackStatusText =
                "只有当前批次全部为 Failed 且未在调度时，才能创建不自动启动 Agent Turn 的手动重试批次。";
            return;
        }

        IsBusy = true;
        PreparedGenerationRunWorkspace? runWorkspace = null;
        var batchCreated = false;
        try
        {
            var retryBatchId = Guid.NewGuid().ToString("N");
            var itemDescriptions = new Dictionary<string, string>(StringComparer.Ordinal);
            var retryItems = new List<CreateGenerationBatchItemRequest>();
            var manifestDrafts = new List<GenerationRunManifestDraft>();
            if (!MaterialGenerationConstraintComposer.TryReadDelivery(
                    failedItems[0].Task.Constraints,
                    out var firstDelivery) ||
                firstDelivery is null)
            {
                throw new InvalidDataException("失败批次缺少可重试的固定交付计划。");
            }

            runWorkspace = _runWorkspaceService.Prepare(retryBatchId, firstDelivery.AssetRoot);
            await _runWorkspaceService.MirrorExistingRunReferencesAsync(runWorkspace, firstDelivery)
                .ConfigureAwait(true);
            for (var retryIndex = 0; retryIndex < failedItems.Length; retryIndex++)
            {
                var failedItem = failedItems[retryIndex];
                var failedTask = failedItem.Task;
                if (!MaterialGenerationConstraintComposer.TryReadDelivery(
                        failedTask.Constraints,
                        out var previousDelivery) ||
                    previousDelivery is null)
                {
                    throw new InvalidDataException(
                        $"失败任务 {ShortId(failedTask.Id)} 缺少可重试的固定交付计划。");
                }

                if (!AssetRootService.PathEquals(firstDelivery.AssetRoot, previousDelivery.AssetRoot))
                {
                    throw new InvalidDataException("同一手动重试批次不能跨越不同项目 Assets 根目录。");
                }

                for (var outputIndex = 0; outputIndex < failedTask.Constraints.Output.Count; outputIndex++)
                {
                    var itemId = Guid.NewGuid().ToString("N");
                    var suggestedBaseName =
                        $"tilemap_{retryBatchId[..8]}_manual_r{retryIndex + 1:D2}_v{outputIndex + 1:D3}";
                    var targetPath = string.IsNullOrEmpty(previousDelivery.OutputDirectory)
                        ? $"Generated/Tilemap/{retryBatchId}/Final/{suggestedBaseName}.png"
                        : $"{previousDelivery.OutputDirectory}/{suggestedBaseName}.png";
                    var fullPath = Path.GetFullPath(Path.Combine(
                        previousDelivery.AssetRoot,
                        targetPath.Replace('/', Path.DirectorySeparatorChar)));
                    if (!AssetRootService.IsWithinRoot(previousDelivery.AssetRoot, fullPath))
                    {
                        throw new InvalidDataException(
                            $"手动重试目标越出了原任务 Assets 根目录：{targetPath}");
                    }

                    if (File.Exists(fullPath) || Directory.Exists(fullPath))
                    {
                        throw new IOException($"手动重试目标已经存在：{fullPath}");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    var singleOutputConstraints = failedTask.Constraints with
                    {
                        Output = failedTask.Constraints.Output with { Count = 1, MaxParallelism = 1 }
                    };
                    var workspacePlan = _runWorkspaceService.CreateOutputPlan(
                        runWorkspace,
                        itemId,
                        suggestedBaseName);
                    var normalizedPath = _runWorkspaceService.CreateNormalizedPath(
                        runWorkspace,
                        itemId,
                        suggestedBaseName);
                    var outputDirectory = Path.GetDirectoryName(targetPath.Replace('/', Path.DirectorySeparatorChar))
                        ?.Replace('\\', '/') ?? string.Empty;
                    var constraints = MaterialGenerationConstraintComposer.ReplaceDelivery(
                        singleOutputConstraints,
                        new GenerationDeliveryPlan
                        {
                            AssetRoot = previousDelivery.AssetRoot,
                            OutputDirectory = outputDirectory,
                            TargetPaths = [targetPath],
                            StagingPaths = [normalizedPath],
                            SuggestedBaseName = suggestedBaseName,
                            Workspace = workspacePlan
                        });
                    await _runWorkspaceService.WritePromptAsync(runWorkspace, workspacePlan, constraints)
                        .ConfigureAwait(true);
                    itemDescriptions[itemId] = failedItem.Description;
                    retryItems.Add(new CreateGenerationBatchItemRequest
                    {
                        ItemId = itemId,
                        DisplayName = $"{failedTask.DisplayName} · 手动重试 {outputIndex + 1}/{failedTask.Constraints.Output.Count}",
                        MaxAttempts = MaxGenerationAttempts,
                        Constraints = constraints
                    });
                    manifestDrafts.Add(new GenerationRunManifestDraft(
                        itemId,
                        failedItem.Description,
                        retryIndex,
                        outputIndex,
                        failedTask.Constraints.Output.Count,
                        workspacePlan.ModelOutputPaths[0],
                        normalizedPath,
                        workspacePlan.ProcessedPaths[0],
                        targetPath));
                }
            }

            var tasks = await _taskStore.CreateBatchAsync(new CreateGenerationBatchRequest
            {
                BatchId = retryBatchId,
                DisplayName = $"Codex 手动重试 · {failedItems.Length} 项",
                RequestedBy = "GAssetForge Desktop · Manual Codex Handoff",
                DispatchRequested = false,
                IsRetryDispatch = true,
                Items = retryItems.ToArray()
            }).ConfigureAwait(true);
            batchCreated = true;
            var tasksByItemId = tasks.ToDictionary(task => task.BatchItemId!, StringComparer.Ordinal);
            await _runWorkspaceService.WriteManifestAsync(
                    runWorkspace,
                    new GenerationRunManifest(
                        1,
                        retryBatchId,
                        retryBatchId,
                        firstDelivery.AssetRoot,
                        failedItems[0].Task.Constraints.WorkflowId,
                        DateTimeOffset.UtcNow,
                        "manual-retry",
                        "冻结自失败批次",
                        manifestDrafts.Select(draft => new GenerationRunManifestItem(
                            draft.ItemId,
                            tasksByItemId[draft.ItemId].Id,
                            draft.Description,
                            draft.RowIndex,
                            draft.VariantIndex,
                            draft.VariantCount,
                            draft.ModelOutputPath,
                            draft.NormalizedPath,
                            draft.ProcessedPath,
                            draft.TargetPath)).ToArray()))
                .ConfigureAwait(true);

            foreach (var failedItem in failedItems)
            {
                failedItem.MarkSuperseded();
            }

            _trackedBatchIds.Add(retryBatchId);
            _currentBatchId = retryBatchId;
            EnsureDispatchBatch(retryBatchId);
            ApplyBatch(
                tasks,
                itemDescriptions,
                forceStatusMessage: true,
                mergeWithExisting: true);
            _showCurrentBatchManualPrompt = true;
            RefreshManualFallbackState();
            StatusText =
                $"已创建手动重试批次 {ShortId(retryBatchId)}，共 {tasks.Count} 个新 Pending 任务；不会自动启动 Agent Turn";
            ManualFallbackStatusText =
                "手动重试批次已创建，旧 Failed taskId 已排除；现在可复制当前批次任务指令。";
            RaiseBatchPublished(retryBatchId, tasks, isRetry: true, dispatchRequested: false);
            _remainingTerminalArtifactPolls = TerminalArtifactPollCount;
            _pollTimer.Start();
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            if (!batchCreated && runWorkspace is not null)
            {
                _runWorkspaceService.DeleteIfUnpublished(runWorkspace);
            }

            StatusText = $"无法创建手动重试批次：{exception.Message}";
            ManualFallbackStatusText = StatusText;
        }
        finally
        {
            IsBusy = false;
            RefreshManualFallbackState();
        }
    }

    private PublishedGenerationTaskViewModel[] GetCurrentBatchActiveTasks()
    {
        if (string.IsNullOrWhiteSpace(_currentBatchId))
        {
            return [];
        }

        return PublishedTasks
            .Where(task =>
                !task.IsSuperseded &&
                string.Equals(task.BatchId, _currentBatchId, StringComparison.Ordinal))
            .OrderBy(task => task.BatchItemIndex)
            .ThenBy(task => task.Task.CreatedAtUtc)
            .ThenBy(task => task.TaskId, StringComparer.Ordinal)
            .ToArray();
    }

    private PublishedGenerationTaskViewModel[] GetCurrentBatchPendingTasks() =>
        GetCurrentBatchActiveTasks()
            .Where(task => task.Status == GenerationTaskStatus.Pending)
            .ToArray();

    private void RefreshManualFallbackState()
    {
        if (_showCurrentBatchManualPrompt && !CanCopyCurrentBatchPrompt)
        {
            _showCurrentBatchManualPrompt = false;
        }

        OnPropertyChanged(nameof(CanCopyCodexConnectionPrompt));
        OnPropertyChanged(nameof(CodexConnectionPrompt));
        OnPropertyChanged(nameof(CanCopyCurrentBatchPrompt));
        OnPropertyChanged(nameof(CanCreateManualRetryBatch));
        OnPropertyChanged(nameof(CurrentBatchManualPrompt));
        OnPropertyChanged(nameof(ManualFallbackPromptPreview));
        OnPropertyChanged(nameof(ManualFallbackAvailabilityText));
        _createManualRetryBatchCommand?.RaiseCanExecuteChanged();
    }

    private bool CanRetryFailedTask(PublishedGenerationTaskViewModel? task) =>
        !IsBusy &&
        !IsDispatching &&
        IsAgentConnected &&
        task?.CanRetry == true;

    private void RaiseBatchPublished(
        string batchId,
        IReadOnlyList<GenerationTask> tasks,
        bool isRetry,
        bool dispatchRequested)
    {
        var orderedTaskIds = tasks
            .OrderBy(task => task.BatchItemIndex ?? int.MaxValue)
            .ThenBy(task => task.CreatedAtUtc)
            .Select(task => task.Id)
            .ToArray();
        try
        {
            var handler = BatchPublished;
            if (handler is null)
            {
                if (dispatchRequested)
                {
                    ReportDispatchFailure(batchId, "应用内 Agent 调度器尚未接入");
                }

                return;
            }

            handler.Invoke(
                this,
                new GenerationBatchPublishedEventArgs(
                    batchId,
                    orderedTaskIds,
                    isRetry,
                    dispatchRequested));
        }
        catch (Exception exception)
        {
            if (dispatchRequested)
            {
                ReportDispatchFailure(batchId, exception.Message);
            }
            else
            {
                StatusText = $"批次 {ShortId(batchId)} 已发布，但发布通知失败：{exception.Message}";
            }
        }
    }

    private async Task RefreshCurrentTaskAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_isPolling)
        {
            _refreshRequested = true;
            return;
        }

        _isPolling = true;
        _refreshRequested = false;
        try
        {
            IReadOnlyList<GenerationTask> tasks;
            if (_trackedBatchIds.Count > 0)
            {
                var trackedTasks = new List<GenerationTask>();
                foreach (var batchId in _trackedBatchIds.ToArray())
                {
                    trackedTasks.AddRange(
                        await _taskStore.ListBatchAsync(batchId).ConfigureAwait(true));
                }

                tasks = trackedTasks;
            }
            else if (_currentTask is null)
            {
                var allTasks = await _taskStore.ListAsync().ConfigureAwait(true);
                var latest = allTasks
                    .Where(IsTaskForCurrentProject)
                    .OrderByDescending(candidate => candidate.CreatedAtUtc)
                    .FirstOrDefault();
                if (latest?.BatchId is { Length: > 0 } latestBatchId)
                {
                    tasks = ResolveRelatedTaskChain(allTasks, latest);
                    foreach (var relatedBatchId in tasks
                                 .Select(task => task.BatchId)
                                 .Where(batchId => !string.IsNullOrWhiteSpace(batchId)))
                    {
                        _trackedBatchIds.Add(relatedBatchId!);
                    }

                    _currentBatchId = latestBatchId;
                }
                else
                {
                    tasks = latest is null ? [] : [latest];
                }
            }
            else
            {
                var task = await _taskStore.GetAsync(_currentTask.Id).ConfigureAwait(true);
                tasks = task is null ? [] : [task];
            }

            if (tasks.Count > 0)
            {
                ApplyBatch(tasks, descriptions: null, forceStatusMessage: false, mergeWithExisting: false);
                var activeTasks = PublishedTasks.Where(task => !task.IsSuperseded).ToArray();
                if (activeTasks.Length > 0 &&
                    activeTasks.All(task =>
                        (task.Status is GenerationTaskStatus.Completed or GenerationTaskStatus.Failed) &&
                        !task.HasAutomaticRetryRemaining))
                {
                    if (_remainingTerminalArtifactPolls > 0)
                    {
                        _remainingTerminalArtifactPolls--;
                    }
                    else
                    {
                        _pollTimer.Stop();
                    }
                }
                else
                {
                    _remainingTerminalArtifactPolls = TerminalArtifactPollCount;
                    if (!_pollTimer.IsEnabled)
                    {
                        _pollTimer.Start();
                    }
                }
            }
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"刷新生成任务失败：{exception.Message}";
        }
        finally
        {
            _isPolling = false;
            if (_refreshRequested && !_disposed)
            {
                _refreshRequested = false;
                _ = RefreshCurrentTaskAsync();
            }
        }
    }

    private bool IsTaskForCurrentProject(GenerationTask task) =>
        MaterialGenerationConstraintComposer.TryReadDelivery(task.Constraints, out var delivery) &&
        delivery is not null &&
        AssetRootService.PathEquals(delivery.AssetRoot, _projectBrowser.RootPath);

    private static GenerationTask[] ResolveRelatedTaskChain(
        IReadOnlyList<GenerationTask> allTasks,
        GenerationTask seed)
    {
        var tasksById = allTasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var tasksByBatch = allTasks
            .Where(task => !string.IsNullOrWhiteSpace(task.BatchId))
            .GroupBy(task => task.BatchId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var tasksByRoot = allTasks
            .Where(task => task.RetryLineage is not null)
            .GroupBy(task => task.RetryLineage!.RootTaskId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<GenerationTask>();
        pending.Enqueue(seed);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!selectedIds.Add(current.Id))
            {
                continue;
            }

            if (current.BatchId is { Length: > 0 } batchId &&
                tasksByBatch.TryGetValue(batchId, out var batchTasks))
            {
                foreach (var batchTask in batchTasks)
                {
                    pending.Enqueue(batchTask);
                }
            }

            if (current.RetryLineage is { } lineage &&
                tasksByRoot.TryGetValue(lineage.RootTaskId, out var rootTasks))
            {
                foreach (var rootTask in rootTasks)
                {
                    pending.Enqueue(rootTask);
                }

                if (lineage.PreviousTaskId is { } previousTaskId &&
                    tasksById.TryGetValue(previousTaskId, out var previousTask))
                {
                    pending.Enqueue(previousTask);
                }
            }

            if (current.SupersededByTaskId is { } childTaskId &&
                tasksById.TryGetValue(childTaskId, out var childTask))
            {
                pending.Enqueue(childTask);
            }
        }

        return allTasks
            .Where(task => selectedIds.Contains(task.Id))
            .OrderBy(task => task.CreatedAtUtc)
            .ThenBy(task => task.BatchItemIndex)
            .ThenBy(task => task.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed record BatchOutputTarget(
        string TaskId,
        string RelativePath,
        string FullPath,
        bool Exists,
        int GlobalIndex);

    private sealed record BatchArtifactTarget(
        GeneratedAssetStage Stage,
        string RelativePath,
        string FullPath,
        bool Exists);

    private static IReadOnlyList<BatchArtifactTarget> BuildArtifactTargets(GenerationDeliveryPlan delivery)
    {
        var candidates = new List<(GeneratedAssetStage Stage, string RelativePath)>();
        if (delivery.Workspace is not null)
        {
            candidates.AddRange(delivery.Workspace.ModelOutputPaths.Select(path =>
                (GeneratedAssetStage.ModelOutput, path)));
        }

        candidates.AddRange(delivery.StagingPaths.Select(path =>
            (GeneratedAssetStage.NormalizedInput, path)));

        if (delivery.Workspace is not null)
        {
            candidates.AddRange(delivery.Workspace.ProcessedPaths.Select(path =>
                (GeneratedAssetStage.BackgroundRemoved, path)));
        }

        candidates.AddRange(delivery.TargetPaths.Select(path =>
            (GeneratedAssetStage.Final, path)));

        var results = new List<BatchArtifactTarget>(candidates.Count);
        var observedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            try
            {
                var relativePath = candidate.RelativePath.Replace('\\', '/');
                var fullPath = Path.GetFullPath(Path.Combine(
                    delivery.AssetRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!AssetRootService.IsWithinRoot(delivery.AssetRoot, fullPath) ||
                    !observedPaths.Add(fullPath))
                {
                    continue;
                }

                results.Add(new BatchArtifactTarget(
                    candidate.Stage,
                    relativePath,
                    fullPath,
                    File.Exists(fullPath)));
            }
            catch (Exception exception) when (exception is
                ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Corrupt legacy delivery paths must not break status polling for the whole batch.
            }
        }

        return results;
    }

    private static string BuildArtifactStageText(GeneratedAssetStage stage) => stage switch
    {
        GeneratedAssetStage.ModelOutput => "模型原图",
        GeneratedAssetStage.NormalizedInput => "画布归一化",
        GeneratedAssetStage.BackgroundRemoved => "背景处理",
        GeneratedAssetStage.Final => "最终产物",
        _ => "生成产物"
    };

    private void ApplyImageProgress(IReadOnlyList<BatchOutputTarget> targets)
    {
        var nowUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        var existingTargets = targets.Where(target => target.Exists).ToArray();
        var existingPaths = existingTargets
            .Select(target => target.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newlyObserved = existingTargets
            .Where(target => !_observedOutputPaths.Contains(target.FullPath))
            .OrderBy(target => target.GlobalIndex)
            .ToArray();
        _observedOutputPaths.IntersectWith(existingPaths);
        foreach (var target in newlyObserved)
        {
            _observedOutputPaths.Add(target.FullPath);
        }

        RequestedImageCount = targets.Count;
        GeneratedImageCount = existingTargets.Length;
        VerifiedImageCount = PublishedTasks
            .Where(task => !task.IsSuperseded)
            .Sum(task => task.CompletedOutputCount);
        ImageProgress = RequestedImageCount <= 0
            ? 0d
            : Math.Clamp(GeneratedImageCount * 100d / RequestedImageCount, 0d, 100d);
        OnPropertyChanged(nameof(ImageProgressText));

        var nextTarget = targets.FirstOrDefault(target => !target.Exists);
        if (RequestedImageCount == 0)
        {
            CurrentImageText = "尚未建立图片交付计划";
        }
        else if (nextTarget is null)
        {
            CurrentImageText = $"全部 {RequestedImageCount} 张已落盘 · 等待任务完成登记与本地校验";
        }
        else
        {
            CurrentImageText =
                $"下一待交付：第 {nextTarget.GlobalIndex + 1} / {RequestedImageCount} 张 · " +
                $"{Path.GetFileName(nextTarget.RelativePath)} · 独立任务等待 Agent Host 调度";
        }

        if (newlyObserved.Length > 0 && HasDispatchProgress)
        {
            _lastOutputActivityUtc = nowUtc;
            var cumulative = Math.Max(0, GeneratedImageCount - newlyObserved.Length);
            foreach (var target in newlyObserved)
            {
                cumulative++;
                AddAgentFeedback(
                    nowUtc,
                    "产物",
                    $"已检测落盘 {cumulative}/{RequestedImageCount}：{target.RelativePath}",
                    isWarning: false);
            }
        }
    }

    private void OnAgentActivityReceived(object? sender, AgentActivityEventArgs e)
    {
        if (_disposed || !HasDispatchProgress ||
            _currentDispatchStage is GenerationBatchDispatchStage.Completed or
                GenerationBatchDispatchStage.Failed or
                GenerationBatchDispatchStage.Canceled)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(e.TaskId) &&
            !PublishedTasks.Any(task =>
                !task.IsSuperseded &&
                string.Equals(task.TaskId, e.TaskId, StringComparison.Ordinal)))
        {
            return;
        }

        _lastAgentActivityUtc = e.Timestamp.ToUniversalTime();
        AddAgentFeedback(
            e.Timestamp,
            GenerationAgentHealthEvaluator.CategoryText(e.Category),
            e.Message,
            e.IsWarning,
            e.Category == AgentActivityCategory.AgentMessage &&
            !string.IsNullOrWhiteSpace(e.ItemId)
                ? $"{e.TurnId}/{e.ItemId}/agent-message"
                : null);
        UpdateAgentHealthStatus();
    }

    internal void ReportAgentActivity(
        DateTimeOffset timestamp,
        string category,
        string message,
        bool isWarning = false,
        string? coalesceKey = null)
    {
        if (!HasDispatchProgress || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _lastAgentActivityUtc = timestamp.ToUniversalTime();
        AddAgentFeedback(timestamp, category, message, isWarning, coalesceKey);
        UpdateAgentHealthStatus();
    }

    private void AddAgentFeedback(
        DateTimeOffset timestamp,
        string category,
        string message,
        bool isWarning,
        string? coalesceKey = null)
    {
        var normalizedMessage = NormalizeFeedbackText(message);
        if (normalizedMessage.Length == 0)
        {
            return;
        }

        var normalizedCategory = string.IsNullOrWhiteSpace(category)
            ? "活动"
            : NormalizeFeedbackText(category, 24);
        var entry = new GenerationAgentFeedbackEntry(
            timestamp,
            normalizedCategory,
            normalizedMessage,
            isWarning);

        if (AgentFeedbackEntries.Count > 0 &&
            ((!string.IsNullOrWhiteSpace(coalesceKey) &&
              string.Equals(_lastAgentFeedbackCoalesceKey, coalesceKey, StringComparison.Ordinal)) ||
             (AgentFeedbackEntries[0].CategoryText == entry.CategoryText &&
              AgentFeedbackEntries[0].Message == entry.Message)))
        {
            AgentFeedbackEntries[0] = entry;
        }
        else
        {
            AgentFeedbackEntries.Insert(0, entry);
        }

        _lastAgentFeedbackCoalesceKey = coalesceKey;

        while (AgentFeedbackEntries.Count > MaximumAgentFeedbackEntries)
        {
            AgentFeedbackEntries.RemoveAt(AgentFeedbackEntries.Count - 1);
        }
    }

    private void UpdateAgentHealthStatus()
    {
        var active = HasDispatchProgress &&
                     _currentDispatchStage is not GenerationBatchDispatchStage.Completed and
                         not GenerationBatchDispatchStage.Failed and
                         not GenerationBatchDispatchStage.Canceled;
        var oldestClaimActivityUtc = PublishedTasks
            .Where(task => !task.IsSuperseded && task.Status == GenerationTaskStatus.Claimed)
            .Select(task => task.Task.UpdatedAtUtc)
            .DefaultIfEmpty()
            .Min();
        var health = GenerationAgentHealthEvaluator.Evaluate(
            _timeProvider.GetUtcNow().ToUniversalTime(),
            active,
            _agentConnection.IsConnected,
            _agentConnection.IsTurnRunning,
            _dispatchStartedAtUtc,
            _lastAgentActivityUtc,
            _lastOutputActivityUtc,
            oldestClaimActivityUtc == default ? null : oldestClaimActivityUtc,
            GeneratedImageCount,
            RequestedImageCount);
        AgentFeedbackStatusText = health.StatusText;
        HasAgentGenerationRisk = health.HasRisk;
        AgentGenerationRiskText = health.RiskText;
        OnPropertyChanged(nameof(CurrentStageElapsedText));
        OnPropertyChanged(nameof(LongestStageText));
    }

    private DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalHours >= 1d
            ? $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}"
            : $"{duration.Minutes:D2}:{duration.Seconds:D2}";
    }

    private static string NormalizeFeedbackText(string? value, int maximumLength = 2_000)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maximumLength));
        foreach (var character in value)
        {
            if (!char.IsControl(character) || character is '\r' or '\n' or '\t')
            {
                builder.Append(character);
            }
        }

        var normalized = builder.ToString().Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : "…" + normalized[^Math.Max(1, maximumLength - 1)..];
    }

    private void ApplyBatch(
        IReadOnlyList<GenerationTask> tasks,
        IReadOnlyDictionary<string, string>? descriptions,
        bool forceStatusMessage,
        bool mergeWithExisting)
    {
        if (tasks.Count == 0)
        {
            return;
        }

        var sourceTasks = mergeWithExisting
            ? PublishedTasks.Select(item => item.Task)
                .Concat(tasks)
                .GroupBy(task => task.Id, StringComparer.Ordinal)
                .Select(group => group.Last())
            : tasks;
        var orderedTasks = sourceTasks
            .OrderBy(task => task.CreatedAtUtc)
            .ThenBy(task => task.BatchItemIndex ?? 0)
            .ToArray();
        var revisionChanged = false;
        foreach (var task in orderedTasks)
        {
            if (!_lastAppliedTaskRevisions.TryGetValue(task.Id, out var revision) || revision != task.Revision)
            {
                revisionChanged = true;
                _lastAppliedTaskRevisions[task.Id] = task.Revision;
            }
        }

        _currentBatchId ??= orderedTasks[0].BatchId;
        _currentTask = orderedTasks.FirstOrDefault(task =>
                           string.Equals(task.BatchId, _currentBatchId, StringComparison.Ordinal)) ??
                       orderedTasks[0];
        var existing = PublishedTasks.ToDictionary(item => item.TaskId, StringComparer.Ordinal);
        PublishedTasks.Clear();
        foreach (var task in orderedTasks)
        {
            if (existing.TryGetValue(task.Id, out var item))
            {
                item.Apply(task);
                PublishedTasks.Add(item);
                continue;
            }

            var description = task.BatchItemId is { Length: > 0 } batchItemId &&
                              descriptions is not null &&
                              descriptions.TryGetValue(batchItemId, out var requestedDescription)
                ? requestedDescription
                : task.DisplayName;
            PublishedTasks.Add(new PublishedGenerationTaskViewModel(task, description));
        }

        OnPropertyChanged(nameof(HasCurrentTask));
        OnPropertyChanged(nameof(HasPublishedBatch));
        OnPropertyChanged(nameof(CurrentBatchId));
        OnPropertyChanged(nameof(CurrentTaskId));
        OnPropertyChanged(nameof(CurrentTaskStatusText));
        OnPropertyChanged(nameof(CurrentTaskDetail));
        RefreshManualFallbackState();

        GeneratedAssets.Clear();
        var shouldRefreshProject = false;
        var outputTargets = new List<BatchOutputTarget>();
        var currentArtifactPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newlyObservedArtifacts = new List<BatchArtifactTarget>();
        var globalOutputIndex = 0;
        foreach (var task in orderedTasks)
        {
            var publishedTask = PublishedTasks.First(item =>
                string.Equals(item.TaskId, task.Id, StringComparison.Ordinal));
            if (!MaterialGenerationConstraintComposer.TryReadDelivery(task.Constraints, out var delivery) ||
                delivery is null)
            {
                publishedTask.ApplyDetectedOutputs(0, null);
                continue;
            }

            foreach (var artifact in BuildArtifactTargets(delivery))
            {
                if (!artifact.Exists)
                {
                    continue;
                }

                currentArtifactPaths.Add(artifact.FullPath);
                if (!_observedArtifactPaths.Contains(artifact.FullPath))
                {
                    newlyObservedArtifacts.Add(artifact);
                }

                GeneratedAssets.Add(new GeneratedAssetViewModel(
                    delivery.AssetRoot,
                    artifact.RelativePath,
                    artifact.Stage,
                    task.Status,
                    publishedTask.CanRetry,
                    publishedTask.IsSuperseded,
                    publishedTask.HasAutomaticRetryRemaining,
                    string.IsNullOrWhiteSpace(task.AutomaticRetryBlockedReason)
                        ? task.FailureMessage
                        : $"自动重做已停止：{task.AutomaticRetryBlockedReason}；原始问题：{task.FailureMessage}",
                    task.Id,
                    task.RetryLineage));
            }

            if (publishedTask.IsSuperseded)
            {
                continue;
            }

            var taskTargets = delivery.TargetPaths
                .Select(relativePath =>
                {
                    var normalizedRelativePath = relativePath.Replace('\\', '/');
                    var fullPath = Path.GetFullPath(Path.Combine(
                        delivery.AssetRoot,
                        normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
                    return new BatchOutputTarget(
                        task.Id,
                         normalizedRelativePath,
                         fullPath,
                         File.Exists(fullPath),
                         globalOutputIndex++);
                })
                .ToArray();
            outputTargets.AddRange(taskTargets);
            var visibleAssetPaths = taskTargets
                .Where(target => target.Exists)
                .Select(target => target.RelativePath)
                .ToArray();
            publishedTask.ApplyDetectedOutputs(
                visibleAssetPaths.Length,
                taskTargets.FirstOrDefault(target => !target.Exists)?.RelativePath);

            if (taskTargets.Any(target =>
                    target.Exists && !_observedOutputPaths.Contains(target.FullPath)) &&
                AssetRootService.PathEquals(delivery.AssetRoot, _projectBrowser.RootPath))
            {
                shouldRefreshProject = true;
            }
        }

        _observedArtifactPaths.IntersectWith(currentArtifactPaths);
        foreach (var artifactPath in currentArtifactPaths)
        {
            _observedArtifactPaths.Add(artifactPath);
        }

        if (newlyObservedArtifacts.Count > 0)
        {
            _remainingTerminalArtifactPolls = TerminalArtifactPollCount;
        }

        if (newlyObservedArtifacts.Count > 0 && HasDispatchProgress)
        {
            _lastOutputActivityUtc = GetUtcNow();
            foreach (var artifact in newlyObservedArtifacts)
            {
                AddAgentFeedback(
                    _lastOutputActivityUtc.Value,
                    "工序产物",
                    $"{BuildArtifactStageText(artifact.Stage)}已落盘：{artifact.RelativePath}",
                    isWarning: false);
            }
        }

        ApplyImageProgress(outputTargets);

        if (shouldRefreshProject)
        {
            _projectBrowser.Refresh();
        }

        UpdateDispatchProgressFromTasks();
        UpdateAgentHealthStatus();
        OnPropertyChanged(nameof(CurrentTaskStatusText));
        OnPropertyChanged(nameof(CurrentTaskDetail));

        if (!revisionChanged && !forceStatusMessage)
        {
            return;
        }

        var displayId = _currentBatchId ?? orderedTasks[0].Id;
        StatusText = $"批次 {displayId[..Math.Min(8, displayId.Length)]} · {BuildBatchProgressText()}";
    }

    private void UpdateDispatchProgressFromTasks()
    {
        if (string.IsNullOrWhiteSpace(_currentDispatchBatchId) ||
            _currentDispatchStage is GenerationBatchDispatchStage.Idle or
                GenerationBatchDispatchStage.PreparingConstraints or
                GenerationBatchDispatchStage.FreezingStyleReferences or
                GenerationBatchDispatchStage.PersistingBatch or
                GenerationBatchDispatchStage.Queued or
                GenerationBatchDispatchStage.BuildingAgentInstruction or
                GenerationBatchDispatchStage.StartingAgentTurn)
        {
            return;
        }

        var active = PublishedTasks
            .Where(task => !task.IsSuperseded &&
                           string.Equals(task.BatchId, _currentDispatchBatchId, StringComparison.Ordinal))
            .ToArray();
        if (active.Length == 0)
        {
            return;
        }

        var completedCount = active.Count(task => task.Status == GenerationTaskStatus.Completed);
        var retrying = active.Where(task => task.HasAutomaticRetryRemaining).ToArray();
        var failed = active.Where(task =>
            task.Status == GenerationTaskStatus.Failed && !task.HasAutomaticRetryRemaining).ToArray();
        var claimed = active.Where(task => task.Status == GenerationTaskStatus.Claimed).ToArray();
        var pending = active.Where(task => task.Status == GenerationTaskStatus.Pending).ToArray();

        if (claimed.Length > 0)
        {
            var current = claimed[0];
            var outputRatio = RequestedImageCount <= 0
                ? 0d
                : GeneratedImageCount / (double)RequestedImageCount;
            var progress = 65d + 25d * outputRatio;
            var nextTarget = current.NextOutputPath;
            ApplyDispatchProgress(
                _currentDispatchBatchId,
                GenerationBatchDispatchStage.GeneratingAssets,
                progress,
                string.IsNullOrWhiteSpace(nextTarget)
                    ? $"等待任务 {ShortId(current.TaskId)} 完成登记与校验"
                    : $"等待单图任务提交：{nextTarget}",
                $"图片 {GeneratedImageCount}/{RequestedImageCount} 已落盘 · " +
                $"{claimed.Length} 个单图任务已认领 · 调度由 Agent Host 管理");
            return;
        }

        if (pending.Length > 0)
        {
            var outputRatio = RequestedImageCount <= 0
                ? 0d
                : GeneratedImageCount / (double)RequestedImageCount;
            var progress = 55d + 25d * outputRatio;
            ApplyDispatchProgress(
                _currentDispatchBatchId,
                GenerationBatchDispatchStage.WaitingForTaskClaim,
                progress,
                $"gasset_forge.claim_generation_task(taskId={ShortId(pending[0].TaskId)})",
                $"等待 Agent 认领 {pending.Length} 个任务 · 图片 {GeneratedImageCount}/{RequestedImageCount} 已落盘");
            return;
        }

        if (retrying.Length > 0)
        {
            var retry = retrying[0];
            ApplyDispatchProgress(
                _currentDispatchBatchId,
                GenerationBatchDispatchStage.GeneratingAssets,
                Math.Max(DispatchProgress, 90d),
                $"为任务 {ShortId(retry.TaskId)} 创建新的自动重做任务与交付路径",
                $"{retrying.Length} 个未达标项仍有重做额度；本次中间产物已保留，软件正在继续尝试。\n{retry.DetailText}");
            return;
        }

        if (failed.Length > 0)
        {
            var failureDetail = string.Join(
                "；",
                failed.Take(3).Select(task =>
                    $"{ShortId(task.TaskId)}：{task.DetailText}"));
            var failureStage = failed.All(task =>
                task.DetailText.Contains("未被 MCP 认领", StringComparison.OrdinalIgnoreCase) ||
                task.DetailText.Contains("调度", StringComparison.OrdinalIgnoreCase))
                ? GenerationBatchDispatchStage.WaitingForTaskClaim
                : GenerationBatchDispatchStage.GeneratingAssets;
            ApplyDispatchProgress(
                _currentDispatchBatchId,
                GenerationBatchDispatchStage.Failed,
                100d,
                "检查失败任务日志并按失败项重新发布",
                $"{completedCount}/{active.Length} 个任务完成，{failed.Length} 个任务失败。",
                failureStage,
                failureDetail);
            IsDispatching = false;
            return;
        }

        ApplyDispatchProgress(
            _currentDispatchBatchId,
            GenerationBatchDispatchStage.VerifyingOutputs,
            95d,
            "校验 delivery.targetPaths、PNG 文件与批次产物数量",
            $"{completedCount}/{active.Length} 个任务已完成 · {GeneratedImageCount}/{RequestedImageCount} 张已落盘，正在核对本地交付文件。");
        var missingAssets = active
            .SelectMany(task =>
            {
                if (!MaterialGenerationConstraintComposer.TryReadDelivery(task.Task.Constraints, out var delivery) ||
                    delivery is null)
                {
                    return new[] { $"{ShortId(task.TaskId)}：缺少 delivery 计划" };
                }

                return task.Task.RelativeAssetPaths
                    .Select(relativePath => new
                    {
                        RelativePath = relativePath,
                        FullPath = Path.GetFullPath(Path.Combine(
                            delivery.AssetRoot,
                            relativePath.Replace('/', Path.DirectorySeparatorChar)))
                    })
                    .Where(asset => !File.Exists(asset.FullPath))
                    .Select(asset => $"{ShortId(task.TaskId)}：{asset.RelativePath}");
            })
            .Take(5)
            .ToArray();
        if (missingAssets.Length > 0)
        {
            var failure = $"任务已回写 Completed，但本地交付文件缺失：{string.Join("；", missingAssets)}";
            ApplyDispatchProgress(
                _currentDispatchBatchId,
                GenerationBatchDispatchStage.Failed,
                100d,
                "检查输出目录和 complete_generation_task 交付路径",
                "本地文件校验未通过。",
                GenerationBatchDispatchStage.VerifyingOutputs,
                failure);
            return;
        }

        ApplyDispatchProgress(
            _currentDispatchBatchId,
            GenerationBatchDispatchStage.Completed,
            100d,
            "批次完成",
            $"{active.Length} 个任务、{GeneratedImageCount} 个产物已完成并通过本地校验。");
    }

    private void ChooseOutputDirectory()
    {
        ChooseRowOutputDirectory(SelectedDraftRow ?? PrimaryDraftRow);
    }

    private void ChooseRowOutputDirectory(AssetGenerationDraftRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            var selected = _outputPicker.SelectOutputDirectory(row.OutputDirectory);
            if (string.IsNullOrWhiteSpace(selected))
            {
                return;
            }

            if (!TryValidateOutputDirectory(selected, out var normalized, out var reason))
            {
                StatusText = $"无法使用该输出目录：{reason}";
                return;
            }

            row.OutputDirectory = normalized;
            SelectedDraftRow = row;
            StatusText = $"生成输出目录已设置为：{normalized}";
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"无法选择输出目录：{exception.Message}";
        }
    }

    private void UseProjectCurrentDirectory()
    {
        UseProjectCurrentDirectoryForRow(SelectedDraftRow ?? PrimaryDraftRow);
    }

    private void UseProjectCurrentDirectoryForRow(AssetGenerationDraftRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        row.OutputDirectory = _projectBrowser.CurrentPath;
        SelectedDraftRow = row;
        StatusText = "已使用项目区当前文件夹作为生成输出目录";
    }

    private void AddDraftRow()
    {
        if (!CanAddDraftRow)
        {
            StatusText = $"批量表格最多允许 {MaximumDraftRowCount} 行";
            return;
        }

        var row = new AssetGenerationDraftRowViewModel(
            Guid.NewGuid().ToString("N"),
            "新的 Tilemap 物品",
            1,
            SelectedDraftRow?.OutputDirectory ?? PrimaryDraftRow?.OutputDirectory ?? _projectBrowser.CurrentPath);
        AttachDraftRow(row);
        DraftRows.Add(row);
        SelectedDraftRow = row;
        RefreshDraftCollectionState();
        StatusText = $"已添加第 {DraftRows.Count} 行批量生成条目";
    }

    private void RemoveDraftRow(AssetGenerationDraftRowViewModel? row)
    {
        if (row is null || DraftRows.Count <= 1 || !DraftRows.Contains(row))
        {
            return;
        }

        var index = DraftRows.IndexOf(row);
        row.Changed -= OnDraftRowChanged;
        DraftRows.Remove(row);
        SelectedDraftRow = DraftRows[Math.Clamp(index, 0, DraftRows.Count - 1)];
        RefreshDraftCollectionState();
        StatusText = "已移除批量生成条目";
    }

    private void OpenOutputDirectory()
    {
        try
        {
            if (!Directory.Exists(OutputDirectory))
            {
                StatusText = "生成输出目录当前不可用";
                return;
            }

            Process.Start(new ProcessStartInfo(OutputDirectory) { UseShellExecute = true });
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"无法打开生成输出目录：{exception.Message}";
        }
    }

    private void RefreshDerivedState()
    {
        ConstraintPreview = BuildConstraintPreview();
        OnPropertyChanged(nameof(IsAgentConnected));
        OnPropertyChanged(nameof(ConnectionStatusText));
        OnPropertyChanged(nameof(CanvasSizeText));
        OnPropertyChanged(nameof(TilemapGeometryText));
        OnPropertyChanged(nameof(IsUsingUnifiedStylePrompt));
        OnPropertyChanged(nameof(UnifiedStylePromptStatusText));
        OnPropertyChanged(nameof(CanCreateTask));
        OnPropertyChanged(nameof(CanStartGeneration));
        RefreshManualFallbackState();
        RaiseCommandStates();
    }

    private string BuildConstraintPreview()
    {
        if (SelectedStyleGroup is null)
        {
            return "请选择一个美术风格组。";
        }

        if (!HasUsableSelectedStyle(SelectedStyleGroup))
        {
            return BuildUnavailableStyleMessage(SelectedStyleGroup);
        }

        var useUnifiedPrompt = TryGetFreshUnifiedPrompt(
            SelectedStyleGroup,
            out var unifiedPromptSample);
        if (!useUnifiedPrompt &&
            !TryValidateStyleReferences(SelectedStyleGroup, out var referenceReason))
        {
            return referenceReason;
        }

        if (DraftRows.Count == 0)
        {
            return "请至少添加一行需要生成的物品。";
        }

        if (!BackgroundRemoval.IsConfigurationValid)
        {
            return $"背景剔除参数无效：{BackgroundRemoval.ConfigurationValidationText}";
        }

        var invalidIndex = DraftRows
            .Select((row, index) => (row, index))
            .FirstOrDefault(item => !item.row.IsValid);
        if (invalidIndex.row is not null)
        {
            return $"第 {invalidIndex.index + 1} 行无效：{invalidIndex.row.ValidationMessage}";
        }

        if (TotalRequestedCount > MaximumBatchOutputCount)
        {
            return $"整批最多生成 {MaximumBatchOutputCount} 个产物，请减少数量或拆分发布。";
        }

        try
        {
            var availableReferences = SelectedStyleGroup.Assets.Count(asset => !asset.IsMissing);
            var stylePrompt = useUnifiedPrompt
                ? unifiedPromptSample!.Prompt.Trim()
                : BuildLegacyStylePrompt(SelectedStyleGroup);
            var builder = new StringBuilder()
                .AppendLine($"【生成步骤】{SelectedGenerationStep.DisplayName}")
                .AppendLine($"【风格组】{SelectedStyleGroup.Name}")
                .AppendLine($"【风格输入】{BuildStyleSourcePreviewText(SelectedStyleGroup, useUnifiedPrompt)}")
                .AppendLine($"【风格提示】{stylePrompt}")
                .AppendLine(
                    useUnifiedPrompt
                        ? $"【参考图片】素材库现有 {availableReferences} 张，本批次携带 0 张；不会让 Agent 重新查看素材"
                        : $"【参考图片】{availableReferences} / {MaterialGenerationConstraintComposer.MaximumReferenceCount} 张，将在发布时冻结一次并供整批共享")
                .AppendLine(
                    $"【首道底色】{BackgroundRemoval.SelectedPresetDisplayName} {BackgroundRemoval.EffectiveColorHex}；Agent 只生成纯色底原图，GAssetForge 本地剔色后才写入最终透明 PNG")
                .AppendLine($"【批量规模】{DraftRows.Count} 种物品 · {TotalRequestedCount} 个产物")
                .AppendLine("【调度】每张图片是独立任务；并发与多 Agent 分配由当前 Agent Host 默认调度，页面不再指定并行数量")
                .AppendLine($"【画布】每个产物 {CanvasSizeText}")
                .AppendLine($"【透视】{TilemapGeometryText}")
                .AppendLine()
                .AppendLine("【批量条目】");
            for (var index = 0; index < DraftRows.Count; index++)
            {
                var row = DraftRows[index];
                builder.AppendLine(
                    $"{index + 1}. {row.Description.Trim()} × {row.Quantity} → {row.OutputDirectoryHint} ({row.OutputDirectory})");
            }

            if (!string.IsNullOrWhiteSpace(NegativePrompt))
            {
                builder.AppendLine().AppendLine($"【共享负向提示】{NegativePrompt.Trim()}");
            }

            return builder.ToString().TrimEnd();
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            return $"当前生成约束无效：{exception.Message}";
        }
    }

    private string BuildPrerequisiteMessage()
    {
        if (SelectedStyleGroup is null)
        {
            return "请选择一个美术风格组";
        }

        if (!HasUsableSelectedStyle(SelectedStyleGroup))
        {
            return BuildUnavailableStyleMessage(SelectedStyleGroup);
        }

        if (!TryGetFreshUnifiedPrompt(SelectedStyleGroup, out _) &&
            !TryValidateStyleReferences(SelectedStyleGroup, out var referenceReason))
        {
            return referenceReason;
        }

        if (DraftRows.Count == 0)
        {
            return "请至少添加一行需要生成的物品";
        }

        if (!BackgroundRemoval.IsConfigurationValid)
        {
            return $"背景剔除参数无效：{BackgroundRemoval.ConfigurationValidationText}";
        }

        var invalid = DraftRows
            .Select((row, index) => (row, index))
            .FirstOrDefault(item => !item.row.IsValid);
        if (invalid.row is not null)
        {
            return $"第 {invalid.index + 1} 行无效：{invalid.row.ValidationMessage}";
        }

        if (TotalRequestedCount > MaximumBatchOutputCount)
        {
            return $"整批最多请求 {MaximumBatchOutputCount} 个产物";
        }

        return "当前批量生成配置尚未满足发布条件";
    }

    private string BuildStartGenerationPrerequisiteMessage()
    {
        if (IsDispatching)
        {
            return "当前生成批次仍在应用内调度中，请等待执行器返回结果";
        }

        if (IsBusy)
        {
            return "当前正在整理并发布生成任务，请稍候";
        }

        if (!IsAgentConnected)
        {
            return "Agent 链路尚未连接，请先在 MCP Agent 接入面板启动并完成连接";
        }

        return BuildPrerequisiteMessage();
    }

    private string ResolveInitialOutputDirectory(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) &&
            TryValidateOutputDirectory(configuredPath, out var normalized, out _))
        {
            return normalized;
        }

        return _projectBrowser.CurrentPath;
    }

    private bool TryValidateOutputDirectory(
        string candidate,
        out string normalized,
        out string reason)
    {
        if (!_assetRootService.TryNormalizeDirectory(candidate, out normalized, out reason))
        {
            return false;
        }

        if (!AssetRootService.IsWithinRoot(_projectBrowser.RootPath, normalized))
        {
            reason = "输出目录必须位于项目区当前 Assets 根目录内";
            normalized = string.Empty;
            return false;
        }

        return true;
    }

    private bool HasUsableSelectedStyle(ArtStyleGroupViewModel group) =>
        TryGetFreshUnifiedPrompt(group, out _) || HasUsableLegacyStyle(group);

    private static bool HasUsableLegacyStyle(ArtStyleGroupViewModel group) =>
        !string.IsNullOrWhiteSpace(group.Description) ||
        !string.IsNullOrWhiteSpace(group.StylePrompt) ||
        group.Assets.Any(asset => !asset.IsMissing);

    private bool TryGetFreshUnifiedPrompt(
        ArtStyleGroupViewModel? group,
        out ArtStylePromptSampleModel? sample)
    {
        sample = group?.UnifiedPromptSample;
        return PreferUnifiedStylePrompt &&
               group is { IsUnifiedPromptFresh: true } &&
               sample is not null &&
               !string.IsNullOrWhiteSpace(sample.Prompt) &&
               !string.IsNullOrWhiteSpace(sample.SourceFingerprint) &&
               sample.GeneratedAtUtc != default;
    }

    private string BuildUnavailableStyleMessage(ArtStyleGroupViewModel group)
    {
        if (!PreferUnifiedStylePrompt)
        {
            return "当前风格组没有可用于匹配的描述、人工提示词或参考图片。";
        }

        return group.UnifiedPromptSample is null
            ? "当前风格组尚无统一提示词样本，且没有可回退的风格描述、人工提示词或参考图片；请先在美术风格素材库分析生成提示词。"
            : "当前统一提示词样本已过期，且没有可回退的风格描述、人工提示词或参考图片；请重新分析当前风格库。";
    }

    private static bool TryValidateStyleReferences(
        ArtStyleGroupViewModel group,
        out string reason)
    {
        var availableAssets = group.Assets
            .Where(asset => !asset.IsMissing)
            .ToArray();
        if (availableAssets.Length > MaterialGenerationConstraintComposer.MaximumReferenceCount)
        {
            reason = $"当前风格组有 {availableAssets.Length} 张可用参考图，单次生成最多使用 " +
                     $"{MaterialGenerationConstraintComposer.MaximumReferenceCount} 张；请拆分风格组后再提交。";
            return false;
        }

        long totalBytes = 0;
        foreach (var asset in availableAssets)
        {
            if (asset.Size is <= 0 or > ArtStyleLibraryValidator.MaximumAssetFileBytes)
            {
                reason = $"参考图“{asset.Name}”为空或超过 " +
                         $"{ArtStyleLibraryValidator.MaximumAssetFileBytes / 1024 / 1024} MB 上限。";
                return false;
            }

            if (totalBytes > GenerationReferenceSnapshotService.MaximumTaskReferenceBytes - asset.Size)
            {
                reason = $"当前风格组的可用参考图总量超过单次生成允许的 " +
                         $"{GenerationReferenceSnapshotService.MaximumTaskReferenceBytes / 1024 / 1024 / 1024} GB；" +
                         "请拆分风格组后再提交。";
                return false;
            }

            totalBytes += asset.Size;
        }

        reason = string.Empty;
        return true;
    }

    private static string BuildLegacyStylePrompt(ArtStyleGroupViewModel group)
    {
        var parts = new[] { group.Description.Trim(), group.StylePrompt.Trim() }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (parts.Length > 0)
        {
            return string.Join("；", parts);
        }

        return $"严格参考风格组“{group.Name}”中的图像，统一色彩、材质、光照、笔触与细节密度";
    }

    private string BuildStyleSourcePreviewText(
        ArtStyleGroupViewModel group,
        bool useUnifiedPrompt)
    {
        if (useUnifiedPrompt && group.UnifiedPromptSample is { } sample)
        {
            var fingerprint = sample.SourceFingerprint.Length > 12
                ? sample.SourceFingerprint[..12]
                : sample.SourceFingerprint;
            return $"统一提示词优先 · 样本生成于 {sample.GeneratedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} · 来源 {fingerprint}";
        }

        if (!PreferUnifiedStylePrompt)
        {
            return "旧链路 · 已关闭统一提示词优先";
        }

        if (group.UnifiedPromptSample is null)
        {
            return "自动回退旧链路 · 当前没有统一提示词样本";
        }
        return "自动回退旧链路 · 统一提示词样本已过期";
    }

    private static string BuildDisplayName(string prompt)
    {
        var compact = string.Join(' ', prompt.Split(
            new[] { '\r', '\n', '\t' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (compact.Length > 80)
        {
            compact = compact[..80];
        }

        return $"Tilemap · {compact}";
    }

    private static GenerationConstraintPackage BuildAutomaticRetryConstraints(
        GenerationTask parent,
        int attemptNumber)
    {
        var subCode = parent.FailureSubCode ?? parent.FailureCode ?? "generation_validation_failed";
        var diagnostic = NormalizeRetryDiagnostic(parent.FailureMessage);
        var correction = BuildDirectedRetryInstruction(subCode, diagnostic);
        var retryRule = new GenerationConstraintRule
        {
            Key = $"generation.retry.attempt-{attemptNumber}",
            Category = "retry",
            Instruction = correction,
            Required = true,
            Weight = 1d
        };
        var negativeAddition = subCode switch
        {
            "tilemap_geometry_aspect_ratio_mismatch" =>
                "vertical rectangle, horizontal rectangle, elongated tile body, unequal foreground width and height",
            "tilemap_geometry_rotation_mismatch" =>
                "tilted square, rotated tile body, diagonal outer edges, unintended canvas rotation",
            "tilemap_geometry_diamond_detected" =>
                "on-point diamond, 45 degree diamond, rhombus silhouette, top and bottom pointed corners",
            _ => "repeat the previously rejected geometry or composition"
        };
        var negativePrompt = string.Join(
            ", ",
            new[] { parent.Constraints.NegativePrompt, negativeAddition }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase));
        var prompt = parent.Constraints.Prompt.Trim() +
                     $"\n\nAutomatic correction attempt {attemptNumber}: {correction}";
        return parent.Constraints with
        {
            Prompt = prompt,
            NegativePrompt = negativePrompt,
            Output = parent.Constraints.Output with { Count = 1, MaxParallelism = 1 },
            Rules = [.. parent.Constraints.Rules, retryRule]
        };
    }

    private static string BuildDirectedRetryInstruction(string subCode, string diagnostic)
    {
        var directed = subCode switch
        {
            "tilemap_geometry_aspect_ratio_mismatch" =>
                "上一轮主体是长方形。本轮前景瓦片主体的外接包围盒宽度与高度必须近似相等；画布正方形不代表主体合格。保持画布轴对齐，主体占画布较短边约 80%～90%，四条边完整，不得生成竖向/横向长方形或菱形。",
            "tilemap_geometry_rotation_mismatch" =>
                "上一轮主体边缘方向与设置角度不符。本轮必须严格使用冻结的边 A/边 B 屏幕方向，不得补偿或附加旋转；0° 时上下边水平、左右边垂直。",
            "tilemap_geometry_diamond_detected" =>
                "上一轮检测到上下尖角、中心最宽的真实菱形轮廓。本轮必须生成画布轴对齐方形主体，禁止 45° on-point diamond，四个方形角区都要有主体内容。",
            _ =>
                "上一轮产物未通过本地约束校验。本轮必须逐项修正诊断中的实测偏差，不得复用或近似复制上一轮构图。"
        };
        return string.IsNullOrWhiteSpace(diagnostic)
            ? directed
            : $"{directed} 上一轮结构化诊断：{diagnostic}";
    }

    private static string NormalizeRetryDiagnostic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = string.Join(
            " ",
            value.Split(
                new[] { '\r', '\n', '\t' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= 2_000 ? normalized : normalized[..2_000];
    }

    private static string ResolveRetryOutputDirectory(string previousOutputDirectory, string retryBatchId)
    {
        var segments = (previousOutputDirectory ?? string.Empty)
            .Replace('\\', '/')
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (segments.Count >= 3 &&
            string.Equals(segments[^3], "Tilemap", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(segments[^2], "N", out _) &&
            string.Equals(segments[^1], "Final", StringComparison.OrdinalIgnoreCase))
        {
            segments[^2] = retryBatchId;
            return string.Join('/', segments);
        }

        var prefix = string.Join('/', segments);
        return string.IsNullOrEmpty(prefix)
            ? $"Generated/Tilemap/{retryBatchId}/Final"
            : $"{prefix}/Tilemap/{retryBatchId}/Final";
    }

    private void SubscribeStyleGroup(ArtStyleGroupViewModel? group)
    {
        if (group is null)
        {
            return;
        }

        group.PropertyChanged += OnSelectedStyleGroupPropertyChanged;
        group.Assets.CollectionChanged += OnSelectedStyleAssetsChanged;
    }

    private void UnsubscribeStyleGroup(ArtStyleGroupViewModel? group)
    {
        if (group is null)
        {
            return;
        }

        group.PropertyChanged -= OnSelectedStyleGroupPropertyChanged;
        group.Assets.CollectionChanged -= OnSelectedStyleAssetsChanged;
    }

    private void OnStyleLibraryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ArtStyleLibraryViewModel.SelectedGroup) &&
            !ReferenceEquals(SelectedStyleGroup, _styleLibrary.SelectedGroup))
        {
            SelectedStyleGroup = _styleLibrary.SelectedGroup;
        }
    }

    private void OnSelectedStyleGroupPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        RefreshDerivedState();

    private void OnSelectedStyleAssetsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RefreshDerivedState();

    private void OnTilemapPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        RefreshDerivedState();

    private void OnAgentConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AgentConnectionViewModel.IsConnected)
            or nameof(AgentConnectionViewModel.IsTurnRunning)
            or nameof(AgentConnectionViewModel.StatusText)
            or nameof(AgentConnectionViewModel.SelectedAgent)
            or nameof(AgentConnectionViewModel.Endpoint)
            or nameof(AgentConnectionViewModel.WorkingDirectory)
            or nameof(AgentConnectionViewModel.CodexConnectionPrompt))
        {
            RefreshDerivedState();
            UpdateAgentHealthStatus();
        }
    }

    private void OnProjectCurrentPathChanged(string path)
    {
        var changedAny = false;
        foreach (var row in DraftRows.Where(row =>
                     !TryValidateOutputDirectory(row.OutputDirectory, out _, out _)))
        {
            row.OutputDirectory = path;
            changedAny = true;
        }

        if (!changedAny)
        {
            RefreshDerivedState();
        }
    }

    private void OnProjectRootPathChanged(string? path)
    {
        var changedAny = false;
        foreach (var row in DraftRows.Where(row =>
                     !TryValidateOutputDirectory(row.OutputDirectory, out _, out _)))
        {
            row.OutputDirectory = _projectBrowser.CurrentPath;
            changedAny = true;
        }

        if (!changedAny)
        {
            RefreshDerivedState();
        }

        _ = RefreshCurrentTaskAsync();
    }

    private async void OnPollTimerTick(object? sender, EventArgs e) =>
        await RefreshCurrentTaskAsync().ConfigureAwait(true);

    private void OnBackgroundRemovalPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BackgroundRemovalViewModel.SelectedPreset) or
            nameof(BackgroundRemovalViewModel.CustomColorHex) or
            nameof(BackgroundRemovalViewModel.EffectiveColorHex) or
            nameof(BackgroundRemovalViewModel.TransparentThreshold) or
            nameof(BackgroundRemovalViewModel.OpaqueThreshold) or
            nameof(BackgroundRemovalViewModel.EdgeContract) or
            nameof(BackgroundRemovalViewModel.EdgeFeather) or
            nameof(BackgroundRemovalViewModel.Despill) or
            nameof(BackgroundRemovalViewModel.EdgeConnectedOnly) or
            nameof(BackgroundRemovalViewModel.IsConfigurationValid))
        {
            RefreshDerivedState();
        }
    }

    private void NotifySettingsChanged()
    {
        if (_suppressSettingsNotification)
        {
            return;
        }

        SettingsChanged?.Invoke(
            this,
            new AssetGenerationSettingsChangedEventArgs(
                SelectedGenerationStep.Key,
                NegativePrompt,
                MaxParallelism,
                AutoRetryEnabled,
                AutoRetryCount,
                PreferUnifiedStylePrompt,
                DraftRows.Select(row => row.Snapshot()).ToArray()));
    }

    private void RaiseCommandStates()
    {
        _createTaskCommand?.RaiseCanExecuteChanged();
        _startGenerationCommand?.RaiseCanExecuteChanged();
        _refreshTaskCommand?.RaiseCanExecuteChanged();
        _openOutputDirectoryCommand?.RaiseCanExecuteChanged();
        _addDraftRowCommand?.RaiseCanExecuteChanged();
        _removeDraftRowCommand?.RaiseCanExecuteChanged();
        _retryFailedTaskCommand?.RaiseCanExecuteChanged();
        _createManualRetryBatchCommand?.RaiseCanExecuteChanged();
    }

    private static bool IsExpectedOperationException(Exception exception) => exception is
        ArgumentException or
        InvalidOperationException or
        InvalidDataException or
        IOException or
        UnauthorizedAccessException or
        NotSupportedException or
        OperationCanceledException or
        System.Security.SecurityException or
        TimeoutException;

    private sealed record GenerationRunManifestDraft(
        string ItemId,
        string Description,
        int RowIndex,
        int VariantIndex,
        int VariantCount,
        string ModelOutputPath,
        string NormalizedPath,
        string ProcessedPath,
        string TargetPath);
}

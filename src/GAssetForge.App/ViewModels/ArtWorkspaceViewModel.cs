using GAssetForge.App.Infrastructure;
using GAssetForge.App.Services;
using GAssetForge.Core.ArtWorkspaces;
using GAssetForge.Core.Generation;
using GAssetForge.Core.Projects;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;

namespace GAssetForge.App.ViewModels;

public sealed record ArtWorkspaceCreationRequestedEventArgs(
    string Instruction,
    double OutputX,
    double OutputY,
    double OutputWidth,
    double OutputHeight,
    string OutputRelativeDirectory,
    IReadOnlyList<string> VisibleImageIds);

public readonly record struct ArtWorkspaceWorldBounds(
    double X,
    double Y,
    double Width,
    double Height);

public enum ArtWorkspaceToolMode
{
    SelectMove,
    ImageRegionSelect
}

public sealed class ArtWorkspaceViewModel : ObservableObject, IDisposable
{
    public const double MinimumZoom = 0.1d;
    public const double MaximumZoom = 4d;
    private static readonly TimeSpan AutoSaveDelay = TimeSpan.FromMilliseconds(650);
    private static readonly string MaximumSerializedAgentReplyReservation =
        new('\u4E00', ArtWorkspaceConversationMessageViewModel.MaximumTextLength);
    private static readonly string MaximumSerializedAgentStateReservation =
        new('\u4E00', ArtWorkspaceConversationMessageViewModel.MaximumStateLength);
    private const int MaximumAutoSaveRetries = 3;

    private readonly JsonArtWorkspaceRepository _repository;
    private readonly IArtWorkspaceImagePicker _imagePicker;
    private readonly WorkbenchConsoleService _console;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _referenceAvailabilityTimer;
    private readonly AsyncRelayCommand _importImagesCommand;
    private readonly AsyncRelayCommand _submitInstructionCommand;
    private readonly AsyncRelayCommand _clearConversationCommand;
    private readonly RelayCommand _removeSelectedImageCommand;
    private readonly RelayCommand _bringSelectedToFrontCommand;
    private readonly RelayCommand _imageRegionSelectToolCommand;
    private readonly RelayCommand _cancelImportCommand;
    private readonly RelayCommand<ArtWorkspaceImageItemViewModel> _selectImageCommand;
    private readonly Dispatcher _dispatcher;
    private readonly List<ArtWorkspaceManagedAsset> _assets = [];
    private readonly List<ArtWorkspaceInstructionDraftSegment> _instructionDraftSegments = [];
    private readonly Dictionary<string, DeferredGenerationContext> _generationContexts =
        new(StringComparer.Ordinal);
    private ArtWorkspaceDocument _document = ArtWorkspaceDocumentFactory.Create();
    private string? _projectRoot;
    private string _currentFilePath = string.Empty;
    private string _statusMessage = "美术工作区正在初始化";
    private string _instructionDraft = string.Empty;
    private int _instructionDraftRevision;
    private string? _lastImportDirectory;
    private ArtWorkspaceImageItemViewModel? _selectedImage;
    private ArtWorkspaceImageRegionSelection? _selectedImageRegion;
    private ArtWorkspaceToolMode _activeToolMode = ArtWorkspaceToolMode.SelectMove;
    private double _canvasWidth = 8192d;
    private double _canvasHeight = 8192d;
    private double _panX = 48d;
    private double _panY = 48d;
    private double _zoom = 0.72d;
    private double _outputFrameX = 640d;
    private double _outputFrameY = 420d;
    private double _outputFrameWidth = 1024d;
    private double _outputFrameHeight = 1024d;
    private string _outputFrameName = "新美术资源输出框";
    private string _outputBackgroundHex = "#20242C";
    private string _outputRelativeDirectory = "Assets/Final";
    private bool _hasPendingSave;
    private int _autoSaveRetryCount;
    private bool _suppressAutoSave;
    private bool _isImporting;
    private CancellationTokenSource? _importCancellation;
    private IArtWorkspaceGenerationGateway? _generationGateway;
    private CancellationTokenSource? _generationCancellation;
    private string? _activeGenerationRequestId;
    private bool _isGenerating;
    private bool _isClearingConversation;
    private Func<string, CancellationToken, Task>? _clearConversationHandler;
    private string _generationProgressText = "尚未启动生成";
    private double _generationProgressPercentage;
    private bool _lastHasMissingVisibleImages;
    private bool _missingAvailabilityInitialized;
    private bool _disposed;

    public ArtWorkspaceViewModel(
        JsonArtWorkspaceRepository repository,
        IArtWorkspaceImagePicker imagePicker,
        WorkbenchConsoleService console)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _imagePicker = imagePicker ?? throw new ArgumentNullException(nameof(imagePicker));
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _dispatcher = Dispatcher.CurrentDispatcher;
        _importImagesCommand = new AsyncRelayCommand(ImportImagesAsync, () => !IsImporting && !IsClearingConversation);
        ImportImagesCommand = _importImagesCommand;
        _cancelImportCommand = new RelayCommand(CancelImport, () => IsImporting);
        CancelImportCommand = _cancelImportCommand;
        _submitInstructionCommand = new AsyncRelayCommand(SubmitInstructionAsync, CanSubmitInstruction);
        SubmitInstructionCommand = _submitInstructionCommand;
        _clearConversationCommand = new AsyncRelayCommand(
            ClearConversationAsync, () => !_disposed && !IsImporting && !IsClearingConversation);
        ClearConversationCommand = _clearConversationCommand;
        SaveCommand = new RelayCommand(() => SaveNow());
        ResetViewportCommand = new RelayCommand(ResetViewportToOrigin);
        FocusOutputFrameCommand = new RelayCommand(FocusOutputFrame);
        _removeSelectedImageCommand = new RelayCommand(RemoveSelectedImage, () => SelectedImage is not null);
        RemoveSelectedImageCommand = _removeSelectedImageCommand;
        _bringSelectedToFrontCommand = new RelayCommand(BringSelectedToFront, () => SelectedImage is not null);
        BringSelectedToFrontCommand = _bringSelectedToFrontCommand;
        SelectMoveToolCommand = new RelayCommand(() => ActiveToolMode = ArtWorkspaceToolMode.SelectMove);
        _imageRegionSelectToolCommand = new RelayCommand(
            () => ActiveToolMode = ArtWorkspaceToolMode.ImageRegionSelect,
            () => SelectedImage is { IsVisible: true, IsMissing: false });
        ImageRegionSelectToolCommand = _imageRegionSelectToolCommand;
        _selectImageCommand = new RelayCommand<ArtWorkspaceImageItemViewModel>(SelectImage);
        SelectImageCommand = _selectImageCommand;
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = AutoSaveDelay
        };
        _saveTimer.Tick += OnSaveTimerTick;
        _referenceAvailabilityTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _referenceAvailabilityTimer.Tick += OnReferenceAvailabilityTimerTick;
        _referenceAvailabilityTimer.Start();
    }

    public event EventHandler<ArtWorkspaceCreationRequestedEventArgs>? CreationRequested;

    public string Header => "美术工作区制作";

    public string Description => "在同一张大画布中组织参考图、输出画幅与对话式创作意图。";

    public ObservableCollection<ArtWorkspaceImageItemViewModel> Images { get; } = [];

    public ObservableCollection<ArtWorkspaceConversationMessageViewModel> Conversation { get; } = [];

    public double CanvasWidth => _canvasWidth;

    public double CanvasHeight => _canvasHeight;

    public double PanX => _panX;

    public double PanY => _panY;

    public double Zoom => _zoom;

    public string ZoomText => $"{Zoom:P0}";

    public string ImageCountText => $"{Images.Count} 张参考图片";

    public string StatusText => $"{ImageCountText} · 输出 {OutputFrameWidth:0.#} × {OutputFrameHeight:0.#} · {_statusMessage}";

    public bool IsImporting
    {
        get => _isImporting;
        private set
        {
            if (!SetProperty(ref _isImporting, value))
            {
                return;
            }

            _importImagesCommand.RaiseCanExecuteChanged();
            _cancelImportCommand.RaiseCanExecuteChanged();
            _submitInstructionCommand.RaiseCanExecuteChanged();
            _clearConversationCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanSubmitInstructionText));
            if (!value && _hasPendingSave)
            {
                ScheduleAutoSave(resetRetryCount: false);
            }
        }
    }

    public string PersistenceText => string.IsNullOrWhiteSpace(_currentFilePath)
        ? "尚未激活工作区文件"
        : _hasPendingSave
            ? $"待保存 · {_currentFilePath}"
            : $"已保存 · {_currentFilePath}";

    public string ProjectContextText => string.IsNullOrWhiteSpace(_projectRoot)
        ? "本地草稿工作区"
        : $"项目工作区 · {_projectRoot}";

    public bool IsAgentConnected => _generationGateway?.IsConnected == true;

    public bool IsAgentBusy => _generationGateway?.IsBusy == true;

    public bool IsGenerating
    {
        get => _isGenerating;
        private set
        {
            if (!SetProperty(ref _isGenerating, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SubmitInstructionButtonText));
            OnPropertyChanged(nameof(AgentConnectionStatusText));
            OnPropertyChanged(nameof(CanSubmitInstructionText));
            _submitInstructionCommand.RaiseCanExecuteChanged();
        }
    }

    public string AgentConnectionStatusText
    {
        get
        {
            if (_generationGateway is null)
            {
                return "Agent 未接入";
            }

            if (!IsAgentConnected)
            {
                return string.IsNullOrWhiteSpace(_generationGateway.StatusText)
                    ? "Agent 尚未达到可执行状态"
                    : _generationGateway.StatusText;
            }

            var displayName = string.IsNullOrWhiteSpace(_generationGateway.AgentDisplayName)
                ? "Agent"
                : _generationGateway.AgentDisplayName.Trim();
            return IsGenerating || IsAgentBusy
                ? $"Agent 已接入 · {displayName} 正在忙碌"
                : $"Agent 已接入可用 · {displayName}";
        }
    }

    public string SubmitInstructionButtonText => IsGenerating ? "正在生成…" : "发送并生成";

    public string GenerationProgressText
    {
        get => _generationProgressText;
        private set => SetProperty(ref _generationProgressText, value ?? string.Empty);
    }

    public double GenerationProgressPercentage
    {
        get => _generationProgressPercentage;
        private set => SetProperty(
            ref _generationProgressPercentage,
            Math.Clamp(double.IsFinite(value) ? value : 0d, 0d, 100d));
    }

    public string InstructionDraft
    {
        get => _instructionDraft;
        set => ReplaceInstructionDraftSegments(
            string.IsNullOrEmpty(value)
                ? []
                : [ArtWorkspaceInstructionDraftSegment.CreateText(value)],
            enforceBudget: false);
    }

    /// <summary>
    /// Immutable editor snapshot. The returned records and their region payloads are deep copies,
    /// so a caller cannot make an already inserted image token follow later canvas selection edits.
    /// </summary>
    public IReadOnlyList<ArtWorkspaceInstructionDraftSegment> InstructionDraftSegments =>
        _instructionDraftSegments.Select(segment => segment.DeepClone()).ToArray();

    /// <summary>
    /// Changes whenever the editor document must be rebuilt (including reference validity changes).
    /// The WPF RichTextBox uses this instead of two-way binding a FlowDocument.
    /// </summary>
    public int InstructionDraftRevision => _instructionDraftRevision;

    public int InstructionDraftSemanticLength => MeasureInstructionDraftSemanticLength(_instructionDraftSegments);

    public bool HasInstructionReferences => _instructionDraftSegments.Any(segment => segment.IsImageReference);

    public bool HasInvalidInstructionReferences => _instructionDraftSegments
        .Where(segment => segment.IsImageReference)
        .Any(segment => !IsInstructionReferenceValid(segment.TokenId));

    public bool CanInsertSelectedImageReference =>
        SelectedImage is { IsVisible: true, IsMissing: false } &&
        _instructionDraftSegments.Count(segment => segment.IsImageReference) <
        ArtWorkspaceGenerationConstraintPackageBuilder.MaximumInlineInstructionReferenceCount;

    public bool IsClearingConversation
    {
        get => _isClearingConversation;
        private set
        {
            if (SetProperty(ref _isClearingConversation, value))
            {
                OnPropertyChanged(nameof(CanSubmitInstructionText));
                _submitInstructionCommand.RaiseCanExecuteChanged();
                _clearConversationCommand.RaiseCanExecuteChanged();
                _importImagesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand ClearConversationCommand { get; }

    public string CanSubmitInstructionText => IsClearingConversation
        ? "正在停止旧美术任务并清除对话，请稍候。"
        : IsImporting
        ? "参考图片正在导入；完成或取消后即可发送生成指令。"
        : IsGenerating
            ? "当前指令正在由 Agent 执行，请等待本轮完成。"
            : string.IsNullOrWhiteSpace(_projectRoot)
                ? "请先创建或打开正式项目；本地草稿不会启动美术生成。"
                : _generationGateway is null
                    ? "请先在 MCP Agent 接入页配置可用的生成会话。"
                    : !IsAgentConnected
                        ? $"Agent 尚不可用：{_generationGateway.StatusText}"
                        : IsAgentBusy
                            ? "Agent 当前正在执行其它任务，完成后即可发送。"
                            : HasMissingVisibleImages
                                ? "当前画布存在受管文件缺失的可见参考图片；请重新导入或先隐藏该图片。"
                                : HasInvalidInstructionReferences
                                    ? "对话中存在已删除、隐藏或文件缺失的图片引用；请删除对应标签或恢复图片后再发送。"
                                : Conversation.Count > ArtWorkspaceValidator.MaximumMessages - 2
                                    ? "对话记录已达到上限，请使用“清除对话并停止任务”后继续。"
                                    : !HasInstructionDraftContent()
                                        ? "输入希望创建的内容，并可在光标处插入一张或多张画布图片。"
                                        : InstructionDraftSemanticLength > ArtWorkspaceConversationMessageViewModel.MaximumTextLength
                                            ? $"当前指令语义长度为 {InstructionDraftSemanticLength:N0}，不能超过 {ArtWorkspaceConversationMessageViewModel.MaximumTextLength:N0}。"
                                        : "发送后会冻结当前输出框与可见参考图，并通过已接入 Agent 生成图片。";

    public double OutputFrameX
    {
        get => _outputFrameX;
        set => SetOutputValue(ref _outputFrameX, value, -100_000d, 100_000d, nameof(OutputFrameX));
    }

    public double OutputFrameY
    {
        get => _outputFrameY;
        set => SetOutputValue(ref _outputFrameY, value, -100_000d, 100_000d, nameof(OutputFrameY));
    }

    public double OutputFrameWidth
    {
        get => _outputFrameWidth;
        set => SetOutputValue(ref _outputFrameWidth, value, 16d, 16384d, nameof(OutputFrameWidth));
    }

    public double OutputFrameHeight
    {
        get => _outputFrameHeight;
        set => SetOutputValue(ref _outputFrameHeight, value, 16d, 16384d, nameof(OutputFrameHeight));
    }

    public string OutputFrameName
    {
        get => _outputFrameName;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "新美术资源输出框" : value.Trim();
            if (normalized.Length > 160)
            {
                normalized = normalized[..160];
            }

            if (SetProperty(ref _outputFrameName, normalized))
            {
                MarkDirty();
            }
        }
    }

    public string OutputBackgroundHex
    {
        get => _outputBackgroundHex;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "#20242C" : value.Trim();
            if (SetProperty(ref _outputBackgroundHex, normalized))
            {
                MarkDirty();
            }
        }
    }

    public string OutputRelativeDirectory
    {
        get => _outputRelativeDirectory;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value)
                ? "Assets/Final"
                : value.Trim().Replace('\\', '/').TrimEnd('/');
            if (!IsSafeAssetsDirectory(normalized) ||
                (!string.Equals(normalized, "Assets/Final", StringComparison.OrdinalIgnoreCase) &&
                 !normalized.StartsWith("Assets/Final/", StringComparison.OrdinalIgnoreCase)))
            {
                _statusMessage = "最终产物分类目录必须位于 Assets/Final 内且不含 . 或 .. 跳转";
                OnPropertyChanged();
                NotifySummaryChanged();
                return;
            }

            if (SetProperty(ref _outputRelativeDirectory, normalized))
            {
                MarkDirty();
            }
        }
    }

    public string OutputFrameSummary =>
        $"位置 ({OutputFrameX:0.#}, {OutputFrameY:0.#}) · {OutputFrameWidth:0.#} × {OutputFrameHeight:0.#}";

    public ArtWorkspaceImageItemViewModel? SelectedImage
    {
        get => _selectedImage;
        private set
        {
            if (ReferenceEquals(_selectedImage, value))
            {
                return;
            }

            if (_selectedImage is not null)
            {
                _selectedImage.IsSelected = false;
                _selectedImage.SetSelectedRegion(null);
            }

            _selectedImage = value;
            _selectedImageRegion = null;
            if (_selectedImage is not null)
            {
                _selectedImage.IsSelected = true;
            }
            else if (ActiveToolMode == ArtWorkspaceToolMode.ImageRegionSelect)
            {
                ActiveToolMode = ArtWorkspaceToolMode.SelectMove;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedImage));
            OnPropertyChanged(nameof(SelectedImageRegion));
            OnPropertyChanged(nameof(HasSelectedImageRegion));
            OnPropertyChanged(nameof(SelectedImageReferenceText));
            OnPropertyChanged(nameof(CanInsertSelectedImageReference));
            _removeSelectedImageCommand.RaiseCanExecuteChanged();
            _bringSelectedToFrontCommand.RaiseCanExecuteChanged();
            _imageRegionSelectToolCommand.RaiseCanExecuteChanged();
            MarkDirty();
        }
    }

    public bool HasSelectedImage => SelectedImage is not null;

    public bool HasMissingVisibleImages => Images.Any(image => image.IsVisible && image.IsMissing);

    public ArtWorkspaceImageRegionSelection? SelectedImageRegion =>
        _selectedImageRegion is null ? null : CloneRegion(_selectedImageRegion);

    public bool HasSelectedImageRegion => _selectedImageRegion is not null;

    public string SelectedImageReferenceText => SelectedImage switch
    {
        null => "本轮未指定图片指代",
        { IsVisible: false } image => $"所选图片“{image.Name}”当前已隐藏，不能插入",
        { IsMissing: true } image => $"所选图片“{image.Name}”的受管文件缺失，不能插入",
        var image when HasSelectedImageRegion => $"可插入：{image.Name} · 当前局部区域（框选）",
        var image => $"可插入：{image.Name} · 整张图片"
    };

    public ArtWorkspaceToolMode ActiveToolMode
    {
        get => _activeToolMode;
        private set
        {
            if (!SetProperty(ref _activeToolMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsSelectMoveToolActive));
            OnPropertyChanged(nameof(IsImageRegionSelectToolActive));
        }
    }

    public bool IsSelectMoveToolActive => ActiveToolMode == ArtWorkspaceToolMode.SelectMove;

    public bool IsImageRegionSelectToolActive => ActiveToolMode == ArtWorkspaceToolMode.ImageRegionSelect;

    public ICommand ImportImagesCommand { get; }

    public ICommand CancelImportCommand { get; }

    public ICommand SubmitInstructionCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand ResetViewportCommand { get; }

    public ICommand FocusOutputFrameCommand { get; }

    public ICommand RemoveSelectedImageCommand { get; }

    public ICommand BringSelectedToFrontCommand { get; }

    public ICommand SelectImageCommand { get; }

    public ICommand SelectMoveToolCommand { get; }

    public ICommand ImageRegionSelectToolCommand { get; }

    public void ConfigureGenerationGateway(IArtWorkspaceGenerationGateway? gateway)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(_generationGateway, gateway))
        {
            RefreshGenerationGatewayState();
            return;
        }

        if (IsGenerating)
        {
            throw new InvalidOperationException("美术生成正在执行，不能在途中替换 Agent 网关。");
        }

        if (_generationGateway is not null)
        {
            _generationGateway.StateChanged -= OnGenerationGatewayStateChanged;
            _generationGateway.DeferredGenerationCompleted -= OnDeferredGenerationCompleted;
        }

        _generationGateway = gateway;
        if (_generationGateway is not null)
        {
            _generationGateway.StateChanged += OnGenerationGatewayStateChanged;
            _generationGateway.DeferredGenerationCompleted += OnDeferredGenerationCompleted;
        }

        RefreshGenerationGatewayState();
    }

    public void ConfigureConversationResetHandler(Func<string, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _clearConversationHandler = handler;
    }

    public bool Activate(string? projectRoot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsImporting || IsClearingConversation)
        {
            _statusMessage = "参考图片导入或对话清理仍在进行，请等待完成后再切换项目";
            NotifySummaryChanged();
            return false;
        }

        var normalizedProjectRoot = string.IsNullOrWhiteSpace(projectRoot)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        if (!string.IsNullOrWhiteSpace(_currentFilePath) &&
            PathsEqual(_projectRoot, normalizedProjectRoot))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(_currentFilePath) && !SavePendingChanges())
        {
            _statusMessage = "当前美术工作区未能安全保存，已取消项目切换";
            NotifySummaryChanged();
            return false;
        }

        try
        {
            var result = _repository.LoadOrCreate(normalizedProjectRoot);
            InvalidateGenerationWait();
            ApplyDocument(normalizedProjectRoot, result);
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                _console.WriteWarning("ArtWorkspace", result.ErrorMessage);
            }

            return true;
        }
        catch (Exception exception)
        {
            _statusMessage = $"加载失败：{exception.Message}";
            NotifySummaryChanged();
            _console.WriteError("ArtWorkspace", $"美术工作区加载失败：{exception.Message}", exception);
            return false;
        }
    }

    public bool SaveNow()
    {
        return SaveNowCore(explicitRequest: true, scheduleRetry: false);
    }

    public bool SavePendingChanges()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return !_hasPendingSave || SaveNowCore(explicitRequest: false, scheduleRetry: false);
    }

    private bool SaveNowCore(bool explicitRequest, bool scheduleRetry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _saveTimer.Stop();
        if (IsImporting)
        {
            _statusMessage = "参考图片仍在导入，当前工作区尚未保存";
            NotifySummaryChanged();
            return false;
        }

        if (string.IsNullOrWhiteSpace(_currentFilePath))
        {
            return true;
        }

        if (!explicitRequest && !_hasPendingSave)
        {
            return true;
        }

        try
        {
            var document = CaptureDocument();
            _repository.Save(_projectRoot, document);
            _document = document;
            _hasPendingSave = false;
            _autoSaveRetryCount = 0;
            _saveTimer.Interval = AutoSaveDelay;
            _statusMessage = "工作区已保存";
            NotifySummaryChanged();
            return true;
        }
        catch (Exception exception)
        {
            _hasPendingSave = true;
            if (scheduleRetry && _autoSaveRetryCount < MaximumAutoSaveRetries)
            {
                _autoSaveRetryCount++;
                _saveTimer.Interval = TimeSpan.FromMilliseconds(
                    AutoSaveDelay.TotalMilliseconds * Math.Pow(2d, _autoSaveRetryCount));
                _saveTimer.Start();
                _statusMessage =
                    $"保存失败，准备自动重试 {_autoSaveRetryCount}/{MaximumAutoSaveRetries}：{exception.Message}";
            }
            else
            {
                _statusMessage = $"保存失败：{exception.Message}";
            }

            NotifySummaryChanged();
            _console.WriteError("ArtWorkspace", $"美术工作区保存失败：{exception.Message}", exception);
            return false;
        }
    }

    public int AddImagesAt(IEnumerable<string> sourcePaths, double? worldX = null, double? worldY = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (IsImporting || IsClearingConversation)
        {
            _statusMessage = "已有参考图片导入任务正在执行";
            NotifySummaryChanged();
            return 0;
        }

        var added = 0;
        var startX = worldX ?? Math.Max(40d, OutputFrameX - 430d);
        var startY = worldY ?? OutputFrameY;
        foreach (var sourcePath in sourcePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (Images.Count >= ArtWorkspaceValidator.MaximumImages)
            {
                _statusMessage = $"已达到 {ArtWorkspaceValidator.MaximumImages} 张图片上限";
                break;
            }

            try
            {
                var imported = _repository.ImportAsset(_projectRoot, CaptureDocument(), sourcePath);
                AddImportedImage(imported, sourcePath, startX + added * 28d, startY + added * 28d);
                added++;
            }
            catch (Exception exception)
            {
                _console.Write(
                    WorkbenchLogLevel.Warning,
                    "ArtWorkspace",
                    $"跳过无法导入的图片 {Path.GetFileName(sourcePath)}：{exception.Message}",
                    exception: exception);
            }
        }

        if (added > 0)
        {
            _statusMessage = $"已导入 {added} 张图片到受管参考目录";
            MarkDirty();
        }
        else
        {
            _statusMessage = "没有导入可用图片";
            NotifySummaryChanged();
        }

        OnPropertyChanged(nameof(ImageCountText));
        NotifyReferenceAvailabilityChanged();
        OnPropertyChanged(nameof(StatusText));
        return added;
    }

    public async Task<int> AddImagesAtAsync(
        IEnumerable<string> sourcePaths,
        double? worldX = null,
        double? worldY = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (IsImporting || IsClearingConversation)
        {
            _statusMessage = "已有参考图片导入任务正在执行";
            NotifySummaryChanged();
            return 0;
        }

        var paths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        if (paths.Length == 0)
        {
            return 0;
        }

        var cancellation = new CancellationTokenSource();
        _importCancellation = cancellation;
        IsImporting = true;
        var added = 0;
        var startX = worldX ?? Math.Max(40d, OutputFrameX - 430d);
        var startY = worldY ?? OutputFrameY;
        try
        {
            for (var index = 0; index < paths.Length; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (Images.Count >= ArtWorkspaceValidator.MaximumImages)
                {
                    _statusMessage = $"已达到 {ArtWorkspaceValidator.MaximumImages} 张图片上限";
                    break;
                }

                var sourcePath = paths[index];
                _statusMessage = $"正在导入参考图片 {index + 1}/{paths.Length}：{Path.GetFileName(sourcePath)}";
                NotifySummaryChanged();
                try
                {
                    var snapshot = CaptureDocument();
                    var imported = await Task.Run(
                        () => _repository.ImportAsset(
                            _projectRoot,
                            snapshot,
                            sourcePath,
                            cancellation.Token),
                        cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    AddImportedImage(
                        imported,
                        sourcePath,
                        startX + added * 28d,
                        startY + added * 28d);
                    added++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _console.Write(
                        WorkbenchLogLevel.Warning,
                        "ArtWorkspace",
                        $"跳过无法导入的图片 {Path.GetFileName(sourcePath)}：{exception.Message}",
                        exception: exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _statusMessage = added > 0
                ? $"已取消导入；保留已完成的 {added} 张图片"
                : "已取消参考图片导入";
        }
        finally
        {
            if (ReferenceEquals(_importCancellation, cancellation))
            {
                _importCancellation = null;
            }

            cancellation.Dispose();
            IsImporting = false;
        }

        if (added > 0)
        {
            if (!_statusMessage.StartsWith("已取消", StringComparison.Ordinal))
            {
                _statusMessage = $"已导入 {added} 张图片到受管参考目录";
            }

            MarkDirty();
        }
        else if (!_statusMessage.StartsWith("已取消", StringComparison.Ordinal))
        {
            _statusMessage = "没有导入可用图片";
            NotifySummaryChanged();
        }

        OnPropertyChanged(nameof(ImageCountText));
        NotifyReferenceAvailabilityChanged();
        OnPropertyChanged(nameof(StatusText));
        return added;
    }

    public void SelectImage(ArtWorkspaceImageItemViewModel? image) =>
        SelectedImage = image is not null && Images.Contains(image) ? image : null;

    /// <summary>
    /// Captures the currently selected image and its current local region as an immutable editor
    /// token. Creating the token does not append it; the RichTextBox inserts it at its caret and
    /// then submits the resulting ordered segment list through <see cref="ReplaceInstructionDraftSegments"/>.
    /// </summary>
    public ArtWorkspaceInstructionDraftSegment? CreateSelectedImageReferenceDraftSegment()
    {
        if (!CanInsertSelectedImageReference || SelectedImage is not { } selected)
        {
            return null;
        }

        var region = _selectedImageRegion is not null &&
                     string.Equals(_selectedImageRegion.ImageId, selected.Id, StringComparison.Ordinal)
            ? new ArtWorkspaceGenerationRegion
            {
                X = _selectedImageRegion.X,
                Y = _selectedImageRegion.Y,
                Width = _selectedImageRegion.Width,
                Height = _selectedImageRegion.Height
            }
            : null;
        return ArtWorkspaceInstructionDraftSegment.CreateImageReference(
            selected.Id,
            selected.Name,
            region);
    }

    // Kept as a concise automation/test-facing alias. The editor itself still decides the caret.
    public ArtWorkspaceInstructionDraftSegment? InsertSelectedImageReference() =>
        CreateSelectedImageReferenceDraftSegment();

    public bool ReplaceInstructionDraftSegments(
        IEnumerable<ArtWorkspaceInstructionDraftSegment> segments,
        bool enforceBudget = true)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var normalized = NormalizeInstructionDraftSegments(segments);
        var semanticLength = MeasureInstructionDraftSemanticLength(normalized);
        if (enforceBudget &&
            semanticLength > ArtWorkspaceConversationMessageViewModel.MaximumTextLength)
        {
            _statusMessage =
                $"对话指令不能超过 {ArtWorkspaceConversationMessageViewModel.MaximumTextLength:N0} 个语义字符";
            NotifySummaryChanged();
            OnPropertyChanged(nameof(CanSubmitInstructionText));
            return false;
        }

        if (InstructionDraftSegmentsEqual(_instructionDraftSegments, normalized))
        {
            return true;
        }

        _instructionDraftSegments.Clear();
        _instructionDraftSegments.AddRange(normalized.Select(segment => segment.DeepClone()));
        var plainText = string.Concat(_instructionDraftSegments
            .Where(segment => segment.IsText)
            .Select(segment => segment.Text));
        if (!string.Equals(_instructionDraft, plainText, StringComparison.Ordinal))
        {
            _instructionDraft = plainText;
            OnPropertyChanged(nameof(InstructionDraft));
        }

        _instructionDraftRevision++;
        OnPropertyChanged(nameof(InstructionDraftSegments));
        OnPropertyChanged(nameof(InstructionDraftRevision));
        OnPropertyChanged(nameof(InstructionDraftSemanticLength));
        OnPropertyChanged(nameof(HasInstructionReferences));
        OnPropertyChanged(nameof(HasInvalidInstructionReferences));
        OnPropertyChanged(nameof(CanInsertSelectedImageReference));
        OnPropertyChanged(nameof(CanSubmitInstructionText));
        _submitInstructionCommand.RaiseCanExecuteChanged();
        return true;
    }

    public bool RemoveInstructionReference(string? tokenId)
    {
        if (string.IsNullOrWhiteSpace(tokenId) ||
            !_instructionDraftSegments.Any(segment =>
                segment.IsImageReference &&
                string.Equals(segment.TokenId, tokenId, StringComparison.Ordinal)))
        {
            return false;
        }

        return ReplaceInstructionDraftSegments(
            _instructionDraftSegments.Where(segment =>
                !segment.IsImageReference ||
                !string.Equals(segment.TokenId, tokenId, StringComparison.Ordinal)));
    }

    public void ClearInstructionDraftReferences() =>
        ReplaceInstructionDraftSegments(_instructionDraftSegments.Where(segment => segment.IsText));

    public bool IsInstructionReferenceValid(string? tokenId)
    {
        var token = _instructionDraftSegments.FirstOrDefault(segment =>
            segment.IsImageReference &&
            string.Equals(segment.TokenId, tokenId, StringComparison.Ordinal));
        if (token is null)
        {
            return false;
        }

        return IsInstructionReferenceValid(token);
    }

    public bool IsInstructionReferenceValid(ArtWorkspaceInstructionDraftSegment token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var image = Images.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, token.ImageId, StringComparison.Ordinal));
        return image is { IsVisible: true, IsMissing: false } && File.Exists(image.AbsolutePath);
    }

    public bool SetSelectedImageRegionNormalized(
        double x,
        double y,
        double width,
        double height,
        bool commit)
    {
        if (SelectedImage is not { IsVisible: true, IsMissing: false } selected ||
            !double.IsFinite(x) ||
            !double.IsFinite(y) ||
            !double.IsFinite(width) ||
            !double.IsFinite(height))
        {
            return false;
        }

        var left = Math.Clamp(Math.Min(x, x + width), 0d, 1d);
        var top = Math.Clamp(Math.Min(y, y + height), 0d, 1d);
        var right = Math.Clamp(Math.Max(x, x + width), 0d, 1d);
        var bottom = Math.Clamp(Math.Max(y, y + height), 0d, 1d);
        if (right - left <= ArtWorkspaceImageRegionGeometry.BoundaryTolerance ||
            bottom - top <= ArtWorkspaceImageRegionGeometry.BoundaryTolerance)
        {
            return false;
        }

        var region = new ArtWorkspaceImageRegionSelection
        {
            ImageId = selected.Id,
            X = left,
            Y = top,
            Width = right - left,
            Height = bottom - top
        };
        ArtWorkspaceImageRegionGeometry.ValidateRegion(region, selected.Id);
        ApplySelectedImageRegion(region, commit);
        return true;
    }

    public bool SetSelectedImageRegionFromWorldPoints(
        double startWorldX,
        double startWorldY,
        double endWorldX,
        double endWorldY,
        bool commit)
    {
        if (SelectedImage is not { IsVisible: true, IsMissing: false } selected)
        {
            return false;
        }

        try
        {
            var region = ArtWorkspaceImageRegionGeometry.FromWorldPoints(
                selected.Id,
                selected.X,
                selected.Y,
                selected.Width,
                selected.Height,
                selected.RotationDegrees,
                startWorldX,
                startWorldY,
                endWorldX,
                endWorldY);
            ApplySelectedImageRegion(region, commit);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public void ClearSelectedImageRegion(bool commit = true)
    {
        if (_selectedImageRegion is null)
        {
            return;
        }

        _selectedImageRegion = null;
        SelectedImage?.SetSelectedRegion(null);
        OnPropertyChanged(nameof(SelectedImageRegion));
        OnPropertyChanged(nameof(HasSelectedImageRegion));
        OnPropertyChanged(nameof(SelectedImageReferenceText));
        if (commit)
        {
            MarkDirty();
        }
    }

    private void ApplySelectedImageRegion(
        ArtWorkspaceImageRegionSelection region,
        bool commit)
    {
        _selectedImageRegion = CloneRegion(region);
        SelectedImage?.SetSelectedRegion(_selectedImageRegion);
        OnPropertyChanged(nameof(SelectedImageRegion));
        OnPropertyChanged(nameof(HasSelectedImageRegion));
        OnPropertyChanged(nameof(SelectedImageReferenceText));
        if (commit)
        {
            _statusMessage = $"已将 {SelectedImage?.Name ?? "所选图片"} 的局部区域绑定到下一条对话指令";
            MarkDirty();
        }
    }

    public void MoveImage(ArtWorkspaceImageItemViewModel image, double x, double y, bool commit)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!Images.Contains(image) || image.IsLocked)
        {
            return;
        }

        _suppressAutoSave = !commit;
        try
        {
            image.X = Math.Clamp(x, -100_000d, 100_000d);
            image.Y = Math.Clamp(y, -100_000d, 100_000d);
        }
        finally
        {
            _suppressAutoSave = false;
        }

        if (commit)
        {
            MarkDirty();
        }
    }

    public void SetOutputFrame(
        double x,
        double y,
        double width,
        double height,
        bool commit)
    {
        _suppressAutoSave = !commit;
        try
        {
            OutputFrameX = x;
            OutputFrameY = y;
            OutputFrameWidth = width;
            OutputFrameHeight = height;
        }
        finally
        {
            _suppressAutoSave = false;
        }

        if (commit)
        {
            MarkDirty();
        }
    }

    public void SetViewport(double panX, double panY, double zoom, bool commit)
    {
        var normalizedPanX = Math.Clamp(double.IsFinite(panX) ? panX : 0d, -1_000_000d, 1_000_000d);
        var normalizedPanY = Math.Clamp(double.IsFinite(panY) ? panY : 0d, -1_000_000d, 1_000_000d);
        var normalizedZoom = Math.Clamp(double.IsFinite(zoom) ? zoom : 1d, MinimumZoom, MaximumZoom);
        var changed = SetProperty(ref _panX, normalizedPanX, nameof(PanX));
        changed |= SetProperty(ref _panY, normalizedPanY, nameof(PanY));
        changed |= SetProperty(ref _zoom, normalizedZoom, nameof(Zoom));
        if (!changed)
        {
            return;
        }

        OnPropertyChanged(nameof(ZoomText));
        if (commit)
        {
            MarkDirty();
        }
    }

    public void ResetViewportToOrigin() => SetViewport(48d, 48d, 0.72d, commit: true);

    public void FocusOutputFrame()
    {
        const double targetZoom = 0.72d;
        SetViewport(
            90d - OutputFrameX * targetZoom,
            90d - OutputFrameY * targetZoom,
            targetZoom,
            commit: true);
    }

    public ArtWorkspaceWorldBounds GetContentBounds()
    {
        var minimumX = OutputFrameX;
        var minimumY = OutputFrameY;
        var maximumX = OutputFrameX + OutputFrameWidth;
        var maximumY = OutputFrameY + OutputFrameHeight;
        foreach (var image in Images.Where(candidate => candidate.IsVisible))
        {
            var radians = image.RotationDegrees * Math.PI / 180d;
            var cosine = Math.Abs(Math.Cos(radians));
            var sine = Math.Abs(Math.Sin(radians));
            var halfWidth = image.Width / 2d;
            var halfHeight = image.Height / 2d;
            var extentX = cosine * halfWidth + sine * halfHeight;
            var extentY = sine * halfWidth + cosine * halfHeight;
            var centerX = image.X + halfWidth;
            var centerY = image.Y + halfHeight;
            minimumX = Math.Min(minimumX, centerX - extentX);
            minimumY = Math.Min(minimumY, centerY - extentY);
            maximumX = Math.Max(maximumX, centerX + extentX);
            maximumY = Math.Max(maximumY, centerY + extentY);
        }

        return new ArtWorkspaceWorldBounds(
            minimumX,
            minimumY,
            Math.Max(1d, maximumX - minimumX),
            Math.Max(1d, maximumY - minimumY));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _importCancellation?.Cancel();
        _generationCancellation?.Cancel();
        if (_generationGateway is not null)
        {
            _generationGateway.StateChanged -= OnGenerationGatewayStateChanged;
            _generationGateway.DeferredGenerationCompleted -= OnDeferredGenerationCompleted;
        }

        _generationContexts.Clear();

        var importChangedDocument = IsImporting &&
                                    (Images.Count != _document.Images.Count ||
                                     _assets.Count != _document.Assets.Count);
        if (importChangedDocument && !string.IsNullOrWhiteSpace(_currentFilePath))
        {
            try
            {
                var document = CaptureDocument();
                _repository.Save(_projectRoot, document);
                _document = document;
            }
            catch (Exception exception)
            {
                _console.WriteError(
                    "ArtWorkspace",
                    $"关闭时保存美术工作区失败：{exception.Message}",
                    exception);
            }
        }
        else
        {
            _ = SavePendingChanges();
        }

        _disposed = true;
        _saveTimer.Stop();
        _saveTimer.Tick -= OnSaveTimerTick;
        _referenceAvailabilityTimer.Stop();
        _referenceAvailabilityTimer.Tick -= OnReferenceAvailabilityTimerTick;
        foreach (var image in Images)
        {
            image.Changed -= OnImageChanged;
        }
    }

    private async Task ImportImagesAsync()
    {
        try
        {
            var selected = _imagePicker.SelectImages(_lastImportDirectory);
            if (selected.Count > 0)
            {
                await AddImagesAtAsync(selected);
            }
        }
        catch (Exception exception)
        {
            _statusMessage = $"打开参考图片失败：{exception.Message}";
            NotifySummaryChanged();
            _console.WriteError("ArtWorkspace", _statusMessage, exception);
        }
    }

    private void CancelImport()
    {
        if (!IsImporting)
        {
            return;
        }

        _statusMessage = "正在取消参考图片导入…";
        NotifySummaryChanged();
        _importCancellation?.Cancel();
    }

    private bool CanSubmitInstruction() =>
        !IsImporting &&
        !IsClearingConversation &&
        !IsGenerating &&
        !string.IsNullOrWhiteSpace(_projectRoot) &&
        _generationGateway is { IsConnected: true, IsBusy: false } &&
        !HasMissingVisibleImages &&
        !HasInvalidInstructionReferences &&
        Conversation.Count <= ArtWorkspaceValidator.MaximumMessages - 2 &&
        HasInstructionDraftContent() &&
        InstructionDraftSemanticLength <= ArtWorkspaceConversationMessageViewModel.MaximumTextLength;

    private void InvalidateGenerationWait()
    {
        var cancellation = _generationCancellation;
        _activeGenerationRequestId = null;
        _generationCancellation = null;
        _generationContexts.Clear();
        IsGenerating = false;
        cancellation?.Cancel();
    }

    private async Task ClearConversationAsync()
    {
        if (_disposed || IsImporting || IsClearingConversation)
        {
            return;
        }

        IsClearingConversation = true;
        InvalidateGenerationWait();
        try
        {
            if (_clearConversationHandler is not null && !string.IsNullOrWhiteSpace(_projectRoot))
            {
                await _clearConversationHandler(_projectRoot, CancellationToken.None);
            }

            if (_disposed)
            {
                return;
            }

            Conversation.Clear();
            // Keep the draft so a user can retry the instruction they were already composing.
            GenerationProgressPercentage = 0d;
            GenerationProgressText = "对话已清除，旧美术任务已停止自动调度";
            MarkDirty();
            if (SaveNow())
            {
                _statusMessage = "已清除对话并暂停旧美术任务；当前草稿、画布图片与已落盘产物均保留";
            }
        }
        catch (Exception exception)
        {
            if (_disposed)
            {
                return;
            }

            _statusMessage = $"停止旧任务未完成，对话记录保留，请重试清除：{exception.Message}";
            GenerationProgressText = _statusMessage;
            _console.WriteError("ArtWorkspace", _statusMessage, exception);
        }
        finally
        {
            IsClearingConversation = false;
            RefreshGenerationGatewayState();
            NotifySummaryChanged();
        }
    }

    private async Task SubmitInstructionAsync()
    {
        var instruction = BuildLegacyInstructionText(_instructionDraftSegments);
        if (!CanSubmitInstruction())
        {
            return;
        }

        ArtWorkspaceGenerationRequest frozenRequest;
        try
        {
            frozenRequest = FreezeGenerationRequest(instruction, DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is
            InvalidDataException or
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            _statusMessage = $"无法冻结生成请求：{exception.Message}";
            NotifySummaryChanged();
            _console.WriteWarning("ArtWorkspace", _statusMessage);
            return;
        }

        var messages = CreateInstructionMessages(
            instruction,
            frozenRequest.CreatedAtUtc,
            _generationGateway?.AgentDisplayName,
            _instructionDraftSegments);
        try
        {
            var candidate = CaptureDocument();
            candidate.Conversation.AddRange(messages.Select(CloneMessage));
            // Reserve the worst-case JSON footprint of the mutable Agent placeholder now. This
            // keeps every later progress/final update saveable even when the document is already
            // close to the 16 MiB persistence boundary. System.Text.Json escapes this CJK code
            // unit as six ASCII bytes, which is the maximum JSON size of one UTF-16 code unit.
            var reservedAgentMessage = candidate.Conversation[^1];
            reservedAgentMessage.Text = MaximumSerializedAgentReplyReservation;
            reservedAgentMessage.State = MaximumSerializedAgentStateReservation;
            var byteCount = Encoding.UTF8.GetByteCount(ArtWorkspaceJson.Serialize(candidate));
            if (byteCount > ArtWorkspaceValidator.MaximumSerializedDocumentBytes)
            {
                _statusMessage = "创作指令未加入：工作区文件将超过 16 MiB 上限，请先归档旧对话";
                NotifySummaryChanged();
                _console.WriteWarning("ArtWorkspace", _statusMessage);
                return;
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            _statusMessage = $"创作指令未加入：{exception.Message}";
            NotifySummaryChanged();
            _console.WriteWarning("ArtWorkspace", _statusMessage);
            return;
        }

        foreach (var message in messages)
        {
            Conversation.Add(new ArtWorkspaceConversationMessageViewModel(message));
        }

        var agentMessage = Conversation[^1];
        InstructionDraft = string.Empty;
        _statusMessage = $"已冻结生成请求 {frozenRequest.RequestId[..8]}，正在提交 Agent";
        CreationRequested?.Invoke(
            this,
            new ArtWorkspaceCreationRequestedEventArgs(
                instruction,
                OutputFrameX,
                OutputFrameY,
                OutputFrameWidth,
                OutputFrameHeight,
                OutputRelativeDirectory,
                Images.Where(image => image.IsVisible).Select(image => image.Id).ToArray()));
        MarkDirty();

        var cancellation = new CancellationTokenSource();
        _generationCancellation = cancellation;
        _activeGenerationRequestId = frozenRequest.RequestId;
        _generationContexts[frozenRequest.RequestId] =
            new DeferredGenerationContext(frozenRequest, agentMessage);
        GenerationProgressPercentage = 0d;
        GenerationProgressText = "正在提交生成请求";
        IsGenerating = true;
        var keepContextForDeferredCompletion = false;

        var progress = new Progress<ArtWorkspaceGenerationProgress>(update =>
            ApplyGenerationProgress(frozenRequest, agentMessage, update));
        try
        {
            var gateway = _generationGateway
                ?? throw new InvalidOperationException("Agent 生成网关在提交前已被移除。");
            var result = await gateway.GenerateAsync(frozenRequest, progress, cancellation.Token)
                .WaitAsync(cancellation.Token);
            if (!IsRequestStillCurrent(frozenRequest))
            {
                _console.WriteWarning(
                    "ArtWorkspace",
                    $"生成请求 {frozenRequest.RequestId} 已结束，但活动项目已切换；结果未导入当前画布。");
                return;
            }

            if (!result.IsSuccess)
            {
                var failure = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? "Agent 未提供失败原因"
                    : WorkbenchConsoleService.RedactCommonCredentials(result.ErrorMessage.Trim());
                if (result.IsDeferred)
                {
                    keepContextForDeferredCompletion = true;
                    agentMessage.Update(
                        $"生成已转入后台等待恢复：{failure} 你可以继续发送新的创作指令；" +
                        "原任务完成后会在当前软件会话中自动导入，重启后仍会保留 Final 原件。",
                        "deferred");
                    GenerationProgressText = "本轮已转入后台，可继续发送";
                    _statusMessage = "生成调度已中断；持久任务保留，当前对话已恢复可编辑";
                    MarkDirty();
                    return;
                }

                agentMessage.Update($"生成未完成：{failure} 输入框已恢复；请按上述原因处理后重试。", "failed");
                GenerationProgressPercentage = 100d;
                GenerationProgressText = failure;
                _statusMessage = "Agent 未能完成本次美术生成，已恢复发送功能";
                MarkDirty();
                return;
            }

            var importedCount = await ImportGeneratedOutputsAsync(
                frozenRequest,
                result.OutputPaths,
                cancellation.Token);
            if (!IsRequestStillCurrent(frozenRequest))
            {
                _console.WriteWarning(
                    "ArtWorkspace",
                    $"生成请求 {frozenRequest.RequestId} 的产物导入期间项目已切换；未更新当前项目对话。");
                return;
            }

            var description = string.IsNullOrWhiteSpace(result.AgentDescription)
                ? $"图片已生成并导入输出框，共 {importedCount} 张。"
                : result.AgentDescription.Trim();
            agentMessage.Update(description, "completed");
            GenerationProgressPercentage = 100d;
            GenerationProgressText = $"生成完成 · 已导入 {importedCount} 张图片";
            _statusMessage = $"Agent 生成完成，{importedCount} 张产物已进入输出框";
            MarkDirty();
        }
        catch (OperationCanceledException)
        {
            if (IsRequestStillCurrent(frozenRequest))
            {
                agentMessage.Update("本次生成已取消；已落盘的中间产物仍由生成任务保留。", "canceled");
                GenerationProgressText = "生成已取消";
                _statusMessage = "已取消当前美术生成";
                MarkDirty();
            }
        }
        catch (Exception exception)
        {
            if (IsRequestStillCurrent(frozenRequest))
            {
                agentMessage.Update($"生成失败：{exception.Message}", "failed");
                GenerationProgressPercentage = 100d;
                GenerationProgressText = exception.Message;
                _statusMessage = "美术生成执行失败";
                MarkDirty();
            }

            _console.WriteError(
                "ArtWorkspace",
                $"生成请求 {frozenRequest.RequestId} 执行失败：{exception.Message}",
                exception);
        }
        finally
        {
            if (string.Equals(_activeGenerationRequestId, frozenRequest.RequestId, StringComparison.Ordinal))
            {
                _activeGenerationRequestId = null;
                _generationCancellation = null;
                IsGenerating = false;
                NotifySummaryChanged();
            }

            if (!keepContextForDeferredCompletion)
            {
                _generationContexts.Remove(frozenRequest.RequestId);
            }

            cancellation.Dispose();
        }
    }

    private ArtWorkspaceGenerationRequest FreezeGenerationRequest(
        string instruction,
        DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(_projectRoot))
        {
            throw new InvalidOperationException("本地草稿工作区不能启动 Agent 生成。");
        }

        var projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_projectRoot));
        var projectPaths = new ProjectPaths(projectRoot);
        var assetRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPaths.AssetDirectory));
        var canonicalOutputRelativeDirectory = NormalizeOutputDirectoryForNewGeneration(
            OutputRelativeDirectory);
        var outputDirectory = Path.GetFullPath(Path.Combine(
            projectRoot,
            canonicalOutputRelativeDirectory.Replace('/', Path.DirectorySeparatorChar)));
        if (!AssetRootService.PathEquals(projectPaths.FinalAssetDirectory, outputDirectory) &&
            !IsPathWithinRoot(projectPaths.FinalAssetDirectory, outputDirectory))
        {
            throw new InvalidDataException("美术工作区最终产物目录必须位于 Assets/Final 内。");
        }

        EnsureExistingSegmentsAreNotReparsePoints(projectRoot, outputDirectory, "美术工作区输出目录");

        var assetsById = _assets.ToDictionary(asset => asset.Id, StringComparer.Ordinal);
        var references = new List<ArtWorkspaceGenerationReferenceSnapshot>();
        foreach (var image in Images.Where(candidate => candidate.IsVisible).OrderBy(candidate => candidate.ZIndex))
        {
            if (!assetsById.TryGetValue(image.AssetId, out var asset))
            {
                throw new InvalidDataException($"可见图片 {image.Name} 缺少受管素材元数据。");
            }

            var absolutePath = Path.GetFullPath(image.AbsolutePath);
            EnsurePathWithinAnyRoot(
                [
                    projectPaths.ArtWorkspaceReferenceDirectory,
                    projectPaths.LegacyArtWorkspaceReferenceDirectory
                ],
                absolutePath,
                $"参考图片 {image.Name}");
            EnsureExistingSegmentsAreNotReparsePoints(projectRoot, absolutePath, $"参考图片 {image.Name}");
            if (!File.Exists(absolutePath))
            {
                throw new FileNotFoundException($"可见参考图片不存在：{image.Name}", absolutePath);
            }

            references.Add(new ArtWorkspaceGenerationReferenceSnapshot
            {
                ImageId = image.Id,
                AssetId = image.AssetId,
                Name = image.Name,
                AbsolutePath = absolutePath,
                Sha256 = asset.Sha256,
                MediaType = asset.MediaType,
                PixelWidth = image.PixelWidth,
                PixelHeight = image.PixelHeight,
                X = image.X,
                Y = image.Y,
                Width = image.Width,
                Height = image.Height,
                RotationDegrees = image.RotationDegrees,
                Opacity = image.Opacity,
                ZIndex = image.ZIndex
            });
        }

        var instructionSegments = _instructionDraftSegments
            .Select(segment => segment.ToGenerationSegment())
            .ToArray();
        foreach (var segment in instructionSegments.Where(segment => string.Equals(
                     segment.Kind,
                     ArtWorkspaceGenerationInstructionSegmentKinds.ImageReference,
                     StringComparison.Ordinal)))
        {
            if (!references.Any(reference =>
                    string.Equals(reference.ImageId, segment.ImageId, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("对话中的图片引用已失效，请删除对应标签或恢复该图片。");
            }
        }

        return new ArtWorkspaceGenerationRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = createdAtUtc,
            ProjectRoot = projectRoot,
            AssetRoot = assetRoot,
            Instruction = instruction,
            OutputFrame = new ArtWorkspaceGenerationOutputFrameSnapshot
            {
                Name = OutputFrameName,
                X = OutputFrameX,
                Y = OutputFrameY,
                Width = OutputFrameWidth,
                Height = OutputFrameHeight,
                PixelWidth = checked((int)Math.Round(OutputFrameWidth, MidpointRounding.AwayFromZero)),
                PixelHeight = checked((int)Math.Round(OutputFrameHeight, MidpointRounding.AwayFromZero)),
                BackgroundHex = OutputBackgroundHex,
                OutputRelativeDirectory = canonicalOutputRelativeDirectory,
                OutputDirectoryPath = outputDirectory
            },
            VisibleReferences = references.ToArray(),
            InstructionSegments = instructionSegments,
            InstructionReference = null
        };
    }

    private void ApplyGenerationProgress(
        ArtWorkspaceGenerationRequest request,
        ArtWorkspaceConversationMessageViewModel agentMessage,
        ArtWorkspaceGenerationProgress update)
    {
        if (!IsRequestStillCurrent(request) || !Conversation.Contains(agentMessage))
        {
            return;
        }

        var message = string.IsNullOrWhiteSpace(update.AgentDescription)
            ? update.Message
            : update.AgentDescription;
        if (!string.IsNullOrWhiteSpace(message))
        {
            agentMessage.Update(message.Trim(), update.State);
            GenerationProgressText = message.Trim();
            MarkDirty();
        }

        if (update.Percentage is { } percentage)
        {
            GenerationProgressPercentage = percentage;
        }
    }

    private async Task<int> ImportGeneratedOutputsAsync(
        ArtWorkspaceGenerationRequest request,
        IEnumerable<string> outputPaths,
        CancellationToken cancellationToken,
        bool requireActiveRequest = true)
    {
        ArgumentNullException.ThrowIfNull(outputPaths);
        var validatedPaths = outputPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => ValidateGeneratedOutputPath(request, path))
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .ToArray();
        if (validatedPaths.Length == 0)
        {
            throw new InvalidDataException("Agent 报告成功，但没有返回位于冻结项目 Assets 内的可用图片路径。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!IsGenerationRequestApplicable(request, requireActiveRequest))
        {
            return 0;
        }

        var previousCount = Images.Count;
        var added = await AddImagesAtAsync(validatedPaths, request.OutputFrame.X, request.OutputFrame.Y);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsGenerationRequestApplicable(request, requireActiveRequest))
        {
            return 0;
        }

        if (added <= 0)
        {
            throw new InvalidDataException("生成图片已落盘，但无法复制为美术工作区的受管画布素材。");
        }

        var generatedImages = Images.Skip(previousCount).Take(added).ToArray();
        for (var index = 0; index < generatedImages.Length; index++)
        {
            var image = generatedImages[index];
            image.Name = generatedImages.Length == 1
                ? request.OutputFrame.Name
                : $"{request.OutputFrame.Name} {index + 1}";
            image.X = request.OutputFrame.X;
            image.Y = request.OutputFrame.Y;
            image.Width = request.OutputFrame.Width;
            image.Height = request.OutputFrame.Height;
            image.RotationDegrees = 0d;
            image.Opacity = 1d;
        }

        if (generatedImages.Length > 0)
        {
            SelectImage(generatedImages[^1]);
        }

        _statusMessage = $"已保留 Final 原件，并复制 {added} 张受管画布预览到输出框";
        MarkDirty();
        return added;
    }

    private static string ValidateGeneratedOutputPath(
        ArtWorkspaceGenerationRequest request,
        string outputPath)
    {
        if (!Path.IsPathRooted(outputPath))
        {
            throw new InvalidDataException("Agent 返回的生成产物路径必须是绝对路径。");
        }

        var fullPath = Path.GetFullPath(outputPath);
        EnsurePathWithinRoot(request.AssetRoot, fullPath, "Agent 生成产物");
        EnsurePathWithinRoot(request.OutputFrame.OutputDirectoryPath, fullPath, "Agent 生成产物");
        EnsureExistingSegmentsAreNotReparsePoints(request.ProjectRoot, fullPath, "Agent 生成产物");
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Agent 返回的生成产物文件不存在。", fullPath);
        }

        return fullPath;
    }

    private bool IsRequestStillCurrent(ArtWorkspaceGenerationRequest request) =>
        string.Equals(_activeGenerationRequestId, request.RequestId, StringComparison.Ordinal) &&
        PathsEqual(_projectRoot, request.ProjectRoot);

    private bool IsGenerationRequestApplicable(
        ArtWorkspaceGenerationRequest request,
        bool requireActiveRequest) =>
        PathsEqual(_projectRoot, request.ProjectRoot) &&
        (!requireActiveRequest ||
         string.Equals(_activeGenerationRequestId, request.RequestId, StringComparison.Ordinal));

    private void OnDeferredGenerationCompleted(
        object? sender,
        ArtWorkspaceDeferredGenerationCompletedEventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        void BeginApply() => _ = ApplyDeferredGenerationCompletedAsync(e);
        if (_dispatcher.CheckAccess())
        {
            BeginApply();
        }
        else
        {
            _dispatcher.BeginInvoke((Action)BeginApply, DispatcherPriority.DataBind);
        }
    }

    private async Task ApplyDeferredGenerationCompletedAsync(
        ArtWorkspaceDeferredGenerationCompletedEventArgs e)
    {
        if (!_generationContexts.Remove(e.Request.RequestId, out var context))
        {
            return;
        }

        try
        {
            if (!PathsEqual(_projectRoot, e.Request.ProjectRoot) ||
                !Conversation.Contains(context.AgentMessage))
            {
                _console.WriteInfo(
                    "ArtWorkspace",
                    $"后台美术任务 {e.Result.TaskId ?? e.Request.RequestId} 已结束；" +
                    "原项目当前未激活，Final 原件保持在冻结目录中，可稍后手动导入。");
                return;
            }

            if (!e.Result.IsSuccess)
            {
                var failure = string.IsNullOrWhiteSpace(e.Result.ErrorMessage)
                    ? "后台任务未提供失败原因"
                    : e.Result.ErrorMessage.Trim();
                context.AgentMessage.Update($"后台生成最终失败：{failure} 可以继续修改指令后再次发送。", "failed");
                _statusMessage = "后台美术任务失败；当前对话仍可继续";
                MarkDirty();
                return;
            }

            var importedCount = await ImportGeneratedOutputsAsync(
                e.Request,
                e.Result.OutputPaths,
                CancellationToken.None,
                requireActiveRequest: false);
            if (!PathsEqual(_projectRoot, e.Request.ProjectRoot) ||
                !Conversation.Contains(context.AgentMessage))
            {
                return;
            }
            var description = string.IsNullOrWhiteSpace(e.Result.AgentDescription)
                ? $"后台图片已生成并导入输出框，共 {importedCount} 张。"
                : e.Result.AgentDescription.Trim();
            context.AgentMessage.Update(description, "completed");
            _statusMessage = $"后台 Agent 生成完成，{importedCount} 张产物已进入输出框";
            if (!IsGenerating)
            {
                GenerationProgressPercentage = 100d;
                GenerationProgressText = $"后台生成完成 · 已导入 {importedCount} 张图片";
            }

            MarkDirty();
        }
        catch (Exception exception)
        {
            if (!PathsEqual(_projectRoot, e.Request.ProjectRoot) ||
                !Conversation.Contains(context.AgentMessage))
            {
                return;
            }

            context.AgentMessage.Update(
                $"后台图片已落盘，但自动导入画布失败：{exception.Message} Final 原件仍保留。",
                "failed");
            _statusMessage = "后台产物自动导入失败；Final 原件仍保留";
            MarkDirty();
            _console.WriteError(
                "ArtWorkspace",
                $"后台生成请求 {e.Request.RequestId} 的产物导入失败：{exception.Message}",
                exception);
        }
        finally
        {
            NotifySummaryChanged();
        }
    }

    private void OnGenerationGatewayStateChanged(object? sender, EventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            RefreshGenerationGatewayState();
        }
        else
        {
            _dispatcher.BeginInvoke(RefreshGenerationGatewayState, DispatcherPriority.DataBind);
        }
    }

    private void RefreshGenerationGatewayState()
    {
        OnPropertyChanged(nameof(IsAgentConnected));
        OnPropertyChanged(nameof(IsAgentBusy));
        OnPropertyChanged(nameof(AgentConnectionStatusText));
        OnPropertyChanged(nameof(CanSubmitInstructionText));
        _submitInstructionCommand.RaiseCanExecuteChanged();
    }

    private sealed record DeferredGenerationContext(
        ArtWorkspaceGenerationRequest Request,
        ArtWorkspaceConversationMessageViewModel AgentMessage);

    private static void EnsurePathWithinRoot(string rootPath, string candidatePath, string label)
    {
        if (!IsPathWithinRoot(rootPath, candidatePath))
        {
            throw new InvalidDataException($"{label}必须位于受信根目录内。");
        }
    }

    private static void EnsurePathWithinAnyRoot(
        IReadOnlyList<string> rootPaths,
        string candidatePath,
        string label)
    {
        if (!rootPaths.Any(rootPath => IsPathWithinRoot(rootPath, candidatePath)))
        {
            throw new InvalidDataException($"{label}必须位于当前或兼容的受信根目录内。");
        }
    }

    private static bool IsPathWithinRoot(string rootPath, string candidatePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var candidate = Path.GetFullPath(candidatePath);
        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) &&
               relative is not ("." or "..") &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static void EnsureExistingSegmentsAreNotReparsePoints(
        string rootPath,
        string candidatePath,
        string label)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var candidate = Path.GetFullPath(candidatePath);
        EnsurePathWithinRoot(root, candidate, label);
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        if (File.Exists(current) || Directory.Exists(current))
        {
            EnsureNotReparsePoint(current, label);
        }

        foreach (var segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            EnsureNotReparsePoint(current, label);
        }
    }

    private static void EnsureNotReparsePoint(string path, string label)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{label}不能经过符号链接或目录联接。");
        }
    }

    private void RemoveSelectedImage()
    {
        if (SelectedImage is not { } selected)
        {
            return;
        }

        selected.Changed -= OnImageChanged;
        Images.Remove(selected);
        if (Images.All(image => !string.Equals(image.AssetId, selected.AssetId, StringComparison.Ordinal)))
        {
            _assets.RemoveAll(asset => string.Equals(asset.Id, selected.AssetId, StringComparison.Ordinal));
        }

        SelectedImage = null;
        _statusMessage = $"已从画布移除 {selected.Name}；受管源文件仍保留供审计与复用";
        OnPropertyChanged(nameof(ImageCountText));
        NotifyInstructionReferenceStateChanged();
        NotifyReferenceAvailabilityChanged();
        MarkDirty();
    }

    private void BringSelectedToFront()
    {
        if (SelectedImage is not { } selected)
        {
            return;
        }

        selected.ZIndex = NextZIndex();
        _statusMessage = $"已将 {selected.Name} 移到最上层";
        MarkDirty();
    }

    private void ApplyDocument(string? projectRoot, ArtWorkspaceLoadResult result)
    {
        var nextDocument = result.Document;
        var nextAssets = nextDocument.Assets.Select(CloneAsset).ToList();
        var assetLookup = nextAssets.ToDictionary(asset => asset.Id, StringComparer.Ordinal);
        var nextImages = new List<ArtWorkspaceImageItemViewModel>(nextDocument.Images.Count);
        foreach (var image in nextDocument.Images.OrderBy(image => image.ZIndex))
        {
            var asset = assetLookup[image.AssetId];
            nextImages.Add(new ArtWorkspaceImageItemViewModel(
                CloneImage(image),
                _repository.ResolveAssetPath(projectRoot, asset.RelativePath),
                asset.PixelWidth,
                asset.PixelHeight));
        }

        var nextConversation = nextDocument.Conversation
            .Select(message => new ArtWorkspaceConversationMessageViewModel(CloneMessage(message)))
            .ToList();
        var restoredInterruptedMessages = false;
        foreach (var message in nextConversation.Where(message => message.IsAgent))
        {
            if (message.State.ToLowerInvariant() is "completed" or "failed" or "canceled" or "cancelled" or "interrupted")
            {
                continue;
            }

            message.Update(message.Text, "interrupted");
            restoredInterruptedMessages = true;
        }
        var nextSelectedImage = nextImages.FirstOrDefault(image =>
            string.Equals(image.Id, nextDocument.SelectedImageId, StringComparison.Ordinal));
        var nextSelectedRegion = nextSelectedImage is { IsVisible: true, IsMissing: false } &&
                                 nextDocument.SelectedImageRegion is { } persistedRegion &&
                                 string.Equals(persistedRegion.ImageId, nextSelectedImage.Id, StringComparison.Ordinal)
            ? CloneRegion(persistedRegion)
            : null;
        var discardedInvalidSelectedRegion = nextDocument.SelectedImageRegion is not null &&
                                             nextSelectedRegion is null;
        if (discardedInvalidSelectedRegion)
        {
            nextDocument.SelectedImageRegion = null;
        }

        _suppressAutoSave = true;
        try
        {
            foreach (var image in Images)
            {
                image.Changed -= OnImageChanged;
            }

            Images.Clear();
            Conversation.Clear();
            _assets.Clear();
            _instructionDraftSegments.Clear();
            _instructionDraft = string.Empty;
            _instructionDraftRevision++;
            _document = nextDocument;
            _projectRoot = projectRoot;
            _currentFilePath = result.FilePath;
            _canvasWidth = nextDocument.CanvasWidth;
            _canvasHeight = nextDocument.CanvasHeight;
            _panX = nextDocument.Viewport.PanX;
            _panY = nextDocument.Viewport.PanY;
            _zoom = nextDocument.Viewport.Zoom;
            _outputFrameX = nextDocument.OutputFrame.X;
            _outputFrameY = nextDocument.OutputFrame.Y;
            _outputFrameWidth = nextDocument.OutputFrame.Width;
            _outputFrameHeight = nextDocument.OutputFrame.Height;
            _outputFrameName = nextDocument.OutputFrame.Name;
            _outputBackgroundHex = nextDocument.OutputFrame.BackgroundHex;
            _outputRelativeDirectory = NormalizeOutputDirectoryForNewGeneration(
                nextDocument.OutputFrame.OutputRelativeDirectory);
            _assets.AddRange(nextAssets);

            foreach (var item in nextImages)
            {
                SubscribeImage(item);
                Images.Add(item);
            }

            foreach (var message in nextConversation)
            {
                Conversation.Add(message);
            }

            _selectedImage = nextSelectedImage;
            _selectedImageRegion = nextSelectedRegion;
            _activeToolMode = ArtWorkspaceToolMode.SelectMove;
            if (_selectedImage is not null)
            {
                _selectedImage.IsSelected = true;
                _selectedImage.SetSelectedRegion(_selectedImageRegion);
            }

            _hasPendingSave = false;
            _autoSaveRetryCount = 0;
            _saveTimer.Interval = AutoSaveDelay;
            _statusMessage = result.RecoverySource switch
            {
                "backup" => "已从备份恢复工作区",
                "safe-default" => "已加载安全空白工作区",
                "default" => "已创建新的美术工作区",
                _ => "工作区已加载"
            };
        }
        finally
        {
            _suppressAutoSave = false;
        }

        NotifyAllChanged();
        GenerationProgressPercentage = 0d;
        GenerationProgressText = restoredInterruptedMessages ? "旧会话已中断，可继续发送新指令" : "尚未启动生成";
        if (restoredInterruptedMessages)
        {
            MarkDirty();
        }
        if (discardedInvalidSelectedRegion)
        {
            _statusMessage = "已清除隐藏或缺失图片上的无效框选区域";
            MarkDirty();
        }
    }

    private ArtWorkspaceDocument CaptureDocument()
    {
        var referencedAssetIds = Images
            .Select(image => image.AssetId)
            .ToHashSet(StringComparer.Ordinal);
        return new ArtWorkspaceDocument
        {
            SchemaVersion = ArtWorkspaceJson.CurrentSchemaVersion,
            Id = _document.Id,
            Name = _document.Name,
            CreatedAtUtc = _document.CreatedAtUtc,
            UpdatedAtUtc = _document.UpdatedAtUtc,
            CanvasWidth = CanvasWidth,
            CanvasHeight = CanvasHeight,
            Viewport = new ArtWorkspaceViewport
            {
                PanX = PanX,
                PanY = PanY,
                Zoom = Zoom
            },
            OutputFrame = new ArtWorkspaceOutputFrame
            {
                Id = _document.OutputFrame.Id,
                Name = OutputFrameName,
                X = OutputFrameX,
                Y = OutputFrameY,
                Width = OutputFrameWidth,
                Height = OutputFrameHeight,
                BackgroundHex = OutputBackgroundHex,
                OutputRelativeDirectory = OutputRelativeDirectory
            },
            Assets = _assets
                .Where(asset => referencedAssetIds.Contains(asset.Id))
                .Select(CloneAsset)
                .ToList(),
            Images = Images.Select(image => image.ToModel()).ToList(),
            Conversation = Conversation.Select(message => message.ToModel()).ToList(),
            SelectedImageId = SelectedImage?.Id,
            SelectedImageRegion = _selectedImageRegion is null ? null : CloneRegion(_selectedImageRegion)
        };
    }

    private void SetOutputValue(
        ref double field,
        double value,
        double minimum,
        double maximum,
        string propertyName)
    {
        var normalized = Math.Clamp(double.IsFinite(value) ? value : minimum, minimum, maximum);
        if (SetProperty(ref field, normalized, propertyName))
        {
            OnPropertyChanged(nameof(OutputFrameSummary));
            OnPropertyChanged(nameof(StatusText));
            MarkDirty();
        }
    }

    private void SubscribeImage(ArtWorkspaceImageItemViewModel image) =>
        image.Changed += OnImageChanged;

    private void OnImageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, SelectedImage))
        {
            OnPropertyChanged(nameof(SelectedImage));
            if (e.PropertyName is nameof(ArtWorkspaceImageItemViewModel.Name) or
                nameof(ArtWorkspaceImageItemViewModel.IsVisible))
            {
                OnPropertyChanged(nameof(SelectedImageReferenceText));
            }

            _imageRegionSelectToolCommand.RaiseCanExecuteChanged();
            if (e.PropertyName == nameof(ArtWorkspaceImageItemViewModel.IsVisible) &&
                SelectedImage is { IsVisible: false })
            {
                ClearSelectedImageRegion();
                ActiveToolMode = ArtWorkspaceToolMode.SelectMove;
            }
        }

        // Inline tokens freeze their display name and local region when inserted. Canvas transform,
        // opacity, z-order, locking, and later renames therefore do not change the draft token and
        // must not rebuild the RichTextBox (which would discard its caret, selection, and undo
        // history). Visibility is the only image property changed through this event that can make
        // an existing token invalid; missing-file transitions are handled by the availability timer.
        if (e.PropertyName == nameof(ArtWorkspaceImageItemViewModel.IsVisible))
        {
            NotifyInstructionReferenceStateChanged();
        }

        NotifyReferenceAvailabilityChanged();
        MarkDirty();
    }

    private void MarkDirty()
    {
        if (_suppressAutoSave || _disposed || string.IsNullOrWhiteSpace(_currentFilePath))
        {
            return;
        }

        _hasPendingSave = true;
        ScheduleAutoSave(resetRetryCount: true);

        NotifySummaryChanged();
    }

    private void OnSaveTimerTick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        if (_hasPendingSave)
        {
            _ = SaveNowCore(explicitRequest: false, scheduleRetry: true);
        }
    }

    private void ScheduleAutoSave(bool resetRetryCount)
    {
        _saveTimer.Stop();
        if (resetRetryCount)
        {
            _autoSaveRetryCount = 0;
            _saveTimer.Interval = AutoSaveDelay;
        }

        if (!IsImporting && _hasPendingSave)
        {
            _saveTimer.Start();
        }
    }

    private void NotifyAllChanged()
    {
        _lastHasMissingVisibleImages = HasMissingVisibleImages;
        _missingAvailabilityInitialized = true;
        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));
        OnPropertyChanged(nameof(PanX));
        OnPropertyChanged(nameof(PanY));
        OnPropertyChanged(nameof(Zoom));
        OnPropertyChanged(nameof(ZoomText));
        OnPropertyChanged(nameof(OutputFrameX));
        OnPropertyChanged(nameof(OutputFrameY));
        OnPropertyChanged(nameof(OutputFrameWidth));
        OnPropertyChanged(nameof(OutputFrameHeight));
        OnPropertyChanged(nameof(OutputFrameName));
        OnPropertyChanged(nameof(OutputBackgroundHex));
        OnPropertyChanged(nameof(OutputRelativeDirectory));
        OnPropertyChanged(nameof(OutputFrameSummary));
        OnPropertyChanged(nameof(InstructionDraft));
        OnPropertyChanged(nameof(InstructionDraftSegments));
        OnPropertyChanged(nameof(InstructionDraftRevision));
        OnPropertyChanged(nameof(InstructionDraftSemanticLength));
        OnPropertyChanged(nameof(HasInstructionReferences));
        OnPropertyChanged(nameof(HasInvalidInstructionReferences));
        OnPropertyChanged(nameof(CanInsertSelectedImageReference));
        OnPropertyChanged(nameof(SelectedImage));
        OnPropertyChanged(nameof(HasSelectedImage));
        OnPropertyChanged(nameof(HasMissingVisibleImages));
        OnPropertyChanged(nameof(SelectedImageRegion));
        OnPropertyChanged(nameof(HasSelectedImageRegion));
        OnPropertyChanged(nameof(SelectedImageReferenceText));
        OnPropertyChanged(nameof(ActiveToolMode));
        OnPropertyChanged(nameof(IsSelectMoveToolActive));
        OnPropertyChanged(nameof(IsImageRegionSelectToolActive));
        OnPropertyChanged(nameof(ImageCountText));
        OnPropertyChanged(nameof(ProjectContextText));
        OnPropertyChanged(nameof(AgentConnectionStatusText));
        OnPropertyChanged(nameof(CanSubmitInstructionText));
        NotifySummaryChanged();
        _submitInstructionCommand.RaiseCanExecuteChanged();
        _removeSelectedImageCommand.RaiseCanExecuteChanged();
        _bringSelectedToFrontCommand.RaiseCanExecuteChanged();
        _imageRegionSelectToolCommand.RaiseCanExecuteChanged();
    }

    private void OnReferenceAvailabilityTimerTick(object? sender, EventArgs e)
    {
        var changed = false;
        foreach (var image in Images)
        {
            changed |= image.RefreshMissingState();
        }

        if (changed)
        {
            if (SelectedImage is { IsMissing: true })
            {
                ClearSelectedImageRegion();
                ActiveToolMode = ArtWorkspaceToolMode.SelectMove;
            }

            NotifyReferenceAvailabilityChanged(force: true);
        }
    }

    private void NotifyReferenceAvailabilityChanged(bool force = false)
    {
        var hasMissingVisibleImages = HasMissingVisibleImages;
        var availabilityChanged = !_missingAvailabilityInitialized ||
                                  _lastHasMissingVisibleImages != hasMissingVisibleImages;
        _lastHasMissingVisibleImages = hasMissingVisibleImages;
        _missingAvailabilityInitialized = true;
        if (!force && !availabilityChanged)
        {
            return;
        }

        OnPropertyChanged(nameof(HasMissingVisibleImages));
        OnPropertyChanged(nameof(SelectedImageReferenceText));
        OnPropertyChanged(nameof(CanInsertSelectedImageReference));
        NotifyInstructionReferenceStateChanged();
        OnPropertyChanged(nameof(CanSubmitInstructionText));
        _submitInstructionCommand.RaiseCanExecuteChanged();
        _imageRegionSelectToolCommand.RaiseCanExecuteChanged();
    }

    private void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PersistenceText));
    }

    private void NotifyInstructionReferenceStateChanged()
    {
        OnPropertyChanged(nameof(HasInvalidInstructionReferences));
        OnPropertyChanged(nameof(CanInsertSelectedImageReference));
        OnPropertyChanged(nameof(CanSubmitInstructionText));
        _submitInstructionCommand.RaiseCanExecuteChanged();
        if (!HasInstructionReferences)
        {
            return;
        }

        _instructionDraftRevision++;
        OnPropertyChanged(nameof(InstructionDraftRevision));
    }

    private static double CalculateDisplayScale(int pixelWidth, int pixelHeight)
    {
        var maximum = Math.Max(pixelWidth, pixelHeight);
        var minimum = Math.Min(pixelWidth, pixelHeight);
        if (maximum <= 0)
        {
            return 1d;
        }

        double preferredScale;
        if (maximum < 96)
        {
            preferredScale = 96d / maximum;
        }
        else
        {
            preferredScale = Math.Min(1d, 420d / maximum);
        }

        return Math.Max(preferredScale, minimum <= 0 ? 1d : 1d / minimum);
    }

    private static string NormalizeOutputDirectoryForNewGeneration(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(normalized, "Assets/Generated/ArtWorkspace", StringComparison.OrdinalIgnoreCase))
        {
            return "Assets/Final";
        }

        const string legacyPrefix = "Assets/Generated/ArtWorkspace/";
        if (normalized.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return $"Assets/Final/{normalized[legacyPrefix.Length..]}";
        }

        if (string.Equals(normalized, "Assets/Final", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Assets/Final/", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (IsSafeAssetsDirectory(normalized))
        {
            return $"Assets/Final/{normalized["Assets/".Length..]}";
        }

        throw new InvalidDataException("美术工作区最终产物目录必须位于 Assets/Final 内。");
    }

    private static bool IsSafeAssetsDirectory(string path)
    {
        if (Path.IsPathRooted(path) ||
            !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");
    }

    private void AddImportedImage(
        ArtWorkspaceImportedAsset imported,
        string sourcePath,
        double x,
        double y)
    {
        PruneUnusedAssetMetadata();
        if (_assets.All(asset => !string.Equals(asset.Id, imported.Asset.Id, StringComparison.Ordinal)))
        {
            if (_assets.Count >= ArtWorkspaceValidator.MaximumAssets)
            {
                throw new InvalidDataException(
                    $"美术工作区最多允许 {ArtWorkspaceValidator.MaximumAssets} 个在用受管素材。");
            }

            _assets.Add(CloneAsset(imported.Asset));
        }

        var scale = CalculateDisplayScale(imported.Asset.PixelWidth, imported.Asset.PixelHeight);
        var item = new ArtWorkspaceImageItemViewModel(
            new ArtWorkspaceImageItem
            {
                AssetId = imported.Asset.Id,
                Name = Path.GetFileNameWithoutExtension(imported.Asset.OriginalFileName),
                X = x,
                Y = y,
                Width = imported.Asset.PixelWidth * scale,
                Height = imported.Asset.PixelHeight * scale,
                ZIndex = NextZIndex()
            },
            imported.AbsolutePath,
            imported.Asset.PixelWidth,
            imported.Asset.PixelHeight);
        SubscribeImage(item);
        Images.Add(item);
        SelectedImage = item;
        _lastImportDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
    }

    private void PruneUnusedAssetMetadata()
    {
        var referencedAssetIds = Images
            .Select(image => image.AssetId)
            .ToHashSet(StringComparer.Ordinal);
        _assets.RemoveAll(asset => !referencedAssetIds.Contains(asset.Id));
    }

    private int NextZIndex()
    {
        if (Images.Count == 0)
        {
            return 1;
        }

        var maximum = Images.Max(image => image.ZIndex);
        if (maximum < 1_000_000)
        {
            return maximum + 1;
        }

        _suppressAutoSave = true;
        try
        {
            var ordered = Images
                .Select((image, index) => (image, index))
                .OrderBy(entry => entry.image.ZIndex)
                .ThenBy(entry => entry.index)
                .Select(entry => entry.image)
                .ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                ordered[index].ZIndex = index + 1;
            }
        }
        finally
        {
            _suppressAutoSave = false;
        }

        return Images.Count + 1;
    }

    private static IReadOnlyList<ArtWorkspaceConversationMessage> CreateInstructionMessages(
        string instruction,
        DateTimeOffset createdAtUtc,
        string? agentDisplayName,
        IReadOnlyList<ArtWorkspaceInstructionDraftSegment> draftSegments)
    {
        var conversationSegments = new List<ArtWorkspaceConversationSegment>(draftSegments.Count);
        var referenceOrdinal = 0;
        foreach (var segment in draftSegments)
        {
            if (segment.IsText)
            {
                conversationSegments.Add(new ArtWorkspaceConversationSegment
                {
                    Kind = ArtWorkspaceConversationSegmentKinds.Text,
                    Text = segment.Text
                });
                continue;
            }

            if (!segment.IsImageReference)
            {
                continue;
            }

            var displayLabel = ArtWorkspaceGenerationConstraintPackageBuilder
                .GetInstructionReferenceLabel(referenceOrdinal++);
            conversationSegments.Add(new ArtWorkspaceConversationSegment
            {
                Kind = ArtWorkspaceConversationSegmentKinds.ImageReference,
                TokenId = segment.TokenId,
                ImageId = segment.ImageId,
                ImageName = segment.ImageName,
                DisplayLabel = displayLabel,
                Region = segment.Region is null
                    ? null
                    : new ArtWorkspaceImageRegionSelection
                    {
                        ImageId = segment.ImageId,
                        X = segment.Region.X,
                        Y = segment.Region.Y,
                        Width = segment.Region.Width,
                        Height = segment.Region.Height
                    }
            });
        }

        return
        [
        new ArtWorkspaceConversationMessage
        {
            Role = ArtWorkspaceMessageRole.User,
            Text = instruction,
            CreatedAtUtc = createdAtUtc,
            State = "captured",
            Segments = conversationSegments
        },
        new ArtWorkspaceConversationMessage
        {
            Role = ArtWorkspaceMessageRole.Agent,
            Text = string.IsNullOrWhiteSpace(agentDisplayName)
                ? "Agent 已收到冻结请求，正在准备生成。"
                : $"{agentDisplayName.Trim()} 已收到冻结请求，正在准备生成。",
            CreatedAtUtc = createdAtUtc,
            State = "queued"
        }
        ];
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static ArtWorkspaceManagedAsset CloneAsset(ArtWorkspaceManagedAsset asset) => new()
    {
        Id = asset.Id,
        RelativePath = asset.RelativePath,
        OriginalFileName = asset.OriginalFileName,
        MediaType = asset.MediaType,
        Sha256 = asset.Sha256,
        SizeBytes = asset.SizeBytes,
        PixelWidth = asset.PixelWidth,
        PixelHeight = asset.PixelHeight,
        ImportedAtUtc = asset.ImportedAtUtc
    };

    private static ArtWorkspaceImageItem CloneImage(ArtWorkspaceImageItem image) => new()
    {
        Id = image.Id,
        AssetId = image.AssetId,
        Name = image.Name,
        X = image.X,
        Y = image.Y,
        Width = image.Width,
        Height = image.Height,
        RotationDegrees = image.RotationDegrees,
        Opacity = image.Opacity,
        ZIndex = image.ZIndex,
        IsLocked = image.IsLocked,
        IsVisible = image.IsVisible
    };

    private bool HasInstructionDraftContent() => _instructionDraftSegments.Any(segment =>
        segment.IsImageReference ||
        segment.IsText && !string.IsNullOrWhiteSpace(segment.Text));

    private static List<ArtWorkspaceInstructionDraftSegment> NormalizeInstructionDraftSegments(
        IEnumerable<ArtWorkspaceInstructionDraftSegment> segments)
    {
        var normalized = new List<ArtWorkspaceInstructionDraftSegment>();
        var tokenIds = new HashSet<string>(StringComparer.Ordinal);
        var referenceCount = 0;
        foreach (var source in segments)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.IsText)
            {
                if (string.IsNullOrEmpty(source.Text))
                {
                    continue;
                }

                if (normalized.LastOrDefault() is { IsText: true } precedingText)
                {
                    normalized[^1] = ArtWorkspaceInstructionDraftSegment.CreateText(
                        precedingText.Text + source.Text);
                }
                else
                {
                    normalized.Add(ArtWorkspaceInstructionDraftSegment.CreateText(source.Text));
                }

                continue;
            }

            if (!source.IsImageReference)
            {
                throw new ArgumentException($"不支持的美术工作区指令片段类型：{source.Kind}。", nameof(segments));
            }

            referenceCount++;
            if (referenceCount > ArtWorkspaceGenerationConstraintPackageBuilder.MaximumInlineInstructionReferenceCount)
            {
                throw new ArgumentException(
                    $"一条指令最多插入 {ArtWorkspaceGenerationConstraintPackageBuilder.MaximumInlineInstructionReferenceCount} 个图片引用。",
                    nameof(segments));
            }

            if (!Guid.TryParseExact(source.TokenId, "N", out _) || !tokenIds.Add(source.TokenId))
            {
                throw new ArgumentException("图片引用标签必须具有唯一的 32 位 token ID。", nameof(segments));
            }

            if (!Guid.TryParseExact(source.ImageId, "N", out _))
            {
                throw new ArgumentException("图片引用标签必须指向有效的画布图片 ID。", nameof(segments));
            }

            var imageName = string.IsNullOrWhiteSpace(source.ImageName)
                ? "参考图片"
                : source.ImageName.Trim();
            if (imageName.Length > 260)
            {
                imageName = imageName[..260];
            }

            ArtWorkspaceGenerationRegion? region = null;
            if (source.Region is not null)
            {
                var selectedRegion = new ArtWorkspaceImageRegionSelection
                {
                    ImageId = source.ImageId,
                    X = source.Region.X,
                    Y = source.Region.Y,
                    Width = source.Region.Width,
                    Height = source.Region.Height
                };
                ArtWorkspaceImageRegionGeometry.ValidateRegion(selectedRegion, source.ImageId);
                region = new ArtWorkspaceGenerationRegion
                {
                    X = selectedRegion.X,
                    Y = selectedRegion.Y,
                    Width = selectedRegion.Width,
                    Height = selectedRegion.Height
                };
            }

            normalized.Add(ArtWorkspaceInstructionDraftSegment.CreateImageReference(
                source.ImageId,
                imageName,
                region,
                source.TokenId));
        }

        if (normalized.Count > ArtWorkspaceGenerationConstraintPackageBuilder.MaximumInstructionSegmentCount)
        {
            throw new ArgumentException(
                $"一条指令最多包含 {ArtWorkspaceGenerationConstraintPackageBuilder.MaximumInstructionSegmentCount} 个片段。",
                nameof(segments));
        }

        return normalized;
    }

    private static int MeasureInstructionDraftSemanticLength(
        IEnumerable<ArtWorkspaceInstructionDraftSegment> segments) =>
        ArtWorkspaceGenerationConstraintPackageBuilder.GetInstructionSemanticLength(
            segments.Select(segment => segment.ToGenerationSegment()).ToArray());

    private static string BuildLegacyInstructionText(
        IEnumerable<ArtWorkspaceInstructionDraftSegment> segments)
    {
        var text = string.Concat(segments
            .Where(segment => segment.IsText)
            .Select(segment => segment.Text))
            .Trim();
        return text.Length > 0
            ? text
            : "请根据对话中按顺序插入的图片引用执行本轮美术生成或编辑。";
    }

    private static bool InstructionDraftSegmentsEqual(
        IReadOnlyList<ArtWorkspaceInstructionDraftSegment> left,
        IReadOnlyList<ArtWorkspaceInstructionDraftSegment> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (!string.Equals(first.Kind, second.Kind, StringComparison.Ordinal) ||
                !string.Equals(first.Text, second.Text, StringComparison.Ordinal) ||
                !string.Equals(first.TokenId, second.TokenId, StringComparison.Ordinal) ||
                !string.Equals(first.ImageId, second.ImageId, StringComparison.Ordinal) ||
                !string.Equals(first.ImageName, second.ImageName, StringComparison.Ordinal) ||
                !RegionsEqual(first.Region, second.Region))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RegionsEqual(
        ArtWorkspaceGenerationRegion? left,
        ArtWorkspaceGenerationRegion? right) =>
        ReferenceEquals(left, right) ||
        left is not null &&
        right is not null &&
        left.X.Equals(right.X) &&
        left.Y.Equals(right.Y) &&
        left.Width.Equals(right.Width) &&
        left.Height.Equals(right.Height);

    private static ArtWorkspaceImageRegionSelection CloneRegion(
        ArtWorkspaceImageRegionSelection region) => new()
    {
        ImageId = region.ImageId,
        X = region.X,
        Y = region.Y,
        Width = region.Width,
        Height = region.Height
    };

    private static ArtWorkspaceConversationMessage CloneMessage(ArtWorkspaceConversationMessage message) => new()
    {
        Id = message.Id,
        Role = message.Role,
        Text = message.Text,
        CreatedAtUtc = message.CreatedAtUtc,
        State = message.State,
        Segments = message.Segments.Select(CloneConversationSegment).ToList(),
        ReferenceImageId = message.ReferenceImageId,
        ReferenceSummary = message.ReferenceSummary
    };

    private static ArtWorkspaceConversationSegment CloneConversationSegment(
        ArtWorkspaceConversationSegment segment) => new()
    {
        Kind = segment.Kind,
        Text = segment.Text,
        TokenId = segment.TokenId,
        ImageId = segment.ImageId,
        ImageName = segment.ImageName,
        DisplayLabel = segment.DisplayLabel,
        Region = segment.Region is null ? null : CloneRegion(segment.Region)
    };
}

public sealed class ArtWorkspaceImageItemViewModel : ObservableObject
{
    private string _name;
    private double _x;
    private double _y;
    private double _width;
    private double _height;
    private double _rotationDegrees;
    private double _opacity;
    private int _zIndex;
    private bool _isLocked;
    private bool _isVisible;
    private bool _isSelected;
    private bool _isMissing;
    private ArtWorkspaceImageRegionSelection? _selectedRegion;

    public ArtWorkspaceImageItemViewModel(
        ArtWorkspaceImageItem model,
        string absolutePath,
        int pixelWidth,
        int pixelHeight)
    {
        ArgumentNullException.ThrowIfNull(model);
        Id = model.Id;
        AssetId = model.AssetId;
        AbsolutePath = absolutePath;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        _name = model.Name;
        _x = model.X;
        _y = model.Y;
        _width = model.Width;
        _height = model.Height;
        _rotationDegrees = model.RotationDegrees;
        _opacity = model.Opacity;
        _zIndex = model.ZIndex;
        _isLocked = model.IsLocked;
        _isVisible = model.IsVisible;
        _isMissing = !File.Exists(absolutePath);
    }

    public event PropertyChangedEventHandler? Changed;

    public string Id { get; }

    public string AssetId { get; }

    public string AbsolutePath { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public bool IsMissing => !File.Exists(AbsolutePath);

    public string PixelSizeText => $"{PixelWidth} × {PixelHeight}px";

    public string Name
    {
        get => _name;
        set => SetChanged(ref _name, NormalizeName(value), nameof(Name));
    }

    public double X
    {
        get => _x;
        set => SetChanged(ref _x, NormalizeFinite(value, -100_000d, 100_000d), nameof(X));
    }

    public double Y
    {
        get => _y;
        set => SetChanged(ref _y, NormalizeFinite(value, -100_000d, 100_000d), nameof(Y));
    }

    public double Width
    {
        get => _width;
        set
        {
            if (SetChanged(ref _width, NormalizeFinite(value, 1d, 32768d), nameof(Width)))
            {
                OnPropertyChanged(nameof(SelectedRegionX));
                OnPropertyChanged(nameof(SelectedRegionWidth));
            }
        }
    }

    public double Height
    {
        get => _height;
        set
        {
            if (SetChanged(ref _height, NormalizeFinite(value, 1d, 32768d), nameof(Height)))
            {
                OnPropertyChanged(nameof(SelectedRegionY));
                OnPropertyChanged(nameof(SelectedRegionHeight));
            }
        }
    }

    public double RotationDegrees
    {
        get => _rotationDegrees;
        set => SetChanged(ref _rotationDegrees, NormalizeFinite(value, -36000d, 36000d), nameof(RotationDegrees));
    }

    public double Opacity
    {
        get => _opacity;
        set => SetChanged(ref _opacity, NormalizeFinite(value, 0d, 1d), nameof(Opacity));
    }

    public int ZIndex
    {
        get => _zIndex;
        set => SetChanged(ref _zIndex, Math.Clamp(value, 0, 1_000_000), nameof(ZIndex));
    }

    public bool IsLocked
    {
        get => _isLocked;
        set => SetChanged(ref _isLocked, value, nameof(IsLocked));
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetChanged(ref _isVisible, value, nameof(IsVisible));
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool HasSelectedRegion => _selectedRegion is not null;

    public double SelectedRegionX => (_selectedRegion?.X ?? 0d) * Width;

    public double SelectedRegionY => (_selectedRegion?.Y ?? 0d) * Height;

    public double SelectedRegionWidth => (_selectedRegion?.Width ?? 0d) * Width;

    public double SelectedRegionHeight => (_selectedRegion?.Height ?? 0d) * Height;

    internal void SetSelectedRegion(ArtWorkspaceImageRegionSelection? region)
    {
        _selectedRegion = region is null
            ? null
            : new ArtWorkspaceImageRegionSelection
            {
                ImageId = region.ImageId,
                X = region.X,
                Y = region.Y,
                Width = region.Width,
                Height = region.Height
            };
        OnPropertyChanged(nameof(HasSelectedRegion));
        OnPropertyChanged(nameof(SelectedRegionX));
        OnPropertyChanged(nameof(SelectedRegionY));
        OnPropertyChanged(nameof(SelectedRegionWidth));
        OnPropertyChanged(nameof(SelectedRegionHeight));
    }

    internal bool RefreshMissingState()
    {
        var isMissing = !File.Exists(AbsolutePath);
        if (_isMissing == isMissing)
        {
            return false;
        }

        _isMissing = isMissing;
        OnPropertyChanged(nameof(IsMissing));
        return true;
    }

    public ArtWorkspaceImageItem ToModel() => new()
    {
        Id = Id,
        AssetId = AssetId,
        Name = Name,
        X = X,
        Y = Y,
        Width = Width,
        Height = Height,
        RotationDegrees = RotationDegrees,
        Opacity = Opacity,
        ZIndex = ZIndex,
        IsLocked = IsLocked,
        IsVisible = IsVisible
    };

    private bool SetChanged<T>(ref T field, T value, string propertyName)
    {
        if (SetProperty(ref field, value, propertyName))
        {
            Changed?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        return false;
    }

    private static string NormalizeName(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "参考图片" : value.Trim();
        return normalized.Length <= 260 ? normalized : normalized[..260];
    }

    private static double NormalizeFinite(double value, double minimum, double maximum) =>
        Math.Clamp(double.IsFinite(value) ? value : minimum, minimum, maximum);
}

public sealed class ArtWorkspaceConversationMessageViewModel : ObservableObject
{
    public const int MaximumTextLength = ArtWorkspaceInstructionSegmentSemantics.MaximumSemanticLength;
    public const int MaximumStateLength = 64;

    private readonly ArtWorkspaceConversationMessage _model;

    public ArtWorkspaceConversationMessageViewModel(ArtWorkspaceConversationMessage model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        Segments = _model.Segments
            .Select(segment => new ArtWorkspaceConversationSegmentViewModel(segment))
            .ToArray();
    }

    public string Id => _model.Id;

    public bool IsUser => _model.Role == ArtWorkspaceMessageRole.User;

    public bool IsAgent => _model.Role == ArtWorkspaceMessageRole.Agent;

    public string RoleLabel => _model.Role switch
    {
        ArtWorkspaceMessageRole.User => "你",
        ArtWorkspaceMessageRole.Agent => "Agent",
        _ => "工作台"
    };

    public string Text => _model.Text;

    public IReadOnlyList<ArtWorkspaceConversationSegmentViewModel> Segments { get; }

    public bool HasStructuredSegments => Segments.Count > 0;

    /// <summary>
    /// User-safe text used by the conversation copy action. Structured image references are
    /// represented only by their visible labels; managed paths, IDs, hashes and coordinates never
    /// enter the clipboard payload.
    /// </summary>
    public string ClipboardText
    {
        get
        {
            if (!HasStructuredSegments)
            {
                return HasReferenceSummary
                    ? $"{Text}{Environment.NewLine}[{ReferenceSummary}]"
                    : Text;
            }

            var builder = new StringBuilder();
            foreach (var segment in Segments)
            {
                if (segment.IsText)
                {
                    builder.Append(segment.Text);
                }
                else if (segment.IsImageReference)
                {
                    builder.Append('[').Append(segment.ReferenceText).Append(']');
                }
            }

            return builder.ToString();
        }
    }

    public string TimeText => _model.CreatedAtUtc.ToLocalTime().ToString("HH:mm:ss");

    public string State => _model.State;

    public bool HasReferenceSummary => !string.IsNullOrWhiteSpace(_model.ReferenceSummary);

    public string ReferenceSummary => _model.ReferenceSummary ?? string.Empty;

    public void Update(string text, string? state = null)
    {
        var normalizedText = string.IsNullOrWhiteSpace(text) ? "Agent 未提供状态文本。" : text.Trim();
        if (normalizedText.Length > MaximumTextLength)
        {
            normalizedText = normalizedText[..MaximumTextLength];
        }

        var normalizedState = string.IsNullOrWhiteSpace(state) ? _model.State : state.Trim();
        if (normalizedState.Length > MaximumStateLength)
        {
            normalizedState = normalizedState[..MaximumStateLength];
        }

        if (!string.Equals(_model.Text, normalizedText, StringComparison.Ordinal))
        {
            _model.Text = normalizedText;
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(ClipboardText));
        }

        if (!string.Equals(_model.State, normalizedState, StringComparison.Ordinal))
        {
            _model.State = normalizedState;
            OnPropertyChanged(nameof(State));
        }
    }

    public ArtWorkspaceConversationMessage ToModel() => new()
    {
        Id = _model.Id,
        Role = _model.Role,
        Text = _model.Text,
        CreatedAtUtc = _model.CreatedAtUtc,
        State = _model.State,
        Segments = Segments.Select(segment => segment.ToModel()).ToList(),
        ReferenceImageId = _model.ReferenceImageId,
        ReferenceSummary = _model.ReferenceSummary
    };
}

public sealed class ArtWorkspaceConversationSegmentViewModel
{
    private readonly ArtWorkspaceConversationSegment _model;

    public ArtWorkspaceConversationSegmentViewModel(ArtWorkspaceConversationSegment model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = Clone(model);
    }

    public bool IsText => string.Equals(
        _model.Kind,
        ArtWorkspaceConversationSegmentKinds.Text,
        StringComparison.Ordinal);

    public bool IsImageReference => string.Equals(
        _model.Kind,
        ArtWorkspaceConversationSegmentKinds.ImageReference,
        StringComparison.Ordinal);

    public string Text => _model.Text;

    public string ReferenceText => _model.Region is null
        ? $"{_model.DisplayLabel} · {_model.ImageName} · 整张图片"
        : $"{_model.DisplayLabel} · {_model.ImageName} · 局部框选";

    public ArtWorkspaceConversationSegment ToModel() => Clone(_model);

    private static ArtWorkspaceConversationSegment Clone(ArtWorkspaceConversationSegment segment) => new()
    {
        Kind = segment.Kind,
        Text = segment.Text,
        TokenId = segment.TokenId,
        ImageId = segment.ImageId,
        ImageName = segment.ImageName,
        DisplayLabel = segment.DisplayLabel,
        Region = segment.Region is null
            ? null
            : new ArtWorkspaceImageRegionSelection
            {
                ImageId = segment.Region.ImageId,
                X = segment.Region.X,
                Y = segment.Region.Y,
                Width = segment.Region.Width,
                Height = segment.Region.Height
            }
    };
}

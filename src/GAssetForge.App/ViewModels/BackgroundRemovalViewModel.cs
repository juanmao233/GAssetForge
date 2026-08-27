using GAssetForge.App.Infrastructure;
using GAssetForge.App.Services;
using GAssetForge.Core.Generation;
using GAssetForge.Core.Imaging;
using SixLabors.ImageSharp;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;

namespace GAssetForge.App.ViewModels;

public sealed record BackgroundRemovalPresetOption(
    BackgroundKeyPreset Preset,
    string DisplayName,
    string ColorHex,
    string Description,
    bool IsHighRisk = false)
{
    public BackgroundKeyPreset Value => Preset;
}

public sealed record BackgroundRemovalToolSettingsSnapshot(
    string PresetKey,
    string CustomColorHex,
    double TransparentThreshold,
    double OpaqueThreshold,
    int EdgeContract,
    int EdgeFeather,
    bool Despill,
    bool EdgeConnectedOnly,
    bool UseFakeTransparencyGrid,
    string CheckerColorAHex,
    string CheckerColorBHex,
    bool EnableCheckerGridAlignment,
    double CheckerCellSize,
    double CheckerOffsetX,
    double CheckerOffsetY,
    double CheckerBoundaryTolerance,
    bool EnableSmartCheckerDetection,
    double CheckerDetectionMinimumCellSize,
    double CheckerDetectionMaximumCellSize,
    double CheckerDetectionSearchRangePercent,
    string? LastInputDirectory,
    string? LastOutputDirectory);

public sealed class BackgroundRemovalSettingsChangedEventArgs(
    BackgroundRemovalToolSettingsSnapshot settings) : EventArgs
{
    public BackgroundRemovalToolSettingsSnapshot Settings { get; } = settings;
}

public enum BackgroundRemovalBatchItemStatus
{
    Pending,
    Processing,
    Completed,
    Failed
}

public sealed class BackgroundRemovalBatchItemViewModel : ObservableObject
{
    private string _outputPath = string.Empty;
    private BackgroundRemovalBatchItemStatus _status = BackgroundRemovalBatchItemStatus.Pending;
    private string _message = "等待处理";
    private string _checkerDetectionText = "—";
    private double _progressPercent;

    public BackgroundRemovalBatchItemViewModel(string sourcePath)
    {
        SourcePath = Path.GetFullPath(sourcePath);
        SourceSizeText = InspectSourceSize(SourcePath);
    }

    public string SourcePath { get; }

    public string FileName => Path.GetFileName(SourcePath);

    public string SourceSizeText { get; }

    public string OutputPath
    {
        get => _outputPath;
        private set
        {
            if (SetProperty(ref _outputPath, value))
            {
                OnPropertyChanged(nameof(OutputFileName));
            }
        }
    }

    public string OutputFileName => string.IsNullOrWhiteSpace(OutputPath)
        ? "—"
        : Path.GetFileName(OutputPath);

    public BackgroundRemovalBatchItemStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusDisplayText));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(IsFailed));
            }
        }
    }

    public string StatusDisplayText => Status switch
    {
        BackgroundRemovalBatchItemStatus.Pending => "等待处理",
        BackgroundRemovalBatchItemStatus.Processing => "处理中",
        BackgroundRemovalBatchItemStatus.Completed => "已完成",
        BackgroundRemovalBatchItemStatus.Failed => "失败",
        _ => Status.ToString()
    };

    public string StatusText => StatusDisplayText;

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public string CheckerDetectionText
    {
        get => _checkerDetectionText;
        private set => SetProperty(ref _checkerDetectionText, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, Math.Clamp(value, 0d, 100d));
    }

    public bool IsCompleted => Status == BackgroundRemovalBatchItemStatus.Completed;

    public bool IsFailed => Status == BackgroundRemovalBatchItemStatus.Failed;

    internal void Reset(string outputPath)
    {
        OutputPath = outputPath;
        Status = BackgroundRemovalBatchItemStatus.Pending;
        Message = "等待处理";
        CheckerDetectionText = "—";
        ProgressPercent = 0d;
    }

    internal void MarkDetecting()
    {
        Status = BackgroundRemovalBatchItemStatus.Processing;
        Message = "正在从四角识别棋盘格…";
        ProgressPercent = 5d;
    }

    internal void SetCheckerDetection(CheckerGridDetectionResult result, bool usedFallback)
    {
        CheckerDetectionText = result.IsDetected
            ? $"{result.CellSize:0.###}px · {result.Confidence:P0} · {result.DetectedCornerCount}/4 角"
            : usedFallback
                ? $"回退手动参数 · {result.Confidence:P0} · {result.FailureReason}"
                : $"{result.Confidence:P0} · {result.FailureReason}";
    }

    internal void MarkProcessing()
    {
        Status = BackgroundRemovalBatchItemStatus.Processing;
        Message = "正在剔除背景…";
        ProgressPercent = 10d;
    }

    internal void MarkCompleted(BackgroundRemovalResult result)
    {
        OutputPath = Path.GetFullPath(result.OutputPath);
        Status = BackgroundRemovalBatchItemStatus.Completed;
        Message = $"已输出 {result.Width}×{result.Height} PNG · 剔除 {result.RemovedPixelCount:N0} 像素";
        ProgressPercent = 100d;
    }

    internal void MarkFailed(string message)
    {
        Status = BackgroundRemovalBatchItemStatus.Failed;
        Message = message;
        ProgressPercent = 100d;
    }

    private static string InspectSourceSize(string sourcePath)
    {
        try
        {
            using var stream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var imageInfo = Image.Identify(stream);
            return imageInfo is null || imageInfo.Width <= 0 || imageInfo.Height <= 0
                ? "无法读取"
                : $"{imageInfo.Width} × {imageInfo.Height}";
        }
        catch (Exception)
        {
            return "无法读取";
        }
    }
}

public sealed class BackgroundRemovalViewModel : ObservableObject
{
    private const string LogSource = "BackgroundRemoval";
    private const double MinimumCheckerCellSize = 0.25d;
    private const double MaximumCheckerCellSize = 4096d;
    private const double MaximumCheckerBoundaryTolerance = 8d;
    private const double MinimumCheckerDetectionCellSize = 0.25d;
    private const double MaximumCheckerDetectionCellSize = 4096d;
    private const double MinimumCheckerDetectionSearchRangePercent = 5d;
    private const double MaximumCheckerDetectionSearchRangePercent = 50d;
    public const int MaximumBatchItemCount = 256;

    private static readonly IReadOnlyList<BackgroundRemovalPresetOption> SupportedPresets =
    [
        new(BackgroundKeyPreset.Magenta, "洋红色（推荐）", "#FF00FF", "与大多数游戏资产颜色区分明显，适合作为默认生成底色。"),
        new(BackgroundKeyPreset.Green, "绿色", "#00FF00", "适合主体中没有高饱和绿色的图片。"),
        new(BackgroundKeyPreset.Blue, "蓝色", "#0000FF", "适合主体中没有高饱和蓝色的图片。"),
        new(BackgroundKeyPreset.Black, "黑色", "#000000", "可能误伤阴影、描边和深色细节。", true),
        new(BackgroundKeyPreset.White, "白色", "#FFFFFF", "可能误伤高光、白色材质和浅色细节。", true),
        new(BackgroundKeyPreset.Custom, "自定义颜色", BackgroundKeyColor.DefaultCustomColorHex, "使用 #RRGGBB 指定精确底色。")
    ];

    private static readonly HashSet<string> SupportedInputExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp"
    };

    private readonly IBackgroundRemovalService _backgroundRemovalService;
    private readonly IBackgroundRemovalInputPicker _inputPicker;
    private readonly IBackgroundRemovalOutputPicker _outputPicker;
    private readonly IImageProcessingPreviewCache _previewCache;
    private readonly IColorPickerService _colorPickerService;
    private readonly ICheckerGridDetectionService _checkerGridDetectionService;
    private readonly IWorkbenchLogSink? _workbenchLogSink;
    private readonly RelayCommand _chooseInputCommand;
    private readonly RelayCommand _chooseOutputCommand;
    private readonly AsyncRelayCommand _processCommand;
    private readonly AsyncRelayCommand _refreshPreviewCommand;
    private readonly AsyncRelayCommand _saveResultCommand;
    private readonly RelayCommand _openOutputDirectoryCommand;
    private readonly RelayCommand _swapCheckerColorsCommand;
    private readonly RelayCommand _chooseCustomColorCommand;
    private readonly RelayCommand _chooseCheckerColorACommand;
    private readonly RelayCommand _chooseCheckerColorBCommand;
    private readonly AsyncRelayCommand _detectCheckerGridCommand;
    private readonly RelayCommand _chooseBatchInputsCommand;
    private readonly RelayCommand _chooseBatchOutputDirectoryCommand;
    private readonly AsyncRelayCommand _processBatchCommand;
    private readonly RelayCommand _clearBatchCommand;
    private readonly RelayCommand _removeSelectedBatchItemCommand;
    private BackgroundRemovalPresetOption _selectedPreset;
    private string _customColorHex;
    private double _transparentThreshold;
    private double _opaqueThreshold;
    private int _edgeContract;
    private int _edgeFeather;
    private bool _despill;
    private bool _edgeConnectedOnly;
    private bool _useFakeTransparencyGrid;
    private string _checkerColorAHex;
    private string _checkerColorBHex;
    private bool _enableCheckerGridAlignment;
    private double _checkerCellSize;
    private double _checkerOffsetX;
    private double _checkerOffsetY;
    private double _checkerBoundaryTolerance;
    private bool _enableSmartCheckerDetection;
    private double _checkerDetectionMinimumCellSize;
    private double _checkerDetectionMaximumCellSize;
    private double _checkerDetectionSearchRangePercent;
    private string _checkerDetectionStatusText = "尚未执行智能网格识别";
    private bool _isCheckerGridDetecting;
    private bool _isCheckerAlignmentEditActive;
    private double _checkerAlignmentEditStartCellSize;
    private double _checkerAlignmentEditStartOffsetX;
    private double _checkerAlignmentEditStartOffsetY;
    private double _checkerAlignmentEditStartBoundaryTolerance;
    private string _inputPath = string.Empty;
    private int _sourcePixelWidth;
    private int _sourcePixelHeight;
    private string _outputPath = string.Empty;
    private string _outputPreviewPath = string.Empty;
    private string _lastSavedOutputPath = string.Empty;
    private bool _isPreviewCurrent;
    private string _previewStatusText = "尚未刷新预览";
    private long _previewSettingsRevision;
    private string? _lastInputDirectory;
    private string? _lastOutputDirectory;
    private string _statusText = "选择一张纯色背景图片，然后设置透明 PNG 的保存位置。";
    private string _resultSummary = "尚未处理图片";
    private bool _isBusy;
    private string _batchOutputDirectory = string.Empty;
    private string _batchStatusText = "尚未选择批处理图片";
    private bool _isBatchRunning;
    private bool _isBatchMode;
    private BackgroundRemovalBatchItemViewModel? _selectedBatchItem;
    private string _batchCurrentFileName = string.Empty;
    private int _batchCompletedCount;
    private int _batchSucceededCount;
    private int _batchFailedCount;
    private bool _suppressSettingsChanged = true;

    public BackgroundRemovalViewModel(
        IBackgroundRemovalService backgroundRemovalService,
        IBackgroundRemovalInputPicker inputPicker,
        IBackgroundRemovalOutputPicker outputPicker,
        string? presetKey = null,
        string? customColorHex = null,
        double transparentThreshold = 30d,
        double opaqueThreshold = 120d,
        int edgeContract = 1,
        int edgeFeather = 0,
        bool despill = true,
        bool edgeConnectedOnly = false,
        bool useFakeTransparencyGrid = false,
        string? checkerColorAHex = null,
        string? checkerColorBHex = null,
        bool enableCheckerGridAlignment = true,
        double checkerCellSize = 16d,
        double checkerOffsetX = 0d,
        double checkerOffsetY = 0d,
        double checkerBoundaryTolerance = 1d,
        string? lastInputDirectory = null,
        string? lastOutputDirectory = null,
        IWorkbenchLogSink? workbenchLogSink = null,
        IImageProcessingPreviewCache? previewCache = null,
        IColorPickerService? colorPickerService = null,
        ICheckerGridDetectionService? checkerGridDetectionService = null,
        bool enableSmartCheckerDetection = false,
        double checkerDetectionMinimumCellSize = 2d,
        double checkerDetectionMaximumCellSize = 256d,
        double checkerDetectionSearchRangePercent = 25d)
    {
        _backgroundRemovalService = backgroundRemovalService ?? throw new ArgumentNullException(nameof(backgroundRemovalService));
        _inputPicker = inputPicker ?? throw new ArgumentNullException(nameof(inputPicker));
        _outputPicker = outputPicker ?? throw new ArgumentNullException(nameof(outputPicker));
        _previewCache = previewCache ?? CreateFallbackPreviewCache();
        _colorPickerService = colorPickerService ?? new DesktopColorPickerService();
        _checkerGridDetectionService = checkerGridDetectionService ?? new ImageSharpCheckerGridDetectionService();
        _workbenchLogSink = workbenchLogSink;
        _selectedPreset = ResolvePresetOption(presetKey);
        _customColorHex = NormalizeInitialCustomColor(customColorHex);
        (_transparentThreshold, _opaqueThreshold) = NormalizeInitialThresholds(
            transparentThreshold,
            opaqueThreshold);
        _edgeContract = Math.Clamp(edgeContract, 0, 3);
        _edgeFeather = Math.Clamp(edgeFeather, 0, 2);
        _despill = despill;
        _edgeConnectedOnly = edgeConnectedOnly;
        _useFakeTransparencyGrid = useFakeTransparencyGrid;
        _checkerColorAHex = NormalizeInitialCustomColor(checkerColorAHex ?? "#FFFFFF");
        _checkerColorBHex = NormalizeInitialCustomColor(checkerColorBHex ?? "#CCCCCC");
        _enableCheckerGridAlignment = enableCheckerGridAlignment;
        _checkerCellSize = NormalizeFinite(
            checkerCellSize,
            16d,
            MinimumCheckerCellSize,
            MaximumCheckerCellSize);
        _checkerOffsetX = NormalizeCheckerOffset(checkerOffsetX, _checkerCellSize);
        _checkerOffsetY = NormalizeCheckerOffset(checkerOffsetY, _checkerCellSize);
        _checkerBoundaryTolerance = NormalizeFinite(
            checkerBoundaryTolerance,
            1d,
            0d,
            MaximumCheckerBoundaryTolerance);
        _enableSmartCheckerDetection = enableSmartCheckerDetection;
        _checkerDetectionMinimumCellSize = NormalizeFinite(
            checkerDetectionMinimumCellSize,
            2d,
            MinimumCheckerDetectionCellSize,
            MaximumCheckerDetectionCellSize);
        _checkerDetectionMaximumCellSize = NormalizeFinite(
            checkerDetectionMaximumCellSize,
            256d,
            _checkerDetectionMinimumCellSize,
            MaximumCheckerDetectionCellSize);
        _checkerDetectionSearchRangePercent = NormalizeFinite(
            checkerDetectionSearchRangePercent,
            25d,
            MinimumCheckerDetectionSearchRangePercent,
            MaximumCheckerDetectionSearchRangePercent);
        _lastInputDirectory = NormalizeExistingDirectory(lastInputDirectory);
        _lastOutputDirectory = NormalizeExistingDirectory(lastOutputDirectory);

        _chooseInputCommand = new RelayCommand(ChooseInput, () => !IsBusy);
        _chooseOutputCommand = new RelayCommand(ChooseOutput, () => !IsBusy && HasInput);
        _refreshPreviewCommand = new AsyncRelayCommand(RefreshPreviewAsync, () => CanRefreshPreview);
        _saveResultCommand = new AsyncRelayCommand(SaveResultAsync, () => CanSaveResult);
        _processCommand = new AsyncRelayCommand(ProcessLegacyAsync, () => CanProcess);
        _openOutputDirectoryCommand = new RelayCommand(OpenOutputDirectory, () => HasSavedOutput);
        _swapCheckerColorsCommand = new RelayCommand(SwapCheckerColors, () => !IsBusy);
        _chooseCustomColorCommand = new RelayCommand(ChooseCustomColor, () => !IsBusy);
        _chooseCheckerColorACommand = new RelayCommand(ChooseCheckerColorA, () => !IsBusy);
        _chooseCheckerColorBCommand = new RelayCommand(ChooseCheckerColorB, () => !IsBusy);
        _detectCheckerGridCommand = new AsyncRelayCommand(
            DetectCheckerGridAsync,
            () => CanDetectCheckerGrid);
        _chooseBatchInputsCommand = new RelayCommand(
            ChooseBatchInputs,
            () => !IsBusy && !IsBatchRunning && BatchItems.Count < MaximumBatchItemCount);
        _chooseBatchOutputDirectoryCommand = new RelayCommand(
            ChooseBatchOutputDirectory,
            () => !IsBusy && !IsBatchRunning && HasBatchItems);
        _processBatchCommand = new AsyncRelayCommand(ProcessBatchAsync, () => CanProcessBatch);
        _clearBatchCommand = new RelayCommand(ClearBatch, () => !IsBusy && !IsBatchRunning && HasBatchItems);
        _removeSelectedBatchItemCommand = new RelayCommand(
            RemoveSelectedBatchItem,
            () => !IsBusy && !IsBatchRunning && SelectedBatchItem is not null);
        ChooseInputCommand = _chooseInputCommand;
        ChooseOutputCommand = _chooseOutputCommand;
        ProcessCommand = _processCommand;
        RefreshPreviewCommand = _refreshPreviewCommand;
        SaveResultCommand = _saveResultCommand;
        OpenOutputDirectoryCommand = _openOutputDirectoryCommand;
        SwapCheckerColorsCommand = _swapCheckerColorsCommand;
        ChooseCustomColorCommand = _chooseCustomColorCommand;
        ChooseCheckerColorACommand = _chooseCheckerColorACommand;
        ChooseCheckerColorBCommand = _chooseCheckerColorBCommand;
        DetectCheckerGridCommand = _detectCheckerGridCommand;
        ChooseBatchInputsCommand = _chooseBatchInputsCommand;
        ChooseBatchOutputDirectoryCommand = _chooseBatchOutputDirectoryCommand;
        ProcessBatchCommand = _processBatchCommand;
        ClearBatchCommand = _clearBatchCommand;
        RemoveSelectedBatchItemCommand = _removeSelectedBatchItemCommand;
        _suppressSettingsChanged = false;
    }

    public event EventHandler<BackgroundRemovalSettingsChangedEventArgs>? SettingsChanged;

    public IReadOnlyList<BackgroundRemovalPresetOption> PresetOptions => SupportedPresets;

    public ObservableCollection<BackgroundRemovalBatchItemViewModel> BatchItems { get; } = [];

    public IReadOnlyList<int> EdgeContractOptions { get; } = [0, 1, 2, 3];

    public IReadOnlyList<int> EdgeFeatherOptions { get; } = [0, 1, 2];

    public BackgroundRemovalPresetOption SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (value is null || !SetProperty(ref _selectedPreset, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsCustomColor));
            OnPropertyChanged(nameof(IsCustomPreset));
            OnPropertyChanged(nameof(EffectiveColorHex));
            OnPropertyChanged(nameof(SelectedPresetDisplayName));
            OnPropertyChanged(nameof(PresetGuidanceText));
            MarkPreviewStale();
            RefreshValidationAndCommands();
            NotifySettingsChanged();
        }
    }

    public bool IsCustomColor => SelectedPreset.Preset == BackgroundKeyPreset.Custom;

    public bool IsCustomPreset => IsCustomColor;

    public string SelectedPresetDisplayName => SelectedPreset.DisplayName;

    public string CustomColorHex
    {
        get => _customColorHex;
        set
        {
            value ??= string.Empty;
            if (!SetProperty(ref _customColorHex, value))
            {
                return;
            }

            OnPropertyChanged(nameof(EffectiveColorHex));
            MarkPreviewStale();
            RefreshValidationAndCommands();
            NotifySettingsChanged();
        }
    }

    public string EffectiveColorHex
    {
        get
        {
            try
            {
                return BackgroundKeyColor.ResolveHex(SelectedPreset.Preset, CustomColorHex);
            }
            catch (ArgumentException)
            {
                return "#00000000";
            }
        }
    }

    public string PresetGuidanceText => SelectedPreset.IsHighRisk
        ? $"高风险：{SelectedPreset.Description} 建议优先使用洋红色。"
        : SelectedPreset.Description;

    public double TransparentThreshold
    {
        get => _transparentThreshold;
        set
        {
            if (!SetProperty(ref _transparentThreshold, value))
            {
                return;
            }

            MarkPreviewStale();
            RefreshValidationAndCommands();
            NotifySettingsChanged();
        }
    }

    public double OpaqueThreshold
    {
        get => _opaqueThreshold;
        set
        {
            if (!SetProperty(ref _opaqueThreshold, value))
            {
                return;
            }

            MarkPreviewStale();
            RefreshValidationAndCommands();
            NotifySettingsChanged();
        }
    }

    public int EdgeContract
    {
        get => _edgeContract;
        set
        {
            var normalized = Math.Clamp(value, 0, 3);
            if (SetProperty(ref _edgeContract, normalized))
            {
                MarkPreviewStale();
                RefreshValidationAndCommands();
                NotifySettingsChanged();
            }
        }
    }

    public int EdgeFeather
    {
        get => _edgeFeather;
        set
        {
            var normalized = Math.Clamp(value, 0, 2);
            if (SetProperty(ref _edgeFeather, normalized))
            {
                MarkPreviewStale();
                RefreshValidationAndCommands();
                NotifySettingsChanged();
            }
        }
    }

    public bool Despill
    {
        get => _despill;
        set
        {
            if (SetProperty(ref _despill, value))
            {
                MarkPreviewStale();
                RefreshValidationAndCommands();
                NotifySettingsChanged();
            }
        }
    }

    public bool EdgeConnectedOnly
    {
        get => _edgeConnectedOnly;
        set
        {
            if (SetProperty(ref _edgeConnectedOnly, value))
            {
                OnPropertyChanged(nameof(RemoveEnclosedBackground));
                OnPropertyChanged(nameof(RemovalScopeText));
                MarkPreviewStale();
                RefreshValidationAndCommands();
                NotifySettingsChanged();
            }
        }
    }

    public bool RemoveEnclosedBackground
    {
        get => !EdgeConnectedOnly;
        set => EdgeConnectedOnly = !value;
    }

    public string RemovalScopeText => RemoveEnclosedBackground
        ? "画布外侧与物体镂空区域内部"
        : "仅与画布外边缘连通的区域";

    public bool UseFakeTransparencyGrid
    {
        get => _useFakeTransparencyGrid;
        set
        {
            if (!SetProperty(ref _useFakeTransparencyGrid, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsSolidColorMode));
            OnPropertyChanged(nameof(BackgroundModeText));
            OnPropertyChanged(nameof(CheckerAlignmentSummary));
            OnPropertyChanged(nameof(ShowCheckerGridOverlay));
            OnPropertyChanged(nameof(CanDetectCheckerGrid));
            CheckerDetectionStatusText = value
                ? "已启用假透明模式，等待智能识别"
                : "智能网格识别仅用于假透明棋盘格模式";
            MarkPreviewStale();
            RefreshValidationAndCommands();
            NotifySettingsChanged();
        }
    }

    public bool IsSolidColorMode => !UseFakeTransparencyGrid;

    public string BackgroundModeText => UseFakeTransparencyGrid
        ? "假透明棋盘格：按两种背景色恢复 Alpha；这是无法获得真实透明源文件时的本地兜底。"
        : "纯色色键：按所选单一底色恢复 Alpha，也是素材生成首道底色使用的稳定模式。";

    public string CheckerColorAHex
    {
        get => _checkerColorAHex;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref _checkerColorAHex, value))
            {
                OnPropertyChanged(nameof(EffectiveCheckerColorAHex));
                MarkPreviewStale();
                RefreshValidationAndCommands();
                NotifySettingsChanged();
            }
        }
    }

    public string CheckerColorBHex
    {
        get => _checkerColorBHex;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref _checkerColorBHex, value))
            {
                OnPropertyChanged(nameof(EffectiveCheckerColorBHex));
                MarkPreviewStale();
                RefreshValidationAndCommands();
                NotifySettingsChanged();
            }
        }
    }

    public string EffectiveCheckerColorAHex => ResolveDisplayColor(CheckerColorAHex);

    public string EffectiveCheckerColorBHex => ResolveDisplayColor(CheckerColorBHex);

    public bool EnableCheckerGridAlignment
    {
        get => _enableCheckerGridAlignment;
        set
        {
            if (SetProperty(ref _enableCheckerGridAlignment, value))
            {
                OnPropertyChanged(nameof(CheckerAlignmentSummary));
                OnPropertyChanged(nameof(ShowCheckerGridOverlay));
                MarkPreviewStale();
                RefreshValidationAndCommands();
                NotifySettingsChanged();
            }
        }
    }

    public double CheckerCellSize
    {
        get => _checkerCellSize;
        set
        {
            var normalized = NormalizeFinite(
                value,
                double.IsFinite(_checkerCellSize) ? _checkerCellSize : 16d,
                MinimumCheckerCellSize,
                MaximumCheckerCellSize);
            if (!SetProperty(ref _checkerCellSize, normalized))
            {
                return;
            }

            CheckerOffsetX = NormalizeCheckerOffset(CheckerOffsetX, normalized);
            CheckerOffsetY = NormalizeCheckerOffset(CheckerOffsetY, normalized);
            OnPropertyChanged(nameof(CheckerOffsetMaximum));
            OnPropertyChanged(nameof(CheckerAlignmentSummary));
            MarkPreviewStale();
            RefreshValidationAndCommands();
            NotifyCheckerAlignmentSettingsChanged();
        }
    }

    public double CheckerOffsetMaximum => CheckerCellSize;

    public double CheckerOffsetX
    {
        get => _checkerOffsetX;
        set
        {
            var normalized = NormalizeCheckerOffset(value, CheckerCellSize);
            if (SetProperty(ref _checkerOffsetX, normalized))
            {
                OnPropertyChanged(nameof(CheckerAlignmentSummary));
                MarkPreviewStale();
                RefreshValidationAndCommands();
                NotifyCheckerAlignmentSettingsChanged();
            }
        }
    }

    public double CheckerOffsetY
    {
        get => _checkerOffsetY;
        set
        {
            var normalized = NormalizeCheckerOffset(value, CheckerCellSize);
            if (SetProperty(ref _checkerOffsetY, normalized))
            {
                OnPropertyChanged(nameof(CheckerAlignmentSummary));
                MarkPreviewStale();
                RefreshValidationAndCommands();
                NotifyCheckerAlignmentSettingsChanged();
            }
        }
    }

    public double CheckerBoundaryTolerance
    {
        get => _checkerBoundaryTolerance;
        set
        {
            var normalized = NormalizeFinite(
                value,
                double.IsFinite(_checkerBoundaryTolerance) ? _checkerBoundaryTolerance : 1d,
                0d,
                MaximumCheckerBoundaryTolerance);
            if (SetProperty(ref _checkerBoundaryTolerance, normalized))
            {
                OnPropertyChanged(nameof(CheckerAlignmentSummary));
                MarkPreviewStale();
                RefreshValidationAndCommands();
                NotifyCheckerAlignmentSettingsChanged();
            }
        }
    }

    public string CheckerAlignmentSummary => EnableCheckerGridAlignment
        ? $"按 {CheckerCellSize:0.###}px 方格、偏移 ({CheckerOffsetX:0.###},{CheckerOffsetY:0.###}) 对齐；格线 ±{CheckerBoundaryTolerance:0.###}px 内允许匹配任一底色。"
        : "关闭网格对齐后，任意位置只要接近两种底色之一就会进入剔除候选，误删风险更高。";

    public bool ShowCheckerGridOverlay => UseFakeTransparencyGrid && EnableCheckerGridAlignment;

    public bool EnableSmartCheckerDetection
    {
        get => _enableSmartCheckerDetection;
        set
        {
            if (!SetProperty(ref _enableSmartCheckerDetection, value))
            {
                return;
            }

            if (value && !EnableCheckerGridAlignment)
            {
                var previousSuppression = _suppressSettingsChanged;
                _suppressSettingsChanged = true;
                try
                {
                    EnableCheckerGridAlignment = true;
                }
                finally
                {
                    _suppressSettingsChanged = previousSuppression;
                }
            }

            CheckerDetectionStatusText = value
                ? "智能识别已开启；刷新和批处理会逐图检测"
                : "智能识别已关闭；将使用手动网格参数";
            OnPropertyChanged(nameof(CanDetectCheckerGrid));
            MarkPreviewStale();
            RefreshValidationAndCommands();
            NotifySettingsChanged();
        }
    }

    public double CheckerDetectionMinimumCellSize
    {
        get => _checkerDetectionMinimumCellSize;
        set
        {
            var normalized = NormalizeFinite(
                value,
                double.IsFinite(_checkerDetectionMinimumCellSize)
                    ? _checkerDetectionMinimumCellSize
                    : 2d,
                MinimumCheckerDetectionCellSize,
                MaximumCheckerDetectionCellSize);
            if (!SetProperty(ref _checkerDetectionMinimumCellSize, normalized))
            {
                return;
            }

            if (CheckerDetectionMaximumCellSize < normalized)
            {
                var previousSuppression = _suppressSettingsChanged;
                _suppressSettingsChanged = true;
                try
                {
                    CheckerDetectionMaximumCellSize = normalized;
                }
                finally
                {
                    _suppressSettingsChanged = previousSuppression;
                }
            }

            CheckerDetectionStatusText = "格宽搜索范围已变化，等待重新识别";
            MarkPreviewStale();
            RefreshValidationAndCommands();
            NotifySettingsChanged();
        }
    }

    public double CheckerDetectionMaximumCellSize
    {
        get => _checkerDetectionMaximumCellSize;
        set
        {
            var normalized = NormalizeFinite(
                value,
                double.IsFinite(_checkerDetectionMaximumCellSize)
                    ? _checkerDetectionMaximumCellSize
                    : 256d,
                MinimumCheckerDetectionCellSize,
                MaximumCheckerDetectionCellSize);
            if (!SetProperty(ref _checkerDetectionMaximumCellSize, normalized))
            {
                return;
            }

            if (CheckerDetectionMinimumCellSize > normalized)
            {
                var previousSuppression = _suppressSettingsChanged;
                _suppressSettingsChanged = true;
                try
                {
                    CheckerDetectionMinimumCellSize = normalized;
                }
                finally
                {
                    _suppressSettingsChanged = previousSuppression;
                }
            }

            CheckerDetectionStatusText = "格宽搜索范围已变化，等待重新识别";
            MarkPreviewStale();
            RefreshValidationAndCommands();
            NotifySettingsChanged();
        }
    }

    public double CheckerDetectionSearchRangePercent
    {
        get => _checkerDetectionSearchRangePercent;
        set
        {
            var normalized = NormalizeFinite(
                value,
                double.IsFinite(_checkerDetectionSearchRangePercent)
                    ? _checkerDetectionSearchRangePercent
                    : 25d,
                MinimumCheckerDetectionSearchRangePercent,
                MaximumCheckerDetectionSearchRangePercent);
            if (!SetProperty(ref _checkerDetectionSearchRangePercent, normalized))
            {
                return;
            }

            CheckerDetectionStatusText = "四角扫描范围已变化，等待重新识别";
            MarkPreviewStale();
            RefreshValidationAndCommands();
            NotifySettingsChanged();
        }
    }

    public string CheckerDetectionStatusText
    {
        get => _checkerDetectionStatusText;
        private set => SetProperty(ref _checkerDetectionStatusText, value);
    }

    public bool IsCheckerGridDetecting
    {
        get => _isCheckerGridDetecting;
        private set => SetProperty(ref _isCheckerGridDetecting, value);
    }

    public bool CanDetectCheckerGrid =>
        !IsBusy && HasInput && UseFakeTransparencyGrid;

    public void BeginCheckerAlignmentEdit()
    {
        if (_isCheckerAlignmentEditActive)
        {
            return;
        }

        _checkerAlignmentEditStartCellSize = CheckerCellSize;
        _checkerAlignmentEditStartOffsetX = CheckerOffsetX;
        _checkerAlignmentEditStartOffsetY = CheckerOffsetY;
        _checkerAlignmentEditStartBoundaryTolerance = CheckerBoundaryTolerance;
        _isCheckerAlignmentEditActive = true;
    }

    public void CommitCheckerAlignmentEdit()
    {
        if (!_isCheckerAlignmentEditActive)
        {
            return;
        }

        var changed = CheckerCellSize != _checkerAlignmentEditStartCellSize ||
                      CheckerOffsetX != _checkerAlignmentEditStartOffsetX ||
                      CheckerOffsetY != _checkerAlignmentEditStartOffsetY ||
                      CheckerBoundaryTolerance != _checkerAlignmentEditStartBoundaryTolerance;
        _isCheckerAlignmentEditActive = false;
        if (changed)
        {
            NotifySettingsChanged();
        }
    }

    public void CancelCheckerAlignmentEdit()
    {
        if (!_isCheckerAlignmentEditActive)
        {
            return;
        }

        var cellSize = _checkerAlignmentEditStartCellSize;
        var offsetX = _checkerAlignmentEditStartOffsetX;
        var offsetY = _checkerAlignmentEditStartOffsetY;
        var boundaryTolerance = _checkerAlignmentEditStartBoundaryTolerance;
        CheckerCellSize = cellSize;
        CheckerOffsetX = offsetX;
        CheckerOffsetY = offsetY;
        CheckerBoundaryTolerance = boundaryTolerance;
        _isCheckerAlignmentEditActive = false;
    }

    public string InputPath
    {
        get => _inputPath;
        private set
        {
            if (!SetProperty(ref _inputPath, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasInput));
            OnPropertyChanged(nameof(InputFileName));
            OnPropertyChanged(nameof(CanDetectCheckerGrid));
            CheckerDetectionStatusText = string.IsNullOrWhiteSpace(value)
                ? "尚未执行智能网格识别"
                : "输入图片已变化，等待重新识别";
            RefreshValidationAndCommands();
        }
    }

    public string InputFileName => HasInput ? Path.GetFileName(InputPath) : "尚未选择输入图片";

    public bool HasInput => File.Exists(InputPath);

    public int SourcePixelWidth
    {
        get => _sourcePixelWidth;
        private set => SetProperty(ref _sourcePixelWidth, value);
    }

    public int SourcePixelHeight
    {
        get => _sourcePixelHeight;
        private set => SetProperty(ref _sourcePixelHeight, value);
    }

    public string OutputPath
    {
        get => _outputPath;
        private set
        {
            if (!SetProperty(ref _outputPath, value))
            {
                return;
            }

            OnPropertyChanged(nameof(OutputFileName));
            RefreshValidationAndCommands();
        }
    }

    public string OutputFileName => string.IsNullOrWhiteSpace(OutputPath)
        ? "尚未设置输出 PNG"
        : Path.GetFileName(OutputPath);

    public string OutputPreviewPath
    {
        get => _outputPreviewPath;
        private set
        {
            if (!SetProperty(ref _outputPreviewPath, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasOutputPreview));
            OnPropertyChanged(nameof(IsPreviewStale));
            OnPropertyChanged(nameof(CanSaveResult));
            RaiseCommandStates();
        }
    }

    public bool HasOutputPreview => File.Exists(OutputPreviewPath);

    public string LastSavedOutputPath
    {
        get => _lastSavedOutputPath;
        private set
        {
            if (SetProperty(ref _lastSavedOutputPath, value))
            {
                OnPropertyChanged(nameof(HasSavedOutput));
                _openOutputDirectoryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSavedOutput => File.Exists(LastSavedOutputPath);

    public bool IsPreviewCurrent
    {
        get => _isPreviewCurrent;
        private set
        {
            if (SetProperty(ref _isPreviewCurrent, value))
            {
                OnPropertyChanged(nameof(IsPreviewStale));
                OnPropertyChanged(nameof(CanSaveResult));
                RaiseCommandStates();
            }
        }
    }

    public bool IsPreviewStale => HasOutputPreview && !IsPreviewCurrent;

    public string PreviewStatusText
    {
        get => _previewStatusText;
        private set => SetProperty(ref _previewStatusText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ResultSummary
    {
        get => _resultSummary;
        private set => SetProperty(ref _resultSummary, value);
    }

    public string BatchOutputDirectory
    {
        get => _batchOutputDirectory;
        private set
        {
            if (!SetProperty(ref _batchOutputDirectory, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasBatchOutputDirectory));
            OnPropertyChanged(nameof(CanProcessBatch));
            RaiseCommandStates();
        }
    }

    public bool HasBatchOutputDirectory => !string.IsNullOrWhiteSpace(BatchOutputDirectory);

    public string BatchStatusText
    {
        get => _batchStatusText;
        private set => SetProperty(ref _batchStatusText, value);
    }

    public bool IsBatchMode
    {
        get => _isBatchMode;
        set => SetProperty(ref _isBatchMode, value);
    }

    public BackgroundRemovalBatchItemViewModel? SelectedBatchItem
    {
        get => _selectedBatchItem;
        set
        {
            if (SetProperty(ref _selectedBatchItem, value))
            {
                _removeSelectedBatchItemCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string BatchCurrentFileName
    {
        get => _batchCurrentFileName;
        private set => SetProperty(ref _batchCurrentFileName, value);
    }

    public bool IsBatchRunning
    {
        get => _isBatchRunning;
        private set
        {
            if (!SetProperty(ref _isBatchRunning, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanProcessBatch));
            RaiseCommandStates();
        }
    }

    public int BatchTotalCount => BatchItems.Count;

    public int BatchCompletedCount
    {
        get => _batchCompletedCount;
        private set
        {
            if (SetProperty(ref _batchCompletedCount, value))
            {
                OnPropertyChanged(nameof(BatchPendingCount));
                OnPropertyChanged(nameof(BatchProgressPercent));
                OnPropertyChanged(nameof(BatchProgress));
                OnPropertyChanged(nameof(BatchProgressText));
            }
        }
    }

    public int BatchSucceededCount
    {
        get => _batchSucceededCount;
        private set
        {
            if (SetProperty(ref _batchSucceededCount, value))
            {
                OnPropertyChanged(nameof(BatchProgressText));
            }
        }
    }

    public int BatchFailedCount
    {
        get => _batchFailedCount;
        private set
        {
            if (SetProperty(ref _batchFailedCount, value))
            {
                OnPropertyChanged(nameof(HasBatchFailures));
                OnPropertyChanged(nameof(BatchProgressText));
            }
        }
    }

    public int BatchPendingCount => Math.Max(0, BatchTotalCount - BatchCompletedCount);

    public double BatchProgressPercent => BatchTotalCount == 0
        ? 0d
        : BatchCompletedCount * 100d / BatchTotalCount;

    public double BatchProgress => BatchProgressPercent;

    public string BatchProgressText => BatchTotalCount == 0
        ? "0 / 0"
        : $"{BatchCompletedCount} / {BatchTotalCount} · 成功 {BatchSucceededCount} · 失败 {BatchFailedCount}";

    public bool HasBatchItems => BatchItems.Count > 0;

    public bool HasBatchFailures => BatchFailedCount > 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanProcess));
            OnPropertyChanged(nameof(CanRefreshPreview));
            OnPropertyChanged(nameof(CanSaveResult));
            OnPropertyChanged(nameof(CanProcessBatch));
            OnPropertyChanged(nameof(CanDetectCheckerGrid));
            RaiseCommandStates();
        }
    }

    public bool CanRefreshPreview => !IsBusy && HasInput && IsConfigurationValid;

    public bool CanSaveResult => !IsBusy && IsPreviewCurrent && HasOutputPreview &&
                                 (string.IsNullOrWhiteSpace(OutputPath) ||
                                  !PathsEqual(InputPath, OutputPath));

    public bool CanProcess => CanRefreshPreview &&
                              !string.IsNullOrWhiteSpace(OutputPath) &&
                              !PathsEqual(InputPath, OutputPath);

    public bool CanProcessBatch => !IsBusy && !IsBatchRunning && HasBatchItems &&
                                   HasBatchOutputDirectory && IsConfigurationValid;

    public bool IsConfigurationValid => TryBuildSettings(out _, out _);

    public bool IsGenerationConfigurationValid => TryBuildSolidColorSettings(out _, out _);

    public string ConfigurationValidationText => TryBuildSettings(out _, out var reason)
        ? UseFakeTransparencyGrid
            ? EnableSmartCheckerDetection
                ? "参数有效；预览和批处理会从每张图片四角独立数格子，再按识别出的颜色、格宽和相位恢复 Alpha。"
                : "参数有效；将按手动双颜色棋盘格恢复 Alpha。单幅合成图无法精确还原原始半透明前景，边缘为近似恢复。"
            : "参数有效；最终输出保持原图尺寸并写入真实 Alpha。"
        : reason;

    public string GenerationConfigurationValidationText => TryBuildSolidColorSettings(out _, out var reason)
        ? "素材生成继续使用所选纯色底；假透明棋盘格仅作为独立工具兜底。"
        : reason;

    public ICommand ChooseInputCommand { get; }

    public ICommand ChooseOutputCommand { get; }

    public ICommand ProcessCommand { get; }

    public ICommand RefreshPreviewCommand { get; }

    public ICommand SaveResultCommand { get; }

    public ICommand OpenOutputDirectoryCommand { get; }

    public ICommand SwapCheckerColorsCommand { get; }

    public ICommand ChooseCustomColorCommand { get; }

    public ICommand ChooseCheckerColorACommand { get; }

    public ICommand ChooseCheckerColorBCommand { get; }

    public ICommand DetectCheckerGridCommand { get; }

    public ICommand ChooseBatchInputsCommand { get; }

    public ICommand ChooseBatchOutputDirectoryCommand { get; }

    public ICommand ProcessBatchCommand { get; }

    public ICommand ClearBatchCommand { get; }

    public ICommand ClearBatchItemsCommand => ClearBatchCommand;

    public ICommand RemoveSelectedBatchItemCommand { get; }

    private async Task DetectCheckerGridAsync()
    {
        if (!CanDetectCheckerGrid)
        {
            CheckerDetectionStatusText = "请先启用假透明棋盘格并选择输入图片。";
            return;
        }

        var inputPath = InputPath;
        var detectionSnapshot = CaptureCheckerDetectionSnapshot();
        var detectionRevision = _previewSettingsRevision;
        IsBusy = true;
        IsCheckerGridDetecting = true;
        CheckerDetectionStatusText = "正在从四角扫描灰白周期并统计格子…";
        StatusText = $"正在智能识别 {Path.GetFileName(inputPath)} 的假透明棋盘格…";
        try
        {
            var result = await DetectCheckerGridForPathAsync(inputPath, detectionSnapshot)
                .ConfigureAwait(true);
            if (!PathsEqual(inputPath, InputPath) || detectionRevision != _previewSettingsRevision)
            {
                CheckerDetectionStatusText = "识别期间输入图片或检测范围已变化；旧结果已丢弃。";
                return;
            }

            if (!result.IsDetected)
            {
                CheckerDetectionStatusText =
                    $"未能可靠识别（置信度 {result.Confidence:P0}）：{result.FailureReason}";
                StatusText = "智能识别未覆盖现有手动参数，可调整搜索范围后重试。";
                _workbenchLogSink?.Write(
                    WorkbenchLogLevel.Warning,
                    LogSource,
                    $"假透明棋盘格智能识别失败：{Path.GetFileName(inputPath)} · {result.FailureReason}");
                return;
            }

            ApplyCheckerDetectionResult(result);
            StatusText = "智能网格参数已填入；可继续拖动标尺微调或刷新透明结果。";
            _workbenchLogSink?.Write(
                WorkbenchLogLevel.Success,
                LogSource,
                $"假透明棋盘格智能识别完成：{Path.GetFileName(inputPath)} · {BuildCheckerDetectionSummary(result)}");
        }
        finally
        {
            IsCheckerGridDetecting = false;
            IsBusy = false;
        }
    }

    private async Task<CheckerGridDetectionResult> DetectCheckerGridForPathAsync(
        string inputPath,
        CheckerDetectionSnapshot detectionSnapshot)
    {
        try
        {
            var detectionSettings = CreateCheckerGridDetectionSettings(inputPath, detectionSnapshot);
            return await Task.Run(() =>
                    _checkerGridDetectionService.Detect(inputPath, detectionSettings))
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            return new CheckerGridDetectionResult
            {
                IsDetected = false,
                FailureReason = GetOperationErrorMessage(exception)
            };
        }
    }

    private static CheckerGridDetectionSettings CreateCheckerGridDetectionSettings(
        string inputPath,
        CheckerDetectionSnapshot detectionSnapshot)
    {
        using var stream = new FileStream(
            inputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var imageInfo = Image.Identify(stream)
            ?? throw new InvalidDataException("无法读取待识别图片的像素尺寸。");
        var shorterSide = Math.Max(1, Math.Min(imageInfo.Width, imageInfo.Height));
        var maximumRange = Math.Max(
            1,
            Math.Min((int)CheckerGridDetectionSettings.MaximumSearchRange, shorterSide / 2));
        var minimumRange = Math.Min(8, maximumRange);
        var requestedRange = (int)Math.Round(
            shorterSide * detectionSnapshot.SearchRangePercent / 100d,
            MidpointRounding.AwayFromZero);
        var searchRange = Math.Clamp(requestedRange, minimumRange, maximumRange);
        return new CheckerGridDetectionSettings
        {
            MinimumCellSize = detectionSnapshot.MinimumCellSize,
            MaximumCellSize = detectionSnapshot.MaximumCellSize,
            SearchRange = searchRange,
            MinimumConfidence = 0.55d
        };
    }

    private CheckerDetectionSnapshot CaptureCheckerDetectionSnapshot() => new(
        EnableSmartCheckerDetection,
        CheckerDetectionMinimumCellSize,
        CheckerDetectionMaximumCellSize,
        CheckerDetectionSearchRangePercent);

    private void ApplyCheckerDetectionResult(CheckerGridDetectionResult result)
    {
        var previousSuppression = _suppressSettingsChanged;
        _suppressSettingsChanged = true;
        try
        {
            CheckerColorAHex = result.ColorAHex;
            CheckerColorBHex = result.ColorBHex;
            EnableCheckerGridAlignment = true;
            CheckerCellSize = result.CellSize;
            CheckerOffsetX = result.OffsetX;
            CheckerOffsetY = result.OffsetY;
        }
        finally
        {
            _suppressSettingsChanged = previousSuppression;
        }

        CheckerDetectionStatusText = BuildCheckerDetectionSummary(result);
        if (!previousSuppression)
        {
            NotifySettingsChanged();
        }
    }

    private static BackgroundRemovalSettings ApplyCheckerDetection(
        BackgroundRemovalSettings settings,
        CheckerGridDetectionResult result) => settings with
    {
        UseFakeTransparencyGrid = true,
        CheckerColorAHex = result.ColorAHex,
        CheckerColorBHex = result.ColorBHex,
        EnableCheckerGridAlignment = true,
        CheckerCellSize = result.CellSize,
        CheckerOffsetX = result.OffsetX,
        CheckerOffsetY = result.OffsetY
    };

    private static string BuildCheckerDetectionSummary(CheckerGridDetectionResult result) =>
        $"识别成功：{result.CellSize:0.###}px（可信范围 {result.CellSizeMinimum:0.###}–{result.CellSizeMaximum:0.###}px），" +
        $"偏移 ({result.OffsetX:0.###},{result.OffsetY:0.###})，" +
        $"约 {result.HorizontalCellCount}×{result.VerticalCellCount} 格，" +
        $"四角 {result.DetectedCornerCount}/4，置信度 {result.Confidence:P0}。";

    private void ChooseInput()
    {
        try
        {
            var selected = _inputPicker.SelectInputFile(_lastInputDirectory);
            if (string.IsNullOrWhiteSpace(selected))
            {
                return;
            }

            var normalized = Path.GetFullPath(selected);
            if (!File.Exists(normalized) || !SupportedInputExtensions.Contains(Path.GetExtension(normalized)))
            {
                StatusText = "选择的文件不是受支持的图片。";
                return;
            }

            UpdateSourcePixelDimensions(normalized);
            ClearPreview();
            InputPath = normalized;
            _lastInputDirectory = Path.GetDirectoryName(normalized);
            OutputPath = string.Empty;
            LastSavedOutputPath = string.Empty;
            ResultSummary = "输入图片已就绪；刷新预览不需要先设置输出位置。";
            PreviewStatusText = "输入已变化，请刷新预览";
            StatusText = $"已选择：{Path.GetFileName(normalized)}";
            NotifySettingsChanged();
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"无法选择输入图片：{exception.Message}";
        }
    }

    private void UpdateSourcePixelDimensions(string path)
    {
        SourcePixelWidth = 0;
        SourcePixelHeight = 0;

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var imageInfo = Image.Identify(stream);
            if (imageInfo is null || imageInfo.Width <= 0 || imageInfo.Height <= 0)
            {
                return;
            }

            SourcePixelWidth = imageInfo.Width;
            SourcePixelHeight = imageInfo.Height;
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            // Dimension lookup only improves the alignment overlay. Keep the selected
            // file so the normal processing path can report a precise decode error.
        }
    }

    private void ChooseOutput()
    {
        if (!HasInput)
        {
            StatusText = "请先选择输入图片。";
            return;
        }

        try
        {
            var inputDirectory = Path.GetDirectoryName(InputPath);
            var initialDirectory = _lastOutputDirectory ?? inputDirectory;
            var suggestedName = $"{Path.GetFileNameWithoutExtension(InputPath)}_transparent.png";
            var selected = _outputPicker.SelectOutputFile(initialDirectory, suggestedName);
            if (string.IsNullOrWhiteSpace(selected))
            {
                return;
            }

            var normalized = Path.GetFullPath(selected);
            if (!string.Equals(Path.GetExtension(normalized), ".png", StringComparison.OrdinalIgnoreCase))
            {
                normalized = Path.ChangeExtension(normalized, ".png");
            }

            OutputPath = normalized;
            _lastOutputDirectory = Path.GetDirectoryName(normalized);
            StatusText = IsPreviewCurrent
                ? $"输出位置：{normalized}；当前预览可直接保存。"
                : $"输出位置：{normalized}";
            NotifySettingsChanged();
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"无法选择输出位置：{exception.Message}";
        }
    }

    private void ChooseBatchInputs()
    {
        try
        {
            var selectedPaths = _inputPicker.SelectInputFiles(_lastInputDirectory);
            if (selectedPaths.Count == 0)
            {
                return;
            }

            var normalizedPaths = new List<string>(selectedPaths.Count);
            var remainingCapacity = Math.Max(0, MaximumBatchItemCount - BatchItems.Count);
            var seenPaths = new HashSet<string>(
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var existingItem in BatchItems)
            {
                seenPaths.Add(existingItem.SourcePath);
            }

            var rejectedCount = 0;
            var capacityRejectedCount = 0;
            foreach (var selectedPath in selectedPaths)
            {
                if (normalizedPaths.Count >= remainingCapacity)
                {
                    capacityRejectedCount++;
                    continue;
                }

                try
                {
                    var normalized = Path.GetFullPath(selectedPath);
                    if (!File.Exists(normalized) ||
                        !SupportedInputExtensions.Contains(Path.GetExtension(normalized)) ||
                        !seenPaths.Add(normalized))
                    {
                        rejectedCount++;
                        continue;
                    }

                    normalizedPaths.Add(normalized);
                }
                catch (Exception exception) when (IsExpectedOperationException(exception))
                {
                    rejectedCount++;
                }
            }

            if (normalizedPaths.Count == 0)
            {
                BatchStatusText = remainingCapacity == 0
                    ? $"批处理队列已达到 {MaximumBatchItemCount} 项上限。"
                    : "没有选中可处理的图片；原批处理队列保持不变。";
                StatusText = BatchStatusText;
                return;
            }

            BackgroundRemovalBatchItemViewModel? firstAddedItem = null;
            foreach (var normalizedPath in normalizedPaths)
            {
                var item = new BackgroundRemovalBatchItemViewModel(normalizedPath);
                BatchItems.Add(item);
                firstAddedItem ??= item;
            }

            _lastInputDirectory = Path.GetDirectoryName(normalizedPaths[0]);
            RecalculateBatchProgressFromItems();
            SelectedBatchItem = firstAddedItem;
            IsBatchMode = true;
            NotifyBatchCollectionChanged();
            var ignoredReasons = new List<string>(2);
            if (rejectedCount > 0)
            {
                ignoredReasons.Add($"{rejectedCount} 个重复或无效文件");
            }

            if (capacityRejectedCount > 0)
            {
                ignoredReasons.Add($"{capacityRejectedCount} 个超出 {MaximumBatchItemCount} 项上限的文件");
            }

            BatchStatusText = ignoredReasons.Count == 0
                ? $"已加入 {normalizedPaths.Count} 张图片；队列共 {BatchTotalCount} 项。"
                : $"已加入 {normalizedPaths.Count} 张图片；忽略 {string.Join("、", ignoredReasons)}。";
            StatusText = BatchStatusText;
            NotifySettingsChanged();
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            BatchStatusText = $"无法选择批处理图片：{exception.Message}";
            StatusText = BatchStatusText;
        }
    }

    private void ChooseBatchOutputDirectory()
    {
        if (!HasBatchItems)
        {
            BatchStatusText = "请先选择需要批量处理的图片。";
            StatusText = BatchStatusText;
            return;
        }

        try
        {
            var firstInputDirectory = Path.GetDirectoryName(BatchItems[0].SourcePath);
            var selectedDirectory = _outputPicker.SelectOutputDirectory(
                _lastOutputDirectory ?? firstInputDirectory);
            if (string.IsNullOrWhiteSpace(selectedDirectory))
            {
                return;
            }

            var normalized = Path.GetFullPath(selectedDirectory);
            Directory.CreateDirectory(normalized);
            BatchOutputDirectory = normalized;
            _lastOutputDirectory = normalized;
            BatchStatusText = $"批处理输出目录：{normalized}";
            StatusText = BatchStatusText;
            NotifySettingsChanged();
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            BatchStatusText = $"无法设置批处理输出目录：{exception.Message}";
            StatusText = BatchStatusText;
        }
    }

    private void ClearBatch()
    {
        BatchItems.Clear();
        SelectedBatchItem = null;
        BatchCurrentFileName = string.Empty;
        ResetBatchProgress();
        NotifyBatchCollectionChanged();
        BatchStatusText = "批处理队列已清空";
        StatusText = BatchStatusText;
    }

    private void RemoveSelectedBatchItem()
    {
        var selectedItem = SelectedBatchItem;
        if (selectedItem is null)
        {
            return;
        }

        var removedIndex = BatchItems.IndexOf(selectedItem);
        if (removedIndex < 0)
        {
            SelectedBatchItem = null;
            return;
        }

        BatchItems.RemoveAt(removedIndex);
        SelectedBatchItem = BatchItems.Count == 0
            ? null
            : BatchItems[Math.Min(removedIndex, BatchItems.Count - 1)];
        RecalculateBatchProgressFromItems();
        NotifyBatchCollectionChanged();
        BatchStatusText = $"已从批处理队列移除 {selectedItem.FileName}；剩余 {BatchTotalCount} 项。";
        StatusText = BatchStatusText;
    }

    private async Task ProcessBatchAsync()
    {
        if (!TryBuildSettings(out var settings, out var reason) || !CanProcessBatch)
        {
            BatchStatusText = string.IsNullOrWhiteSpace(reason)
                ? "批处理输入、输出目录或参数尚未就绪。"
                : reason;
            StatusText = BatchStatusText;
            return;
        }

        var detectionSnapshot = CaptureCheckerDetectionSnapshot();

        var outputDirectory = Path.GetFullPath(BatchOutputDirectory);
        try
        {
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            BatchStatusText = $"无法创建批处理输出目录：{exception.Message}";
            StatusText = BatchStatusText;
            return;
        }

        var reservedOutputPaths = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var item in BatchItems)
        {
            item.Reset(string.Empty);
        }

        ResetBatchProgress();
        IsBatchRunning = true;
        IsBusy = true;
        IsBatchMode = true;
        BatchCurrentFileName = string.Empty;
        BatchStatusText = $"正在批量处理 0 / {BatchTotalCount}…";
        StatusText = BatchStatusText;
        _workbenchLogSink?.Write(
            WorkbenchLogLevel.Information,
            LogSource,
            $"开始批量背景剔除：{BatchTotalCount} 项 · 输出目录 {outputDirectory}");

        string? lastSuccessfulOutput = null;
        try
        {
            foreach (var item in BatchItems)
            {
                BatchCurrentFileName = item.FileName;
                BatchStatusText =
                    $"正在处理 {BatchCompletedCount + 1} / {BatchTotalCount}：{item.FileName}";
                StatusText = BatchStatusText;

                try
                {
                    item.Reset(ReserveBatchOutputPath(
                        outputDirectory,
                        item.SourcePath,
                        reservedOutputPaths));
                    var itemSettings = settings;
                    if (settings.UseFakeTransparencyGrid && detectionSnapshot.Enabled)
                    {
                        item.MarkDetecting();
                        BatchStatusText =
                            $"正在识别 {BatchCompletedCount + 1} / {BatchTotalCount}：{item.FileName}";
                        var detectionResult = await DetectCheckerGridForPathAsync(
                                item.SourcePath,
                                detectionSnapshot)
                            .ConfigureAwait(true);
                        item.SetCheckerDetection(detectionResult, usedFallback: false);
                        if (!detectionResult.IsDetected)
                        {
                            throw new InvalidOperationException(
                                $"智能棋盘格识别失败（置信度 {detectionResult.Confidence:P0}）：" +
                                $"{detectionResult.FailureReason}；该项未套用其他图片的网格参数。");
                        }

                        itemSettings = ApplyCheckerDetection(settings, detectionResult);
                        _workbenchLogSink?.Write(
                            WorkbenchLogLevel.Information,
                            LogSource,
                            $"批量图片网格识别完成：{item.FileName} · {BuildCheckerDetectionSummary(detectionResult)}");
                    }

                    item.MarkProcessing();
                    var result = await Task.Run(() =>
                            _backgroundRemovalService.RemoveToPng(
                                item.SourcePath,
                                item.OutputPath,
                                itemSettings,
                                overwrite: false))
                        .ConfigureAwait(true);
                    var renderedOutputPath = Path.GetFullPath(result.OutputPath);
                    if (!File.Exists(renderedOutputPath))
                    {
                        throw new IOException("背景剔除服务没有生成预期的输出 PNG。");
                    }

                    item.MarkCompleted(result);
                    BatchSucceededCount++;
                    lastSuccessfulOutput = renderedOutputPath;
                    _workbenchLogSink?.Write(
                        WorkbenchLogLevel.Success,
                        LogSource,
                        $"批量背景剔除完成：{item.FileName} -> {renderedOutputPath}");
                }
                catch (Exception exception)
                {
                    var errorMessage = GetOperationErrorMessage(exception);
                    item.MarkFailed(errorMessage);
                    BatchFailedCount++;
                    _workbenchLogSink?.Write(
                        WorkbenchLogLevel.Error,
                        LogSource,
                        $"批量背景剔除单项失败，继续处理后续图片：{item.FileName} · {errorMessage}",
                        exception: exception);
                }

                BatchCompletedCount++;
            }

            if (!string.IsNullOrWhiteSpace(lastSuccessfulOutput))
            {
                LastSavedOutputPath = lastSuccessfulOutput;
            }

            BatchStatusText = BatchFailedCount == 0
                ? $"批处理完成：{BatchSucceededCount} 张图片已输出到 {outputDirectory}"
                : $"批处理完成：成功 {BatchSucceededCount} 张，失败 {BatchFailedCount} 张；失败项已保留在队列中。";
            StatusText = BatchStatusText;
            _workbenchLogSink?.Write(
                BatchFailedCount == 0 ? WorkbenchLogLevel.Success : WorkbenchLogLevel.Warning,
                LogSource,
                BatchStatusText);
        }
        finally
        {
            BatchCurrentFileName = string.Empty;
            IsBatchRunning = false;
            IsBusy = false;
        }
    }

    private static string ReserveBatchOutputPath(
        string outputDirectory,
        string sourcePath,
        ISet<string> reservedOutputPaths)
    {
        var sourceName = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            sourceName = "image";
        }

        var baseName = $"{sourceName}_transparent";
        var suffix = 1;
        while (true)
        {
            var fileName = suffix == 1 ? $"{baseName}.png" : $"{baseName}_{suffix}.png";
            var candidate = Path.GetFullPath(Path.Combine(outputDirectory, fileName));
            if (!File.Exists(candidate) &&
                !PathsEqual(candidate, sourcePath) &&
                reservedOutputPaths.Add(candidate))
            {
                return candidate;
            }

            suffix++;
        }
    }

    private void ResetBatchProgress()
    {
        BatchCompletedCount = 0;
        BatchSucceededCount = 0;
        BatchFailedCount = 0;
    }

    private void RecalculateBatchProgressFromItems()
    {
        BatchCurrentFileName = string.Empty;
        BatchSucceededCount = BatchItems.Count(item => item.Status == BackgroundRemovalBatchItemStatus.Completed);
        BatchFailedCount = BatchItems.Count(item => item.Status == BackgroundRemovalBatchItemStatus.Failed);
        BatchCompletedCount = BatchSucceededCount + BatchFailedCount;
    }

    private void NotifyBatchCollectionChanged()
    {
        OnPropertyChanged(nameof(BatchTotalCount));
        OnPropertyChanged(nameof(BatchPendingCount));
        OnPropertyChanged(nameof(BatchProgressPercent));
        OnPropertyChanged(nameof(BatchProgress));
        OnPropertyChanged(nameof(BatchProgressText));
        OnPropertyChanged(nameof(HasBatchItems));
        OnPropertyChanged(nameof(CanProcessBatch));
        RaiseCommandStates();
    }

    private async Task RefreshPreviewAsync()
    {
        if (!TryBuildSettings(out var settings, out var reason) || !CanRefreshPreview)
        {
            StatusText = string.IsNullOrWhiteSpace(reason) ? "当前输入或参数尚未就绪。" : reason;
            return;
        }

        var inputPath = InputPath;
        var detectionSnapshot = CaptureCheckerDetectionSnapshot();
        var detectionRevision = _previewSettingsRevision;
        var previousPreviewPath = OutputPreviewPath;
        string? reservedPreviewPath = null;
        IsBusy = true;
        IsPreviewCurrent = false;
        PreviewStatusText = "正在刷新缓存预览…";
        try
        {
            if (settings.UseFakeTransparencyGrid && detectionSnapshot.Enabled)
            {
                IsCheckerGridDetecting = true;
                CheckerDetectionStatusText = "刷新前正在逐图识别棋盘格…";
                var detectionResult = await DetectCheckerGridForPathAsync(inputPath, detectionSnapshot)
                    .ConfigureAwait(true);
                IsCheckerGridDetecting = false;
                if (!PathsEqual(inputPath, InputPath) || detectionRevision != _previewSettingsRevision)
                {
                    PreviewStatusText = "识别期间输入图片或检测范围已变化；旧结果已丢弃";
                    StatusText = "请针对当前输入和参数重新刷新预览。";
                    return;
                }

                if (!detectionResult.IsDetected)
                {
                    CheckerDetectionStatusText =
                        $"未能可靠识别（置信度 {detectionResult.Confidence:P0}）：{detectionResult.FailureReason}";
                    PreviewStatusText = "智能网格识别失败，未执行背景剔除";
                    StatusText = "可扩大角部识别范围、调整格宽搜索范围，或关闭智能识别后使用手动标尺。";
                    _workbenchLogSink?.Write(
                        WorkbenchLogLevel.Warning,
                        LogSource,
                        $"刷新预览前的棋盘格识别失败：{Path.GetFileName(inputPath)} · {detectionResult.FailureReason}");
                    return;
                }

                ApplyCheckerDetectionResult(detectionResult);
                settings = ApplyCheckerDetection(settings, detectionResult);
            }

            var settingsRevision = _previewSettingsRevision;
            var modeText = settings.UseFakeTransparencyGrid
                ? $"假透明棋盘格 {settings.CheckerColorAHex} / {settings.CheckerColorBHex}"
                : $"纯色 {settings.KeyColorHex}";
            StatusText = $"正在使用{modeText}剔除背景并刷新预览…";
            _workbenchLogSink?.Write(
                WorkbenchLogLevel.Information,
                LogSource,
                $"开始刷新背景剔除预览：{Path.GetFileName(inputPath)} · {modeText}");
            reservedPreviewPath = _previewCache.ReservePreviewPath("background-removal");
            _previewCache.PinPreview(reservedPreviewPath);
            var result = await Task.Run(() =>
                    _backgroundRemovalService.RemoveToPng(inputPath, reservedPreviewPath, settings, overwrite: false))
                .ConfigureAwait(true);
            var renderedPreviewPath = Path.GetFullPath(result.OutputPath);
            if (!File.Exists(renderedPreviewPath) || !_previewCache.IsManagedPreviewPath(renderedPreviewPath))
            {
                throw new IOException("背景剔除服务未生成有效的受管缓存 PNG。");
            }

            if (!PathsEqual(reservedPreviewPath, renderedPreviewPath))
            {
                _previewCache.PinPreview(renderedPreviewPath);
                ReleaseSupersededPreview(reservedPreviewPath, renderedPreviewPath);
                reservedPreviewPath = renderedPreviewPath;
            }

            if (!PathsEqual(inputPath, InputPath))
            {
                ReleaseSupersededPreview(renderedPreviewPath, keepPath: null);
                PreviewStatusText = "刷新期间输入图片发生变化；已丢弃旧输入的预览结果";
                StatusText = "背景剔除预览已丢弃，请针对当前输入重新刷新。";
                return;
            }

            OutputPreviewPath = renderedPreviewPath;
            var remainsCurrent = settingsRevision == _previewSettingsRevision;
            IsPreviewCurrent = remainsCurrent;
            PreviewStatusText = remainsCurrent
                ? "预览已刷新，可设置输出位置并保存结果"
                : "刷新期间参数发生变化；结果仅供对比，请再次刷新";
            ResultSummary =
                $"{result.Width} × {result.Height} px · 剔除 {result.RemovedPixelCount:N0} 像素 · " +
                $"透明 {result.TransparentPixelCount:N0} · 半透明 {result.PartialPixelCount:N0}";
            StatusText = remainsCurrent
                ? "背景剔除预览已刷新；尚未写入最终输出位置。"
                : "背景剔除预览已生成，但参数已变化，需要再次刷新。";
            ReleaseSupersededPreview(previousPreviewPath, renderedPreviewPath);
            _workbenchLogSink?.Write(
                WorkbenchLogLevel.Success,
                LogSource,
                $"背景剔除缓存预览完成：{Path.GetFileName(inputPath)} · " +
                $"{result.Width}×{result.Height} px · 剔除 {result.RemovedPixelCount:N0} 像素");
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            ReleaseSupersededPreview(reservedPreviewPath, previousPreviewPath);
            var errorMessage = GetOperationErrorMessage(exception);
            PreviewStatusText = HasOutputPreview
                ? "预览刷新失败；保留上一次结果但已标记为过期，不能保存"
                : "预览刷新失败，尚无可用结果";
            if (!HasOutputPreview)
            {
                ResultSummary = "处理失败，输入文件保持不变。";
            }

            StatusText = $"背景剔除失败：{errorMessage}";
            _workbenchLogSink?.Write(
                WorkbenchLogLevel.Error,
                LogSource,
                $"背景剔除预览失败：{Path.GetFileName(inputPath)} · {errorMessage}",
                exception: exception);
        }
        finally
        {
            IsCheckerGridDetecting = false;
            IsBusy = false;
        }
    }

    private async Task ProcessLegacyAsync()
    {
        await RefreshPreviewAsync().ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(OutputPath) && CanSaveResult)
        {
            await SaveResultAsync().ConfigureAwait(true);
        }
    }

    private async Task SaveResultAsync()
    {
        if (!CanSaveResult)
        {
            StatusText = IsPreviewStale
                ? "当前预览已过期，请先刷新预览。"
                : "请先刷新预览并设置最终输出位置。";
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            ChooseOutput();
        }

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            return;
        }

        if (!CanSaveResult)
        {
            StatusText = "最终输出路径不能覆盖输入图片，请重新设置输出位置。";
            return;
        }

        var previewPath = OutputPreviewPath;
        var outputPath = OutputPath;
        IsBusy = true;
        StatusText = "正在保存当前预览…";
        try
        {
            var publishedPath = await Task.Run(() =>
                    _previewCache.PublishPreview(previewPath, outputPath, overwrite: true))
                .ConfigureAwait(true);
            LastSavedOutputPath = publishedPath;
            PreviewStatusText = "当前预览已保存；参数变化后需重新刷新";
            StatusText = $"背景剔除结果已保存：{publishedPath}";
            _workbenchLogSink?.Write(
                WorkbenchLogLevel.Success,
                LogSource,
                $"背景剔除预览已原样发布：{publishedPath}");
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            var errorMessage = GetOperationErrorMessage(exception);
            StatusText = $"无法保存背景剔除结果：{errorMessage}";
            _workbenchLogSink?.Write(
                WorkbenchLogLevel.Error,
                LogSource,
                $"背景剔除保存失败：{outputPath} · {errorMessage}",
                exception: exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenOutputDirectory()
    {
        try
        {
            var directory = Path.GetDirectoryName(LastSavedOutputPath);
            if (!Directory.Exists(directory))
            {
                StatusText = "输出目录当前不可用。";
                return;
            }

            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"无法打开输出目录：{exception.Message}";
        }
    }

    private void ChooseCustomColor() => PickColor(
        CustomColorHex,
        "选择背景剔除自定义颜色",
        color => CustomColorHex = color);

    private void ChooseCheckerColorA() => PickColor(
        CheckerColorAHex,
        "选择假透明网格颜色 A",
        color => CheckerColorAHex = color);

    private void ChooseCheckerColorB() => PickColor(
        CheckerColorBHex,
        "选择假透明网格颜色 B",
        color => CheckerColorBHex = color);

    private void PickColor(string initialColor, string title, Action<string> applyColor)
    {
        var selected = _colorPickerService.PickColor(initialColor, title);
        if (selected is null)
        {
            return;
        }

        applyColor(selected.Hex);
        StatusText = $"颜色已设置为 {selected.Hex}；请刷新预览。";
    }

    private void SwapCheckerColors()
    {
        var colorA = CheckerColorAHex;
        CheckerColorAHex = CheckerColorBHex;
        CheckerColorBHex = colorA;
        StatusText = "已交换假透明网格的 A/B 颜色相位。";
    }

    private void MarkPreviewStale()
    {
        _previewSettingsRevision++;
        var previewPath = OutputPreviewPath;
        if (string.IsNullOrWhiteSpace(previewPath))
        {
            return;
        }

        if (!File.Exists(previewPath))
        {
            ReleaseSupersededPreview(previewPath, keepPath: null);
            OutputPreviewPath = string.Empty;
            IsPreviewCurrent = false;
            PreviewStatusText = "预览缓存已不存在，请重新刷新";
            return;
        }

        IsPreviewCurrent = false;
        PreviewStatusText = "参数已变化；保留上一次预览用于对比，刷新后才能保存";
    }

    private void ClearPreview()
    {
        _previewSettingsRevision++;
        var previousPreviewPath = OutputPreviewPath;
        OutputPreviewPath = string.Empty;
        IsPreviewCurrent = false;
        PreviewStatusText = "尚未刷新预览";
        ReleaseSupersededPreview(previousPreviewPath, keepPath: null);
    }

    private void ReleaseSupersededPreview(string? candidatePath, string? keepPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) ||
            PathsEqual(candidatePath, keepPath ?? string.Empty) ||
            !_previewCache.IsManagedPreviewPath(candidatePath))
        {
            return;
        }

        try
        {
            _previewCache.ReleasePreview(candidatePath);
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            _workbenchLogSink?.Write(
                WorkbenchLogLevel.Warning,
                LogSource,
                $"无法清理过期的背景剔除预览缓存：{exception.Message}");
        }
    }

    private bool TryBuildSettings(out BackgroundRemovalSettings settings, out string reason)
    {
        if (!UseFakeTransparencyGrid)
        {
            return TryBuildSolidColorSettings(out settings, out reason);
        }

        if (!TryBuildMatteSettings(out settings, out reason))
        {
            return false;
        }

        if (!double.IsFinite(CheckerBoundaryTolerance) ||
            CheckerBoundaryTolerance is < 0d or > MaximumCheckerBoundaryTolerance)
        {
            reason = "网格边界容差必须是 0 到 8 之间的有限像素值。";
            return false;
        }

        if (EnableSmartCheckerDetection)
        {
            if (!double.IsFinite(CheckerDetectionMinimumCellSize) ||
                !double.IsFinite(CheckerDetectionMaximumCellSize) ||
                CheckerDetectionMinimumCellSize is < MinimumCheckerDetectionCellSize or > MaximumCheckerDetectionCellSize ||
                CheckerDetectionMaximumCellSize is < MinimumCheckerDetectionCellSize or > MaximumCheckerDetectionCellSize ||
                CheckerDetectionMaximumCellSize < CheckerDetectionMinimumCellSize)
            {
                reason = "智能识别格宽范围无效；最小值必须小于等于最大值。";
                return false;
            }

            if (!double.IsFinite(CheckerDetectionSearchRangePercent) ||
                CheckerDetectionSearchRangePercent is < MinimumCheckerDetectionSearchRangePercent or > MaximumCheckerDetectionSearchRangePercent)
            {
                reason = "四角扫描范围必须是图片短边的 5% 到 50%。";
                return false;
            }

            settings = settings with
            {
                UseFakeTransparencyGrid = true,
                EnableCheckerGridAlignment = true,
                CheckerBoundaryTolerance = CheckerBoundaryTolerance
            };
            return true;
        }

        string checkerColorA;
        string checkerColorB;
        try
        {
            checkerColorA = BackgroundKeyColor.NormalizeHex(CheckerColorAHex);
            checkerColorB = BackgroundKeyColor.NormalizeHex(CheckerColorBHex);
        }
        catch (ArgumentException exception)
        {
            reason = exception.Message;
            return false;
        }

        if (string.Equals(checkerColorA, checkerColorB, StringComparison.Ordinal))
        {
            reason = "假透明网格的两种颜色不能相同。";
            return false;
        }

        if (!double.IsFinite(CheckerCellSize) ||
            CheckerCellSize is < MinimumCheckerCellSize or > MaximumCheckerCellSize)
        {
            reason = "假透明网格尺寸必须是 0.25 到 4096 之间的有限像素值。";
            return false;
        }

        if (!double.IsFinite(CheckerOffsetX) || !double.IsFinite(CheckerOffsetY))
        {
            reason = "假透明网格偏移必须是有限像素值。";
            return false;
        }

        settings = settings with
        {
            UseFakeTransparencyGrid = true,
            CheckerColorAHex = checkerColorA,
            CheckerColorBHex = checkerColorB,
            EnableCheckerGridAlignment = EnableCheckerGridAlignment,
            CheckerCellSize = CheckerCellSize,
            CheckerOffsetX = CheckerOffsetX,
            CheckerOffsetY = CheckerOffsetY,
            CheckerBoundaryTolerance = CheckerBoundaryTolerance
        };
        return true;
    }

    private bool TryBuildSolidColorSettings(out BackgroundRemovalSettings settings, out string reason)
    {
        if (!TryBuildMatteSettings(out settings, out reason))
        {
            return false;
        }

        string keyColor;
        try
        {
            keyColor = BackgroundKeyColor.ResolveHex(SelectedPreset.Preset, CustomColorHex);
        }
        catch (ArgumentException exception)
        {
            reason = exception.Message;
            return false;
        }

        settings = settings with
        {
            KeyColorHex = keyColor
        };
        return true;
    }

    private bool TryBuildMatteSettings(out BackgroundRemovalSettings settings, out string reason)
    {
        settings = new BackgroundRemovalSettings();
        reason = string.Empty;
        if (!double.IsFinite(TransparentThreshold) ||
            !double.IsFinite(OpaqueThreshold) ||
            TransparentThreshold is < 0d or > 441.7d ||
            OpaqueThreshold is <= 0d or > 441.7d ||
            OpaqueThreshold <= TransparentThreshold)
        {
            reason = "不透明阈值必须大于透明阈值，且两者需位于有效 RGB 距离范围内。";
            return false;
        }

        settings = new BackgroundRemovalSettings
        {
            TransparentThreshold = TransparentThreshold,
            OpaqueThreshold = OpaqueThreshold,
            EdgeContract = EdgeContract,
            EdgeFeather = EdgeFeather,
            Despill = Despill,
            EdgeConnectedOnly = EdgeConnectedOnly
        };
        return true;
    }

    private void RefreshValidationAndCommands()
    {
        OnPropertyChanged(nameof(IsConfigurationValid));
        OnPropertyChanged(nameof(IsGenerationConfigurationValid));
        OnPropertyChanged(nameof(ConfigurationValidationText));
        OnPropertyChanged(nameof(GenerationConfigurationValidationText));
        OnPropertyChanged(nameof(CanProcess));
        OnPropertyChanged(nameof(CanRefreshPreview));
        OnPropertyChanged(nameof(CanSaveResult));
        OnPropertyChanged(nameof(CanProcessBatch));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        _chooseInputCommand.RaiseCanExecuteChanged();
        _chooseOutputCommand.RaiseCanExecuteChanged();
        _processCommand.RaiseCanExecuteChanged();
        _refreshPreviewCommand.RaiseCanExecuteChanged();
        _saveResultCommand.RaiseCanExecuteChanged();
        _openOutputDirectoryCommand.RaiseCanExecuteChanged();
        _swapCheckerColorsCommand.RaiseCanExecuteChanged();
        _chooseCustomColorCommand.RaiseCanExecuteChanged();
        _chooseCheckerColorACommand.RaiseCanExecuteChanged();
        _chooseCheckerColorBCommand.RaiseCanExecuteChanged();
        _detectCheckerGridCommand.RaiseCanExecuteChanged();
        _chooseBatchInputsCommand.RaiseCanExecuteChanged();
        _chooseBatchOutputDirectoryCommand.RaiseCanExecuteChanged();
        _processBatchCommand.RaiseCanExecuteChanged();
        _clearBatchCommand.RaiseCanExecuteChanged();
        _removeSelectedBatchItemCommand.RaiseCanExecuteChanged();
    }

    private void NotifySettingsChanged()
    {
        if (_suppressSettingsChanged)
        {
            return;
        }

        SettingsChanged?.Invoke(this, new BackgroundRemovalSettingsChangedEventArgs(CaptureSettings()));
    }

    private void NotifyCheckerAlignmentSettingsChanged()
    {
        if (_isCheckerAlignmentEditActive)
        {
            return;
        }

        NotifySettingsChanged();
    }

    public BackgroundRemovalToolSettingsSnapshot CaptureSettings() => new(
        SelectedPreset.Preset.ToString().ToLowerInvariant(),
        CustomColorHex,
        TransparentThreshold,
        OpaqueThreshold,
        EdgeContract,
        EdgeFeather,
        Despill,
        EdgeConnectedOnly,
        UseFakeTransparencyGrid,
        CheckerColorAHex,
        CheckerColorBHex,
        EnableCheckerGridAlignment,
        CheckerCellSize,
        CheckerOffsetX,
        CheckerOffsetY,
        CheckerBoundaryTolerance,
        EnableSmartCheckerDetection,
        CheckerDetectionMinimumCellSize,
        CheckerDetectionMaximumCellSize,
        CheckerDetectionSearchRangePercent,
        _lastInputDirectory,
        _lastOutputDirectory);

    public GenerationBackgroundRemovalSpec CreateGenerationSpec()
    {
        if (!TryBuildSolidColorSettings(out var settings, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        return new GenerationBackgroundRemovalSpec
        {
            Preset = SelectedPreset.Preset.ToString().ToLowerInvariant(),
            ColorHex = settings.KeyColorHex,
            TransparentThreshold = settings.TransparentThreshold,
            OpaqueThreshold = settings.OpaqueThreshold,
            EdgeContract = settings.EdgeContract,
            EdgeFeather = settings.EdgeFeather,
            Despill = settings.Despill,
            EdgeConnectedOnly = settings.EdgeConnectedOnly
        };
    }

    private static BackgroundRemovalPresetOption ResolvePresetOption(string? presetKey)
    {
        if (Enum.TryParse<BackgroundKeyPreset>(presetKey, ignoreCase: true, out var preset) && Enum.IsDefined(preset))
        {
            return SupportedPresets.First(option => option.Preset == preset);
        }

        return SupportedPresets[0];
    }

    private static string NormalizeInitialCustomColor(string? customColorHex)
    {
        try
        {
            return BackgroundKeyColor.NormalizeHex(customColorHex ?? BackgroundKeyColor.DefaultCustomColorHex);
        }
        catch (ArgumentException)
        {
            return BackgroundKeyColor.DefaultCustomColorHex;
        }
    }

    private static string ResolveDisplayColor(string colorHex)
    {
        try
        {
            return BackgroundKeyColor.NormalizeHex(colorHex);
        }
        catch (ArgumentException)
        {
            return "#00000000";
        }
    }

    private static IImageProcessingPreviewCache CreateFallbackPreviewCache() =>
        new ImageProcessingPreviewCache(
            Path.Combine(
                Path.GetTempPath(),
                "GAssetForge",
                "Workspace",
                ImageProcessingPreviewCache.DefaultDirectoryName));

    private static double NormalizeFinite(
        double value,
        double fallback,
        double minimum,
        double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static double NormalizeCheckerOffset(double value, double cellSize)
    {
        if (!double.IsFinite(value))
        {
            return 0d;
        }

        var normalizedCellSize = NormalizeFinite(
            cellSize,
            16d,
            MinimumCheckerCellSize,
            MaximumCheckerCellSize);
        var remainder = value % normalizedCellSize;
        if (remainder == 0d)
        {
            return 0d;
        }

        return remainder < 0d ? remainder + normalizedCellSize : remainder;
    }

    private static (double Transparent, double Opaque) NormalizeInitialThresholds(
        double transparent,
        double opaque)
    {
        if (!double.IsFinite(transparent) || !double.IsFinite(opaque) ||
            transparent is < 0d or > 441.7d || opaque is <= 0d or > 441.7d || opaque <= transparent)
        {
            return (30d, 120d);
        }

        return (transparent, opaque);
    }

    private static string? NormalizeExistingDirectory(string? directory)
    {
        try
        {
            return Directory.Exists(directory) ? Path.GetFullPath(directory!) : null;
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            return null;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            return false;
        }
    }

    private static bool IsExpectedOperationException(Exception exception) => exception is
        ArgumentException or
        ImageFormatException or
        InvalidOperationException or
        IOException or
        UnauthorizedAccessException or
        NotSupportedException;

    private static string GetOperationErrorMessage(Exception exception) => exception is ImageFormatException
        ? $"图片文件已损坏或格式无法识别：{exception.Message}"
        : exception.Message;

    private readonly record struct CheckerDetectionSnapshot(
        bool Enabled,
        double MinimumCellSize,
        double MaximumCellSize,
        double SearchRangePercent);
}

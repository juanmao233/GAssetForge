using GAssetForge.App.Infrastructure;
using GAssetForge.App.Services;
using GAssetForge.Core.Imaging;
using SixLabors.ImageSharp;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;

namespace GAssetForge.App.ViewModels;

public sealed record PixelSamplingModeOption(
    PixelCellSamplingMode Mode,
    string DisplayName,
    string Description);

public sealed record PixelAlignmentToolSettingsSnapshot(
    int MinimumUpscaleFactor,
    int Columns,
    int Rows,
    string SamplingMode,
    bool ShowGrid,
    bool EnableAspectRatioCorrection,
    double TargetAspectRatioA,
    double TargetAspectRatioB,
    bool EnableRotationCorrection,
    double RotationDegrees,
    bool EnablePerspectiveCorrection,
    double TargetEdgeAngleDegrees,
    string? LastInputDirectory,
    string? LastOutputDirectory,
    bool EnableAutomaticColorIndex,
    int MaximumIndexedColors);

public enum PixelAlignmentBatchItemStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped
}

public sealed class PixelAlignmentBatchItemViewModel : ObservableObject
{
    private PixelAlignmentBatchItemStatus _status;
    private string _outputPath = string.Empty;
    private string _message = "等待处理";

    public PixelAlignmentBatchItemViewModel(PixelAlignmentImageInfo info)
    {
        InputPath = info.InputPath;
        RefreshSourceInfo(info);
    }

    public string InputPath { get; }

    public string FileName => Path.GetFileName(InputPath);

    public int SourceWidth { get; private set; }

    public int SourceHeight { get; private set; }

    public string FormatName { get; private set; } = string.Empty;

    public string SourceSizeText => $"{SourceWidth} × {SourceHeight} px";

    public bool IsProcessable => SourceWidth > 0 && SourceHeight > 0 && File.Exists(InputPath);

    public PixelAlignmentBatchItemStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string StatusText => Status switch
    {
        PixelAlignmentBatchItemStatus.Pending => "等待",
        PixelAlignmentBatchItemStatus.Running => "处理中",
        PixelAlignmentBatchItemStatus.Completed => "完成",
        PixelAlignmentBatchItemStatus.Failed => "失败",
        PixelAlignmentBatchItemStatus.Skipped => "跳过",
        _ => Status.ToString()
    };

    public string OutputPath
    {
        get => _outputPath;
        set
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

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public void RefreshSourceInfo(PixelAlignmentImageInfo info)
    {
        if (!string.Equals(
                Path.GetFullPath(info.InputPath),
                Path.GetFullPath(InputPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("批处理图片信息与队列路径不一致。", nameof(info));
        }

        var sizeChanged = SourceWidth != info.Width || SourceHeight != info.Height;
        SourceWidth = info.Width;
        SourceHeight = info.Height;
        FormatName = info.FormatName;
        if (sizeChanged)
        {
            OnPropertyChanged(nameof(SourceWidth));
            OnPropertyChanged(nameof(SourceHeight));
            OnPropertyChanged(nameof(SourceSizeText));
        }

        OnPropertyChanged(nameof(FormatName));
        OnPropertyChanged(nameof(IsProcessable));
    }
}

public sealed class PixelAlignmentSettingsChangedEventArgs(
    PixelAlignmentToolSettingsSnapshot settings) : EventArgs
{
    public PixelAlignmentToolSettingsSnapshot Settings { get; } = settings;
}

public sealed class PixelAlignmentViewModel : ObservableObject
{
    public const int DefaultMinimumUpscaleFactor = 10;
    public const int DefaultColumns = 32;
    public const int DefaultRows = 32;
    public const int MaximumGridDimension = PixelAlignmentLimits.MaximumGridAxis;
    public const int MaximumBatchItemCount = 256;

    private const string LogSource = "PixelAlignment";
    private const int ApproximatePreviewTarget = 500;

    private static readonly IReadOnlyList<PixelSamplingModeOption> SupportedSamplingModes =
    [
        new(
            PixelCellSamplingMode.CenterPixel,
            "中心像素（推荐）",
            "直接读取每格中心对应的源像素，速度最快、不会混色，优先用于像素点重新对齐。"),
        new(
            PixelCellSamplingMode.DominantColor,
            "主色采样",
            "按每格覆盖面积选择出现最多的完整 RGBA 颜色，适合清理局部噪点并保持调色板。"),
        new(
            PixelCellSamplingMode.AverageColor,
            "平均色采样",
            "按覆盖面积和 Alpha 加权平均，适合颜色连续但可能产生新颜色。")
    ];

    private static readonly HashSet<string> SupportedInputExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp"
    };

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly IPixelAlignmentService _service;
    private readonly IPixelAlignmentInputPicker _inputPicker;
    private readonly IPixelAlignmentOutputPicker _outputPicker;
    private readonly IImageProcessingPreviewCache _previewCache;
    private readonly IWorkbenchLogSink? _workbenchLogSink;
    private readonly RelayCommand _chooseInputCommand;
    private readonly RelayCommand _chooseOutputCommand;
    private readonly RelayCommand _chooseBatchInputsCommand;
    private readonly RelayCommand _chooseBatchOutputDirectoryCommand;
    private readonly RelayCommand _removeSelectedBatchItemCommand;
    private readonly RelayCommand _clearBatchItemsCommand;
    private readonly RelayCommand _resetCropCommand;
    private readonly RelayCommand _fitToApproximateSizeCommand;
    private readonly RelayCommand<int> _setGridPresetCommand;
    private readonly RelayCommand<string> _setAspectRatioPresetCommand;
    private readonly RelayCommand _syncGridToAspectRatioCommand;
    private readonly RelayCommand<double> _setRotationPresetCommand;
    private readonly RelayCommand _resetPerspectivePointsCommand;
    private readonly AsyncRelayCommand _refreshPreviewCommand;
    private readonly AsyncRelayCommand _saveResultCommand;
    private readonly AsyncRelayCommand _processCommand;
    private readonly AsyncRelayCommand _processBatchCommand;
    private readonly AsyncRelayCommand _exportWorkingImageCommand;
    private readonly RelayCommand _openOutputDirectoryCommand;
    private int _minimumUpscaleFactor;
    private int _columns;
    private int _rows;
    private PixelSamplingModeOption _selectedSamplingMode;
    private bool _enableAutomaticColorIndex = true;
    private int _maximumIndexedColors = PixelAlignmentLimits.MaximumIndexedColorCount;
    private bool _showGrid;
    private bool _isBatchMode;
    private PixelAlignmentBatchItemViewModel? _selectedBatchItem;
    private string _batchOutputDirectory = string.Empty;
    private int _batchProcessedCount;
    private int _batchCompletedCount;
    private int _batchFailedCount;
    private string _batchCurrentFileName = string.Empty;
    private string _inputPath = string.Empty;
    private string _outputPath = string.Empty;
    private string _outputPreviewPath = string.Empty;
    private string _singlePreviewPath = string.Empty;
    private string _lastSavedOutputPath = string.Empty;
    private bool _isPreviewCurrent;
    private bool _isPreviewStale;
    private string _previewStatusText = "尚未刷新预览。";
    private long _previewSettingsRevision;
    private string? _lastInputDirectory;
    private string? _lastOutputDirectory;
    private int _sourceWidth;
    private int _sourceHeight;
    private int _cropLeft;
    private int _cropTop;
    private int _cropRight;
    private int _cropBottom;
    private bool _enableAspectRatioCorrection;
    private double _targetAspectRatioA = 1d;
    private double _targetAspectRatioB = 1d;
    private bool _enableRotationCorrection;
    private double _rotationDegrees;
    private bool _enablePerspectiveCorrection;
    private double _perspectiveTopLeftX;
    private double _perspectiveTopLeftY;
    private double _perspectiveTopRightX;
    private double _perspectiveTopRightY;
    private double _perspectiveBottomRightX;
    private double _perspectiveBottomRightY;
    private double _perspectiveBottomLeftX;
    private double _perspectiveBottomLeftY;
    private double _targetEdgeAngleDegrees = 90d;
    private PixelAlignmentPlan? _currentPlan;
    private string _previewTitle = "对齐结果预览";
    private string _previewSizeText = "—";
    private string _statusText = "选择图片后，可拖动四条标尺线确定采样范围。";
    private string _resultSummary = "尚未生成像素对齐结果";
    private bool _isBusy;
    private bool _suppressSettingsChanged = true;
    private bool _suppressPlanRefresh;

    public PixelAlignmentViewModel(
        IPixelAlignmentService service,
        IPixelAlignmentInputPicker inputPicker,
        IPixelAlignmentOutputPicker outputPicker,
        int minimumUpscaleFactor = DefaultMinimumUpscaleFactor,
        int columns = DefaultColumns,
        int rows = DefaultRows,
        string? samplingMode = null,
        bool showGrid = true,
        bool enableAspectRatioCorrection = false,
        double targetAspectRatioA = 1d,
        double targetAspectRatioB = 1d,
        bool enableRotationCorrection = false,
        double rotationDegrees = 0d,
        bool enablePerspectiveCorrection = false,
        double targetEdgeAngleDegrees = 90d,
        bool enableAutomaticColorIndex = true,
        int maximumIndexedColors = PixelAlignmentLimits.MaximumIndexedColorCount,
        string? lastInputDirectory = null,
        string? lastOutputDirectory = null,
        IWorkbenchLogSink? workbenchLogSink = null,
        IImageProcessingPreviewCache? previewCache = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _inputPicker = inputPicker ?? throw new ArgumentNullException(nameof(inputPicker));
        _outputPicker = outputPicker ?? throw new ArgumentNullException(nameof(outputPicker));
        _workbenchLogSink = workbenchLogSink;
        _previewCache = previewCache ?? new ImageProcessingPreviewCache(
            Path.Combine(
                Path.GetTempPath(),
                "GAssetForge",
                "Workspace",
                ImageProcessingPreviewCache.DefaultDirectoryName));
        _minimumUpscaleFactor = Math.Clamp(minimumUpscaleFactor, 1, 1024);
        _columns = Math.Clamp(columns, 1, MaximumGridDimension);
        _rows = Math.Clamp(rows, 1, MaximumGridDimension);
        _selectedSamplingMode = ResolveSamplingMode(samplingMode);
        _showGrid = showGrid;
        _enableAspectRatioCorrection = enableAspectRatioCorrection;
        _targetAspectRatioA = NormalizeAspectComponent(targetAspectRatioA);
        _targetAspectRatioB = NormalizeAspectComponent(targetAspectRatioB);
        _enableRotationCorrection = enableRotationCorrection;
        _rotationDegrees = NormalizeRotation(rotationDegrees);
        _enablePerspectiveCorrection = enablePerspectiveCorrection;
        _targetEdgeAngleDegrees = NormalizeTargetEdgeAngle(targetEdgeAngleDegrees);
        _enableAutomaticColorIndex = enableAutomaticColorIndex;
        _maximumIndexedColors = Math.Clamp(
            maximumIndexedColors,
            PixelAlignmentLimits.MinimumIndexedColorCount,
            PixelAlignmentLimits.MaximumIndexedColorCount);
        _lastInputDirectory = NormalizeExistingDirectory(lastInputDirectory);
        _lastOutputDirectory = NormalizeExistingDirectory(lastOutputDirectory);

        _chooseInputCommand = new RelayCommand(ChooseInput, () => !IsBusy);
        _chooseOutputCommand = new RelayCommand(ChooseOutput, () => !IsBusy && HasInput);
        _chooseBatchInputsCommand = new RelayCommand(ChooseBatchInputs, () => !IsBusy);
        _chooseBatchOutputDirectoryCommand = new RelayCommand(ChooseBatchOutputDirectory, () => !IsBusy && BatchItems.Count > 0);
        _removeSelectedBatchItemCommand = new RelayCommand(RemoveSelectedBatchItem, () => !IsBusy && SelectedBatchItem is not null);
        _clearBatchItemsCommand = new RelayCommand(ClearBatchItems, () => !IsBusy && BatchItems.Count > 0);
        _resetCropCommand = new RelayCommand(ResetCrop, () => !IsBusy && HasInput);
        _fitToApproximateSizeCommand = new RelayCommand(FitToApproximateSize, () => !IsBusy && HasInput);
        _setGridPresetCommand = new RelayCommand<int>(SetGridPreset, value => !IsBusy && value is 16 or 32 or 64);
        _setAspectRatioPresetCommand = new RelayCommand<string>(SetAspectRatioPreset, value => !IsBusy && !string.IsNullOrWhiteSpace(value));
        _syncGridToAspectRatioCommand = new RelayCommand(SyncGridToAspectRatio, () => !IsBusy);
        _setRotationPresetCommand = new RelayCommand<double>(SetRotationPreset, _ => !IsBusy);
        _resetPerspectivePointsCommand = new RelayCommand(ResetPerspectivePoints, () => !IsBusy && HasInput);
        _refreshPreviewCommand = new AsyncRelayCommand(RefreshPreviewAsync, () => CanRefreshPreview);
        _saveResultCommand = new AsyncRelayCommand(SaveResultAsync, () => CanSaveResult);
        _processCommand = new AsyncRelayCommand(ProcessLegacyAsync, () => CanProcess);
        _processBatchCommand = new AsyncRelayCommand(ProcessBatchAsync, () => CanProcessBatch);
        _exportWorkingImageCommand = new AsyncRelayCommand(ExportWorkingImageAsync, () => CanExportWorkingImage);
        _openOutputDirectoryCommand = new RelayCommand(OpenOutputDirectory, () => HasSavedOutput);
        ChooseInputCommand = _chooseInputCommand;
        ChooseOutputCommand = _chooseOutputCommand;
        ChooseBatchInputsCommand = _chooseBatchInputsCommand;
        ChooseBatchOutputDirectoryCommand = _chooseBatchOutputDirectoryCommand;
        RemoveSelectedBatchItemCommand = _removeSelectedBatchItemCommand;
        ClearBatchItemsCommand = _clearBatchItemsCommand;
        ResetCropCommand = _resetCropCommand;
        FitToApproximateSizeCommand = _fitToApproximateSizeCommand;
        SetGridPresetCommand = _setGridPresetCommand;
        SetAspectRatioPresetCommand = _setAspectRatioPresetCommand;
        SyncGridToAspectRatioCommand = _syncGridToAspectRatioCommand;
        SetRotationPresetCommand = _setRotationPresetCommand;
        ResetPerspectivePointsCommand = _resetPerspectivePointsCommand;
        RefreshPreviewCommand = _refreshPreviewCommand;
        SaveResultCommand = _saveResultCommand;
        ProcessCommand = _processCommand;
        ProcessBatchCommand = _processBatchCommand;
        ExportWorkingImageCommand = _exportWorkingImageCommand;
        OpenOutputDirectoryCommand = _openOutputDirectoryCommand;
        _suppressSettingsChanged = false;
    }

    public event EventHandler<PixelAlignmentSettingsChangedEventArgs>? SettingsChanged;

    public IReadOnlyList<PixelSamplingModeOption> SamplingModeOptions => SupportedSamplingModes;

    public IReadOnlyList<int> GridPresets { get; } = [16, 32, 64];

    public int MinimumUpscaleFactor
    {
        get => _minimumUpscaleFactor;
        set
        {
            var normalized = Math.Clamp(value, 1, 1024);
            if (SetProperty(ref _minimumUpscaleFactor, normalized))
            {
                RefreshPlan();
                NotifySettingsChanged();
            }
        }
    }

    public int Columns
    {
        get => _columns;
        set
        {
            var normalized = Math.Clamp(value, 1, MaximumGridDimension);
            if (SetProperty(ref _columns, normalized))
            {
                OnPropertyChanged(nameof(OutputSizeText));
                RaiseAspectGridPropertiesChanged();
                RefreshPlan();
                NotifySettingsChanged();
            }
        }
    }

    public int Rows
    {
        get => _rows;
        set
        {
            var normalized = Math.Clamp(value, 1, MaximumGridDimension);
            if (SetProperty(ref _rows, normalized))
            {
                OnPropertyChanged(nameof(OutputSizeText));
                RaiseAspectGridPropertiesChanged();
                RefreshPlan();
                NotifySettingsChanged();
            }
        }
    }

    public PixelSamplingModeOption SelectedSamplingMode
    {
        get => _selectedSamplingMode;
        set
        {
            if (value is null || !SetProperty(ref _selectedSamplingMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SamplingModeDescription));
            RefreshPlan();
            NotifySettingsChanged();
        }
    }

    public string SamplingModeDescription => SelectedSamplingMode.Description;

    public bool EnableAutomaticColorIndex
    {
        get => _enableAutomaticColorIndex;
        set
        {
            if (SetProperty(ref _enableAutomaticColorIndex, value))
            {
                OnPropertyChanged(nameof(ColorIndexDescription));
                MarkPreviewStale();
                NotifySettingsChanged();
            }
        }
    }

    public int MaximumIndexedColors
    {
        get => _maximumIndexedColors;
        set
        {
            var normalized = Math.Clamp(
                value,
                PixelAlignmentLimits.MinimumIndexedColorCount,
                PixelAlignmentLimits.MaximumIndexedColorCount);
            if (SetProperty(ref _maximumIndexedColors, normalized))
            {
                OnPropertyChanged(nameof(ColorIndexDescription));
                MarkPreviewStale();
                NotifySettingsChanged();
            }
        }
    }

    public string ColorIndexDescription => EnableAutomaticColorIndex
        ? $"输出完成后自动建立每图独立颜色索引，最多保留 {MaximumIndexedColors} 种 RGBA 颜色。"
        : "关闭颜色索引；保留采样后的完整颜色。";

    public bool IsBatchMode
    {
        get => _isBatchMode;
        set
        {
            if (!SetProperty(ref _isBatchMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanProcessBatch));
            OnPropertyChanged(nameof(CanRefreshPreview));
            OnPropertyChanged(nameof(CanSaveResult));
            StatusText = value
                ? "批量模式：添加多张图片后，当前标尺和透视点会按基准图片尺寸等比映射。"
                : "单图模式：选择图片后，可拖动四条标尺线确定采样范围。";
            RefreshCommands();
        }
    }

    public ObservableCollection<PixelAlignmentBatchItemViewModel> BatchItems { get; } = [];

    public PixelAlignmentBatchItemViewModel? SelectedBatchItem
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

    public string BatchOutputDirectory
    {
        get => _batchOutputDirectory;
        private set
        {
            if (SetProperty(ref _batchOutputDirectory, value))
            {
                OnPropertyChanged(nameof(BatchOutputDirectoryDisplay));
                OnPropertyChanged(nameof(CanProcessBatch));
                RefreshCommands();
            }
        }
    }

    public string BatchOutputDirectoryDisplay => string.IsNullOrWhiteSpace(BatchOutputDirectory)
        ? "尚未设置输出目录"
        : BatchOutputDirectory;

    public int BatchProcessedCount
    {
        get => _batchProcessedCount;
        private set
        {
            if (SetProperty(ref _batchProcessedCount, value))
            {
                RaiseBatchProgressPropertiesChanged();
            }
        }
    }

    public int BatchCompletedCount
    {
        get => _batchCompletedCount;
        private set
        {
            if (SetProperty(ref _batchCompletedCount, value))
            {
                RaiseBatchProgressPropertiesChanged();
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
                RaiseBatchProgressPropertiesChanged();
            }
        }
    }

    public string BatchCurrentFileName
    {
        get => _batchCurrentFileName;
        private set
        {
            if (SetProperty(ref _batchCurrentFileName, value))
            {
                RaiseBatchProgressPropertiesChanged();
            }
        }
    }

    public int BatchTotalCount => BatchItems.Count;

    public double BatchProgressPercent => BatchTotalCount == 0
        ? 0d
        : BatchProcessedCount * 100d / BatchTotalCount;

    public string BatchProgressText => BatchTotalCount == 0
        ? "尚未添加批处理图片"
        : string.IsNullOrWhiteSpace(BatchCurrentFileName)
            ? $"{BatchProcessedCount}/{BatchTotalCount} · 完成 {BatchCompletedCount} · 失败 {BatchFailedCount}"
            : $"{BatchProcessedCount}/{BatchTotalCount} · 当前 {BatchCurrentFileName} · 完成 {BatchCompletedCount} · 失败 {BatchFailedCount}";

    public bool ShowGrid
    {
        get => _showGrid;
        set
        {
            if (SetProperty(ref _showGrid, value))
            {
                NotifySettingsChanged();
            }
        }
    }

    public bool EnableAspectRatioCorrection
    {
        get => _enableAspectRatioCorrection;
        set
        {
            if (SetProperty(ref _enableAspectRatioCorrection, value))
            {
                RaiseCorrectionPropertiesChanged();
                RefreshPlan();
                NotifySettingsChanged();
            }
        }
    }

    public double TargetAspectRatioA
    {
        get => _targetAspectRatioA;
        set
        {
            var normalized = NormalizeAspectComponent(value);
            if (SetProperty(ref _targetAspectRatioA, normalized))
            {
                RaiseAspectGridPropertiesChanged();
                RaiseCorrectionPropertiesChanged();
                RefreshPlan();
                NotifySettingsChanged();
            }
        }
    }

    public double TargetAspectRatioB
    {
        get => _targetAspectRatioB;
        set
        {
            var normalized = NormalizeAspectComponent(value);
            if (SetProperty(ref _targetAspectRatioB, normalized))
            {
                RaiseAspectGridPropertiesChanged();
                RaiseCorrectionPropertiesChanged();
                RefreshPlan();
                NotifySettingsChanged();
            }
        }
    }

    public bool EnableRotationCorrection
    {
        get => _enableRotationCorrection;
        set
        {
            if (SetProperty(ref _enableRotationCorrection, value))
            {
                RaiseCorrectionPropertiesChanged();
                RefreshPlan();
                NotifySettingsChanged();
            }
        }
    }

    public double RotationDegrees
    {
        get => _rotationDegrees;
        set
        {
            var normalized = NormalizeRotation(value);
            if (SetProperty(ref _rotationDegrees, normalized))
            {
                RaiseCorrectionPropertiesChanged();
                RefreshPlan();
                NotifySettingsChanged();
            }
        }
    }

    public bool EnablePerspectiveCorrection
    {
        get => _enablePerspectiveCorrection;
        set
        {
            var resetPoints = value && HasInput && !ArePerspectivePointsInsideCrop();
            if (resetPoints)
            {
                SetPerspectivePointsToCropBoundsWithoutRefreshing();
            }

            if (SetProperty(ref _enablePerspectiveCorrection, value))
            {
                RaiseCorrectionPropertiesChanged();
                RefreshPlan();
                NotifySettingsChanged();
                if (resetPoints)
                {
                    StatusText = "透视矫正已启用；原四点超出当前裁切范围，已自动重置到裁切角点。";
                }
            }
        }
    }

    public double PerspectiveTopLeftX
    {
        get => _perspectiveTopLeftX;
        set => SetPerspectiveCoordinate(ref _perspectiveTopLeftX, value, SourceWidth, nameof(PerspectiveTopLeftX));
    }

    public double PerspectiveTopLeftY
    {
        get => _perspectiveTopLeftY;
        set => SetPerspectiveCoordinate(ref _perspectiveTopLeftY, value, SourceHeight, nameof(PerspectiveTopLeftY));
    }

    public double PerspectiveTopRightX
    {
        get => _perspectiveTopRightX;
        set => SetPerspectiveCoordinate(ref _perspectiveTopRightX, value, SourceWidth, nameof(PerspectiveTopRightX));
    }

    public double PerspectiveTopRightY
    {
        get => _perspectiveTopRightY;
        set => SetPerspectiveCoordinate(ref _perspectiveTopRightY, value, SourceHeight, nameof(PerspectiveTopRightY));
    }

    public double PerspectiveBottomRightX
    {
        get => _perspectiveBottomRightX;
        set => SetPerspectiveCoordinate(ref _perspectiveBottomRightX, value, SourceWidth, nameof(PerspectiveBottomRightX));
    }

    public double PerspectiveBottomRightY
    {
        get => _perspectiveBottomRightY;
        set => SetPerspectiveCoordinate(ref _perspectiveBottomRightY, value, SourceHeight, nameof(PerspectiveBottomRightY));
    }

    public double PerspectiveBottomLeftX
    {
        get => _perspectiveBottomLeftX;
        set => SetPerspectiveCoordinate(ref _perspectiveBottomLeftX, value, SourceWidth, nameof(PerspectiveBottomLeftX));
    }

    public double PerspectiveBottomLeftY
    {
        get => _perspectiveBottomLeftY;
        set => SetPerspectiveCoordinate(ref _perspectiveBottomLeftY, value, SourceHeight, nameof(PerspectiveBottomLeftY));
    }

    public double TargetEdgeAngleDegrees
    {
        get => _targetEdgeAngleDegrees;
        set
        {
            var normalized = NormalizeTargetEdgeAngle(value);
            if (SetProperty(ref _targetEdgeAngleDegrees, normalized))
            {
                RaiseCorrectionPropertiesChanged();
                RefreshPlan();
                NotifySettingsChanged();
            }
        }
    }

    public bool IsAnyCorrectionEnabled =>
        EnableAspectRatioCorrection || EnableRotationCorrection || EnablePerspectiveCorrection;

    public bool IsAspectGridMismatch
    {
        get
        {
            if (!EnableAspectRatioCorrection || Columns <= 0 || Rows <= 0)
            {
                return false;
            }

            var targetRatio = TargetAspectRatioA / TargetAspectRatioB;
            var gridRatio = Columns / (double)Rows;
            return Math.Abs(gridRatio - targetRatio) / targetRatio > 0.005d;
        }
    }

    public string AspectGridStatusText
    {
        get
        {
            if (!EnableAspectRatioCorrection)
            {
                return "启用后，源画面会按目标 A:B 比例重整；最终 PNG 仍严格使用自定义行列数。";
            }

            var target = $"{FormatNumber(TargetAspectRatioA)}:{FormatNumber(TargetAspectRatioB)}";
            return IsAspectGridMismatch
                ? $"⚠ 当前网格 {Columns}:{Rows} 与目标 {target} 不一致；可显式同步，系统不会自动覆盖自定义行列。"
                : $"网格 {Columns}:{Rows} 与目标比例 {target} 一致。";
        }
    }

    public string CorrectionStepsText
    {
        get
        {
            var steps = new List<string>(3);
            if (EnablePerspectiveCorrection)
            {
                steps.Add($"四点透视 → A/B {TargetEdgeAngleDegrees:0.##}°");
            }

            if (EnableAspectRatioCorrection)
            {
                steps.Add($"比例 {FormatNumber(TargetAspectRatioA)}:{FormatNumber(TargetAspectRatioB)}");
            }

            if (EnableRotationCorrection)
            {
                steps.Add($"顺时针旋转 {RotationDegrees:0.##}°");
            }

            return steps.Count == 0 ? "无矫正：直接裁切并进行网格采样" : string.Join(" · ", steps);
        }
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
            RefreshCommands();
        }
    }

    public string InputFileName => HasInput ? Path.GetFileName(InputPath) : "尚未选择输入图片";

    public bool HasInput => SourceWidth > 0 && SourceHeight > 0 && File.Exists(InputPath);

    public string OutputPath
    {
        get => _outputPath;
        private set
        {
            if (SetProperty(ref _outputPath, value))
            {
                OnPropertyChanged(nameof(OutputFileName));
                RefreshCommands();
            }
        }
    }

    public string OutputFileName => string.IsNullOrWhiteSpace(OutputPath)
        ? "尚未设置对齐结果 PNG"
        : Path.GetFileName(OutputPath);

    public string OutputPreviewPath
    {
        get => _outputPreviewPath;
        private set
        {
            if (SetProperty(ref _outputPreviewPath, value))
            {
                OnPropertyChanged(nameof(HasOutputPreview));
                RefreshCommands();
            }
        }
    }

    public bool HasOutputPreview => File.Exists(OutputPreviewPath);

    public bool IsPreviewCurrent
    {
        get => _isPreviewCurrent;
        private set
        {
            if (SetProperty(ref _isPreviewCurrent, value))
            {
                OnPropertyChanged(nameof(CanSaveResult));
                RefreshCommands();
            }
        }
    }

    public bool IsPreviewStale
    {
        get => _isPreviewStale;
        private set => SetProperty(ref _isPreviewStale, value);
    }

    public string PreviewStatusText
    {
        get => _previewStatusText;
        private set => SetProperty(ref _previewStatusText, value);
    }

    public string LastSavedOutputPath
    {
        get => _lastSavedOutputPath;
        private set
        {
            if (SetProperty(ref _lastSavedOutputPath, value))
            {
                OnPropertyChanged(nameof(HasSavedOutput));
                RefreshCommands();
            }
        }
    }

    public bool HasSavedOutput => File.Exists(LastSavedOutputPath);

    public string PreviewTitle
    {
        get => _previewTitle;
        private set => SetProperty(ref _previewTitle, value);
    }

    public string PreviewSizeText
    {
        get => _previewSizeText;
        private set => SetProperty(ref _previewSizeText, value);
    }

    public int SourceWidth
    {
        get => _sourceWidth;
        private set
        {
            if (SetProperty(ref _sourceWidth, value))
            {
                OnPropertyChanged(nameof(SourceSizeText));
                OnPropertyChanged(nameof(HasInput));
                OnPropertyChanged(nameof(InputFileName));
                RefreshCommands();
            }
        }
    }

    public int SourceHeight
    {
        get => _sourceHeight;
        private set
        {
            if (SetProperty(ref _sourceHeight, value))
            {
                OnPropertyChanged(nameof(SourceSizeText));
                OnPropertyChanged(nameof(HasInput));
                OnPropertyChanged(nameof(InputFileName));
                RefreshCommands();
            }
        }
    }

    public int CropLeft
    {
        get => _cropLeft;
        set
        {
            var followCrop = PerspectivePointsMatchCropBounds();
            var maximum = Math.Max(0, CropRight - 1);
            if (SetProperty(ref _cropLeft, Math.Clamp(value, 0, maximum)))
            {
                FollowPerspectivePointsToCropBounds(followCrop);
                RefreshCropAndPlan();
            }
        }
    }

    public int CropTop
    {
        get => _cropTop;
        set
        {
            var followCrop = PerspectivePointsMatchCropBounds();
            var maximum = Math.Max(0, CropBottom - 1);
            if (SetProperty(ref _cropTop, Math.Clamp(value, 0, maximum)))
            {
                FollowPerspectivePointsToCropBounds(followCrop);
                RefreshCropAndPlan();
            }
        }
    }

    public int CropRight
    {
        get => _cropRight;
        set
        {
            var followCrop = PerspectivePointsMatchCropBounds();
            var normalized = SourceWidth <= 0
                ? 0
                : Math.Clamp(value, Math.Min(SourceWidth, CropLeft + 1), SourceWidth);
            if (SetProperty(ref _cropRight, normalized))
            {
                FollowPerspectivePointsToCropBounds(followCrop);
                RefreshCropAndPlan();
            }
        }
    }

    public int CropBottom
    {
        get => _cropBottom;
        set
        {
            var followCrop = PerspectivePointsMatchCropBounds();
            var normalized = SourceHeight <= 0
                ? 0
                : Math.Clamp(value, Math.Min(SourceHeight, CropTop + 1), SourceHeight);
            if (SetProperty(ref _cropBottom, normalized))
            {
                FollowPerspectivePointsToCropBounds(followCrop);
                RefreshCropAndPlan();
            }
        }
    }

    public int CropWidth => Math.Max(0, CropRight - CropLeft);

    public int CropHeight => Math.Max(0, CropBottom - CropTop);

    public string SourceSizeText => SourceWidth > 0 ? $"{SourceWidth} × {SourceHeight} px" : "—";

    public string CropSizeText => HasInput
        ? $"{CropWidth} × {CropHeight} px · X {CropLeft}–{CropRight} / Y {CropTop}–{CropBottom}"
        : "—";

    public string OutputSizeText => $"{Columns} × {Rows} px";

    public PixelAlignmentPlan? CurrentPlan
    {
        get => _currentPlan;
        private set
        {
            if (SetProperty(ref _currentPlan, value))
            {
                RaisePlanPropertiesChanged();
            }
        }
    }

    public string RequestedWorkingSizeText => CurrentPlan is null
        ? "—"
        : $"至少 {(long)CurrentPlan.CorrectedBaseWidth * MinimumUpscaleFactor:N0} × " +
          $"{(long)CurrentPlan.CorrectedBaseHeight * MinimumUpscaleFactor:N0} px";

    public string CorrectedBaseSizeText => CurrentPlan is null
        ? "—"
        : $"{CurrentPlan.CorrectedBaseWidth:N0} × {CurrentPlan.CorrectedBaseHeight:N0} px";

    public string CorrectedWorkingSizeText => CurrentPlan is null
        ? "—"
        : $"{CurrentPlan.CorrectedWorkingWidth:N0} × {CurrentPlan.CorrectedWorkingHeight:N0} px";

    public string WorkingSizeText => CurrentPlan is null
        ? "—"
        : $"{CurrentPlan.WorkingWidth:N0} × {CurrentPlan.WorkingHeight:N0} px";

    public string WorkingScaleText => CurrentPlan is null
        ? "—"
        : CurrentPlan.WorkingScale == MinimumUpscaleFactor
            ? $"{CurrentPlan.WorkingScale}×（已兼容网格）"
            : $"{MinimumUpscaleFactor}× → {CurrentPlan.WorkingScale}×（自动提升）";

    public string CellSizeText => CurrentPlan is null
        ? "—"
        : $"{CurrentPlan.CellWidth:N0} × {CurrentPlan.CellHeight:N0} 工作像素/格";

    public string PlanExplanation => CurrentPlan is null
        ? "选择图片后计算整数倍与网格的最小兼容尺寸。"
        : (CurrentPlan.WorkingScale == MinimumUpscaleFactor
            ? $"当前 {MinimumUpscaleFactor}× 放大已经能被 {Columns}×{Rows} 网格完整切分，不需要插值。"
            : $"期望至少 {MinimumUpscaleFactor}×；为兼容 {Columns}×{Rows} 网格，统一整数倍率自动提升到 " +
              $"{CurrentPlan.WorkingScale}×。中间工作格为虚拟采样坐标，不会额外写入大型临时图。") +
          (CurrentPlan.IsCorrectionEnabled
              ? $" 矫正顺序：{CorrectionStepsText}。"
              : string.Empty) +
          (CanMaterializeWorkingImage(CurrentPlan)
              ? string.Empty
              : " 精确兼容工作坐标仅参与虚拟采样；尺寸超过安全画幅，因此不会创建巨型中间图，但最终自定义网格仍可生成。");

    public bool IsGeometryFitPaddingRequired
    {
        get
        {
            if (CurrentPlan is not { IsCorrectionEnabled: true })
            {
                return false;
            }

            var correctedRatio = CurrentPlan.CorrectedBaseWidth / (double)CurrentPlan.CorrectedBaseHeight;
            var outputRatio = Columns / (double)Rows;
            return Math.Abs(outputRatio - correctedRatio) / correctedRatio > 0.005d;
        }
    }

    public string GeometryFitStatusText => CurrentPlan switch
    {
        null => "选择图片并启用矫正后，将显示最终画布适配方式。",
        { IsCorrectionEnabled: false } => "未启用矫正：按自定义网格直接采样。",
        _ when IsGeometryFitPaddingRequired =>
            "最终网格与矫正画布比例不同：内容会等比居中并使用透明补边，旋转角和 A/B 夹角不会被非等比拉伸改变。",
        _ => "最终网格与矫正画布比例一致：矫正内容会填满输出。"
    };

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

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanProcess));
                OnPropertyChanged(nameof(CanRefreshPreview));
                OnPropertyChanged(nameof(CanSaveResult));
                OnPropertyChanged(nameof(CanProcessBatch));
                OnPropertyChanged(nameof(CanExportWorkingImage));
                RefreshCommands();
            }
        }
    }

    public bool CanProcess => !IsBusy && HasInput && CurrentPlan is not null &&
                               !string.IsNullOrWhiteSpace(OutputPath) &&
                               !PathsEqual(InputPath, OutputPath);

    public bool CanRefreshPreview => !IsBusy && !IsBatchMode && HasInput && CurrentPlan is not null;

    public bool CanSaveResult =>
        !IsBusy && !IsBatchMode && IsPreviewCurrent && !IsPreviewStale &&
        !string.IsNullOrWhiteSpace(_singlePreviewPath) &&
        File.Exists(_singlePreviewPath) &&
        _previewCache.IsManagedPreviewPath(_singlePreviewPath) &&
        (string.IsNullOrWhiteSpace(OutputPath) || !PathsEqual(InputPath, OutputPath));

    public bool CanExportWorkingImage =>
        !IsBusy && HasInput && CurrentPlan is not null && CanMaterializeWorkingImage(CurrentPlan);

    public bool CanProcessBatch =>
        !IsBusy && IsBatchMode && SourceWidth > 0 && SourceHeight > 0 && CurrentPlan is not null && BatchItems.Count > 0 &&
        !string.IsNullOrWhiteSpace(BatchOutputDirectory) && Directory.Exists(BatchOutputDirectory);

    public ICommand ChooseInputCommand { get; }

    public ICommand ChooseOutputCommand { get; }

    public ICommand ChooseBatchInputsCommand { get; }

    public ICommand ChooseBatchOutputDirectoryCommand { get; }

    public ICommand RemoveSelectedBatchItemCommand { get; }

    public ICommand ClearBatchItemsCommand { get; }

    public ICommand ResetCropCommand { get; }

    public ICommand FitToApproximateSizeCommand { get; }

    public ICommand SetGridPresetCommand { get; }

    public ICommand SetAspectRatioPresetCommand { get; }

    public ICommand SyncGridToAspectRatioCommand { get; }

    public ICommand SetRotationPresetCommand { get; }

    public ICommand ResetPerspectivePointsCommand { get; }

    public ICommand RefreshPreviewCommand { get; }

    public ICommand SaveResultCommand { get; }

    public ICommand ProcessCommand { get; }

    public ICommand ProcessBatchCommand { get; }

    public ICommand ExportWorkingImageCommand { get; }

    public ICommand OpenOutputDirectoryCommand { get; }

    public PixelAlignmentToolSettingsSnapshot CaptureSettings() => new(
        MinimumUpscaleFactor,
        Columns,
        Rows,
        SelectedSamplingMode.Mode.ToString(),
        ShowGrid,
        EnableAspectRatioCorrection,
        TargetAspectRatioA,
        TargetAspectRatioB,
        EnableRotationCorrection,
        RotationDegrees,
        EnablePerspectiveCorrection,
        TargetEdgeAngleDegrees,
        _lastInputDirectory,
        _lastOutputDirectory,
        EnableAutomaticColorIndex,
        MaximumIndexedColors);

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

            var info = _service.Inspect(normalized);
            var clearedBatchQueue = ClearBatchQueueForSingleInput();
            LoadInputInfo(info);
            ResultSummary = clearedBatchQueue
                ? "已清除旧批处理队列；单张输入图片已就绪。"
                : "输入图片已就绪；拖动标尺并设置输出位置。";
            StatusText = $"已读取 {Path.GetFileName(info.InputPath)} · {info.Width}×{info.Height} px · {info.FormatName}";
            NotifySettingsChanged();
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            ResetInputState();
            StatusText = $"无法读取输入图片：{exception.Message}";
            _workbenchLogSink?.Write(WorkbenchLogLevel.Error, LogSource, StatusText, exception: exception);
        }
    }

    private void ChooseBatchInputs()
    {
        IReadOnlyList<string> selected;
        try
        {
            selected = _inputPicker.SelectInputFiles(_lastInputDirectory);
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"无法选择批处理图片：{exception.Message}";
            return;
        }

        if (selected.Count == 0)
        {
            return;
        }

        var existingPaths = new HashSet<string>(
            BatchItems.Select(item => Path.GetFullPath(item.InputPath)),
            PathComparer);
        var wasEmpty = BatchItems.Count == 0;
        PixelAlignmentBatchItemViewModel? firstAdded = null;
        var rejectedCount = 0;
        foreach (var candidate in selected)
        {
            if (BatchItems.Count >= MaximumBatchItemCount)
            {
                break;
            }

            try
            {
                var normalized = Path.GetFullPath(candidate);
                if (!File.Exists(normalized) ||
                    !SupportedInputExtensions.Contains(Path.GetExtension(normalized)) ||
                    !existingPaths.Add(normalized))
                {
                    rejectedCount++;
                    continue;
                }

                var item = new PixelAlignmentBatchItemViewModel(_service.Inspect(normalized));
                BatchItems.Add(item);
                firstAdded ??= item;
            }
            catch (Exception exception) when (IsExpectedOperationException(exception))
            {
                rejectedCount++;
                _workbenchLogSink?.Write(
                    WorkbenchLogLevel.Warning,
                    LogSource,
                    $"批处理输入已跳过：{candidate} · {exception.Message}",
                    exception: exception);
            }
        }

        if (firstAdded is null)
        {
            StatusText = rejectedCount > 0
                ? "没有新增可处理图片；重复、无效或超出批次数量上限的文件已跳过。"
                : "没有新增可处理图片。";
            return;
        }

        if (wasEmpty)
        {
            LoadInputInfo(new PixelAlignmentImageInfo
            {
                InputPath = firstAdded.InputPath,
                Width = firstAdded.SourceWidth,
                Height = firstAdded.SourceHeight,
                FormatName = firstAdded.FormatName
            });
            SelectedBatchItem = firstAdded;
        }

        if (string.IsNullOrWhiteSpace(BatchOutputDirectory))
        {
            BatchOutputDirectory = NormalizeExistingDirectory(_lastOutputDirectory) ??
                                   Path.GetDirectoryName(firstAdded.InputPath) ?? string.Empty;
        }

        ResetBatchProgress(resetItems: true);
        RaiseBatchCollectionPropertiesChanged();
        ResultSummary = $"批处理已加入 {BatchItems.Count} 张图片；第一张作为标尺与透视映射基准。";
        StatusText = rejectedCount == 0
            ? $"批处理队列已就绪：{BatchItems.Count} 张图片。"
            : $"批处理队列已就绪：{BatchItems.Count} 张图片，另有 {rejectedCount} 项被跳过。";
        NotifySettingsChanged();
    }

    private void ChooseBatchOutputDirectory()
    {
        try
        {
            var initialDirectory = NormalizeExistingDirectory(BatchOutputDirectory) ??
                                   _lastOutputDirectory ??
                                   (HasInput ? Path.GetDirectoryName(InputPath) : null);
            var selected = _outputPicker.SelectOutputDirectory(initialDirectory);
            if (string.IsNullOrWhiteSpace(selected))
            {
                return;
            }

            var normalized = Path.GetFullPath(selected);
            if (!Directory.Exists(normalized))
            {
                StatusText = "所选批处理输出目录不存在。";
                return;
            }

            BatchOutputDirectory = normalized;
            _lastOutputDirectory = normalized;
            StatusText = $"批处理结果将保存到：{normalized}";
            NotifySettingsChanged();
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"无法设置批处理输出目录：{exception.Message}";
        }
    }

    private void RemoveSelectedBatchItem()
    {
        if (SelectedBatchItem is not { } selected || !BatchItems.Remove(selected))
        {
            return;
        }

        SelectedBatchItem = BatchItems.FirstOrDefault();
        if (PathsEqual(InputPath, selected.InputPath))
        {
            if (BatchItems.FirstOrDefault() is { } replacement)
            {
                LoadInputInfo(new PixelAlignmentImageInfo
                {
                    InputPath = replacement.InputPath,
                    Width = replacement.SourceWidth,
                    Height = replacement.SourceHeight,
                    FormatName = replacement.FormatName
                });
            }
            else
            {
                ResetInputState();
            }
        }

        ResetBatchProgress(resetItems: true);
        RaiseBatchCollectionPropertiesChanged();
        StatusText = BatchItems.Count == 0
            ? "批处理队列已清空。"
            : $"已移除图片；当前批处理队列共 {BatchItems.Count} 项。";
    }

    private void ClearBatchItems()
    {
        BatchItems.Clear();
        SelectedBatchItem = null;
        ResetBatchProgress(resetItems: false);
        ResetInputState();
        RaiseBatchCollectionPropertiesChanged();
        StatusText = "批处理队列已清空。";
    }

    private bool ClearBatchQueueForSingleInput()
    {
        if (BatchItems.Count == 0)
        {
            return false;
        }

        BatchItems.Clear();
        SelectedBatchItem = null;
        ResetBatchProgress(resetItems: false);
        RaiseBatchCollectionPropertiesChanged();
        return true;
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
            var initialDirectory = _lastOutputDirectory ?? Path.GetDirectoryName(InputPath);
            var suggestedName = $"{Path.GetFileNameWithoutExtension(InputPath)}_{Columns}x{Rows}_aligned.png";
            var selected = _outputPicker.SelectOutputFile(
                initialDirectory,
                suggestedName,
                "保存像素对齐 PNG");
            if (string.IsNullOrWhiteSpace(selected))
            {
                return;
            }

            OutputPath = NormalizePngPath(selected);
            _lastOutputDirectory = Path.GetDirectoryName(OutputPath);
            StatusText = IsPreviewCurrent
                ? $"输出位置已设置；当前预览可保存到：{OutputPath}"
                : $"对齐结果将保存到：{OutputPath}";
            NotifySettingsChanged();
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"无法设置输出位置：{exception.Message}";
        }
    }

    private async Task RefreshPreviewAsync()
    {
        if (!CanRefreshPreview || CurrentPlan is null)
        {
            StatusText = "当前输入、标尺或网格尚未就绪，无法刷新预览。";
            return;
        }

        var inputPath = InputPath;
        var settings = BuildSettings();
        var previousPreviewPath = _singlePreviewPath;
        var candidatePreviewPath = string.Empty;
        IsBusy = true;
        MarkPreviewStale();
        var settingsRevision = _previewSettingsRevision;
        PreviewStatusText = "正在使用正式像素对齐算法刷新缓存预览…";
        StatusText = $"正在按 {Columns}×{Rows} 网格刷新{SelectedSamplingMode.DisplayName}预览…";
        _workbenchLogSink?.Write(
            WorkbenchLogLevel.Information,
            LogSource,
            $"开始刷新像素对齐缓存预览：{Path.GetFileName(inputPath)} · crop {CropWidth}×{CropHeight} · " +
            $"grid {Columns}×{Rows} · minimum {MinimumUpscaleFactor}×");
        try
        {
            candidatePreviewPath = _previewCache.ReservePreviewPath("pixel-alignment");
            _previewCache.PinPreview(candidatePreviewPath);
            var result = await Task.Run(() =>
                    _service.AlignToPng(inputPath, candidatePreviewPath, settings, overwrite: false))
                .ConfigureAwait(true);
            var renderedPath = Path.GetFullPath(result.OutputPath);
            if (!_previewCache.IsManagedPreviewPath(renderedPath) || !File.Exists(renderedPath))
            {
                throw new InvalidOperationException("像素对齐服务未返回有效的受管预览缓存 PNG。");
            }

            if (!PathsEqual(candidatePreviewPath, renderedPath))
            {
                _previewCache.PinPreview(renderedPath);
                TryReleasePreview(candidatePreviewPath);
                candidatePreviewPath = renderedPath;
            }

            if (!PathsEqual(inputPath, InputPath))
            {
                TryReleasePreview(renderedPath);
                PreviewStatusText = "刷新期间输入图片发生变化；已丢弃旧输入的预览结果。";
                StatusText = "像素对齐预览已丢弃，请针对当前输入重新刷新。";
                return;
            }

            var remainsCurrent = settingsRevision == _previewSettingsRevision;
            if (remainsCurrent)
            {
                CurrentPlan = result.Plan;
            }

            _singlePreviewPath = renderedPath;
            OutputPreviewPath = renderedPath;
            PreviewTitle = "对齐结果预览";
            PreviewSizeText = $"{result.OutputWidth} × {result.OutputHeight} px";
            ResultSummary =
                $"输出 {result.OutputWidth} × {result.OutputHeight} px · 工作尺寸 " +
                $"{result.Plan.WorkingWidth:N0} × {result.Plan.WorkingHeight:N0} · " +
                $"{SelectedSamplingMode.DisplayName} · {FormatColorIndexResult(result)}";
            IsPreviewStale = !remainsCurrent;
            IsPreviewCurrent = remainsCurrent;
            PreviewStatusText = remainsCurrent
                ? "预览已按当前参数刷新，可确认保存；参数变化后需再次刷新。"
                : "刷新期间参数发生变化；结果仅供对比，请再次刷新。";
            StatusText = remainsCurrent
                ? "像素对齐预览已刷新，尚未写入最终输出位置。"
                : "像素对齐预览已生成，但参数已变化，需要再次刷新。";
            if (!string.IsNullOrWhiteSpace(previousPreviewPath) &&
                !PathsEqual(previousPreviewPath, renderedPath))
            {
                TryReleasePreview(previousPreviewPath);
            }

            _workbenchLogSink?.Write(
                WorkbenchLogLevel.Success,
                LogSource,
                $"像素对齐缓存预览已刷新：{renderedPath} · {ResultSummary}");
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            if (!string.IsNullOrWhiteSpace(candidatePreviewPath) &&
                !PathsEqual(candidatePreviewPath, previousPreviewPath))
            {
                TryReleasePreview(candidatePreviewPath);
            }

            _singlePreviewPath = previousPreviewPath;
            if (!string.IsNullOrWhiteSpace(previousPreviewPath) && File.Exists(previousPreviewPath))
            {
                OutputPreviewPath = previousPreviewPath;
                IsPreviewStale = true;
                PreviewStatusText = $"刷新失败，已保留上一次预览用于对比；该预览不可保存。{exception.Message}";
                ResultSummary = "刷新失败；上一次缓存预览已保留，但必须重新刷新后才能保存。";
            }
            else
            {
                _singlePreviewPath = string.Empty;
                OutputPreviewPath = string.Empty;
                IsPreviewStale = false;
                PreviewStatusText = $"预览刷新失败：{exception.Message}";
                ResultSummary = "预览刷新失败，输入图片保持不变。";
            }

            IsPreviewCurrent = false;
            StatusText = $"像素对齐预览刷新失败：{exception.Message}";
            _workbenchLogSink?.Write(WorkbenchLogLevel.Error, LogSource, StatusText, exception: exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveResultAsync()
    {
        if (!CanSaveResult)
        {
            StatusText = IsPreviewStale
                ? "当前预览已过期，请先刷新预览再保存。"
                : "请先刷新出与当前参数一致的预览。";
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

        if (PathsEqual(InputPath, OutputPath))
        {
            StatusText = "最终输出路径不能覆盖输入图片，请重新设置输出位置。";
            return;
        }

        var previewPath = _singlePreviewPath;
        var outputPath = OutputPath;
        IsBusy = true;
        StatusText = $"正在保存像素对齐结果：{outputPath}";
        try
        {
            var publishedPath = await Task.Run(() => _previewCache.PublishPreview(
                    previewPath,
                    outputPath,
                    overwrite: true))
                .ConfigureAwait(true);
            LastSavedOutputPath = publishedPath;
            _lastOutputDirectory = Path.GetDirectoryName(publishedPath);
            PreviewStatusText = $"当前预览已原样保存：{Path.GetFileName(publishedPath)}";
            StatusText = $"像素对齐结果已保存：{publishedPath}";
            NotifySettingsChanged();
            _workbenchLogSink?.Write(
                WorkbenchLogLevel.Success,
                LogSource,
                $"缓存预览已按原始 PNG 字节发布：{publishedPath}");
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"像素对齐结果保存失败：{exception.Message}";
            _workbenchLogSink?.Write(WorkbenchLogLevel.Error, LogSource, StatusText, exception: exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ProcessLegacyAsync()
    {
        if (!CanProcess)
        {
            StatusText = "当前输入、标尺、网格或输出位置尚未就绪。";
            return;
        }

        if (!IsPreviewCurrent || IsPreviewStale)
        {
            await RefreshPreviewAsync().ConfigureAwait(true);
        }

        if (CanSaveResult)
        {
            await SaveResultAsync().ConfigureAwait(true);
        }
    }

    private async Task ProcessBatchAsync()
    {
        if (!CanProcessBatch || CurrentPlan is null)
        {
            StatusText = "批处理图片、基准标尺或输出目录尚未就绪。";
            return;
        }

        var referenceWidth = SourceWidth;
        var referenceHeight = SourceHeight;
        var referenceSettings = BuildSettings();
        var outputDirectory = Path.GetFullPath(BatchOutputDirectory);
        var reservedOutputPaths = new HashSet<string>(PathComparer);
        PixelAlignmentResult? lastSuccessfulResult = null;

        ResetBatchProgress(resetItems: true);
        ClearSinglePreview(clearVisiblePreview: true);
        IsBusy = true;
        PreviewTitle = "批处理结果预览";
        PreviewSizeText = "—";
        PreviewStatusText = "批量模式会继续按原有逻辑逐项直接保存；此处显示最后一个成功结果。";
        ResultSummary = $"正在顺序处理 {BatchItems.Count} 张图片；单项失败不会终止批次。";
        _workbenchLogSink?.Write(
            WorkbenchLogLevel.Information,
            LogSource,
            $"开始像素对齐批处理：{BatchItems.Count} 项 · grid {Columns}×{Rows} · output {outputDirectory}");

        try
        {
            foreach (var item in BatchItems)
            {
                BatchCurrentFileName = item.FileName;
                item.Status = PixelAlignmentBatchItemStatus.Running;
                item.Message = "正在按基准图片的相对标尺处理";
                StatusText = $"批处理中 {BatchProcessedCount + 1}/{BatchTotalCount}：{item.FileName}";

                try
                {
                    var currentInfo = _service.Inspect(item.InputPath);
                    var sourceSizeChanged = currentInfo.Width != item.SourceWidth ||
                                            currentInfo.Height != item.SourceHeight;
                    item.RefreshSourceInfo(currentInfo);
                    var mappedSettings = MapSettingsToBatchItem(
                        referenceSettings,
                        referenceWidth,
                        referenceHeight,
                        currentInfo.Width,
                        currentInfo.Height);
                    var outputPath = ResolveUniqueBatchOutputPath(
                        outputDirectory,
                        item.InputPath,
                        mappedSettings.Columns,
                        mappedSettings.Rows,
                        reservedOutputPaths);
                    item.OutputPath = outputPath;
                    var result = await Task.Run(() =>
                            _service.AlignToPng(
                                item.InputPath,
                                outputPath,
                                mappedSettings,
                                overwrite: false))
                        .ConfigureAwait(true);

                    lastSuccessfulResult = result;
                    item.OutputPath = result.OutputPath;
                    item.Status = PixelAlignmentBatchItemStatus.Completed;
                    item.Message = sourceSizeChanged
                        ? $"实际尺寸已更新为 {currentInfo.Width}×{currentInfo.Height} · {FormatColorIndexResult(result)}"
                        : FormatColorIndexResult(result);
                    BatchCompletedCount++;
                    _workbenchLogSink?.Write(
                        WorkbenchLogLevel.Success,
                        LogSource,
                        $"批处理完成：{item.FileName} → {result.OutputPath} · {item.Message}");
                }
                catch (Exception exception) when (IsExpectedOperationException(exception))
                {
                    item.Status = PixelAlignmentBatchItemStatus.Failed;
                    item.Message = exception.Message;
                    BatchFailedCount++;
                    _workbenchLogSink?.Write(
                        WorkbenchLogLevel.Error,
                        LogSource,
                        $"批处理单项失败：{item.FileName} · {exception.Message}",
                        exception: exception);
                }
                finally
                {
                    BatchProcessedCount++;
                }
            }

            if (lastSuccessfulResult is not null)
            {
                OutputPreviewPath = lastSuccessfulResult.OutputPath;
                LastSavedOutputPath = lastSuccessfulResult.OutputPath;
                PreviewTitle = "批处理最后成功结果";
                PreviewSizeText = $"{lastSuccessfulResult.OutputWidth} × {lastSuccessfulResult.OutputHeight} px";
                PreviewStatusText = "批量结果已经直接写入所选输出目录。";
            }

            _lastOutputDirectory = outputDirectory;
            BatchCurrentFileName = string.Empty;
            ResultSummary =
                $"批处理结束：共 {BatchTotalCount} 项 · 完成 {BatchCompletedCount} · 失败 {BatchFailedCount} · " +
                $"{(EnableAutomaticColorIndex ? $"每图自动索引最多 {MaximumIndexedColors} 色" : "未启用颜色索引")}";
            StatusText = BatchFailedCount == 0
                ? $"批处理全部完成：{outputDirectory}"
                : $"批处理已完成，{BatchFailedCount} 项需要检查；成功产物已保留。";
            NotifySettingsChanged();
        }
        finally
        {
            BatchCurrentFileName = string.Empty;
            IsBusy = false;
        }
    }

    private async Task ExportWorkingImageAsync()
    {
        if (!CanExportWorkingImage || CurrentPlan is null)
        {
            StatusText = "请先选择图片并形成有效的像素对齐计划。";
            return;
        }

        string? selected;
        try
        {
            selected = _outputPicker.SelectOutputFile(
                _lastOutputDirectory ?? Path.GetDirectoryName(InputPath),
                $"{Path.GetFileNameWithoutExtension(InputPath)}_{CurrentPlan.WorkingWidth}x{CurrentPlan.WorkingHeight}_nearest.png",
                "导出最近邻整数倍工作图");
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"无法设置工作图输出位置：{exception.Message}";
            return;
        }

        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        var outputPath = NormalizePngPath(selected);
        var plan = CurrentPlan;
        IsBusy = true;
        StatusText = $"正在导出 {plan.WorkingScale}× 最近邻工作图…";
        try
        {
            var result = await Task.Run(() => _service.ExportWorkingImageToPng(
                    InputPath,
                    outputPath,
                    BuildSettings(),
                    overwrite: true))
                .ConfigureAwait(true);
            if (result.Plan is not null)
            {
                CurrentPlan = result.Plan;
            }

            ClearSinglePreview(clearVisiblePreview: false);
            _lastOutputDirectory = Path.GetDirectoryName(result.OutputPath);
            OutputPreviewPath = result.OutputPath;
            LastSavedOutputPath = result.OutputPath;
            PreviewTitle = result.IsCorrectionApplied ? "矫正工作图预览" : "最近邻工作图预览";
            PreviewSizeText = $"{result.Width:N0} × {result.Height:N0} px";
            PreviewStatusText = "工作图已经直接导出到所选位置。";
            ResultSummary =
                $"{(result.IsCorrectionApplied ? "矫正工作图" : "最近邻工作图")} " +
                $"{result.Width:N0} × {result.Height:N0} px · {result.Scale}× · 未使用插值";
            StatusText = $"工作图导出完成：{result.OutputPath}";
            NotifySettingsChanged();
            _workbenchLogSink?.Write(WorkbenchLogLevel.Success, LogSource, StatusText);
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            ResultSummary = "工作图导出失败，输入图片保持不变。";
            StatusText = $"工作图导出失败：{exception.Message}";
            _workbenchLogSink?.Write(WorkbenchLogLevel.Error, LogSource, StatusText, exception: exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ResetCrop()
    {
        if (!HasInput)
        {
            return;
        }

        _suppressPlanRefresh = true;
        CropLeft = 0;
        CropTop = 0;
        CropRight = SourceWidth;
        CropBottom = SourceHeight;
        _suppressPlanRefresh = false;
        RefreshCropAndPlan();
        StatusText = "裁切标尺已恢复到完整图片边界。";
    }

    private void FitToApproximateSize()
    {
        var longestSide = Math.Max(CropWidth, CropHeight);
        if (longestSide <= 0)
        {
            return;
        }

        MinimumUpscaleFactor = Math.Max(1, (int)Math.Ceiling(ApproximatePreviewTarget / (double)longestSide));
        StatusText = $"最小整数倍率已按约 {ApproximatePreviewTarget}px 工作边长设置为 {MinimumUpscaleFactor}×。";
    }

    private void SetGridPreset(int value)
    {
        if (value is not (16 or 32 or 64))
        {
            return;
        }

        Columns = value;
        Rows = value;
    }

    private void SetAspectRatioPreset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var components = value.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (components.Length != 2 ||
            !double.TryParse(components[0], out var a) ||
            !double.TryParse(components[1], out var b))
        {
            return;
        }

        _suppressPlanRefresh = true;
        TargetAspectRatioA = a;
        TargetAspectRatioB = b;
        EnableAspectRatioCorrection = true;
        _suppressPlanRefresh = false;
        RefreshPlan();
        StatusText = $"目标比例已设置为 {FormatNumber(TargetAspectRatioA)}:{FormatNumber(TargetAspectRatioB)}；自定义网格保持不变。";
    }

    private void SyncGridToAspectRatio()
    {
        var targetRatio = TargetAspectRatioA / TargetAspectRatioB;
        if (!double.IsFinite(targetRatio) ||
            targetRatio < PixelAlignmentLimits.MinimumTargetAspectRatio ||
            targetRatio > PixelAlignmentLimits.MaximumTargetAspectRatio)
        {
            StatusText =
                $"目标 A:B 比例必须位于 {PixelAlignmentLimits.MinimumTargetAspectRatio:0.####} 到 " +
                $"{PixelAlignmentLimits.MaximumTargetAspectRatio:0.##} 之间，无法同步网格。";
            return;
        }

        var baseColumns = targetRatio >= 1d ? targetRatio : 1d;
        var baseRows = targetRatio >= 1d ? 1d : 1d / targetRatio;
        var preferredScale = Math.Max(1d, Columns / baseColumns);
        var requestedColumns = Math.Max(1d, Math.Round(baseColumns * preferredScale));
        var requestedRows = Math.Max(1d, Math.Round(baseRows * preferredScale));
        var limitingScale = Math.Min(
            1d,
            Math.Min(
                MaximumGridDimension / requestedColumns,
                Math.Min(
                    MaximumGridDimension / requestedRows,
                    Math.Sqrt(
                        PixelAlignmentLimits.MaximumGridPixelCount /
                        (requestedColumns * requestedRows)))));
        var newColumns = Math.Max(1, (int)Math.Round(requestedColumns * limitingScale));
        var newRows = Math.Max(1, (int)Math.Round(requestedRows * limitingScale));
        while (newColumns > MaximumGridDimension ||
               newRows > MaximumGridDimension ||
               (long)newColumns * newRows > PixelAlignmentLimits.MaximumGridPixelCount)
        {
            if (targetRatio >= 1d && newColumns > 1)
            {
                newColumns--;
                newRows = Math.Max(1, (int)Math.Round(newColumns / targetRatio));
            }
            else if (newRows > 1)
            {
                newRows--;
                newColumns = Math.Max(1, (int)Math.Round(newRows * targetRatio));
            }
            else
            {
                break;
            }
        }

        _suppressPlanRefresh = true;
        Columns = newColumns;
        Rows = newRows;
        _suppressPlanRefresh = false;
        RefreshPlan();
        var constrained = newColumns != (int)requestedColumns || newRows != (int)requestedRows;
        StatusText = constrained
            ? $"网格已按比例和安全像素上限同步为 {Columns}×{Rows}；仍可继续独立修改行列。"
            : $"网格已显式同步为 {Columns}×{Rows}；仍可继续独立修改行列。";
    }

    private void SetRotationPreset(double value)
    {
        _suppressPlanRefresh = true;
        RotationDegrees = value;
        EnableRotationCorrection = true;
        _suppressPlanRefresh = false;
        RefreshPlan();
        StatusText = $"顺时针角度调整已设置为 {RotationDegrees:0.##}°。";
    }

    private void ResetPerspectivePoints()
    {
        if (!HasInput)
        {
            return;
        }

        _suppressPlanRefresh = true;
        SetPerspectivePointsToCropBounds();
        _suppressPlanRefresh = false;
        RefreshPlan();
        StatusText = "四个透视顶点已重置到当前裁切边界。";
    }

    private void SetPerspectivePointsToCropBounds()
    {
        SetPerspectiveCoordinateCore(ref _perspectiveTopLeftX, CropLeft, nameof(PerspectiveTopLeftX));
        SetPerspectiveCoordinateCore(ref _perspectiveTopLeftY, CropTop, nameof(PerspectiveTopLeftY));
        SetPerspectiveCoordinateCore(ref _perspectiveTopRightX, CropRight, nameof(PerspectiveTopRightX));
        SetPerspectiveCoordinateCore(ref _perspectiveTopRightY, CropTop, nameof(PerspectiveTopRightY));
        SetPerspectiveCoordinateCore(ref _perspectiveBottomRightX, CropRight, nameof(PerspectiveBottomRightX));
        SetPerspectiveCoordinateCore(ref _perspectiveBottomRightY, CropBottom, nameof(PerspectiveBottomRightY));
        SetPerspectiveCoordinateCore(ref _perspectiveBottomLeftX, CropLeft, nameof(PerspectiveBottomLeftX));
        SetPerspectiveCoordinateCore(ref _perspectiveBottomLeftY, CropBottom, nameof(PerspectiveBottomLeftY));
    }

    private void SetPerspectivePointsToCropBoundsWithoutRefreshing()
    {
        var previousSuppression = _suppressPlanRefresh;
        _suppressPlanRefresh = true;
        SetPerspectivePointsToCropBounds();
        _suppressPlanRefresh = previousSuppression;
    }

    private void FollowPerspectivePointsToCropBounds(bool shouldFollow)
    {
        if (shouldFollow)
        {
            SetPerspectivePointsToCropBoundsWithoutRefreshing();
        }
    }

    private bool PerspectivePointsMatchCropBounds() =>
        NearlyEqual(PerspectiveTopLeftX, CropLeft) &&
        NearlyEqual(PerspectiveTopLeftY, CropTop) &&
        NearlyEqual(PerspectiveTopRightX, CropRight) &&
        NearlyEqual(PerspectiveTopRightY, CropTop) &&
        NearlyEqual(PerspectiveBottomRightX, CropRight) &&
        NearlyEqual(PerspectiveBottomRightY, CropBottom) &&
        NearlyEqual(PerspectiveBottomLeftX, CropLeft) &&
        NearlyEqual(PerspectiveBottomLeftY, CropBottom);

    private bool ArePerspectivePointsInsideCrop() =>
        IsPointInsideCrop(PerspectiveTopLeftX, PerspectiveTopLeftY) &&
        IsPointInsideCrop(PerspectiveTopRightX, PerspectiveTopRightY) &&
        IsPointInsideCrop(PerspectiveBottomRightX, PerspectiveBottomRightY) &&
        IsPointInsideCrop(PerspectiveBottomLeftX, PerspectiveBottomLeftY);

    private bool IsPointInsideCrop(double x, double y) =>
        x >= CropLeft && x <= CropRight && y >= CropTop && y <= CropBottom;

    private static bool NearlyEqual(double left, double right) => Math.Abs(left - right) <= 1e-9;

    private void RefreshCropAndPlan()
    {
        OnPropertyChanged(nameof(CropWidth));
        OnPropertyChanged(nameof(CropHeight));
        OnPropertyChanged(nameof(CropSizeText));
        if (!_suppressPlanRefresh)
        {
            RefreshPlan();
        }
    }

    private void RefreshPlan()
    {
        MarkPreviewStale();
        if (!HasInput || CropWidth <= 0 || CropHeight <= 0)
        {
            CurrentPlan = null;
            RefreshCommands();
            return;
        }

        try
        {
            CurrentPlan = _service.CreatePlan(SourceWidth, SourceHeight, BuildSettings());
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            CurrentPlan = null;
            StatusText = $"当前像素对齐参数无效：{exception.Message}";
        }

        OnPropertyChanged(nameof(CanProcess));
        OnPropertyChanged(nameof(CanProcessBatch));
        OnPropertyChanged(nameof(CanExportWorkingImage));
        RefreshCommands();
    }

    private PixelAlignmentSettings BuildSettings() => new()
    {
        CropBounds = new PixelCropBounds
        {
            Left = CropLeft,
            Top = CropTop,
            Right = CropRight,
            Bottom = CropBottom
        },
        Columns = Columns,
        Rows = Rows,
        MinimumUpscaleFactor = MinimumUpscaleFactor,
        SamplingMode = SelectedSamplingMode.Mode,
        ColorIndex = new PixelColorIndexSettings
        {
            Mode = EnableAutomaticColorIndex
                ? PixelColorIndexMode.Automatic
                : PixelColorIndexMode.Disabled,
            MaximumColors = MaximumIndexedColors
        },
        Correction = new PixelAlignmentCorrectionSettings
        {
            EnableAspectRatioCorrection = EnableAspectRatioCorrection,
            TargetAspectRatioA = TargetAspectRatioA,
            TargetAspectRatioB = TargetAspectRatioB,
            EnableRotationCorrection = EnableRotationCorrection,
            RotationDegrees = RotationDegrees,
            EnablePerspectiveCorrection = EnablePerspectiveCorrection,
            SourceQuadrilateral = EnablePerspectiveCorrection
                ? new PixelPerspectiveQuadrilateral
                {
                    TopLeft = new PixelCoordinate { X = PerspectiveTopLeftX, Y = PerspectiveTopLeftY },
                    TopRight = new PixelCoordinate { X = PerspectiveTopRightX, Y = PerspectiveTopRightY },
                    BottomRight = new PixelCoordinate { X = PerspectiveBottomRightX, Y = PerspectiveBottomRightY },
                    BottomLeft = new PixelCoordinate { X = PerspectiveBottomLeftX, Y = PerspectiveBottomLeftY }
                }
                : null,
            TargetEdgeAngleDegrees = TargetEdgeAngleDegrees
        }
    };

    private void RaisePlanPropertiesChanged()
    {
        OnPropertyChanged(nameof(RequestedWorkingSizeText));
        OnPropertyChanged(nameof(CorrectedBaseSizeText));
        OnPropertyChanged(nameof(CorrectedWorkingSizeText));
        OnPropertyChanged(nameof(WorkingSizeText));
        OnPropertyChanged(nameof(WorkingScaleText));
        OnPropertyChanged(nameof(CellSizeText));
        OnPropertyChanged(nameof(PlanExplanation));
        OnPropertyChanged(nameof(IsGeometryFitPaddingRequired));
        OnPropertyChanged(nameof(GeometryFitStatusText));
        OnPropertyChanged(nameof(CanProcess));
        OnPropertyChanged(nameof(CanRefreshPreview));
        OnPropertyChanged(nameof(CanSaveResult));
        OnPropertyChanged(nameof(CanProcessBatch));
        OnPropertyChanged(nameof(CanExportWorkingImage));
        RefreshCommands();
    }

    private void RaiseCorrectionPropertiesChanged()
    {
        OnPropertyChanged(nameof(IsAnyCorrectionEnabled));
        OnPropertyChanged(nameof(CorrectionStepsText));
        OnPropertyChanged(nameof(PlanExplanation));
        OnPropertyChanged(nameof(AspectGridStatusText));
        OnPropertyChanged(nameof(IsAspectGridMismatch));
    }

    private void RaiseAspectGridPropertiesChanged()
    {
        OnPropertyChanged(nameof(IsAspectGridMismatch));
        OnPropertyChanged(nameof(AspectGridStatusText));
    }

    private void RaisePerspectivePointPropertiesChanged()
    {
        OnPropertyChanged(nameof(PerspectiveTopLeftX));
        OnPropertyChanged(nameof(PerspectiveTopLeftY));
        OnPropertyChanged(nameof(PerspectiveTopRightX));
        OnPropertyChanged(nameof(PerspectiveTopRightY));
        OnPropertyChanged(nameof(PerspectiveBottomRightX));
        OnPropertyChanged(nameof(PerspectiveBottomRightY));
        OnPropertyChanged(nameof(PerspectiveBottomLeftX));
        OnPropertyChanged(nameof(PerspectiveBottomLeftY));
    }

    private void SetPerspectiveCoordinate(
        ref double field,
        double value,
        int maximum,
        string propertyName)
    {
        var normalized = double.IsFinite(value)
            ? Math.Round(Math.Clamp(value, 0d, Math.Max(0, maximum)), MidpointRounding.AwayFromZero)
            : 0d;
        if (!SetProperty(ref field, normalized, propertyName))
        {
            return;
        }

        if (!_suppressPlanRefresh)
        {
            RefreshPlan();
        }
    }

    private void SetPerspectiveCoordinateCore(ref double field, double value, string propertyName)
    {
        var normalized = Math.Round(value, MidpointRounding.AwayFromZero);
        if (SetProperty(ref field, normalized, propertyName) && !_suppressPlanRefresh)
        {
            RefreshPlan();
        }
    }

    private void RefreshCommands()
    {
        _chooseInputCommand.RaiseCanExecuteChanged();
        _chooseOutputCommand.RaiseCanExecuteChanged();
        _chooseBatchInputsCommand.RaiseCanExecuteChanged();
        _chooseBatchOutputDirectoryCommand.RaiseCanExecuteChanged();
        _removeSelectedBatchItemCommand.RaiseCanExecuteChanged();
        _clearBatchItemsCommand.RaiseCanExecuteChanged();
        _resetCropCommand.RaiseCanExecuteChanged();
        _fitToApproximateSizeCommand.RaiseCanExecuteChanged();
        _setGridPresetCommand.RaiseCanExecuteChanged();
        _setAspectRatioPresetCommand.RaiseCanExecuteChanged();
        _syncGridToAspectRatioCommand.RaiseCanExecuteChanged();
        _setRotationPresetCommand.RaiseCanExecuteChanged();
        _resetPerspectivePointsCommand.RaiseCanExecuteChanged();
        _processCommand.RaiseCanExecuteChanged();
        _refreshPreviewCommand.RaiseCanExecuteChanged();
        _saveResultCommand.RaiseCanExecuteChanged();
        _processBatchCommand.RaiseCanExecuteChanged();
        _exportWorkingImageCommand.RaiseCanExecuteChanged();
        _openOutputDirectoryCommand.RaiseCanExecuteChanged();
    }

    private void OpenOutputDirectory()
    {
        var directory = Path.GetDirectoryName(LastSavedOutputPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"无法打开输出目录：{exception.Message}";
        }
    }

    private void LoadInputInfo(PixelAlignmentImageInfo info)
    {
        ClearSinglePreview(clearVisiblePreview: true);
        LastSavedOutputPath = string.Empty;
        _suppressPlanRefresh = true;
        InputPath = info.InputPath;
        SourceWidth = info.Width;
        SourceHeight = info.Height;
        _cropLeft = 0;
        _cropTop = 0;
        _cropRight = info.Width;
        _cropBottom = info.Height;
        OnPropertyChanged(nameof(CropLeft));
        OnPropertyChanged(nameof(CropTop));
        OnPropertyChanged(nameof(CropRight));
        OnPropertyChanged(nameof(CropBottom));
        SetPerspectivePointsToCropBounds();
        _suppressPlanRefresh = false;
        _lastInputDirectory = Path.GetDirectoryName(info.InputPath);
        OutputPath = string.Empty;
        ResetPreviewMetadata();
        PreviewStatusText = "输入图片已变化，请刷新预览。";
        RefreshCropAndPlan();
    }

    private void ResetBatchProgress(bool resetItems)
    {
        if (resetItems)
        {
            foreach (var item in BatchItems)
            {
                item.Status = PixelAlignmentBatchItemStatus.Pending;
                item.OutputPath = string.Empty;
                item.Message = "等待处理";
            }
        }

        BatchProcessedCount = 0;
        BatchCompletedCount = 0;
        BatchFailedCount = 0;
        BatchCurrentFileName = string.Empty;
        RaiseBatchProgressPropertiesChanged();
    }

    private void RaiseBatchCollectionPropertiesChanged()
    {
        OnPropertyChanged(nameof(BatchTotalCount));
        OnPropertyChanged(nameof(CanProcessBatch));
        RaiseBatchProgressPropertiesChanged();
        RefreshCommands();
    }

    private void RaiseBatchProgressPropertiesChanged()
    {
        OnPropertyChanged(nameof(BatchProgressPercent));
        OnPropertyChanged(nameof(BatchProgressText));
    }

    private static PixelAlignmentSettings MapSettingsToBatchItem(
        PixelAlignmentSettings reference,
        int referenceWidth,
        int referenceHeight,
        int targetWidth,
        int targetHeight)
    {
        if (referenceWidth <= 0 || referenceHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetWidth), "批处理图片尺寸必须大于 0。");
        }

        if (referenceWidth == targetWidth && referenceHeight == targetHeight)
        {
            return reference;
        }

        var sourceCrop = reference.CropBounds ?? new PixelCropBounds
        {
            Left = 0,
            Top = 0,
            Right = referenceWidth,
            Bottom = referenceHeight
        };
        var scaleX = targetWidth / (double)referenceWidth;
        var scaleY = targetHeight / (double)referenceHeight;
        var left = Math.Clamp((int)Math.Floor(sourceCrop.Left * scaleX), 0, Math.Max(0, targetWidth - 1));
        var top = Math.Clamp((int)Math.Floor(sourceCrop.Top * scaleY), 0, Math.Max(0, targetHeight - 1));
        var right = Math.Clamp((int)Math.Ceiling(sourceCrop.Right * scaleX), left + 1, targetWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(sourceCrop.Bottom * scaleY), top + 1, targetHeight);
        var mappedCrop = new PixelCropBounds
        {
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom
        };

        var correction = reference.Correction ?? new PixelAlignmentCorrectionSettings();
        var quadrilateral = correction.EnablePerspectiveCorrection && correction.SourceQuadrilateral is { } sourceQuad
            ? new PixelPerspectiveQuadrilateral
            {
                TopLeft = MapPerspectivePoint(sourceQuad.TopLeft, scaleX, scaleY, mappedCrop),
                TopRight = MapPerspectivePoint(sourceQuad.TopRight, scaleX, scaleY, mappedCrop),
                BottomRight = MapPerspectivePoint(sourceQuad.BottomRight, scaleX, scaleY, mappedCrop),
                BottomLeft = MapPerspectivePoint(sourceQuad.BottomLeft, scaleX, scaleY, mappedCrop)
            }
            : null;

        return reference with
        {
            CropBounds = mappedCrop,
            Correction = correction with { SourceQuadrilateral = quadrilateral }
        };
    }

    private static PixelCoordinate MapPerspectivePoint(
        PixelCoordinate source,
        double scaleX,
        double scaleY,
        PixelCropBounds crop) =>
        new()
        {
            X = Math.Clamp(source.X * scaleX, crop.Left, crop.Right),
            Y = Math.Clamp(source.Y * scaleY, crop.Top, crop.Bottom)
        };

    private static string ResolveUniqueBatchOutputPath(
        string outputDirectory,
        string inputPath,
        int columns,
        int rows,
        ISet<string> reservedPaths)
    {
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var baseName = $"{stem}_{columns}x{rows}_aligned";
        var candidate = Path.Combine(outputDirectory, $"{baseName}.png");
        var suffix = 2;
        while (File.Exists(candidate) || reservedPaths.Contains(candidate) || PathsEqual(inputPath, candidate))
        {
            candidate = Path.Combine(outputDirectory, $"{baseName}_{suffix:000}.png");
            suffix++;
        }

        reservedPaths.Add(candidate);
        return candidate;
    }

    private static string FormatColorIndexResult(PixelAlignmentResult result) =>
        result.ColorIndexMode == PixelColorIndexMode.Automatic
            ? $"自动索引 {result.ActualIndexedColorCount}/{result.MaximumIndexedColors} 色"
            : "颜色索引关闭";

    private void ResetInputState()
    {
        ClearSinglePreview(clearVisiblePreview: true);
        LastSavedOutputPath = string.Empty;
        _suppressPlanRefresh = true;
        InputPath = string.Empty;
        SourceWidth = 0;
        SourceHeight = 0;
        _cropLeft = 0;
        _cropTop = 0;
        _cropRight = 0;
        _cropBottom = 0;
        _perspectiveTopLeftX = 0d;
        _perspectiveTopLeftY = 0d;
        _perspectiveTopRightX = 0d;
        _perspectiveTopRightY = 0d;
        _perspectiveBottomRightX = 0d;
        _perspectiveBottomRightY = 0d;
        _perspectiveBottomLeftX = 0d;
        _perspectiveBottomLeftY = 0d;
        OnPropertyChanged(nameof(CropLeft));
        OnPropertyChanged(nameof(CropTop));
        OnPropertyChanged(nameof(CropRight));
        OnPropertyChanged(nameof(CropBottom));
        RaisePerspectivePointPropertiesChanged();
        _suppressPlanRefresh = false;
        CurrentPlan = null;
        OutputPath = string.Empty;
        ResetPreviewMetadata();
        PreviewStatusText = "尚未刷新预览。";
        RefreshCropAndPlan();
    }

    private void NotifySettingsChanged()
    {
        if (_suppressSettingsChanged)
        {
            return;
        }

        SettingsChanged?.Invoke(
            this,
            new PixelAlignmentSettingsChangedEventArgs(CaptureSettings()));
    }

    private void ResetPreviewMetadata()
    {
        PreviewTitle = "对齐结果预览";
        PreviewSizeText = "—";
    }

    private void MarkPreviewStale()
    {
        _previewSettingsRevision++;
        if (string.IsNullOrWhiteSpace(_singlePreviewPath))
        {
            return;
        }

        if (!File.Exists(_singlePreviewPath))
        {
            TryReleasePreview(_singlePreviewPath);
            _singlePreviewPath = string.Empty;
            IsPreviewCurrent = false;
            IsPreviewStale = false;
            if (_previewCache.IsManagedPreviewPath(OutputPreviewPath))
            {
                OutputPreviewPath = string.Empty;
                ResetPreviewMetadata();
            }

            PreviewStatusText = "预览缓存已不存在，请重新刷新。";
            return;
        }

        IsPreviewCurrent = false;
        IsPreviewStale = true;
        PreviewStatusText = "参数已变化；当前预览仅供对比，刷新前不可保存。";
    }

    private void ClearSinglePreview(bool clearVisiblePreview)
    {
        _previewSettingsRevision++;
        var oldPreviewPath = _singlePreviewPath;
        _singlePreviewPath = string.Empty;
        IsPreviewCurrent = false;
        IsPreviewStale = false;
        if (clearVisiblePreview || PathsEqual(OutputPreviewPath, oldPreviewPath))
        {
            OutputPreviewPath = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(oldPreviewPath))
        {
            TryReleasePreview(oldPreviewPath);
        }
    }

    private void TryReleasePreview(string previewPath)
    {
        try
        {
            if (_previewCache.IsManagedPreviewPath(previewPath))
            {
                _previewCache.ReleasePreview(previewPath);
            }
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            _workbenchLogSink?.Write(
                WorkbenchLogLevel.Warning,
                LogSource,
                $"无法清理像素对齐预览缓存：{previewPath} · {exception.Message}",
                exception: exception);
        }
    }

    private static PixelSamplingModeOption ResolveSamplingMode(string? value)
    {
        if (Enum.TryParse<PixelCellSamplingMode>(value, ignoreCase: true, out var parsed))
        {
            var match = SupportedSamplingModes.FirstOrDefault(option => option.Mode == parsed);
            if (match is not null)
            {
                return match;
            }
        }

        return SupportedSamplingModes.First(option => option.Mode == PixelCellSamplingMode.CenterPixel);
    }

    private static double NormalizeAspectComponent(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.01d, 4096d) : 1d;

    private static double NormalizeRotation(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, -180d, 180d) : 0d;

    private static double NormalizeTargetEdgeAngle(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(
                value,
                PixelAlignmentLimits.MinimumTargetEdgeAngleDegrees,
                PixelAlignmentLimits.MaximumTargetEdgeAngleDegrees)
            : 90d;

    private static string FormatNumber(double value) => value.ToString("0.##");

    private static string NormalizePngPath(string path)
    {
        var normalized = Path.GetFullPath(path);
        return string.Equals(Path.GetExtension(normalized), ".png", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : Path.ChangeExtension(normalized, ".png");
    }

    private static string? NormalizeExistingDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var normalized = Path.GetFullPath(value);
            return Directory.Exists(normalized) ? normalized : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool CanMaterializeWorkingImage(PixelAlignmentPlan plan) =>
        plan.CorrectedWorkingWidth <= PixelAlignmentLimits.MaximumWorkingDimension &&
        plan.CorrectedWorkingHeight <= PixelAlignmentLimits.MaximumWorkingDimension &&
        plan.CorrectedWorkingWidth * plan.CorrectedWorkingHeight <=
        PixelAlignmentLimits.MaximumWorkingPixelCount;

    private static bool PathsEqual(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool IsExpectedOperationException(Exception exception) => exception is
        IOException or UnauthorizedAccessException or ArgumentException or ImageFormatException or
        NotSupportedException or InvalidOperationException or OverflowException;
}

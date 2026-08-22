using GAssetForge.App.Infrastructure;
using GAssetForge.App.Services;
using GAssetForge.Core.Imaging;
using SixLabors.ImageSharp;
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
    string? LastOutputDirectory);

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

    private readonly IPixelAlignmentService _service;
    private readonly IPixelAlignmentInputPicker _inputPicker;
    private readonly IPixelAlignmentOutputPicker _outputPicker;
    private readonly IWorkbenchLogSink? _workbenchLogSink;
    private readonly RelayCommand _chooseInputCommand;
    private readonly RelayCommand _chooseOutputCommand;
    private readonly RelayCommand _resetCropCommand;
    private readonly RelayCommand _fitToApproximateSizeCommand;
    private readonly RelayCommand<int> _setGridPresetCommand;
    private readonly RelayCommand<string> _setAspectRatioPresetCommand;
    private readonly RelayCommand _syncGridToAspectRatioCommand;
    private readonly RelayCommand<double> _setRotationPresetCommand;
    private readonly RelayCommand _resetPerspectivePointsCommand;
    private readonly AsyncRelayCommand _processCommand;
    private readonly AsyncRelayCommand _exportWorkingImageCommand;
    private readonly RelayCommand _openOutputDirectoryCommand;
    private int _minimumUpscaleFactor;
    private int _columns;
    private int _rows;
    private PixelSamplingModeOption _selectedSamplingMode;
    private bool _showGrid;
    private string _inputPath = string.Empty;
    private string _outputPath = string.Empty;
    private string _outputPreviewPath = string.Empty;
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
        string? lastInputDirectory = null,
        string? lastOutputDirectory = null,
        IWorkbenchLogSink? workbenchLogSink = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _inputPicker = inputPicker ?? throw new ArgumentNullException(nameof(inputPicker));
        _outputPicker = outputPicker ?? throw new ArgumentNullException(nameof(outputPicker));
        _workbenchLogSink = workbenchLogSink;
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
        _lastInputDirectory = NormalizeExistingDirectory(lastInputDirectory);
        _lastOutputDirectory = NormalizeExistingDirectory(lastOutputDirectory);

        _chooseInputCommand = new RelayCommand(ChooseInput, () => !IsBusy);
        _chooseOutputCommand = new RelayCommand(ChooseOutput, () => !IsBusy && HasInput);
        _resetCropCommand = new RelayCommand(ResetCrop, () => !IsBusy && HasInput);
        _fitToApproximateSizeCommand = new RelayCommand(FitToApproximateSize, () => !IsBusy && HasInput);
        _setGridPresetCommand = new RelayCommand<int>(SetGridPreset, value => !IsBusy && value is 16 or 32 or 64);
        _setAspectRatioPresetCommand = new RelayCommand<string>(SetAspectRatioPreset, value => !IsBusy && !string.IsNullOrWhiteSpace(value));
        _syncGridToAspectRatioCommand = new RelayCommand(SyncGridToAspectRatio, () => !IsBusy);
        _setRotationPresetCommand = new RelayCommand<double>(SetRotationPreset, _ => !IsBusy);
        _resetPerspectivePointsCommand = new RelayCommand(ResetPerspectivePoints, () => !IsBusy && HasInput);
        _processCommand = new AsyncRelayCommand(ProcessAsync, () => CanProcess);
        _exportWorkingImageCommand = new AsyncRelayCommand(ExportWorkingImageAsync, () => CanExportWorkingImage);
        _openOutputDirectoryCommand = new RelayCommand(OpenOutputDirectory, () => HasOutputPreview);
        ChooseInputCommand = _chooseInputCommand;
        ChooseOutputCommand = _chooseOutputCommand;
        ResetCropCommand = _resetCropCommand;
        FitToApproximateSizeCommand = _fitToApproximateSizeCommand;
        SetGridPresetCommand = _setGridPresetCommand;
        SetAspectRatioPresetCommand = _setAspectRatioPresetCommand;
        SyncGridToAspectRatioCommand = _syncGridToAspectRatioCommand;
        SetRotationPresetCommand = _setRotationPresetCommand;
        ResetPerspectivePointsCommand = _resetPerspectivePointsCommand;
        ProcessCommand = _processCommand;
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
                OnPropertyChanged(nameof(CanExportWorkingImage));
                RefreshCommands();
            }
        }
    }

    public bool CanProcess => !IsBusy && HasInput && CurrentPlan is not null &&
                              !string.IsNullOrWhiteSpace(OutputPath) &&
                              !PathsEqual(InputPath, OutputPath);

    public bool CanExportWorkingImage =>
        !IsBusy && HasInput && CurrentPlan is not null && CanMaterializeWorkingImage(CurrentPlan);

    public ICommand ChooseInputCommand { get; }

    public ICommand ChooseOutputCommand { get; }

    public ICommand ResetCropCommand { get; }

    public ICommand FitToApproximateSizeCommand { get; }

    public ICommand SetGridPresetCommand { get; }

    public ICommand SetAspectRatioPresetCommand { get; }

    public ICommand SyncGridToAspectRatioCommand { get; }

    public ICommand SetRotationPresetCommand { get; }

    public ICommand ResetPerspectivePointsCommand { get; }

    public ICommand ProcessCommand { get; }

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
        _lastOutputDirectory);

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
            OutputPreviewPath = string.Empty;
            ResetPreviewMetadata();
            ResultSummary = "输入图片已就绪；拖动标尺并设置输出位置。";
            RefreshCropAndPlan();
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
            OutputPreviewPath = string.Empty;
            ResetPreviewMetadata();
            _lastOutputDirectory = Path.GetDirectoryName(OutputPath);
            StatusText = $"对齐结果将保存到：{OutputPath}";
            NotifySettingsChanged();
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"无法设置输出位置：{exception.Message}";
        }
    }

    private async Task ProcessAsync()
    {
        if (!CanProcess || CurrentPlan is null)
        {
            StatusText = "当前输入、标尺、网格或输出位置尚未就绪。";
            return;
        }

        var inputPath = InputPath;
        var outputPath = OutputPath;
        var settings = BuildSettings();
        IsBusy = true;
        OutputPreviewPath = string.Empty;
        StatusText = $"正在按 {Columns}×{Rows} 网格进行{SelectedSamplingMode.DisplayName}…";
        _workbenchLogSink?.Write(
            WorkbenchLogLevel.Information,
            LogSource,
            $"开始像素对齐：{Path.GetFileName(inputPath)} · crop {CropWidth}×{CropHeight} · " +
            $"grid {Columns}×{Rows} · minimum {MinimumUpscaleFactor}×");
        try
        {
            var result = await Task.Run(() =>
                    _service.AlignToPng(inputPath, outputPath, settings, overwrite: true))
                .ConfigureAwait(true);
            CurrentPlan = result.Plan;
            OutputPreviewPath = result.OutputPath;
            PreviewTitle = "对齐结果预览";
            PreviewSizeText = $"{result.OutputWidth} × {result.OutputHeight} px";
            ResultSummary =
                $"输出 {result.OutputWidth} × {result.OutputHeight} px · 工作尺寸 " +
                $"{result.Plan.WorkingWidth:N0} × {result.Plan.WorkingHeight:N0} · " +
                $"{SelectedSamplingMode.DisplayName}";
            StatusText = $"像素对齐完成：{result.OutputPath}";
            _workbenchLogSink?.Write(
                WorkbenchLogLevel.Success,
                LogSource,
                $"像素对齐完成：{result.OutputPath} · {ResultSummary}");
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            ResultSummary = "处理失败，输入图片保持不变。";
            StatusText = $"像素对齐失败：{exception.Message}";
            _workbenchLogSink?.Write(WorkbenchLogLevel.Error, LogSource, StatusText, exception: exception);
        }
        finally
        {
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

            _lastOutputDirectory = Path.GetDirectoryName(result.OutputPath);
            OutputPreviewPath = result.OutputPath;
            PreviewTitle = result.IsCorrectionApplied ? "矫正工作图预览" : "最近邻工作图预览";
            PreviewSizeText = $"{result.Width:N0} × {result.Height:N0} px";
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
        _resetCropCommand.RaiseCanExecuteChanged();
        _fitToApproximateSizeCommand.RaiseCanExecuteChanged();
        _setGridPresetCommand.RaiseCanExecuteChanged();
        _setAspectRatioPresetCommand.RaiseCanExecuteChanged();
        _syncGridToAspectRatioCommand.RaiseCanExecuteChanged();
        _setRotationPresetCommand.RaiseCanExecuteChanged();
        _resetPerspectivePointsCommand.RaiseCanExecuteChanged();
        _processCommand.RaiseCanExecuteChanged();
        _exportWorkingImageCommand.RaiseCanExecuteChanged();
        _openOutputDirectoryCommand.RaiseCanExecuteChanged();
    }

    private void OpenOutputDirectory()
    {
        var directory = Path.GetDirectoryName(OutputPreviewPath);
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

    private void ResetInputState()
    {
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
        OutputPreviewPath = string.Empty;
        ResetPreviewMetadata();
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

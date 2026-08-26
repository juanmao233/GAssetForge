using GAssetForge.App.Infrastructure;
using GAssetForge.App.Services;
using GAssetForge.Core.Generation;
using GAssetForge.Core.Imaging;
using SixLabors.ImageSharp;
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
    int CheckerCellSize,
    int CheckerOffsetX,
    int CheckerOffsetY,
    int CheckerBoundaryTolerance,
    string? LastInputDirectory,
    string? LastOutputDirectory);

public sealed class BackgroundRemovalSettingsChangedEventArgs(
    BackgroundRemovalToolSettingsSnapshot settings) : EventArgs
{
    public BackgroundRemovalToolSettingsSnapshot Settings { get; } = settings;
}

public sealed class BackgroundRemovalViewModel : ObservableObject
{
    private const string LogSource = "BackgroundRemoval";

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
    private int _checkerCellSize;
    private int _checkerOffsetX;
    private int _checkerOffsetY;
    private int _checkerBoundaryTolerance;
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
        bool edgeConnectedOnly = true,
        bool useFakeTransparencyGrid = false,
        string? checkerColorAHex = null,
        string? checkerColorBHex = null,
        bool enableCheckerGridAlignment = true,
        int checkerCellSize = 16,
        int checkerOffsetX = 0,
        int checkerOffsetY = 0,
        int checkerBoundaryTolerance = 1,
        string? lastInputDirectory = null,
        string? lastOutputDirectory = null,
        IWorkbenchLogSink? workbenchLogSink = null,
        IImageProcessingPreviewCache? previewCache = null,
        IColorPickerService? colorPickerService = null)
    {
        _backgroundRemovalService = backgroundRemovalService ?? throw new ArgumentNullException(nameof(backgroundRemovalService));
        _inputPicker = inputPicker ?? throw new ArgumentNullException(nameof(inputPicker));
        _outputPicker = outputPicker ?? throw new ArgumentNullException(nameof(outputPicker));
        _previewCache = previewCache ?? CreateFallbackPreviewCache();
        _colorPickerService = colorPickerService ?? new DesktopColorPickerService();
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
        _checkerCellSize = Math.Clamp(checkerCellSize, 2, 512);
        _checkerOffsetX = NormalizeCheckerOffset(checkerOffsetX, _checkerCellSize);
        _checkerOffsetY = NormalizeCheckerOffset(checkerOffsetY, _checkerCellSize);
        _checkerBoundaryTolerance = Math.Clamp(checkerBoundaryTolerance, 0, 8);
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
        _suppressSettingsChanged = false;
    }

    public event EventHandler<BackgroundRemovalSettingsChangedEventArgs>? SettingsChanged;

    public IReadOnlyList<BackgroundRemovalPresetOption> PresetOptions => SupportedPresets;

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
                MarkPreviewStale();
                RefreshValidationAndCommands();
                NotifySettingsChanged();
            }
        }
    }

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

    public int CheckerCellSize
    {
        get => _checkerCellSize;
        set
        {
            var normalized = Math.Clamp(value, 2, 512);
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
            NotifySettingsChanged();
        }
    }

    public int CheckerOffsetMaximum => Math.Max(1, CheckerCellSize - 1);

    public int CheckerOffsetX
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
                NotifySettingsChanged();
            }
        }
    }

    public int CheckerOffsetY
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
                NotifySettingsChanged();
            }
        }
    }

    public int CheckerBoundaryTolerance
    {
        get => _checkerBoundaryTolerance;
        set
        {
            var normalized = Math.Clamp(value, 0, 8);
            if (SetProperty(ref _checkerBoundaryTolerance, normalized))
            {
                OnPropertyChanged(nameof(CheckerAlignmentSummary));
                MarkPreviewStale();
                RefreshValidationAndCommands();
                NotifySettingsChanged();
            }
        }
    }

    public string CheckerAlignmentSummary => EnableCheckerGridAlignment
        ? $"按 {CheckerCellSize}px 方格、偏移 ({CheckerOffsetX},{CheckerOffsetY}) 对齐；格线 ±{CheckerBoundaryTolerance}px 内允许匹配任一底色。"
        : "关闭网格对齐后，任意位置只要接近两种底色之一就会进入剔除候选，误删风险更高。";

    public bool ShowCheckerGridOverlay => UseFakeTransparencyGrid && EnableCheckerGridAlignment;

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

    public bool IsConfigurationValid => TryBuildSettings(out _, out _);

    public bool IsGenerationConfigurationValid => TryBuildSolidColorSettings(out _, out _);

    public string ConfigurationValidationText => TryBuildSettings(out _, out var reason)
        ? UseFakeTransparencyGrid
            ? "参数有效；将按双颜色棋盘格恢复 Alpha。单幅合成图无法精确还原原始半透明前景，边缘为近似恢复。"
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

    private async Task RefreshPreviewAsync()
    {
        if (!TryBuildSettings(out var settings, out var reason) || !CanRefreshPreview)
        {
            StatusText = string.IsNullOrWhiteSpace(reason) ? "当前输入或参数尚未就绪。" : reason;
            return;
        }

        var inputPath = InputPath;
        var settingsRevision = _previewSettingsRevision;
        var previousPreviewPath = OutputPreviewPath;
        string? reservedPreviewPath = null;
        IsBusy = true;
        IsPreviewCurrent = false;
        var modeText = settings.UseFakeTransparencyGrid
            ? $"假透明棋盘格 {settings.CheckerColorAHex} / {settings.CheckerColorBHex}"
            : $"纯色 {settings.KeyColorHex}";
        PreviewStatusText = "正在刷新缓存预览…";
        StatusText = $"正在使用{modeText}剔除背景并刷新预览…";
        _workbenchLogSink?.Write(
            WorkbenchLogLevel.Information,
            LogSource,
            $"开始刷新背景剔除预览：{Path.GetFileName(inputPath)} · {modeText}");
        try
        {
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
        if (!TryBuildSolidColorSettings(out settings, out reason))
        {
            return false;
        }

        if (!UseFakeTransparencyGrid)
        {
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

        if (CheckerCellSize is < 2 or > 512)
        {
            reason = "假透明网格尺寸必须位于 2 到 512 像素之间。";
            return false;
        }

        if (CheckerBoundaryTolerance is < 0 or > 8)
        {
            reason = "网格边界容差必须位于 0 到 8 像素之间。";
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
        settings = new BackgroundRemovalSettings();
        reason = string.Empty;
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
            KeyColorHex = keyColor,
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
    }

    private void NotifySettingsChanged()
    {
        if (_suppressSettingsChanged)
        {
            return;
        }

        SettingsChanged?.Invoke(this, new BackgroundRemovalSettingsChangedEventArgs(CaptureSettings()));
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

    private static int NormalizeCheckerOffset(int value, int cellSize)
    {
        var normalizedCellSize = Math.Clamp(cellSize, 2, 512);
        var remainder = value % normalizedCellSize;
        return remainder < 0 ? remainder + normalizedCellSize : remainder;
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
}

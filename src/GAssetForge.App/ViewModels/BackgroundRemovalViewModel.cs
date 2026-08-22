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
    private readonly IWorkbenchLogSink? _workbenchLogSink;
    private readonly RelayCommand _chooseInputCommand;
    private readonly RelayCommand _chooseOutputCommand;
    private readonly AsyncRelayCommand _processCommand;
    private readonly RelayCommand _openOutputDirectoryCommand;
    private BackgroundRemovalPresetOption _selectedPreset;
    private string _customColorHex;
    private double _transparentThreshold;
    private double _opaqueThreshold;
    private int _edgeContract;
    private int _edgeFeather;
    private bool _despill;
    private bool _edgeConnectedOnly;
    private string _inputPath = string.Empty;
    private string _outputPath = string.Empty;
    private string _outputPreviewPath = string.Empty;
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
        string? lastInputDirectory = null,
        string? lastOutputDirectory = null,
        IWorkbenchLogSink? workbenchLogSink = null)
    {
        _backgroundRemovalService = backgroundRemovalService ?? throw new ArgumentNullException(nameof(backgroundRemovalService));
        _inputPicker = inputPicker ?? throw new ArgumentNullException(nameof(inputPicker));
        _outputPicker = outputPicker ?? throw new ArgumentNullException(nameof(outputPicker));
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
        _lastInputDirectory = NormalizeExistingDirectory(lastInputDirectory);
        _lastOutputDirectory = NormalizeExistingDirectory(lastOutputDirectory);

        _chooseInputCommand = new RelayCommand(ChooseInput, () => !IsBusy);
        _chooseOutputCommand = new RelayCommand(ChooseOutput, () => !IsBusy && HasInput);
        _processCommand = new AsyncRelayCommand(ProcessAsync, () => CanProcess);
        _openOutputDirectoryCommand = new RelayCommand(OpenOutputDirectory, () => HasOutputPreview);
        ChooseInputCommand = _chooseInputCommand;
        ChooseOutputCommand = _chooseOutputCommand;
        ProcessCommand = _processCommand;
        OpenOutputDirectoryCommand = _openOutputDirectoryCommand;
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
                NotifySettingsChanged();
            }
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
            RefreshValidationAndCommands();
        }
    }

    public string InputFileName => HasInput ? Path.GetFileName(InputPath) : "尚未选择输入图片";

    public bool HasInput => File.Exists(InputPath);

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
            _openOutputDirectoryCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasOutputPreview => File.Exists(OutputPreviewPath);

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
            RaiseCommandStates();
        }
    }

    public bool CanProcess => !IsBusy && HasInput &&
                              !string.IsNullOrWhiteSpace(OutputPath) &&
                              IsConfigurationValid &&
                              !PathsEqual(InputPath, OutputPath);

    public bool IsConfigurationValid => TryBuildSettings(out _, out _);

    public string ConfigurationValidationText => TryBuildSettings(out _, out var reason)
        ? "参数有效；最终输出保持原图尺寸并写入真实 Alpha。"
        : reason;

    public ICommand ChooseInputCommand { get; }

    public ICommand ChooseOutputCommand { get; }

    public ICommand ProcessCommand { get; }

    public ICommand OpenOutputDirectoryCommand { get; }

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

            InputPath = normalized;
            _lastInputDirectory = Path.GetDirectoryName(normalized);
            OutputPath = string.Empty;
            OutputPreviewPath = string.Empty;
            ResultSummary = "输入图片已就绪；请选择透明 PNG 的输出位置。";
            StatusText = $"已选择：{Path.GetFileName(normalized)}";
            NotifySettingsChanged();
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"无法选择输入图片：{exception.Message}";
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
            OutputPreviewPath = string.Empty;
            _lastOutputDirectory = Path.GetDirectoryName(normalized);
            StatusText = $"输出位置：{normalized}";
            NotifySettingsChanged();
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"无法选择输出位置：{exception.Message}";
        }
    }

    private async Task ProcessAsync()
    {
        if (!TryBuildSettings(out var settings, out var reason) || !CanProcess)
        {
            StatusText = string.IsNullOrWhiteSpace(reason) ? "当前输入、输出或参数尚未就绪。" : reason;
            return;
        }

        var inputPath = InputPath;
        var outputPath = OutputPath;
        IsBusy = true;
        StatusText = $"正在使用 {settings.KeyColorHex} 剔除背景…";
        OutputPreviewPath = string.Empty;
        _workbenchLogSink?.Write(
            WorkbenchLogLevel.Information,
            LogSource,
            $"开始背景剔除：{Path.GetFileName(inputPath)} · 底色 {settings.KeyColorHex} · 输出 {outputPath}");
        try
        {
            var result = await Task.Run(() =>
                    _backgroundRemovalService.RemoveToPng(inputPath, outputPath, settings, overwrite: true))
                .ConfigureAwait(true);
            OutputPreviewPath = result.OutputPath;
            ResultSummary =
                $"{result.Width} × {result.Height} px · 剔除 {result.RemovedPixelCount:N0} 像素 · " +
                $"透明 {result.TransparentPixelCount:N0} · 半透明 {result.PartialPixelCount:N0}";
            StatusText = $"背景剔除完成：{result.OutputPath}";
            _workbenchLogSink?.Write(
                WorkbenchLogLevel.Success,
                LogSource,
                $"背景剔除完成：{Path.GetFileName(inputPath)} → {result.OutputPath} · " +
                $"{result.Width}×{result.Height} px · 剔除 {result.RemovedPixelCount:N0} 像素");
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            var errorMessage = GetOperationErrorMessage(exception);
            ResultSummary = "处理失败，输入文件保持不变。";
            StatusText = $"背景剔除失败：{errorMessage}";
            _workbenchLogSink?.Write(
                WorkbenchLogLevel.Error,
                LogSource,
                $"背景剔除失败：{Path.GetFileName(inputPath)} → {outputPath} · {errorMessage}",
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
            var directory = Path.GetDirectoryName(OutputPreviewPath);
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

    private bool TryBuildSettings(out BackgroundRemovalSettings settings, out string reason)
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
        OnPropertyChanged(nameof(ConfigurationValidationText));
        OnPropertyChanged(nameof(CanProcess));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        _chooseInputCommand.RaiseCanExecuteChanged();
        _chooseOutputCommand.RaiseCanExecuteChanged();
        _processCommand.RaiseCanExecuteChanged();
        _openOutputDirectoryCommand.RaiseCanExecuteChanged();
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
        _lastInputDirectory,
        _lastOutputDirectory);

    public GenerationBackgroundRemovalSpec CreateGenerationSpec()
    {
        if (!TryBuildSettings(out var settings, out var reason))
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

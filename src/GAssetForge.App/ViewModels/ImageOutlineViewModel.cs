using GAssetForge.App.Infrastructure;
using GAssetForge.App.Services;
using GAssetForge.Core.Imaging;
using SixLabors.ImageSharp;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;

namespace GAssetForge.App.ViewModels;

public sealed record ImageOutlineModeOption(
    ImageOutlinePlacement Placement,
    string DisplayName,
    string Description);

public sealed record ImageOutlineToolSettingsSnapshot(
    string Placement,
    int Width,
    string ColorHex,
    int AlphaThreshold,
    bool ExpandCanvas,
    string? LastInputDirectory,
    string? LastOutputDirectory);

public sealed class ImageOutlineSettingsChangedEventArgs(
    ImageOutlineToolSettingsSnapshot settings) : EventArgs
{
    public ImageOutlineToolSettingsSnapshot Settings { get; } = settings;
}

public enum ImageOutlineBatchItemStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped
}

public sealed class ImageOutlineBatchItemViewModel : ObservableObject
{
    private ImageOutlineBatchItemStatus _status;
    private string _outputPath = string.Empty;
    private string _message = "等待处理";

    public ImageOutlineBatchItemViewModel(ImageOutlineImageInfo info)
    {
        InputPath = info.InputPath;
        SourceWidth = info.Width;
        SourceHeight = info.Height;
        FormatName = info.FormatName;
    }

    public string InputPath { get; }

    public string FileName => Path.GetFileName(InputPath);

    public int SourceWidth { get; }

    public int SourceHeight { get; }

    public string FormatName { get; }

    public string SourceSizeText => $"{SourceWidth} × {SourceHeight} px";

    public bool IsProcessable => SourceWidth > 0 && SourceHeight > 0 && File.Exists(InputPath);

    public ImageOutlineBatchItemStatus Status
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
        ImageOutlineBatchItemStatus.Pending => "等待",
        ImageOutlineBatchItemStatus.Running => "处理中",
        ImageOutlineBatchItemStatus.Completed => "完成",
        ImageOutlineBatchItemStatus.Failed => "失败",
        ImageOutlineBatchItemStatus.Skipped => "跳过",
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
}

public sealed class ImageOutlineViewModel : ObservableObject
{
    public const int MaximumBatchItemCount = 256;

    private const string LogSource = "ImageOutline";

    private static readonly IReadOnlyList<ImageOutlineModeOption> SupportedModes =
    [
        new(ImageOutlinePlacement.Outside, "向外描边", "描边位于主体 Alpha 外侧；默认扩展画布，避免贴边轮廓被裁切。"),
        new(ImageOutlinePlacement.Inside, "向内描边", "描边覆盖主体边缘内部，不改变原图画布尺寸。"),
        new(ImageOutlinePlacement.Center, "中心描边", "总宽度按外侧 ceil/2、内侧 floor/2 分配，效果接近常见图像编辑器。")
    ];

    private static readonly HashSet<string> SupportedInputExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp"
    };

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly IImageOutlineService _service;
    private readonly IImageOutlineInputPicker _inputPicker;
    private readonly IImageOutlineOutputPicker _outputPicker;
    private readonly IImageProcessingPreviewCache _previewCache;
    private readonly IColorPickerService _colorPickerService;
    private readonly IWorkbenchLogSink? _workbenchLogSink;
    private readonly RelayCommand _chooseInputCommand;
    private readonly RelayCommand _chooseOutputCommand;
    private readonly RelayCommand _chooseBatchInputsCommand;
    private readonly RelayCommand _chooseBatchOutputDirectoryCommand;
    private readonly RelayCommand _removeSelectedBatchItemCommand;
    private readonly RelayCommand _clearBatchItemsCommand;
    private readonly AsyncRelayCommand _processCommand;
    private readonly AsyncRelayCommand _refreshPreviewCommand;
    private readonly AsyncRelayCommand _saveResultCommand;
    private readonly AsyncRelayCommand _processBatchCommand;
    private readonly RelayCommand _openOutputDirectoryCommand;
    private readonly RelayCommand _chooseOutlineColorCommand;
    private ImageOutlineModeOption _selectedMode;
    private int _outlineWidth;
    private string _outlineColorHex;
    private int _alphaThreshold;
    private bool _expandCanvas;
    private bool _isBatchMode;
    private ImageOutlineBatchItemViewModel? _selectedBatchItem;
    private string _batchOutputDirectory = string.Empty;
    private int _batchProcessedCount;
    private int _batchCompletedCount;
    private int _batchFailedCount;
    private string _batchCurrentFileName = string.Empty;
    private string _inputPath = string.Empty;
    private string _outputPath = string.Empty;
    private string _outputPreviewPath = string.Empty;
    private string _lastSavedOutputPath = string.Empty;
    private bool _isPreviewCurrent;
    private string _previewStatusText = "尚未刷新预览";
    private long _previewSettingsRevision;
    private ImageOutlineImageInfo? _inputInfo;
    private string? _lastInputDirectory;
    private string? _lastOutputDirectory;
    private string _statusText = "选择一张带 Alpha 的图片，或切换到批量模式一次处理多张图片。";
    private string _resultSummary = "尚未生成描边结果";
    private bool _isBusy;
    private bool _suppressSettingsChanged = true;

    public ImageOutlineViewModel(
        IImageOutlineService service,
        IImageOutlineInputPicker inputPicker,
        IImageOutlineOutputPicker outputPicker,
        string? placement = null,
        int width = 2,
        string? colorHex = null,
        int alphaThreshold = 1,
        bool expandCanvas = true,
        string? lastInputDirectory = null,
        string? lastOutputDirectory = null,
        IWorkbenchLogSink? workbenchLogSink = null,
        IImageProcessingPreviewCache? previewCache = null,
        IColorPickerService? colorPickerService = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _inputPicker = inputPicker ?? throw new ArgumentNullException(nameof(inputPicker));
        _outputPicker = outputPicker ?? throw new ArgumentNullException(nameof(outputPicker));
        _previewCache = previewCache ?? CreateFallbackPreviewCache();
        _colorPickerService = colorPickerService ?? new DesktopColorPickerService();
        _workbenchLogSink = workbenchLogSink;
        _selectedMode = ResolveMode(placement);
        _outlineWidth = Math.Clamp(width, 1, 128);
        _outlineColorHex = NormalizeInitialColor(colorHex);
        _alphaThreshold = Math.Clamp(alphaThreshold, 1, 254);
        _expandCanvas = expandCanvas;
        _lastInputDirectory = NormalizeExistingDirectory(lastInputDirectory);
        _lastOutputDirectory = NormalizeExistingDirectory(lastOutputDirectory);

        _chooseInputCommand = new RelayCommand(ChooseInput, () => !IsBusy);
        _chooseOutputCommand = new RelayCommand(ChooseOutput, () => !IsBusy && HasInput);
        _chooseBatchInputsCommand = new RelayCommand(ChooseBatchInputs, () => !IsBusy);
        _chooseBatchOutputDirectoryCommand = new RelayCommand(
            ChooseBatchOutputDirectory,
            () => !IsBusy && BatchItems.Count > 0);
        _removeSelectedBatchItemCommand = new RelayCommand(
            RemoveSelectedBatchItem,
            () => !IsBusy && SelectedBatchItem is not null);
        _clearBatchItemsCommand = new RelayCommand(
            ClearBatchItems,
            () => !IsBusy && BatchItems.Count > 0);
        _refreshPreviewCommand = new AsyncRelayCommand(RefreshPreviewAsync, () => CanRefreshPreview);
        _saveResultCommand = new AsyncRelayCommand(SaveResultAsync, () => CanSaveResult);
        // Legacy integrations expect one command to both process and write the configured output.
        // The interactive UI uses the two explicit commands below.
        _processCommand = new AsyncRelayCommand(ProcessLegacyAsync, () => CanProcess);
        _processBatchCommand = new AsyncRelayCommand(ProcessBatchAsync, () => CanProcessBatch);
        _openOutputDirectoryCommand = new RelayCommand(OpenOutputDirectory, () => HasSavedOutput);
        _chooseOutlineColorCommand = new RelayCommand(ChooseOutlineColor, () => !IsBusy);

        ChooseInputCommand = _chooseInputCommand;
        ChooseOutputCommand = _chooseOutputCommand;
        ChooseBatchInputsCommand = _chooseBatchInputsCommand;
        ChooseBatchOutputDirectoryCommand = _chooseBatchOutputDirectoryCommand;
        RemoveSelectedBatchItemCommand = _removeSelectedBatchItemCommand;
        ClearBatchItemsCommand = _clearBatchItemsCommand;
        ProcessCommand = _processCommand;
        RefreshPreviewCommand = _refreshPreviewCommand;
        SaveResultCommand = _saveResultCommand;
        ProcessBatchCommand = _processBatchCommand;
        OpenOutputDirectoryCommand = _openOutputDirectoryCommand;
        ChooseOutlineColorCommand = _chooseOutlineColorCommand;
        _suppressSettingsChanged = false;
    }

    public event EventHandler<ImageOutlineSettingsChangedEventArgs>? SettingsChanged;

    public IReadOnlyList<ImageOutlineModeOption> ModeOptions => SupportedModes;

    public ObservableCollection<ImageOutlineBatchItemViewModel> BatchItems { get; } = [];

    public ImageOutlineModeOption SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (value is null || !SetProperty(ref _selectedMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ModeDescription));
            OnPropertyChanged(nameof(CanvasBehaviorText));
            MarkPreviewStale();
            RefreshValidationAndCommands();
            NotifySettingsChanged();
        }
    }

    public string ModeDescription => SelectedMode.Description;

    public int OutlineWidth
    {
        get => _outlineWidth;
        set
        {
            var normalized = Math.Clamp(value, 1, 128);
            if (SetProperty(ref _outlineWidth, normalized))
            {
                OnPropertyChanged(nameof(CanvasBehaviorText));
                MarkPreviewStale();
                RefreshValidationAndCommands();
                NotifySettingsChanged();
            }
        }
    }

    public string OutlineColorHex
    {
        get => _outlineColorHex;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref _outlineColorHex, value))
            {
                OnPropertyChanged(nameof(EffectiveColorHex));
                MarkPreviewStale();
                RefreshValidationAndCommands();
                NotifySettingsChanged();
            }
        }
    }

    public string EffectiveColorHex
    {
        get
        {
            try
            {
                return BackgroundKeyColor.NormalizeHex(OutlineColorHex);
            }
            catch (ArgumentException)
            {
                return "#00000000";
            }
        }
    }

    public int AlphaThreshold
    {
        get => _alphaThreshold;
        set
        {
            var normalized = Math.Clamp(value, 1, 254);
            if (SetProperty(ref _alphaThreshold, normalized))
            {
                MarkPreviewStale();
                RefreshValidationAndCommands();
                NotifySettingsChanged();
            }
        }
    }

    public bool ExpandCanvas
    {
        get => _expandCanvas;
        set
        {
            if (SetProperty(ref _expandCanvas, value))
            {
                OnPropertyChanged(nameof(CanvasBehaviorText));
                MarkPreviewStale();
                RefreshValidationAndCommands();
                NotifySettingsChanged();
            }
        }
    }

    public string CanvasBehaviorText
    {
        get
        {
            var outerWidth = SelectedMode.Placement switch
            {
                ImageOutlinePlacement.Outside => OutlineWidth,
                ImageOutlinePlacement.Center => (OutlineWidth + 1) / 2,
                _ => 0
            };
            return outerWidth == 0
                ? "向内描边保持原画布尺寸。"
                : ExpandCanvas
                    ? $"输出四周扩展 {outerWidth}px，完整保留外侧轮廓。"
                    : "保持原画布尺寸；贴近边缘的外侧轮廓可能被裁切。";
        }
    }

    public bool IsBatchMode
    {
        get => _isBatchMode;
        set
        {
            if (SetProperty(ref _isBatchMode, value))
            {
                OnPropertyChanged(nameof(IsSingleMode));
                StatusText = value
                    ? "批量模式：选择多张图片和一个输出目录。"
                    : "单图模式：选择输入图片与输出 PNG。";
                RaiseCommandStates();
            }
        }
    }

    public bool IsSingleMode => !IsBatchMode;

    public ImageOutlineBatchItemViewModel? SelectedBatchItem
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
                OnPropertyChanged(nameof(HasBatchOutputDirectory));
                OnPropertyChanged(nameof(CanProcessBatch));
                RaiseCommandStates();
            }
        }
    }

    public bool HasBatchOutputDirectory => Directory.Exists(BatchOutputDirectory);

    public int BatchProcessedCount
    {
        get => _batchProcessedCount;
        private set
        {
            if (SetProperty(ref _batchProcessedCount, value))
            {
                OnPropertyChanged(nameof(BatchProgress));
                OnPropertyChanged(nameof(BatchProgressText));
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
                OnPropertyChanged(nameof(BatchProgressText));
            }
        }
    }

    public string BatchCurrentFileName
    {
        get => _batchCurrentFileName;
        private set => SetProperty(ref _batchCurrentFileName, value);
    }

    public double BatchProgress => BatchItems.Count == 0
        ? 0d
        : Math.Clamp(BatchProcessedCount * 100d / BatchItems.Count, 0d, 100d);

    public string BatchProgressText => BatchItems.Count == 0
        ? "尚未加入批处理图片"
        : $"{BatchProcessedCount}/{BatchItems.Count} · 完成 {BatchCompletedCount} · 失败 {BatchFailedCount}";

    public string InputPath
    {
        get => _inputPath;
        private set
        {
            if (SetProperty(ref _inputPath, value))
            {
                OnPropertyChanged(nameof(HasInput));
                OnPropertyChanged(nameof(InputFileName));
                RefreshValidationAndCommands();
            }
        }
    }

    public string InputFileName => HasInput ? Path.GetFileName(InputPath) : "尚未选择输入图片";

    public bool HasInput => File.Exists(InputPath);

    public string InputSizeText => _inputInfo is null
        ? "—"
        : $"{_inputInfo.Width} × {_inputInfo.Height} px · {_inputInfo.FormatName}";

    public string OutputPath
    {
        get => _outputPath;
        private set
        {
            if (SetProperty(ref _outputPath, value))
            {
                OnPropertyChanged(nameof(OutputFileName));
                RefreshValidationAndCommands();
            }
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
            if (SetProperty(ref _outputPreviewPath, value))
            {
                OnPropertyChanged(nameof(HasOutputPreview));
                OnPropertyChanged(nameof(IsPreviewStale));
                OnPropertyChanged(nameof(CanSaveResult));
                RaiseCommandStates();
            }
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
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanProcess));
                OnPropertyChanged(nameof(CanRefreshPreview));
                OnPropertyChanged(nameof(CanSaveResult));
                OnPropertyChanged(nameof(CanProcessBatch));
                RaiseCommandStates();
            }
        }
    }

    public bool CanRefreshPreview => !IsBusy && IsSingleMode && HasInput && IsConfigurationValid;

    public bool CanSaveResult => !IsBusy && IsSingleMode && IsPreviewCurrent && HasOutputPreview &&
                                 (string.IsNullOrWhiteSpace(OutputPath) ||
                                  !PathsEqual(InputPath, OutputPath));

    // The legacy command still requires a configured destination because it refreshes and publishes
    // in one invocation. The interactive UI uses the output-independent refresh command instead.
    public bool CanProcess => CanRefreshPreview &&
                              !string.IsNullOrWhiteSpace(OutputPath) &&
                              !PathsEqual(InputPath, OutputPath);

    public bool CanProcessBatch => !IsBusy && IsBatchMode &&
                                   BatchItems.Any(item => item.IsProcessable) &&
                                   HasBatchOutputDirectory &&
                                   IsConfigurationValid;

    public bool IsConfigurationValid => TryBuildSettings(out _, out _);

    public string ConfigurationValidationText => TryBuildSettings(out _, out var reason)
        ? "参数有效；描边由本地 Alpha 蒙版计算并原子写入 PNG。"
        : reason;

    public ICommand ChooseInputCommand { get; }

    public ICommand ChooseOutputCommand { get; }

    public ICommand ChooseBatchInputsCommand { get; }

    public ICommand ChooseBatchOutputDirectoryCommand { get; }

    public ICommand RemoveSelectedBatchItemCommand { get; }

    public ICommand ClearBatchItemsCommand { get; }

    public ICommand ProcessCommand { get; }

    public ICommand RefreshPreviewCommand { get; }

    public ICommand SaveResultCommand { get; }

    public ICommand ProcessBatchCommand { get; }

    public ICommand OpenOutputDirectoryCommand { get; }

    public ICommand ChooseOutlineColorCommand { get; }

    public ImageOutlineToolSettingsSnapshot CaptureSettings() => new(
        SelectedMode.Placement.ToString(),
        OutlineWidth,
        OutlineColorHex,
        AlphaThreshold,
        ExpandCanvas,
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

            var normalized = NormalizeSupportedInput(selected);
            _inputInfo = _service.Inspect(normalized);
            ClearPreview();
            InputPath = normalized;
            OnPropertyChanged(nameof(InputSizeText));
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
            StatusText = $"无法选择输入图片：{GetOperationErrorMessage(exception)}";
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
            var suggestedName = $"{Path.GetFileNameWithoutExtension(InputPath)}_outline.png";
            var selected = _outputPicker.SelectOutputFile(initialDirectory, suggestedName);
            if (string.IsNullOrWhiteSpace(selected))
            {
                return;
            }

            var normalized = EnsurePngPath(selected);
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
            var selected = _inputPicker.SelectInputFiles(_lastInputDirectory);
            if (selected.Count == 0)
            {
                return;
            }

            var knownPaths = BatchItems.Select(item => item.InputPath).ToHashSet(PathComparer);
            var added = 0;
            var rejected = 0;
            foreach (var path in selected)
            {
                if (BatchItems.Count >= MaximumBatchItemCount)
                {
                    break;
                }

                try
                {
                    var normalized = NormalizeSupportedInput(path);
                    if (!knownPaths.Add(normalized))
                    {
                        continue;
                    }

                    BatchItems.Add(new ImageOutlineBatchItemViewModel(_service.Inspect(normalized)));
                    _lastInputDirectory = Path.GetDirectoryName(normalized);
                    added++;
                }
                catch (Exception exception) when (IsExpectedOperationException(exception))
                {
                    rejected++;
                    _workbenchLogSink?.Write(
                        WorkbenchLogLevel.Warning,
                        LogSource,
                        $"跳过无法加入描边批次的图片：{path} · {GetOperationErrorMessage(exception)}");
                }
            }

            ResetBatchProgress();
            OnPropertyChanged(nameof(BatchProgress));
            OnPropertyChanged(nameof(BatchProgressText));
            OnPropertyChanged(nameof(CanProcessBatch));
            RaiseCommandStates();
            StatusText = $"批处理队列新增 {added} 张图片" +
                         (rejected > 0 ? $"，跳过 {rejected} 张无效图片。" : "。") +
                         (BatchItems.Count >= MaximumBatchItemCount ? $" 队列上限为 {MaximumBatchItemCount} 张。" : string.Empty);
            NotifySettingsChanged();
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"无法选择批处理图片：{exception.Message}";
        }
    }

    private void ChooseBatchOutputDirectory()
    {
        try
        {
            var selected = _outputPicker.SelectOutputDirectory(_lastOutputDirectory ?? _lastInputDirectory);
            if (string.IsNullOrWhiteSpace(selected))
            {
                return;
            }

            var normalized = Path.GetFullPath(selected);
            Directory.CreateDirectory(normalized);
            BatchOutputDirectory = normalized;
            _lastOutputDirectory = normalized;
            StatusText = $"批量输出目录：{normalized}";
            NotifySettingsChanged();
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"无法选择批量输出目录：{exception.Message}";
        }
    }

    private void RemoveSelectedBatchItem()
    {
        if (SelectedBatchItem is null)
        {
            return;
        }

        BatchItems.Remove(SelectedBatchItem);
        SelectedBatchItem = null;
        ResetBatchProgress();
        OnPropertyChanged(nameof(BatchProgress));
        OnPropertyChanged(nameof(BatchProgressText));
        OnPropertyChanged(nameof(CanProcessBatch));
        RaiseCommandStates();
    }

    private void ClearBatchItems()
    {
        BatchItems.Clear();
        SelectedBatchItem = null;
        ResetBatchProgress();
        OnPropertyChanged(nameof(BatchProgress));
        OnPropertyChanged(nameof(BatchProgressText));
        OnPropertyChanged(nameof(CanProcessBatch));
        RaiseCommandStates();
        StatusText = "已清空图片描边批处理队列。";
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
        PreviewStatusText = "正在刷新缓存预览…";
        StatusText = $"正在添加 {SelectedMode.DisplayName}并刷新预览…";
        _workbenchLogSink?.Write(
            WorkbenchLogLevel.Information,
            LogSource,
            $"开始刷新图片描边预览：{Path.GetFileName(inputPath)} · {SelectedMode.DisplayName} {OutlineWidth}px {settings.ColorHex}");
        try
        {
            reservedPreviewPath = _previewCache.ReservePreviewPath("image-outline");
            _previewCache.PinPreview(reservedPreviewPath);
            var result = await Task.Run(() =>
                    _service.ApplyOutlineToPng(inputPath, reservedPreviewPath, settings, overwrite: false))
                .ConfigureAwait(true);
            var renderedPreviewPath = Path.GetFullPath(result.OutputPath);
            if (!File.Exists(renderedPreviewPath) || !_previewCache.IsManagedPreviewPath(renderedPreviewPath))
            {
                throw new IOException("描边服务未生成有效的受管缓存 PNG。");
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
                StatusText = "图片描边预览已丢弃，请针对当前输入重新刷新。";
                return;
            }

            OutputPreviewPath = renderedPreviewPath;
            var remainsCurrent = settingsRevision == _previewSettingsRevision;
            IsPreviewCurrent = remainsCurrent;
            PreviewStatusText = remainsCurrent
                ? "预览已刷新，可设置输出位置并保存结果"
                : "刷新期间参数发生变化；结果仅供对比，请再次刷新";
            ResultSummary =
                $"{result.OutputWidth} × {result.OutputHeight} px · 描边像素 {result.OutlinedPixelCount:N0} · " +
                $"画布扩展 {result.CanvasExpansion}px";
            StatusText = remainsCurrent
                ? "图片描边预览已刷新；尚未写入最终输出位置。"
                : "图片描边预览已生成，但参数已变化，需要再次刷新。";
            ReleaseSupersededPreview(previousPreviewPath, renderedPreviewPath);
            _workbenchLogSink?.Write(
                WorkbenchLogLevel.Success,
                LogSource,
                $"图片描边缓存预览完成：{Path.GetFileName(inputPath)} · " +
                $"{result.OutputWidth}×{result.OutputHeight} px · 描边 {result.OutlinedPixelCount:N0} 像素");
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            ReleaseSupersededPreview(reservedPreviewPath, previousPreviewPath);
            PreviewStatusText = HasOutputPreview
                ? "预览刷新失败；保留上一次结果但已标记为过期，不能保存"
                : "预览刷新失败，尚无可用结果";
            if (!HasOutputPreview)
            {
                ResultSummary = "处理失败，输入文件保持不变。";
            }

            StatusText = $"图片描边预览失败：{GetOperationErrorMessage(exception)}";
            _workbenchLogSink?.Write(
                WorkbenchLogLevel.Error,
                LogSource,
                $"图片描边预览失败：{Path.GetFileName(inputPath)} · {GetOperationErrorMessage(exception)}",
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
            StatusText = $"图片描边结果已保存：{publishedPath}";
            _workbenchLogSink?.Write(
                WorkbenchLogLevel.Success,
                LogSource,
                $"图片描边预览已原样发布：{publishedPath}");
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"无法保存图片描边结果：{GetOperationErrorMessage(exception)}";
            _workbenchLogSink?.Write(
                WorkbenchLogLevel.Error,
                LogSource,
                $"图片描边保存失败：{outputPath} · {GetOperationErrorMessage(exception)}",
                exception: exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ProcessBatchAsync()
    {
        if (!TryBuildSettings(out var settings, out var reason) || !CanProcessBatch)
        {
            StatusText = string.IsNullOrWhiteSpace(reason) ? "批处理图片或输出目录尚未就绪。" : reason;
            return;
        }

        var items = BatchItems.ToArray();
        var outputDirectory = Path.GetFullPath(BatchOutputDirectory);
        var reservedPaths = new HashSet<string>(PathComparer);
        ClearPreview();
        ResetBatchProgress();
        foreach (var item in items)
        {
            item.Status = ImageOutlineBatchItemStatus.Pending;
            item.OutputPath = string.Empty;
            item.Message = item.IsProcessable ? "等待处理" : "源文件不可用";
        }

        IsBusy = true;
        StatusText = $"正在批量描边 {items.Length} 张图片…";
        _workbenchLogSink?.Write(
            WorkbenchLogLevel.Information,
            LogSource,
            $"开始批量图片描边：{items.Length} 张 · {SelectedMode.DisplayName} {OutlineWidth}px {settings.ColorHex} · 输出 {outputDirectory}");
        try
        {
            foreach (var item in items)
            {
                BatchCurrentFileName = item.FileName;
                if (!item.IsProcessable)
                {
                    item.Status = ImageOutlineBatchItemStatus.Skipped;
                    item.Message = "源文件不存在或尺寸无效";
                    BatchFailedCount++;
                    BatchProcessedCount++;
                    continue;
                }

                item.Status = ImageOutlineBatchItemStatus.Running;
                item.Message = "正在计算 Alpha 轮廓";
                try
                {
                    var outputPath = BuildUniqueBatchOutputPath(
                        outputDirectory,
                        item.InputPath,
                        settings,
                        reservedPaths);
                    var result = await Task.Run(() =>
                            _service.ApplyOutlineToPng(item.InputPath, outputPath, settings, overwrite: false))
                        .ConfigureAwait(true);
                    item.OutputPath = result.OutputPath;
                    item.Status = ImageOutlineBatchItemStatus.Completed;
                    item.Message = $"{result.OutputWidth}×{result.OutputHeight} · {result.OutlinedPixelCount:N0} 描边像素";
                    BatchCompletedCount++;
                    OutputPreviewPath = result.OutputPath;
                    LastSavedOutputPath = result.OutputPath;
                    IsPreviewCurrent = false;
                    PreviewStatusText = "批量模式显示最新完成的正式输出";
                    ResultSummary = $"最新完成：{item.FileName} → {Path.GetFileName(result.OutputPath)}";
                }
                catch (Exception exception) when (IsExpectedOperationException(exception))
                {
                    item.Status = ImageOutlineBatchItemStatus.Failed;
                    item.Message = GetOperationErrorMessage(exception);
                    BatchFailedCount++;
                    _workbenchLogSink?.Write(
                        WorkbenchLogLevel.Error,
                        LogSource,
                        $"批量描边单项失败：{item.InputPath} · {item.Message}",
                        exception: exception);
                }
                finally
                {
                    BatchProcessedCount++;
                }
            }

            StatusText = BatchFailedCount == 0
                ? $"批量描边完成：{BatchCompletedCount} 张图片已写入 {outputDirectory}"
                : $"批量描边结束：完成 {BatchCompletedCount}，失败或跳过 {BatchFailedCount}。";
            _workbenchLogSink?.Write(
                BatchFailedCount == 0 ? WorkbenchLogLevel.Success : WorkbenchLogLevel.Warning,
                LogSource,
                StatusText);
        }
        finally
        {
            BatchCurrentFileName = string.Empty;
            IsBusy = false;
        }
    }

    private bool TryBuildSettings(out ImageOutlineSettings settings, out string reason)
    {
        settings = new ImageOutlineSettings();
        reason = string.Empty;
        string color;
        try
        {
            color = BackgroundKeyColor.NormalizeHex(OutlineColorHex);
        }
        catch (ArgumentException exception)
        {
            reason = exception.Message;
            return false;
        }

        if (OutlineWidth is < 1 or > 128)
        {
            reason = "描边宽度必须位于 1 到 128 像素之间。";
            return false;
        }

        if (AlphaThreshold is < 1 or > 254)
        {
            reason = "主体 Alpha 阈值必须位于 1 到 254 之间。";
            return false;
        }

        settings = new ImageOutlineSettings
        {
            Placement = SelectedMode.Placement,
            Width = OutlineWidth,
            ColorHex = color,
            AlphaThreshold = AlphaThreshold,
            ExpandCanvas = ExpandCanvas
        };
        return true;
    }

    private void RefreshValidationAndCommands()
    {
        OnPropertyChanged(nameof(IsConfigurationValid));
        OnPropertyChanged(nameof(ConfigurationValidationText));
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
        _chooseBatchInputsCommand.RaiseCanExecuteChanged();
        _chooseBatchOutputDirectoryCommand.RaiseCanExecuteChanged();
        _removeSelectedBatchItemCommand.RaiseCanExecuteChanged();
        _clearBatchItemsCommand.RaiseCanExecuteChanged();
        _processCommand.RaiseCanExecuteChanged();
        _refreshPreviewCommand.RaiseCanExecuteChanged();
        _saveResultCommand.RaiseCanExecuteChanged();
        _processBatchCommand.RaiseCanExecuteChanged();
        _openOutputDirectoryCommand.RaiseCanExecuteChanged();
        _chooseOutlineColorCommand.RaiseCanExecuteChanged();
    }

    private void NotifySettingsChanged()
    {
        if (!_suppressSettingsChanged)
        {
            SettingsChanged?.Invoke(this, new ImageOutlineSettingsChangedEventArgs(CaptureSettings()));
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

    private void ChooseOutlineColor()
    {
        var selected = _colorPickerService.PickColor(OutlineColorHex, "选择描边颜色");
        if (selected is null)
        {
            return;
        }

        OutlineColorHex = selected.Hex;
        StatusText = $"描边颜色已设置为 {selected.Hex}；请刷新预览。";
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
                $"无法清理过期的图片描边预览缓存：{exception.Message}");
        }
    }

    private void ResetBatchProgress()
    {
        BatchProcessedCount = 0;
        BatchCompletedCount = 0;
        BatchFailedCount = 0;
        BatchCurrentFileName = string.Empty;
    }

    private static string BuildUniqueBatchOutputPath(
        string outputDirectory,
        string inputPath,
        ImageOutlineSettings settings,
        ISet<string> reservedPaths)
    {
        var mode = settings.Placement.ToString().ToLowerInvariant();
        var stem = $"{Path.GetFileNameWithoutExtension(inputPath)}_{mode}_{settings.Width}px_outline";
        for (var suffix = 0; suffix < 10_000; suffix++)
        {
            var fileName = suffix == 0 ? $"{stem}.png" : $"{stem}_{suffix + 1:000}.png";
            var candidate = Path.GetFullPath(Path.Combine(outputDirectory, fileName));
            if (!File.Exists(candidate) && reservedPaths.Add(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"无法为 {Path.GetFileName(inputPath)} 分配唯一的批量输出文件名。");
    }

    private static string NormalizeSupportedInput(string path)
    {
        var normalized = Path.GetFullPath(path);
        if (!File.Exists(normalized) || !SupportedInputExtensions.Contains(Path.GetExtension(normalized)))
        {
            throw new ArgumentException("选择的文件不是受支持的图片。", nameof(path));
        }

        return normalized;
    }

    private static string EnsurePngPath(string path)
    {
        var normalized = Path.GetFullPath(path);
        return string.Equals(Path.GetExtension(normalized), ".png", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : Path.ChangeExtension(normalized, ".png");
    }

    private static ImageOutlineModeOption ResolveMode(string? placement)
    {
        if (Enum.TryParse<ImageOutlinePlacement>(placement, ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed))
        {
            return SupportedModes.First(option => option.Placement == parsed);
        }

        return SupportedModes[0];
    }

    private static string NormalizeInitialColor(string? colorHex)
    {
        try
        {
            return BackgroundKeyColor.NormalizeHex(colorHex ?? "#000000");
        }
        catch (ArgumentException)
        {
            return "#000000";
        }
    }

    private static IImageProcessingPreviewCache CreateFallbackPreviewCache() =>
        new ImageProcessingPreviewCache(
            Path.Combine(
                Path.GetTempPath(),
                "GAssetForge",
                "Workspace",
                ImageProcessingPreviewCache.DefaultDirectoryName));

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
        try
        {
            return !string.IsNullOrWhiteSpace(left) &&
                   !string.IsNullOrWhiteSpace(right) &&
                   PathComparer.Equals(Path.GetFullPath(left), Path.GetFullPath(right));
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

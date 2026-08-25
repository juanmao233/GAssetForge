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
    private readonly IWorkbenchLogSink? _workbenchLogSink;
    private readonly RelayCommand _chooseInputCommand;
    private readonly RelayCommand _chooseOutputCommand;
    private readonly RelayCommand _chooseBatchInputsCommand;
    private readonly RelayCommand _chooseBatchOutputDirectoryCommand;
    private readonly RelayCommand _removeSelectedBatchItemCommand;
    private readonly RelayCommand _clearBatchItemsCommand;
    private readonly AsyncRelayCommand _processCommand;
    private readonly AsyncRelayCommand _processBatchCommand;
    private readonly RelayCommand _openOutputDirectoryCommand;
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
        IWorkbenchLogSink? workbenchLogSink = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _inputPicker = inputPicker ?? throw new ArgumentNullException(nameof(inputPicker));
        _outputPicker = outputPicker ?? throw new ArgumentNullException(nameof(outputPicker));
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
        _processCommand = new AsyncRelayCommand(ProcessAsync, () => CanProcess);
        _processBatchCommand = new AsyncRelayCommand(ProcessBatchAsync, () => CanProcessBatch);
        _openOutputDirectoryCommand = new RelayCommand(OpenOutputDirectory, () => HasOutputPreview);

        ChooseInputCommand = _chooseInputCommand;
        ChooseOutputCommand = _chooseOutputCommand;
        ChooseBatchInputsCommand = _chooseBatchInputsCommand;
        ChooseBatchOutputDirectoryCommand = _chooseBatchOutputDirectoryCommand;
        RemoveSelectedBatchItemCommand = _removeSelectedBatchItemCommand;
        ClearBatchItemsCommand = _clearBatchItemsCommand;
        ProcessCommand = _processCommand;
        ProcessBatchCommand = _processBatchCommand;
        OpenOutputDirectoryCommand = _openOutputDirectoryCommand;
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
                _openOutputDirectoryCommand.RaiseCanExecuteChanged();
            }
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
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanProcess));
                OnPropertyChanged(nameof(CanProcessBatch));
                RaiseCommandStates();
            }
        }
    }

    public bool CanProcess => !IsBusy && IsSingleMode && HasInput &&
                              !string.IsNullOrWhiteSpace(OutputPath) &&
                              IsConfigurationValid &&
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

    public ICommand ProcessBatchCommand { get; }

    public ICommand OpenOutputDirectoryCommand { get; }

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
            InputPath = normalized;
            OnPropertyChanged(nameof(InputSizeText));
            _lastInputDirectory = Path.GetDirectoryName(normalized);
            OutputPath = string.Empty;
            OutputPreviewPath = string.Empty;
            ResultSummary = "输入图片已就绪；请选择描边 PNG 的输出位置。";
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
        OutputPreviewPath = string.Empty;
        StatusText = $"正在添加 {SelectedMode.DisplayName}…";
        _workbenchLogSink?.Write(
            WorkbenchLogLevel.Information,
            LogSource,
            $"开始图片描边：{Path.GetFileName(inputPath)} · {SelectedMode.DisplayName} {OutlineWidth}px {settings.ColorHex} · 输出 {outputPath}");
        try
        {
            var result = await Task.Run(() =>
                    _service.ApplyOutlineToPng(inputPath, outputPath, settings, overwrite: true))
                .ConfigureAwait(true);
            OutputPreviewPath = result.OutputPath;
            ResultSummary =
                $"{result.OutputWidth} × {result.OutputHeight} px · 描边像素 {result.OutlinedPixelCount:N0} · " +
                $"画布扩展 {result.CanvasExpansion}px";
            StatusText = $"图片描边完成：{result.OutputPath}";
            _workbenchLogSink?.Write(
                WorkbenchLogLevel.Success,
                LogSource,
                $"图片描边完成：{Path.GetFileName(inputPath)} → {result.OutputPath} · " +
                $"{result.OutputWidth}×{result.OutputHeight} px · 描边 {result.OutlinedPixelCount:N0} 像素");
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            ResultSummary = "处理失败，输入文件保持不变。";
            StatusText = $"图片描边失败：{GetOperationErrorMessage(exception)}";
            _workbenchLogSink?.Write(
                WorkbenchLogLevel.Error,
                LogSource,
                $"图片描边失败：{Path.GetFileName(inputPath)} → {outputPath} · {GetOperationErrorMessage(exception)}",
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
        _processBatchCommand.RaiseCanExecuteChanged();
        _openOutputDirectoryCommand.RaiseCanExecuteChanged();
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

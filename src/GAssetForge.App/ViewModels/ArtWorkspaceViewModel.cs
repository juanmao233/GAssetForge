using GAssetForge.App.Infrastructure;
using GAssetForge.App.Services;
using GAssetForge.Core.ArtWorkspaces;
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

public sealed class ArtWorkspaceViewModel : ObservableObject, IDisposable
{
    public const double MinimumZoom = 0.1d;
    public const double MaximumZoom = 4d;
    private static readonly TimeSpan AutoSaveDelay = TimeSpan.FromMilliseconds(650);
    private const int MaximumAutoSaveRetries = 3;

    private readonly JsonArtWorkspaceRepository _repository;
    private readonly IArtWorkspaceImagePicker _imagePicker;
    private readonly WorkbenchConsoleService _console;
    private readonly DispatcherTimer _saveTimer;
    private readonly AsyncRelayCommand _importImagesCommand;
    private readonly RelayCommand _submitInstructionCommand;
    private readonly RelayCommand _removeSelectedImageCommand;
    private readonly RelayCommand _bringSelectedToFrontCommand;
    private readonly RelayCommand _cancelImportCommand;
    private readonly RelayCommand<ArtWorkspaceImageItemViewModel> _selectImageCommand;
    private readonly List<ArtWorkspaceManagedAsset> _assets = [];
    private ArtWorkspaceDocument _document = ArtWorkspaceDocumentFactory.Create();
    private string? _projectRoot;
    private string _currentFilePath = string.Empty;
    private string _statusMessage = "美术工作区正在初始化";
    private string _instructionDraft = string.Empty;
    private string? _lastImportDirectory;
    private ArtWorkspaceImageItemViewModel? _selectedImage;
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
    private string _outputRelativeDirectory = "Assets/Generated/ArtWorkspace";
    private bool _hasPendingSave;
    private int _autoSaveRetryCount;
    private bool _suppressAutoSave;
    private bool _isImporting;
    private CancellationTokenSource? _importCancellation;
    private bool _disposed;

    public ArtWorkspaceViewModel(
        JsonArtWorkspaceRepository repository,
        IArtWorkspaceImagePicker imagePicker,
        WorkbenchConsoleService console)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _imagePicker = imagePicker ?? throw new ArgumentNullException(nameof(imagePicker));
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _importImagesCommand = new AsyncRelayCommand(ImportImagesAsync, () => !IsImporting);
        ImportImagesCommand = _importImagesCommand;
        _cancelImportCommand = new RelayCommand(CancelImport, () => IsImporting);
        CancelImportCommand = _cancelImportCommand;
        _submitInstructionCommand = new RelayCommand(SubmitInstruction, CanSubmitInstruction);
        SubmitInstructionCommand = _submitInstructionCommand;
        SaveCommand = new RelayCommand(() => SaveNow());
        ResetViewportCommand = new RelayCommand(ResetViewportToOrigin);
        FocusOutputFrameCommand = new RelayCommand(FocusOutputFrame);
        _removeSelectedImageCommand = new RelayCommand(RemoveSelectedImage, () => SelectedImage is not null);
        RemoveSelectedImageCommand = _removeSelectedImageCommand;
        _bringSelectedToFrontCommand = new RelayCommand(BringSelectedToFront, () => SelectedImage is not null);
        BringSelectedToFrontCommand = _bringSelectedToFrontCommand;
        _selectImageCommand = new RelayCommand<ArtWorkspaceImageItemViewModel>(SelectImage);
        SelectImageCommand = _selectImageCommand;
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = AutoSaveDelay
        };
        _saveTimer.Tick += OnSaveTimerTick;
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

    public string InstructionDraft
    {
        get => _instructionDraft;
        set
        {
            if (SetProperty(ref _instructionDraft, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanSubmitInstructionText));
                _submitInstructionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string CanSubmitInstructionText => IsImporting
        ? "参考图片正在导入；完成或取消后即可提交创作指令。"
        : Conversation.Count > ArtWorkspaceValidator.MaximumMessages - 2
            ? "对话记录已达到上限，请先归档或创建新的工作区会话。"
            : string.IsNullOrWhiteSpace(InstructionDraft)
                ? "输入希望创建的内容、构图、风格或修改要求。"
                : "当前指令会保存为工作区创作意图；AI 执行链将在后续接入。";

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
        set => SetOutputValue(ref _outputFrameWidth, value, 16d, 32768d, nameof(OutputFrameWidth));
    }

    public double OutputFrameHeight
    {
        get => _outputFrameHeight;
        set => SetOutputValue(ref _outputFrameHeight, value, 16d, 32768d, nameof(OutputFrameHeight));
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
                ? "Assets/Generated/ArtWorkspace"
                : value.Trim().Replace('\\', '/').TrimEnd('/');
            if (!IsSafeAssetsDirectory(normalized))
            {
                _statusMessage = "输出目录必须是 Assets 根目录内且不含 . 或 .. 跳转的相对路径";
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
            }

            _selectedImage = value;
            if (_selectedImage is not null)
            {
                _selectedImage.IsSelected = true;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedImage));
            _removeSelectedImageCommand.RaiseCanExecuteChanged();
            _bringSelectedToFrontCommand.RaiseCanExecuteChanged();
            MarkDirty();
        }
    }

    public bool HasSelectedImage => SelectedImage is not null;

    public ICommand ImportImagesCommand { get; }

    public ICommand CancelImportCommand { get; }

    public ICommand SubmitInstructionCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand ResetViewportCommand { get; }

    public ICommand FocusOutputFrameCommand { get; }

    public ICommand RemoveSelectedImageCommand { get; }

    public ICommand BringSelectedToFrontCommand { get; }

    public ICommand SelectImageCommand { get; }

    public bool Activate(string? projectRoot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsImporting)
        {
            _statusMessage = "参考图片仍在导入，请等待完成或先取消导入后再切换项目";
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
        if (IsImporting)
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
        if (IsImporting)
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
        OnPropertyChanged(nameof(StatusText));
        return added;
    }

    public void SelectImage(ArtWorkspaceImageItemViewModel? image) =>
        SelectedImage = image is not null && Images.Contains(image) ? image : null;

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
        Conversation.Count <= ArtWorkspaceValidator.MaximumMessages - 2 &&
        !string.IsNullOrWhiteSpace(InstructionDraft) &&
        InstructionDraft.Trim().Length <= 16000;

    private void SubmitInstruction()
    {
        var instruction = InstructionDraft.Trim();
        if (instruction.Length == 0 || instruction.Length > 16000)
        {
            return;
        }

        var messages = CreateInstructionMessages(instruction, DateTimeOffset.UtcNow);
        try
        {
            var candidate = CaptureDocument();
            candidate.Conversation.AddRange(messages.Select(CloneMessage));
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

        InstructionDraft = string.Empty;
        _statusMessage = "创作指令已记录，正在保存工作区；等待生成适配器接入";
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
        var nextSelectedImage = nextImages.FirstOrDefault(image =>
            string.Equals(image.Id, nextDocument.SelectedImageId, StringComparison.Ordinal));

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
            _outputRelativeDirectory = nextDocument.OutputFrame.OutputRelativeDirectory;
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
            if (_selectedImage is not null)
            {
                _selectedImage.IsSelected = true;
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
            SelectedImageId = SelectedImage?.Id
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

    private void OnImageChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, SelectedImage))
        {
            OnPropertyChanged(nameof(SelectedImage));
        }

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
        OnPropertyChanged(nameof(SelectedImage));
        OnPropertyChanged(nameof(HasSelectedImage));
        OnPropertyChanged(nameof(ImageCountText));
        OnPropertyChanged(nameof(ProjectContextText));
        OnPropertyChanged(nameof(CanSubmitInstructionText));
        NotifySummaryChanged();
        _submitInstructionCommand.RaiseCanExecuteChanged();
        _removeSelectedImageCommand.RaiseCanExecuteChanged();
        _bringSelectedToFrontCommand.RaiseCanExecuteChanged();
    }

    private void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PersistenceText));
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
        DateTimeOffset createdAtUtc) =>
    [
        new ArtWorkspaceConversationMessage
        {
            Role = ArtWorkspaceMessageRole.User,
            Text = instruction,
            CreatedAtUtc = createdAtUtc,
            State = "captured"
        },
        new ArtWorkspaceConversationMessage
        {
            Role = ArtWorkspaceMessageRole.Workbench,
            Text = "创作意图已记录到当前工作区。当前版本尚未启动 AI 生成；后续接入时会从这里冻结提示和输出框。",
            CreatedAtUtc = createdAtUtc,
            State = "awaiting-generation-adapter"
        }
    ];

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

    private static ArtWorkspaceConversationMessage CloneMessage(ArtWorkspaceConversationMessage message) => new()
    {
        Id = message.Id,
        Role = message.Role,
        Text = message.Text,
        CreatedAtUtc = message.CreatedAtUtc,
        State = message.State
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
    }

    public event EventHandler? Changed;

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
        set => SetChanged(ref _width, NormalizeFinite(value, 1d, 32768d), nameof(Width));
    }

    public double Height
    {
        get => _height;
        set => SetChanged(ref _height, NormalizeFinite(value, 1d, 32768d), nameof(Height));
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

    private void SetChanged<T>(ref T field, T value, string propertyName)
    {
        if (SetProperty(ref field, value, propertyName))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string NormalizeName(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "参考图片" : value.Trim();
        return normalized.Length <= 260 ? normalized : normalized[..260];
    }

    private static double NormalizeFinite(double value, double minimum, double maximum) =>
        Math.Clamp(double.IsFinite(value) ? value : minimum, minimum, maximum);
}

public sealed class ArtWorkspaceConversationMessageViewModel
{
    private readonly ArtWorkspaceConversationMessage _model;

    public ArtWorkspaceConversationMessageViewModel(ArtWorkspaceConversationMessage model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
    }

    public string Id => _model.Id;

    public bool IsUser => _model.Role == ArtWorkspaceMessageRole.User;

    public string RoleLabel => IsUser ? "你" : "工作台";

    public string Text => _model.Text;

    public string TimeText => _model.CreatedAtUtc.ToLocalTime().ToString("HH:mm:ss");

    public string State => _model.State;

    public ArtWorkspaceConversationMessage ToModel() => new()
    {
        Id = _model.Id,
        Role = _model.Role,
        Text = _model.Text,
        CreatedAtUtc = _model.CreatedAtUtc,
        State = _model.State
    };
}

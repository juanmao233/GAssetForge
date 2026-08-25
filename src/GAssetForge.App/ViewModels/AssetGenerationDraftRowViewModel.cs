using GAssetForge.App.Infrastructure;
using GAssetForge.Core.Generation;

namespace GAssetForge.App.ViewModels;

public sealed record AssetGenerationDraftRowSnapshot(
    string Id,
    string Description,
    int Quantity,
    string OutputDirectory);

public sealed class AssetGenerationDraftRowViewModel : ObservableObject
{
    private string _description;
    private int _quantity;
    private string _outputDirectory;
    private string _outputDirectoryHint = string.Empty;
    private string _validationMessage = string.Empty;

    public AssetGenerationDraftRowViewModel(
        string? id,
        string? description,
        int quantity,
        string outputDirectory)
    {
        Id = Guid.TryParseExact(id, "N", out var parsedId)
            ? parsedId.ToString("N")
            : Guid.NewGuid().ToString("N");
        _description = description ?? string.Empty;
        _quantity = quantity;
        _outputDirectory = outputDirectory ?? string.Empty;
    }

    public event EventHandler? Changed;

    public string Id { get; }

    public string Description
    {
        get => _description;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref _description, value))
            {
                RaiseChanged();
            }
        }
    }

    public int Quantity
    {
        get => _quantity;
        set
        {
            if (SetProperty(ref _quantity, value))
            {
                RaiseChanged();
            }
        }
    }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref _outputDirectory, value))
            {
                RaiseChanged();
            }
        }
    }

    public string OutputDirectoryHint
    {
        get => _outputDirectoryHint;
        internal set => SetProperty(ref _outputDirectoryHint, value ?? string.Empty);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        internal set
        {
            if (SetProperty(ref _validationMessage, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsValid));
            }
        }
    }

    public bool IsValid => string.IsNullOrEmpty(ValidationMessage);

    public AssetGenerationDraftRowSnapshot Snapshot() => new(
        Id,
        Description,
        Quantity,
        OutputDirectory);

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

public sealed class PublishedGenerationTaskViewModel : ObservableObject
{
    private GenerationTask _task;
    private bool _isSuperseded;
    private int _generatedOutputCount;
    private string? _nextOutputPath;

    public PublishedGenerationTaskViewModel(GenerationTask task, string description)
    {
        _task = task ?? throw new ArgumentNullException(nameof(task));
        _isSuperseded = !string.IsNullOrWhiteSpace(task.SupersededByTaskId);
        Description = description ?? string.Empty;
    }

    public string TaskId => _task.Id;

    public string BatchId => _task.BatchId ?? string.Empty;

    public string BatchItemId => _task.BatchItemId ?? string.Empty;

    public int BatchItemIndex => _task.BatchItemIndex ?? 0;

    public string Description { get; }

    public int RequestedCount => _task.Constraints.Output.Count;

    public int CompletedOutputCount => _task.Status == GenerationTaskStatus.Completed
        ? _task.RelativeAssetPaths.Length
        : 0;

    public int GeneratedOutputCount => _generatedOutputCount;

    public double OutputProgress => RequestedCount <= 0
        ? 0d
        : Math.Clamp(GeneratedOutputCount * 100d / RequestedCount, 0d, 100d);

    public string OutputProgressText => $"{GeneratedOutputCount}/{RequestedCount} 张已落盘";

    public string? NextOutputPath => _nextOutputPath;

    public GenerationTaskStatus Status => _task.Status;

    public bool IsSuperseded
    {
        get => _isSuperseded;
        private set
        {
            if (SetProperty(ref _isSuperseded, value))
            {
                OnPropertyChanged(nameof(HasAutomaticRetryRemaining));
                OnPropertyChanged(nameof(IsAutomaticRetryBlocked));
                OnPropertyChanged(nameof(CanRetry));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(DetailText));
            }
        }
    }

    public bool IsAutomaticRetryBlocked =>
        _task.Status == GenerationTaskStatus.Failed &&
        !IsSuperseded &&
        !string.IsNullOrWhiteSpace(_task.AutomaticRetryBlockedReason);

    public bool HasAutomaticRetryRemaining =>
        _task.Status == GenerationTaskStatus.Failed &&
        !IsSuperseded &&
        !IsAutomaticRetryBlocked &&
        _task.FailureRetryable == true &&
        _task.RetryLineage is { } lineage &&
        lineage.AttemptNumber < lineage.MaxAttempts;

    public bool CanRetry =>
        _task.Status == GenerationTaskStatus.Failed &&
        !IsSuperseded &&
        !HasAutomaticRetryRemaining;

    public string StatusText => IsSuperseded ? "未通过 · 已自动重做" : _task.Status switch
    {
        GenerationTaskStatus.Pending => GeneratedOutputCount > 0
            ? $"等待认领 · {GeneratedOutputCount}/{RequestedCount} 已落盘"
            : "等待认领",
        GenerationTaskStatus.Claimed => $"{_task.ClaimedBy} 制作中 · {GeneratedOutputCount}/{RequestedCount}",
        GenerationTaskStatus.Completed => $"已完成 {CompletedOutputCount}/{RequestedCount}",
        GenerationTaskStatus.Failed when IsAutomaticRetryBlocked => "自动重做受阻 · 可人工接管",
        GenerationTaskStatus.Failed when HasAutomaticRetryRemaining => "未通过 · 自动重做中",
        GenerationTaskStatus.Failed => "生成失败 · 需要处理",
        _ => _task.Status.ToString()
    };

    public string DetailText => IsSuperseded
        ? $"原任务 {_task.Id[..8]} 保留失败记录，新的重做任务已单独发布"
        : _task.Status switch
    {
        GenerationTaskStatus.Pending => $"任务 {_task.Id[..8]} 已进入队列",
        GenerationTaskStatus.Claimed =>
            $"任务 {_task.Id[..8]} · {_task.ClaimedBy ?? "未知 Agent"} 认领于 {_task.ClaimedAtUtc?.ToLocalTime():HH:mm:ss}" +
            (string.IsNullOrWhiteSpace(NextOutputPath) ? string.Empty : $" · 下一目标 {NextOutputPath}"),
        GenerationTaskStatus.Completed =>
            $"任务 {_task.Id[..8]} · 完成于 {_task.CompletedAtUtc?.ToLocalTime():HH:mm:ss}",
        GenerationTaskStatus.Failed when IsAutomaticRetryBlocked =>
            $"任务 {_task.Id[..8]} · 自动重做已停止：{_task.AutomaticRetryBlockedReason} · " +
            $"原始问题：{_task.FailureMessage}",
        GenerationTaskStatus.Failed when HasAutomaticRetryRemaining =>
            $"任务 {_task.Id[..8]} · 第 {_task.RetryLineage!.AttemptNumber}/{_task.RetryLineage.MaxAttempts} 次未通过，正在创建下一次尝试 · {_task.FailureMessage}",
        GenerationTaskStatus.Failed =>
            $"任务 {_task.Id[..8]} · {_task.FailureMessage ?? "Agent 未提供失败原因"}",
        _ => string.Empty
    };

    internal GenerationTask Task => _task;

    public void MarkSuperseded() => IsSuperseded = true;

    public void ApplyDetectedOutputs(int generatedOutputCount, string? nextOutputPath)
    {
        var normalizedCount = Math.Clamp(generatedOutputCount, 0, Math.Max(0, RequestedCount));
        var normalizedNextPath = string.IsNullOrWhiteSpace(nextOutputPath)
            ? null
            : nextOutputPath.Replace('\\', '/');
        var changed = false;
        if (_generatedOutputCount != normalizedCount)
        {
            _generatedOutputCount = normalizedCount;
            OnPropertyChanged(nameof(GeneratedOutputCount));
            OnPropertyChanged(nameof(OutputProgress));
            OnPropertyChanged(nameof(OutputProgressText));
            changed = true;
        }

        if (!string.Equals(_nextOutputPath, normalizedNextPath, StringComparison.Ordinal))
        {
            _nextOutputPath = normalizedNextPath;
            OnPropertyChanged(nameof(NextOutputPath));
            changed = true;
        }

        if (changed)
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(DetailText));
        }
    }

    public void Apply(GenerationTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!string.Equals(task.Id, _task.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("不能用其它任务更新当前批次行。", nameof(task));
        }

        if (task.Revision == _task.Revision)
        {
            return;
        }

        _task = task;
        if (!string.IsNullOrWhiteSpace(task.SupersededByTaskId))
        {
            IsSuperseded = true;
        }
        OnPropertyChanged(nameof(RequestedCount));
        OnPropertyChanged(nameof(CompletedOutputCount));
        OnPropertyChanged(nameof(GeneratedOutputCount));
        OnPropertyChanged(nameof(OutputProgress));
        OnPropertyChanged(nameof(OutputProgressText));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsAutomaticRetryBlocked));
        OnPropertyChanged(nameof(HasAutomaticRetryRemaining));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DetailText));
    }
}

using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.Core.Generation;
using System.IO;
using System.Threading.Channels;

namespace GAssetForge.App.Services;

internal sealed record GenerationBatchDispatchRequest(
    string BatchId,
    IReadOnlyList<string> TaskIds,
    bool IsRetry);

internal enum GenerationBatchDispatchStage
{
    Idle,
    PreparingConstraints,
    FreezingStyleReferences,
    PersistingBatch,
    Queued,
    BuildingAgentInstruction,
    StartingAgentTurn,
    WaitingForTaskClaim,
    GeneratingAssets,
    VerifyingOutputs,
    Completed,
    Failed,
    Canceled
}

internal sealed record GenerationBatchDispatchProgressSnapshot(
    GenerationBatchDispatchRequest Request,
    GenerationBatchDispatchStage Stage,
    double ProgressPercentage,
    string Instruction,
    string Detail,
    GenerationBatchDispatchStage? FailureStage = null,
    string? FailureMessage = null)
{
    public bool IsTerminal => Stage is
        GenerationBatchDispatchStage.Completed or
        GenerationBatchDispatchStage.Failed or
        GenerationBatchDispatchStage.Canceled;
}

internal sealed class GenerationBatchDispatchProgressChangedEventArgs(
    GenerationBatchDispatchProgressSnapshot snapshot) : EventArgs
{
    public GenerationBatchDispatchProgressSnapshot Snapshot { get; } = snapshot;
}

internal sealed class GenerationBatchDispatchCompletedEventArgs(
    GenerationBatchDispatchRequest request,
    AgentTurnResult? result,
    string? failureMessage) : EventArgs
{
    public GenerationBatchDispatchRequest Request { get; } = request;

    public AgentTurnResult? Result { get; } = result;

    public string? FailureMessage { get; } = failureMessage;

    public bool IsSuccess => Result?.IsSuccess == true && string.IsNullOrWhiteSpace(FailureMessage);
}

/// <summary>
/// Serializes published generation batches into Agent turns. Generation tasks are already persisted
/// before they enter this coordinator, so a transport failure only affects automatic dispatch and
/// never removes or rewrites the pending queue records.
/// </summary>
internal sealed class GenerationBatchDispatchCoordinator : IAsyncDisposable
{
    private const double BuildingInstructionProgress = 45d;
    private const double StartingTurnProgress = 50d;
    private const double WaitingForClaimProgress = 55d;
    private static readonly TimeSpan DefaultClaimHeartbeatInterval = TimeSpan.FromMinutes(5);

    private readonly Channel<GenerationBatchDispatchRequest> _requests =
        Channel.CreateUnbounded<GenerationBatchDispatchRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly Func<string, CancellationToken, Task<AgentTurnResult>> _runTurn;
    private readonly IGenerationTaskStore? _taskStore;
    private readonly IImageDeliveryPreflight? _imageDeliveryPreflight;
    private readonly TimeSpan _claimHeartbeatInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _heartbeatDelay;
    private readonly object _scheduledBatchLock = new();
    private readonly HashSet<string> _scheduledBatchIds = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private int _disposed;

    public GenerationBatchDispatchCoordinator(
        Func<string, CancellationToken, Task<AgentTurnResult>> runTurn,
        IGenerationTaskStore? taskStore = null,
        IImageDeliveryPreflight? imageDeliveryPreflight = null,
        TimeSpan? claimHeartbeatInterval = null,
        Func<TimeSpan, CancellationToken, Task>? heartbeatDelay = null)
    {
        _runTurn = runTurn ?? throw new ArgumentNullException(nameof(runTurn));
        _taskStore = taskStore;
        _imageDeliveryPreflight = imageDeliveryPreflight;
        _claimHeartbeatInterval = claimHeartbeatInterval ?? DefaultClaimHeartbeatInterval;
        if (_claimHeartbeatInterval <= TimeSpan.Zero || _claimHeartbeatInterval > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(claimHeartbeatInterval),
                "生成任务认领心跳间隔必须大于 0 且不超过 1 天。");
        }

        _heartbeatDelay = heartbeatDelay ?? Task.Delay;
        _worker = ProcessRequestsAsync();
    }

    public event EventHandler<GenerationBatchDispatchCompletedEventArgs>? DispatchCompleted;

    public event EventHandler<GenerationBatchDispatchProgressChangedEventArgs>? DispatchProgressChanged;

    public bool TryEnqueue(string batchId, IReadOnlyList<string> taskIds, bool isRetry)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentNullException.ThrowIfNull(taskIds);
        var normalizedTaskIds = taskIds
            .Where(taskId => !string.IsNullOrWhiteSpace(taskId))
            .Select(taskId => taskId.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedTaskIds.Length == 0)
        {
            throw new ArgumentException("自动调度至少需要一个有效任务 ID。", nameof(taskIds));
        }

        var normalizedBatchId = batchId.Trim();
        lock (_scheduledBatchLock)
        {
            if (!_scheduledBatchIds.Add(normalizedBatchId))
            {
                return true;
            }

            if (_requests.Writer.TryWrite(
                    new GenerationBatchDispatchRequest(normalizedBatchId, normalizedTaskIds, isRetry)))
            {
                return true;
            }

            _scheduledBatchIds.Remove(normalizedBatchId);
            return false;
        }
    }

    public void ReleaseBatch(string batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId))
        {
            return;
        }

        lock (_scheduledBatchLock)
        {
            _scheduledBatchIds.Remove(batchId.Trim());
        }
    }

    private async Task ProcessRequestsAsync()
    {
        try
        {
            await foreach (var request in _requests.Reader.ReadAllAsync(_shutdown.Token)
                               .ConfigureAwait(false))
            {
                await DispatchAsync(request, _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal application shutdown cancels an active Agent turn and drops only the
            // in-memory dispatch requests. Their persisted generation tasks remain Pending.
        }
    }

    private async Task DispatchAsync(
        GenerationBatchDispatchRequest request,
        CancellationToken cancellationToken)
    {
        AgentTurnResult? result = null;
        string? failureMessage = null;
        GenerationBatchDispatchProgressSnapshot? lastProgress = null;
        try
        {
            var preflight = ImageDeliveryPreflightResult.NotChecked;
            var preflightPerformed = false;
            if (_imageDeliveryPreflight is not null)
            {
                lastProgress = ReportProgress(
                    request,
                    GenerationBatchDispatchStage.BuildingAgentInstruction,
                    40d,
                    "ImageDeliveryPreflight.CheckAsync(recommendedSourcePaths)",
                    "正在检查项目内模型原图目录、Python/py、官方 image_gen.py 与 API fallback 环境。");
                var recommendedSourcePaths = await ResolveRecommendedSourcePathsAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (recommendedSourcePaths.Count > 0)
                {
                    preflight = await _imageDeliveryPreflight.CheckAsync(
                            recommendedSourcePaths,
                            cancellationToken)
                        .ConfigureAwait(false);
                    preflightPerformed = true;
                    if (!preflight.CanDispatch)
                    {
                        throw new InvalidOperationException(preflight.StatusText);
                    }
                }
            }

            lastProgress = ReportProgress(
                request,
                GenerationBatchDispatchStage.BuildingAgentInstruction,
                BuildingInstructionProgress,
                "GenerationAgentPromptBuilder.Build(taskIds)",
                !preflightPerformed
                    ? $"正在为 {request.TaskIds.Count} 个任务构建 Agent 批次指令。"
                    : $"{preflight.StatusText} 正在为 {request.TaskIds.Count} 个任务构建 Agent 批次指令。");
            var prompt = preflightPerformed
                ? GenerationAgentPromptBuilder.Build(request.TaskIds, preflight)
                : GenerationAgentPromptBuilder.Build(request.TaskIds);
            lastProgress = ReportProgress(
                request,
                GenerationBatchDispatchStage.StartingAgentTurn,
                StartingTurnProgress,
                "AgentConnection.RunTurnAsync(prompt)",
                "正在把批次指令提交到当前 Agent 会话。");
            using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var turnTask = _runTurn(prompt, turnCancellation.Token);
            lastProgress = ReportProgress(
                request,
                GenerationBatchDispatchStage.WaitingForTaskClaim,
                WaitingForClaimProgress,
                "gasset_forge.claim_generation_task(taskId)",
                "Agent Turn 已提交，等待任务通过 MCP 被认领。");
            result = await AwaitTurnWithClaimHeartbeatAsync(
                    request,
                    turnTask,
                    turnCancellation,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                failureMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? $"Agent Turn 结束状态为 {result.Status}。"
                    : result.ErrorMessage;
                ReportTerminalProgress(
                    request,
                    GenerationBatchDispatchStage.Failed,
                    lastProgress,
                    failureMessage);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            ReportTerminalProgress(
                request,
                GenerationBatchDispatchStage.Canceled,
                lastProgress,
                "协调器已停止，当前 Agent Turn 已取消；持久化任务仍保留在本地队列。");
            throw;
        }
        catch (Exception exception)
        {
            failureMessage = exception.Message;
            ReportTerminalProgress(
                request,
                GenerationBatchDispatchStage.Failed,
                lastProgress,
                failureMessage);
        }

        DispatchCompleted?.Invoke(
            this,
            new GenerationBatchDispatchCompletedEventArgs(request, result, failureMessage));
    }

    private async Task<IReadOnlyList<string>> ResolveRecommendedSourcePathsAsync(
        GenerationBatchDispatchRequest request,
        CancellationToken cancellationToken)
    {
        if (_taskStore is null)
        {
            throw new InvalidOperationException("图片本地交付预检要求可读取持久化 GenerationTaskStore。");
        }

        var result = new List<string>(request.TaskIds.Count);
        foreach (var taskId in request.TaskIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var task = await _taskStore.GetAsync(taskId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"图片本地交付预检找不到任务：{taskId}");
            if (!MaterialGenerationConstraintComposer.TryReadDelivery(task.Constraints, out var delivery))
            {
                if (task.Constraints.Parameters.ContainsKey("delivery"))
                {
                    throw new InvalidDataException(
                        $"任务 {taskId} 包含损坏或不受支持的 parameters.delivery。");
                }

                continue;
            }

            if (delivery?.Workspace is null)
            {
                continue;
            }

            if (delivery.Workspace.ModelOutputPaths is not { Length: 1 } modelOutputPaths)
            {
                throw new InvalidDataException(
                    $"任务 {taskId} 没有唯一 generation workspace modelOutputPaths[0]。");
            }

            var sourcePath = ResolveRecommendedSourcePath(taskId, delivery, modelOutputPaths[0]);
            result.Add(sourcePath);
        }

        return result;
    }

    internal static string ResolveRecommendedSourcePath(
        string taskId,
        GenerationDeliveryPlan delivery,
        string modelOutputPath)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (string.IsNullOrWhiteSpace(delivery.AssetRoot) ||
            !Path.IsPathFullyQualified(delivery.AssetRoot))
        {
            throw new InvalidDataException($"任务 {taskId} 的 generation workspace AssetRoot 不是绝对路径。");
        }

        string assetRoot;
        string normalizedRelativePath;
        string sourcePath;
        try
        {
            assetRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(delivery.AssetRoot));
            normalizedRelativePath = GenerationInputValidator.NormalizeRelativeAssetPath(modelOutputPath);
            sourcePath = Path.GetFullPath(Path.Combine(
                assetRoot,
                normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            EnsureNoReparsePointsAllowingMissing(assetRoot, sourcePath, "推荐模型源文件");
        }
        catch (Exception exception) when (exception is
                   ArgumentException or
                   InvalidDataException or
                   IOException or
                   UnauthorizedAccessException or
                   NotSupportedException or
                   PathTooLongException or
                   System.Security.SecurityException)
        {
            throw new InvalidDataException(
                $"任务 {taskId} 的 recommendedSourcePath 合同无效：{exception.Message}",
                exception);
        }

        if (!AssetRootService.IsWithinRoot(assetRoot, sourcePath) ||
            AssetRootService.PathEquals(assetRoot, sourcePath))
        {
            throw new InvalidDataException(
                $"任务 {taskId} 的 recommendedSourcePath 越出了冻结的 Assets 根目录。");
        }

        return sourcePath;
    }

    private static void EnsureNoReparsePointsAllowingMissing(
        string rootPath,
        string candidatePath,
        string pathRole)
    {
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"冻结的 Assets 根目录不存在：{rootPath}");
        }

        if ((File.GetAttributes(rootPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("冻结的 Assets 根目录不能是符号链接或目录联接。");
        }

        var relative = Path.GetRelativePath(rootPath, candidatePath);
        var segments = relative.Split(
            new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        var current = rootPath;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            var isFile = File.Exists(current);
            var isDirectory = Directory.Exists(current);
            if (!isFile && !isDirectory)
            {
                return;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"{pathRole}路径不能经过符号链接或目录联接：{candidatePath}");
            }

            if (isFile && index < segments.Length - 1)
            {
                throw new InvalidDataException($"{pathRole}路径的父级不能是文件：{candidatePath}");
            }
        }
    }

    private async Task<AgentTurnResult> AwaitTurnWithClaimHeartbeatAsync(
        GenerationBatchDispatchRequest request,
        Task<AgentTurnResult> turnTask,
        CancellationTokenSource turnCancellation,
        CancellationToken cancellationToken)
    {
        if (_taskStore is null)
        {
            return await turnTask.ConfigureAwait(false);
        }

        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = MaintainClaimLeasesAsync(request, heartbeatCancellation.Token);
        try
        {
            var completed = await Task.WhenAny(turnTask, heartbeatTask).ConfigureAwait(false);
            if (ReferenceEquals(completed, turnTask))
            {
                var result = await turnTask.ConfigureAwait(false);
                if (heartbeatTask.IsFaulted)
                {
                    await heartbeatTask.ConfigureAwait(false);
                }

                return result;
            }

            try
            {
                await heartbeatTask.ConfigureAwait(false);
                throw new InvalidOperationException("生成任务认领心跳循环意外停止。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                turnCancellation.Cancel();
                try
                {
                    await turnTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (turnCancellation.IsCancellationRequested)
                {
                }

                throw new InvalidOperationException(
                    $"生成任务认领心跳失败，已中断当前 Agent Turn：{exception.Message}",
                    exception);
            }
        }
        finally
        {
            heartbeatCancellation.Cancel();
            if (!heartbeatTask.IsCompleted)
            {
                try
                {
                    await heartbeatTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (heartbeatCancellation.IsCancellationRequested)
                {
                }
            }
        }
    }

    private async Task MaintainClaimLeasesAsync(
        GenerationBatchDispatchRequest request,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await _heartbeatDelay(_claimHeartbeatInterval, cancellationToken).ConfigureAwait(false);
            foreach (var taskId in request.TaskIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var task = await _taskStore!.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
                if (task?.Status != GenerationTaskStatus.Claimed ||
                    string.IsNullOrWhiteSpace(task.ClaimedBy))
                {
                    continue;
                }

                try
                {
                    await _taskStore.RenewClaimAsync(task.Id, task.ClaimedBy, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException exception)
                {
                    var latest = await _taskStore.GetAsync(task.Id, cancellationToken).ConfigureAwait(false);
                    if (latest?.Status is GenerationTaskStatus.Completed or GenerationTaskStatus.Failed)
                    {
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"任务 {task.Id} 的活动认领无法续租；当前状态为 " +
                        $"{latest?.Status.ToString() ?? "Missing"}，所有者为 {latest?.ClaimedBy ?? "none"}。",
                        exception);
                }
            }
        }
    }

    private GenerationBatchDispatchProgressSnapshot ReportProgress(
        GenerationBatchDispatchRequest request,
        GenerationBatchDispatchStage stage,
        double progressPercentage,
        string instruction,
        string detail,
        GenerationBatchDispatchStage? failureStage = null,
        string? failureMessage = null)
    {
        var snapshot = new GenerationBatchDispatchProgressSnapshot(
            request,
            stage,
            progressPercentage,
            instruction,
            detail,
            failureStage,
            failureMessage);
        DispatchProgressChanged?.Invoke(
            this,
            new GenerationBatchDispatchProgressChangedEventArgs(snapshot));
        return snapshot;
    }

    private void ReportTerminalProgress(
        GenerationBatchDispatchRequest request,
        GenerationBatchDispatchStage terminalStage,
        GenerationBatchDispatchProgressSnapshot? lastProgress,
        string detail)
    {
        var failureStage = lastProgress?.Stage ?? GenerationBatchDispatchStage.BuildingAgentInstruction;
        ReportProgress(
            request,
            terminalStage,
            lastProgress?.ProgressPercentage ?? BuildingInstructionProgress,
            lastProgress?.Instruction ?? "GenerationAgentPromptBuilder.Build(taskIds)",
            detail,
            terminalStage == GenerationBatchDispatchStage.Failed ? failureStage : null,
            terminalStage == GenerationBatchDispatchStage.Failed ? detail : null);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _requests.Writer.TryComplete();
        _shutdown.Cancel();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Expected when an active turn is interrupted by application shutdown.
        }
        finally
        {
            _shutdown.Dispose();
        }
    }
}

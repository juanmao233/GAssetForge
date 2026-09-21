using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.Core.Generation;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;

namespace GAssetForge.App.Services;

internal sealed record GenerationBatchDispatchRequest(
    string BatchId,
    IReadOnlyList<string> TaskIds,
    bool IsRetry);

internal enum GenerationBatchDispatchEnqueueResult
{
    Enqueued,
    AlreadyScheduled,
    QueueStopped
}

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
    Interrupted,
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
    string? FailureMessage = null,
    int? ChunkIndex = null,
    int? ChunkCount = null,
    int? ChunkTaskCount = null,
    long? ElapsedMilliseconds = null,
    string? FailureCode = null)
{
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.Now;

    public string? ObservedThreadId { get; init; }

    public string? ObservedTurnId { get; init; }

    public GenerationDispatchFailure? Diagnostic { get; init; }

    public bool IsTerminal => Stage is
        GenerationBatchDispatchStage.Completed or
        GenerationBatchDispatchStage.Interrupted or
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

    public GenerationDispatchFailure? Diagnostic { get; init; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.Now;

    public bool IsSuccess => Result?.IsSuccess == true && string.IsNullOrWhiteSpace(FailureMessage);
}

/// <summary>
/// Serializes published generation batches into Agent turns. Generation tasks are already persisted
/// before they enter this coordinator, so a transport failure only affects automatic dispatch and
/// never removes or rewrites the pending queue records.
/// </summary>
internal sealed class GenerationBatchDispatchCoordinator : IAsyncDisposable
{
    internal const int MaximumTasksPerTurn = 5;

    private const double BuildingInstructionProgress = 45d;
    private const double WaitingForClaimProgress = 55d;
    private static readonly TimeSpan DefaultClaimHeartbeatInterval = TimeSpan.FromMinutes(5);

    private readonly Channel<DispatchLifecycle> _requests =
        Channel.CreateUnbounded<DispatchLifecycle>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly Func<string, CancellationToken, Task<AgentTurnResult>> _runTurn;
    private readonly Func<GenerationBatchDispatchRequest, CancellationToken, Task>? _prepareBatch;
    private readonly Func<string?>? _currentThreadIdProvider;
    private readonly IGenerationTaskStore? _taskStore;
    private readonly IImageDeliveryPreflight? _imageDeliveryPreflight;
    private readonly TimeSpan _claimHeartbeatInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _heartbeatDelay;
    private readonly object _scheduledBatchLock = new();
    private readonly object _turnObservationLock = new();
    private readonly Queue<string> _finishedTurnIds = new();
    private TurnDispatchObservation? _activeTurnObservation;
    private readonly Dictionary<string, DispatchLifecycle> _scheduledBatches =
        new(StringComparer.Ordinal);
    private readonly HashSet<DispatchLifecycle> _unfinishedDispatches = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private int _disposed;

    public GenerationBatchDispatchCoordinator(
        Func<string, CancellationToken, Task<AgentTurnResult>> runTurn,
        IGenerationTaskStore? taskStore = null,
        IImageDeliveryPreflight? imageDeliveryPreflight = null,
        TimeSpan? claimHeartbeatInterval = null,
        Func<TimeSpan, CancellationToken, Task>? heartbeatDelay = null,
        Func<GenerationBatchDispatchRequest, CancellationToken, Task>? prepareBatch = null,
        Func<string?>? currentThreadIdProvider = null)
    {
        _runTurn = runTurn ?? throw new ArgumentNullException(nameof(runTurn));
        _prepareBatch = prepareBatch;
        _currentThreadIdProvider = currentThreadIdProvider;
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

    public bool NotifyTurnStarted(string? threadId, string? turnId, DateTimeOffset? occurredAt = null)
    {
        // A Task returned by RunTurnAsync is not a server acknowledgement. Only a matching live
        // TurnStarted notification may advance dispatch to the claim-wait stage.
        lock (_turnObservationLock)
        {
            var observation = _activeTurnObservation;
            if (observation is null || observation.CancellationToken.IsCancellationRequested ||
                string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId) ||
                string.IsNullOrWhiteSpace(observation.ExpectedThreadId) ||
                !string.Equals(threadId, observation.ExpectedThreadId, StringComparison.Ordinal) ||
                observation.TurnId is not null ||
                _finishedTurnIds.Contains($"{threadId}/{turnId}", StringComparer.Ordinal))
            {
                return false;
            }

            observation.TurnId = turnId;
            observation.LastProgress = ReportProgress(
                observation.Request,
                GenerationBatchDispatchStage.WaitingForTaskClaim,
                observation.WaitingProgress,
                "gasset_forge.claim_generation_task(taskId)",
                $"{observation.ChunkLabel} 已收到 Agent Turn 启动确认，等待任务通过 MCP 被认领。",
                observedThreadId: threadId,
                observedTurnId: turnId,
                occurredAt: occurredAt);
            return true;
        }
    }

    public GenerationBatchDispatchEnqueueResult TryEnqueue(
        string batchId,
        IReadOnlyList<string> taskIds,
        bool isRetry)
    {
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
        if (Volatile.Read(ref _disposed) != 0)
        {
            return GenerationBatchDispatchEnqueueResult.QueueStopped;
        }

        lock (_scheduledBatchLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return GenerationBatchDispatchEnqueueResult.QueueStopped;
            }

            if (_scheduledBatches.ContainsKey(normalizedBatchId))
            {
                return GenerationBatchDispatchEnqueueResult.AlreadyScheduled;
            }

            var lifecycle = new DispatchLifecycle(
                new GenerationBatchDispatchRequest(normalizedBatchId, normalizedTaskIds, isRetry));
            _scheduledBatches.Add(normalizedBatchId, lifecycle);
            _unfinishedDispatches.Add(lifecycle);
            if (_requests.Writer.TryWrite(lifecycle))
            {
                return GenerationBatchDispatchEnqueueResult.Enqueued;
            }

            _scheduledBatches.Remove(normalizedBatchId);
            _unfinishedDispatches.Remove(lifecycle);
            lifecycle.Released.TrySetResult();
            lifecycle.WorkerExited.TrySetResult();
            lifecycle.Dispose();
            return GenerationBatchDispatchEnqueueResult.QueueStopped;
        }
    }

    public Task WaitForBatchReleasedAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        Task releaseTask;
        lock (_scheduledBatchLock)
        {
            if (!_scheduledBatches.TryGetValue(batchId.Trim(), out var lifecycle))
            {
                return Task.CompletedTask;
            }

            releaseTask = lifecycle.Released.Task;
        }

        return cancellationToken.CanBeCanceled
            ? releaseTask.WaitAsync(cancellationToken)
            : releaseTask;
    }

    public void ReleaseBatch(string batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId))
        {
            return;
        }

        DispatchLifecycle? lifecycle;
        lock (_scheduledBatchLock)
        {
            var normalizedBatchId = batchId.Trim();
            _scheduledBatches.Remove(normalizedBatchId, out lifecycle);
        }

        lifecycle?.Released.TrySetResult();
    }

    public async Task CancelBatchesAsync(
        IReadOnlyList<string> batchIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batchIds);
        cancellationToken.ThrowIfCancellationRequested();
        var requestedIds = batchIds
            .Where(batchId => !string.IsNullOrWhiteSpace(batchId))
            .Select(batchId => batchId.Trim())
            .ToHashSet(StringComparer.Ordinal);
        DispatchLifecycle[] lifecycles;
        lock (_scheduledBatchLock)
        {
            // Include a released but still unwinding turn. Each queued item owns its own token,
            // so a later enqueue of the same batch ID cannot be canceled by this snapshot.
            lifecycles = _unfinishedDispatches.Concat(_scheduledBatches.Values)
                .Where(lifecycle => requestedIds.Contains(lifecycle.Request.BatchId))
                .Distinct()
                .ToArray();
            foreach (var lifecycle in lifecycles)
            {
                lifecycle.CancellationRequested = true;
                if (!lifecycle.IsRunning)
                {
                    _unfinishedDispatches.Remove(lifecycle);
                    lifecycle.WorkerExited.TrySetResult();
                }

                ReleaseLifecycle(lifecycle);
            }
        }

        // Cancellation callbacks can call back into the coordinator; never run them under its
        // scheduling lock. Cancellation of this caller only stops waiting, not the chosen stop.
        await Task.WhenAll(lifecycles.Select(lifecycle => lifecycle.CancelAsync()))
            .ConfigureAwait(false);
        await Task.WhenAll(lifecycles.Select(lifecycle => lifecycle.WorkerExited.Task))
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var lifecycle in lifecycles)
        {
            lifecycle.Dispose();
        }
    }

    private void ReleaseLifecycle(DispatchLifecycle lifecycle)
    {
        if (_scheduledBatches.TryGetValue(lifecycle.Request.BatchId, out var scheduled) &&
            ReferenceEquals(scheduled, lifecycle))
        {
            _scheduledBatches.Remove(lifecycle.Request.BatchId);
        }

        lifecycle.Released.TrySetResult();
    }

    private async Task ProcessRequestsAsync()
    {
        try
        {
            await foreach (var lifecycle in _requests.Reader.ReadAllAsync(_shutdown.Token)
                               .ConfigureAwait(false))
            {
                lock (_scheduledBatchLock)
                {
                    if (lifecycle.CancellationRequested)
                    {
                        lifecycle.Dispose();
                        continue;
                    }

                    lifecycle.IsRunning = true;
                }

                try
                {
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        _shutdown.Token,
                        lifecycle.Cancellation.Token);
                    await DispatchAsync(lifecycle, cancellation.Token).ConfigureAwait(false);
                }
                finally
                {
                    lock (_scheduledBatchLock)
                    {
                        lifecycle.IsRunning = false;
                        _unfinishedDispatches.Remove(lifecycle);
                        if (lifecycle.CancellationRequested)
                        {
                            ReleaseLifecycle(lifecycle);
                        }

                        lifecycle.WorkerExited.TrySetResult();
                    }

                    lifecycle.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal application shutdown cancels an active Agent turn and drops only the
            // in-memory dispatch requests. Their persisted generation tasks remain Pending.
        }
    }

    private async Task DispatchAsync(
        DispatchLifecycle lifecycle,
        CancellationToken cancellationToken)
    {
        var request = lifecycle.Request;
        AgentTurnResult? result = null;
        string? failureMessage = null;
        GenerationDispatchFailure? diagnostic = null;
        var failureOperation = "dispatch_preparation";
        GenerationBatchDispatchProgressSnapshot? lastProgress = null;
        TurnDispatchObservation? turnObservation = null;
        long? activeTurnStartedTimestamp = null;
        int? activeChunkIndex = null;
        int? activeChunkCount = null;
        int? activeChunkTaskCount = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preflight = ImageDeliveryPreflightResult.NotChecked;
            var preflightPerformed = false;
            if (_imageDeliveryPreflight is not null)
            {
                failureOperation = "image_preflight";
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

            failureOperation = "dispatch_preparation";
            var identityResolution = await ResolveClaimIdentitiesAsync(request, cancellationToken)
                .ConfigureAwait(false);
            request = request with { TaskIds = identityResolution.TaskIds };

            var chunks = request.TaskIds
                .Chunk(MaximumTasksPerTurn)
                .Select(chunk => (IReadOnlyList<string>)chunk)
                .ToArray();
            if (_prepareBatch is not null)
            {
                failureOperation = "session_preparation";
                lastProgress = ReportProgress(
                    request,
                    GenerationBatchDispatchStage.BuildingAgentInstruction,
                    42d,
                    "AgentConnection.PrepareGenerationBatchAsync(batchId)",
                    $"正在为本批次准备独立 Agent thread；随后 {chunks.Length} 个 Turn 分块将共用该 thread。");
                await _prepareBatch(request, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var chunkProgressSpan =
                (WaitingForClaimProgress - BuildingInstructionProgress) / chunks.Length;
            for (var chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The previous chunk's completed Turn must never become evidence that this
                // chunk started when prompt construction or submission fails.
                result = null;
                turnObservation = null;
                failureOperation = "dispatch_preparation";
                var chunkTaskIds = chunks[chunkIndex];
                var chunkRequest = request with { TaskIds = chunkTaskIds };
                var chunkClaimIdentities = chunkTaskIds.ToDictionary(
                    taskId => taskId,
                    taskId => identityResolution.ClaimIdentities[taskId],
                    StringComparer.Ordinal);
                var chunkNumber = chunkIndex + 1;
                var buildingProgress = BuildingInstructionProgress + chunkProgressSpan * chunkIndex;
                var startingProgress = buildingProgress + chunkProgressSpan * 0.5d;
                var waitingProgress = buildingProgress + chunkProgressSpan;
                var chunkLabel = $"Turn 分块 {chunkNumber}/{chunks.Length} · {chunkTaskIds.Count} 个任务";

                lastProgress = ReportProgress(
                    chunkRequest,
                    GenerationBatchDispatchStage.BuildingAgentInstruction,
                    buildingProgress,
                    "GenerationAgentPromptBuilder.Build(chunkTaskIds)",
                    !preflightPerformed
                        ? $"正在构建 {chunkLabel} 的 Agent 指令。"
                        : $"{preflight.StatusText} 正在构建 {chunkLabel} 的 Agent 指令。");
                var prompt = preflightPerformed
                    ? GenerationAgentPromptBuilder.Build(
                        chunkTaskIds,
                        preflight,
                        chunkClaimIdentities)
                    : GenerationAgentPromptBuilder.Build(chunkTaskIds, chunkClaimIdentities);
                var expectedThreadId = _currentThreadIdProvider?.Invoke();
                lastProgress = ReportProgress(
                    chunkRequest,
                    GenerationBatchDispatchStage.StartingAgentTurn,
                    startingProgress,
                    "AgentConnection.RunTurnAsync(prompt)",
                    $"正在提交 {chunkLabel}，等待 Agent 启动确认；本批后续分块会等待当前 Turn 收束后再启动。",
                    observedThreadId: expectedThreadId);
                using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                activeTurnStartedTimestamp = Stopwatch.GetTimestamp();
                activeChunkIndex = chunkNumber;
                activeChunkCount = chunks.Length;
                activeChunkTaskCount = chunkTaskIds.Count;
                failureOperation = "turn_submission";
                turnObservation = new TurnDispatchObservation(
                    chunkRequest,
                    expectedThreadId,
                    chunkLabel,
                    waitingProgress,
                    lastProgress,
                    cancellationToken);
                lock (_turnObservationLock)
                {
                    _activeTurnObservation = turnObservation;
                }

                var turnTask = _runTurn(prompt, turnCancellation.Token);
                result = await AwaitTurnWithClaimHeartbeatAsync(
                        chunkRequest,
                        chunkClaimIdentities,
                        turnTask,
                        turnCancellation,
                        cancellationToken)
                    .ConfigureAwait(false);
                lastProgress = FinishTurnObservation(turnObservation, result) ?? lastProgress;
                cancellationToken.ThrowIfCancellationRequested();
                var turnElapsedMilliseconds = (long)Stopwatch
                    .GetElapsedTime(activeTurnStartedTimestamp.Value)
                    .TotalMilliseconds;
                if (result.IsSuccess)
                {
                    lastProgress = ReportProgress(
                        chunkRequest,
                        GenerationBatchDispatchStage.WaitingForTaskClaim,
                        waitingProgress,
                        "AgentConnection.RunTurnAsync(prompt) completed",
                        $"{chunkLabel} 已在 {turnElapsedMilliseconds} ms 后结束；" +
                        "最终产物状态继续以 MCP 任务库和本地文件为准。",
                        chunkIndex: chunkNumber,
                        chunkCount: chunks.Length,
                        chunkTaskCount: chunkTaskIds.Count,
                        elapsedMilliseconds: turnElapsedMilliseconds,
                        observedThreadId: result.ThreadId,
                        observedTurnId: result.TurnId);
                    activeTurnStartedTimestamp = null;
                    continue;
                }

                failureMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? $"Agent Turn 结束状态为 {result.Status}。"
                    : result.ErrorMessage;
                diagnostic = GenerationDispatchFailure.FromTurnResult(result);
                var failure = AgentTurnFailureClassifier.Classify(result, failureMessage);
                var terminalStage = failure?.PreservePendingTasks == true
                    ? GenerationBatchDispatchStage.Interrupted
                    : GenerationBatchDispatchStage.Failed;
                var terminalDetail = !diagnostic.TurnStarted
                    ? $"{chunkLabel}：{diagnostic.UserMessage} 后续分块未启动；调度调用用时 {turnElapsedMilliseconds} ms。"
                    : terminalStage == GenerationBatchDispatchStage.Interrupted
                    ? $"{chunkLabel} 的上游模型响应流中断，后续分块未启动；" +
                      $"原任务保持 Pending；Turn 用时 {turnElapsedMilliseconds} ms；" +
                      $"response.completed=否。{failureMessage}"
                    : $"{chunkLabel} 执行失败，后续分块未启动；" +
                      $"Turn 用时 {turnElapsedMilliseconds} ms：{failureMessage}";
                ReportTerminalProgress(
                    chunkRequest,
                    terminalStage,
                    lastProgress,
                    terminalDetail,
                    turnElapsedMilliseconds,
                    chunkNumber,
                    chunks.Length,
                    chunkTaskIds.Count,
                    diagnostic.Code,
                    diagnostic);
                break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lastProgress = FinishTurnObservation(turnObservation, result) ?? lastProgress;
            ReportTerminalProgress(
                request,
                GenerationBatchDispatchStage.Canceled,
                lastProgress,
                _shutdown.IsCancellationRequested
                    ? "协调器已停止，此批次调度已取消；持久化任务仍保留在本地队列。"
                    : "此批次调度已手动停止；持久化任务和已有文件仍保留。");
            return;
        }
        catch (Exception exception)
        {
            lastProgress = FinishTurnObservation(turnObservation, result) ?? lastProgress;
            failureMessage = exception.Message;
            var turnStarted = turnObservation?.TurnId is not null;
            diagnostic = GenerationDispatchFailure.FromException(
                exception,
                turnStarted ? "turn_execution" : failureOperation,
                turnStarted,
                turnObservation?.ExpectedThreadId,
                turnObservation?.TurnId);
            var failure = AgentTurnFailureClassifier.Classify(null, failureMessage);
            long? elapsedMilliseconds = activeTurnStartedTimestamp is { } startedTimestamp
                ? (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds
                : null;
            ReportTerminalProgress(
                request,
                failure?.PreservePendingTasks == true
                    ? GenerationBatchDispatchStage.Interrupted
                    : GenerationBatchDispatchStage.Failed,
                lastProgress,
                diagnostic.UserMessage,
                elapsedMilliseconds,
                activeChunkIndex,
                activeChunkCount,
                activeChunkTaskCount,
                diagnostic.Code,
                diagnostic);
        }

        lock (_scheduledBatchLock)
        {
            if (!lifecycle.CancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                DispatchCompleted?.Invoke(
                    this,
                    new GenerationBatchDispatchCompletedEventArgs(request, result, failureMessage)
                    {
                        Diagnostic = diagnostic,
                        OccurredAt = diagnostic?.OccurredAt ?? DateTimeOffset.Now
                    });
            }
        }
    }

    private GenerationBatchDispatchProgressSnapshot? FinishTurnObservation(
        TurnDispatchObservation? observation,
        AgentTurnResult? result)
    {
        lock (_turnObservationLock)
        {
            if (observation is null)
            {
                return null;
            }

            if (ReferenceEquals(_activeTurnObservation, observation))
            {
                _activeTurnObservation = null;
                var threadId = result?.ThreadId ?? observation.ExpectedThreadId;
                var turnId = result?.TurnId ?? observation.TurnId;
                if (!string.IsNullOrWhiteSpace(threadId) && !string.IsNullOrWhiteSpace(turnId))
                {
                    _finishedTurnIds.Enqueue($"{threadId}/{turnId}");
                    while (_finishedTurnIds.Count > 128)
                    {
                        _finishedTurnIds.Dequeue();
                    }
                }
            }

            return observation.LastProgress;
        }
    }

    private async Task<DispatchClaimIdentityResolution> ResolveClaimIdentitiesAsync(
        GenerationBatchDispatchRequest request,
        CancellationToken cancellationToken)
    {
        if (_taskStore is null)
        {
            var fallbackIdentities = request.TaskIds.ToDictionary(
                taskId => taskId,
                GenerationManagedAgentIdentity.ForTask,
                StringComparer.Ordinal);
            return new DispatchClaimIdentityResolution(request.TaskIds, fallbackIdentities);
        }

        var taskIds = new List<string>(request.TaskIds.Count);
        var claimIdentities = new Dictionary<string, string>(StringComparer.Ordinal);
        var incompatibleClaims = new List<string>();
        foreach (var taskId in request.TaskIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var task = await _taskStore.GetAsync(taskId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Agent 调度找不到任务：{taskId}");
            if (!string.IsNullOrWhiteSpace(task.BatchId) &&
                !string.Equals(task.BatchId, request.BatchId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"任务 {task.Id} 属于批次 {task.BatchId}，不能由批次 {request.BatchId} 续派。");
            }

            if (!string.IsNullOrWhiteSpace(task.SupersededByTaskId) ||
                task.Status is GenerationTaskStatus.Completed or GenerationTaskStatus.Failed)
            {
                continue;
            }

            if (task.Status == GenerationTaskStatus.Claimed &&
                !GenerationManagedAgentIdentity.IsManagedClaimantForTask(
                    task.Id,
                    task.ClaimedBy))
            {
                incompatibleClaims.Add($"{task.Id} ({task.ClaimedBy ?? "unknown"})");
                continue;
            }

            if (!GenerationManagedAgentIdentity.CanResume(task))
            {
                continue;
            }

            var claimIdentity = GenerationManagedAgentIdentity.ResolveForTask(task);
            if (task.Status == GenerationTaskStatus.Claimed)
            {
                try
                {
                    task = await _taskStore.RenewClaimAsync(
                            task.Id,
                            claimIdentity,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    var latest = await _taskStore.GetAsync(task.Id, cancellationToken)
                        .ConfigureAwait(false);
                    if (latest?.Status != GenerationTaskStatus.Pending)
                    {
                        throw;
                    }

                    task = latest;
                    claimIdentity = GenerationManagedAgentIdentity.ForTask(task.Id);
                }
            }

            taskIds.Add(task.Id);
            claimIdentities.Add(task.Id, claimIdentity);
        }

        if (taskIds.Count == 0)
        {
            var ownershipDetail = incompatibleClaims.Count == 0
                ? string.Empty
                : $" 非当前受管所有者：{string.Join(", ", incompatibleClaims)}；不会抢占。";
            throw new InvalidOperationException(
                $"批次 {request.BatchId} 已没有可由当前 Codex Runtime 继续的 Pending/Claimed 任务。" +
                ownershipDetail);
        }

        return new DispatchClaimIdentityResolution(taskIds, claimIdentities);
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
        IReadOnlyDictionary<string, string> claimIdentities,
        Task<AgentTurnResult> turnTask,
        CancellationTokenSource turnCancellation,
        CancellationToken cancellationToken)
    {
        if (_taskStore is null)
        {
            return await turnTask.ConfigureAwait(false);
        }

        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = MaintainClaimLeasesAsync(
            request,
            claimIdentities,
            heartbeatCancellation.Token);
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
                // The heartbeat can observe cancellation before the runtime finishes interrupting
                // its turn. Keep serialization until that turn has actually unwound.
                try
                {
                    await turnTask.ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                }

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
        IReadOnlyDictionary<string, string> claimIdentities,
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

                if (!claimIdentities.TryGetValue(task.Id, out var expectedAgentId))
                {
                    throw new InvalidOperationException(
                        $"任务 {task.Id} 缺少本轮冻结的受管 Agent 身份，停止续租。");
                }

                if (!string.Equals(task.ClaimedBy, expectedAgentId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"任务 {task.Id} 已由身份 {task.ClaimedBy} 抢先认领，" +
                        $"与本轮冻结身份 {expectedAgentId} 不一致；不会替其它身份续租。");
                }

                try
                {
                    await _taskStore.RenewClaimAsync(task.Id, expectedAgentId, cancellationToken)
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
        string? failureMessage = null,
        int? chunkIndex = null,
        int? chunkCount = null,
        int? chunkTaskCount = null,
        long? elapsedMilliseconds = null,
        string? failureCode = null,
        GenerationDispatchFailure? diagnostic = null,
        string? observedThreadId = null,
        string? observedTurnId = null,
        DateTimeOffset? occurredAt = null)
    {
        var snapshot = new GenerationBatchDispatchProgressSnapshot(
            request,
            stage,
            progressPercentage,
            instruction,
            detail,
            failureStage,
            failureMessage,
            chunkIndex,
            chunkCount,
            chunkTaskCount,
            elapsedMilliseconds,
            failureCode)
        {
            Diagnostic = diagnostic,
            OccurredAt = diagnostic?.OccurredAt ?? occurredAt ?? DateTimeOffset.Now,
            ObservedThreadId = diagnostic?.ThreadId ?? observedThreadId,
            ObservedTurnId = diagnostic?.TurnId ?? observedTurnId
        };
        DispatchProgressChanged?.Invoke(
            this,
            new GenerationBatchDispatchProgressChangedEventArgs(snapshot));
        return snapshot;
    }

    private void ReportTerminalProgress(
        GenerationBatchDispatchRequest request,
        GenerationBatchDispatchStage terminalStage,
        GenerationBatchDispatchProgressSnapshot? lastProgress,
        string detail,
        long? elapsedMilliseconds = null,
        int? chunkIndex = null,
        int? chunkCount = null,
        int? chunkTaskCount = null,
        string? failureCode = null,
        GenerationDispatchFailure? diagnostic = null)
    {
        var failureStage = lastProgress?.Stage ?? GenerationBatchDispatchStage.BuildingAgentInstruction;
        ReportProgress(
            request,
            terminalStage,
            lastProgress?.ProgressPercentage ?? BuildingInstructionProgress,
            lastProgress?.Instruction ?? "GenerationAgentPromptBuilder.Build(taskIds)",
            detail,
            terminalStage is GenerationBatchDispatchStage.Failed or
                GenerationBatchDispatchStage.Interrupted
                ? failureStage
                : null,
            terminalStage is GenerationBatchDispatchStage.Failed or
                GenerationBatchDispatchStage.Interrupted
                ? detail
                : null,
            chunkIndex,
            chunkCount,
            chunkTaskCount,
            elapsedMilliseconds,
            failureCode,
            diagnostic);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        DispatchLifecycle[] lifecycles;
        lock (_scheduledBatchLock)
        {
            lifecycles = _unfinishedDispatches.Concat(_scheduledBatches.Values).Distinct().ToArray();
            _scheduledBatches.Clear();
            foreach (var lifecycle in lifecycles)
            {
                lifecycle.CancellationRequested = true;
                lifecycle.Released.TrySetResult();
                if (!lifecycle.IsRunning)
                {
                    lifecycle.WorkerExited.TrySetResult();
                }
            }
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
            foreach (var lifecycle in lifecycles)
            {
                lifecycle.Dispose();
            }

            _shutdown.Dispose();
        }
    }

    private sealed class DispatchLifecycle(GenerationBatchDispatchRequest request) : IDisposable
    {
        private int _disposed;

        public GenerationBatchDispatchRequest Request { get; } = request;

        public CancellationTokenSource Cancellation { get; } = new();

        public TaskCompletionSource Released { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WorkerExited { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // These state flags are accessed only while holding _scheduledBatchLock.
        public bool CancellationRequested { get; set; }

        public bool IsRunning { get; set; }

        public async Task CancelAsync()
        {
            try
            {
                await Cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException) when (WorkerExited.Task.IsCompleted)
            {
                // Dispatch already finished while the caller was leaving the scheduling lock.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Cancellation.Dispose();
            }
        }
    }

    private sealed record DispatchClaimIdentityResolution(
        IReadOnlyList<string> TaskIds,
        IReadOnlyDictionary<string, string> ClaimIdentities);

    private sealed class TurnDispatchObservation(
        GenerationBatchDispatchRequest request,
        string? expectedThreadId,
        string chunkLabel,
        double waitingProgress,
        GenerationBatchDispatchProgressSnapshot lastProgress,
        CancellationToken cancellationToken)
    {
        public GenerationBatchDispatchRequest Request { get; } = request;
        public string? ExpectedThreadId { get; } = expectedThreadId;
        public string ChunkLabel { get; } = chunkLabel;
        public double WaitingProgress { get; } = waitingProgress;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public string? TurnId { get; set; }
        public GenerationBatchDispatchProgressSnapshot LastProgress { get; set; } = lastProgress;
    }
}

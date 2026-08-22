using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.Core.Generation;
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
    private readonly TimeSpan _claimHeartbeatInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _heartbeatDelay;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private int _disposed;

    public GenerationBatchDispatchCoordinator(
        Func<string, CancellationToken, Task<AgentTurnResult>> runTurn,
        IGenerationTaskStore? taskStore = null,
        TimeSpan? claimHeartbeatInterval = null,
        Func<TimeSpan, CancellationToken, Task>? heartbeatDelay = null)
    {
        _runTurn = runTurn ?? throw new ArgumentNullException(nameof(runTurn));
        _taskStore = taskStore;
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

        return _requests.Writer.TryWrite(
            new GenerationBatchDispatchRequest(batchId.Trim(), normalizedTaskIds, isRetry));
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
            lastProgress = ReportProgress(
                request,
                GenerationBatchDispatchStage.BuildingAgentInstruction,
                BuildingInstructionProgress,
                "GenerationAgentPromptBuilder.Build(taskIds)",
                $"正在为 {request.TaskIds.Count} 个任务构建 Agent 批次指令。");
            var prompt = GenerationAgentPromptBuilder.Build(request.TaskIds);
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

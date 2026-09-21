namespace GAssetForge.Core.Generation;

public interface IGenerationTaskStore
{
    Task<GenerationTask> CreateAsync(
        CreateGenerationTaskRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GenerationTask>> CreateBatchAsync(
        CreateGenerationBatchRequest request,
        CancellationToken cancellationToken = default);

    Task<CreateGenerationRetryBatchResult> CreateRetryBatchAsync(
        CreateGenerationRetryBatchRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GenerationTask>> BlockAutomaticRetryAsync(
        IReadOnlyList<string> taskIds,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Withdraws desktop automatic dispatch for the exact task IDs without changing task status,
    /// claim ownership, frozen constraints, or delivered files.
    /// </summary>
    Task<IReadOnlyList<GenerationTask>> SuspendAutomaticDispatchAsync(
        IReadOnlyList<string> taskIds,
        string reason,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("当前任务存储不支持暂停自动调度。");

    Task<IReadOnlyList<GenerationTask>> ListBatchAsync(
        string batchId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GenerationTask>> ListAsync(
        GenerationTaskStatus? status = null,
        string? workflowId = null,
        CancellationToken cancellationToken = default);

    Task<GenerationTask?> GetAsync(
        string taskId,
        CancellationToken cancellationToken = default);

    Task<GenerationTask?> ClaimNextAsync(
        string agentId,
        string? workflowId = null,
        CancellationToken cancellationToken = default);

    Task<GenerationTask> ClaimAsync(
        string taskId,
        string agentId,
        CancellationToken cancellationToken = default);

    Task<GenerationTask> RenewClaimAsync(
        string taskId,
        string agentId,
        CancellationToken cancellationToken = default);

    Task<GenerationTask> CompleteAsync(
        string taskId,
        string agentId,
        IEnumerable<string> relativeAssetPaths,
        CancellationToken cancellationToken = default);

    Task<GenerationTask> FailAsync(
        string taskId,
        string agentId,
        string failureMessage,
        CancellationToken cancellationToken = default);

    Task<GenerationTask> FailAsync(
        string taskId,
        string agentId,
        GenerationTaskFailure failure,
        CancellationToken cancellationToken = default);
}

namespace GAssetForge.Core.Generation;

public interface IGenerationTaskStore
{
    Task<GenerationTask> CreateAsync(
        CreateGenerationTaskRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GenerationTask>> CreateBatchAsync(
        CreateGenerationBatchRequest request,
        CancellationToken cancellationToken = default);

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
}

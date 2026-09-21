using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace GAssetForge.Core.Generation;

/// <summary>
/// A local, provider-neutral task queue backed by one versioned JSON file.
/// Mutations are serialized across store instances and processes, then committed by same-directory atomic rename.
/// </summary>
public sealed class JsonGenerationTaskStore : IGenerationTaskStore
{
    public const string StoreFileName = "generation-tasks.v1.json";

    public static readonly TimeSpan DefaultClaimLeaseDuration = TimeSpan.FromMinutes(30);

    private const string LockFileName = ".generation-tasks.lock";
    private const long MaximumStoreFileBytes = 64L * 1024L * 1024L;
    private const int MaximumTaskCount = 100_000;
    private static readonly TimeSpan FileLockTimeout = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _claimLeaseDuration;
    private readonly string _lockFilePath;
    private readonly SemaphoreSlim _processLock;

    public JsonGenerationTaskStore(
        string storageRoot,
        TimeProvider? timeProvider = null,
        TimeSpan? claimLeaseDuration = null)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            throw new ArgumentException("生成任务存储根目录不能为空。", nameof(storageRoot));
        }

        StorageRoot = Path.GetFullPath(storageRoot);
        var pathRoot = Path.GetPathRoot(StorageRoot);
        if (pathRoot is not null &&
            string.Equals(
                Path.TrimEndingDirectorySeparator(StorageRoot),
                Path.TrimEndingDirectorySeparator(pathRoot),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("生成任务存储根目录不能是文件系统根目录。", nameof(storageRoot));
        }

        if (File.Exists(StorageRoot))
        {
            throw new ArgumentException("生成任务存储根目录指向了文件。", nameof(storageRoot));
        }

        StoreFilePath = Path.Combine(StorageRoot, StoreFileName);
        _lockFilePath = Path.Combine(StorageRoot, LockFileName);
        _processLock = ProcessLocks.GetOrAdd(StoreFilePath, static _ => new SemaphoreSlim(1, 1));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _claimLeaseDuration = claimLeaseDuration ?? DefaultClaimLeaseDuration;
        if (_claimLeaseDuration <= TimeSpan.Zero || _claimLeaseDuration > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(
                nameof(claimLeaseDuration),
                "生成任务认领租约必须大于 0 且不超过 7 天。");
        }
    }

    public string StorageRoot { get; }

    public string StoreFilePath { get; }

    public async Task<GenerationTask> CreateAsync(
        CreateGenerationTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var safeRequest = GenerationJson.Clone(request);
        GenerationInputValidator.ValidateCreateRequest(safeRequest);

        return await WithExclusiveLockAsync(async token =>
        {
            var document = await LoadDocumentAndRecoverExpiredClaimsAsync(token).ConfigureAwait(false);
            if (document.Tasks.Count >= MaximumTaskCount)
            {
                throw new InvalidOperationException($"生成任务数量已达到上限 {MaximumTaskCount}。");
            }

            var now = GetUtcNow();
            var task = new GenerationTask
            {
                Id = Guid.NewGuid().ToString("N"),
                Revision = 1,
                DisplayName = safeRequest.DisplayName,
                RequestedBy = safeRequest.RequestedBy,
                Status = GenerationTaskStatus.Pending,
                Constraints = safeRequest.Constraints,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                DispatchIntent = new GenerationTaskDispatchIntent
                {
                    Requested = safeRequest.DispatchRequested,
                    IsRetry = safeRequest.IsRetryDispatch
                }
            };
            task = task with
            {
                RetryLineage = new GenerationTaskRetryLineage
                {
                    RootTaskId = task.Id,
                    AttemptNumber = 1,
                    MaxAttempts = safeRequest.MaxAttempts
                }
            };

            GenerationInputValidator.ValidatePersistedTask(task);
            document.Tasks.Add(task);
            await SaveDocumentAsync(document with { UpdatedAtUtc = now }, token).ConfigureAwait(false);
            return GenerationJson.Clone(task);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GenerationTask>> CreateBatchAsync(
        CreateGenerationBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var safeRequest = GenerationJson.Clone(request);
        GenerationInputValidator.ValidateCreateBatchRequest(safeRequest);

        return await WithExclusiveLockAsync(async token =>
        {
            var document = await LoadDocumentAndRecoverExpiredClaimsAsync(token).ConfigureAwait(false);
            if (document.Tasks.Any(task =>
                    string.Equals(task.BatchId, safeRequest.BatchId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"生成批次已经存在：{safeRequest.BatchId}。");
            }

            if (document.Tasks.Count > MaximumTaskCount - safeRequest.Items.Length)
            {
                throw new InvalidOperationException($"生成任务数量将超过上限 {MaximumTaskCount}。");
            }

            var now = GetUtcNow();
            var tasks = new GenerationTask[safeRequest.Items.Length];
            for (var index = 0; index < safeRequest.Items.Length; index++)
            {
                var item = safeRequest.Items[index];
                var task = new GenerationTask
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Revision = 1,
                    DisplayName = item.DisplayName,
                    RequestedBy = safeRequest.RequestedBy,
                    BatchId = safeRequest.BatchId,
                    BatchItemId = item.ItemId,
                    BatchItemIndex = index,
                    Status = GenerationTaskStatus.Pending,
                    Constraints = item.Constraints,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    DispatchIntent = new GenerationTaskDispatchIntent
                    {
                        Requested = safeRequest.DispatchRequested,
                        IsRetry = safeRequest.IsRetryDispatch
                    }
                };
                task = task with
                {
                    RetryLineage = new GenerationTaskRetryLineage
                    {
                        RootTaskId = task.Id,
                        AttemptNumber = 1,
                        MaxAttempts = item.MaxAttempts
                    }
                };

                GenerationInputValidator.ValidatePersistedTask(task);
                tasks[index] = task;
            }

            document.Tasks.AddRange(tasks);
            await SaveDocumentAsync(document with { UpdatedAtUtc = now }, token).ConfigureAwait(false);
            return tasks.Select(GenerationJson.Clone).ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CreateGenerationRetryBatchResult> CreateRetryBatchAsync(
        CreateGenerationRetryBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var safeRequest = GenerationJson.Clone(request);
        GenerationInputValidator.ValidateCreateRetryBatchRequest(safeRequest);

        return await WithExclusiveLockAsync(async token =>
        {
            var document = await LoadDocumentAndRecoverExpiredClaimsAsync(token).ConfigureAwait(false);
            var parents = safeRequest.Items
                .Select(item => RequireTask(document, item.ParentTaskId))
                .ToArray();
            var existingChildIds = parents
                .Select(parent => parent.SupersededByTaskId)
                .Where(childId => childId is not null)
                .ToArray();
            if (existingChildIds.Length > 0)
            {
                if (existingChildIds.Length != parents.Length)
                {
                    throw new InvalidOperationException(
                        "重试父任务中只有部分已被后继任务接替；请刷新任务状态后重新组织重试批次。");
                }

                var existingChildren = existingChildIds
                    .Select(childId => RequireTask(document, childId!))
                    .OrderBy(task => task.BatchItemIndex)
                    .ThenBy(task => task.Id, StringComparer.Ordinal)
                    .Select(GenerationJson.Clone)
                    .ToArray();
                return new CreateGenerationRetryBatchResult(false, existingChildren);
            }

            if (document.Tasks.Any(task =>
                    string.Equals(task.BatchId, safeRequest.BatchId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"生成重试批次已经存在：{safeRequest.BatchId}。");
            }

            if (document.Tasks.Count > MaximumTaskCount - safeRequest.Items.Length)
            {
                throw new InvalidOperationException($"生成任务数量将超过上限 {MaximumTaskCount}。");
            }

            foreach (var parent in parents)
            {
                if (parent.Status != GenerationTaskStatus.Failed)
                {
                    throw new InvalidOperationException(
                        $"任务 {parent.Id} 当前状态为 {parent.Status}，不能创建重试后继任务。");
                }

                if (parent.RetryLineage is not { } lineage)
                {
                    throw new InvalidOperationException(
                        $"历史任务 {parent.Id} 没有冻结重试策略，不能被自动重试。");
                }

                if (lineage.AttemptNumber >= lineage.MaxAttempts)
                {
                    throw new InvalidOperationException(
                        $"任务 {parent.Id} 已用尽 {lineage.MaxAttempts} 次生成尝试。");
                }

                if (!string.IsNullOrWhiteSpace(parent.AutomaticRetryBlockedReason) &&
                    !safeRequest.ManualOverride)
                {
                    throw new InvalidOperationException(
                        $"任务 {parent.Id} 的自动重做已阻断，需要人工接管后才能创建后继任务。");
                }

                if (parent.DispatchIntent is { Requested: false } && !safeRequest.ManualOverride)
                {
                    throw new InvalidOperationException(
                        $"任务 {parent.Id} 未启用自动调度，需要人工接管后才能创建后继任务。");
                }

                if ((parent.FailureRetryable != true ||
                     GenerationFailureClassifier.IsAgentInfrastructureFailureCode(parent.FailureCode)) &&
                    !safeRequest.ManualOverride)
                {
                    throw new InvalidOperationException(
                        $"任务 {parent.Id} 的失败属于不可自动重做范围，不能创建自动重试任务。");
                }
            }

            var now = GetUtcNow(parents.Max(parent => parent.UpdatedAtUtc));
            var children = new GenerationTask[safeRequest.Items.Length];
            for (var index = 0; index < safeRequest.Items.Length; index++)
            {
                var item = safeRequest.Items[index];
                var parent = parents[index];
                var parentLineage = parent.RetryLineage!;
                var child = new GenerationTask
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Revision = 1,
                    DisplayName = item.DisplayName,
                    RequestedBy = safeRequest.RequestedBy,
                    BatchId = safeRequest.BatchId,
                    BatchItemId = item.ItemId,
                    BatchItemIndex = index,
                    Status = GenerationTaskStatus.Pending,
                    Constraints = item.Constraints,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    DispatchIntent = new GenerationTaskDispatchIntent
                    {
                        Requested = safeRequest.DispatchRequested,
                        IsRetry = true
                    },
                    RetryLineage = new GenerationTaskRetryLineage
                    {
                        RootTaskId = parentLineage.RootTaskId,
                        PreviousTaskId = parent.Id,
                        AttemptNumber = parentLineage.AttemptNumber + 1,
                        MaxAttempts = parentLineage.MaxAttempts
                    }
                };
                GenerationInputValidator.ValidatePersistedTask(child);
                children[index] = child;
            }

            for (var index = 0; index < parents.Length; index++)
            {
                var parent = parents[index];
                var superseded = parent with
                {
                    Revision = parent.Revision + 1,
                    UpdatedAtUtc = now,
                    SupersededByTaskId = children[index].Id,
                    AutomaticRetryBlockedReason = null
                };
                GenerationInputValidator.ValidatePersistedTask(superseded);
                ReplaceTask(document, superseded);
            }

            document.Tasks.AddRange(children);
            await SaveDocumentAsync(document with { UpdatedAtUtc = now }, token).ConfigureAwait(false);
            return new CreateGenerationRetryBatchResult(
                true,
                children.Select(GenerationJson.Clone).ToArray());
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GenerationTask>> BlockAutomaticRetryAsync(
        IReadOnlyList<string> taskIds,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        var normalizedTaskIds = taskIds
            .Select(GenerationInputValidator.NormalizeTaskId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedTaskIds.Length == 0)
        {
            throw new ArgumentException("至少需要一个自动重做阻断任务。", nameof(taskIds));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var normalizedReason = reason.Trim();
        GenerationInputValidator.ValidateFailureMessage(normalizedReason);
        if (normalizedReason.Length > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(reason), "自动重做阻断原因不能超过 4096 个字符。");
        }

        return await WithExclusiveLockAsync(async token =>
        {
            var document = await LoadDocumentAndRecoverExpiredClaimsAsync(token).ConfigureAwait(false);
            var tasks = normalizedTaskIds.Select(taskId => RequireTask(document, taskId)).ToArray();
            var now = GetUtcNow(tasks.Max(task => task.UpdatedAtUtc));
            var blocked = new GenerationTask[tasks.Length];
            var changed = false;
            for (var index = 0; index < tasks.Length; index++)
            {
                var task = tasks[index];
                if (task.Status != GenerationTaskStatus.Failed ||
                    task.FailureRetryable != true ||
                    task.RetryLineage is not { } lineage ||
                    lineage.AttemptNumber >= lineage.MaxAttempts ||
                    task.SupersededByTaskId is not null)
                {
                    throw new InvalidOperationException(
                        $"任务 {task.Id} 不是仍有额度且尚未被接替的可恢复失败，不能阻断自动重做。");
                }

                if (string.Equals(task.AutomaticRetryBlockedReason, normalizedReason, StringComparison.Ordinal))
                {
                    blocked[index] = GenerationJson.Clone(task);
                    continue;
                }

                var updated = task with
                {
                    Revision = task.Revision + 1,
                    UpdatedAtUtc = now,
                    AutomaticRetryBlockedReason = normalizedReason
                };
                GenerationInputValidator.ValidatePersistedTask(updated);
                ReplaceTask(document, updated);
                blocked[index] = GenerationJson.Clone(updated);
                changed = true;
            }

            if (changed)
            {
                await SaveDocumentAsync(document with { UpdatedAtUtc = now }, token).ConfigureAwait(false);
            }

            return (IReadOnlyList<GenerationTask>)blocked;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GenerationTask>> SuspendAutomaticDispatchAsync(
        IReadOnlyList<string> taskIds,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        var normalizedTaskIds = taskIds
            .Select(GenerationInputValidator.NormalizeTaskId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedTaskIds.Length == 0)
        {
            throw new ArgumentException("至少需要一个需要暂停自动调度的任务。", nameof(taskIds));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var normalizedReason = reason.Trim();
        GenerationInputValidator.ValidateFailureMessage(normalizedReason);
        if (normalizedReason.Length > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(reason), "暂停自动调度原因不能超过 4096 个字符。");
        }

        return await WithExclusiveLockAsync(async token =>
        {
            // Suspending scheduling must not renew or expire any claim, including unselected tasks.
            var document = await LoadDocumentAsync(token).ConfigureAwait(false);
            var tasks = normalizedTaskIds.Select(taskId => RequireTask(document, taskId)).ToArray();
            var now = GetUtcNow(tasks.Max(task => task.UpdatedAtUtc));
            var suspended = new GenerationTask[tasks.Length];
            var changed = false;
            for (var index = 0; index < tasks.Length; index++)
            {
                var task = tasks[index];
                var canBlockRetry = task.Status == GenerationTaskStatus.Failed &&
                                    task.FailureRetryable == true &&
                                    task.RetryLineage is { } lineage &&
                                    lineage.AttemptNumber < lineage.MaxAttempts &&
                                    task.SupersededByTaskId is null;
                var blockedReason = canBlockRetry
                    ? task.AutomaticRetryBlockedReason ?? normalizedReason
                    : task.AutomaticRetryBlockedReason;
                if (task.DispatchIntent is { Requested: false } &&
                    string.Equals(task.AutomaticRetryBlockedReason, blockedReason, StringComparison.Ordinal))
                {
                    suspended[index] = GenerationJson.Clone(task);
                    continue;
                }

                var updated = task with
                {
                    Revision = task.Revision + 1,
                    // Claim expiry uses UpdatedAtUtc; a UI scheduling change is not a lease heartbeat.
                    UpdatedAtUtc = task.Status == GenerationTaskStatus.Claimed ? task.UpdatedAtUtc : now,
                    DispatchIntent = task.DispatchIntent is { } intent
                        ? intent with { Requested = false }
                        : new GenerationTaskDispatchIntent { Requested = false },
                    AutomaticRetryBlockedReason = blockedReason
                };
                GenerationInputValidator.ValidatePersistedTask(updated);
                ReplaceTask(document, updated);
                suspended[index] = GenerationJson.Clone(updated);
                changed = true;
            }

            if (changed)
            {
                await SaveDocumentAsync(document with { UpdatedAtUtc = now }, token).ConfigureAwait(false);
            }

            return (IReadOnlyList<GenerationTask>)suspended;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GenerationTask>> ListBatchAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        var normalizedBatchId = GenerationInputValidator.NormalizeBatchId(batchId);
        return await WithExclusiveLockAsync(async token =>
        {
            var document = await LoadDocumentAndRecoverExpiredClaimsAsync(token).ConfigureAwait(false);
            var tasks = document.Tasks
                .Where(task => string.Equals(task.BatchId, normalizedBatchId, StringComparison.Ordinal))
                .OrderBy(task => task.BatchItemIndex)
                .ThenBy(task => task.CreatedAtUtc)
                .ThenBy(task => task.Id, StringComparer.Ordinal)
                .Select(GenerationJson.Clone)
                .ToArray();
            return (IReadOnlyList<GenerationTask>)tasks;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GenerationTask>> ListAsync(
        GenerationTaskStatus? status = null,
        string? workflowId = null,
        CancellationToken cancellationToken = default)
    {
        if (status is { } value && !Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(status), "生成任务状态无效。");
        }

        if (workflowId is not null)
        {
            GenerationInputValidator.ValidateWorkflowId(workflowId);
        }

        return await WithExclusiveLockAsync(async token =>
        {
            var document = await LoadDocumentAndRecoverExpiredClaimsAsync(token).ConfigureAwait(false);
            var tasks = document.Tasks
                .Where(task => status is null || task.Status == status)
                .Where(task => workflowId is null ||
                    string.Equals(task.Constraints.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(task => task.CreatedAtUtc)
                .ThenBy(task => task.Id, StringComparer.Ordinal)
                .Select(GenerationJson.Clone)
                .ToArray();

            return (IReadOnlyList<GenerationTask>)tasks;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GenerationTask?> GetAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        var normalizedTaskId = GenerationInputValidator.NormalizeTaskId(taskId);

        return await WithExclusiveLockAsync(async token =>
        {
            var document = await LoadDocumentAndRecoverExpiredClaimsAsync(token).ConfigureAwait(false);
            var task = FindTask(document, normalizedTaskId);
            return task is null ? null : GenerationJson.Clone(task);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GenerationTask?> ClaimNextAsync(
        string agentId,
        string? workflowId = null,
        CancellationToken cancellationToken = default)
    {
        GenerationInputValidator.ValidateAgentId(agentId);
        if (workflowId is not null)
        {
            GenerationInputValidator.ValidateWorkflowId(workflowId);
        }

        return await WithExclusiveLockAsync(async token =>
        {
            var document = await LoadDocumentAndRecoverExpiredClaimsAsync(token).ConfigureAwait(false);
            var task = document.Tasks
                .Where(candidate => candidate.Status == GenerationTaskStatus.Pending)
                .Where(candidate => workflowId is null ||
                    string.Equals(candidate.Constraints.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(candidate => candidate.CreatedAtUtc)
                .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
                .FirstOrDefault();

            if (task is null)
            {
                return null;
            }

            var claimed = Claim(task, agentId, GetUtcNow());
            ReplaceTask(document, claimed);
            await SaveDocumentAsync(document with { UpdatedAtUtc = claimed.UpdatedAtUtc }, token).ConfigureAwait(false);
            return GenerationJson.Clone(claimed);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GenerationTask> ClaimAsync(
        string taskId,
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var normalizedTaskId = GenerationInputValidator.NormalizeTaskId(taskId);
        GenerationInputValidator.ValidateAgentId(agentId);

        return await WithExclusiveLockAsync(async token =>
        {
            var document = await LoadDocumentAndRecoverExpiredClaimsAsync(token).ConfigureAwait(false);
            var task = RequireTask(document, normalizedTaskId);

            if (task.Status == GenerationTaskStatus.Claimed &&
                string.Equals(task.ClaimedBy, agentId, StringComparison.Ordinal))
            {
                return GenerationJson.Clone(task);
            }

            var claimed = Claim(task, agentId, GetUtcNow());
            ReplaceTask(document, claimed);
            await SaveDocumentAsync(document with { UpdatedAtUtc = claimed.UpdatedAtUtc }, token).ConfigureAwait(false);
            return GenerationJson.Clone(claimed);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GenerationTask> RenewClaimAsync(
        string taskId,
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var normalizedTaskId = GenerationInputValidator.NormalizeTaskId(taskId);
        GenerationInputValidator.ValidateAgentId(agentId);

        return await WithExclusiveLockAsync(async token =>
        {
            var document = await LoadDocumentAndRecoverExpiredClaimsAsync(token).ConfigureAwait(false);
            var task = RequireTask(document, normalizedTaskId);
            RequireClaimOwnership(task, agentId);

            var now = GetUtcNow(task.UpdatedAtUtc);
            if (now == task.UpdatedAtUtc)
            {
                return GenerationJson.Clone(task);
            }

            var renewed = task with
            {
                Revision = task.Revision + 1,
                UpdatedAtUtc = now
            };

            GenerationInputValidator.ValidatePersistedTask(renewed);
            ReplaceTask(document, renewed);
            await SaveDocumentAsync(document with { UpdatedAtUtc = now }, token).ConfigureAwait(false);
            return GenerationJson.Clone(renewed);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GenerationTask> CompleteAsync(
        string taskId,
        string agentId,
        IEnumerable<string> relativeAssetPaths,
        CancellationToken cancellationToken = default)
    {
        var normalizedTaskId = GenerationInputValidator.NormalizeTaskId(taskId);
        GenerationInputValidator.ValidateAgentId(agentId);
        ArgumentNullException.ThrowIfNull(relativeAssetPaths);

        var normalizedPaths = relativeAssetPaths
            .Select(GenerationInputValidator.NormalizeRelativeAssetPath)
            .ToArray();

        if (normalizedPaths.Length == 0)
        {
            throw new ArgumentException("完成任务时至少要登记一个资产相对路径。", nameof(relativeAssetPaths));
        }

        if (normalizedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalizedPaths.Length)
        {
            throw new ArgumentException("完成任务时不能登记重复资产路径。", nameof(relativeAssetPaths));
        }

        return await WithExclusiveLockAsync(async token =>
        {
            var document = await LoadDocumentAndRecoverExpiredClaimsAsync(token).ConfigureAwait(false);
            var task = RequireTask(document, normalizedTaskId);

            if (task.Status == GenerationTaskStatus.Completed &&
                string.Equals(task.ClaimedBy, agentId, StringComparison.Ordinal) &&
                task.RelativeAssetPaths.SequenceEqual(normalizedPaths, StringComparer.Ordinal))
            {
                return GenerationJson.Clone(task);
            }

            RequireClaimOwnership(task, agentId);
            if (normalizedPaths.Length != task.Constraints.Output.Count)
            {
                throw new InvalidOperationException(
                    $"任务要求输出 {task.Constraints.Output.Count} 个资产，但登记了 {normalizedPaths.Length} 个路径。");
            }

            var now = GetUtcNow(task.UpdatedAtUtc);
            var completed = task with
            {
                Revision = task.Revision + 1,
                Status = GenerationTaskStatus.Completed,
                UpdatedAtUtc = now,
                CompletedAtUtc = now,
                RelativeAssetPaths = normalizedPaths
            };

            GenerationInputValidator.ValidatePersistedTask(completed);
            ReplaceTask(document, completed);
            await SaveDocumentAsync(document with { UpdatedAtUtc = now }, token).ConfigureAwait(false);
            return GenerationJson.Clone(completed);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GenerationTask> FailAsync(
        string taskId,
        string agentId,
        string failureMessage,
        CancellationToken cancellationToken = default)
    {
        var classified = GenerationFailureClassifier.Classify(failureMessage);
        return await FailAsync(taskId, agentId, classified, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GenerationTask> FailAsync(
        string taskId,
        string agentId,
        GenerationTaskFailure failure,
        CancellationToken cancellationToken = default)
    {
        var normalizedTaskId = GenerationInputValidator.NormalizeTaskId(taskId);
        GenerationInputValidator.ValidateAgentId(agentId);
        ArgumentNullException.ThrowIfNull(failure);
        var safeFailure = GenerationJson.Clone(failure);
        GenerationInputValidator.ValidateFailureMessage(safeFailure.Message);
        GenerationInputValidator.ValidateFailureCode(safeFailure.Code);
        if (safeFailure.SubCode is not null)
        {
            GenerationInputValidator.ValidateFailureCode(safeFailure.SubCode);
        }
        GenerationInputValidator.ValidateFailureStage(safeFailure.Stage);

        return await WithExclusiveLockAsync(async token =>
        {
            var document = await LoadDocumentAndRecoverExpiredClaimsAsync(token).ConfigureAwait(false);
            var task = RequireTask(document, normalizedTaskId);

            if (task.Status == GenerationTaskStatus.Failed &&
                string.Equals(task.ClaimedBy, agentId, StringComparison.Ordinal) &&
                string.Equals(task.FailureMessage, safeFailure.Message, StringComparison.Ordinal) &&
                string.Equals(task.FailureCode, safeFailure.Code, StringComparison.Ordinal) &&
                string.Equals(task.FailureSubCode, safeFailure.SubCode, StringComparison.Ordinal) &&
                string.Equals(task.FailureStage, safeFailure.Stage, StringComparison.Ordinal) &&
                task.FailureRetryable == safeFailure.Retryable)
            {
                return GenerationJson.Clone(task);
            }

            RequireClaimOwnership(task, agentId);
            var now = GetUtcNow(task.UpdatedAtUtc);
            var failed = task with
            {
                Revision = task.Revision + 1,
                Status = GenerationTaskStatus.Failed,
                UpdatedAtUtc = now,
                FailedAtUtc = now,
                FailureMessage = safeFailure.Message,
                FailureCode = safeFailure.Code,
                FailureSubCode = safeFailure.SubCode,
                FailureStage = safeFailure.Stage,
                FailureRetryable = safeFailure.Retryable
            };

            GenerationInputValidator.ValidatePersistedTask(failed);
            ReplaceTask(document, failed);
            await SaveDocumentAsync(document with { UpdatedAtUtc = now }, token).ConfigureAwait(false);
            return GenerationJson.Clone(failed);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> WithExclusiveLockAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await _processLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(StorageRoot);
            await using var fileLock = await AcquireFileLockAsync(cancellationToken).ConfigureAwait(false);
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _processLock.Release();
        }
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var delay = TimeSpan.FromMilliseconds(20);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (stopwatch.Elapsed < FileLockTimeout)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                var nextMilliseconds = Math.Min(delay.TotalMilliseconds * 2d, 250d);
                delay = TimeSpan.FromMilliseconds(nextMilliseconds);
            }
            catch (IOException exception)
            {
                throw new TimeoutException($"等待生成任务存储锁超时：{_lockFilePath}", exception);
            }
        }
    }

    private async Task<GenerationTaskStoreDocument> LoadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StoreFilePath))
        {
            return new GenerationTaskStoreDocument { UpdatedAtUtc = GetUtcNow() };
        }

        var fileInfo = new FileInfo(StoreFilePath);
        if (fileInfo.Length > MaximumStoreFileBytes)
        {
            throw new InvalidDataException($"生成任务存储超过 {MaximumStoreFileBytes} 字节上限。");
        }

        try
        {
            await using var stream = new FileStream(
                StoreFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var document = await JsonSerializer.DeserializeAsync<GenerationTaskStoreDocument>(
                stream,
                GenerationJson.Options,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("生成任务存储 JSON 为空。");

            ValidateDocument(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"无法读取生成任务存储：{StoreFilePath}", exception);
        }
    }

    private async Task<GenerationTaskStoreDocument> LoadDocumentAndRecoverExpiredClaimsAsync(
        CancellationToken cancellationToken)
    {
        var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var documentUpdatedAtUtc = document.UpdatedAtUtc;
        var recoveredAny = false;

        for (var index = 0; index < document.Tasks.Count; index++)
        {
            var task = document.Tasks[index];
            if (!IsClaimExpired(task, now))
            {
                continue;
            }

            var recoveredAtUtc = now < task.UpdatedAtUtc ? task.UpdatedAtUtc : now;
            var recovered = task with
            {
                Revision = task.Revision + 1,
                Status = GenerationTaskStatus.Pending,
                ClaimedBy = null,
                ClaimedAtUtc = null,
                UpdatedAtUtc = recoveredAtUtc
            };

            GenerationInputValidator.ValidatePersistedTask(recovered);
            document.Tasks[index] = recovered;
            if (recoveredAtUtc > documentUpdatedAtUtc)
            {
                documentUpdatedAtUtc = recoveredAtUtc;
            }

            recoveredAny = true;
        }

        if (recoveredAny)
        {
            document = document with { UpdatedAtUtc = documentUpdatedAtUtc };
            await SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);
        }

        return document;
    }

    private async Task SaveDocumentAsync(
        GenerationTaskStoreDocument document,
        CancellationToken cancellationToken)
    {
        ValidateDocument(document);
        var temporaryPath = Path.Combine(StorageRoot, $".{StoreFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    GenerationJson.Options,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, StoreFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ValidateDocument(GenerationTaskStoreDocument document)
    {
        if (document.SchemaVersion != GenerationTaskStoreDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"不支持的生成任务存储版本：{document.SchemaVersion}。");
        }

        if (document.Tasks is null)
        {
            throw new InvalidDataException("生成任务存储缺少任务集合。");
        }

        if (document.Tasks.Count > MaximumTaskCount)
        {
            throw new InvalidDataException($"生成任务数量超过上限 {MaximumTaskCount}。");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var batchItemIds = new HashSet<string>(StringComparer.Ordinal);
        var batchIndexes = new HashSet<string>(StringComparer.Ordinal);
        var newestTaskUpdate = DateTimeOffset.MinValue;
        foreach (var task in document.Tasks)
        {
            if (task is null)
            {
                throw new InvalidDataException("生成任务存储包含空任务。");
            }

            GenerationInputValidator.ValidatePersistedTask(task);
            if (!ids.Add(task.Id))
            {
                throw new InvalidDataException($"生成任务 ID 重复：{task.Id}。");
            }

            if (task.BatchId is not null)
            {
                var batchItemKey = $"{task.BatchId}/{task.BatchItemId}";
                if (!batchItemIds.Add(batchItemKey))
                {
                    throw new InvalidDataException($"生成批次项目 ID 重复：{batchItemKey}。");
                }

                var batchIndexKey = $"{task.BatchId}/{task.BatchItemIndex}";
                if (!batchIndexes.Add(batchIndexKey))
                {
                    throw new InvalidDataException($"生成批次项目索引重复：{batchIndexKey}。");
                }
            }

            if (task.UpdatedAtUtc > newestTaskUpdate)
            {
                newestTaskUpdate = task.UpdatedAtUtc;
            }
        }

        var tasksById = document.Tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        foreach (var task in document.Tasks.Where(task => task.RetryLineage is not null))
        {
            var lineage = task.RetryLineage!;
            if (!tasksById.ContainsKey(lineage.RootTaskId))
            {
                throw new InvalidDataException(
                    $"生成任务 {task.Id} 的重试根任务不存在：{lineage.RootTaskId}。");
            }

            if (lineage.PreviousTaskId is { } previousTaskId)
            {
                if (!tasksById.TryGetValue(previousTaskId, out var previous) ||
                    !string.Equals(previous.SupersededByTaskId, task.Id, StringComparison.Ordinal) ||
                    previous.RetryLineage is not { } previousLineage ||
                    previousLineage.AttemptNumber + 1 != lineage.AttemptNumber ||
                    previousLineage.MaxAttempts != lineage.MaxAttempts ||
                    !string.Equals(previousLineage.RootTaskId, lineage.RootTaskId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"生成任务 {task.Id} 与上一重试任务 {previousTaskId} 的谱系不一致。");
                }
            }

            if (task.SupersededByTaskId is { } childTaskId &&
                (!tasksById.TryGetValue(childTaskId, out var child) ||
                 child.RetryLineage?.PreviousTaskId != task.Id))
            {
                throw new InvalidDataException(
                    $"生成任务 {task.Id} 的后继任务不存在或谱系不一致：{childTaskId}。");
            }
        }

        if (document.UpdatedAtUtc == default || document.UpdatedAtUtc < newestTaskUpdate)
        {
            throw new InvalidDataException("生成任务存储更新时间无效。");
        }
    }

    private static GenerationTask Claim(GenerationTask task, string agentId, DateTimeOffset now)
    {
        if (task.Status != GenerationTaskStatus.Pending)
        {
            throw new InvalidOperationException($"任务 {task.Id} 当前状态为 {task.Status}，不能被认领。");
        }

        if (now < task.UpdatedAtUtc)
        {
            now = task.UpdatedAtUtc;
        }

        var claimed = task with
        {
            Revision = task.Revision + 1,
            Status = GenerationTaskStatus.Claimed,
            ClaimedBy = agentId,
            ClaimedAtUtc = now,
            UpdatedAtUtc = now
        };

        GenerationInputValidator.ValidatePersistedTask(claimed);
        return claimed;
    }

    private bool IsClaimExpired(GenerationTask task, DateTimeOffset now)
    {
        if (task.Status != GenerationTaskStatus.Claimed || task.ClaimedAtUtc is null)
        {
            return false;
        }

        DateTimeOffset expiresAtUtc;
        try
        {
            expiresAtUtc = task.UpdatedAtUtc.Add(_claimLeaseDuration);
        }
        catch (ArgumentOutOfRangeException)
        {
            expiresAtUtc = DateTimeOffset.MaxValue;
        }

        return now >= expiresAtUtc;
    }

    private static void RequireClaimOwnership(GenerationTask task, string agentId)
    {
        if (task.Status != GenerationTaskStatus.Claimed)
        {
            throw new InvalidOperationException($"任务 {task.Id} 当前状态为 {task.Status}，不能结束。");
        }

        if (!string.Equals(task.ClaimedBy, agentId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"任务 {task.Id} 已由 Agent {task.ClaimedBy} 认领。");
        }
    }

    private static GenerationTask? FindTask(GenerationTaskStoreDocument document, string taskId) =>
        document.Tasks.FirstOrDefault(task => string.Equals(task.Id, taskId, StringComparison.Ordinal));

    private static GenerationTask RequireTask(GenerationTaskStoreDocument document, string taskId) =>
        FindTask(document, taskId) ?? throw new KeyNotFoundException($"生成任务不存在：{taskId}。");

    private static void ReplaceTask(GenerationTaskStoreDocument document, GenerationTask replacement)
    {
        var index = document.Tasks.FindIndex(task => string.Equals(task.Id, replacement.Id, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new KeyNotFoundException($"生成任务不存在：{replacement.Id}。");
        }

        document.Tasks[index] = replacement;
    }

    private DateTimeOffset GetUtcNow(DateTimeOffset? minimum = null)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        return minimum is { } value && now < value ? value : now;
    }
}

internal sealed record GenerationTaskStoreDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public DateTimeOffset UpdatedAtUtc { get; init; }

    public List<GenerationTask> Tasks { get; init; } = [];
}

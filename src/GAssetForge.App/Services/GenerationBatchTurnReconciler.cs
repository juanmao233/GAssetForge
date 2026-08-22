using System.Text;
using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.Core.Generation;
using GAssetForge.Core;

namespace GAssetForge.App.Services;

internal sealed record GenerationBatchTurnReconciliationResult(
    string BatchId,
    int TotalCount,
    int PendingCount,
    int ClaimedCount,
    int CompletedCount,
    int FailedCount,
    int AutoFailedCount,
    string? FailureDetail)
{
    public bool AllTerminal => TotalCount > 0 && PendingCount == 0 && ClaimedCount == 0;
}

/// <summary>
/// Reconciles durable generation-task state after an Agent Turn has ended. A transport-level
/// completed Turn is not proof that any task was claimed, generated, or validated.
/// </summary>
internal sealed class GenerationBatchTurnReconciler
{
    public const string DispatchProxyAgentId = ProductIdentity.DispatchReconcilerAgentId;

    private const int MaximumFailureDetailLength = 8_000;
    private readonly IGenerationTaskStore _taskStore;

    public GenerationBatchTurnReconciler(IGenerationTaskStore taskStore)
    {
        _taskStore = taskStore ?? throw new ArgumentNullException(nameof(taskStore));
    }

    public async Task<GenerationBatchTurnReconciliationResult> ReconcileAsync(
        string batchId,
        AgentTurnResult? turnResult,
        string? failureMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        var initialTasks = await _taskStore.ListBatchAsync(batchId, cancellationToken)
            .ConfigureAwait(false);
        if (initialTasks.Count == 0)
        {
            return new GenerationBatchTurnReconciliationResult(
                batchId,
                TotalCount: 0,
                PendingCount: 0,
                ClaimedCount: 0,
                CompletedCount: 0,
                FailedCount: 0,
                AutoFailedCount: 0,
                FailureDetail: $"未找到批次 {batchId} 的生成任务，无法确认终态。");
        }

        var pendingFailureDetail = BuildPendingFailureDetail(turnResult, failureMessage);
        var reconciliationFailures = new List<string>();
        var autoFailedCount = 0;
        foreach (var task in initialTasks.Where(task => task.Status == GenerationTaskStatus.Pending))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _taskStore.ClaimAsync(task.Id, DispatchProxyAgentId, cancellationToken)
                    .ConfigureAwait(false);
                await _taskStore.FailAsync(
                        task.Id,
                        DispatchProxyAgentId,
                        pendingFailureDetail,
                        cancellationToken)
                    .ConfigureAwait(false);
                autoFailedCount++;
            }
            catch (InvalidOperationException exception)
            {
                // The Agent may have claimed or completed the task after the initial snapshot.
                // Never steal an active claim or rewrite a terminal result.
                var current = await _taskStore.GetAsync(task.Id, cancellationToken).ConfigureAwait(false);
                if (current is null || current.Status == GenerationTaskStatus.Pending)
                {
                    throw;
                }

                if (current.Status == GenerationTaskStatus.Claimed &&
                    ProductIdentity.IsDispatchReconcilerAgent(current.ClaimedBy))
                {
                    reconciliationFailures.Add(
                        $"任务 {ShortId(task.Id)} 已由调度代理认领，但写入失败状态时出错：{Sanitize(exception.Message)}");
                }
            }
        }

        var finalTasks = await _taskStore.ListBatchAsync(batchId, cancellationToken)
            .ConfigureAwait(false);
        var pendingCount = finalTasks.Count(task => task.Status == GenerationTaskStatus.Pending);
        var claimedCount = finalTasks.Count(task => task.Status == GenerationTaskStatus.Claimed);
        var completedCount = finalTasks.Count(task => task.Status == GenerationTaskStatus.Completed);
        var failedCount = finalTasks.Count(task => task.Status == GenerationTaskStatus.Failed);
        var detailParts = new List<string>();
        if (autoFailedCount > 0)
        {
            detailParts.Add(pendingFailureDetail);
        }
        else if (!string.IsNullOrWhiteSpace(failureMessage) || turnResult?.IsSuccess == false)
        {
            detailParts.Add(BuildTurnFailureDetail(turnResult, failureMessage));
        }

        if (claimedCount > 0)
        {
            detailParts.Add($"仍有 {claimedCount} 个任务处于 Claimed；为避免破坏 Agent 所有权，收束服务未改写这些任务。");
        }

        if (pendingCount > 0)
        {
            detailParts.Add($"仍有 {pendingCount} 个任务处于 Pending，批次尚未完全收束。");
        }

        detailParts.AddRange(reconciliationFailures);
        return new GenerationBatchTurnReconciliationResult(
            batchId,
            finalTasks.Count,
            pendingCount,
            claimedCount,
            completedCount,
            failedCount,
            autoFailedCount,
            JoinDetails(detailParts));
    }

    private static string BuildPendingFailureDetail(
        AgentTurnResult? turnResult,
        string? failureMessage)
    {
        var explicitFailure = Sanitize(failureMessage);
        if (!string.IsNullOrWhiteSpace(explicitFailure))
        {
            return Limit(
                $"Agent Turn 已结束，但任务未被 Agent 领取或完成。调度失败：{explicitFailure}");
        }

        if (turnResult is null)
        {
            return "Agent Turn 已结束但未返回结果，任务未被 Agent 领取或完成。";
        }

        var turnId = Sanitize(turnResult.TurnId);
        var turnLabel = string.IsNullOrWhiteSpace(turnId) ? "Agent Turn" : $"Agent Turn {turnId}";
        if (turnResult.IsSuccess)
        {
            return Limit($"{turnLabel} 已报告 completed，但任务未被 Agent 领取或完成。");
        }

        var error = Sanitize(turnResult.ErrorMessage);
        var suffix = string.IsNullOrWhiteSpace(error) ? string.Empty : $"：{error}";
        return Limit(
            $"{turnLabel} 以 {Sanitize(turnResult.Status) ?? "未知状态"} 结束{suffix}；任务未被 Agent 领取或完成。");
    }

    private static string BuildTurnFailureDetail(
        AgentTurnResult? turnResult,
        string? failureMessage)
    {
        var explicitFailure = Sanitize(failureMessage);
        if (!string.IsNullOrWhiteSpace(explicitFailure))
        {
            return Limit($"Agent Turn 调度失败：{explicitFailure}");
        }

        if (turnResult is null)
        {
            return "Agent Turn 未返回结果。";
        }

        var error = Sanitize(turnResult.ErrorMessage);
        var suffix = string.IsNullOrWhiteSpace(error) ? string.Empty : $"：{error}";
        return Limit($"Agent Turn 以 {Sanitize(turnResult.Status) ?? "未知状态"} 结束{suffix}。");
    }

    private static string? JoinDetails(IEnumerable<string> details)
    {
        var normalized = details
            .Select(Sanitize)
            .Where(detail => !string.IsNullOrWhiteSpace(detail))
            .ToArray();
        return normalized.Length == 0
            ? null
            : Limit(string.Join(" ", normalized!));
    }

    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (!char.IsControl(character) || character is '\r' or '\n' or '\t')
            {
                builder.Append(character);
            }
        }

        var sanitized = builder.ToString().Trim();
        return sanitized.Length == 0 ? null : Limit(sanitized);
    }

    private static string Limit(string value) => value.Length <= MaximumFailureDetailLength
        ? value
        : value[..MaximumFailureDetailLength];

    private static string ShortId(string value) => value.Length <= 12 ? value : value[..12];
}

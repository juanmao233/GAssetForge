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

        var turnFailure = AgentTurnFailureClassifier.Classify(turnResult, failureMessage);
        var finalTasks = await _taskStore.ListBatchAsync(batchId, cancellationToken)
            .ConfigureAwait(false);
        var pendingCount = finalTasks.Count(task => task.Status == GenerationTaskStatus.Pending);
        var claimedCount = finalTasks.Count(task => task.Status == GenerationTaskStatus.Claimed);
        var completedCount = finalTasks.Count(task => task.Status == GenerationTaskStatus.Completed);
        var failedCount = finalTasks.Count(task => task.Status == GenerationTaskStatus.Failed);
        var detailParts = new List<string>();
        if (turnFailure?.PreservePendingTasks == true)
        {
            detailParts.Add(BuildTransportInterruptionDetail(turnFailure, turnResult, failureMessage));
        }
        else if (pendingCount > 0)
        {
            detailParts.Add(BuildPendingPreservationDetail(
                turnResult,
                failureMessage,
                pendingCount));
        }
        else if (!string.IsNullOrWhiteSpace(failureMessage) || turnResult?.IsSuccess == false)
        {
            detailParts.Add(BuildTurnFailureDetail(turnResult, failureMessage));
        }

        if (claimedCount > 0)
        {
            detailParts.Add($"仍有 {claimedCount} 个任务处于 Claimed；为避免破坏 Agent 所有权，收束服务未改写这些任务。");
        }

        return new GenerationBatchTurnReconciliationResult(
            batchId,
            finalTasks.Count,
            pendingCount,
            claimedCount,
            completedCount,
            failedCount,
            AutoFailedCount: 0,
            JoinDetails(detailParts));
    }

    private static string BuildPendingPreservationDetail(
        AgentTurnResult? turnResult,
        string? failureMessage,
        int pendingCount)
    {
        var turnDetail = BuildPendingFailureDetail(turnResult, failureMessage);
        return Limit(
            $"{turnDetail} 仍有 {pendingCount} 个任务保持 Pending；" +
            "GAssetForge 未创建替代任务，等待连接恢复或下一次原任务调度。");
    }

    private static string BuildTransportInterruptionDetail(
        AgentTurnFailureClassification failure,
        AgentTurnResult? turnResult,
        string? failureMessage)
    {
        var detail = Sanitize(failureMessage) ?? Sanitize(turnResult?.ErrorMessage);
        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" 原始错误：{detail}";
        return Limit(
            $"{failure.Code}: 上游模型响应流在 response.completed 前中断；" +
            $"未开始的任务已保持 Pending，等待连接恢复后继续调度。{suffix}");
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

}

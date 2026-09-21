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
    public GenerationDispatchFailure? Diagnostic { get; init; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.Now;

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
        CancellationToken cancellationToken = default,
        GenerationDispatchFailure? diagnostic = null)
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
                FailureDetail: $"未找到批次 {batchId} 的生成任务，无法确认终态。")
            {
                Diagnostic = diagnostic,
                OccurredAt = diagnostic?.OccurredAt ?? DateTimeOffset.Now
            };
        }

        var turnFailure = AgentTurnFailureClassifier.Classify(turnResult, failureMessage);
        var finalTasks = await _taskStore.ListBatchAsync(batchId, cancellationToken)
            .ConfigureAwait(false);
        var pendingCount = finalTasks.Count(task => task.Status == GenerationTaskStatus.Pending);
        var claimedCount = finalTasks.Count(task => task.Status == GenerationTaskStatus.Claimed);
        var completedCount = finalTasks.Count(task => task.Status == GenerationTaskStatus.Completed);
        var failedCount = finalTasks.Count(task => task.Status == GenerationTaskStatus.Failed);
        var detailParts = new List<string>();
        foreach (var failedTask in finalTasks.Where(task => task.Status == GenerationTaskStatus.Failed).Take(3))
        {
            // A provider turn can complete normally after reporting a failed image tool. The
            // persisted task error must still reach the final workbench summary.
            detailParts.Add($"任务 {failedTask.Id} 失败 " +
                $"[{Sanitize(failedTask.FailureCode) ?? "generation_failed"} / " +
                $"{Sanitize(failedTask.FailureStage) ?? "unknown"}]：" +
                (Sanitize(failedTask.FailureMessage) ?? "任务未提供失败详情。"));
        }

        if (failedCount > 3)
        {
            detailParts.Add($"另有 {failedCount - 3} 个失败任务，请查看任务记录。");
        }

        if (diagnostic is not null)
        {
            detailParts.Add(pendingCount > 0
                ? BuildPendingPreservationDetail(turnResult, failureMessage, pendingCount, diagnostic)
                : diagnostic.UserMessage);
        }
        else if (turnFailure?.PreservePendingTasks == true)
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
            JoinDetails(detailParts))
        {
            Diagnostic = diagnostic,
            OccurredAt = diagnostic?.OccurredAt ?? DateTimeOffset.Now
        };
    }

    private static string BuildPendingPreservationDetail(
        AgentTurnResult? turnResult,
        string? failureMessage,
        int pendingCount,
        GenerationDispatchFailure? diagnostic = null)
    {
        var turnDetail = diagnostic?.UserMessage ?? BuildPendingFailureDetail(turnResult, failureMessage);
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
                turnResult is null
                    ? $"调度失败，未获得 Agent Turn 的启动或完成结果：{explicitFailure}"
                    : $"Agent Turn 调度或执行失败，任务未被 Agent 领取或完成：{explicitFailure}");
        }

        if (turnResult is null)
        {
            return "未获得 Agent Turn 的启动或完成结果，任务尚未被 Agent 领取或完成。";
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

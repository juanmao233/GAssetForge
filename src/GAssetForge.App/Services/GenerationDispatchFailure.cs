using GAssetForge.App.Services.AgentRuntime;

namespace GAssetForge.App.Services;

/// <summary>
/// A dispatch failure is separate from a durable generation-task failure. It records the actual
/// failing operation and retains the exception until the workbench logger has captured it.
/// </summary>
internal sealed record GenerationDispatchFailure(
    string Code,
    string Stage,
    string Message,
    string RecoveryHint,
    string? ThreadId = null,
    string? TurnId = null,
    string? ReadinessState = null,
    Exception? Exception = null)
{
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.Now;

    public bool TurnStarted { get; init; }

    public string UserMessage
    {
        get
        {
            var summary = TurnStarted
                ? "Agent Turn 执行失败"
                : Stage is "session_preparation" or "dispatch_preparation" or "image_preflight"
                    ? "调度准备失败，尚未启动 Agent Turn"
                    : "Agent Turn 未启动或尚未收到启动确认";
            return WorkbenchConsoleService.RedactCommonCredentials(
                $"{summary} [{Code} / {Stage}]：{Message} 建议：{RecoveryHint}");
        }
    }

    public static GenerationDispatchFailure FromException(
        Exception exception,
        string stage,
        bool turnStarted = false,
        string? threadId = null,
        string? turnId = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is AgentReadinessException readiness)
            {
                return new GenerationDispatchFailure(
                    readiness.Code,
                    readiness.Stage,
                    exception.Message,
                    readiness.RecoveryHint,
                    readiness.ThreadId ?? threadId,
                    turnId,
                    readiness.StateSummary,
                    exception)
                {
                    OccurredAt = readiness.OccurredAt,
                    TurnStarted = false
                };
            }
        }

        var transport = AgentTurnFailureClassifier.Classify(null, exception.Message);
        return new GenerationDispatchFailure(
            transport?.Code ?? (stage switch
            {
                "session_preparation" => "agent_session_preparation_failed",
                "image_preflight" => "image_delivery_preflight_failed",
                "turn_submission" => "agent_turn_submission_failed",
                "turn_execution" => "agent_turn_execution_failed",
                _ => "generation_dispatch_preparation_failed"
            }),
            stage,
            exception.Message,
            transport?.PreservePendingTasks == true
                ? "检查 Agent 连接并恢复原任务；未领取的任务保持 Pending，无需创建替代任务。"
                : "根据错误码和原始错误检查当前连接或运行环境；原任务及已有文件保持不变。",
            threadId,
            turnId,
            Exception: exception)
        {
            TurnStarted = turnStarted
        };
    }

    public static GenerationDispatchFailure FromTurnResult(AgentTurnResult result)
    {
        var message = string.IsNullOrWhiteSpace(result.ErrorMessage)
            ? $"Agent 返回状态 {result.Status}，未提供错误详情。"
            : result.ErrorMessage;
        var transport = AgentTurnFailureClassifier.Classify(result, message);
        var started = !string.IsNullOrWhiteSpace(result.TurnId);
        return new GenerationDispatchFailure(
            transport?.Code ?? (started ? "agent_turn_execution_failed" : "agent_turn_submission_failed"),
            started ? "turn_execution" : "turn_submission",
            message,
            "检查 Agent 连接及原始错误后恢复原任务；最终产物状态仍以任务库和本地文件为准。",
            result.ThreadId,
            result.TurnId)
        {
            TurnStarted = started
        };
    }
}

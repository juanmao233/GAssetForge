using GAssetForge.App.Services.AgentRuntime;

namespace GAssetForge.App.ViewModels;

public sealed record GenerationAgentFeedbackEntry(
    DateTimeOffset Timestamp,
    string CategoryText,
    string Message,
    bool IsWarning)
{
    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss");
}

internal enum GenerationAgentHealthState
{
    Idle,
    Healthy,
    Waiting,
    Slow,
    Stalled,
    Disconnected
}

internal sealed record GenerationAgentHealthSnapshot(
    GenerationAgentHealthState State,
    string StatusText,
    bool HasRisk,
    string RiskText);

internal static class GenerationAgentHealthEvaluator
{
    internal static readonly TimeSpan WaitingThreshold = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan SlowThreshold = TimeSpan.FromSeconds(90);
    internal static readonly TimeSpan StalledThreshold = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan ClaimLeaseWarningThreshold = TimeSpan.FromMinutes(25);

    internal static GenerationAgentHealthSnapshot Evaluate(
        DateTimeOffset nowUtc,
        bool isBatchActive,
        bool isAgentConnected,
        bool isTurnRunning,
        DateTimeOffset? dispatchStartedAtUtc,
        DateTimeOffset? lastAgentActivityUtc,
        DateTimeOffset? lastOutputActivityUtc,
        DateTimeOffset? oldestClaimActivityUtc,
        int generatedImageCount,
        int requestedImageCount)
    {
        if (!isBatchActive)
        {
            return new GenerationAgentHealthSnapshot(
                GenerationAgentHealthState.Idle,
                requestedImageCount > 0
                    ? "当前批次没有正在运行的 Agent Turn"
                    : "尚未开始监听 Agent 回传",
                false,
                string.Empty);
        }

        if (!isAgentConnected)
        {
            var lastObservedActivityUtc = Latest(lastAgentActivityUtc, lastOutputActivityUtc);
            var statusText = lastObservedActivityUtc is { } observedAt
                ? $"Agent 链路已断开 · 最近活动 {FormatDuration(NonNegative(nowUtc - observedAt))}前"
                : "Agent 链路已断开";
            return new GenerationAgentHealthSnapshot(
                GenerationAgentHealthState.Disconnected,
                statusText,
                true,
                $"Agent 连接不可用；已落盘 {generatedImageCount}/{requestedImageCount} 张会保留，任务不会因短时断连被自动判失败。请检查 MCP Agent 接入与指令日志。");
        }

        if (oldestClaimActivityUtc is { } claimActivityAt)
        {
            var leaseIdleAge = NonNegative(nowUtc - claimActivityAt);
            if (leaseIdleAge >= ClaimLeaseWarningThreshold)
            {
                return new GenerationAgentHealthSnapshot(
                    GenerationAgentHealthState.Slow,
                    $"任务仍在执行 · 认领心跳已静默 {FormatDuration(leaseIdleAge)}",
                    true,
                    $"任务认领心跳已接近 30 分钟租约边界。当前已落盘 {generatedImageCount}/{requestedImageCount} 张；请检查 Agent 与本地任务存储的续租是否仍在运行，避免任务回收后重新进入 Pending。");
            }
        }

        var latestActivityUtc = Latest(dispatchStartedAtUtc, lastAgentActivityUtc, lastOutputActivityUtc);
        if (latestActivityUtc is null)
        {
            return new GenerationAgentHealthSnapshot(
                GenerationAgentHealthState.Waiting,
                "等待 Agent 首次回传",
                false,
                string.Empty);
        }

        var age = NonNegative(nowUtc - latestActivityUtc.Value);
        if (age < WaitingThreshold)
        {
            return new GenerationAgentHealthSnapshot(
                GenerationAgentHealthState.Healthy,
                $"Agent 活跃 · 最近活动 {FormatDuration(age)}前",
                false,
                string.Empty);
        }

        if (age < SlowThreshold)
        {
            return new GenerationAgentHealthSnapshot(
                GenerationAgentHealthState.Waiting,
                isTurnRunning
                    ? $"正在等待图像模型或工具返回 · {FormatDuration(age)} 无新消息"
                    : $"等待 Agent 下一次回传 · {FormatDuration(age)} 无新消息",
                false,
                string.Empty);
        }

        if (age < StalledThreshold)
        {
            return new GenerationAgentHealthSnapshot(
                GenerationAgentHealthState.Slow,
                $"Agent 回传较慢 · {FormatDuration(age)} 无新活动",
                true,
                $"长图像生成可能仍在正常执行，但已经超过 {FormatDuration(SlowThreshold)} 没有 Agent 文本或新图片。建议继续观察连接状态与日志；系统不会自动中止或重试。");
        }

        return new GenerationAgentHealthSnapshot(
            GenerationAgentHealthState.Stalled,
            $"长时间无回传 · {FormatDuration(age)}",
            true,
            $"超过 {FormatDuration(StalledThreshold)} 没有 Agent 文本、工具活动或新图片落盘。可能是模型调用过慢、Agent 卡住或链路半断开；请检查 MCP Agent 接入、指令日志与输出目录。");
    }

    internal static string CategoryText(AgentActivityCategory category) => category switch
    {
        AgentActivityCategory.TurnStarted => "Turn",
        AgentActivityCategory.TurnCompleted => "Turn",
        AgentActivityCategory.AgentMessage => "Agent",
        AgentActivityCategory.Plan => "计划",
        AgentActivityCategory.Reasoning => "分析",
        AgentActivityCategory.ToolStarted => "工具",
        AgentActivityCategory.ToolCompleted => "工具",
        AgentActivityCategory.CommandStarted => "命令",
        AgentActivityCategory.CommandCompleted => "命令",
        AgentActivityCategory.Warning => "警告",
        AgentActivityCategory.Error => "错误",
        _ => "活动"
    };

    private static DateTimeOffset? Latest(params DateTimeOffset?[] values) => values
        .Where(value => value is not null)
        .Select(value => value!.Value)
        .DefaultIfEmpty()
        .Max() is var latest && latest != default
            ? latest
            : null;

    private static TimeSpan NonNegative(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromSeconds(1))
        {
            return "0 秒";
        }

        if (duration < TimeSpan.FromMinutes(1))
        {
            return $"{Math.Floor(duration.TotalSeconds):0} 秒";
        }

        if (duration < TimeSpan.FromHours(1))
        {
            return $"{Math.Floor(duration.TotalMinutes):0} 分钟";
        }

        return $"{Math.Floor(duration.TotalHours):0} 小时 {duration.Minutes} 分钟";
    }
}

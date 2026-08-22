using GAssetForge.App.ViewModels;

namespace GAssetForge.SmokeTests;

internal static class GenerationAgentHealthSmokeTests
{
    public static void Run()
    {
        var started = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

        var healthy = Evaluate(started.AddSeconds(10), started, started.AddSeconds(5));
        Equal(GenerationAgentHealthState.Healthy, healthy.State, "新鲜 Agent 回传应判定为活跃");
        False(healthy.HasRisk, "活跃 Agent 不应显示风险");

        var waiting = Evaluate(started.AddSeconds(45), started, started);
        Equal(GenerationAgentHealthState.Waiting, waiting.State, "短时 ImageGen 等待不应误判卡死");
        False(waiting.HasRisk, "90 秒内等待不应显示风险");

        var slow = Evaluate(started.AddSeconds(95), started, started);
        Equal(GenerationAgentHealthState.Slow, slow.State, "超过慢反馈阈值应给出提示");
        True(slow.HasRisk, "慢反馈状态应显示可诊断风险");

        var stalled = Evaluate(started.AddMinutes(2).AddSeconds(1), started, started);
        Equal(GenerationAgentHealthState.Stalled, stalled.State, "两分钟无活动应判定为长时间无回传");
        True(stalled.RiskText.Contains("模型调用过慢", StringComparison.Ordinal) &&
             stalled.RiskText.Contains("链路半断开", StringComparison.Ordinal),
            "长时间无回传风险应列出慢模型与半断连两类原因");

        var outputHeartbeat = GenerationAgentHealthEvaluator.Evaluate(
            started.AddMinutes(2).AddSeconds(1),
            isBatchActive: true,
            isAgentConnected: true,
            isTurnRunning: true,
            dispatchStartedAtUtc: started,
            lastAgentActivityUtc: started,
            lastOutputActivityUtc: started.AddMinutes(2),
            oldestClaimActivityUtc: started.AddMinutes(1),
            generatedImageCount: 2,
            requestedImageCount: 4);
        Equal(GenerationAgentHealthState.Healthy, outputHeartbeat.State,
            "即使 Agent 文本沉默，新图片落盘也必须作为有效心跳");

        var disconnected = GenerationAgentHealthEvaluator.Evaluate(
            started.AddSeconds(5),
            isBatchActive: true,
            isAgentConnected: false,
            isTurnRunning: false,
            dispatchStartedAtUtc: started,
            lastAgentActivityUtc: started,
            lastOutputActivityUtc: null,
            oldestClaimActivityUtc: started,
            generatedImageCount: 1,
            requestedImageCount: 3);
        Equal(GenerationAgentHealthState.Disconnected, disconnected.State, "断连应立即识别");
        True(disconnected.StatusText.Contains("最近活动", StringComparison.Ordinal),
            "断连状态仍应保留最后一次真实活动时间，便于区分瞬断与从未回传");
        True(disconnected.RiskText.Contains("1/3", StringComparison.Ordinal) &&
             disconnected.RiskText.Contains("不会因短时断连被自动判失败", StringComparison.Ordinal),
            "断连提示应说明已落盘数量和保留策略");

        var leaseRisk = GenerationAgentHealthEvaluator.Evaluate(
            started.AddMinutes(26),
            isBatchActive: true,
            isAgentConnected: true,
            isTurnRunning: true,
            dispatchStartedAtUtc: started,
            lastAgentActivityUtc: started.AddMinutes(25).AddSeconds(55),
            lastOutputActivityUtc: null,
            oldestClaimActivityUtc: started,
            generatedImageCount: 2,
            requestedImageCount: 4);
        Equal(GenerationAgentHealthState.Slow, leaseRisk.State, "接近认领租约边界应提示风险");
        True(leaseRisk.RiskText.Contains("30 分钟租约", StringComparison.Ordinal),
            "租约风险应给出明确边界");

        var renewedLease = GenerationAgentHealthEvaluator.Evaluate(
            started.AddMinutes(40),
            isBatchActive: true,
            isAgentConnected: true,
            isTurnRunning: true,
            dispatchStartedAtUtc: started,
            lastAgentActivityUtc: started.AddMinutes(39).AddSeconds(55),
            lastOutputActivityUtc: null,
            oldestClaimActivityUtc: started.AddMinutes(39).AddSeconds(50),
            generatedImageCount: 3,
            requestedImageCount: 4);
        Equal(GenerationAgentHealthState.Healthy, renewedLease.State,
            "长任务持续续租时不得按首次认领时间误报租约风险");
    }

    private static GenerationAgentHealthSnapshot Evaluate(
        DateTimeOffset now,
        DateTimeOffset started,
        DateTimeOffset lastActivity) => GenerationAgentHealthEvaluator.Evaluate(
        now,
        isBatchActive: true,
        isAgentConnected: true,
        isTurnRunning: true,
        dispatchStartedAtUtc: started,
        lastAgentActivityUtc: lastActivity,
        lastOutputActivityUtc: null,
        oldestClaimActivityUtc: started.AddSeconds(1),
        generatedImageCount: 1,
        requestedImageCount: 3);

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool condition, string message) => True(!condition, message);

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }
}

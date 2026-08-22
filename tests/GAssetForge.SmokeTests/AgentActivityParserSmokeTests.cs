using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.App.ViewModels;
using System.Text.Json;

namespace GAssetForge.SmokeTests;

internal static class AgentActivityParserSmokeTests
{
    public static void Run()
    {
        AppServerEventsProduceBoundedSafeActivities();
        ExecJsonlEventsUseTheSameActivityContract();
        StructuredLogTimingKeysPairStartsWithTerminalEvents();
        AgentConnectionExposesTheStableActivitySurface();
    }

    private static void AppServerEventsProduceBoundedSafeActivities()
    {
        var parser = new AgentActivityParser();
        var timestamp = new DateTimeOffset(2026, 8, 21, 9, 30, 0, TimeSpan.Zero);
        var firstDelta = parser.Parse(
            Notification(
                "item/agentMessage/delta",
                new
                {
                    threadId = "thread-app",
                    turnId = "turn-app",
                    itemId = "message-1",
                    delta = "正在绘制"
                }),
            timestamp);
        Equal(AgentActivityCategory.AgentMessage, firstDelta?.Category, "App Server agentMessage delta 分类");
        Equal("正在绘制", firstDelta?.Message, "App Server 首段 Agent 文本");
        Equal("thread-app", firstDelta?.ThreadId, "App Server threadId");
        Equal("turn-app", firstDelta?.TurnId, "App Server turnId");

        var secondDelta = parser.Parse(
            Notification(
                "item/agentMessage/delta",
                new
                {
                    threadId = "thread-app",
                    turnId = "turn-app",
                    itemId = "message-1",
                    delta = "第 1 张。\0 OPENAI_API_KEY=must-not-appear"
                }),
            timestamp.AddSeconds(1));
        True(secondDelta?.Message.Contains("正在绘制第 1 张。", StringComparison.Ordinal) == true,
            "Agent 文本应累计并移除控制字符");
        False(secondDelta?.Message.Contains("must-not-appear", StringComparison.Ordinal) == true,
            "Agent 文本中的常见凭据应被脱敏");

        var toolStarted = parser.Parse(
            Notification(
                "item/started",
                new
                {
                    threadId = "thread-app",
                    turnId = "turn-app",
                    item = new
                    {
                        id = "tool-1",
                        type = "mcpToolCall",
                        server = "gasset_forge",
                        tool = "claim_generation_task",
                        status = "inProgress",
                        arguments = new
                        {
                            taskId = "0123456789abcdef0123456789abcdef",
                            bearerToken = "must-not-appear"
                        }
                    }
                }),
            timestamp.AddSeconds(2));
        Equal(AgentActivityCategory.ToolStarted, toolStarted?.Category, "MCP 工具开始分类");
        Equal("0123456789abcdef0123456789abcdef", toolStarted?.TaskId, "MCP 工具 taskId 关联");
        True(toolStarted?.Message.Contains("claim_generation_task", StringComparison.Ordinal) == true,
            "MCP 工具摘要应保留工具名");
        False(toolStarted?.Message.Contains("must-not-appear", StringComparison.Ordinal) == true,
            "MCP 工具摘要不得回显任意参数");

        var reasoning = parser.Parse(
            Notification(
                "item/started",
                new
                {
                    threadId = "thread-app",
                    turnId = "turn-app",
                    item = new
                    {
                        id = "reasoning-1",
                        type = "reasoning",
                        content = "internal-reasoning-must-not-appear"
                    }
                }),
            timestamp.AddSeconds(3));
        Equal(AgentActivityCategory.Reasoning, reasoning?.Category, "推理项目分类");
        False(reasoning?.Message.Contains("internal-reasoning", StringComparison.Ordinal) == true,
            "活动摘要不得暴露原始推理内容");

        var command = parser.Parse(
            Notification(
                "item/completed",
                new
                {
                    threadId = "thread-app",
                    turnId = "turn-app",
                    item = new
                    {
                        id = "command-1",
                        type = "commandExecution",
                        status = "completed",
                        command = "image_gen --api-key must-not-appear"
                    }
                }),
            timestamp.AddSeconds(4));
        Equal(AgentActivityCategory.CommandCompleted, command?.Category, "命令完成分类");
        False(command?.Message.Contains("must-not-appear", StringComparison.Ordinal) == true,
            "命令摘要不得回显原始命令行");

        var oversized = parser.Parse(
            Notification(
                "item/agentMessage/delta",
                new
                {
                    threadId = "thread-app",
                    turnId = "turn-app",
                    itemId = "message-large",
                    delta = new string('x', AgentActivityParser.MaximumMessageLength + 200)
                }),
            timestamp.AddSeconds(5));
        True(oversized?.Message.Length <= AgentActivityParser.MaximumMessageLength,
            "Agent 文本必须受最大显示长度约束");
    }

    private static void ExecJsonlEventsUseTheSameActivityContract()
    {
        var parser = new AgentActivityParser();
        var timestamp = new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero);
        var started = parser.Parse(
            Notification(
                "turn.started",
                new
                {
                    type = "turn.started",
                    thread_id = "thread-exec",
                    turn_id = "turn-exec"
                }),
            timestamp);
        Equal(AgentActivityCategory.TurnStarted, started?.Category, "exec turn.started 分类");
        Equal("turn/started", started?.EventName, "exec 点号事件应规范化为斜杠事件");
        Equal("thread-exec", started?.ThreadId, "exec thread_id");
        Equal("turn-exec", started?.TurnId, "exec turn_id");

        var message = parser.Parse(
            Notification(
                "item.completed",
                new
                {
                    type = "item.completed",
                    thread_id = "thread-exec",
                    turn_id = "turn-exec",
                    item = new
                    {
                        id = "message-exec",
                        type = "agent_message",
                        text = "第 1 张图片已保存。",
                        phase = "commentary"
                    }
                }),
            timestamp.AddSeconds(1));
        Equal(AgentActivityCategory.AgentMessage, message?.Category, "exec agent_message 分类");
        Equal("agentMessage", message?.ItemType, "exec snake_case item 类型规范化");
        Equal("第 1 张图片已保存。", message?.Message, "exec Agent 完整文本");

        var failed = parser.Parse(
            Notification(
                "turn.failed",
                new
                {
                    type = "turn.failed",
                    thread_id = "thread-exec",
                    turn_id = "turn-exec",
                    error = new { message = "connection reset" }
                }),
            timestamp.AddSeconds(2));
        Equal(AgentActivityCategory.TurnCompleted, failed?.Category, "exec turn.failed 应收束为 Turn 完成事件");
        True(failed?.IsWarning == true && failed.IsTerminal, "exec turn.failed 应标记警告与终态");
        True(failed?.Message.Contains("connection reset", StringComparison.Ordinal) == true,
            "exec Turn 失败应保留安全错误摘要");
    }

    private static void AgentConnectionExposesTheStableActivitySurface()
    {
        var activityEvent = typeof(AgentConnectionViewModel).GetEvent(nameof(AgentConnectionViewModel.AgentActivityReceived));
        Equal(typeof(EventHandler<AgentActivityEventArgs>), activityEvent?.EventHandlerType,
            "AgentConnectionViewModel AgentActivityReceived 事件签名");
        True(typeof(AgentConnectionViewModel).GetProperty(nameof(AgentConnectionViewModel.IsTurnRunning)) is not null,
            "AgentConnectionViewModel 应公开 IsTurnRunning");
    }

    private static void StructuredLogTimingKeysPairStartsWithTerminalEvents()
    {
        var timestamp = new DateTimeOffset(2026, 8, 21, 10, 30, 0, TimeSpan.Zero);
        var turnStarted = new AgentActivityEventArgs(
            timestamp,
            AgentActivityCategory.TurnStarted,
            "turn started",
            false,
            turnId: "turn-timing");
        var turnCompleted = new AgentActivityEventArgs(
            timestamp.AddSeconds(5),
            AgentActivityCategory.TurnCompleted,
            "turn completed",
            false,
            turnId: "turn-timing",
            isTerminal: true);
        Equal(
            ShellViewModel.BuildAgentActivityTimingKey(turnStarted),
            ShellViewModel.BuildAgentActivityTimingKey(turnCompleted),
            "Turn 开始与结束必须使用同一个耗时键");

        var toolStarted = new AgentActivityEventArgs(
            timestamp,
            AgentActivityCategory.ToolStarted,
            "tool started",
            false,
            turnId: "turn-timing",
            itemId: "tool-1",
            itemType: "mcpToolCall");
        var toolFailed = new AgentActivityEventArgs(
            timestamp.AddSeconds(2),
            AgentActivityCategory.Error,
            "tool failed",
            true,
            turnId: "turn-timing",
            itemId: "tool-1",
            itemType: "mcpToolCall",
            isTerminal: true);
        Equal(
            ShellViewModel.BuildAgentActivityTimingKey(toolStarted),
            ShellViewModel.BuildAgentActivityTimingKey(toolFailed),
            "失败工具事件必须收束同一条开始计时记录");
    }

    private static AgentNotificationEventArgs Notification(string method, object payload) =>
        new(method, JsonSerializer.SerializeToElement(payload));

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
            throw new InvalidOperationException($"{message}：expected={expected} actual={actual}");
        }
    }
}

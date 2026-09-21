using GAssetForge.App.Services;
using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.App.ViewModels;
using System.IO;
using System.Reflection;

namespace GAssetForge.SmokeTests;

internal static class AgentExecutionDiagnosticsSmokeTests
{
    internal static void Run(string temporaryRoot)
    {
        DispatchLogsRetainCorrelationBeforeUiCallbacks(temporaryRoot);
        var paths = new WorkspacePaths(Path.Combine(temporaryRoot, "agent-entry-diagnostics"));
        using var log = new WorkbenchConsoleService(paths);
        var startup = log.GetSnapshot().Single(entry => entry.Context?.Stage == "workbench.start");
        True(startup.Message.Contains("build=", StringComparison.Ordinal) &&
             startup.Message.Contains("executable=", StringComparison.Ordinal),
            "启动诊断必须标识真实运行入口和构建，避免旧包误诊。");
        using var server = new McpServerProcessService(paths);
        using var viewModel = new AgentConnectionViewModel(
            server, new CodexAgentRuntimeService("fixture-never-launched"),
            workingDirectory: paths.ProjectDirectory, workbenchLogSink: log);

        Exception? failure = null;
        // Do not pump the UI dispatcher: evidence must already be persisted on the
        // failing worker, not timestamped later when its UI callback is processed.
        Task.Run(async () =>
        {
            try { await viewModel.RunTurnAsync("fixture must never reach an Agent"); }
            catch (Exception exception) { failure = exception; }
        }).GetAwaiter().GetResult();

        True(failure is not null, "未启动的会话必须拒绝提交。");
        var entry = log.GetSnapshot().LastOrDefault(item => item.Level == WorkbenchLogLevel.Error);
        True(entry is not null, "入口拒绝必须在 UI dispatcher 回调前持久化错误。");
        True(entry!.Context?.ErrorCode == "agent_session_not_running",
            "未启动必须使用独立错误码，不得笼统归因于认证或 MCP。");
        True(entry.Context?.Stage == "turn.prepare", "入口拒绝必须记录实际准备阶段。");
        True(!entry.Message.Contains("Turn 已结束", StringComparison.Ordinal),
            "尚未提交的 Turn 不得描述为已结束。");
        True(entry.Details.Contains(failure!.GetType().Name, StringComparison.Ordinal),
            "日志详情必须保留异常类型，不能只剩界面文案。");

        var safePanelMessage = (string)typeof(AgentConnectionViewModel)
            .GetMethod("Sanitize", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, ["api_key=fixture-panel-key Authorization: Bearer fixture-panel-token"] )!;
        True(!safePanelMessage.Contains("fixture-panel", StringComparison.Ordinal),
            "Agent自身面板必须与全局日志同样隐藏常见凭据。");
    }

    private static void DispatchLogsRetainCorrelationBeforeUiCallbacks(string temporaryRoot)
    {
        var paths = new WorkspacePaths(Path.Combine(temporaryRoot, "dispatch-source-diagnostics"));
        var editor = new EditorWorkspaceViewModel(new JsonProjectRepository(paths),
            new DesktopFileDialogService(), new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), new UserSettingsService(paths), editor);
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var shellType = typeof(ShellViewModel);
            var coordinator = shellType.GetField("_agentDispatchCoordinator", flags)!.GetValue(shell);
            var log = (WorkbenchConsoleService)shellType.GetField("_workbenchConsoleService", flags)!.GetValue(shell)!;
            var occurredAt = DateTimeOffset.Now.AddSeconds(-2);
            const string secret = "fixture-dispatch-secret-should-not-leak";
            log.RegisterSensitiveValue(secret);
            var exception = new IOException($"fixture failure {secret}");
            var diagnostic = new GenerationDispatchFailure("agent_turn_submission_failed", "turn_submission",
                exception.Message, "检查连接并继续原任务", "fixture-thread", Exception: exception)
            { OccurredAt = occurredAt };
            var snapshot = new GenerationBatchDispatchProgressSnapshot(
                new GenerationBatchDispatchRequest("fixture-batch", ["fixture-task"], false),
                GenerationBatchDispatchStage.Failed, 50, "turn/start", diagnostic.UserMessage)
            { OccurredAt = occurredAt, Diagnostic = diagnostic };

            Task.Run(() => shellType.GetMethod("OnAgentDispatchProgressChanged", flags)!
                .Invoke(shell, [coordinator, new GenerationBatchDispatchProgressChangedEventArgs(snapshot)]))
                .GetAwaiter().GetResult();
            var entry = log.GetSnapshot().Single(item => item.Context?.BatchId == "fixture-batch");
            True(entry.Timestamp == occurredAt, "调度日志必须保留事件原始时间，不取UI执行时间。");
            True(entry.Context?.TaskId == "fixture-task" && entry.Context.AgentThreadId == "fixture-thread",
                "调度异常必须关联原批次、任务和线程。");
            True(entry.Context?.ErrorCode == "agent_turn_submission_failed" &&
                 entry.Context.FailureStage == "turn_submission", "错误码与操作阶段不得混用。");
            True(entry.Context?.RecoveryHint == "检查连接并继续原任务", "恢复建议应贯通至日志出口。");
            True(entry.Details.Contains("fixture failure", StringComparison.Ordinal) &&
                 !entry.ExportText.Contains(secret, StringComparison.Ordinal),
                "异常细节保留但导出不得泄露凭据。");
        }
        finally { shell.Shutdown(); }
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

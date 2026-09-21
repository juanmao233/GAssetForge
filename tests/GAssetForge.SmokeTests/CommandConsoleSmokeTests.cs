using GAssetForge.App.Services;
using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.App.ViewModels;
using GAssetForge.App.Views;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace GAssetForge.SmokeTests;

internal static class CommandConsoleSmokeTests
{
    public static void Run()
    {
        StructuredLogsPersistAsJsonLinesAndRespectCapacity();
        BackgroundWritesReturnSafelyToTheUiDispatcher();
        QueuedBackgroundEntriesDoNotReappearAfterClear();
        RegisteredSensitiveValuesAreRedactedAtTheLogBoundary();
        DiagnosticContextRetainsOccurrenceAndRecordingTimes();
        CommonCredentialAssignmentsAreRedactedWithoutHidingNormalMessages();
        FileWriteAndDisposeFailuresFallBackToVisibleMemoryLogs();
        UnavailableLogDirectoryHasAnActionableWarning();
        AgentConnectionRegistersItsBearerTokenAtTheLogBoundary();
        WhitelistedCommandsReportResultsAndClearOnlyTheDisplay();
        PixelAlignmentCorrectionPreferencesPersistWithoutSourceGeometry();
        ShellRegistersSafeWorkbenchCommands();
        ConsoleViewBindsTheLogGridAndCommandControls();
    }

    private static void StructuredLogsPersistAsJsonLinesAndRespectCapacity()
    {
        var root = CreateTestRoot("structured-log");
        try
        {
            var paths = new WorkspacePaths(root);
            using var service = new WorkbenchConsoleService(paths, bufferCapacity: 3);
            var timestamp = new DateTimeOffset(2026, 8, 21, 14, 30, 15, TimeSpan.FromHours(8));
            var exception = new InvalidOperationException("console-smoke-details");

            service.WriteInfo("Smoke", "capacity-1");
            service.WriteWarning("Smoke", "capacity-2");
            service.WriteError("Generation", "structured-marker", exception, timestamp);
            service.WriteSuccess("Smoke", "capacity-4");

            var snapshot = service.GetSnapshot();
            Equal(3, snapshot.Count, "工作台日志内存容量裁剪");
            SequenceEqual(
                new[] { "capacity-2", "structured-marker", "capacity-4" },
                snapshot.Select(entry => entry.Message),
                "容量裁剪应保留最新日志并维持顺序");
            True(snapshot.Zip(snapshot.Skip(1), (left, right) => left.Sequence < right.Sequence).All(value => value),
                "结构化日志序号应严格递增");

            True(File.Exists(service.SessionLogPath), "工作台应创建 JSONL 会话日志文件");
            var persisted = FindJsonLine(service.SessionLogPath, "structured-marker");
            Equal("Error", persisted.GetProperty("level").GetString(), "JSONL 日志级别");
            Equal("Generation", persisted.GetProperty("source").GetString(), "JSONL 日志来源");
            Equal("structured-marker", persisted.GetProperty("message").GetString(), "JSONL 日志消息");
            True(
                persisted.GetProperty("details").GetString()?.Contains(
                    "console-smoke-details",
                    StringComparison.Ordinal) == true,
                "JSONL 应持久化异常详细信息");
            True(persisted.GetProperty("sequence").GetInt64() > 0, "JSONL 应持久化稳定日志序号");
            Equal(timestamp, persisted.GetProperty("timestamp").GetDateTimeOffset(), "JSONL 日志时间戳");

            foreach (var line in ReadSessionLines(service.SessionLogPath))
            {
                using var document = JsonDocument.Parse(line);
                True(document.RootElement.ValueKind == JsonValueKind.Object, "每个 JSONL 行都必须是对象");
            }
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void BackgroundWritesReturnSafelyToTheUiDispatcher()
    {
        var root = CreateTestRoot("dispatcher");
        try
        {
            var paths = new WorkspacePaths(root);
            using var service = new WorkbenchConsoleService(paths, bufferCapacity: 16);
            using var viewModel = new CommandConsoleViewModel(service, entryCapacity: 2);
            service.ClearInMemory();
            PumpDispatcher();

            Task.Run(() => service.WriteInfo("Worker", "background-entry"))
                .GetAwaiter()
                .GetResult();
            PumpDispatcherUntil(
                () => viewModel.Entries.Any(entry => entry.Message == "background-entry"),
                "后台线程发布的日志未安全回填到 UI 集合");

            service.WriteInfo("Worker", "newer-entry");
            service.WriteWarning("Worker", "newest-entry");
            Equal(2, viewModel.Entries.Count, "控制台显示容量裁剪");
            SequenceEqual(
                new[] { "newer-entry", "newest-entry" },
                viewModel.Entries.Select(entry => entry.Message),
                "控制台显示容量应保留最新两条日志");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void QueuedBackgroundEntriesDoNotReappearAfterClear()
    {
        var root = CreateTestRoot("clear-barrier");
        try
        {
            var paths = new WorkspacePaths(root);
            using var service = new WorkbenchConsoleService(paths, bufferCapacity: 16);
            using var viewModel = new CommandConsoleViewModel(service, entryCapacity: 16);
            service.ClearInMemory();
            PumpDispatcher();

            Task.Run(() => service.WriteInfo("Worker", "queued-before-clear"))
                .GetAwaiter()
                .GetResult();
            service.ClearInMemory();

            Equal(0, service.GetSnapshot().Count, "清空后服务缓冲区应为空");
            PumpDispatcher();
            False(
                viewModel.Entries.Any(entry => entry.Message == "queued-before-clear"),
                "清空前已排入 Dispatcher 的旧日志不得在清空后重新出现");

            service.WriteInfo("Worker", "written-after-clear");
            True(
                viewModel.Entries.Any(entry => entry.Message == "written-after-clear"),
                "清空后的新日志仍应正常显示");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void RegisteredSensitiveValuesAreRedactedAtTheLogBoundary()
    {
        var root = CreateTestRoot("redaction-boundary");
        try
        {
            const string secret = "secret-device-or-bearer-value";
            var paths = new WorkspacePaths(root);
            using var service = new WorkbenchConsoleService(paths, bufferCapacity: 16);
            var registry = (IWorkbenchLogRedactionRegistry)service;
            WorkbenchLogEntry? published = null;
            service.EntryPublished += (_, eventArgs) =>
            {
                if (eventArgs.Entry.Message.Contains("redaction-marker", StringComparison.Ordinal))
                {
                    published = eventArgs.Entry;
                }
            };

            True(registry.RegisterSensitiveValue(secret), "首次注册敏感值应生效");
            False(registry.RegisterSensitiveValue(secret), "重复敏感值不应重复注册");
            service.WriteError(
                $"Source-{secret}",
                $"redaction-marker message={secret}",
                new InvalidOperationException($"details={secret}"));

            var entry = service.GetSnapshot().Last(item =>
                item.Message.Contains("redaction-marker", StringComparison.Ordinal));
            True(ReferenceEquals(entry, published), "事件与内存缓冲应发布同一份已脱敏日志");
            AssertRedacted(entry.Source, secret, "内存日志来源");
            AssertRedacted(entry.Message, secret, "内存日志消息");
            AssertRedacted(entry.Details, secret, "内存日志异常详情");

            var persisted = FindJsonLine(service.SessionLogPath, entry.Message);
            AssertRedacted(persisted.GetProperty("source").GetString() ?? string.Empty, secret, "JSONL 来源");
            AssertRedacted(persisted.GetProperty("message").GetString() ?? string.Empty, secret, "JSONL 消息");
            AssertRedacted(persisted.GetProperty("details").GetString() ?? string.Empty, secret, "JSONL 详情");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void DiagnosticContextRetainsOccurrenceAndRecordingTimes()
    {
        var root = CreateTestRoot("diagnostic-context");
        try
        {
            const string secret = "diagnostic-context-secret";
            using var service = new WorkbenchConsoleService(new WorkspacePaths(root));
            using var viewModel = new CommandConsoleViewModel(service);
            service.RegisterSensitiveValue(secret);
            var occurrenceTime = DateTimeOffset.Now.AddMinutes(-2);
            var beforeWrite = DateTimeOffset.Now;
            var context = new WorkbenchLogContext
            {
                BatchId = "diagnostic-batch",
                AgentThreadId = "diagnostic-thread",
                AgentTurnId = "diagnostic-turn",
                AgentItemId = "diagnostic-item",
                ExpectedPath = $"diagnostic-path/{secret}/expected.png",
                ErrorCode = "AGENT_MCP_NOT_READY"
            };
            // Reflection keeps the regression runnable against the pre-fix public contract.
            foreach (var propertyName in new[] { "FailureStage", "RecoveryHint", "ReadinessState", "ExceptionType" })
            {
                var property = typeof(WorkbenchLogContext).GetProperty(propertyName);
                True(property is not null, $"诊断上下文缺少结构化字段 {propertyName}");
                property!.SetValue(context, $"{propertyName}={secret}");
            }

            service.WriteStructured(
                WorkbenchLogLevel.Error, "Agent", "diagnostic-context-marker", context,
                occurrenceTime, new InvalidOperationException($"nested={secret}"));
            var entry = service.GetSnapshot().Last();
            var persisted = FindJsonLine(service.SessionLogPath, entry.Message);
            Equal(2, persisted.GetProperty("schemaVersion").GetInt32(), "日志格式版本应可识别");
            Equal(occurrenceTime, persisted.GetProperty("timestamp").GetDateTimeOffset(), "不得改写源事件时间");
            var recordedAt = persisted.GetProperty("recordedAt").GetDateTimeOffset();
            True(recordedAt >= beforeWrite && recordedAt > occurrenceTime,
                "写入时间应独立记录，不能伪装为较早的源事件时间");
            foreach (var propertyName in new[] { "FailureStage", "RecoveryHint", "ReadinessState", "ExceptionType" })
            {
                AssertRedacted(persisted.GetProperty("context").GetProperty(propertyName).GetString()!, secret,
                    $"新增诊断字段 {propertyName}");
            }

            AssertRedacted(entry.Details, secret, "诊断异常详情");
            AssertRedacted(entry.ExportText, secret, "完整诊断导出");
            True(entry.ExportText.Contains("diagnostic-item", StringComparison.Ordinal),
                "完整导出必须包含摘要省略的 AgentItemId");
            True(entry.ExportText.Contains("expected.png", StringComparison.Ordinal),
                "完整导出必须包含摘要省略的目标路径");
            viewModel.SelectedEntry = entry;
            True(viewModel.SelectedEntryText.Contains("diagnostic-thread", StringComparison.Ordinal),
                "复制详情应包含线程标识");
            True(viewModel.SelectedEntryText.Contains("RecoveryHint=", StringComparison.Ordinal),
                "复制详情应包含恢复建议");
            True(viewModel.SelectedEntryText.Contains(occurrenceTime.ToString("O"), StringComparison.Ordinal),
                "复制详情应包含完整的源事件时间");
            True(viewModel.SelectedEntryText.Contains(recordedAt.ToString("O"), StringComparison.Ordinal),
                "复制详情应包含完整的写入时间");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void CommonCredentialAssignmentsAreRedactedWithoutHidingNormalMessages()
    {
        var root = CreateTestRoot("credential-redaction");
        try
        {
            using var service = new WorkbenchConsoleService(new WorkspacePaths(root));
            const string bearerSecret = "unregistered-bearer-secret";
            const string apiSecret = "unregistered-api-secret";
            const string accessSecret = "unregistered-access-secret";
            service.WriteError("Agent", "credential-marker",
                new InvalidOperationException(
                    $"Authorization: Bearer {bearerSecret}\n\"api_key\": \"{apiSecret}\"\n" +
                    $"https://example.invalid/callback?access_token={accessSecret}&state=normal-state"));
            var entry = service.GetSnapshot().Last();
            foreach (var secret in new[] { bearerSecret, apiSecret, accessSecret })
            {
                AssertRedacted(entry.Details, secret, "未预注册的常见凭据");
            }

            True(entry.Details.Contains("state=normal-state", StringComparison.Ordinal),
                "凭据脱敏不能吞掉后续的普通查询参数");
            const string normal = "API key unavailable; Bearer Token authentication is configured; model=gpt-image";
            service.WriteWarning("Agent", normal);
            Equal(normal, service.GetSnapshot().Last().Message, "普通认证说明不能误判为凭据值");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void FileWriteAndDisposeFailuresFallBackToVisibleMemoryLogs()
    {
        var root = CreateTestRoot("sink-failure");
        try
        {
            using var service = new WorkbenchConsoleService(new WorkspacePaths(root));
            using var viewModel = new CommandConsoleViewModel(service);
            const string secret = "log-sink-secret";
            var persistenceStatusChanged = false;
            viewModel.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.PropertyName == nameof(CommandConsoleViewModel.PersistenceStatusText))
                {
                    persistenceStatusChanged = true;
                }
            };
            service.RegisterSensitiveValue(secret);
            var writerField = typeof(WorkbenchConsoleService).GetField("_sessionWriter",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            ((StreamWriter)writerField.GetValue(service)!).Dispose();
            writerField.SetValue(service, new FailingLogWriter(secret));
            service.WriteError("Agent", "original-generation-error");
            False(service.IsFileLoggingAvailable, "写失败后应关闭不可用的文件日志通道");
            True(persistenceStatusChanged, "运行中日志降级必须刷新已打开控制台的持久化状态");
            AssertRedacted(service.FileLoggingError, secret, "文件日志错误状态");
            var snapshot = service.GetSnapshot();
            True(snapshot.Any(entry => entry.Message == "original-generation-error"),
                "磁盘写失败不得丢弃原始内存错误");
            var warning = snapshot.SingleOrDefault(entry => entry.Context?.ErrorCode == "LOG_FILE_UNAVAILABLE");
            True(warning is { Level: WorkbenchLogLevel.Warning }, "日志持久化失败必须可见且有稳定错误码");
            AssertRedacted(warning!.Details, secret, "文件日志降级详情");
            True(viewModel.Entries.Any(entry => entry.Sequence == warning.Sequence),
                "日志降级警告应发布到控制台");
            service.WriteInfo("Agent", "memory-logging-continues");
            True(service.GetSnapshot().Any(entry => entry.Message == "memory-logging-continues"),
                "日志降级不能打断后续任务或内存记录");
            Equal(1, service.GetSnapshot().Count(entry => entry.Context?.ErrorCode == "LOG_FILE_UNAVAILABLE"),
                "不可用的文件日志通道不能反复刷屏");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void UnavailableLogDirectoryHasAnActionableWarning()
    {
        var root = CreateTestRoot("log-directory-unavailable");
        try
        {
            File.WriteAllText(Path.Combine(root, "Logs"), "blocking-file");
            using var service = new WorkbenchConsoleService(new WorkspacePaths(root));
            False(service.IsFileLoggingAvailable, "日志目录不可写时应启动内存日志");
            True(service.GetSnapshot().Any(entry =>
                    entry.Level == WorkbenchLogLevel.Warning &&
                    entry.Context?.ErrorCode == "LOG_FILE_UNAVAILABLE"),
                "启动时无法创建日志目录也必须产生明确的降级警告");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private sealed class FailingLogWriter(string secret) : StreamWriter(new MemoryStream())
    {
        public override void WriteLine(string? value) => throw new IOException($"sink-write-failed {secret}");

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            throw new IOException($"sink-dispose-failed {secret}");
        }
    }

    private static void AgentConnectionRegistersItsBearerTokenAtTheLogBoundary()
    {
        var root = CreateTestRoot("agent-bearer-redaction");
        try
        {
            var paths = new WorkspacePaths(root);
            using var service = new WorkbenchConsoleService(paths, bufferCapacity: 16);
            var server = new McpServerProcessService(paths);
            var runtime = new CodexAgentRuntimeService();
            using var viewModel = new AgentConnectionViewModel(
                server,
                runtime,
                workingDirectory: root,
                workbenchLogSink: service);
            var token = server.TokenEnvironmentVariableValue;

            service.WriteInfo("Agent", $"agent-bearer-marker token={token}");

            var entry = service.GetSnapshot().Last(item =>
                item.Message.Contains("agent-bearer-marker", StringComparison.Ordinal));
            AssertRedacted(entry.Message, token, "Agent MCP Bearer Token");
            var persisted = FindJsonLine(service.SessionLogPath, entry.Message);
            AssertRedacted(
                persisted.GetProperty("message").GetString() ?? string.Empty,
                token,
                "Agent MCP Bearer Token JSONL");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void WhitelistedCommandsReportResultsAndClearOnlyTheDisplay()
    {
        var root = CreateTestRoot("commands");
        try
        {
            var paths = new WorkspacePaths(root);
            using var service = new WorkbenchConsoleService(paths, bufferCapacity: 128);
            using var viewModel = new CommandConsoleViewModel(service, entryCapacity: 128);
            viewModel.RegisterCommand(
                "status",
                "status",
                "返回离线测试状态。",
                (_, _) => Task.FromResult<CommandConsoleResult?>(
                    CommandConsoleResult.Info("console-status-ok")));
            viewModel.RegisterCommand(
                "explode",
                "explode",
                "验证异常被转换为错误日志。",
                (_, _) => Task.FromException<CommandConsoleResult?>(
                    new InvalidOperationException("console-handler-fault")));
            viewModel.ClearDisplay();
            PumpDispatcher();

            Execute(viewModel, "help");
            ContainsLog(viewModel, WorkbenchLogLevel.Information, "允许执行的命令", "help 命令结果");
            ContainsLog(viewModel, WorkbenchLogLevel.Information, "status", "help 应列出注册命令");

            Execute(viewModel, "status");
            ContainsLog(viewModel, WorkbenchLogLevel.Information, "console-status-ok", "status 命令结果");

            Execute(viewModel, "not-a-command");
            ContainsLog(viewModel, WorkbenchLogLevel.Error, "未知命令", "未知命令错误");

            Execute(viewModel, "explode");
            var failedEntry = viewModel.Entries.LastOrDefault(entry =>
                entry.Level == WorkbenchLogLevel.Error &&
                entry.Message.Contains("命令执行失败", StringComparison.Ordinal));
            True(failedEntry is not null, "命令处理器异常应转换为结构化错误日志");
            True(
                failedEntry!.Details.Contains("console-handler-fault", StringComparison.Ordinal),
                "命令处理器异常详情应保留在日志中");

            var logPath = service.SessionLogPath;
            var persistedLengthBeforeClear = new FileInfo(logPath).Length;
            Execute(viewModel, "clear");
            Equal(0, service.GetSnapshot().Count, "clear 应清空服务显示缓冲区");
            Equal(0, viewModel.Entries.Count, "clear 应清空控制台日志表格");
            True(File.Exists(logPath), "clear 不得删除当前 JSONL 会话日志文件");
            True(
                new FileInfo(logPath).Length >= persistedLengthBeforeClear,
                "clear 不得截断已持久化的会话日志");
            _ = FindJsonLine(logPath, "> clear");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void PixelAlignmentCorrectionPreferencesPersistWithoutSourceGeometry()
    {
        var root = CreateTestRoot("pixel-alignment-settings");
        ShellViewModel? shell = null;
        try
        {
            var paths = new WorkspacePaths(root);
            var service = new UserSettingsService(paths);
            var defaults = service.Load();
            Equal("CenterPixel", defaults.PixelAlignmentSamplingMode,
                "像素对齐工具默认应优先使用中心像素采样");

            defaults.PixelAlignmentEnableAspectRatioCorrection = true;
            defaults.PixelAlignmentTargetAspectRatioA = 4d;
            defaults.PixelAlignmentTargetAspectRatioB = 3d;
            defaults.PixelAlignmentEnableRotationCorrection = true;
            defaults.PixelAlignmentRotationDegrees = -12.5d;
            defaults.PixelAlignmentEnablePerspectiveCorrection = true;
            defaults.PixelAlignmentTargetEdgeAngleDegrees = 60d;
            defaults.HasCompletedWorkflowSelection = true;
            service.Save(defaults);

            var editor = new EditorWorkspaceViewModel(
                new JsonProjectRepository(paths),
                new DesktopFileDialogService(),
                new ProjectHistoryService());
            shell = new ShellViewModel(new ThemeService(), service, editor);
            var loaded = shell.PixelAlignment.CaptureSettings();
            True(loaded.EnableAspectRatioCorrection, "Shell 应加载比例重整开关");
            Equal(4d, loaded.TargetAspectRatioA, "Shell 应加载目标 A 边比例");
            Equal(3d, loaded.TargetAspectRatioB, "Shell 应加载目标 B 边比例");
            True(loaded.EnableRotationCorrection, "Shell 应加载角度调整开关");
            Equal(-12.5d, loaded.RotationDegrees, "Shell 应加载旋转角度");
            True(loaded.EnablePerspectiveCorrection, "Shell 应加载夹角透视矫正开关");
            Equal(60d, loaded.TargetEdgeAngleDegrees, "Shell 应加载目标 A/B 边夹角");

            shell.PixelAlignment.EnableAspectRatioCorrection = false;
            shell.PixelAlignment.TargetAspectRatioA = 5d;
            shell.PixelAlignment.TargetAspectRatioB = 4d;
            shell.PixelAlignment.EnableRotationCorrection = false;
            shell.PixelAlignment.RotationDegrees = 22.5d;
            shell.PixelAlignment.EnablePerspectiveCorrection = false;
            shell.PixelAlignment.TargetEdgeAngleDegrees = 75d;

            var immediatelyPersisted = service.Load();
            False(immediatelyPersisted.PixelAlignmentEnableAspectRatioCorrection,
                "比例重整开关变化应即时持久化");
            Equal(5d, immediatelyPersisted.PixelAlignmentTargetAspectRatioA,
                "目标 A 边比例变化应即时持久化");
            Equal(4d, immediatelyPersisted.PixelAlignmentTargetAspectRatioB,
                "目标 B 边比例变化应即时持久化");
            False(immediatelyPersisted.PixelAlignmentEnableRotationCorrection,
                "角度调整开关变化应即时持久化");
            Equal(22.5d, immediatelyPersisted.PixelAlignmentRotationDegrees,
                "旋转角度变化应即时持久化");
            False(immediatelyPersisted.PixelAlignmentEnablePerspectiveCorrection,
                "夹角透视矫正开关变化应即时持久化");
            Equal(75d, immediatelyPersisted.PixelAlignmentTargetEdgeAngleDegrees,
                "目标 A/B 边夹角变化应即时持久化");

            immediatelyPersisted.PixelAlignmentTargetAspectRatioA = 9d;
            immediatelyPersisted.PixelAlignmentTargetAspectRatioB = 16d;
            immediatelyPersisted.PixelAlignmentRotationDegrees = -90d;
            immediatelyPersisted.PixelAlignmentTargetEdgeAngleDegrees = 120d;
            service.Save(immediatelyPersisted);
            shell.Shutdown();
            shell = null;

            var shutdownPersisted = service.Load();
            Equal(5d, shutdownPersisted.PixelAlignmentTargetAspectRatioA,
                "Shutdown 应从工具快照重新捕获目标 A 边比例");
            Equal(4d, shutdownPersisted.PixelAlignmentTargetAspectRatioB,
                "Shutdown 应从工具快照重新捕获目标 B 边比例");
            Equal(22.5d, shutdownPersisted.PixelAlignmentRotationDegrees,
                "Shutdown 应从工具快照重新捕获旋转角度");
            Equal(75d, shutdownPersisted.PixelAlignmentTargetEdgeAngleDegrees,
                "Shutdown 应从工具快照重新捕获目标夹角");

            var serialized = File.ReadAllText(paths.UserSettingsPath);
            False(serialized.Contains("sourceQuadrilateral", StringComparison.OrdinalIgnoreCase),
                "源图专属四边形不得进入用户设置 JSON");
            False(serialized.Contains("sourceTopLeft", StringComparison.OrdinalIgnoreCase),
                "源图左上顶点不得进入用户设置 JSON");
            False(serialized.Contains("sourceTopRight", StringComparison.OrdinalIgnoreCase),
                "源图右上顶点不得进入用户设置 JSON");
            False(serialized.Contains("sourceBottomRight", StringComparison.OrdinalIgnoreCase),
                "源图右下顶点不得进入用户设置 JSON");
            False(serialized.Contains("sourceBottomLeft", StringComparison.OrdinalIgnoreCase),
                "源图左下顶点不得进入用户设置 JSON");
            False(serialized.Contains("pixelAlignmentCrop", StringComparison.OrdinalIgnoreCase),
                "源图专属裁切边界不得进入用户设置 JSON");
        }
        finally
        {
            shell?.Shutdown();
            DeleteTestRoot(root);
        }
    }

    private static void ShellRegistersSafeWorkbenchCommands()
    {
        var root = CreateTestRoot("shell-commands");
        var paths = new WorkspacePaths(root);
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(
            new ThemeService(),
            new UserSettingsService(paths),
            editor);

        try
        {
            var console = shell.CommandConsole;
            console.ClearDisplay();
            PumpDispatcher();

            Execute(console, "help show");
            ContainsLog(console, WorkbenchLogLevel.Information, "show <console|project|generation", "show 白名单注册");
            Execute(console, "help theme");
            ContainsLog(console, WorkbenchLogLevel.Information, "theme [dark|light|toggle]", "theme 白名单注册");
            Execute(console, "help agent");
            ContainsLog(console, WorkbenchLogLevel.Information, "agent [status|start|stop]", "agent 白名单注册");
            Execute(console, "help generation");
            ContainsLog(
                console,
                WorkbenchLogLevel.Information,
                "generation [status|timeline|start|publish]",
                "generation 白名单注册");

            string? requestedPanel = null;
            shell.PanelShowRequested += (_, contentId) => requestedPanel = contentId;
            Execute(console, "show console");
            Equal("command-console", requestedPanel, "show console 应请求拉起指令日志停靠页");
            Execute(console, "show pixel-grid");
            Equal("pixel-grid-alignment", requestedPanel, "show pixel-grid 应请求拉起像素点对齐停靠页");
            Execute(console, "show outline");
            Equal("image-outline", requestedPanel, "show outline 应请求拉起图片描边停靠页");
            Execute(console, "show icon");
            Equal("icon-design", requestedPanel, "show icon 应请求拉起 Icon 图标制作停靠页");
            Execute(console, "theme");
            ContainsLog(console, WorkbenchLogLevel.Information, "当前主题", "theme 状态查询");
            Execute(console, "status");
            ContainsLog(console, WorkbenchLogLevel.Information, "项目：", "工作台 status 查询");
            Execute(console, "agent status");
            ContainsLog(console, WorkbenchLogLevel.Information, "MCP：", "Agent 状态查询");
            Execute(console, "generation status");
            ContainsLog(console, WorkbenchLogLevel.Information, "可直接生成", "素材生成状态查询");
            Execute(console, "generation timeline");
            ContainsLog(console, WorkbenchLogLevel.Information, "最长步骤", "素材生成时间线查询");

            // Deliberately do not execute logs.open, agent start, generation start or publish.
            // Their presence is verified through help so this smoke test stays offline and side-effect free.
        }
        finally
        {
            shell.Shutdown();
            DeleteTestRoot(root);
        }
    }

    private static void ConsoleViewBindsTheLogGridAndCommandControls()
    {
        var root = CreateTestRoot("view-bindings");
        try
        {
            var paths = new WorkspacePaths(root);
            using var service = new WorkbenchConsoleService(paths);
            using var viewModel = new CommandConsoleViewModel(service);
            var view = new CommandConsoleView
            {
                DataContext = viewModel
            };
            view.Measure(new Size(1080, 620));
            view.Arrange(new Rect(0, 0, 1080, 620));
            view.UpdateLayout();
            PumpDispatcher();

            var logGrid = view.FindName("LogDataGrid") as DataGrid
                ?? throw new InvalidOperationException("指令日志 View 缺少 LogDataGrid。");
            var commandInput = view.FindName("CommandInputTextBox") as TextBox
                ?? throw new InvalidOperationException("指令日志 View 缺少 CommandInputTextBox。");
            var executeButton = FindVisualDescendant<Button>(
                view,
                button => string.Equals(button.Content as string, "执行", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("指令日志 View 缺少执行按钮。");
            var clearButton = FindVisualDescendant<Button>(
                view,
                button => string.Equals(button.Content as string, "清空", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("指令日志 View 缺少清空按钮。");

            True(ReferenceEquals(viewModel.FilteredEntries, logGrid.ItemsSource),
                "日志表格应绑定 FilteredEntries");
            Equal(5, logGrid.Columns.Count, "日志表格正式列数");
            var detailsColumn = logGrid.Columns.Single(column => Equals(column.Header, "详细信息"));
            Equal("ExportText", (detailsColumn.ClipboardContentBinding as Binding)?.Path?.Path,
                "实际表格复制出口必须使用完整诊断导出，而非截断的显示摘要");
            var levelColumn = logGrid.Columns.Single(column => Equals(column.Header, "类型"));
            Equal("LevelText", (levelColumn.ClipboardContentBinding as Binding)?.Path?.Path,
                "模板类型列复制应包含可读级别");
            const string clipboardSecret = "clipboard-context-secret";
            service.RegisterSensitiveValue(clipboardSecret);
            service.WriteStructured(WorkbenchLogLevel.Error, "Clipboard", "clipboard-diagnostic-marker",
                new WorkbenchLogContext
                {
                    AgentThreadId = "clipboard-thread",
                    RecoveryHint = $"retry after initialization; api_key={clipboardSecret}",
                    ExpectedPath = $"target/{clipboardSecret}/asset.png"
                }, DateTimeOffset.Now.AddMinutes(-1), new InvalidOperationException(clipboardSecret));
            var clipboardEntry = service.GetSnapshot().Last();
            var clipboardText = detailsColumn.OnCopyingCellClipboardContent(clipboardEntry)?.ToString() ?? string.Empty;
            Equal(clipboardEntry.ExportText, clipboardText, "DataGrid 实际复制事件应解析完整 ExportText 绑定");
            AssertRedacted(clipboardText, clipboardSecret, "实际 DataGrid 剪贴板诊断内容");
            SequenceEqual(
                new[] { "时间", "类型", "来源", "消息", "详细信息" },
                logGrid.Columns.Select(column => column.Header?.ToString() ?? string.Empty),
                "日志表格列头");
            Equal(
                "FilteredEntries",
                RequireBindingPath(logGrid, ItemsControl.ItemsSourceProperty, "日志表格 ItemsSource"),
                "日志表格绑定路径");
            Equal(
                "InputText",
                RequireBindingPath(commandInput, TextBox.TextProperty, "命令输入框 Text"),
                "命令输入框绑定路径");
            Equal(
                "ExecuteCommandCommand",
                RequireBindingPath(executeButton, ButtonBase.CommandProperty, "执行按钮 Command"),
                "执行按钮绑定路径");
            Equal(
                "ClearDisplayCommand",
                RequireBindingPath(clearButton, ButtonBase.CommandProperty, "清空按钮 Command"),
                "清空按钮绑定路径");
            True(ReferenceEquals(viewModel.ExecuteCommandCommand, executeButton.Command),
                "执行按钮应使用控制台命令入口");
            True(ReferenceEquals(viewModel.ClearDisplayCommand, clearButton.Command),
                "清空按钮应只清理显示缓冲区");

            commandInput.Text = "status";
            commandInput.CaretIndex = 0;
            view.FocusCommandInput();
            Equal(commandInput.Text.Length, commandInput.CaretIndex,
                "拉起控制台时命令输入光标应移动到末尾");

            view.DataContext = null;
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static void Execute(CommandConsoleViewModel viewModel, string command)
    {
        viewModel.InputText = command;
        viewModel.ExecuteCurrentCommandAsync().GetAwaiter().GetResult();
        PumpDispatcher();
        False(viewModel.IsExecuting, $"命令执行后仍处于忙碌状态：{command}");
    }

    private static void ContainsLog(
        CommandConsoleViewModel viewModel,
        WorkbenchLogLevel level,
        string messagePart,
        string description)
    {
        True(
            viewModel.Entries.Any(entry =>
                entry.Level == level &&
                entry.Message.Contains(messagePart, StringComparison.Ordinal)),
            $"{description}缺少日志片段：{messagePart}");
    }

    private static void AssertRedacted(string value, string secret, string description)
    {
        False(value.Contains(secret, StringComparison.Ordinal), $"{description}仍包含敏感值明文");
        True(value.Contains("[REDACTED]", StringComparison.Ordinal), $"{description}缺少脱敏占位符");
    }

    private static JsonElement FindJsonLine(string path, string message)
    {
        foreach (var line in ReadSessionLines(path))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("message", out var messageElement) &&
                string.Equals(messageElement.GetString(), message, StringComparison.Ordinal))
            {
                return root.Clone();
            }
        }

        throw new InvalidOperationException($"JSONL 日志中缺少消息：{message}");
    }

    private static IReadOnlyList<string> ReadSessionLines(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static string RequireBindingPath(
        DependencyObject target,
        DependencyProperty property,
        string description)
    {
        var expression = BindingOperations.GetBindingExpression(target, property)
            ?? throw new InvalidOperationException($"{description} 缺少 BindingExpression。");
        return expression.ParentBinding.Path?.Path ?? string.Empty;
    }

    private static T? FindVisualDescendant<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && predicate(match))
            {
                return match;
            }

            var descendant = FindVisualDescendant(child, predicate);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static void PumpDispatcherUntil(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            PumpDispatcher();
            if (condition())
            {
                return;
            }

            Thread.Sleep(10);
        }

        throw new InvalidOperationException(message);
    }

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);

    private static string CreateTestRoot(string testName)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "command-console",
            testName,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTestRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

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

    private static void SequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string message)
    {
        var expectedArray = expected.ToArray();
        var actualArray = actual.ToArray();
        if (!expectedArray.SequenceEqual(actualArray))
        {
            throw new InvalidOperationException(
                $"{message}：预期 [{string.Join(", ", expectedArray)}]，" +
                $"实际 [{string.Join(", ", actualArray)}]。");
        }
    }
}

using GAssetForge.App.Services;
using GAssetForge.App.ViewModels;
using System.IO;

namespace GAssetForge.SmokeTests;

internal static class UserSettingsPersistenceSmokeTests
{
    internal static void Run(string temporaryRoot)
    {
        var root = Path.Combine(temporaryRoot, "user-settings-save-failure");
        var paths = new WorkspacePaths(root);
        var settingsService = new FailingUserSettingsService(paths);
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        ShellViewModel? shell = null;

        try
        {
            shell = new ShellViewModel(
                new ThemeService(),
                settingsService,
                editor);

            shell.SetTheme(AppTheme.Light);

            Equal(1, settingsService.SaveCallCount,
                "主题变更应尝试保存一次用户设置");
            Equal(AppTheme.Light, shell.CurrentTheme,
                "设置存储不可用时，已应用的工作台状态不得回滚或崩溃");
            var failure = shell.CommandConsole.Entries.SingleOrDefault(entry =>
                entry.Level == WorkbenchLogLevel.Error &&
                string.Equals(entry.Source, "Settings", StringComparison.Ordinal));
            NotNull(failure,
                "用户设置写入失败必须进入用户可见的 Workbench Console");
            Contains(failure!.Message, "下一次设置变更或正常退出时会重试",
                "可见错误应说明编辑器仍可继续及重试时机");
            Contains(failure.Details, FailingUserSettingsService.FailureMarker,
                "诊断详情应保留底层设置写入异常");

            var sessionLog = ReadActiveSessionLog(shell.CommandConsole.SessionLogPath);
            Contains(sessionLog, "\"level\":\"Error\"",
                "用户设置写入失败必须持久化为 Error 级别诊断");
            Contains(sessionLog, "\"source\":\"Settings\"",
                "用户设置写入失败诊断必须使用稳定 Settings 来源");
            Contains(sessionLog, FailingUserSettingsService.FailureMarker,
                "会话诊断日志应包含底层失败标记");
        }
        finally
        {
            // Shutdown performs another best-effort save. It must remain non-throwing
            // even while the settings store is still unavailable.
            shell?.Shutdown();
        }
    }

    private static string ReadActiveSessionLog(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void Contains(string actual, string expected, string message)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{message}：未找到 {expected}。");
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{message}：预期 {expected}，实际 {actual}。");
        }
    }

    private static void NotNull(object? value, string message)
    {
        if (value is null)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FailingUserSettingsService(WorkspacePaths paths) : IUserSettingsService
    {
        internal const string FailureMarker = "fixture-settings-save-denied";

        public WorkspacePaths Paths { get; } = paths;

        public int SaveCallCount { get; private set; }

        public UserSettingsModel Load() => new()
        {
            HasCompletedWorkflowSelection = true
        };

        public void Save(UserSettingsModel settings)
        {
            SaveCallCount++;
            throw new IOException(FailureMarker);
        }
    }
}

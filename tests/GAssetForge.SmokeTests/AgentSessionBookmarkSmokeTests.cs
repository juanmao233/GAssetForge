using GAssetForge.App.Services;
using System.IO;

namespace GAssetForge.SmokeTests;

internal static class AgentSessionBookmarkSmokeTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "GAssetForge.SessionBookmark", Guid.NewGuid().ToString("N"));
        var projectA = Path.Combine(root, "ProjectA");
        var projectB = Path.Combine(root, "ProjectB");
        var settings = new UserSettingsModel();

        AgentSessionBookmarkService.Store(settings, "thread-a", projectA);
        Equal("thread-a", AgentSessionBookmarkService.ResolveResumeThreadId(settings, projectA),
            "相同项目工作目录应恢复保存的 Codex thread");
        False(AgentSessionBookmarkService.ClearIfWorkingDirectoryChanged(settings, projectA),
            "相同工作目录不得清除 thread bookmark");
        True(AgentSessionBookmarkService.ClearIfWorkingDirectoryChanged(settings, projectB),
            "切换项目时必须清除旧 thread bookmark");
        Equal<string?>(null, settings.CodexThreadId, "切换项目后 thread id 应清空");
        Equal<string?>(null, settings.CodexThreadWorkingDirectory,
            "切换项目后 thread 工作目录应清空");

        settings.CodexThreadId = "orphan-thread";
        settings.CodexThreadWorkingDirectory = null;
        True(AgentSessionBookmarkService.ClearIfWorkingDirectoryChanged(settings, projectA),
            "不完整 bookmark 必须清理，避免跨项目误恢复");
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
}

using System.IO;
using System.Windows.Threading;
using GAssetForge.App.Services;
using GAssetForge.App.ViewModels;
using GAssetForge.Core.Generation;

namespace GAssetForge.SmokeTests;

internal static class AssetGenerationProjectIsolationSmokeTests
{
    public static void Run()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "asset-generation-project-isolation",
            Guid.NewGuid().ToString("N"));
        var paths = new WorkspacePaths(testRoot);
        ShellViewModel? shell = null;

        try
        {
            shell = CreateShell(paths);
            shell.SelectWorkflow(TilemapConstraintPackageBuilder.WorkflowId);
            var style = shell.ArtStyleLibrary.SelectedGroup
                ?? throw new InvalidOperationException("项目隔离 smoke 缺少默认风格组。");
            style.Description = "项目隔离 smoke 风格";

            var generation = shell.AssetGeneration;
            generation.DraftRows.Single().Description = "项目 A 瓦片";
            var published = new List<GenerationBatchPublishedEventArgs>();
            generation.BatchPublished += (_, args) => published.Add(args);
            generation.CreateTaskCommand.Execute(null);
            WaitUntil(
                () => published.Count == 1 && generation.PublishedTasks.Count == 1,
                TimeSpan.FromSeconds(8),
                "项目 A 的生成批次未发布");
            var projectABatchId = published[0].BatchId;

            var projectBAssetRoot = Path.Combine(testRoot, "ProjectB", "Assets");
            Directory.CreateDirectory(projectBAssetRoot);
            True(shell.ProjectBrowser.TrySetRootPath(projectBAssetRoot),
                "Project 面板未切换到项目 B 的 Assets");
            WaitUntil(
                () => string.Equals(generation.CurrentBatchId, projectABatchId, StringComparison.Ordinal) &&
                      generation.PublishedTasks.Count == 1,
                TimeSpan.FromSeconds(8),
                "项目切换不应丢失本次会话正在审计的已发布批次");

            shell.Shutdown();
            shell = CreateShell(paths);
            generation = shell.AssetGeneration;
            WaitUntil(
                () => generation.PublishedTasks.Count == 0 && !generation.HasCurrentTask,
                TimeSpan.FromSeconds(8),
                "应用重载后为项目 B 恢复了项目 A 的最新生成任务");

            True(shell.ProjectBrowser.TrySetRootPath(paths.AssetDirectory),
                "Project 面板未切回项目 A 的 Assets");
            WaitUntil(
                () => string.Equals(generation.CurrentBatchId, projectABatchId, StringComparison.Ordinal) &&
                      generation.PublishedTasks.Count == 1,
                TimeSpan.FromSeconds(8),
                "切回项目 A 后未恢复该 Assets 根目录的最新生成批次");
        }
        finally
        {
            shell?.Shutdown();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static ShellViewModel CreateShell(WorkspacePaths paths)
    {
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        return new ShellViewModel(
            new ThemeService(),
            new UserSettingsService(paths),
            editor);
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string message)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
            Thread.Sleep(10);
        }

        throw new InvalidOperationException(message);
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

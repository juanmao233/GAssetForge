using System.Text.Json.Nodes;
using System.Windows.Threading;
using System.IO;
using GAssetForge.App.Services;
using GAssetForge.App.ViewModels;
using GAssetForge.Core.Generation;

namespace GAssetForge.SmokeTests;

internal static class IconAssetGenerationWorkflowSmokeTests
{
    public static void Run()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "icon-asset-generation",
            Guid.NewGuid().ToString("N"));
        var paths = new WorkspacePaths(testRoot);
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
            var generation = shell.AssetGeneration;
            Equal(2, generation.GenerationSteps.Count, "素材生成应公开两个正式生成步骤");
            var iconStep = generation.GenerationSteps.Single(step =>
                string.Equals(
                    step.Key,
                    IconDesignConstraintPackageBuilder.WorkflowId,
                    StringComparison.Ordinal));
            generation.SelectedGenerationStep = iconStep;

            var style = shell.ArtStyleLibrary.SelectedGroup
                ?? throw new InvalidOperationException("Icon 素材生成 smoke 缺少默认风格组。");
            style.Description = "低饱和手绘奇幻游戏 Icon，硬边高光与清晰轮廓";
            var outputDirectory = Path.Combine(paths.GeneratedAssetDirectory, "IconItems");
            Directory.CreateDirectory(outputDirectory);
            var row = generation.DraftRows.Single();
            row.Description = "单个银质机械钥匙 Icon";
            row.Quantity = 1;
            row.OutputDirectory = outputDirectory;

            shell.IconDesign.ImageWidth = 640;
            shell.IconDesign.ImageHeight = 512;
            shell.IconDesign.CameraAzimuthDegrees = -30d;
            shell.IconDesign.CameraElevationDegrees = 25d;
            shell.IconDesign.CameraRollDegrees = 5d;
            shell.IconDesign.CameraDistance = 1.5d;
            shell.IconDesign.FocalLengthMillimeters = 200d;
            False(shell.IconDesign.Projection.IsFullyInFrame,
                "极近长焦参数应让 Icon 主体超出镜头边界");
            False(generation.CanCreateTask,
                "Icon 主体未完整入镜时不得发布自相矛盾的生成合同");
            True(generation.ConstraintPreview.Contains("超出镜头边界", StringComparison.Ordinal),
                "Icon 出框时确认描述应给出可执行修正提示");

            shell.IconDesign.CameraDistance = 8d;
            shell.IconDesign.FocalLengthMillimeters = 50d;
            True(shell.IconDesign.Projection.IsFullyInFrame,
                "安全物距与焦距应让 Icon 主体完整入镜");
            True(generation.CanCreateTask,
                "有效风格、输出与 Icon 相机参数应允许离线发布");
            True(generation.CanvasSizeText.Contains("640 × 512", StringComparison.Ordinal) &&
                 generation.GenerationPerspectiveText.Contains("方位角 -30°", StringComparison.Ordinal) &&
                 generation.ConstraintPreview.Contains("生成角度描述", StringComparison.Ordinal),
                "素材生成预览应实时显示 Icon 画幅、相机摘要与完整角度描述");

            GenerationBatchPublishedEventArgs? published = null;
            generation.BatchPublished += (_, args) => published = args;
            generation.CreateTaskCommand.Execute(null);
            WaitUntil(
                () => published is not null && generation.PublishedTasks.Count == 1,
                TimeSpan.FromSeconds(8),
                "Icon 素材生成任务未完成离线发布");

            var task = new JsonGenerationTaskStore(paths.GenerationDirectory)
                .ListBatchAsync(published!.BatchId)
                .GetAwaiter()
                .GetResult()
                .Single();
            Equal(IconDesignConstraintPackageBuilder.WorkflowId,
                task.Constraints.WorkflowId,
                "Icon 发布任务 workflowId");
            Equal(640, task.Constraints.Output.Width, "Icon 发布任务画幅宽度");
            Equal(512, task.Constraints.Output.Height, "Icon 发布任务画幅高度");
            var icon = task.Constraints.Parameters["iconDesign"] as JsonObject
                ?? throw new InvalidOperationException("Icon 发布任务缺少 parameters.iconDesign。");
            Near(-30d, icon["cameraAzimuthDegrees"]!.GetValue<double>(), 0.000000001d,
                "Icon 发布任务冻结方位角");
            Near(25d, icon["cameraElevationDegrees"]!.GetValue<double>(), 0.000000001d,
                "Icon 发布任务冻结仰角");
            Near(5d, icon["cameraRollDegrees"]!.GetValue<double>(), 0.000000001d,
                "Icon 发布任务冻结滚转角");
            Near(8d, icon["cameraDistance"]!.GetValue<double>(), 0.000000001d,
                "Icon 发布任务冻结物距");
            Near(50d, icon["focalLengthMillimeters"]!.GetValue<double>(), 0.000000001d,
                "Icon 发布任务冻结焦距");
            True(task.Constraints.Prompt.Contains(row.Description, StringComparison.Ordinal) &&
                 task.Constraints.Prompt.Contains("不得添加 Tilemap", StringComparison.Ordinal),
                "Icon 发布任务应使用物品描述并明确隔离 Tilemap 语义");
            True(MaterialGenerationConstraintComposer.TryReadDelivery(task.Constraints, out var delivery) &&
                 delivery is not null &&
                 delivery.TargetPaths.Single().Contains("/Icon/", StringComparison.Ordinal) &&
                 Path.GetFileName(delivery.TargetPaths.Single()).StartsWith("icon_", StringComparison.Ordinal),
                "Icon 首次生成应落入独立 Icon 分类目录与文件名前缀");

            shell.IconDesign.CameraAzimuthDegrees = 75d;
            shell.IconDesign.ImageWidth = 1024;
            var reloaded = new JsonGenerationTaskStore(paths.GenerationDirectory)
                .GetAsync(task.Id)
                .GetAwaiter()
                .GetResult()
                ?? throw new InvalidOperationException("Icon 发布任务在设置修改后丢失。");
            var frozenIcon = reloaded.Constraints.Parameters["iconDesign"] as JsonObject
                ?? throw new InvalidOperationException("重载 Icon 发布任务缺少冻结参数。");
            Equal(640, reloaded.Constraints.Output.Width,
                "发布后修改 Icon 画幅不得改写已持久化任务");
            Near(-30d, frozenIcon["cameraAzimuthDegrees"]!.GetValue<double>(), 0.000000001d,
                "发布后修改 Icon 相机不得改写已持久化任务");
        }
        finally
        {
            shell.Shutdown();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
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

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException($"{message}：预期 {expected} ± {tolerance}，实际 {actual}。");
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}：预期 {expected}，实际 {actual}。");
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
}

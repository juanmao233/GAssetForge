using GAssetForge.App.Services;
using GAssetForge.App.ViewModels;
using GAssetForge.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace GAssetForge.SmokeTests;

internal static class ImageToolIntegrationSmokeTests
{
    public static void Run(string temporaryRoot)
    {
        var testRoot = Path.Combine(temporaryRoot, "image-tool-integration");
        Directory.CreateDirectory(testRoot);
        ImageOutlineBatchContinuesAfterIndividualFailures(testRoot);
        ShellLoadsAndPersistsOutlineAndCheckerPreferences(testRoot);
        LegacyBackgroundRemovalSettingsEnableHollowRemovalByDefault(testRoot);
    }

    private static void ImageOutlineBatchContinuesAfterIndividualFailures(string testRoot)
    {
        var batchRoot = Path.Combine(testRoot, "outline-batch");
        var firstDirectory = Path.Combine(batchRoot, "first");
        var secondDirectory = Path.Combine(batchRoot, "second");
        var thirdDirectory = Path.Combine(batchRoot, "third");
        var outputDirectory = Path.Combine(batchRoot, "output");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        Directory.CreateDirectory(thirdDirectory);
        Directory.CreateDirectory(outputDirectory);
        var firstInput = Path.Combine(firstDirectory, "sprite.png");
        var secondInput = Path.Combine(secondDirectory, "sprite.png");
        var removedInput = Path.Combine(thirdDirectory, "removed.png");
        SaveTransparentFixture(firstInput, new Rgba32(220, 80, 30, 255));
        SaveTransparentFixture(secondInput, new Rgba32(30, 150, 220, 255));
        SaveTransparentFixture(removedInput, new Rgba32(80, 220, 100, 255));

        var viewModel = new ImageOutlineViewModel(
            new ImageSharpImageOutlineService(),
            new FixedOutlineInputPicker(new[] { firstInput, secondInput, removedInput }),
            new FixedOutlineOutputPicker(outputDirectory),
            placement: ImageOutlinePlacement.Outside.ToString(),
            width: 2,
            colorHex: "#112233");
        viewModel.IsBatchMode = true;
        viewModel.ChooseBatchInputsCommand.Execute(null);
        Equal(3, viewModel.BatchItems.Count, "图片描边批量队列数量");
        viewModel.ChooseBatchOutputDirectoryCommand.Execute(null);
        File.Delete(removedInput);
        True(viewModel.ProcessBatchCommand.CanExecute(null), "仍有有效图片时批量描边应可执行");

        viewModel.ProcessBatchCommand.Execute(null);
        WaitUntil(
            () => !viewModel.IsBusy && viewModel.BatchProcessedCount == 3,
            TimeSpan.FromSeconds(10),
            "图片描边批处理应完成全部队列项");

        Equal(2, viewModel.BatchCompletedCount, "批量描边成功数量");
        Equal(1, viewModel.BatchFailedCount, "批量描边失败或跳过数量");
        Equal(ImageOutlineBatchItemStatus.Skipped, viewModel.BatchItems[2].Status,
            "选择后消失的源图应被跳过且不阻断后续收束");
        var completedOutputs = viewModel.BatchItems
            .Where(item => item.Status == ImageOutlineBatchItemStatus.Completed)
            .Select(item => item.OutputPath)
            .ToArray();
        Equal(2, completedOutputs.Length, "批量描边应落盘两个成功产物");
        True(completedOutputs.All(File.Exists), "批量描边成功项必须真实落盘");
        Equal(2, completedOutputs.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            "同名源图必须获得唯一输出路径");
        True(completedOutputs.Any(path => Path.GetFileName(path).Contains("_002", StringComparison.Ordinal)),
            "同名批量输出应使用确定性数字后缀避免覆盖");
        True(File.Exists(viewModel.OutputPreviewPath), "批处理后应预览最新成功产物");
    }

    private static void ShellLoadsAndPersistsOutlineAndCheckerPreferences(string testRoot)
    {
        var settingsRoot = Path.Combine(testRoot, "settings");
        var inputDirectory = Path.Combine(settingsRoot, "inputs");
        var outputDirectory = Path.Combine(settingsRoot, "outputs");
        Directory.CreateDirectory(inputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var paths = new WorkspacePaths(settingsRoot);
        var settingsService = new UserSettingsService(paths);
        settingsService.Save(new UserSettingsModel
        {
            HasCompletedWorkflowSelection = true,
            ImageOutlinePlacement = ImageOutlinePlacement.Center.ToString(),
            ImageOutlineWidth = 7,
            ImageOutlineColorHex = "#123456",
            ImageOutlineAlphaThreshold = 9,
            ImageOutlineExpandCanvas = false,
            ImageOutlineLastInputDirectory = inputDirectory,
            ImageOutlineLastOutputDirectory = outputDirectory,
            BackgroundRemovalUseFakeTransparencyGrid = true,
            BackgroundRemovalCheckerColorAHex = "#F0F0F0",
            BackgroundRemovalCheckerColorBHex = "#A0A0A0",
            BackgroundRemovalEnableCheckerGridAlignment = true,
            BackgroundRemovalCheckerCellSize = 20,
            BackgroundRemovalCheckerOffsetX = 3,
            BackgroundRemovalCheckerOffsetY = 5,
            BackgroundRemovalCheckerBoundaryTolerance = 2
        });
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), settingsService, editor);

        try
        {
            var outline = shell.ImageOutline.CaptureSettings();
            Equal(ImageOutlinePlacement.Center.ToString(), outline.Placement, "Shell 加载描边种类");
            Equal(7, outline.Width, "Shell 加载描边宽度");
            Equal("#123456", outline.ColorHex, "Shell 加载描边颜色");
            Equal(9, outline.AlphaThreshold, "Shell 加载描边 Alpha 阈值");
            False(outline.ExpandCanvas, "Shell 加载描边扩展画布开关");
            Equal(inputDirectory, outline.LastInputDirectory, "Shell 加载描边最近输入目录");
            Equal(outputDirectory, outline.LastOutputDirectory, "Shell 加载描边最近输出目录");

            var checker = shell.BackgroundRemoval.CaptureSettings();
            True(checker.UseFakeTransparencyGrid, "Shell 加载假透明网格开关");
            Equal("#F0F0F0", checker.CheckerColorAHex, "Shell 加载棋盘色 A");
            Equal("#A0A0A0", checker.CheckerColorBHex, "Shell 加载棋盘色 B");
            Equal(20, checker.CheckerCellSize, "Shell 加载棋盘格尺寸");
            Equal(3, checker.CheckerOffsetX, "Shell 加载棋盘 X 偏移");
            Equal(5, checker.CheckerOffsetY, "Shell 加载棋盘 Y 偏移");
            Equal(2, checker.CheckerBoundaryTolerance, "Shell 加载棋盘边界容差");
            True(shell.BackgroundRemoval.RemoveEnclosedBackground,
                "新设置应默认识别并剔除镂空内部底色");
            False(checker.EdgeConnectedOnly,
                "默认镂空识别应在 Core 快照中使用全图色键模式");

            shell.ImageOutline.SelectedMode = shell.ImageOutline.ModeOptions.Single(
                option => option.Placement == ImageOutlinePlacement.Inside);
            shell.ImageOutline.OutlineWidth = 11;
            shell.ImageOutline.OutlineColorHex = "#ABCDEF";
            shell.ImageOutline.AlphaThreshold = 17;
            shell.ImageOutline.ExpandCanvas = true;
            shell.BackgroundRemoval.UseFakeTransparencyGrid = true;
            shell.BackgroundRemoval.CheckerColorAHex = "#EEEEEE";
            shell.BackgroundRemoval.CheckerColorBHex = "#999999";
            shell.BackgroundRemoval.CheckerCellSize = 24;
            shell.BackgroundRemoval.CheckerOffsetX = 6;
            shell.BackgroundRemoval.CheckerOffsetY = 8;
            shell.BackgroundRemoval.CheckerBoundaryTolerance = 3;

            var persisted = settingsService.Load();
            Equal(ImageOutlinePlacement.Inside.ToString(), persisted.ImageOutlinePlacement,
                "描边种类变化应即时持久化");
            Equal(11, persisted.ImageOutlineWidth, "描边宽度变化应即时持久化");
            Equal("#ABCDEF", persisted.ImageOutlineColorHex, "描边颜色变化应即时持久化");
            Equal(17, persisted.ImageOutlineAlphaThreshold, "描边 Alpha 阈值变化应即时持久化");
            True(persisted.ImageOutlineExpandCanvas, "描边扩展画布变化应即时持久化");
            True(persisted.BackgroundRemovalUseFakeTransparencyGrid,
                "假透明网格开关变化应即时持久化");
            Equal("#EEEEEE", persisted.BackgroundRemovalCheckerColorAHex,
                "棋盘色 A 变化应即时持久化");
            Equal("#999999", persisted.BackgroundRemovalCheckerColorBHex,
                "棋盘色 B 变化应即时持久化");
            Equal(24, persisted.BackgroundRemovalCheckerCellSize,
                "棋盘格尺寸变化应即时持久化");
            Equal(6, persisted.BackgroundRemovalCheckerOffsetX,
                "棋盘 X 偏移变化应即时持久化");
            Equal(8, persisted.BackgroundRemovalCheckerOffsetY,
                "棋盘 Y 偏移变化应即时持久化");
            Equal(3, persisted.BackgroundRemovalCheckerBoundaryTolerance,
                "棋盘边界容差变化应即时持久化");

            var generationSpec = shell.BackgroundRemoval.CreateGenerationSpec();
            False(generationSpec.ColorHex is "#EEEEEE" or "#999999",
                "素材生成首道底色不得被独立假透明棋盘格颜色替换");
            False(generationSpec.EdgeConnectedOnly,
                "素材生成应默认冻结镂空内部底色剔除规则");
            var serialized = File.ReadAllText(paths.UserSettingsPath);
            False(serialized.Contains("batchItems", StringComparison.OrdinalIgnoreCase),
                "图片描边批处理队列不得进入用户设置");
        }
        finally
        {
            shell.Shutdown();
        }
    }

    private static void LegacyBackgroundRemovalSettingsEnableHollowRemovalByDefault(string testRoot)
    {
        var settingsRoot = Path.Combine(testRoot, "legacy-background-removal-settings");
        var paths = new WorkspacePaths(settingsRoot);
        File.WriteAllText(
            paths.UserSettingsPath,
            """
            {
              "schemaVersion": 1,
              "theme": 0,
              "activeWorkflowKey": "ui",
              "hasCompletedWorkflowSelection": true,
              "backgroundRemovalEdgeConnectedOnly": true
            }
            """);
        var settingsService = new UserSettingsService(paths);
        var migrated = settingsService.Load();
        True(migrated.BackgroundRemovalRemoveEnclosedBackground,
            "旧设置缺少正向字段时应迁移为默认开启镂空识别");

        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), settingsService, editor);

        try
        {
            True(shell.BackgroundRemoval.RemoveEnclosedBackground,
                "旧设置加载到 Shell 后应默认开启镂空识别");
            False(shell.BackgroundRemoval.EdgeConnectedOnly,
                "旧设置中的历史默认值不得覆盖新的正向默认行为");

            shell.BackgroundRemoval.RemoveEnclosedBackground = false;
            var disabled = settingsService.Load();
            False(disabled.BackgroundRemovalRemoveEnclosedBackground,
                "用户关闭镂空识别后应持久化正向设置");
            True(disabled.BackgroundRemovalEdgeConnectedOnly,
                "关闭镂空识别后应同步旧版兼容字段");

            shell.BackgroundRemoval.RemoveEnclosedBackground = true;
            var enabled = settingsService.Load();
            True(enabled.BackgroundRemovalRemoveEnclosedBackground,
                "重新开启镂空识别后应持久化正向设置");
            False(enabled.BackgroundRemovalEdgeConnectedOnly,
                "重新开启镂空识别后应同步旧版兼容字段");
        }
        finally
        {
            shell.Shutdown();
        }
    }

    private static void SaveTransparentFixture(string path, Rgba32 subjectColor)
    {
        using var image = new Image<Rgba32>(8, 8, new Rgba32(0, 0, 0, 0));
        for (var y = 2; y <= 5; y++)
        {
            for (var x = 2; x <= 5; x++)
            {
                image[x, y] = subjectColor;
            }
        }

        image.Save(path, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string message)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(10);
        }

        if (!condition())
        {
            throw new InvalidOperationException(message);
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

    private sealed class FixedOutlineInputPicker(IReadOnlyList<string> inputPaths) : IImageOutlineInputPicker
    {
        public string? SelectInputFile(string? initialDirectory) => inputPaths.FirstOrDefault();

        public IReadOnlyList<string> SelectInputFiles(string? initialDirectory) => inputPaths;
    }

    private sealed class FixedOutlineOutputPicker(string outputDirectory) : IImageOutlineOutputPicker
    {
        public string? SelectOutputFile(string? initialDirectory, string suggestedFileName) =>
            Path.Combine(outputDirectory, suggestedFileName);

        public string? SelectOutputDirectory(string? initialDirectory) => outputDirectory;
    }
}

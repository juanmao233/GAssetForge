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
        CheckerAlignmentEditPersistsOnlyOnCommit(testRoot);
        LegacyIntegerCheckerSettingsLoadIntoFractionalModel(testRoot);
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
            BackgroundRemovalCheckerCellSize = 20.5d,
            BackgroundRemovalCheckerOffsetX = 3.25d,
            BackgroundRemovalCheckerOffsetY = 5.75d,
            BackgroundRemovalCheckerBoundaryTolerance = 1.5d,
            BackgroundRemovalEnableSmartCheckerDetection = true,
            BackgroundRemovalCheckerDetectionMinimumCellSize = 4.5d,
            BackgroundRemovalCheckerDetectionMaximumCellSize = 96.5d,
            BackgroundRemovalCheckerDetectionSearchRangePercent = 30d
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
            Near(20.5d, checker.CheckerCellSize, 0.000001d, "Shell 加载小数棋盘格尺寸");
            Near(3.25d, checker.CheckerOffsetX, 0.000001d, "Shell 加载小数棋盘 X 偏移");
            Near(5.75d, checker.CheckerOffsetY, 0.000001d, "Shell 加载小数棋盘 Y 偏移");
            Near(1.5d, checker.CheckerBoundaryTolerance, 0.000001d, "Shell 加载小数棋盘边界容差");
            True(checker.EnableSmartCheckerDetection, "Shell 加载逐图智能棋盘识别开关");
            Near(4.5d, checker.CheckerDetectionMinimumCellSize, 0.000001d,
                "Shell 加载智能识别最小格宽");
            Near(96.5d, checker.CheckerDetectionMaximumCellSize, 0.000001d,
                "Shell 加载智能识别最大格宽");
            Near(30d, checker.CheckerDetectionSearchRangePercent, 0.000001d,
                "Shell 加载四角扫描范围");
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
            shell.BackgroundRemoval.CheckerCellSize = 24.75d;
            shell.BackgroundRemoval.CheckerOffsetX = 6.125d;
            shell.BackgroundRemoval.CheckerOffsetY = 8.875d;
            shell.BackgroundRemoval.CheckerBoundaryTolerance = 2.25d;
            shell.BackgroundRemoval.EnableSmartCheckerDetection = false;
            shell.BackgroundRemoval.CheckerDetectionMinimumCellSize = 6.25d;
            shell.BackgroundRemoval.CheckerDetectionMaximumCellSize = 128.75d;
            shell.BackgroundRemoval.CheckerDetectionSearchRangePercent = 35d;

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
            Near(24.75d, persisted.BackgroundRemovalCheckerCellSize, 0.000001d,
                "小数棋盘格尺寸变化应即时持久化");
            Near(6.125d, persisted.BackgroundRemovalCheckerOffsetX, 0.000001d,
                "小数棋盘 X 偏移变化应即时持久化");
            Near(8.875d, persisted.BackgroundRemovalCheckerOffsetY, 0.000001d,
                "小数棋盘 Y 偏移变化应即时持久化");
            Near(2.25d, persisted.BackgroundRemovalCheckerBoundaryTolerance, 0.000001d,
                "小数棋盘边界容差变化应即时持久化");
            False(persisted.BackgroundRemovalEnableSmartCheckerDetection,
                "逐图智能棋盘识别开关变化应即时持久化");
            Near(6.25d, persisted.BackgroundRemovalCheckerDetectionMinimumCellSize, 0.000001d,
                "智能识别最小格宽变化应即时持久化");
            Near(128.75d, persisted.BackgroundRemovalCheckerDetectionMaximumCellSize, 0.000001d,
                "智能识别最大格宽变化应即时持久化");
            Near(35d, persisted.BackgroundRemovalCheckerDetectionSearchRangePercent, 0.000001d,
                "四角扫描范围变化应即时持久化");

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

    private static void LegacyIntegerCheckerSettingsLoadIntoFractionalModel(string testRoot)
    {
        var settingsRoot = Path.Combine(testRoot, "legacy-integer-checker-settings");
        var paths = new WorkspacePaths(settingsRoot);
        Directory.CreateDirectory(settingsRoot);
        File.WriteAllText(
            paths.UserSettingsPath,
            """
            {
              "schemaVersion": 1,
              "theme": 1,
              "activeWorkflowKey": "tilemap",
              "hasCompletedWorkflowSelection": true,
              "backgroundRemovalUseFakeTransparencyGrid": true,
              "backgroundRemovalCheckerCellSize": 21,
              "backgroundRemovalCheckerOffsetX": 4,
              "backgroundRemovalCheckerOffsetY": 6,
              "backgroundRemovalCheckerBoundaryTolerance": 2
            }
            """);

        var settings = new UserSettingsService(paths).Load();
        Equal(AppTheme.Light, settings.Theme,
            "读取旧整数网格设置失败时不得把整份用户设置重置为默认值");
        Equal("tilemap", settings.ActiveWorkflowKey,
            "旧设置中的工作流标记必须保留");
        Near(21d, settings.BackgroundRemovalCheckerCellSize, 0.000001d,
            "旧整数格宽必须兼容读取为 double");
        Near(4d, settings.BackgroundRemovalCheckerOffsetX, 0.000001d,
            "旧整数 X 偏移必须兼容读取为 double");
        Near(6d, settings.BackgroundRemovalCheckerOffsetY, 0.000001d,
            "旧整数 Y 偏移必须兼容读取为 double");
        Near(2d, settings.BackgroundRemovalCheckerBoundaryTolerance, 0.000001d,
            "旧整数边界容差必须兼容读取为 double");
    }

    private static void CheckerAlignmentEditPersistsOnlyOnCommit(string testRoot)
    {
        var settingsRoot = Path.Combine(testRoot, "checker-alignment-edit-transaction");
        var paths = new WorkspacePaths(settingsRoot);
        var settingsService = new UserSettingsService(paths);
        settingsService.Save(new UserSettingsModel
        {
            HasCompletedWorkflowSelection = true,
            BackgroundRemovalUseFakeTransparencyGrid = true,
            BackgroundRemovalCheckerCellSize = 16.5d,
            BackgroundRemovalCheckerOffsetX = 2.25d,
            BackgroundRemovalCheckerOffsetY = 3.75d,
            BackgroundRemovalCheckerBoundaryTolerance = 1.25d
        });
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), settingsService, editor);

        try
        {
            var settingsChangedCount = 0;
            shell.BackgroundRemoval.SettingsChanged += (_, _) => settingsChangedCount++;

            shell.BackgroundRemoval.BeginCheckerAlignmentEdit();
            shell.BackgroundRemoval.CheckerCellSize = 31.5d;
            shell.BackgroundRemoval.CheckerOffsetX = 7.125d;
            shell.BackgroundRemoval.CheckerOffsetY = 9.875d;
            shell.BackgroundRemoval.CheckerBoundaryTolerance = 2.5d;

            Equal(0, settingsChangedCount,
                "拖动标尺期间棋盘参数变化不得触发用户设置写盘事件");
            var whileDragging = settingsService.Load();
            Near(16.5d, whileDragging.BackgroundRemovalCheckerCellSize, 0.000001d,
                "拖动标尺期间持久化格宽必须保持起始值");
            Near(2.25d, whileDragging.BackgroundRemovalCheckerOffsetX, 0.000001d,
                "拖动标尺期间持久化 X 偏移必须保持起始值");
            Near(3.75d, whileDragging.BackgroundRemovalCheckerOffsetY, 0.000001d,
                "拖动标尺期间持久化 Y 偏移必须保持起始值");
            Near(1.25d, whileDragging.BackgroundRemovalCheckerBoundaryTolerance, 0.000001d,
                "拖动标尺期间持久化边界容差必须保持起始值");

            shell.BackgroundRemoval.CommitCheckerAlignmentEdit();
            Equal(1, settingsChangedCount,
                "完成一次标尺拖动只能发布一次用户设置变化事件");
            var committed = settingsService.Load();
            Near(31.5d, committed.BackgroundRemovalCheckerCellSize, 0.000001d,
                "提交标尺后小数格宽必须落盘");
            Near(7.125d, committed.BackgroundRemovalCheckerOffsetX, 0.000001d,
                "提交标尺后小数 X 偏移必须落盘");
            Near(9.875d, committed.BackgroundRemovalCheckerOffsetY, 0.000001d,
                "提交标尺后小数 Y 偏移必须落盘");
            Near(2.5d, committed.BackgroundRemovalCheckerBoundaryTolerance, 0.000001d,
                "提交标尺后小数边界容差必须落盘");

            shell.BackgroundRemoval.BeginCheckerAlignmentEdit();
            shell.BackgroundRemoval.CheckerCellSize = 48.25d;
            shell.BackgroundRemoval.CheckerOffsetX = 12.5d;
            shell.BackgroundRemoval.CheckerOffsetY = 14.75d;
            shell.BackgroundRemoval.CheckerBoundaryTolerance = 4.5d;
            shell.BackgroundRemoval.CancelCheckerAlignmentEdit();

            Equal(1, settingsChangedCount,
                "取消标尺拖动不得发布新的用户设置变化事件");
            Near(31.5d, shell.BackgroundRemoval.CheckerCellSize, 0.000001d,
                "取消标尺拖动应恢复起始格宽");
            Near(7.125d, shell.BackgroundRemoval.CheckerOffsetX, 0.000001d,
                "取消标尺拖动应恢复起始 X 偏移");
            Near(9.875d, shell.BackgroundRemoval.CheckerOffsetY, 0.000001d,
                "取消标尺拖动应恢复起始 Y 偏移");
            Near(2.5d, shell.BackgroundRemoval.CheckerBoundaryTolerance, 0.000001d,
                "取消标尺拖动应恢复起始边界容差");
            var afterCancel = settingsService.Load();
            Near(31.5d, afterCancel.BackgroundRemovalCheckerCellSize, 0.000001d,
                "取消标尺拖动不得覆盖已提交格宽");
            Near(7.125d, afterCancel.BackgroundRemovalCheckerOffsetX, 0.000001d,
                "取消标尺拖动不得覆盖已提交 X 偏移");
            Near(9.875d, afterCancel.BackgroundRemovalCheckerOffsetY, 0.000001d,
                "取消标尺拖动不得覆盖已提交 Y 偏移");
            Near(2.5d, afterCancel.BackgroundRemovalCheckerBoundaryTolerance, 0.000001d,
                "取消标尺拖动不得覆盖已提交边界容差");
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

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException(
                $"{message}：预期 {expected} ± {tolerance}，实际 {actual}。");
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

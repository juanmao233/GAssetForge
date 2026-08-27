using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using GAssetForge.App.ViewModels;
using GAssetForge.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace GAssetForge.SmokeTests;

internal static class CheckerGridDetectionSmokeTests
{
    private static readonly ImageSharpCheckerGridDetectionService Detector = new();

    public static void Run()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "checker-grid-detection",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        try
        {
            ExactFifteenPixelGridReportsColorsGeometryAndCounts(testRoot);
            FractionalCellSizeAndPhaseSurviveScaledCanvases(testRoot);
            NormalizedOffsetPreservesTheDetectedCheckerColorPhase(testRoot);
            BlurredBoundariesStillRevealTheTwoDominantCheckerColors(testRoot);
            CornerOcclusionUsesTheConfiguredSearchRange(testRoot);
            PerImageDetectionDoesNotReuseTheFirstGridGeometry(testRoot);
            BatchRemovalAppliesEachImagesDetectedGridWithoutChangingGlobalSettings(testRoot);
            BatchDetectionFailureSkipsRemovalAndContinuesWithTheNextImage(testRoot);
            SingleImageDetectionPublishesOnceAndDiscardsResultsForChangedInput(testRoot);
            SmartDetectionValidationIgnoresManualCheckerFieldsThatItWillReplace(testRoot);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void ExactFifteenPixelGridReportsColorsGeometryAndCounts(string testRoot)
    {
        const double cellSize = 15d;
        var inputPath = Path.Combine(testRoot, "exact-15px.png");
        SaveCheckerFixture(
            inputPath,
            width: 150,
            height: 120,
            ParseHex("#EEEEEE"),
            ParseHex("#B8B8B8"),
            cellSize,
            offsetX: 0d,
            offsetY: 0d);

        var result = Detector.Detect(inputPath, CreateSettings(8d, 24d, searchRange: 60));

        True(result.IsDetected, $"15px 精确棋盘应被识别：{result.FailureReason}");
        Near(cellSize, result.CellSize, 0.35d, "精确棋盘格宽");
        NearCyclic(0d, result.OffsetX, result.CellSize, 0.5d, "精确棋盘 X 相位");
        NearCyclic(0d, result.OffsetY, result.CellSize, 0.5d, "精确棋盘 Y 相位");
        AssertDetectedColors(result, "#EEEEEE", "#B8B8B8", tolerance: 3d);
        Equal(10, result.HorizontalCellCount, "150px 画布应横向数出 10 个完整格子");
        Equal(8, result.VerticalCellCount, "120px 画布应纵向数出 8 个完整格子");
        True(result.DetectedCornerCount >= 2, "无主体遮挡时至少应由两个角提供一致证据");
        True(result.Confidence >= 0.8d, "精确棋盘应给出高置信度");
        AssertCellSizeRangeContains(result, cellSize, maximumSpan: 2d);
    }

    private static void FractionalCellSizeAndPhaseSurviveScaledCanvases(string testRoot)
    {
        const double cellSize = 14.5d;
        const double offsetX = 3.25d;
        const double offsetY = 8.75d;
        var cases = new[]
        {
            (Name: "fractional-small", Width: 174, Height: 116),
            (Name: "fractional-wide", Width: 377, Height: 203)
        };

        foreach (var testCase in cases)
        {
            var inputPath = Path.Combine(testRoot, $"{testCase.Name}.png");
            SaveCheckerFixture(
                inputPath,
                testCase.Width,
                testCase.Height,
                ParseHex("#F2F2F2"),
                ParseHex("#C4C4C4"),
                cellSize,
                offsetX,
                offsetY);

            var result = Detector.Detect(inputPath, CreateSettings(10d, 20d, searchRange: 72));

            True(result.IsDetected,
                $"{testCase.Width}×{testCase.Height} 的小数格宽棋盘应被识别：{result.FailureReason}");
            Near(cellSize, result.CellSize, 0.55d, $"{testCase.Name} 小数格宽");
            NearCyclic(offsetX, result.OffsetX, result.CellSize, 0.75d, $"{testCase.Name} X 相位");
            NearCyclic(offsetY, result.OffsetY, result.CellSize, 0.75d, $"{testCase.Name} Y 相位");
            AssertDetectedColors(result, "#F2F2F2", "#C4C4C4", tolerance: 4d);
            AssertCellSizeRangeContains(result, cellSize, maximumSpan: 3d);
        }
    }

    private static void BlurredBoundariesStillRevealTheTwoDominantCheckerColors(string testRoot)
    {
        const double cellSize = 15d;
        const double offsetX = 4d;
        const double offsetY = 6d;
        var inputPath = Path.Combine(testRoot, "blurred-boundaries.png");
        SaveCheckerFixture(
            inputPath,
            width: 210,
            height: 165,
            ParseHex("#F4F4F4"),
            ParseHex("#AFAFAF"),
            cellSize,
            offsetX,
            offsetY,
            blurSigma: 0.85f,
            addDeterministicNoise: true);

        var result = Detector.Detect(inputPath, CreateSettings(9d, 22d, searchRange: 75));

        True(result.IsDetected, $"羽化和轻微色值波动不应破坏棋盘识别：{result.FailureReason}");
        Near(cellSize, result.CellSize, 0.8d, "模糊边界格宽");
        NearCyclic(offsetX, result.OffsetX, result.CellSize, 1.25d, "模糊边界 X 相位");
        NearCyclic(offsetY, result.OffsetY, result.CellSize, 1.25d, "模糊边界 Y 相位");
        AssertDetectedColors(result, "#F4F4F4", "#AFAFAF", tolerance: 12d);
        True(result.Confidence >= 0.55d, "模糊棋盘仍应达到可操作的识别置信度");
    }

    private static void NormalizedOffsetPreservesTheDetectedCheckerColorPhase(string testRoot)
    {
        const double cellSize = 15d;
        const double rawOffsetX = 18d;
        const double rawOffsetY = 4d;
        const int subjectX = 73;
        const int subjectY = 61;
        var subject = new Rgba32(210, 45, 120, 255);
        var inputPath = Path.Combine(testRoot, "normalized-phase-input.png");
        var outputPath = Path.Combine(testRoot, "normalized-phase-output.png");
        SaveCheckerFixture(
            inputPath,
            width: 180,
            height: 135,
            ParseHex("#F1F1F1"),
            ParseHex("#B9B9B9"),
            cellSize,
            rawOffsetX,
            rawOffsetY,
            configure: image => image[subjectX, subjectY] = subject);

        var detection = Detector.Detect(
            inputPath,
            CreateSettings(10d, 20d, searchRange: 72, minimumConfidence: 0.45d));

        True(detection.IsDetected,
            $"跨奇数格的原始相位应被识别：{detection.FailureReason}");
        True(detection.OffsetX >= 0d && detection.OffsetX < detection.CellSize,
            "检测 X 相位必须规范化到 [0, CellSize)");
        True(detection.OffsetY >= 0d && detection.OffsetY < detection.CellSize,
            "检测 Y 相位必须规范化到 [0, CellSize)");
        NearCyclic(rawOffsetX, detection.OffsetX, detection.CellSize, 0.75d,
            "规范化后的 X 网格线位置");
        NearCyclic(rawOffsetY, detection.OffsetY, detection.CellSize, 0.75d,
            "规范化后的 Y 网格线位置");

        var removal = new ImageSharpBackgroundRemovalService();
        removal.RemoveToPng(
            inputPath,
            outputPath,
            new BackgroundRemovalSettings
            {
                UseFakeTransparencyGrid = true,
                CheckerColorAHex = detection.ColorAHex,
                CheckerColorBHex = detection.ColorBHex,
                EnableCheckerGridAlignment = true,
                CheckerCellSize = detection.CellSize,
                CheckerOffsetX = detection.OffsetX,
                CheckerOffsetY = detection.OffsetY,
                CheckerBoundaryTolerance = 1d,
                TransparentThreshold = 8d,
                OpaqueThreshold = 24d,
                EdgeContract = 0,
                EdgeFeather = 0,
                Despill = false,
                EdgeConnectedOnly = false
            });

        using var output = Image.Load<Rgba32>(outputPath);
        for (var y = 0; y < output.Height; y++)
        {
            for (var x = 0; x < output.Width; x++)
            {
                if (x == subjectX && y == subjectY)
                {
                    Equal(subject, output[x, y],
                        "检测参数回填后应保留非棋盘色主体");
                    continue;
                }

                Equal((byte)0, output[x, y].A,
                    $"规范 offset 时必须同步维持 A/B 相位，背景像素 ({x},{y}) 应透明");
            }
        }
    }

    private static void CornerOcclusionUsesTheConfiguredSearchRange(string testRoot)
    {
        const double cellSize = 12d;
        const int occlusionSize = 26;
        var inputPath = Path.Combine(testRoot, "four-corners-occluded.png");
        SaveCheckerFixture(
            inputPath,
            width: 180,
            height: 144,
            ParseHex("#EFEFEF"),
            ParseHex("#BDBDBD"),
            cellSize,
            offsetX: 2d,
            offsetY: 5d,
            configure: image => PaintCornerOcclusions(
                image,
                occlusionSize,
                new Rgba32(38, 96, 174, 255)));

        var tooShallow = Detector.Detect(
            inputPath,
            CreateSettings(8d, 18d, searchRange: 16, minimumConfidence: 0.6d));
        False(tooShallow.IsDetected,
            "搜索范围完全落在四角主体遮挡内时不得臆造棋盘参数");
        True(!string.IsNullOrWhiteSpace(tooShallow.FailureReason),
            "无法识别时应返回可展示的失败原因");

        var deepEnough = Detector.Detect(
            inputPath,
            CreateSettings(8d, 18d, searchRange: 72, minimumConfidence: 0.45d));
        True(deepEnough.IsDetected,
            $"搜索范围越过四角遮挡后应从内部棋盘证据恢复：{deepEnough.FailureReason}");
        Near(cellSize, deepEnough.CellSize, 0.75d, "四角遮挡后的格宽");
        NearCyclic(2d, deepEnough.OffsetX, deepEnough.CellSize, 1d, "四角遮挡后的 X 相位");
        NearCyclic(5d, deepEnough.OffsetY, deepEnough.CellSize, 1d, "四角遮挡后的 Y 相位");
        AssertDetectedColors(deepEnough, "#EFEFEF", "#BDBDBD", tolerance: 6d);
    }

    private static void PerImageDetectionDoesNotReuseTheFirstGridGeometry(string testRoot)
    {
        var cases = new[]
        {
            new DetectionCase("batch-grid-a", 136, 102, 8.5d, 1.25d, 4.5d, "#F0F0F0", "#C0C0C0"),
            new DetectionCase("batch-grid-b", 192, 132, 12d, 7d, 2d, "#E8E8E8", "#B0B0B0"),
            new DetectionCase("batch-grid-c", 308, 231, 19.25d, 5.5d, 11.75d, "#F8F8F8", "#C8C8C8")
        };

        var results = new List<CheckerGridDetectionResult>();
        foreach (var testCase in cases)
        {
            var inputPath = Path.Combine(testRoot, $"{testCase.Name}.png");
            SaveCheckerFixture(
                inputPath,
                testCase.Width,
                testCase.Height,
                ParseHex(testCase.ColorAHex),
                ParseHex(testCase.ColorBHex),
                testCase.CellSize,
                testCase.OffsetX,
                testCase.OffsetY);
            results.Add(Detector.Detect(
                inputPath,
                CreateSettings(6d, 24d, searchRange: 76, minimumConfidence: 0.45d)));
        }

        for (var index = 0; index < cases.Length; index++)
        {
            var testCase = cases[index];
            var result = results[index];
            True(result.IsDetected, $"批量第 {index + 1} 张应独立识别：{result.FailureReason}");
            Near(testCase.CellSize, result.CellSize, 0.8d, $"批量第 {index + 1} 张独立格宽");
            NearCyclic(testCase.OffsetX, result.OffsetX, result.CellSize, 1d,
                $"批量第 {index + 1} 张独立 X 相位");
            NearCyclic(testCase.OffsetY, result.OffsetY, result.CellSize, 1d,
                $"批量第 {index + 1} 张独立 Y 相位");
        }

        True(results.Select(result => Math.Round(result.CellSize, 1)).Distinct().Count() == cases.Length,
            "批量识别不得把第一张图片的网格尺寸复用到后续图片");
    }

    private static void BatchRemovalAppliesEachImagesDetectedGridWithoutChangingGlobalSettings(string testRoot)
    {
        var batchRoot = Path.Combine(testRoot, "view-model-batch-detection");
        var outputDirectory = Path.Combine(batchRoot, "output");
        Directory.CreateDirectory(outputDirectory);
        var firstPath = Path.Combine(batchRoot, "first.png");
        var secondPath = Path.Combine(batchRoot, "second.png");
        SaveCheckerFixture(
            firstPath,
            100,
            80,
            ParseHex("#EEEEEE"),
            ParseHex("#BBBBBB"),
            10d,
            2d,
            4d);
        SaveCheckerFixture(
            secondPath,
            240,
            120,
            ParseHex("#F6F6F6"),
            ParseHex("#C6C6C6"),
            20d,
            7d,
            11d);

        var detectionService = new RecordingCheckerGridDetectionService(new Dictionary<string, CheckerGridDetectionResult>(
            StringComparer.OrdinalIgnoreCase)
        {
            [Path.GetFullPath(firstPath)] = CreateDetectedResult(
                "#EEEEEE", "#BBBBBB", 10d, 2d, 4d, horizontalCells: 10, verticalCells: 8),
            [Path.GetFullPath(secondPath)] = CreateDetectedResult(
                "#F6F6F6", "#C6C6C6", 20d, 7d, 11d, horizontalCells: 12, verticalCells: 6)
        });
        var removalService = new RecordingBackgroundRemovalService();
        var viewModel = new BackgroundRemovalViewModel(
            removalService,
            new FixedBatchInputPicker([firstPath, secondPath]),
            new FixedBatchOutputPicker(outputDirectory),
            useFakeTransparencyGrid: true,
            checkerColorAHex: "#FFFFFF",
            checkerColorBHex: "#CCCCCC",
            checkerCellSize: 16d,
            checkerOffsetX: 0d,
            checkerOffsetY: 0d,
            checkerGridDetectionService: detectionService)
        {
            EnableSmartCheckerDetection = true,
            CheckerDetectionMinimumCellSize = 6d,
            CheckerDetectionMaximumCellSize = 28d,
            CheckerDetectionSearchRangePercent = 25d
        };

        viewModel.ChooseBatchInputsCommand.Execute(null);
        viewModel.ChooseBatchOutputDirectoryCommand.Execute(null);
        True(viewModel.ProcessBatchCommand.CanExecute(null),
            "开启智能棋盘识别且队列就绪后应允许批量剔除");
        viewModel.ProcessBatchCommand.Execute(null);
        WaitUntil(
            () => !viewModel.IsBatchRunning && viewModel.BatchCompletedCount == 2,
            TimeSpan.FromSeconds(10),
            "批量智能棋盘识别与剔除应完成");

        Equal(2, detectionService.Calls.Count, "批量处理必须对每张图片各调用一次智能识别");
        Equal(Path.GetFullPath(firstPath), detectionService.Calls[0].InputPath,
            "第一项检测路径");
        Near(20d, detectionService.Calls[0].Settings.SearchRange, 0.01d,
            "第一项搜索范围应按自身短边的 25% 换算");
        Equal(Path.GetFullPath(secondPath), detectionService.Calls[1].InputPath,
            "第二项检测路径");
        Near(30d, detectionService.Calls[1].Settings.SearchRange, 0.01d,
            "第二项搜索范围应按自身短边的 25% 换算");
        True(detectionService.Calls.All(call =>
                Math.Abs(call.Settings.MinimumCellSize - 6d) < 0.001d &&
                Math.Abs(call.Settings.MaximumCellSize - 28d) < 0.001d),
            "每图检测应收到 UI 配置的网格尺寸范围");

        var removalCalls = removalService.Calls.ToArray();
        Equal(2, removalCalls.Length, "两张图片都应进入实际背景剔除");
        AssertRemovalSettings(removalCalls[0], firstPath,
            "#EEEEEE", "#BBBBBB", 10d, 2d, 4d);
        AssertRemovalSettings(removalCalls[1], secondPath,
            "#F6F6F6", "#C6C6C6", 20d, 7d, 11d);
        Equal(2, viewModel.BatchSucceededCount, "每图检测成功后两项都应完成");
        Equal(0, viewModel.BatchFailedCount, "不同格宽不应导致批处理失败");

        Equal("#FFFFFF", viewModel.CheckerColorAHex,
            "批量逐图识别不得覆盖全局棋盘颜色 A");
        Equal("#CCCCCC", viewModel.CheckerColorBHex,
            "批量逐图识别不得覆盖全局棋盘颜色 B");
        Near(16d, viewModel.CheckerCellSize, 0.001d,
            "批量逐图识别不得覆盖全局格宽");
        Near(0d, viewModel.CheckerOffsetX, 0.001d,
            "批量逐图识别不得覆盖全局 X 相位");
        Near(0d, viewModel.CheckerOffsetY, 0.001d,
            "批量逐图识别不得覆盖全局 Y 相位");
    }

    private static void BatchDetectionFailureSkipsRemovalAndContinuesWithTheNextImage(string testRoot)
    {
        var batchRoot = Path.Combine(testRoot, "batch-detection-failure-isolation");
        var outputDirectory = Path.Combine(batchRoot, "output");
        Directory.CreateDirectory(outputDirectory);
        var firstPath = Path.Combine(batchRoot, "first-valid.png");
        var failedPath = Path.Combine(batchRoot, "middle-undetected.png");
        var lastPath = Path.Combine(batchRoot, "last-valid.png");
        SaveCheckerFixture(firstPath, 96, 72, ParseHex("#EEEEEE"), ParseHex("#BBBBBB"), 12d, 2d, 3d);
        SaveCheckerFixture(failedPath, 108, 84, ParseHex("#F2F2F2"), ParseHex("#C2C2C2"), 9d, 1d, 5d);
        SaveCheckerFixture(lastPath, 160, 120, ParseHex("#FAFAFA"), ParseHex("#C8C8C8"), 20d, 7d, 11d);

        const string failureReason = "四角搜索范围内没有足够的灰白周期证据";
        var detectionService = new RecordingCheckerGridDetectionService(new Dictionary<string, CheckerGridDetectionResult>(
            StringComparer.OrdinalIgnoreCase)
        {
            [Path.GetFullPath(firstPath)] = CreateDetectedResult(
                "#EEEEEE", "#BBBBBB", 12d, 2d, 3d, horizontalCells: 8, verticalCells: 6),
            [Path.GetFullPath(failedPath)] = new CheckerGridDetectionResult
            {
                IsDetected = false,
                FailureReason = failureReason,
                Confidence = 0.18d
            },
            [Path.GetFullPath(lastPath)] = CreateDetectedResult(
                "#FAFAFA", "#C8C8C8", 20d, 7d, 11d, horizontalCells: 8, verticalCells: 6)
        });
        var removalService = new RecordingBackgroundRemovalService();
        var viewModel = new BackgroundRemovalViewModel(
            removalService,
            new FixedBatchInputPicker([firstPath, failedPath, lastPath]),
            new FixedBatchOutputPicker(outputDirectory),
            useFakeTransparencyGrid: true,
            checkerGridDetectionService: detectionService,
            enableSmartCheckerDetection: true);

        viewModel.ChooseBatchInputsCommand.Execute(null);
        viewModel.ChooseBatchOutputDirectoryCommand.Execute(null);
        viewModel.ProcessBatchCommand.Execute(null);
        WaitUntil(
            () => !viewModel.IsBatchRunning && viewModel.BatchCompletedCount == 3,
            TimeSpan.FromSeconds(10),
            "中间项识别失败后批处理仍应完成剩余队列");

        Equal(3, detectionService.Calls.Count,
            "中间项失败不得阻止后一张图片执行智能识别");
        var removalCalls = removalService.Calls.ToArray();
        Equal(2, removalCalls.Length,
            "智能识别失败项不得进入背景剔除服务");
        Equal(Path.GetFullPath(firstPath), removalCalls[0].InputPath,
            "第一张成功项应进入背景剔除");
        Equal(Path.GetFullPath(lastPath), removalCalls[1].InputPath,
            "识别失败后的后一张成功项仍应进入背景剔除");
        True(removalCalls.All(call => !string.Equals(
                call.InputPath,
                Path.GetFullPath(failedPath),
                StringComparison.OrdinalIgnoreCase)),
            "中间识别失败项绝不能使用全局或前一张参数继续剔除");

        Equal(BackgroundRemovalBatchItemStatus.Completed, viewModel.BatchItems[0].Status,
            "第一张图片应完成");
        Equal(BackgroundRemovalBatchItemStatus.Failed, viewModel.BatchItems[1].Status,
            "中间识别失败只应标记自身 Failed");
        Equal(BackgroundRemovalBatchItemStatus.Completed, viewModel.BatchItems[2].Status,
            "失败项之后的图片应继续并完成");
        True(viewModel.BatchItems[1].CheckerDetectionText.Contains(failureReason, StringComparison.Ordinal),
            "失败队列项应保留智能识别原因供用户诊断");
        True(viewModel.BatchItems[1].Message.Contains(failureReason, StringComparison.Ordinal),
            "失败队列项的操作消息应说明未进入背景剔除的原因");
        False(File.Exists(viewModel.BatchItems[1].OutputPath),
            "识别失败项不应产生伪造输出文件");
        True(File.Exists(viewModel.BatchItems[2].OutputPath),
            "失败项之后的成功图片应真实落盘");
        Equal(2, viewModel.BatchSucceededCount, "失败隔离后成功计数");
        Equal(1, viewModel.BatchFailedCount, "失败隔离后失败计数");
    }

    private static void SingleImageDetectionPublishesOnceAndDiscardsResultsForChangedInput(string testRoot)
    {
        var commandRoot = Path.Combine(testRoot, "single-detection-command");
        Directory.CreateDirectory(commandRoot);
        var successPath = Path.Combine(commandRoot, "success.png");
        SaveCheckerFixture(
            successPath,
            150,
            120,
            ParseHex("#F0F0F0"),
            ParseHex("#BDBDBD"),
            12.5d,
            3.25d,
            7.5d);
        var expected = CreateDetectedResult(
            "#F0F0F0",
            "#BDBDBD",
            12.5d,
            3.25d,
            7.5d,
            horizontalCells: 12,
            verticalCells: 10);
        var successDetector = new RecordingCheckerGridDetectionService(
            new Dictionary<string, CheckerGridDetectionResult>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.GetFullPath(successPath)] = expected
            });
        var successViewModel = new BackgroundRemovalViewModel(
            new RecordingBackgroundRemovalService(),
            new SequenceInputPicker([successPath]),
            new FixedBatchOutputPicker(commandRoot),
            useFakeTransparencyGrid: true,
            checkerColorAHex: "#FFFFFF",
            checkerColorBHex: "#CCCCCC",
            enableCheckerGridAlignment: false,
            checkerCellSize: 16d,
            checkerOffsetX: 0d,
            checkerOffsetY: 0d,
            checkerGridDetectionService: successDetector,
            enableSmartCheckerDetection: true);
        successViewModel.ChooseInputCommand.Execute(null);
        var publishedSnapshots = new ConcurrentQueue<BackgroundRemovalToolSettingsSnapshot>();
        successViewModel.SettingsChanged += (_, args) => publishedSnapshots.Enqueue(args.Settings);

        True(successViewModel.DetectCheckerGridCommand.CanExecute(null),
            "选择单图并启用假透明模式后应允许智能识别");
        successViewModel.DetectCheckerGridCommand.Execute(null);
        WaitUntil(
            () => !successViewModel.IsBusy &&
                  successViewModel.CheckerDetectionStatusText.StartsWith("识别成功", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10),
            "单图智能识别命令应完成并回填结果");

        Equal(1, publishedSnapshots.Count,
            "颜色、格宽、偏移和对齐开关应作为一个设置事务只发布一次");
        var published = publishedSnapshots.Single();
        Equal(expected.ColorAHex, successViewModel.CheckerColorAHex, "单图命令回填颜色 A");
        Equal(expected.ColorBHex, successViewModel.CheckerColorBHex, "单图命令回填颜色 B");
        Near(expected.CellSize, successViewModel.CheckerCellSize, 0.001d, "单图命令回填格宽");
        Near(expected.OffsetX, successViewModel.CheckerOffsetX, 0.001d, "单图命令回填 X 相位");
        Near(expected.OffsetY, successViewModel.CheckerOffsetY, 0.001d, "单图命令回填 Y 相位");
        True(successViewModel.EnableCheckerGridAlignment,
            "智能识别成功应同时启用网格对齐");
        Equal(expected.ColorAHex, published.CheckerColorAHex,
            "唯一设置快照必须已经包含最终颜色 A");
        Equal(expected.ColorBHex, published.CheckerColorBHex,
            "唯一设置快照必须已经包含最终颜色 B");
        Near(expected.CellSize, published.CheckerCellSize, 0.001d,
            "唯一设置快照必须已经包含最终格宽");
        Near(expected.OffsetX, published.CheckerOffsetX, 0.001d,
            "唯一设置快照必须已经包含最终 X 相位");
        Near(expected.OffsetY, published.CheckerOffsetY, 0.001d,
            "唯一设置快照必须已经包含最终 Y 相位");
        True(published.EnableCheckerGridAlignment,
            "唯一设置快照必须包含已启用的网格对齐状态");

        var stalePath = Path.Combine(commandRoot, "stale-source.png");
        var replacementPath = Path.Combine(commandRoot, "replacement-source.png");
        SaveCheckerFixture(stalePath, 128, 96, ParseHex("#EEEEEE"), ParseHex("#B8B8B8"), 8d, 1d, 2d);
        SaveCheckerFixture(replacementPath, 144, 108, ParseHex("#F8F8F8"), ParseHex("#C8C8C8"), 18d, 4d, 6d);
        var staleResult = CreateDetectedResult(
            "#123456",
            "#ABCDEF",
            31d,
            13d,
            17d,
            horizontalCells: 4,
            verticalCells: 3);
        using var blockingDetector = new BlockingCheckerGridDetectionService(staleResult);
        var staleViewModel = new BackgroundRemovalViewModel(
            new RecordingBackgroundRemovalService(),
            new SequenceInputPicker([stalePath, replacementPath]),
            new FixedBatchOutputPicker(commandRoot),
            useFakeTransparencyGrid: true,
            checkerColorAHex: "#FFFFFF",
            checkerColorBHex: "#CCCCCC",
            enableCheckerGridAlignment: true,
            checkerCellSize: 16d,
            checkerOffsetX: 2d,
            checkerOffsetY: 3d,
            checkerGridDetectionService: blockingDetector,
            enableSmartCheckerDetection: true);
        staleViewModel.ChooseInputCommand.Execute(null);
        var staleSettingsPublishCount = 0;
        staleViewModel.SettingsChanged += (_, _) => Interlocked.Increment(ref staleSettingsPublishCount);

        staleViewModel.DetectCheckerGridCommand.Execute(null);
        True(blockingDetector.WaitUntilEntered(TimeSpan.FromSeconds(5)),
            "延迟检测器应收到旧输入请求");
        True(staleViewModel.IsBusy, "检测在途时 ViewModel 应保持忙碌状态");
        False(staleViewModel.ChooseInputCommand.CanExecute(null),
            "正常 UI 在检测期间应禁用输入选择");

        // 模拟项目切换或其他外部工作流在检测期间替换输入。RelayCommand 的公开
        // Execute seam 仍能触发同一选择行为，用于证明 ViewModel 自身不依赖 UI 禁用兜底。
        staleViewModel.ChooseInputCommand.Execute(null);
        Equal(Path.GetFullPath(replacementPath), staleViewModel.InputPath,
            "检测完成前输入应已切换到新图片");
        blockingDetector.Release();
        WaitUntil(
            () => !staleViewModel.IsBusy,
            TimeSpan.FromSeconds(10),
            "旧输入检测返回后命令应正常收束");

        Equal(Path.GetFullPath(replacementPath), staleViewModel.InputPath,
            "陈旧检测结果不得把当前输入切回旧图片");
        Equal("#FFFFFF", staleViewModel.CheckerColorAHex,
            "陈旧检测结果不得覆盖当前颜色 A");
        Equal("#CCCCCC", staleViewModel.CheckerColorBHex,
            "陈旧检测结果不得覆盖当前颜色 B");
        Near(16d, staleViewModel.CheckerCellSize, 0.001d,
            "陈旧检测结果不得覆盖当前格宽");
        Near(2d, staleViewModel.CheckerOffsetX, 0.001d,
            "陈旧检测结果不得覆盖当前 X 相位");
        Near(3d, staleViewModel.CheckerOffsetY, 0.001d,
            "陈旧检测结果不得覆盖当前 Y 相位");
        Equal(1, staleSettingsPublishCount,
            "输入切换只应发布输入变化本身，陈旧检测结果不得再发布设置");
        True(staleViewModel.CheckerDetectionStatusText.Contains("旧结果已丢弃", StringComparison.Ordinal),
            "界面应明确说明旧输入识别结果已丢弃");
    }

    private static void SmartDetectionValidationIgnoresManualCheckerFieldsThatItWillReplace(string testRoot)
    {
        var validationRoot = Path.Combine(testRoot, "smart-validation");
        var outputDirectory = Path.Combine(validationRoot, "output");
        Directory.CreateDirectory(outputDirectory);
        var inputPath = Path.Combine(validationRoot, "checker.png");
        SaveCheckerFixture(
            inputPath,
            128,
            96,
            ParseHex("#EEEEEE"),
            ParseHex("#BBBBBB"),
            12d,
            2d,
            4d);
        var viewModel = new BackgroundRemovalViewModel(
            new RecordingBackgroundRemovalService(),
            new FixedBatchInputPicker([inputPath]),
            new FixedBatchOutputPicker(outputDirectory),
            useFakeTransparencyGrid: true,
            checkerGridDetectionService: new RecordingCheckerGridDetectionService(
                new Dictionary<string, CheckerGridDetectionResult>()),
            enableSmartCheckerDetection: true,
            checkerDetectionMinimumCellSize: 8d,
            checkerDetectionMaximumCellSize: 32d,
            checkerDetectionSearchRangePercent: 25d);
        viewModel.ChooseInputCommand.Execute(null);
        viewModel.ChooseBatchInputsCommand.Execute(null);
        viewModel.ChooseBatchOutputDirectoryCommand.Execute(null);

        // 这个手动格宽远离智能检测的 8–32px 搜索范围；智能模式不应读取它。
        viewModel.CheckerCellSize = 2048d;
        viewModel.CheckerColorAHex = "not-a-color";
        viewModel.CheckerColorBHex = "#GGGGGG";

        True(viewModel.IsConfigurationValid,
            "智能模式应忽略即将被检测结果覆盖的无效手动颜色文本");
        True(viewModel.CanRefreshPreview,
            "智能模式下无效手动颜色不得阻断单图预览");
        True(viewModel.CanProcessBatch,
            "智能模式下无效手动颜色和无关手动格宽不得阻断批处理");
        True(viewModel.RefreshPreviewCommand.CanExecute(null),
            "预览命令状态应与智能检测校验一致");
        True(viewModel.ProcessBatchCommand.CanExecute(null),
            "批处理命令状态应与智能检测校验一致");

        viewModel.EnableSmartCheckerDetection = false;
        False(viewModel.IsConfigurationValid,
            "关闭智能检测后必须恢复手动颜色格式校验");
        False(viewModel.CanRefreshPreview,
            "关闭智能检测后无效手动颜色必须阻断预览");
        False(viewModel.CanProcessBatch,
            "关闭智能检测后无效手动颜色必须阻断批处理");

        viewModel.EnableSmartCheckerDetection = true;
        viewModel.CheckerColorAHex = "#C0C0C0";
        viewModel.CheckerColorBHex = "#C0C0C0";
        True(viewModel.CanRefreshPreview && viewModel.CanProcessBatch,
            "智能模式也应忽略相同的手动 A/B 颜色");

        viewModel.EnableSmartCheckerDetection = false;
        False(viewModel.IsConfigurationValid,
            "关闭智能检测后相同 A/B 颜色仍必须被拒绝");
        False(viewModel.RefreshPreviewCommand.CanExecute(null),
            "手动模式下相同 A/B 颜色必须禁用预览命令");
        False(viewModel.ProcessBatchCommand.CanExecute(null),
            "手动模式下相同 A/B 颜色必须禁用批处理命令");
    }

    private static CheckerGridDetectionSettings CreateSettings(
        double minimumCellSize,
        double maximumCellSize,
        double searchRange,
        double minimumConfidence = 0.5d) => new()
    {
        MinimumCellSize = minimumCellSize,
        MaximumCellSize = maximumCellSize,
        SearchRange = searchRange,
        MinimumConfidence = minimumConfidence
    };

    private static CheckerGridDetectionResult CreateDetectedResult(
        string colorAHex,
        string colorBHex,
        double cellSize,
        double offsetX,
        double offsetY,
        int horizontalCells,
        int verticalCells) => new()
    {
        IsDetected = true,
        ColorAHex = colorAHex,
        ColorBHex = colorBHex,
        CellSize = cellSize,
        OffsetX = offsetX,
        OffsetY = offsetY,
        CellSizeMinimum = cellSize - 0.25d,
        CellSizeMaximum = cellSize + 0.25d,
        HorizontalCellCount = horizontalCells,
        VerticalCellCount = verticalCells,
        DetectedCornerCount = 4,
        Confidence = 0.95d,
        FailureReason = string.Empty
    };

    private static void SaveCheckerFixture(
        string path,
        int width,
        int height,
        Rgba32 colorA,
        Rgba32 colorB,
        double cellSize,
        double offsetX,
        double offsetY,
        float blurSigma = 0f,
        bool addDeterministicNoise = false,
        Action<Image<Rgba32>>? configure = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new Image<Rgba32>(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var cellX = (long)Math.Floor(((x + 0.5d) - offsetX) / cellSize);
                var cellY = (long)Math.Floor(((y + 0.5d) - offsetY) / cellSize);
                image[x, y] = ((cellX + cellY) & 1L) == 0L ? colorA : colorB;
            }
        }

        if (blurSigma > 0f)
        {
            image.Mutate(context => context.GaussianBlur(blurSigma));
        }

        if (addDeterministicNoise)
        {
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x++)
                    {
                        var delta = ((x * 17 + y * 31) % 5) - 2;
                        row[x] = AddRgbDelta(row[x], delta);
                    }
                }
            });
        }

        configure?.Invoke(image);
        image.Save(path, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
    }

    private static void PaintCornerOcclusions(Image<Rgba32> image, int size, Rgba32 color)
    {
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                image[x, y] = color;
                image[image.Width - 1 - x, y] = color;
                image[x, image.Height - 1 - y] = color;
                image[image.Width - 1 - x, image.Height - 1 - y] = color;
            }
        }
    }

    private static Rgba32 AddRgbDelta(Rgba32 color, int delta) => new(
        (byte)Math.Clamp(color.R + delta, byte.MinValue, byte.MaxValue),
        (byte)Math.Clamp(color.G + delta, byte.MinValue, byte.MaxValue),
        (byte)Math.Clamp(color.B + delta, byte.MinValue, byte.MaxValue),
        color.A);

    private static void AssertDetectedColors(
        CheckerGridDetectionResult result,
        string expectedAHex,
        string expectedBHex,
        double tolerance)
    {
        var actual = new[] { ParseHex(result.ColorAHex), ParseHex(result.ColorBHex) };
        var expectedA = ParseHex(expectedAHex);
        var expectedB = ParseHex(expectedBHex);
        var direct = ColorDistance(expectedA, actual[0]) <= tolerance &&
                     ColorDistance(expectedB, actual[1]) <= tolerance;
        var swapped = ColorDistance(expectedA, actual[1]) <= tolerance &&
                      ColorDistance(expectedB, actual[0]) <= tolerance;
        True(direct || swapped,
            $"识别出的两种棋盘颜色应接近 {expectedAHex}/{expectedBHex}，实际 {result.ColorAHex}/{result.ColorBHex}");
    }

    private static void AssertCellSizeRangeContains(
        CheckerGridDetectionResult result,
        double expectedCellSize,
        double maximumSpan)
    {
        True(result.CellSizeMinimum <= expectedCellSize && expectedCellSize <= result.CellSizeMaximum,
            $"识别格宽范围应覆盖实际值 {expectedCellSize}，实际 [{result.CellSizeMinimum}, {result.CellSizeMaximum}]");
        True(result.CellSizeMaximum - result.CellSizeMinimum <= maximumSpan,
            $"稳定棋盘的格宽范围不应过宽，实际跨度 {result.CellSizeMaximum - result.CellSizeMinimum}");
    }

    private static void AssertRemovalSettings(
        RemovalCall call,
        string expectedInputPath,
        string colorAHex,
        string colorBHex,
        double cellSize,
        double offsetX,
        double offsetY)
    {
        Equal(Path.GetFullPath(expectedInputPath), call.InputPath, "剔除服务应处理对应的批量输入");
        True(call.Settings.UseFakeTransparencyGrid, "智能识别结果应以假透明棋盘模式执行");
        Equal(colorAHex, call.Settings.CheckerColorAHex, "逐图识别颜色 A");
        Equal(colorBHex, call.Settings.CheckerColorBHex, "逐图识别颜色 B");
        Near(cellSize, call.Settings.CheckerCellSize, 0.001d, "逐图识别格宽");
        Near(offsetX, call.Settings.CheckerOffsetX, 0.001d, "逐图识别 X 相位");
        Near(offsetY, call.Settings.CheckerOffsetY, 0.001d, "逐图识别 Y 相位");
    }

    private static Rgba32 ParseHex(string hex)
    {
        var normalized = BackgroundKeyColor.NormalizeHex(hex);
        return new Rgba32(
            Convert.ToByte(normalized.Substring(1, 2), 16),
            Convert.ToByte(normalized.Substring(3, 2), 16),
            Convert.ToByte(normalized.Substring(5, 2), 16),
            255);
    }

    private static double ColorDistance(Rgba32 left, Rgba32 right)
    {
        var red = left.R - right.R;
        var green = left.G - right.G;
        var blue = left.B - right.B;
        return Math.Sqrt(red * red + green * green + blue * blue);
    }

    private static void NearCyclic(
        double expected,
        double actual,
        double period,
        double tolerance,
        string message)
    {
        var normalizedExpected = PositiveModulo(expected, period);
        var normalizedActual = PositiveModulo(actual, period);
        var distance = Math.Abs(normalizedExpected - normalizedActual);
        distance = Math.Min(distance, period - distance);
        if (distance > tolerance)
        {
            throw new SmokeTestException(
                $"{message}：预期 {normalizedExpected} ± {tolerance}（周期 {period}），实际 {normalizedActual}。");
        }
    }

    private static double PositiveModulo(double value, double divisor)
    {
        var remainder = value % divisor;
        return remainder < 0d ? remainder + divisor : remainder;
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string message)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > timeout)
            {
                throw new SmokeTestException(message);
            }

            Thread.Sleep(20);
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new SmokeTestException(message);
        }
    }

    private static void False(bool condition, string message) => True(!condition, message);

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new SmokeTestException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new SmokeTestException($"{message}：预期 {expected} ± {tolerance}，实际 {actual}。");
        }
    }

    private sealed class RecordingCheckerGridDetectionService(
        IReadOnlyDictionary<string, CheckerGridDetectionResult> results)
        : ICheckerGridDetectionService
    {
        public List<DetectionCall> Calls { get; } = [];

        public CheckerGridDetectionResult Detect(string inputPath, CheckerGridDetectionSettings settings)
        {
            var fullPath = Path.GetFullPath(inputPath);
            Calls.Add(new DetectionCall(fullPath, settings));
            return results.TryGetValue(fullPath, out var result)
                ? result
                : new CheckerGridDetectionResult
                {
                    IsDetected = false,
                    FailureReason = $"测试未配置检测结果：{fullPath}"
                };
        }
    }

    private sealed class BlockingCheckerGridDetectionService(CheckerGridDetectionResult result)
        : ICheckerGridDetectionService, IDisposable
    {
        private readonly ManualResetEventSlim _entered = new(initialState: false);
        private readonly ManualResetEventSlim _release = new(initialState: false);

        public CheckerGridDetectionResult Detect(string inputPath, CheckerGridDetectionSettings settings)
        {
            _entered.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("测试延迟检测器等待释放超时。");
            }

            return result;
        }

        public bool WaitUntilEntered(TimeSpan timeout) => _entered.Wait(timeout);

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _entered.Dispose();
            _release.Dispose();
        }
    }

    private sealed class RecordingBackgroundRemovalService : IBackgroundRemovalService
    {
        public ConcurrentQueue<RemovalCall> Calls { get; } = new();

        public BackgroundRemovalResult RemoveToPng(
            string inputPath,
            string outputPath,
            BackgroundRemovalSettings settings,
            bool overwrite = false)
        {
            var normalizedInput = Path.GetFullPath(inputPath);
            var normalizedOutput = Path.GetFullPath(outputPath);
            Calls.Enqueue(new RemovalCall(normalizedInput, normalizedOutput, settings));
            Directory.CreateDirectory(Path.GetDirectoryName(normalizedOutput)!);
            using (var image = Image.Load<Rgba32>(normalizedInput))
            {
                image.Save(normalizedOutput, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
                return new BackgroundRemovalResult
                {
                    OutputPath = normalizedOutput,
                    KeyColorHex = settings.CheckerColorAHex,
                    Width = image.Width,
                    Height = image.Height,
                    RemovedPixelCount = image.Width * (long)image.Height,
                    TransparentPixelCount = image.Width * (long)image.Height
                };
            }
        }
    }

    private sealed class FixedBatchInputPicker(IReadOnlyList<string> paths)
        : GAssetForge.App.Services.IBackgroundRemovalInputPicker
    {
        public string? SelectInputFile(string? initialDirectory) => paths.FirstOrDefault();

        public IReadOnlyList<string> SelectInputFiles(string? initialDirectory) => paths;
    }

    private sealed class SequenceInputPicker(IReadOnlyList<string> paths)
        : GAssetForge.App.Services.IBackgroundRemovalInputPicker
    {
        private int _nextIndex;

        public string? SelectInputFile(string? initialDirectory)
        {
            if (_nextIndex >= paths.Count)
            {
                return null;
            }

            return paths[_nextIndex++];
        }
    }

    private sealed class FixedBatchOutputPicker(string outputDirectory)
        : GAssetForge.App.Services.IBackgroundRemovalOutputPicker
    {
        public string? SelectOutputFile(string? initialDirectory, string suggestedFileName) =>
            Path.Combine(outputDirectory, suggestedFileName);

        public string? SelectOutputDirectory(string? initialDirectory) => outputDirectory;
    }

    private sealed record DetectionCase(
        string Name,
        int Width,
        int Height,
        double CellSize,
        double OffsetX,
        double OffsetY,
        string ColorAHex,
        string ColorBHex);

    private sealed record DetectionCall(string InputPath, CheckerGridDetectionSettings Settings);

    private sealed record RemovalCall(
        string InputPath,
        string OutputPath,
        BackgroundRemovalSettings Settings);

    private sealed class SmokeTestException(string message) : Exception(message);
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GAssetForge.App.Controls;
using GAssetForge.App.Services;
using GAssetForge.App.ViewModels;
using GAssetForge.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace GAssetForge.SmokeTests;

internal static class BackgroundRemovalSmokeTests
{
    private static readonly ImageSharpBackgroundRemovalService Service = new();

    public static void Run()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "background-removal",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        try
        {
            PresetsRemoveTheirKeyColorsAndKeepSourceDimensions(testRoot);
            ExactCheckerboardBecomesTransparent(testRoot);
            GridAlignmentPreservesOppositeCheckerColorSubject(testRoot);
            UnalignedCheckerModeRemovesBothColors(testRoot);
            CheckerOffsetsUseMathematicalFloorDivision(testRoot);
            CheckerDespillUsesPerPixelExpectedColor(testRoot);
            CheckerModeNeverRevivesSourceAlpha(testRoot);
            InvalidCheckerSettingsAreRejected(testRoot);
            DefaultModeRemovesEnclosedHollowBackground(testRoot);
            CheckerboardHollowBackgroundRespectsRemovalScope(testRoot);
            EdgeConnectedModePreservesEnclosedSubjectColor(testRoot);
            GlobalModeRemovesEnclosedMatchingColor(testRoot);
            ViewModelDefaultsToRemovingEnclosedBackground();
            FeatherOnlySoftensTowardTheSubjectInterior(testRoot);
            FeatherDoesNotReviveSourceTransparencyOrRaiseExistingAlpha(testRoot);
            ExistingAlphaIsPreserved(testRoot);
            InputSelectionCapturesOriginalPixelDimensions(testRoot);
            CheckerGridOverlayUsesOriginalPixelDimensions();
            InvalidImagesAreReportedByTheViewModel(testRoot);
            OutputIsAtomicallyReplacedWithoutTemporaryResidue(testRoot);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void ExactCheckerboardBecomesTransparent(string testRoot)
    {
        const string colorAHex = "#FFFFFF";
        const string colorBHex = "#CCCCCC";
        var inputPath = Path.Combine(testRoot, "checker-exact-input.png");
        var outputPath = Path.Combine(testRoot, "checker-exact-output.png");
        var subject = new Rgba32(28, 122, 214, 255);
        SaveCheckerFixture(
            inputPath,
            8,
            6,
            ParseHex(colorAHex),
            ParseHex(colorBHex),
            cellSize: 2,
            offsetX: 0,
            offsetY: 0,
            image => image[3, 3] = subject);

        var result = Service.RemoveToPng(
            inputPath,
            outputPath,
            CreateExactCheckerSettings(
                colorAHex,
                colorBHex,
                enableGridAlignment: true,
                cellSize: 2));

        using var output = Image.Load<Rgba32>(outputPath);
        Equal((byte)0, output[0, 0].A, "精确棋盘格 A 色应透明");
        Equal((byte)0, output[2, 0].A, "精确棋盘格 B 色应透明");
        Equal(subject, output[3, 3], "棋盘格中的非底色主体应保持不透明原色");
        Equal(47L, result.RemovedPixelCount, "除主体外的精确棋盘格像素都应被剔除");
    }

    private static void GridAlignmentPreservesOppositeCheckerColorSubject(string testRoot)
    {
        const string colorAHex = "#FFFFFF";
        const string colorBHex = "#CCCCCC";
        var colorA = ParseHex(colorAHex);
        var colorB = ParseHex(colorBHex);
        var inputPath = Path.Combine(testRoot, "checker-phase-protection-input.png");
        var outputPath = Path.Combine(testRoot, "checker-phase-protection-output.png");
        SaveCheckerFixture(
            inputPath,
            6,
            4,
            colorA,
            colorB,
            cellSize: 2,
            offsetX: 0,
            offsetY: 0,
            image => image[0, 0] = colorB);

        Service.RemoveToPng(
            inputPath,
            outputPath,
            CreateExactCheckerSettings(
                colorAHex,
                colorBHex,
                enableGridAlignment: true,
                cellSize: 2) with
            {
                TransparentThreshold = 30d,
                OpaqueThreshold = 120d
            });

        using var output = Image.Load<Rgba32>(outputPath);
        Equal((byte)255, output[0, 0].A,
            "A 相位格内出现 B 色主体时，即使阈值较宽也必须保持完全不透明");
        Equal(colorB, output[0, 0], "错相位主体同色像素不得被 despill 或改色");
        Equal((byte)0, output[2, 0].A, "正确 B 相位格内的 B 色仍应被剔除");
    }

    private static void UnalignedCheckerModeRemovesBothColors(string testRoot)
    {
        const string colorAHex = "#FFFFFF";
        const string colorBHex = "#CCCCCC";
        var colorA = ParseHex(colorAHex);
        var colorB = ParseHex(colorBHex);
        var subject = new Rgba32(200, 45, 110, 255);
        var inputPath = Path.Combine(testRoot, "checker-unaligned-input.png");
        var outputPath = Path.Combine(testRoot, "checker-unaligned-output.png");
        SaveFixture(
            inputPath,
            5,
            3,
            colorA,
            image =>
            {
                image[1, 0] = colorB;
                image[2, 0] = colorB;
                image[4, 1] = colorB;
                image[2, 2] = subject;
            });

        Service.RemoveToPng(
            inputPath,
            outputPath,
            CreateExactCheckerSettings(
                colorAHex,
                colorBHex,
                enableGridAlignment: false,
                cellSize: 16));

        using var output = Image.Load<Rgba32>(outputPath);
        Equal((byte)0, output[0, 0].A, "关闭网格对齐后 A 色应按最近色剔除");
        Equal((byte)0, output[4, 1].A, "关闭网格对齐后任意位置的 B 色应按最近色剔除");
        Equal(subject, output[2, 2], "关闭网格对齐仍应保留非棋盘色主体");
    }

    private static void CheckerOffsetsUseMathematicalFloorDivision(string testRoot)
    {
        const string colorAHex = "#FFFFFF";
        const string colorBHex = "#CCCCCC";
        const int cellSize = 3;
        const int offsetX = 2;
        const int offsetY = 2;
        var inputPath = Path.Combine(testRoot, "checker-floor-division-input.png");
        var outputPath = Path.Combine(testRoot, "checker-floor-division-output.png");
        SaveCheckerFixture(
            inputPath,
            8,
            7,
            ParseHex(colorAHex),
            ParseHex(colorBHex),
            cellSize,
            offsetX,
            offsetY,
            _ => { });

        Service.RemoveToPng(
            inputPath,
            outputPath,
            CreateExactCheckerSettings(
                colorAHex,
                colorBHex,
                enableGridAlignment: true,
                cellSize,
                offsetX,
                offsetY));

        using var output = Image.Load<Rgba32>(outputPath);
        for (var y = 0; y < output.Height; y++)
        {
            for (var x = 0; x < output.Width; x++)
            {
                Equal((byte)0, output[x, y].A,
                    $"正偏移导致 x/y 小于 offset 时仍必须按数学 FloorDiv 对齐 ({x},{y})");
            }
        }
    }

    private static void CheckerDespillUsesPerPixelExpectedColor(string testRoot)
    {
        const string colorAHex = "#FF0000";
        const string colorBHex = "#0000FF";
        var colorA = ParseHex(colorAHex);
        var colorB = ParseHex(colorBHex);
        var inputPath = Path.Combine(testRoot, "checker-per-pixel-despill-input.png");
        var outputPath = Path.Combine(testRoot, "checker-per-pixel-despill-output.png");
        SaveCheckerFixture(
            inputPath,
            4,
            2,
            colorA,
            colorB,
            cellSize: 2,
            offsetX: 0,
            offsetY: 0,
            image =>
            {
                image[1, 0] = new Rgba32(128, 128, 0, 255);
                image[2, 0] = new Rgba32(0, 128, 128, 255);
            });

        Service.RemoveToPng(
            inputPath,
            outputPath,
            CreateExactCheckerSettings(
                colorAHex,
                colorBHex,
                enableGridAlignment: true,
                cellSize: 2) with
            {
                OpaqueThreshold = 360.624d,
                Despill = true
            });

        using var output = Image.Load<Rgba32>(outputPath);
        AssertApproximatelyGreenHalfAlpha(output[1, 0], "A 色格的半透明边缘");
        AssertApproximatelyGreenHalfAlpha(output[2, 0], "B 色格的半透明边缘");
    }

    private static void CheckerModeNeverRevivesSourceAlpha(string testRoot)
    {
        const string colorAHex = "#FFFFFF";
        const string colorBHex = "#CCCCCC";
        var inputPath = Path.Combine(testRoot, "checker-source-alpha-input.png");
        var outputPath = Path.Combine(testRoot, "checker-source-alpha-output.png");
        SaveCheckerFixture(
            inputPath,
            5,
            1,
            ParseHex(colorAHex),
            ParseHex(colorBHex),
            cellSize: 2,
            offsetX: 0,
            offsetY: 0,
            image =>
            {
                image[1, 0] = new Rgba32(10, 20, 30, 0);
                image[3, 0] = new Rgba32(210, 120, 35, 96);
            });

        Service.RemoveToPng(
            inputPath,
            outputPath,
            CreateExactCheckerSettings(
                colorAHex,
                colorBHex,
                enableGridAlignment: true,
                cellSize: 2) with
            {
                EdgeFeather = 1
            });

        using var output = Image.Load<Rgba32>(outputPath);
        Equal((byte)0, output[1, 0].A, "棋盘格羽化不得复活输入中已有的全透明像素");
        True(output[3, 0].A <= 96, "棋盘格羽化不得提高输入中已有的半透明 Alpha");
    }

    private static void InvalidCheckerSettingsAreRejected(string testRoot)
    {
        var inputPath = Path.Combine(testRoot, "checker-invalid-settings-input.png");
        var outputPath = Path.Combine(testRoot, "checker-invalid-settings-output.png");
        SaveFixture(inputPath, 2, 2, new Rgba32(255, 255, 255, 255), _ => { });
        var valid = CreateExactCheckerSettings(
            "#FFFFFF",
            "#CCCCCC",
            enableGridAlignment: true,
            cellSize: 16);

        var cases = new[]
        {
            valid with { CheckerColorAHex = "#GGGGGG" },
            valid with { CheckerColorBHex = "not-a-color" },
            valid with { CheckerColorBHex = "#FFFFFF" },
            valid with { CheckerCellSize = 1 },
            valid with { CheckerCellSize = 513 },
            valid with { CheckerBoundaryTolerance = -1 },
            valid with { CheckerBoundaryTolerance = 9 }
        };
        foreach (var settings in cases)
        {
            Throws<ArgumentException>(
                () => Service.RemoveToPng(inputPath, outputPath, settings),
                $"非法假透明网格设置必须被拒绝：{settings}");
        }
    }

    private static void PresetsRemoveTheirKeyColorsAndKeepSourceDimensions(string testRoot)
    {
        var cases = new[]
        {
            new PresetCase(BackgroundKeyPreset.Magenta, null, "#FF00FF"),
            new PresetCase(BackgroundKeyPreset.Green, null, "#00FF00"),
            new PresetCase(BackgroundKeyPreset.Blue, null, "#0000FF"),
            new PresetCase(BackgroundKeyPreset.Black, null, "#000000"),
            new PresetCase(BackgroundKeyPreset.White, null, "#FFFFFF"),
            new PresetCase(BackgroundKeyPreset.Custom, "  #1a2b3c  ", "#1A2B3C")
        };

        foreach (var testCase in cases)
        {
            var resolvedHex = BackgroundKeyColor.ResolveHex(testCase.Preset, testCase.CustomColorHex);
            Equal(testCase.ExpectedHex, resolvedHex, $"{testCase.Preset} 底色解析");

            var key = ParseHex(resolvedHex);
            var subject = new Rgba32(
                (byte)(255 - key.R),
                (byte)(255 - key.G),
                (byte)(255 - key.B),
                255);
            var inputPath = Path.Combine(testRoot, $"preset-{testCase.Preset}.png");
            var outputPath = Path.Combine(testRoot, $"preset-{testCase.Preset}-transparent.png");
            SaveFixture(inputPath, 5, 3, key, image => image[2, 1] = subject);

            var result = Service.RemoveToPng(
                inputPath,
                outputPath,
                CreateExactKeySettings(resolvedHex, edgeConnectedOnly: false));

            Equal(5, result.Width, $"{testCase.Preset} 结果宽度");
            Equal(3, result.Height, $"{testCase.Preset} 结果高度");
            Equal(resolvedHex, result.KeyColorHex, $"{testCase.Preset} 结果底色");
            using var output = Image.Load<Rgba32>(outputPath);
            Equal(5, output.Width, $"{testCase.Preset} PNG 宽度");
            Equal(3, output.Height, $"{testCase.Preset} PNG 高度");
            Equal((byte)0, output[0, 0].A, $"{testCase.Preset} 背景应透明");
            Equal((byte)255, output[2, 1].A, $"{testCase.Preset} 主体应保持不透明");
        }
    }

    private static void EdgeConnectedModePreservesEnclosedSubjectColor(string testRoot)
    {
        const string keyHex = "#FF00FF";
        var inputPath = Path.Combine(testRoot, "edge-connected-input.png");
        var outputPath = Path.Combine(testRoot, "edge-connected-output.png");
        SaveEnclosedKeyFixture(inputPath, ParseHex(keyHex));

        Service.RemoveToPng(
            inputPath,
            outputPath,
            CreateExactKeySettings(keyHex, edgeConnectedOnly: true));

        using var output = Image.Load<Rgba32>(outputPath);
        Equal((byte)0, output[0, 0].A, "边缘连通模式应删除与画布边缘连通的底色");
        Equal((byte)255, output[3, 3].A, "边缘连通模式应保留被主体包围的同色像素");
        Equal((byte)255, output[3, 2].A, "边缘连通模式不得删除包围中心色的主体像素");
    }

    private static void DefaultModeRemovesEnclosedHollowBackground(string testRoot)
    {
        const string keyHex = "#FF00FF";
        var inputPath = Path.Combine(testRoot, "default-hollow-input.png");
        var outputPath = Path.Combine(testRoot, "default-hollow-output.png");
        SaveEnclosedKeyFixture(inputPath, ParseHex(keyHex));

        Service.RemoveToPng(
            inputPath,
            outputPath,
            new BackgroundRemovalSettings
            {
                KeyColorHex = keyHex,
                TransparentThreshold = 0d,
                OpaqueThreshold = 1d,
                EdgeContract = 0,
                EdgeFeather = 0,
                Despill = false
            });

        using var output = Image.Load<Rgba32>(outputPath);
        Equal((byte)0, output[0, 0].A, "默认模式应删除物体外侧底色");
        Equal((byte)0, output[3, 3].A, "默认模式应识别并删除被镂空轮廓包围的底色");
        Equal((byte)255, output[3, 2].A, "默认模式仍应保留构成镂空轮廓的主体像素");
    }

    private static void GlobalModeRemovesEnclosedMatchingColor(string testRoot)
    {
        const string keyHex = "#FF00FF";
        var inputPath = Path.Combine(testRoot, "global-input.png");
        var outputPath = Path.Combine(testRoot, "global-output.png");
        SaveEnclosedKeyFixture(inputPath, ParseHex(keyHex));

        Service.RemoveToPng(
            inputPath,
            outputPath,
            CreateExactKeySettings(keyHex, edgeConnectedOnly: false));

        using var output = Image.Load<Rgba32>(outputPath);
        Equal((byte)0, output[0, 0].A, "全局模式应删除外部底色");
        Equal((byte)0, output[3, 3].A, "全局模式应删除主体内部与底色相同的像素");
        Equal((byte)255, output[3, 2].A, "全局模式仍应保留非底色主体像素");
    }

    private static void CheckerboardHollowBackgroundRespectsRemovalScope(string testRoot)
    {
        const string colorAHex = "#FFFFFF";
        const string colorBHex = "#CCCCCC";
        var inputPath = Path.Combine(testRoot, "checker-hollow-input.png");
        var globalOutputPath = Path.Combine(testRoot, "checker-hollow-global-output.png");
        var edgeOutputPath = Path.Combine(testRoot, "checker-hollow-edge-output.png");
        var subject = new Rgba32(30, 120, 220, 255);
        SaveCheckerFixture(
            inputPath,
            10,
            10,
            ParseHex(colorAHex),
            ParseHex(colorBHex),
            cellSize: 2,
            offsetX: 0,
            offsetY: 0,
            image =>
            {
                for (var coordinate = 2; coordinate <= 7; coordinate++)
                {
                    image[coordinate, 2] = subject;
                    image[coordinate, 7] = subject;
                    image[2, coordinate] = subject;
                    image[7, coordinate] = subject;
                }
            });

        Service.RemoveToPng(
            inputPath,
            globalOutputPath,
            CreateExactCheckerSettings(
                colorAHex,
                colorBHex,
                enableGridAlignment: true,
                cellSize: 2,
                edgeConnectedOnly: false));
        Service.RemoveToPng(
            inputPath,
            edgeOutputPath,
            CreateExactCheckerSettings(
                colorAHex,
                colorBHex,
                enableGridAlignment: true,
                cellSize: 2,
                edgeConnectedOnly: true));

        using var globalOutput = Image.Load<Rgba32>(globalOutputPath);
        Equal((byte)0, globalOutput[0, 0].A, "棋盘格全局模式应删除物体外侧底色");
        Equal((byte)0, globalOutput[4, 4].A, "棋盘格全局模式应删除镂空内部底色");
        Equal((byte)255, globalOutput[2, 4].A, "棋盘格全局模式应保留主体轮廓");

        using var edgeOutput = Image.Load<Rgba32>(edgeOutputPath);
        Equal((byte)0, edgeOutput[0, 0].A, "棋盘格边缘连通模式应删除物体外侧底色");
        Equal((byte)255, edgeOutput[4, 4].A, "棋盘格边缘连通模式应保留被主体包围的底色");
        Equal((byte)255, edgeOutput[2, 4].A, "棋盘格边缘连通模式应保留主体轮廓");
    }

    private static void ViewModelDefaultsToRemovingEnclosedBackground()
    {
        var viewModel = new BackgroundRemovalViewModel(
            Service,
            new FixedInputPicker(string.Empty),
            new FixedOutputPicker(string.Empty));
        var settingsChangedCount = 0;
        viewModel.SettingsChanged += (_, _) => settingsChangedCount++;

        True(viewModel.RemoveEnclosedBackground, "背景剔除工具应默认开启镂空内部底色识别");
        False(viewModel.EdgeConnectedOnly, "默认镂空识别应映射为全图色键模式");
        False(viewModel.CaptureSettings().EdgeConnectedOnly, "默认工具快照应冻结全图色键模式");
        False(viewModel.CreateGenerationSpec().EdgeConnectedOnly, "新生成合同应默认剔除镂空内部底色");

        viewModel.RemoveEnclosedBackground = false;

        False(viewModel.RemoveEnclosedBackground, "用户应能关闭镂空内部底色识别");
        True(viewModel.EdgeConnectedOnly, "关闭镂空识别后应恢复旧的边缘连通模式");
        True(viewModel.CaptureSettings().EdgeConnectedOnly, "关闭后的工具快照应保留边缘连通模式");
        True(viewModel.CreateGenerationSpec().EdgeConnectedOnly, "关闭后的生成合同应保留边缘连通模式");
        Equal(1, settingsChangedCount, "切换镂空识别应只发出一次设置变化通知");
    }

    private static void ExistingAlphaIsPreserved(string testRoot)
    {
        const string keyHex = "#00FF00";
        var key = ParseHex(keyHex);
        var inputPath = Path.Combine(testRoot, "existing-alpha-input.png");
        var outputPath = Path.Combine(testRoot, "existing-alpha-output.png");
        SaveFixture(
            inputPath,
            4,
            3,
            key,
            image =>
            {
                image[1, 1] = new Rgba32(180, 70, 20, 96);
                image[2, 1] = new Rgba32(10, 20, 30, 0);
            });

        var result = Service.RemoveToPng(
            inputPath,
            outputPath,
            CreateExactKeySettings(keyHex, edgeConnectedOnly: false));

        using var output = Image.Load<Rgba32>(outputPath);
        Equal((byte)96, output[1, 1].A, "非底色主体已有的半透明 Alpha 应保持不变");
        Equal((byte)0, output[2, 1].A, "输入中原有的全透明像素应保持透明");
        Equal(1L, result.PartialPixelCount, "结果应准确统计保留的半透明像素");
    }

    private static void FeatherOnlySoftensTowardTheSubjectInterior(string testRoot)
    {
        const string keyHex = "#FF00FF";
        var inputPath = Path.Combine(testRoot, "feather-inward-input.png");
        var outputPath = Path.Combine(testRoot, "feather-inward-output.png");
        SaveFixture(
            inputPath,
            3,
            1,
            ParseHex(keyHex),
            image => image[1, 0] = new Rgba32(30, 140, 220, 255));

        Service.RemoveToPng(
            inputPath,
            outputPath,
            CreateExactKeySettings(keyHex, edgeConnectedOnly: false) with { EdgeFeather = 1 });

        using var output = Image.Load<Rgba32>(outputPath);
        Equal((byte)0, output[0, 0].A, "羽化不得把 0,255,0 matte 的左侧透明像素复活");
        Equal((byte)85, output[1, 0].A, "羽化应向主体内部软化 0,255,0 matte 的边缘");
        Equal((byte)0, output[2, 0].A, "羽化不得把 0,255,0 matte 的右侧透明像素复活");
    }

    private static void FeatherDoesNotReviveSourceTransparencyOrRaiseExistingAlpha(string testRoot)
    {
        const string keyHex = "#FF00FF";
        var inputPath = Path.Combine(testRoot, "feather-source-alpha-input.png");
        var outputPath = Path.Combine(testRoot, "feather-source-alpha-output.png");
        var subject = new Rgba32(30, 140, 220, 255);
        SaveFixture(
            inputPath,
            5,
            1,
            subject,
            image =>
            {
                image[1, 0] = new Rgba32(10, 20, 30, 0);
                image[3, 0] = new Rgba32(210, 120, 35, 96);
            });

        Service.RemoveToPng(
            inputPath,
            outputPath,
            CreateExactKeySettings(keyHex, edgeConnectedOnly: false) with { EdgeFeather = 1 });

        using var output = Image.Load<Rgba32>(outputPath);
        Equal((byte)0, output[1, 0].A, "羽化不得复活输入中原有的全透明像素");
        Equal((byte)96, output[3, 0].A, "羽化不得把输入中已有的半透明 Alpha 提高");
        var matteBeforeFeather = new byte[] { 255, 0, 255, 96, 255 };
        for (var x = 0; x < matteBeforeFeather.Length; x++)
        {
            True(
                output[x, 0].A <= matteBeforeFeather[x],
                $"羽化后像素 {x} 的 Alpha 不得高于进入羽化前的 matte");
        }
    }

    private static void InvalidImagesAreReportedByTheViewModel(string testRoot)
    {
        var cases = new[]
        {
            new InvalidImageCase("unknown-image.png", "这不是图片"u8.ToArray()),
            new InvalidImageCase("truncated-image.png", [137, 80, 78, 71, 13, 10, 26, 10])
        };

        foreach (var testCase in cases)
        {
            var inputPath = Path.Combine(testRoot, testCase.FileName);
            var outputPath = Path.Combine(testRoot, $"{Path.GetFileNameWithoutExtension(testCase.FileName)}-output.png");
            File.WriteAllBytes(inputPath, testCase.Bytes);
            var logs = new RecordingLogSink();
            var viewModel = new BackgroundRemovalViewModel(
                Service,
                new FixedInputPicker(inputPath),
                new FixedOutputPicker(outputPath),
                workbenchLogSink: logs);

            viewModel.ChooseInputCommand.Execute(null);
            Equal(0, viewModel.SourcePixelWidth,
                $"{testCase.FileName} 无法识别时应回退到预览解码宽度");
            Equal(0, viewModel.SourcePixelHeight,
                $"{testCase.FileName} 无法识别时应回退到预览解码高度");
            viewModel.ChooseOutputCommand.Execute(null);
            True(viewModel.CanProcess, $"{testCase.FileName} 应在扩展名检查后进入真实解码流程");
            viewModel.ProcessCommand.Execute(null);
            WaitUntil(
                () => !viewModel.IsBusy &&
                      viewModel.StatusText.StartsWith("背景剔除失败：", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5),
                $"{testCase.FileName} 解码异常应由 ViewModel 转换为失败状态");

            Equal("处理失败，输入文件保持不变。", viewModel.ResultSummary,
                $"{testCase.FileName} 应显示稳定的失败摘要");
            False(File.Exists(outputPath), $"{testCase.FileName} 解码失败不得创建输出文件");
            True(
                logs.Entries.Any(entry =>
                    entry.Level == WorkbenchLogLevel.Error &&
                    entry.Exception is ImageFormatException),
                $"{testCase.FileName} 的 ImageSharp 解码异常应写入错误日志");
        }
    }

    private static void InputSelectionCapturesOriginalPixelDimensions(string testRoot)
    {
        var inputPath = Path.Combine(testRoot, "source-dimensions-input.png");
        SaveFixture(
            inputPath,
            1600,
            800,
            new Rgba32(255, 255, 255, 255),
            _ => { });
        var viewModel = new BackgroundRemovalViewModel(
            Service,
            new FixedInputPicker(inputPath),
            new FixedOutputPicker(Path.Combine(testRoot, "source-dimensions-output.png")));

        viewModel.ChooseInputCommand.Execute(null);

        Equal(1600, viewModel.SourcePixelWidth, "输入选择应记录源图真实像素宽度");
        Equal(800, viewModel.SourcePixelHeight, "输入选择应记录源图真实像素高度");
    }

    private static void CheckerGridOverlayUsesOriginalPixelDimensions()
    {
        var preview = new WriteableBitmap(
            100,
            50,
            96d,
            96d,
            PixelFormats.Bgra32,
            null);
        var overlay = new CheckerGridOverlay
        {
            Width = 200d,
            Height = 100d,
            ImageSource = preview,
            ShowGrid = true,
            CellSize = 100,
            SourcePixelWidth = 1000,
            SourcePixelHeight = 500
        };
        overlay.Measure(new System.Windows.Size(200d, 100d));
        overlay.Arrange(new Rect(0d, 0d, 200d, 100d));
        overlay.UpdateLayout();

        var rendered = new RenderTargetBitmap(200, 100, 96d, 96d, PixelFormats.Pbgra32);
        rendered.Render(overlay);
        var pixels = new byte[rendered.PixelWidth * rendered.PixelHeight * 4];
        rendered.CopyPixels(pixels, rendered.PixelWidth * 4, 0);

        var guideColumnHasInk = false;
        for (var y = 0; y < rendered.PixelHeight; y++)
        {
            for (var x = 19; x <= 21; x++)
            {
                if (pixels[(y * rendered.PixelWidth + x) * 4 + 3] == 0)
                {
                    continue;
                }

                guideColumnHasInk = true;
                break;
            }

            if (guideColumnHasInk)
            {
                break;
            }
        }

        True(guideColumnHasInk,
            "1000px 源图的 100px 网格在 200px 预览中应落在 x=20，而不是按 100px 缩略图尺寸绘制");
    }

    private static void OutputIsAtomicallyReplacedWithoutTemporaryResidue(string testRoot)
    {
        const string keyHex = "#0000FF";
        var atomicRoot = Path.Combine(testRoot, "atomic-output");
        Directory.CreateDirectory(atomicRoot);
        var inputPath = Path.Combine(atomicRoot, "input.png");
        var outputPath = Path.Combine(atomicRoot, "final.png");
        SaveFixture(
            inputPath,
            3,
            2,
            ParseHex(keyHex),
            image => image[1, 1] = new Rgba32(255, 120, 0, 255));
        File.WriteAllText(outputPath, "existing-output-must-not-be-partially-overwritten");

        Throws<IOException>(
            () => Service.RemoveToPng(
                inputPath,
                outputPath,
                CreateExactKeySettings(keyHex, edgeConnectedOnly: false)),
            "未授权覆盖时应保护既有输出");
        Equal(
            "existing-output-must-not-be-partially-overwritten",
            File.ReadAllText(outputPath),
            "拒绝覆盖后既有输出内容必须保持完整");

        var result = Service.RemoveToPng(
            inputPath,
            outputPath,
            CreateExactKeySettings(keyHex, edgeConnectedOnly: false),
            overwrite: true);

        Equal(Path.GetFullPath(outputPath), result.OutputPath, "原子输出应返回最终绝对路径");
        using var output = Image.Load<Rgba32>(outputPath);
        Equal((byte)0, output[0, 0].A, "原子替换后的最终文件应是完整透明 PNG");
        Equal((byte)255, output[1, 1].A, "原子替换后的最终文件应保留主体");
        False(
            Directory.EnumerateFiles(atomicRoot, $".{Path.GetFileName(outputPath)}.*.tmp").Any(),
            "成功输出后不得残留临时文件");
    }

    private static BackgroundRemovalSettings CreateExactKeySettings(
        string keyColorHex,
        bool edgeConnectedOnly) => new()
    {
        KeyColorHex = keyColorHex,
        TransparentThreshold = 0d,
        OpaqueThreshold = 1d,
        EdgeContract = 0,
        EdgeFeather = 0,
        Despill = false,
        EdgeConnectedOnly = edgeConnectedOnly
    };

    private static BackgroundRemovalSettings CreateExactCheckerSettings(
        string colorAHex,
        string colorBHex,
        bool enableGridAlignment,
        int cellSize,
        int offsetX = 0,
        int offsetY = 0,
        bool edgeConnectedOnly = false) => new()
    {
        UseFakeTransparencyGrid = true,
        CheckerColorAHex = colorAHex,
        CheckerColorBHex = colorBHex,
        EnableCheckerGridAlignment = enableGridAlignment,
        CheckerCellSize = cellSize,
        CheckerOffsetX = offsetX,
        CheckerOffsetY = offsetY,
        CheckerBoundaryTolerance = 0,
        TransparentThreshold = 0d,
        OpaqueThreshold = 1d,
        EdgeContract = 0,
        EdgeFeather = 0,
        Despill = false,
        EdgeConnectedOnly = edgeConnectedOnly
    };

    private static void SaveEnclosedKeyFixture(string path, Rgba32 key)
    {
        SaveFixture(
            path,
            7,
            7,
            key,
            image =>
            {
                var subject = new Rgba32(210, 120, 35, 255);
                for (var y = 2; y <= 4; y++)
                {
                    for (var x = 2; x <= 4; x++)
                    {
                        image[x, y] = subject;
                    }
                }

                image[3, 3] = key;
            });
    }

    private static void SaveCheckerFixture(
        string path,
        int width,
        int height,
        Rgba32 colorA,
        Rgba32 colorB,
        int cellSize,
        int offsetX,
        int offsetY,
        Action<Image<Rgba32>> configure)
    {
        using var image = new Image<Rgba32>(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var cellX = (int)Math.Floor((x - offsetX) / (double)cellSize);
                var cellY = (int)Math.Floor((y - offsetY) / (double)cellSize);
                image[x, y] = PositiveModulo(cellX + cellY, 2) == 0 ? colorA : colorB;
            }
        }

        configure(image);
        image.Save(path, new PngEncoder
        {
            ColorType = PngColorType.RgbWithAlpha
        });
    }

    private static void AssertApproximatelyGreenHalfAlpha(Rgba32 pixel, string message)
    {
        True(pixel.A is >= 126 and <= 129, $"{message} Alpha 应约为 50%，实际 {pixel.A}");
        True(pixel.R <= 2, $"{message} 应按本格底色去除红色污染，实际 R={pixel.R}");
        True(pixel.G >= 253, $"{message} 应恢复绿色主体，实际 G={pixel.G}");
        True(pixel.B <= 2, $"{message} 应按本格底色去除蓝色污染，实际 B={pixel.B}");
    }

    private static int PositiveModulo(int value, int divisor)
    {
        var remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }

    private static void SaveFixture(
        string path,
        int width,
        int height,
        Rgba32 background,
        Action<Image<Rgba32>> configure)
    {
        using var image = new Image<Rgba32>(width, height, background);
        configure(image);
        image.Save(path, new PngEncoder
        {
            ColorType = PngColorType.RgbWithAlpha
        });
    }

    private static Rgba32 ParseHex(string hex) => new(
        Convert.ToByte(hex.Substring(1, 2), 16),
        Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16),
        255);

    private static void Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void False(bool condition, string message)
    {
        if (condition)
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

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string message)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }

            System.Threading.Thread.Sleep(10);
        }

        if (!condition())
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }

    private sealed record PresetCase(
        BackgroundKeyPreset Preset,
        string? CustomColorHex,
        string ExpectedHex);

    private sealed record InvalidImageCase(string FileName, byte[] Bytes);

    private sealed class FixedInputPicker(string inputPath) : IBackgroundRemovalInputPicker
    {
        public string? SelectInputFile(string? initialDirectory) => inputPath;
    }

    private sealed class FixedOutputPicker(string outputPath) : IBackgroundRemovalOutputPicker
    {
        public string? SelectOutputFile(string? initialDirectory, string suggestedFileName) => outputPath;
    }

    private sealed class RecordingLogSink : IWorkbenchLogSink
    {
        private readonly ConcurrentQueue<RecordedLogEntry> _entries = new();

        public IReadOnlyCollection<RecordedLogEntry> Entries => _entries.ToArray();

        public void Write(
            WorkbenchLogLevel level,
            string source,
            string message,
            DateTimeOffset? timestamp = null,
            Exception? exception = null) =>
            _entries.Enqueue(new RecordedLogEntry(level, source, message, exception));
    }

    private sealed record RecordedLogEntry(
        WorkbenchLogLevel Level,
        string Source,
        string Message,
        Exception? Exception);
}

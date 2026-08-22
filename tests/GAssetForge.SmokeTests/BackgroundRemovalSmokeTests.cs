using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            EdgeConnectedModePreservesEnclosedSubjectColor(testRoot);
            GlobalModeRemovesEnclosedMatchingColor(testRoot);
            FeatherOnlySoftensTowardTheSubjectInterior(testRoot);
            FeatherDoesNotReviveSourceTransparencyOrRaiseExistingAlpha(testRoot);
            ExistingAlphaIsPreserved(testRoot);
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

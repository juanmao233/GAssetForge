using GAssetForge.App.Services;
using GAssetForge.App.ViewModels;
using GAssetForge.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace GAssetForge.SmokeTests;

internal static class ImageProcessingUiSmokeTests
{
    private static readonly byte[] MinimalPng = CreateMinimalPng();

    public static void Run(string temporaryRoot)
    {
        ColorConversionAndPickerStateStaySynchronized();
        ToolColorCommandsCommitOnlyConfirmedPickerResults();
        PreviewCachePublishesTheExactRenderedBytes(temporaryRoot);
        PreviewCacheProtectsPinnedResultsFromCleanup(temporaryRoot);
        ImageToolsRenderToCacheBeforePublishing(temporaryRoot);
    }

    private static void ColorConversionAndPickerStateStaySynchronized()
    {
        AssertHsv(new RgbColor(255, 0, 0), 0d, 1d, 1d, "纯红 HSV");
        AssertHsv(new RgbColor(0, 255, 0), 120d, 1d, 1d, "纯绿 HSV");
        AssertHsv(new RgbColor(0, 0, 255), 240d, 1d, 1d, "纯蓝 HSV");

        var representativeColors = new[]
        {
            new RgbColor(0, 0, 0),
            new RgbColor(255, 255, 255),
            new RgbColor(74, 144, 226),
            new RgbColor(17, 203, 91),
            new RgbColor(241, 79, 132)
        };
        foreach (var original in representativeColors)
        {
            var roundTrip = ColorConversion.ToRgb(ColorConversion.ToHsv(original, 197d));
            Equal(original, roundTrip, $"RGB/HSV 往返 {original.Hex}");
        }

        True(RgbColor.TryParseHex("4a90e2", out var parsed), "无井号 Hex 应可解析");
        Equal("#4A90E2", parsed.Hex, "Hex 应规范化为大写 #RRGGBB");
        False(RgbColor.TryParseHex("#XYZ123", out _), "非法 Hex 应被拒绝");

        var gray = ColorConversion.ToHsv(new RgbColor(128, 128, 128), 217d);
        Near(217d, gray.Hue, 0.000001d, "灰色应保留最近有效 Hue");

        var viewModel = new ColorPickerViewModel("#4A90E2");
        viewModel.Hue = 135d;
        var hueBeforeGray = viewModel.Hue;
        viewModel.Saturation = 0d;
        Near(hueBeforeGray, viewModel.Hue, 0.000001d, "饱和度归零时不应跳回红色");

        var selectedBeforeInvalidInput = viewModel.SelectedHex;
        viewModel.HexInput = "#12";
        True(viewModel.HasHexError, "编辑中的非法 Hex 应显示校验状态");
        Equal(selectedBeforeInvalidInput, viewModel.SelectedHex, "非法 Hex 不得污染当前颜色");
        viewModel.HexInput = "#112233";
        False(viewModel.HasHexError, "合法 Hex 应清除校验状态");
        Equal("#112233", viewModel.SelectedHex, "合法 Hex 应同步 RGB/HSV 颜色状态");
        viewModel.ResetToOriginalCommand.Execute(null);
        Equal("#4A90E2", viewModel.SelectedHex, "恢复原色应回到弹窗打开时颜色");
    }

    private static void PreviewCachePublishesTheExactRenderedBytes(string temporaryRoot)
    {
        var root = Path.Combine(temporaryRoot, "image-processing-preview-cache");
        var cache = new ImageProcessingPreviewCache(
            Path.Combine(root, "cache"),
            maximumFileCount: 8,
            maximumTotalBytes: 1024 * 1024);
        var previewPath = cache.ReservePreviewPath("image-outline");
        True(cache.IsManagedPreviewPath(previewPath), "保留路径必须属于预览缓存");
        File.WriteAllBytes(previewPath, MinimalPng);
        True(cache.PinPreview(previewPath), "当前界面采用的缓存预览应可被保护");
        False(cache.PinPreview(previewPath), "重复保护同一预览不应创建重复租约");

        var outputPath = Path.Combine(root, "published", "outline.png");
        var publishedPath = cache.PublishPreview(previewPath, outputPath);
        Equal(Path.GetFullPath(outputPath), publishedPath, "发布结果应返回规范化最终路径");
        True(File.Exists(outputPath), "发布应真实创建最终 PNG");
        Equal(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(previewPath))),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(outputPath))),
            "正式保存必须与预览缓存字节完全一致");
        True(File.Exists(previewPath), "发布后应保留预览用于继续查看或另存");

        Throws<IOException>(
            () => cache.PublishPreview(previewPath, outputPath),
            "默认不得静默覆盖已有文件");
        File.WriteAllBytes(outputPath, [0x00]);
        cache.PublishPreview(previewPath, outputPath, overwrite: true);
        Equal(MinimalPng.Length, File.ReadAllBytes(outputPath).Length, "显式覆盖应重新发布完整缓存 PNG");

        True(cache.ReleasePreview(previewPath), "释放现有预览应返回 true");
        False(File.Exists(previewPath), "释放预览应删除缓存文件");
        False(cache.ReleasePreview(previewPath), "重复释放应安全返回 false");

        var invalidPreview = cache.ReservePreviewPath("background-removal");
        File.WriteAllBytes(
            invalidPreview,
            [
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0x47, 0x41, 0x53, 0x53, 0x45, 0x54, 0x46, 0x4F, 0x52, 0x47, 0x45
            ]);
        Throws<InvalidDataException>(
            () => cache.PublishPreview(invalidPreview, Path.Combine(root, "published", "invalid.png")),
            "仅有 PNG 签名但结构损坏的缓存不得发布");
        cache.ReleasePreview(invalidPreview);

        var truncatedPreview = cache.ReservePreviewPath("pixel-alignment");
        File.WriteAllBytes(truncatedPreview, MinimalPng[..(MinimalPng.Length / 2)]);
        Throws<InvalidDataException>(
            () => cache.PublishPreview(truncatedPreview, Path.Combine(root, "published", "truncated.png")),
            "截断的 PNG 不得发布");
        cache.ReleasePreview(truncatedPreview);
    }

    private static void PreviewCacheProtectsPinnedResultsFromCleanup(string temporaryRoot)
    {
        var root = Path.Combine(temporaryRoot, "image-processing-preview-pins");
        var time = new LeaseRegressionTests.ManualTimeProvider(
            new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero));
        var cache = new ImageProcessingPreviewCache(
            Path.Combine(root, "cache"),
            timeProvider: time,
            maximumAge: TimeSpan.FromHours(1),
            maximumFileCount: 1,
            maximumTotalBytes: 1024 * 1024);

        var pinnedPreview = cache.ReservePreviewPath("image-outline");
        True(cache.PinPreview(pinnedPreview), "渲染写入前应能先保护已保留路径");
        File.WriteAllBytes(pinnedPreview, MinimalPng);
        File.SetLastWriteTimeUtc(pinnedPreview, time.GetUtcNow().AddHours(-2).UtcDateTime);

        var excessPreview = cache.ReservePreviewPath("background-removal");
        File.WriteAllBytes(excessPreview, MinimalPng);
        File.SetLastWriteTimeUtc(excessPreview, time.GetUtcNow().UtcDateTime);

        var cleanup = cache.CleanupExpiredAndExcessPreviews();
        True(File.Exists(pinnedPreview), "过期或超量清理不得删除界面仍在使用的缓存预览");
        False(File.Exists(excessPreview), "超出缓存数量上限时应优先删除未保护的预览");
        Equal(0, cleanup.FailedRemovalCount, "跳过受保护预览不应记为清理失败");

        True(cache.UnpinPreview(pinnedPreview), "界面释放预览后应解除保护");
        cache.CleanupExpiredAndExcessPreviews();
        False(File.Exists(pinnedPreview), "解除保护后，过期预览应能在下一轮清理中删除");
    }

    private static void ImageToolsRenderToCacheBeforePublishing(string temporaryRoot)
    {
        var root = Path.Combine(temporaryRoot, "image-processing-preview-workflows");
        Directory.CreateDirectory(root);
        OutlinePreviewStaysInspectibleUntilExplicitSave(root);
        BackgroundRemovalPreviewStaysInspectibleUntilExplicitSave(root);
    }

    private static void OutlinePreviewStaysInspectibleUntilExplicitSave(string root)
    {
        var inputPath = Path.Combine(root, "outline-source.png");
        var outputPath = Path.Combine(root, "outline-final.png");
        SaveOutlineFixture(inputPath);
        var cache = new ImageProcessingPreviewCache(Path.Combine(root, "cache"));
        var service = new CountingOutlineService(new ImageSharpImageOutlineService());
        var viewModel = new ImageOutlineViewModel(
            service,
            new FixedOutlineInputPicker(inputPath),
            new FixedOutlineOutputPicker(outputPath),
            previewCache: cache);

        viewModel.ChooseInputCommand.Execute(null);
        True(string.IsNullOrWhiteSpace(viewModel.OutputPath),
            "描边刷新预览前不应要求选择最终输出路径");
        True(viewModel.RefreshPreviewCommand.CanExecute(null),
            "输入和参数有效时描边预览应可刷新");
        viewModel.RefreshPreviewCommand.Execute(null);
        WaitUntil(
            () => viewModel.IsPreviewCurrent && viewModel.HasOutputPreview && !viewModel.IsBusy,
            TimeSpan.FromSeconds(5),
            "描边缓存预览应完成");

        var firstPreviewPath = viewModel.OutputPreviewPath;
        True(cache.IsManagedPreviewPath(firstPreviewPath), "描边单图预览必须写入受管缓存");
        False(File.Exists(outputPath), "刷新描边预览不得创建最终文件");
        viewModel.OutlineWidth++;
        True(viewModel.IsPreviewStale, "描边参数变化后应保留并标记旧预览过期");
        False(viewModel.SaveResultCommand.CanExecute(null), "过期描边预览不得保存");

        viewModel.RefreshPreviewCommand.Execute(null);
        WaitUntil(
            () => viewModel.IsPreviewCurrent &&
                  !string.Equals(viewModel.OutputPreviewPath, firstPreviewPath, StringComparison.OrdinalIgnoreCase) &&
                  !viewModel.IsBusy,
            TimeSpan.FromSeconds(5),
            "连续描边刷新必须切换唯一缓存文件");
        False(File.Exists(firstPreviewPath), "新描边预览生效后应释放被替代的旧缓存");

        True(viewModel.SaveResultCommand.CanExecute(null),
            "当前描边预览应可直接进入保存并在需要时弹出路径选择");
        viewModel.SaveResultCommand.Execute(null);
        WaitUntil(
            () => File.Exists(outputPath) && !viewModel.IsBusy,
            TimeSpan.FromSeconds(5),
            "描边预览应发布到最终输出");
        Equal(2, service.RenderCount, "描边保存不得重新调用渲染服务");
        Equal(HashFile(viewModel.OutputPreviewPath), HashFile(outputPath),
            "描边正式结果必须与已确认缓存预览字节一致");
    }

    private static void BackgroundRemovalPreviewStaysInspectibleUntilExplicitSave(string root)
    {
        var inputPath = Path.Combine(root, "background-source.png");
        var outputPath = Path.Combine(root, "background-final.png");
        SaveBackgroundFixture(inputPath);
        var cache = new ImageProcessingPreviewCache(Path.Combine(root, "cache"));
        var service = new CountingBackgroundRemovalService(new ImageSharpBackgroundRemovalService());
        var viewModel = new BackgroundRemovalViewModel(
            service,
            new FixedBackgroundInputPicker(inputPath),
            new FixedBackgroundOutputPicker(outputPath),
            previewCache: cache);

        viewModel.ChooseInputCommand.Execute(null);
        True(viewModel.RefreshPreviewCommand.CanExecute(null),
            "未设置输出位置时背景剔除预览仍应可刷新");
        viewModel.RefreshPreviewCommand.Execute(null);
        WaitUntil(
            () => viewModel.IsPreviewCurrent && viewModel.HasOutputPreview && !viewModel.IsBusy,
            TimeSpan.FromSeconds(5),
            "背景剔除缓存预览应完成");
        False(File.Exists(outputPath), "刷新背景剔除预览不得创建最终文件");

        viewModel.TransparentThreshold++;
        True(viewModel.IsPreviewStale, "背景剔除参数变化后应保留并标记旧预览过期");
        False(viewModel.SaveResultCommand.CanExecute(null), "过期背景剔除预览不得保存");
        viewModel.RefreshPreviewCommand.Execute(null);
        WaitUntil(
            () => viewModel.IsPreviewCurrent && !viewModel.IsBusy,
            TimeSpan.FromSeconds(5),
            "背景剔除参数变化后应能重新刷新");

        True(viewModel.SaveResultCommand.CanExecute(null),
            "当前背景剔除预览应可直接进入保存并在需要时弹出路径选择");
        viewModel.SaveResultCommand.Execute(null);
        WaitUntil(
            () => File.Exists(outputPath) && !viewModel.IsBusy,
            TimeSpan.FromSeconds(5),
            "背景剔除预览应发布到最终输出");
        Equal(2, service.RenderCount, "背景剔除保存不得重新调用渲染服务");
        Equal(HashFile(viewModel.OutputPreviewPath), HashFile(outputPath),
            "背景剔除正式结果必须与已确认缓存预览字节一致");
    }

    private static void ToolColorCommandsCommitOnlyConfirmedPickerResults()
    {
        var outlinePicker = new SequencedColorPickerService(
            null,
            CreateColorPickerResult("#A1B2C3"));
        var outline = new ImageOutlineViewModel(
            new ImageSharpImageOutlineService(),
            new FixedOutlineInputPicker(string.Empty),
            new FixedOutlineOutputPicker(string.Empty),
            colorHex: "#010203",
            colorPickerService: outlinePicker);
        outline.ChooseOutlineColorCommand.Execute(null);
        Equal("#010203", outline.OutlineColorHex,
            "取消描边取色不得修改宿主颜色");
        outline.ChooseOutlineColorCommand.Execute(null);
        Equal("#A1B2C3", outline.OutlineColorHex,
            "确认描边取色应只提交规范化 Hex");

        var backgroundPicker = new SequencedColorPickerService(
            CreateColorPickerResult("#102030"),
            CreateColorPickerResult("#405060"),
            CreateColorPickerResult("#708090"));
        var background = new BackgroundRemovalViewModel(
            new ImageSharpBackgroundRemovalService(),
            new FixedBackgroundInputPicker(string.Empty),
            new FixedBackgroundOutputPicker(string.Empty),
            colorPickerService: backgroundPicker);
        background.ChooseCustomColorCommand.Execute(null);
        background.ChooseCheckerColorACommand.Execute(null);
        background.ChooseCheckerColorBCommand.Execute(null);
        Equal("#102030", background.CustomColorHex, "自定义背景色取色接入");
        Equal("#405060", background.CheckerColorAHex, "假透明网格 A 色取色接入");
        Equal("#708090", background.CheckerColorBHex, "假透明网格 B 色取色接入");
        Equal(3, backgroundPicker.RequestedTitles.Count,
            "背景剔除三个颜色入口应复用同一取色服务");
    }

    private static ColorPickerResult CreateColorPickerResult(string hex)
    {
        var rgb = RgbColor.ParseHex(hex);
        return new ColorPickerResult(rgb, ColorConversion.ToHsv(rgb));
    }

    private static byte[] CreateMinimalPng()
    {
        using var image = new Image<Rgba32>(1, 1);
        image[0, 0] = new Rgba32(74, 144, 226, 255);
        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        return stream.ToArray();
    }

    private static void SaveOutlineFixture(string path)
    {
        using var image = new Image<Rgba32>(12, 12, new Rgba32(0, 0, 0, 0));
        for (var y = 3; y <= 8; y++)
        {
            for (var x = 3; x <= 8; x++)
            {
                image[x, y] = new Rgba32(40, 150, 230, 255);
            }
        }

        image.Save(path, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
    }

    private static void SaveBackgroundFixture(string path)
    {
        using var image = new Image<Rgba32>(12, 12, new Rgba32(255, 0, 255, 255));
        for (var y = 3; y <= 8; y++)
        {
            for (var x = 3; x <= 8; x++)
            {
                image[x, y] = new Rgba32(40, 150, 230, 255);
            }
        }

        image.Save(path, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

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

    private static void AssertHsv(
        RgbColor rgb,
        double expectedHue,
        double expectedSaturation,
        double expectedValue,
        string description)
    {
        var hsv = ColorConversion.ToHsv(rgb);
        Near(expectedHue, hsv.Hue, 0.000001d, $"{description} Hue");
        Near(expectedSaturation, hsv.Saturation, 0.000001d, $"{description} Saturation");
        Near(expectedValue, hsv.Value, 0.000001d, $"{description} Value");
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
            throw new InvalidOperationException($"{message}：期望 {expected}，实际 {actual}。");
        }
    }

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException($"{message}：期望 {expected}，实际 {actual}。");
        }
    }

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

    private sealed class FixedOutlineInputPicker(string inputPath) : IImageOutlineInputPicker
    {
        public string? SelectInputFile(string? initialDirectory) => inputPath;
    }

    private sealed class FixedOutlineOutputPicker(string outputPath) : IImageOutlineOutputPicker
    {
        public string? SelectOutputFile(string? initialDirectory, string suggestedFileName) => outputPath;
    }

    private sealed class FixedBackgroundInputPicker(string inputPath) : IBackgroundRemovalInputPicker
    {
        public string? SelectInputFile(string? initialDirectory) => inputPath;
    }

    private sealed class FixedBackgroundOutputPicker(string outputPath) : IBackgroundRemovalOutputPicker
    {
        public string? SelectOutputFile(string? initialDirectory, string suggestedFileName) => outputPath;
    }

    private sealed class CountingOutlineService(IImageOutlineService inner) : IImageOutlineService
    {
        public int RenderCount { get; private set; }

        public ImageOutlineImageInfo Inspect(string inputPath) => inner.Inspect(inputPath);

        public ImageOutlineResult ApplyOutlineToPng(
            string inputPath,
            string outputPath,
            ImageOutlineSettings settings,
            bool overwrite = false)
        {
            RenderCount++;
            return inner.ApplyOutlineToPng(inputPath, outputPath, settings, overwrite);
        }
    }

    private sealed class CountingBackgroundRemovalService(IBackgroundRemovalService inner)
        : IBackgroundRemovalService
    {
        public int RenderCount { get; private set; }

        public BackgroundRemovalResult RemoveToPng(
            string inputPath,
            string outputPath,
            BackgroundRemovalSettings settings,
            bool overwrite = false)
        {
            RenderCount++;
            return inner.RemoveToPng(inputPath, outputPath, settings, overwrite);
        }
    }

    private sealed class SequencedColorPickerService(params ColorPickerResult?[] results)
        : IColorPickerService
    {
        private readonly Queue<ColorPickerResult?> _results = new(results);

        public List<string> RequestedTitles { get; } = [];

        public ColorPickerResult? PickColor(string? initialColorHex, string? title = null)
        {
            RequestedTitles.Add(title ?? string.Empty);
            return _results.Count == 0 ? null : _results.Dequeue();
        }
    }
}

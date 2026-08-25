using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GAssetForge.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace GAssetForge.SmokeTests;

internal static class ImageOutlineSmokeTests
{
    private static readonly ImageSharpImageOutlineService Service = new();

    public static void Run()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "image-outline",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        try
        {
            InspectReturnsStableImageMetadata(testRoot);
            OutsideOutlineExpandsCanvasAndKeepsTheSourceAboveTheStroke(testRoot);
            InsideOutlineUsesTheCanvasBoundaryAndPreservesAlpha(testRoot);
            CenterOutlineSplitsOddWidthsOutwardFirst(testRoot);
            AlphaThresholdSuppressesLowAlphaPixelsWithoutDeletingTheSource(testRoot);
            CanvasExpansionCanBeDisabled(testRoot);
            InvalidSettingsAndUnsafeOutputsAreRejected(testRoot);
            OutputReplacementIsAtomicAndLeavesNoTemporaryFiles(testRoot);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void InspectReturnsStableImageMetadata(string testRoot)
    {
        var inputPath = Path.Combine(testRoot, "inspect.png");
        SaveFixture(inputPath, 7, 5, _ => { });

        var info = Service.Inspect(inputPath);

        Equal(Path.GetFullPath(inputPath), info.InputPath, "Inspect 应返回绝对输入路径");
        Equal(7, info.Width, "Inspect 图片宽度");
        Equal(5, info.Height, "Inspect 图片高度");
        True(string.Equals("PNG", info.FormatName, StringComparison.OrdinalIgnoreCase),
            "Inspect 应返回 PNG 格式名");
    }

    private static void OutsideOutlineExpandsCanvasAndKeepsTheSourceAboveTheStroke(string testRoot)
    {
        var inputPath = Path.Combine(testRoot, "outside-input.png");
        var outputPath = Path.Combine(testRoot, "outside-output.png");
        var sourcePixel = new Rgba32(220, 40, 30, 128);
        SaveFixture(inputPath, 3, 3, image => image[1, 1] = sourcePixel);

        var result = Service.ApplyOutlineToPng(
            inputPath,
            outputPath,
            new ImageOutlineSettings
            {
                Placement = ImageOutlinePlacement.Outside,
                Width = 1,
                ColorHex = "#20C060",
                AlphaThreshold = 1,
                ExpandCanvas = true
            });

        Equal(3, result.InputWidth, "外描边输入宽度");
        Equal(3, result.InputHeight, "外描边输入高度");
        Equal(5, result.OutputWidth, "外描边扩展后宽度");
        Equal(5, result.OutputHeight, "外描边扩展后高度");
        Equal(1, result.CanvasExpansion, "外描边画布单边扩展");
        Equal(8L, result.OutlinedPixelCount, "单像素方形邻域应产生八个外描边像素");
        Equal("#20C060", result.ColorHex, "结果应返回规范化描边颜色");

        using var output = Image.Load<Rgba32>(outputPath);
        Equal(sourcePixel, output[2, 2], "外描边必须位于原图后方并保持源像素 RGBA");
        Equal(new Rgba32(32, 192, 96, 128), output[1, 1],
            "半透明主体应生成同等覆盖率的外描边");
        Equal(default(Rgba32), output[0, 0], "描边半径外应保持透明");
    }

    private static void InsideOutlineUsesTheCanvasBoundaryAndPreservesAlpha(string testRoot)
    {
        var opaqueInput = Path.Combine(testRoot, "inside-opaque-input.png");
        var opaqueOutput = Path.Combine(testRoot, "inside-opaque-output.png");
        var red = new Rgba32(230, 30, 20, 255);
        SaveFixture(opaqueInput, 3, 3, image => Fill(image, red));

        var opaqueResult = Service.ApplyOutlineToPng(
            opaqueInput,
            opaqueOutput,
            new ImageOutlineSettings
            {
                Placement = ImageOutlinePlacement.Inside,
                Width = 1,
                ColorHex = "#1020F0",
                AlphaThreshold = 1,
                ExpandCanvas = true
            });

        Equal(3, opaqueResult.OutputWidth, "内描边不得扩展宽度");
        Equal(3, opaqueResult.OutputHeight, "内描边不得扩展高度");
        Equal(0, opaqueResult.CanvasExpansion, "内描边画布扩展必须为零");
        Equal(8L, opaqueResult.OutlinedPixelCount,
            "完全不透明画布的外边缘必须把画布外视为透明并形成内描边");
        using (var output = Image.Load<Rgba32>(opaqueOutput))
        {
            Equal(new Rgba32(16, 32, 240, 255), output[0, 0], "画布角落应被内描边覆盖");
            Equal(red, output[1, 1], "离边界一像素的中心应保持原色");
        }

        var partialInput = Path.Combine(testRoot, "inside-partial-input.png");
        var partialOutput = Path.Combine(testRoot, "inside-partial-output.png");
        SaveFixture(
            partialInput,
            3,
            3,
            image => image[1, 1] = new Rgba32(200, 100, 40, 96));

        Service.ApplyOutlineToPng(
            partialInput,
            partialOutput,
            new ImageOutlineSettings
            {
                Placement = ImageOutlinePlacement.Inside,
                Width = 1,
                ColorHex = "#000000",
                AlphaThreshold = 1
            });

        using var partialOutputImage = Image.Load<Rgba32>(partialOutput);
        Equal(new Rgba32(0, 0, 0, 96), partialOutputImage[1, 1],
            "内描边可以替换边缘颜色，但不得提高或降低源 Alpha");
    }

    private static void CenterOutlineSplitsOddWidthsOutwardFirst(string testRoot)
    {
        var inputPath = Path.Combine(testRoot, "center-input.png");
        var outputPath = Path.Combine(testRoot, "center-output.png");
        var subject = new Rgba32(220, 80, 30, 255);
        SaveFixture(
            inputPath,
            5,
            5,
            image =>
            {
                for (var y = 1; y <= 3; y++)
                {
                    for (var x = 1; x <= 3; x++)
                    {
                        image[x, y] = subject;
                    }
                }
            });

        var result = Service.ApplyOutlineToPng(
            inputPath,
            outputPath,
            new ImageOutlineSettings
            {
                Placement = ImageOutlinePlacement.Center,
                Width = 3,
                ColorHex = "#7A20E0",
                AlphaThreshold = 1,
                ExpandCanvas = true
            });

        Equal(2, result.CanvasExpansion,
            "3px 中心描边应按 ceil(width/2) 向外扩展 2px");
        Equal(9, result.OutputWidth, "中心描边输出宽度");
        Equal(9, result.OutputHeight, "中心描边输出高度");
        Equal(48L, result.OutlinedPixelCount,
            "3×3 主体的 2px 外环与 1px 内环应形成稳定像素数量");

        using var output = Image.Load<Rgba32>(outputPath);
        var outline = new Rgba32(122, 32, 224, 255);
        Equal(outline, output[1, 1], "中心描边的向外部分应覆盖 2px");
        Equal(outline, output[3, 3], "中心描边的向内部分应覆盖 1px");
        Equal(subject, output[4, 4], "内描边半径以外的主体中心应保持原色");
        Equal(default(Rgba32), output[0, 0], "中心描边半径外应透明");
    }

    private static void AlphaThresholdSuppressesLowAlphaPixelsWithoutDeletingTheSource(string testRoot)
    {
        var inputPath = Path.Combine(testRoot, "threshold-input.png");
        var suppressedPath = Path.Combine(testRoot, "threshold-suppressed.png");
        var includedPath = Path.Combine(testRoot, "threshold-included.png");
        var sourcePixel = new Rgba32(210, 110, 20, 100);
        SaveFixture(inputPath, 1, 1, image => image[0, 0] = sourcePixel);

        var suppressed = Service.ApplyOutlineToPng(
            inputPath,
            suppressedPath,
            new ImageOutlineSettings
            {
                Placement = ImageOutlinePlacement.Outside,
                Width = 1,
                ColorHex = "#00FF00",
                AlphaThreshold = 101
            });
        Equal(0L, suppressed.OutlinedPixelCount,
            "低于 Alpha 阈值的像素不得参与轮廓形态学");
        using (var output = Image.Load<Rgba32>(suppressedPath))
        {
            Equal(sourcePixel, output[1, 1],
                "Alpha 阈值只控制轮廓识别，不得删除低 Alpha 源像素");
            Equal(default(Rgba32), output[0, 0], "被阈值抑制后不得产生外描边");
        }

        var included = Service.ApplyOutlineToPng(
            inputPath,
            includedPath,
            new ImageOutlineSettings
            {
                Placement = ImageOutlinePlacement.Outside,
                Width = 1,
                ColorHex = "#00FF00",
                AlphaThreshold = 100
            });
        Equal(8L, included.OutlinedPixelCount,
            "达到 Alpha 阈值的单像素应产生完整方形外环");
        using var includedOutput = Image.Load<Rgba32>(includedPath);
        Equal(new Rgba32(0, 255, 0, 100), includedOutput[0, 0],
            "实际 Alpha max 形态学应把源覆盖率带到外描边");
    }

    private static void CanvasExpansionCanBeDisabled(string testRoot)
    {
        var inputPath = Path.Combine(testRoot, "no-expand-input.png");
        var outputPath = Path.Combine(testRoot, "no-expand-output.png");
        SaveFixture(
            inputPath,
            5,
            5,
            image => image[2, 2] = new Rgba32(255, 255, 255, 255));

        var result = Service.ApplyOutlineToPng(
            inputPath,
            outputPath,
            new ImageOutlineSettings
            {
                Placement = ImageOutlinePlacement.Outside,
                Width = 2,
                ColorHex = "#112233",
                AlphaThreshold = 1,
                ExpandCanvas = false
            });

        Equal(0, result.CanvasExpansion, "关闭扩展后 CanvasExpansion 应为零");
        Equal(5, result.OutputWidth, "关闭扩展后保持输入宽度");
        Equal(5, result.OutputHeight, "关闭扩展后保持输入高度");
        using var output = Image.Load<Rgba32>(outputPath);
        Equal(new Rgba32(17, 34, 51, 255), output[0, 0],
            "关闭扩展仍应保留画布范围内的外描边");
    }

    private static void InvalidSettingsAndUnsafeOutputsAreRejected(string testRoot)
    {
        var inputPath = Path.Combine(testRoot, "invalid-input.png");
        SaveFixture(inputPath, 2, 2, image => image[0, 0] = new Rgba32(255, 255, 255, 255));

        Throws<ArgumentOutOfRangeException>(
            () => Service.ApplyOutlineToPng(
                inputPath,
                Path.Combine(testRoot, "width-low.png"),
                new ImageOutlineSettings { Width = 0 }),
            "描边宽度下界必须受保护");
        Throws<ArgumentOutOfRangeException>(
            () => Service.ApplyOutlineToPng(
                inputPath,
                Path.Combine(testRoot, "width-high.png"),
                new ImageOutlineSettings { Width = 129 }),
            "描边宽度上界必须受保护");
        Throws<ArgumentOutOfRangeException>(
            () => Service.ApplyOutlineToPng(
                inputPath,
                Path.Combine(testRoot, "threshold-low.png"),
                new ImageOutlineSettings { AlphaThreshold = 0 }),
            "Alpha 阈值下界必须受保护");
        Throws<ArgumentOutOfRangeException>(
            () => Service.ApplyOutlineToPng(
                inputPath,
                Path.Combine(testRoot, "threshold-high.png"),
                new ImageOutlineSettings { AlphaThreshold = 255 }),
            "Alpha 阈值上界必须受保护");
        Throws<ArgumentOutOfRangeException>(
            () => Service.ApplyOutlineToPng(
                inputPath,
                Path.Combine(testRoot, "placement.png"),
                new ImageOutlineSettings { Placement = (ImageOutlinePlacement)999 }),
            "未知描边类型必须被拒绝");
        Throws<ArgumentException>(
            () => Service.ApplyOutlineToPng(
                inputPath,
                Path.Combine(testRoot, "color.png"),
                new ImageOutlineSettings { ColorHex = "not-a-color" }),
            "无效 Hex 颜色必须被拒绝");
        Throws<ArgumentException>(
            () => Service.ApplyOutlineToPng(inputPath, inputPath, new ImageOutlineSettings()),
            "输入与输出不得为同一路径");
        Throws<ArgumentException>(
            () => Service.ApplyOutlineToPng(
                inputPath,
                Path.Combine(testRoot, "not-png.jpg"),
                new ImageOutlineSettings()),
            "输出扩展名必须为 PNG");
    }

    private static void OutputReplacementIsAtomicAndLeavesNoTemporaryFiles(string testRoot)
    {
        var atomicRoot = Path.Combine(testRoot, "atomic");
        Directory.CreateDirectory(atomicRoot);
        var inputPath = Path.Combine(atomicRoot, "input.png");
        var outputPath = Path.Combine(atomicRoot, "final.png");
        SaveFixture(inputPath, 3, 3, image => image[1, 1] = new Rgba32(255, 255, 255, 255));
        File.WriteAllText(outputPath, "existing-output-must-remain-intact");

        Throws<IOException>(
            () => Service.ApplyOutlineToPng(
                inputPath,
                outputPath,
                new ImageOutlineSettings()),
            "未授权覆盖时必须保护既有输出");
        Equal("existing-output-must-remain-intact", File.ReadAllText(outputPath),
            "拒绝覆盖后既有文件内容必须保持完整");

        var result = Service.ApplyOutlineToPng(
            inputPath,
            outputPath,
            new ImageOutlineSettings
            {
                Placement = ImageOutlinePlacement.Outside,
                Width = 1,
                ColorHex = " 1a2b3c "
            },
            overwrite: true);

        Equal(Path.GetFullPath(outputPath), result.OutputPath, "原子替换应返回最终绝对路径");
        Equal("#1A2B3C", result.ColorHex, "颜色应被规范化为大写 #RRGGBB");
        using var output = Image.Load<Rgba32>(outputPath);
        Equal(5, output.Width, "原子替换后的 PNG 必须完整可解码");
        Equal(new Rgba32(26, 43, 60, 255), output[1, 1],
            "原子替换后的 PNG 应包含请求的描边颜色");
        False(
            Directory.EnumerateFiles(atomicRoot, $".{Path.GetFileName(outputPath)}.*.tmp").Any(),
            "成功输出后不得残留临时文件");
    }

    private static void SaveFixture(
        string path,
        int width,
        int height,
        Action<Image<Rgba32>> configure)
    {
        using var image = new Image<Rgba32>(width, height);
        configure(image);
        image.Save(path, new PngEncoder
        {
            ColorType = PngColorType.RgbWithAlpha
        });
    }

    private static void Fill(Image<Rgba32> image, Rgba32 color)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                accessor.GetRowSpan(y).Fill(color);
            }
        });
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

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }
}

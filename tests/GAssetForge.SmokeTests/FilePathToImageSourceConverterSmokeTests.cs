using GAssetForge.App.Converters;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GAssetForge.SmokeTests;

internal static class FilePathToImageSourceConverterSmokeTests
{
    public static void Run(string temporaryRoot)
    {
        var root = Path.Combine(temporaryRoot, "file-path-image-converter");
        Directory.CreateDirectory(root);

        TallImagesAreBoundedAndSameVersionIsShared(root);
        TotalPixelBudgetBoundsLargeSquareImages(root);
        ReplacedFilesInvalidateCachedPreviews(root);
        BrokenFilesReturnNull(root);
    }

    private static void TallImagesAreBoundedAndSameVersionIsShared(string root)
    {
        var path = Path.Combine(root, "tall.png");
        SaveIndexedPng(path, width: 64, height: 4096, fill: 0xAA);
        var converter = new FilePathToImageSourceConverter();

        var first = Convert(converter, path, "1400")
            ?? throw new FilePathImageConverterSmokeTestException("窄高图片应生成受限预览。");
        True(first.PixelWidth <= 1400 && first.PixelHeight <= 1400,
            $"预览宽高必须同时受限，实际 {first.PixelWidth}×{first.PixelHeight}。");
        True(first.PixelHeight < 4096,
            "窄高图片不能因只设置 DecodePixelWidth 而保留完整高度。");

        var second = Convert(converter, path, "1400");
        True(ReferenceEquals(first, second),
            "同一路径、文件版本和解码边界应复用同一个冻结预览。");
    }

    private static void TotalPixelBudgetBoundsLargeSquareImages(string root)
    {
        var path = Path.Combine(root, "large-square.png");
        SaveIndexedPng(path, width: 4096, height: 4096, fill: 0x5A);
        var converter = new FilePathToImageSourceConverter();

        var preview = Convert(converter, path, "4096")
            ?? throw new FilePathImageConverterSmokeTestException("大正方形图片应生成受限预览。");
        var pixels = (long)preview.PixelWidth * preview.PixelHeight;
        True(pixels <= 8L * 1024L * 1024L,
            $"预览总像素必须受限，实际 {pixels}。");
        True(preview.PixelWidth < 4096 && preview.PixelHeight < 4096,
            "总像素预算应在单边仍合法时继续缩小大图。");
    }

    private static void ReplacedFilesInvalidateCachedPreviews(string root)
    {
        var path = Path.Combine(root, "replace.png");
        SaveIndexedPng(path, width: 32, height: 16, fill: 0x00);
        var converter = new FilePathToImageSourceConverter();
        var original = Convert(converter, path, "1400")
            ?? throw new FilePathImageConverterSmokeTestException("替换测试的原始预览加载失败。");
        var previousWrite = File.GetLastWriteTimeUtc(path);

        SaveIndexedPng(path, width: 16, height: 32, fill: 0xFF, overwrite: true);
        File.SetLastWriteTimeUtc(path, previousWrite.AddSeconds(2));
        var replaced = Convert(converter, path, "1400")
            ?? throw new FilePathImageConverterSmokeTestException("替换后的预览加载失败。");

        True(!ReferenceEquals(original, replaced), "文件替换后不得返回旧缓存对象。");
        True(replaced.PixelWidth == 16 && replaced.PixelHeight == 32,
            $"文件替换后应读取新尺寸，实际 {replaced.PixelWidth}×{replaced.PixelHeight}。");
    }

    private static void BrokenFilesReturnNull(string root)
    {
        var path = Path.Combine(root, "broken.png");
        File.WriteAllText(path, "not a raster image");
        var converter = new FilePathToImageSourceConverter();
        True(Convert(converter, path, "1400") is null,
            "破损图片应返回空占位，不得向 WPF 冒泡异常。");
    }

    private static BitmapSource? Convert(
        FilePathToImageSourceConverter converter,
        string path,
        string edge) =>
        converter.Convert(path, typeof(ImageSource), edge, CultureInfo.InvariantCulture) as BitmapSource;

    private static void SaveIndexedPng(
        string path,
        int width,
        int height,
        byte fill,
        bool overwrite = false)
    {
        var stride = (width + 7) / 8;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, fill);
        var palette = new BitmapPalette([Colors.Black, Colors.White]);
        var source = BitmapSource.Create(
            width,
            height,
            96d,
            96d,
            PixelFormats.Indexed1,
            palette,
            pixels,
            stride);
        source.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new FileStream(
            path,
            overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        encoder.Save(stream);
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new FilePathImageConverterSmokeTestException(message);
        }
    }

    private sealed class FilePathImageConverterSmokeTestException(string message) : Exception(message);
}

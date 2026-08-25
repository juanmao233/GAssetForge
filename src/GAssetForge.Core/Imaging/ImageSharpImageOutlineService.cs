using System.Globalization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace GAssetForge.Core.Imaging;

public sealed class ImageSharpImageOutlineService : IImageOutlineService
{
    public ImageOutlineImageInfo Inspect(string inputPath)
    {
        var normalizedInput = NormalizeExistingInput(inputPath);
        var image = Image.Identify(normalizedInput)
            ?? throw new InvalidOperationException("无法识别图片描边输入图片。");
        ValidateSourceSize(image.Width, image.Height, nameof(inputPath));
        return new ImageOutlineImageInfo
        {
            InputPath = normalizedInput,
            Width = image.Width,
            Height = image.Height,
            FormatName = image.Metadata.DecodedImageFormat?.Name ?? string.Empty
        };
    }

    public ImageOutlineResult ApplyOutlineToPng(
        string inputPath,
        string outputPath,
        ImageOutlineSettings settings,
        bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateSettings(settings);
        var (normalizedColorHex, outlineColor) = ParseColor(settings.ColorHex);
        var imageInfo = Inspect(inputPath);
        var normalizedOutput = PrepareOutput(imageInfo.InputPath, outputPath, overwrite);

        Rgba32[] sourcePixels;
        int sourceWidth;
        int sourceHeight;
        using (var source = Image.Load<Rgba32>(imageInfo.InputPath))
        {
            ValidateSourceSize(source.Width, source.Height, nameof(inputPath));
            sourceWidth = source.Width;
            sourceHeight = source.Height;
            sourcePixels = new Rgba32[checked(sourceWidth * sourceHeight)];
            source.CopyPixelDataTo(sourcePixels);
        }

        var outsideRadius = settings.Placement switch
        {
            ImageOutlinePlacement.Outside => settings.Width,
            ImageOutlinePlacement.Center => (settings.Width + 1) / 2,
            _ => 0
        };
        var insideRadius = settings.Placement switch
        {
            ImageOutlinePlacement.Inside => settings.Width,
            ImageOutlinePlacement.Center => settings.Width / 2,
            _ => 0
        };
        var canvasPadding = settings.ExpandCanvas ? outsideRadius : 0;
        var outputWidth = checked(sourceWidth + canvasPadding * 2);
        var outputHeight = checked(sourceHeight + canvasPadding * 2);
        var outputPixelCount = checked(outputWidth * outputHeight);
        var silhouetteAlpha = new byte[outputPixelCount];
        for (var sourceY = 0; sourceY < sourceHeight; sourceY++)
        {
            var sourceOffset = sourceY * sourceWidth;
            var outputOffset = (sourceY + canvasPadding) * outputWidth + canvasPadding;
            for (var sourceX = 0; sourceX < sourceWidth; sourceX++)
            {
                var alpha = sourcePixels[sourceOffset + sourceX].A;
                silhouetteAlpha[outputOffset + sourceX] = alpha >= settings.AlphaThreshold
                    ? alpha
                    : (byte)0;
            }
        }

        var dilatedAlpha = outsideRadius > 0
            ? ApplySquareExtrema(
                silhouetteAlpha,
                outputWidth,
                outputHeight,
                outsideRadius,
                findMaximum: true)
            : null;
        var erodedAlpha = insideRadius > 0
            ? ApplySquareExtrema(
                silhouetteAlpha,
                outputWidth,
                outputHeight,
                insideRadius,
                findMaximum: false)
            : null;

        long outlinedPixelCount = 0;
        using var output = new Image<Rgba32>(outputWidth, outputHeight);
        output.ProcessPixelRows(accessor =>
        {
            for (var outputY = 0; outputY < outputHeight; outputY++)
            {
                var row = accessor.GetRowSpan(outputY);
                var outputOffset = outputY * outputWidth;
                var sourceY = outputY - canvasPadding;
                for (var outputX = 0; outputX < outputWidth; outputX++)
                {
                    var sourceX = outputX - canvasPadding;
                    var sourcePixel = sourceX >= 0 && sourceX < sourceWidth &&
                                      sourceY >= 0 && sourceY < sourceHeight
                        ? sourcePixels[sourceY * sourceWidth + sourceX]
                        : default;
                    var index = outputOffset + outputX;
                    var baseAlpha = silhouetteAlpha[index];
                    var outsideCoverage = dilatedAlpha is null
                        ? (byte)0
                        : SubtractSaturated(dilatedAlpha[index], baseAlpha);
                    var insideCoverage = erodedAlpha is null
                        ? (byte)0
                        : SubtractSaturated(baseAlpha, erodedAlpha[index]);

                    var rendered = sourcePixel;
                    if (outsideCoverage > 0)
                    {
                        rendered = CompositeSourceOver(
                            sourcePixel,
                            new Rgba32(outlineColor.R, outlineColor.G, outlineColor.B, outsideCoverage));
                    }

                    if (insideCoverage > 0)
                    {
                        rendered = ApplyClippedInnerColor(
                            rendered,
                            outlineColor,
                            insideCoverage,
                            baseAlpha);
                    }

                    if (outsideCoverage > 0 || insideCoverage > 0)
                    {
                        outlinedPixelCount++;
                    }

                    row[outputX] = rendered;
                }
            }
        });

        SavePngAtomically(output, normalizedOutput, overwrite);
        return new ImageOutlineResult
        {
            OutputPath = normalizedOutput,
            Placement = settings.Placement,
            StrokeWidth = settings.Width,
            ColorHex = normalizedColorHex,
            AlphaThreshold = settings.AlphaThreshold,
            InputWidth = sourceWidth,
            InputHeight = sourceHeight,
            OutputWidth = outputWidth,
            OutputHeight = outputHeight,
            CanvasExpansion = canvasPadding,
            OutlinedPixelCount = outlinedPixelCount
        };
    }

    private static byte[] ApplySquareExtrema(
        IReadOnlyList<byte> source,
        int width,
        int height,
        int radius,
        bool findMaximum)
    {
        if (radius <= 0)
        {
            return source.ToArray();
        }

        var horizontal = new byte[source.Count];
        var result = new byte[source.Count];
        var queueCapacity = checked(Math.Max(width, height) + radius * 2);
        var queueIndices = new int[queueCapacity];
        var queueValues = new byte[queueCapacity];

        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * width;
            ApplyHorizontalExtrema(
                source,
                horizontal,
                rowOffset,
                width,
                radius,
                findMaximum,
                queueIndices,
                queueValues);
        }

        for (var x = 0; x < width; x++)
        {
            ApplyVerticalExtrema(
                horizontal,
                result,
                x,
                width,
                height,
                radius,
                findMaximum,
                queueIndices,
                queueValues);
        }

        return result;
    }

    private static void ApplyHorizontalExtrema(
        IReadOnlyList<byte> source,
        byte[] destination,
        int rowOffset,
        int width,
        int radius,
        bool findMaximum,
        int[] queueIndices,
        byte[] queueValues)
    {
        var head = 0;
        var tail = 0;
        for (var scan = -radius; scan < width + radius; scan++)
        {
            var value = scan >= 0 && scan < width
                ? source[rowOffset + scan]
                : (byte)0;
            while (tail > head && IsDominated(queueValues[tail - 1], value, findMaximum))
            {
                tail--;
            }

            queueIndices[tail] = scan;
            queueValues[tail] = value;
            tail++;

            var windowStart = scan - radius * 2;
            while (tail > head && queueIndices[head] < windowStart)
            {
                head++;
            }

            var targetX = scan - radius;
            if (targetX >= 0 && targetX < width)
            {
                destination[rowOffset + targetX] = queueValues[head];
            }
        }
    }

    private static void ApplyVerticalExtrema(
        IReadOnlyList<byte> source,
        byte[] destination,
        int x,
        int width,
        int height,
        int radius,
        bool findMaximum,
        int[] queueIndices,
        byte[] queueValues)
    {
        var head = 0;
        var tail = 0;
        for (var scan = -radius; scan < height + radius; scan++)
        {
            var value = scan >= 0 && scan < height
                ? source[scan * width + x]
                : (byte)0;
            while (tail > head && IsDominated(queueValues[tail - 1], value, findMaximum))
            {
                tail--;
            }

            queueIndices[tail] = scan;
            queueValues[tail] = value;
            tail++;

            var windowStart = scan - radius * 2;
            while (tail > head && queueIndices[head] < windowStart)
            {
                head++;
            }

            var targetY = scan - radius;
            if (targetY >= 0 && targetY < height)
            {
                destination[targetY * width + x] = queueValues[head];
            }
        }
    }

    private static bool IsDominated(byte existing, byte candidate, bool findMaximum) =>
        findMaximum ? existing <= candidate : existing >= candidate;

    private static byte SubtractSaturated(byte minuend, byte subtrahend) =>
        minuend > subtrahend ? (byte)(minuend - subtrahend) : (byte)0;

    private static Rgba32 CompositeSourceOver(Rgba32 foreground, Rgba32 background)
    {
        var foregroundAlpha = foreground.A / 255d;
        var backgroundAlpha = background.A / 255d;
        var outputAlpha = foregroundAlpha + backgroundAlpha * (1d - foregroundAlpha);
        if (outputAlpha <= 0d)
        {
            return default;
        }

        return new Rgba32(
            ClampByte((foreground.R * foregroundAlpha +
                       background.R * backgroundAlpha * (1d - foregroundAlpha)) / outputAlpha),
            ClampByte((foreground.G * foregroundAlpha +
                       background.G * backgroundAlpha * (1d - foregroundAlpha)) / outputAlpha),
            ClampByte((foreground.B * foregroundAlpha +
                       background.B * backgroundAlpha * (1d - foregroundAlpha)) / outputAlpha),
            ClampByte(outputAlpha * 255d));
    }

    private static Rgba32 ApplyClippedInnerColor(
        Rgba32 source,
        Rgba32 outlineColor,
        byte coverage,
        byte silhouetteAlpha)
    {
        if (coverage == 0 || silhouetteAlpha == 0)
        {
            return source;
        }

        var blend = Math.Clamp(coverage / (double)silhouetteAlpha, 0d, 1d);
        return new Rgba32(
            ClampByte(source.R + (outlineColor.R - source.R) * blend),
            ClampByte(source.G + (outlineColor.G - source.G) * blend),
            ClampByte(source.B + (outlineColor.B - source.B) * blend),
            source.A);
    }

    private static void ValidateSettings(ImageOutlineSettings settings)
    {
        if (!Enum.IsDefined(settings.Placement))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "未知的图片描边位置类型。");
        }

        if (settings.Width is < ImageOutlineLimits.MinimumWidth or > ImageOutlineLimits.MaximumWidth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                $"描边宽度必须位于 {ImageOutlineLimits.MinimumWidth} 到 " +
                $"{ImageOutlineLimits.MaximumWidth} 像素之间。");
        }

        if (settings.AlphaThreshold is < ImageOutlineLimits.MinimumAlphaThreshold or
            > ImageOutlineLimits.MaximumAlphaThreshold)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                $"Alpha 阈值必须位于 {ImageOutlineLimits.MinimumAlphaThreshold} 到 " +
                $"{ImageOutlineLimits.MaximumAlphaThreshold} 之间。");
        }

        _ = ParseColor(settings.ColorHex);
    }

    private static (string NormalizedHex, Rgba32 Color) ParseColor(string? colorHex)
    {
        var normalized = colorHex?.Trim() ?? string.Empty;
        if (normalized.StartsWith('#'))
        {
            normalized = normalized[1..];
        }

        if (normalized.Length != 6 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("描边颜色必须是 #RRGGBB 格式。", nameof(colorHex));
        }

        var red = byte.Parse(normalized.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var green = byte.Parse(normalized.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var blue = byte.Parse(normalized.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ($"#{normalized.ToUpperInvariant()}", new Rgba32(red, green, blue, 255));
    }

    private static string NormalizeExistingInput(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        var normalizedInput = Path.GetFullPath(inputPath);
        if (!File.Exists(normalizedInput))
        {
            throw new FileNotFoundException("图片描边输入图片不存在。", normalizedInput);
        }

        return normalizedInput;
    }

    private static string PrepareOutput(string normalizedInput, string outputPath, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var normalizedOutput = Path.GetFullPath(outputPath);
        if (string.Equals(
                normalizedInput,
                normalizedOutput,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("图片描边输入与输出路径不能相同。", nameof(outputPath));
        }

        if (!string.Equals(Path.GetExtension(normalizedOutput), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("图片描边输出必须是 PNG。", nameof(outputPath));
        }

        var outputDirectory = Path.GetDirectoryName(normalizedOutput)
            ?? throw new InvalidOperationException("无法解析图片描边输出目录。");
        Directory.CreateDirectory(outputDirectory);
        if (File.Exists(normalizedOutput) && !overwrite)
        {
            throw new IOException($"图片描边输出已经存在：{normalizedOutput}");
        }

        return normalizedOutput;
    }

    private static void ValidateSourceSize(long width, long height, string parameterName)
    {
        if (width <= 0 || height <= 0 ||
            width > ImageOutlineLimits.MaximumSourceDimension ||
            height > ImageOutlineLimits.MaximumSourceDimension)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"源图片的任一边不能超过 {ImageOutlineLimits.MaximumSourceDimension} 像素。");
        }

        if (checked(width * height) > ImageOutlineLimits.MaximumSourcePixelCount)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"源图片不能超过 {ImageOutlineLimits.MaximumSourcePixelCount:N0} 像素。请先裁切或缩小图片。");
        }
    }

    private static void SavePngAtomically(Image<Rgba32> image, string outputPath, bool overwrite)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("无法解析图片描边输出目录。");
        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            image.Save(temporaryPath, new PngEncoder
            {
                ColorType = PngColorType.RgbWithAlpha
            });
            File.Move(temporaryPath, outputPath, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static byte ClampByte(double value) =>
        (byte)Math.Clamp((int)Math.Round(value), byte.MinValue, byte.MaxValue);
}

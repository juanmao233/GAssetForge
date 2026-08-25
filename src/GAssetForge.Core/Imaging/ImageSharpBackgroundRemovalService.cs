using System.Globalization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace GAssetForge.Core.Imaging;

public sealed class ImageSharpBackgroundRemovalService : IBackgroundRemovalService
{
    public BackgroundRemovalResult RemoveToPng(
        string inputPath,
        string outputPath,
        BackgroundRemovalSettings settings,
        bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(settings);
        ValidateSettings(settings);

        var normalizedInput = Path.GetFullPath(inputPath);
        var normalizedOutput = Path.GetFullPath(outputPath);
        if (!File.Exists(normalizedInput))
        {
            throw new FileNotFoundException("待剔除背景的图片不存在。", normalizedInput);
        }

        if (string.Equals(
                normalizedInput,
                normalizedOutput,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("背景剔除输入与输出路径不能相同。", nameof(outputPath));
        }

        if (!string.Equals(Path.GetExtension(normalizedOutput), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("背景剔除输出必须是 PNG。", nameof(outputPath));
        }

        var outputDirectory = Path.GetDirectoryName(normalizedOutput)
            ?? throw new InvalidOperationException("无法解析背景剔除输出目录。");
        Directory.CreateDirectory(outputDirectory);
        if (File.Exists(normalizedOutput) && !overwrite)
        {
            throw new IOException($"背景剔除输出已经存在：{normalizedOutput}");
        }

        using var image = Image.Load<Rgba32>(normalizedInput);
        var key = ParseColor(settings.KeyColorHex);
        var checkerColorA = settings.UseFakeTransparencyGrid
            ? ParseColor(settings.CheckerColorAHex)
            : default;
        var checkerColorB = settings.UseFakeTransparencyGrid
            ? ParseColor(settings.CheckerColorBHex)
            : default;
        var originalAlpha = new byte[checked(image.Width * image.Height)];
        var distances = new float[originalAlpha.Length];
        if (settings.UseFakeTransparencyGrid)
        {
            ReadCheckerSourceState(
                image,
                checkerColorA,
                checkerColorB,
                settings,
                originalAlpha,
                distances);
        }
        else
        {
            ReadSourceState(image, key, originalAlpha, distances);
        }

        var keyedRegion = settings.EdgeConnectedOnly
            ? FindEdgeConnectedRegion(image.Width, image.Height, distances, settings.OpaqueThreshold)
            : Enumerable.Repeat(true, originalAlpha.Length).ToArray();
        var matte = BuildMatte(originalAlpha, distances, keyedRegion, settings);

        if (settings.EdgeContract > 0)
        {
            matte = ContractAlpha(matte, image.Width, image.Height, settings.EdgeContract);
        }

        if (settings.EdgeFeather > 0)
        {
            matte = FeatherAlpha(matte, image.Width, image.Height, settings.EdgeFeather);
        }

        var stats = settings.UseFakeTransparencyGrid
            ? ApplyCheckerMatte(
                image,
                originalAlpha,
                matte,
                checkerColorA,
                checkerColorB,
                settings)
            : ApplyMatte(image, originalAlpha, matte, key, settings.Despill);
        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(normalizedOutput)}.{Guid.NewGuid():N}.tmp");
        try
        {
            image.Save(temporaryPath, new PngEncoder
            {
                ColorType = PngColorType.RgbWithAlpha
            });
            File.Move(temporaryPath, normalizedOutput, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return new BackgroundRemovalResult
        {
            OutputPath = normalizedOutput,
            KeyColorHex = BackgroundKeyColor.NormalizeHex(settings.KeyColorHex),
            Width = image.Width,
            Height = image.Height,
            RemovedPixelCount = stats.Removed,
            TransparentPixelCount = stats.Transparent,
            PartialPixelCount = stats.Partial,
            OpaquePixelCount = stats.Opaque
        };
    }

    private static void ValidateSettings(BackgroundRemovalSettings settings)
    {
        _ = BackgroundKeyColor.NormalizeHex(settings.KeyColorHex);
        if (settings.UseFakeTransparencyGrid)
        {
            var normalizedColorA = BackgroundKeyColor.NormalizeHex(settings.CheckerColorAHex);
            var normalizedColorB = BackgroundKeyColor.NormalizeHex(settings.CheckerColorBHex);
            if (string.Equals(normalizedColorA, normalizedColorB, StringComparison.Ordinal))
            {
                throw new ArgumentException("假透明网格的两种颜色不能相同。", nameof(settings));
            }

            if (settings.CheckerCellSize is < 2 or > 512)
            {
                throw new ArgumentException("假透明网格单元尺寸必须位于 2 到 512 像素之间。", nameof(settings));
            }

            if (settings.CheckerBoundaryTolerance is < 0 or > 8)
            {
                throw new ArgumentException("假透明网格边界容差必须位于 0 到 8 像素之间。", nameof(settings));
            }
        }

        if (!double.IsFinite(settings.TransparentThreshold) ||
            !double.IsFinite(settings.OpaqueThreshold) ||
            settings.TransparentThreshold is < 0d or > 441.7d ||
            settings.OpaqueThreshold is <= 0d or > 441.7d ||
            settings.OpaqueThreshold <= settings.TransparentThreshold)
        {
            throw new ArgumentException("背景剔除阈值无效；不透明阈值必须大于透明阈值。", nameof(settings));
        }

        if (settings.EdgeContract is < 0 or > 3)
        {
            throw new ArgumentException("边缘收缩必须位于 0 到 3 像素之间。", nameof(settings));
        }

        if (settings.EdgeFeather is < 0 or > 2)
        {
            throw new ArgumentException("边缘羽化必须位于 0 到 2 像素之间。", nameof(settings));
        }
    }

    private static Rgba32 ParseColor(string hex)
    {
        var normalized = BackgroundKeyColor.NormalizeHex(hex)[1..];
        return new Rgba32(
            byte.Parse(normalized.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(normalized.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(normalized.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            255);
    }

    private static void ReadSourceState(
        Image<Rgba32> image,
        Rgba32 key,
        byte[] originalAlpha,
        float[] distances)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var index = y * accessor.Width + x;
                    var pixel = row[x];
                    originalAlpha[index] = pixel.A;
                    distances[index] = Distance(pixel, key);
                }
            }
        });
    }

    private static void ReadCheckerSourceState(
        Image<Rgba32> image,
        Rgba32 checkerColorA,
        Rgba32 checkerColorB,
        BackgroundRemovalSettings settings,
        byte[] originalAlpha,
        float[] distances)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var index = y * accessor.Width + x;
                    var pixel = row[x];
                    originalAlpha[index] = pixel.A;
                    distances[index] = ResolveCheckerKey(
                        pixel,
                        x,
                        y,
                        checkerColorA,
                        checkerColorB,
                        settings).Distance;
                }
            }
        });
    }

    private static bool[] FindEdgeConnectedRegion(
        int width,
        int height,
        IReadOnlyList<float> distances,
        double opaqueThreshold)
    {
        var selected = new bool[checked(width * height)];
        var queue = new Queue<int>();

        void EnqueueIfCandidate(int x, int y)
        {
            var index = y * width + x;
            if (selected[index] || distances[index] >= opaqueThreshold)
            {
                return;
            }

            selected[index] = true;
            queue.Enqueue(index);
        }

        for (var x = 0; x < width; x++)
        {
            EnqueueIfCandidate(x, 0);
            EnqueueIfCandidate(x, height - 1);
        }

        for (var y = 1; y < height - 1; y++)
        {
            EnqueueIfCandidate(0, y);
            EnqueueIfCandidate(width - 1, y);
        }

        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            var x = index % width;
            var y = index / width;
            if (x > 0)
            {
                EnqueueIfCandidate(x - 1, y);
            }

            if (x + 1 < width)
            {
                EnqueueIfCandidate(x + 1, y);
            }

            if (y > 0)
            {
                EnqueueIfCandidate(x, y - 1);
            }

            if (y + 1 < height)
            {
                EnqueueIfCandidate(x, y + 1);
            }
        }

        return selected;
    }

    private static byte[] BuildMatte(
        IReadOnlyList<byte> sourceAlpha,
        IReadOnlyList<float> distances,
        IReadOnlyList<bool> keyedRegion,
        BackgroundRemovalSettings settings)
    {
        var matte = new byte[sourceAlpha.Count];
        for (var index = 0; index < matte.Length; index++)
        {
            var alpha = sourceAlpha[index];
            if (alpha == 0 || !keyedRegion[index])
            {
                matte[index] = alpha;
                continue;
            }

            var distance = distances[index];
            if (distance <= settings.TransparentThreshold)
            {
                matte[index] = 0;
            }
            else if (distance >= settings.OpaqueThreshold)
            {
                matte[index] = alpha;
            }
            else
            {
                var coverage = (distance - settings.TransparentThreshold) /
                               (settings.OpaqueThreshold - settings.TransparentThreshold);
                matte[index] = (byte)Math.Clamp((int)Math.Round(alpha * coverage), 0, 255);
            }
        }

        return matte;
    }

    private static byte[] ContractAlpha(byte[] source, int width, int height, int radius)
    {
        var result = new byte[source.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var minimum = byte.MaxValue;
                for (var yy = Math.Max(0, y - radius); yy <= Math.Min(height - 1, y + radius); yy++)
                {
                    for (var xx = Math.Max(0, x - radius); xx <= Math.Min(width - 1, x + radius); xx++)
                    {
                        minimum = Math.Min(minimum, source[yy * width + xx]);
                    }
                }

                result[y * width + x] = minimum;
            }
        }

        return result;
    }

    private static byte[] FeatherAlpha(byte[] source, int width, int height, int radius)
    {
        var result = new byte[source.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var total = 0;
                var count = 0;
                for (var yy = Math.Max(0, y - radius); yy <= Math.Min(height - 1, y + radius); yy++)
                {
                    for (var xx = Math.Max(0, x - radius); xx <= Math.Min(width - 1, x + radius); xx++)
                    {
                        total += source[yy * width + xx];
                        count++;
                    }
                }

                var index = y * width + x;
                var neighborhoodAverage = (byte)(total / Math.Max(1, count));
                // Feather inward only. A regular blur expands opaque coverage into transparent
                // pixels and can even revive source pixels whose Alpha was already zero.
                result[index] = Math.Min(source[index], neighborhoodAverage);
            }
        }

        return result;
    }

    private static (long Removed, long Transparent, long Partial, long Opaque) ApplyMatte(
        Image<Rgba32> image,
        IReadOnlyList<byte> originalAlpha,
        IReadOnlyList<byte> matte,
        Rgba32 key,
        bool despill)
    {
        long removed = 0;
        long transparent = 0;
        long partial = 0;
        long opaque = 0;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var index = y * accessor.Width + x;
                    var pixel = row[x];
                    var sourceA = originalAlpha[index];
                    // Every matte stage is intended to be subtractive. Keep the source Alpha as
                    // a final invariant so existing transparency can never be resurrected.
                    var targetA = Math.Min(sourceA, matte[index]);
                    if (targetA < sourceA)
                    {
                        removed++;
                    }

                    if (targetA == 0)
                    {
                        pixel.R = 0;
                        pixel.G = 0;
                        pixel.B = 0;
                        transparent++;
                    }
                    else
                    {
                        if (despill && sourceA > 0 && targetA < sourceA)
                        {
                            var coverage = Math.Clamp(targetA / (double)sourceA, 1d / 255d, 1d);
                            pixel.R = Unmix(pixel.R, key.R, coverage);
                            pixel.G = Unmix(pixel.G, key.G, coverage);
                            pixel.B = Unmix(pixel.B, key.B, coverage);
                        }

                        if (targetA < 255)
                        {
                            partial++;
                        }
                        else
                        {
                            opaque++;
                        }
                    }

                    pixel.A = targetA;
                    row[x] = pixel;
                }
            }
        });

        return (removed, transparent, partial, opaque);
    }

    private static (long Removed, long Transparent, long Partial, long Opaque) ApplyCheckerMatte(
        Image<Rgba32> image,
        IReadOnlyList<byte> originalAlpha,
        IReadOnlyList<byte> matte,
        Rgba32 checkerColorA,
        Rgba32 checkerColorB,
        BackgroundRemovalSettings settings)
    {
        long removed = 0;
        long transparent = 0;
        long partial = 0;
        long opaque = 0;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var index = y * accessor.Width + x;
                    var pixel = row[x];
                    var sourceA = originalAlpha[index];
                    var targetA = Math.Min(sourceA, matte[index]);
                    if (targetA < sourceA)
                    {
                        removed++;
                    }

                    if (targetA == 0)
                    {
                        pixel.R = 0;
                        pixel.G = 0;
                        pixel.B = 0;
                        transparent++;
                    }
                    else
                    {
                        if (settings.Despill && sourceA > 0 && targetA < sourceA)
                        {
                            var key = ResolveCheckerKey(
                                pixel,
                                x,
                                y,
                                checkerColorA,
                                checkerColorB,
                                settings).Key;
                            var coverage = Math.Clamp(targetA / (double)sourceA, 1d / 255d, 1d);
                            pixel.R = Unmix(pixel.R, key.R, coverage);
                            pixel.G = Unmix(pixel.G, key.G, coverage);
                            pixel.B = Unmix(pixel.B, key.B, coverage);
                        }

                        if (targetA < 255)
                        {
                            partial++;
                        }
                        else
                        {
                            opaque++;
                        }
                    }

                    pixel.A = targetA;
                    row[x] = pixel;
                }
            }
        });

        return (removed, transparent, partial, opaque);
    }

    private static CheckerKey ResolveCheckerKey(
        Rgba32 pixel,
        int x,
        int y,
        Rgba32 checkerColorA,
        Rgba32 checkerColorB,
        BackgroundRemovalSettings settings)
    {
        var distanceA = Distance(pixel, checkerColorA);
        var distanceB = Distance(pixel, checkerColorB);
        if (!settings.EnableCheckerGridAlignment || IsNearCheckerBoundary(x, y, settings))
        {
            return distanceA <= distanceB
                ? new CheckerKey(checkerColorA, distanceA)
                : new CheckerKey(checkerColorB, distanceB);
        }

        var cellX = FloorDiv((long)x - settings.CheckerOffsetX, settings.CheckerCellSize);
        var cellY = FloorDiv((long)y - settings.CheckerOffsetY, settings.CheckerCellSize);
        var expectsColorA = PositiveModulo(cellX + cellY, 2) == 0;
        var expectedKey = expectsColorA ? checkerColorA : checkerColorB;
        var expectedDistance = expectsColorA ? distanceA : distanceB;
        var oppositeDistance = expectsColorA ? distanceB : distanceA;

        // Away from a predicted grid boundary, a pixel that is closer to the opposite checker
        // color is foreground evidence rather than background evidence. Treat it as fully distant
        // so a subject detail matching the other checker color cannot become partially transparent
        // merely because the caller chose a wide opacity threshold.
        if (oppositeDistance < expectedDistance)
        {
            expectedDistance = float.MaxValue;
        }

        return new CheckerKey(expectedKey, expectedDistance);
    }

    private static bool IsNearCheckerBoundary(
        int x,
        int y,
        BackgroundRemovalSettings settings)
    {
        var tolerance = settings.CheckerBoundaryTolerance;
        if (tolerance <= 0)
        {
            return false;
        }

        var localX = PositiveModulo(
            (long)x - settings.CheckerOffsetX,
            settings.CheckerCellSize);
        var localY = PositiveModulo(
            (long)y - settings.CheckerOffsetY,
            settings.CheckerCellSize);
        return localX < tolerance || localX >= settings.CheckerCellSize - tolerance ||
               localY < tolerance || localY >= settings.CheckerCellSize - tolerance;
    }

    private static long FloorDiv(long value, int divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static int PositiveModulo(long value, int divisor)
    {
        var remainder = value % divisor;
        return (int)(remainder < 0 ? remainder + divisor : remainder);
    }

    private static byte Unmix(byte observed, byte key, double coverage) =>
        (byte)Math.Clamp(
            (int)Math.Round((observed - (1d - coverage) * key) / coverage),
            0,
            255);

    private static float Distance(Rgba32 pixel, Rgba32 key)
    {
        var red = pixel.R - key.R;
        var green = pixel.G - key.G;
        var blue = pixel.B - key.B;
        return MathF.Sqrt(red * red + green * green + blue * blue);
    }

    private readonly record struct CheckerKey(Rgba32 Key, float Distance);
}

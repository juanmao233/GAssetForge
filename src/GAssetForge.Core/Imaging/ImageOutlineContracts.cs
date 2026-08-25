namespace GAssetForge.Core.Imaging;

public enum ImageOutlinePlacement
{
    Outside,
    Inside,
    Center
}

public static class ImageOutlineLimits
{
    public const int MinimumWidth = 1;

    public const int MaximumWidth = 128;

    public const int MinimumAlphaThreshold = 1;

    public const int MaximumAlphaThreshold = 254;

    public const int MaximumSourceDimension = 16_384;

    public const long MaximumSourcePixelCount = 16_777_216;
}

public sealed record ImageOutlineSettings
{
    public ImageOutlinePlacement Placement { get; init; } = ImageOutlinePlacement.Outside;

    public int Width { get; init; } = 1;

    public string ColorHex { get; init; } = "#000000";

    public int AlphaThreshold { get; init; } = 1;

    public bool ExpandCanvas { get; init; } = true;
}

public sealed record ImageOutlineImageInfo
{
    public string InputPath { get; init; } = string.Empty;

    public int Width { get; init; }

    public int Height { get; init; }

    public string FormatName { get; init; } = string.Empty;
}

public sealed record ImageOutlineResult
{
    public string OutputPath { get; init; } = string.Empty;

    public ImageOutlinePlacement Placement { get; init; }

    public int StrokeWidth { get; init; }

    public string ColorHex { get; init; } = string.Empty;

    public int AlphaThreshold { get; init; }

    public int InputWidth { get; init; }

    public int InputHeight { get; init; }

    public int OutputWidth { get; init; }

    public int OutputHeight { get; init; }

    public int CanvasExpansion { get; init; }

    public long OutlinedPixelCount { get; init; }
}

public interface IImageOutlineService
{
    ImageOutlineImageInfo Inspect(string inputPath);

    ImageOutlineResult ApplyOutlineToPng(
        string inputPath,
        string outputPath,
        ImageOutlineSettings settings,
        bool overwrite = false);
}

namespace GAssetForge.Core.Imaging;

public sealed record BackgroundRemovalSettings
{
    public string KeyColorHex { get; init; } = "#FF00FF";

    public bool UseFakeTransparencyGrid { get; init; }

    public string CheckerColorAHex { get; init; } = "#FFFFFF";

    public string CheckerColorBHex { get; init; } = "#CCCCCC";

    public bool EnableCheckerGridAlignment { get; init; } = true;

    public int CheckerCellSize { get; init; } = 16;

    public int CheckerOffsetX { get; init; }

    public int CheckerOffsetY { get; init; }

    public int CheckerBoundaryTolerance { get; init; }

    public double TransparentThreshold { get; init; } = 30d;

    public double OpaqueThreshold { get; init; } = 120d;

    public int EdgeContract { get; init; } = 1;

    public int EdgeFeather { get; init; }

    public bool Despill { get; init; } = true;

    public bool EdgeConnectedOnly { get; init; } = true;
}

public sealed record BackgroundRemovalResult
{
    public string OutputPath { get; init; } = string.Empty;

    public string KeyColorHex { get; init; } = string.Empty;

    public int Width { get; init; }

    public int Height { get; init; }

    public long RemovedPixelCount { get; init; }

    public long TransparentPixelCount { get; init; }

    public long PartialPixelCount { get; init; }

    public long OpaquePixelCount { get; init; }
}

public interface IBackgroundRemovalService
{
    BackgroundRemovalResult RemoveToPng(
        string inputPath,
        string outputPath,
        BackgroundRemovalSettings settings,
        bool overwrite = false);
}

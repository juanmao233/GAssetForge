namespace GAssetForge.Core.Imaging;

public static class PixelAlignmentLimits
{
    public const int MaximumSourceDimension = 16_384;

    public const long MaximumSourcePixelCount = 16_777_216;

    public const int MaximumGridAxis = 4096;

    public const int MaximumGridPixelCount = 4_194_304;

    public const int MaximumUpscaleFactor = 1024;

    public const int MaximumNearestNeighborScale = 16_384;

    public const int MaximumWorkingDimension = 16_384;

    public const long MaximumWorkingPixelCount = 67_108_864;

    public const int MaximumDominantDistinctColorsPerCell = 262_144;

    public const double MinimumTargetAspectRatio = 1d / 64d;

    public const double MaximumTargetAspectRatio = 64d;

    public const double MaximumAbsoluteRotationDegrees = 360d;

    public const double MinimumTargetEdgeAngleDegrees = 5d;

    public const double MaximumTargetEdgeAngleDegrees = 175d;
}

/// <summary>
/// Defines a crop rectangle in source-image pixel coordinates. Right and Bottom are exclusive.
/// </summary>
public sealed record PixelCropBounds
{
    public int Left { get; init; }

    public int Top { get; init; }

    public int Right { get; init; }

    public int Bottom { get; init; }
}

public enum PixelCellSamplingMode
{
    DominantColor,
    AverageColor,
    CenterPixel
}

/// <summary>
/// A point in source-image pixel-edge coordinates. For example, the complete bounds of a
/// 50×50 image are (0,0), (50,0), (50,50), (0,50).
/// </summary>
public sealed record PixelCoordinate
{
    public double X { get; init; }

    public double Y { get; init; }
}

/// <summary>
/// Four ordered source points describing the visible plane. The order must remain
/// top-left, top-right, bottom-right, bottom-left and form a strictly convex quadrilateral.
/// </summary>
public sealed record PixelPerspectiveQuadrilateral
{
    public PixelCoordinate TopLeft { get; init; } = new();

    public PixelCoordinate TopRight { get; init; } = new();

    public PixelCoordinate BottomRight { get; init; } = new();

    public PixelCoordinate BottomLeft { get; init; } = new();
}

public sealed record PixelAlignmentCorrectionSettings
{
    public bool EnableAspectRatioCorrection { get; init; }

    /// <summary>
    /// Target width component A. The effective target aspect ratio is A / B.
    /// </summary>
    public double TargetAspectRatioA { get; init; } = 1d;

    /// <summary>
    /// Target height component B. The effective target aspect ratio is A / B.
    /// </summary>
    public double TargetAspectRatioB { get; init; } = 1d;

    public bool EnableRotationCorrection { get; init; }

    /// <summary>
    /// Clockwise image-space rotation around the corrected canvas centre.
    /// </summary>
    public double RotationDegrees { get; init; }

    public bool EnablePerspectiveCorrection { get; init; }

    /// <summary>
    /// Source plane corners in source-image pixel-edge coordinates. Required when perspective
    /// correction is enabled. The points must lie inside the active crop bounds.
    /// </summary>
    public PixelPerspectiveQuadrilateral? SourceQuadrilateral { get; init; }

    /// <summary>
    /// Desired clockwise angle from target edge A (left-to-right) to edge B (top-to-bottom).
    /// 90 degrees produces a rectangle; other values produce a fitted parallelogram.
    /// </summary>
    public double TargetEdgeAngleDegrees { get; init; } = 90d;

    public bool IsEnabled =>
        EnableAspectRatioCorrection ||
        EnableRotationCorrection ||
        EnablePerspectiveCorrection;
}

public sealed record PixelAlignmentSettings
{
    /// <summary>
    /// Null selects the complete source image. Right and Bottom are exclusive when supplied.
    /// </summary>
    public PixelCropBounds? CropBounds { get; init; }

    public int Columns { get; init; } = 32;

    public int Rows { get; init; } = 32;

    /// <summary>
    /// Requested lower bound for integer nearest-neighbor enlargement. The actual factor can be
    /// raised to the next grid-compatible multiple.
    /// </summary>
    public int MinimumUpscaleFactor { get; init; } = 1;

    public PixelCellSamplingMode SamplingMode { get; init; } = PixelCellSamplingMode.CenterPixel;

    public PixelAlignmentCorrectionSettings Correction { get; init; } = new();
}

public sealed record PixelAlignmentImageInfo
{
    public string InputPath { get; init; } = string.Empty;

    public int Width { get; init; }

    public int Height { get; init; }

    public string FormatName { get; init; } = string.Empty;
}

public sealed record PixelAlignmentPlan
{
    public int SourceWidth { get; init; }

    public int SourceHeight { get; init; }

    public PixelCropBounds CropBounds { get; init; } = new();

    public int CropWidth { get; init; }

    public int CropHeight { get; init; }

    public bool IsCorrectionEnabled { get; init; }

    /// <summary>
    /// Corrected canvas size before the compatible integer nearest-neighbour enlargement.
    /// Equals CropWidth/CropHeight when correction is disabled.
    /// </summary>
    public int CorrectedBaseWidth { get; init; }

    public int CorrectedBaseHeight { get; init; }

    public int Columns { get; init; }

    public int Rows { get; init; }

    /// <summary>
    /// Smallest uniform integer scale that makes both grid axes divide without fractions.
    /// </summary>
    public int GridAlignmentFactor { get; init; }

    public int WorkingScale { get; init; }

    /// <summary>
    /// Width of the exact virtual nearest-neighbour working grid. This can be much larger than
    /// a materialisable bitmap when mutually-prime custom row/column counts require a large LCM.
    /// </summary>
    public long WorkingWidth { get; init; }

    public long WorkingHeight { get; init; }

    public long CorrectedWorkingWidth { get; init; }

    public long CorrectedWorkingHeight { get; init; }

    public long CellWidth { get; init; }

    public long CellHeight { get; init; }

    public PixelAlignmentCorrectionSettings Correction { get; init; } = new();
}

public sealed record PixelAlignmentResult
{
    public string OutputPath { get; init; } = string.Empty;

    public PixelAlignmentPlan Plan { get; init; } = new();

    public PixelCellSamplingMode SamplingMode { get; init; }

    public int OutputWidth { get; init; }

    public int OutputHeight { get; init; }
}

public sealed record PixelNearestNeighborUpscaleResult
{
    public string OutputPath { get; init; } = string.Empty;

    public PixelCropBounds CropBounds { get; init; } = new();

    public int Scale { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public bool IsCorrectionApplied { get; init; }

    public PixelAlignmentPlan? Plan { get; init; }
}

public interface IPixelAlignmentService
{
    PixelAlignmentImageInfo Inspect(string inputPath);

    PixelAlignmentPlan CreatePlan(string inputPath, PixelAlignmentSettings settings);

    PixelAlignmentPlan CreatePlan(int sourceWidth, int sourceHeight, PixelAlignmentSettings settings);

    PixelNearestNeighborUpscaleResult UpscaleNearestNeighborToPng(
        string inputPath,
        string outputPath,
        int scale,
        PixelCropBounds? cropBounds = null,
        bool overwrite = false);

    PixelNearestNeighborUpscaleResult ExportWorkingImageToPng(
        string inputPath,
        string outputPath,
        PixelAlignmentSettings settings,
        bool overwrite = false);

    PixelAlignmentResult AlignToPng(
        string inputPath,
        string outputPath,
        PixelAlignmentSettings settings,
        bool overwrite = false);
}

using System.Globalization;

namespace GAssetForge.McpServer;

internal static class TilemapGeometryErrorCodes
{
    public const string AspectRatioMismatch = "tilemap_geometry_aspect_ratio_mismatch";

    public const string RotationMismatch = "tilemap_geometry_rotation_mismatch";

    public const string DiamondDetected = "tilemap_geometry_diamond_detected";
}

internal sealed record TilemapForegroundBoundingBox(
    int X,
    int Y,
    int Width,
    int Height);

internal sealed record TilemapCornerOccupancy(
    double TopLeftPercent,
    double TopRightPercent,
    double BottomLeftPercent,
    double BottomRightPercent)
{
    public double AveragePercent =>
        (TopLeftPercent + TopRightPercent + BottomLeftPercent + BottomRightPercent) / 4d;
}

internal sealed record TilemapGeometryDiagnostics(
    TilemapForegroundBoundingBox ForegroundBoundingBox,
    int ActualWidth,
    int ActualHeight,
    double ActualAspectRatio,
    double ExpectedAspectRatio,
    double AllowedDeviationPercent,
    TilemapCornerOccupancy CornerOccupancy,
    double RotationAngleEstimate,
    double DiamondScore,
    string ValidatedImagePath);

internal sealed class TilemapGeometryValidationException : IOException
{
    public TilemapGeometryValidationException(
        string errorCode,
        string description,
        TilemapGeometryDiagnostics diagnostics)
        : base(CreateMessage(errorCode, description, diagnostics))
    {
        ErrorCode = errorCode;
        Diagnostics = diagnostics;
    }

    public string ErrorCode { get; }

    public TilemapGeometryDiagnostics Diagnostics { get; }

    private static string CreateMessage(
        string errorCode,
        string description,
        TilemapGeometryDiagnostics diagnostics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var box = diagnostics.ForegroundBoundingBox;
        var corners = diagnostics.CornerOccupancy;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{description} errorCode={errorCode}; " +
            $"foregroundBoundingBox=({box.X},{box.Y},{box.Width},{box.Height}); " +
            $"actualWidth={diagnostics.ActualWidth}; actualHeight={diagnostics.ActualHeight}; " +
            $"actualAspectRatio={diagnostics.ActualAspectRatio:0.####}; " +
            $"expectedAspectRatio={diagnostics.ExpectedAspectRatio:0.####}; " +
            $"allowedDeviationPercent={diagnostics.AllowedDeviationPercent:0.##}; " +
            $"cornerOccupancy=({corners.TopLeftPercent:0.##}%,{corners.TopRightPercent:0.##}%," +
            $"{corners.BottomLeftPercent:0.##}%,{corners.BottomRightPercent:0.##}%); " +
            $"rotationAngleEstimate={diagnostics.RotationAngleEstimate:0.##}; " +
            $"diamondScore={diagnostics.DiamondScore:0.####}; " +
            $"validatedImagePath={diagnostics.ValidatedImagePath}");
    }
}

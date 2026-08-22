namespace GAssetForge.Core.Generation;

public enum TilemapRotationConvention
{
    AxisAAligned,
    LegacyDiamondAxisAligned
}

public readonly record struct TilemapAxisBasis(
    double AxisAX,
    double AxisAY,
    double AxisBX,
    double AxisBY)
{
    public double FootprintWidth => Math.Abs(AxisAX) + Math.Abs(AxisBX);

    public double FootprintHeight => Math.Abs(AxisAY) + Math.Abs(AxisBY);
}

public static class TilemapGeometryCalculator
{
    public const string AxisAAlignedRotationConventionId =
        "screen-clockwise-positive-axis-a-zero";

    public const string LegacyDiamondAxisAlignedRotationConventionId =
        "screen-clockwise-positive";

    public static TilemapAxisBasis CalculateAxes(
        double sideLength,
        double diamondAngle,
        double rotationAngle,
        TilemapRotationConvention convention = TilemapRotationConvention.AxisAAligned)
    {
        Validate(sideLength, diamondAngle, rotationAngle);

        var halfAngle = diamondAngle * Math.PI / 360d;
        var horizontal = sideLength * Math.Cos(halfAngle);
        var vertical = sideLength * Math.Sin(halfAngle);
        var effectiveRotation = CalculateEffectiveRotationRadians(
            diamondAngle,
            rotationAngle,
            convention);
        var cosine = Math.Cos(effectiveRotation);
        var sine = Math.Sin(effectiveRotation);
        var axisA = Rotate(horizontal, -vertical, cosine, sine);
        var axisB = Rotate(horizontal, vertical, cosine, sine);

        return new TilemapAxisBasis(axisA.X, axisA.Y, axisB.X, axisB.Y);
    }

    public static double CalculateEffectiveRotationRadians(
        double diamondAngle,
        double rotationAngle,
        TilemapRotationConvention convention = TilemapRotationConvention.AxisAAligned)
    {
        if (!double.IsFinite(diamondAngle) || diamondAngle is <= 0d or >= 180d)
        {
            throw new ArgumentOutOfRangeException(nameof(diamondAngle));
        }

        if (!double.IsFinite(rotationAngle))
        {
            throw new ArgumentOutOfRangeException(nameof(rotationAngle));
        }

        var baselineOffset = convention switch
        {
            TilemapRotationConvention.AxisAAligned => diamondAngle / 2d,
            TilemapRotationConvention.LegacyDiamondAxisAligned => 0d,
            _ => throw new ArgumentOutOfRangeException(nameof(convention))
        };
        return (rotationAngle + baselineOffset) * Math.PI / 180d;
    }

    public static bool TryParseRotationConvention(
        string? value,
        out TilemapRotationConvention convention)
    {
        if (string.Equals(value, AxisAAlignedRotationConventionId, StringComparison.Ordinal))
        {
            convention = TilemapRotationConvention.AxisAAligned;
            return true;
        }

        if (string.Equals(value, LegacyDiamondAxisAlignedRotationConventionId, StringComparison.Ordinal))
        {
            convention = TilemapRotationConvention.LegacyDiamondAxisAligned;
            return true;
        }

        convention = default;
        return false;
    }

    private static void Validate(double sideLength, double diamondAngle, double rotationAngle)
    {
        if (!double.IsFinite(sideLength) || sideLength <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(sideLength));
        }

        if (!double.IsFinite(diamondAngle) || diamondAngle is <= 0d or >= 180d)
        {
            throw new ArgumentOutOfRangeException(nameof(diamondAngle));
        }

        if (!double.IsFinite(rotationAngle))
        {
            throw new ArgumentOutOfRangeException(nameof(rotationAngle));
        }
    }

    private static (double X, double Y) Rotate(
        double x,
        double y,
        double cosine,
        double sine) =>
        (x * cosine - y * sine, x * sine + y * cosine);
}

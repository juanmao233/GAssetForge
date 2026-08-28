namespace GAssetForge.Core.ArtWorkspaces;

public readonly record struct ArtWorkspacePoint(double X, double Y);

public readonly record struct ArtWorkspacePixelBounds(int X, int Y, int Width, int Height);

/// <summary>
/// Converts between a persisted image-local normalized selection and frozen canvas/source-pixel
/// geometry. Image rotation is interpreted around the displayed image centre, matching the WPF
/// Art Workspace canvas transform.
/// </summary>
public static class ArtWorkspaceImageRegionGeometry
{
    public const double BoundaryTolerance = 0.000000001d;
    private const double PixelIntegerBoundaryTolerance = 0.000000001d;

    public static ArtWorkspaceImageRegionSelection FromWorldPoints(
        string imageId,
        double imageX,
        double imageY,
        double imageWidth,
        double imageHeight,
        double rotationDegrees,
        double startWorldX,
        double startWorldY,
        double endWorldX,
        double endWorldY)
    {
        ValidateImageGeometry(imageId, imageX, imageY, imageWidth, imageHeight, rotationDegrees);
        ValidateFinite(startWorldX, nameof(startWorldX));
        ValidateFinite(startWorldY, nameof(startWorldY));
        ValidateFinite(endWorldX, nameof(endWorldX));
        ValidateFinite(endWorldY, nameof(endWorldY));

        var first = WorldToNormalized(
            imageX,
            imageY,
            imageWidth,
            imageHeight,
            rotationDegrees,
            startWorldX,
            startWorldY);
        var second = WorldToNormalized(
            imageX,
            imageY,
            imageWidth,
            imageHeight,
            rotationDegrees,
            endWorldX,
            endWorldY);
        var left = Math.Clamp(Math.Min(first.X, second.X), 0d, 1d);
        var top = Math.Clamp(Math.Min(first.Y, second.Y), 0d, 1d);
        var right = Math.Clamp(Math.Max(first.X, second.X), 0d, 1d);
        var bottom = Math.Clamp(Math.Max(first.Y, second.Y), 0d, 1d);
        if (right - left <= BoundaryTolerance || bottom - top <= BoundaryTolerance)
        {
            throw new ArgumentException("框选区域必须与所选图片产生非空交集。", nameof(endWorldX));
        }

        return new ArtWorkspaceImageRegionSelection
        {
            ImageId = imageId,
            X = left,
            Y = top,
            Width = right - left,
            Height = bottom - top
        };
    }

    public static IReadOnlyList<ArtWorkspacePoint> GetWorldCorners(
        ArtWorkspaceImageItem image,
        ArtWorkspaceImageRegionSelection region)
    {
        ArgumentNullException.ThrowIfNull(image);
        return GetWorldCorners(
            image.Id,
            image.X,
            image.Y,
            image.Width,
            image.Height,
            image.RotationDegrees,
            region);
    }

    public static IReadOnlyList<ArtWorkspacePoint> GetWorldCorners(
        string imageId,
        double imageX,
        double imageY,
        double imageWidth,
        double imageHeight,
        double rotationDegrees,
        ArtWorkspaceImageRegionSelection region)
    {
        ValidateImageGeometry(imageId, imageX, imageY, imageWidth, imageHeight, rotationDegrees);
        ValidateRegion(region, imageId);
        return
        [
            NormalizedToWorld(imageX, imageY, imageWidth, imageHeight, rotationDegrees, region.X, region.Y),
            NormalizedToWorld(imageX, imageY, imageWidth, imageHeight, rotationDegrees, region.X + region.Width, region.Y),
            NormalizedToWorld(imageX, imageY, imageWidth, imageHeight, rotationDegrees, region.X + region.Width, region.Y + region.Height),
            NormalizedToWorld(imageX, imageY, imageWidth, imageHeight, rotationDegrees, region.X, region.Y + region.Height)
        ];
    }

    public static ArtWorkspacePixelBounds GetSourcePixelBounds(
        int pixelWidth,
        int pixelHeight,
        ArtWorkspaceImageRegionSelection region)
    {
        if (pixelWidth is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        }

        if (pixelHeight is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelHeight));
        }

        ValidateRegion(region, region.ImageId);
        var left = Math.Clamp(
            (int)Math.Floor(SnapNearPixelInteger(region.X * pixelWidth)),
            0,
            pixelWidth - 1);
        var top = Math.Clamp(
            (int)Math.Floor(SnapNearPixelInteger(region.Y * pixelHeight)),
            0,
            pixelHeight - 1);
        var right = Math.Clamp(
            (int)Math.Ceiling(SnapNearPixelInteger((region.X + region.Width) * pixelWidth)),
            left + 1,
            pixelWidth);
        var bottom = Math.Clamp(
            (int)Math.Ceiling(SnapNearPixelInteger((region.Y + region.Height) * pixelHeight)),
            top + 1,
            pixelHeight);
        return new ArtWorkspacePixelBounds(left, top, right - left, bottom - top);
    }

    private static double SnapNearPixelInteger(double value)
    {
        var nearestInteger = Math.Round(value);
        return Math.Abs(value - nearestInteger) <= PixelIntegerBoundaryTolerance
            ? nearestInteger
            : value;
    }

    public static void ValidateRegion(
        ArtWorkspaceImageRegionSelection region,
        string expectedImageId)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (string.IsNullOrWhiteSpace(expectedImageId) ||
            !string.Equals(region.ImageId, expectedImageId, StringComparison.Ordinal))
        {
            throw new ArgumentException("框选区域必须绑定当前图片 ID。", nameof(region));
        }

        ValidateNormalized(region.X, nameof(region.X), allowOne: true);
        ValidateNormalized(region.Y, nameof(region.Y), allowOne: true);
        ValidateNormalized(region.Width, nameof(region.Width), allowOne: true, requirePositive: true);
        ValidateNormalized(region.Height, nameof(region.Height), allowOne: true, requirePositive: true);
        if (region.X + region.Width > 1d + BoundaryTolerance ||
            region.Y + region.Height > 1d + BoundaryTolerance)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "框选区域不能越出图片本地边界。");
        }
    }

    private static ArtWorkspacePoint WorldToNormalized(
        double imageX,
        double imageY,
        double imageWidth,
        double imageHeight,
        double rotationDegrees,
        double worldX,
        double worldY)
    {
        var radians = rotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var centreX = imageX + imageWidth / 2d;
        var centreY = imageY + imageHeight / 2d;
        var deltaX = worldX - centreX;
        var deltaY = worldY - centreY;
        var localX = deltaX * cosine + deltaY * sine + imageWidth / 2d;
        var localY = -deltaX * sine + deltaY * cosine + imageHeight / 2d;
        return new ArtWorkspacePoint(localX / imageWidth, localY / imageHeight);
    }

    private static ArtWorkspacePoint NormalizedToWorld(
        double imageX,
        double imageY,
        double imageWidth,
        double imageHeight,
        double rotationDegrees,
        double normalizedX,
        double normalizedY)
    {
        var radians = rotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var localX = (normalizedX - 0.5d) * imageWidth;
        var localY = (normalizedY - 0.5d) * imageHeight;
        return new ArtWorkspacePoint(
            imageX + imageWidth / 2d + localX * cosine - localY * sine,
            imageY + imageHeight / 2d + localX * sine + localY * cosine);
    }

    private static void ValidateImageGeometry(
        string imageId,
        double imageX,
        double imageY,
        double imageWidth,
        double imageHeight,
        double rotationDegrees)
    {
        if (string.IsNullOrWhiteSpace(imageId))
        {
            throw new ArgumentException("图片 ID 不能为空。", nameof(imageId));
        }

        ValidateFinite(imageX, nameof(imageX));
        ValidateFinite(imageY, nameof(imageY));
        ValidateFinite(rotationDegrees, nameof(rotationDegrees));
        if (!double.IsFinite(imageWidth) || imageWidth <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidth));
        }

        if (!double.IsFinite(imageHeight) || imageHeight <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(imageHeight));
        }
    }

    private static void ValidateNormalized(
        double value,
        string name,
        bool allowOne,
        bool requirePositive = false)
    {
        var minimum = requirePositive ? BoundaryTolerance : 0d;
        var maximum = allowOne ? 1d : 1d - BoundaryTolerance;
        if (!double.IsFinite(value) || value < minimum || value > maximum + BoundaryTolerance)
        {
            throw new ArgumentOutOfRangeException(name, "归一化区域坐标必须位于 0 到 1 之间。");
        }
    }

    private static void ValidateFinite(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}

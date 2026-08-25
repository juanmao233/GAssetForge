namespace GAssetForge.Core.IconDesign;

public enum IconDesignMode
{
    SpecifiedAngle
}

public readonly record struct IconPoint2(double X, double Y)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}

public readonly record struct IconVector3(double X, double Y, double Z)
{
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);

    public IconVector3 Normalize()
    {
        var length = Length;
        return !double.IsFinite(length) || length <= 0.000000000001d
            ? default
            : this / length;
    }

    public static double Dot(IconVector3 left, IconVector3 right) =>
        left.X * right.X + left.Y * right.Y + left.Z * right.Z;

    public static IconVector3 Cross(IconVector3 left, IconVector3 right) =>
        new(
            left.Y * right.Z - left.Z * right.Y,
            left.Z * right.X - left.X * right.Z,
            left.X * right.Y - left.Y * right.X);

    public static IconVector3 operator +(IconVector3 left, IconVector3 right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    public static IconVector3 operator -(IconVector3 left, IconVector3 right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    public static IconVector3 operator -(IconVector3 value) => new(-value.X, -value.Y, -value.Z);

    public static IconVector3 operator *(IconVector3 value, double scalar) =>
        new(value.X * scalar, value.Y * scalar, value.Z * scalar);

    public static IconVector3 operator *(double scalar, IconVector3 value) => value * scalar;

    public static IconVector3 operator /(IconVector3 value, double scalar) =>
        new(value.X / scalar, value.Y / scalar, value.Z / scalar);
}

public readonly record struct IconMeshEdge(int StartVertexIndex, int EndVertexIndex);

public sealed record IconMeshFace(
    int Index,
    IReadOnlyList<int> VertexIndices,
    IconVector3 Normal);

public readonly record struct IconProjectedVertex(
    int Index,
    IconVector3 WorldPosition,
    IconPoint2 NormalizedDevicePoint,
    IconPoint2 ImagePixelPoint,
    double CameraDepth,
    bool IsInFrontOfCamera);

public sealed record IconProjectedFace(
    int Index,
    IReadOnlyList<int> VertexIndices,
    IconVector3 WorldNormal,
    double AverageCameraDepth,
    bool IsCameraFacing);

public readonly record struct IconProjectionBounds(
    double MinimumX,
    double MinimumY,
    double MaximumX,
    double MaximumY)
{
    public double Width => MaximumX - MinimumX;

    public double Height => MaximumY - MinimumY;

    public double WidthPercentOfFrame => Width * 50d;

    public double HeightPercentOfFrame => Height * 50d;

    public bool IsFinite =>
        double.IsFinite(MinimumX) &&
        double.IsFinite(MinimumY) &&
        double.IsFinite(MaximumX) &&
        double.IsFinite(MaximumY);
}

public sealed record IconProjectionParameters
{
    public IconDesignMode Mode { get; init; } = IconDesignMode.SpecifiedAngle;

    public int ImageWidth { get; init; } = 512;

    public int ImageHeight { get; init; } = 512;

    /// <summary>
    /// Camera orbit angle around the world Y axis. Zero places the camera on +Z;
    /// positive values move it towards +X.
    /// </summary>
    public double CameraAzimuthDegrees { get; init; } = 45d;

    /// <summary>
    /// Camera elevation above the XZ ground plane. Positive values look down at the subject.
    /// </summary>
    public double CameraElevationDegrees { get; init; } = 30d;

    /// <summary>
    /// Clockwise lens roll in the resulting image.
    /// </summary>
    public double CameraRollDegrees { get; init; }

    public double CameraDistance { get; init; } = 4.5d;

    public double FocalLengthMillimeters { get; init; } = 50d;

    public double SensorHeightMillimeters { get; init; } = 24d;

    public double SubjectSize { get; init; } = 1d;
}

public sealed record IconProjectionSnapshot(
    IconProjectionParameters Parameters,
    IconVector3 SubjectCenter,
    IconVector3 CameraPosition,
    IconVector3 CameraForward,
    IconVector3 CameraRight,
    IconVector3 CameraUp,
    double VerticalFieldOfViewDegrees,
    double HorizontalFieldOfViewDegrees,
    IReadOnlyList<IconVector3> CubeVertices,
    IReadOnlyList<IconMeshEdge> CubeEdges,
    IReadOnlyList<IconMeshFace> CubeFaces,
    IReadOnlyList<IconProjectedVertex> ProjectedVertices,
    IReadOnlyList<IconProjectedFace> ProjectedFaces,
    IconProjectionBounds NormalizedBounds,
    bool IsFullyInFrame);

public interface IIconPerspectiveProjectionService
{
    IconProjectionSnapshot Calculate(IconProjectionParameters parameters);
}

/// <summary>
/// Produces a deterministic pinhole-camera projection used by the icon perspective editor.
/// World coordinates use X right, Y up and Z towards the default camera. The subject remains
/// axis aligned at the origin while the camera orbits around it.
/// </summary>
public sealed class IconPerspectiveProjectionService : IIconPerspectiveProjectionService
{
    public const int MinimumImageDimension = 16;
    public const int MaximumImageDimension = 8192;
    public const double MinimumElevationDegrees = -85d;
    public const double MaximumElevationDegrees = 85d;
    public const double MinimumFocalLengthMillimeters = 12d;
    public const double MaximumFocalLengthMillimeters = 200d;
    public const double MinimumCameraDistance = 1.5d;
    public const double MaximumCameraDistance = 100d;
    public const double MinimumSubjectSize = 0.1d;
    public const double MaximumSubjectSize = 20d;

    private const double NearPlane = 0.001d;
    private static readonly IconVector3 SubjectCenter = new(0d, 0d, 0d);
    private static readonly IconVector3 WorldUp = new(0d, 1d, 0d);

    private static readonly IReadOnlyList<IconMeshEdge> CubeEdges = Array.AsReadOnly(
    new IconMeshEdge[]
    {
        new(0, 1), new(1, 2), new(2, 3), new(3, 0),
        new(4, 5), new(5, 6), new(6, 7), new(7, 4),
        new(0, 4), new(1, 5), new(2, 6), new(3, 7)
    });

    private static readonly IReadOnlyList<IconMeshFace> CubeFaces = Array.AsReadOnly(
    new[]
    {
        new IconMeshFace(0, Array.AsReadOnly(new[] { 0, 3, 2, 1 }), new(0d, 0d, -1d)),
        new IconMeshFace(1, Array.AsReadOnly(new[] { 4, 5, 6, 7 }), new(0d, 0d, 1d)),
        new IconMeshFace(2, Array.AsReadOnly(new[] { 0, 4, 7, 3 }), new(-1d, 0d, 0d)),
        new IconMeshFace(3, Array.AsReadOnly(new[] { 1, 2, 6, 5 }), new(1d, 0d, 0d)),
        new IconMeshFace(4, Array.AsReadOnly(new[] { 0, 1, 5, 4 }), new(0d, -1d, 0d)),
        new IconMeshFace(5, Array.AsReadOnly(new[] { 3, 7, 6, 2 }), new(0d, 1d, 0d))
    });

    public IconProjectionSnapshot Calculate(IconProjectionParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var normalized = Normalize(parameters);
        var aspectRatio = normalized.ImageWidth / (double)normalized.ImageHeight;
        var azimuth = DegreesToRadians(normalized.CameraAzimuthDegrees);
        var elevation = DegreesToRadians(normalized.CameraElevationDegrees);
        var roll = DegreesToRadians(normalized.CameraRollDegrees);
        var cosineElevation = Math.Cos(elevation);

        var cameraPosition = new IconVector3(
            normalized.CameraDistance * Math.Sin(azimuth) * cosineElevation,
            normalized.CameraDistance * Math.Sin(elevation),
            normalized.CameraDistance * Math.Cos(azimuth) * cosineElevation);
        var forward = (SubjectCenter - cameraPosition).Normalize();
        var baseRight = IconVector3.Cross(forward, WorldUp).Normalize();
        var baseUp = IconVector3.Cross(baseRight, forward).Normalize();
        var cosineRoll = Math.Cos(roll);
        var sineRoll = Math.Sin(roll);
        var right = (baseRight * cosineRoll + baseUp * sineRoll).Normalize();
        var up = (baseUp * cosineRoll - baseRight * sineRoll).Normalize();

        var verticalFieldOfView = 2d * Math.Atan(
            normalized.SensorHeightMillimeters / (2d * normalized.FocalLengthMillimeters));
        var horizontalFieldOfView = 2d * Math.Atan(
            Math.Tan(verticalFieldOfView / 2d) * aspectRatio);
        var tangentHalfVerticalField = Math.Tan(verticalFieldOfView / 2d);
        var cubeVertices = CreateCubeVertices(normalized.SubjectSize);
        var projectedVertices = new IconProjectedVertex[cubeVertices.Count];

        for (var index = 0; index < cubeVertices.Count; index++)
        {
            var worldPosition = cubeVertices[index];
            var relative = worldPosition - cameraPosition;
            var depth = IconVector3.Dot(relative, forward);
            var isInFront = double.IsFinite(depth) && depth > NearPlane;
            var normalizedPoint = isInFront
                ? new IconPoint2(
                    IconVector3.Dot(relative, right) / (depth * tangentHalfVerticalField * aspectRatio),
                    IconVector3.Dot(relative, up) / (depth * tangentHalfVerticalField))
                : new IconPoint2(double.NaN, double.NaN);
            var pixelPoint = normalizedPoint.IsFinite
                ? new IconPoint2(
                    (normalizedPoint.X + 1d) * normalized.ImageWidth / 2d,
                    (1d - normalizedPoint.Y) * normalized.ImageHeight / 2d)
                : new IconPoint2(double.NaN, double.NaN);
            projectedVertices[index] = new IconProjectedVertex(
                index,
                worldPosition,
                normalizedPoint,
                pixelPoint,
                depth,
                isInFront);
        }

        var projectedFaces = CubeFaces
            .Select(face => ProjectFace(face, cubeVertices, projectedVertices, cameraPosition))
            .ToArray();
        var bounds = CalculateBounds(projectedVertices);
        var fullyInFrame = projectedVertices.All(vertex =>
            vertex.IsInFrontOfCamera &&
            vertex.NormalizedDevicePoint.IsFinite &&
            Math.Abs(vertex.NormalizedDevicePoint.X) <= 1d &&
            Math.Abs(vertex.NormalizedDevicePoint.Y) <= 1d);

        return new IconProjectionSnapshot(
            normalized,
            SubjectCenter,
            cameraPosition,
            forward,
            right,
            up,
            RadiansToDegrees(verticalFieldOfView),
            RadiansToDegrees(horizontalFieldOfView),
            cubeVertices,
            CubeEdges,
            CubeFaces,
            projectedVertices,
            projectedFaces,
            bounds,
            fullyInFrame);
    }

    private static IconProjectionParameters Normalize(IconProjectionParameters parameters)
    {
        var subjectSize = CoerceFinite(
            parameters.SubjectSize,
            1d,
            MinimumSubjectSize,
            MaximumSubjectSize);
        var requestedDistance = CoerceFinite(
            parameters.CameraDistance,
            4.5d,
            MinimumCameraDistance,
            MaximumCameraDistance);
        var minimumClearDistance = subjectSize * Math.Sqrt(3d) / 2d + 0.05d;

        return parameters with
        {
            Mode = IconDesignMode.SpecifiedAngle,
            ImageWidth = Math.Clamp(
                parameters.ImageWidth,
                MinimumImageDimension,
                MaximumImageDimension),
            ImageHeight = Math.Clamp(
                parameters.ImageHeight,
                MinimumImageDimension,
                MaximumImageDimension),
            CameraAzimuthDegrees = NormalizeSignedAngle(
                CoerceFinite(parameters.CameraAzimuthDegrees, 45d, -36000d, 36000d)),
            CameraElevationDegrees = CoerceFinite(
                parameters.CameraElevationDegrees,
                30d,
                MinimumElevationDegrees,
                MaximumElevationDegrees),
            CameraRollDegrees = NormalizeSignedAngle(
                CoerceFinite(parameters.CameraRollDegrees, 0d, -36000d, 36000d)),
            CameraDistance = Math.Clamp(
                Math.Max(requestedDistance, minimumClearDistance),
                MinimumCameraDistance,
                MaximumCameraDistance),
            FocalLengthMillimeters = CoerceFinite(
                parameters.FocalLengthMillimeters,
                50d,
                MinimumFocalLengthMillimeters,
                MaximumFocalLengthMillimeters),
            SensorHeightMillimeters = CoerceFinite(
                parameters.SensorHeightMillimeters,
                24d,
                4d,
                100d),
            SubjectSize = subjectSize
        };
    }

    private static IReadOnlyList<IconVector3> CreateCubeVertices(double subjectSize)
    {
        var half = subjectSize / 2d;
        return Array.AsReadOnly(
        new IconVector3[]
        {
            new(-half, -half, -half),
            new(half, -half, -half),
            new(half, half, -half),
            new(-half, half, -half),
            new(-half, -half, half),
            new(half, -half, half),
            new(half, half, half),
            new(-half, half, half)
        });
    }

    private static IconProjectedFace ProjectFace(
        IconMeshFace face,
        IReadOnlyList<IconVector3> worldVertices,
        IReadOnlyList<IconProjectedVertex> projectedVertices,
        IconVector3 cameraPosition)
    {
        var center = default(IconVector3);
        var depth = 0d;
        foreach (var vertexIndex in face.VertexIndices)
        {
            center += worldVertices[vertexIndex];
            depth += projectedVertices[vertexIndex].CameraDepth;
        }

        center /= face.VertexIndices.Count;
        depth /= face.VertexIndices.Count;
        var cameraFacing = IconVector3.Dot(face.Normal, cameraPosition - center) > 0.000000001d;
        return new IconProjectedFace(
            face.Index,
            face.VertexIndices,
            face.Normal,
            depth,
            cameraFacing);
    }

    private static IconProjectionBounds CalculateBounds(
        IReadOnlyList<IconProjectedVertex> projectedVertices)
    {
        var finiteVertices = projectedVertices
            .Where(vertex => vertex.IsInFrontOfCamera && vertex.NormalizedDevicePoint.IsFinite)
            .ToArray();
        if (finiteVertices.Length == 0)
        {
            return new IconProjectionBounds(double.NaN, double.NaN, double.NaN, double.NaN);
        }

        return new IconProjectionBounds(
            finiteVertices.Min(vertex => vertex.NormalizedDevicePoint.X),
            finiteVertices.Min(vertex => vertex.NormalizedDevicePoint.Y),
            finiteVertices.Max(vertex => vertex.NormalizedDevicePoint.X),
            finiteVertices.Max(vertex => vertex.NormalizedDevicePoint.Y));
    }

    private static double CoerceFinite(double value, double fallback, double minimum, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static double NormalizeSignedAngle(double degrees)
    {
        var normalized = degrees % 360d;
        if (normalized > 180d)
        {
            normalized -= 360d;
        }
        else if (normalized < -180d)
        {
            normalized += 360d;
        }

        return Math.Abs(normalized) <= 0.000000000001d ? 0d : normalized;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

    private static double RadiansToDegrees(double radians) => radians * 180d / Math.PI;
}

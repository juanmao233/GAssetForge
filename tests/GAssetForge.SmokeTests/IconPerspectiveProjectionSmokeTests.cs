using GAssetForge.Core.IconDesign;

namespace GAssetForge.SmokeTests;

internal static class IconPerspectiveProjectionSmokeTests
{
    public static void Run()
    {
        FrontCameraUsesDocumentedWorldAxes();
        ThreeQuarterProjectionIsFiniteAndCameraBasisIsOrthonormal();
        FocalLengthAndDistanceChangeFramingPredictably();
        PositiveRollRotatesTheCameraFrameClockwise();
        InvalidInputsAreNormalizedToSafeProjectionParameters();
    }

    private static void FrontCameraUsesDocumentedWorldAxes()
    {
        var snapshot = Calculate(
            azimuth: 0d,
            elevation: 0d,
            roll: 0d,
            distance: 5d,
            focalLength: 50d);

        VectorNear(new IconVector3(0d, 0d, 5d), snapshot.CameraPosition, 0.000000001d,
            "0° 方位角应把相机放在 +Z");
        VectorNear(new IconVector3(0d, 0d, -1d), snapshot.CameraForward, 0.000000001d,
            "正面相机应朝向 -Z");
        VectorNear(new IconVector3(1d, 0d, 0d), snapshot.CameraRight, 0.000000001d,
            "未滚转正面相机的画面右侧应为 +X");
        VectorNear(new IconVector3(0d, 1d, 0d), snapshot.CameraUp, 0.000000001d,
            "未滚转正面相机的画面上方应为 +Y");
        True(snapshot.ProjectedVertices.All(vertex => vertex.ImagePixelPoint.IsFinite),
            "正面投影的全部顶点必须得到有效像素坐标");
        True(snapshot.IsFullyInFrame, "默认正面参数应完整容纳方块");
    }

    private static void ThreeQuarterProjectionIsFiniteAndCameraBasisIsOrthonormal()
    {
        var snapshot = Calculate(
            azimuth: 45d,
            elevation: 30d,
            roll: 0d,
            distance: 4.5d,
            focalLength: 50d);

        True(snapshot.CameraPosition.X > 0d && snapshot.CameraPosition.Y > 0d && snapshot.CameraPosition.Z > 0d,
            "正方位角和正仰角应将相机放在 +X/+Y/+Z 八分空间");
        Near(1d, snapshot.CameraForward.Length, 0.000000001d, "相机 Forward 必须归一化");
        Near(1d, snapshot.CameraRight.Length, 0.000000001d, "相机 Right 必须归一化");
        Near(1d, snapshot.CameraUp.Length, 0.000000001d, "相机 Up 必须归一化");
        Near(0d, IconVector3.Dot(snapshot.CameraForward, snapshot.CameraRight), 0.000000001d,
            "Forward 与 Right 必须正交");
        Near(0d, IconVector3.Dot(snapshot.CameraForward, snapshot.CameraUp), 0.000000001d,
            "Forward 与 Up 必须正交");
        Near(0d, IconVector3.Dot(snapshot.CameraRight, snapshot.CameraUp), 0.000000001d,
            "Right 与 Up 必须正交");
        Equal(8, snapshot.CubeVertices.Count, "方块顶点数");
        Equal(12, snapshot.CubeEdges.Count, "方块边数");
        Equal(6, snapshot.CubeFaces.Count, "方块面数");
        Equal(3, snapshot.ProjectedFaces.Count(face => face.IsCameraFacing),
            "一般 3/4 视角应看到方块三个面");
        True(snapshot.NormalizedBounds.IsFinite && snapshot.NormalizedBounds.Width > 0d && snapshot.NormalizedBounds.Height > 0d,
            "3/4 视角必须产生有限且非空的画面包围盒");
    }

    private static void FocalLengthAndDistanceChangeFramingPredictably()
    {
        var wideLens = Calculate(45d, 30d, 0d, 4.5d, 24d);
        var longLens = Calculate(45d, 30d, 0d, 4.5d, 85d);
        True(longLens.NormalizedBounds.Width > wideLens.NormalizedBounds.Width,
            "相同物距下增加焦距应放大画面中的主体宽度");
        True(longLens.NormalizedBounds.Height > wideLens.NormalizedBounds.Height,
            "相同物距下增加焦距应放大画面中的主体高度");
        True(longLens.VerticalFieldOfViewDegrees < wideLens.VerticalFieldOfViewDegrees,
            "增加焦距应缩小垂直视场角");

        var near = Calculate(45d, 30d, 0d, 3d, 50d);
        var far = Calculate(45d, 30d, 0d, 8d, 50d);
        True(near.NormalizedBounds.Width > far.NormalizedBounds.Width,
            "相同焦距下增大物距应缩小画面中的主体");
        True(near.NormalizedBounds.Height > far.NormalizedBounds.Height,
            "相同焦距下增大物距应缩小画面中的主体高度");
    }

    private static void PositiveRollRotatesTheCameraFrameClockwise()
    {
        var unrolled = Calculate(0d, 0d, 0d, 5d, 50d);
        var rolled = Calculate(0d, 0d, 90d, 5d, 50d);
        var lowerRightFrontVertex = 5;
        var unrolledPoint = unrolled.ProjectedVertices[lowerRightFrontVertex].NormalizedDevicePoint;
        var rolledPoint = rolled.ProjectedVertices[lowerRightFrontVertex].NormalizedDevicePoint;

        True(unrolledPoint.X > 0d && unrolledPoint.Y < 0d,
            "未滚转时 +X/-Y 顶点应位于画面右下");
        True(rolledPoint.X < 0d && rolledPoint.Y < 0d,
            "+90° 顺时针滚转后原右下顶点应位于画面左下");
        Near(Math.Abs(unrolledPoint.X), Math.Abs(rolledPoint.Y), 0.000000001d,
            "90° 滚转应保持透视半径");
        Near(Math.Abs(unrolledPoint.Y), Math.Abs(rolledPoint.X), 0.000000001d,
            "90° 滚转应交换屏幕 X/Y 分量");
    }

    private static void InvalidInputsAreNormalizedToSafeProjectionParameters()
    {
        var service = new IconPerspectiveProjectionService();
        var snapshot = service.Calculate(
            new IconProjectionParameters
            {
                ImageWidth = -1,
                ImageHeight = 99999,
                CameraAzimuthDegrees = 450d,
                CameraElevationDegrees = double.NaN,
                CameraRollDegrees = -540d,
                CameraDistance = -4d,
                FocalLengthMillimeters = double.PositiveInfinity
            });

        Equal(IconPerspectiveProjectionService.MinimumImageDimension, snapshot.Parameters.ImageWidth,
            "非法图片宽度应收束到下限");
        Equal(IconPerspectiveProjectionService.MaximumImageDimension, snapshot.Parameters.ImageHeight,
            "超大图片高度应收束到上限");
        Near(90d, snapshot.Parameters.CameraAzimuthDegrees, 0.000000001d,
            "方位角应规范到有符号一周");
        Near(30d, snapshot.Parameters.CameraElevationDegrees, 0.000000001d,
            "非有限仰角应回退默认值");
        Near(-180d, snapshot.Parameters.CameraRollDegrees, 0.000000001d,
            "滚转角应规范到有符号一周");
        Near(IconPerspectiveProjectionService.MinimumCameraDistance, snapshot.Parameters.CameraDistance, 0.000000001d,
            "非法物距应收束到安全下限");
        Near(50d, snapshot.Parameters.FocalLengthMillimeters, 0.000000001d,
            "非有限焦距应回退默认值");
        True(snapshot.ProjectedVertices.All(vertex => vertex.IsInFrontOfCamera),
            "规范后的相机必须位于方块外并让全部顶点处于镜头前方");
    }

    private static IconProjectionSnapshot Calculate(
        double azimuth,
        double elevation,
        double roll,
        double distance,
        double focalLength) =>
        new IconPerspectiveProjectionService().Calculate(
            new IconProjectionParameters
            {
                ImageWidth = 512,
                ImageHeight = 512,
                CameraAzimuthDegrees = azimuth,
                CameraElevationDegrees = elevation,
                CameraRollDegrees = roll,
                CameraDistance = distance,
                FocalLengthMillimeters = focalLength
            });

    private static void VectorNear(
        IconVector3 expected,
        IconVector3 actual,
        double tolerance,
        string message)
    {
        Near(expected.X, actual.X, tolerance, $"{message} / X");
        Near(expected.Y, actual.Y, tolerance, $"{message} / Y");
        Near(expected.Z, actual.Z, tolerance, $"{message} / Z");
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}：期望 {expected}，实际 {actual}。");
        }
    }

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException(
                $"{message}：期望 {expected} ± {tolerance}，实际 {actual}。");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

using GAssetForge.Core.Generation;
using GAssetForge.McpServer;
using System;
using System.Collections.Generic;
using System.IO;

namespace GAssetForge.SmokeTests;

internal static class TilemapGeometryDiagnosticsSmokeTests
{
    private const int CanvasSize = 512;

    public static void Run(string temporaryRoot)
    {
        var constraints = TilemapConstraintPackageBuilder.Build(
            new TilemapGenerationConstraintInput
            {
                GridUnitSize = 100d,
                ArtWidth = CanvasSize,
                ArtHeight = CanvasSize,
                DiamondAngle = 90d,
                RotationAngle = 0d
            });

        var directory = Path.Combine(temporaryRoot, "tilemap-geometry-diagnostics");
        Directory.CreateDirectory(directory);

        var rectanglePath = Path.Combine(directory, "axis-aligned-384x448.png");
        Program.WritePngFixture(
            rectanglePath,
            CanvasSize,
            CanvasSize,
            includeAlpha: true,
            foregroundPredicate: CreateRotatedRectanglePredicate(384d, 448d, 0d));
        var rectangle = CaptureGeometryFailure(rectanglePath, constraints);
        Equal(TilemapGeometryErrorCodes.AspectRatioMismatch, rectangle.ErrorCode,
            "轴对齐竖向长方形应识别为主体宽高比错误");
        Equal(384, rectangle.Diagnostics.ActualWidth, "长方形前景宽度");
        Equal(448, rectangle.Diagnostics.ActualHeight, "长方形前景高度");
        Near(384d / 448d, rectangle.Diagnostics.ActualAspectRatio, 0.000001d,
            "长方形前景宽高比");
        Near(1d, rectangle.Diagnostics.ExpectedAspectRatio, 0.000001d,
            "正方形期望宽高比");
        True(rectangle.Diagnostics.CornerOccupancy.AveragePercent > 99d,
            "轴对齐长方形的自身四角应接近完全占用");
        True(rectangle.Diagnostics.RotationAngleEstimate < 1d,
            "轴对齐长方形不应被误判为旋转轮廓");
        True(rectangle.Diagnostics.DiamondScore < 0.5d,
            "轴对齐长方形不应有高菱形评分");
        True(!rectangle.Message.Contains("菱形", StringComparison.Ordinal) &&
             !rectangle.Message.Contains("钻石", StringComparison.Ordinal),
            "长方形错误不应继续误报为菱形");
        Equal(Path.GetFullPath(rectanglePath), rectangle.Diagnostics.ValidatedImagePath,
            "诊断必须指向实际校验文件");

        var rotatedSquarePath = Path.Combine(directory, "rotated-square-22deg.png");
        Program.WritePngFixture(
            rotatedSquarePath,
            CanvasSize,
            CanvasSize,
            includeAlpha: true,
            foregroundPredicate: CreateRotatedRectanglePredicate(352d, 352d, 22d));
        var rotatedSquare = CaptureGeometryFailure(rotatedSquarePath, constraints);
        Equal(TilemapGeometryErrorCodes.RotationMismatch, rotatedSquare.ErrorCode,
            "22° 旋转正方形应识别为轮廓方向错误");
        True(rotatedSquare.Diagnostics.ActualAspectRatio is > 0.98d and < 1.02d,
            "旋转正方形的包围盒应仍接近正方形");
        Near(22d, rotatedSquare.Diagnostics.RotationAngleEstimate, 2d,
            "旋转正方形的边缘方向估计");
        True(rotatedSquare.Diagnostics.DiamondScore < 0.90d,
            "非 45° 旋转正方形不应分类为菱形");

        var diamondPath = Path.Combine(directory, "on-point-diamond-45deg.png");
        Program.WritePngFixture(
            diamondPath,
            CanvasSize,
            CanvasSize,
            includeAlpha: true,
            foregroundPredicate: CreateRotatedRectanglePredicate(320d, 320d, 45d));
        var diamond = CaptureGeometryFailure(diamondPath, constraints);
        Equal(TilemapGeometryErrorCodes.DiamondDetected, diamond.ErrorCode,
            "45° 上下尖角轮廓应明确识别为菱形");
        Near(45d, diamond.Diagnostics.RotationAngleEstimate, 2d,
            "真实菱形的边缘方向估计");
        True(diamond.Diagnostics.DiamondScore >= 0.90d,
            "真实菱形应有高菱形评分");
        True(diamond.Diagnostics.CornerOccupancy.AveragePercent < 5d,
            "真实菱形的包围盒四角应基本为空");
    }

    private static Func<int, int, bool> CreateRotatedRectanglePredicate(
        double width,
        double height,
        double clockwiseDegrees)
    {
        var radians = clockwiseDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var halfWidth = width / 2d;
        var halfHeight = height / 2d;
        var center = CanvasSize / 2d;
        return (x, y) =>
        {
            var translatedX = x + 0.5d - center;
            var translatedY = y + 0.5d - center;
            var localX = translatedX * cosine + translatedY * sine;
            var localY = -translatedX * sine + translatedY * cosine;
            return Math.Abs(localX) <= halfWidth && Math.Abs(localY) <= halfHeight;
        };
    }

    private static TilemapGeometryValidationException CaptureGeometryFailure(
        string fullPath,
        GenerationConstraintPackage constraints)
    {
        try
        {
            GeneratedAssetValidator.Validate(fullPath, constraints);
        }
        catch (TilemapGeometryValidationException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"期望 TilemapGeometryValidationException，实际为 {exception.GetType().Name}: {exception.Message}",
                exception);
        }

        throw new InvalidOperationException("预期 Tilemap 几何校验失败，但产物被错误接受。");
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
        if (Math.Abs(expected - actual) > tolerance)
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

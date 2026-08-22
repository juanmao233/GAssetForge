using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using GAssetForge.App.Controls;
using GAssetForge.App.Converters;
using GAssetForge.App.Services;
using GAssetForge.App.ViewModels;
using GAssetForge.App.Views;
using GAssetForge.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;

namespace GAssetForge.SmokeTests;

internal static class PixelAlignmentSmokeTests
{
    private static readonly ImageSharpPixelAlignmentService Service = new();

    public static void Run()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "pixel-alignment",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        try
        {
            PlanUsesTheSmallestUniformGridCompatibleScale();
            NearestNeighborUpscaleKeepsExactPixelBlocks(testRoot);
            AspectRatioCorrectionRebuildsGeometryAndExportsTheCorrectedWorkingImage(testRoot);
            PositiveRotationIsClockwiseAndCustomGridSizeIsStrict(testRoot);
            RotationExpandsNonSquareCanvasWithoutClipping(testRoot);
            PerspectiveCorrectionRectifiesFourPointsAndSupportsTargetShear(testRoot);
            InvalidPerspectiveGeometryIsRejected();
            GridResolvedScaleAboveTheRequestedLimitCanBeExported(testRoot);
            LargeMutuallyPrimeCustomGridUsesVirtualWorkingCoordinates(testRoot);
            CropRulersAndDominantSamplingProduceTheLogicalGrid(testRoot);
            AverageSamplingUsesAlphaAwareColor(testRoot);
            DominantSamplingRejectsUnboundedHighEntropyCells(testRoot);
            JpegInputCanBeInspectedAndExportedAsPng(testRoot);
            ResourceLimitsRejectUnsafeGridAndWorkingSizes(testRoot);
            ExistingOutputsAreProtectedAndTemporaryFilesAreRemoved(testRoot);
            ViewModelBuildsTheCompatiblePlanAndWritesTheConfiguredOutput(testRoot);
            ViewModelKeepsIndependentGridAndSafelySynchronizesAspectRatio(testRoot);
            ViewBindsCommandsParametersAndTheInteractiveSurface(testRoot);
            ShellLoadsAndPersistsToolPreferencesWithoutCropBounds(testRoot);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void PlanUsesTheSmallestUniformGridCompatibleScale()
    {
        var plan = Service.CreatePlan(
            50,
            50,
            new PixelAlignmentSettings
            {
                Columns = 32,
                Rows = 32,
                MinimumUpscaleFactor = 10
            });

        Equal(16, plan.GridAlignmentFactor, "50 像素按 32 格切分所需的最小整数倍率");
        Equal(16, plan.WorkingScale, "10 倍请求应提升到下一可整除倍率");
        Equal(800, plan.WorkingWidth, "无插值工作宽度");
        Equal(800, plan.WorkingHeight, "无插值工作高度");
        Equal(25, plan.CellWidth, "工作图每格宽度");
        Equal(25, plan.CellHeight, "工作图每格高度");

        var asymmetric = Service.CreatePlan(
            8,
            6,
            new PixelAlignmentSettings
            {
                CropBounds = new PixelCropBounds { Left = 1, Top = 1, Right = 6, Bottom = 5 },
                Columns = 4,
                Rows = 3,
                MinimumUpscaleFactor = 10
            });
        Equal(12, asymmetric.GridAlignmentFactor, "5×4 裁切区按 4×3 网格需要统一 12 倍");
        Equal(12, asymmetric.WorkingScale, "统一倍率必须保持裁切区宽高比例");
        Equal(60, asymmetric.WorkingWidth, "非方形裁切区工作宽度");
        Equal(48, asymmetric.WorkingHeight, "非方形裁切区工作高度");
        Equal(15, asymmetric.CellWidth, "非方形网格单元宽度");
        Equal(16, asymmetric.CellHeight, "非方形网格单元高度");
    }

    private static void NearestNeighborUpscaleKeepsExactPixelBlocks(string testRoot)
    {
        var inputPath = Path.Combine(testRoot, "nearest-input.png");
        var outputPath = Path.Combine(testRoot, "nearest-output.png");
        var red = new Rgba32(220, 20, 30, 255);
        var green = new Rgba32(30, 210, 80, 255);
        var blue = new Rgba32(20, 70, 230, 255);
        var transparent = new Rgba32(120, 80, 40, 0);
        using (var source = new Image<Rgba32>(2, 2))
        {
            source[0, 0] = red;
            source[1, 0] = green;
            source[0, 1] = blue;
            source[1, 1] = transparent;
            source.Save(inputPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        }

        var result = Service.UpscaleNearestNeighborToPng(inputPath, outputPath, scale: 3);
        Equal(6, result.Width, "最近邻放大宽度");
        Equal(6, result.Height, "最近邻放大高度");
        using var output = SixLabors.ImageSharp.Image.Load<Rgba32>(outputPath);
        AssertBlock(output, 0, 0, 3, 3, red, "左上像素块");
        AssertBlock(output, 3, 0, 3, 3, green, "右上像素块");
        AssertBlock(output, 0, 3, 3, 3, blue, "左下像素块");
        for (var y = 3; y < 6; y++)
        {
            for (var x = 3; x < 6; x++)
            {
                Equal((byte)0, output[x, y].A, "透明像素块 Alpha");
            }
        }
    }

    private static void AspectRatioCorrectionRebuildsGeometryAndExportsTheCorrectedWorkingImage(
        string testRoot)
    {
        var inputPath = Path.Combine(testRoot, "aspect-correction-input.png");
        var outputPath = Path.Combine(testRoot, "aspect-correction-output.png");
        var workingPath = Path.Combine(testRoot, "aspect-correction-working.png");
        var left = new Rgba32(220, 30, 40, 255);
        var right = new Rgba32(20, 170, 230, 255);
        using (var source = new Image<Rgba32>(4, 2))
        {
            for (var y = 0; y < source.Height; y++)
            {
                source[0, y] = left;
                source[1, y] = left;
                source[2, y] = right;
                source[3, y] = right;
            }

            source.Save(inputPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        }

        var settings = new PixelAlignmentSettings
        {
            Columns = 4,
            Rows = 4,
            SamplingMode = PixelCellSamplingMode.CenterPixel,
            Correction = new PixelAlignmentCorrectionSettings
            {
                EnableAspectRatioCorrection = true,
                TargetAspectRatioA = 1,
                TargetAspectRatioB = 1
            }
        };
        var plan = Service.CreatePlan(4, 2, settings);
        True(plan.IsCorrectionEnabled, "比例重整应标记计划已启用矫正");
        Equal(4, plan.CorrectedBaseWidth, "2:1 转 1:1 保持较长边宽度");
        Equal(4, plan.CorrectedBaseHeight, "2:1 转 1:1 应扩展短边形成正方形画幅");
        Equal(4, plan.CorrectedWorkingWidth, "兼容倍率为 1 时的矫正工作宽度");
        Equal(4, plan.CorrectedWorkingHeight, "兼容倍率为 1 时的矫正工作高度");

        Service.AlignToPng(inputPath, outputPath, settings);
        using (var output = SixLabors.ImageSharp.Image.Load<Rgba32>(outputPath))
        {
            Equal(4, output.Width, "比例重整输出网格宽度");
            Equal(4, output.Height, "比例重整输出网格高度");
            for (var y = 0; y < output.Height; y++)
            {
                Equal(left, output[0, y], "比例重整应把内容纵向重映射，而非上下留透明边");
                Equal(right, output[3, y], "比例重整后的右侧内容应覆盖完整目标高度");
            }
        }

        var workingResult = Service.ExportWorkingImageToPng(inputPath, workingPath, settings);
        True(workingResult.IsCorrectionApplied, "新工作图导出 API 应明确标记矫正已应用");
        Equal(4, workingResult.Width, "矫正工作图宽度");
        Equal(4, workingResult.Height, "矫正工作图高度");
        True(workingResult.Plan?.IsCorrectionEnabled == true, "工作图结果应携带对应矫正计划");
        using var working = SixLabors.ImageSharp.Image.Load<Rgba32>(workingPath);
        Equal(left, working[0, 3], "矫正工作图必须落盘重映射后的内容");
        Equal(right, working[3, 0], "矫正工作图不得退回旧裁切放大路径");
    }

    private static void PositiveRotationIsClockwiseAndCustomGridSizeIsStrict(string testRoot)
    {
        var inputPath = Path.Combine(testRoot, "rotation-input.png");
        var outputPath = Path.Combine(testRoot, "rotation-output.png");
        var customOutputPath = Path.Combine(testRoot, "custom-grid-output.png");
        var red = new Rgba32(230, 20, 30, 255);
        var green = new Rgba32(30, 210, 80, 255);
        var blue = new Rgba32(20, 70, 230, 255);
        var yellow = new Rgba32(240, 210, 20, 255);
        using (var source = new Image<Rgba32>(2, 2))
        {
            source[0, 0] = red;
            source[1, 0] = green;
            source[0, 1] = blue;
            source[1, 1] = yellow;
            source.Save(inputPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        }

        var rotation = new PixelAlignmentCorrectionSettings
        {
            EnableRotationCorrection = true,
            RotationDegrees = 90
        };
        Service.AlignToPng(
            inputPath,
            outputPath,
            new PixelAlignmentSettings
            {
                Columns = 2,
                Rows = 2,
                SamplingMode = PixelCellSamplingMode.CenterPixel,
                Correction = rotation
            });
        using (var output = SixLabors.ImageSharp.Image.Load<Rgba32>(outputPath))
        {
            Equal(blue, output[0, 0], "屏幕坐标正 90° 应把左下像素转到左上");
            Equal(red, output[1, 0], "屏幕坐标正 90° 应顺时针旋转");
            Equal(yellow, output[0, 1], "顺时针旋转后的左下像素");
            Equal(green, output[1, 1], "顺时针旋转后的右下像素");
        }

        Service.AlignToPng(
            inputPath,
            customOutputPath,
            new PixelAlignmentSettings
            {
                Columns = 7,
                Rows = 3,
                SamplingMode = PixelCellSamplingMode.CenterPixel,
                Correction = rotation
            });
        using var customOutput = SixLabors.ImageSharp.Image.Load<Rgba32>(customOutputPath);
        Equal(7, customOutput.Width, "矫正不得覆盖用户自定义列数");
        Equal(3, customOutput.Height, "矫正不得覆盖用户自定义行数");
    }

    private static void RotationExpandsNonSquareCanvasWithoutClipping(string testRoot)
    {
        var inputPath = Path.Combine(testRoot, "rotation-non-square-input.png");
        var outputPath = Path.Combine(testRoot, "rotation-non-square-output.png");
        var colors = new[]
        {
            new Rgba32(220, 20, 20, 255),
            new Rgba32(220, 120, 20, 255),
            new Rgba32(220, 220, 20, 255),
            new Rgba32(20, 200, 80, 255),
            new Rgba32(20, 180, 220, 255),
            new Rgba32(30, 80, 220, 255),
            new Rgba32(130, 50, 220, 255),
            new Rgba32(220, 40, 150, 255)
        };
        using (var source = new Image<Rgba32>(4, 2))
        {
            for (var y = 0; y < source.Height; y++)
            {
                for (var x = 0; x < source.Width; x++)
                {
                    source[x, y] = colors[y * source.Width + x];
                }
            }

            source.Save(inputPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        }

        var settings = new PixelAlignmentSettings
        {
            Columns = 2,
            Rows = 4,
            SamplingMode = PixelCellSamplingMode.CenterPixel,
            Correction = new PixelAlignmentCorrectionSettings
            {
                EnableRotationCorrection = true,
                RotationDegrees = 90
            }
        };
        var plan = Service.CreatePlan(4, 2, settings);
        Equal(2, plan.CorrectedBaseWidth, "4×2 顺时针 90° 后画布宽度应交换");
        Equal(4, plan.CorrectedBaseHeight, "4×2 顺时针 90° 后画布高度应交换");

        Service.AlignToPng(inputPath, outputPath, settings);
        using var output = SixLabors.ImageSharp.Image.Load<Rgba32>(outputPath);
        Equal(colors[4], output[0, 0], "非方形旋转后的左上像素");
        Equal(colors[0], output[1, 0], "非方形旋转后的右上像素");
        Equal(colors[5], output[0, 1], "非方形旋转第 2 行左像素");
        Equal(colors[1], output[1, 1], "非方形旋转第 2 行右像素");
        Equal(colors[6], output[0, 2], "非方形旋转第 3 行左像素");
        Equal(colors[2], output[1, 2], "非方形旋转第 3 行右像素");
        Equal(colors[7], output[0, 3], "非方形旋转后的左下像素");
        Equal(colors[3], output[1, 3], "非方形旋转后的右下像素");
    }

    private static void PerspectiveCorrectionRectifiesFourPointsAndSupportsTargetShear(string testRoot)
    {
        var inputPath = Path.Combine(testRoot, "perspective-input.png");
        var rectanglePath = Path.Combine(testRoot, "perspective-rectangle.png");
        var shearPath = Path.Combine(testRoot, "perspective-shear.png");
        var shearFinalPath = Path.Combine(testRoot, "perspective-shear-final.png");
        var green = new Rgba32(30, 200, 90, 255);
        using (var source = new Image<Rgba32>(6, 4, green))
        {
            source.Save(inputPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        }

        var trapezoid = new PixelPerspectiveQuadrilateral
        {
            TopLeft = new PixelCoordinate { X = 1, Y = 0 },
            TopRight = new PixelCoordinate { X = 5, Y = 0 },
            BottomRight = new PixelCoordinate { X = 6, Y = 4 },
            BottomLeft = new PixelCoordinate { X = 0, Y = 4 }
        };
        var rectangleSettings = new PixelAlignmentSettings
        {
            Columns = 6,
            Rows = 4,
            SamplingMode = PixelCellSamplingMode.CenterPixel,
            Correction = new PixelAlignmentCorrectionSettings
            {
                EnablePerspectiveCorrection = true,
                SourceQuadrilateral = trapezoid,
                TargetEdgeAngleDegrees = 90
            }
        };
        Service.AlignToPng(inputPath, rectanglePath, rectangleSettings);
        using (var rectangle = SixLabors.ImageSharp.Image.Load<Rgba32>(rectanglePath))
        {
            Equal(6, rectangle.Width, "90°透视矫正目标宽度");
            Equal(4, rectangle.Height, "90°透视矫正目标高度");
            AssertBlock(rectangle, 0, 0, 6, 4, green, "90°目标夹角应形成完整矩形");
        }

        var shearSettings = rectangleSettings with
        {
            Correction = rectangleSettings.Correction with { TargetEdgeAngleDegrees = 60 }
        };
        var shearResult = Service.ExportWorkingImageToPng(inputPath, shearPath, shearSettings);
        True(shearResult.IsCorrectionApplied, "60°目标夹角工作图必须应用透视矫正");
        Equal(8, shearResult.Plan?.CorrectedBaseWidth ?? 0,
            "60°目标平行四边形应扩展基础画布宽度避免裁切");
        Equal(4, shearResult.Plan?.CorrectedBaseHeight ?? 0,
            "60°目标平行四边形基础画布高度");
        using var shear = SixLabors.ImageSharp.Image.Load<Rgba32>(shearPath);
        Equal(24, shear.Width, "60°目标平行四边形工作图应使用网格兼容整数倍率");
        Equal(12, shear.Height, "60°目标平行四边形工作图高度应使用同一倍率");
        Equal((byte)0, shear[shear.Width - 1, 0].A, "60°目标平行四边形右上角应有透明画布余量");
        Equal(green, shear[shear.Width / 2, shear.Height / 2], "60°目标平行四边形中心应保留内容");
        Equal((byte)0, shear[0, shear.Height - 1].A,
            "60°目标平行四边形左下角应有透明画布余量");

        Service.AlignToPng(inputPath, shearFinalPath, shearSettings);
        using var shearFinal = SixLabors.ImageSharp.Image.Load<Rgba32>(shearFinalPath);
        Equal(6, shearFinal.Width, "透视矫正最终 PNG 仍须严格使用自定义列数");
        Equal(4, shearFinal.Height, "透视矫正最终 PNG 仍须严格使用自定义行数");
        True(Enumerable.Range(0, shearFinal.Width).All(x => shearFinal[x, shearFinal.Height - 1].A == 0),
            "输出网格比例不匹配时应等比适配并透明补边，不得非等比拉伸目标夹角");
    }

    private static void InvalidPerspectiveGeometryIsRejected()
    {
        Throws<ArgumentOutOfRangeException>(
            () => Service.CreatePlan(
                4,
                4,
                new PixelAlignmentSettings
                {
                    Correction = new PixelAlignmentCorrectionSettings
                    {
                        EnablePerspectiveCorrection = true,
                        TargetEdgeAngleDegrees = 0,
                        SourceQuadrilateral = FullImageQuadrilateral(4, 4)
                    }
                }),
            "退化的目标 A/B 夹角必须明确拒绝");
        Throws<ArgumentOutOfRangeException>(
            () => Service.CreatePlan(
                4,
                4,
                new PixelAlignmentSettings
                {
                    Correction = new PixelAlignmentCorrectionSettings
                    {
                        EnablePerspectiveCorrection = true,
                        SourceQuadrilateral = new PixelPerspectiveQuadrilateral
                        {
                            TopLeft = new PixelCoordinate { X = 0, Y = 0 },
                            TopRight = new PixelCoordinate { X = 5, Y = 0 },
                            BottomRight = new PixelCoordinate { X = 4, Y = 4 },
                            BottomLeft = new PixelCoordinate { X = 0, Y = 4 }
                        }
                    }
                }),
            "越过源图连续边界的顶点必须明确拒绝");
        Throws<ArgumentException>(
            () => Service.CreatePlan(
                4,
                4,
                new PixelAlignmentSettings
                {
                    Correction = new PixelAlignmentCorrectionSettings
                    {
                        EnablePerspectiveCorrection = true,
                        SourceQuadrilateral = new PixelPerspectiveQuadrilateral
                        {
                            TopLeft = new PixelCoordinate { X = 0, Y = 0 },
                            TopRight = new PixelCoordinate { X = 2, Y = 0 },
                            BottomRight = new PixelCoordinate { X = 4, Y = 0 },
                            BottomLeft = new PixelCoordinate { X = 0, Y = 4 }
                        }
                    }
                }),
            "共线或退化的四顶点必须明确拒绝");
    }

    private static PixelPerspectiveQuadrilateral FullImageQuadrilateral(int width, int height) => new()
    {
        TopLeft = new PixelCoordinate { X = 0, Y = 0 },
        TopRight = new PixelCoordinate { X = width, Y = 0 },
        BottomRight = new PixelCoordinate { X = width, Y = height },
        BottomLeft = new PixelCoordinate { X = 0, Y = height }
    };

    private static void CropRulersAndDominantSamplingProduceTheLogicalGrid(string testRoot)
    {
        var inputPath = Path.Combine(testRoot, "dominant-input.png");
        var outputPath = Path.Combine(testRoot, "dominant-output.png");
        var border = new Rgba32(255, 0, 255, 255);
        var red = new Rgba32(220, 30, 30, 255);
        var blue = new Rgba32(20, 70, 230, 255);
        var green = new Rgba32(20, 210, 80, 255);
        var yellow = new Rgba32(240, 210, 20, 255);
        using (var source = new Image<Rgba32>(6, 4, border))
        {
            source[1, 1] = red;
            source[2, 1] = red;
            source[1, 2] = red;
            source[2, 2] = blue;
            source[3, 1] = green;
            source[4, 1] = green;
            source[3, 2] = green;
            source[4, 2] = yellow;
            source.Save(inputPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        }

        var result = Service.AlignToPng(
            inputPath,
            outputPath,
            new PixelAlignmentSettings
            {
                CropBounds = new PixelCropBounds { Left = 1, Top = 1, Right = 5, Bottom = 3 },
                Columns = 2,
                Rows = 1,
                MinimumUpscaleFactor = 5,
                SamplingMode = PixelCellSamplingMode.DominantColor
            });

        Equal(2, result.OutputWidth, "逻辑网格输出宽度");
        Equal(1, result.OutputHeight, "逻辑网格输出高度");
        Equal(5, result.Plan.WorkingScale, "已经可整除时保留用户整数倍率");
        using var output = SixLabors.ImageSharp.Image.Load<Rgba32>(outputPath);
        Equal(red, output[0, 0], "左格应采用覆盖面积最大的红色");
        Equal(green, output[1, 0], "右格应采用覆盖面积最大的绿色");
        False(output[0, 0] == border || output[1, 0] == border, "标尺外边框不得参与采样");
    }

    private static void GridResolvedScaleAboveTheRequestedLimitCanBeExported(string testRoot)
    {
        var inputPath = Path.Combine(testRoot, "resolved-scale-input.png");
        var outputPath = Path.Combine(testRoot, "resolved-scale-output.png");
        using (var source = new Image<Rgba32>(1, 1, new Rgba32(80, 140, 220, 255)))
        {
            source.Save(inputPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        }

        var plan = Service.CreatePlan(
            1,
            1,
            new PixelAlignmentSettings
            {
                Columns = 33,
                Rows = 32,
                MinimumUpscaleFactor = 1
            });
        Equal(1056, plan.WorkingScale, "互质网格可把自动兼容倍率提升到请求倍率上限之外");
        True(plan.WorkingScale > PixelAlignmentLimits.MaximumUpscaleFactor,
            "回归样例必须覆盖已解析倍率大于用户请求倍率上限的情况");

        var result = Service.UpscaleNearestNeighborToPng(
            inputPath,
            outputPath,
            plan.WorkingScale);
        Equal(1056, result.Width, "自动兼容倍率对应的最近邻工作图宽度");
        Equal(1056, result.Height, "自动兼容倍率对应的最近邻工作图高度");
    }

    private static void LargeMutuallyPrimeCustomGridUsesVirtualWorkingCoordinates(string testRoot)
    {
        var inputPath = Path.Combine(testRoot, "large-custom-grid-input.png");
        var outputPath = Path.Combine(testRoot, "large-custom-grid-output.png");
        var workingPath = Path.Combine(testRoot, "large-custom-grid-working.png");
        using (var source = new Image<Rgba32>(50, 50, new Rgba32(40, 120, 220, 255)))
        {
            source.Save(inputPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        }

        var settings = new PixelAlignmentSettings
        {
            Columns = 1024,
            Rows = 513,
            SamplingMode = PixelCellSamplingMode.CenterPixel,
            Correction = new PixelAlignmentCorrectionSettings
            {
                EnableAspectRatioCorrection = true,
                TargetAspectRatioA = 1,
                TargetAspectRatioB = 1
            }
        };
        var plan = Service.CreatePlan(50, 50, settings);
        Equal(262656, plan.WorkingScale, "50×50 到 1024×513 的精确兼容倍率");
        Equal(13_132_800L, plan.WorkingWidth, "大互质网格应保留精确虚拟工作宽度");
        Equal(13_132_800L, plan.WorkingHeight, "大互质网格应保留精确虚拟工作高度");
        True(plan.WorkingWidth > PixelAlignmentLimits.MaximumWorkingDimension,
            "回归样例必须覆盖不可物化但可虚拟采样的工作坐标");

        Service.AlignToPng(inputPath, outputPath, settings);
        using (var output = SixLabors.ImageSharp.Image.Load<Rgba32>(outputPath))
        {
            Equal(1024, output.Width, "互质自定义列数必须真实成为最终 PNG 宽度");
            Equal(513, output.Height, "互质自定义行数必须真实成为最终 PNG 高度");
        }

        Throws<ArgumentOutOfRangeException>(
            () => Service.ExportWorkingImageToPng(inputPath, workingPath, settings),
            "超过安全画幅的虚拟工作坐标不得物化为巨型中间图");
        False(File.Exists(workingPath), "拒绝巨型中间图后不得残留文件");
    }

    private static void AverageSamplingUsesAlphaAwareColor(string testRoot)
    {
        var inputPath = Path.Combine(testRoot, "average-input.png");
        var outputPath = Path.Combine(testRoot, "average-output.png");
        using (var source = new Image<Rgba32>(2, 1))
        {
            source[0, 0] = new Rgba32(200, 20, 10, 255);
            source[1, 0] = new Rgba32(10, 250, 240, 0);
            source.Save(inputPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        }

        Service.AlignToPng(
            inputPath,
            outputPath,
            new PixelAlignmentSettings
            {
                Columns = 1,
                Rows = 1,
                SamplingMode = PixelCellSamplingMode.AverageColor
            });

        using var output = SixLabors.ImageSharp.Image.Load<Rgba32>(outputPath);
        Equal((byte)200, output[0, 0].R, "全透明像素的隐藏 RGB 不应污染平均红色");
        Equal((byte)20, output[0, 0].G, "全透明像素的隐藏 RGB 不应污染平均绿色");
        Equal((byte)10, output[0, 0].B, "全透明像素的隐藏 RGB 不应污染平均蓝色");
        Equal((byte)128, output[0, 0].A, "平均采样应保留覆盖 Alpha");
    }

    private static void DominantSamplingRejectsUnboundedHighEntropyCells(string testRoot)
    {
        const int side = 513;
        var inputPath = Path.Combine(testRoot, "high-entropy-input.png");
        var outputPath = Path.Combine(testRoot, "high-entropy-output.png");
        using (var source = new Image<Rgba32>(side, side))
        {
            var index = 0;
            for (var y = 0; y < side; y++)
            {
                for (var x = 0; x < side; x++, index++)
                {
                    source[x, y] = new Rgba32(
                        (byte)(index >> 16),
                        (byte)(index >> 8),
                        (byte)index,
                        255);
                }
            }

            source.Save(inputPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        }

        Throws<InvalidOperationException>(
            () => Service.AlignToPng(
                inputPath,
                outputPath,
                new PixelAlignmentSettings
                {
                    Columns = 1,
                    Rows = 1,
                    SamplingMode = PixelCellSamplingMode.DominantColor
                }),
            "高熵大单格不得让主色 Dictionary 无界增长");
        False(File.Exists(outputPath), "主色内存保护触发后不得留下半成品");
    }

    private static void JpegInputCanBeInspectedAndExportedAsPng(string testRoot)
    {
        var inputPath = Path.Combine(testRoot, "jpeg-input.jpg");
        var outputPath = Path.Combine(testRoot, "jpeg-output.png");
        using (var source = new Image<Rgba32>(3, 2, new Rgba32(40, 100, 190, 255)))
        {
            source[2, 1] = new Rgba32(230, 160, 30, 255);
            source.Save(inputPath, new JpegEncoder { Quality = 100 });
        }

        var info = Service.Inspect(inputPath);
        Equal(3, info.Width, "JPEG 检查宽度");
        Equal(2, info.Height, "JPEG 检查高度");
        True(info.FormatName.Contains("JPEG", StringComparison.OrdinalIgnoreCase), "应识别 JPEG 解码格式");

        Service.AlignToPng(
            inputPath,
            outputPath,
            new PixelAlignmentSettings
            {
                Columns = 3,
                Rows = 2,
                SamplingMode = PixelCellSamplingMode.CenterPixel
            });
        using var output = SixLabors.ImageSharp.Image.Load<Rgba32>(outputPath);
        Equal(3, output.Width, "JPEG 转 PNG 后宽度");
        Equal(2, output.Height, "JPEG 转 PNG 后高度");
    }

    private static void ExistingOutputsAreProtectedAndTemporaryFilesAreRemoved(string testRoot)
    {
        var atomicRoot = Path.Combine(testRoot, "atomic");
        Directory.CreateDirectory(atomicRoot);
        var inputPath = Path.Combine(atomicRoot, "input.png");
        var outputPath = Path.Combine(atomicRoot, "output.png");
        using (var source = new Image<Rgba32>(2, 2, new Rgba32(20, 40, 60, 255)))
        {
            source.Save(inputPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        }

        File.WriteAllText(outputPath, "keep-existing-output");
        Throws<IOException>(
            () => Service.AlignToPng(
                inputPath,
                outputPath,
                new PixelAlignmentSettings { Columns = 1, Rows = 1 }),
            "未授权覆盖时应保护已有文件");
        Equal("keep-existing-output", File.ReadAllText(outputPath), "拒绝覆盖后原文件内容");

        Service.AlignToPng(
            inputPath,
            outputPath,
            new PixelAlignmentSettings { Columns = 1, Rows = 1 },
            overwrite: true);
        using var output = SixLabors.ImageSharp.Image.Load<Rgba32>(outputPath);
        Equal(1, output.Width, "覆盖后的有效 PNG 宽度");
        False(
            Directory.EnumerateFiles(atomicRoot, $".{Path.GetFileName(outputPath)}.*.tmp").Any(),
            "原子保存后不得残留临时文件");
    }

    private static void ResourceLimitsRejectUnsafeGridAndWorkingSizes(string testRoot)
    {
        Throws<ArgumentOutOfRangeException>(
            () => Service.CreatePlan(
                50,
                50,
                new PixelAlignmentSettings
                {
                    Columns = PixelAlignmentLimits.MaximumGridAxis + 1,
                    Rows = 32
                }),
            "网格列数必须有明确硬上限");
        Throws<ArgumentOutOfRangeException>(
            () => Service.CreatePlan(
                50,
                50,
                new PixelAlignmentSettings
                {
                    Columns = 32,
                    Rows = PixelAlignmentLimits.MaximumGridAxis + 1
                }),
            "网格行数必须有明确硬上限");
        Throws<ArgumentOutOfRangeException>(
            () => Service.CreatePlan(
                50,
                50,
                new PixelAlignmentSettings
                {
                    Columns = 2049,
                    Rows = 2049
                }),
            "单轴合法时仍必须限制逻辑输出像素总量");
        Throws<ArgumentOutOfRangeException>(
            () => Service.CreatePlan(
                50,
                50,
                new PixelAlignmentSettings
                {
                    Columns = 32,
                    Rows = 32,
                    MinimumUpscaleFactor = PixelAlignmentLimits.MaximumUpscaleFactor + 1
                }),
            "用户请求倍率必须有明确硬上限");
        Throws<ArgumentOutOfRangeException>(
            () => Service.CreatePlan(
                PixelAlignmentLimits.MaximumWorkingDimension + 1,
                1,
                new PixelAlignmentSettings { Columns = 1, Rows = 1 }),
            "工作画幅单边必须有明确硬上限");
        Throws<ArgumentOutOfRangeException>(
            () => Service.CreatePlan(
                8193,
                8193,
                new PixelAlignmentSettings { Columns = 1, Rows = 1 }),
            "工作画幅像素总量必须有明确硬上限");
        var inputPath = Path.Combine(testRoot, "unsafe-preview-input.png");
        var outputPath = Path.Combine(testRoot, "unsafe-preview-output.png");
        using (var source = new Image<Rgba32>(32, 32, new Rgba32(20, 40, 60, 255)))
        {
            source.Save(inputPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        }

        Throws<ArgumentOutOfRangeException>(
            () => Service.UpscaleNearestNeighborToPng(
                inputPath,
                outputPath,
                PixelAlignmentLimits.MaximumUpscaleFactor + 1),
            "独立最近邻预览倍率同样必须受硬上限保护");
        False(File.Exists(outputPath), "资源上限拒绝后不得创建输出文件");
    }

    private static void ShellLoadsAndPersistsToolPreferencesWithoutCropBounds(string testRoot)
    {
        var settingsRoot = Path.Combine(testRoot, "settings");
        var inputDirectory = Path.Combine(settingsRoot, "inputs");
        var outputDirectory = Path.Combine(settingsRoot, "outputs");
        Directory.CreateDirectory(inputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var paths = new WorkspacePaths(settingsRoot);
        var settingsService = new UserSettingsService(paths);
        settingsService.Save(new UserSettingsModel
        {
            HasCompletedWorkflowSelection = true,
            PixelAlignmentMinimumUpscaleFactor = 7,
            PixelAlignmentColumns = 16,
            PixelAlignmentRows = 64,
            PixelAlignmentSamplingMode = PixelCellSamplingMode.CenterPixel.ToString(),
            PixelAlignmentShowGrid = false,
            PixelAlignmentLastInputDirectory = inputDirectory,
            PixelAlignmentLastOutputDirectory = outputDirectory
        });
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), settingsService, editor);

        try
        {
            var loaded = shell.PixelAlignment.CaptureSettings();
            Equal(7, loaded.MinimumUpscaleFactor, "Shell 加载最低放大倍率");
            Equal(16, loaded.Columns, "Shell 加载网格列数");
            Equal(64, loaded.Rows, "Shell 加载网格行数");
            Equal(PixelCellSamplingMode.CenterPixel.ToString(), loaded.SamplingMode,
                "Shell 加载采样方式");
            False(loaded.ShowGrid, "Shell 加载网格显示开关");
            Equal(inputDirectory, loaded.LastInputDirectory, "Shell 加载最近输入目录");
            Equal(outputDirectory, loaded.LastOutputDirectory, "Shell 加载最近输出目录");

            shell.PixelAlignment.MinimumUpscaleFactor = 12;
            shell.PixelAlignment.Columns = 48;
            shell.PixelAlignment.Rows = 24;
            shell.PixelAlignment.SelectedSamplingMode = shell.PixelAlignment.SamplingModeOptions.Single(
                option => option.Mode == PixelCellSamplingMode.AverageColor);
            shell.PixelAlignment.ShowGrid = true;

            var persisted = settingsService.Load();
            Equal(12, persisted.PixelAlignmentMinimumUpscaleFactor, "倍率变化应即时持久化");
            Equal(48, persisted.PixelAlignmentColumns, "网格列变化应即时持久化");
            Equal(24, persisted.PixelAlignmentRows, "网格行变化应即时持久化");
            Equal(PixelCellSamplingMode.AverageColor.ToString(), persisted.PixelAlignmentSamplingMode,
                "采样方式变化应即时持久化");
            True(persisted.PixelAlignmentShowGrid, "网格显示变化应即时持久化");
            Equal(inputDirectory, persisted.PixelAlignmentLastInputDirectory,
                "其他设置变化不应丢失最近输入目录");
            Equal(outputDirectory, persisted.PixelAlignmentLastOutputDirectory,
                "其他设置变化不应丢失最近输出目录");

            var serialized = File.ReadAllText(paths.UserSettingsPath);
            False(serialized.Contains("pixelAlignmentCrop", StringComparison.OrdinalIgnoreCase),
                "用户设置不得跨图片持久化裁切边界");
        }
        finally
        {
            shell.Shutdown();
        }
    }

    private static void ViewModelBuildsTheCompatiblePlanAndWritesTheConfiguredOutput(string testRoot)
    {
        var toolRoot = Path.Combine(testRoot, "view-model");
        Directory.CreateDirectory(toolRoot);
        var inputPath = Path.Combine(toolRoot, "source-50x50.png");
        var outputPath = Path.Combine(toolRoot, "aligned-32x32.png");
        var workingPath = Path.Combine(toolRoot, "working-800x800.png");
        using (var source = new Image<Rgba32>(50, 50, new Rgba32(20, 40, 60, 255)))
        {
            for (var y = 0; y < source.Height; y++)
            {
                for (var x = source.Width / 2; x < source.Width; x++)
                {
                    source[x, y] = new Rgba32(220, 120, 30, 255);
                }
            }

            source.Save(inputPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        }

        var viewModel = new PixelAlignmentViewModel(
            Service,
            new FixedInputPicker(inputPath),
            new QueueOutputPicker(outputPath, workingPath));

        viewModel.ChooseInputCommand.Execute(null);
        Equal(PixelCellSamplingMode.CenterPixel, viewModel.SelectedSamplingMode.Mode,
            "未配置采样方式时 ViewModel 应优先选择中心像素");
        Equal(50, viewModel.SourceWidth, "ViewModel 应读取源图宽度");
        Equal(50, viewModel.SourceHeight, "ViewModel 应读取源图高度");
        Equal(0, viewModel.CropLeft, "新图片左标尺应归零");
        Equal(0, viewModel.CropTop, "新图片上标尺应归零");
        Equal(50, viewModel.CropRight, "新图片右标尺应落在排他边界");
        Equal(50, viewModel.CropBottom, "新图片下标尺应落在排他边界");
        Equal(0d, viewModel.PerspectiveTopLeftX, "新图片 TL X 应重置到裁切左边界");
        Equal(0d, viewModel.PerspectiveTopLeftY, "新图片 TL Y 应重置到裁切上边界");
        Equal(50d, viewModel.PerspectiveTopRightX, "新图片 TR X 应使用连续右边界");
        Equal(50d, viewModel.PerspectiveBottomRightY, "新图片 BR Y 应使用连续下边界");
        viewModel.CropLeft = 2;
        Equal(2d, viewModel.PerspectiveTopLeftX, "默认透视四点应自动跟随裁切左边界");
        Equal(2d, viewModel.PerspectiveBottomLeftX, "默认 BL 应自动跟随裁切左边界");
        viewModel.PerspectiveTopLeftX = 0d;
        viewModel.EnablePerspectiveCorrection = true;
        Equal(2d, viewModel.PerspectiveTopLeftX,
            "启用透视时，超出当前裁切范围的四点应自动重置到裁切角点");
        viewModel.EnablePerspectiveCorrection = false;
        viewModel.ResetCropCommand.Execute(null);
        Equal(0d, viewModel.PerspectiveTopLeftX, "恢复全图时默认四点应继续跟随裁切边界");
        Equal("source-50x50.png", viewModel.InputFileName, "读取尺寸后输入文件名状态必须同步刷新");
        Equal(16, viewModel.CurrentPlan?.WorkingScale ?? 0, "50×50 / 32×32 应自动提升到 16 倍");
        Equal(800, viewModel.CurrentPlan?.WorkingWidth ?? 0, "兼容工作图宽度");
        Equal(800, viewModel.CurrentPlan?.WorkingHeight ?? 0, "兼容工作图高度");
        Equal(25, viewModel.CurrentPlan?.CellWidth ?? 0, "每格工作像素宽度");
        True(viewModel.WorkingScaleText.Contains("10× → 16×", StringComparison.Ordinal),
            "界面应明确解释请求倍率被提升为兼容倍率");

        viewModel.EnablePerspectiveCorrection = true;
        viewModel.PerspectiveTopLeftX = 1d;
        True(viewModel.CurrentPlan is not null, "修改合法透视顶点后计划应即时更新");
        viewModel.ResetPerspectivePointsCommand.Execute(null);
        Equal(0d, viewModel.PerspectiveTopLeftX, "重置四点命令应恢复当前裁切左边界");
        viewModel.EnablePerspectiveCorrection = false;

        viewModel.Columns = 1024;
        viewModel.Rows = 513;
        Equal(1024, viewModel.Columns, "自定义互质列数不得被预设覆盖");
        Equal(513, viewModel.Rows, "自定义互质行数不得被预设覆盖");
        True(viewModel.CurrentPlan is not null, "大互质行列应形成虚拟采样计划");
        False(viewModel.CanExportWorkingImage, "不可安全物化的巨型工作图应禁用中间图导出");
        viewModel.EnableAspectRatioCorrection = true;
        viewModel.TargetAspectRatioA = 1d;
        viewModel.TargetAspectRatioB = 1d;
        True(viewModel.IsGeometryFitPaddingRequired,
            "正方形矫正画布输出到 1024×513 时应启用等比透明补边");
        True(viewModel.GeometryFitStatusText.Contains("透明补边", StringComparison.Ordinal),
            "界面应明确说明补边用于保持角度和夹角");
        viewModel.EnableAspectRatioCorrection = false;
        viewModel.Columns = 32;
        viewModel.Rows = 32;

        viewModel.ChooseOutputCommand.Execute(null);
        True(viewModel.CanProcess, "输入、计划和输出路径就绪后应允许生成");
        viewModel.ProcessCommand.Execute(null);
        WaitUntil(
            () => !viewModel.IsBusy && File.Exists(outputPath),
            TimeSpan.FromSeconds(5),
            "像素对齐 ViewModel 未在超时前完成输出");

        using var output = SixLabors.ImageSharp.Image.Load<Rgba32>(outputPath);
        Equal(32, output.Width, "ViewModel 最终输出宽度应等于网格列数");
        Equal(32, output.Height, "ViewModel 最终输出高度应等于网格行数");
        Equal(Path.GetFullPath(outputPath), viewModel.OutputPreviewPath, "生成结果应立即成为右侧预览源");
        Equal("对齐结果预览", viewModel.PreviewTitle, "采样结果预览标题");
        Equal("32 × 32 px", viewModel.PreviewSizeText, "采样结果预览尺寸");
        True(viewModel.StatusText.StartsWith("像素对齐完成：", StringComparison.Ordinal),
            "成功后应回传明确状态");

        viewModel.ExportWorkingImageCommand.Execute(null);
        WaitUntil(
            () => !viewModel.IsBusy && File.Exists(workingPath),
            TimeSpan.FromSeconds(5),
            "最近邻工作图未在超时前完成输出");
        Equal(Path.GetFullPath(workingPath), viewModel.OutputPreviewPath, "工作图应成为当前预览文件");
        Equal("最近邻工作图预览", viewModel.PreviewTitle, "工作图预览标题不得仍显示对齐结果");
        Equal("800 × 800 px", viewModel.PreviewSizeText, "工作图预览尺寸不得仍显示 32×32");
    }

    private static void ViewModelKeepsIndependentGridAndSafelySynchronizesAspectRatio(string testRoot)
    {
        var viewModel = new PixelAlignmentViewModel(
            Service,
            new FixedInputPicker(Path.Combine(testRoot, "unused-grid-input.png")),
            new FixedOutputPicker(Path.Combine(testRoot, "unused-grid-output.png")));

        viewModel.Columns = 7;
        viewModel.Rows = 3;
        viewModel.SetAspectRatioPresetCommand.Execute("1:1");
        Equal(7, viewModel.Columns, "比例预设不得隐式覆盖自定义列数");
        Equal(3, viewModel.Rows, "比例预设不得隐式覆盖自定义行数");
        True(viewModel.IsAspectGridMismatch, "自定义 7×3 与 1:1 不一致时应明确提示");

        viewModel.Columns = 4096;
        viewModel.Rows = 1;
        viewModel.SyncGridToAspectRatioCommand.Execute(null);
        Equal(2048, viewModel.Columns, "1:1 同步应在总像素上限内选择最大安全列数");
        Equal(2048, viewModel.Rows, "1:1 同步应在总像素上限内选择最大安全行数");
        True((long)viewModel.Columns * viewModel.Rows <= PixelAlignmentLimits.MaximumGridPixelCount,
            "比例同步不得产生超过逻辑输出总像素上限的网格");

        viewModel.EnablePerspectiveCorrection = true;
        viewModel.EnableAspectRatioCorrection = true;
        viewModel.EnableRotationCorrection = true;
        var perspectiveIndex = viewModel.CorrectionStepsText.IndexOf("四点透视", StringComparison.Ordinal);
        var aspectIndex = viewModel.CorrectionStepsText.IndexOf("比例", StringComparison.Ordinal);
        var rotationIndex = viewModel.CorrectionStepsText.IndexOf("顺时针旋转", StringComparison.Ordinal);
        True(perspectiveIndex >= 0 && perspectiveIndex < aspectIndex && aspectIndex < rotationIndex,
            "界面矫正摘要必须按透视、比例、旋转的真实执行顺序显示");
    }

    private static void ViewBindsCommandsParametersAndTheInteractiveSurface(string testRoot)
    {
        var interactiveInput = Path.Combine(testRoot, "interactive-perspective-12x8.png");
        using (var source = new Image<Rgba32>(12, 8, new Rgba32(40, 120, 220, 255)))
        {
            source.Save(interactiveInput, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        }

        var viewModel = new PixelAlignmentViewModel(
            Service,
            new FixedInputPicker(interactiveInput),
            new FixedOutputPicker(Path.Combine(testRoot, "unused-output.png")));
        viewModel.ChooseInputCommand.Execute(null);
        var view = new PixelAlignmentView { DataContext = viewModel };
        view.Measure(new System.Windows.Size(1280, 760));
        view.Arrange(new Rect(0, 0, 1280, 760));
        view.UpdateLayout();
        PumpDispatcher();

        var chooseInput = RequireElement<Button>(view, "ChoosePixelAlignmentInputButton");
        var chooseOutput = RequireElement<Button>(view, "ChoosePixelAlignmentOutputButton");
        var process = RequireElement<Button>(view, "ProcessPixelAlignmentButton");
        var exportWorking = RequireElement<Button>(view, "ExportPixelAlignmentWorkingButton");
        var surface = RequireElement<PixelAlignmentSurface>(view, "PixelAlignmentSurface");
        var outputPreview = RequireElement<System.Windows.Controls.Image>(view, "PixelAlignmentOutputPreview");
        var status = RequireElement<TextBlock>(view, "PixelAlignmentStatusText");
        var columns = RequireElement<TextBox>(view, "PixelAlignmentColumnsTextBox");
        var rows = RequireElement<TextBox>(view, "PixelAlignmentRowsTextBox");
        var sampling = RequireElement<ComboBox>(view, "PixelAlignmentSamplingModeComboBox");
        var syncAspect = RequireElement<Button>(view, "PixelAlignmentSyncGridAspectButton");
        var rotation = RequireElement<TextBox>(view, "PixelAlignmentRotationDegreesTextBox");
        var targetAngle = RequireElement<TextBox>(view, "PixelAlignmentTargetEdgeAngleTextBox");

        Equal("ChooseInputCommand", RequireBindingPath(chooseInput, ButtonBase.CommandProperty, "导入按钮"),
            "导入按钮命令绑定");
        Equal("ChooseOutputCommand", RequireBindingPath(chooseOutput, ButtonBase.CommandProperty, "输出按钮"),
            "输出按钮命令绑定");
        Equal("ProcessCommand", RequireBindingPath(process, ButtonBase.CommandProperty, "生成按钮"),
            "生成按钮命令绑定");
        Equal("ExportWorkingImageCommand", RequireBindingPath(exportWorking, ButtonBase.CommandProperty, "工作图按钮"),
            "最近邻工作图命令绑定");
        Equal("Columns", RequireBindingPath(columns, TextBox.TextProperty, "自定义列数"),
            "自定义列数绑定");
        Equal("Rows", RequireBindingPath(rows, TextBox.TextProperty, "自定义行数"),
            "自定义行数绑定");
        Equal("SelectedSamplingMode", RequireBindingPath(sampling, Selector.SelectedItemProperty, "采样方式"),
            "采样方式绑定");
        Equal("SyncGridToAspectRatioCommand",
            RequireBindingPath(syncAspect, ButtonBase.CommandProperty, "比例同步按钮"),
            "比例同步命令绑定");
        Equal("RotationDegrees", RequireBindingPath(rotation, TextBox.TextProperty, "旋转角度"),
            "旋转角度绑定");
        Equal("TargetEdgeAngleDegrees", RequireBindingPath(targetAngle, TextBox.TextProperty, "目标夹角"),
            "目标夹角绑定");
        Equal("InputPath", RequireBindingPath(surface, PixelAlignmentSurface.SourcePathProperty, "画布源图"),
            "画布源图绑定");
        Equal("CropLeft", RequireBindingPath(surface, PixelAlignmentSurface.CropLeftProperty, "左标尺"),
            "左标尺双向绑定");
        Equal("CropTop", RequireBindingPath(surface, PixelAlignmentSurface.CropTopProperty, "上标尺"),
            "上标尺双向绑定");
        Equal("CropRight", RequireBindingPath(surface, PixelAlignmentSurface.CropRightProperty, "右标尺"),
            "右标尺双向绑定");
        Equal("CropBottom", RequireBindingPath(surface, PixelAlignmentSurface.CropBottomProperty, "下标尺"),
            "下标尺双向绑定");
        Equal("PerspectiveTopLeftX",
            RequireBindingPath(surface, PixelAlignmentSurface.PerspectiveTopLeftXProperty, "透视 TL X"),
            "透视 TL X 双向绑定");
        Equal("PerspectiveTopLeftY",
            RequireBindingPath(surface, PixelAlignmentSurface.PerspectiveTopLeftYProperty, "透视 TL Y"),
            "透视 TL Y 双向绑定");
        Equal("PerspectiveTopRightX",
            RequireBindingPath(surface, PixelAlignmentSurface.PerspectiveTopRightXProperty, "透视 TR X"),
            "透视 TR X 双向绑定");
        Equal("PerspectiveTopRightY",
            RequireBindingPath(surface, PixelAlignmentSurface.PerspectiveTopRightYProperty, "透视 TR Y"),
            "透视 TR Y 双向绑定");
        Equal("PerspectiveBottomRightX",
            RequireBindingPath(surface, PixelAlignmentSurface.PerspectiveBottomRightXProperty, "透视 BR X"),
            "透视 BR X 双向绑定");
        Equal("PerspectiveBottomRightY",
            RequireBindingPath(surface, PixelAlignmentSurface.PerspectiveBottomRightYProperty, "透视 BR Y"),
            "透视 BR Y 双向绑定");
        Equal("PerspectiveBottomLeftX",
            RequireBindingPath(surface, PixelAlignmentSurface.PerspectiveBottomLeftXProperty, "透视 BL X"),
            "透视 BL X 双向绑定");
        Equal("PerspectiveBottomLeftY",
            RequireBindingPath(surface, PixelAlignmentSurface.PerspectiveBottomLeftYProperty, "透视 BL Y"),
            "透视 BL Y 双向绑定");
        Equal("OutputPreviewPath", RequireBindingPath(outputPreview, System.Windows.Controls.Image.SourceProperty, "输出预览"),
            "输出预览绑定");
        Equal("StatusText", RequireBindingPath(status, TextBlock.TextProperty, "状态文本"),
            "状态文本绑定");
        True(PixelAlignmentSurface.CropLeftProperty.GetMetadata(typeof(PixelAlignmentSurface)) is FrameworkPropertyMetadata
             { BindsTwoWayByDefault: true },
            "可拖动裁切依赖属性必须默认双向绑定");
        var perspectiveProperties = new[]
        {
            PixelAlignmentSurface.PerspectiveTopLeftXProperty,
            PixelAlignmentSurface.PerspectiveTopLeftYProperty,
            PixelAlignmentSurface.PerspectiveTopRightXProperty,
            PixelAlignmentSurface.PerspectiveTopRightYProperty,
            PixelAlignmentSurface.PerspectiveBottomRightXProperty,
            PixelAlignmentSurface.PerspectiveBottomRightYProperty,
            PixelAlignmentSurface.PerspectiveBottomLeftXProperty,
            PixelAlignmentSurface.PerspectiveBottomLeftYProperty
        };
        True(perspectiveProperties.All(property =>
                property.GetMetadata(typeof(PixelAlignmentSurface)) is FrameworkPropertyMetadata
                { BindsTwoWayByDefault: true }),
            "八个可拖透视坐标依赖属性必须全部默认双向绑定");

        viewModel.EnablePerspectiveCorrection = true;
        surface.SetCurrentValue(PixelAlignmentSurface.PerspectiveTopLeftXProperty, 1d);
        surface.SetCurrentValue(PixelAlignmentSurface.PerspectiveTopLeftYProperty, 1d);
        PumpDispatcher();
        Equal(1d, viewModel.PerspectiveTopLeftX, "Surface 拖点式 SetCurrentValue 应回写 VM 的 TL X");
        Equal(1d, viewModel.PerspectiveTopLeftY, "Surface 拖点式 SetCurrentValue 应回写 VM 的 TL Y");
        True(viewModel.CurrentPlan is not null, "Surface 回写合法四点后应即时刷新矫正计划");

        var previewFixture = Path.Combine(testRoot, "native-preview-2x1.png");
        using (var source = new Image<Rgba32>(2, 1))
        {
            source[0, 0] = new Rgba32(255, 0, 0, 255);
            source[1, 0] = new Rgba32(0, 255, 0, 255);
            source.Save(previewFixture, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        }

        var decodedPreview = new FilePathToImageSourceConverter().Convert(
            previewFixture,
            typeof(System.Windows.Media.ImageSource),
            "1200",
            System.Globalization.CultureInfo.InvariantCulture) as System.Windows.Media.Imaging.BitmapSource
            ?? throw new InvalidOperationException("像素预览转换器未返回 BitmapSource。");
        Equal(2, decodedPreview.PixelWidth, "预览上限大于原图时不得在解码阶段放大小图");
        Equal(1, decodedPreview.PixelHeight, "像素预览必须保持源图纵向尺寸");

        var webpFixture = Path.Combine(testRoot, "surface-preview-2x1.webp");
        using (var source = new Image<Rgba32>(2, 1, new Rgba32(40, 120, 220, 255)))
        {
            source.Save(webpFixture);
        }

        var webpSurface = new PixelAlignmentSurface { SourcePath = webpFixture };
        Equal(2, webpSurface.LoadedSourcePixelWidth, "Surface 应通过 WIC 或 ImageSharp 兜底读取 WebP");
        Equal(1, webpSurface.LoadedSourcePixelHeight, "WebP 预览高度");

        view.DataContext = null;
    }

    private static void AssertBlock(
        Image<Rgba32> image,
        int left,
        int top,
        int width,
        int height,
        Rgba32 expected,
        string message)
    {
        for (var y = top; y < top + height; y++)
        {
            for (var x = left; x < left + width; x++)
            {
                Equal(expected, image[x, y], $"{message} ({x},{y})");
            }
        }
    }

    private static void Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool condition, string message)
    {
        if (condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }

    private static T RequireElement<T>(FrameworkElement owner, string name)
        where T : FrameworkElement =>
        owner.FindName(name) as T ??
        throw new InvalidOperationException($"像素点对齐 View 缺少 {name}。");

    private static string RequireBindingPath(
        DependencyObject target,
        DependencyProperty property,
        string description)
    {
        var expression = BindingOperations.GetBindingExpression(target, property)
            ?? throw new InvalidOperationException($"{description}缺少 BindingExpression。");
        return expression.ParentBinding.Path?.Path ?? string.Empty;
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string message)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            PumpDispatcher();
            if (condition())
            {
                return;
            }

            System.Threading.Thread.Sleep(10);
        }

        if (!condition())
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new DispatcherOperationCallback(_ =>
            {
                frame.Continue = false;
                return null;
            }),
            null);
        Dispatcher.PushFrame(frame);
    }

    private sealed class FixedInputPicker(string inputPath) : IPixelAlignmentInputPicker
    {
        public string? SelectInputFile(string? initialDirectory) => inputPath;
    }

    private sealed class FixedOutputPicker(string outputPath) : IPixelAlignmentOutputPicker
    {
        public string? SelectOutputFile(string? initialDirectory, string suggestedFileName, string title) => outputPath;
    }

    private sealed class QueueOutputPicker(params string[] outputPaths) : IPixelAlignmentOutputPicker
    {
        private readonly Queue<string> _outputPaths = new(outputPaths);

        public string? SelectOutputFile(string? initialDirectory, string suggestedFileName, string title) =>
            _outputPaths.Count > 0 ? _outputPaths.Dequeue() : null;
    }
}

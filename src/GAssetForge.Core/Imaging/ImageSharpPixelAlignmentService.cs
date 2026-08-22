using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace GAssetForge.Core.Imaging;

public sealed class ImageSharpPixelAlignmentService : IPixelAlignmentService
{
    public PixelAlignmentImageInfo Inspect(string inputPath)
    {
        var normalizedInput = NormalizeExistingInput(inputPath);
        var image = Image.Identify(normalizedInput)
            ?? throw new InvalidOperationException("无法识别像素对齐输入图片。");
        ValidateSourceSize(image.Width, image.Height, nameof(inputPath));
        return new PixelAlignmentImageInfo
        {
            InputPath = normalizedInput,
            Width = image.Width,
            Height = image.Height,
            FormatName = image.Metadata.DecodedImageFormat?.Name ?? string.Empty
        };
    }

    public PixelAlignmentPlan CreatePlan(string inputPath, PixelAlignmentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalizedInput = NormalizeExistingInput(inputPath);
        var image = Image.Identify(normalizedInput)
            ?? throw new InvalidOperationException("无法识别像素对齐输入图片。");
        return CreatePlan(image.Width, image.Height, settings);
    }

    public PixelAlignmentPlan CreatePlan(
        int sourceWidth,
        int sourceHeight,
        PixelAlignmentSettings settings) =>
        CreatePlanCore(sourceWidth, sourceHeight, settings, validateSourceLimits: true);

    private PixelAlignmentPlan CreatePlanCore(
        int sourceWidth,
        int sourceHeight,
        PixelAlignmentSettings settings,
        bool validateSourceLimits)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (sourceWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "源图片宽度必须大于 0。");
        }

        if (sourceHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceHeight), "源图片高度必须大于 0。");
        }

        if (validateSourceLimits)
        {
            ValidateSourceSize(sourceWidth, sourceHeight, nameof(settings));
        }

        ValidateGrid(settings);
        var crop = ResolveCropBounds(settings.CropBounds, sourceWidth, sourceHeight);
        var cropWidth = crop.Right - crop.Left;
        var cropHeight = crop.Bottom - crop.Top;
        var correction = settings.Correction ?? new PixelAlignmentCorrectionSettings();
        ValidateCorrection(correction, crop);
        var (correctedBaseWidth, correctedBaseHeight) = ResolveCorrectedBaseSize(
            cropWidth,
            cropHeight,
            correction);

        // For one axis, width * scale must be divisible by its target cell count. Reducing the
        // fraction count / width gives the smallest required scale for that axis. LCM then finds
        // the smallest uniform scale that satisfies both axes and therefore keeps aspect ratio.
        var horizontalFactor = settings.Columns / GreatestCommonDivisor(correctedBaseWidth, settings.Columns);
        var verticalFactor = settings.Rows / GreatestCommonDivisor(correctedBaseHeight, settings.Rows);
        var gridAlignmentFactor = LeastCommonMultiple(horizontalFactor, verticalFactor);
        var workingScaleValue = checked(
            DivideRoundUp((long)settings.MinimumUpscaleFactor, gridAlignmentFactor) * gridAlignmentFactor);
        var workingWidthValue = checked(correctedBaseWidth * workingScaleValue);
        var workingHeightValue = checked(correctedBaseHeight * workingScaleValue);
        var workingScale = checked((int)workingScaleValue);

        return new PixelAlignmentPlan
        {
            SourceWidth = sourceWidth,
            SourceHeight = sourceHeight,
            CropBounds = crop,
            CropWidth = cropWidth,
            CropHeight = cropHeight,
            IsCorrectionEnabled = correction.IsEnabled,
            CorrectedBaseWidth = correctedBaseWidth,
            CorrectedBaseHeight = correctedBaseHeight,
            Columns = settings.Columns,
            Rows = settings.Rows,
            GridAlignmentFactor = gridAlignmentFactor,
            WorkingScale = workingScale,
            WorkingWidth = workingWidthValue,
            WorkingHeight = workingHeightValue,
            CorrectedWorkingWidth = workingWidthValue,
            CorrectedWorkingHeight = workingHeightValue,
            CellWidth = workingWidthValue / settings.Columns,
            CellHeight = workingHeightValue / settings.Rows,
            Correction = correction
        };
    }

    public PixelNearestNeighborUpscaleResult UpscaleNearestNeighborToPng(
        string inputPath,
        string outputPath,
        int scale,
        PixelCropBounds? cropBounds = null,
        bool overwrite = false)
    {
        if (scale is <= 0 or > PixelAlignmentLimits.MaximumNearestNeighborScale)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scale),
                $"最近邻放大倍率必须位于 1 到 {PixelAlignmentLimits.MaximumNearestNeighborScale} 之间。");
        }

        var normalizedInput = NormalizeExistingInput(inputPath);
        var normalizedOutput = PrepareOutput(normalizedInput, outputPath, overwrite);
        var imageInfo = Image.Identify(normalizedInput)
            ?? throw new InvalidOperationException("无法识别像素对齐输入图片。");
        ValidateSourceSize(imageInfo.Width, imageInfo.Height, nameof(inputPath));
        using var source = Image.Load<Rgba32>(normalizedInput);
        ValidateSourceSize(source.Width, source.Height, nameof(inputPath));
        var crop = ResolveCropBounds(cropBounds, source.Width, source.Height);
        var targetWidthValue = checked((long)(crop.Right - crop.Left) * scale);
        var targetHeightValue = checked((long)(crop.Bottom - crop.Top) * scale);
        ValidateWorkingSize(targetWidthValue, targetHeightValue, nameof(scale));
        var targetWidth = (int)targetWidthValue;
        var targetHeight = (int)targetHeightValue;
        using var enlarged = source.Clone(context => context
            .Crop(new Rectangle(crop.Left, crop.Top, crop.Right - crop.Left, crop.Bottom - crop.Top))
            .Resize(new ResizeOptions
            {
                Size = new Size(targetWidth, targetHeight),
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.NearestNeighbor,
                Compand = false
            }));

        SavePngAtomically(enlarged, normalizedOutput, overwrite);
        return new PixelNearestNeighborUpscaleResult
        {
            OutputPath = normalizedOutput,
            CropBounds = crop,
            Scale = scale,
            Width = targetWidth,
            Height = targetHeight
        };
    }

    public PixelAlignmentResult AlignToPng(
        string inputPath,
        string outputPath,
        PixelAlignmentSettings settings,
        bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalizedInput = NormalizeExistingInput(inputPath);
        var normalizedOutput = PrepareOutput(normalizedInput, outputPath, overwrite);
        var imageInfo = Image.Identify(normalizedInput)
            ?? throw new InvalidOperationException("无法识别像素对齐输入图片。");
        ValidateSourceSize(imageInfo.Width, imageInfo.Height, nameof(inputPath));
        using var source = Image.Load<Rgba32>(normalizedInput);
        ValidateSourceSize(source.Width, source.Height, nameof(inputPath));
        var plan = CreatePlan(source.Width, source.Height, settings);
        using var aligned = new Image<Rgba32>(settings.Columns, settings.Rows);

        if (plan.IsCorrectionEnabled)
        {
            using var correctedBase = CreateCorrectedBaseImage(source, plan);
            SampleCorrectedGridPreservingGeometry(correctedBase, aligned, settings.SamplingMode);
        }
        else
        {
            // Keep the original virtual-upscale path intact so disabled correction remains an
            // exact behavioural match, including weighted dominant/average sampling.
            SampleGrid(source, aligned, plan, settings.SamplingMode);
        }

        SavePngAtomically(aligned, normalizedOutput, overwrite);
        return new PixelAlignmentResult
        {
            OutputPath = normalizedOutput,
            Plan = plan,
            SamplingMode = settings.SamplingMode,
            OutputWidth = aligned.Width,
            OutputHeight = aligned.Height
        };
    }

    public PixelNearestNeighborUpscaleResult ExportWorkingImageToPng(
        string inputPath,
        string outputPath,
        PixelAlignmentSettings settings,
        bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalizedInput = NormalizeExistingInput(inputPath);
        var imageInfo = Image.Identify(normalizedInput)
            ?? throw new InvalidOperationException("无法识别像素对齐输入图片。");
        ValidateSourceSize(imageInfo.Width, imageInfo.Height, nameof(inputPath));
        var plan = CreatePlan(imageInfo.Width, imageInfo.Height, settings);

        if (!plan.IsCorrectionEnabled)
        {
            var legacyResult = UpscaleNearestNeighborToPng(
                normalizedInput,
                outputPath,
                plan.WorkingScale,
                plan.CropBounds,
                overwrite);
            return legacyResult with
            {
                Plan = plan,
                IsCorrectionApplied = false
            };
        }

        var normalizedOutput = PrepareOutput(normalizedInput, outputPath, overwrite);
        using var source = Image.Load<Rgba32>(normalizedInput);
        ValidateSourceSize(source.Width, source.Height, nameof(inputPath));
        ValidateWorkingSize(
            plan.CorrectedWorkingWidth,
            plan.CorrectedWorkingHeight,
            nameof(settings));
        using var correctedBase = CreateCorrectedBaseImage(source, plan);
        using var correctedWorking = correctedBase.Clone(context => context.Resize(new ResizeOptions
        {
            Size = new Size(
                checked((int)plan.CorrectedWorkingWidth),
                checked((int)plan.CorrectedWorkingHeight)),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.NearestNeighbor,
            Compand = false
        }));
        SavePngAtomically(correctedWorking, normalizedOutput, overwrite);
        return new PixelNearestNeighborUpscaleResult
        {
            OutputPath = normalizedOutput,
            CropBounds = plan.CropBounds,
            Scale = plan.WorkingScale,
            Width = correctedWorking.Width,
            Height = correctedWorking.Height,
            IsCorrectionApplied = true,
            Plan = plan
        };
    }

    private static Image<Rgba32> CreateCorrectedBaseImage(
        Image<Rgba32> source,
        PixelAlignmentPlan plan)
    {
        var destination = new Image<Rgba32>(plan.CorrectedBaseWidth, plan.CorrectedBaseHeight);
        try
        {
            var correction = plan.Correction;
            var target = CreateTargetGeometry(plan);
            var sourceTransform = correction.EnablePerspectiveCorrection
                ? ProjectiveTransform.FromUnitSquare(
                    correction.SourceQuadrilateral
                    ?? throw new InvalidOperationException("透视矫正缺少源图四顶点。"))
                : default;

            destination.ProcessPixelRows(destinationAccessor =>
            {
                for (var y = 0; y < destination.Height; y++)
                {
                    var row = destinationAccessor.GetRowSpan(y);
                    for (var x = 0; x < destination.Width; x++)
                    {
                        var targetX = x + 0.5d;
                        var targetY = y + 0.5d;

                        if (!target.TryMapToUnitSquare(targetX, targetY, out var unitX, out var unitY))
                        {
                            continue;
                        }

                        double sourceX;
                        double sourceY;
                        if (correction.EnablePerspectiveCorrection)
                        {
                            if (!sourceTransform.TryMap(unitX, unitY, out sourceX, out sourceY))
                            {
                                continue;
                            }
                        }
                        else
                        {
                            sourceX = plan.CropBounds.Left + unitX * plan.CropWidth;
                            sourceY = plan.CropBounds.Top + unitY * plan.CropHeight;
                        }

                        if (!TryResolveSourcePixel(sourceX, sourceY, plan.CropBounds, out var pixelX, out var pixelY))
                        {
                            continue;
                        }

                        row[x] = source[pixelX, pixelY];
                    }
                }
            });
            return destination;
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }

    private void SampleCorrectedGridPreservingGeometry(
        Image<Rgba32> correctedBase,
        Image<Rgba32> destination,
        PixelCellSamplingMode samplingMode)
    {
        var sourceRatio = correctedBase.Width / (double)correctedBase.Height;
        var destinationRatio = destination.Width / (double)destination.Height;
        int fittedWidth;
        int fittedHeight;
        if (destinationRatio >= sourceRatio)
        {
            fittedHeight = destination.Height;
            fittedWidth = Math.Clamp(
                (int)Math.Round(correctedBase.Width * (destination.Height / (double)correctedBase.Height)),
                1,
                destination.Width);
        }
        else
        {
            fittedWidth = destination.Width;
            fittedHeight = Math.Clamp(
                (int)Math.Round(correctedBase.Height * (destination.Width / (double)correctedBase.Width)),
                1,
                destination.Height);
        }

        var samplingPlan = CreatePlanCore(
            correctedBase.Width,
            correctedBase.Height,
            new PixelAlignmentSettings
            {
                Columns = fittedWidth,
                Rows = fittedHeight,
                MinimumUpscaleFactor = 1,
                SamplingMode = samplingMode
            },
            validateSourceLimits: false);
        var offsetX = (destination.Width - fittedWidth) / 2;
        var offsetY = (destination.Height - fittedHeight) / 2;
        SampleGrid(correctedBase, destination, samplingPlan, samplingMode, offsetX, offsetY);
    }

    private static TargetParallelogram CreateTargetGeometry(PixelAlignmentPlan plan)
    {
        var (contentWidth, contentHeight) = ResolveAspectCorrectedContentSize(
            plan.CropWidth,
            plan.CropHeight,
            plan.Correction);
        return ResolveTargetGeometryLayout(contentWidth, contentHeight, plan.Correction).Target;
    }

    private static bool TryResolveSourcePixel(
        double sourceX,
        double sourceY,
        PixelCropBounds crop,
        out int pixelX,
        out int pixelY)
    {
        const double coordinateTolerance = 1e-9;
        if (sourceX < crop.Left - coordinateTolerance ||
            sourceY < crop.Top - coordinateTolerance ||
            sourceX >= crop.Right + coordinateTolerance ||
            sourceY >= crop.Bottom + coordinateTolerance)
        {
            pixelX = 0;
            pixelY = 0;
            return false;
        }

        sourceX = Math.Clamp(sourceX, crop.Left, Math.BitDecrement((double)crop.Right));
        sourceY = Math.Clamp(sourceY, crop.Top, Math.BitDecrement((double)crop.Bottom));
        pixelX = (int)Math.Floor(sourceX);
        pixelY = (int)Math.Floor(sourceY);
        return true;
    }

    private static void SampleGrid(
        Image<Rgba32> source,
        Image<Rgba32> destination,
        PixelAlignmentPlan plan,
        PixelCellSamplingMode samplingMode,
        int destinationOffsetX = 0,
        int destinationOffsetY = 0)
    {
        destination.ProcessPixelRows(destinationAccessor =>
        {
            for (var row = 0; row < plan.Rows; row++)
            {
                var destinationRow = destinationAccessor.GetRowSpan(destinationOffsetY + row);
                for (var column = 0; column < plan.Columns; column++)
                {
                    destinationRow[destinationOffsetX + column] =
                        SampleCell(source, plan, column, row, samplingMode);
                }
            }
        });
    }

    private static Rgba32 SampleCell(
        Image<Rgba32> source,
        PixelAlignmentPlan plan,
        int column,
        int row,
        PixelCellSamplingMode samplingMode)
    {
        var workingLeft = column * plan.CellWidth;
        var workingTop = row * plan.CellHeight;
        var workingRight = workingLeft + plan.CellWidth;
        var workingBottom = workingTop + plan.CellHeight;

        if (samplingMode == PixelCellSamplingMode.CenterPixel)
        {
            var sourceX = plan.CropBounds.Left + checked((int)Math.Min(
                plan.CropWidth - 1L,
                (workingLeft + plan.CellWidth / 2) / plan.WorkingScale));
            var sourceY = plan.CropBounds.Top + checked((int)Math.Min(
                plan.CropHeight - 1L,
                (workingTop + plan.CellHeight / 2) / plan.WorkingScale));
            return source[sourceX, sourceY];
        }

        var firstSourceX = checked((int)(workingLeft / plan.WorkingScale));
        var lastSourceX = checked((int)(DivideRoundUp(workingRight, plan.WorkingScale) - 1));
        var firstSourceY = checked((int)(workingTop / plan.WorkingScale));
        var lastSourceY = checked((int)(DivideRoundUp(workingBottom, plan.WorkingScale) - 1));

        return samplingMode switch
        {
            PixelCellSamplingMode.DominantColor => SampleDominantColor(
                source,
                plan,
                workingLeft,
                workingTop,
                workingRight,
                workingBottom,
                firstSourceX,
                firstSourceY,
                lastSourceX,
                lastSourceY),
            PixelCellSamplingMode.AverageColor => SampleAverageColor(
                source,
                plan,
                workingLeft,
                workingTop,
                workingRight,
                workingBottom,
                firstSourceX,
                firstSourceY,
                lastSourceX,
                lastSourceY),
            _ => throw new ArgumentOutOfRangeException(nameof(samplingMode), samplingMode, "未知的像素格采样模式。")
        };
    }

    private static Rgba32 SampleDominantColor(
        Image<Rgba32> source,
        PixelAlignmentPlan plan,
        long workingLeft,
        long workingTop,
        long workingRight,
        long workingBottom,
        int firstSourceX,
        int firstSourceY,
        int lastSourceX,
        int lastSourceY)
    {
        var weights = new Dictionary<uint, long>();
        var encounterOrder = new List<uint>();
        foreach (var sample in EnumerateCellSamples(
                     source,
                     plan,
                     workingLeft,
                     workingTop,
                     workingRight,
                     workingBottom,
                     firstSourceX,
                     firstSourceY,
                     lastSourceX,
                     lastSourceY))
        {
            var packed = Pack(sample.Color);
            if (weights.TryGetValue(packed, out var currentWeight))
            {
                weights[packed] = checked(currentWeight + sample.Weight);
            }
            else
            {
                if (weights.Count >= PixelAlignmentLimits.MaximumDominantDistinctColorsPerCell)
                {
                    throw new InvalidOperationException(
                        $"单个网格的主色采样最多统计 " +
                        $"{PixelAlignmentLimits.MaximumDominantDistinctColorsPerCell:N0} 种不同颜色；" +
                        "请增加网格行列、缩小裁切范围，或改用平均色/中心像素采样。");
                }

                weights.Add(packed, sample.Weight);
                encounterOrder.Add(packed);
            }
        }

        var selected = encounterOrder[0];
        var selectedWeight = weights[selected];
        for (var index = 1; index < encounterOrder.Count; index++)
        {
            var candidate = encounterOrder[index];
            var candidateWeight = weights[candidate];
            if (candidateWeight > selectedWeight)
            {
                selected = candidate;
                selectedWeight = candidateWeight;
            }
        }

        return Unpack(selected);
    }

    private static Rgba32 SampleAverageColor(
        Image<Rgba32> source,
        PixelAlignmentPlan plan,
        long workingLeft,
        long workingTop,
        long workingRight,
        long workingBottom,
        int firstSourceX,
        int firstSourceY,
        int lastSourceX,
        int lastSourceY)
    {
        double totalWeight = 0;
        double alphaWeight = 0;
        double red = 0;
        double green = 0;
        double blue = 0;
        foreach (var sample in EnumerateCellSamples(
                     source,
                     plan,
                     workingLeft,
                     workingTop,
                     workingRight,
                     workingBottom,
                     firstSourceX,
                     firstSourceY,
                     lastSourceX,
                     lastSourceY))
        {
            totalWeight += sample.Weight;
            var effectiveAlphaWeight = sample.Weight * (sample.Color.A / 255d);
            alphaWeight += effectiveAlphaWeight;
            red += sample.Color.R * effectiveAlphaWeight;
            green += sample.Color.G * effectiveAlphaWeight;
            blue += sample.Color.B * effectiveAlphaWeight;
        }

        if (totalWeight <= 0 || alphaWeight <= 0)
        {
            return default;
        }

        return new Rgba32(
            ClampByte(red / alphaWeight),
            ClampByte(green / alphaWeight),
            ClampByte(blue / alphaWeight),
            ClampByte(alphaWeight / totalWeight * 255d));
    }

    private static IEnumerable<WeightedPixel> EnumerateCellSamples(
        Image<Rgba32> source,
        PixelAlignmentPlan plan,
        long workingLeft,
        long workingTop,
        long workingRight,
        long workingBottom,
        int firstSourceX,
        int firstSourceY,
        int lastSourceX,
        int lastSourceY)
    {
        for (var sourceY = firstSourceY; sourceY <= lastSourceY; sourceY++)
        {
            var pixelTop = (long)sourceY * plan.WorkingScale;
            var pixelBottom = pixelTop + plan.WorkingScale;
            var overlapHeight = Math.Min(workingBottom, pixelBottom) - Math.Max(workingTop, pixelTop);
            for (var sourceX = firstSourceX; sourceX <= lastSourceX; sourceX++)
            {
                var pixelLeft = (long)sourceX * plan.WorkingScale;
                var pixelRight = pixelLeft + plan.WorkingScale;
                var overlapWidth = Math.Min(workingRight, pixelRight) - Math.Max(workingLeft, pixelLeft);
                var weight = checked(overlapWidth * overlapHeight);
                yield return new WeightedPixel(
                    source[plan.CropBounds.Left + sourceX, plan.CropBounds.Top + sourceY],
                    weight);
            }
        }
    }

    private static (int Width, int Height) ResolveCorrectedBaseSize(
        int cropWidth,
        int cropHeight,
        PixelAlignmentCorrectionSettings correction)
    {
        var (contentWidth, contentHeight) = ResolveAspectCorrectedContentSize(
            cropWidth,
            cropHeight,
            correction);
        var layout = ResolveTargetGeometryLayout(contentWidth, contentHeight, correction);
        ValidateWorkingSize(layout.CanvasWidth, layout.CanvasHeight, nameof(correction));
        return (layout.CanvasWidth, layout.CanvasHeight);
    }

    private static (int Width, int Height) ResolveAspectCorrectedContentSize(
        int cropWidth,
        int cropHeight,
        PixelAlignmentCorrectionSettings correction)
    {
        if (!correction.EnableAspectRatioCorrection)
        {
            return (cropWidth, cropHeight);
        }

        var targetAspectRatio = correction.TargetAspectRatioA / correction.TargetAspectRatioB;
        var currentAspectRatio = (double)cropWidth / cropHeight;
        long width = cropWidth;
        long height = cropHeight;
        if (currentAspectRatio > targetAspectRatio)
        {
            height = checked((long)Math.Ceiling(cropWidth / targetAspectRatio));
        }
        else if (currentAspectRatio < targetAspectRatio)
        {
            width = checked((long)Math.Ceiling(cropHeight * targetAspectRatio));
        }

        return (checked((int)width), checked((int)height));
    }

    private static TargetGeometryLayout ResolveTargetGeometryLayout(
        int contentWidth,
        int contentHeight,
        PixelAlignmentCorrectionSettings correction)
    {
        var edgeAngleRadians = correction.EnablePerspectiveCorrection
            ? correction.TargetEdgeAngleDegrees * Math.PI / 180d
            : Math.PI / 2d;
        var edgeCosine = NormalizeTrigonometricValue(Math.Cos(edgeAngleRadians));
        var edgeSine = NormalizeTrigonometricValue(Math.Sin(edgeAngleRadians));
        var points = new[]
        {
            new GeometryPoint(0d, 0d),
            new GeometryPoint(contentWidth, 0d),
            new GeometryPoint(contentWidth + contentHeight * edgeCosine, contentHeight * edgeSine),
            new GeometryPoint(contentHeight * edgeCosine, contentHeight * edgeSine)
        };

        if (correction.EnableRotationCorrection && Math.Abs(correction.RotationDegrees) > 1e-12)
        {
            var centre = new GeometryPoint(
                (points[0].X + points[2].X) / 2d,
                (points[0].Y + points[2].Y) / 2d);
            var rotationRadians = correction.RotationDegrees * Math.PI / 180d;
            var cosine = NormalizeTrigonometricValue(Math.Cos(rotationRadians));
            var sine = NormalizeTrigonometricValue(Math.Sin(rotationRadians));
            for (var index = 0; index < points.Length; index++)
            {
                var offsetX = points[index].X - centre.X;
                var offsetY = points[index].Y - centre.Y;
                points[index] = new GeometryPoint(
                    centre.X + cosine * offsetX - sine * offsetY,
                    centre.Y + sine * offsetX + cosine * offsetY);
            }
        }

        var minimumX = points.Min(point => point.X);
        var maximumX = points.Max(point => point.X);
        var minimumY = points.Min(point => point.Y);
        var maximumY = points.Max(point => point.Y);
        var boundsWidth = maximumX - minimumX;
        var boundsHeight = maximumY - minimumY;
        var canvasWidth = ResolveCanvasDimension(boundsWidth, "矫正后画布宽度");
        var canvasHeight = ResolveCanvasDimension(boundsHeight, "矫正后画布高度");
        var offsetToCanvasX = (canvasWidth - boundsWidth) / 2d - minimumX;
        var offsetToCanvasY = (canvasHeight - boundsHeight) / 2d - minimumY;
        var origin = new GeometryPoint(
            points[0].X + offsetToCanvasX,
            points[0].Y + offsetToCanvasY);

        return new TargetGeometryLayout(
            new TargetParallelogram(
                origin.X,
                origin.Y,
                points[1].X - points[0].X,
                points[1].Y - points[0].Y,
                points[3].X - points[0].X,
                points[3].Y - points[0].Y),
            canvasWidth,
            canvasHeight);
    }

    private static double NormalizeTrigonometricValue(double value) =>
        Math.Abs(value) <= 1e-12 ? 0d : value;

    private static int ResolveCanvasDimension(double value, string description)
    {
        if (!double.IsFinite(value) || value <= 0d || value > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"{description}无效或过大。");
        }

        return Math.Max(1, checked((int)Math.Ceiling(value - 1e-9)));
    }

    private static void ValidateCorrection(
        PixelAlignmentCorrectionSettings correction,
        PixelCropBounds crop)
    {
        if (correction.EnableAspectRatioCorrection)
        {
            if (!double.IsFinite(correction.TargetAspectRatioA) || correction.TargetAspectRatioA <= 0d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(correction),
                    "比例矫正的目标 A 必须是大于 0 的有限数值。");
            }

            if (!double.IsFinite(correction.TargetAspectRatioB) || correction.TargetAspectRatioB <= 0d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(correction),
                    "比例矫正的目标 B 必须是大于 0 的有限数值。");
            }

            var targetRatio = correction.TargetAspectRatioA / correction.TargetAspectRatioB;
            if (!double.IsFinite(targetRatio) ||
                targetRatio < PixelAlignmentLimits.MinimumTargetAspectRatio ||
                targetRatio > PixelAlignmentLimits.MaximumTargetAspectRatio)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(correction),
                    $"目标 A:B 的宽高比必须位于 {PixelAlignmentLimits.MinimumTargetAspectRatio:N4} 到 " +
                    $"{PixelAlignmentLimits.MaximumTargetAspectRatio:N0} 之间。");
            }
        }

        if (correction.EnableRotationCorrection &&
            (!double.IsFinite(correction.RotationDegrees) ||
             Math.Abs(correction.RotationDegrees) > PixelAlignmentLimits.MaximumAbsoluteRotationDegrees))
        {
            throw new ArgumentOutOfRangeException(
                nameof(correction),
                $"旋转矫正角度必须位于 -{PixelAlignmentLimits.MaximumAbsoluteRotationDegrees:N0}° 到 " +
                $"{PixelAlignmentLimits.MaximumAbsoluteRotationDegrees:N0}° 之间。");
        }

        if (!correction.EnablePerspectiveCorrection)
        {
            return;
        }

        if (!double.IsFinite(correction.TargetEdgeAngleDegrees) ||
            correction.TargetEdgeAngleDegrees < PixelAlignmentLimits.MinimumTargetEdgeAngleDegrees ||
            correction.TargetEdgeAngleDegrees > PixelAlignmentLimits.MaximumTargetEdgeAngleDegrees)
        {
            throw new ArgumentOutOfRangeException(
                nameof(correction),
                $"透视矫正的目标 A/B 夹角必须位于 " +
                $"{PixelAlignmentLimits.MinimumTargetEdgeAngleDegrees:N0}° 到 " +
                $"{PixelAlignmentLimits.MaximumTargetEdgeAngleDegrees:N0}° 之间。");
        }

        var quadrilateral = correction.SourceQuadrilateral
            ?? throw new ArgumentException("启用透视矫正时必须提供源图四顶点。", nameof(correction));
        var points = new[]
        {
            quadrilateral.TopLeft,
            quadrilateral.TopRight,
            quadrilateral.BottomRight,
            quadrilateral.BottomLeft
        };
        if (points.Any(point => point is null))
        {
            throw new ArgumentException("透视矫正的四个源图顶点都必须存在。", nameof(correction));
        }

        foreach (var point in points)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                throw new ArgumentOutOfRangeException(nameof(correction), "透视矫正顶点必须是有限数值。");
            }

            if (point.X < crop.Left || point.X > crop.Right || point.Y < crop.Top || point.Y > crop.Bottom)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(correction),
                    "透视矫正顶点必须位于当前裁切范围内；Right/Bottom 排他边界可作为连续边界坐标使用。");
            }
        }

        const double geometricTolerance = 1e-8;
        double? orientation = null;
        for (var index = 0; index < points.Length; index++)
        {
            var first = points[index];
            var second = points[(index + 1) % points.Length];
            var third = points[(index + 2) % points.Length];
            var cross = Cross(first, second, third);
            if (Math.Abs(cross) <= geometricTolerance)
            {
                throw new ArgumentException("透视矫正四顶点不得出现重合或共线边。", nameof(correction));
            }

            orientation ??= Math.Sign(cross);
            if (Math.Sign(cross) != orientation.Value)
            {
                throw new ArgumentException(
                    "透视矫正四顶点必须按 TL、TR、BR、BL 顺序构成严格凸四边形，不允许交叉。",
                    nameof(correction));
            }
        }

        _ = ProjectiveTransform.FromUnitSquare(quadrilateral);
    }

    private static double Cross(PixelCoordinate first, PixelCoordinate second, PixelCoordinate third) =>
        (second.X - first.X) * (third.Y - second.Y) -
        (second.Y - first.Y) * (third.X - second.X);

    private static void ValidateGrid(PixelAlignmentSettings settings)
    {
        if (settings.Columns is <= 0 or > PixelAlignmentLimits.MaximumGridAxis)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                $"网格列数必须位于 1 到 {PixelAlignmentLimits.MaximumGridAxis} 之间。");
        }

        if (settings.Rows is <= 0 or > PixelAlignmentLimits.MaximumGridAxis)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                $"网格行数必须位于 1 到 {PixelAlignmentLimits.MaximumGridAxis} 之间。");
        }

        if (settings.MinimumUpscaleFactor is <= 0 or > PixelAlignmentLimits.MaximumUpscaleFactor)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                $"最小放大倍率必须位于 1 到 {PixelAlignmentLimits.MaximumUpscaleFactor} 之间。");
        }

        if (!Enum.IsDefined(settings.SamplingMode))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "未知的像素格采样模式。");
        }

        var outputPixelCount = checked(settings.Columns * settings.Rows);
        if (outputPixelCount > PixelAlignmentLimits.MaximumGridPixelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                $"网格像素总数不能超过 {PixelAlignmentLimits.MaximumGridPixelCount}。");
        }
    }

    private static void ValidateSourceSize(long width, long height, string parameterName)
    {
        if (width <= 0 ||
            height <= 0 ||
            width > PixelAlignmentLimits.MaximumSourceDimension ||
            height > PixelAlignmentLimits.MaximumSourceDimension)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"源图片的任一边不能超过 {PixelAlignmentLimits.MaximumSourceDimension} 像素。");
        }

        if (checked(width * height) > PixelAlignmentLimits.MaximumSourcePixelCount)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"源图片不能超过 {PixelAlignmentLimits.MaximumSourcePixelCount:N0} 像素。请先裁切或缩小图片。");
        }
    }

    private static void ValidateWorkingSize(long width, long height, string parameterName)
    {
        if (width <= 0 ||
            height <= 0 ||
            width > PixelAlignmentLimits.MaximumWorkingDimension ||
            height > PixelAlignmentLimits.MaximumWorkingDimension)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"无插值工作画幅的任一边不能超过 {PixelAlignmentLimits.MaximumWorkingDimension} 像素。");
        }

        if (checked(width * height) > PixelAlignmentLimits.MaximumWorkingPixelCount)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"无插值工作画幅不能超过 {PixelAlignmentLimits.MaximumWorkingPixelCount} 像素。");
        }
    }

    private static PixelCropBounds ResolveCropBounds(
        PixelCropBounds? requested,
        int sourceWidth,
        int sourceHeight)
    {
        var crop = requested ?? new PixelCropBounds
        {
            Right = sourceWidth,
            Bottom = sourceHeight
        };
        if (crop.Left < 0 || crop.Top < 0 || crop.Right > sourceWidth || crop.Bottom > sourceHeight)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requested),
                "裁切标尺必须完全位于源图片范围内。");
        }

        if (crop.Right <= crop.Left || crop.Bottom <= crop.Top)
        {
            throw new ArgumentException("裁切标尺必须保留至少 1×1 像素的有效区域。", nameof(requested));
        }

        return crop;
    }

    private static string NormalizeExistingInput(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        var normalizedInput = Path.GetFullPath(inputPath);
        if (!File.Exists(normalizedInput))
        {
            throw new FileNotFoundException("像素对齐输入图片不存在。", normalizedInput);
        }

        return normalizedInput;
    }

    private static string PrepareOutput(string normalizedInput, string outputPath, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var normalizedOutput = Path.GetFullPath(outputPath);
        if (string.Equals(
                normalizedInput,
                normalizedOutput,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("像素对齐输入与输出路径不能相同。", nameof(outputPath));
        }

        if (!string.Equals(Path.GetExtension(normalizedOutput), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("像素对齐输出必须是 PNG。", nameof(outputPath));
        }

        var outputDirectory = Path.GetDirectoryName(normalizedOutput)
            ?? throw new InvalidOperationException("无法解析像素对齐输出目录。");
        Directory.CreateDirectory(outputDirectory);
        if (File.Exists(normalizedOutput) && !overwrite)
        {
            throw new IOException($"像素对齐输出已经存在：{normalizedOutput}");
        }

        return normalizedOutput;
    }

    private static void SavePngAtomically(Image<Rgba32> image, string outputPath, bool overwrite)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("无法解析像素对齐输出目录。");
        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            image.Save(temporaryPath, new PngEncoder
            {
                ColorType = PngColorType.RgbWithAlpha
            });
            File.Move(temporaryPath, outputPath, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return Math.Abs(left);
    }

    private static int LeastCommonMultiple(int left, int right) =>
        checked(left / GreatestCommonDivisor(left, right) * right);

    private static int DivideRoundUp(int value, int divisor) =>
        checked((value + divisor - 1) / divisor);

    private static long DivideRoundUp(long value, long divisor) =>
        checked((value + divisor - 1) / divisor);

    private static byte ClampByte(double value) =>
        (byte)Math.Clamp((int)Math.Round(value), byte.MinValue, byte.MaxValue);

    private static uint Pack(Rgba32 color) =>
        ((uint)color.R << 24) |
        ((uint)color.G << 16) |
        ((uint)color.B << 8) |
        color.A;

    private static Rgba32 Unpack(uint packed) => new(
        (byte)(packed >> 24),
        (byte)(packed >> 16),
        (byte)(packed >> 8),
        (byte)packed);

    private readonly record struct TargetParallelogram(
        double OriginX,
        double OriginY,
        double EdgeAX,
        double EdgeAY,
        double EdgeBX,
        double EdgeBY)
    {
        public bool TryMapToUnitSquare(double x, double y, out double unitX, out double unitY)
        {
            var determinant = EdgeAX * EdgeBY - EdgeAY * EdgeBX;
            if (Math.Abs(determinant) <= 1e-12)
            {
                unitX = 0d;
                unitY = 0d;
                return false;
            }

            var localX = x - OriginX;
            var localY = y - OriginY;
            unitX = (localX * EdgeBY - localY * EdgeBX) / determinant;
            unitY = (EdgeAX * localY - EdgeAY * localX) / determinant;
            return unitX >= 0d && unitX < 1d && unitY >= 0d && unitY < 1d;
        }
    }

    private readonly record struct GeometryPoint(double X, double Y);

    private readonly record struct TargetGeometryLayout(
        TargetParallelogram Target,
        int CanvasWidth,
        int CanvasHeight);

    private readonly record struct ProjectiveTransform(
        double A,
        double B,
        double C,
        double D,
        double E,
        double F,
        double G,
        double H)
    {
        public static ProjectiveTransform FromUnitSquare(PixelPerspectiveQuadrilateral quadrilateral)
        {
            var topLeft = quadrilateral.TopLeft;
            var topRight = quadrilateral.TopRight;
            var bottomRight = quadrilateral.BottomRight;
            var bottomLeft = quadrilateral.BottomLeft;
            var deltaX1 = topRight.X - bottomRight.X;
            var deltaX2 = bottomLeft.X - bottomRight.X;
            var deltaX3 = topLeft.X - topRight.X + bottomRight.X - bottomLeft.X;
            var deltaY1 = topRight.Y - bottomRight.Y;
            var deltaY2 = bottomLeft.Y - bottomRight.Y;
            var deltaY3 = topLeft.Y - topRight.Y + bottomRight.Y - bottomLeft.Y;

            double projectiveX;
            double projectiveY;
            if (Math.Abs(deltaX3) <= 1e-12 && Math.Abs(deltaY3) <= 1e-12)
            {
                projectiveX = 0d;
                projectiveY = 0d;
            }
            else
            {
                var determinant = deltaX1 * deltaY2 - deltaX2 * deltaY1;
                if (Math.Abs(determinant) <= 1e-12)
                {
                    throw new ArgumentException("透视矫正四顶点无法构成稳定的投影变换。", nameof(quadrilateral));
                }

                projectiveX = (deltaX3 * deltaY2 - deltaX2 * deltaY3) / determinant;
                projectiveY = (deltaX1 * deltaY3 - deltaX3 * deltaY1) / determinant;
            }

            return new ProjectiveTransform(
                topRight.X - topLeft.X + projectiveX * topRight.X,
                bottomLeft.X - topLeft.X + projectiveY * bottomLeft.X,
                topLeft.X,
                topRight.Y - topLeft.Y + projectiveX * topRight.Y,
                bottomLeft.Y - topLeft.Y + projectiveY * bottomLeft.Y,
                topLeft.Y,
                projectiveX,
                projectiveY);
        }

        public bool TryMap(double unitX, double unitY, out double x, out double y)
        {
            var denominator = G * unitX + H * unitY + 1d;
            if (!double.IsFinite(denominator) || Math.Abs(denominator) <= 1e-12)
            {
                x = 0d;
                y = 0d;
                return false;
            }

            x = (A * unitX + B * unitY + C) / denominator;
            y = (D * unitX + E * unitY + F) / denominator;
            return double.IsFinite(x) && double.IsFinite(y);
        }
    }

    private readonly record struct WeightedPixel(Rgba32 Color, long Weight);
}

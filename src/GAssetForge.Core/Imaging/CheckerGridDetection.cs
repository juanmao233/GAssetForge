using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace GAssetForge.Core.Imaging;

public sealed record CheckerGridDetectionSettings
{
    public const double MinimumSearchRange = 1d;

    public const double MaximumSearchRange = 4096d;

    public double MinimumCellSize { get; init; } = 2d;

    public double MaximumCellSize { get; init; } = 256d;

    public double SearchRange { get; init; } = 128d;

    public double MinimumConfidence { get; init; } = 0.55d;
}

public sealed record CheckerGridDetectionResult
{
    public bool IsDetected { get; init; }

    public string ColorAHex { get; init; } = string.Empty;

    public string ColorBHex { get; init; } = string.Empty;

    public double CellSize { get; init; }

    public double OffsetX { get; init; }

    public double OffsetY { get; init; }

    public double CellSizeMinimum { get; init; }

    public double CellSizeMaximum { get; init; }

    public int HorizontalCellCount { get; init; }

    public int VerticalCellCount { get; init; }

    public int DetectedCornerCount { get; init; }

    public double Confidence { get; init; }

    public string FailureReason { get; init; } = string.Empty;
}

public interface ICheckerGridDetectionService
{
    CheckerGridDetectionResult Detect(string inputPath, CheckerGridDetectionSettings settings);
}

/// <summary>
/// Detects an axis-aligned, two-color transparency checker by collecting repeated transitions from
/// all four image corners. The detector does not mutate the source and deliberately remains
/// separate from <see cref="IBackgroundRemovalService"/> so a batch can detect independent grid
/// geometry for every input before applying the existing deterministic removal path.
/// </summary>
public sealed class ImageSharpCheckerGridDetectionService : ICheckerGridDetectionService
{
    private const double MinimumSupportedCellSize = 0.25d;
    private const double MaximumSupportedCellSize = 4096d;
    private const int MaximumHistogramSamples = 262_144;
    private const int QuantizationShift = 4;
    private const int MaximumDominantBins = 32;
    private const int ScanlinesPerCornerAxis = 9;
    private const double MinimumColorSeparation = 10d;

    public CheckerGridDetectionResult Detect(
        string inputPath,
        CheckerGridDetectionSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(settings);
        ValidateSettings(settings);

        var normalizedInput = Path.GetFullPath(inputPath);
        if (!File.Exists(normalizedInput))
        {
            throw new FileNotFoundException("待识别棋盘格的图片不存在。", normalizedInput);
        }

        using var image = Image.Load<Rgba32>(normalizedInput);
        if (image.Width <= 0 || image.Height <= 0)
        {
            return Failure("图片没有可分析的像素。", 0d);
        }

        var regions = CreateCornerRegions(image.Width, image.Height, settings.SearchRange);
        var sampledPixels = SampleCornerPixels(image, regions);
        if (sampledPixels.Count < 16)
        {
            return Failure("四角搜索范围内没有足够的不透明像素。", 0d);
        }

        var colorPair = DetectDominantColorPair(sampledPixels);
        if (colorPair is null)
        {
            return Failure("四角未找到稳定且重复出现的两种棋盘底色。", 0d);
        }

        var horizontalObservations = new List<AxisScanObservation>();
        var verticalObservations = new List<AxisScanObservation>();
        foreach (var region in regions)
        {
            AppendCornerScanObservations(
                image,
                region,
                colorPair.Value,
                settings,
                horizontalObservations,
                verticalObservations);
        }

        var horizontalFit = FitAxis(horizontalObservations, settings);
        var verticalFit = FitAxis(verticalObservations, settings);
        if (horizontalFit is null || verticalFit is null)
        {
            return Failure(
                "四角没有检测到足够的水平和垂直交替格线；请扩大识别范围或调整格宽范围。",
                Math.Max(horizontalFit?.Score ?? 0d, verticalFit?.Score ?? 0d));
        }

        var firstHorizontal = horizontalFit.Value;
        var firstVertical = verticalFit.Value;
        var firstAverageCellSize =
            (firstHorizontal.CellSize * firstHorizontal.Score +
             firstVertical.CellSize * firstVertical.Score) /
            Math.Max(0.000001d, firstHorizontal.Score + firstVertical.Score);
        var axisDifference = Math.Abs(firstHorizontal.CellSize - firstVertical.CellSize);
        var allowedAxisDifference = Math.Max(1d, firstAverageCellSize * 0.12d);
        if (axisDifference > allowedAxisDifference)
        {
            var mismatchConfidence = Math.Clamp(
                (firstHorizontal.Score + firstVertical.Score) / 2d *
                Math.Max(0d, 1d - axisDifference / Math.Max(firstAverageCellSize, 0.000001d)),
                0d,
                1d);
            return Failure("水平与垂直方向检测到的格宽不一致，无法确认正方形棋盘格。", mismatchConfidence);
        }

        var cellSize = Math.Clamp(
            firstAverageCellSize,
            settings.MinimumCellSize,
            settings.MaximumCellSize);
        // Refit both phases against one shared square-cell size. This prevents two independently
        // rounded estimates from producing a visible phase drift at the opposite image edge.
        var horizontal = EvaluateCandidate(horizontalObservations, cellSize);
        var vertical = EvaluateCandidate(verticalObservations, cellSize);
        if (horizontal is null || vertical is null)
        {
            return Failure("检测到格宽候选，但无法在四角拟合出稳定网格相位。", 0d);
        }

        var offsetX = PositiveModulo(horizontal.Value.Phase, cellSize);
        var offsetY = PositiveModulo(vertical.Value.Phase, cellSize);
        var orderedColors = OrderColorsForNormalizedPhase(
            sampledPixels,
            colorPair.Value,
            cellSize,
            offsetX,
            offsetY);
        var detectedCornerMask = horizontal.Value.CornerMask | vertical.Value.CornerMask;
        var detectedCornerCount = BitOperations.PopCount((uint)detectedCornerMask);
        var axisConsistency = Math.Clamp(
            1d - axisDifference / Math.Max(allowedAxisDifference, 0.000001d),
            0d,
            1d);
        var cornerConfidence = detectedCornerCount / 4d;
        var confidence = Math.Clamp(
            colorPair.Value.Confidence * 0.20d +
            horizontal.Value.Score * 0.30d +
            vertical.Value.Score * 0.30d +
            axisConsistency * 0.10d +
            cornerConfidence * 0.10d,
            0d,
            1d);
        var sizeSamples = CollectCellSizeSamples(
            horizontalObservations,
            verticalObservations,
            cellSize);
        var cellSizeMinimum = sizeSamples.Count == 0
            ? cellSize
            : Percentile(sizeSamples, 0.10d);
        var cellSizeMaximum = sizeSamples.Count == 0
            ? cellSize
            : Percentile(sizeSamples, 0.90d);
        var result = new CheckerGridDetectionResult
        {
            IsDetected = confidence >= settings.MinimumConfidence && detectedCornerCount >= 2,
            ColorAHex = orderedColors.ColorA.Hex,
            ColorBHex = orderedColors.ColorB.Hex,
            CellSize = cellSize,
            OffsetX = offsetX,
            OffsetY = offsetY,
            CellSizeMinimum = cellSizeMinimum,
            CellSizeMaximum = cellSizeMaximum,
            HorizontalCellCount = CountIntersectingCells(image.Width, cellSize, offsetX),
            VerticalCellCount = CountIntersectingCells(image.Height, cellSize, offsetY),
            DetectedCornerCount = detectedCornerCount,
            Confidence = confidence,
            FailureReason = confidence >= settings.MinimumConfidence && detectedCornerCount >= 2
                ? string.Empty
                : detectedCornerCount < 2
                    ? "有效格线只出现在不足两个角落，智能识别结果不可靠。"
                    : $"智能识别置信度 {confidence:P0} 低于要求 {settings.MinimumConfidence:P0}。"
        };
        return result;
    }

    private static void ValidateSettings(CheckerGridDetectionSettings settings)
    {
        if (!double.IsFinite(settings.MinimumCellSize) ||
            settings.MinimumCellSize is < MinimumSupportedCellSize or > MaximumSupportedCellSize)
        {
            throw new ArgumentException(
                "智能棋盘格最小格宽必须是 0.25 到 4096 之间的有限像素值。",
                nameof(settings));
        }

        if (!double.IsFinite(settings.MaximumCellSize) ||
            settings.MaximumCellSize is < MinimumSupportedCellSize or > MaximumSupportedCellSize ||
            settings.MaximumCellSize < settings.MinimumCellSize)
        {
            throw new ArgumentException(
                "智能棋盘格最大格宽必须不小于最小格宽，且位于 0.25 到 4096 像素之间。",
                nameof(settings));
        }

        if (!double.IsFinite(settings.SearchRange) ||
            settings.SearchRange is < CheckerGridDetectionSettings.MinimumSearchRange or
                > CheckerGridDetectionSettings.MaximumSearchRange)
        {
            throw new ArgumentException(
                $"智能棋盘格四角搜索范围必须是 " +
                $"{CheckerGridDetectionSettings.MinimumSearchRange:0.#} 到 " +
                $"{CheckerGridDetectionSettings.MaximumSearchRange:0.#} 像素。",
                nameof(settings));
        }

        if (!double.IsFinite(settings.MinimumConfidence) ||
            settings.MinimumConfidence is < 0d or > 1d)
        {
            throw new ArgumentException("智能棋盘格最低置信度必须位于 0 到 1。", nameof(settings));
        }
    }

    private static IReadOnlyList<CornerRegion> CreateCornerRegions(int width, int height, double searchRange)
    {
        var requestedRange = (int)Math.Ceiling(searchRange);
        var rangeX = Math.Min(width, requestedRange);
        var rangeY = Math.Min(height, requestedRange);
        return
        [
            new CornerRegion(0, 0, 0, rangeX, rangeY),
            new CornerRegion(1, width - rangeX, 0, width, rangeY),
            new CornerRegion(2, 0, height - rangeY, rangeX, height),
            new CornerRegion(3, width - rangeX, height - rangeY, width, height)
        ];
    }

    private static List<SampledPixel> SampleCornerPixels(
        Image<Rgba32> image,
        IReadOnlyList<CornerRegion> regions)
    {
        var totalArea = regions.Sum(region => (long)region.Width * region.Height);
        var step = Math.Max(
            1,
            (int)Math.Ceiling(Math.Sqrt(totalArea / (double)MaximumHistogramSamples)));
        var samples = new List<SampledPixel>((int)Math.Min(totalArea, MaximumHistogramSamples));
        image.ProcessPixelRows(accessor =>
        {
            foreach (var region in regions)
            {
                for (var y = region.Top; y < region.Bottom; y += step)
                {
                    var row = accessor.GetRowSpan(y);
                    for (var x = region.Left; x < region.Right; x += step)
                    {
                        var pixel = row[x];
                        if (pixel.A < 32)
                        {
                            continue;
                        }

                        samples.Add(new SampledPixel(
                            x,
                            y,
                            region.CornerId,
                            new RgbColor(pixel.R, pixel.G, pixel.B)));
                    }
                }
            }
        });
        return samples;
    }

    private static DominantColorPair? DetectDominantColorPair(IReadOnlyList<SampledPixel> samples)
    {
        var bins = new Dictionary<int, ColorBin>();
        foreach (var sample in samples)
        {
            var key = Quantize(sample.Color);
            if (!bins.TryGetValue(key, out var bin))
            {
                bin = new ColorBin();
                bins.Add(key, bin);
            }

            bin.Add(sample.Color, sample.CornerId);
        }

        var candidates = bins.Values
            .Where(bin => bin.Count >= Math.Max(2, samples.Count / 2000))
            .OrderByDescending(bin => bin.Count * (1d + 0.12d * (bin.CornerCount - 1)))
            .Take(MaximumDominantBins)
            .ToArray();
        if (candidates.Length < 2)
        {
            return null;
        }

        ColorBin? bestA = null;
        ColorBin? bestB = null;
        var bestScore = double.NegativeInfinity;
        for (var first = 0; first < candidates.Length - 1; first++)
        {
            for (var second = first + 1; second < candidates.Length; second++)
            {
                var a = candidates[first];
                var b = candidates[second];
                var distance = Distance(a.MeanColor, b.MeanColor);
                if (distance < MinimumColorSeparation)
                {
                    continue;
                }

                var pairCount = a.Count + b.Count;
                var balance = 2d * Math.Min(a.Count, b.Count) / Math.Max(1d, pairCount);
                var support = pairCount / (double)samples.Count;
                var individualCoverage = (a.CornerCount + b.CornerCount) / 8d;
                var separation = Math.Clamp(distance / 96d, 0d, 1d);
                var score = support *
                            (0.50d + 0.50d * balance) *
                            (0.45d + 0.55d * individualCoverage) *
                            (0.45d + 0.55d * separation);
                if (score <= bestScore)
                {
                    continue;
                }

                bestScore = score;
                bestA = a;
                bestB = b;
            }
        }

        if (bestA is null || bestB is null)
        {
            return null;
        }

        var colorA = bestA.MeanColor;
        var colorB = bestB.MeanColor;
        for (var iteration = 0; iteration < 2; iteration++)
        {
            var separation = Distance(colorA, colorB);
            var assignmentLimit = Math.Max(10d, separation * 0.42d);
            var accumulatorA = new ColorAccumulator();
            var accumulatorB = new ColorAccumulator();
            foreach (var sample in samples)
            {
                var distanceA = Distance(sample.Color, colorA);
                var distanceB = Distance(sample.Color, colorB);
                if (Math.Min(distanceA, distanceB) > assignmentLimit)
                {
                    continue;
                }

                if (distanceA <= distanceB)
                {
                    accumulatorA.Add(sample.Color, sample.CornerId);
                }
                else
                {
                    accumulatorB.Add(sample.Color, sample.CornerId);
                }
            }

            if (accumulatorA.Count == 0 || accumulatorB.Count == 0)
            {
                break;
            }

            colorA = accumulatorA.MeanColor;
            colorB = accumulatorB.MeanColor;
        }

        var finalSeparation = Distance(colorA, colorB);
        if (finalSeparation < MinimumColorSeparation)
        {
            return null;
        }

        var finalA = new ColorAccumulator();
        var finalB = new ColorAccumulator();
        var finalLimit = Math.Max(10d, finalSeparation * 0.42d);
        foreach (var sample in samples)
        {
            var distanceA = Distance(sample.Color, colorA);
            var distanceB = Distance(sample.Color, colorB);
            if (Math.Min(distanceA, distanceB) > finalLimit)
            {
                continue;
            }

            if (distanceA <= distanceB)
            {
                finalA.Add(sample.Color, sample.CornerId);
            }
            else
            {
                finalB.Add(sample.Color, sample.CornerId);
            }
        }

        if (finalA.Count == 0 || finalB.Count == 0)
        {
            return null;
        }

        var pairSupport = (finalA.Count + finalB.Count) / (double)samples.Count;
        var pairBalance = 2d * Math.Min(finalA.Count, finalB.Count) /
                          Math.Max(1d, finalA.Count + finalB.Count);
        var coverage = (finalA.CornerCount + finalB.CornerCount) / 8d;
        var separationConfidence = Math.Clamp(finalSeparation / 96d, 0d, 1d);
        var confidence = Math.Clamp(
            pairSupport * 0.35d +
            pairBalance * 0.20d +
            coverage * 0.25d +
            separationConfidence * 0.20d,
            0d,
            1d);
        return new DominantColorPair(colorA, colorB, finalSeparation, confidence);
    }

    private static void AppendCornerScanObservations(
        Image<Rgba32> image,
        CornerRegion region,
        DominantColorPair colors,
        CheckerGridDetectionSettings settings,
        ICollection<AxisScanObservation> horizontal,
        ICollection<AxisScanObservation> vertical)
    {
        var horizontalScanCoordinates = CreateScanCoordinates(
            region.Top,
            region.Bottom,
            ScanlinesPerCornerAxis);
        foreach (var y in horizontalScanCoordinates)
        {
            horizontal.Add(new AxisScanObservation(
                region.CornerId,
                region.Left,
                region.Right,
                ExtractTransitions(
                    image,
                    horizontal: true,
                    y,
                    region.Left,
                    region.Right,
                    colors,
                    settings.MinimumCellSize)));
        }

        var verticalScanCoordinates = CreateScanCoordinates(
            region.Left,
            region.Right,
            ScanlinesPerCornerAxis);
        foreach (var x in verticalScanCoordinates)
        {
            vertical.Add(new AxisScanObservation(
                region.CornerId,
                region.Top,
                region.Bottom,
                ExtractTransitions(
                    image,
                    horizontal: false,
                    x,
                    region.Top,
                    region.Bottom,
                    colors,
                    settings.MinimumCellSize)));
        }
    }

    private static IReadOnlyList<int> CreateScanCoordinates(int start, int end, int maximumCount)
    {
        var extent = end - start;
        if (extent <= 0)
        {
            return [];
        }

        var count = Math.Min(maximumCount, extent);
        if (count == 1)
        {
            return [start];
        }

        var coordinates = new List<int>(count);
        for (var index = 0; index < count; index++)
        {
            var coordinate = start + (int)Math.Round(index * (extent - 1d) / (count - 1d));
            if (coordinates.Count == 0 || coordinates[^1] != coordinate)
            {
                coordinates.Add(coordinate);
            }
        }

        return coordinates;
    }

    private static IReadOnlyList<double> ExtractTransitions(
        Image<Rgba32> image,
        bool horizontal,
        int fixedCoordinate,
        int start,
        int end,
        DominantColorPair colors,
        double minimumCellSize)
    {
        var length = end - start;
        if (length < 2)
        {
            return [];
        }

        var labels = new sbyte[length];
        Array.Fill(labels, (sbyte)-1);
        var distanceLimit = Math.Max(10d, colors.Separation * 0.45d);
        var ambiguityMargin = Math.Max(2d, colors.Separation * 0.10d);
        image.ProcessPixelRows(accessor =>
        {
            for (var index = 0; index < length; index++)
            {
                var x = horizontal ? start + index : fixedCoordinate;
                var y = horizontal ? fixedCoordinate : start + index;
                var pixel = accessor.GetRowSpan(y)[x];
                if (pixel.A < 32)
                {
                    continue;
                }

                var color = new RgbColor(pixel.R, pixel.G, pixel.B);
                var distanceA = Distance(color, colors.ColorA);
                var distanceB = Distance(color, colors.ColorB);
                if (Math.Min(distanceA, distanceB) > distanceLimit ||
                    Math.Abs(distanceA - distanceB) < ambiguityMargin)
                {
                    continue;
                }

                labels[index] = distanceA <= distanceB ? (sbyte)0 : (sbyte)1;
            }
        });

        var smoothed = SmoothLabels(labels);
        var known = new List<KnownLabel>(length);
        for (var index = 0; index < smoothed.Length; index++)
        {
            if (smoothed[index] >= 0)
            {
                known.Add(new KnownLabel(index, smoothed[index]));
            }
        }

        if (known.Count < 4)
        {
            return [];
        }

        var transitions = new List<double>();
        var minimumSpacing = Math.Max(0.5d, minimumCellSize * 0.30d);
        for (var index = 1; index < known.Count; index++)
        {
            var previous = known[index - 1];
            var current = known[index];
            if (previous.Label == current.Label ||
                !HasStableSupport(known, index - 1, previous.Label, backwards: true) ||
                !HasStableSupport(known, index, current.Label, backwards: false))
            {
                continue;
            }

            var previousCenter = start + previous.Index + 0.5d;
            var currentCenter = start + current.Index + 0.5d;
            var boundary = (previousCenter + currentCenter) / 2d;
            if (transitions.Count > 0 && boundary - transitions[^1] < minimumSpacing)
            {
                transitions[^1] = (transitions[^1] + boundary) / 2d;
            }
            else
            {
                transitions.Add(boundary);
            }
        }

        return transitions;
    }

    private static sbyte[] SmoothLabels(IReadOnlyList<sbyte> labels)
    {
        var result = new sbyte[labels.Count];
        Array.Fill(result, (sbyte)-1);
        for (var index = 0; index < labels.Count; index++)
        {
            var countA = 0;
            var countB = 0;
            for (var neighbor = Math.Max(0, index - 1);
                 neighbor <= Math.Min(labels.Count - 1, index + 1);
                 neighbor++)
            {
                if (labels[neighbor] == 0)
                {
                    countA++;
                }
                else if (labels[neighbor] == 1)
                {
                    countB++;
                }
            }

            if (countA >= 2 && countB == 0)
            {
                result[index] = 0;
            }
            else if (countB >= 2 && countA == 0)
            {
                result[index] = 1;
            }
            else if (labels[index] == 0 && countA > 0 && countB == 0)
            {
                result[index] = 0;
            }
            else if (labels[index] == 1 && countB > 0 && countA == 0)
            {
                result[index] = 1;
            }
        }

        return result;
    }

    private static bool HasStableSupport(
        IReadOnlyList<KnownLabel> labels,
        int index,
        sbyte expected,
        bool backwards)
    {
        var support = 0;
        var origin = labels[index].Index;
        for (var step = 0; step < 5; step++)
        {
            var candidateIndex = backwards ? index - step : index + step;
            if (candidateIndex < 0 || candidateIndex >= labels.Count)
            {
                break;
            }

            var candidate = labels[candidateIndex];
            if (Math.Abs(candidate.Index - origin) > 5)
            {
                break;
            }

            if (candidate.Label == expected)
            {
                support++;
            }
            else
            {
                break;
            }
        }

        return support >= 2;
    }

    private static AxisFit? FitAxis(
        IReadOnlyList<AxisScanObservation> observations,
        CheckerGridDetectionSettings settings)
    {
        var candidates = BuildCellSizeCandidates(observations, settings);
        AxisFit? best = null;
        foreach (var candidate in candidates)
        {
            var evaluated = EvaluateCandidate(observations, candidate);
            if (evaluated is null || (best is not null && evaluated.Value.Score <= best.Value.Score))
            {
                continue;
            }

            best = evaluated;
        }

        return best is { Score: >= 0.40d } ? best : null;
    }

    private static IReadOnlyList<double> BuildCellSizeCandidates(
        IReadOnlyList<AxisScanObservation> observations,
        CheckerGridDetectionSettings settings)
    {
        var rawCandidates = new List<double>();
        foreach (var observation in observations)
        {
            for (var first = 0; first < observation.Boundaries.Count - 1; first++)
            {
                var maximumSecond = Math.Min(observation.Boundaries.Count, first + 5);
                for (var second = first + 1; second < maximumSecond; second++)
                {
                    var distance = observation.Boundaries[second] - observation.Boundaries[first];
                    if (!double.IsFinite(distance) || distance <= 0d)
                    {
                        continue;
                    }

                    var maximumDivisor = Math.Min(
                        32,
                        Math.Max(1, (int)Math.Floor(distance / settings.MinimumCellSize)));
                    for (var divisor = 1; divisor <= maximumDivisor; divisor++)
                    {
                        var candidate = distance / divisor;
                        if (candidate >= settings.MinimumCellSize && candidate <= settings.MaximumCellSize)
                        {
                            rawCandidates.Add(candidate);
                        }
                    }
                }
            }
        }

        if (rawCandidates.Count == 0)
        {
            return [];
        }

        rawCandidates.Sort();
        var merged = new List<double>();
        var cluster = new List<double> { rawCandidates[0] };
        for (var index = 1; index < rawCandidates.Count; index++)
        {
            var previousMean = cluster.Average();
            var tolerance = Math.Max(0.02d, previousMean * 0.005d);
            if (Math.Abs(rawCandidates[index] - previousMean) <= tolerance)
            {
                cluster.Add(rawCandidates[index]);
                continue;
            }

            merged.Add(Median(cluster));
            cluster.Clear();
            cluster.Add(rawCandidates[index]);
        }

        merged.Add(Median(cluster));
        return merged;
    }

    private static AxisFit? EvaluateCandidate(
        IReadOnlyList<AxisScanObservation> observations,
        double cellSize)
    {
        var allBoundaries = observations.SelectMany(observation => observation.Boundaries).ToArray();
        if (allBoundaries.Length < 4 || !double.IsFinite(cellSize) || cellSize <= 0d)
        {
            return null;
        }

        var phaseVectorX = 0d;
        var phaseVectorY = 0d;
        foreach (var boundary in allBoundaries)
        {
            var angle = Math.Tau * PositiveModulo(boundary, cellSize) / cellSize;
            phaseVectorX += Math.Cos(angle);
            phaseVectorY += Math.Sin(angle);
        }

        var phaseConcentration = Math.Sqrt(
            phaseVectorX * phaseVectorX + phaseVectorY * phaseVectorY) / allBoundaries.Length;
        if (phaseConcentration < 0.12d)
        {
            return null;
        }

        var phaseAngle = Math.Atan2(phaseVectorY, phaseVectorX);
        if (phaseAngle < 0d)
        {
            phaseAngle += Math.Tau;
        }

        var phase = phaseAngle * cellSize / Math.Tau;
        var tolerance = Math.Min(2.5d, Math.Max(0.6d, cellSize * 0.10d));
        var inlierCount = 0;
        var fitQualityTotal = 0d;
        var cornerMask = 0;
        foreach (var observation in observations)
        {
            var observationHasInlier = false;
            foreach (var boundary in observation.Boundaries)
            {
                var residual = DistanceToPeriodicBoundary(boundary, phase, cellSize);
                if (residual > tolerance)
                {
                    continue;
                }

                inlierCount++;
                fitQualityTotal += 1d - residual / tolerance;
                observationHasInlier = true;
            }

            if (observationHasInlier)
            {
                cornerMask |= 1 << observation.CornerId;
            }
        }

        if (inlierCount < 4)
        {
            return null;
        }

        var inlierRatio = inlierCount / (double)allBoundaries.Length;
        var fitQuality = fitQualityTotal / inlierCount;
        var completenessValues = new List<double>();
        foreach (var observation in observations.Where(item => item.Boundaries.Count > 0))
        {
            var expected = CountPeriodicBoundaries(observation.Start, observation.End, phase, cellSize);
            if (expected <= 0)
            {
                continue;
            }

            var observed = observation.Boundaries.Count(boundary =>
                DistanceToPeriodicBoundary(boundary, phase, cellSize) <= tolerance);
            completenessValues.Add(Math.Min(1d, observed / (double)expected));
        }

        completenessValues.Sort((left, right) => right.CompareTo(left));
        var retainedCompletenessCount = Math.Max(1, (completenessValues.Count + 1) / 2);
        var completeness = completenessValues.Count == 0
            ? 0d
            : completenessValues.Take(retainedCompletenessCount).Average();
        var spacingQualityValues = new List<double>();
        foreach (var observation in observations)
        {
            for (var index = 1; index < observation.Boundaries.Count; index++)
            {
                var distance = observation.Boundaries[index] - observation.Boundaries[index - 1];
                var multiple = Math.Max(1, (int)Math.Round(distance / cellSize));
                var normalized = distance / multiple;
                var residual = Math.Abs(normalized - cellSize);
                spacingQualityValues.Add(Math.Clamp(1d - residual / tolerance, 0d, 1d));
            }
        }

        var spacingQuality = spacingQualityValues.Count == 0 ? 0d : spacingQualityValues.Average();
        var score = Math.Clamp(
            inlierRatio * 0.30d +
            fitQuality * 0.20d +
            completeness * 0.30d +
            phaseConcentration * 0.10d +
            spacingQuality * 0.10d,
            0d,
            1d);
        return new AxisFit(cellSize, phase, score, cornerMask);
    }

    private static OrderedColors OrderColorsForNormalizedPhase(
        IReadOnlyList<SampledPixel> samples,
        DominantColorPair colors,
        double cellSize,
        double offsetX,
        double offsetY)
    {
        var directMatches = 0;
        var swappedMatches = 0;
        var distanceLimit = Math.Max(10d, colors.Separation * 0.45d);
        foreach (var sample in samples)
        {
            var distanceA = Distance(sample.Color, colors.ColorA);
            var distanceB = Distance(sample.Color, colors.ColorB);
            if (Math.Min(distanceA, distanceB) > distanceLimit ||
                Math.Abs(distanceA - distanceB) < colors.Separation * 0.10d ||
                IsNearBoundary(sample.X + 0.5d, offsetX, cellSize) ||
                IsNearBoundary(sample.Y + 0.5d, offsetY, cellSize))
            {
                continue;
            }

            var observedA = distanceA <= distanceB;
            var cellX = (long)Math.Floor((sample.X + 0.5d - offsetX) / cellSize);
            var cellY = (long)Math.Floor((sample.Y + 0.5d - offsetY) / cellSize);
            var evenCell = ((cellX + cellY) & 1L) == 0L;
            if (observedA == evenCell)
            {
                directMatches++;
            }
            else
            {
                swappedMatches++;
            }
        }

        return directMatches >= swappedMatches
            ? new OrderedColors(colors.ColorA, colors.ColorB)
            : new OrderedColors(colors.ColorB, colors.ColorA);
    }

    private static List<double> CollectCellSizeSamples(
        IReadOnlyList<AxisScanObservation> horizontal,
        IReadOnlyList<AxisScanObservation> vertical,
        double cellSize)
    {
        var accepted = new List<double>();
        foreach (var observation in horizontal.Concat(vertical))
        {
            for (var index = 1; index < observation.Boundaries.Count; index++)
            {
                var distance = observation.Boundaries[index] - observation.Boundaries[index - 1];
                var multiple = Math.Max(1, (int)Math.Round(distance / cellSize));
                var normalized = distance / multiple;
                if (Math.Abs(normalized - cellSize) <= Math.Max(1d, cellSize * 0.15d))
                {
                    accepted.Add(normalized);
                }
            }
        }

        accepted.Sort();
        return accepted;
    }

    private static int CountIntersectingCells(int extent, double cellSize, double offset)
    {
        var first = (long)Math.Floor((0d - offset) / cellSize);
        var lastExclusive = (long)Math.Ceiling((extent - offset) / cellSize);
        return (int)Math.Clamp(lastExclusive - first, 0L, int.MaxValue);
    }

    private static int CountPeriodicBoundaries(double start, double end, double phase, double cellSize)
    {
        var first = (long)Math.Ceiling((start - phase) / cellSize);
        var last = (long)Math.Floor((end - phase) / cellSize);
        return (int)Math.Clamp(last - first + 1L, 0L, int.MaxValue);
    }

    private static double DistanceToPeriodicBoundary(double value, double phase, double period)
    {
        var local = PositiveModulo(value - phase, period);
        return Math.Min(local, period - local);
    }

    private static bool IsNearBoundary(double value, double phase, double period) =>
        DistanceToPeriodicBoundary(value, phase, period) <= Math.Min(2d, Math.Max(0.6d, period * 0.10d));

    private static double PositiveModulo(double value, double divisor)
    {
        var remainder = value % divisor;
        if (remainder < 0d)
        {
            remainder += divisor;
        }

        return remainder >= divisor ? 0d : remainder;
    }

    private static int Quantize(RgbColor color) =>
        ((color.Red >> QuantizationShift) << 8) |
        ((color.Green >> QuantizationShift) << 4) |
        (color.Blue >> QuantizationShift);

    private static double Distance(RgbColor first, RgbColor second)
    {
        var red = first.Red - second.Red;
        var green = first.Green - second.Green;
        var blue = first.Blue - second.Blue;
        return Math.Sqrt(red * red + green * green + blue * blue);
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        var middle = ordered.Length / 2;
        return (ordered.Length & 1) == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2d;
    }

    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0d;
        }

        var position = Math.Clamp(percentile, 0d, 1d) * (sortedValues.Count - 1d);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sortedValues[lower];
        }

        var fraction = position - lower;
        return sortedValues[lower] * (1d - fraction) + sortedValues[upper] * fraction;
    }

    private static CheckerGridDetectionResult Failure(string reason, double confidence) => new()
    {
        Confidence = Math.Clamp(confidence, 0d, 1d),
        FailureReason = reason
    };

    private sealed class ColorBin
    {
        private long _red;
        private long _green;
        private long _blue;
        private int _cornerMask;

        public int Count { get; private set; }

        public int CornerCount => BitOperations.PopCount((uint)_cornerMask);

        public RgbColor MeanColor => RgbColor.FromInts(
            (int)Math.Round(_red / (double)Math.Max(1, Count)),
            (int)Math.Round(_green / (double)Math.Max(1, Count)),
            (int)Math.Round(_blue / (double)Math.Max(1, Count)));

        public void Add(RgbColor color, int cornerId)
        {
            Count++;
            _red += color.Red;
            _green += color.Green;
            _blue += color.Blue;
            _cornerMask |= 1 << cornerId;
        }
    }

    private sealed class ColorAccumulator
    {
        private long _red;
        private long _green;
        private long _blue;
        private int _cornerMask;

        public int Count { get; private set; }

        public int CornerCount => BitOperations.PopCount((uint)_cornerMask);

        public RgbColor MeanColor => RgbColor.FromInts(
            (int)Math.Round(_red / (double)Math.Max(1, Count)),
            (int)Math.Round(_green / (double)Math.Max(1, Count)),
            (int)Math.Round(_blue / (double)Math.Max(1, Count)));

        public void Add(RgbColor color, int cornerId)
        {
            Count++;
            _red += color.Red;
            _green += color.Green;
            _blue += color.Blue;
            _cornerMask |= 1 << cornerId;
        }
    }

    private readonly record struct CornerRegion(
        int CornerId,
        int Left,
        int Top,
        int Right,
        int Bottom)
    {
        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    private readonly record struct SampledPixel(int X, int Y, int CornerId, RgbColor Color);

    private readonly record struct DominantColorPair(
        RgbColor ColorA,
        RgbColor ColorB,
        double Separation,
        double Confidence);

    private readonly record struct AxisScanObservation(
        int CornerId,
        double Start,
        double End,
        IReadOnlyList<double> Boundaries);

    private readonly record struct AxisFit(double CellSize, double Phase, double Score, int CornerMask);

    private readonly record struct KnownLabel(int Index, sbyte Label);

    private readonly record struct OrderedColors(RgbColor ColorA, RgbColor ColorB);
}

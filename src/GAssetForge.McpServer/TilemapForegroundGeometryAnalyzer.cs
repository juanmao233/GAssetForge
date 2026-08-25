namespace GAssetForge.McpServer;

internal sealed class TilemapForegroundGeometryAnalyzer
{
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _foregroundMask;
    private readonly int[] _rowMinimumX;
    private readonly int[] _rowMaximumX;
    private readonly int[] _columnMinimumY;
    private readonly int[] _columnMaximumY;
    private int _minimumX;
    private int _minimumY;
    private int _maximumX = -1;
    private int _maximumY = -1;

    public TilemapForegroundGeometryAnalyzer(int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        _width = width;
        _height = height;
        _minimumX = width;
        _minimumY = height;
        var pixelCount = checked((long)width * height);
        _foregroundMask = new byte[checked((int)((pixelCount + 7L) / 8L))];
        _rowMinimumX = new int[height];
        _rowMaximumX = new int[height];
        _columnMinimumY = new int[width];
        _columnMaximumY = new int[width];
        Array.Fill(_rowMinimumX, width);
        Array.Fill(_rowMaximumX, -1);
        Array.Fill(_columnMinimumY, height);
        Array.Fill(_columnMaximumY, -1);
    }

    public void Add(int x, int y)
    {
        if ((uint)x >= (uint)_width || (uint)y >= (uint)_height)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        var pixelIndex = checked((long)y * _width + x);
        var byteIndex = checked((int)(pixelIndex >> 3));
        var bit = (byte)(1 << (int)(pixelIndex & 7L));
        if ((_foregroundMask[byteIndex] & bit) != 0)
        {
            return;
        }

        _foregroundMask[byteIndex] |= bit;
        _minimumX = Math.Min(_minimumX, x);
        _minimumY = Math.Min(_minimumY, y);
        _maximumX = Math.Max(_maximumX, x);
        _maximumY = Math.Max(_maximumY, y);
        _rowMinimumX[y] = Math.Min(_rowMinimumX[y], x);
        _rowMaximumX[y] = Math.Max(_rowMaximumX[y], x);
        _columnMinimumY[x] = Math.Min(_columnMinimumY[x], y);
        _columnMaximumY[x] = Math.Max(_columnMaximumY[x], y);
    }

    public TilemapGeometryDiagnostics Analyze(
        double expectedAspectRatio,
        double allowedDeviationPercent,
        string validatedImagePath)
    {
        if (_maximumX < _minimumX || _maximumY < _minimumY)
        {
            throw new InvalidOperationException("Tilemap 前景几何分析器没有可分析像素。");
        }

        if (!double.IsFinite(expectedAspectRatio) || expectedAspectRatio <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedAspectRatio));
        }

        if (!double.IsFinite(allowedDeviationPercent) || allowedDeviationPercent < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(allowedDeviationPercent));
        }

        var actualWidth = _maximumX - _minimumX + 1;
        var actualHeight = _maximumY - _minimumY + 1;
        var actualAspectRatio = actualWidth / (double)actualHeight;
        var cornerOccupancy = CalculateCornerOccupancy(actualWidth, actualHeight);
        var rotationAngleEstimate = EstimateRotationAngle();
        var diamondScore = CalculateDiamondScore(
            actualWidth,
            actualHeight,
            cornerOccupancy,
            rotationAngleEstimate);
        return new TilemapGeometryDiagnostics(
            new TilemapForegroundBoundingBox(
                _minimumX,
                _minimumY,
                actualWidth,
                actualHeight),
            actualWidth,
            actualHeight,
            actualAspectRatio,
            expectedAspectRatio,
            allowedDeviationPercent,
            cornerOccupancy,
            rotationAngleEstimate,
            diamondScore,
            Path.GetFullPath(validatedImagePath));
    }

    private TilemapCornerOccupancy CalculateCornerOccupancy(int actualWidth, int actualHeight)
    {
        var anchorWidth = Math.Max(1, (int)Math.Ceiling(actualWidth / 8d));
        var anchorHeight = Math.Max(1, (int)Math.Ceiling(actualHeight / 8d));
        return new TilemapCornerOccupancy(
            CalculateOccupancy(
                _minimumX,
                _minimumY,
                anchorWidth,
                anchorHeight),
            CalculateOccupancy(
                _maximumX - anchorWidth + 1,
                _minimumY,
                anchorWidth,
                anchorHeight),
            CalculateOccupancy(
                _minimumX,
                _maximumY - anchorHeight + 1,
                anchorWidth,
                anchorHeight),
            CalculateOccupancy(
                _maximumX - anchorWidth + 1,
                _maximumY - anchorHeight + 1,
                anchorWidth,
                anchorHeight));
    }

    private double CalculateOccupancy(int startX, int startY, int width, int height)
    {
        long occupied = 0;
        var area = checked((long)width * height);
        for (var y = startY; y < startY + height; y++)
        {
            for (var x = startX; x < startX + width; x++)
            {
                if (IsForeground(x, y))
                {
                    occupied++;
                }
            }
        }

        return occupied * 100d / area;
    }

    private double EstimateRotationAngle()
    {
        var hull = BuildConvexHull(BuildBoundaryPoints());
        if (hull.Count < 2)
        {
            return 0d;
        }

        double cosineSum = 0d;
        double sineSum = 0d;
        double totalWeight = 0d;
        for (var index = 0; index < hull.Count; index++)
        {
            var start = hull[index];
            var end = hull[(index + 1) % hull.Count];
            var deltaX = end.X - start.X;
            var deltaY = end.Y - start.Y;
            var length = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
            if (length <= 0.5d)
            {
                continue;
            }

            var angle = Math.Atan2(deltaY, deltaX);
            cosineSum += length * Math.Cos(4d * angle);
            sineSum += length * Math.Sin(4d * angle);
            totalWeight += length;
        }

        if (totalWeight <= 0d || Math.Sqrt(cosineSum * cosineSum + sineSum * sineSum) < totalWeight * 0.05d)
        {
            return 0d;
        }

        var degrees = Math.Atan2(sineSum, cosineSum) * 180d / Math.PI / 4d;
        while (degrees < 0d)
        {
            degrees += 90d;
        }

        while (degrees >= 90d)
        {
            degrees -= 90d;
        }

        return degrees <= 45d ? degrees : 90d - degrees;
    }

    private double CalculateDiamondScore(
        int actualWidth,
        int actualHeight,
        TilemapCornerOccupancy cornerOccupancy,
        double rotationAngleEstimate)
    {
        var angleScore = Clamp01((rotationAngleEstimate - 30d) / 15d);
        var profileScore = (CalculateRowTriangleFit(actualWidth) +
                            CalculateColumnTriangleFit(actualHeight)) / 2d;
        var cornerVacancyScore = Clamp01(1d - cornerOccupancy.AveragePercent / 100d);
        var tipCenteringScore = CalculateTipCenteringScore(actualWidth, actualHeight);
        return Clamp01(
            profileScore * 0.45d +
            cornerVacancyScore * 0.25d +
            tipCenteringScore * 0.20d +
            angleScore * 0.10d);
    }

    private double CalculateRowTriangleFit(int actualWidth)
    {
        double error = 0d;
        var sampleCount = 0;
        var actualHeight = _maximumY - _minimumY + 1;
        for (var y = _minimumY; y <= _maximumY; y++)
        {
            if (_rowMaximumX[y] < _rowMinimumX[y])
            {
                continue;
            }

            var normalizedPosition = (y - _minimumY + 0.5d) / actualHeight;
            var expected = 1d - Math.Abs(2d * normalizedPosition - 1d);
            var actual = (_rowMaximumX[y] - _rowMinimumX[y] + 1d) / actualWidth;
            error += Math.Abs(actual - expected);
            sampleCount++;
        }

        return sampleCount == 0
            ? 0d
            : Clamp01(1d - error / sampleCount / 0.35d);
    }

    private double CalculateColumnTriangleFit(int actualHeight)
    {
        double error = 0d;
        var sampleCount = 0;
        var actualWidth = _maximumX - _minimumX + 1;
        for (var x = _minimumX; x <= _maximumX; x++)
        {
            if (_columnMaximumY[x] < _columnMinimumY[x])
            {
                continue;
            }

            var normalizedPosition = (x - _minimumX + 0.5d) / actualWidth;
            var expected = 1d - Math.Abs(2d * normalizedPosition - 1d);
            var actual = (_columnMaximumY[x] - _columnMinimumY[x] + 1d) / actualHeight;
            error += Math.Abs(actual - expected);
            sampleCount++;
        }

        return sampleCount == 0
            ? 0d
            : Clamp01(1d - error / sampleCount / 0.35d);
    }

    private double CalculateTipCenteringScore(int actualWidth, int actualHeight)
    {
        var centerX = (_minimumX + _maximumX) / 2d;
        var centerY = (_minimumY + _maximumY) / 2d;
        var topCenter = (_rowMinimumX[_minimumY] + _rowMaximumX[_minimumY]) / 2d;
        var bottomCenter = (_rowMinimumX[_maximumY] + _rowMaximumX[_maximumY]) / 2d;
        var leftCenter = (_columnMinimumY[_minimumX] + _columnMaximumY[_minimumX]) / 2d;
        var rightCenter = (_columnMinimumY[_maximumX] + _columnMaximumY[_maximumX]) / 2d;
        return (
            CenteringScore(topCenter, centerX, actualWidth) +
            CenteringScore(bottomCenter, centerX, actualWidth) +
            CenteringScore(leftCenter, centerY, actualHeight) +
            CenteringScore(rightCenter, centerY, actualHeight)) / 4d;
    }

    private IReadOnlyList<GeometryPoint> BuildBoundaryPoints()
    {
        var keys = new HashSet<long>();
        var points = new List<GeometryPoint>(checked(2 * (_width + _height)));
        for (var y = _minimumY; y <= _maximumY; y++)
        {
            AddBoundaryPoint(_rowMinimumX[y], y, keys, points);
            AddBoundaryPoint(_rowMaximumX[y], y, keys, points);
        }

        for (var x = _minimumX; x <= _maximumX; x++)
        {
            AddBoundaryPoint(x, _columnMinimumY[x], keys, points);
            AddBoundaryPoint(x, _columnMaximumY[x], keys, points);
        }

        return points;
    }

    private void AddBoundaryPoint(
        int x,
        int y,
        ISet<long> keys,
        ICollection<GeometryPoint> points)
    {
        if (x < 0 || y < 0 || x >= _width || y >= _height)
        {
            return;
        }

        var key = checked((long)y * _width + x);
        if (keys.Add(key))
        {
            points.Add(new GeometryPoint(x + 0.5d, y + 0.5d));
        }
    }

    private static List<GeometryPoint> BuildConvexHull(IReadOnlyList<GeometryPoint> source)
    {
        if (source.Count <= 1)
        {
            return source.ToList();
        }

        var points = source
            .OrderBy(point => point.X)
            .ThenBy(point => point.Y)
            .ToArray();
        var lower = new List<GeometryPoint>(points.Length);
        foreach (var point in points)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], point) <= 0d)
            {
                lower.RemoveAt(lower.Count - 1);
            }

            lower.Add(point);
        }

        var upper = new List<GeometryPoint>(points.Length);
        for (var index = points.Length - 1; index >= 0; index--)
        {
            var point = points[index];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], point) <= 0d)
            {
                upper.RemoveAt(upper.Count - 1);
            }

            upper.Add(point);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    private bool IsForeground(int x, int y)
    {
        var pixelIndex = checked((long)y * _width + x);
        var byteIndex = checked((int)(pixelIndex >> 3));
        var bit = (byte)(1 << (int)(pixelIndex & 7L));
        return (_foregroundMask[byteIndex] & bit) != 0;
    }

    private static double Cross(GeometryPoint origin, GeometryPoint a, GeometryPoint b) =>
        (a.X - origin.X) * (b.Y - origin.Y) -
        (a.Y - origin.Y) * (b.X - origin.X);

    private static double CenteringScore(double value, double center, int span) =>
        Clamp01(1d - Math.Abs(value - center) / Math.Max(1d, span / 2d));

    private static double Clamp01(double value) => Math.Clamp(value, 0d, 1d);

    private readonly record struct GeometryPoint(double X, double Y);
}

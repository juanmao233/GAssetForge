using GAssetForge.Core.Generation;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace GAssetForge.App.Controls;

public sealed class TilemapGridSurface : FrameworkElement
{
    public const double MinimumZoom = 0.25;
    public const double MaximumZoom = 4.0;
    public const double DefaultCellSize = 64d;
    public const double DefaultDiamondAngle = 53.13010235415598d;
    private const int MaximumLinesPerFamily = 4096;

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background),
        typeof(Brush),
        typeof(TilemapGridSurface),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinorGridBrushProperty = DependencyProperty.Register(
        nameof(MinorGridBrush),
        typeof(Brush),
        typeof(TilemapGridSurface),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MajorGridBrushProperty = DependencyProperty.Register(
        nameof(MajorGridBrush),
        typeof(Brush),
        typeof(TilemapGridSurface),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty XAxisBrushProperty = DependencyProperty.Register(
        nameof(XAxisBrush),
        typeof(Brush),
        typeof(TilemapGridSurface),
        new FrameworkPropertyMetadata(Brushes.IndianRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty YAxisBrushProperty = DependencyProperty.Register(
        nameof(YAxisBrush),
        typeof(Brush),
        typeof(TilemapGridSurface),
        new FrameworkPropertyMetadata(Brushes.MediumSeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoverCellBrushProperty = DependencyProperty.Register(
        nameof(HoverCellBrush),
        typeof(Brush),
        typeof(TilemapGridSurface),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoverCellBorderBrushProperty = DependencyProperty.Register(
        nameof(HoverCellBorderBrush),
        typeof(Brush),
        typeof(TilemapGridSurface),
        new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CellSizeProperty = DependencyProperty.Register(
        nameof(CellSize),
        typeof(double),
        typeof(TilemapGridSurface),
        new FrameworkPropertyMetadata(
            DefaultCellSize,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnGridMetricChanged,
            (_, value) => CoerceFiniteDouble((double)value, DefaultCellSize, 8d, 512d)));

    public static readonly DependencyProperty DiamondAngleProperty = DependencyProperty.Register(
        nameof(DiamondAngle),
        typeof(double),
        typeof(TilemapGridSurface),
        new FrameworkPropertyMetadata(
            DefaultDiamondAngle,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnGridMetricChanged,
            (_, value) => CoerceFiniteDouble((double)value, DefaultDiamondAngle, 15d, 165d)));

    public static readonly DependencyProperty GridRotationProperty = DependencyProperty.Register(
        nameof(GridRotation),
        typeof(double),
        typeof(TilemapGridSurface),
        new FrameworkPropertyMetadata(
            0d,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnGridMetricChanged,
            (_, value) => CoerceFiniteDouble((double)value, 0d, -180d, 180d)));

    public static readonly DependencyProperty MajorLineIntervalProperty = DependencyProperty.Register(
        nameof(MajorLineInterval),
        typeof(int),
        typeof(TilemapGridSurface),
        new FrameworkPropertyMetadata(
            8,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnGridMetricChanged,
            (_, value) => Math.Clamp((int)value, 1, 64)));

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom),
        typeof(double),
        typeof(TilemapGridSurface),
        new FrameworkPropertyMetadata(
            1d,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnZoomChanged,
            (_, value) => CoerceFiniteDouble((double)value, 1d, MinimumZoom, MaximumZoom)));

    public static readonly DependencyProperty PanXProperty = DependencyProperty.Register(
        nameof(PanX),
        typeof(double),
        typeof(TilemapGridSurface),
        new FrameworkPropertyMetadata(
            0d,
            FrameworkPropertyMetadataOptions.AffectsRender,
            null,
            (_, value) => double.IsFinite((double)value) ? value : 0d));

    public static readonly DependencyProperty PanYProperty = DependencyProperty.Register(
        nameof(PanY),
        typeof(double),
        typeof(TilemapGridSurface),
        new FrameworkPropertyMetadata(
            0d,
            FrameworkPropertyMetadataOptions.AffectsRender,
            null,
            (_, value) => double.IsFinite((double)value) ? value : 0d));

    private static readonly DependencyPropertyKey ZoomTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(ZoomText),
        typeof(string),
        typeof(TilemapGridSurface),
        new PropertyMetadata("100%"));

    public static readonly DependencyProperty ZoomTextProperty = ZoomTextPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey HoveredCellTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(HoveredCellText),
        typeof(string),
        typeof(TilemapGridSurface),
        new PropertyMetadata("—"));

    public static readonly DependencyProperty HoveredCellTextProperty = HoveredCellTextPropertyKey.DependencyProperty;

    private bool _isPanning;
    private bool _hasHoveredCell;
    private Point _lastPointerPosition;
    private int _hoveredCellX;
    private int _hoveredCellY;

    public TilemapGridSurface()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
    }

    public Brush Background
    {
        get => (Brush)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public Brush MinorGridBrush
    {
        get => (Brush)GetValue(MinorGridBrushProperty);
        set => SetValue(MinorGridBrushProperty, value);
    }

    public Brush MajorGridBrush
    {
        get => (Brush)GetValue(MajorGridBrushProperty);
        set => SetValue(MajorGridBrushProperty, value);
    }

    public Brush XAxisBrush
    {
        get => (Brush)GetValue(XAxisBrushProperty);
        set => SetValue(XAxisBrushProperty, value);
    }

    public Brush YAxisBrush
    {
        get => (Brush)GetValue(YAxisBrushProperty);
        set => SetValue(YAxisBrushProperty, value);
    }

    public Brush HoverCellBrush
    {
        get => (Brush)GetValue(HoverCellBrushProperty);
        set => SetValue(HoverCellBrushProperty, value);
    }

    public Brush HoverCellBorderBrush
    {
        get => (Brush)GetValue(HoverCellBorderBrushProperty);
        set => SetValue(HoverCellBorderBrushProperty, value);
    }

    public double CellSize
    {
        get => (double)GetValue(CellSizeProperty);
        set => SetValue(CellSizeProperty, value);
    }

    public double DiamondAngle
    {
        get => (double)GetValue(DiamondAngleProperty);
        set => SetValue(DiamondAngleProperty, value);
    }

    public double GridRotation
    {
        get => (double)GetValue(GridRotationProperty);
        set => SetValue(GridRotationProperty, value);
    }

    public int MajorLineInterval
    {
        get => (int)GetValue(MajorLineIntervalProperty);
        set => SetValue(MajorLineIntervalProperty, value);
    }

    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public double PanX
    {
        get => (double)GetValue(PanXProperty);
        set => SetValue(PanXProperty, value);
    }

    public double PanY
    {
        get => (double)GetValue(PanYProperty);
        set => SetValue(PanYProperty, value);
    }

    public string ZoomText => (string)GetValue(ZoomTextProperty);

    public string HoveredCellText => (string)GetValue(HoveredCellTextProperty);

    public (int X, int Y) GetCellAt(Point point)
    {
        if (!TryGetGridCoordinates(point, out var gridX, out var gridY))
        {
            return (0, 0);
        }

        return (
            FloorToCellIndex(SnapNearInteger(gridX)),
            FloorToCellIndex(SnapNearInteger(gridY)));
    }

    public (double X, double Y) GetGridCoordinates(Point point) =>
        TryGetGridCoordinates(point, out var gridX, out var gridY)
            ? (gridX, gridY)
            : (double.NaN, double.NaN);

    public Point GetScreenPoint(double gridX, double gridY)
    {
        if (!double.IsFinite(gridX) || !double.IsFinite(gridY) ||
            !TryGetBasis(out var axisA, out var axisB))
        {
            return new Point(double.NaN, double.NaN);
        }

        return GridToScreen(gridX, gridY, GetOrigin(), axisA, axisB);
    }

    public Point GetCellCenter(int cellX, int cellY) =>
        GetScreenPoint(cellX + 0.5d, cellY + 0.5d);

    public void ZoomBy(double factor)
    {
        ZoomAt(new Point(ActualWidth / 2d, ActualHeight / 2d), factor);
    }

    public void ZoomAt(Point anchor, double factor)
    {
        if (!double.IsFinite(anchor.X) || !double.IsFinite(anchor.Y) ||
            !double.IsFinite(factor) || factor <= 0)
        {
            return;
        }

        if (!TryGetGridCoordinates(anchor, out var gridX, out var gridY))
        {
            return;
        }

        var oldZoom = Zoom;
        var requestedZoom = Math.Clamp(oldZoom * factor, MinimumZoom, MaximumZoom);
        if (Math.Abs(requestedZoom - oldZoom) < 0.0001)
        {
            return;
        }

        SetCurrentValue(ZoomProperty, requestedZoom);
        if (!TryGetBasis(out var axisA, out var axisB))
        {
            return;
        }

        var relative = GridToVector(gridX, gridY, axisA, axisB);
        SetCurrentValue(PanXProperty, anchor.X - ActualWidth / 2d - relative.X);
        SetCurrentValue(PanYProperty, anchor.Y - ActualHeight / 2d - relative.Y);
        UpdateHoveredCell(anchor);
    }

    public void ResetView()
    {
        SetCurrentValue(ZoomProperty, 1d);
        SetCurrentValue(PanXProperty, 0d);
        SetCurrentValue(PanYProperty, 0d);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        drawingContext.DrawRectangle(Background, null, new Rect(0, 0, width, height));

        if (!TryGetBasis(out var axisA, out var axisB))
        {
            return;
        }

        var origin = GetOrigin();
        if (!TryGetVisibleGridBounds(width, height, origin, axisA, axisB, out var bounds))
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var onePhysicalPixel = 1d / Math.Max(dpi.DpiScaleX, dpi.DpiScaleY);
        var minorPen = CreatePen(MinorGridBrush, onePhysicalPixel);
        var majorPen = CreatePen(MajorGridBrush, onePhysicalPixel);
        var xAxisPen = CreatePen(XAxisBrush, onePhysicalPixel * 2d);
        var yAxisPen = CreatePen(YAxisBrush, onePhysicalPixel * 2d);

        var (firstA, lastA, stepA) = CreateVisibleIndexRange(bounds.MinimumA, bounds.MaximumA);
        for (var axisIndex = (long)firstA; axisIndex <= lastA; axisIndex += stepA)
        {
            var index = (int)axisIndex;
            if (index == 0)
            {
                continue;
            }

            drawingContext.DrawLine(
                IsMajorLine(index) ? majorPen : minorPen,
                GridToScreen(index, bounds.MinimumB, origin, axisA, axisB),
                GridToScreen(index, bounds.MaximumB, origin, axisA, axisB));
        }

        var (firstB, lastB, stepB) = CreateVisibleIndexRange(bounds.MinimumB, bounds.MaximumB);
        for (var axisIndex = (long)firstB; axisIndex <= lastB; axisIndex += stepB)
        {
            var index = (int)axisIndex;
            if (index == 0)
            {
                continue;
            }

            drawingContext.DrawLine(
                IsMajorLine(index) ? majorPen : minorPen,
                GridToScreen(bounds.MinimumA, index, origin, axisA, axisB),
                GridToScreen(bounds.MaximumA, index, origin, axisA, axisB));
        }

        if (_hasHoveredCell)
        {
            drawingContext.DrawGeometry(
                HoverCellBrush,
                CreatePen(HoverCellBorderBrush, onePhysicalPixel),
                CreateCellGeometry(_hoveredCellX, _hoveredCellY, origin, axisA, axisB));
        }

        drawingContext.DrawLine(
            xAxisPen,
            GridToScreen(bounds.MinimumA, 0d, origin, axisA, axisB),
            GridToScreen(bounds.MaximumA, 0d, origin, axisA, axisB));
        drawingContext.DrawLine(
            yAxisPen,
            GridToScreen(0d, bounds.MinimumB, origin, axisA, axisB),
            GridToScreen(0d, bounds.MaximumB, origin, axisA, axisB));

        drawingContext.DrawEllipse(
            HoverCellBorderBrush,
            null,
            origin,
            2.5,
            2.5);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        ZoomAt(e.GetPosition(this), e.Delta > 0 ? 1.15d : 1d / 1.15d);
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        _lastPointerPosition = e.GetPosition(this);
        if (!CaptureMouse())
        {
            return;
        }

        _isPanning = true;
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        if (_isPanning)
        {
            if (e.MiddleButton != MouseButtonState.Pressed)
            {
                EndPan();
            }
            else
            {
                var delta = position - _lastPointerPosition;
                SetCurrentValue(PanXProperty, PanX + delta.X);
                SetCurrentValue(PanYProperty, PanY + delta.Y);
                _lastPointerPosition = position;
            }

            e.Handled = true;
        }

        UpdateHoveredCell(position);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Middle)
        {
            EndPan();
            e.Handled = true;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_isPanning)
        {
            ClearHoveredCell();
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndPan();
    }

    private static void OnGridMetricChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var surface = (TilemapGridSurface)dependencyObject;
        surface.UpdateHoveredCell(Mouse.GetPosition(surface));
    }

    private static void OnZoomChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var surface = (TilemapGridSurface)dependencyObject;
        surface.SetValue(ZoomTextPropertyKey, $"{(double)e.NewValue:P0}");
        surface.UpdateHoveredCell(Mouse.GetPosition(surface));
    }

    private void UpdateHoveredCell(Point position)
    {
        if (!IsMouseOver && !_isPanning)
        {
            return;
        }

        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y))
        {
            ClearHoveredCell();
            return;
        }

        var (cellX, cellY) = GetCellAt(position);
        if (_hasHoveredCell && cellX == _hoveredCellX && cellY == _hoveredCellY)
        {
            return;
        }

        (_hoveredCellX, _hoveredCellY) = (cellX, cellY);
        _hasHoveredCell = true;
        SetValue(HoveredCellTextPropertyKey, $"{_hoveredCellX}, {_hoveredCellY}");
        InvalidateVisual();
    }

    private void EndPan()
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        Cursor = Cursors.Cross;
        if (!IsMouseOver)
        {
            ClearHoveredCell();
        }
    }

    private bool IsMajorLine(int index) => index % MajorLineInterval == 0;

    private Point GetOrigin() => new(ActualWidth / 2d + PanX, ActualHeight / 2d + PanY);

    private bool TryGetBasis(out Vector axisA, out Vector axisB)
    {
        var basis = TilemapGeometryCalculator.CalculateAxes(
            CellSize * Zoom,
            DiamondAngle,
            GridRotation);
        axisA = new Vector(basis.AxisAX, basis.AxisAY);
        axisB = new Vector(basis.AxisBX, basis.AxisBY);
        var determinant = Cross(axisA, axisB);
        return IsFinite(axisA) && IsFinite(axisB) &&
               double.IsFinite(determinant) && Math.Abs(determinant) > 0.000000001d;
    }

    private bool TryGetGridCoordinates(Point point, out double gridA, out double gridB)
    {
        if (!TryGetBasis(out var axisA, out var axisB))
        {
            gridA = 0d;
            gridB = 0d;
            return false;
        }

        return TryScreenToGrid(point, GetOrigin(), axisA, axisB, out gridA, out gridB);
    }

    private static bool TryScreenToGrid(
        Point point,
        Point origin,
        Vector axisA,
        Vector axisB,
        out double gridA,
        out double gridB)
    {
        var determinant = Cross(axisA, axisB);
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            !double.IsFinite(determinant) || Math.Abs(determinant) <= 0.000000001d)
        {
            gridA = 0d;
            gridB = 0d;
            return false;
        }

        var offsetX = point.X - origin.X;
        var offsetY = point.Y - origin.Y;
        gridA = (offsetX * axisB.Y - offsetY * axisB.X) / determinant;
        gridB = (axisA.X * offsetY - axisA.Y * offsetX) / determinant;
        return double.IsFinite(gridA) && double.IsFinite(gridB);
    }

    private static bool TryGetVisibleGridBounds(
        double width,
        double height,
        Point origin,
        Vector axisA,
        Vector axisB,
        out GridBounds bounds)
    {
        var corners = new[]
        {
            new Point(0d, 0d),
            new Point(width, 0d),
            new Point(0d, height),
            new Point(width, height)
        };

        var minimumA = double.PositiveInfinity;
        var maximumA = double.NegativeInfinity;
        var minimumB = double.PositiveInfinity;
        var maximumB = double.NegativeInfinity;
        foreach (var corner in corners)
        {
            if (!TryScreenToGrid(corner, origin, axisA, axisB, out var gridA, out var gridB))
            {
                bounds = default;
                return false;
            }

            minimumA = Math.Min(minimumA, gridA);
            maximumA = Math.Max(maximumA, gridA);
            minimumB = Math.Min(minimumB, gridB);
            maximumB = Math.Max(maximumB, gridB);
        }

        bounds = new GridBounds(minimumA - 1d, maximumA + 1d, minimumB - 1d, maximumB + 1d);
        return true;
    }

    private static (int First, int Last, int Step) CreateVisibleIndexRange(double minimum, double maximum)
    {
        var first = FloorToCellIndex(minimum);
        var last = CeilingToCellIndex(maximum);
        if (first > last)
        {
            (first, last) = (last, first);
        }

        var count = (long)last - first + 1L;
        var step = count <= MaximumLinesPerFamily
            ? 1
            : (int)Math.Ceiling(count / (double)MaximumLinesPerFamily);
        return (first, last, Math.Max(1, step));
    }

    private static StreamGeometry CreateCellGeometry(
        int cellA,
        int cellB,
        Point origin,
        Vector axisA,
        Vector axisB)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(GridToScreen(cellA, cellB, origin, axisA, axisB), true, true);
            context.LineTo(GridToScreen(cellA + 1d, cellB, origin, axisA, axisB), true, false);
            context.LineTo(GridToScreen(cellA + 1d, cellB + 1d, origin, axisA, axisB), true, false);
            context.LineTo(GridToScreen(cellA, cellB + 1d, origin, axisA, axisB), true, false);
        }

        if (geometry.CanFreeze)
        {
            geometry.Freeze();
        }

        return geometry;
    }

    private static Point GridToScreen(
        double gridA,
        double gridB,
        Point origin,
        Vector axisA,
        Vector axisB)
    {
        var vector = GridToVector(gridA, gridB, axisA, axisB);
        return new Point(origin.X + vector.X, origin.Y + vector.Y);
    }

    private static Vector GridToVector(double gridA, double gridB, Vector axisA, Vector axisB) =>
        new(
            gridA * axisA.X + gridB * axisB.X,
            gridA * axisA.Y + gridB * axisB.Y);

    private static double Cross(Vector left, Vector right) => left.X * right.Y - left.Y * right.X;

    private static bool IsFinite(Vector vector) => double.IsFinite(vector.X) && double.IsFinite(vector.Y);

    private static double SnapNearInteger(double value)
    {
        var nearest = Math.Round(value);
        return Math.Abs(value - nearest) <= 0.000000001d ? nearest : value;
    }

    private static int FloorToCellIndex(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }

        var floor = Math.Floor(value);
        if (floor <= int.MinValue)
        {
            return int.MinValue;
        }

        return floor >= int.MaxValue ? int.MaxValue : (int)floor;
    }

    private static int CeilingToCellIndex(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }

        var ceiling = Math.Ceiling(value);
        if (ceiling <= int.MinValue)
        {
            return int.MinValue;
        }

        return ceiling >= int.MaxValue ? int.MaxValue : (int)ceiling;
    }

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        return pen;
    }

    private void ClearHoveredCell()
    {
        _hasHoveredCell = false;
        SetValue(HoveredCellTextPropertyKey, "—");
        InvalidateVisual();
    }

    private static double CoerceFiniteDouble(double value, double fallback, double minimum, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private readonly record struct GridBounds(
        double MinimumA,
        double MaximumA,
        double MinimumB,
        double MaximumB);
}

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GAssetForge.App.Controls;

/// <summary>
/// Navigable image preview whose checker-grid guides and editable reference cell share the exact
/// same source-pixel coordinate space as the displayed image.
/// </summary>
public sealed class CheckerGridImagePreviewControl : ImagePreviewControl
{
    private const double MinimumCellSize = 0.25d;
    private const double MaximumCellSize = 4096d;
    private const double CornerHandleRadius = 5d;
    private const double CornerHitTolerance = 11d;

    public static readonly DependencyProperty SourcePixelWidthProperty = DependencyProperty.Register(
        nameof(SourcePixelWidth),
        typeof(int),
        typeof(CheckerGridImagePreviewControl),
        new FrameworkPropertyMetadata(
            0,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnSourceGeometryChanged));

    public static readonly DependencyProperty SourcePixelHeightProperty = DependencyProperty.Register(
        nameof(SourcePixelHeight),
        typeof(int),
        typeof(CheckerGridImagePreviewControl),
        new FrameworkPropertyMetadata(
            0,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnSourceGeometryChanged));

    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid),
        typeof(bool),
        typeof(CheckerGridImagePreviewControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CellSizeProperty = DependencyProperty.Register(
        nameof(CellSize),
        typeof(double),
        typeof(CheckerGridImagePreviewControl),
        new FrameworkPropertyMetadata(
            16d,
            FrameworkPropertyMetadataOptions.AffectsRender |
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnAlignmentGeometryChanged));

    public static readonly DependencyProperty OffsetXProperty = DependencyProperty.Register(
        nameof(OffsetX),
        typeof(double),
        typeof(CheckerGridImagePreviewControl),
        new FrameworkPropertyMetadata(
            0d,
            FrameworkPropertyMetadataOptions.AffectsRender |
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnAlignmentGeometryChanged));

    public static readonly DependencyProperty OffsetYProperty = DependencyProperty.Register(
        nameof(OffsetY),
        typeof(double),
        typeof(CheckerGridImagePreviewControl),
        new FrameworkPropertyMetadata(
            0d,
            FrameworkPropertyMetadataOptions.AffectsRender |
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnAlignmentGeometryChanged));

    public static readonly DependencyProperty IsRulerEditingEnabledProperty = DependencyProperty.Register(
        nameof(IsRulerEditingEnabled),
        typeof(bool),
        typeof(CheckerGridImagePreviewControl),
        new FrameworkPropertyMetadata(
            false,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnRulerEditingEnabledChanged));

    private Rect _rulerSourceRect = Rect.Empty;
    private RulerDragMode _dragMode;
    private Point _dragStartSource;
    private Rect _dragStartRulerSourceRect;
    private RulerEditSnapshot _editSnapshot;
    private bool _isApplyingRulerValues;

    public event EventHandler? RulerEditStarted;

    public event EventHandler? RulerEditCompleted;

    public event EventHandler? RulerEditCanceled;

    public int SourcePixelWidth
    {
        get => (int)GetValue(SourcePixelWidthProperty);
        set => SetValue(SourcePixelWidthProperty, value);
    }

    public int SourcePixelHeight
    {
        get => (int)GetValue(SourcePixelHeightProperty);
        set => SetValue(SourcePixelHeightProperty, value);
    }

    public bool ShowGrid
    {
        get => (bool)GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    public double CellSize
    {
        get => (double)GetValue(CellSizeProperty);
        set => SetValue(CellSizeProperty, value);
    }

    public double OffsetX
    {
        get => (double)GetValue(OffsetXProperty);
        set => SetValue(OffsetXProperty, value);
    }

    public double OffsetY
    {
        get => (double)GetValue(OffsetYProperty);
        set => SetValue(OffsetYProperty, value);
    }

    public bool IsRulerEditingEnabled
    {
        get => (bool)GetValue(IsRulerEditingEnabledProperty);
        set => SetValue(IsRulerEditingEnabledProperty, value);
    }

    public Rect RulerSourceRect
    {
        get
        {
            EnsureRulerSourceRect();
            return _rulerSourceRect;
        }
    }

    protected override bool ResetOnLeftDoubleClick => false;

    public Point SourceToViewport(Point sourcePoint)
    {
        if (!IsFinite(sourcePoint) || !TryGetSourceGeometry(out var imageRect, out var sourceSize))
        {
            return new Point(double.NaN, double.NaN);
        }

        return new Point(
            imageRect.Left + sourcePoint.X * imageRect.Width / sourceSize.Width,
            imageRect.Top + sourcePoint.Y * imageRect.Height / sourceSize.Height);
    }

    public Point ViewportToSource(Point viewportPoint)
    {
        if (!IsFinite(viewportPoint) || !TryGetSourceGeometry(out var imageRect, out var sourceSize))
        {
            return new Point(double.NaN, double.NaN);
        }

        return new Point(
            (viewportPoint.X - imageRect.Left) * sourceSize.Width / imageRect.Width,
            (viewportPoint.Y - imageRect.Top) * sourceSize.Height / imageRect.Height);
    }

    public void SetRulerFromViewportPoints(Point start, Point end)
    {
        if (!IsFinite(start) || !IsFinite(end))
        {
            throw new ArgumentOutOfRangeException(nameof(start), "标尺端点必须是有限坐标。");
        }

        if (!TryGetSourceGeometry(out _, out var sourceSize))
        {
            return;
        }

        var sourceStart = ClampSourcePoint(ViewportToSource(start), sourceSize);
        var sourceEnd = ClampSourcePoint(ViewportToSource(end), sourceSize);
        ApplyRulerSourceRect(CreateSquareFromDrag(sourceStart, sourceEnd, sourceSize));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!ShowGrid || Source is null || !TryGetSourceSize(out var sourceSize))
        {
            return;
        }

        CheckerGridRenderer.Draw(
            drawingContext,
            ViewportContentRect,
            (int)Math.Round(sourceSize.Width),
            (int)Math.Round(sourceSize.Height),
            CellSize,
            OffsetX,
            OffsetY);

        if (IsRulerEditingEnabled)
        {
            DrawRuler(drawingContext);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.Handled || !CanEditRuler())
        {
            return;
        }

        Focus();
        EnsureRulerSourceRect();
        var viewportPoint = e.GetPosition(this);
        if (!ViewportContentRect.Contains(viewportPoint))
        {
            return;
        }

        _dragMode = HitTestRuler(viewportPoint);
        if (_dragMode == RulerDragMode.None)
        {
            _dragMode = RulerDragMode.Create;
        }

        _dragStartSource = ClampSourcePoint(ViewportToSource(viewportPoint), GetSourceSize());
        _dragStartRulerSourceRect = _rulerSourceRect;
        _editSnapshot = new RulerEditSnapshot(CellSize, OffsetX, OffsetY, _rulerSourceRect);
        if (!CaptureMouse())
        {
            _dragMode = RulerDragMode.None;
            return;
        }

        Cursor = ResolveCursor(_dragMode);
        RulerEditStarted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Handled)
        {
            return;
        }

        var viewportPoint = e.GetPosition(this);
        if (_dragMode != RulerDragMode.None && IsMouseCaptured)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                CancelRulerEdit();
                e.Handled = true;
                return;
            }

            var sourceSize = GetSourceSize();
            var sourcePoint = ClampSourcePoint(ViewportToSource(viewportPoint), sourceSize);
            var ruler = _dragMode switch
            {
                RulerDragMode.Create => CreateSquareFromDrag(_dragStartSource, sourcePoint, sourceSize),
                RulerDragMode.Move => MoveRuler(sourcePoint, sourceSize),
                _ => ResizeRuler(sourcePoint, sourceSize)
            };
            ApplyRulerSourceRect(ruler);
            e.Handled = true;
            return;
        }

        Cursor = CanEditRuler()
            ? ResolveCursor(HitTestRuler(viewportPoint), fallbackToCross: true)
            : Cursors.Arrow;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Handled || e.ChangedButton != MouseButton.Left || _dragMode == RulerDragMode.None)
        {
            return;
        }

        CompleteRulerEdit();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (_dragMode != RulerDragMode.None)
        {
            CancelRulerEdit();
        }

        base.OnMouseRightButtonDown(e);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragMode != RulerDragMode.None)
        {
            CancelRulerEdit(releaseCapture: false);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape || _dragMode == RulerDragMode.None)
        {
            return;
        }

        CancelRulerEdit();
        e.Handled = true;
    }

    private static void OnSourceGeometryChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        var control = (CheckerGridImagePreviewControl)dependencyObject;
        if (control._dragMode != RulerDragMode.None)
        {
            control.CancelRulerEdit();
        }

        control._rulerSourceRect = Rect.Empty;
    }

    private static void OnAlignmentGeometryChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        var control = (CheckerGridImagePreviewControl)dependencyObject;
        if (!control._isApplyingRulerValues && control._dragMode == RulerDragMode.None)
        {
            control._rulerSourceRect = Rect.Empty;
        }
    }

    private static void OnRulerEditingEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        var control = (CheckerGridImagePreviewControl)dependencyObject;
        if (!(bool)e.NewValue && control._dragMode != RulerDragMode.None)
        {
            control.CancelRulerEdit();
        }

        control.InvalidateVisual();
    }

    private bool CanEditRuler() =>
        IsRulerEditingEnabled && ShowGrid && Source is not null && TryGetSourceGeometry(out _, out _);

    private void EnsureRulerSourceRect()
    {
        if (!TryGetSourceGeometry(out var imageRect, out var sourceSize))
        {
            _rulerSourceRect = Rect.Empty;
            return;
        }

        if (IsValidRulerRect(_rulerSourceRect, sourceSize))
        {
            return;
        }

        var side = double.IsFinite(CellSize)
            ? Math.Clamp(CellSize, MinimumCellSize, MaximumCellSize)
            : 16d;
        side = Math.Min(side, Math.Min(sourceSize.Width, sourceSize.Height));
        if (side <= 0d)
        {
            _rulerSourceRect = Rect.Empty;
            return;
        }

        var visibleRect = Rect.Intersect(imageRect, new Rect(RenderSize));
        var viewportCenter = visibleRect.IsEmpty
            ? new Point(imageRect.Left + imageRect.Width / 2d, imageRect.Top + imageRect.Height / 2d)
            : new Point(visibleRect.Left + visibleRect.Width / 2d, visibleRect.Top + visibleRect.Height / 2d);
        var sourceCenter = ViewportToSource(viewportCenter);
        var normalizedOffsetX = PositiveModulo(double.IsFinite(OffsetX) ? OffsetX : 0d, side);
        var normalizedOffsetY = PositiveModulo(double.IsFinite(OffsetY) ? OffsetY : 0d, side);
        var left = FindVisibleCellOrigin(
            normalizedOffsetX,
            side,
            sourceSize.Width,
            sourceCenter.X - side / 2d);
        var top = FindVisibleCellOrigin(
            normalizedOffsetY,
            side,
            sourceSize.Height,
            sourceCenter.Y - side / 2d);
        _rulerSourceRect = new Rect(left, top, side, side);
    }

    private void DrawRuler(DrawingContext context)
    {
        EnsureRulerSourceRect();
        if (_rulerSourceRect.IsEmpty || ViewportContentRect.IsEmpty)
        {
            return;
        }

        var topLeft = SourceToViewport(_rulerSourceRect.TopLeft);
        var bottomRight = SourceToViewport(_rulerSourceRect.BottomRight);
        if (!IsFinite(topLeft) || !IsFinite(bottomRight))
        {
            return;
        }

        var rulerRect = new Rect(topLeft, bottomRight);
        var fill = new SolidColorBrush(Color.FromArgb(44, 0, 210, 255));
        var shadowPen = new Pen(new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)), 4d);
        var borderPen = new Pen(new SolidColorBrush(Color.FromArgb(255, 0, 210, 255)), 2d);
        fill.Freeze();
        shadowPen.Freeze();
        borderPen.Freeze();

        context.PushClip(new RectangleGeometry(ViewportContentRect));
        context.DrawRectangle(fill, shadowPen, rulerRect);
        context.DrawRectangle(null, borderPen, rulerRect);
        foreach (var corner in GetRulerCorners(rulerRect))
        {
            context.DrawEllipse(Brushes.White, borderPen, corner.Point, CornerHandleRadius, CornerHandleRadius);
        }

        DrawRulerLabel(context, rulerRect);
        context.Pop();
    }

    private void DrawRulerLabel(DrawingContext context, Rect rulerRect)
    {
        var text = $"{CellSize:0.###} × {CellSize:0.###} px";
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            10d,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var labelRect = new Rect(
            rulerRect.Left + 5d,
            rulerRect.Top + 5d,
            formatted.Width + 10d,
            formatted.Height + 5d);
        var background = new SolidColorBrush(Color.FromArgb(210, 18, 24, 31));
        background.Freeze();
        context.DrawRoundedRectangle(background, null, labelRect, 3d, 3d);
        context.DrawText(formatted, new Point(labelRect.Left + 5d, labelRect.Top + 2d));
    }

    private RulerDragMode HitTestRuler(Point viewportPoint)
    {
        if (!IsRulerEditingEnabled || _rulerSourceRect.IsEmpty || !IsFinite(viewportPoint))
        {
            return RulerDragMode.None;
        }

        var rulerRect = GetRulerViewportRect();
        if (rulerRect.IsEmpty)
        {
            return RulerDragMode.None;
        }

        var corner = GetRulerCorners(rulerRect)
            .Select(candidate => (candidate.Mode, Distance: (candidate.Point - viewportPoint).Length))
            .Where(candidate => candidate.Distance <= CornerHitTolerance)
            .OrderBy(candidate => candidate.Distance)
            .FirstOrDefault();
        if (corner.Mode != RulerDragMode.None)
        {
            return corner.Mode;
        }

        return rulerRect.Contains(viewportPoint) ? RulerDragMode.Move : RulerDragMode.None;
    }

    private Rect GetRulerViewportRect()
    {
        var topLeft = SourceToViewport(_rulerSourceRect.TopLeft);
        var bottomRight = SourceToViewport(_rulerSourceRect.BottomRight);
        return IsFinite(topLeft) && IsFinite(bottomRight)
            ? new Rect(topLeft, bottomRight)
            : Rect.Empty;
    }

    private static IReadOnlyList<RulerCorner> GetRulerCorners(Rect rulerRect) =>
    [
        new(RulerDragMode.ResizeTopLeft, rulerRect.TopLeft),
        new(RulerDragMode.ResizeTopRight, rulerRect.TopRight),
        new(RulerDragMode.ResizeBottomRight, rulerRect.BottomRight),
        new(RulerDragMode.ResizeBottomLeft, rulerRect.BottomLeft)
    ];

    private Rect MoveRuler(Point currentSource, Size sourceSize)
    {
        var delta = currentSource - _dragStartSource;
        var left = Math.Clamp(
            _dragStartRulerSourceRect.Left + delta.X,
            0d,
            Math.Max(0d, sourceSize.Width - _dragStartRulerSourceRect.Width));
        var top = Math.Clamp(
            _dragStartRulerSourceRect.Top + delta.Y,
            0d,
            Math.Max(0d, sourceSize.Height - _dragStartRulerSourceRect.Height));
        return new Rect(left, top, _dragStartRulerSourceRect.Width, _dragStartRulerSourceRect.Height);
    }

    private Rect ResizeRuler(Point currentSource, Size sourceSize)
    {
        var start = _dragStartRulerSourceRect;
        return _dragMode switch
        {
            RulerDragMode.ResizeTopLeft => CreateSquareFromFixedCorner(
                start.BottomRight,
                currentSource,
                -1d,
                -1d,
                sourceSize),
            RulerDragMode.ResizeTopRight => CreateSquareFromFixedCorner(
                start.BottomLeft,
                currentSource,
                1d,
                -1d,
                sourceSize),
            RulerDragMode.ResizeBottomRight => CreateSquareFromFixedCorner(
                start.TopLeft,
                currentSource,
                1d,
                1d,
                sourceSize),
            RulerDragMode.ResizeBottomLeft => CreateSquareFromFixedCorner(
                start.TopRight,
                currentSource,
                -1d,
                1d,
                sourceSize),
            _ => start
        };
    }

    private static Rect CreateSquareFromFixedCorner(
        Point fixedCorner,
        Point current,
        double horizontalDirection,
        double verticalDirection,
        Size sourceSize)
    {
        var requestedHorizontal = Math.Max(0d, (current.X - fixedCorner.X) * horizontalDirection);
        var requestedVertical = Math.Max(0d, (current.Y - fixedCorner.Y) * verticalDirection);
        var requestedSide = Math.Max(requestedHorizontal, requestedVertical);
        var maximumHorizontal = horizontalDirection > 0d
            ? sourceSize.Width - fixedCorner.X
            : fixedCorner.X;
        var maximumVertical = verticalDirection > 0d
            ? sourceSize.Height - fixedCorner.Y
            : fixedCorner.Y;
        var maximumSide = Math.Min(
            MaximumCellSize,
            Math.Max(0d, Math.Min(maximumHorizontal, maximumVertical)));
        if (maximumSide <= 0d)
        {
            return Rect.Empty;
        }

        var minimumSide = Math.Min(MinimumCellSize, maximumSide);
        var side = Math.Clamp(requestedSide, minimumSide, maximumSide);
        var left = horizontalDirection > 0d ? fixedCorner.X : fixedCorner.X - side;
        var top = verticalDirection > 0d ? fixedCorner.Y : fixedCorner.Y - side;
        return new Rect(left, top, side, side);
    }

    private static Rect CreateSquareFromDrag(Point start, Point end, Size sourceSize)
    {
        var horizontalDirection = end.X >= start.X ? 1d : -1d;
        var verticalDirection = end.Y >= start.Y ? 1d : -1d;
        return CreateSquareFromFixedCorner(
            start,
            end,
            horizontalDirection,
            verticalDirection,
            sourceSize);
    }

    private void ApplyRulerSourceRect(Rect sourceRect)
    {
        if (!TryGetSourceSize(out var sourceSize) || sourceRect.IsEmpty)
        {
            return;
        }

        var side = Math.Min(sourceRect.Width, sourceRect.Height);
        var availableSide = Math.Min(sourceSize.Width, sourceSize.Height);
        side = Math.Clamp(side, Math.Min(MinimumCellSize, availableSide), Math.Min(MaximumCellSize, availableSide));
        var left = Math.Clamp(sourceRect.Left, 0d, Math.Max(0d, sourceSize.Width - side));
        var top = Math.Clamp(sourceRect.Top, 0d, Math.Max(0d, sourceSize.Height - side));
        _rulerSourceRect = new Rect(left, top, side, side);

        _isApplyingRulerValues = true;
        try
        {
            SetCurrentValue(CellSizeProperty, side);
            SetCurrentValue(OffsetXProperty, PositiveModulo(left, side));
            SetCurrentValue(OffsetYProperty, PositiveModulo(top, side));
        }
        finally
        {
            _isApplyingRulerValues = false;
        }

        InvalidateVisual();
    }

    private void CompleteRulerEdit()
    {
        if (_dragMode == RulerDragMode.None)
        {
            return;
        }

        _dragMode = RulerDragMode.None;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        Cursor = Cursors.Arrow;
        RulerEditCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void CancelRulerEdit(bool releaseCapture = true)
    {
        if (_dragMode == RulerDragMode.None)
        {
            return;
        }

        _dragMode = RulerDragMode.None;
        _rulerSourceRect = _editSnapshot.RulerSourceRect;
        _isApplyingRulerValues = true;
        try
        {
            SetCurrentValue(CellSizeProperty, _editSnapshot.CellSize);
            SetCurrentValue(OffsetXProperty, _editSnapshot.OffsetX);
            SetCurrentValue(OffsetYProperty, _editSnapshot.OffsetY);
        }
        finally
        {
            _isApplyingRulerValues = false;
        }

        if (releaseCapture && IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        Cursor = Cursors.Arrow;
        InvalidateVisual();
        RulerEditCanceled?.Invoke(this, EventArgs.Empty);
    }

    private bool TryGetSourceGeometry(out Rect imageRect, out Size sourceSize)
    {
        imageRect = ViewportContentRect;
        return !imageRect.IsEmpty && imageRect.Width > 0d && imageRect.Height > 0d &&
               TryGetSourceSize(out sourceSize);
    }

    private bool TryGetSourceSize(out Size sourceSize)
    {
        var bitmap = Source as BitmapSource;
        var width = SourcePixelWidth > 0
            ? SourcePixelWidth
            : bitmap?.PixelWidth ?? Source?.Width ?? 0d;
        var height = SourcePixelHeight > 0
            ? SourcePixelHeight
            : bitmap?.PixelHeight ?? Source?.Height ?? 0d;
        sourceSize = new Size(width, height);
        return double.IsFinite(width) && double.IsFinite(height) && width > 0d && height > 0d;
    }

    private Size GetSourceSize() => TryGetSourceSize(out var sourceSize) ? sourceSize : Size.Empty;

    private static Point ClampSourcePoint(Point point, Size sourceSize) => new(
        Math.Clamp(point.X, 0d, sourceSize.Width),
        Math.Clamp(point.Y, 0d, sourceSize.Height));

    private static bool IsValidRulerRect(Rect ruler, Size sourceSize) =>
        !ruler.IsEmpty &&
        double.IsFinite(ruler.Left) && double.IsFinite(ruler.Top) &&
        double.IsFinite(ruler.Width) && double.IsFinite(ruler.Height) &&
        ruler.Width > 0d && Math.Abs(ruler.Width - ruler.Height) <= 0.000001d &&
        ruler.Left >= 0d && ruler.Top >= 0d &&
        ruler.Right <= sourceSize.Width + 0.000001d &&
        ruler.Bottom <= sourceSize.Height + 0.000001d;

    private static double FindVisibleCellOrigin(
        double normalizedOffset,
        double cellSize,
        double sourceExtent,
        double targetOrigin)
    {
        var firstIndex = Math.Ceiling(-normalizedOffset / cellSize);
        var lastIndex = Math.Floor((sourceExtent - cellSize - normalizedOffset) / cellSize);
        if (lastIndex < firstIndex)
        {
            return Math.Clamp(normalizedOffset, 0d, Math.Max(0d, sourceExtent - cellSize));
        }

        var targetIndex = Math.Round((targetOrigin - normalizedOffset) / cellSize);
        var index = Math.Clamp(targetIndex, firstIndex, lastIndex);
        return normalizedOffset + index * cellSize;
    }

    private static double PositiveModulo(double value, double divisor)
    {
        var remainder = value % divisor;
        return remainder < 0d ? remainder + divisor : remainder;
    }

    private static bool IsFinite(Point point) => double.IsFinite(point.X) && double.IsFinite(point.Y);

    private static Cursor ResolveCursor(RulerDragMode mode, bool fallbackToCross = false) => mode switch
    {
        RulerDragMode.Move => Cursors.SizeAll,
        RulerDragMode.ResizeTopLeft or RulerDragMode.ResizeBottomRight => Cursors.SizeNWSE,
        RulerDragMode.ResizeTopRight or RulerDragMode.ResizeBottomLeft => Cursors.SizeNESW,
        RulerDragMode.Create => Cursors.Cross,
        _ => fallbackToCross ? Cursors.Cross : Cursors.Arrow
    };

    private enum RulerDragMode
    {
        None,
        Create,
        Move,
        ResizeTopLeft,
        ResizeTopRight,
        ResizeBottomRight,
        ResizeBottomLeft
    }

    private readonly record struct RulerCorner(RulerDragMode Mode, Point Point);

    private readonly record struct RulerEditSnapshot(
        double CellSize,
        double OffsetX,
        double OffsetY,
        Rect RulerSourceRect);
}

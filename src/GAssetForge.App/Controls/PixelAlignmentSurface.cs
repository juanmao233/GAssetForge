using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GAssetForge.App.Controls;

public sealed class PixelAlignmentSurface : ImageViewportSurface
{
    private const double RulerThickness = 24d;
    private const double EdgeHitTolerance = 7d;
    private const double PerspectiveHandleRadius = 6d;
    private const double PerspectiveHitTolerance = 11d;
    private const int MaximumRenderedGridLines = 512;

    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath),
        typeof(string),
        typeof(PixelAlignmentSurface),
        new FrameworkPropertyMetadata(string.Empty, OnSourcePathChanged));

    public static readonly DependencyProperty CropLeftProperty = RegisterCropProperty(nameof(CropLeft));
    public static readonly DependencyProperty CropTopProperty = RegisterCropProperty(nameof(CropTop));
    public static readonly DependencyProperty CropRightProperty = RegisterCropProperty(nameof(CropRight));
    public static readonly DependencyProperty CropBottomProperty = RegisterCropProperty(nameof(CropBottom));

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns),
        typeof(int),
        typeof(PixelAlignmentSurface),
        new FrameworkPropertyMetadata(32, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows),
        typeof(int),
        typeof(PixelAlignmentSurface),
        new FrameworkPropertyMetadata(32, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid),
        typeof(bool),
        typeof(PixelAlignmentSurface),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PerspectiveCorrectionEnabledProperty = DependencyProperty.Register(
        nameof(PerspectiveCorrectionEnabled),
        typeof(bool),
        typeof(PixelAlignmentSurface),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PerspectiveTopLeftXProperty = RegisterPerspectiveCoordinateProperty(nameof(PerspectiveTopLeftX));
    public static readonly DependencyProperty PerspectiveTopLeftYProperty = RegisterPerspectiveCoordinateProperty(nameof(PerspectiveTopLeftY));
    public static readonly DependencyProperty PerspectiveTopRightXProperty = RegisterPerspectiveCoordinateProperty(nameof(PerspectiveTopRightX));
    public static readonly DependencyProperty PerspectiveTopRightYProperty = RegisterPerspectiveCoordinateProperty(nameof(PerspectiveTopRightY));
    public static readonly DependencyProperty PerspectiveBottomRightXProperty = RegisterPerspectiveCoordinateProperty(nameof(PerspectiveBottomRightX));
    public static readonly DependencyProperty PerspectiveBottomRightYProperty = RegisterPerspectiveCoordinateProperty(nameof(PerspectiveBottomRightY));
    public static readonly DependencyProperty PerspectiveBottomLeftXProperty = RegisterPerspectiveCoordinateProperty(nameof(PerspectiveBottomLeftX));
    public static readonly DependencyProperty PerspectiveBottomLeftYProperty = RegisterPerspectiveCoordinateProperty(nameof(PerspectiveBottomLeftY));

    private BitmapSource? _bitmap;
    private DragMode _dragMode;

    public PixelAlignmentSurface()
    {
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    public string SourcePath
    {
        get => (string)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    public int CropLeft
    {
        get => (int)GetValue(CropLeftProperty);
        set => SetValue(CropLeftProperty, value);
    }

    public int CropTop
    {
        get => (int)GetValue(CropTopProperty);
        set => SetValue(CropTopProperty, value);
    }

    public int CropRight
    {
        get => (int)GetValue(CropRightProperty);
        set => SetValue(CropRightProperty, value);
    }

    public int CropBottom
    {
        get => (int)GetValue(CropBottomProperty);
        set => SetValue(CropBottomProperty, value);
    }

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public int Rows
    {
        get => (int)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public bool ShowGrid
    {
        get => (bool)GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    public bool PerspectiveCorrectionEnabled
    {
        get => (bool)GetValue(PerspectiveCorrectionEnabledProperty);
        set => SetValue(PerspectiveCorrectionEnabledProperty, value);
    }

    public double PerspectiveTopLeftX
    {
        get => (double)GetValue(PerspectiveTopLeftXProperty);
        set => SetValue(PerspectiveTopLeftXProperty, value);
    }

    public double PerspectiveTopLeftY
    {
        get => (double)GetValue(PerspectiveTopLeftYProperty);
        set => SetValue(PerspectiveTopLeftYProperty, value);
    }

    public double PerspectiveTopRightX
    {
        get => (double)GetValue(PerspectiveTopRightXProperty);
        set => SetValue(PerspectiveTopRightXProperty, value);
    }

    public double PerspectiveTopRightY
    {
        get => (double)GetValue(PerspectiveTopRightYProperty);
        set => SetValue(PerspectiveTopRightYProperty, value);
    }

    public double PerspectiveBottomRightX
    {
        get => (double)GetValue(PerspectiveBottomRightXProperty);
        set => SetValue(PerspectiveBottomRightXProperty, value);
    }

    public double PerspectiveBottomRightY
    {
        get => (double)GetValue(PerspectiveBottomRightYProperty);
        set => SetValue(PerspectiveBottomRightYProperty, value);
    }

    public double PerspectiveBottomLeftX
    {
        get => (double)GetValue(PerspectiveBottomLeftXProperty);
        set => SetValue(PerspectiveBottomLeftXProperty, value);
    }

    public double PerspectiveBottomLeftY
    {
        get => (double)GetValue(PerspectiveBottomLeftYProperty);
        set => SetValue(PerspectiveBottomLeftYProperty, value);
    }

    public int LoadedSourcePixelWidth => _bitmap?.PixelWidth ?? 0;

    public int LoadedSourcePixelHeight => _bitmap?.PixelHeight ?? 0;

    protected override bool HasViewportContent =>
        _bitmap is { PixelWidth: > 0, PixelHeight: > 0 };

    protected override Size ViewportContentSize => _bitmap is null
        ? Size.Empty
        : new Size(_bitmap.PixelWidth, _bitmap.PixelHeight);

    protected override Rect ViewportBounds
    {
        get
        {
            var availableWidth = Math.Max(1d, ActualWidth - RulerThickness - 16d);
            var availableHeight = Math.Max(1d, ActualHeight - RulerThickness - 16d);
            return new Rect(RulerThickness, RulerThickness, availableWidth, availableHeight);
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var bounds = new Rect(RenderSize);
        drawingContext.DrawRectangle(ResolveBrush("App.InputBackgroundBrush", Color.FromRgb(29, 33, 40)), null, bounds);
        if (_bitmap is null || _bitmap.PixelWidth <= 0 || _bitmap.PixelHeight <= 0)
        {
            DrawCenteredText(drawingContext, "选择图片后在此拖动四条裁切标尺", bounds, 13d, ResolveBrush("App.TextMutedBrush", Colors.Gray));
            return;
        }

        var imageRect = GetImageRect();
        if (imageRect.IsEmpty)
        {
            return;
        }

        drawingContext.DrawImage(_bitmap, imageRect);
        DrawRulers(drawingContext, imageRect);
        var crop = GetCropRect(imageRect);
        DrawCropMask(drawingContext, imageRect, crop);
        DrawGrid(drawingContext, crop);

        var borderPen = new Pen(ResolveBrush("App.TilemapGuideBrush", Color.FromRgb(0, 196, 204)), 1.5d);
        borderPen.Freeze();
        drawingContext.DrawRectangle(null, borderPen, crop);
        DrawPerspectiveOverlay(drawingContext, imageRect);

        var labelBounds = new Rect(crop.X + 6d, crop.Y + 6d, Math.Max(1d, crop.Width - 12d), 22d);
        DrawText(
            drawingContext,
            $"{Math.Max(1, CropRight - CropLeft)} × {Math.Max(1, CropBottom - CropTop)} px",
            labelBounds.TopLeft,
            11d,
            Brushes.White,
            ResolveBrush("App.TilemapOverlayBrush", Color.FromArgb(210, 25, 28, 35)));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.Handled)
        {
            return;
        }

        Focus();
        if (_bitmap is null)
        {
            return;
        }

        var point = e.GetPosition(this);
        _dragMode = HitTestPerspectiveVertex(point);
        if (_dragMode == DragMode.None)
        {
            _dragMode = HitTestCropEdge(point);
        }
        if (_dragMode == DragMode.None)
        {
            return;
        }

        CaptureMouse();
        Cursor = ResolveCursor(_dragMode);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Handled)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (_dragMode != DragMode.None && IsMouseCaptured)
        {
            if (IsPerspectiveDragMode(_dragMode))
            {
                UpdateDraggedPerspectiveVertex(point);
            }
            else
            {
                UpdateDraggedCropEdge(point);
            }

            e.Handled = true;
            return;
        }

        var hit = HitTestPerspectiveVertex(point);
        if (hit == DragMode.None)
        {
            hit = HitTestCropEdge(point);
        }

        Cursor = ResolveCursor(hit);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Handled)
        {
            return;
        }

        if (_dragMode == DragMode.None)
        {
            return;
        }

        _dragMode = DragMode.None;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        Cursor = Cursors.Arrow;
        e.Handled = true;
    }

    private static DependencyProperty RegisterCropProperty(string name) => DependencyProperty.Register(
        name,
        typeof(int),
        typeof(PixelAlignmentSurface),
        new FrameworkPropertyMetadata(
            0,
            FrameworkPropertyMetadataOptions.AffectsRender |
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private static DependencyProperty RegisterPerspectiveCoordinateProperty(string name) => DependencyProperty.Register(
        name,
        typeof(double),
        typeof(PixelAlignmentSurface),
        new FrameworkPropertyMetadata(
            0d,
            FrameworkPropertyMetadataOptions.AffectsRender |
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private static void OnSourcePathChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var surface = (PixelAlignmentSurface)dependencyObject;
        surface.LoadBitmap(e.NewValue as string);
    }

    private void LoadBitmap(string? path)
    {
        _bitmap = null;
        ResetView();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            InvalidateVisual();
            return;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            if (bitmap.CanFreeze)
            {
                bitmap.Freeze();
            }

            _bitmap = bitmap;
        }
        catch
        {
            try
            {
                _bitmap = LoadBitmapWithImageSharp(path);
            }
            catch
            {
                _bitmap = null;
            }
        }

        InvalidateVisual();
    }

    private static BitmapSource LoadBitmapWithImageSharp(string path)
    {
        using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Bgra32>(path);
        var stride = checked(image.Width * 4);
        var pixels = GC.AllocateUninitializedArray<byte>(checked(stride * image.Height));
        image.CopyPixelDataTo(pixels);
        var bitmap = BitmapSource.Create(
            image.Width,
            image.Height,
            96d,
            96d,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        if (bitmap.CanFreeze)
        {
            bitmap.Freeze();
        }

        return bitmap;
    }

    private Rect GetImageRect() => GetViewportContentRect();

    private Rect GetCropRect(Rect imageRect)
    {
        if (_bitmap is null)
        {
            return Rect.Empty;
        }

        var left = Math.Clamp(CropLeft, 0, Math.Max(0, _bitmap.PixelWidth - 1));
        var top = Math.Clamp(CropTop, 0, Math.Max(0, _bitmap.PixelHeight - 1));
        var right = Math.Clamp(CropRight, left + 1, _bitmap.PixelWidth);
        var bottom = Math.Clamp(CropBottom, top + 1, _bitmap.PixelHeight);
        return new Rect(
            imageRect.X + left * imageRect.Width / _bitmap.PixelWidth,
            imageRect.Y + top * imageRect.Height / _bitmap.PixelHeight,
            (right - left) * imageRect.Width / _bitmap.PixelWidth,
            (bottom - top) * imageRect.Height / _bitmap.PixelHeight);
    }

    private void DrawCropMask(DrawingContext context, Rect imageRect, Rect crop)
    {
        var mask = new SolidColorBrush(Color.FromArgb(145, 8, 10, 14));
        mask.Freeze();
        context.DrawRectangle(mask, null, new Rect(imageRect.Left, imageRect.Top, imageRect.Width, Math.Max(0d, crop.Top - imageRect.Top)));
        context.DrawRectangle(mask, null, new Rect(imageRect.Left, crop.Bottom, imageRect.Width, Math.Max(0d, imageRect.Bottom - crop.Bottom)));
        context.DrawRectangle(mask, null, new Rect(imageRect.Left, crop.Top, Math.Max(0d, crop.Left - imageRect.Left), crop.Height));
        context.DrawRectangle(mask, null, new Rect(crop.Right, crop.Top, Math.Max(0d, imageRect.Right - crop.Right), crop.Height));
    }

    private void DrawGrid(DrawingContext context, Rect crop)
    {
        if (!ShowGrid || Columns <= 0 || Rows <= 0 || crop.IsEmpty)
        {
            return;
        }

        var gridBrush = new SolidColorBrush(Color.FromArgb(130, 255, 255, 255));
        gridBrush.Freeze();
        var pen = new Pen(gridBrush, 0.75d);
        pen.Freeze();
        var columnStep = Math.Max(1, (int)Math.Ceiling(Columns / (double)MaximumRenderedGridLines));
        var rowStep = Math.Max(1, (int)Math.Ceiling(Rows / (double)MaximumRenderedGridLines));
        if (crop.Width / Columns >= 2d)
        {
            columnStep = Math.Max(1, columnStep);
        }
        else
        {
            columnStep = Math.Max(columnStep, (int)Math.Ceiling(2d * Columns / Math.Max(1d, crop.Width)));
        }

        if (crop.Height / Rows >= 2d)
        {
            rowStep = Math.Max(1, rowStep);
        }
        else
        {
            rowStep = Math.Max(rowStep, (int)Math.Ceiling(2d * Rows / Math.Max(1d, crop.Height)));
        }

        for (var column = columnStep; column < Columns; column += columnStep)
        {
            var x = crop.Left + crop.Width * column / Columns;
            context.DrawLine(pen, new Point(x, crop.Top), new Point(x, crop.Bottom));
        }

        for (var row = rowStep; row < Rows; row += rowStep)
        {
            var y = crop.Top + crop.Height * row / Rows;
            context.DrawLine(pen, new Point(crop.Left, y), new Point(crop.Right, y));
        }
    }

    private void DrawPerspectiveOverlay(DrawingContext context, Rect imageRect)
    {
        if (!PerspectiveCorrectionEnabled || _bitmap is null)
        {
            return;
        }

        var points = GetPerspectiveScreenPoints(imageRect);
        var guideBrush = new SolidColorBrush(Color.FromRgb(255, 166, 64));
        guideBrush.Freeze();
        var guidePen = new Pen(guideBrush, 2d);
        guidePen.Freeze();
        var fillBrush = new SolidColorBrush(Color.FromArgb(36, 255, 166, 64));
        fillBrush.Freeze();
        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            geometryContext.BeginFigure(points.TopLeft, isFilled: true, isClosed: true);
            geometryContext.LineTo(points.TopRight, isStroked: true, isSmoothJoin: false);
            geometryContext.LineTo(points.BottomRight, isStroked: true, isSmoothJoin: false);
            geometryContext.LineTo(points.BottomLeft, isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        context.DrawGeometry(fillBrush, guidePen, geometry);
        DrawPerspectiveHandle(context, points.TopLeft, "TL");
        DrawPerspectiveHandle(context, points.TopRight, "TR");
        DrawPerspectiveHandle(context, points.BottomRight, "BR");
        DrawPerspectiveHandle(context, points.BottomLeft, "BL");
    }

    private void DrawPerspectiveHandle(DrawingContext context, Point point, string label)
    {
        var fill = ResolveBrush("App.SurfaceRaisedBrush", Color.FromRgb(43, 48, 57));
        var outline = new Pen(new SolidColorBrush(Color.FromRgb(255, 166, 64)), 2d);
        if (outline.Brush.CanFreeze)
        {
            outline.Brush.Freeze();
        }

        outline.Freeze();
        context.DrawEllipse(fill, outline, point, PerspectiveHandleRadius, PerspectiveHandleRadius);
        var labelOrigin = new Point(point.X + 9d, point.Y - 16d);
        DrawText(
            context,
            label,
            labelOrigin,
            9d,
            Brushes.White,
            new SolidColorBrush(Color.FromArgb(220, 132, 76, 18)));
    }

    private PerspectiveScreenPoints GetPerspectiveScreenPoints(Rect imageRect) => new(
        SourceToScreen(PerspectiveTopLeftX, PerspectiveTopLeftY, imageRect),
        SourceToScreen(PerspectiveTopRightX, PerspectiveTopRightY, imageRect),
        SourceToScreen(PerspectiveBottomRightX, PerspectiveBottomRightY, imageRect),
        SourceToScreen(PerspectiveBottomLeftX, PerspectiveBottomLeftY, imageRect));

    private Point SourceToScreen(double sourceX, double sourceY, Rect imageRect)
    {
        if (_bitmap is null)
        {
            return default;
        }

        return new Point(
            imageRect.Left + sourceX * imageRect.Width / _bitmap.PixelWidth,
            imageRect.Top + sourceY * imageRect.Height / _bitmap.PixelHeight);
    }

    private void DrawRulers(DrawingContext context, Rect imageRect)
    {
        if (_bitmap is null)
        {
            return;
        }

        var rulerBrush = ResolveBrush("App.SurfaceRaisedBrush", Color.FromRgb(43, 48, 57));
        context.DrawRectangle(rulerBrush, null, new Rect(imageRect.Left, 0d, imageRect.Width, RulerThickness));
        context.DrawRectangle(rulerBrush, null, new Rect(0d, imageRect.Top, RulerThickness, imageRect.Height));
        var tickBrush = ResolveBrush("App.TextMutedBrush", Colors.Gray);
        var pen = new Pen(tickBrush, 1d);
        pen.Freeze();
        var pixelScaleX = imageRect.Width / _bitmap.PixelWidth;
        var pixelScaleY = imageRect.Height / _bitmap.PixelHeight;
        var stepX = CalculateRulerStep(pixelScaleX);
        var stepY = CalculateRulerStep(pixelScaleY);
        for (var x = 0; x <= _bitmap.PixelWidth; x += stepX)
        {
            var screenX = imageRect.Left + x * pixelScaleX;
            var major = x % (stepX * 5) == 0;
            context.DrawLine(
                pen,
                new Point(screenX, RulerThickness - (major ? 10d : 5d)),
                new Point(screenX, RulerThickness));
        }

        for (var y = 0; y <= _bitmap.PixelHeight; y += stepY)
        {
            var screenY = imageRect.Top + y * pixelScaleY;
            var major = y % (stepY * 5) == 0;
            context.DrawLine(
                pen,
                new Point(RulerThickness - (major ? 10d : 5d), screenY),
                new Point(RulerThickness, screenY));
        }
    }

    private static int CalculateRulerStep(double pixelScale)
    {
        if (pixelScale >= 8d)
        {
            return 1;
        }

        if (pixelScale >= 2d)
        {
            return 5;
        }

        return Math.Max(10, (int)Math.Ceiling(12d / Math.Max(pixelScale, 0.001d) / 10d) * 10);
    }

    private DragMode HitTestCropEdge(Point point)
    {
        if (_bitmap is null)
        {
            return DragMode.None;
        }

        var crop = GetCropRect(GetImageRect());
        var candidates = new[]
        {
            (Mode: DragMode.Left, Distance: Math.Abs(point.X - crop.Left), Along: point.Y >= crop.Top - EdgeHitTolerance && point.Y <= crop.Bottom + EdgeHitTolerance),
            (Mode: DragMode.Right, Distance: Math.Abs(point.X - crop.Right), Along: point.Y >= crop.Top - EdgeHitTolerance && point.Y <= crop.Bottom + EdgeHitTolerance),
            (Mode: DragMode.Top, Distance: Math.Abs(point.Y - crop.Top), Along: point.X >= crop.Left - EdgeHitTolerance && point.X <= crop.Right + EdgeHitTolerance),
            (Mode: DragMode.Bottom, Distance: Math.Abs(point.Y - crop.Bottom), Along: point.X >= crop.Left - EdgeHitTolerance && point.X <= crop.Right + EdgeHitTolerance)
        };
        var match = candidates
            .Where(candidate => candidate.Along && candidate.Distance <= EdgeHitTolerance)
            .OrderBy(candidate => candidate.Distance)
            .FirstOrDefault();
        return match.Mode;
    }

    private DragMode HitTestPerspectiveVertex(Point point)
    {
        if (!PerspectiveCorrectionEnabled || _bitmap is null)
        {
            return DragMode.None;
        }

        var points = GetPerspectiveScreenPoints(GetImageRect());
        var candidates = new[]
        {
            (Mode: DragMode.PerspectiveTopLeft, Point: points.TopLeft),
            (Mode: DragMode.PerspectiveTopRight, Point: points.TopRight),
            (Mode: DragMode.PerspectiveBottomRight, Point: points.BottomRight),
            (Mode: DragMode.PerspectiveBottomLeft, Point: points.BottomLeft)
        };
        var match = candidates
            .Select(candidate => (candidate.Mode, Distance: (candidate.Point - point).Length))
            .Where(candidate => candidate.Distance <= PerspectiveHitTolerance)
            .OrderBy(candidate => candidate.Distance)
            .FirstOrDefault();
        return match.Mode;
    }

    private void UpdateDraggedCropEdge(Point point)
    {
        if (_bitmap is null)
        {
            return;
        }

        var imageRect = GetImageRect();
        var sourceX = (int)Math.Round((point.X - imageRect.Left) * _bitmap.PixelWidth / imageRect.Width);
        var sourceY = (int)Math.Round((point.Y - imageRect.Top) * _bitmap.PixelHeight / imageRect.Height);
        switch (_dragMode)
        {
            case DragMode.Left:
                SetCurrentValue(CropLeftProperty, Math.Clamp(sourceX, 0, Math.Max(0, CropRight - 1)));
                break;
            case DragMode.Right:
                SetCurrentValue(CropRightProperty, Math.Clamp(sourceX, Math.Min(_bitmap.PixelWidth, CropLeft + 1), _bitmap.PixelWidth));
                break;
            case DragMode.Top:
                SetCurrentValue(CropTopProperty, Math.Clamp(sourceY, 0, Math.Max(0, CropBottom - 1)));
                break;
            case DragMode.Bottom:
                SetCurrentValue(CropBottomProperty, Math.Clamp(sourceY, Math.Min(_bitmap.PixelHeight, CropTop + 1), _bitmap.PixelHeight));
                break;
        }
    }

    private void UpdateDraggedPerspectiveVertex(Point point)
    {
        if (_bitmap is null)
        {
            return;
        }

        var imageRect = GetImageRect();
        var sourceX = Math.Round(
            (point.X - imageRect.Left) * _bitmap.PixelWidth / imageRect.Width,
            MidpointRounding.AwayFromZero);
        var sourceY = Math.Round(
            (point.Y - imageRect.Top) * _bitmap.PixelHeight / imageRect.Height,
            MidpointRounding.AwayFromZero);
        sourceX = Math.Clamp(sourceX, CropLeft, CropRight);
        sourceY = Math.Clamp(sourceY, CropTop, CropBottom);
        switch (_dragMode)
        {
            case DragMode.PerspectiveTopLeft:
                SetCurrentValue(PerspectiveTopLeftXProperty, sourceX);
                SetCurrentValue(PerspectiveTopLeftYProperty, sourceY);
                break;
            case DragMode.PerspectiveTopRight:
                SetCurrentValue(PerspectiveTopRightXProperty, sourceX);
                SetCurrentValue(PerspectiveTopRightYProperty, sourceY);
                break;
            case DragMode.PerspectiveBottomRight:
                SetCurrentValue(PerspectiveBottomRightXProperty, sourceX);
                SetCurrentValue(PerspectiveBottomRightYProperty, sourceY);
                break;
            case DragMode.PerspectiveBottomLeft:
                SetCurrentValue(PerspectiveBottomLeftXProperty, sourceX);
                SetCurrentValue(PerspectiveBottomLeftYProperty, sourceY);
                break;
        }
    }

    private static bool IsPerspectiveDragMode(DragMode mode) => mode is
        DragMode.PerspectiveTopLeft or
        DragMode.PerspectiveTopRight or
        DragMode.PerspectiveBottomRight or
        DragMode.PerspectiveBottomLeft;

    private static Cursor ResolveCursor(DragMode mode) => mode switch
    {
        DragMode.Left or DragMode.Right => Cursors.SizeWE,
        DragMode.Top or DragMode.Bottom => Cursors.SizeNS,
        DragMode.PerspectiveTopLeft or DragMode.PerspectiveTopRight or
            DragMode.PerspectiveBottomRight or DragMode.PerspectiveBottomLeft => Cursors.SizeAll,
        _ => Cursors.Arrow
    };

    private static Brush ResolveBrush(string resourceKey, Color fallback)
    {
        if (Application.Current?.TryFindResource(resourceKey) is Brush resourceBrush)
        {
            return resourceBrush;
        }

        var brush = new SolidColorBrush(fallback);
        brush.Freeze();
        return brush;
    }

    private void DrawCenteredText(
        DrawingContext context,
        string text,
        Rect bounds,
        double fontSize,
        Brush foreground)
    {
        var formatted = CreateFormattedText(text, fontSize, foreground);
        context.DrawText(
            formatted,
            new Point(
                bounds.Left + (bounds.Width - formatted.Width) / 2d,
                bounds.Top + (bounds.Height - formatted.Height) / 2d));
    }

    private void DrawText(
        DrawingContext context,
        string text,
        Point origin,
        double fontSize,
        Brush foreground,
        Brush background)
    {
        var formatted = CreateFormattedText(text, fontSize, foreground);
        context.DrawRoundedRectangle(
            background,
            null,
            new Rect(origin.X - 4d, origin.Y - 2d, formatted.Width + 8d, formatted.Height + 4d),
            3d,
            3d);
        context.DrawText(formatted, origin);
    }

    private FormattedText CreateFormattedText(string text, double fontSize, Brush foreground) => new(
        text,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        new Typeface("Segoe UI"),
        fontSize,
        foreground,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private enum DragMode
    {
        None,
        Left,
        Top,
        Right,
        Bottom,
        PerspectiveTopLeft,
        PerspectiveTopRight,
        PerspectiveBottomRight,
        PerspectiveBottomLeft
    }

    private readonly record struct PerspectiveScreenPoints(
        Point TopLeft,
        Point TopRight,
        Point BottomRight,
        Point BottomLeft);
}

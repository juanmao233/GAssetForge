using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GAssetForge.App.Controls;

public sealed class CheckerGridOverlay : FrameworkElement
{
    public static readonly DependencyProperty ImageSourceProperty = DependencyProperty.Register(
        nameof(ImageSource),
        typeof(ImageSource),
        typeof(CheckerGridOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SourcePixelWidthProperty = DependencyProperty.Register(
        nameof(SourcePixelWidth),
        typeof(int),
        typeof(CheckerGridOverlay),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SourcePixelHeightProperty = DependencyProperty.Register(
        nameof(SourcePixelHeight),
        typeof(int),
        typeof(CheckerGridOverlay),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid),
        typeof(bool),
        typeof(CheckerGridOverlay),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CellSizeProperty = DependencyProperty.Register(
        nameof(CellSize),
        typeof(int),
        typeof(CheckerGridOverlay),
        new FrameworkPropertyMetadata(16, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OffsetXProperty = DependencyProperty.Register(
        nameof(OffsetX),
        typeof(int),
        typeof(CheckerGridOverlay),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OffsetYProperty = DependencyProperty.Register(
        nameof(OffsetY),
        typeof(int),
        typeof(CheckerGridOverlay),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public ImageSource? ImageSource
    {
        get => (ImageSource?)GetValue(ImageSourceProperty);
        set => SetValue(ImageSourceProperty, value);
    }

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

    public int CellSize
    {
        get => (int)GetValue(CellSizeProperty);
        set => SetValue(CellSizeProperty, value);
    }

    public int OffsetX
    {
        get => (int)GetValue(OffsetXProperty);
        set => SetValue(OffsetXProperty, value);
    }

    public int OffsetY
    {
        get => (int)GetValue(OffsetYProperty);
        set => SetValue(OffsetYProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!ShowGrid || ImageSource is not BitmapSource bitmap ||
            bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 ||
            ActualWidth <= 0d || ActualHeight <= 0d)
        {
            return;
        }

        var hasSourcePixelSize = SourcePixelWidth > 0 && SourcePixelHeight > 0;
        var sourcePixelWidth = hasSourcePixelSize ? SourcePixelWidth : bitmap.PixelWidth;
        var sourcePixelHeight = hasSourcePixelSize ? SourcePixelHeight : bitmap.PixelHeight;
        var cellSize = Math.Clamp(CellSize, 2, 512);
        var scale = Math.Min(ActualWidth / sourcePixelWidth, ActualHeight / sourcePixelHeight);
        if (!double.IsFinite(scale) || scale <= 0d)
        {
            return;
        }

        var renderWidth = sourcePixelWidth * scale;
        var renderHeight = sourcePixelHeight * scale;
        var left = (ActualWidth - renderWidth) / 2d;
        var top = (ActualHeight - renderHeight) / 2d;
        var imageBounds = new Rect(left, top, renderWidth, renderHeight);
        drawingContext.PushClip(new RectangleGeometry(imageBounds));

        var shadowPen = new Pen(new SolidColorBrush(Color.FromArgb(155, 0, 0, 0)), 2d)
        {
            DashStyle = DashStyles.Dash
        };
        var guidePen = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), 1d)
        {
            DashStyle = DashStyles.Dash
        };
        shadowPen.Freeze();
        guidePen.Freeze();

        var normalizedOffsetX = PositiveModulo(OffsetX, cellSize);
        var normalizedOffsetY = PositiveModulo(OffsetY, cellSize);
        var firstX = normalizedOffsetX;
        while (firstX > 0)
        {
            firstX -= cellSize;
        }

        for (var pixelX = firstX; pixelX <= sourcePixelWidth; pixelX += cellSize)
        {
            var x = left + pixelX * scale;
            DrawGuideLine(drawingContext, shadowPen, guidePen, new Point(x, top), new Point(x, top + renderHeight));
        }

        var firstY = normalizedOffsetY;
        while (firstY > 0)
        {
            firstY -= cellSize;
        }

        for (var pixelY = firstY; pixelY <= sourcePixelHeight; pixelY += cellSize)
        {
            var y = top + pixelY * scale;
            DrawGuideLine(drawingContext, shadowPen, guidePen, new Point(left, y), new Point(left + renderWidth, y));
        }

        var originX = left + normalizedOffsetX * scale;
        var originY = top + normalizedOffsetY * scale;
        var originPen = new Pen(new SolidColorBrush(Color.FromArgb(245, 0, 210, 255)), 2d);
        originPen.Freeze();
        drawingContext.DrawEllipse(null, originPen, new Point(originX, originY), 4d, 4d);
        drawingContext.Pop();
    }

    private static void DrawGuideLine(
        DrawingContext context,
        Pen shadowPen,
        Pen guidePen,
        Point start,
        Point end)
    {
        context.DrawLine(shadowPen, start, end);
        context.DrawLine(guidePen, start, end);
    }

    private static int PositiveModulo(int value, int divisor)
    {
        var remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }
}

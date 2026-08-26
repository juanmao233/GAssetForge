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
        CheckerGridRenderer.Draw(
            drawingContext,
            new Rect(left, top, renderWidth, renderHeight),
            sourcePixelWidth,
            sourcePixelHeight,
            cellSize,
            OffsetX,
            OffsetY);
    }
}

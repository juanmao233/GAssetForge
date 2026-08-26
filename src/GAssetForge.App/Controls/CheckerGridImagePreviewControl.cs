using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GAssetForge.App.Controls;

/// <summary>
/// Navigable image preview whose checker-grid guides are rendered in the same transformed image
/// rectangle. The overlay therefore remains locked to source pixels while zooming and panning.
/// </summary>
public sealed class CheckerGridImagePreviewControl : ImagePreviewControl
{
    public static readonly DependencyProperty SourcePixelWidthProperty = DependencyProperty.Register(
        nameof(SourcePixelWidth),
        typeof(int),
        typeof(CheckerGridImagePreviewControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SourcePixelHeightProperty = DependencyProperty.Register(
        nameof(SourcePixelHeight),
        typeof(int),
        typeof(CheckerGridImagePreviewControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid),
        typeof(bool),
        typeof(CheckerGridImagePreviewControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CellSizeProperty = DependencyProperty.Register(
        nameof(CellSize),
        typeof(int),
        typeof(CheckerGridImagePreviewControl),
        new FrameworkPropertyMetadata(16, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OffsetXProperty = DependencyProperty.Register(
        nameof(OffsetX),
        typeof(int),
        typeof(CheckerGridImagePreviewControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OffsetYProperty = DependencyProperty.Register(
        nameof(OffsetY),
        typeof(int),
        typeof(CheckerGridImagePreviewControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

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
        if (!ShowGrid || Source is null)
        {
            return;
        }

        var bitmap = Source as BitmapSource;
        var sourceWidth = SourcePixelWidth > 0 ? SourcePixelWidth : bitmap?.PixelWidth ?? 0;
        var sourceHeight = SourcePixelHeight > 0 ? SourcePixelHeight : bitmap?.PixelHeight ?? 0;
        CheckerGridRenderer.Draw(
            drawingContext,
            ViewportContentRect,
            sourceWidth,
            sourceHeight,
            CellSize,
            OffsetX,
            OffsetY);
    }
}

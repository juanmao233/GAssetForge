using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GAssetForge.App.Controls;

/// <summary>
/// Lightweight reusable image preview with built-in wheel zoom, middle-button pan, and view reset.
/// Bind <see cref="Source"/> exactly like a WPF Image control.
/// </summary>
public class ImagePreviewControl : ImageViewportSurface
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(ImageSource),
        typeof(ImagePreviewControl),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnSourceChanged));

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background),
        typeof(Brush),
        typeof(ImagePreviewControl),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlaceholderForegroundProperty = DependencyProperty.Register(
        nameof(PlaceholderForeground),
        typeof(Brush),
        typeof(ImagePreviewControl),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText),
        typeof(string),
        typeof(ImagePreviewControl),
        new FrameworkPropertyMetadata("暂无预览图像", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ViewportPaddingProperty = DependencyProperty.Register(
        nameof(ViewportPadding),
        typeof(Thickness),
        typeof(ImagePreviewControl),
        new FrameworkPropertyMetadata(
            new Thickness(0d),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BitmapScalingModeProperty = DependencyProperty.Register(
        nameof(BitmapScalingMode),
        typeof(BitmapScalingMode),
        typeof(ImagePreviewControl),
        new FrameworkPropertyMetadata(
            BitmapScalingMode.NearestNeighbor,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnBitmapScalingModeChanged));

    public ImagePreviewControl()
    {
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode);
    }

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public Brush Background
    {
        get => (Brush)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public Brush PlaceholderForeground
    {
        get => (Brush)GetValue(PlaceholderForegroundProperty);
        set => SetValue(PlaceholderForegroundProperty, value);
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public Thickness ViewportPadding
    {
        get => (Thickness)GetValue(ViewportPaddingProperty);
        set => SetValue(ViewportPaddingProperty, value);
    }

    public BitmapScalingMode BitmapScalingMode
    {
        get => (BitmapScalingMode)GetValue(BitmapScalingModeProperty);
        set => SetValue(BitmapScalingModeProperty, value);
    }

    protected override bool HasViewportContent =>
        Source is not null && ViewportContentSize.Width > 0d && ViewportContentSize.Height > 0d;

    protected override Size ViewportContentSize => Source switch
    {
        BitmapSource bitmap when bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0 =>
            new Size(bitmap.PixelWidth, bitmap.PixelHeight),
        { Width: > 0d, Height: > 0d } image => new Size(image.Width, image.Height),
        _ => Size.Empty
    };

    protected override Rect ViewportBounds
    {
        get
        {
            var padding = ViewportPadding;
            var width = ActualWidth - padding.Left - padding.Right;
            var height = ActualHeight - padding.Top - padding.Bottom;
            return width <= 0d || height <= 0d
                ? Rect.Empty
                : new Rect(padding.Left, padding.Top, width, height);
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(Background, null, new Rect(RenderSize));
        if (Source is not null)
        {
            var imageRect = GetViewportContentRect();
            if (!imageRect.IsEmpty)
            {
                drawingContext.DrawImage(Source, imageRect);
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(PlaceholderText))
        {
            return;
        }

        var formatted = new FormattedText(
            PlaceholderText.Trim(),
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            12d,
            PlaceholderForeground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(
            formatted,
            new Point(
                Math.Max(0d, (ActualWidth - formatted.Width) / 2d),
                Math.Max(0d, (ActualHeight - formatted.Height) / 2d)));
    }

    private static void OnSourceChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        ((ImagePreviewControl)dependencyObject).ResetView();
    }

    private static void OnBitmapScalingModeChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        RenderOptions.SetBitmapScalingMode(
            (ImagePreviewControl)dependencyObject,
            (BitmapScalingMode)e.NewValue);
    }
}

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GAssetForge.Core.Imaging;

namespace GAssetForge.App.Controls;

/// <summary>
/// Interactive HSV saturation/value plane. Saturation increases left-to-right and value
/// decreases top-to-bottom. The control is independent of any particular dialog or tool.
/// </summary>
public sealed class HsvSaturationValueSurface : FrameworkElement
{
    private static readonly Brush SaturationOverlayBrush = Freeze(new LinearGradientBrush(
        Colors.White,
        Color.FromArgb(0, 255, 255, 255),
        new Point(0d, 0.5d),
        new Point(1d, 0.5d)));
    private static readonly Brush ValueOverlayBrush = Freeze(new LinearGradientBrush(
        Color.FromArgb(0, 0, 0, 0),
        Colors.Black,
        new Point(0.5d, 0d),
        new Point(0.5d, 1d)));
    private static readonly Pen MarkerShadowPen = Freeze(new Pen(Brushes.Black, 3d));
    private static readonly Pen MarkerHighlightPen = Freeze(new Pen(Brushes.White, 1.5d));
    private static readonly Pen BorderPen = Freeze(new Pen(Brushes.Black, 1d));

    public static readonly DependencyProperty HueProperty = DependencyProperty.Register(
        nameof(Hue),
        typeof(double),
        typeof(HsvSaturationValueSurface),
        new FrameworkPropertyMetadata(
            0d,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            null,
            CoerceHue));

    public static readonly DependencyProperty SaturationProperty = DependencyProperty.Register(
        nameof(Saturation),
        typeof(double),
        typeof(HsvSaturationValueSurface),
        new FrameworkPropertyMetadata(
            0d,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            null,
            CoerceUnit));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(HsvSaturationValueSurface),
        new FrameworkPropertyMetadata(
            1d,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            null,
            CoerceUnit));

    private bool _isDragging;
    private double _cachedHue = double.NaN;
    private Brush? _cachedHueBrush;

    public HsvSaturationValueSurface()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
    }

    public double Hue
    {
        get => (double)GetValue(HueProperty);
        set => SetValue(HueProperty, value);
    }

    public double Saturation
    {
        get => (double)GetValue(SaturationProperty);
        set => SetValue(SaturationProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var bounds = new Rect(RenderSize);
        if (bounds.Width <= 0d || bounds.Height <= 0d)
        {
            return;
        }

        drawingContext.DrawRectangle(GetHueBrush(), null, bounds);
        drawingContext.DrawRectangle(SaturationOverlayBrush, null, bounds);
        drawingContext.DrawRectangle(ValueOverlayBrush, null, bounds);

        var x = Math.Clamp(Saturation, 0d, 1d) * bounds.Width;
        var y = (1d - Math.Clamp(Value, 0d, 1d)) * bounds.Height;
        var center = new Point(
            Math.Clamp(x, 1d, Math.Max(1d, bounds.Width - 1d)),
            Math.Clamp(y, 1d, Math.Max(1d, bounds.Height - 1d)));
        drawingContext.DrawEllipse(null, MarkerShadowPen, center, 6d, 6d);
        drawingContext.DrawEllipse(null, MarkerHighlightPen, center, 6d, 6d);

        drawingContext.DrawRectangle(null, BorderPen, bounds);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        _isDragging = CaptureMouse();
        UpdateSelection(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateSelection(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_isDragging)
        {
            UpdateSelection(e.GetPosition(this));
            ReleaseMouseCapture();
            _isDragging = false;
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _isDragging = false;
        base.OnLostMouseCapture(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        const double step = 0.01d;
        switch (e.Key)
        {
            case Key.Left:
                SetCurrentValue(SaturationProperty, Saturation - step);
                break;
            case Key.Right:
                SetCurrentValue(SaturationProperty, Saturation + step);
                break;
            case Key.Up:
                SetCurrentValue(ValueProperty, Value + step);
                break;
            case Key.Down:
                SetCurrentValue(ValueProperty, Value - step);
                break;
            default:
                base.OnKeyDown(e);
                return;
        }

        e.Handled = true;
    }

    private void UpdateSelection(Point point)
    {
        if (ActualWidth <= 0d || ActualHeight <= 0d)
        {
            return;
        }

        SetCurrentValue(SaturationProperty, Math.Clamp(point.X / ActualWidth, 0d, 1d));
        SetCurrentValue(ValueProperty, 1d - Math.Clamp(point.Y / ActualHeight, 0d, 1d));
    }

    private static object CoerceHue(DependencyObject dependencyObject, object baseValue) =>
        ColorConversion.NormalizeHue((double)baseValue);

    private static object CoerceUnit(DependencyObject dependencyObject, object baseValue) =>
        ColorConversion.ClampUnit((double)baseValue);

    private Brush GetHueBrush()
    {
        var normalizedHue = ColorConversion.NormalizeHue(Hue);
        if (_cachedHueBrush is not null && Math.Abs(_cachedHue - normalizedHue) < 0.000001d)
        {
            return _cachedHueBrush;
        }

        var hueRgb = ColorConversion.ToRgb(new HsvColor(normalizedHue, 1d, 1d));
        _cachedHue = normalizedHue;
        _cachedHueBrush = Freeze(
            new SolidColorBrush(Color.FromRgb(hueRgb.Red, hueRgb.Green, hueRgb.Blue)));
        return _cachedHueBrush;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

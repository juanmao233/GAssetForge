using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GAssetForge.Core.Imaging;

namespace GAssetForge.App.Controls;

/// <summary>
/// Vertical, interactive 0-360 degree hue spectrum. It behaves like the hue strip in
/// professional image editors and can be reused independently of the color dialog.
/// </summary>
public sealed class HueSpectrumSlider : FrameworkElement
{
    public static readonly DependencyProperty HueProperty = DependencyProperty.Register(
        nameof(Hue),
        typeof(double),
        typeof(HueSpectrumSlider),
        new FrameworkPropertyMetadata(
            0d,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            null,
            CoerceHue));

    private static readonly Brush SpectrumBrush = CreateSpectrumBrush();
    private static readonly Pen MarkerShadowPen = Freeze(new Pen(Brushes.Black, 4d));
    private static readonly Pen MarkerHighlightPen = Freeze(new Pen(Brushes.White, 2d));
    private bool _isDragging;

    public HueSpectrumSlider()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
    }

    public double Hue
    {
        get => (double)GetValue(HueProperty);
        set => SetValue(HueProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var bounds = new Rect(RenderSize);
        if (bounds.Width <= 0d || bounds.Height <= 0d)
        {
            return;
        }

        drawingContext.DrawRectangle(SpectrumBrush, new Pen(Brushes.Black, 1d), bounds);

        var y = Math.Clamp(Hue / 360d, 0d, 1d) * bounds.Height;
        y = Math.Clamp(y, 1d, Math.Max(1d, bounds.Height - 1d));
        drawingContext.DrawLine(MarkerShadowPen, new Point(-3d, y), new Point(bounds.Width + 3d, y));
        drawingContext.DrawLine(MarkerHighlightPen, new Point(-3d, y), new Point(bounds.Width + 3d, y));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        _isDragging = CaptureMouse();
        UpdateHue(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateHue(e.GetPosition(this).Y);
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_isDragging)
        {
            UpdateHue(e.GetPosition(this).Y);
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
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10d : 1d;
        switch (e.Key)
        {
            case Key.Up:
            case Key.Left:
                SetCurrentValue(HueProperty, Hue - step);
                break;
            case Key.Down:
            case Key.Right:
                SetCurrentValue(HueProperty, Hue + step);
                break;
            default:
                base.OnKeyDown(e);
                return;
        }

        e.Handled = true;
    }

    private void UpdateHue(double y)
    {
        if (ActualHeight <= 0d)
        {
            return;
        }

        // Keep the bottom edge just below 360 so the normalized value does not jump to zero
        // until the pointer reaches the red endpoint intentionally.
        var ratio = Math.Clamp(y / ActualHeight, 0d, 1d);
        SetCurrentValue(HueProperty, ratio >= 1d ? 359.999d : ratio * 360d);
    }

    private static object CoerceHue(DependencyObject dependencyObject, object baseValue) =>
        ColorConversion.NormalizeHue((double)baseValue);

    private static Brush CreateSpectrumBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.5d, 0d),
            EndPoint = new Point(0.5d, 1d)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 0, 0), 0d));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 255, 0), 1d / 6d));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0, 255, 0), 2d / 6d));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0, 255, 255), 3d / 6d));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0, 0, 255), 4d / 6d));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 0, 255), 5d / 6d));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 0, 0), 1d));
        brush.Freeze();
        return brush;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

using System.Windows;
using System.Windows.Input;

namespace GAssetForge.App.Controls;

/// <summary>
/// Shared WPF input surface for image-like content. Mouse wheel zooms around the pointer, middle
/// mouse drags the viewport, and right click or left double-click resets the fitted view.
/// </summary>
public abstract class ImageViewportSurface : FrameworkElement
{
    private const double DefaultWheelZoomFactor = 1.2d;
    private bool _isPanning;
    private Point _lastPanPoint;

    protected ImageViewportSurface()
    {
        Focusable = true;
        ClipToBounds = true;
        Viewport.Changed += OnViewportChanged;
    }

    public event EventHandler? ViewportChanged;

    public ImageViewportState Viewport { get; } = new();

    public double Zoom => Viewport.Zoom;

    public Vector Pan => Viewport.Pan;

    public string ZoomText => $"{Zoom:P0}";

    public Rect ViewportContentRect => GetViewportContentRect();

    protected abstract bool HasViewportContent { get; }

    protected abstract Size ViewportContentSize { get; }

    protected virtual Rect ViewportBounds => new(RenderSize);

    protected virtual bool ResetOnLeftDoubleClick => true;

    protected virtual bool ResetOnRightClick => true;

    public bool ResetView() => Viewport.Reset();

    public bool ZoomBy(double factor)
    {
        var bounds = ViewportBounds;
        return ZoomAt(
            bounds.IsEmpty
                ? new Point(ActualWidth / 2d, ActualHeight / 2d)
                : new Point(bounds.X + bounds.Width / 2d, bounds.Y + bounds.Height / 2d),
            factor);
    }

    public bool ZoomAt(Point anchor, double factor) =>
        HasViewportContent &&
        Viewport.ZoomAt(anchor, factor, ViewportBounds, ViewportContentSize);

    public bool PanBy(Vector delta) => HasViewportContent && Viewport.PanBy(delta);

    protected Rect GetViewportContentRect() => HasViewportContent
        ? Viewport.CalculateContentRect(ViewportBounds, ViewportContentSize)
        : Rect.Empty;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (!HasViewportContent || !ResetOnLeftDoubleClick || e.ClickCount < 2)
        {
            return;
        }

        ResetView();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (!HasViewportContent || !ResetOnRightClick)
        {
            return;
        }

        ResetView();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle || !HasViewportContent)
        {
            return;
        }

        Focus();
        _lastPanPoint = e.GetPosition(this);
        _isPanning = CaptureMouse();
        if (!_isPanning)
        {
            return;
        }

        Cursor = Cursors.Hand;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_isPanning)
        {
            return;
        }

        if (e.MiddleButton != MouseButtonState.Pressed)
        {
            EndPan(releaseCapture: true);
            return;
        }

        var point = e.GetPosition(this);
        Viewport.PanBy(point - _lastPanPoint);
        _lastPanPoint = point;
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_isPanning || e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        EndPan(releaseCapture: true);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndPan(releaseCapture: false);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!HasViewportContent || e.Delta == 0)
        {
            return;
        }

        ZoomAt(
            e.GetPosition(this),
            e.Delta > 0 ? DefaultWheelZoomFactor : 1d / DefaultWheelZoomFactor);
        e.Handled = true;
    }

    private void EndPan(bool releaseCapture)
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        if (releaseCapture && IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        Cursor = Cursors.Arrow;
    }

    private void OnViewportChanged(object? sender, EventArgs e)
    {
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }
}

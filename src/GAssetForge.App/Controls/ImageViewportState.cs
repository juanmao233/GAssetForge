using System.Windows;

namespace GAssetForge.App.Controls;

/// <summary>
/// Stores zoom and pan for a uniformly fitted image and keeps pointer-anchored zoom math in one place.
/// The state is UI-agnostic apart from WPF geometry types, so custom drawing surfaces and reusable
/// preview controls can share the same viewport behavior.
/// </summary>
public sealed class ImageViewportState
{
    public const double DefaultMinimumZoom = 0.25d;
    public const double DefaultMaximumZoom = 32d;

    public ImageViewportState(
        double minimumZoom = DefaultMinimumZoom,
        double maximumZoom = DefaultMaximumZoom)
    {
        if (!double.IsFinite(minimumZoom) || minimumZoom <= 0d || minimumZoom > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumZoom));
        }

        if (!double.IsFinite(maximumZoom) || maximumZoom < 1d || maximumZoom < minimumZoom)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumZoom));
        }

        MinimumZoom = minimumZoom;
        MaximumZoom = maximumZoom;
    }

    public event EventHandler? Changed;

    public double MinimumZoom { get; }

    public double MaximumZoom { get; }

    public double Zoom { get; private set; } = 1d;

    public Vector Pan { get; private set; }

    public Rect CalculateContentRect(Rect viewportBounds, Size contentSize)
    {
        if (viewportBounds.IsEmpty ||
            viewportBounds.Width <= 0d ||
            viewportBounds.Height <= 0d ||
            !double.IsFinite(contentSize.Width) ||
            !double.IsFinite(contentSize.Height) ||
            contentSize.Width <= 0d ||
            contentSize.Height <= 0d)
        {
            return Rect.Empty;
        }

        var fitScale = Math.Min(
            viewportBounds.Width / contentSize.Width,
            viewportBounds.Height / contentSize.Height);
        var scale = Math.Max(0.0001d, fitScale * Zoom);
        var width = contentSize.Width * scale;
        var height = contentSize.Height * scale;
        return new Rect(
            viewportBounds.X + (viewportBounds.Width - width) / 2d + Pan.X,
            viewportBounds.Y + (viewportBounds.Height - height) / 2d + Pan.Y,
            width,
            height);
    }

    public bool ZoomAt(
        Point anchor,
        double factor,
        Rect viewportBounds,
        Size contentSize)
    {
        if (!double.IsFinite(anchor.X) || !double.IsFinite(anchor.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(anchor));
        }

        if (!double.IsFinite(factor) || factor <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(factor));
        }

        var oldRect = CalculateContentRect(viewportBounds, contentSize);
        if (oldRect.IsEmpty)
        {
            return false;
        }

        var targetZoom = Math.Clamp(Zoom * factor, MinimumZoom, MaximumZoom);
        if (targetZoom.Equals(Zoom))
        {
            return false;
        }

        var contentX = (anchor.X - oldRect.X) / Math.Max(oldRect.Width, 0.0001d);
        var contentY = (anchor.Y - oldRect.Y) / Math.Max(oldRect.Height, 0.0001d);
        Zoom = targetZoom;
        var newRect = CalculateContentRect(viewportBounds, contentSize);
        Pan += new Vector(
            anchor.X - (newRect.X + contentX * newRect.Width),
            anchor.Y - (newRect.Y + contentY * newRect.Height));
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool PanBy(Vector delta)
    {
        if (!double.IsFinite(delta.X) || !double.IsFinite(delta.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(delta));
        }

        if (delta.X == 0d && delta.Y == 0d)
        {
            return false;
        }

        Pan += delta;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Reset()
    {
        if (Zoom == 1d && Pan.X == 0d && Pan.Y == 0d)
        {
            return false;
        }

        Zoom = 1d;
        Pan = default;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}

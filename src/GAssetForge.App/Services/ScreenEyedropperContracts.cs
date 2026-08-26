using GAssetForge.Core.Imaging;

namespace GAssetForge.App.Services;

/// <summary>
/// Starts a desktop-wide eyedropper session. A null result means that the user cancelled.
/// </summary>
public interface IScreenEyedropperService
{
    RgbColor? PickColor();
}

internal readonly record struct PhysicalScreenPoint(int X, int Y);

internal readonly record struct PhysicalScreenBounds(int Left, int Top, int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;

    public int Bottom => checked(Top + Height);
}

internal interface IScreenColorSampler
{
    PhysicalScreenBounds GetVirtualScreenBounds();

    bool TryGetCursorPoint(out PhysicalScreenPoint point);

    bool TrySample(PhysicalScreenPoint point, out RgbColor color);
}

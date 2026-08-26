using GAssetForge.Core.Imaging;

namespace GAssetForge.App.Services;

public sealed record ColorPickerResult(RgbColor Rgb, HsvColor Hsv)
{
    public string Hex => Rgb.Hex;
}

/// <summary>
/// Opens the shared desktop color picker. A null result means that the user cancelled.
/// </summary>
public interface IColorPickerService
{
    ColorPickerResult? PickColor(string? initialColorHex, string? title = null);
}

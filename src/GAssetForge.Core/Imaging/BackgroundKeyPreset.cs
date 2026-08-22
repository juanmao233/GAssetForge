using System.Globalization;

namespace GAssetForge.Core.Imaging;

public enum BackgroundKeyPreset
{
    Magenta,
    Green,
    Blue,
    Black,
    White,
    Custom
}

public static class BackgroundKeyColor
{
    public const string DefaultCustomColorHex = "#FF00FF";

    public static string ResolveHex(BackgroundKeyPreset preset, string? customColorHex = null) => preset switch
    {
        BackgroundKeyPreset.Magenta => "#FF00FF",
        BackgroundKeyPreset.Green => "#00FF00",
        BackgroundKeyPreset.Blue => "#0000FF",
        BackgroundKeyPreset.Black => "#000000",
        BackgroundKeyPreset.White => "#FFFFFF",
        BackgroundKeyPreset.Custom => NormalizeHex(customColorHex),
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "不支持的背景底色预设。")
    };

    public static string NormalizeHex(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.StartsWith('#'))
        {
            normalized = normalized[1..];
        }

        if (normalized.Length != 6 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("自定义背景色必须是 #RRGGBB 格式。", nameof(value));
        }

        _ = byte.Parse(normalized.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        _ = byte.Parse(normalized.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        _ = byte.Parse(normalized.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return $"#{normalized.ToUpperInvariant()}";
    }
}

using System.Globalization;

namespace GAssetForge.Core.Imaging;

/// <summary>
/// An opaque sRGB color. This type deliberately has no dependency on a UI framework.
/// </summary>
public readonly record struct RgbColor(byte Red, byte Green, byte Blue)
{
    public string Hex => $"#{Red:X2}{Green:X2}{Blue:X2}";

    public static RgbColor FromInts(int red, int green, int blue) =>
        new(
            (byte)Math.Clamp(red, byte.MinValue, byte.MaxValue),
            (byte)Math.Clamp(green, byte.MinValue, byte.MaxValue),
            (byte)Math.Clamp(blue, byte.MinValue, byte.MaxValue));

    public static bool TryParseHex(string? value, out RgbColor color) =>
        ColorConversion.TryParseHex(value, out color);

    public static RgbColor ParseHex(string value) => ColorConversion.ParseHex(value);

    public override string ToString() => Hex;
}

/// <summary>
/// An HSV color with a hue in [0, 360) and saturation/value in [0, 1].
/// Constructor input is normalized so this value is always safe to consume.
/// </summary>
public readonly record struct HsvColor
{
    public HsvColor(double hue, double saturation, double value)
    {
        Hue = ColorConversion.NormalizeHue(hue);
        Saturation = ColorConversion.ClampUnit(saturation);
        Value = ColorConversion.ClampUnit(value);
    }

    public double Hue { get; }

    public double Saturation { get; }

    public double Value { get; }

    public override string ToString() =>
        FormattableString.Invariant($"HSV({Hue:0.##}, {Saturation:0.###}, {Value:0.###})");
}

/// <summary>
/// Framework-independent conversion and normalization helpers shared by color-aware tools.
/// </summary>
public static class ColorConversion
{
    private const double ChromaticEpsilon = 1d / (255d * 255d);

    public static double NormalizeHue(double hue)
    {
        if (!double.IsFinite(hue))
        {
            return 0d;
        }

        var normalized = hue % 360d;
        return normalized < 0d ? normalized + 360d : normalized;
    }

    public static double ClampUnit(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0d, 1d) : 0d;

    public static HsvColor ToHsv(RgbColor rgb, double fallbackHue = 0d)
    {
        var red = rgb.Red / 255d;
        var green = rgb.Green / 255d;
        var blue = rgb.Blue / 255d;
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var delta = maximum - minimum;

        // Hue is mathematically undefined for a gray. Preserving the caller's last useful
        // hue prevents the picker from jumping back to red while S or V crosses zero.
        var hue = NormalizeHue(fallbackHue);
        if (delta > ChromaticEpsilon)
        {
            if (Math.Abs(maximum - red) <= ChromaticEpsilon)
            {
                hue = 60d * (((green - blue) / delta) % 6d);
            }
            else if (Math.Abs(maximum - green) <= ChromaticEpsilon)
            {
                hue = 60d * (((blue - red) / delta) + 2d);
            }
            else
            {
                hue = 60d * (((red - green) / delta) + 4d);
            }

            hue = NormalizeHue(hue);
        }

        var saturation = maximum <= ChromaticEpsilon ? 0d : delta / maximum;
        return new HsvColor(hue, saturation, maximum);
    }

    public static RgbColor ToRgb(HsvColor hsv)
    {
        var hue = NormalizeHue(hsv.Hue);
        var saturation = ClampUnit(hsv.Saturation);
        var value = ClampUnit(hsv.Value);
        var chroma = value * saturation;
        var hueSector = hue / 60d;
        var intermediate = chroma * (1d - Math.Abs((hueSector % 2d) - 1d));

        var (red, green, blue) = hueSector switch
        {
            < 1d => (chroma, intermediate, 0d),
            < 2d => (intermediate, chroma, 0d),
            < 3d => (0d, chroma, intermediate),
            < 4d => (0d, intermediate, chroma),
            < 5d => (intermediate, 0d, chroma),
            _ => (chroma, 0d, intermediate)
        };

        var match = value - chroma;
        return new RgbColor(
            ToByte(red + match),
            ToByte(green + match),
            ToByte(blue + match));
    }

    public static bool TryParseHex(string? value, out RgbColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var span = value.Trim().AsSpan();
        if (span.Length == 7 && span[0] == '#')
        {
            span = span[1..];
        }

        if (span.Length != 6 ||
            !byte.TryParse(span[..2], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var red) ||
            !byte.TryParse(span.Slice(2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var green) ||
            !byte.TryParse(span.Slice(4, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var blue))
        {
            return false;
        }

        color = new RgbColor(red, green, blue);
        return true;
    }

    public static RgbColor ParseHex(string value)
    {
        if (TryParseHex(value, out var color))
        {
            return color;
        }

        throw new FormatException("颜色必须使用 #RRGGBB 格式。");
    }

    public static string NormalizeHex(string? value, RgbColor fallback)
    {
        return TryParseHex(value, out var parsed) ? parsed.Hex : fallback.Hex;
    }

    private static byte ToByte(double value) =>
        (byte)Math.Clamp((int)Math.Round(ClampUnit(value) * 255d, MidpointRounding.AwayFromZero), 0, 255);
}

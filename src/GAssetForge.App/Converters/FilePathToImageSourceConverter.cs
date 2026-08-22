using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace GAssetForge.App.Converters;

/// <summary>
/// Loads a local image into memory so the source file is not held open by WPF.
/// The decoded width defaults to a thumbnail-friendly maximum of 320 pixels.
/// Small images are kept at their native resolution so pixel art is never interpolated during decode.
/// </summary>
public sealed class FilePathToImageSourceConverter : IValueConverter
{
    private const int DefaultDecodePixelWidth = 320;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var decodePixelWidth = parameter is string text &&
                                   int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
                ? Math.Max(1, width)
                : DefaultDecodePixelWidth;

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);
            var sourcePixelWidth = decoder.Frames.Count > 0
                ? decoder.Frames[0].PixelWidth
                : 0;
            stream.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (sourcePixelWidth > decodePixelWidth)
            {
                bitmap.DecodePixelWidth = decodePixelWidth;
            }

            bitmap.StreamSource = stream;
            bitmap.EndInit();

            if (bitmap.CanFreeze)
            {
                bitmap.Freeze();
            }

            return bitmap;
        }
        // A broken or concurrently replaced reference image must not take down
        // the whole dock pane; the card keeps its neutral image placeholder.
        catch (Exception)
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

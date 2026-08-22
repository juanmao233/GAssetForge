using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace GAssetForge.App.Converters;

public sealed class StringToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var colorText = value as string;
        if (string.IsNullOrWhiteSpace(colorText))
        {
            colorText = parameter as string;
        }

        if (string.IsNullOrWhiteSpace(colorText))
        {
            return Brushes.Transparent;
        }

        try
        {
            var brush = (Brush)new BrushConverter().ConvertFromString(colorText)!;
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }

            return brush;
        }
        catch (FormatException)
        {
            return Brushes.Transparent;
        }
        catch (NotSupportedException)
        {
            return Brushes.Transparent;
        }
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

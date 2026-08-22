using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GAssetForge.App.Converters;

public sealed class DoubleToCornerRadiusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var radius = value is double number ? Math.Max(0, number) : 0;
        return new CornerRadius(radius);
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

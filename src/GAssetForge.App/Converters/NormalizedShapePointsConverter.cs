using GAssetForge.Core.Projects;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace GAssetForge.App.Converters;

public sealed class NormalizedShapePointsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var points = new PointCollection();
        if (value is not IEnumerable<UiShapePointModel> shapePoints)
        {
            return points;
        }

        foreach (var point in shapePoints)
        {
            if (double.IsFinite(point.X) && double.IsFinite(point.Y))
            {
                points.Add(new Point(point.X, point.Y));
            }
        }

        return points;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

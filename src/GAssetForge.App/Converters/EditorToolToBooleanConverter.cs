using GAssetForge.App.ViewModels;
using System.Globalization;
using System.Windows.Data;

namespace GAssetForge.App.Converters;

public sealed class EditorToolToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is EditorTool tool &&
        Enum.TryParse<EditorTool>(parameter?.ToString(), true, out var expected) &&
        tool == expected;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

using AvalonDock.Themes;
using System.Windows;

namespace GAssetForge.App.Services;

public sealed class ThemeService
{
    private const string PaletteMarker = "/Themes/Palette.";

    public AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

    public Theme DockTheme => CurrentTheme == AppTheme.Dark
        ? new Vs2013DarkTheme()
        : new Vs2013LightTheme();

    public void Apply(AppTheme theme)
    {
        if (!Enum.IsDefined(typeof(AppTheme), theme))
        {
            theme = AppTheme.Dark;
        }

        CurrentTheme = theme;
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var currentPalette = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.Contains(PaletteMarker, StringComparison.OrdinalIgnoreCase) == true ||
            dictionary.Source?.OriginalString.StartsWith("Themes/Palette.", StringComparison.OrdinalIgnoreCase) == true);
        var nextPalette = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/GAssetForge;component/Themes/Palette.{theme}.xaml",
                UriKind.Absolute)
        };

        if (currentPalette is null)
        {
            dictionaries.Insert(0, nextPalette);
        }
        else
        {
            var index = dictionaries.IndexOf(currentPalette);
            dictionaries[index] = nextPalette;
        }
    }
}

using System.Windows;
using GAssetForge.App.Views;

namespace GAssetForge.App.Services;

public sealed class DesktopColorPickerService : IColorPickerService
{
    private readonly Func<Window?> _ownerProvider;

    public DesktopColorPickerService(Func<Window?>? ownerProvider = null)
    {
        _ownerProvider = ownerProvider ?? ResolveDefaultOwner;
    }

    public ColorPickerResult? PickColor(string? initialColorHex, string? title = null)
    {
        var application = Application.Current;
        if (application is not null && !application.Dispatcher.CheckAccess())
        {
            return application.Dispatcher.Invoke(() => PickColor(initialColorHex, title));
        }

        var dialog = new ColorPickerDialog(initialColorHex);
        if (!string.IsNullOrWhiteSpace(title))
        {
            dialog.Title = title.Trim();
        }

        var owner = _ownerProvider();
        if (owner is not null && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog() == true
            ? new ColorPickerResult(dialog.SelectedRgb, dialog.SelectedHsv)
            : null;
    }

    private static Window? ResolveDefaultOwner()
    {
        var application = Application.Current;
        if (application is null)
        {
            return null;
        }

        return application.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
               ?? application.MainWindow;
    }
}

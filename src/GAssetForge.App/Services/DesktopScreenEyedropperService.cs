using System.Windows;
using System.Windows.Threading;
using GAssetForge.App.Views;
using GAssetForge.Core.Imaging;

namespace GAssetForge.App.Services;

public sealed class DesktopScreenEyedropperService : IScreenEyedropperService
{
    private readonly IScreenColorSampler _screenColorSampler;
    private readonly Func<Window?> _ownerProvider;

    public DesktopScreenEyedropperService(Func<Window?>? ownerProvider = null)
        : this(new Win32ScreenColorSampler(), ownerProvider)
    {
    }

    internal DesktopScreenEyedropperService(
        IScreenColorSampler screenColorSampler,
        Func<Window?>? ownerProvider = null)
    {
        ArgumentNullException.ThrowIfNull(screenColorSampler);
        _screenColorSampler = screenColorSampler;
        _ownerProvider = ownerProvider ?? ResolveDefaultOwner;
    }

    public RgbColor? PickColor()
    {
        var application = Application.Current;
        if (application is not null && !application.Dispatcher.CheckAccess())
        {
            return application.Dispatcher.Invoke(PickColor);
        }

        var owner = _ownerProvider();
        var originalOpacity = owner?.Opacity ?? 1d;
        var originalHitTestVisibility = owner?.IsHitTestVisible ?? true;
        try
        {
            if (owner is not null)
            {
                // Keep a ShowDialog-hosted picker alive while exposing the pixels behind it.
                // Hide() would end WPF's modal loop, so the owner is made transparent instead.
                owner.IsHitTestVisible = false;
                owner.Opacity = 0d;
                owner.Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
                DesktopWindowInterop.FlushDesktopComposition();
            }

            var overlay = new ScreenEyedropperWindow(_screenColorSampler);
            if (owner is not null && !ReferenceEquals(owner, overlay))
            {
                overlay.Owner = owner;
            }

            return overlay.ShowDialog() == true
                ? overlay.SelectedColor
                : null;
        }
        finally
        {
            if (owner is not null)
            {
                owner.Opacity = originalOpacity;
                owner.IsHitTestVisible = originalHitTestVisibility;
                owner.Activate();
            }
        }
    }

    private static Window? ResolveDefaultOwner()
    {
        var application = Application.Current;
        if (application is null)
        {
            return null;
        }

        return application.Windows
                   .OfType<ColorPickerDialog>()
                   .FirstOrDefault(window => window.IsActive)
               ?? application.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
               ?? application.MainWindow;
    }
}

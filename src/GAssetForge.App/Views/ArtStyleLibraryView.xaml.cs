using GAssetForge.App.ViewModels;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace GAssetForge.App.Views;

public partial class ArtStyleLibraryView : UserControl
{
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff"
    };

    public ArtStyleLibraryView()
    {
        InitializeComponent();
    }

    private void AssetDropZone_OnPreviewDragEnter(object sender, DragEventArgs e) =>
        UpdateDropFeedback(e);

    private void AssetDropZone_OnPreviewDragOver(object sender, DragEventArgs e) =>
        UpdateDropFeedback(e);

    private void AssetDropZone_OnPreviewDragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void AssetDropZone_OnPreviewDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        var paths = GetSupportedImagePaths(e.Data);
        if (paths.Length == 0 || DataContext is not ArtStyleLibraryViewModel workspace)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var command = workspace.ImportDroppedAssetsCommand;
        if (!command.CanExecute(paths))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        command.Execute(paths);
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void UpdateDropFeedback(DragEventArgs e)
    {
        var paths = GetSupportedImagePaths(e.Data);
        var canImport = paths.Length > 0 &&
                        DataContext is ArtStyleLibraryViewModel workspace &&
                        workspace.ImportDroppedAssetsCommand.CanExecute(paths);

        e.Effects = canImport ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = canImport ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private static string[] GetSupportedImagePaths(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) ||
            data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return [];
        }

        return paths
            .Where(File.Exists)
            .Where(path => SupportedImageExtensions.Contains(Path.GetExtension(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

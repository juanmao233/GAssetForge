using GAssetForge.App.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace GAssetForge.App.Views;

public partial class ArtWorkspaceToolPaletteView : UserControl
{
    public ArtWorkspaceToolPaletteView()
    {
        InitializeComponent();
    }

    private void ArtWorkspaceClearRegionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ArtWorkspaceViewModel viewModel)
        {
            viewModel.ClearSelectedImageRegion();
        }
    }
}

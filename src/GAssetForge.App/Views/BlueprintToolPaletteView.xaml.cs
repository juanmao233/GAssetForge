using GAssetForge.App.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace GAssetForge.App.Views;

public partial class BlueprintToolPaletteView : UserControl
{
    public BlueprintToolPaletteView()
    {
        InitializeComponent();
    }

    private void BlueprintAddTypeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is BlueprintViewModel viewModel &&
            sender is MenuItem { DataContext: BlueprintNodeTypeOption option })
        {
            viewModel.SelectAddNodeType(option);
            e.Handled = true;
        }
    }
}

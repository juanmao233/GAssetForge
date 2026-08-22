using System.Windows;
using System.Windows.Controls;

namespace GAssetForge.App.Views;

public partial class TilemapWorkspaceView : UserControl
{
    public TilemapWorkspaceView()
    {
        InitializeComponent();
    }

    private void ZoomOut_OnClick(object sender, RoutedEventArgs e) => TilemapGridSurface.ZoomBy(1d / 1.2d);

    private void ResetView_OnClick(object sender, RoutedEventArgs e) => TilemapGridSurface.ResetView();

    private void ZoomIn_OnClick(object sender, RoutedEventArgs e) => TilemapGridSurface.ZoomBy(1.2d);
}

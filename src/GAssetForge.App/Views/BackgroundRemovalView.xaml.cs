using System.Windows.Controls;
using GAssetForge.App.ViewModels;

namespace GAssetForge.App.Views;

public partial class BackgroundRemovalView : UserControl
{
    private BackgroundRemovalViewModel? _checkerAlignmentEditViewModel;

    public BackgroundRemovalView()
    {
        InitializeComponent();
    }

    private void BackgroundRemovalInputPreview_OnRulerEditStarted(object? sender, EventArgs e)
    {
        _checkerAlignmentEditViewModel = DataContext as BackgroundRemovalViewModel;
        _checkerAlignmentEditViewModel?.BeginCheckerAlignmentEdit();
    }

    private void BackgroundRemovalInputPreview_OnRulerEditCompleted(object? sender, EventArgs e)
    {
        (_checkerAlignmentEditViewModel ?? DataContext as BackgroundRemovalViewModel)?
            .CommitCheckerAlignmentEdit();
        _checkerAlignmentEditViewModel = null;
    }

    private void BackgroundRemovalInputPreview_OnRulerEditCanceled(object? sender, EventArgs e)
    {
        (_checkerAlignmentEditViewModel ?? DataContext as BackgroundRemovalViewModel)?
            .CancelCheckerAlignmentEdit();
        _checkerAlignmentEditViewModel = null;
    }
}

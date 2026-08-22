using GAssetForge.App.Infrastructure;
using GAssetForge.App.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GAssetForge.App.Views;

public partial class CanvasDocumentView : UserControl
{
    private FrameworkElement? _dragSource;
    private Point _dragStart;
    private bool _moveTransactionOpen;

    public CanvasDocumentView()
    {
        InitializeComponent();
    }

    private EditorWorkspaceViewModel? Workspace => DataContext as EditorWorkspaceViewModel;

    private void CanvasSurface_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Workspace is not { ActiveTool: EditorTool.Text } workspace)
        {
            return;
        }

        var point = e.GetPosition(CanvasSurface);
        workspace.CreateTextAt(point.X, point.Y);
        e.Handled = true;
    }

    private void Layer_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            Workspace is not { ActiveTool: EditorTool.Move } workspace ||
            sender is not FrameworkElement element ||
            element.DataContext is not LayerViewModel layer)
        {
            return;
        }

        workspace.SelectLayer(layer.Id);
        _dragStart = e.GetPosition(CanvasSurface);
        _dragSource = element;
        _moveTransactionOpen = workspace.BeginMove(layer.Id);
        if (_moveTransactionOpen)
        {
            Mouse.Capture(element, CaptureMode.Element);
        }

        e.Handled = true;
    }

    private void Layer_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_moveTransactionOpen || e.LeftButton != MouseButtonState.Pressed || Workspace is not { } workspace)
        {
            return;
        }

        var current = e.GetPosition(CanvasSurface);
        workspace.PreviewMove(current.X - _dragStart.X, current.Y - _dragStart.Y);
        e.Handled = true;
    }

    private void Layer_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        CompleteMoveTransaction();
        e.Handled = true;
    }

    private void Layer_OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        CompleteMoveTransaction();
    }

    private void CompleteMoveTransaction()
    {
        if (_moveTransactionOpen)
        {
            Workspace?.EndMove();
        }

        _moveTransactionOpen = false;
        if (_dragSource?.IsMouseCaptured == true)
        {
            _dragSource.ReleaseMouseCapture();
        }

        _dragSource = null;
    }

    private void CanvasSurface_OnPreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(PrefabDragDrop.Format)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void CanvasSurface_OnDrop(object sender, DragEventArgs e)
    {
        if (Workspace is not { } workspace || e.Data.GetData(PrefabDragDrop.Format) is not string prefabId)
        {
            return;
        }

        var point = e.GetPosition(CanvasSurface);
        workspace.InstantiatePrefabAt(prefabId, point.X, point.Y);
        e.Handled = true;
    }

    private void CanvasScrollViewer_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || Workspace is not { } workspace)
        {
            return;
        }

        if (e.Delta > 0)
        {
            workspace.ZoomInCommand.Execute(null);
        }
        else
        {
            workspace.ZoomOutCommand.Execute(null);
        }

        e.Handled = true;
    }
}

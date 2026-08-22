using GAssetForge.App.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GAssetForge.App.Views;

public partial class LayersView : UserControl
{
    private const string LayerDragFormat = "GAssetForge/LayerId";
    private EditorWorkspaceViewModel? _workspace;
    private bool _syncingSelection;
    private Point _dragStart;
    private LayerViewModel? _dragLayer;
    private DependencyObject? _dragSource;

    public LayersView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachWorkspace();
        AttachWorkspace(e.NewValue as EditorWorkspaceViewModel);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        AttachWorkspace(DataContext as EditorWorkspaceViewModel);

    private void OnUnloaded(object sender, RoutedEventArgs e) => DetachWorkspace();

    private void AttachWorkspace(EditorWorkspaceViewModel? workspace)
    {
        if (workspace is null || ReferenceEquals(_workspace, workspace))
        {
            return;
        }

        DetachWorkspace();
        _workspace = workspace;
        _workspace.PropertyChanged += Workspace_OnPropertyChanged;
        QueueSelectionSync();
    }

    private void DetachWorkspace()
    {
        if (_workspace is not null)
        {
            _workspace.PropertyChanged -= Workspace_OnPropertyChanged;
        }

        _workspace = null;
    }

    private void Workspace_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(EditorWorkspaceViewModel.SelectedLayer), StringComparison.Ordinal))
        {
            QueueSelectionSync();
        }
    }

    private void QueueSelectionSync()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, SyncSelectionFromWorkspace);
    }

    private void SyncSelectionFromWorkspace()
    {
        var target = _workspace?.SelectedLayer;
        if (target is null)
        {
            return;
        }

        var path = new List<LayerViewModel>();
        if (!TryBuildPath(_workspace!.LayerRoots, target, path))
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            ItemsControl owner = LayersTree;
            foreach (var layer in path)
            {
                owner.UpdateLayout();
                if (owner.ItemContainerGenerator.ContainerFromItem(layer) is not TreeViewItem container)
                {
                    return;
                }

                if (ReferenceEquals(layer, target))
                {
                    container.IsSelected = true;
                    container.BringIntoView();
                    return;
                }

                container.IsExpanded = true;
                owner = container;
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private static bool TryBuildPath(IEnumerable<LayerViewModel> layers, LayerViewModel target, ICollection<LayerViewModel> path)
    {
        foreach (var layer in layers)
        {
            path.Add(layer);
            if (ReferenceEquals(layer, target) || TryBuildPath(layer.Children, target, path))
            {
                return true;
            }

            path.Remove(layer);
        }

        return false;
    }

    private void LayersTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncingSelection || _workspace is null || e.NewValue is not LayerViewModel layer)
        {
            return;
        }

        _workspace.SelectLayer(layer.Id);
    }

    private void LayersTree_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null ||
            FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is not { DataContext: LayerViewModel layer })
        {
            _dragLayer = null;
            _dragSource = null;
            return;
        }

        _dragStart = e.GetPosition(LayersTree);
        _dragLayer = layer;
        _dragSource = e.OriginalSource as DependencyObject;
    }

    private void LayersTree_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragLayer is null || _dragSource is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(LayersTree);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject();
        data.SetData(LayerDragFormat, _dragLayer.Id);
        DragDrop.DoDragDrop(_dragSource, data, DragDropEffects.Move);
        _dragLayer = null;
        _dragSource = null;
    }

    private void LayersTree_OnPreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDropTarget(e, out var sourceId, out var target, out var intoTarget) &&
            _workspace?.CanMoveLayer(sourceId, target.Id, intoTarget) == true
                ? DragDropEffects.Move
                : DragDropEffects.None;
        e.Handled = true;
    }

    private void LayersTree_OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (TryGetDropTarget(e, out var sourceId, out var target, out var intoTarget) &&
            _workspace?.CanMoveLayer(sourceId, target.Id, intoTarget) == true)
        {
            _workspace.MoveLayer(sourceId, target.Id, intoTarget);
            e.Effects = DragDropEffects.Move;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        _dragLayer = null;
        _dragSource = null;
        e.Handled = true;
    }

    private static bool TryGetDropTarget(
        DragEventArgs e,
        out string sourceId,
        out LayerViewModel target,
        out bool intoTarget)
    {
        sourceId = e.Data.GetData(LayerDragFormat) as string ?? string.Empty;
        target = null!;
        intoTarget = false;
        if (string.IsNullOrWhiteSpace(sourceId) ||
            FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is not { DataContext: LayerViewModel layer })
        {
            return false;
        }

        target = layer;
        intoTarget = layer.IsGroup && (Keyboard.Modifiers & ModifierKeys.Shift) == 0;
        return true;
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        var current = element;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}

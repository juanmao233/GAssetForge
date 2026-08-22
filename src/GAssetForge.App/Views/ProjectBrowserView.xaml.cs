using GAssetForge.App.Infrastructure;
using GAssetForge.App.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace GAssetForge.App.Views;

public partial class ProjectBrowserView : UserControl
{
    private Point _dragStart;
    private PrefabViewModel? _dragPrefab;

    private ProjectBrowserViewModel? Workspace => DataContext as ProjectBrowserViewModel;

    public ProjectBrowserView()
    {
        InitializeComponent();
    }

    private void Prefab_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PrefabViewModel prefab })
        {
            return;
        }

        if (e.ClickCount == 2 &&
            Workspace is { IsPrefabLibraryAvailable: true, EditorWorkspace: { } editorWorkspace })
        {
            editorWorkspace.InstantiatePrefabCommand.Execute(prefab);
            _dragPrefab = null;
            e.Handled = true;
            return;
        }

        _dragStart = e.GetPosition(this);
        _dragPrefab = prefab;
    }

    private void Prefab_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragPrefab is null || e.LeftButton != MouseButtonState.Pressed || sender is not DependencyObject dragSource)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject();
        data.SetData(PrefabDragDrop.Format, _dragPrefab.Id);
        DragDrop.DoDragDrop(dragSource, data, DragDropEffects.Copy);
        _dragPrefab = null;
    }

    private void Prefab_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragPrefab = null;
    }

    private void Breadcrumb_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
        {
            Workspace?.SelectDirectory(path);
        }
    }

    private void AssetFolderTreeItem_OnExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem
            {
                DataContext: AssetFolderNodeViewModel { IsPlaceholder: false } node
            })
        {
            Workspace?.LoadChildren(node);
        }
    }

    private void AssetFolderTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is AssetFolderNodeViewModel { IsPlaceholder: false } node)
        {
            Workspace?.SelectDirectory(node.FullPath);
        }
    }

    private void AssetEntryList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(AssetEntryList, source) is not ListViewItem
            {
                DataContext: ProjectAssetEntryViewModel entry
            })
        {
            return;
        }

        Workspace?.OpenEntry(entry);
        e.Handled = true;
    }

    private void AssetEntryList_OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var hit = AssetEntryList.InputHitTest(Mouse.GetPosition(AssetEntryList)) as DependencyObject;
        if (hit is null ||
            ItemsControl.ContainerFromElement(AssetEntryList, hit) is ListViewItem ||
            FindVisualAncestor<GridViewColumnHeader>(hit) is not null ||
            FindVisualAncestor<ScrollBar>(hit) is not null)
        {
            e.Handled = true;
            return;
        }

        AssetEntryList.SelectedItem = null;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
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

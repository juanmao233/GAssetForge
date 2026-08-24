using GAssetForge.App.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace GAssetForge.App.Views;

public partial class BlueprintView : UserControl
{
    private bool _isPanning;
    private Point _panStartScreen;
    private double _panStartX;
    private double _panStartY;
    private MouseButton _panButton;
    private bool _panMoved;
    private BlueprintNodeViewModel? _movingNode;
    private Point _moveStartScreen;
    private double _moveStartX;
    private double _moveStartY;
    private bool _suppressRightButtonUp;
    private BlueprintConnectionViewModel? _rightPanConnectionCandidate;

    public BlueprintView()
    {
        InitializeComponent();
    }

    private BlueprintViewModel? ViewModel => DataContext as BlueprintViewModel;

    private void BlueprintCanvas_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        BlueprintCanvas.Focus();
        var sourceNode = FindDataContext<BlueprintNodeViewModel>(e.OriginalSource as DependencyObject);
        var sourceConnection = FindDataContext<BlueprintConnectionViewModel>(e.OriginalSource as DependencyObject);
        var isInteractive = IsInteractiveSource(e.OriginalSource);
        var screen = e.GetPosition(BlueprintCanvas);
        if (e.ChangedButton == MouseButton.Right)
        {
            if (viewModel.IsConnectTool && viewModel.HasPendingConnection)
            {
                viewModel.CancelPendingConnectionSelection();
                _suppressRightButtonUp = true;
                BlueprintCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            if (viewModel.IsAddTool && sourceNode is null && !isInteractive)
            {
                _suppressRightButtonUp = true;
                OpenAddNodeTypeMenu(screen, viewModel);
                e.Handled = true;
                return;
            }

            if (viewModel.IsPointerTool && !isInteractive)
            {
                if (e.ClickCount >= 2)
                {
                    FinishPointerOperation(viewModel, releaseCapture: true);
                    viewModel.ResetViewportToOrigin();
                    _suppressRightButtonUp = true;
                    BlueprintCanvas.CaptureMouse();
                }
                else
                {
                    BeginPan(screen, viewModel, e.ChangedButton, sourceConnection);
                }

                e.Handled = true;
            }

            return;
        }

        if (e.ChangedButton == MouseButton.Middle)
        {
            if (viewModel.IsPointerTool)
            {
                BeginPan(screen, viewModel, e.ChangedButton);
                e.Handled = true;
            }

            return;
        }

        if (e.ChangedButton != MouseButton.Left || sourceNode is not null || isInteractive)
        {
            return;
        }

        if (viewModel.IsAddTool)
        {
            var world = ScreenToWorld(screen, viewModel);
            if (viewModel.HasPlacementPreview)
            {
                viewModel.CommitPlacementPreview(world.X, world.Y);
            }
            else
            {
                viewModel.BeginPlacementPreview(world.X, world.Y);
            }

            e.Handled = true;
            return;
        }

        if (viewModel.IsPointerTool)
        {
            viewModel.SelectNode(null, additive: false);
            e.Handled = true;
        }
    }

    private void BlueprintCanvas_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        var screen = e.GetPosition(BlueprintCanvas);
        if (_isPanning)
        {
            var isButtonPressed = _panButton switch
            {
                MouseButton.Middle => e.MiddleButton == MouseButtonState.Pressed,
                MouseButton.Right => e.RightButton == MouseButtonState.Pressed,
                _ => false
            };
            if (!isButtonPressed)
            {
                FinishPointerOperation(viewModel, releaseCapture: true);
                return;
            }

            var delta = screen - _panStartScreen;
            if (!_panMoved &&
                Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                e.Handled = true;
                return;
            }

            if (!_panMoved)
            {
                _panMoved = true;
            }
            viewModel.SetViewport(
                _panStartX + delta.X,
                _panStartY + delta.Y,
                viewModel.Zoom,
                commit: false);
            e.Handled = true;
            return;
        }

        if (_movingNode is not null)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                FinishPointerOperation(viewModel, releaseCapture: true);
                return;
            }

            var delta = screen - _moveStartScreen;
            viewModel.MoveNode(
                _movingNode,
                _moveStartX + delta.X / viewModel.Zoom,
                _moveStartY + delta.Y / viewModel.Zoom,
                commit: false);
            e.Handled = true;
            return;
        }

        var world = ScreenToWorld(screen, viewModel);
        if (viewModel.IsAddTool && viewModel.HasPlacementPreview)
        {
            viewModel.UpdatePlacementPreview(world.X, world.Y);
        }

        if (viewModel.IsConnectTool && viewModel.HasPendingConnection)
        {
            viewModel.UpdatePendingConnectionPointer(world.X, world.Y);
        }
    }

    private void BlueprintCanvas_OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        if (_suppressRightButtonUp && e.ChangedButton == MouseButton.Right)
        {
            _suppressRightButtonUp = false;
            if (BlueprintCanvas.IsMouseCaptured)
            {
                BlueprintCanvas.ReleaseMouseCapture();
            }
            e.Handled = true;
            return;
        }

        if (_isPanning && e.ChangedButton == _panButton)
        {
            var panButton = _panButton;
            var panMoved = _panMoved;
            var connectionCandidate = _rightPanConnectionCandidate;
            _isPanning = false;
            _panMoved = false;
            _rightPanConnectionCandidate = null;
            BlueprintCanvas.ReleaseMouseCapture();
            viewModel.SetViewport(viewModel.PanX, viewModel.PanY, viewModel.Zoom, commit: true);
            if (panButton == MouseButton.Right && !panMoved && connectionCandidate is not null)
            {
                viewModel.RemoveConnection(connectionCandidate);
                e.Handled = true;
            }
            else
            {
                e.Handled = panButton == MouseButton.Middle || panMoved;
            }
            return;
        }

        if (_movingNode is not null && e.ChangedButton == MouseButton.Left)
        {
            var node = _movingNode;
            _movingNode = null;
            BlueprintCanvas.ReleaseMouseCapture();
            viewModel.MoveNode(node, node.X, node.Y, commit: true);
            e.Handled = true;
        }
    }

    private void BlueprintCanvas_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || !viewModel.IsPointerTool || IsInteractiveSource(e.OriginalSource))
        {
            return;
        }

        var pointer = e.GetPosition(BlueprintCanvas);
        var anchor = ScreenToWorld(pointer, viewModel);
        var factor = e.Delta > 0 ? 1.12d : 1d / 1.12d;
        var zoom = Math.Clamp(viewModel.Zoom * factor, 0.25d, 2.5d);
        var panX = pointer.X - anchor.X * zoom;
        var panY = pointer.Y - anchor.Y * zoom;
        viewModel.SetViewport(panX, panY, zoom, commit: true);
        e.Handled = true;
    }

    private void BlueprintCanvas_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || ViewModel is not { } viewModel)
        {
            return;
        }

        var hadPointerOperation = _isPanning || _movingNode is not null;
        FinishPointerOperation(viewModel, releaseCapture: true);
        if (viewModel.CancelTransientInteraction() || hadPointerOperation)
        {
            e.Handled = true;
        }
    }

    private void BlueprintCanvas_OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        _suppressRightButtonUp = false;
        if (ViewModel is { } viewModel && (_isPanning || _movingNode is not null))
        {
            FinishPointerOperation(viewModel, releaseCapture: false);
        }
    }

    private void BlueprintCanvas_OnUnloaded(object sender, RoutedEventArgs e)
    {
        _suppressRightButtonUp = false;
        if (ViewModel is { } viewModel)
        {
            FinishPointerOperation(viewModel, releaseCapture: false);
            viewModel.CancelTransientInteraction();
        }
    }

    private void FocusAllNodes_OnClick(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || viewModel.Nodes.Count == 0 ||
            BlueprintCanvas.ActualWidth <= 0d || BlueprintCanvas.ActualHeight <= 0d)
        {
            return;
        }

        var minimumX = viewModel.Nodes.Min(node => node.X);
        var minimumY = viewModel.Nodes.Min(node => node.Y);
        var maximumX = viewModel.Nodes.Max(node => node.X + node.Width);
        var maximumY = viewModel.Nodes.Max(node => node.Y + BlueprintViewModel.MaximumEstimatedNodeHeight);
        const double padding = 72d;
        var contentWidth = Math.Max(1d, maximumX - minimumX);
        var contentHeight = Math.Max(1d, maximumY - minimumY);
        var zoom = Math.Clamp(
            Math.Min(
                (BlueprintCanvas.ActualWidth - padding * 2d) / contentWidth,
                (BlueprintCanvas.ActualHeight - padding * 2d) / contentHeight),
            0.25d,
            1.4d);
        viewModel.SetViewport(
            padding - minimumX * zoom,
            padding - minimumY * zoom,
            zoom,
            commit: true);
    }

    private void NodeCard_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || sender is not FrameworkElement { DataContext: BlueprintNodeViewModel node })
        {
            return;
        }

        if (viewModel.IsMoveNodeTool)
        {
            viewModel.SelectNode(node, additive: false);
            _movingNode = node;
            _moveStartScreen = e.GetPosition(BlueprintCanvas);
            _moveStartX = node.X;
            _moveStartY = node.Y;
            BlueprintCanvas.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (!IsInteractiveSource(e.OriginalSource) && viewModel.IsPointerTool)
        {
            var additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            viewModel.SelectNode(node, additive);
        }
    }

    private void Connection_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (viewModel.HasPendingConnection)
        {
            viewModel.CancelPendingConnectionSelection();
            e.Handled = true;
            return;
        }

        if (sender is FrameworkElement { DataContext: BlueprintConnectionViewModel connection })
        {
            viewModel.RemoveConnection(connection);
            e.Handled = true;
        }
    }

    private void OpenAddNodeTypeMenu(Point screen, BlueprintViewModel viewModel)
    {
        var world = ScreenToWorld(screen, viewModel);
        var menu = new ContextMenu
        {
            PlacementTarget = BlueprintCanvas,
            Placement = PlacementMode.RelativePoint,
            HorizontalOffset = screen.X,
            VerticalOffset = screen.Y
        };
        foreach (var option in viewModel.AddNodeTypes)
        {
            var item = new MenuItem
            {
                Header = option.Label,
                ToolTip = option.Description,
                IsCheckable = true,
                IsChecked = ReferenceEquals(option, viewModel.SelectedAddNodeType)
            };
            item.Click += (_, args) =>
            {
                viewModel.SelectAddNodeType(option);
                viewModel.BeginPlacementPreview(world.X, world.Y);
                args.Handled = true;
            };
            menu.Items.Add(item);
        }

        menu.Closed += (_, _) => _suppressRightButtonUp = false;
        menu.IsOpen = true;
    }

    private void BeginPan(
        Point screen,
        BlueprintViewModel viewModel,
        MouseButton button,
        BlueprintConnectionViewModel? connectionCandidate = null)
    {
        _isPanning = true;
        _panButton = button;
        _panMoved = false;
        _rightPanConnectionCandidate = button == MouseButton.Right
            ? connectionCandidate
            : null;
        _panStartScreen = screen;
        _panStartX = viewModel.PanX;
        _panStartY = viewModel.PanY;
        BlueprintCanvas.CaptureMouse();
    }

    private void FinishPointerOperation(BlueprintViewModel viewModel, bool releaseCapture)
    {
        if (_isPanning)
        {
            _isPanning = false;
            _panMoved = false;
            _rightPanConnectionCandidate = null;
            viewModel.SetViewport(viewModel.PanX, viewModel.PanY, viewModel.Zoom, commit: true);
        }

        if (_movingNode is not null)
        {
            var node = _movingNode;
            _movingNode = null;
            viewModel.MoveNode(node, node.X, node.Y, commit: true);
        }

        if (releaseCapture && BlueprintCanvas.IsMouseCaptured)
        {
            BlueprintCanvas.ReleaseMouseCapture();
        }
    }

    private static Point ScreenToWorld(Point screen, BlueprintViewModel viewModel) =>
        new(
            (screen.X - viewModel.PanX) / viewModel.Zoom,
            (screen.Y - viewModel.PanY) / viewModel.Zoom);

    private bool IsInteractiveSource(object? source)
    {
        for (var current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, BlueprintCanvas))
            {
                return false;
            }

            if (current is ButtonBase or TextBoxBase or PasswordBox or ComboBox or Selector or
                ScrollViewer or Slider or ScrollBar)
            {
                return true;
            }

            if (current is Border { Name: "BlueprintNodeCard" })
            {
                break;
            }
        }

        return false;
    }

    private static T? FindDataContext<T>(DependencyObject? source) where T : class
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { DataContext: T value })
            {
                return value;
            }
        }

        return null;
    }
}

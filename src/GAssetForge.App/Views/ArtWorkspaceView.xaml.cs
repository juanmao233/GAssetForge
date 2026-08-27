using GAssetForge.App.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace GAssetForge.App.Views;

public partial class ArtWorkspaceView : UserControl
{
    private bool _isPanning;
    private MouseButton _panButton;
    private Point _panStartScreen;
    private double _panStartX;
    private double _panStartY;
    private ArtWorkspaceImageItemViewModel? _movingImage;
    private Point _moveStartScreen;
    private double _moveStartX;
    private double _moveStartY;
    private OutputInteraction _outputInteraction;
    private double _outputStartX;
    private double _outputStartY;
    private double _outputStartWidth;
    private double _outputStartHeight;

    public ArtWorkspaceView()
    {
        InitializeComponent();
    }

    private ArtWorkspaceViewModel? ViewModel => DataContext as ArtWorkspaceViewModel;

    private void ArtWorkspaceViewport_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        ArtWorkspaceViewport.Focus();
        if (e.ChangedButton == MouseButton.Right && e.ClickCount >= 2)
        {
            CancelInteraction(restoreStartValues: true);
            viewModel.ResetViewportToOrigin();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            BeginPan(e.GetPosition(ArtWorkspaceViewport), e.ChangedButton, viewModel);
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left && !IsInteractiveSource(e.OriginalSource))
        {
            viewModel.SelectImage(null);
            e.Handled = true;
        }
    }

    private void ImageCard_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } viewModel ||
            sender is not FrameworkElement { DataContext: ArtWorkspaceImageItemViewModel image })
        {
            return;
        }

        viewModel.SelectImage(image);
        if (!image.IsLocked)
        {
            _movingImage = image;
            _moveStartScreen = e.GetPosition(ArtWorkspaceViewport);
            _moveStartX = image.X;
            _moveStartY = image.Y;
            ArtWorkspaceViewport.CaptureMouse();
        }

        e.Handled = true;
    }

    private void OutputFrame_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } viewModel ||
            ReferenceEquals(e.OriginalSource, ArtWorkspaceOutputResizeHandle) ||
            FindAncestor<Border>(e.OriginalSource as DependencyObject, ArtWorkspaceOutputResizeHandle) is not null)
        {
            return;
        }

        viewModel.SelectImage(null);
        BeginOutputInteraction(OutputInteraction.Move, e.GetPosition(ArtWorkspaceViewport), viewModel);
        e.Handled = true;
    }

    private void OutputResizeHandle_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.SelectImage(null);
        BeginOutputInteraction(OutputInteraction.Resize, e.GetPosition(ArtWorkspaceViewport), viewModel);
        e.Handled = true;
    }

    private void ArtWorkspaceViewport_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        var screen = e.GetPosition(ArtWorkspaceViewport);
        if (_isPanning)
        {
            var pressed = _panButton == MouseButton.Middle
                ? e.MiddleButton == MouseButtonState.Pressed
                : e.RightButton == MouseButtonState.Pressed;
            if (!pressed)
            {
                CompleteInteraction();
                return;
            }

            var delta = screen - _panStartScreen;
            viewModel.SetViewport(
                _panStartX + delta.X,
                _panStartY + delta.Y,
                viewModel.Zoom,
                commit: false);
            e.Handled = true;
            return;
        }

        if (_movingImage is not null)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                CompleteInteraction();
                return;
            }

            var delta = screen - _moveStartScreen;
            viewModel.MoveImage(
                _movingImage,
                _moveStartX + delta.X / viewModel.Zoom,
                _moveStartY + delta.Y / viewModel.Zoom,
                commit: false);
            e.Handled = true;
            return;
        }

        if (_outputInteraction != OutputInteraction.None)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                CompleteInteraction();
                return;
            }

            var delta = screen - _moveStartScreen;
            if (_outputInteraction == OutputInteraction.Move)
            {
                viewModel.SetOutputFrame(
                    _outputStartX + delta.X / viewModel.Zoom,
                    _outputStartY + delta.Y / viewModel.Zoom,
                    _outputStartWidth,
                    _outputStartHeight,
                    commit: false);
            }
            else
            {
                viewModel.SetOutputFrame(
                    _outputStartX,
                    _outputStartY,
                    Math.Max(16d, _outputStartWidth + delta.X / viewModel.Zoom),
                    Math.Max(16d, _outputStartHeight + delta.Y / viewModel.Zoom),
                    commit: false);
            }

            e.Handled = true;
        }
    }

    private void ArtWorkspaceViewport_OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if ((_isPanning && e.ChangedButton == _panButton) ||
            (_movingImage is not null && e.ChangedButton == MouseButton.Left) ||
            (_outputInteraction != OutputInteraction.None && e.ChangedButton == MouseButton.Left))
        {
            CompleteInteraction();
            e.Handled = true;
        }
    }

    private void ArtWorkspaceViewport_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        var pointer = e.GetPosition(ArtWorkspaceViewport);
        var anchor = ScreenToWorld(pointer, viewModel);
        var factor = e.Delta > 0 ? 1.12d : 1d / 1.12d;
        var zoom = Math.Clamp(
            viewModel.Zoom * factor,
            ArtWorkspaceViewModel.MinimumZoom,
            ArtWorkspaceViewModel.MaximumZoom);
        viewModel.SetViewport(
            pointer.X - anchor.X * zoom,
            pointer.Y - anchor.Y * zoom,
            zoom,
            commit: true);
        e.Handled = true;
    }

    private void ArtWorkspaceViewport_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && HasActiveInteraction)
        {
            CancelInteraction(restoreStartValues: true);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete &&
            !IsInteractiveSource(Keyboard.FocusedElement) &&
            ViewModel?.RemoveSelectedImageCommand.CanExecute(null) == true)
        {
            ViewModel.RemoveSelectedImageCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ArtWorkspaceViewport_OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (HasActiveInteraction)
        {
            CompleteInteraction(releaseCapture: false);
        }
    }

    private void ArtWorkspaceView_OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (HasActiveInteraction)
        {
            CompleteInteraction();
        }
    }

    private void ArtWorkspaceViewport_OnPreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void ArtWorkspaceViewport_OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (ViewModel is not { } viewModel ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] paths ||
            paths.Length == 0)
        {
            return;
        }

        var world = ScreenToWorld(e.GetPosition(ArtWorkspaceViewport), viewModel);
        _ = await viewModel.AddImagesAtAsync(paths, world.X, world.Y);
        e.Handled = true;
    }

    private void FocusAll_OnClick(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || ArtWorkspaceViewport.ActualWidth <= 0d || ArtWorkspaceViewport.ActualHeight <= 0d)
        {
            return;
        }

        var bounds = viewModel.GetContentBounds();
        const double padding = 84d;
        var zoom = Math.Clamp(
            Math.Min(
                Math.Max(1d, ArtWorkspaceViewport.ActualWidth - padding * 2d) / bounds.Width,
                Math.Max(1d, ArtWorkspaceViewport.ActualHeight - padding * 2d) / bounds.Height),
            ArtWorkspaceViewModel.MinimumZoom,
            1.5d);
        viewModel.SetViewport(
            padding - bounds.X * zoom,
            padding - bounds.Y * zoom,
            zoom,
            commit: true);
    }

    private void BeginPan(Point screen, MouseButton button, ArtWorkspaceViewModel viewModel)
    {
        _isPanning = true;
        _panButton = button;
        _panStartScreen = screen;
        _panStartX = viewModel.PanX;
        _panStartY = viewModel.PanY;
        ArtWorkspaceViewport.CaptureMouse();
    }

    private void BeginOutputInteraction(
        OutputInteraction interaction,
        Point screen,
        ArtWorkspaceViewModel viewModel)
    {
        _outputInteraction = interaction;
        _moveStartScreen = screen;
        _outputStartX = viewModel.OutputFrameX;
        _outputStartY = viewModel.OutputFrameY;
        _outputStartWidth = viewModel.OutputFrameWidth;
        _outputStartHeight = viewModel.OutputFrameHeight;
        ArtWorkspaceViewport.CaptureMouse();
    }

    private void CompleteInteraction(bool releaseCapture = true)
    {
        if (ViewModel is not { } viewModel)
        {
            ClearInteraction(releaseCapture);
            return;
        }

        if (_isPanning)
        {
            viewModel.SetViewport(viewModel.PanX, viewModel.PanY, viewModel.Zoom, commit: true);
        }
        else if (_movingImage is { } image)
        {
            viewModel.MoveImage(image, image.X, image.Y, commit: true);
        }
        else if (_outputInteraction != OutputInteraction.None)
        {
            viewModel.SetOutputFrame(
                viewModel.OutputFrameX,
                viewModel.OutputFrameY,
                viewModel.OutputFrameWidth,
                viewModel.OutputFrameHeight,
                commit: true);
        }

        ClearInteraction(releaseCapture);
    }

    private void CancelInteraction(bool restoreStartValues)
    {
        if (restoreStartValues && ViewModel is { } viewModel)
        {
            if (_isPanning)
            {
                viewModel.SetViewport(_panStartX, _panStartY, viewModel.Zoom, commit: false);
            }
            else if (_movingImage is { } image)
            {
                viewModel.MoveImage(image, _moveStartX, _moveStartY, commit: false);
            }
            else if (_outputInteraction != OutputInteraction.None)
            {
                viewModel.SetOutputFrame(
                    _outputStartX,
                    _outputStartY,
                    _outputStartWidth,
                    _outputStartHeight,
                    commit: false);
            }
        }

        ClearInteraction(releaseCapture: true);
    }

    private void ClearInteraction(bool releaseCapture)
    {
        _isPanning = false;
        _movingImage = null;
        _outputInteraction = OutputInteraction.None;
        if (releaseCapture && ArtWorkspaceViewport.IsMouseCaptured)
        {
            ArtWorkspaceViewport.ReleaseMouseCapture();
        }
    }

    private bool HasActiveInteraction =>
        _isPanning || _movingImage is not null || _outputInteraction != OutputInteraction.None;

    private static Point ScreenToWorld(Point screen, ArtWorkspaceViewModel viewModel) =>
        new(
            (screen.X - viewModel.PanX) / viewModel.Zoom,
            (screen.Y - viewModel.PanY) / viewModel.Zoom);

    private bool IsInteractiveSource(object? source)
    {
        for (var current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ArtWorkspaceViewport))
            {
                return false;
            }

            if (current is ButtonBase or TextBoxBase or PasswordBox or ComboBox or Selector or Slider or ScrollBar)
            {
                return true;
            }

            if (current is Border { Name: "ArtWorkspaceImageCard" } ||
                ReferenceEquals(current, ArtWorkspaceOutputFrame))
            {
                return true;
            }
        }

        return false;
    }

    private static T? FindAncestor<T>(DependencyObject? source, T target) where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, target))
            {
                return target;
            }
        }

        return null;
    }

    private enum OutputInteraction
    {
        None,
        Move,
        Resize
    }
}

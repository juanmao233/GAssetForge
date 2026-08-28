using GAssetForge.App.Infrastructure;
using GAssetForge.App.ViewModels;
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace GAssetForge.App.Views;

public partial class CanvasDocumentView : UserControl
{
    private FrameworkElement? _dragSource;
    private Point _dragStart;
    private bool _moveTransactionOpen;
    private INotifyCollectionChanged? _observedConversation;
    private readonly HashSet<INotifyPropertyChanged> _observedConversationMessages = [];

    public CanvasDocumentView()
    {
        InitializeComponent();
        Loaded += CanvasDocumentView_OnLoaded;
        Unloaded += CanvasDocumentView_OnUnloaded;
        DataContextChanged += CanvasDocumentView_OnDataContextChanged;
    }

    private EditorWorkspaceViewModel? Workspace => DataContext as EditorWorkspaceViewModel;

    private void CanvasDocumentView_OnLoaded(object sender, RoutedEventArgs e)
    {
        ObservePageUiConversation();
    }

    private void CanvasDocumentView_OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopObservingPageUiConversation();
    }

    private void CanvasDocumentView_OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(ObservePageUiConversation));
    }

    private void ObservePageUiConversation()
    {
        StopObservingPageUiConversation();

        if (PageUiConversationList.ItemsSource is not { } itemsSource)
        {
            return;
        }

        if (itemsSource is INotifyCollectionChanged observableCollection)
        {
            _observedConversation = observableCollection;
            _observedConversation.CollectionChanged += PageUiConversation_OnCollectionChanged;
        }

        ObserveConversationMessages(itemsSource);
        ScrollPageUiConversationToEnd();
    }

    private void StopObservingPageUiConversation()
    {
        if (_observedConversation is not null)
        {
            _observedConversation.CollectionChanged -= PageUiConversation_OnCollectionChanged;
            _observedConversation = null;
        }

        foreach (var message in _observedConversationMessages)
        {
            message.PropertyChanged -= PageUiConversationMessage_OnPropertyChanged;
        }

        _observedConversationMessages.Clear();
    }

    private void PageUiConversation_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var message in _observedConversationMessages)
            {
                message.PropertyChanged -= PageUiConversationMessage_OnPropertyChanged;
            }

            _observedConversationMessages.Clear();
            if (PageUiConversationList.ItemsSource is { } itemsSource)
            {
                ObserveConversationMessages(itemsSource);
            }
        }
        else
        {
            if (e.OldItems is not null)
            {
                foreach (var message in e.OldItems.OfType<INotifyPropertyChanged>())
                {
                    message.PropertyChanged -= PageUiConversationMessage_OnPropertyChanged;
                    _observedConversationMessages.Remove(message);
                }
            }

            if (e.NewItems is not null)
            {
                ObserveConversationMessages(e.NewItems);
            }
        }

        ScrollPageUiConversationToEnd();
    }

    private void ObserveConversationMessages(IEnumerable messages)
    {
        foreach (var message in messages.OfType<INotifyPropertyChanged>())
        {
            if (_observedConversationMessages.Add(message))
            {
                message.PropertyChanged += PageUiConversationMessage_OnPropertyChanged;
            }
        }
    }

    private void PageUiConversationMessage_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        ScrollPageUiConversationToEnd();
    }

    private void ScrollPageUiConversationToEnd()
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => PageUiConversationScrollViewer.ScrollToEnd()));
    }

    private void PageUiInstructionTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        PageUiInstructionTextBox
            .GetBindingExpression(TextBox.TextProperty)
            ?.UpdateSource();

        var command = PageUiSubmitInstructionButton.Command;
        var parameter = PageUiSubmitInstructionButton.CommandParameter;
        if (command?.CanExecute(parameter) == true)
        {
            command.Execute(parameter);
        }

        e.Handled = true;
    }

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

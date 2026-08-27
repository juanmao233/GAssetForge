using GAssetForge.App.ViewModels;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace GAssetForge.App.Views;

public partial class ArtWorkspacePropertiesView : UserControl
{
    private ArtWorkspaceViewModel? _observedViewModel;

    public ArtWorkspacePropertiesView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ObserveViewModel(DataContext as ArtWorkspaceViewModel);
        ScrollConversationToEnd();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => ObserveViewModel(null);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ObserveViewModel(e.NewValue as ArtWorkspaceViewModel);
        }
    }

    private void ObserveViewModel(ArtWorkspaceViewModel? viewModel)
    {
        if (ReferenceEquals(_observedViewModel, viewModel))
        {
            return;
        }

        if (_observedViewModel is not null)
        {
            _observedViewModel.Conversation.CollectionChanged -= OnConversationChanged;
            foreach (var message in _observedViewModel.Conversation)
            {
                message.PropertyChanged -= OnConversationMessagePropertyChanged;
            }
        }

        _observedViewModel = viewModel;
        if (_observedViewModel is not null)
        {
            _observedViewModel.Conversation.CollectionChanged += OnConversationChanged;
            foreach (var message in _observedViewModel.Conversation)
            {
                message.PropertyChanged += OnConversationMessagePropertyChanged;
            }
        }
    }

    private void OnConversationChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var message in e.OldItems.OfType<ArtWorkspaceConversationMessageViewModel>())
            {
                message.PropertyChanged -= OnConversationMessagePropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var message in e.NewItems.OfType<ArtWorkspaceConversationMessageViewModel>())
            {
                message.PropertyChanged += OnConversationMessagePropertyChanged;
            }
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ScrollConversationToEnd);
    }

    private void OnConversationMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ArtWorkspaceConversationMessageViewModel.Text) or
            nameof(ArtWorkspaceConversationMessageViewModel.State))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ScrollConversationToEnd);
        }
    }

    private void ScrollConversationToEnd() => ArtWorkspaceConversationScrollViewer.ScrollToEnd();

    private void ArtWorkspaceImageLayerList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ArtWorkspaceViewModel viewModel &&
            ArtWorkspaceImageLayerList.SelectedItem is ArtWorkspaceImageItemViewModel image)
        {
            viewModel.SelectImage(image);
        }
    }

    private void ArtWorkspaceInstructionTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter ||
            !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
            DataContext is not ArtWorkspaceViewModel viewModel ||
            !viewModel.SubmitInstructionCommand.CanExecute(null))
        {
            return;
        }

        viewModel.SubmitInstructionCommand.Execute(null);
        e.Handled = true;
        ArtWorkspaceInstructionTextBox.Focus();
    }
}

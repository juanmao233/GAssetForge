using GAssetForge.App.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace GAssetForge.App.Views;

public partial class CommandConsoleView : UserControl
{
    private CommandConsoleViewModel? _attachedViewModel;

    public CommandConsoleView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel(DataContext as CommandConsoleViewModel);
        ScrollToEnd();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        DetachViewModel();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
        {
            AttachViewModel(e.NewValue as CommandConsoleViewModel);
        }
    }

    private void AttachViewModel(CommandConsoleViewModel? viewModel)
    {
        if (ReferenceEquals(_attachedViewModel, viewModel))
        {
            return;
        }

        DetachViewModel();
        _attachedViewModel = viewModel;
        if (_attachedViewModel is not null)
        {
            _attachedViewModel.ScrollToEndRequested += OnScrollToEndRequested;
        }
    }

    private void DetachViewModel()
    {
        if (_attachedViewModel is not null)
        {
            _attachedViewModel.ScrollToEndRequested -= OnScrollToEndRequested;
            _attachedViewModel = null;
        }
    }

    private void OnScrollToEndRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(ScrollToEnd, DispatcherPriority.Background);

    public void FocusCommandInput()
    {
        CommandInputTextBox.CaretIndex = CommandInputTextBox.Text.Length;
        CommandInputTextBox.Focus();
        Keyboard.Focus(CommandInputTextBox);
    }

    private void ScrollToEnd()
    {
        if (_attachedViewModel is not { AutoScroll: true } viewModel || viewModel.FilteredEntries.IsEmpty)
        {
            return;
        }

        var lastEntry = viewModel.FilteredEntries.Cast<object>().LastOrDefault();
        if (lastEntry is not null)
        {
            LogDataGrid.ScrollIntoView(lastEntry);
        }
    }

    private void CommandInput_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_attachedViewModel is not { } viewModel)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                if (viewModel.ExecuteCommandCommand.CanExecute(null))
                {
                    viewModel.ExecuteCommandCommand.Execute(null);
                }

                e.Handled = true;
                break;

            case Key.Up:
                if (viewModel.PreviousHistoryCommand.CanExecute(null))
                {
                    viewModel.PreviousHistoryCommand.Execute(null);
                    MoveCommandCaretToEnd();
                }

                e.Handled = true;
                break;

            case Key.Down:
                if (viewModel.NextHistoryCommand.CanExecute(null))
                {
                    viewModel.NextHistoryCommand.Execute(null);
                    MoveCommandCaretToEnd();
                }

                e.Handled = true;
                break;
        }
    }

    private void MoveCommandCaretToEnd() =>
        CommandInputTextBox.Dispatcher.BeginInvoke(
            FocusCommandInput,
            DispatcherPriority.Input);
}

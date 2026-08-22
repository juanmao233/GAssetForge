using System.Windows.Input;
using System.Windows.Threading;

namespace GAssetForge.App.Infrastructure;

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() =>
        RaiseOnDispatcher(_dispatcher, () => CanExecuteChanged?.Invoke(this, EventArgs.Empty));

    private static void RaiseOnDispatcher(Dispatcher dispatcher, Action action)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action, DispatcherPriority.DataBind);
        }
    }
}

public sealed class RelayCommand<T>(Action<T?> execute, Func<T?, bool>? canExecute = null) : ICommand
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(ConvertParameter(parameter)) ?? true;

    public void Execute(object? parameter) => execute(ConvertParameter(parameter));

    public void RaiseCanExecuteChanged()
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _dispatcher.BeginInvoke(
                () => CanExecuteChanged?.Invoke(this, EventArgs.Empty),
                DispatcherPriority.DataBind);
        }
    }

    private static T? ConvertParameter(object? parameter)
    {
        if (parameter is null)
        {
            return default;
        }

        if (parameter is T value)
        {
            return value;
        }

        return (T?)Convert.ChangeType(parameter, typeof(T));
    }
}

public sealed class AsyncRelayCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null) : ICommand
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isExecuting && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute().ConfigureAwait(true);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _dispatcher.BeginInvoke(
                () => CanExecuteChanged?.Invoke(this, EventArgs.Empty),
                DispatcherPriority.DataBind);
        }
    }
}

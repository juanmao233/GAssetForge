using System.Windows.Input;

namespace GAssetForge.App.Infrastructure;

public sealed class GatedCommand : ICommand, IDisposable
{
    private readonly ICommand _inner;
    private readonly Func<bool> _gate;
    private bool _isDisposed;

    public GatedCommand(ICommand inner, Func<bool> gate)
    {
        _inner = inner;
        _gate = gate;
        _inner.CanExecuteChanged += OnInnerCanExecuteChanged;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isDisposed && _gate() && _inner.CanExecute(parameter);

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            _inner.Execute(parameter);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _inner.CanExecuteChanged -= OnInnerCanExecuteChanged;
        RaiseCanExecuteChanged();
    }

    private void OnInnerCanExecuteChanged(object? sender, EventArgs e) => RaiseCanExecuteChanged();
}

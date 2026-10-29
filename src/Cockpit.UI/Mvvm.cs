using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Cockpit.UI;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>ICommand without a framework dependency. Call <see cref="Refresh"/> when CanExecute may have changed.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) _execute(parameter);
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Marshals runtime events (thread pool) onto the UI thread. WPF: Dispatcher.BeginInvoke.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Runs actions inline under a lock: for tests and console hosts. Keeps the ViewModel single-threaded in effect.</summary>
public sealed class InlineDispatcher : IUiDispatcher
{
    private readonly object _gate = new();
    public void Post(Action action)
    {
        lock (_gate) action();
    }
}

using System.Windows.Input;

namespace ClaudeSessionBackup.App.Services;

/// <summary>Standard delegate command.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public static void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}

/// <summary>
/// Async command that blocks re-entry while its task is in flight.
/// </summary>
/// <remarks>
/// The re-entry guard is the point. Every long operation in this app - running
/// a backup, rebuilding the catalog - is triggered by a button, and a double-click
/// on "Backup now" would otherwise start a second run against the same destination.
/// The engine also refuses that at its own level, but throwing an exception at the
/// user is a worse answer than a button that is simply disabled while it works.
/// </remarks>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Predicate<object?>? _canExecute;
    private bool _running;

    public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) =>
        !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (_running)
        {
            return;
        }

        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await _execute(parameter).ConfigureAwait(true);
        }
        finally
        {
            // finally, not after the await: if the task faults, leaving
            // _running true would disable the button permanently and the only
            // recovery would be restarting the app.
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

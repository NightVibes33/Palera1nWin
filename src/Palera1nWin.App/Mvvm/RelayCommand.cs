using System.Windows.Input;
using Palera1nWin.Core.Settings;

namespace Palera1nWin.App.Mvvm;

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public void RaiseCanExecuteChanged() =>
        System.Windows.Application.Current?.Dispatcher.Invoke(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private int _isRunning;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;
    public bool CanExecute(object? parameter) => !IsRunning && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter) || Interlocked.Exchange(ref _isRunning, 1) != 0) return;
        RaiseCanExecuteChanged();
        try
        {
            await _execute().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Commands own their user-facing cancellation state.
        }
        catch (Exception exception)
        {
            var path = WriteFailure(exception);
            System.Windows.MessageBox.Show(
                $"Unexpected error: {exception.Message}\n\nThe full error was written to:\n{path ?? AppSettings.LogsDirectory}",
                "Palera1nWin",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() =>
        System.Windows.Application.Current?.Dispatcher.Invoke(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));

    private static string? WriteFailure(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.LogsDirectory);
            var path = Path.Combine(
                AppSettings.LogsDirectory,
                $"command-error-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            File.WriteAllText(path, exception.ToString());
            return path;
        }
        catch
        {
            return null;
        }
    }
}

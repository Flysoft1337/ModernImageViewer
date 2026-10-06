using System.Windows.Input;

namespace ModernImageViewer.UI.Commands;

public sealed class AsyncRelayCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null,
    Action<Exception>? onError = null,
    bool allowConcurrent = false) : ICommand
{
    private int _executionCount;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => (allowConcurrent || _executionCount == 0) && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        await ExecuteAsync();
    }

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
        {
            return;
        }

        _executionCount++;
        RaiseCanExecuteChanged();
        try
        {
            await execute();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            onError?.Invoke(exception);
        }
        finally
        {
            _executionCount--;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

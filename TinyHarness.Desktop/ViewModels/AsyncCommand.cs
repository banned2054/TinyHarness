using System.Windows.Input;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
/// <para>等待一次界面操作并报告异常；每个命令实例只阻止自身重入。</para>
/// <para>Awaits one UI operation and reports errors; each instance prevents only its own reentry.</para>
/// </summary>
public sealed class AsyncCommand(Func<Task> execute, Action<Exception> error) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running;

    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(); }
        catch (Exception exception) { error(exception); }
        finally
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

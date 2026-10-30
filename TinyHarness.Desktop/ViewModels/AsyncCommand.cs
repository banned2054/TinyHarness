using System.Diagnostics;
using System.Windows.Input;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>等待一次界面操作并报告异常；每个命令实例只阻止自身重入。</para>
///     <para>Awaits one UI operation and reports errors; each instance prevents only its own reentry.</para>
/// </summary>
public sealed class AsyncCommand(Func<Task> execute, Action<Exception> error) : ICommand
{
    private  bool              _running;
    internal Exception?        LastError { get; private set; }
    public event EventHandler? CanExecuteChanged;
    public bool                CanExecute(object? parameter) => !_running;

    public async void Execute(object? parameter) => await ExecuteAsync();

    internal async Task ExecuteAsync()
    {
        if (_running) return;
        _running  = true;
        LastError = null;
        List<Exception> errors = [];
        try
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            await execute();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
        finally
        {
            _running = false;
            try
            {
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }

        if (errors.Count == 0) return;
        LastError = errors.Count == 1 ? errors[0] : new AggregateException(errors);
        try
        {
            error(LastError);
        }
        catch (Exception exception)
        {
            LastError = new AggregateException(LastError, exception);
            // 错误展示自身失效时保留原始异常，但不将可能含凭据的消息写入诊断。
            Trace.TraceError("Desktop command error reporting failed ({0}).", exception.GetType().Name);
        }
    }
}

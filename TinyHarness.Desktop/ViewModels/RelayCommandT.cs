using System.Windows.Input;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>带参数的同步命令；用于列表行选择、分组展开等轻量交互。</summary>
public sealed class RelayCommand<T>(Action<T?> execute, Func<T?, bool>? canExecute = null) : ICommand
{
    private readonly Func<T?, bool>? _canExecute = canExecute;
    private readonly Action<T?>      _execute    = execute ?? throw new ArgumentNullException(nameof(execute));

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(Convert(parameter)) ?? true;

    public void Execute(object? parameter)
    {
        _execute(Convert(parameter));
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private static T? Convert(object? parameter) => parameter is T typed ? typed : default;
}

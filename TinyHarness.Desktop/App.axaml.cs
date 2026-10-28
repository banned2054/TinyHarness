using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TinyHarness.Desktop.Presentation.Views;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var model = new MainWindowViewModel();
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow   = new MainWindow(model);
            // 等窗口真正显示（Opened 由生命周期的 Show 触发）后再启动轻量准备与自动整理；
            // 赋值 MainWindow 不代表已显示，提前重初始化会在窗口出现前占用 UI 线程。
            desktop.MainWindow.Opened += (_, _) => _ = model.StartAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

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
            desktop.MainWindow = new MainWindow(model);
            _ = model.InitializeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Presentation.Views;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _model;
    private          bool                _allowClose;
    private          bool                _closing;

    public MainWindow() : this(new MainWindowViewModel())
    {
    }

    public MainWindow(MainWindowViewModel model)
    {
        InitializeComponent();
        _model      =  model;
        DataContext =  model;
        Closing     += OnClosing;
    }

    private async void BrowseFolder(object? sender, RoutedEventArgs args)
    {
        if (sender is not Control { DataContext: SessionViewModel session } || !session.CanChooseWorkspace) return;
        try
        {
            var folders =
                await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path && session.CanChooseWorkspace &&
                ReferenceEquals(_model.SelectedSession, session)) await _model.OpenProjectAsync(path);
        }
        catch (Exception exception)
        {
            session.ShowError(exception);
        }
    }

    private async void OpenProjectFolder(object? sender, RoutedEventArgs args)
    {
        try
        {
            var folders =
                await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) await _model.OpenProjectAsync(path);
        }
        catch (Exception exception)
        {
            _model.Notice = exception.Message;
        }
    }

    private void SelectHistory(object? sender, RoutedEventArgs args)
    {
        if (sender is Control { DataContext: SessionViewModel session }) _model.SelectedSession = session;
    }

    /// <summary>
    ///     <para>先等待协调层完成关闭，再退出桌面消息循环，保证后台回调仍可被 UI 接收。</para>
    ///     <para>
    ///         Awaits coordinator shutdown before exiting the desktop event loop so background callbacks can still reach the
    ///         UI.
    ///     </para>
    /// </summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_closing) return;
        _closing  = true;
        IsEnabled = false;
        try
        {
            await _model.DisposeAsync();
            _allowClose = true;
            if (App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
        }
        catch (Exception exception)
        {
            _model.Notice = $"关闭未完成：{exception.Message}";
            _closing      = false;
            IsEnabled     = true;
        }
    }
}

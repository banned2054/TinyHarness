using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Presentation.Views;

public sealed partial class MainWindow : Window
{
    // 侧栏拖拽常量：宽度上限 = min(480, 可用宽 - 右栏最小 560)；
    // splitter 布局列宽 0，命中区悬跨分界线、不占布局宽度。
    private const double SidebarMinWidth     = 240;
    private const double SidebarMaxWidth     = 480;
    private const double SidebarDefaultWidth = 280;
    private const double ContentMinWidth     = 560;

    private readonly MainWindowViewModel _model;
    private          bool                _allowClose;
    private          bool                _closing;

    public MainWindow() : this(new MainWindowViewModel())
    {
    }

    public MainWindow(MainWindowViewModel model)
    {
        InitializeComponent();
        _model                  =  model;
        DataContext             =  model;
        Closing                 += OnClosing;
        ContentGrid.SizeChanged += OnContentGridSizeChanged;
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

    // Avalonia 不为 ColumnDefinition 的 x:Name 生成字段，按位置取列。
    private ColumnDefinition SidebarColumn => ContentGrid.ColumnDefinitions[0];

    /// <summary>窗口尺寸变化后重新 clamp 左栏宽度。</summary>
    private void OnContentGridSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ClampSidebarWidth();
    }

    /// <summary>
    ///     <para>
    ///         左栏宽度 clamp 到 [240, min(480, 可用宽-560)]；上限同步写入列与侧栏内容的 MaxWidth，
    ///         让 GridSplitter 拖拽在右栏最小宽 560 之前停下。
    ///     </para>
    ///     <para>
    ///         Clamps the sidebar width to [240, min(480, available-560)]; the limit also goes to the sidebar surface so
    ///         dragging stops before the 560px minimum of the content column.
    ///     </para>
    /// </summary>
    private void ClampSidebarWidth()
    {
        var available = ContentGrid.Bounds.Width;
        var upper     = Math.Max(SidebarMinWidth, Math.Min(SidebarMaxWidth, available - ContentMinWidth));
        SidebarColumn.MaxWidth = upper;
        Sidebar.SetSurfaceMaxWidth(upper);
        var current = SidebarColumn.Width.IsAbsolute ? SidebarColumn.Width.Value : SidebarDefaultWidth;
        SidebarColumn.Width = new GridLength(Math.Clamp(current, SidebarMinWidth, upper), GridUnitType.Pixel);
    }
}

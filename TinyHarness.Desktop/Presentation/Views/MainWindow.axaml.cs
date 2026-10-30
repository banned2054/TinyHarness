using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Presentation.Views;

public sealed partial class MainWindow : Window
{
    // 侧栏拖拽常量：宽度上限 = min(480, 可用宽 - 右栏最小 560)；
    // splitter 布局列宽 0，命中区悬跨分界线、不占布局宽度。
    private const    double               SidebarMinWidth     = 240;
    private const    double               SidebarMaxWidth     = 480;
    private const    double               SidebarDefaultWidth = 280;
    private const    double               ContentMinWidth     = 560;
    private readonly DesktopCloseWorkflow _closeWorkflow;

    private readonly MainWindowViewModel _model;
    private          bool                _allowClose;

    public MainWindow() : this(new MainWindowViewModel())
    {
    }

    public MainWindow(MainWindowViewModel model)
    {
        InitializeComponent();
        _model = model;
        _closeWorkflow = new DesktopCloseWorkflow(async () =>
        {
            await _model.DisposeAsync();
            DesktopLifecycleDiagnostics.Record("window-cleanup-completed");
        }, ConfirmExitAsync, Exit);
        DataContext             =  model;
        Closing                 += OnClosing;
        ContentGrid.SizeChanged += OnContentGridSizeChanged;
    }

    // Avalonia 不为 ColumnDefinition 的 x:Name 生成字段，按位置取列。
    private ColumnDefinition SidebarColumn => ContentGrid.ColumnDefinitions[0];

    private async void BrowseFolder(object? sender, RoutedEventArgs args)
    {
        if (sender is not Control { DataContext: SessionViewModel session } || !session.CanChooseWorkspace) return;
        // 入口可能位于空白页目标菜单内：先收起弹层再打开系统目录选择。
        session.IsWorkspaceMenuOpen = false;
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
        DesktopLifecycleDiagnostics.Record("window-close-requested");
        if (Content is Control content) content.IsEnabled = false;
        try
        {
            await _closeWorkflow.RequestCloseAsync();
        }
        catch (Exception exception)
        {
            DesktopLifecycleDiagnostics.Record("window-close-prompt-failed", exception);
            _model.Notice = "退出确认未完成，请再次关闭窗口。";
        }
    }

    /// <summary>
    ///     <para>清理尝试结束后询问是否仍然退出；默认选择保留窗口，异常详情仅进入诊断。</para>
    ///     <para>
    ///         Asks whether to exit after cleanup attempts finish; defaults to retaining the window and sends exception
    ///         details only to diagnostics.
    ///     </para>
    /// </summary>
    private Task<bool> ConfirmExitAsync(Exception exception)
    {
        DesktopLifecycleDiagnostics.Record("window-cleanup-failed", exception);
        _model.Notice = "关闭清理异常，可能有内容未保存。再次关闭窗口可重新确认退出。";
        var dialog = new Window
        {
            Title                 = "关闭清理异常",
            Width                 = 460,
            SizeToContent         = SizeToContent.Height,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var stay  = new Button { Content = "暂不退出", IsCancel = true, IsDefault = true };
        var leave = new Button { Content = "仍然退出" };
        stay.Click  += (_, _) => dialog.Close(false);
        leave.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin  = new Thickness(24),
            Spacing = 20,
            Children =
            {
                new TextBlock
                {
                    Text         = "关闭清理异常，可能有内容未保存，是否仍然退出？\n\n暂不退出后，应用将保持关闭状态；再次关闭窗口可重新确认。",
                    TextWrapping = TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation         = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing             = 12,
                    Children            = { stay, leave }
                }
            }
        };
        return dialog.ShowDialog<bool>(this);
    }

    private void Exit()
    {
        _allowClose = true;
        DesktopLifecycleDiagnostics.Record("window-exit-approved");
        if (App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
        else Close();
    }

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

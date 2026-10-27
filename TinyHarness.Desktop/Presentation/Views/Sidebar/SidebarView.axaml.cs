using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Presentation.Views.Sidebar;

/// <summary>
///     <para>侧栏视图：打开项目目录的文件夹对话框与重命名弹窗的开关聚焦属于界面行为。</para>
///     <para>Sidebar view: the project folder picker and rename-popup focus are view behaviors.</para>
/// </summary>
public sealed partial class SidebarView : UserControl
{
    public SidebarView()
    {
        InitializeComponent();
        ProjectRenameInput.AddHandler(KeyDownEvent, OnRenameInputKeyDown, RoutingStrategies.Tunnel);
        ProjectRenamePopup.Opened += OnProjectRenamePopupOpened;
        ProjectRenamePopup.AddHandler(KeyDownEvent, OnRenamePopupKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>
    ///     <para>同步左栏内容表面的宽度上限；列宽上限由 MainWindow 的 clamp 逻辑统一计算。</para>
    ///     <para>
    ///         Syncs the sidebar surface width limit; the column limit is computed by MainWindow's clamp logic.
    ///     </para>
    /// </summary>
    public void SetSurfaceMaxWidth(double maxWidth)
    {
        Surface.MaxWidth = maxWidth;
    }

    /// <summary>
    ///     <para>文件夹对话框取得路径后交给根 ViewModel 打开项目；取消即结束。</para>
    ///     <para>Hands a picked folder path to the root view model; picking nothing ends silently.</para>
    /// </summary>
    private async void OnOpenProjectClick(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MainWindowViewModel viewModel || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        try
        {
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { AllowMultiple = false });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) await viewModel.OpenProjectAsync(path);
        }
        catch (Exception exception)
        {
            viewModel.Notice = exception.Message;
        }
    }

    /// <summary>重命名弹窗展开即聚焦输入框并全选当前名（对齐 DSH 重命名 Modal）。</summary>
    private void OnProjectRenamePopupOpened(object? sender, EventArgs eventArgs)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ProjectRenameInput.Focus();
            ProjectRenameInput.SelectAll();
        });
    }

    /// <summary>重命名输入框内 Enter 直接确认（等价点击「重命名」，守卫在命令方法内）。</summary>
    private void OnRenameInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainWindowViewModel viewModel)
        {
            e.Handled = true;
            viewModel.ConfirmProjectRenameCommand.Execute(null);
        }
    }

    /// <summary>轻失焦关不掉的 Esc 由 code-behind 兜底：重命名弹窗 Esc 等价取消。</summary>
    private void OnRenamePopupKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainWindowViewModel viewModel) return;

        e.Handled = true;
        viewModel.CancelProjectRenameCommand.Execute(null);
    }
}

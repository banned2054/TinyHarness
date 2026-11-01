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
    private static readonly DataFormat<ProjectViewModel> ProjectReorderDataFormat =
        DataFormat.CreateInProcessFormat<ProjectViewModel>("TinyHarness.ProjectViewModel");

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
    ///     <para>仅从可编辑项目的鼠标拖动手柄启动重排。</para>
    ///     <para>Starts project reordering only from an editable project's mouse drag handle.</para>
    /// </summary>
    private async void OnProjectDragHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control handle                              || e.Pointer.Type != PointerType.Mouse ||
            !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed ||
            handle.DataContext is not ProjectViewModel project        || !project.CanReorder ||
            DataContext is not MainWindowViewModel viewModel) return;

        e.Handled = true;
        try
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(ProjectReorderDataFormat, project));
            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
        }
        catch (Exception exception)
        {
            viewModel.Notice = exception.Message;
        }
    }

    /// <summary>
    ///     <para>只接受同一置顶类别中可编辑且允许手动排序的项目。</para>
    ///     <para>Accepts only editable projects that can be manually reordered in the same pinned category.</para>
    /// </summary>
    private void OnProjectHeaderDragOver(object? sender, DragEventArgs e)
    {
        try
        {
            e.DragEffects = sender is Border targetHeader                       &&
                            targetHeader.DataContext is ProjectViewModel target &&
                            CanMoveProject(e, target, out _)
                ? DragDropEffects.Move
                : DragDropEffects.None;
            e.Handled = true;
        }
        catch (Exception exception)
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled     = true;
            if (DataContext is MainWindowViewModel viewModel) viewModel.Notice = exception.Message;
        }
    }

    /// <summary>
    ///     <para>按指针位于项目行中线的前后，将项目插入目标之前或之后。</para>
    ///     <para>Inserts the project before or after the target based on the pointer's position relative to its midpoint.</para>
    /// </summary>
    private void OnProjectHeaderDrop(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        e.Handled     = true;
        try
        {
            if (sender is not Border targetHeader                       ||
                targetHeader.DataContext is not ProjectViewModel target ||
                !CanMoveProject(e, target, out var source)              || source is null ||
                DataContext is not MainWindowViewModel viewModel) return;

            var pointerPosition = e.GetPosition(targetHeader);
            var insertAfter     = pointerPosition.Y >= targetHeader.Bounds.Height / 2;
            viewModel.MoveProject(source, target, insertAfter);
            e.DragEffects = DragDropEffects.Move;
        }
        catch (Exception exception)
        {
            if (DataContext is MainWindowViewModel viewModel) viewModel.Notice = exception.Message;
        }
    }

    /// <summary>
    ///     <para>检查拖动数据、项目可编辑性、手动排序状态和置顶类别是否兼容。</para>
    ///     <para>Checks whether drag data, editability, manual sorting, and pinned categories are compatible.</para>
    /// </summary>
    private static bool CanMoveProject(DragEventArgs e, ProjectViewModel target, out ProjectViewModel? source)
    {
        source = e.DataTransfer.TryGetValue(ProjectReorderDataFormat);
        return source is { CanEdit: true, CanReorder: true } &&
               target.CanEdit && target.CanReorder && source.IsPinned == target.IsPinned &&
               !ReferenceEquals(source, target);
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
                                                                               new FolderPickerOpenOptions
                                                                                   { AllowMultiple = false });
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

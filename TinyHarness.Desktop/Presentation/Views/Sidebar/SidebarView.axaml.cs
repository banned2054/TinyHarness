using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Presentation.Views.Sidebar;

/// <summary>
///     <para>侧栏视图：文件夹选择、项目操作菜单、重命名弹窗焦点和手动排序拖动属于界面行为。</para>
///     <para>
///         Sidebar view: folder picking, project actions, rename-popup focus, and manual-reorder dragging are view
///         behaviors.
///     </para>
/// </summary>
public sealed partial class SidebarView : UserControl
{
    private static readonly DataFormat<ProjectViewModel> ProjectReorderDataFormat =
        DataFormat.CreateInProcessFormat<ProjectViewModel>("TinyHarness.ProjectViewModel");

    private static readonly DataFormat<SessionViewModel> SessionReorderDataFormat =
        DataFormat.CreateInProcessFormat<SessionViewModel>("TinyHarness.SessionViewModel");

    private Button? _projectReorderClickToSuppress;

    private (Button Button, ProjectViewModel Project, PointerPressedEventArgs Pressed, Point Start)?
        _projectReorderGesture;

    private Button? _sessionReorderClickToSuppress;

    private (Button Button, SessionViewModel Session, PointerPressedEventArgs Pressed, Point Start)?
        _sessionReorderGesture;

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
    ///     <para>记录三点按钮上的鼠标按下，移动超过拖动阈值时仍可重排项目。</para>
    ///     <para>Records a mouse press on the ellipsis button so dragging beyond the threshold can still reorder projects.</para>
    /// </summary>
    private void OnProjectActionsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _projectReorderGesture         = null;
        _projectReorderClickToSuppress = null;
        if (sender is not Button button                               ||
            e.Pointer.Type != PointerType.Mouse                       ||
            !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed ||
            button.DataContext is not ProjectViewModel { CanReorder: true } project)
            return;

        _projectReorderGesture = (button, project, e, e.GetPosition(button));
    }

    /// <summary>
    ///     <para>三点按钮保持点击开菜单；鼠标移动超过阈值时启动项目重排。</para>
    ///     <para>A click opens the actions menu; moving the mouse beyond the threshold starts project reordering.</para>
    /// </summary>
    private async void OnProjectActionsPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_projectReorderGesture is not { } gesture ||
            !ReferenceEquals(sender, gesture.Button)  ||
            !ReferenceEquals(e.Pointer, gesture.Pressed.Pointer))
            return;

        if (!gesture.Project.CanReorder || !e.GetCurrentPoint(gesture.Button).Properties.IsLeftButtonPressed)
        {
            _projectReorderGesture = null;
            return;
        }

        var offset = e.GetPosition(gesture.Button) - gesture.Start;
        if (offset.X * offset.X + offset.Y * offset.Y < 25) return;

        _projectReorderGesture         = null;
        _projectReorderClickToSuppress = gesture.Button;
        e.Handled                      = true;
        if (DataContext is not MainWindowViewModel viewModel) return;

        try
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(ProjectReorderDataFormat, gesture.Project));
            await DragDrop.DoDragDropAsync(gesture.Pressed, transfer, DragDropEffects.Move);
        }
        catch (Exception exception)
        {
            viewModel.Notice = exception.Message;
        }
    }

    /// <summary>
    ///     <para>鼠标点击三点按钮时切换项目操作菜单；拖动手势不打开菜单。</para>
    ///     <para>Toggles the project actions menu on click and ignores a click consumed by dragging.</para>
    /// </summary>
    private void OnProjectActionsButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ProjectViewModel project } button) return;
        if (ReferenceEquals(_projectReorderClickToSuppress, button))
        {
            _projectReorderClickToSuppress = null;
            return;
        }

        project.IsActionsMenuOpen = !project.IsActionsMenuOpen;
    }

    /// <summary>
    ///     <para>释放三点按钮上的鼠标时结束尚未启动重排的手势，并清理拖动后的点击抑制状态。</para>
    ///     <para>Ends an unstarted reorder gesture on release and clears click suppression after dragging.</para>
    /// </summary>
    private void OnProjectActionsPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Button button) return;
        if (_projectReorderGesture is { } gesture && ReferenceEquals(button, gesture.Button))
            _projectReorderGesture = null;
        if (!ReferenceEquals(_projectReorderClickToSuppress, button)) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_projectReorderClickToSuppress, button))
                _projectReorderClickToSuppress = null;
        });
    }

    /// <summary>
    ///     <para>记录空闲会话行上的鼠标按下，移动超过拖动阈值时可调整顺序。</para>
    ///     <para>Records a mouse press on an idle session row so a drag beyond the threshold can reorder it.</para>
    /// </summary>
    private void OnSessionRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _sessionReorderGesture         = null;
        _sessionReorderClickToSuppress = null;
        if (sender is not Button button                               ||
            e.Pointer.Type != PointerType.Mouse                       ||
            !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed ||
            button.DataContext is not SessionViewModel { CanReorder: true } session)
            return;

        _sessionReorderGesture = (button, session, e, e.GetPosition(button));
    }

    /// <summary>
    ///     <para>普通点击仍选择会话；鼠标移动超过阈值时启动会话重排。</para>
    ///     <para>A click still selects the session; moving the mouse beyond the threshold starts reordering.</para>
    /// </summary>
    private async void OnSessionRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_sessionReorderGesture is not { } gesture ||
            !ReferenceEquals(sender, gesture.Button)  ||
            !ReferenceEquals(e.Pointer, gesture.Pressed.Pointer))
            return;

        if (!gesture.Session.CanReorder || !e.GetCurrentPoint(gesture.Button).Properties.IsLeftButtonPressed)
        {
            _sessionReorderGesture = null;
            return;
        }

        var offset = e.GetPosition(gesture.Button) - gesture.Start;
        if (offset.X * offset.X + offset.Y * offset.Y < 25) return;

        _sessionReorderGesture         = null;
        _sessionReorderClickToSuppress = gesture.Button;
        e.Handled                      = true;
        if (DataContext is not MainWindowViewModel viewModel) return;

        try
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(SessionReorderDataFormat, gesture.Session));
            await DragDrop.DoDragDropAsync(gesture.Pressed, transfer, DragDropEffects.Move);
        }
        catch (Exception exception)
        {
            viewModel.Notice = exception.Message;
        }
    }

    /// <summary>
    ///     <para>释放会话行时结束尚未启动的手势，并清理拖动后的点击抑制状态。</para>
    ///     <para>Ends an unstarted session gesture on release and clears click suppression after dragging.</para>
    /// </summary>
    private void OnSessionRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Button button) return;
        if (_sessionReorderGesture is { } gesture && ReferenceEquals(button, gesture.Button))
            _sessionReorderGesture = null;
        if (!ReferenceEquals(_sessionReorderClickToSuppress, button)) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_sessionReorderClickToSuppress, button))
                _sessionReorderClickToSuppress = null;
        });
    }

    /// <summary>
    ///     <para>抑制拖动手势结束时的误选；普通点击仍执行会话选择命令。</para>
    ///     <para>Suppresses accidental selection after dragging while preserving normal session selection clicks.</para>
    /// </summary>
    private void OnSessionRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SessionViewModel session } button ||
            DataContext is not MainWindowViewModel viewModel)
            return;

        if (ReferenceEquals(_sessionReorderClickToSuppress, button))
        {
            _sessionReorderClickToSuppress = null;
            e.Handled                      = true;
            return;
        }

        viewModel.SelectSessionCommand.Execute(session);
    }

    /// <summary>
    ///     <para>只接受当前同一分类内可重排的空闲会话。</para>
    ///     <para>Accepts only idle sessions that can be reordered within their current category.</para>
    /// </summary>
    private void OnSessionRowDragOver(object? sender, DragEventArgs e)
    {
        try
        {
            e.DragEffects = sender is Grid targetRow                         &&
                            targetRow.DataContext is SessionViewModel target &&
                            DataContext is MainWindowViewModel viewModel     &&
                            TryGetSessionMove(e, target, viewModel, out _)
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
    ///     <para>按指针位于会话行中线的前后，将会话插入目标之前或之后。</para>
    ///     <para>Inserts the session before or after the target based on the pointer's position relative to its midpoint.</para>
    /// </summary>
    private void OnSessionRowDrop(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        e.Handled     = true;
        try
        {
            if (sender is not Grid targetRow                             ||
                targetRow.DataContext is not SessionViewModel target     ||
                DataContext is not MainWindowViewModel viewModel         ||
                !TryGetSessionMove(e, target, viewModel, out var source) || source is null)
                return;

            var pointerPosition = e.GetPosition(targetRow);
            var insertAfter     = pointerPosition.Y >= targetRow.Bounds.Height / 2;
            viewModel.MoveSession(source, target, insertAfter);
            e.DragEffects = DragDropEffects.Move;
        }
        catch (Exception exception)
        {
            if (DataContext is MainWindowViewModel viewModel) viewModel.Notice = exception.Message;
        }
    }

    /// <summary>
    ///     <para>验证拖放数据和会话在当前侧栏分类中的可重排性。</para>
    ///     <para>Validates drag data and session reorderability within the current sidebar category.</para>
    /// </summary>
    private static bool TryGetSessionMove(DragEventArgs         e,
                                          SessionViewModel      target,
                                          MainWindowViewModel   viewModel,
                                          out SessionViewModel? source)
    {
        source = e.DataTransfer.TryGetValue(SessionReorderDataFormat);
        return source is not null && viewModel.CanMoveSession(source, target);
    }

    /// <summary>
    ///     <para>从项目操作菜单切换置顶状态，并关闭菜单。</para>
    ///     <para>Toggles the pin state from the project actions menu and closes the menu.</para>
    /// </summary>
    private void OnToggleProjectPinMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ProjectViewModel project } ||
            DataContext is not MainWindowViewModel viewModel)
            return;

        project.IsActionsMenuOpen = false;
        viewModel.ToggleProjectPinCommand.Execute(project);
    }

    /// <summary>
    ///     <para>从项目操作菜单打开重命名弹窗，并关闭菜单。</para>
    ///     <para>Opens the rename dialog from the project actions menu and closes the menu.</para>
    /// </summary>
    private void OnRenameProjectMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ProjectViewModel project } ||
            DataContext is not MainWindowViewModel viewModel)
            return;

        project.IsActionsMenuOpen = false;
        viewModel.OpenProjectRenameCommand.Execute(project);
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

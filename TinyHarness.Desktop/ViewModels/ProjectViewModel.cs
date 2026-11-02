using System.Collections.ObjectModel;
using TinyHarness.Core.Models.Project;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>项目侧栏投影；无项目归属来自日志，缺失项目与整理未完成分组仅用于展示。</para>
///     <para>
///         Sidebar projection; no-project ownership comes from the journal, while missing-project and unfinished-organize
///         groups are display-only.
///     </para>
/// </summary>
public sealed class ProjectViewModel : ObservableObject
{
    internal const int SessionPageSize = 5;
    private bool _canReorder;

    private string _editedName;
    private bool _expanded = true;
    private bool _isActionsMenuOpen;
    private bool _isPinned;

    public ProjectViewModel(string key, Project? project, string name, string root,
                            Func<ProjectViewModel, Task> create,
                            Action<Exception> error)
    {
        Key = key;
        Project = project;
        Name = name;
        Root = root;
        _editedName = name;
        MoreSessionsRow = new ProjectSessionsMoreViewModel(this);
        NewSessionCommand = new AsyncCommand(() => create(this), error);
    }

    public string Key { get; }
    public bool IsNoProjectGroup => Key.StartsWith("no-project:", StringComparison.Ordinal);
    public Project? Project { get; private set; }
    public string Name { get; private set; }
    public string Root { get; }
    /// <summary>
    ///     <para>该侧栏分组所属的会话存储目录。</para>
    ///     <para>The session-store directory that owns this sidebar group.</para>
    /// </summary>
    internal string? StoreDirectory { get; set; }

    public bool CanEdit => Project is not null;
    public ObservableCollection<SessionViewModel> Sessions { get; } = [];
    public AsyncCommand NewSessionCommand { get; }
    internal int VisibleSessionCount { get; set; } = SessionPageSize;
    internal ProjectSessionsMoreViewModel MoreSessionsRow { get; }

    /// <summary>纯展示组的附加说明（如整理未完成原因）；真实项目组不使用。</summary>
    public string? Detail { get; private set; }

    /// <summary>组头提示：有附加说明时优先展示，否则显示目录路径；草稿组无目录时为空。</summary>
    public string? HeaderTip => Detail ?? (Root.Length > 0 ? Root : null);

    public string EditedName
    {
        get => _editedName;
        set => SetProperty(ref _editedName, value);
    }

    /// <summary>
    ///     <para>项目操作菜单是否展开。</para>
    ///     <para>Whether the project actions menu is open.</para>
    /// </summary>
    public bool IsActionsMenuOpen
    {
        get => _isActionsMenuOpen;
        set => SetProperty(ref _isActionsMenuOpen, value);
    }

    public bool IsExpanded
    {
        get => _expanded;
        set => SetProperty(ref _expanded, value);
    }

    /// <summary>
    ///     <para>项目是否处于置顶分类；展示组不能置顶。</para>
    ///     <para>Whether the project belongs to the pinned category; display-only groups cannot be pinned.</para>
    /// </summary>
    public bool IsPinned
    {
        get => _isPinned;
        private set
        {
            if (!SetProperty(ref _isPinned, value)) return;
            OnPropertyChanged(nameof(PinActionName));
        }
    }

    /// <summary>
    ///     <para>当前是否允许通过鼠标拖动调整项目顺序。</para>
    ///     <para>Whether mouse dragging can reorder this project in the current sidebar mode.</para>
    /// </summary>
    public bool CanReorder
    {
        get => _canReorder;
        private set => SetProperty(ref _canReorder, value);
    }

    /// <summary>
    ///     <para>项目置顶按钮当前执行的操作名称。</para>
    ///     <para>The current action name for the project pin button.</para>
    /// </summary>
    public string PinActionName => IsPinned ? "取消置顶项目" : "置顶项目";

    /// <summary>
    ///     <para>该组是否包含当前选中且未置顶的会话；置顶会话显示在全局分类中。</para>
    ///     <para>
    ///         Whether this group contains the selected unpinned session; pinned sessions appear in the global category.
    ///     </para>
    /// </summary>
    public bool IsCurrent => Sessions.Any(session => session.IsSelected && !session.IsPinned);

    /// <summary>
    ///     <para>刷新侧栏置顶和拖动状态，不改变项目归属或展开状态。</para>
    ///     <para>Refreshes pinned and drag state without changing project ownership or expansion.</para>
    /// </summary>
    internal void SetSidebarState(bool isPinned, bool canReorder)
    {
        IsPinned = isPinned && CanEdit;
        CanReorder = canReorder && CanEdit;
    }

    /// <summary>
    ///     <para>选中变化或成员列表重建后统一刷新 IsCurrent 通知；高亮本身按成员实时求值。</para>
    ///     <para>Refreshes the IsCurrent notification after selection or membership changes; the highlight is evaluated live.</para>
    /// </summary>
    public void NotifyCurrentChanged()
    {
        OnPropertyChanged(nameof(IsCurrent));
    }

    /// <summary>
    ///     <para>刷新可靠保存的项目定义，保留分组展开状态。</para>
    ///     <para>Refreshes a reliably saved project definition while preserving expansion state.</para>
    /// </summary>
    public void Update(Project project)
    {
        if (Project == project) return;
        Project = project;
        Name = project.Name;
        EditedName = project.Name;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(CanEdit));
    }

    /// <summary>
    ///     <para>刷新纯展示组的名称与说明；真实项目组仍以 <see cref="Update" /> 保存的定义为准。</para>
    ///     <para>
    ///         Refreshes a display-only group's name and detail; real project groups keep following the saved
    ///         definition via <see cref="Update" />.
    ///     </para>
    /// </summary>
    public void RefreshDisplay(string name, string? detail)
    {
        if (Project is not null) return;
        if (Name != name)
        {
            Name = name;
            EditedName = name;
            OnPropertyChanged(nameof(Name));
        }

        if (Detail == detail) return;
        Detail = detail;
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(HeaderTip));
    }
}

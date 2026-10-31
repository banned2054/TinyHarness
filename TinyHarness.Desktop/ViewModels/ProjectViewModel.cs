using System.Collections.ObjectModel;
using TinyHarness.Core.Models.Project;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>项目侧栏投影；草稿、缺失项目与整理未完成分组仅用于展示，不推断持久化归属。</para>
///     <para>
///         Sidebar projection; draft, missing-project and unfinished-organize groups are display-only and never infer
///         persisted ownership.
///     </para>
/// </summary>
public sealed class ProjectViewModel : ObservableObject
{
    internal const int SessionPageSize = 5;

    private string _editedName;
    private bool   _expanded = true;

    public ProjectViewModel(string                       key, Project? project, string name, string root,
                            Func<ProjectViewModel, Task> create,
                            Action<Exception>            error)
    {
        Key               = key;
        Project           = project;
        Name              = name;
        Root              = root;
        _editedName       = name;
        MoreSessionsRow   = new ProjectSessionsMoreViewModel(this);
        NewSessionCommand = new AsyncCommand(() => create(this), error);
    }

    public   string                                 Key                 { get; }
    public   Project?                               Project             { get; private set; }
    public   string                                 Name                { get; private set; }
    public   string                                 Root                { get; }
    public   bool                                   CanEdit             => Project is not null;
    public   ObservableCollection<SessionViewModel> Sessions            { get; } = [];
    public   AsyncCommand                           NewSessionCommand   { get; }
    internal int                                    VisibleSessionCount { get; set; } = SessionPageSize;
    internal ProjectSessionsMoreViewModel           MoreSessionsRow     { get; }

    /// <summary>纯展示组的附加说明（如整理未完成原因）；真实项目组不使用。</summary>
    public string? Detail { get; private set; }

    /// <summary>组头提示：有附加说明时优先展示，否则显示目录路径；草稿组无目录时为空。</summary>
    public string? HeaderTip => Detail ?? (Root.Length > 0 ? Root : null);

    public string EditedName
    {
        get => _editedName;
        set => SetProperty(ref _editedName, value);
    }

    public bool IsExpanded
    {
        get => _expanded;
        set => SetProperty(ref _expanded, value);
    }

    /// <summary>该组是否包含当前选中的会话；驱动组头行的选中高亮。</summary>
    public bool IsCurrent => Sessions.Any(session => session.IsSelected);

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
        Project    = project;
        Name       = project.Name;
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
            Name       = name;
            EditedName = name;
            OnPropertyChanged(nameof(Name));
        }

        if (Detail == detail) return;
        Detail = detail;
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(HeaderTip));
    }
}

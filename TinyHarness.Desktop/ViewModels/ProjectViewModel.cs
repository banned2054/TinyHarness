using System.Collections.ObjectModel;
using TinyHarness.Core.Models.Project;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>项目侧栏投影；缺失项目和待初始化分组仅用于展示，不推断持久化归属。</para>
///     <para>Sidebar projection; missing-project and pending groups are display-only and never infer persisted ownership.</para>
/// </summary>
public sealed class ProjectViewModel : ObservableObject
{
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
        NewSessionCommand = new AsyncCommand(() => create(this), error);
    }

    public string                                 Key               { get; }
    public Project?                               Project           { get; private set; }
    public string                                 Name              { get; private set; }
    public string                                 Root              { get; }
    public bool                                   CanEdit           => Project is not null;
    public ObservableCollection<SessionViewModel> Sessions          { get; } = [];
    public AsyncCommand                           NewSessionCommand { get; }

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
}

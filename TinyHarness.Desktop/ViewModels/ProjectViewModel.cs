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

    public ProjectViewModel(string                       key,    Project? project, string name, string root,
                            Func<ProjectViewModel, Task> create, Func<ProjectViewModel, Task> rename,
                            Action<Exception>            error)
    {
        Key               = key;
        Project           = project;
        Name              = name;
        Root              = root;
        _editedName       = name;
        NewSessionCommand = new AsyncCommand(() => create(this), error);
        RenameCommand     = new AsyncCommand(() => rename(this), error);
    }

    public string                                 Key               { get; }
    public Project?                               Project           { get; private set; }
    public string                                 Name              { get; private set; }
    public string                                 Root              { get; }
    public bool                                   CanEdit           => Project is not null;
    public ObservableCollection<SessionViewModel> Sessions          { get; } = [];
    public AsyncCommand                           NewSessionCommand { get; }
    public AsyncCommand                           RenameCommand     { get; }

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

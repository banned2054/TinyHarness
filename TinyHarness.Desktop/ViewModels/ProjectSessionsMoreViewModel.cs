namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>项目会话列表底部的展开入口；随项目复用，保持侧栏行身份稳定。</para>
///     <para>Show-more entry below a project's sessions; reused with the project to keep the sidebar row stable.</para>
/// </summary>
public sealed class ProjectSessionsMoreViewModel(ProjectViewModel project)
{
    public ProjectViewModel Project { get; } = project;
}

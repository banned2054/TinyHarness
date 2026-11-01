namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>侧栏中的固定分类标题，仅用于划分置顶与普通条目。</para>
///     <para>A fixed sidebar category heading used to separate pinned and regular entries.</para>
/// </summary>
public sealed class SidebarCategoryViewModel(string title)
{
    public string Title { get; } = title;
}

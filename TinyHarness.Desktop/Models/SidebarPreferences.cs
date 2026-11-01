namespace TinyHarness.Desktop.Models;

/// <summary>
///     <para>桌面侧栏偏好设置，保存视图模式以及各会话存储目录的排序和置顶状态。</para>
///     <para>Desktop sidebar preferences for view mode and per-session-store ordering and pin state.</para>
/// </summary>
internal sealed record SidebarPreferences
{
    /// <summary>
    ///     <para>侧栏偏好设置的 schema 版本。</para>
    ///     <para>The schema version of the sidebar preferences.</para>
    /// </summary>
    public string Schema { get; init; } = "tinyharness.desktop-sidebar/1";

    /// <summary>
    ///     <para>是否按项目显示会话；false 表示纯会话列表。</para>
    ///     <para>Whether sessions are grouped by project; false selects the flat session list.</para>
    /// </summary>
    public bool IsProjectMode { get; init; } = true;

    /// <summary>
    ///     <para>是否按最近活动时间排列项目。</para>
    ///     <para>Whether projects are ordered by recent activity.</para>
    /// </summary>
    public bool SortProjectsByActivity { get; init; } = false;

    /// <summary>
    ///     <para>按会话存储目录区分的侧栏状态。</para>
    ///     <para>Sidebar state keyed by session-store directory.</para>
    /// </summary>
    public IReadOnlyList<SidebarStorePreferences> Stores { get; init; } = [];
}

/// <summary>
///     <para>单个会话存储目录对应的项目顺序和置顶会话。</para>
///     <para>Project ordering and pinned sessions for one session-store directory.</para>
/// </summary>
internal sealed record SidebarStorePreferences
{
    /// <summary>
    ///     <para>此偏好设置所对应的会话存储目录绝对路径。</para>
    ///     <para>The absolute session-store directory path for these preferences.</para>
    /// </summary>
    public string StoreDirectory { get; init; } = "";

    /// <summary>
    ///     <para>普通项目的手动顺序。</para>
    ///     <para>The manual order of regular projects.</para>
    /// </summary>
    public IReadOnlyList<string> ManualProjectOrder { get; init; } = [];

    /// <summary>
    ///     <para>置顶项目的手动顺序。</para>
    ///     <para>The manual order of pinned projects.</para>
    /// </summary>
    public IReadOnlyList<string> PinnedProjectOrder { get; init; } = [];

    /// <summary>
    ///     <para>置顶会话的 ID 列表。</para>
    ///     <para>The IDs of pinned sessions.</para>
    /// </summary>
    public IReadOnlyList<string> PinnedSessionIds { get; init; } = [];
}

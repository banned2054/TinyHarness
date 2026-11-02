using System.Text.Json.Serialization;

namespace TinyHarness.Desktop.Models;

/// <summary>
///     <para>桌面侧栏偏好设置，保存视图模式、分类状态和普通项目顺序；置顶状态由 Core 持久化。</para>
///     <para>Desktop sidebar preferences for view mode, category state and regular project order; Core persists pin state.</para>
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
    ///     <para>已折叠的固定侧栏分类键；未列出的分类默认展开。</para>
    ///     <para>Collapsed fixed sidebar category keys; categories not listed are expanded by default.</para>
    /// </summary>
    public IReadOnlyList<string> CollapsedCategoryKeys { get; init; } = [];

    /// <summary>
    ///     <para>按会话存储目录区分的侧栏状态。</para>
    ///     <para>Sidebar state keyed by session-store directory.</para>
    /// </summary>
    public IReadOnlyList<SidebarStorePreferences> Stores { get; init; } = [];
}

/// <summary>
///     <para>单个会话存储目录对应的排序与分组展开状态，以及供迁移使用的旧置顶值。</para>
///     <para>Ordering and group expansion state for one session store, plus legacy pin values used during migration.</para>
/// </summary>
internal sealed record SidebarStorePreferences
{
    /// <summary>
    ///     <para>此偏好设置所对应的会话存储目录绝对路径。</para>
    ///     <para>The absolute session-store directory path for these preferences.</para>
    /// </summary>
    public string StoreDirectory { get; init; } = "";

    /// <summary>
    ///     <para>项目在普通分类中的手动顺序；置顶期间保留原位置，供取消置顶后恢复。</para>
    ///     <para>
    ///         The manual order of projects in the regular category; pinned projects retain their position
    ///         for restoration when unpinned.
    ///     </para>
    /// </summary>
    public IReadOnlyList<string> ManualProjectOrder { get; init; } = [];

    /// <summary>
    ///     <para>此会话存储目录中的手动会话顺序；运行中的会话仍按最近活动排列。</para>
    ///     <para>The manual session order for this store; running sessions remain ordered by recent activity.</para>
    /// </summary>
    public IReadOnlyList<string> ManualSessionOrder { get; init; } = [];

    /// <summary>
    ///     <para>已折叠的项目或展示组键；未列出的组默认展开。</para>
    ///     <para>Collapsed project or display-group keys; groups not listed are expanded by default.</para>
    /// </summary>
    public IReadOnlyList<string> CollapsedGroupKeys { get; init; } = [];

    /// <summary>
    ///     <para>是否已将旧版追加到末尾的最新项目顺序迁移到顶部。</para>
    ///     <para>Whether the latest project appended by an older version has been migrated to the top.</para>
    /// </summary>
    public bool ManualProjectOrderMigrationCompleted { get; init; }

    /// <summary>
    ///     <para>旧版 Desktop 偏好中的置顶项目顺序；启动时迁入 Core 后清空。</para>
    ///     <para>The pinned project order from legacy Desktop preferences; cleared after migration to Core.</para>
    /// </summary>
    [JsonPropertyName("pinnedProjectOrder")]
    public IReadOnlyList<string> LegacyPinnedProjectOrder { get; init; } = [];

    /// <summary>
    ///     <para>旧版 Desktop 偏好中的置顶会话 ID；启动时迁入 Core 后清空。</para>
    ///     <para>The pinned session IDs from legacy Desktop preferences; cleared after migration to Core.</para>
    /// </summary>
    [JsonPropertyName("pinnedSessionIds")]
    public IReadOnlyList<string> LegacyPinnedSessionIds { get; init; } = [];
}

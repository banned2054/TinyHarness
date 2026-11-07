using TinyHarness.Core.Models.Permissions;

namespace TinyHarness.Core.Models.Persistence;

/// <summary>
///     <para>索引条目的建立状态：待建立、与来源一致、来源已变化或建立失败。</para>
///     <para>
///         An index entry's build state: pending, consistent with its source, source changed, or build failed.
///     </para>
/// </summary>
public enum SessionIndexEntryState
{
    Pending,
    Current,
    Stale,
    Failed
}

/// <summary>
///     <para>
///         会话列表摘要：只承载列表与会话行实际消费的轻量元数据，不携带对话正文、运行事实或可执行授权。
///     </para>
///     <para>
///         A session-list summary carrying only the lightweight metadata the list and session rows consume, without
///         conversation content, run facts, or executable authorization.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         权威来源仍是会话 JSONL；摘要只是可重建投影，<see cref="CommitSequence" /> 与 <see cref="IndexedBytes" />
///         必须对应同一来源快照中最后完整有效批次。标题来自首回合用户输入首行，可能包含用户内容。
///     </para>
///     <para>
///         The session JSONL stays authoritative; the summary is a rebuildable projection whose
///         <see cref="CommitSequence" /> and <see cref="IndexedBytes" /> must match the last complete valid batch of
///         one source snapshot. The title comes from the first turn's first input line and may contain user content.
///     </para>
/// </remarks>
public sealed record SessionSummary
{
    public string Id { get; init; } = string.Empty;

    public string WorkspaceRoot { get; init; } = string.Empty;

    /// <summary>
    ///     <para>日志 project-binding 记录的项目归属；未绑定的旧日志为 null，不支持转移。</para>
    ///     <para>Project ownership from the log's project-binding record; null for unbound legacy logs and never transferred.</para>
    /// </summary>
    public string? ProjectId { get; init; }

    public SessionProjectAssignmentState ProjectAssignmentState { get; init; }
        = SessionProjectAssignmentState.PendingInitialization;

    /// <summary>
    ///     <para>侧栏标题：首回合输入首行（最多 48 字符）；尚无回合时为会话 ID 前 8 位。</para>
    ///     <para>
    ///         Sidebar title: the first line of the first turn's input (48 characters at most); the first 8 id
    ///         characters until a turn exists.
    ///     </para>
    /// </summary>
    public string Title { get; init; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>
    ///     <para>最后一个已索引批次的提交时间；纯归属维护批次同样推进。</para>
    ///     <para>The commit time of the last indexed batch; ownership-only batches advance it too.</para>
    /// </summary>
    public DateTimeOffset UpdatedUtc { get; init; }

    /// <summary>
    ///     <para>最近一次对话活动时间；纯归属维护批次不推进，排序与归档筛选依赖它。</para>
    ///     <para>
    ///         The latest conversation-activity time; ownership-only batches do not advance it, and ordering and
    ///         archive filtering depend on it.
    ///     </para>
    /// </summary>
    public DateTimeOffset LastActivityUtc { get; init; }

    public int TurnCount { get; init; }

    /// <summary>
    ///     <para>日志中最近一次权限模式选择；旧日志未记录时为 null。</para>
    ///     <para>The latest journaled permission mode; null for legacy logs without a selection.</para>
    /// </summary>
    public string? PermissionModeName { get; init; }

    /// <summary>
    ///     <para>已索引的最后完整有效批次序号；半行不算完成。</para>
    ///     <para>The last complete valid indexed batch sequence; a partial line never counts.</para>
    /// </summary>
    public long CommitSequence { get; init; }

    /// <summary>
    ///     <para>已索引的日志字节数；必须等于该序号前的完整字节前缀。</para>
    ///     <para>The indexed log byte count; it must equal the complete byte prefix through that sequence.</para>
    /// </summary>
    public long IndexedBytes { get; init; }
}

/// <summary>
///     <para>索引行状态：摘要或失败标记加上来源标记、归档投影与错误说明，供后台对账比较。</para>
///     <para>
///         An index row state: the summary or failure marker plus source markers, the archived projection, and an
///         error note for background reconciliation.
///     </para>
/// </summary>
public sealed record SessionIndexRow
{
    public string Id { get; init; } = string.Empty;

    public SessionIndexEntryState State { get; init; }

    public SessionSummary? Summary { get; init; }

    /// <summary>
    ///     <para>建立摘要时观察到的日志长度；只是失效线索，不是完整性证明。</para>
    ///     <para>The log length observed when the summary was built; a staleness hint, never integrity proof.</para>
    /// </summary>
    public long SourceLength { get; init; }

    /// <summary>
    ///     <para>建立摘要时观察到的日志修改时间；同样只是失效线索。</para>
    ///     <para>The log modification time observed when the summary was built; equally just a staleness hint.</para>
    /// </summary>
    public DateTimeOffset? SourceModifiedUtc { get; init; }

    /// <summary>
    ///     <para>归档投影：true/false 来自归档来源，null 表示来源不可读或尚未建立，不以 false 冒充。</para>
    ///     <para>
    ///         The archived projection: true/false come from the archive source, null means unreadable or not built
    ///         yet, never faked as false.
    ///     </para>
    /// </summary>
    public bool? Archived { get; init; }

    /// <summary>
    ///     <para>建立失败时不含凭据的原因；条目健康时为 null。</para>
    ///     <para>The credential-free failure reason; null while the entry is healthy.</para>
    /// </summary>
    public string? Error { get; init; }
}

/// <summary>
///     <para>摘要查询：排序固定为活动时间降序加 ID 次键，归档与项目可选过滤，按游标有界返回。</para>
///     <para>
///         A summary query: fixed ordering by activity time descending with an id tie-break, optional archive and
///         project filters, bounded return behind a cursor.
///     </para>
/// </summary>
public sealed record SessionSummaryQuery
{
    /// <summary>
    ///     <para>本页最大条数；必须为正。</para>
    ///     <para>The page's maximum item count; strictly positive.</para>
    /// </summary>
    public int Limit { get; init; } = 200;

    /// <summary>
    ///     <para>续页游标的活动时间部分；null 从头开始。</para>
    ///     <para>The continuation cursor's activity-time part; null starts from the head.</para>
    /// </summary>
    public long? AfterLastActivityUtcMs { get; init; }

    /// <summary>
    ///     <para>续页游标的 ID 部分；与活动时间一起构成稳定次键。</para>
    ///     <para>The continuation cursor's id part; together with the activity time it forms the stable tie-break.</para>
    /// </summary>
    public string? AfterId { get; init; }

    /// <summary>
    ///     <para>按项目过滤；null 表示不过滤。</para>
    ///     <para>The project filter; null disables it.</para>
    /// </summary>
    public string? ProjectId { get; init; }

    /// <summary>
    ///     <para>按归档投影过滤；null 表示不过滤，false 同时包含未知状态。</para>
    ///     <para>
    ///         The archive-projection filter; null disables it, and false includes unknown states so unknown entries
    ///         are not hidden from the default view.
    ///     </para>
    /// </summary>
    public bool? Archived { get; init; }
}

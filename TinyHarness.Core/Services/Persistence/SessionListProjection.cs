using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Persistence;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>一条降级摘要扫描的条目：读取成功的摘要或该日志的失败原因，二者必有其一。</para>
///     <para>One degraded summary-scan entry: a successfully read summary or the log's failure reason, exactly one.</para>
/// </summary>
/// <param name="Id">
///     <para>会话 ID（小写十六进制）。</para>
///     <para>The session id (lowercase hexadecimal).</para>
/// </param>
/// <param name="Summary">
///     <para>读取成功时按最后一个完整有效批次构建的摘要；失败时为 null。</para>
///     <para>
///         The summary built through the last complete valid batch on success; null when the log failed to read.
///     </para>
/// </param>
/// <param name="Error">
///     <para>失败时不含凭据的原因；成功时为 null。</para>
///     <para>The credential-free failure reason; null on success.</para>
/// </param>
public sealed record SessionListScanEntry(string Id, SessionSummary? Summary, string? Error);

/// <summary>
///     <para>
///         单 store 会话列表投影的读取门面：索引可用时按游标分页查询摘要，索引缺失或不可用时直接做
///         不落库的摘要扫描降级，并把后台对账与跨进程监测入口收口在同一处，供 CLI 与 Desktop 共用。
///     </para>
///     <para>
///         The read façade for one store's session-list projection: paged summary queries while the index is
///         available, a degraded non-persisted summary scan when it is missing or unusable, with background
///         reconciliation and cross-process monitoring behind the same entry point for CLI and Desktop.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         本门面只读或触发可重建的索引维护，不写会话 JSONL、目录册或归档状态；权威数据边界与
///         <see cref="SessionIndexStore" /> 一致。降级扫描不经数据库，也不创建索引文件，只按严格摘要
///         扫描逐日志构建内存摘要，使索引不可用时列表仍能以轻量元数据呈现。
///     </para>
///     <para>
///         The façade only reads or triggers rebuildable index maintenance; it never writes the session JSONL,
///         catalog, or archive state, keeping the authoritative-data boundary of
///         <see cref="SessionIndexStore" />. The degraded scan bypasses the database without creating an index
///         file, building in-memory summaries per log through the strict summary scan so the list still renders
///         lightweight metadata while the index is unusable.
///     </para>
/// </remarks>
public sealed class SessionListProjection
{
    private readonly SessionIndexReconciler _reconciler;
    private readonly SessionIndexStore      _store;

    /// <summary>
    ///     <para>绑定一个受保护 session store 目录；目录须为绝对路径，不立即访问文件系统。</para>
    ///     <para>
    ///         Binds one protected session-store directory; it must be absolute, and no file system access happens
    ///         yet.
    ///     </para>
    /// </summary>
    public SessionListProjection(string storeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        _store         = new SessionIndexStore(storeDirectory);
        _reconciler    = new SessionIndexReconciler(_store);
        StoreDirectory = storeDirectory;
    }

    /// <summary>
    ///     <para>绑定的 session store 目录。</para>
    ///     <para>The bound session-store directory.</para>
    /// </summary>
    public string StoreDirectory { get; }

    /// <summary>
    ///     <para>检查索引可用性：缺失、缺 schema、版本不受支持、损坏或其他失败分别报告。</para>
    ///     <para>
    ///         Checks index availability: missing, schema-less, unsupported version, damaged, and other failures are
    ///         reported separately.
    ///     </para>
    /// </summary>
    public Task<SessionIndexAvailability> GetAvailabilityAsync(CancellationToken cancellationToken) =>
        _store.GetAvailabilityAsync(cancellationToken);

    /// <summary>
    ///     <para>
    ///         按查询取回全部健康摘要：内部按页游标推进直到取完，排序固定为活动时间降序加 ID 次键。
    ///     </para>
    ///     <para>
    ///         Fetches every healthy summary matching the query by advancing page cursors to the end; ordering is
    ///         fixed to activity time descending with the id tie-break.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>失败、过期或待建立条目不返回，由 <see cref="ListRowsAsync" /> 与对账负责。</para>
    ///     <para>
    ///         Failed, stale, and pending entries are not returned; <see cref="ListRowsAsync" /> and reconciliation own
    ///         them.
    ///     </para>
    /// </remarks>
    public async Task<IReadOnlyList<SessionSummary>> ListSummariesAsync(
        SessionSummaryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var summaries = new List<SessionSummary>();
        var cursor    = query with { };
        while (true)
        {
            var page = await _store.ListSummariesAsync(cursor, cancellationToken).ConfigureAwait(false);
            summaries.AddRange(page);
            if (page.Count < cursor.Limit) return summaries;
            var last = page[^1];
            cursor = cursor with
            {
                AfterLastActivityUtcMs = last.LastActivityUtc.ToUnixTimeMilliseconds(),
                AfterId = last.Id
            };
        }
    }

    /// <summary>
    ///     <para>返回全部索引行状态，供失败条目展示与归属整理挑出待处理集合。</para>
    ///     <para>
    ///         Returns every index row for failed-entry display and for organize to pick its pending set.
    ///     </para>
    /// </summary>
    public Task<IReadOnlyList<SessionIndexRow>> ListRowsAsync(CancellationToken cancellationToken) =>
        _store.ListRowsAsync(cancellationToken);

    /// <summary>
    ///     <para>
    ///         降级路径：不经数据库逐日志做严格摘要扫描，返回成功摘要与失败条目；不创建或改写索引文件。
    ///     </para>
    ///     <para>
    ///         The degraded path: a strict per-log summary scan without the database, returning summaries and
    ///         failures; it never creates or rewrites index files.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         扫描拒绝范围与索引重建一致；来源被占用等原因按条目失败报告，不中断其余条目，
    ///         结果按活动时间降序排列。
    ///     </para>
    ///     <para>
    ///         The scan rejects exactly what index rebuilds reject; occupied or unreadable sources fail their own
    ///         entry without interrupting the rest, and results sort by activity time descending.
    ///     </para>
    /// </remarks>
    public async Task<IReadOnlyList<SessionListScanEntry>> ScanSummariesAsync(CancellationToken cancellationToken)
    {
        var entries = new List<SessionListScanEntry>();
        if (!Directory.Exists(StoreDirectory)) return entries;
        foreach (var path in Directory.EnumerateFiles(StoreDirectory, "*" + SessionStoreWriter.LogFileSuffix))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionId = Path.GetFileName(path)[..^SessionStoreWriter.LogFileSuffix.Length];
            try
            {
                var build = await SessionSummaryBuilder
                                 .BuildAsync(StoreDirectory, sessionId, cancellationToken).ConfigureAwait(false);
                entries.Add(build is null
                                ? new SessionListScanEntry(sessionId, null,
                                                           $"Session log '{sessionId}' disappeared before it could be read.")
                                : new SessionListScanEntry(sessionId, build.Summary, null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is SessionStoreException or SessionIndexException or IOException
                                          or UnauthorizedAccessException or NotSupportedException
                                          or PathTooLongException)
            {
                entries.Add(new SessionListScanEntry(sessionId, null, ex.Message));
            }
        }

        return entries.OrderByDescending(entry => entry.Summary?.LastActivityUtc ?? DateTimeOffset.MinValue)
                      .ToArray();
    }

    /// <summary>
    ///     <para>执行一轮索引对账；选项可携带逐条提交回调供首次建库渐进展示。</para>
    ///     <para>
    ///         Runs one reconciliation round; options may carry a per-commit callback for progressive first builds.
    ///     </para>
    /// </summary>
    public Task<SessionIndexReconcileReport> ReconcileAsync(
        SessionIndexReconcileOptions options, CancellationToken cancellationToken) =>
        _reconciler.ReconcileAsync(options, cancellationToken);

    /// <summary>
    ///     <para>
    ///         打开一条常驻 data_version 监测连接；调用方负责释放，语义与
    ///         <see cref="SessionIndexStore.OpenVersionWatcherAsync" /> 一致。
    ///     </para>
    ///     <para>
    ///         Opens a standing data_version monitor connection; the caller releases it, with the same semantics as
    ///         <see cref="SessionIndexStore.OpenVersionWatcherAsync" />.
    ///     </para>
    /// </summary>
    public Task<SessionIndexVersionWatcher> OpenVersionWatcherAsync(CancellationToken cancellationToken) =>
        _store.OpenVersionWatcherAsync(cancellationToken);
}

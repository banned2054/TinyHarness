using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Persistence;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>一轮对账的计数与说明；说明不含凭据且条数有界。</para>
///     <para>One reconciliation round's counts and notes; notes are credential-free and bounded in number.</para>
/// </summary>
/// <param name="Availability">
///     <para>本轮开始或初始化后的索引可用性；不可用时其余计数为零。</para>
///     <para>The index availability at round start or after initialization; other counts stay zero when unusable.</para>
/// </param>
/// <param name="SourceCount">
///     <para>枚举到的有效会话日志数量。</para>
///     <para>The number of valid session logs enumerated.</para>
/// </param>
/// <param name="Unchanged">
///     <para>来源标记与索引一致而跳过正文扫描的条目数。</para>
///     <para>Entries skipped without body scans because their source markers matched the index.</para>
/// </param>
/// <param name="Rebuilt">
///     <para>本轮重新扫描并写入摘要的条目数。</para>
///     <para>Entries rescanned and rewritten with fresh summaries this round.</para>
/// </param>
/// <param name="Removed">
///     <para>因来源日志消失而移除的条目数。</para>
///     <para>Entries removed because their source logs disappeared.</para>
/// </param>
/// <param name="MarkedFailed">
///     <para>本轮写入失败标记的次数，含来源疑似替换时的重建前预标记。</para>
///     <para>
///         Failure marks written this round, including the pre-rebuild mark taken when a source looks replaced.
///     </para>
/// </param>
/// <param name="ArchiveSynced">
///     <para>归档投影是否已从可读的归档来源同步；来源不可读时为 false 且投影保持不变。</para>
///     <para>
///         Whether the archived projection synced from a readable archive source; false leaves the projection
///         untouched when the source is unreadable.
///     </para>
/// </param>
/// <param name="Notes">
///     <para>本轮跳过或失败的说明列表，数量有界。</para>
///     <para>The bounded note list for skips and failures this round.</para>
/// </param>
public sealed record SessionIndexReconcileReport(
    SessionIndexAvailability Availability,
    int                      SourceCount,
    int                      Unchanged,
    int                      Rebuilt,
    int                      Removed,
    int                      MarkedFailed,
    bool                     ArchiveSynced,
    IReadOnlyList<string>    Notes);

/// <summary>
///     <para>一轮对账的可选项；全部为 null 时与无参重载行为一致。</para>
///     <para>Optional knobs for one reconciliation round; all null matches the parameterless overload.</para>
/// </summary>
public sealed record SessionIndexReconcileOptions
{
    /// <summary>
    ///     <para>默认选项实例：无回调。</para>
    ///     <para>The default options instance without callbacks.</para>
    /// </summary>
    public static readonly SessionIndexReconcileOptions Default = new();

    /// <summary>
    ///     <para>
    ///         每份日志重建并条件提交后立即以新摘要回调一次，供首次建库分批渐进上屏；失败条目不回调。
    ///     </para>
    ///     <para>
    ///         Invoked with each freshly committed summary right after its conditional commit so first builds can
    ///         surface rows progressively; failed entries never call back.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         回调在对账实例的串行门内执行，实现必须快速返回并自行调度耗时工作；非取消异常按有界说明记录，
    ///         不中断本轮其余条目，取消原样传播。
    ///     </para>
    ///     <para>
    ///         The callback runs inside the reconciler's serial gate, so implementations must return quickly and
    ///         schedule heavy work themselves; non-cancellation exceptions become bounded notes without interrupting
    ///         the remaining entries, while cancellation propagates unchanged.
    ///     </para>
    /// </remarks>
    public Func<SessionSummary, CancellationToken, Task>? SummaryCommitted { get; init; }
}

/// <summary>
///     <para>
///         对单 store 的列表索引做一轮对账：未变日志零正文扫描，缺失或变化条目整份重建，
///         变短或疑似替换的来源先标记失败再重建，孤儿行移除，归档投影从权威来源同步。
///     </para>
///     <para>
///         Runs one reconciliation round over one store's list index: unchanged logs skip body scans, missing or
///         changed entries rebuild wholly, shrunk or seemingly replaced sources are marked failed before rebuild,
///         orphan rows are removed, and the archived projection syncs from the authoritative source.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         对账只读来源日志，不修复、截断或改写原日志；每个条目独立提交，取消后已完成条目保留，可续作。
///         长度与修改时间只是失效线索：同长且同时间的替换不可检测，索引不据此宣称来源完整，
///         实际续聊仍由权威日志的严格恢复路径把关。损坏、版本不受支持或不可用的索引分别报告，
///         不被清空或降级。
///     </para>
///     <para>
///         Reconciliation only reads source logs — it never repairs, truncates, or rewrites them. Each entry
///         commits independently, so cancellation keeps finished entries and rounds resume. Length and modification
///         time are staleness hints only: a same-length, same-time replacement is undetectable, and the index never
///         claims source integrity from them — actual continuation stays guarded by the authoritative log's strict
///         recovery path. Damaged, unsupported, and unusable indexes report separately and are never cleared or
///         downgraded.
///     </para>
/// </remarks>
public sealed class SessionIndexReconciler
{
    /// <summary>
    ///     <para>单轮报告说明的条数上限，防止坏来源把报告撑到无界。</para>
    ///     <para>The per-round note cap, keeping bad sources from growing the report without bound.</para>
    /// </summary>
    private const int MaxNotes = 8;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly SessionIndexStore _indexStore;

    /// <summary>
    ///     <para>绑定一个索引 store；对账范围即该 store 目录下的会话日志。</para>
    ///     <para>
    ///         Binds one index store; reconciliation covers the session logs under that store directory.
    ///     </para>
    /// </summary>
    public SessionIndexReconciler(SessionIndexStore indexStore)
    {
        ArgumentNullException.ThrowIfNull(indexStore);
        _indexStore = indexStore;
    }

    /// <summary>
    ///     <para>执行一轮对账并返回计数报告；基础设施失败包装为 <see cref="SessionIndexException" />。</para>
    ///     <para>
    ///         Runs one reconciliation round and returns the count report; infrastructure failures surface as
    ///         <see cref="SessionIndexException" />.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>同实例并发调用串行执行；跨进程并发由索引的条件提交保证收敛。取消中断本轮并向上传播。</para>
    ///     <para>
    ///         Concurrent calls on one instance serialize; cross-process concurrency converges through the index's
    ///         conditional commits. Cancellation interrupts the round and propagates to the caller.
    ///     </para>
    /// </remarks>
    public Task<SessionIndexReconcileReport> ReconcileAsync(CancellationToken cancellationToken) =>
        ReconcileAsync(SessionIndexReconcileOptions.Default, cancellationToken);

    /// <summary>
    ///     <para>
    ///         按可选项执行一轮对账并返回计数报告；基础设施失败包装为 <see cref="SessionIndexException" />。
    ///     </para>
    ///     <para>
    ///         Runs one reconciliation round with options and returns the count report; infrastructure failures
    ///         surface as <see cref="SessionIndexException" />.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>同实例并发调用串行执行；跨进程并发由索引的条件提交保证收敛。取消中断本轮并向上传播。</para>
    ///     <para>
    ///         Concurrent calls on one instance serialize; cross-process concurrency converges through the index's
    ///         conditional commits. Cancellation interrupts the round and propagates to the caller.
    ///     </para>
    /// </remarks>
    public async Task<SessionIndexReconcileReport> ReconcileAsync(
        SessionIndexReconcileOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReconcileCoreAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                      or PathTooLongException)
        {
            throw new SessionIndexException(
                                            $"Session index reconciliation for '{_indexStore.DatabasePath}' failed: {ex.Message}",
                                            ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SessionIndexReconcileReport> ReconcileCoreAsync(
        SessionIndexReconcileOptions options, CancellationToken cancellationToken)
    {
        var notes        = new List<string>();
        var availability = await _indexStore.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        if (availability.Status is not (SessionIndexAvailabilityStatus.Available
                                     or SessionIndexAvailabilityStatus.Missing
                                     or SessionIndexAvailabilityStatus.NeedsSchema))
            return new SessionIndexReconcileReport(availability, 0, 0, 0, 0, 0, false, notes);

        if (availability.Status is not SessionIndexAvailabilityStatus.Available)
            try
            {
                await _indexStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                availability = SessionIndexAvailability.Ok;
            }
            catch (SessionIndexException ex)
            {
                return new SessionIndexReconcileReport(
                                                       new SessionIndexAvailability(
                                                            SessionIndexAvailabilityStatus
                                                               .Failed, ex.Message),
                                                       0, 0, 0, 0, 0, false, notes);
            }

        var sourceCount  = 0;
        var unchanged    = 0;
        var rebuilt      = 0;
        var removed      = 0;
        var markedFailed = 0;
        var sourceIds    = new HashSet<string>(StringComparer.Ordinal);
        var rows = (await _indexStore.ListRowsAsync(cancellationToken).ConfigureAwait(false))
           .ToDictionary(row => row.Id, StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(_indexStore.StoreDirectory,
                                                      "*" + SessionStoreWriter.LogFileSuffix))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName  = Path.GetFileName(path);
            var sessionId = fileName[..^SessionStoreWriter.LogFileSuffix.Length].ToLowerInvariant();
            if (!SessionStorePaths.IsValidSessionId(sessionId))
            {
                if (notes.Count < MaxNotes) notes.Add($"Skipped invalid session log file name '{fileName}'.");
                continue;
            }

            sourceIds.Add(sessionId);
            sourceCount++;
            try
            {
                var row    = rows.GetValueOrDefault(sessionId);
                var source = new FileInfo(path);
                if (!source.Exists)
                {
                    // 枚举与 stat 之间被删除：有行则移除，无行直接跳过。
                    if (row is not null)
                    {
                        await _indexStore.RemoveAsync(sessionId, cancellationToken).ConfigureAwait(false);
                        removed++;
                    }

                    continue;
                }

                var length   = source.Length;
                var modified = new DateTimeOffset(source.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                if (row is { State: SessionIndexEntryState.Current, SourceLength: var storedLength } &&
                    storedLength                                    == length                        &&
                    // 索引按毫秒存储修改时间；比较须用同一截断口径，否则未变日志被反复重扫。
                    // The index stores modification time in milliseconds; comparisons must use the same truncation,
                    // or unchanged logs get rescanned every round.
                    row.SourceModifiedUtc?.ToUnixTimeMilliseconds() == modified)
                {
                    unchanged++;
                    continue;
                }

                // 变短或同长但修改时间变化：来源可能被替换或截断；健康行先标记失败，使重建结果不被条件
                // 提交拒绝，坏来源保持可见。单纯增长属于正常追加，直接重建即可。
                if (row is not null &&
                    (length < row.SourceLength ||
                     (length == row.SourceLength                                              &&
                      row.SourceModifiedUtc?.ToUnixTimeMilliseconds() is { } previousModified &&
                      previousModified != modified)))
                {
                    await _indexStore.MarkFailedAsync(sessionId,
                                                      "source log shrank or was replaced; rebuilding from the current log",
                                                      cancellationToken).ConfigureAwait(false);
                    markedFailed++;
                }

                var build = await SessionSummaryBuilder
                                 .BuildAsync(_indexStore.StoreDirectory, sessionId, cancellationToken)
                                 .ConfigureAwait(false);
                if (build is null)
                {
                    await _indexStore.RemoveAsync(sessionId, cancellationToken).ConfigureAwait(false);
                    removed++;
                    continue;
                }

                await _indexStore.WriteSummaryAsync(build.Summary, build.SourceLength, build.SourceModifiedUtc,
                                                    cancellationToken).ConfigureAwait(false);
                rebuilt++;
                if (options.SummaryCommitted is { } committed)
                    try
                    {
                        await committed(build.Summary, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // 回调失败只记说明，不吞取消，也不阻断本轮其余条目。
                        if (notes.Count < MaxNotes)
                            notes.Add($"Summary callback for session '{sessionId}' failed: {ex.Message}");
                    }
            }
            catch (Exception ex) when (ex is SessionStoreException or IOException or UnauthorizedAccessException
                                          or NotSupportedException or PathTooLongException)
            {
                // 摘要扫描被严格恢复语义拒绝，或来源被占用等原因不可读：条目先标记失败并保留旧摘要，
                // 等来源恢复后由后续对账重建；其余条目与归档同步继续本轮，不中止对账。
                // The summary scan is rejected by strict recovery, or the source is unreadable (e.g. occupied):
                // the entry is marked failed with its old summary kept for later rounds to rebuild, while the
                // remaining entries and the archive sync continue this round instead of aborting it.
                try
                {
                    await _indexStore.MarkFailedAsync(sessionId, ex.Message, cancellationToken)
                                     .ConfigureAwait(false);
                    markedFailed++;
                }
                catch (SessionIndexException markEx)
                {
                    if (notes.Count < MaxNotes)
                        notes.Add($"Failed to mark session '{sessionId}' as failed: {markEx.Message}");
                }
            }
            catch (SessionIndexException ex)
            {
                if (notes.Count < MaxNotes)
                    notes.Add($"Reconciliation for session '{sessionId}' failed: {ex.Message}");
            }
        }

        foreach (var orphan in rows.Keys.Except(sourceIds, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _indexStore.RemoveAsync(orphan, cancellationToken).ConfigureAwait(false);
                removed++;
            }
            catch (SessionIndexException ex)
            {
                if (notes.Count < MaxNotes) notes.Add($"Failed to remove orphan row '{orphan}': {ex.Message}");
            }
        }

        var archiveSynced = false;
        try
        {
            var archives = await SessionStoreArchiveStore
                                .LoadAsync(_indexStore.StoreDirectory, cancellationToken)
                                .ConfigureAwait(false);
            await _indexStore
                 .SyncArchiveProjectionAsync(
                                             new HashSet<string>(archives.ArchivedSessionIds,
                                                                 StringComparer.Ordinal),
                                             cancellationToken)
                 .ConfigureAwait(false);
            archiveSynced = true;
        }
        catch (Exception ex) when (ex is SessionStoreArchiveException or SessionIndexException)
        {
            // 归档来源不可读或投影写入失败：既有投影保持不变，不以本轮结果当作权威覆盖。
            if (notes.Count < MaxNotes)
                notes.Add($"Archive projection left unchanged: {ex.Message}");
        }

        return new SessionIndexReconcileReport(availability, sourceCount, unchanged, rebuilt, removed, markedFailed,
                                               archiveSynced, notes);
    }
}

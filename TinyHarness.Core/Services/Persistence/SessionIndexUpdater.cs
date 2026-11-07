using System.Diagnostics;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Persistence;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>
///         把 writer 已可靠提交的批次增量应用到单 store 的列表索引：优先从既有健康摘要续种投影，
///         仅在条目缺失或来源不连续时回退整份扫描，使已提交日志最终收敛到索引。
///     </para>
///     <para>
///         Applies writer-committed batch increments to one store's list index: healthy summaries seed the
///         projection incrementally, and only missing or discontinuous entries fall back to a full scan, so
///         committed logs eventually converge into the index.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         更新只发布已提交事实，失败永不向上传播：writer 的提交语义不受索引故障影响，未完成的更新由
///         后台对账追平。schema 缺失时惰性初始化；版本不受支持或数据库损坏时本实例停用并停报，避免每次
///         提交空转；其余失败保留重试机会，并在 <see cref="LastFailure" /> 留下不含凭据的说明。
///     </para>
///     <para>
///         Updates publish committed facts only and never propagate failures upward: the writer's commit semantics
///         are unaffected by index faults, and unfinished updates are caught up by background reconciliation. The
///         schema initializes lazily; unsupported versions and damaged databases disable this instance instead of
///         spinning per commit, while other failures stay retryable and leave a credential-free note in
///         <see cref="LastFailure" />.
///     </para>
///     <para>
///         增量快路径要求既有行恰好覆盖本批次之前的字节前缀（序号连续且来源长度一致）；跨进程旧端写入或
///         索引滞后都会落到整份扫描路径，不凭长度或时间宣称完整性。
///     </para>
///     <para>
///         The incremental fast path requires the existing row to cover exactly the byte prefix before this batch
///         (continuous sequence and matching source length); cross-process legacy writes or a lagging index fall to
///         the full-scan path, never claiming integrity from length or time alone.
///     </para>
/// </remarks>
public sealed class SessionIndexUpdater
{
    private readonly SessionIndexStore _indexStore;
    private          bool              _disabled;
    private          bool              _schemaReady;

    /// <summary>
    ///     <para>绑定一个索引 store；更新按批次到达逐条应用，不做全库扫描。</para>
    ///     <para>
    ///         Binds one index store; updates apply per arriving batch without whole-store scans.
    ///     </para>
    /// </summary>
    public SessionIndexUpdater(SessionIndexStore indexStore)
    {
        ArgumentNullException.ThrowIfNull(indexStore);
        _indexStore = indexStore;
    }

    /// <summary>
    ///     <para>最近一次更新失败或停用的说明；成功应用一个批次后清空。</para>
    ///     <para>The latest update-failure or disabling note, cleared once a batch applies successfully.</para>
    /// </summary>
    public string? LastFailure { get; private set; }

    /// <summary>
    ///     <para>处理一个已可靠提交的批次；索引故障只记录，不改变提交结果。</para>
    ///     <para>Handles one durably committed batch; index faults are recorded without altering the commit outcome.</para>
    /// </summary>
    /// <param name="batch">
    ///     <para>已完整落盘并推进序号的批次；只读使用。</para>
    ///     <para>The batch that was fully written and sequenced; used read-only.</para>
    /// </param>
    /// <param name="logLengthBeforeBatch">
    ///     <para>本批次写入前的日志字节长度，用于确认既有行是否覆盖同一字节前缀。</para>
    ///     <para>The log byte length before this batch, confirming whether the existing row covers the same prefix.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消本次更新；取消后由后台对账追平，已提交批次不受影响。</para>
    ///     <para>
    ///         Cancels this update; the committed batch stays unaffected and background reconciliation catches up.
    ///     </para>
    /// </param>
    public async ValueTask HandleBatchCommittedAsync(
        SessionLogBatch batch, long logLengthBeforeBatch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (_disabled) return;
        try
        {
            if (!_schemaReady) _schemaReady = await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            if (!_schemaReady) return;
            await UpdateEntryAsync(batch, logLengthBeforeBatch, cancellationToken).ConfigureAwait(false);
            LastFailure = null;
        }
        catch (Exception ex) when (ex is SessionIndexException or SessionStoreException or IOException
                                      or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
        {
            LastFailure = ex.Message;
            Trace.TraceError("Session index '{0}' update for batch {1}:{2} failed: {3}",
                             _indexStore.DatabasePath, batch.SessionId, batch.Sequence, ex.Message);
        }
    }

    /// <summary>
    ///     <para>惰性初始化 schema；版本不受支持或数据库损坏时停用本实例并记录原因，暂态失败抛给外层记录。</para>
    ///     <para>
    ///         Initializes the schema lazily; unsupported versions and damaged databases disable this instance with a
    ///         recorded note, while transient failures throw for the outer boundary to record.
    ///     </para>
    /// </summary>
    private async Task<bool> EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        var availability = await _indexStore.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        switch (availability.Status)
        {
            case SessionIndexAvailabilityStatus.Available :
                return true;
            case SessionIndexAvailabilityStatus.Missing :
            case SessionIndexAvailabilityStatus.NeedsSchema :
                await _indexStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                return true;
            case SessionIndexAvailabilityStatus.NeedsRebuild :
                // 旧版本索引按契约整库重建：索引只是可重建投影，重建只清空索引自身并重写 schema，
                // 随后本批次与后台对账从日志重新派生内容。
                await _indexStore.RebuildSchemaAsync(cancellationToken).ConfigureAwait(false);
                return true;
            case SessionIndexAvailabilityStatus.Unsupported :
            case SessionIndexAvailabilityStatus.Damaged :
                // 版本不受支持或损坏需人工介入；停用避免每次提交空转，修复与重建入口在对账与显式重建。
                // Unsupported versions and damage need manual intervention; disabling avoids spinning per commit,
                // and repair or rebuild entry points live in reconciliation and explicit rebuilds.
                _disabled = true;
                LastFailure = availability.Detail ??
                              $"Session index '{_indexStore.DatabasePath}' is {availability.Status}.";
                Trace.TraceError("Session index updates are disabled: {0}", LastFailure);
                return false;
            default :
                throw new SessionIndexException(
                                                $"Session index '{_indexStore.DatabasePath}' is not usable: {availability.Detail ?? "unknown reason"}.");
        }
    }

    private async Task UpdateEntryAsync(
        SessionLogBatch batch, long logLengthBeforeBatch, CancellationToken cancellationToken)
    {
        var sessionId = batch.SessionId.ToLowerInvariant();
        var logPath   = Path.Combine(_indexStore.StoreDirectory, sessionId + SessionStoreWriter.LogFileSuffix);
        SessionStorePaths.EnsureStoreLeaf(_indexStore.StoreDirectory, logPath);
        var source = new FileInfo(logPath);
        if (!source.Exists)
        {
            // 提交与更新之间日志被外部删除：索引行随之移除，等待对账复核。
            // The log vanished between commit and update through external deletion: the row goes with it, pending
            // a reconciliation review.
            await _indexStore.RemoveAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return;
        }

        var sourceLength   = source.Length;
        var sourceModified = (DateTimeOffset)source.LastWriteTimeUtc;
        var row            = await _indexStore.GetRowAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (row is { State: SessionIndexEntryState.Current, Summary: { } seeded } &&
            seeded.CommitSequence == batch.Sequence - 1                           &&
            row.SourceLength      == logLengthBeforeBatch                         &&
            sourceLength          >= logLengthBeforeBatch)
        {
            // 增量快路径：既有行恰好覆盖本批次前缀，从已提交批次直接推进摘要，不重扫日志正文。
            // Incremental fast path: the existing row covers exactly this batch's prefix, so the summary advances
            // straight from the committed batch without rescanning the log body.
            var projection = new SessionSummaryBuilder.Projection(sessionId);
            projection.SeedFromSummary(seeded);
            projection.Apply(batch);
            var advanced = projection.ToSummary(batch.Sequence,
                                                seeded.IndexedBytes + (sourceLength - logLengthBeforeBatch));
            await _indexStore.TryAdvanceFromCommitAsync(advanced, sourceLength, sourceModified, cancellationToken)
                             .ConfigureAwait(false);
            return;
        }

        var build = await SessionSummaryBuilder
                         .BuildAsync(_indexStore.StoreDirectory, sessionId, cancellationToken)
                         .ConfigureAwait(false);
        if (build is null)
        {
            await _indexStore.RemoveAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _indexStore.WriteSummaryAsync(build.Summary, build.SourceLength, build.SourceModifiedUtc,
                                            cancellationToken).ConfigureAwait(false);
    }
}

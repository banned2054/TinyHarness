using System.Diagnostics;
using System.Text.Json;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>删除项目时逐日志解除项目归属的结果状态。</para>
///     <para>The result state for removing project ownership from each journal during project deletion.</para>
/// </summary>
public enum SessionProjectUnassignmentStatus
{
    /// <summary>
    ///     <para>会话已转为无项目，或本来就没有该项目归属。</para>
    ///     <para>The session is now unassigned or did not carry the project assignment.</para>
    /// </summary>
    Unassigned,

    /// <summary>
    ///     <para>日志当前被其他会话 writer 占用，需稍后重试。</para>
    ///     <para>The journal is held by another session writer and must be retried later.</para>
    /// </summary>
    Pending,

    /// <summary>
    ///     <para>日志无法安全读取或追加解除归属事实。</para>
    ///     <para>The journal could not be safely read or updated with an unassignment fact.</para>
    /// </summary>
    Failed
}

/// <summary>
///     <para>一个会话日志的项目解除归属结果及必要的失败原因。</para>
///     <para>One session journal's project-unassignment outcome and any failure reason.</para>
/// </summary>
/// <param name="SessionId">
///     <para>session store 中的会话 ID。</para>
///     <para>The session id in the session store.</para>
/// </param>
/// <param name="Status">
///     <para>该日志的解除归属结果状态。</para>
///     <para>The unassignment status for this journal.</para>
/// </param>
/// <param name="Detail">
///     <para>失败或占用的说明；成功时为空。</para>
///     <para>The failure or lock detail; null on success.</para>
/// </param>
public sealed record SessionProjectUnassignmentOutcome(
    string                           SessionId,
    SessionProjectUnassignmentStatus Status,
    string?                          Detail);

/// <summary>
///     <para>当前 store 中与指定项目相关的日志解除归属结果。</para>
///     <para>Unassignment outcomes for journals associated with a project in the current store.</para>
/// </summary>
/// <param name="Outcomes">
///     <para>已处理或未能处理的日志结果。</para>
///     <para>Journal outcomes that were completed or could not be completed.</para>
/// </param>
/// <param name="Cancelled">
///     <para>扫描是否因取消而提前停止。</para>
///     <para>Whether scanning stopped early because it was cancelled.</para>
/// </param>
public sealed record SessionProjectUnassignmentResult(
    IReadOnlyList<SessionProjectUnassignmentOutcome> Outcomes,
    bool                                             Cancelled = false)
{
    /// <summary>
    ///     <para>所有已发现的目标归属均已解除。</para>
    ///     <para>Every discovered assignment to the target project has been removed.</para>
    /// </summary>
    public bool IsComplete => !Cancelled &&
                              Outcomes.All(outcome => outcome.Status == SessionProjectUnassignmentStatus.Unassigned);
}

/// <summary>
///     <para>安全、可重试地将当前 session store 中指定项目的会话归属转为无项目；不修改历史正文与执行目录。</para>
///     <para>
///         Safely and resumably changes sessions assigned to a project in the current store to no-project, preserving
///         history content and workspace roots.
///     </para>
/// </summary>
public static class SessionProjectUnbinder
{
    /// <summary>
    ///     <para>用索引筛选未变日志，缺失或变化条目扫描摘要；目标日志在 writer 锁内严格重读，提交后增量更新索引。</para>
    ///     <para>
    ///         Filters unchanged journals through the index and scans summaries for missing or changed entries;
    ///         target journals are strictly re-read under their writer locks and indexed after commit.
    ///     </para>
    /// </summary>
    /// <param name="storeDirectory">
    ///     <para>要扫描的 session store 目录。</para>
    ///     <para>The session-store directory to scan.</para>
    /// </param>
    /// <param name="projectId">
    ///     <para>需要解除归属的规范项目 ID。</para>
    ///     <para>The canonical project id to unassign.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>请求提前停止扫描与追加。</para>
    ///     <para>Requests early cancellation of scanning and appends.</para>
    /// </param>
    /// <returns>
    ///     <para>每个已检查日志的结果；发生取消时标记为未完成。</para>
    ///     <para>Outcomes for checked journals, marked incomplete if cancellation occurred.</para>
    /// </returns>
    /// <exception cref="SessionStoreException">
    ///     <para>无法枚举指定目录中的会话日志。</para>
    ///     <para>Session journals in the specified directory could not be enumerated.</para>
    /// </exception>
    public static async Task<SessionProjectUnassignmentResult> UnassignAsync(
        string storeDirectory, string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (!SessionLogProjectBinding.IsValidProjectId(projectId))
            throw new ArgumentException("A project id must contain exactly 32 lowercase hexadecimal characters.",
                                        nameof(projectId));

        var directory  = Path.GetFullPath(storeDirectory);
        var resolution = await SessionStoreAuthority.ResolveAsync(directory, cancellationToken).ConfigureAwait(false);
        SessionStoreAuthority.ThrowIfBlocked(directory, resolution);
        if (resolution.Mode == SessionStoreMode.Migrated)
            return await UnassignFromBusinessStoreAsync(directory, projectId, cancellationToken).ConfigureAwait(false);

        List<string> sessionIds;
        try
        {
            sessionIds = Directory.EnumerateFiles(directory, "*" + SessionStoreWriter.LogFileSuffix)
                                  .Select(path => Path.GetFileName(path)[..^SessionStoreWriter.LogFileSuffix.Length])
                                  .OrderBy(id => id, StringComparer.Ordinal)
                                  .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SessionStoreException(
                                            $"Cannot enumerate session logs in '{directory}' while deleting project '{projectId}': {ex.Message}",
                                            ex);
        }

        var index = new SessionIndexStore(directory);
        IReadOnlyDictionary<string, SessionIndexRow> rows;
        try
        {
            var availability = await index.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
            rows = availability.Status == SessionIndexAvailabilityStatus.Available
                ? (await index.ListRowsAsync(cancellationToken).ConfigureAwait(false))
                  .ToDictionary(row => row.Id, StringComparer.Ordinal)
                : new Dictionary<string, SessionIndexRow>(StringComparer.Ordinal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SessionProjectUnassignmentResult([], true);
        }
        catch (Exception ex) when (ex is SessionIndexException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceError("Project deletion index lookup for '{0}' failed: {1}", directory, ex.Message);
            rows = new Dictionary<string, SessionIndexRow>(StringComparer.Ordinal);
        }

        var updater  = new SessionIndexUpdater(index);
        var outcomes = new List<SessionProjectUnassignmentOutcome>();
        foreach (var sessionId in sessionIds)
        {
            if (cancellationToken.IsCancellationRequested)
                return new SessionProjectUnassignmentResult(outcomes, true);
            if (!SessionStorePaths.IsValidSessionId(sessionId))
            {
                outcomes.Add(new SessionProjectUnassignmentOutcome(
                                                                   sessionId, SessionProjectUnassignmentStatus.Failed,
                                                                   "The journal filename does not contain a valid session id."));
                continue;
            }

            SessionSummary? observed;
            try
            {
                var path = Path.Combine(directory, sessionId + SessionStoreWriter.LogFileSuffix);
                SessionStorePaths.EnsureStoreLeaf(directory, path);
                var source = new FileInfo(path);
                var row    = rows.GetValueOrDefault(sessionId);
                // 来源标记仅筛选候选；实际追加仍由锁内严格读取决定，不能据索引跳过写入校验。
                if (row is { State: SessionIndexEntryState.Current, Summary: { } summary } &&
                    source.Exists &&
                    row.SourceLength == source.Length &&
                    row.SourceModifiedUtc?.ToUnixTimeMilliseconds() ==
                    new DateTimeOffset(source.LastWriteTimeUtc).ToUnixTimeMilliseconds())
                    observed = summary;
                else
                    observed = (await SessionSummaryBuilder.BuildAsync(directory, sessionId, cancellationToken)
                                                          .ConfigureAwait(false))?.Summary;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new SessionProjectUnassignmentResult(outcomes, true);
            }
            catch (Exception ex) when (SessionStoreWriter.IsRecoverableOpenFailure(ex))
            {
                outcomes.Add(new SessionProjectUnassignmentOutcome(
                                                                   sessionId, SessionProjectUnassignmentStatus.Failed,
                                                                   ex.Message));
                continue;
            }

            if (observed is null)
            {
                outcomes.Add(new SessionProjectUnassignmentOutcome(
                                                                   sessionId, SessionProjectUnassignmentStatus.Failed,
                                                                   "The session journal is empty."));
                continue;
            }

            if (observed.ProjectId != projectId) continue;

            SessionWriterOpenResult opened;
            try
            {
                opened = await SessionStoreWriter.TryOpenExistingAsync(directory, sessionId, null, cancellationToken,
                                                                       updater.HandleBatchCommittedAsync)
                                                 .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new SessionProjectUnassignmentResult(outcomes, true);
            }

            if (opened.Status == SessionWriterOpenStatus.LockHeld)
            {
                outcomes.Add(new SessionProjectUnassignmentOutcome(
                                                                   sessionId, SessionProjectUnassignmentStatus.Pending,
                                                                   opened.Error?.Message));
                continue;
            }

            if (opened.Status == SessionWriterOpenStatus.Failed || opened.Writer is null)
            {
                outcomes.Add(new SessionProjectUnassignmentOutcome(
                                                                   sessionId, SessionProjectUnassignmentStatus.Failed,
                                                                   opened.Error?.Message));
                continue;
            }

            using var writer   = opened.Writer;
            var       snapshot = writer.LoadedSnapshot;
            if (snapshot is null)
            {
                outcomes.Add(new SessionProjectUnassignmentOutcome(
                                                                   sessionId, SessionProjectUnassignmentStatus.Failed,
                                                                   "The session writer did not load a journal snapshot."));
                continue;
            }

            if (snapshot.ProjectId != projectId) continue;
            if (snapshot.ProjectAssignmentState != SessionProjectAssignmentState.Project)
            {
                outcomes.Add(new SessionProjectUnassignmentOutcome(
                                                                   sessionId, SessionProjectUnassignmentStatus.Failed,
                                                                   "The journal project id and assignment state are inconsistent."));
                continue;
            }

            try
            {
                await writer.AppendRecordsAsync(
                                                [
                                                    new SessionLogRecord(string.Empty, "project-unassignment",
                                                                         JsonSerializer.SerializeToElement(
                                                                          new SessionLogProjectUnassignment(
                                                                           SessionLogProjectUnassignment
                                                                              .SchemaName, projectId),
                                                                          SessionLogJsonContext.Default
                                                                             .SessionLogProjectUnassignment))
                                                ],
                                                cancellationToken).ConfigureAwait(false);
                outcomes.Add(new SessionProjectUnassignmentOutcome(
                                                                   sessionId,
                                                                   SessionProjectUnassignmentStatus.Unassigned, null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcomes.Add(new SessionProjectUnassignmentOutcome(
                                                                   sessionId, SessionProjectUnassignmentStatus.Failed,
                                                                   "Cancellation interrupted the ownership append; inspect the journal before retrying."));
                return new SessionProjectUnassignmentResult(outcomes, true);
            }
            catch (Exception ex) when (ex is SessionStoreException or IOException or UnauthorizedAccessException)
            {
                outcomes.Add(new SessionProjectUnassignmentOutcome(
                                                                   sessionId, SessionProjectUnassignmentStatus.Failed,
                                                                   ex.Message));
            }
        }

        return new SessionProjectUnassignmentResult(outcomes);
    }

    /// <summary>
    ///     <para>
    ///         迁移完成后的解绑路径：归属只改业务库，把仍指向该项目的归属行显式改为无项目；不读、不写 JSONL
    ///         归属，也不获取 writer 锁，因此运行中会话不会被写锁阻塞。失败向上抛出并保留删除标记供重试。
    ///     </para>
    ///     <para>
    ///         The post-migration unassignment path: only the business database changes, turning placement rows that
    ///         still reference the project into explicitly no-project; no JSONL ownership is read or written and no
    ///         writer lock is taken, so running sessions are not blocked by a write lock. Failures propagate and keep
    ///         the deletion marker for a retry.
    ///     </para>
    /// </summary>
    private static async Task<SessionProjectUnassignmentResult> UnassignFromBusinessStoreAsync(
        string directory, string projectId, CancellationToken cancellationToken)
    {
        var sessionIds = await SessionStorePlacementStore.ListProjectSessionIdsAsync(directory, projectId,
                                                                                   cancellationToken)
                                                        .ConfigureAwait(false);
        if (sessionIds.Count > 0)
            await SessionStorePlacementStore.UnassignProjectAsync(directory, projectId, cancellationToken)
                                            .ConfigureAwait(false);

        try
        {
            var index        = new SessionIndexStore(directory);
            var availability = await index.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
            if (availability.Status == SessionIndexAvailabilityStatus.Available)
            {
                var placements = await SessionStorePlacementStore.LoadAllAsync(directory, cancellationToken)
                                                                 .ConfigureAwait(false);
                await index.SyncPlacementProjectionAsync(placements, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SessionIndexException or SessionStoreException
                                       or SessionStoreMigrationException or IOException
                                       or UnauthorizedAccessException)
        {
            // 归属权威已更新；索引投影由后台对账追平，不因此把解绑报告为失败。
            Trace.TraceError("Placement projection sync for '{0}' failed: {1}", directory, ex.Message);
        }

        return new SessionProjectUnassignmentResult(
                                                    sessionIds.Select(id => new SessionProjectUnassignmentOutcome(
                                                                        id, SessionProjectUnassignmentStatus.Unassigned,
                                                                        null)).ToArray());
    }
}

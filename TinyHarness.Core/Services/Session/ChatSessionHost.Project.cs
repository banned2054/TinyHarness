using System.Diagnostics;
using System.Text.Json;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Core.Services.Session;

public sealed partial class ChatSessionHost
{
    /// <summary>
    ///     <para>
    ///         可靠解除项目归属后把内存会话转为无项目；迁移完成后归属只写业务库，不再追加 JSONL 归属记录。
    ///         不改动执行目录、历史或运行权限。
    ///     </para>
    ///     <para>
    ///         Durably removes project ownership and then changes the in-memory session to no-project; after migration the
    ///         ownership lives only in the business database and no JSONL ownership record is appended. The workspace
    ///         root, history, and execution permissions are unchanged.
    ///     </para>
    /// </summary>
    /// <param name="projectId">
    ///     <para>必须与会话当前归属一致的项目 ID。</para>
    ///     <para>The project id that must match the session's current assignment.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消本次归属写入。</para>
    ///     <para>Cancels this ownership write.</para>
    /// </param>
    public async Task UnassignProjectAsync(string projectId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (Session.ProjectAssignmentState == SessionProjectAssignmentState.NoProject) return;
        if (Session.ProjectId              != projectId ||
            Session.ProjectAssignmentState != SessionProjectAssignmentState.Project)
            throw new InvalidOperationException(
                                                $"Session '{Session.Id}' is not assigned to project '{projectId}'.");
        if (_sessionWriter is null)
            throw new InvalidOperationException($"Session '{Session.Id}' has no persistent journal writer.");

        var storeDirectory = _sessionWriter.StoreDirectory;
        // 迁移完成后归属以业务库为准：writer 会拒绝旧的归属记录，运行中宿主必须直接改写业务库归属行，
        // 否则项目删除会卡在"标记已写、解绑失败"。业务库写入失败时内存归属保持不变。
        if (await SessionStoreAuthority.IsMigratedAsync(storeDirectory, cancellationToken).ConfigureAwait(false))
        {
            await SessionStorePlacementStore.SetNoProjectAsync(storeDirectory, Session.Id, cancellationToken)
                                            .ConfigureAwait(false);
            Session.UnassignProject(projectId);
            await SyncPlacementProjectionQuietlyAsync(storeDirectory, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _sessionWriter.AppendRecordsAsync(
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
        Session.UnassignProject(projectId);
    }

    /// <summary>
    ///     <para>把业务库归属尽力刷新到列表索引；索引缺失、不可用或写入失败只记诊断，不影响已提交的归属。</para>
    ///     <para>
    ///         Best-effort refreshes the list index from the business placement; a missing or unusable index and write
    ///         failures are diagnostic only and never change the committed ownership.
    ///     </para>
    /// </summary>
    private static async Task SyncPlacementProjectionQuietlyAsync(
        string storeDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var index        = new SessionIndexStore(storeDirectory);
            var availability = await index.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
            if (availability.Status != SessionIndexAvailabilityStatus.Available) return;
            var placements = await SessionStorePlacementStore.LoadAllAsync(storeDirectory, cancellationToken)
                                                             .ConfigureAwait(false);
            await index.SyncPlacementProjectionAsync(placements, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SessionIndexException or SessionStoreException
                                       or SessionStoreMigrationException or IOException
                                       or UnauthorizedAccessException)
        {
            Trace.TraceError("Placement projection sync for '{0}' failed: {1}", storeDirectory, ex.Message);
        }
    }
}

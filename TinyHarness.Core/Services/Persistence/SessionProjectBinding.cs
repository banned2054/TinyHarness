using System.Diagnostics;
using System.Text.Json;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Project;
using TinyHarness.Core.Models.Session;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>为生产入口保存项目并在原 writer 的空闲边界补齐旧会话归属；归属不改变执行权限。</para>
///     <para>
///         Saves projects for production entries and binds legacy sessions at their writer's idle boundary without
///         changing permissions.
///     </para>
/// </summary>
public static class SessionProjectBinding
{
    /// <summary>
    ///     <para>按最终执行目录可靠保存项目；调用方须先校验所有附加可写根。</para>
    ///     <para>
    ///         Reliably saves the project for the final execution root; callers must first validate all additional writable
    ///         roots.
    ///     </para>
    /// </summary>
    public static Task<Project> GetProjectAsync(
        string storeDirectory, string workspaceRoot, CancellationToken cancellationToken)
    {
        var directory = SessionStorePaths.ResolveProtected(storeDirectory, workspaceRoot);
        return ProjectStore.GetOrCreateAsync(ProjectStore.FilePath(directory), workspaceRoot,
                                             cancellationToken : cancellationToken);
    }

    /// <summary>
    ///     <para>
    ///         严格恢复成功后，使用原 writer 追加一次归属；已绑定会话不查询目录册，保存失败不改变内存归属。
    ///         迁移后归属只写业务库，并只在该会话尚无归属行时插入，随后读回最终归属同步内存与索引。
    ///     </para>
    ///     <para>
    ///         After strict restoration, appends ownership once through the original writer; bound sessions skip the
    ///         catalog and failed writes leave memory unbound. After migration ownership goes to the business database
    ///         alone: the row is inserted only while the session has none, and the final placement is read back to sync
    ///         memory and the index.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         恢复快照可能早于并发写入：后台初始化或另一个进程可能已写入归属，甚至已显式解绑为 NoProject。
    ///         因此这里不能无条件 UPSERT，否则续聊会覆盖更新后的权威；先到者胜出，本次只镜像最终事实。
    ///     </para>
    ///     <para>
    ///         The restored snapshot can predate concurrent writes: background initialization or another process may
    ///         already have written a placement, or explicitly unassigned the session to NoProject. An unconditional
    ///         upsert here would therefore overwrite the newer authority; the first writer wins and this call only
    ///         mirrors the final fact.
    ///     </para>
    /// </remarks>
    public static async Task BindRestoredAsync(
        string storeDirectory, ChatSession session, SessionStoreWriter writer, CancellationToken cancellationToken)
    {
        if (writer.SessionId != session.Id)
            throw new InvalidOperationException("The writer does not belong to the restored session.");
        if (session.ProjectAssignmentState != SessionProjectAssignmentState.PendingInitialization) return;

        // 迁移完成后归属只写业务库：恢复后的待初始化会话按保存的执行根创建/复用项目并写入归属行，
        // 不追加 JSONL 归属记录，也不让历史日志成为第二权威。
        if (await SessionStoreAuthority.IsMigratedAsync(storeDirectory, cancellationToken).ConfigureAwait(false))
        {
            var migratedProject = await GetProjectAsync(storeDirectory, session.WorkspaceRoot, cancellationToken)
                                       .ConfigureAwait(false);
            // 仅缺行时插入：并发初始化或显式解绑已经写入的归属不被续聊覆盖。
            // Insert only while the row is missing: a concurrent initialization or explicit unassignment wins.
            await SessionStorePlacementStore.TryInitializeProjectAsync(storeDirectory, session.Id, migratedProject.Id,
                                                                       cancellationToken).ConfigureAwait(false);
            var placement = await SessionStorePlacementStore.GetAsync(storeDirectory, session.Id, cancellationToken)
                                                           .ConfigureAwait(false);
            switch (placement.State)
            {
                case SessionProjectAssignmentState.Project when placement.ProjectId is { } projectId :
                    session.BindProject(projectId);
                    break;
                case SessionProjectAssignmentState.NoProject :
                    session.MarkNoProject();
                    break;
                default :
                    throw new SessionStoreException(
                                                     $"Session '{session.Id}' still has no placement after the restore binding attempt.");
            }

            try
            {
                var index = new SessionIndexStore(storeDirectory);
                if ((await index.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false)).Status ==
                    SessionIndexAvailabilityStatus.Available)
                    await index.SetPlacementProjectionAsync(session.Id, placement.ProjectId, placement.State,
                                                            cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is SessionIndexException or SessionStoreException or IOException
                                           or UnauthorizedAccessException)
            {
                // 索引只是派生投影；归属已写入业务库，缺口由后台对账追平。
                Trace.TraceError("Placement projection for '{0}' failed: {1}", session.Id, ex.Message);
            }

            return;
        }

        var project = await GetProjectAsync(storeDirectory, session.WorkspaceRoot, cancellationToken)
           .ConfigureAwait(false);
        await writer.AppendRecordsAsync(
        [
            new SessionLogRecord(string.Empty, "project-binding",
                                 JsonSerializer.SerializeToElement(
                                                                   new SessionLogProjectBinding(SessionLogProjectBinding
                                                                               .SchemaName, project.Id),
                                                                   SessionLogJsonContext.Default
                                                                      .SessionLogProjectBinding))
        ], cancellationToken).ConfigureAwait(false);
        session.BindProject(project.Id);
    }
}

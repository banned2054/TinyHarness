using System.Text.Json;
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
    ///     <para>严格恢复成功后，使用原 writer 追加一次归属；已绑定会话不查询目录册，保存失败不改变内存归属。</para>
    ///     <para>
    ///         After strict restoration, appends ownership once through the original writer; bound sessions skip the catalog
    ///         and failed writes leave memory unbound.
    ///     </para>
    /// </summary>
    public static async Task BindRestoredAsync(
        string storeDirectory, ChatSession session, SessionStoreWriter writer, CancellationToken cancellationToken)
    {
        if (writer.SessionId != session.Id)
            throw new InvalidOperationException("The writer does not belong to the restored session.");
        if (session.ProjectId is not null) return;
        var project = await GetProjectAsync(storeDirectory, session.WorkspaceRoot, cancellationToken)
                            .ConfigureAwait(false);
        await writer.AppendRecordsAsync(
        [
            new SessionLogRecord(string.Empty, "project-binding",
                                 JsonSerializer.SerializeToElement(
                                     new SessionLogProjectBinding(SessionLogProjectBinding.SchemaName, project.Id),
                                     SessionLogJsonContext.Default.SessionLogProjectBinding))
        ], cancellationToken).ConfigureAwait(false);
        session.BindProject(project.Id);
    }
}

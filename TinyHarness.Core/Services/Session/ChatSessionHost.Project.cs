using System.Text.Json;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Core.Services.Session;

public sealed partial class ChatSessionHost
{
    /// <summary>
    ///     <para>可靠追加项目解除归属事实后，将内存会话转为无项目；不改动执行目录、历史或运行权限。</para>
    ///     <para>
    ///         After durably appending the project-unassignment fact, changes the in-memory session to no-project without
    ///         changing its workspace root, history, or execution permissions.
    ///     </para>
    /// </summary>
    /// <param name="projectId">
    ///     <para>必须与会话当前归属一致的项目 ID。</para>
    ///     <para>The project id that must match the session's current assignment.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消本次日志追加。</para>
    ///     <para>Cancels this journal append.</para>
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
}

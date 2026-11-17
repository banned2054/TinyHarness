using System.Data;
using Microsoft.Data.Sqlite;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Persistence;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>
///         业务库中的会话当前项目归属：以行存在性表达三态——有行且项目非空为已归属、有行且项目为空为
///         明确无项目、无行为尚未初始化。仅在迁移完成后作为生产权威。
///     </para>
///     <para>
///         Session current project placement inside the business database: row presence expresses the three states —
///         a row with a project means assigned, a row with a null project means explicitly no project, and no row
///         means not initialized yet. It is the production authority only after the migration completes.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         缺行不能被读成"无项目"：读取返回 <see cref="SessionProjectAssignmentState.PendingInitialization" />，
///         只有显式写入空项目才产生"明确无项目"。归属外键要求被引用的项目已存在于业务库。
///     </para>
///     <para>
///         A missing row is never read as "no project": reads return
///         <see cref="SessionProjectAssignmentState.PendingInitialization" /> and only an explicit null-project write
///         produces "explicitly no project". The placement foreign key requires the referenced project to exist.
///     </para>
/// </remarks>
public static class SessionStorePlacementStore
{
    /// <summary>
    ///     <para>读取一个会话的归属；缺行为尚未初始化，绝不推断为无项目。</para>
    ///     <para>Reads one session's placement; a missing row is not initialized and is never inferred as no project.</para>
    /// </summary>
    public static async Task<SessionStorePlacement> GetAsync(
        string storeDirectory, string sessionId, CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
                                                                  cancellationToken).ConfigureAwait(false);
        return await GetCoreAsync(connection, null, sessionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>读取全部归属行，供列表投影与初始化批处理一次取回；未列出的会话为尚未初始化。</para>
    ///     <para>Reads every placement row for list projections and batch initialization; sessions not listed are not initialized yet.</para>
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, SessionStorePlacement>> LoadAllAsync(
        string storeDirectory, CancellationToken cancellationToken)
    {
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
                                                                  cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id, project_id FROM session_placement;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var placements = new Dictionary<string, SessionStorePlacement>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            placements[reader.GetString(0)] = reader.IsDBNull(1)
                ? SessionStorePlacement.NoProject()
                : SessionStorePlacement.ForProject(reader.GetString(1));
        return placements;
    }

    /// <summary>
    ///     <para>
    ///         仅在会话尚无归属行时写入项目归属；并发初始化时后到者不覆盖先到者，返回是否由本次写入。
    ///     </para>
    ///     <para>
    ///         Writes a project placement only while the session has no placement row yet; during concurrent
    ///         initialization the later caller never overwrites the earlier one, and the return value says whether this
    ///         call wrote the row.
    ///     </para>
    /// </summary>
    public static async Task<bool> TryInitializeProjectAsync(
        string storeDirectory, string sessionId, string projectId, CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        ValidateProjectId(projectId);
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
                                                                  cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_placement (session_id, project_id, updated_utc)
            VALUES ($session_id, $project_id, $updated_utc)
            ON CONFLICT(session_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$project_id", projectId);
        command.Parameters.AddWithValue("$updated_utc", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new SessionStoreException(
                                            $"Session '{sessionId}' cannot be assigned to project '{projectId}' because the project does not exist in the business catalog.",
                                            ex);
        }
    }

    /// <summary>
    ///     <para>把会话归属写入指定项目；重复写入幂等，被引用项目必须存在。</para>
    ///     <para>Writes a session's placement to the given project idempotently; the referenced project must exist.</para>
    /// </summary>
    public static Task SetProjectAsync(
        string storeDirectory, string sessionId, string projectId, CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        ValidateProjectId(projectId);
        return UpsertAsync(storeDirectory, sessionId, projectId, cancellationToken);
    }

    /// <summary>
    ///     <para>把会话归属显式设为无项目；这是三态中"明确无项目"，与缺行的"尚未初始化"不同。</para>
    ///     <para>Explicitly sets a session to no project; this is the "explicitly no project" state, unlike the "not initialized" state of a missing row.</para>
    /// </summary>
    public static Task SetNoProjectAsync(
        string storeDirectory, string sessionId, CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        return UpsertAsync(storeDirectory, sessionId, null, cancellationToken);
    }

    /// <summary>
    ///     <para>把仍归属于指定项目的会话显式改为无项目；返回处理的会话数，用于项目删除解绑。</para>
    ///     <para>Explicitly turns sessions still assigned to the project into no-project; returns the affected count for project-deletion unassignment.</para>
    /// </summary>
    public static async Task<int> UnassignProjectAsync(
        string storeDirectory, string projectId, CancellationToken cancellationToken)
    {
        ValidateProjectId(projectId);
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
                                                                  cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE session_placement SET project_id = NULL, updated_utc = $updated " +
                              "WHERE project_id = $project_id;";
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$project_id", projectId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>列出仍归属于指定项目的会话 ID，供删除前诊断；解绑本身按项目批量更新。</para>
    ///     <para>Lists session ids still assigned to the project for pre-deletion diagnostics; unassignment itself updates by project.</para>
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListProjectSessionIdsAsync(
        string storeDirectory, string projectId, CancellationToken cancellationToken)
    {
        ValidateProjectId(projectId);
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
                                                                  cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id FROM session_placement WHERE project_id = $project_id " +
                              "ORDER BY session_id;";
        command.Parameters.AddWithValue("$project_id", projectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ids = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>
    ///     <para>删除一个已永久删除会话的归属行；不属于已切换会话的删除清理。</para>
    ///     <para>Removes one permanently deleted session's placement row as part of switched-session cleanup.</para>
    /// </summary>
    public static async Task RemoveSessionAsync(
        string storeDirectory, string sessionId, CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
                                                                  cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM session_placement WHERE session_id = $session_id;";
        command.Parameters.AddWithValue("$session_id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>在给定事务内用导入快照整体替换归属行；只由迁移引擎调用。</para>
    ///     <para>Replaces placement rows with the imported snapshot inside the given transaction; used only by the migration engine.</para>
    /// </summary>
    internal static async Task ReplaceAllAsync(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<SessionStorePlacementRow> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM session_placement;";
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        foreach (var row in rows)
        {
            ValidateSessionId(row.SessionId);
            if (row.ProjectId is { } projectId) ValidateProjectId(projectId);
            var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO session_placement (session_id, project_id, updated_utc)
                VALUES ($session_id, $project_id, $updated_utc);
                """;
            insert.Parameters.AddWithValue("$session_id", row.SessionId);
            insert.Parameters.AddWithValue("$project_id", (object?)row.ProjectId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$updated_utc", row.UpdatedUtc.ToUnixTimeMilliseconds());
            try
            {
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                throw new SessionStoreMigrationException(
                                                        $"Session placement for '{row.SessionId}' references project " +
                                                        $"'{row.ProjectId ?? "(none)"}' that does not exist in the imported catalog.",
                                                        SessionStoreFailureKind.Conflict, ex);
            }
        }
    }

    /// <summary>
    ///     <para>读取单个会话的归属；不打开连接。</para>
    ///     <para>Reads one session placement without opening a connection.</para>
    /// </summary>
    private static async Task<SessionStorePlacement> GetCoreAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sessionId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT project_id FROM session_placement WHERE session_id = $session_id;";
        command.Parameters.AddWithValue("$session_id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return SessionStorePlacement.PendingInitialization();
        return reader.IsDBNull(0)
            ? SessionStorePlacement.NoProject()
            : SessionStorePlacement.ForProject(reader.GetString(0));
    }

    private static async Task UpsertAsync(
        string storeDirectory, string sessionId, string? projectId, CancellationToken cancellationToken)
    {
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
                                                                  cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, false);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_placement (session_id, project_id, updated_utc)
            VALUES ($session_id, $project_id, $updated_utc)
            ON CONFLICT(session_id) DO UPDATE SET
                project_id  = excluded.project_id,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$project_id", (object?)projectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated_utc", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new SessionStoreException(
                                            $"Session '{sessionId}' cannot be assigned to project '{projectId}' because the project does not exist in the business catalog.",
                                            ex);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateSessionId(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!SessionStorePaths.IsValidSessionId(sessionId) || sessionId.Any(char.IsUpper))
            throw new ArgumentException(
                                        $"Session id '{sessionId}' must contain exactly 32 lowercase hexadecimal characters.",
                                        nameof(sessionId));
    }

    private static void ValidateProjectId(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (!SessionLogProjectBinding.IsValidProjectId(projectId))
            throw new ArgumentException(
                                        $"Project id '{projectId}' must contain exactly 32 lowercase hexadecimal characters.",
                                        nameof(projectId));
    }
}

using Microsoft.Data.Sqlite;
using System.Data;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Project;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>
///         项目删除事务的开始结果：带删除标记的项目，以及本次一并解除归属的会话数与会话 ID。
///     </para>
///     <para>
///         The result of the project-deletion transaction's start: the project carrying the deletion marker plus the
///         number and the ids of the session placements unassigned in the same transaction.
///     </para>
/// </summary>
/// <param name="Project">
///     <para>已带删除标记的项目；中断后续作时沿用原修订号。</para>
///     <para>The project carrying the deletion marker; a resumed attempt keeps the stored revision.</para>
/// </param>
/// <param name="UnassignedSessionCount">
///     <para>本次事务内改为"明确无项目"的会话数。</para>
///     <para>The number of sessions turned into "explicitly no project" inside this transaction.</para>
/// </param>
/// <param name="UnassignedSessionIds">
///     <para>
///         已解除该项目归属、需要宿主校正现存行的会话 ID（按序数排序）：包含本次事务解绑的会话，以及此前
///         已提交但删除未完成的运行留下的同名名单，使重试仍能把过期行纠正过来。
///     </para>
///     <para>
///         The ids of the sessions no longer assigned to the project whose host rows need correcting, ordinal-sorted:
///         the sessions unassigned by this transaction plus the roster recorded by an earlier committed run whose
///         deletion did not finish, so a retry still corrects stale rows.
///     </para>
/// </param>
public sealed record SessionProjectDeletionStart(
    Project               Project,
    int                   UnassignedSessionCount,
    IReadOnlyList<string> UnassignedSessionIds);

/// <summary>
///     <para>
///         业务库中的项目目录册：项目定义、目录、修订号与删除标记的读写权威；仅在迁移完成后使用。
///     </para>
///     <para>
///         The project catalog inside the business database: the read/write authority for project definitions, roots,
///         revisions, and deletion markers; used only after the migration completes.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         目录册修订号在旧 JSON 文件中是每次保存递增的全局计数，业务库没有对应列；读取时返回 0，变更检测依赖
///         项目级修订号与逐项内容比较，不依赖全局计数。项目顺序以创建时间升序近似原目录册的追加顺序，用户
///         可见顺序仍由手动顺序与活动时间决定。
///     </para>
///     <para>
///         The catalog revision in the legacy JSON file is a global counter incremented on every save, and the
///         business database has no such column; reads return zero and change detection relies on per-project
///         revisions and per-item content comparison. Project order approximates the legacy append order by creation
///         time, while user-visible order stays with manual order and activity.
///     </para>
/// </remarks>
public static class SessionStoreProjectCatalogStore
{
    /// <summary>
    ///     <para>
    ///         解绑名单在业务库元数据中的键前缀；值为按序数排序、逗号分隔的会话 ID，供中断后重试重放。
    ///     </para>
    ///     <para>
    ///         The metadata key prefix of the unassignment roster; its value is the ordinal-sorted, comma-separated
    ///         session ids so an interrupted deletion can replay them on retry.
    ///     </para>
    /// </summary>
    private const string UnassignmentRosterKeyPrefix = "project_unassigned:";

    /// <summary>
    ///     <para>读取全部项目并构造目录册快照；目录册修订号固定为 0。</para>
    ///     <para>Reads every project and builds a catalog snapshot; the catalog revision is always zero.</para>
    /// </summary>
    public static async Task<ProjectCatalog> LoadAsync(string storeDirectory, CancellationToken cancellationToken)
    {
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
                 cancellationToken).ConfigureAwait(false);
        return await LoadCoreAsync(connection, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>按最终执行目录创建或复用项目：锁内按平台比较键精确匹配，命中即复用，未命中才创建。</para>
    ///     <para>
    ///         Creates or reuses a project for the final execution root: exact matching by the platform comparison key
    ///         inside the transaction, reusing a hit and only creating on a miss.
    ///     </para>
    /// </summary>
    public static async Task<Project> GetOrCreateAsync(
        string storeDirectory, string workspaceRoot, DateTimeOffset? utcNow, CancellationToken cancellationToken)
    {
        var root      = ProjectRoots.Normalize(workspaceRoot);
        var rootKey   = RootKey(root);
        var timestamp = utcNow ?? DateTimeOffset.UtcNow;
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
                 cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, false);
        var existing = await FindByRootKeyAsync(connection, transaction, rootKey, cancellationToken)
           .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.IsDeleting)
                throw new ProjectStoreException($"Project '{existing.Id}' for root '{root}' is being deleted; " +
                                                "wait for deletion to finish before creating a session.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        var project = Project.CreateNew(root, timestamp);
        await InsertAsync(connection, transaction, project, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return project;
    }

    /// <summary>
    ///     <para>按调用方持有的修订号改名；过期基线、项目不存在或删除中的项目明确拒绝。</para>
    ///     <para>
    ///         Renames with the caller's revision; a stale baseline, a missing project, or a deleting project is refused
    ///         explicitly.
    ///     </para>
    /// </summary>
    public static async Task<Project> RenameAsync(string            storeDirectory,   string projectId, string newName,
                                                  long              expectedRevision, DateTimeOffset? utcNow,
                                                  CancellationToken cancellationToken)
    {
        var name = Project.ValidateName(newName);
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
                 cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, false);
        var project = await FindByIdAsync(connection, transaction, projectId, cancellationToken)
                         .ConfigureAwait(false)
                   ?? throw new ProjectStoreException($"Project '{projectId}' was not found in the business " +
                                                      $"catalog of '{Path.GetFullPath(storeDirectory)}'.");
        if (project.IsDeleting)
            throw new ProjectStoreException($"Project '{projectId}' is being deleted and cannot be renamed.");
        if (project.Revision != expectedRevision)
            throw new ProjectStoreException($"Project '{projectId}' changed while it was being edited (stored " +
                                            $"revision {project.Revision}, expected {expectedRevision}); reload it and retry.");

        var renamed = project with
        {
            Name = name,
            Revision = project.Revision + 1,
            UpdatedUtc = utcNow ?? DateTimeOffset.UtcNow
        };
        await UpdateAsync(connection, transaction, renamed, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return renamed;
    }

    /// <summary>
    ///     <para>在事务内把项目标记为删除中并递增修订号；已标记项目直接返回，便于中断后继续。</para>
    ///     <para>
    ///         Marks a project as deleting and increments its revision inside a transaction; an already marked project is
    ///         returned so interruption can resume.
    ///     </para>
    /// </summary>
    public static async Task<Project> BeginDeletionAsync(string storeDirectory, string projectId, long expectedRevision,
                                                         DateTimeOffset? utcNow, CancellationToken cancellationToken)
    {
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
                 cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, false);
        var project = await FindByIdAsync(connection, transaction, projectId, cancellationToken)
                         .ConfigureAwait(false)
                   ?? throw new ProjectStoreException($"Project '{projectId}' was not found in the business " +
                                                      $"catalog of '{Path.GetFullPath(storeDirectory)}'.");
        if (project.IsDeleting)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return project;
        }

        if (project.Revision != expectedRevision)
            throw new ProjectStoreException($"Project '{projectId}' changed while deletion was being confirmed "   +
                                            $"(stored revision {project.Revision}, expected {expectedRevision}); " +
                                            "reload it and retry.");

        var deleting = project with
        {
            IsDeleting = true,
            Revision = project.Revision + 1,
            UpdatedUtc = utcNow ?? DateTimeOffset.UtcNow
        };
        await UpdateAsync(connection, transaction, deleting, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleting;
    }

    /// <summary>
    ///     <para>
    ///         在单个事务内开始迁移后的项目删除：标记删除中、把仍归属该项目的会话改为明确无项目、清除该项目的
    ///         置顶与手动顺序引用，一次提交后不再需要逐日志扫描旧归属。
    ///     </para>
    ///     <para>
    ///         Starts a post-migration project deletion inside one transaction: it marks the project deleting, turns
    ///         every remaining placement into explicitly no project, and clears the project's pin and manual-order
    ///         references, so no per-log legacy scan is needed after the commit.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         会话日志不被读写：迁移后归属与组织状态只在业务库。已标记删除的项目直接沿用以支持中断后的续作，
    ///         只重新执行幂等清理。折叠分组键属于界面层，仍由宿主钩子按项目清理。运行中的会话宿主与后续并发
    ///         写入不在本事务保护范围内，最终移除仍由归属外键兜底。
    ///     </para>
    ///     <para>
    ///         Session logs are never read or written: after migration, placement and organization state live in the
    ///         business database only. An already marked project is reused so an interrupted deletion can resume and
    ///         only rerun the idempotent cleanup. Collapsed group keys belong to the UI layer and stay with the host
    ///         hook. Running session hosts and later concurrent writes are outside this transaction, and the placement
    ///         foreign key still guards the final removal.
    ///     </para>
    /// </remarks>
    /// <param name="storeDirectory">
    ///     <para>调用方已解析并校验的受保护存储目录。</para>
    ///     <para>The caller-resolved and validated protected store directory.</para>
    /// </param>
    /// <param name="projectId">
    ///     <para>要删除的规范项目 ID。</para>
    ///     <para>The canonical project id to delete.</para>
    /// </param>
    /// <param name="expectedRevision">
    ///     <para>发起删除时看到的目录册修订号；项目已标记删除时忽略。</para>
    ///     <para>The catalog revision observed when deletion was confirmed; ignored once the project is already marked.</para>
    /// </param>
    /// <param name="utcNow">
    ///     <para>注入的当前时间，为空时使用系统时间。</para>
    ///     <para>The injected current time, or the system time when null.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消事务内的读取与写入。</para>
    ///     <para>Cancels the transaction's reads and writes.</para>
    /// </param>
    /// <returns>
    ///     <para>带删除标记的项目，以及本次解除归属的会话数与 ID。</para>
    ///     <para>The project carrying the deletion marker plus the number and ids of the placements unassigned by this call.</para>
    /// </returns>
    /// <exception cref="ProjectStoreException">
    ///     <para>项目不存在或修订号已过期。</para>
    ///     <para>The project is missing or its revision is stale.</para>
    /// </exception>
    public static async Task<SessionProjectDeletionStart> BeginDeletionAndUnassignAsync(
        string            storeDirectory, string projectId, long expectedRevision, DateTimeOffset? utcNow,
        CancellationToken cancellationToken)
    {
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
                 cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, false);
        var project = await FindByIdAsync(connection, transaction, projectId, cancellationToken)
                         .ConfigureAwait(false)
                   ?? throw new ProjectStoreException($"Project '{projectId}' was not found in the business " +
                                                      $"catalog of '{Path.GetFullPath(storeDirectory)}'.");

        if (!project.IsDeleting)
        {
            if (project.Revision != expectedRevision)
                throw new ProjectStoreException($"Project '{projectId}' changed while deletion was being "  +
                                                $"confirmed (stored revision {project.Revision}, expected " +
                                                $"{expectedRevision}); reload it and retry.");
            project = project with
            {
                IsDeleting = true,
                Revision = project.Revision + 1,
                UpdatedUtc = utcNow ?? DateTimeOffset.UtcNow
            };
            await UpdateAsync(connection, transaction, project, cancellationToken).ConfigureAwait(false);
        }

        // 先读出确切的归属会话再解绑：宿主需要这份名单更新现存行投影，只给计数会让未打开 runtime 的行
        // 继续显示已删除的项目。
        // Read the exact placements before unassigning them: the host needs the list to update its existing row
        // projections, and a bare count would leave rows without an open runtime still showing the deleted project.
        var stillPlaced = await ListProjectSessionIdsAsync(connection, transaction, projectId, cancellationToken)
           .ConfigureAwait(false);
        var now      = (utcNow ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        var unassign = connection.CreateCommand();
        unassign.Transaction = transaction;
        unassign.CommandText = "UPDATE session_placement SET project_id = NULL, updated_utc = $now " +
                               "WHERE project_id = $project_id;";
        unassign.Parameters.AddWithValue("$now", now);
        unassign.Parameters.AddWithValue("$project_id", projectId);
        var unassigned = await unassign.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // 名单与解绑同事务落库：解绑提交后若宿主钩子失败，重试时归属行已经为空，只有这份累计名单还能让宿主
        // 校正仍显示该项目的现存行。项目最终移除时在同一事务内清除。
        // The roster is stored in the same transaction as the unassignment: after this commit the placements are
        // empty, so when a host hook fails only this accumulated roster still lets the host correct the surviving
        // rows. Final removal clears it inside the same transaction.
        var roster = new SortedSet<string>(stillPlaced, StringComparer.Ordinal);
        roster.UnionWith(await ReadUnassignmentRosterAsync(connection, transaction, projectId, cancellationToken)
                            .ConfigureAwait(false));
        if (roster.Count > 0)
            await WriteUnassignmentRosterAsync(connection, transaction, projectId, roster, cancellationToken)
               .ConfigureAwait(false);

        var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = """
        DELETE FROM pinned_project WHERE project_id = $project_id;
        DELETE FROM project_order WHERE project_id = $project_id;
        """;
        clear.Parameters.AddWithValue("$project_id", projectId);
        await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SessionProjectDeletionStart(project, unassigned, [.. roster]);
    }

    /// <summary>
    ///     <para>读取一个项目已解绑但尚未完成删除的会话名单；键缺失或为空时返回空集合。</para>
    ///     <para>
    ///         Reads the roster of sessions unassigned from a project whose deletion has not completed; a missing or
    ///         blank key yields an empty set.
    ///     </para>
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadUnassignmentRosterAsync(
        SqliteConnection  connection, SqliteTransaction transaction, string projectId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT value FROM {SessionStoreDatabase.MetaTableName} WHERE key = $key;";
        command.Parameters.AddWithValue("$key", UnassignmentRosterKeyPrefix + projectId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (string.IsNullOrEmpty(value)) return [];

        // 只有本类型的写入会生成该键，因此非法片段说明外部改写过：丢弃它比让删除永久无法重试更安全，
        // 且被丢弃的片段本来也不可能是真实会话 ID。
        // Only this type writes the key, so an invalid fragment means an external rewrite: dropping it is safer than
        // making the deletion permanently unretryable, and a dropped fragment could not name a real session anyway.
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(SessionStorePaths.IsValidSessionId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
    }

    /// <summary>
    ///     <para>把解绑名单写入业务库元数据，与解绑处于同一事务。</para>
    ///     <para>
    ///         Writes the unassignment roster into the business metadata inside the same transaction as the
    ///         unassignment.
    ///     </para>
    /// </summary>
    private static Task WriteUnassignmentRosterAsync(SqliteConnection  connection, SqliteTransaction transaction,
                                                     string            projectId,  IReadOnlyCollection<string> roster,
                                                     CancellationToken cancellationToken) =>
        SessionStoreDatabase.WriteMetaValueAsync(connection, transaction, UnassignmentRosterKeyPrefix + projectId,
                                                 string.Join(',', roster.Order(StringComparer.Ordinal)),
                                                 cancellationToken);

    /// <summary>
    ///     <para>在项目最终移除的同一事务内删除解绑名单，避免已完成的删除继续留下元数据。</para>
    ///     <para>
    ///         Removes the unassignment roster inside the same transaction as the project's final removal, so a
    ///         completed deletion leaves no metadata behind.
    ///     </para>
    /// </summary>
    private static async Task ClearUnassignmentRosterAsync(SqliteConnection connection, SqliteTransaction transaction,
                                                           string projectId, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {SessionStoreDatabase.MetaTableName} WHERE key = $key;";
        command.Parameters.AddWithValue("$key", UnassignmentRosterKeyPrefix + projectId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>在给定事务内读出仍归属该项目的会话 ID，按序数排序。</para>
    ///     <para>Reads the session ids still placed in the project inside the given transaction, ordinal-sorted.</para>
    /// </summary>
    private static async Task<IReadOnlyList<string>> ListProjectSessionIdsAsync(
        SqliteConnection  connection, SqliteTransaction transaction, string projectId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT session_id FROM session_placement WHERE project_id = $project_id " +
                              "ORDER BY session_id;";
        command.Parameters.AddWithValue("$project_id", projectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var             ids    = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>
    ///     <para>从业务库移除已标记删除的项目；归属外键会拒绝仍有会话指向的项目。</para>
    ///     <para>
    ///         Removes a project marked for deletion; the placement foreign key refuses a project that sessions still
    ///         reference.
    ///     </para>
    /// </summary>
    public static async Task CompleteDeletionAsync(
        string storeDirectory, string projectId, long expectedRevision, CancellationToken cancellationToken)
    {
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
                 cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, false);
        var project = await FindByIdAsync(connection, transaction, projectId, cancellationToken)
           .ConfigureAwait(false);
        if (project is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!project.IsDeleting) throw new ProjectStoreException($"Project '{projectId}' is not marked for deletion.");
        if (project.Revision != expectedRevision)
            throw new ProjectStoreException($"Project '{projectId}' changed while deletion was being completed " +
                                            $"(stored revision {project.Revision}, expected {expectedRevision}); reload and retry.");

        var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM project WHERE id = $id;";
        delete.Parameters.AddWithValue("$id", projectId);
        try
        {
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new ProjectStoreException($"Project '{projectId}' is still referenced by session placements and " +
                                            "cannot be removed; unassign every session first.", ex);
        }

        // 项目与它的解绑名单在同一事务内消失：名单只为中断后的重试存在。
        // The project and its unassignment roster disappear in one transaction: the roster only exists for retries
        // after an interruption.
        await ClearUnassignmentRosterAsync(connection, transaction, projectId, cancellationToken)
           .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>在给定事务内用导入快照整体替换项目定义；只由迁移引擎调用。</para>
    ///     <para>
    ///         Replaces project definitions with the imported snapshot inside the given transaction; used only by the
    ///         migration engine.
    ///     </para>
    /// </summary>
    internal static async Task ReplaceAllAsync(SqliteConnection       connection, SqliteTransaction transaction,
                                               IReadOnlyList<Project> projects,   CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM project;";
        try
        {
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new SessionStoreMigrationException("The project catalog cannot be imported while session "  +
                                                     "placements still reference projects; the source order " +
                                                     "was changed and the migration must be restarted from organization state.",
                                                     SessionStoreFailureKind.Conflict, ex);
        }

        foreach (var project in projects)
            await InsertAsync(connection, transaction, project, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>返回项目的平台比较键：Windows 使用大写不变形式，其他平台使用规范化路径。</para>
    ///     <para>
    ///         Returns the platform comparison key of a root: uppercase invariant on Windows and the normalized path
    ///         elsewhere.
    ///     </para>
    /// </summary>
    internal static string RootKey(string normalizedRoot) =>
        OperatingSystem.IsWindows() ? normalizedRoot.ToUpperInvariant() : normalizedRoot;

    private static async Task<ProjectCatalog> LoadCoreAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, name, root_path, revision, is_deleting, created_utc, " +
                              "updated_utc FROM project ORDER BY created_utc, id;";
        await using var reader   = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var             projects = new List<Project>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) projects.Add(ReadProject(reader));
        return new ProjectCatalog
        {
            Schema   = ProjectCatalog.CurrentSchema,
            Revision = 0,
            Projects = projects
        };
    }

    private static Project ReadProject(SqliteDataReader reader)
    {
        var rootPath = reader.GetString(2);
        var project = new Project
        {
            Id          = reader.GetString(0),
            Name        = reader.GetString(1),
            Roots       = [new ProjectRoot(rootPath)],
            PrimaryRoot = rootPath,
            Revision    = reader.GetInt64(3),
            IsDeleting  = reader.GetInt64(4) != 0,
            CreatedUtc  = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
            UpdatedUtc  = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6))
        };
        project.Validate($"business project '{project.Id}'");
        return project;
    }

    private static async Task<Project?> FindByIdAsync(SqliteConnection connection, SqliteTransaction? transaction,
                                                      string           projectId,  CancellationToken  cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, name, root_path, revision, is_deleting, created_utc, " +
                              "updated_utc FROM project WHERE id = $id;";
        command.Parameters.AddWithValue("$id", projectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadProject(reader) : null;
    }

    private static async Task<Project?> FindByRootKeyAsync(SqliteConnection connection, SqliteTransaction? transaction,
                                                           string rootKey, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, name, root_path, revision, is_deleting, created_utc, updated_utc " +
                              "FROM project WHERE root_key = $root_key;";
        command.Parameters.AddWithValue("$root_key", rootKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadProject(reader) : null;
    }

    private static async Task InsertAsync(SqliteConnection  connection, SqliteTransaction transaction, Project project,
                                          CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
        INSERT INTO project (id, name, root_path, root_key, revision, is_deleting, created_utc, updated_utc)
        VALUES ($id, $name, $root_path, $root_key, $revision, $is_deleting, $created_utc, $updated_utc);
        """;
        command.Parameters.AddWithValue("$id", project.Id);
        command.Parameters.AddWithValue("$name", project.Name);
        command.Parameters.AddWithValue("$root_path", project.PrimaryRoot);
        command.Parameters.AddWithValue("$root_key", RootKey(project.PrimaryRoot));
        command.Parameters.AddWithValue("$revision", project.Revision);
        command.Parameters.AddWithValue("$is_deleting", project.IsDeleting ? 1 : 0);
        command.Parameters.AddWithValue("$created_utc", project.CreatedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$updated_utc", project.UpdatedUtc.ToUnixTimeMilliseconds());
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new ProjectStoreException($"Project root '{project.PrimaryRoot}' is already registered " +
                                            $"in the business catalog of this store.", ex);
        }
    }

    private static async Task UpdateAsync(SqliteConnection  connection, SqliteTransaction transaction, Project project,
                                          CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
        UPDATE project
        SET name = $name, revision = $revision, is_deleting = $is_deleting, updated_utc = $updated_utc
        WHERE id = $id;
        """;
        command.Parameters.AddWithValue("$name", project.Name);
        command.Parameters.AddWithValue("$revision", project.Revision);
        command.Parameters.AddWithValue("$is_deleting", project.IsDeleting ? 1 : 0);
        command.Parameters.AddWithValue("$updated_utc", project.UpdatedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", project.Id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

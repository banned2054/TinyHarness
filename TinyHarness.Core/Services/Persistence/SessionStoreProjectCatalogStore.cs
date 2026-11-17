using System.Data;
using Microsoft.Data.Sqlite;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Project;

namespace TinyHarness.Core.Services.Persistence;

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
        var       root      = ProjectRoots.Normalize(workspaceRoot);
        var       rootKey   = RootKey(root);
        var       timestamp = utcNow ?? DateTimeOffset.UtcNow;
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
                                                                  cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, false);
        var existing = await FindByRootKeyAsync(connection, transaction, rootKey, cancellationToken)
           .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.IsDeleting)
                throw new ProjectStoreException(
                                                $"Project '{existing.Id}' for root '{root}' is being deleted; wait for deletion to finish before creating a session.");
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
    ///     <para>Renames with the caller's revision; a stale baseline, a missing project, or a deleting project is refused explicitly.</para>
    /// </summary>
    public static async Task<Project> RenameAsync(
        string storeDirectory, string projectId, string newName, long expectedRevision, DateTimeOffset? utcNow,
        CancellationToken cancellationToken)
    {
        var       name = Project.ValidateName(newName);
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
                                                                  cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, false);
        var project = await FindByIdAsync(connection, transaction, projectId, cancellationToken)
                         .ConfigureAwait(false)
                      ?? throw new ProjectStoreException(
                                                         $"Project '{projectId}' was not found in the business catalog of '{Path.GetFullPath(storeDirectory)}'.");
        if (project.IsDeleting)
            throw new ProjectStoreException($"Project '{projectId}' is being deleted and cannot be renamed.");
        if (project.Revision != expectedRevision)
            throw new ProjectStoreException(
                                            $"Project '{projectId}' changed while it was being edited (stored revision {project.Revision}, expected {expectedRevision}); reload it and retry.");

        var renamed = project with
        {
            Name       = name,
            Revision   = project.Revision + 1,
            UpdatedUtc = utcNow ?? DateTimeOffset.UtcNow
        };
        await UpdateAsync(connection, transaction, renamed, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return renamed;
    }

    /// <summary>
    ///     <para>在事务内把项目标记为删除中并递增修订号；已标记项目直接返回，便于中断后继续。</para>
    ///     <para>Marks a project as deleting and increments its revision inside a transaction; an already marked project is returned so interruption can resume.</para>
    /// </summary>
    public static async Task<Project> BeginDeletionAsync(
        string storeDirectory, string projectId, long expectedRevision, DateTimeOffset? utcNow,
        CancellationToken cancellationToken)
    {
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
                                                                  cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, false);
        var project = await FindByIdAsync(connection, transaction, projectId, cancellationToken)
                         .ConfigureAwait(false)
                      ?? throw new ProjectStoreException(
                                                         $"Project '{projectId}' was not found in the business catalog of '{Path.GetFullPath(storeDirectory)}'.");
        if (project.IsDeleting)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return project;
        }

        if (project.Revision != expectedRevision)
            throw new ProjectStoreException(
                                            $"Project '{projectId}' changed while deletion was being confirmed (stored revision {project.Revision}, expected {expectedRevision}); reload it and retry.");

        var deleting = project with
        {
            IsDeleting = true,
            Revision   = project.Revision + 1,
            UpdatedUtc = utcNow ?? DateTimeOffset.UtcNow
        };
        await UpdateAsync(connection, transaction, deleting, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleting;
    }

    /// <summary>
    ///     <para>从业务库移除已标记删除的项目；归属外键会拒绝仍有会话指向的项目。</para>
    ///     <para>Removes a project marked for deletion; the placement foreign key refuses a project that sessions still reference.</para>
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
            throw new ProjectStoreException(
                                            $"Project '{projectId}' changed while deletion was being completed (stored revision {project.Revision}, expected {expectedRevision}); reload and retry.");

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
            throw new ProjectStoreException(
                                            $"Project '{projectId}' is still referenced by session placements and cannot be removed; unassign every session first.",
                                            ex);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>在给定事务内用导入快照整体替换项目定义；只由迁移引擎调用。</para>
    ///     <para>Replaces project definitions with the imported snapshot inside the given transaction; used only by the migration engine.</para>
    /// </summary>
    internal static async Task ReplaceAllAsync(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<Project> projects,
        CancellationToken cancellationToken)
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
            throw new SessionStoreMigrationException(
                                                    "The project catalog cannot be imported while session placements still reference projects; " +
                                                    "the source order was changed and the migration must be restarted from organization state.",
                                                    SessionStoreFailureKind.Conflict, ex);
        }

        foreach (var project in projects)
            await InsertAsync(connection, transaction, project, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>返回项目的平台比较键：Windows 使用大写不变形式，其他平台使用规范化路径。</para>
    ///     <para>Returns the platform comparison key of a root: uppercase invariant on Windows and the normalized path elsewhere.</para>
    /// </summary>
    internal static string RootKey(string normalizedRoot) =>
        OperatingSystem.IsWindows() ? normalizedRoot.ToUpperInvariant() : normalizedRoot;

    private static async Task<ProjectCatalog> LoadCoreAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT id, name, root_path, revision, is_deleting, created_utc, updated_utc FROM project " +
            "ORDER BY created_utc, id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var projects = new List<Project>();
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

    private static async Task<Project?> FindByIdAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string projectId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT id, name, root_path, revision, is_deleting, created_utc, updated_utc FROM project WHERE id = $id;";
        command.Parameters.AddWithValue("$id", projectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadProject(reader) : null;
    }

    private static async Task<Project?> FindByRootKeyAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string rootKey,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT id, name, root_path, revision, is_deleting, created_utc, updated_utc FROM project " +
            "WHERE root_key = $root_key;";
        command.Parameters.AddWithValue("$root_key", rootKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadProject(reader) : null;
    }

    private static async Task InsertAsync(
        SqliteConnection connection, SqliteTransaction transaction, Project project,
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
            throw new ProjectStoreException(
                                            $"Project root '{project.PrimaryRoot}' is already registered in the business catalog of this store.",
                                            ex);
        }
    }

    private static async Task UpdateAsync(
        SqliteConnection connection, SqliteTransaction transaction, Project project,
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

using System.Text.Json;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Project;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Cli.Commands;

/// <summary>
///     <para>
///         NativeAOT 离线 smoke 中的业务库证明：在隔离临时目录里构造旧文件与会话日志，执行真实迁移、业务库读写、
///         进程内重开、备份、校验与恢复，随后断言并把所有临时目录清理掉。
///     </para>
///     <para>
///         The business-database proof inside the NativeAOT offline smoke: isolated temporary directories hold legacy
///         files and session logs, then the real migration, business read/write, in-process reopen, backup,
///         verification, and restore run and are asserted before every temporary directory is removed.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         该路径刻意走发布产物中的真实入口（迁移引擎、组织快照存储、备份 API），不使用测试专用接缝；失败时抛出
///         带具体期望值的异常，由既有 smoke 失败路径转换为非零退出码。它不联网、不调用模型、不读取凭据。
///     </para>
///     <para>
///         This path deliberately uses the real entry points inside the published artifact (migration engine,
///         organization snapshot store, backup API) instead of test-only seams; a failure throws with the expected
///         value and the existing smoke failure path turns it into a non-zero exit code. It uses no network, calls no
///         model, and reads no credentials.
///     </para>
/// </remarks>
internal static class SessionStoreOfflineSmoke
{
    private const string WorkDirectoryName = "session-store-smoke";

    /// <summary>
    ///     <para>在给定 smoke 根目录下运行一次完整的迁移、读写、备份与恢复验证，并打印一行结果。</para>
    ///     <para>Runs one full migration, read/write, backup, and restore verification under the given smoke root and prints one result line.</para>
    /// </summary>
    /// <param name="root">
    ///     <para>本次 smoke 的临时根目录；业务库验证使用其中的固定子目录并在结束时清理。</para>
    ///     <para>The temporary root of this smoke run; the verification uses and then clears a fixed subdirectory inside it.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消文件写入、迁移、备份与恢复。</para>
    ///     <para>Cancels fixture writes, migration, backup, and restore.</para>
    /// </param>
    public static async Task RunAsync(string root, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var workDirectory = Path.Combine(Path.GetFullPath(root), WorkDirectoryName);
        try
        {
            Directory.CreateDirectory(workDirectory);
            var storeDirectory  = Path.Combine(workDirectory, "store");
            var backupDirectory = Path.Combine(workDirectory, "backup");
            var workspaceRoot   = Path.GetFullPath(Path.Combine(workDirectory, "workspace"));
            Directory.CreateDirectory(storeDirectory);

            var projectId       = Guid.NewGuid().ToString("N");
            var archivedId      = Guid.NewGuid().ToString("N");
            var boundSessionId  = Guid.NewGuid().ToString("N");
            var pendingSessionId = Guid.NewGuid().ToString("N");
            var projectCatalogPath = ProjectStore.FilePath(storeDirectory);
            var pinStatePath       = SessionStorePinStore.FilePath(storeDirectory);
            var archiveStatePath   = SessionStoreArchiveStore.FilePath(storeDirectory);
            var sidebarPath        = Path.Combine(workDirectory, "desktop-sidebar.json");
            WriteProjectCatalog(projectCatalogPath, projectId, workspaceRoot);
            WritePinState(pinStatePath, projectId, archivedId);
            WriteArchiveState(archiveStatePath, archivedId);
            WriteSidebarPreferences(sidebarPath, storeDirectory, projectId, boundSessionId);
            await WriteSessionLogAsync(storeDirectory, boundSessionId, workspaceRoot, projectId, cancellationToken)
               .ConfigureAwait(false);
            await WriteSessionLogAsync(storeDirectory, pendingSessionId, workspaceRoot, null, cancellationToken)
               .ConfigureAwait(false);

            var legacyFiles  = new[] { projectCatalogPath, pinStatePath, archiveStatePath, sidebarPath };
            var legacyBefore = legacyFiles.ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);

            var result = await SessionStoreMigration.MigrateAsync(new SessionStoreMigrationOptions(storeDirectory)
                                                                 {
                                                                     SidebarPreferencesPath = sidebarPath
                                                                 },
                                                                 cancellationToken).ConfigureAwait(false);
            Require(!result.AlreadyCompleted, "the first migration must import");
            Require(result.Batches.Count == SessionStoreMigrationMarker.BatchOrder.Length,
                    $"the migration must report {SessionStoreMigrationMarker.BatchOrder.Length} batches");
            Require(result.Batches.All(batch => batch.SwitchedThisRun), "every batch must switch in the first run");
            Require(result.Batches[0].ImportedRows == 6, "organization import must bring six rows");
            Require(result.Batches[1].ImportedRows == 1, "project import must bring one project");
            Require(result.Batches[2].ImportedRows == 1, "placement import must bring the bound session row");

            var inspection = await SessionStoreMigration.InspectAsync(storeDirectory, cancellationToken)
                                                       .ConfigureAwait(false);
            Require(inspection.Status == SessionStoreMigrationStatus.Completed,
                    $"inspection must report Completed, not {inspection.Status}");

            var organization = await SessionStoreOrganizationStore.LoadAsync(storeDirectory, cancellationToken)
                                                                 .ConfigureAwait(false);
            Require(organization.ArchivedSessionIds.Contains(archivedId, StringComparer.Ordinal),
                    "imported archive must contain the legacy archived session");
            Require(organization.PinnedProjectIds.Contains(projectId, StringComparer.Ordinal),
                    "imported pins must contain the legacy pinned project");
            Require(organization.PinnedSessionIds.SequenceEqual([archivedId]),
                    "imported session pins must preserve the pin file order");
            Require(organization.ManualProjectOrder.SequenceEqual([projectId]),
                    "imported manual project order must come from the sidebar preferences");
            Require(organization.ManualSessionOrder.SequenceEqual([boundSessionId]),
                    "imported manual session order must come from the sidebar preferences");
            Require(organization.CollapsedGroupKeys.SequenceEqual(["smoke-group"]),
                    "imported collapsed groups must come from the sidebar preferences");

            var placements = await SessionStorePlacementStore.LoadAllAsync(storeDirectory, cancellationToken)
                                                            .ConfigureAwait(false);
            Require(placements.TryGetValue(boundSessionId, out var boundPlacement) &&
                    boundPlacement is { State: SessionProjectAssignmentState.Project, ProjectId: var placementProjectId } &&
                    string.Equals(placementProjectId, projectId, StringComparison.Ordinal),
                    "the project-bound session must be imported as assigned");
            Require(!placements.ContainsKey(pendingSessionId),
                    "an unbound legacy session must stay without a placement row");

            await SessionStoreOrganizationStore.SetSessionArchivedAsync(storeDirectory, boundSessionId, true,
                                                                        cancellationToken).ConfigureAwait(false);
            // 新实例重开：写后读必须走同一业务库，而不是进程内缓存。
            var reopened = await SessionStoreOrganizationStore.LoadAsync(storeDirectory, cancellationToken)
                                                             .ConfigureAwait(false);
            Require(reopened.ArchivedSessionIds.Contains(boundSessionId, StringComparer.Ordinal),
                    "a business write must persist across a reopened snapshot store");

            var backupInfo = await SessionStoreBackup.CreateAsync(storeDirectory, backupDirectory, cancellationToken)
                                                     .ConfigureAwait(false);
            Require(File.Exists(backupInfo.Path), "backup file must exist");
            Require(backupInfo.StoreIdentity == inspection.StoreIdentity,
                    "the backup must carry the store identity recorded by the migration marker");
            Require(backupInfo.SchemaVersion == SessionStoreDatabase.CurrentSchemaVersion,
                    "the backup must use the current business schema version");
            var backupVerification = await SessionStoreBackup.VerifyAsync(backupDirectory, cancellationToken)
                                                            .ConfigureAwait(false);
            Require(backupVerification.Status == SessionStoreBackupVerificationStatus.Valid,
                    $"the backup must verify, not {backupVerification.Status}");

            DeleteBusinessDatabase(storeDirectory);
            await SessionStoreBackup.RestoreAsync(storeDirectory, backupDirectory, cancellationToken)
                                   .ConfigureAwait(false);
            var restored = await SessionStoreOrganizationStore.LoadAsync(storeDirectory, cancellationToken)
                                                             .ConfigureAwait(false);
            Require(restored.ArchivedSessionIds.Contains(boundSessionId, StringComparer.Ordinal),
                    "the restored business database must still hold the written archive state");
            foreach (var (path, before) in legacyBefore)
                Require(File.ReadAllBytes(path).SequenceEqual(before),
                        $"legacy file '{Path.GetFileName(path)}' must stay byte-identical through migration and restore");
            Require(Directory.EnumerateFiles(backupDirectory).Any(),
                    "the backup source directory must stay untouched by the restore");

            await Console.Out
                         .WriteLineAsync("session store: verified migration, read/write, reopen, backup and restore")
                         .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    /// <summary>
    ///     <para>写入一个合法的旧项目目录册。</para>
    ///     <para>Writes one valid legacy project catalog.</para>
    /// </summary>
    private static void WriteProjectCatalog(string path, string projectId, string workspaceRoot)
    {
        var now = DateTimeOffset.UtcNow;
        var project = new Project
        {
            Id          = projectId,
            Name        = "Smoke project",
            Roots       = [new ProjectRoot(workspaceRoot)],
            PrimaryRoot = workspaceRoot,
            Revision    = 1,
            CreatedUtc  = now,
            UpdatedUtc  = now
        };
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new ProjectCatalog
        {
            Schema   = ProjectCatalog.CurrentSchema,
            Revision = 1,
            Projects = [project]
        }, ProjectJsonContext.Default.ProjectCatalog));
    }

    /// <summary>
    ///     <para>写入一个合法的旧置顶状态文件。</para>
    ///     <para>Writes one valid legacy pin-state file.</para>
    /// </summary>
    private static void WritePinState(string path, string pinnedProjectId, string pinnedSessionId) =>
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new SessionStorePins
        {
            Schema           = SessionStorePins.CurrentSchema,
            PinnedProjectIds = [pinnedProjectId],
            PinnedSessionIds = [pinnedSessionId]
        }, SessionStorePinJsonContext.Default.SessionStorePins));

    /// <summary>
    ///     <para>写入一个合法的旧归档状态文件。</para>
    ///     <para>Writes one valid legacy archive-state file.</para>
    /// </summary>
    private static void WriteArchiveState(string path, string archivedSessionId) =>
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new SessionStoreArchives
        {
            Schema             = SessionStoreArchives.CurrentSchema,
            ArchivedSessionIds = [archivedSessionId]
        }, SessionStoreArchiveJsonContext.Default.SessionStoreArchives));

    /// <summary>
    ///     <para>写入桌面侧栏偏好中的目标 store 条目（手动顺序与折叠分组）。</para>
    ///     <para>Writes the target store entry of the desktop sidebar preferences (manual order and collapsed groups).</para>
    /// </summary>
    private static void WriteSidebarPreferences(
        string path, string storeDirectory, string projectId, string sessionId)
    {
        // 使用源生成的文档类型序列化夹具：不经过 JsonArray.Add<T> 的泛型实例化，NativeAOT 下无裁剪告警。
        var document = new SidebarPreferencesDocument
        {
            Schema = SessionStoreSidebarPreferencesReader.CurrentSchema,
            Stores =
            [
                new SidebarStoreDocument
                {
                    StoreDirectory      = storeDirectory,
                    ManualProjectOrder  = [projectId],
                    ManualSessionOrder  = [sessionId],
                    CollapsedGroupKeys  = ["smoke-group"],
                    LegacyPinnedProjectOrder = [],
                    LegacyPinnedSessionIds   = []
                }
            ]
        };
        File.WriteAllBytes(path,
                           JsonSerializer.SerializeToUtf8Bytes(
                               document,
                               SessionStoreSidebarPreferencesJsonContext.Default.SidebarPreferencesDocument));
    }

    /// <summary>
    ///     <para>
    ///         用真实写入器提交一条只含身份批次（以及可选项目归属）的会话日志，覆盖有绑定与无绑定两种旧日志。
    ///     </para>
    ///     <para>
    ///         Commits a session log containing only the identity batch (plus an optional project binding) through the
    ///         real writer, covering both a bound and an unbound legacy log.
    ///     </para>
    /// </summary>
    private static async Task WriteSessionLogAsync(
        string storeDirectory, string sessionId, string workspaceRoot, string? projectId,
        CancellationToken cancellationToken)
    {
        using var writer = new SessionStoreWriter(storeDirectory, sessionId);
        var model = new SessionModelIdentity("offline-smoke", "https://offline.invalid/v1",
                                             ChatApiKind.ChatCompletions);
        var created = DateTimeOffset.UtcNow;
        var session = projectId is null
            ? new ChatSession(sessionId, workspaceRoot, model, created)
            : new ChatSession(sessionId, workspaceRoot, model, created, projectId);
        var context = new ConversationContext(new ContextOptions());
        await writer.CommitAsync(session, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>删除业务库及其 WAL／SHM，模拟"迁移后业务库丢失、只能由备份恢复"的状态。</para>
    ///     <para>Deletes the business database and its WAL／SHM to simulate "lost after migration, recoverable only from backup".</para>
    /// </summary>
    private static void DeleteBusinessDatabase(string storeDirectory)
    {
        var databasePath = SessionStoreDatabase.PathFor(storeDirectory);
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            if (File.Exists(path))
                File.Delete(path);
    }

    /// <summary>
    ///     <para>断言 smoke 条件，失败时抛出带上下文的异常供 CLI 报告非零退出码。</para>
    ///     <para>Asserts a smoke condition, throwing a contextual exception that makes the CLI report a non-zero exit code.</para>
    /// </summary>
    private static void Require(bool condition, string requirement)
    {
        if (!condition)
            throw new InvalidOperationException($"Session-store smoke failed: {requirement}.");
    }

    /// <summary>
    ///     <para>尽力删除本次 smoke 创建的临时目录；清理失败不掩盖真实验证结果。</para>
    ///     <para>Best-effort deletes the temporary directory this smoke created without masking the real verification result.</para>
    /// </summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录清理失败不改变 smoke 的验证结论。
        }
    }
}

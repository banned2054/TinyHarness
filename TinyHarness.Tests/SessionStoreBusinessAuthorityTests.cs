using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Project;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         S4 业务库权威测试：迁移标记决定生产权威、三个领域存储（组织状态、项目目录册、会话归属）的读写、
///         恢复投影不被历史 JSONL 归属覆盖、初始化/解绑/删除在业务库模式下的行为，以及阻塞状态拒绝生产访问。
///     </para>
///     <para>
///         S4 business-authority tests: the migration marker decides the production authority, the three domain
///         stores (organization state, project catalog, session placement) read and write correctly, the recovery
///         projection is not overridden by historical JSONL ownership, initialization/unassignment/deletion behave in
///         database mode, and a blocked state refuses production access.
///     </para>
/// </summary>
public class SessionStoreBusinessAuthorityTests
{
    private static readonly SessionModelIdentity Model =
        new("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions);

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static async Task<Project> CreateProjectAsync(string storeDirectory, string workspaceRoot)
    {
        var project = await SessionStoreProjectCatalogStore.GetOrCreateAsync(storeDirectory, workspaceRoot, null,
                                                                            CancellationToken.None);
        return project;
    }

    /// <summary>
    ///     <para>构造一个已完成迁移的 store：业务库建库、写入库身份与迁移标识，并写下三批已切换的迁移标记。</para>
    ///     <para>Builds a fully migrated store: creates the business database, records its identity and migration id, and writes the marker with all three batches switched.</para>
    /// </summary>
    private static async Task CreateMigratedStoreAsync(string storeDirectory)
    {
        var database = new SessionStoreDatabase(storeDirectory);
        await database.InitializeAsync(CancellationToken.None);
        var migrationId = NewId();
        string? identity;
        await using (var connection = await database.OpenExistingForInternalUseAsync(CancellationToken.None))
        {
            identity = await SessionStoreDatabase.ReadStoreIdentityAsync(connection, CancellationToken.None);
            await SessionStoreDatabase.WriteMetaValueAsync(connection, null, "migration_id", migrationId,
                                                           CancellationToken.None);
        }

        var now     = DateTimeOffset.UtcNow;
        var marker  = SessionStoreMigrationMarker.Start(migrationId, now)
                                                 .WithStoreIdentity(identity ?? throw new InvalidOperationException(
                                                                     "The test database has no identity."), now)
                                                 .WithBatchSwitched(SessionStoreMigrationBatch.OrganizationState,
                                                                    "org-fingerprint", now)
                                                 .WithBatchSwitched(SessionStoreMigrationBatch.ProjectCatalog,
                                                                    "projects-fingerprint", now)
                                                 .WithBatchSwitched(SessionStoreMigrationBatch.SessionPlacement,
                                                                    "placement-fingerprint", now);
        await SessionStoreMigration.WriteMarkerAsync(storeDirectory, marker, CancellationToken.None);
    }

    private static async Task<string> CommitLegacySessionAsync(
        string storeDirectory, string workspaceRoot, string? projectId = null,
        SessionProjectAssignmentState state = SessionProjectAssignmentState.PendingInitialization)
    {
        // 归属状态与项目 ID 必须一致：给出项目 ID 而未显式要求"无项目"时按已归属提交。
        var effectiveState = state == SessionProjectAssignmentState.NoProject
            ? SessionProjectAssignmentState.NoProject
            : projectId is null
                ? SessionProjectAssignmentState.PendingInitialization
                : SessionProjectAssignmentState.Project;
        var session = new ChatSession(NewId(), workspaceRoot, Model, null, projectId, effectiveState);
        var context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        context.Append(ChatMessage.User("hello"));
        using var writer = new SessionStoreWriter(storeDirectory, session.Id);
        await writer.CommitAsync(session, context, CancellationToken.None);
        return session.Id;
    }

    private static Dictionary<string, byte[]> ReadLogBytes(string storeDirectory)
    {
        var bytesByFile = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(storeDirectory, "*" + SessionStoreWriter.LogFileSuffix))
            bytesByFile[Path.GetFileName(path)] = File.ReadAllBytes(path);
        return bytesByFile;
    }

    private static void AssertLogBytesUnchanged(Dictionary<string, byte[]> before, string storeDirectory)
    {
        var after = ReadLogBytes(storeDirectory);
        Assert.That(after.Keys, Is.EquivalentTo(before.Keys));
        foreach (var pair in before) Assert.That(after[pair.Key], Is.EqualTo(pair.Value), $"Log '{pair.Key}' changed.");
    }

    [Test]
    public async Task Authority_WithoutMarker_StaysLegacyAndWritesLegacyFiles()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();

        var resolution = await SessionStoreAuthority.ResolveAsync(store.Root, CancellationToken.None);
        Assert.That(resolution.Mode, Is.EqualTo(SessionStoreMode.Legacy));
        Assert.That(resolution.Status, Is.EqualTo(SessionStoreMigrationStatus.NotStarted));

        var project = await ProjectStore.GetOrCreateAsync(ProjectStore.FilePath(store.Root), workspace.Root, null,
                                                          CancellationToken.None);
        Assert.That(File.Exists(ProjectStore.FilePath(store.Root)), Is.True, "Legacy mode must keep writing projects.json.");
        Assert.That(Directory.GetFiles(store.Root, SessionStoreDatabase.DatabaseFileName), Is.Empty,
                    "Legacy mode must not create the business database.");

        var pins = await SessionStorePinStore.SetProjectPinnedAsync(store.Root, project.Id, true,
                                                                    CancellationToken.None);
        Assert.That(pins.PinnedProjectIds, Does.Contain(project.Id));
        Assert.That(File.Exists(SessionStorePinStore.FilePath(store.Root)), Is.True);
    }

    [Test]
    public async Task Authority_PartialMarker_BlocksProductionAndDoesNotFallBack()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        var       database  = new SessionStoreDatabase(store.Root);
        await database.InitializeAsync(CancellationToken.None);

        var now     = DateTimeOffset.UtcNow;
        var started = SessionStoreMigrationMarker.Start(NewId(), now);
        await SessionStoreMigration.WriteMarkerAsync(store.Root, started, CancellationToken.None);

        var resolution = await SessionStoreAuthority.ResolveAsync(store.Root, CancellationToken.None);
        Assert.That(resolution.Mode, Is.EqualTo(SessionStoreMode.Blocked));
        Assert.That(resolution.Status, Is.EqualTo(SessionStoreMigrationStatus.InProgress));

        var projectError = await TestAssert.ThrowsAsync<SessionStoreMigrationException>(
                                                                                () => ProjectStore.LoadAsync(
                                                                                     ProjectStore.FilePath(store.Root),
                                                                                     CancellationToken.None));
        Assert.That(projectError.Kind, Is.EqualTo(SessionStoreFailureKind.Conflict));
        Assert.That(File.Exists(ProjectStore.FilePath(store.Root)), Is.False,
                    "A blocked store must never fall back to legacy files.");

        var pinError = await TestAssert.ThrowsAsync<SessionStoreMigrationException>(
                                                                                    () => SessionStorePinStore.LoadAsync(
                                                                                        store.Root, CancellationToken.None));
        Assert.That(pinError.Kind, Is.EqualTo(SessionStoreFailureKind.Conflict));
    }

    [Test]
    public async Task Authority_CompletedMarker_IsMigratedAndDamagedMarkerIsBlocked()
    {
        using var store = new TestTempDir();
        await CreateMigratedStoreAsync(store.Root);

        var resolution = await SessionStoreAuthority.ResolveAsync(store.Root, CancellationToken.None);
        Assert.That(resolution.Mode, Is.EqualTo(SessionStoreMode.Migrated));
        Assert.That(resolution.Status, Is.EqualTo(SessionStoreMigrationStatus.Completed));
        Assert.That(await SessionStoreAuthority.IsMigratedAsync(store.Root, CancellationToken.None), Is.True);

        await File.WriteAllTextAsync(SessionStoreMigration.MarkerPathFor(store.Root), "{ broken", CancellationToken.None);
        var damaged = await SessionStoreAuthority.ResolveAsync(store.Root, CancellationToken.None);
        Assert.That(damaged.Mode, Is.EqualTo(SessionStoreMode.Blocked));
        Assert.That(damaged.Status, Is.EqualTo(SessionStoreMigrationStatus.MarkerDamaged));
    }

    [Test]
    public async Task Organization_BusinessStoreRoundTripsAndNeverWritesLegacyFiles()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        await CreateMigratedStoreAsync(store.Root);
        var project = await CreateProjectAsync(store.Root, workspace.Root);
        var session = NewId();

        await SessionStoreOrganizationStore.SetSessionArchivedAsync(store.Root, session, true, CancellationToken.None);
        await SessionStoreOrganizationStore.SetProjectPinnedAsync(store.Root, project.Id, true, CancellationToken.None);
        await SessionStoreOrganizationStore.SetSessionPinnedAsync(store.Root, session, true, CancellationToken.None);
        await SessionStoreOrganizationStore.SetManualProjectOrderAsync(store.Root, [project.Id],
                                                                       CancellationToken.None);
        await SessionStoreOrganizationStore.SetManualSessionOrderAsync(store.Root, [session], CancellationToken.None);
        await SessionStoreOrganizationStore.SetGroupCollapsedAsync(store.Root, "project:" + project.Id, true,
                                                                   CancellationToken.None);

        var state = await SessionStoreOrganizationStore.LoadAsync(store.Root, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.ArchivedSessionIds, Is.EqualTo(new[] { session }));
            Assert.That(state.PinnedProjectIds, Is.EqualTo(new[] { project.Id }));
            Assert.That(state.PinnedSessionIds, Is.EqualTo(new[] { session }));
            Assert.That(state.ManualProjectOrder, Is.EqualTo(new[] { project.Id }));
            Assert.That(state.ManualSessionOrder, Is.EqualTo(new[] { session }));
            Assert.That(state.CollapsedGroupKeys, Is.EqualTo(new[] { "project:" + project.Id }));
        });

        // 既有文件存储在业务库模式下不得被写入，也不得创建。
        Assert.That(File.Exists(SessionStorePinStore.FilePath(store.Root)), Is.False,
                    "Pin state must not be written to legacy files after migration.");
        Assert.That(File.Exists(SessionStoreArchiveStore.FilePath(store.Root)), Is.False,
                    "Archive state must not be written to legacy files after migration.");

        // 领域外观读到的就是业务库值。
        Assert.That((await SessionStoreArchiveStore.LoadAsync(store.Root, CancellationToken.None)).ArchivedSessionIds,
                    Is.EqualTo(new[] { session }));
        Assert.That((await SessionStorePinStore.LoadAsync(store.Root, CancellationToken.None)).PinnedProjectIds,
                    Is.EqualTo(new[] { project.Id }));

        // 取消置顶/归档与手动顺序清理同样只写业务库。
        await SessionStoreOrganizationStore.SetProjectPinnedAsync(store.Root, project.Id, false,
                                                                  CancellationToken.None);
        await SessionStoreOrganizationStore.SetSessionArchivedAsync(store.Root, session, false,
                                                                    CancellationToken.None);
        await SessionStoreOrganizationStore.RemoveSessionAsync(store.Root, session, CancellationToken.None);
        var cleared = await SessionStoreOrganizationStore.LoadAsync(store.Root, CancellationToken.None);
        Assert.That(cleared.ArchivedSessionIds, Is.Empty);
        Assert.That(cleared.PinnedProjectIds, Is.Empty);
        Assert.That(cleared.PinnedSessionIds, Is.Empty);
        Assert.That(cleared.ManualSessionOrder, Is.Empty);
    }

    [Test]
    public async Task Placement_ThreeStatesRoundTripAndMissingRowIsNotNoProject()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        await CreateMigratedStoreAsync(store.Root);
        var project = await CreateProjectAsync(store.Root, workspace.Root);
        var session = NewId();

        var pending = await SessionStorePlacementStore.GetAsync(store.Root, session, CancellationToken.None);
        Assert.That(pending.State, Is.EqualTo(SessionProjectAssignmentState.PendingInitialization));
        Assert.That(pending.ProjectId, Is.Null);

        await SessionStorePlacementStore.SetProjectAsync(store.Root, session, project.Id, CancellationToken.None);
        var assigned = await SessionStorePlacementStore.GetAsync(store.Root, session, CancellationToken.None);
        Assert.That(assigned.State, Is.EqualTo(SessionProjectAssignmentState.Project));
        Assert.That(assigned.ProjectId, Is.EqualTo(project.Id));

        await SessionStorePlacementStore.SetNoProjectAsync(store.Root, session, CancellationToken.None);
        var noProject = await SessionStorePlacementStore.GetAsync(store.Root, session, CancellationToken.None);
        Assert.That(noProject.State, Is.EqualTo(SessionProjectAssignmentState.NoProject));
        Assert.That(noProject.ProjectId, Is.Null);

        await SessionStorePlacementStore.RemoveSessionAsync(store.Root, session, CancellationToken.None);
        Assert.That((await SessionStorePlacementStore.GetAsync(store.Root, session, CancellationToken.None)).State,
                    Is.EqualTo(SessionProjectAssignmentState.PendingInitialization));

        var all = await SessionStorePlacementStore.LoadAllAsync(store.Root, CancellationToken.None);
        Assert.That(all, Is.Empty);
    }

    [Test]
    public async Task Placement_UnassignProjectTurnsAssignedRowsIntoExplicitNoProject()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        await CreateMigratedStoreAsync(store.Root);
        var project = await CreateProjectAsync(store.Root, workspace.Root);
        var first   = NewId();
        var second  = NewId();
        await SessionStorePlacementStore.SetProjectAsync(store.Root, first, project.Id, CancellationToken.None);
        await SessionStorePlacementStore.SetProjectAsync(store.Root, second, project.Id, CancellationToken.None);

        var unassigned = await SessionStoreUnassignmentTestFacade.UnassignAsync(store.Root, project.Id);
        Assert.That(unassigned.Outcomes, Has.Count.EqualTo(2));
        Assert.That(unassigned.Outcomes.All(outcome => outcome.Status == SessionProjectUnassignmentStatus.Unassigned),
                    Is.True);
        Assert.That(unassigned.IsComplete, Is.True);

        foreach (var session in new[] { first, second })
        {
            var placement = await SessionStorePlacementStore.GetAsync(store.Root, session, CancellationToken.None);
            Assert.That(placement.State, Is.EqualTo(SessionProjectAssignmentState.NoProject));
        }

        Assert.That(await SessionStorePlacementStore.ListProjectSessionIdsAsync(store.Root, project.Id,
                                                                                CancellationToken.None), Is.Empty);
    }

    [Test]
    public async Task Reader_HistoricalJournalOwnershipNeverOverridesBusinessPlacement()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        var       legacyProjectId = NewId();
        var       session  = await CommitLegacySessionAsync(store.Root, workspace.Root, legacyProjectId);
        var       logPath  = Path.Combine(store.Root, session + SessionStoreWriter.LogFileSuffix);
        var       logBytes = await File.ReadAllBytesAsync(logPath, CancellationToken.None);

        await CreateMigratedStoreAsync(store.Root);

        // 业务库明确无项目：历史日志里的 project-binding 不再生效。
        await SessionStorePlacementStore.SetNoProjectAsync(store.Root, session, CancellationToken.None);
        var restored = await SessionStoreReader.ReadAsync(store.Root, session, CancellationToken.None);
        Assert.That(restored, Is.Not.Null);
        Assert.That(restored!.ProjectAssignmentState, Is.EqualTo(SessionProjectAssignmentState.NoProject));
        Assert.That(restored.ProjectId, Is.Null);

        // 业务库归属另一个项目：以业务库为准。
        var       otherWorkspace = new TestTempDir();
        using var _              = otherWorkspace;
        var       project        = await CreateProjectAsync(store.Root, otherWorkspace.Root);
        await SessionStorePlacementStore.SetProjectAsync(store.Root, session, project.Id, CancellationToken.None);
        var rebound = await SessionStoreReader.ReadAsync(store.Root, session, CancellationToken.None);
        Assert.That(rebound!.ProjectAssignmentState, Is.EqualTo(SessionProjectAssignmentState.Project));
        Assert.That(rebound.ProjectId, Is.EqualTo(project.Id));

        // 缺行表示尚未初始化，不能被日志里的历史归属填成已绑定。
        await SessionStorePlacementStore.RemoveSessionAsync(store.Root, session, CancellationToken.None);
        var pending = await SessionStoreReader.ReadAsync(store.Root, session, CancellationToken.None);
        Assert.That(pending!.ProjectAssignmentState,
                    Is.EqualTo(SessionProjectAssignmentState.PendingInitialization));
        Assert.That(pending.ProjectId, Is.Null);

        // 全程不追加日志：历史 JSONL 字节不变。
        Assert.That(await File.ReadAllBytesAsync(logPath, CancellationToken.None), Is.EqualTo(logBytes));
    }

    [Test]
    public async Task ProjectCatalog_BusinessStoreReusesRootsAndEnforcesRevisionsAndPlacementRestriction()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        await CreateMigratedStoreAsync(store.Root);

        var catalog = await ProjectStore.LoadAsync(ProjectStore.FilePath(store.Root), CancellationToken.None);
        Assert.That(catalog.Projects, Is.Empty);

        var first  = await ProjectStore.GetOrCreateAsync(ProjectStore.FilePath(store.Root), workspace.Root, null,
                                                         CancellationToken.None);
        var second = await ProjectStore.GetOrCreateAsync(ProjectStore.FilePath(store.Root),
                                                         workspace.Root + Path.DirectorySeparatorChar, null,
                                                         CancellationToken.None);
        Assert.That(second.Id, Is.EqualTo(first.Id), "The same root must reuse one project row.");
        Assert.That((await ProjectStore.LoadAsync(ProjectStore.FilePath(store.Root), CancellationToken.None)).Projects,
                    Has.Count.EqualTo(1));
        Assert.That(File.Exists(ProjectStore.FilePath(store.Root)), Is.False,
                    "Business catalog mode must not write projects.json.");

        var renamed = await ProjectStore.RenameAsync(ProjectStore.FilePath(store.Root), first.Id, "Renamed",
                                                     first.Revision, null, CancellationToken.None);
        Assert.That(renamed.Name, Is.EqualTo("Renamed"));
        Assert.That(renamed.Revision, Is.EqualTo(first.Revision + 1));
        await TestAssert.ThrowsAsync<ProjectStoreException>(() => ProjectStore.RenameAsync(ProjectStore.FilePath(store.Root),
                                                                                first.Id, "Stale", first.Revision,
                                                                                null, CancellationToken.None));

        var deleting = await ProjectStore.BeginDeletionAsync(ProjectStore.FilePath(store.Root), first.Id,
                                                             renamed.Revision, null, CancellationToken.None);
        Assert.That(deleting.IsDeleting, Is.True);

        // 归属外键把"先解绑后删除"变成数据库不变量。
        var session = NewId();
        await SessionStorePlacementStore.SetProjectAsync(store.Root, session, first.Id, CancellationToken.None);
        await TestAssert.ThrowsAsync<ProjectStoreException>(() => ProjectStore.CompleteDeletionAsync(
                                                                        ProjectStore.FilePath(store.Root), first.Id,
                                                                        deleting.Revision, CancellationToken.None));

        await SessionStorePlacementStore.SetNoProjectAsync(store.Root, session, CancellationToken.None);
        await ProjectStore.CompleteDeletionAsync(ProjectStore.FilePath(store.Root), first.Id, deleting.Revision,
                                                 CancellationToken.None);
        Assert.That((await ProjectStore.LoadAsync(ProjectStore.FilePath(store.Root), CancellationToken.None)).Projects,
                    Is.Empty);
    }

    [Test]
    public async Task Initializer_BusinessModeBindsPendingSessionsWithoutTouchingLogs()
    {
        using var       store        = new TestTempDir();
        using var       workspace    = new TestTempDir();
        using var       legacyStoreA = new TestTempDir();
        var             legacyLog    = NewId();
        var       unboundLog  = await CommitLegacySessionAsync(store.Root, workspace.Root);
        var       noProjectLog = await CommitLegacySessionAsync(store.Root, workspace.Root, null,
                                                                SessionProjectAssignmentState.NoProject);
        var       boundLog = await CommitLegacySessionAsync(store.Root, legacyStoreA.Root, legacyLog);
        var       before   = ReadLogBytes(store.Root);

        await CreateMigratedStoreAsync(store.Root);
        var legacyProject = await CreateProjectAsync(store.Root, legacyStoreA.Root);
        await SessionStorePlacementStore.SetProjectAsync(store.Root, boundLog, legacyProject.Id,
                                                         CancellationToken.None);
        await SessionStorePlacementStore.SetNoProjectAsync(store.Root, noProjectLog, CancellationToken.None);

        var result = await SessionProjectInitializer.InitializeAsync(store.Root);
        Assert.That(result.Cancelled, Is.False);
        Assert.That(result.HasMore, Is.False);

        var outcomes = result.Outcomes.ToDictionary(outcome => outcome.SessionId, StringComparer.Ordinal);
        Assert.That(outcomes[unboundLog].Status, Is.EqualTo(SessionProjectBindingStatus.Bound));
        Assert.That(outcomes[noProjectLog].Status, Is.EqualTo(SessionProjectBindingStatus.NoProject));
        Assert.That(outcomes[boundLog].Status, Is.EqualTo(SessionProjectBindingStatus.AlreadyBound));
        Assert.That(outcomes[boundLog].ProjectId, Is.EqualTo(legacyProject.Id));

        var binding = await SessionStorePlacementStore.GetAsync(store.Root, unboundLog, CancellationToken.None);
        Assert.That(binding.State, Is.EqualTo(SessionProjectAssignmentState.Project));
        var projects = (await ProjectStore.LoadAsync(ProjectStore.FilePath(store.Root), CancellationToken.None))
           .Projects;
        var boundProject = projects.Single(project => project.Id == binding.ProjectId);
        Assert.That(ProjectRoots.AreEqual(boundProject.PrimaryRoot, workspace.Root), Is.True);

        AssertLogBytesUnchanged(before, store.Root);
        Assert.That(File.Exists(ProjectStore.FilePath(store.Root)), Is.False,
                    "Business mode must not recreate projects.json while initializing.");
    }

    [Test]
    public async Task Initializer_ConcurrentInsertNeverOverwritesExistingPlacement()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        using var second    = new TestTempDir();
        await CreateMigratedStoreAsync(store.Root);
        var firstProject  = await CreateProjectAsync(store.Root, workspace.Root);
        var secondProject = await CreateProjectAsync(store.Root, second.Root);
        var session       = NewId();

        Assert.That(await SessionStorePlacementStore.TryInitializeProjectAsync(store.Root, session,
                                                                                firstProject.Id, CancellationToken.None),
                    Is.True);
        Assert.That(await SessionStorePlacementStore.TryInitializeProjectAsync(store.Root, session,
                                                                                secondProject.Id, CancellationToken.None),
                    Is.False);
        var placement = await SessionStorePlacementStore.GetAsync(store.Root, session, CancellationToken.None);
        Assert.That(placement.ProjectId, Is.EqualTo(firstProject.Id));
    }

    [Test]
    public async Task Writer_FirstCommitInMigratedStoreWritesPlacementAndOmitsJournalOwnership()
    {
        using var store       = new TestTempDir();
        using var workspace   = new TestTempDir();
        using var otherRoot   = new TestTempDir();
        await CreateMigratedStoreAsync(store.Root);
        var project = await CreateProjectAsync(store.Root, workspace.Root);
        var session = new ChatSession(NewId(), workspace.Root, Model, null, project.Id,
                                      SessionProjectAssignmentState.Project);
        var context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        context.Append(ChatMessage.User("hello"));
        using (var writer = new SessionStoreWriter(store.Root, session.Id))
            await writer.CommitAsync(session, context, CancellationToken.None);

        var placement = await SessionStorePlacementStore.GetAsync(store.Root, session.Id, CancellationToken.None);
        Assert.That(placement.State, Is.EqualTo(SessionProjectAssignmentState.Project));
        Assert.That(placement.ProjectId, Is.EqualTo(project.Id));

        var log = await File.ReadAllTextAsync(Path.Combine(store.Root, session.Id + SessionStoreWriter.LogFileSuffix),
                                              CancellationToken.None);
        Assert.That(log, Does.Not.Contain("project-binding"),
                    "After migration ownership must not be appended to the journal.");
        Assert.That(log, Does.Contain("\"identity\""));

        // 明确无项目的新会话同样只写业务库归属行。
        var noProjectSession = new ChatSession(NewId(), otherRoot.Root, Model, null, null,
                                               SessionProjectAssignmentState.NoProject);
        var noProjectContext = new ConversationContext();
        noProjectContext.Append(ChatMessage.System("system"));
        using (var writer = new SessionStoreWriter(store.Root, noProjectSession.Id))
            await writer.CommitAsync(noProjectSession, noProjectContext, CancellationToken.None);
        var noProjectPlacement = await SessionStorePlacementStore.GetAsync(store.Root, noProjectSession.Id,
                                                                           CancellationToken.None);
        Assert.That(noProjectPlacement.State, Is.EqualTo(SessionProjectAssignmentState.NoProject));
        var noProjectLog =
            await File.ReadAllTextAsync(Path.Combine(store.Root,
                                                     noProjectSession.Id + SessionStoreWriter.LogFileSuffix),
                                        CancellationToken.None);
        Assert.That(noProjectLog, Does.Not.Contain("project-assignment"));
    }

    [Test]
    public async Task Binding_RestoredSessionWritesPlacementWithoutAppendingJournal()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        var       sessionId = await CommitLegacySessionAsync(store.Root, workspace.Root);
        var       logPath   = Path.Combine(store.Root, sessionId + SessionStoreWriter.LogFileSuffix);
        var       before    = await File.ReadAllBytesAsync(logPath, CancellationToken.None);

        await CreateMigratedStoreAsync(store.Root);
        var restored = await SessionStoreReader.ReadAsync(store.Root, sessionId, CancellationToken.None);
        Assert.That(restored, Is.Not.Null);
        Assert.That(restored!.ProjectAssignmentState,
                    Is.EqualTo(SessionProjectAssignmentState.PendingInitialization));

        var opened = await SessionStoreWriter.TryOpenExistingAsync(store.Root, sessionId, null,
                                                                   CancellationToken.None);
        Assert.That(opened.Status, Is.EqualTo(SessionWriterOpenStatus.Opened));
        var session = new ChatSession(sessionId, restored.WorkspaceRoot, Model, null);
        using (var writer = opened.Writer!)
            await SessionProjectBinding.BindRestoredAsync(store.Root, session, writer, CancellationToken.None);

        var placement = await SessionStorePlacementStore.GetAsync(store.Root, sessionId, CancellationToken.None);
        Assert.That(placement.State, Is.EqualTo(SessionProjectAssignmentState.Project));
        Assert.That(session.ProjectId, Is.EqualTo(placement.ProjectId));
        Assert.That(await File.ReadAllBytesAsync(logPath, CancellationToken.None), Is.EqualTo(before));
    }

    [Test]
    public async Task Binding_RestoredSessionDoesNotOverwriteConcurrentPlacement()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        var       sessionId = await CommitLegacySessionAsync(store.Root, workspace.Root);
        await CreateMigratedStoreAsync(store.Root);
        var restored = await SessionStoreReader.ReadAsync(store.Root, sessionId, CancellationToken.None);
        Assert.That(restored!.ProjectAssignmentState,
                    Is.EqualTo(SessionProjectAssignmentState.PendingInitialization));

        // 恢复快照之后、绑定之前，后台初始化或另一进程已显式解绑：续聊不得把它覆盖回项目。
        await SessionStorePlacementStore.SetNoProjectAsync(store.Root, sessionId, CancellationToken.None);

        var opened = await SessionStoreWriter.TryOpenExistingAsync(store.Root, sessionId, null,
                                                                   CancellationToken.None);
        Assert.That(opened.Status, Is.EqualTo(SessionWriterOpenStatus.Opened));
        var session = new ChatSession(sessionId, restored.WorkspaceRoot, Model, null);
        using (var writer = opened.Writer!)
            await SessionProjectBinding.BindRestoredAsync(store.Root, session, writer, CancellationToken.None);

        var placement = await SessionStorePlacementStore.GetAsync(store.Root, sessionId, CancellationToken.None);
        Assert.That(placement.State, Is.EqualTo(SessionProjectAssignmentState.NoProject),
                    "并发写入的明确无项目必须胜出，不能被续聊覆盖。");
        Assert.That(session.ProjectAssignmentState, Is.EqualTo(SessionProjectAssignmentState.NoProject),
                    "内存会话必须镜像最终的业务库归属。");
        Assert.That(session.ProjectId, Is.Null);
    }

    /// <summary>
    ///     <para>
    ///         迁移后运行中宿主的解绑只写业务库归属行，不再追加 JSONL 归属记录，也不被 writer 的旧归属拒绝拦下；
    ///         这是项目删除解绑打开会话的前提。
    ///     </para>
    ///     <para>
    ///         After migration an open host's unassignment writes only the business placement row and appends no JSONL
    ///         ownership record, so the writer's legacy-ownership refusal never blocks project deletion.
    ///     </para>
    /// </summary>
    [Test]
    public async Task HostUnassignment_InMigratedStoreWritesBusinessPlacementOnly()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        await CreateMigratedStoreAsync(store.Root);
        var project = await CreateProjectAsync(store.Root, workspace.Root);
        var session = new ChatSession(NewId(), workspace.Root, Model, null, project.Id,
                                      SessionProjectAssignmentState.Project);
        var context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        context.Append(ChatMessage.User("hello"));

        var logPath = Path.Combine(store.Root, session.Id + SessionStoreWriter.LogFileSuffix);
        long lengthBefore;
        using (var writer = new SessionStoreWriter(store.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
            lengthBefore = new FileInfo(logPath).Length;

            using (var host = new ChatSessionHost(session, context,
                                                  new AgentOptions
                                                  {
                                                      Model = Model.ModelId, MaxAgentSteps = 4,
                                                      DefaultToolTimeoutSeconds = 30
                                                  }, null, new ToolRegistry([]), "system", sessionWriter : writer))
                await host.UnassignProjectAsync(project.Id, CancellationToken.None);
        }

        var placement = await SessionStorePlacementStore.GetAsync(store.Root, session.Id, CancellationToken.None);
        Assert.That(placement.State, Is.EqualTo(SessionProjectAssignmentState.NoProject));
        Assert.That(session.ProjectAssignmentState, Is.EqualTo(SessionProjectAssignmentState.NoProject));
        Assert.That(new FileInfo(logPath).Length, Is.EqualTo(lengthBefore),
                    "迁移后解绑不得追加 JSONL 归属记录。");
        Assert.That(await File.ReadAllTextAsync(logPath, CancellationToken.None), Does.Not.Contain("project-unassignment"));
    }

    [Test]
    public async Task Deletion_CleansPlacementAndOrganizationRowsInMigratedStore()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        var       sessionId = await CommitLegacySessionAsync(store.Root, workspace.Root);
        await CreateMigratedStoreAsync(store.Root);
        var project = await CreateProjectAsync(store.Root, workspace.Root);
        await SessionStorePlacementStore.SetProjectAsync(store.Root, sessionId, project.Id, CancellationToken.None);
        await SessionStoreOrganizationStore.SetSessionArchivedAsync(store.Root, sessionId, true,
                                                                    CancellationToken.None);
        await SessionStoreOrganizationStore.SetSessionPinnedAsync(store.Root, sessionId, true,
                                                                  CancellationToken.None);

        var operations = new SessionStoreOperations();
        var issues = await operations.DeleteSessionPermanentlyAsync(store.Root, workspace.Root, sessionId,
                                                                    CancellationToken.None);
        Assert.That(issues, Is.Empty);
        Assert.That(File.Exists(Path.Combine(store.Root, sessionId + SessionStoreWriter.LogFileSuffix)), Is.False);
        Assert.That((await SessionStorePlacementStore.GetAsync(store.Root, sessionId, CancellationToken.None)).State,
                    Is.EqualTo(SessionProjectAssignmentState.PendingInitialization));
        var organization = await SessionStoreOrganizationStore.LoadAsync(store.Root, CancellationToken.None);
        Assert.That(organization.ArchivedSessionIds, Is.Empty);
        Assert.That(organization.PinnedSessionIds, Is.Empty);
    }

    [Test]
    public async Task ProjectDeletion_BusinessModeUnassignsPlacementsAndRemovesProject()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        await CreateMigratedStoreAsync(store.Root);
        var project = await CreateProjectAsync(store.Root, workspace.Root);
        var session = NewId();
        await SessionStorePlacementStore.SetProjectAsync(store.Root, session, project.Id, CancellationToken.None);
        await SessionStoreOrganizationStore.SetProjectPinnedAsync(store.Root, project.Id, true,
                                                                  CancellationToken.None);
        await SessionStoreOrganizationStore.SetManualProjectOrderAsync(store.Root, [project.Id],
                                                                       CancellationToken.None);

        var operations = new SessionStoreOperations();
        var gateway    = new RecordingProjectDeletionGateway();
        var deleted    = await operations.DeleteProjectAsync(store.Root, project.Id, project.Revision, gateway,
                                                             CancellationToken.None);

        Assert.That(deleted.Id, Is.EqualTo(project.Id));
        Assert.That((await ProjectStore.LoadAsync(ProjectStore.FilePath(store.Root), CancellationToken.None)).Projects,
                    Is.Empty);
        Assert.That((await SessionStorePlacementStore.GetAsync(store.Root, session, CancellationToken.None)).State,
                    Is.EqualTo(SessionProjectAssignmentState.NoProject));
        var organization = await SessionStoreOrganizationStore.LoadAsync(store.Root, CancellationToken.None);
        Assert.That(organization.PinnedProjectIds, Is.Empty);
        Assert.That(organization.ManualProjectOrder, Is.Empty);
        Assert.That(gateway.Calls, Is.EqualTo(new[]
        {
            "DeletionMarked", "UnbindDraftPages", "UnbindOpenRuntimeSessions", "UnassignmentApplied",
            "PrepareCompletion", "Completed"
        }));
    }

    [Test]
    public async Task Title_LastNonNullSavedNameWinsInBothRecoveryAndSummary()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        var       sessionId = NewId();
        var       session   = new ChatSession(sessionId, workspace.Root, Model);
        var       context   = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        context.Append(ChatMessage.User("hello"));
        using (var writer = new SessionStoreWriter(store.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
            await writer.AppendRecordsAsync(
            [
                new SessionLogRecord(string.Empty, "session-title",
                                     System.Text.Json.JsonSerializer.SerializeToElement(
                                      new SessionTitle { Name = "Saved name", Pending = false },
                                      SessionLogJsonContext.Default.SessionTitle))
            ], CancellationToken.None);
            // 已保存名称之后出现待生成标记：完整恢复与摘要都必须保留已保存名称。
            await writer.AppendRecordsAsync(
            [
                new SessionLogRecord(string.Empty, "session-title",
                                     System.Text.Json.JsonSerializer.SerializeToElement(
                                      new SessionTitle { Name = null, Pending = true },
                                      SessionLogJsonContext.Default.SessionTitle))
            ], CancellationToken.None);
        }

        var restored = await SessionStoreReader.ReadAsync(store.Root, sessionId, CancellationToken.None);
        Assert.That(restored!.Title.Name, Is.EqualTo("Saved name"));

        var summary = await SessionSummaryBuilder.BuildAsync(store.Root, sessionId, CancellationToken.None);
        Assert.That(summary!.Summary.Title, Is.EqualTo("Saved name"));
    }

    /// <summary>
    ///     <para>
    ///         迁移进行中时公开读取入口拒绝，导入器专用的原始日志读取仍可读取旧来源：阻塞不得被读成"未迁移"，
    ///         也不得反过来把导入器自己挡住。
    ///     </para>
    ///     <para>
    ///         While a migration is running the public read entries refuse, yet the importer-only raw log read still
    ///         reads legacy sources: blocked must never be read as "not migrated", and must never block the importer
    ///         itself either.
    ///     </para>
    /// </summary>
    [Test]
    public async Task BlockedStore_RefusesPublicPlacementReadsButKeepsRawLogReadForImporter()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        var       sessionId = await CommitLegacySessionAsync(store.Root, workspace.Root);
        var       database  = new SessionStoreDatabase(store.Root);
        await database.InitializeAsync(CancellationToken.None);
        await SessionStoreMigration.WriteMarkerAsync(store.Root,
                                                     SessionStoreMigrationMarker.Start(NewId(), DateTimeOffset.UtcNow),
                                                     CancellationToken.None);
        Assert.That((await SessionStoreAuthority.ResolveAsync(store.Root, CancellationToken.None)).Mode,
                    Is.EqualTo(SessionStoreMode.Blocked));

        // 公开入口：严格恢复、单条读取、列表与降级扫描都不得回退日志推导的归属。
        var readError = await TestAssert.ThrowsAsync<SessionStoreMigrationException>(
            () => SessionStoreReader.ReadAsync(store.Root, sessionId, CancellationToken.None));
        Assert.That(readError.Kind, Is.EqualTo(SessionStoreFailureKind.Conflict));
        var listError = await TestAssert.ThrowsAsync<SessionStoreMigrationException>(
            () => SessionStoreReader.ListAsync(store.Root, CancellationToken.None));
        Assert.That(listError.Kind, Is.EqualTo(SessionStoreFailureKind.Conflict));
        var scanError = await TestAssert.ThrowsAsync<SessionStoreMigrationException>(
            () => new SessionListProjection(store.Root).ScanSummariesAsync(CancellationToken.None));
        Assert.That(scanError.Kind, Is.EqualTo(SessionStoreFailureKind.Conflict));

        // 导入器入口：只读日志自身的严格投影，不解析迁移权威。
        var raw = await SessionStoreReader.ReadLogProjectionAsync(store.Root, sessionId, CancellationToken.None);
        Assert.That(raw, Is.Not.Null);
        Assert.That(raw!.Id, Is.EqualTo(sessionId));
        Assert.That(raw.ProjectAssignmentState,
                    Is.EqualTo(SessionProjectAssignmentState.PendingInitialization),
                    "原始读取必须返回日志自身的归属，不叠加业务库值。");
    }

    [Test]
    public async Task LegacyReaders_BypassAuthorityDispatchWhileMigrationIsBlocked()
    {        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        var legacyProject = await ProjectStore.GetOrCreateAsync(ProjectStore.FilePath(store.Root), workspace.Root, null,
                                                                CancellationToken.None);
        await SessionStorePinStore.SetProjectPinnedAsync(store.Root, legacyProject.Id, true, CancellationToken.None);

        // 迁移开始后标记存在但批次未切换：生产入口全部阻塞，导入器必须仍能读取旧来源。
        var          database = new SessionStoreDatabase(store.Root);
        await database.InitializeAsync(CancellationToken.None);
        await SessionStoreMigration.WriteMarkerAsync(store.Root,
                                                     SessionStoreMigrationMarker.Start(NewId(), DateTimeOffset.UtcNow),
                                                     CancellationToken.None);
        Assert.That((await SessionStoreAuthority.ResolveAsync(store.Root, CancellationToken.None)).Mode,
                    Is.EqualTo(SessionStoreMode.Blocked));

        var pins = await SessionStorePinStore.LoadLegacyFileAsync(store.Root, CancellationToken.None);
        Assert.That(pins.PinnedProjectIds, Is.EqualTo(new[] { legacyProject.Id }));
        var catalog = await ProjectStore.LoadLegacyFileAsync(ProjectStore.FilePath(store.Root), CancellationToken.None);
        Assert.That(catalog.Projects.Select(project => project.Id), Is.EqualTo(new[] { legacyProject.Id }));
        var archives = await SessionStoreArchiveStore.LoadLegacyFileAsync(store.Root, CancellationToken.None);
        Assert.That(archives.ArchivedSessionIds, Is.Empty);
    }

    /// <summary>
    ///     <para>把解绑入口暴露给测试的薄门面；保持测试直接调用真实生产方法。</para>
    ///     <para>A thin facade exposing the unassignment entry to tests while still calling the real production method.</para>
    /// </summary>
    private static class SessionStoreUnassignmentTestFacade
    {
        public static Task<SessionProjectUnassignmentResult> UnassignAsync(string storeDirectory, string projectId) =>
            SessionProjectUnbinder.UnassignAsync(storeDirectory, projectId, CancellationToken.None);
    }

    /// <summary>
    ///     <para>记录项目删除各阶段调用顺序的宿主接缝实现。</para>
    ///     <para>A host-seam implementation recording the project-deletion stage order.</para>
    /// </summary>
    private sealed class RecordingProjectDeletionGateway : ISessionProjectDeletionGateway
    {
        public List<string> Calls { get; } = [];

        public Task DeletionMarkedAsync(Project deleting, CancellationToken cancellationToken)
        {
            Calls.Add("DeletionMarked");
            return Task.CompletedTask;
        }

        public Task UnbindDraftPagesAsync(string storeDirectory, string projectId, CancellationToken cancellationToken)
        {
            Calls.Add("UnbindDraftPages");
            return Task.CompletedTask;
        }

        public Task UnbindOpenRuntimeSessionsAsync(
            string storeDirectory, string projectId, CancellationToken cancellationToken)
        {
            Calls.Add("UnbindOpenRuntimeSessions");
            return Task.CompletedTask;
        }

        public Task UnassignmentAppliedAsync(
            SessionProjectUnassignmentResult result, CancellationToken cancellationToken)
        {
            Calls.Add("UnassignmentApplied");
            return Task.CompletedTask;
        }

        public Task PrepareCompletionAsync(string storeDirectory, string projectId, CancellationToken cancellationToken)
        {
            Calls.Add("PrepareCompletion");
            return Task.CompletedTask;
        }

        public Task CompletedAsync(Project deleted, CancellationToken cancellationToken)
        {
            Calls.Add("Completed");
            return Task.CompletedTask;
        }
    }
}

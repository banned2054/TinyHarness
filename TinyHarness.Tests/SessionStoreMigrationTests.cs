using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Project;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         迁移引擎的契约测试：首轮导入三个批次、重复运行为无操作、中断续作与崩溃窗口幂等、标记与库身份保护、
///         旧来源无法表示时明确失败、损坏日志与悬空引用只报告不猜测、租约互斥与取消可重试。
///     </para>
///     <para>
///         Contract tests of the migration engine: importing all three batches on the first run, no-op repeated runs,
///         resuming interrupted work and crash-window idempotency, marker and database-identity protection, explicit
///         failure when a legacy source cannot be represented, damaged logs and dangling references reported rather
///         than guessed, and lease exclusion plus retryable cancellation.
///     </para>
/// </summary>
public class SessionStoreMigrationTests
{
    [Test]
    public async Task Migrate_ImportsAllBatchesAndCompletes()
    {
        using var fixture = new StoreFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId, fixture.ProjectId);
        await fixture.WriteSessionAsync(fixture.NoProjectSessionId, null,
                                        SessionProjectAssignmentState.NoProject);
        await fixture.WriteSessionAsync(fixture.PendingSessionId, null);
        try
        {
            var result = await SessionStoreMigration.MigrateAsync(MigrationOptions(fixture, fixture.SidebarPath),
                                                                 CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(result.AlreadyCompleted, Is.False);
                Assert.That(result.Batches, Has.Count.EqualTo(3));
                Assert.That(result.Batches.Select(batch => batch.Batch),
                            Is.EqualTo(SessionStoreMigrationMarker.BatchOrder));
                Assert.That(result.Batches.All(batch => batch.SwitchedThisRun), Is.True);
                Assert.That(result.Batches.All(batch => batch.SourceFingerprint.Length == 64), Is.True);
                Assert.That(result.Batches[0].ImportedRows, Is.EqualTo(6));
                Assert.That(result.Batches[1].ImportedRows, Is.EqualTo(1));
                Assert.That(result.Batches[2].ImportedRows, Is.EqualTo(2));
            });

            var inspection = await SessionStoreMigration.InspectAsync(MigrationOptions(fixture, fixture.SidebarPath),
                                                                    CancellationToken.None);
            Assert.That(inspection.Status, Is.EqualTo(SessionStoreMigrationStatus.Completed));
            Assert.That(inspection.IsMigrated, Is.True);
            Assert.That(inspection.Warnings, Is.Empty);
            Assert.That(inspection.StoreIdentity, Is.EqualTo(result.StoreIdentity));

            var organization = await SessionStoreOrganizationStore.LoadAsync(fixture.StoreDirectory,
                                                                            CancellationToken.None);
            Assert.Multiple(() =>
            {
                Assert.That(organization.ArchivedSessionIds, Is.EqualTo(new[] { fixture.ArchivedSessionId }));
                Assert.That(organization.PinnedProjectIds, Is.EqualTo(new[] { fixture.ProjectId }));
                Assert.That(organization.PinnedSessionIds, Is.EqualTo(new[] { fixture.ArchivedSessionId }));
                Assert.That(organization.ManualProjectOrder, Is.EqualTo(new[] { fixture.ProjectId }));
                Assert.That(organization.ManualSessionOrder, Is.EqualTo(new[] { fixture.BoundSessionId }));
                Assert.That(organization.CollapsedGroupKeys, Is.EqualTo(new[] { "group-a" }));
            });

            var placements = await SessionStorePlacementStore.LoadAllAsync(fixture.StoreDirectory,
                                                                          CancellationToken.None);
            Assert.Multiple(() =>
            {
                Assert.That(placements, Has.Count.EqualTo(2));
                Assert.That(placements[fixture.BoundSessionId].State,
                            Is.EqualTo(SessionProjectAssignmentState.Project));
                Assert.That(placements[fixture.BoundSessionId].ProjectId, Is.EqualTo(fixture.ProjectId));
                Assert.That(placements[fixture.NoProjectSessionId].State,
                            Is.EqualTo(SessionProjectAssignmentState.NoProject));
                Assert.That(placements[fixture.NoProjectSessionId].ProjectId, Is.Null);
                Assert.That(placements.ContainsKey(fixture.PendingSessionId), Is.False);
            });

            var catalog = await SessionStoreProjectCatalogStore.LoadAsync(fixture.StoreDirectory, CancellationToken.None);
            Assert.That(catalog.Projects.Single().Id, Is.EqualTo(fixture.ProjectId));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Migrate_WhenAlreadyCompleted_IsNoOp()
    {
        using var fixture = new StoreFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId, fixture.ProjectId);
        try
        {
            await SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None);
            var markerBytes = await File.ReadAllBytesAsync(SessionStoreMigration.MarkerPathFor(fixture.StoreDirectory));
            var databaseBytes = await File.ReadAllBytesAsync(SessionStoreDatabase.PathFor(fixture.StoreDirectory));
            var legacyBytes = fixture.ReadLegacyBytes();

            var second = await SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(second.AlreadyCompleted, Is.True);
                Assert.That(second.Batches.All(batch => !batch.SwitchedThisRun), Is.True);
                Assert.That(second.StoreIdentity, Is.Not.Empty);
            });
            Assert.That(await File.ReadAllBytesAsync(SessionStoreMigration.MarkerPathFor(fixture.StoreDirectory)),
                        Is.EqualTo(markerBytes), "重复运行不得改写标记。");
            Assert.That(await File.ReadAllBytesAsync(SessionStoreDatabase.PathFor(fixture.StoreDirectory)),
                        Is.EqualTo(databaseBytes), "重复运行不得重写业务库。");
            AssertLegacyBytesEqual(legacyBytes, fixture.ReadLegacyBytes());
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Migrate_DuplicatePinsAcrossSources_DoNotDuplicateRows()
    {
        using var fixture = new StoreFixture();
        var secondProjectId = StoreFixture.NewId();
        fixture.WriteProjectCatalog((fixture.ProjectId, "Alpha", fixture.Workspace, 1),
                                    (secondProjectId, "Beta", Path.Combine(fixture.Root, "beta"), 1));
        // 置顶文件与旧侧栏置顶列表都包含同一项目：合并后保持一次且保留文件顺序在前。
        fixture.WritePinState([fixture.ProjectId], [fixture.ArchivedSessionId]);
        fixture.WriteArchiveState(fixture.ArchivedSessionId);
        fixture.WriteSidebar([fixture.ProjectId], [], ["group-a"],
                             [fixture.ProjectId, secondProjectId], [fixture.ArchivedSessionId]);
        try
        {
            await SessionStoreMigration.MigrateAsync(MigrationOptions(fixture, fixture.SidebarPath),
                                                     CancellationToken.None);

            var organization = await SessionStoreOrganizationStore.LoadAsync(fixture.StoreDirectory,
                                                                            CancellationToken.None);
            Assert.That(organization.PinnedProjectIds, Is.EqualTo(new[] { fixture.ProjectId, secondProjectId }));
            Assert.That(organization.PinnedSessionIds, Is.EqualTo(new[] { fixture.ArchivedSessionId }));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Migrate_ResumesInterruptedMigrationAndKeepsSwitchedBatches()
    {
        using var fixture = new StoreFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId, fixture.ProjectId);
        try
        {
            // 手工构造"组织状态已提交且已切换、项目与归属仍待切换"的中断状态。
            var migrationId = StoreFixture.NewId();
            var database     = new SessionStoreDatabase(fixture.StoreDirectory);
            await database.InitializeAsync(CancellationToken.None);
            string identity;
            await using (var connection = await database.OpenExistingForInternalUseAsync(CancellationToken.None))
            {
                identity = (await SessionStoreDatabase.ReadStoreIdentityAsync(connection, CancellationToken.None))!;
                await SessionStoreDatabase.WriteMetaValueAsync(connection, null, SessionStoreMigration.MigrationIdMetaKey,
                                                               migrationId, CancellationToken.None);
                await using var transaction =
                    (SqliteTransaction)await connection.BeginTransactionAsync(CancellationToken.None);
                // 中断前导入的组织状态只含一个归档行；续作不得重新导入组织状态，否则这里会被置顶/顺序行改写。
                var interrupted = new SessionStoreOrganizationState([fixture.ArchivedSessionId], [], [], [], [], []);
                await SessionStoreOrganizationStore.ReplaceAllAsync(connection, transaction, interrupted,
                                                                    CancellationToken.None);
                await transaction.CommitAsync(CancellationToken.None);
            }

            var now = DateTimeOffset.UtcNow;
            await SessionStoreMigration.WriteMarkerAsync(
                fixture.StoreDirectory,
                SessionStoreMigrationMarker.Start(migrationId, now).WithStoreIdentity(identity, now)
                                         .WithBatchSwitched(SessionStoreMigrationBatch.OrganizationState,
                                                            "interrupted-fingerprint", now),
                CancellationToken.None);

            var result = await SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(result.AlreadyCompleted, Is.False);
                Assert.That(result.Batches.Single(batch => batch.Batch == SessionStoreMigrationBatch.OrganizationState)
                                  .SwitchedThisRun, Is.False);
                Assert.That(result.Batches.Single(batch => batch.Batch == SessionStoreMigrationBatch.ProjectCatalog)
                                  .SwitchedThisRun, Is.True);
                Assert.That(result.Batches.Single(batch => batch.Batch == SessionStoreMigrationBatch.SessionPlacement)
                                  .SwitchedThisRun, Is.True);
            });

            var organization = await SessionStoreOrganizationStore.LoadAsync(fixture.StoreDirectory,
                                                                            CancellationToken.None);
            Assert.Multiple(() =>
            {
                // 已切换批次保持中断前内容：没有置顶、没有手动顺序、只有一个归档行。
                Assert.That(organization.ArchivedSessionIds, Is.EqualTo(new[] { fixture.ArchivedSessionId }));
                Assert.That(organization.PinnedProjectIds, Is.Empty);
                Assert.That(organization.PinnedSessionIds, Is.Empty);
                Assert.That(organization.ManualProjectOrder, Is.Empty);
                Assert.That(organization.ManualSessionOrder, Is.Empty);
                Assert.That(organization.CollapsedGroupKeys, Is.Empty);
            });
            var placements = await SessionStorePlacementStore.LoadAllAsync(fixture.StoreDirectory,
                                                                          CancellationToken.None);
            Assert.That(placements.Keys, Is.EqualTo(new[] { fixture.BoundSessionId }));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Migrate_CrashWindowAfterDatabaseCommit_RetryIsIdempotent()
    {
        using var fixture = new StoreFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId, fixture.ProjectId);
        try
        {
            var first = await SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None);
            var placementsBefore = await SessionStorePlacementStore.LoadAllAsync(fixture.StoreDirectory,
                                                                                CancellationToken.None);
            // 模拟"归属事务已提交、标记仍为待切换"的崩溃窗口：只把标记回退到该批待切换。
            var marker = (await SessionStoreMigration.ReadMarkerAsync(fixture.StoreDirectory, CancellationToken.None))
               .Marker!;
            var batches = marker.Batches.ToDictionary(pair => pair.Key, pair => pair.Value);
            batches[SessionStoreMigrationBatch.SessionPlacement] = SessionStoreBatchState.Pending;
            var fingerprints = marker.SourceFingerprints.ToDictionary(pair => pair.Key, pair => pair.Value);
            fingerprints.Remove(SessionStoreMigrationBatch.SessionPlacement);
            await SessionStoreMigration.WriteMarkerAsync(fixture.StoreDirectory,
                                                         marker with
                                                         {
                                                             Batches            = batches,
                                                             SourceFingerprints = fingerprints
                                                         },
                                                         CancellationToken.None);

            var second = await SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(second.AlreadyCompleted, Is.False);
                Assert.That(second.StoreIdentity, Is.EqualTo(first.StoreIdentity));
                Assert.That(second.Batches.Single(batch => batch.Batch == SessionStoreMigrationBatch.SessionPlacement)
                                  .SwitchedThisRun, Is.True);
                Assert.That(second.Batches.Single(batch => batch.Batch == SessionStoreMigrationBatch.OrganizationState)
                                  .SwitchedThisRun, Is.False);
            });
            var placementsAfter = await SessionStorePlacementStore.LoadAllAsync(fixture.StoreDirectory,
                                                                               CancellationToken.None);
            Assert.That(placementsAfter.Keys, Is.EqualTo(placementsBefore.Keys), "重做归属批次不得产生重复行。");
            Assert.That((await SessionStoreMigration.InspectAsync(fixture.StoreDirectory, CancellationToken.None))
                       .Status, Is.EqualTo(SessionStoreMigrationStatus.Completed));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Migrate_AdoptsDatabaseIdentityWhenMarkerWasInterruptedBeforeRecordingIt()
    {
        using var fixture = new StoreFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId, fixture.ProjectId);
        try
        {
            // 模拟"开始标记已写、业务库已建、库身份尚未写回标记"的崩溃窗口。
            var migrationId = StoreFixture.NewId();
            var now         = DateTimeOffset.UtcNow;
            await SessionStoreMigration.WriteMarkerAsync(
                fixture.StoreDirectory, SessionStoreMigrationMarker.Start(migrationId, now), CancellationToken.None);
            var database = new SessionStoreDatabase(fixture.StoreDirectory);
            await database.InitializeAsync(CancellationToken.None);
            await using (var connection = await database.OpenExistingForInternalUseAsync(CancellationToken.None))
                await SessionStoreDatabase.WriteMetaValueAsync(connection, null, SessionStoreMigration.MigrationIdMetaKey,
                                                               migrationId, CancellationToken.None);

            var result = await SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None);

            var marker = (await SessionStoreMigration.ReadMarkerAsync(fixture.StoreDirectory, CancellationToken.None))
               .Marker!;
            Assert.Multiple(() =>
            {
                Assert.That(result.AlreadyCompleted, Is.False);
                Assert.That(result.MigrationId, Is.EqualTo(migrationId));
                Assert.That(marker.StoreIdentity, Is.EqualTo(result.StoreIdentity));
                Assert.That(marker.IsComplete, Is.True);
            });
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Migrate_DamagedMarker_RefusesAndKeepsFile()
    {
        using var fixture = new StoreFixture();
        fixture.WriteDefaultSources();
        await File.WriteAllTextAsync(SessionStoreMigration.MarkerPathFor(fixture.StoreDirectory),
                                     "{\"schema\":\"tinyharness.session-store-migration/1\",\"migrationId\":\"bad\"}");
        try
        {
            var markerBytes = await File.ReadAllBytesAsync(SessionStoreMigration.MarkerPathFor(fixture.StoreDirectory));
            var legacyBytes = fixture.ReadLegacyBytes();
            var exception = await TestAssert.ThrowsAsync<SessionStoreMigrationException>(
                () => SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None));

            Assert.That(exception.Kind, Is.EqualTo(SessionStoreFailureKind.Damaged));
            Assert.That(await File.ReadAllBytesAsync(SessionStoreMigration.MarkerPathFor(fixture.StoreDirectory)),
                        Is.EqualTo(markerBytes), "损坏标记必须原样保留。");
            AssertLegacyBytesEqual(legacyBytes, fixture.ReadLegacyBytes());
            Assert.That((await SessionStoreMigration.InspectAsync(fixture.StoreDirectory, CancellationToken.None))
                       .Status, Is.EqualTo(SessionStoreMigrationStatus.MarkerDamaged));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Migrate_CompletedMarkerWithMissingDatabase_RefusesWithoutReimporting()
    {
        using var fixture = new StoreFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId, fixture.ProjectId);
        try
        {
            await SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None);
            StoreFixture.DeleteDatabase(fixture.StoreDirectory);
            var legacyBytes = fixture.ReadLegacyBytes();

            var exception = await TestAssert.ThrowsAsync<SessionStoreMigrationException>(
                () => SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None));

            Assert.That(exception.Kind, Is.EqualTo(SessionStoreFailureKind.Missing));
            Assert.That(SessionStoreDatabase.Exists(fixture.StoreDirectory), Is.False,
                        "迁移完成后业务库丢失不得被重新建成空库。");
            AssertLegacyBytesEqual(legacyBytes, fixture.ReadLegacyBytes());
            Assert.That((await SessionStoreMigration.InspectAsync(fixture.StoreDirectory, CancellationToken.None))
                       .Status, Is.EqualTo(SessionStoreMigrationStatus.DatabaseMissing));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Migrate_CompletedMarkerWithReplacedDatabase_Refuses()
    {
        using var fixture = new StoreFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId, fixture.ProjectId);
        try
        {
            await SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None);
            // 用另一个已初始化库替换同名文件：库身份不同，必须按"被替换"拒绝而不是当作同一库继续。
            using (var other = new StoreFixture("tinyharness-session-store-other"))
            {
                await new SessionStoreDatabase(other.StoreDirectory).InitializeAsync(CancellationToken.None);
                StoreFixture.DeleteDatabase(fixture.StoreDirectory);
                File.Copy(SessionStoreDatabase.PathFor(other.StoreDirectory),
                          SessionStoreDatabase.PathFor(fixture.StoreDirectory));
            }

            var exception = await TestAssert.ThrowsAsync<SessionStoreMigrationException>(
                () => SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None));

            Assert.That(exception.Kind, Is.EqualTo(SessionStoreFailureKind.Conflict));
            Assert.That((await SessionStoreMigration.InspectAsync(fixture.StoreDirectory, CancellationToken.None))
                       .Status, Is.EqualTo(SessionStoreMigrationStatus.DatabaseReplaced));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Migrate_CompletedMarkerWithDamagedDatabase_Refuses()
    {
        using var fixture = new StoreFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId, fixture.ProjectId);
        try
        {
            await SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None);
            StoreFixture.DeleteDatabase(fixture.StoreDirectory);
            await File.WriteAllTextAsync(SessionStoreDatabase.PathFor(fixture.StoreDirectory),
                                         "this is not a sqlite database");

            var exception = await TestAssert.ThrowsAsync<SessionStoreMigrationException>(
                () => SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None));

            Assert.That(exception.Kind, Is.EqualTo(SessionStoreFailureKind.Damaged));
            Assert.That((await SessionStoreMigration.InspectAsync(fixture.StoreDirectory, CancellationToken.None))
                       .Status, Is.EqualTo(SessionStoreMigrationStatus.DatabaseDamaged));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Inspect_WarnsWhenLegacySourceChangedAfterSwitch()
    {
        using var fixture = new StoreFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId, fixture.ProjectId);
        try
        {
            await SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None);
            // 旧程序在切换后继续写置顶文件：检查仍为 Completed，但必须提示旧来源已被改写。
            fixture.WritePinState([fixture.ProjectId], [fixture.ArchivedSessionId, StoreFixture.NewId()]);

            var inspection = await SessionStoreMigration.InspectAsync(fixture.StoreDirectory, CancellationToken.None);

            Assert.That(inspection.Status, Is.EqualTo(SessionStoreMigrationStatus.Completed));
            Assert.That(inspection.Warnings.Any(warning => warning.Contains("organization-state", StringComparison.Ordinal)),
                        Is.True, $"Expected an organization-state warning, got: {string.Join(" | ", inspection.Warnings)}");
            var organization = await SessionStoreOrganizationStore.LoadAsync(fixture.StoreDirectory,
                                                                            CancellationToken.None);
            Assert.That(organization.PinnedSessionIds, Is.EqualTo(new[] { fixture.ArchivedSessionId }),
                        "业务库仍是权威，检查不得按改写后的旧文件修正数据。");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Migrate_InvalidLegacyId_FailsAndLeavesEveryBatchPending()
    {
        using var fixture = new StoreFixture();
        fixture.WriteProjectCatalog((fixture.ProjectId, "Alpha", fixture.Workspace, 1));
        fixture.WritePinState([fixture.ProjectId], ["session-a"]);
        fixture.WriteArchiveState(fixture.ArchivedSessionId);
        try
        {
            var legacyBytes = fixture.ReadLegacyBytes();
            var exception = await TestAssert.ThrowsAsync<SessionStoreMigrationException>(
                () => SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Kind, Is.EqualTo(SessionStoreFailureKind.Damaged));
                Assert.That(exception.Message, Does.Contain("pinned-items.json"));
                Assert.That(exception.Message, Does.Contain("session-a"));
            });
            var marker = (await SessionStoreMigration.ReadMarkerAsync(fixture.StoreDirectory, CancellationToken.None))
               .Marker!;
            Assert.That(marker.PendingBatches, Has.Count.EqualTo(3));
            Assert.That(await StoreFixture.CountRowsAsync(fixture.StoreDirectory, "archived_session"), Is.Zero);
            Assert.That(await StoreFixture.CountRowsAsync(fixture.StoreDirectory, "project"), Is.Zero);
            AssertLegacyBytesEqual(legacyBytes, fixture.ReadLegacyBytes());
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Migrate_DamagedSessionLog_LeavesSessionPendingAndReportsWarning()
    {
        using var fixture = new StoreFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId, fixture.ProjectId);
        var damagedSessionId = StoreFixture.NewId();
        await File.WriteAllTextAsync(Path.Combine(fixture.StoreDirectory,
                                                  damagedSessionId + SessionStoreWriter.LogFileSuffix),
                                     "{ this is not a session log");
        try
        {
            var result = await SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None);

            Assert.That(result.AlreadyCompleted, Is.False);
            var placements = await SessionStorePlacementStore.LoadAllAsync(fixture.StoreDirectory,
                                                                          CancellationToken.None);
            Assert.Multiple(() =>
            {
                Assert.That(placements.Keys, Is.EqualTo(new[] { fixture.BoundSessionId }),
                            "损坏日志必须保持未导入，其他可读日志照常导入。");
                Assert.That(placements.ContainsKey(damagedSessionId), Is.False);
            });
            var inspection = await SessionStoreMigration.InspectAsync(fixture.StoreDirectory, CancellationToken.None);
            Assert.That(inspection.Status, Is.EqualTo(SessionStoreMigrationStatus.Completed));
            Assert.That(inspection.Warnings.Any(warning => warning.Contains(damagedSessionId, StringComparison.Ordinal)),
                        Is.True, $"Expected the damaged session id in warnings, got: {string.Join(" | ", inspection.Warnings)}");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Migrate_DanglingProjectReference_IsDroppedWithDiagnostic()
    {
        using var fixture = new StoreFixture();
        fixture.WriteDefaultSources();
        var missingProjectId = StoreFixture.NewId();
        await fixture.WriteSessionAsync(fixture.BoundSessionId, missingProjectId);
        try
        {
            await SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None);

            var placements = await SessionStorePlacementStore.LoadAllAsync(fixture.StoreDirectory,
                                                                          CancellationToken.None);
            Assert.That(placements.ContainsKey(fixture.BoundSessionId), Is.False,
                        "目录册外的项目引用必须丢弃为待整理，绝不猜测项目。");
            var inspection = await SessionStoreMigration.InspectAsync(fixture.StoreDirectory, CancellationToken.None);
            Assert.That(inspection.Warnings.Any(warning => warning.Contains(missingProjectId, StringComparison.Ordinal)),
                        Is.True, $"Expected the dangling project id in warnings, got: {string.Join(" | ", inspection.Warnings)}");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Migrate_LeaseHeldByAnotherOperation_FailsLocked()
    {
        using var fixture = new StoreFixture();
        fixture.WriteDefaultSources();
        try
        {
            await using var held = await SessionStoreMigration.AcquireLeaseAsync(fixture.StoreDirectory,
                                                                                 CancellationToken.None);
            var exception = await TestAssert.ThrowsAsync<SessionStoreMigrationException>(
                () => SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None));

            Assert.That(exception.Kind, Is.EqualTo(SessionStoreFailureKind.Locked));
            Assert.That(SessionStoreDatabase.Exists(fixture.StoreDirectory), Is.False);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Migrate_CancelledBeforeStart_LeavesRetryableState()
    {
        using var fixture = new StoreFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId, fixture.ProjectId);
        try
        {
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await TestAssert.ThrowsAsync<OperationCanceledException>(
                () => SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), cancelled.Token));
            Assert.That((await SessionStoreMigration.InspectAsync(fixture.StoreDirectory, CancellationToken.None))
                       .Status, Is.EqualTo(SessionStoreMigrationStatus.NotStarted),
                        "提交前取消不得留下开始标记或半迁移状态。");

            var retried = await SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None);
            Assert.That(retried.AlreadyCompleted, Is.False);
            Assert.That((await SessionStoreMigration.InspectAsync(fixture.StoreDirectory, CancellationToken.None))
                       .Status, Is.EqualTo(SessionStoreMigrationStatus.Completed));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Migrate_InvalidSidebarPreferencesPath_FailsDamaged()
    {
        using var fixture = new StoreFixture();
        fixture.WriteDefaultSources();
        try
        {
            var legacyBytes = fixture.ReadLegacyBytes();
            var exception = await TestAssert.ThrowsAsync<SessionStoreMigrationException>(
                () => SessionStoreMigration.MigrateAsync(
                          MigrationOptions(fixture, Path.Combine(fixture.Root, "missing-sidebar.json")),
                          CancellationToken.None));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Kind, Is.EqualTo(SessionStoreFailureKind.Damaged));
                Assert.That(exception.Message, Does.Contain("missing-sidebar.json"));
            });
            var marker = (await SessionStoreMigration.ReadMarkerAsync(fixture.StoreDirectory, CancellationToken.None))
               .Marker!;
            Assert.That(marker.PendingBatches, Has.Count.EqualTo(3));
            AssertLegacyBytesEqual(legacyBytes, fixture.ReadLegacyBytes());
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Migrate_MissingProjectCatalog_ImportsEmptyCatalog()
    {
        using var fixture = new StoreFixture();
        await fixture.WriteSessionAsync(fixture.PendingSessionId, null);
        try
        {
            var result = await SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(result.Batches[1].ImportedRows, Is.Zero);
                Assert.That(result.Batches[2].ImportedRows, Is.Zero);
                Assert.That(File.Exists(fixture.ProjectCatalogPath), Is.False,
                            "迁移不得创建旧项目目录册文件。");
            });
            var catalog = await SessionStoreProjectCatalogStore.LoadAsync(fixture.StoreDirectory, CancellationToken.None);
            Assert.That(catalog.Projects, Is.Empty);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static SessionStoreMigrationOptions MigrationOptions(StoreFixture fixture, string? sidebarPath = null) =>
        new(fixture.StoreDirectory) { SidebarPreferencesPath = sidebarPath };

    /// <summary>
    ///     <para>断言迁移前后旧文件的字节内容一致；字典值按数组逐元素比较，而不是比较数组引用。</para>
    ///     <para>Asserts that every legacy file stayed byte-identical across the migration by comparing the arrays element-wise rather than by reference.</para>
    /// </summary>
    private static void AssertLegacyBytesEqual(
        IReadOnlyDictionary<string, byte[]> expected, IReadOnlyDictionary<string, byte[]> actual)
    {
        Assert.That(actual.Keys, Is.EquivalentTo(expected.Keys));
        foreach (var (path, bytes) in expected)
            Assert.That(actual[path], Is.EqualTo(bytes), $"legacy file '{Path.GetFileName(path)}' changed");
    }

    /// <summary>
    ///     <para>迁移测试的隔离 store 夹具：唯一临时目录、合法旧文件与会话日志写入器，清理由测试自身负责。</para>
    ///     <para>The isolated store fixture of the migration tests: a unique temporary directory, writers for valid legacy files and session logs, and cleanup owned by the test.</para>
    /// </summary>
    private sealed class StoreFixture : IDisposable
    {
        public StoreFixture(string prefix = "tinyharness-session-store-migration")
        {
            Root          = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
            StoreDirectory = Path.Combine(Root, "store");
            Workspace     = Path.GetFullPath(Path.Combine(Root, "workspace"));
            SidebarPath   = Path.Combine(Root, "desktop-sidebar.json");
            Directory.CreateDirectory(StoreDirectory);
            Directory.CreateDirectory(Workspace);
            ProjectId         = NewId();
            ArchivedSessionId = NewId();
            BoundSessionId    = NewId();
            NoProjectSessionId = NewId();
            PendingSessionId  = NewId();
        }

        public string Root { get; }

        public string StoreDirectory { get; }

        public string Workspace { get; }

        public string SidebarPath { get; }

        public string ProjectId { get; }

        public string ArchivedSessionId { get; }

        public string BoundSessionId { get; }

        public string NoProjectSessionId { get; }

        public string PendingSessionId { get; }

        public string ProjectCatalogPath => ProjectStore.FilePath(StoreDirectory);

        public string PinStatePath => SessionStorePinStore.FilePath(StoreDirectory);

        public string ArchiveStatePath => SessionStoreArchiveStore.FilePath(StoreDirectory);

        /// <summary>
        ///     <para>创建带 32 位小写十六进制格式的随机 ID。</para>
        ///     <para>Creates a random 32-character lowercase hexadecimal id.</para>
        /// </summary>
        public static string NewId() => Guid.NewGuid().ToString("N");

        /// <summary>
        ///     <para>写入默认旧来源：一个项目、置顶项目与会话、一个归档会话、侧栏顺序与折叠分组。</para>
        ///     <para>Writes the default legacy sources: one project, pinned project and session, one archived session, and sidebar order and collapsed groups.</para>
        /// </summary>
        public void WriteDefaultSources()
        {
            WriteProjectCatalog((ProjectId, "Alpha", Workspace, 1));
            WritePinState([ProjectId], [ArchivedSessionId]);
            WriteArchiveState(ArchivedSessionId);
            WriteSidebar([ProjectId], [BoundSessionId], ["group-a"]);
        }

        /// <summary>
        ///     <para>写入项目目录册。</para>
        ///     <para>Writes the project catalog.</para>
        /// </summary>
        public void WriteProjectCatalog(params (string Id, string Name, string Root, long Revision)[] projects)
        {
            var now = DateTimeOffset.UtcNow;
            var catalog = new ProjectCatalog
            {
                Schema   = ProjectCatalog.CurrentSchema,
                Revision = 1,
                Projects = projects.Select(project => new Project
                {
                    Id          = project.Id,
                    Name        = project.Name,
                    Roots       = [new ProjectRoot(project.Root)],
                    PrimaryRoot = project.Root,
                    Revision    = project.Revision,
                    CreatedUtc  = now,
                    UpdatedUtc  = now
                }).ToArray()
            };
            File.WriteAllBytes(ProjectCatalogPath,
                               JsonSerializer.SerializeToUtf8Bytes(catalog, ProjectJsonContext.Default.ProjectCatalog));
        }

        /// <summary>
        ///     <para>写入置顶状态文件。</para>
        ///     <para>Writes the pin-state file.</para>
        /// </summary>
        public void WritePinState(IReadOnlyList<string> projectIds, IReadOnlyList<string> sessionIds) =>
            File.WriteAllBytes(PinStatePath, JsonSerializer.SerializeToUtf8Bytes(new SessionStorePins
            {
                Schema           = SessionStorePins.CurrentSchema,
                PinnedProjectIds = projectIds,
                PinnedSessionIds = sessionIds
            }, SessionStorePinJsonContext.Default.SessionStorePins));

        /// <summary>
        ///     <para>写入归档状态文件。</para>
        ///     <para>Writes the archive-state file.</para>
        /// </summary>
        public void WriteArchiveState(params string[] sessionIds) =>
            File.WriteAllBytes(ArchiveStatePath, JsonSerializer.SerializeToUtf8Bytes(new SessionStoreArchives
            {
                Schema             = SessionStoreArchives.CurrentSchema,
                ArchivedSessionIds = sessionIds
            }, SessionStoreArchiveJsonContext.Default.SessionStoreArchives));

        /// <summary>
        ///     <para>写入桌面侧栏偏好文件中的目标 store 条目。</para>
        ///     <para>Writes the target store entry inside the desktop sidebar preferences file.</para>
        /// </summary>
        public void WriteSidebar(
            IReadOnlyList<string> manualProjectOrder, IReadOnlyList<string> manualSessionOrder,
            IReadOnlyList<string> collapsedGroupKeys, IReadOnlyList<string>? legacyPins = null,
            IReadOnlyList<string>? legacySessionPins = null)
        {
            static JsonArray Array(IReadOnlyList<string> values)
            {
                var array = new JsonArray();
                foreach (var value in values) array.Add(value);
                return array;
            }

            var document = new JsonObject
            {
                ["schema"] = "tinyharness.desktop-sidebar/1",
                ["stores"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["storeDirectory"]     = StoreDirectory,
                        ["manualProjectOrder"] = Array(manualProjectOrder),
                        ["manualSessionOrder"] = Array(manualSessionOrder),
                        ["collapsedGroupKeys"] = Array(collapsedGroupKeys),
                        ["pinnedProjectOrder"] = Array(legacyPins ?? []),
                        ["pinnedSessionIds"]   = Array(legacySessionPins ?? [])
                    }
                }
            };
            File.WriteAllText(SidebarPath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>
        ///     <para>用真实写入器提交一条只含身份批次的会话日志；归属状态由项目 ID 或显式状态决定。</para>
        ///     <para>Commits a session log with only the identity batch through the real writer; the assignment comes from the project id or the explicit state.</para>
        /// </summary>
        public async Task WriteSessionAsync(
            string sessionId, string? projectId,
            SessionProjectAssignmentState state = SessionProjectAssignmentState.PendingInitialization)
        {
            using var writer = new SessionStoreWriter(StoreDirectory, sessionId);
            var model = new SessionModelIdentity("test-model", "https://example.invalid/v1",
                                                 ChatApiKind.ChatCompletions);
            var session = projectId is null
                ? new ChatSession(sessionId, Workspace, model, DateTimeOffset.UtcNow, null, state)
                : new ChatSession(sessionId, Workspace, model, DateTimeOffset.UtcNow, projectId);
            var context = new ConversationContext(new ContextOptions());
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        /// <summary>
        ///     <para>读取全部旧文件的字节，用于断言迁移与恢复没有改写它们。</para>
        ///     <para>Reads the bytes of every legacy file to assert that migration and restore never rewrote them.</para>
        /// </summary>
        public Dictionary<string, byte[]> ReadLegacyBytes()
        {
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var path in new[] { ProjectCatalogPath, PinStatePath, ArchiveStatePath, SidebarPath })
                if (File.Exists(path))
                    result[path] = File.ReadAllBytes(path);
            return result;
        }

        /// <summary>
        ///     <para>删除业务库及其 WAL／SHM。</para>
        ///     <para>Deletes the business database and its WAL／SHM.</para>
        /// </summary>
        public static void DeleteDatabase(string storeDirectory)
        {
            var path = SessionStoreDatabase.PathFor(storeDirectory);
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(candidate))
                    File.Delete(candidate);
        }

        /// <summary>
        ///     <para>直接统计业务库中的表行数，供尚未完成迁移的中间状态断言。</para>
        ///     <para>Counts a business-table row directly, for intermediate states before the migration completes.</para>
        /// </summary>
        public static async Task<long> CountRowsAsync(string storeDirectory, string table)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = SessionStoreDatabase.PathFor(storeDirectory),
                Mode       = SqliteOpenMode.ReadWrite,
                Pooling    = false
            };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 测试临时目录清理失败不能掩盖测试本身的断言结果。
            }
        }
    }
}

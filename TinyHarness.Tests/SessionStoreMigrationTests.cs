using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    ///     <para>
    ///         把业务库退回版本 1 的真实结构：删掉版本 2 新增的墓碑表与状态索引，再把版本号改回 1；
    ///         版本 1 的业务行保持原样。升级路径必须能从这份结构继续，而不是要求重建库。
    ///     </para>
    ///     <para>
    ///         Rolls the business database back to the real version-1 structure by dropping the version-2 tombstone
    ///         table and its state index and setting the version to 1, while keeping every version-1 business row. The
    ///         upgrade path must continue from this structure instead of demanding a rebuilt database.
    ///     </para>
    /// </summary>
    private static async Task RollBackToVersionOneAsync(string storeDirectory)
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
        command.CommandText = $"""
            DROP INDEX IF EXISTS {SessionStoreDatabase.TombstoneStateIndex};
            DROP TABLE IF EXISTS {SessionStoreDatabase.TombstoneTableName};
            UPDATE {SessionStoreDatabase.MetaTableName} SET value = '1'
            WHERE key = '{SessionStoreDatabase.SchemaVersionKey}';
            """;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    ///     <para>
    ///         把业务库替换成另一份结构合法的版本 1 库：身份、建库时间与既有业务行都不同，版本号仍是 1，
    ///         因此可用性探测只会报告"需要升级"。用于验证升级不得先改写异库。
    ///     </para>
    ///     <para>
    ///         Replaces the business database with a different, structurally valid version-1 database whose identity,
    ///         creation time, and business rows all differ while the version stays 1, so availability probing reports
    ///         only "needs upgrade". It proves an upgrade never rewrites a foreign database first.
    ///     </para>
    /// </summary>
    private static async Task ReplaceWithForeignVersionOneDatabaseAsync(string storeDirectory, string projectName)
    {
        var database = new SessionStoreDatabase(storeDirectory, isBusinessStore: false);
        StoreFixture.DeleteDatabase(storeDirectory);
        await database.InitializeAsync(CancellationToken.None);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = SessionStoreDatabase.PathFor(storeDirectory),
            Mode       = SqliteOpenMode.ReadWrite,
            Pooling    = false
        };
        await using (var connection = new SqliteConnection(builder.ToString()))
        {
            await connection.OpenAsync();
            var insert = connection.CreateCommand();
            insert.CommandText = $"""
                INSERT INTO project (id, name, root_path, root_key, revision, is_deleting, created_utc, updated_utc)
                VALUES ('{StoreFixture.NewId()}', $name, 'c:\foreign\root', 'C:\FOREIGN\ROOT', 1, 0, 1000, 1000);
                UPDATE {SessionStoreDatabase.MetaTableName} SET value = '1'
                WHERE key = '{SessionStoreDatabase.SchemaVersionKey}';
                UPDATE {SessionStoreDatabase.MetaTableName} SET value = 'foreign-identity-0000000000000000'
                WHERE key = '{SessionStoreDatabase.StoreIdentityKey}';
                """;
            insert.Parameters.AddWithValue("$name", projectName);
            await insert.ExecuteNonQueryAsync();
            var drop = connection.CreateCommand();
            drop.CommandText = $"""
                DROP INDEX IF EXISTS {SessionStoreDatabase.TombstoneStateIndex};
                DROP TABLE IF EXISTS {SessionStoreDatabase.TombstoneTableName};
                """;
            await drop.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    ///     <para>读取业务库的 schema 版本、库身份与项目名，用于断言被拒绝的升级没有改写文件。</para>
    ///     <para>Reads the schema version, store identity, and project name so a refused upgrade can be proven not to have rewritten the file.</para>
    /// </summary>
    private static async Task<(string? Version, string? Identity, IReadOnlyList<string> ProjectNames)>
        ReadDatabaseFactsAsync(string storeDirectory)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = SessionStoreDatabase.PathFor(storeDirectory),
            Mode       = SqliteOpenMode.ReadWrite,
            Pooling    = false
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        var version = await SessionStoreDatabase.ReadMetaValueAsync(connection, SessionStoreDatabase.SchemaVersionKey,
                                                                    CancellationToken.None);
        var identity = await SessionStoreDatabase.ReadStoreIdentityAsync(connection, CancellationToken.None);
        var names = connection.CreateCommand();
        names.CommandText = "SELECT name FROM project ORDER BY name;";
        var projects = new List<string>();
        await using var reader = await names.ExecuteReaderAsync();
        while (await reader.ReadAsync()) projects.Add(reader.GetString(0));
        return (version, identity, projects);
    }

    /// <summary>
    ///     <para>
    ///         已完成迁移的 store 在程序升级后仍可能带着旧 schema：显式迁移入口必须就地升级并保留业务行，
    ///         结果报告升级来源版本，重复运行再变为无操作。
    ///     </para>
    ///     <para>
    ///         A completed store can still carry an older schema after a program upgrade: the explicit migration entry
    ///         must upgrade it in place, keep every business row, report the source version, and turn the next run into
    ///         a no-op.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Migrate_CompletedStoreWithOlderSchema_UpgradesInPlaceAndKeepsRows()
    {
        using var fixture = new StoreFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId, fixture.ProjectId);
        try
        {
            await SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None);
            await RollBackToVersionOneAsync(fixture.StoreDirectory);
            var legacyBytes = fixture.ReadLegacyBytes();

            var before = await SessionStoreMigration.InspectAsync(fixture.StoreDirectory, CancellationToken.None);
            Assert.That(before.Status, Is.EqualTo(SessionStoreMigrationStatus.DatabaseNeedsUpgrade),
                        "旧 schema 必须作为需要升级报告，而不是损坏或已完成。");
            // 生产读写在此之前拒绝：迁移标记仍是 Completed，只有业务库连接入口能看出 schema 落后。
            var refused = await TestAssert.ThrowsAsync<SessionStoreMigrationException>(
                () => SessionStorePlacementStore.LoadAllAsync(fixture.StoreDirectory, CancellationToken.None));
            Assert.That(refused.Kind, Is.EqualTo(SessionStoreFailureKind.Unsupported),
                        $"升级前的业务库必须拒绝生产读写，实际为 {refused.Kind}。");

            var upgraded = await SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(upgraded.AlreadyCompleted, Is.True, "迁移早已完成，本次只做 schema 升级。");
                Assert.That(upgraded.SchemaUpgradedFrom, Is.EqualTo(1));
                Assert.That(upgraded.StoreIdentity, Is.EqualTo(before.StoreIdentity));
            });
            var after = await SessionStoreMigration.InspectAsync(fixture.StoreDirectory, CancellationToken.None);
            Assert.That(after.Status, Is.EqualTo(SessionStoreMigrationStatus.Completed));
            Assert.That(await StoreFixture.CountRowsAsync(fixture.StoreDirectory, "project"), Is.EqualTo(1L),
                        "升级不得丢失既有项目行。");
            var placements = await SessionStorePlacementStore.LoadAllAsync(fixture.StoreDirectory,
                                                                          CancellationToken.None);
            Assert.That(placements[fixture.BoundSessionId].ProjectId, Is.EqualTo(fixture.ProjectId),
                        "升级不得丢失既有归属行。");
            var organization = await SessionStoreOrganizationStore.LoadAsync(fixture.StoreDirectory,
                                                                            CancellationToken.None);
            Assert.That(organization.ArchivedSessionIds, Is.EqualTo(new[] { fixture.ArchivedSessionId }));
            // 墓碑表必须补上：新程序的删除协议依赖它，缺表会让删除路径在运行期失败。
            await SessionStoreTombstoneStore.MarkPendingAsync(fixture.StoreDirectory, fixture.PendingSessionId,
                                                             CancellationToken.None);
            Assert.That(await SessionStoreTombstoneStore.IsTombstonedAsync(fixture.StoreDirectory,
                                                                          fixture.PendingSessionId,
                                                                          CancellationToken.None), Is.True);
            AssertLegacyBytesEqual(legacyBytes, fixture.ReadLegacyBytes());

            var repeated = await SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None);
            Assert.That(repeated.SchemaUpgradedFrom, Is.Null, "已升级的库再次运行不得重复升级。");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    /// <summary>
    ///     <para>
    ///         未完成迁移的 store 带着旧 schema 续作时，升级在导入之前发生：身份核对、导入与校验都按当前结构
    ///         进行，不会因为版本更低而停在导入中途。
    ///     </para>
    ///     <para>
    ///         When an unfinished store resumes on an older schema the upgrade happens before the import, so identity
    ///         checks, imports, and validation all run on the current structure instead of stopping mid-import.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Migrate_UnfinishedStoreWithOlderSchema_UpgradesBeforeResumingImport()
    {
        using var fixture = new StoreFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId, fixture.ProjectId);
        try
        {
            // 构造旧程序留下的现场：业务库由更早的版本建出（schema 1）并在导入任何批次前中断；迁移标记与
            // 库身份都已绑定，旧来源仍是该 store 的唯一数据来源。
            // Reproduce what an older build leaves behind: the business database was created by an earlier version
            // (schema 1) and the run stopped before importing any batch, while the marker is already bound to its
            // identity and the legacy sources still hold every row.
            var migrationId = StoreFixture.NewId();
            var database    = new SessionStoreDatabase(fixture.StoreDirectory);
            await database.InitializeAsync(CancellationToken.None);
            string identity;
            await using (var connection = await database.OpenExistingForInternalUseAsync(CancellationToken.None))
                identity = (await SessionStoreDatabase.ReadStoreIdentityAsync(connection, CancellationToken.None))!;
            await RollBackToVersionOneAsync(fixture.StoreDirectory);
            await WriteMigrationIdAsync(fixture.StoreDirectory, migrationId);
            var now     = DateTimeOffset.UtcNow;
            var started = SessionStoreMigrationMarker.Start(migrationId, now).WithStoreIdentity(identity, now);
            await SessionStoreMigration.WriteMarkerAsync(fixture.StoreDirectory, started, CancellationToken.None);

            var resumed = await SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(resumed.AlreadyCompleted, Is.False);
                Assert.That(resumed.SchemaUpgradedFrom, Is.EqualTo(1),
                            "续作必须先升级旧 schema 再导入，而不是直接按当前结构写入。");
                Assert.That(resumed.Batches.All(batch => batch.SwitchedThisRun), Is.True);
            });
            Assert.That((await SessionStoreMigration.InspectAsync(fixture.StoreDirectory, CancellationToken.None))
                       .Status, Is.EqualTo(SessionStoreMigrationStatus.Completed));
            var placements = await SessionStorePlacementStore.LoadAllAsync(fixture.StoreDirectory,
                                                                          CancellationToken.None);
            Assert.That(placements[fixture.BoundSessionId].ProjectId, Is.EqualTo(fixture.ProjectId));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    /// <summary>
    ///     <para>
    ///         已完成迁移的 store 被另一份结构合法的旧库替换后再次运行迁移：本入口必须在提交任何升级写入前按
    ///         迁移标记拒绝，被替换的文件保持原版本、原结构与原业务行，创建它的旧程序仍可继续使用。
    ///     </para>
    ///     <para>
    ///         When a completed store's business database is replaced by a different structurally valid older
    ///         database, running the migration again must refuse on the marker before committing any upgrade write,
    ///         leaving the replaced file at its original version, structure, and rows so the older program that
    ///         created it can keep using it.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Migrate_CompletedStoreWithForeignOlderDatabase_RefusesWithoutUpgradingIt()
    {
        using var fixture = new StoreFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId, fixture.ProjectId);
        try
        {
            await SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None);
            var marker = (await SessionStoreMigration.ReadMarkerAsync(fixture.StoreDirectory, CancellationToken.None))
               .Marker;
            await ReplaceWithForeignVersionOneDatabaseAsync(fixture.StoreDirectory, "Foreign");
            var before = await ReadDatabaseFactsAsync(fixture.StoreDirectory);
            Assert.Multiple(() =>
            {
                Assert.That(before.Version, Is.EqualTo("1"), "夹具必须先是一份结构合法的版本 1 库。");
                Assert.That(before.Identity, Is.Not.EqualTo(marker!.StoreIdentity),
                            "夹具身份必须与迁移标记不同，才能代表被替换的异库。");
                Assert.That(before.ProjectNames, Is.EqualTo(new[] { "Foreign" }));
            });

            var refused = await TestAssert.ThrowsAsync<SessionStoreMigrationException>(
                () => SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None));

            Assert.Multiple(() =>
            {
                Assert.That(refused.Kind, Is.EqualTo(SessionStoreFailureKind.Conflict));
                Assert.That(refused.Message, Does.Contain("schema upgrade was refused"));
            });
            var after = await ReadDatabaseFactsAsync(fixture.StoreDirectory);
            Assert.Multiple(() =>
            {
                Assert.That(after.Version, Is.EqualTo("1"), "拒绝升级后仍须保持原 schema 版本。");
                Assert.That(after.Identity, Is.EqualTo(before.Identity), "异库身份不得被改写。");
                Assert.That(after.ProjectNames, Is.EqualTo(before.ProjectNames), "异库业务行不得被改动。");
            });
            Assert.That(await MissingVersionTwoObjectsAsync(fixture.StoreDirectory), Is.True,
                        "异库不得被补上版本 2 的墓碑结构。");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    /// <summary>
    ///     <para>
    ///         未完成迁移的 store 被另一份结构合法的旧库替换：续作路径同样必须先核对迁移归属再升级，拒绝后
    ///         异库保持原版本与原业务行。
    ///     </para>
    ///     <para>
    ///         When an unfinished store's business database is replaced by a different structurally valid older
    ///         database, the resume path must also verify migration ownership before upgrading, and the refusal
    ///         leaves that database at its original version and rows.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Migrate_UnfinishedStoreWithForeignOlderDatabase_RefusesWithoutUpgradingIt()
    {
        using var fixture = new StoreFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId, fixture.ProjectId);
        try
        {
            // 中断现场：标记已绑定原库身份，但业务库被换成另一份属于别的迁移的版本 1 库。
            // Interrupted state: the marker is bound to the original identity while the file was replaced by a
            // version-1 database belonging to a different migration.
            var migrationId = StoreFixture.NewId();
            var original    = new SessionStoreDatabase(fixture.StoreDirectory);
            await original.InitializeAsync(CancellationToken.None);
            string originalIdentity;
            await using (var connection = await original.OpenExistingForInternalUseAsync(CancellationToken.None))
                originalIdentity =
                    (await SessionStoreDatabase.ReadStoreIdentityAsync(connection, CancellationToken.None))!;
            var now     = DateTimeOffset.UtcNow;
            var started = SessionStoreMigrationMarker.Start(migrationId, now)
                                                     .WithStoreIdentity(originalIdentity, now);
            await SessionStoreMigration.WriteMarkerAsync(fixture.StoreDirectory, started, CancellationToken.None);

            await ReplaceWithForeignVersionOneDatabaseAsync(fixture.StoreDirectory, "Foreign");
            await WriteMigrationIdAsync(fixture.StoreDirectory, StoreFixture.NewId());
            var before = await ReadDatabaseFactsAsync(fixture.StoreDirectory);

            var refused = await TestAssert.ThrowsAsync<SessionStoreMigrationException>(
                () => SessionStoreMigration.MigrateAsync(MigrationOptions(fixture), CancellationToken.None));

            Assert.Multiple(() =>
            {
                Assert.That(refused.Kind, Is.EqualTo(SessionStoreFailureKind.Conflict));
                Assert.That(refused.Message, Does.Contain("schema upgrade was refused"));
            });
            var after = await ReadDatabaseFactsAsync(fixture.StoreDirectory);
            Assert.Multiple(() =>
            {
                Assert.That(after.Version, Is.EqualTo("1"));
                Assert.That(after.Identity, Is.EqualTo(before.Identity));
                Assert.That(after.ProjectNames, Is.EqualTo(new[] { "Foreign" }));
            });
            Assert.That(await MissingVersionTwoObjectsAsync(fixture.StoreDirectory), Is.True);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    /// <summary>
    ///     <para>检查业务库是否仍缺版本 2 的墓碑表与状态索引，用于断言被拒绝的升级没有写入任何结构。</para>
    ///     <para>Checks whether the version-2 tombstone table and state index are still absent, proving a refused upgrade wrote no structure.</para>
    /// </summary>
    private static async Task<bool> MissingVersionTwoObjectsAsync(string storeDirectory)
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
        command.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE name IN ($table, $index);
            """;
        command.Parameters.AddWithValue("$table", SessionStoreDatabase.TombstoneTableName);
        command.Parameters.AddWithValue("$index", SessionStoreDatabase.TombstoneStateIndex);
        var found = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        return found == 0;
    }

    /// <summary>
    ///     <para>把迁移标识写进业务库元数据，使标记与库的双向绑定成立。</para>
    ///     <para>Writes the migration id into the business metadata so the marker and the database bind both ways.</para>
    /// </summary>
    private static async Task WriteMigrationIdAsync(string storeDirectory, string migrationId)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = SessionStoreDatabase.PathFor(storeDirectory),
            Mode       = SqliteOpenMode.ReadWrite,
            Pooling    = false
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await SessionStoreDatabase.WriteMetaValueAsync(connection, null, "migration_id", migrationId,
                                                      CancellationToken.None);
    }

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

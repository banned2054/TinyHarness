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
///         业务库备份、校验与恢复的契约测试：在线备份生成可校验快照、拒绝覆盖已有备份、缺失／损坏／版本不符的
///         校验结果、恢复要求已完成迁移且备份身份一致，以及恢复只清理可重建的索引派生数据并保留旧文件。
///     </para>
///     <para>
///         Contract tests of business-database backup, verification, and restoration: the online backup produces a
///         verifiable snapshot, existing backups are never overwritten, missing/damaged/unsupported backups verify
///         accordingly, restoration requires a completed migration with a matching identity, and only rebuildable
///         index-derived data is cleared while legacy files survive.
///     </para>
/// </summary>
public class SessionStoreBackupTests
{
    [Test]
    public async Task Create_FromMigratedStore_ProducesValidBackup()
    {
        using var fixture = new BackupFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId);
        try
        {
            await fixture.MigrateAsync();

            var info = await SessionStoreBackup.CreateAsync(fixture.StoreDirectory, fixture.BackupDirectory,
                                                            CancellationToken.None);
            var verification = await SessionStoreBackup.VerifyAsync(fixture.BackupDirectory, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(info.Path), Is.True);
                Assert.That(info.Path, Is.EqualTo(Path.Combine(fixture.BackupDirectory,
                                                                SessionStoreDatabase.DatabaseFileName)));
                Assert.That(info.StoreIdentity, Is.EqualTo(fixture.MarkerStoreIdentity));
                Assert.That(info.SchemaVersion, Is.EqualTo(SessionStoreDatabase.CurrentSchemaVersion));
                Assert.That(info.LengthBytes, Is.EqualTo(new FileInfo(info.Path).Length));
                Assert.That(verification.Status, Is.EqualTo(SessionStoreBackupVerificationStatus.Valid));
                Assert.That(verification.StoreIdentity, Is.EqualTo(info.StoreIdentity));
                Assert.That(verification.SchemaVersion, Is.EqualTo(SessionStoreDatabase.CurrentSchemaVersion));
            });
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Create_RefusesExistingBackupFile()
    {
        using var fixture = new BackupFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId);
        try
        {
            await fixture.MigrateAsync();
            var first = await SessionStoreBackup.CreateAsync(fixture.StoreDirectory, fixture.BackupDirectory,
                                                             CancellationToken.None);
            var bytesBefore = await File.ReadAllBytesAsync(first.Path);

            var exception = await TestAssert.ThrowsAsync<SessionStoreBackupException>(
                () => SessionStoreBackup.CreateAsync(fixture.StoreDirectory, fixture.BackupDirectory,
                                                     CancellationToken.None));

            Assert.That(exception.Kind, Is.EqualTo(SessionStoreFailureKind.Conflict));
            Assert.That(await File.ReadAllBytesAsync(first.Path), Is.EqualTo(bytesBefore),
                        "已存在的用户备份不得被覆盖或删除。");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Create_RequiresCompletedMigration()
    {
        using var fixture = new BackupFixture();
        fixture.WriteDefaultSources();
        try
        {
            var exception = await TestAssert.ThrowsAsync<SessionStoreBackupException>(
                () => SessionStoreBackup.CreateAsync(fixture.StoreDirectory, fixture.BackupDirectory,
                                                     CancellationToken.None));

            Assert.That(exception.Kind, Is.EqualTo(SessionStoreFailureKind.MigrationRequired));
            Assert.That(Directory.Exists(fixture.BackupDirectory), Is.False, "拒绝时不得创建备份目录。");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Verify_MissingBackup_ReturnsMissing()
    {
        using var fixture = new BackupFixture();
        Directory.CreateDirectory(fixture.BackupDirectory);
        try
        {
            var verification = await SessionStoreBackup.VerifyAsync(fixture.BackupDirectory, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(verification.Status, Is.EqualTo(SessionStoreBackupVerificationStatus.Missing));
                Assert.That(verification.Detail, Does.Contain(SessionStoreDatabase.DatabaseFileName));
                Assert.That(verification.StoreIdentity, Is.Null);
            });
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Verify_CorruptBackup_ReturnsDamaged()
    {
        using var fixture = new BackupFixture();
        Directory.CreateDirectory(fixture.BackupDirectory);
        await File.WriteAllTextAsync(Path.Combine(fixture.BackupDirectory, SessionStoreDatabase.DatabaseFileName),
                                     "this is not a sqlite database");
        try
        {
            var verification = await SessionStoreBackup.VerifyAsync(fixture.BackupDirectory, CancellationToken.None);

            Assert.That(verification.Status, Is.EqualTo(SessionStoreBackupVerificationStatus.Damaged));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Verify_TruncatedBackup_ReturnsDamaged()
    {
        using var fixture = new BackupFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId);
        try
        {
            await fixture.MigrateAsync();
            var info = await SessionStoreBackup.CreateAsync(fixture.StoreDirectory, fixture.BackupDirectory,
                                                            CancellationToken.None);
            await using (var stream = new FileStream(info.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                stream.SetLength(stream.Length / 2);

            var verification = await SessionStoreBackup.VerifyAsync(fixture.BackupDirectory, CancellationToken.None);

            Assert.That(verification.Status, Is.EqualTo(SessionStoreBackupVerificationStatus.Damaged));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Restore_IntoStoreWithDeletedDatabase_SucceedsAndKeepsLegacyFiles()
    {
        using var fixture = new BackupFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId);
        try
        {
            await fixture.MigrateAsync();
            await SessionStoreOrganizationStore.SetSessionArchivedAsync(fixture.StoreDirectory,
                                                                        fixture.BoundSessionId, true,
                                                                        CancellationToken.None);
            await SessionStoreBackup.CreateAsync(fixture.StoreDirectory, fixture.BackupDirectory,
                                                 CancellationToken.None);
            var legacyBytes = fixture.ReadLegacyBytes();
            fixture.DeleteDatabase();

            await SessionStoreBackup.RestoreAsync(fixture.StoreDirectory, fixture.BackupDirectory,
                                                  CancellationToken.None);

            var inspection = await SessionStoreMigration.InspectAsync(fixture.StoreDirectory, CancellationToken.None);
            Assert.That(inspection.Status, Is.EqualTo(SessionStoreMigrationStatus.Completed));
            var organization = await SessionStoreOrganizationStore.LoadAsync(fixture.StoreDirectory,
                                                                            CancellationToken.None);
            Assert.That(organization.ArchivedSessionIds, Does.Contain(fixture.BoundSessionId),
                        "恢复后的业务库必须仍保存备份时的归档状态。");
            Assert.That(fixture.ReadLegacyBytes().Keys, Is.EquivalentTo(legacyBytes.Keys));
            foreach (var (path, bytes) in legacyBytes)
                Assert.That(fixture.ReadLegacyBytes()[path], Is.EqualTo(bytes),
                            $"恢复不得改写或删除旧 JSON 文件 '{Path.GetFileName(path)}'。");
            Assert.That(Directory.EnumerateFiles(fixture.StoreDirectory)
                                 .Any(path => Path.GetFileName(path).Contains(
                                          SessionStoreBackup.PreRestoreSuffix, StringComparison.Ordinal)),
                        Is.False, "业务库缺失时没有可保留的旧库，不应生成恢复前快照。");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Restore_IntoStoreWithReplacedDatabase_Succeeds()
    {
        using var fixture = new BackupFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId);
        try
        {
            await fixture.MigrateAsync();
            await SessionStoreOrganizationStore.SetSessionArchivedAsync(fixture.StoreDirectory,
                                                                        fixture.BoundSessionId, true,
                                                                        CancellationToken.None);
            await SessionStoreBackup.CreateAsync(fixture.StoreDirectory, fixture.BackupDirectory,
                                                 CancellationToken.None);
            var originalIdentity = fixture.MarkerStoreIdentity;
            // 用另一个已初始化库替换：恢复必须接受"标记身份等于备份身份"的备份，而不是被同名文件干扰。
            using (var foreign = new BackupFixture("tinyharness-session-store-foreign"))
            {
                await new SessionStoreDatabase(foreign.StoreDirectory).InitializeAsync(CancellationToken.None);
                fixture.DeleteDatabase();
                File.Copy(SessionStoreDatabase.PathFor(foreign.StoreDirectory),
                          SessionStoreDatabase.PathFor(fixture.StoreDirectory));
            }

            await SessionStoreBackup.RestoreAsync(fixture.StoreDirectory, fixture.BackupDirectory,
                                                  CancellationToken.None);

            var restoredIdentity = (await SessionStoreMigration
                                          .InspectAsync(fixture.StoreDirectory, CancellationToken.None)).StoreIdentity;
            Assert.That(restoredIdentity, Is.EqualTo(originalIdentity),
                        "恢复后的业务库身份必须是备份与标记记录的身份。");
            var organization = await SessionStoreOrganizationStore.LoadAsync(fixture.StoreDirectory,
                                                                            CancellationToken.None);
            Assert.That(organization.ArchivedSessionIds, Does.Contain(fixture.BoundSessionId));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Verify_BackupWithDanglingPlacement_ReturnsDamaged()
    {
        using var fixture = new BackupFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId);
        try
        {
            await fixture.MigrateAsync();
            var info = await SessionStoreBackup.CreateAsync(fixture.StoreDirectory, fixture.BackupDirectory,
                                                            CancellationToken.None);
            // 直接往备份里写一条指向不存在项目的归属行：integrity_check 不会发现，外键检查必须拒绝。
            await InsertDanglingPlacementAsync(info.Path, BackupFixture.NewId());

            var verification = await SessionStoreBackup.VerifyAsync(fixture.BackupDirectory, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(verification.Status, Is.EqualTo(SessionStoreBackupVerificationStatus.Damaged));
                Assert.That(verification.Detail, Does.Contain("foreign-key"));
            });
        }
        finally
        {
            fixture.Dispose();
        }
    }

    /// <summary>
    ///     <para>
    ///         外键损坏的备份不得替换当前业务库：恢复必须在替换之前失败，原库内容保持不变。
    ///     </para>
    ///     <para>
    ///         A backup with a broken foreign key must never replace the current business database: the restore fails
    ///         before the replacement and the existing content stays intact.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Restore_BackupWithDanglingPlacement_LeavesCurrentDatabaseUntouched()
    {
        using var fixture = new BackupFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId);
        try
        {
            await fixture.MigrateAsync();
            var info = await SessionStoreBackup.CreateAsync(fixture.StoreDirectory, fixture.BackupDirectory,
                                                            CancellationToken.None);
            await InsertDanglingPlacementAsync(info.Path, BackupFixture.NewId());
            var databasePath  = SessionStoreDatabase.PathFor(fixture.StoreDirectory);
            var bytesBefore   = await File.ReadAllBytesAsync(databasePath);
            var projectBefore = await SessionStoreProjectCatalogStore.LoadAsync(fixture.StoreDirectory,
                                                                               CancellationToken.None);

            var exception = await TestAssert.ThrowsAsync<SessionStoreBackupException>(
                () => SessionStoreBackup.RestoreAsync(fixture.StoreDirectory, fixture.BackupDirectory,
                                                      CancellationToken.None));

            Assert.That(exception.Kind, Is.EqualTo(SessionStoreFailureKind.Damaged));
            Assert.That(await File.ReadAllBytesAsync(databasePath), Is.EqualTo(bytesBefore),
                        "恢复失败时当前业务库必须保持原样，不得被坏备份覆盖。");
            var projectAfter = await SessionStoreProjectCatalogStore.LoadAsync(fixture.StoreDirectory,
                                                                              CancellationToken.None);
            Assert.That(projectAfter.Projects.Select(project => project.Id),
                        Is.EquivalentTo(projectBefore.Projects.Select(project => project.Id)));
            Assert.That(Directory.EnumerateFiles(fixture.StoreDirectory, "*.restore-*.tmp"), Is.Empty,
                        "失败恢复不得留下待提交的临时副本。");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    /// <summary>
    ///     <para>直接往业务库文件写一条引用不存在项目的归属行，模拟外键损坏的备份。</para>
    ///     <para>Writes a placement row referencing a missing project straight into a database file, simulating a backup with a broken foreign key.</para>
    /// </summary>
    private static async Task InsertDanglingPlacementAsync(string databasePath, string sessionId)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite, Pooling = false
        };
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ToString());
        await connection.OpenAsync(CancellationToken.None);
        var pragma = connection.CreateCommand();
        // 外键必须关闭才能写入悬挂引用；这正是 integrity_check 发现不了的情形。
        pragma.CommandText = "PRAGMA foreign_keys=OFF;";
        await pragma.ExecuteNonQueryAsync(CancellationToken.None);
        var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO session_placement (session_id, project_id, updated_utc) " +
                             "VALUES ($session_id, $project_id, 0);";
        insert.Parameters.AddWithValue("$session_id", sessionId);
        insert.Parameters.AddWithValue("$project_id", BackupFixture.NewId());
        await insert.ExecuteNonQueryAsync(CancellationToken.None);
    }

    [Test]
    public async Task Restore_RefusesBackupOfDifferentStore()
    {
        using var source = new BackupFixture("tinyharness-session-store-source");
        source.WriteDefaultSources();
        await source.WriteSessionAsync(source.BoundSessionId);
        await source.MigrateAsync();
        var info = await SessionStoreBackup.CreateAsync(source.StoreDirectory, source.BackupDirectory,
                                                        CancellationToken.None);
        using var target = new BackupFixture("tinyharness-session-store-target");
        try
        {
            target.WriteDefaultSources();
            await target.WriteSessionAsync(target.BoundSessionId);
            await target.MigrateAsync();

            var exception = await TestAssert.ThrowsAsync<SessionStoreBackupException>(
                () => SessionStoreBackup.RestoreAsync(target.StoreDirectory, source.BackupDirectory,
                                                      CancellationToken.None));

            Assert.That(exception.Kind, Is.EqualTo(SessionStoreFailureKind.Conflict));
            Assert.That(exception.Message, Does.Contain(info.StoreIdentity));
            Assert.That((await SessionStoreMigration.InspectAsync(target.StoreDirectory, CancellationToken.None))
                       .Status, Is.EqualTo(SessionStoreMigrationStatus.Completed),
                        "拒绝恢复后目标 store 必须保持原状态。");
        }
        finally
        {
            source.Dispose();
            target.Dispose();
        }
    }

    [Test]
    public async Task Restore_RefusesMissingOrCorruptBackup()
    {
        using var fixture = new BackupFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId);
        try
        {
            await fixture.MigrateAsync();
            var missing = await TestAssert.ThrowsAsync<SessionStoreBackupException>(
                () => SessionStoreBackup.RestoreAsync(fixture.StoreDirectory, fixture.BackupDirectory,
                                                      CancellationToken.None));
            Assert.That(missing.Kind, Is.EqualTo(SessionStoreFailureKind.Missing));

            Directory.CreateDirectory(fixture.BackupDirectory);
            await File.WriteAllTextAsync(Path.Combine(fixture.BackupDirectory,
                                                      SessionStoreDatabase.DatabaseFileName),
                                         "not a database");
            var damaged = await TestAssert.ThrowsAsync<SessionStoreBackupException>(
                () => SessionStoreBackup.RestoreAsync(fixture.StoreDirectory, fixture.BackupDirectory,
                                                      CancellationToken.None));
            Assert.That(damaged.Kind, Is.EqualTo(SessionStoreFailureKind.Damaged));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task Restore_RefusesDestinationWithoutCompletedMarker()
    {
        using var backupStore = new BackupFixture("tinyharness-session-store-backup-source");
        backupStore.WriteDefaultSources();
        await backupStore.WriteSessionAsync(backupStore.BoundSessionId);
        await backupStore.MigrateAsync();
        await SessionStoreBackup.CreateAsync(backupStore.StoreDirectory, backupStore.BackupDirectory,
                                             CancellationToken.None);
        using var unmigrated = new BackupFixture("tinyharness-session-store-unmigrated");
        try
        {
            unmigrated.WriteDefaultSources();
            var missingMarker = await TestAssert.ThrowsAsync<SessionStoreBackupException>(
                () => SessionStoreBackup.RestoreAsync(unmigrated.StoreDirectory, backupStore.BackupDirectory,
                                                      CancellationToken.None));
            Assert.That(missingMarker.Kind, Is.EqualTo(SessionStoreFailureKind.MigrationRequired));

            await SessionStoreMigration.WriteMarkerAsync(
                unmigrated.StoreDirectory,
                SessionStoreMigrationMarker.Start(BackupFixture.NewId(), DateTimeOffset.UtcNow),
                CancellationToken.None);
            var interrupted = await TestAssert.ThrowsAsync<SessionStoreBackupException>(
                () => SessionStoreBackup.RestoreAsync(unmigrated.StoreDirectory, backupStore.BackupDirectory,
                                                      CancellationToken.None));
            Assert.That(interrupted.Kind, Is.EqualTo(SessionStoreFailureKind.Conflict),
                        "迁移进行中的目标不得被恢复覆盖。");
        }
        finally
        {
            backupStore.Dispose();
            unmigrated.Dispose();
        }
    }

    [Test]
    public async Task Restore_DeletesOnlyDerivedIndexFilesAndPreservesPreRestoreCopy()
    {
        using var fixture = new BackupFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId);
        try
        {
            await fixture.MigrateAsync();
            await SessionStoreBackup.CreateAsync(fixture.StoreDirectory, fixture.BackupDirectory,
                                                 CancellationToken.None);
            var indexPath = Path.Combine(fixture.StoreDirectory, SessionIndexStore.DatabaseFileName);
            foreach (var path in new[] { indexPath, indexPath + "-wal", indexPath + "-shm" })
                await File.WriteAllTextAsync(path, "derived index artifact");
            var businessBytesBefore = await File.ReadAllBytesAsync(SessionStoreDatabase.PathFor(fixture.StoreDirectory));
            var legacyBytes = fixture.ReadLegacyBytes();

            await SessionStoreBackup.RestoreAsync(fixture.StoreDirectory, fixture.BackupDirectory,
                                                  CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(indexPath), Is.False, "索引库必须被删除以便对账重建。");
                Assert.That(File.Exists(indexPath + "-wal"), Is.False);
                Assert.That(File.Exists(indexPath + "-shm"), Is.False);
                foreach (var path in legacyBytes.Keys)
                    Assert.That(File.Exists(path), Is.True, $"旧文件 '{Path.GetFileName(path)}' 必须保留。");
                Assert.That(File.Exists(SessionStoreDatabase.PathFor(fixture.StoreDirectory)), Is.True);
            });
            var preRestoreCopies = Directory.EnumerateFiles(fixture.StoreDirectory)
                                           .Where(path => Path.GetFileName(path)
                                                              .Contains(SessionStoreBackup.PreRestoreSuffix,
                                                                       StringComparison.Ordinal))
                                           .ToArray();
            Assert.That(preRestoreCopies, Has.Length.EqualTo(1), "恢复前必须保留一份旧业务库副本。");
            Assert.That(new FileInfo(preRestoreCopies[0]).Length, Is.GreaterThan(0),
                        "恢复前快照必须包含旧业务库内容。");
            Assert.That(new FileInfo(preRestoreCopies[0]).Length,
                        Is.GreaterThanOrEqualTo(businessBytesBefore.Length / 2));
            Assert.That(Directory.EnumerateFiles(fixture.BackupDirectory),
                        Is.EquivalentTo(new[]
                        {
                            Path.Combine(fixture.BackupDirectory, SessionStoreDatabase.DatabaseFileName)
                        }),
                        "恢复不得删除或改写备份源目录。");
            // 替换窗口成功收尾后，恢复记录与隔离日志都不得残留：残留记录会让下一次打开重做恢复。
            Assert.That(Directory.EnumerateFiles(fixture.StoreDirectory)
                                 .Any(path => Path.GetFileName(path)
                                                  .StartsWith(SessionStoreRestoreRecovery.RecordFileName,
                                                              StringComparison.Ordinal)),
                        Is.False, "成功恢复后不得留下恢复记录。");
            Assert.That(Directory.EnumerateFiles(fixture.StoreDirectory, "*.stale-*.tmp"), Is.Empty,
                        "成功恢复后不得留下隔离日志副本。");
            Assert.That(SessionStoreRestoreRecovery.HasPendingRecord(fixture.StoreDirectory), Is.False);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    /// <summary>
    ///     <para>
    ///         回滚只能使用本次恢复保存的副本：目录里存在名字更靠后的其他 pre-restore 文件时，仍必须放回调用方
    ///         指定的那一份，而不是按文件名挑"最新"。
    ///     </para>
    ///     <para>
    ///         A rollback may only use the copy saved by the current restore: even when a differently named pre-restore
    ///         file sorts later in the directory, the copy the caller names must be put back instead of the
    ///         "newest" file name.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Rollback_UsesExactPreservedCopyInsteadOfNewestFileName()
    {
        using var fixture = new BackupFixture();
        try
        {
            var databasePath = SessionStoreDatabase.PathFor(fixture.StoreDirectory);
            var olderCopy = databasePath + SessionStoreBackup.PreRestoreSuffix + "1000";
            var newerCopy = databasePath + SessionStoreBackup.PreRestoreSuffix + "9999";
            await File.WriteAllTextAsync(olderCopy, "copy-saved-by-this-restore");
            await File.WriteAllTextAsync(newerCopy, "unrelated-older-restore-copy");
            await File.WriteAllTextAsync(databasePath, "newly-restored-database");

            var failure = await SessionStoreBackup.TryRollBackRestoreAsync(
                fixture.StoreDirectory, databasePath, olderCopy);

            Assert.That(failure, Is.Null, "使用本次副本回滚必须成功。");
            Assert.That(await File.ReadAllTextAsync(databasePath), Is.EqualTo("copy-saved-by-this-restore"),
                        "回滚必须放回本次恢复保存的副本，而不是名字排序更靠后的历史副本。");
            Assert.That(File.Exists(newerCopy), Is.True, "回滚不得删除或改写其他 pre-restore 副本。");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    /// <summary>
    ///     <para>
    ///         本次恢复没有保存副本（恢复前业务库缺失）时必须拒绝回滚：目录里的历史 pre-restore 文件不得被当成
    ///         本次副本覆盖刚恢复的数据库。
    ///     </para>
    ///     <para>
    ///         A restore that saved no copy, because the business database was absent beforehand, must refuse to roll
    ///         back: a historical pre-restore file in the directory must never stand in for the missing copy and
    ///         overwrite the freshly restored database.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Rollback_RefusesHistoricalCopyWhenThisRestoreSavedNone()
    {
        using var fixture = new BackupFixture();
        try
        {
            var databasePath = SessionStoreDatabase.PathFor(fixture.StoreDirectory);
            var historicalCopy = databasePath + SessionStoreBackup.PreRestoreSuffix + "1000";
            await File.WriteAllTextAsync(historicalCopy, "historical-copy-of-an-earlier-restore");
            await File.WriteAllTextAsync(databasePath, "newly-restored-database");

            var failure = await SessionStoreBackup.TryRollBackRestoreAsync(
                fixture.StoreDirectory, databasePath, null);

            Assert.That(failure, Is.Not.Null, "没有本次副本时必须报告无法回滚。");
            Assert.That(failure, Does.Contain("no previous database"), "失败原因必须说明本次恢复没有保存旧库。");
            Assert.That(await File.ReadAllTextAsync(databasePath), Is.EqualTo("newly-restored-database"),
                        "拒绝回滚后刚恢复的数据库必须保持原样。");
            Assert.That(File.ReadAllText(historicalCopy), Is.EqualTo("historical-copy-of-an-earlier-restore"),
                        "历史副本不得被当作本次副本使用或改写。");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    /// <summary>
    ///     <para>
    ///         旧代际 WAL 无法隔离（被独占句柄占用）时恢复必须在替换前中止：业务库主文件不被替换，残留日志原样
    ///         保留，调用方得到明确失败，而不是"替换成功但目录里仍有一份会被应用到新文件的旧 WAL"。
    ///     </para>
    ///     <para>
    ///         A restore must abort before the replacement when the previous generation's WAL cannot be quarantined
    ///         because an exclusive handle holds it: the business-database file is not replaced, the leftover journal
    ///         stays as is, and the caller gets a definite failure instead of a successful replacement with a stale WAL
    ///         waiting to be applied to the new file.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Restore_WhenStaleWalCannotBeQuarantined_AbortsBeforeReplacingBusinessDatabase()
    {
        using var fixture = new BackupFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId);
        await fixture.MigrateAsync();
        await SessionStoreBackup.CreateAsync(fixture.StoreDirectory, fixture.BackupDirectory, CancellationToken.None);
        fixture.DeleteDatabase();
        var databasePath = SessionStoreDatabase.PathFor(fixture.StoreDirectory);
        var walPath      = databasePath + "-wal";
        await File.WriteAllTextAsync(walPath, "stale-journal-of-the-replaced-generation");
        // Windows 允许改名只读文件，因此用独占句柄稳定制造"日志无法移开"。
        using (var holder = new FileStream(walPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var exception = await TestAssert.ThrowsAsync<SessionStoreBackupException>(
                () => SessionStoreBackup.RestoreAsync(fixture.StoreDirectory, fixture.BackupDirectory,
                                                      CancellationToken.None));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Kind, Is.EqualTo(SessionStoreFailureKind.IoFailure));
                Assert.That(exception.Message, Does.Contain("-wal"));
                Assert.That(File.Exists(databasePath), Is.False, "旧 WAL 未隔离时不得提交新业务库。");
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(walPath), Is.EqualTo("stale-journal-of-the-replaced-generation"),
                        "无法隔离的旧日志必须原样保留供人工处理。");
            Assert.That(Directory.EnumerateFiles(fixture.StoreDirectory, "*.restore-*.tmp"), Is.Empty,
                        "中止的恢复不得留下待提交的临时副本。");
        });
    }

    /// <summary>
    ///     <para>
    ///         回滚时旧 WAL 无法隔离必须报告失败：不得声称"旧库已放回"，也不得在残留日志存在时覆盖刚恢复的
    ///         数据库。
    ///     </para>
    ///     <para>
    ///         A rollback must report failure when the stale WAL cannot be quarantined: it must not claim the previous
    ///         database was put back, nor overwrite the freshly restored database while the leftover journal remains.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Rollback_WhenStaleWalCannotBeQuarantined_ReportsFailureWithoutClaimingSuccess()
    {
        using var fixture = new BackupFixture();
        var databasePath  = SessionStoreDatabase.PathFor(fixture.StoreDirectory);
        var preservedCopy = databasePath + SessionStoreBackup.PreRestoreSuffix + "1000";
        var walPath       = databasePath + "-wal";
        await File.WriteAllTextAsync(preservedCopy, "copy-saved-by-this-restore");
        await File.WriteAllTextAsync(databasePath, "newly-restored-database");
        await File.WriteAllTextAsync(walPath, "stale-journal-of-the-restored-generation");
        using var holder = new FileStream(walPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var failure = await SessionStoreBackup.TryRollBackRestoreAsync(
            fixture.StoreDirectory, databasePath, preservedCopy);

        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.Not.Null, "旧 WAL 未隔离时回滚必须报告失败。");
            Assert.That(failure, Does.Contain("-wal"));
            Assert.That(File.ReadAllText(databasePath), Is.EqualTo("newly-restored-database"),
                        "回滚未完成时不得覆盖刚恢复的数据库。");
            Assert.That(File.ReadAllText(preservedCopy), Is.EqualTo("copy-saved-by-this-restore"),
                        "本次副本必须保留，供用户手工恢复。");
            Assert.That(Directory.EnumerateFiles(fixture.StoreDirectory, "*.rollback-*.tmp"), Is.Empty,
                        "失败的回滚不得留下临时文件。");
        });
    }

    /// <summary>
    ///     <para>
    ///         回滚放回步骤失败（旧主文件名被目录占位）时，已隔离的旧日志必须放回原日志文件名：失败一代的主文件
    ///         与它的日志仍是自洽的一代，不能在原地留下"日志被移走、主文件未替换"的半状态。
    ///     </para>
    ///     <para>
    ///         When the rollback's put-back step fails because the main-database name is taken by a directory, the
    ///         quarantined journal must go back under its journal name: the failed generation's main file and its
    ///         journal stay one consistent generation instead of leaving "journal moved aside, main file not replaced".
    ///     </para>
    /// </summary>
    [Test]
    public async Task Rollback_WhenPutBackFails_PutsQuarantinedJournalBack()
    {
        using var fixture = new BackupFixture();
        var databasePath  = SessionStoreDatabase.PathFor(fixture.StoreDirectory);
        var preservedCopy = databasePath + SessionStoreBackup.PreRestoreSuffix + "1000";
        var walPath       = databasePath + "-wal";
        await File.WriteAllTextAsync(preservedCopy, "copy-saved-by-this-restore");
        await File.WriteAllTextAsync(walPath, "journal-of-the-generation-being-rolled-back");
        // 主文件名被目录占位：隔离日志后放回必然失败。
        Directory.CreateDirectory(databasePath);

        var failure = await SessionStoreBackup.TryRollBackRestoreAsync(
            fixture.StoreDirectory, databasePath, preservedCopy);

        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.Not.Null, "放回失败时回滚必须报告失败。");
            Assert.That(File.Exists(walPath), Is.True, "放回失败后旧日志必须回到原日志文件名。");
            Assert.That(File.ReadAllText(walPath), Is.EqualTo("journal-of-the-generation-being-rolled-back"));
            Assert.That(Directory.EnumerateFiles(fixture.StoreDirectory, "*-wal.stale-*.tmp"), Is.Empty,
                        "放回后不得残留隔离副本。");
        });
    }

    /// <summary>
    ///     <para>
    ///         构造"隔离完成、替换未发生"时磁盘应有的文件状态：恢复记录已落盘、旧日志已改名、待提交副本仍在原位。
    ///         下一次打开业务库必须先按记录把隔离日志放回原日志名并清除记录，然后才能读到旧主库已有的数据。
    ///     </para>
    ///     <para>
    ///         Reproduces the on-disk state left between the quarantine and the replacement: the recovery record is
    ///         durable, the old journals were renamed, and the pending copy is still in place. The next open must first
    ///         put the journals back under their journal names and clear the record, and only then serve the data the old
    ///         main file already held.
    ///     </para>
    ///     <para>
    ///         这是状态构造，不是真实进程终止测试：整个用例运行在同一测试进程内，WAL 也是文本夹具而非真实 SQLite
    ///         日志，因此不覆盖句柄释放时机与 SQLite 的 WAL 重放。
    ///     </para>
    ///     <para>
    ///         This builds a state rather than testing a real process termination: the case runs inside one test process
    ///         and the WAL is a text fixture rather than a real SQLite journal, so handle-release timing and SQLite WAL
    ///         replay are not covered.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Reopen_AfterQuarantineBeforeReplacement_PutsJournalsBackAndServesData()
    {
        using var fixture = new BackupFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId);
        await fixture.MigrateAsync();
        await SessionStoreOrganizationStore.SetSessionArchivedAsync(fixture.StoreDirectory, fixture.BoundSessionId,
                                                                    true, CancellationToken.None);
        var databasePath = SessionStoreDatabase.PathFor(fixture.StoreDirectory);
        var walPath      = databasePath + "-wal";
        var pendingCopy  = SessionStoreRestoreRecovery.NewPendingCopyPath(
            databasePath, SessionStoreRestoreRecovery.RestoreOperation);
        var quarantined = SessionStoreRestoreRecovery.NewQuarantinePath(walPath);
        // 旧 WAL 里只有旧主库才有的内容：没有日志重放就读不到，靠它区分"恢复是否真的放回了日志"。
        await File.WriteAllTextAsync(walPath, "committed-content-only-in-the-old-wal");
        File.Move(walPath, quarantined);
        await File.WriteAllTextAsync(pendingCopy, "pending-copy-the-replacement-never-consumed");
        await SessionStoreRestoreRecovery.WriteAsync(
            fixture.StoreDirectory, SessionStoreRestoreRecovery.RestoreOperation, pendingCopy,
            [new SessionStoreJournalQuarantine(walPath, quarantined)], CancellationToken.None);

        // 记录与隔离副本都还在：打开前的恢复必须把它们收拾干净，并把存储判为可用。
        await SessionStoreRestoreRecovery.RecoverAsync(fixture.StoreDirectory, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(walPath), Is.True, "替换未发生时隔离的旧日志必须回到原日志名。");
            Assert.That(File.ReadAllText(walPath), Is.EqualTo("committed-content-only-in-the-old-wal"),
                        "放回的日志内容不得被改写。");
            Assert.That(File.Exists(quarantined), Is.False, "放回后不得残留隔离副本。");
            Assert.That(File.Exists(pendingCopy), Is.False, "替换未发生，待提交副本属于本次中断的恢复，必须清除。");
            Assert.That(File.Exists(SessionStoreRestoreRecovery.RecordPathFor(fixture.StoreDirectory)), Is.False,
                        "恢复完成后必须删除恢复记录。");
        });
        var organization = await SessionStoreOrganizationStore.LoadAsync(fixture.StoreDirectory, CancellationToken.None);
        Assert.That(organization.ArchivedSessionIds, Does.Contain(fixture.BoundSessionId),
                    "恢复完成后业务库必须仍可读取，旧主库数据不得丢失。");
    }

    /// <summary>
    ///     <para>
    ///         构造"替换已发生、记录未清除"时磁盘应有的文件状态：待提交副本已被替换动作消耗，隔离日志属于上一代。
    ///         下一次打开必须丢弃隔离日志而不是把它们放回，否则旧 WAL 会被应用到新的主文件上。
    ///     </para>
    ///     <para>
    ///         Reproduces the on-disk state left after the replacement but before the record was cleared: the
    ///         replacement consumed the pending copy, so the quarantined journals belong to the previous generation. The
    ///         next open must discard them instead of putting them back, because a stale WAL must never be applied to the
    ///         new main file.
    ///     </para>
    ///     <para>
    ///         与相邻用例一样，这是文件状态构造而非真实进程终止；WAL 内容不参与 SQLite 重放。
    ///     </para>
    ///     <para>
    ///         Like the neighbouring case this builds a file state instead of terminating a real process, and the WAL
    ///         content never takes part in SQLite replay.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Reopen_AfterReplacementBeforeRecordCleared_DiscardsStaleJournals()
    {
        using var fixture = new BackupFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId);
        await fixture.MigrateAsync();
        var databasePath = SessionStoreDatabase.PathFor(fixture.StoreDirectory);
        var walPath      = databasePath + "-wal";
        var missingCopy  = SessionStoreRestoreRecovery.NewPendingCopyPath(
            databasePath, SessionStoreRestoreRecovery.RestoreOperation);
        var quarantined = SessionStoreRestoreRecovery.NewQuarantinePath(walPath);
        await File.WriteAllTextAsync(quarantined, "stale-journal-of-the-previous-generation");
        await SessionStoreRestoreRecovery.WriteAsync(
            fixture.StoreDirectory, SessionStoreRestoreRecovery.RestoreOperation, missingCopy,
            [new SessionStoreJournalQuarantine(walPath, quarantined)], CancellationToken.None);

        var availability = await new SessionStoreDatabase(fixture.StoreDirectory)
           .GetAvailabilityAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(availability.Status, Is.EqualTo(SessionStoreDatabaseStatus.Available));
            Assert.That(File.Exists(quarantined), Is.False, "替换已提交，上一代隔离日志必须被丢弃。");
            Assert.That(File.Exists(walPath), Is.False, "上一代日志不得回到日志文件名上。");
            Assert.That(File.Exists(SessionStoreRestoreRecovery.RecordPathFor(fixture.StoreDirectory)), Is.False,
                        "收尾后必须删除恢复记录。");
        });
        var organization = await SessionStoreOrganizationStore.LoadAsync(fixture.StoreDirectory, CancellationToken.None);
        Assert.That(organization, Is.Not.Null, "丢弃上一代日志后业务库仍必须可用。");
    }

    /// <summary>
    ///     <para>
    ///         恢复记录损坏或无法收尾时，业务库必须判为不可用而不是照旧读写：隔离日志的下落不明时读到的主库
    ///         可能缺少已提交内容，静默继续等于丢数据。
    ///     </para>
    ///     <para>
    ///         A damaged or unresolvable recovery record must make the business database unavailable instead of serving
    ///         it anyway: while the quarantined journals' whereabouts are unknown the main file may lack committed
    ///         content, and continuing silently would lose data.
    ///     </para>
    /// </summary>
    [Test]
    public async Task UnreadableRecoveryRecord_BlocksBusinessDatabase()
    {
        using var fixture = new BackupFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId);
        await fixture.MigrateAsync();
        await File.WriteAllTextAsync(SessionStoreRestoreRecovery.RecordPathFor(fixture.StoreDirectory),
                                     "{ this is not the recovery record }");

        var availability = await new SessionStoreDatabase(fixture.StoreDirectory)
           .GetAvailabilityAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(availability.Status, Is.EqualTo(SessionStoreDatabaseStatus.Failed),
                        "恢复记录损坏时业务库必须按不可用报告。");
            Assert.That(availability.Detail, Does.Contain(SessionStoreRestoreRecovery.RecordFileName));
            Assert.That(File.Exists(SessionStoreRestoreRecovery.RecordPathFor(fixture.StoreDirectory)), Is.True,
                        "损坏的记录必须保留供人工恢复，不得被自动删除。");
        });
        await TestAssert.ThrowsAsync<SessionStoreMigrationException>(
            () => SessionStoreBusinessConnection.OpenVerifiedAsync(fixture.StoreDirectory, CancellationToken.None));
    }

    /// <summary>
    ///     <para>
    ///         结构合法但把业务库本身写成待提交副本的记录必须被拒绝：若只校验目录边界，恢复会把真实业务库当成
    ///         未提交的临时副本删除。记录保留、业务库保持不可用，文件内容一个字节都不能变。
    ///     </para>
    ///     <para>
    ///         A structurally valid record that names the business database as its pending copy must be refused: with
    ///         only a directory-boundary check, recovery would delete the real business database as an uncommitted
    ///         temporary copy. The record survives, the store stays unavailable, and not one byte of data changes.
    ///     </para>
    /// </summary>
    [Test]
    public async Task RecoveryRecord_NamingBusinessDatabaseAsPendingCopy_IsRefusedWithoutDeletingIt()
    {
        using var fixture = new BackupFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId);
        await fixture.MigrateAsync();
        var databasePath = SessionStoreDatabase.PathFor(fixture.StoreDirectory);
        var bytesBefore  = await File.ReadAllBytesAsync(databasePath);

        await WriteRawRecordAsync(fixture.StoreDirectory, SessionStoreRestoreRecovery.RestoreOperation,
                                  SessionStoreDatabase.DatabaseFileName);

        var availability = await new SessionStoreDatabase(fixture.StoreDirectory)
           .GetAvailabilityAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(availability.Status, Is.EqualTo(SessionStoreDatabaseStatus.Failed),
                        "把业务库写成待提交副本的记录必须按损坏拒绝，业务库不得报告为可用。");
            Assert.That(File.Exists(databasePath), Is.True, "被记录点名的业务库不得被当作临时副本删除。");
            Assert.That(File.Exists(SessionStoreRestoreRecovery.RecordPathFor(fixture.StoreDirectory)), Is.True,
                        "被拒绝的记录必须保留供人工处理。");
        });
        Assert.That(await File.ReadAllBytesAsync(databasePath), Is.EqualTo(bytesBefore),
                    "拒绝恢复时业务库内容必须原样保留。");
    }

    /// <summary>
    ///     <para>
    ///         结构合法但把会话日志写成隔离副本的记录必须被拒绝：替换已提交时恢复会"丢弃上一代日志"而删掉这个
    ///         文件，所以角色校验必须先于任何删除动作。
    ///     </para>
    ///     <para>
    ///         A structurally valid record that names a session log as a quarantined copy must be refused: once the
    ///         replacement looks committed, recovery discards "previous-generation journals" and would delete that file,
    ///         so role validation has to run before any deletion.
    ///     </para>
    /// </summary>
    [Test]
    public async Task RecoveryRecord_NamingSessionLogAsQuarantine_IsRefusedWithoutDeletingIt()
    {
        using var fixture = new BackupFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId);
        await fixture.MigrateAsync();
        var databasePath = SessionStoreDatabase.PathFor(fixture.StoreDirectory);
        var sessionLog   = Path.Combine(fixture.StoreDirectory, fixture.BoundSessionId + SessionStoreWriter.LogFileSuffix);
        var logBytes     = await File.ReadAllBytesAsync(sessionLog);

        // 待提交副本缺失等价于"替换已提交"，此时隔离名会被直接丢弃；记录借这个分支点名会话日志。
        await WriteRawRecordAsync(fixture.StoreDirectory, SessionStoreRestoreRecovery.RestoreOperation,
                                  Path.GetFileName(SessionStoreRestoreRecovery.NewPendingCopyPath(
                                      databasePath, SessionStoreRestoreRecovery.RestoreOperation)),
                                  [(SessionStoreDatabase.DatabaseFileName + "-wal", Path.GetFileName(sessionLog))]);

        var availability = await new SessionStoreDatabase(fixture.StoreDirectory)
           .GetAvailabilityAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(availability.Status, Is.EqualTo(SessionStoreDatabaseStatus.Failed),
                        "隔离名不是由原日志派生的记录必须按损坏拒绝。");
            Assert.That(File.Exists(sessionLog), Is.True, "被记录写成隔离副本的会话日志不得被删除。");
        });
        Assert.That(await File.ReadAllBytesAsync(sessionLog), Is.EqualTo(logBytes),
                    "拒绝恢复时会话日志内容必须原样保留。");
    }

    /// <summary>
    ///     <para>
    ///         恢复记录缺少 journals 字段时必须按损坏拒绝。字段默认初始化为空列表，因此本测试用原始 JSON 明确省略
    ///         该字段，并确认隔离 WAL、待提交副本和恢复记录都保留。
    ///     </para>
    ///     <para>
    ///         A restore record without the journals field must be refused as damaged. Since the property defaults to an
    ///         empty list, this test writes raw JSON that omits the field and confirms the quarantined WAL, pending copy,
    ///         and recovery record all survive.
    ///     </para>
    /// </summary>
    [Test]
    public async Task RecoveryRecord_MissingJournals_IsRefusedWithoutChangingFiles()
    {
        using var fixture = new BackupFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId);
        await fixture.MigrateAsync();
        var databasePath   = SessionStoreDatabase.PathFor(fixture.StoreDirectory);
        var databaseBytes  = await File.ReadAllBytesAsync(databasePath);
        var pendingPath    = SessionStoreRestoreRecovery.NewPendingCopyPath(
            databasePath, SessionStoreRestoreRecovery.RestoreOperation);
        var pendingContent = "pending-copy-still-unconsumed";
        await File.WriteAllTextAsync(pendingPath, pendingContent);
        var walPath         = databasePath + "-wal";
        var quarantinedPath = SessionStoreRestoreRecovery.NewQuarantinePath(walPath);
        var walContent      = "committed-content-only-in-the-quarantined-wal";
        await File.WriteAllTextAsync(quarantinedPath, walContent);
        var recordPath = SessionStoreRestoreRecovery.RecordPathFor(fixture.StoreDirectory);
        var document = new JsonObject
        {
            ["schema"]          = "tinyharness.session-store-restore-recovery/1",
            ["operation"]       = SessionStoreRestoreRecovery.RestoreOperation,
            ["pendingFileName"] = Path.GetFileName(pendingPath)
        };
        await File.WriteAllTextAsync(recordPath, document.ToJsonString());

        var availability = await new SessionStoreDatabase(fixture.StoreDirectory)
           .GetAvailabilityAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(availability.Status, Is.EqualTo(SessionStoreDatabaseStatus.Failed),
                        "缺少日志清单时必须拒绝恢复并保持业务库不可用。");
            Assert.That(availability.Detail, Does.Contain(recordPath));
            Assert.That(File.Exists(recordPath), Is.True, "损坏记录必须保留供人工处理。");
            Assert.That(File.Exists(pendingPath), Is.True, "替换未提交时不得删除待提交副本。");
            Assert.That(File.ReadAllText(pendingPath), Is.EqualTo(pendingContent));
            Assert.That(File.Exists(quarantinedPath), Is.True, "未解析日志清单时不得丢弃隔离 WAL。");
            Assert.That(File.ReadAllText(quarantinedPath), Is.EqualTo(walContent));
            Assert.That(File.Exists(walPath), Is.False, "被隔离日志在恢复被拒绝时不得改动。");
            Assert.That(File.Exists(databasePath), Is.True, "恢复失败时业务库必须保留。");
        });
        Assert.That(await File.ReadAllBytesAsync(databasePath), Is.EqualTo(databaseBytes),
                    "拒绝恢复时业务库内容必须原样保留。");
    }

    /// <summary>
    ///     <para>
    ///         分别拒绝其余几类语义非法的记录：未知操作、原日志不是业务库 <c>-wal</c>／<c>-shm</c>、隔离名与前缀
    ///         不符、同一名字重复、待提交副本为会话日志。每种记录都必须让业务库不可用且不改动任何被点名的文件。
    ///     </para>
    ///     <para>
    ///         Also refuses the remaining semantically invalid records: an unknown operation, an original journal that is
    ///         not the business database's <c>-wal</c>／<c>-shm</c>, a quarantine name that does not derive from its
    ///         journal, a repeated name, and a pending copy that is a session log. Each must make the store unavailable
    ///         without touching any named file.
    ///     </para>
    /// </summary>
    [Test]
    public async Task RecoveryRecord_SemanticallyInvalidShapes_AreRefusedWithoutChangingFiles()
    {
        using var fixture = new BackupFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId);
        await fixture.MigrateAsync();
        var databasePath   = SessionStoreDatabase.PathFor(fixture.StoreDirectory);
        var walPath        = databasePath + "-wal";
        var pendingCopy    = SessionStoreRestoreRecovery.NewPendingCopyPath(
            databasePath, SessionStoreRestoreRecovery.RestoreOperation);
        var pendingName    = Path.GetFileName(pendingCopy);
        var sessionLogName = fixture.BoundSessionId + SessionStoreWriter.LogFileSuffix;
        await File.WriteAllTextAsync(walPath, "journal-of-the-generation-under-recovery");
        await File.WriteAllTextAsync(pendingCopy, "pending-copy-still-unconsumed");
        var recordPath = SessionStoreRestoreRecovery.RecordPathFor(fixture.StoreDirectory);

        var cases = new (string Description, string Operation, string PendingName,
                         (string Original, string Quarantined)[] Journals)[]
        {
            ("未知操作", "migrate", pendingName,
             [(SessionStoreDatabase.DatabaseFileName + "-wal", SessionStoreRestoreRecovery.NewQuarantinePath(walPath))]),
            ("待提交副本为会话日志", SessionStoreRestoreRecovery.RestoreOperation, sessionLogName, []),
            ("原日志不是业务库日志", SessionStoreRestoreRecovery.RestoreOperation, pendingName,
             [(sessionLogName, SessionStoreRestoreRecovery.NewQuarantinePath(sessionLogName))]),
            ("隔离名与前缀不符", SessionStoreRestoreRecovery.RestoreOperation, pendingName,
             [(SessionStoreDatabase.DatabaseFileName + "-wal", SessionStoreDatabase.DatabaseFileName + "-wal")]),
            ("同一名字重复", SessionStoreRestoreRecovery.RestoreOperation, pendingName,
             [(SessionStoreDatabase.DatabaseFileName + "-wal", SessionStoreRestoreRecovery.NewQuarantinePath(walPath)),
              (SessionStoreDatabase.DatabaseFileName + "-wal", SessionStoreRestoreRecovery.NewQuarantinePath(walPath))])
        };

        foreach (var (description, operation, rawPendingName, journals) in cases)
        {
            await WriteRawRecordAsync(fixture.StoreDirectory, operation, rawPendingName, journals);
            var availability = await new SessionStoreDatabase(fixture.StoreDirectory)
               .GetAvailabilityAsync(CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(availability.Status, Is.EqualTo(SessionStoreDatabaseStatus.Failed),
                            $"'{description}'的记录必须按损坏拒绝。");
                Assert.That(File.Exists(recordPath), Is.True, $"'{description}'被拒绝后记录必须保留。");
                Assert.That(File.Exists(walPath), Is.True, $"'{description}'不得改动原日志。");
                Assert.That(File.ReadAllText(walPath), Is.EqualTo("journal-of-the-generation-under-recovery"),
                            $"'{description}'不得改写原日志内容。");
                Assert.That(File.Exists(pendingCopy), Is.True, $"'{description}'不得删除待提交副本。");
            });
            File.Delete(recordPath);
        }
    }

    /// <summary>
    ///     <para>
    ///         按记录协议的原始 JSON 写一份角色非法的恢复记录，绕过写入端校验，模拟被手工改写或由其他程序写入的
    ///         记录。
    ///     </para>
    ///     <para>
    ///         Writes a role-invalid recovery record as raw protocol JSON, bypassing the writer's validation to simulate a
    ///         hand-edited record or one written by another program.
    ///     </para>
    /// </summary>
    private static async Task WriteRawRecordAsync(
        string storeDirectory, string operation, string pendingFileName,
        (string Original, string Quarantined)[]? journals = null)
    {
        var document = new JsonObject
        {
            ["schema"]          = "tinyharness.session-store-restore-recovery/1",
            ["operation"]       = operation,
            ["pendingFileName"] = pendingFileName,
            ["journals"] = new JsonArray((journals ?? [])
                                        .Select(journal => (JsonNode)new JsonObject
                                        {
                                            ["originalFileName"]    = journal.Original,
                                            ["quarantinedFileName"] = journal.Quarantined
                                        }).ToArray())
        };
        await File.WriteAllTextAsync(SessionStoreRestoreRecovery.RecordPathFor(storeDirectory),
                                     document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    ///     <para>在恢复改动任何文件之前完成全部角色校验；先放一份必须被拒绝的记录，再确认文件未被触碰。</para>
    ///     <para>Runs all role validation before recovery changes any file: writes a record that must be refused, then confirms nothing was touched.</para>
    /// </summary>
    [Test]
    public async Task RecoverAsync_RoleInvalidRecord_DoesNotMoveOrDeleteAnyFile()
    {
        using var fixture = new BackupFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId);
        await fixture.MigrateAsync();
        var databasePath = SessionStoreDatabase.PathFor(fixture.StoreDirectory);
        var quarantined  = SessionStoreRestoreRecovery.NewQuarantinePath(databasePath + "-wal");
        await File.WriteAllTextAsync(quarantined, "stale-journal-that-must-not-be-discarded");
        var recordPath = SessionStoreRestoreRecovery.RecordPathFor(fixture.StoreDirectory);
        // 名字形状合法但原日志写成业务库主文件名：一旦放行，Apply 会把这个文件当成上一代日志删掉。
        await WriteRawRecordAsync(fixture.StoreDirectory, SessionStoreRestoreRecovery.RestoreOperation,
                                  Path.GetFileName(SessionStoreRestoreRecovery.NewPendingCopyPath(
                                      databasePath, SessionStoreRestoreRecovery.RestoreOperation)),
                                  [(SessionStoreDatabase.DatabaseFileName, Path.GetFileName(quarantined))]);

        var exception = await TestAssert.ThrowsAsync<SessionStoreBackupException>(
            () => SessionStoreRestoreRecovery.RecoverAsync(fixture.StoreDirectory, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(exception.Kind, Is.EqualTo(SessionStoreFailureKind.Damaged));
            Assert.That(exception.Message, Does.Contain(recordPath), "错误必须指出需要人工处理的记录路径。");
            Assert.That(File.Exists(quarantined), Is.True, "恢复被拒绝时不得删除记录点名的任何文件。");
            Assert.That(File.ReadAllText(quarantined), Is.EqualTo("stale-journal-that-must-not-be-discarded"));
            Assert.That(File.Exists(recordPath), Is.True, "被拒绝的记录必须保留，业务库才能保持不可用。");
        });
    }

    /// <summary>
    ///     <para>确认写入端也不会产出自己拒绝的记录：不符合替换窗口形状的待提交副本在落盘之前就被拒绝。</para>
    ///     <para>Confirms the writer never produces a record the reader would refuse: a pending copy that does not fit the replacement shape is rejected before anything is written.</para>
    /// </summary>
    [Test]
    public async Task WriteAsync_RejectsPendingCopyOutsideTheReplacementShape()
    {
        using var fixture = new BackupFixture();
        Directory.CreateDirectory(fixture.StoreDirectory);
        var databasePath = SessionStoreDatabase.PathFor(fixture.StoreDirectory);
        var target       = await TestAssert.ThrowsAsync<SessionStoreBackupException>(
            () => SessionStoreRestoreRecovery.WriteAsync(fixture.StoreDirectory,
                                                         SessionStoreRestoreRecovery.RestoreOperation, databasePath, [],
                                                         CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(target.Kind, Is.EqualTo(SessionStoreFailureKind.Damaged));
            Assert.That(File.Exists(SessionStoreRestoreRecovery.RecordPathFor(fixture.StoreDirectory)), Is.False,
                        "被拒绝的记录不得落盘。");
        });
    }

    /// <summary>
    ///     <para>
    ///         恢复记录中的隔离日志无法放回时，恢复必须报告失败并保留记录，业务库保持不可用；错误信息要同时
    ///         指出放回失败的文件与记录路径，调用方才知道需要手工处理哪一个。
    ///     </para>
    ///     <para>
    ///         When a quarantined journal named by the recovery record cannot be put back, recovery must report failure and
    ///         keep the record so the business database stays unavailable; the message must name both the file that could
    ///         not be put back and the record path so the caller knows what to resolve by hand.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Recovery_WhenJournalCannotBePutBack_KeepsRecordAndReportsPath()
    {
        using var fixture = new BackupFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId);
        await fixture.MigrateAsync();
        var databasePath = SessionStoreDatabase.PathFor(fixture.StoreDirectory);
        var walPath      = databasePath + "-wal";
        var pendingCopy  = SessionStoreRestoreRecovery.NewPendingCopyPath(
            databasePath, SessionStoreRestoreRecovery.RestoreOperation);
        var quarantined = SessionStoreRestoreRecovery.NewQuarantinePath(walPath);
        await File.WriteAllTextAsync(walPath, "committed-content-only-in-the-old-wal");
        File.Move(walPath, quarantined);
        await File.WriteAllTextAsync(pendingCopy, "pending-copy-the-replacement-never-consumed");
        await SessionStoreRestoreRecovery.WriteAsync(
            fixture.StoreDirectory, SessionStoreRestoreRecovery.RestoreOperation, pendingCopy,
            [new SessionStoreJournalQuarantine(walPath, quarantined)], CancellationToken.None);

        using (new FileStream(quarantined, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var recordPath = SessionStoreRestoreRecovery.RecordPathFor(fixture.StoreDirectory);
            var exception = await TestAssert.ThrowsAsync<SessionStoreBackupException>(
                () => SessionStoreRestoreRecovery.RecoverAsync(fixture.StoreDirectory, CancellationToken.None));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Does.Contain(walPath), "错误必须指出放回失败的日志路径。");
                Assert.That(exception.Message, Does.Contain(quarantined), "错误必须指出隔离副本当前所在位置。");
                Assert.That(exception.Message, Does.Contain(recordPath), "错误必须指出需要人工处理的记录路径。");
                Assert.That(File.Exists(recordPath), Is.True, "恢复失败时记录必须保留，业务库才能保持不可用。");
                Assert.That(File.Exists(pendingCopy), Is.True, "恢复失败时待提交副本必须保留，它是替换未发生的证据。");
            });

            // 占用仍在时打开业务库：记录收不了尾，必须按不可用报告而不是读到缺少最新数据的旧主库。
            var blocked = await new SessionStoreDatabase(fixture.StoreDirectory)
               .GetAvailabilityAsync(CancellationToken.None);
            Assert.That(blocked.Status, Is.EqualTo(SessionStoreDatabaseStatus.Failed),
                        "文件代际未恢复一致前，业务库不得报告为可用。");
        }
        // 释放独占句柄后同一条记录必须能自行收尾：恢复是幂等的，不需要人工删除记录。
        await SessionStoreRestoreRecovery.RecoverAsync(fixture.StoreDirectory, CancellationToken.None);
        // 文件断言必须在打开业务库之前：SQLite 打开时会把无法识别的 WAL 文件重置，届时断言就会看错对象。
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(walPath), Is.True, "解除占用后隔离日志必须放回原日志名。");
            Assert.That(File.ReadAllText(walPath), Is.EqualTo("committed-content-only-in-the-old-wal"),
                        "放回的日志内容不得被改写。");
            Assert.That(File.Exists(SessionStoreRestoreRecovery.RecordPathFor(fixture.StoreDirectory)), Is.False);
        });
        Assert.That((await new SessionStoreDatabase(fixture.StoreDirectory)
                       .GetAvailabilityAsync(CancellationToken.None)).Status,
                    Is.EqualTo(SessionStoreDatabaseStatus.Available));
    }

    /// <summary>
    ///     <para>
    ///         在真实恢复流程里越过隔离点后由测试接缝抛出取消：待提交副本与隔离日志一并留在磁盘上，恢复记录已经落盘。
    ///         下一次打开业务库必须先按记录把日志放回原日志名；旧主库数据不得因为"日志被改名为隔离名"而丢失。
    ///     </para>
    ///     <para>
    ///         Throws a cancellation from the test seam right after a real restore crosses the quarantine point: the
    ///         pending copy and the quarantined journals stay on disk and the recovery record is already durable. The
    ///         next open must put the journals back under their journal names, so the old main file never loses data
    ///         because its journal was left under a quarantined name.
    ///     </para>
    ///     <para>
    ///         接缝异常在同一测试进程内被捕获，模拟的是"磁盘停在该窗口"，不是真实的子进程终止；WAL 为文本夹具，
    ///         因此未覆盖真实终止及 SQLite 对该日志的重放。
    ///     </para>
    ///     <para>
    ///         The seam exception is caught inside the same test process, so it reproduces the on-disk state of that
    ///         window rather than a real child-process termination, and the WAL is a text fixture, so neither a real
    ///         termination nor SQLite replay of that journal is covered.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Restore_ProcessTerminatedAfterQuarantine_IsRecoveredOnNextOpen()
    {
        using var fixture = new BackupFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId);
        await fixture.MigrateAsync();
        await SessionStoreBackup.CreateAsync(fixture.StoreDirectory, fixture.BackupDirectory, CancellationToken.None);
        // 与"旧 WAL 无法隔离"用例同一构造：业务库缺失、只剩上一代的 WAL 等待隔离。活动连接会先让恢复前的复制失败并
        // 中止，因此这里不持有连接，只留下日志文件本身。
        fixture.DeleteDatabase();
        var databasePath = SessionStoreDatabase.PathFor(fixture.StoreDirectory);
        var walPath      = databasePath + "-wal";
        await File.WriteAllTextAsync(walPath, "stale-journal-of-the-replaced-generation");

        var interrupted = false;
        var recordWasDurable = false;
        byte[]? recordBytes = null;
        string[] quarantinedPaths = [];
        SessionStoreBackup.QuarantineCommittedHookForTests = (storePath, recordPending, quarantined, _) =>
        {
            interrupted = true;
            // 终止点：日志已隔离、替换尚未提交。先断言记录在隔离之前就已落盘——顺序反了就没有可依赖的恢复依据。
            recordWasDurable = recordPending;
            recordBytes = File.ReadAllBytes(SessionStoreRestoreRecovery.RecordPathFor(storePath));
            quarantinedPaths = quarantined;
            throw new OperationCanceledException("simulated process termination");
        };

        try
        {
            await TestAssert.ThrowsAsync<OperationCanceledException>(
                () => SessionStoreBackup.RestoreAsync(fixture.StoreDirectory, fixture.BackupDirectory,
                                                      CancellationToken.None));
        }
        finally
        {
            SessionStoreBackup.QuarantineCommittedHookForTests = null;
        }

        Assert.That(interrupted, Is.True, "测试必须真的越过隔离点，否则窗口没有覆盖。");
        Assert.That(recordWasDurable, Is.True,
                    "恢复记录必须在隔离任何日志之前落盘，否则进程在隔离后被终止时无从恢复。");
        Assert.That(quarantinedPaths, Has.Length.EqualTo(1), "旧 WAL 必须已在隔离名下。");
        // 记录在隔离之前已经落盘，且必须点名真实的隔离文件；恢复依赖这一点才能找到日志。
        var recordText = TextOf(recordBytes!);
        Assert.That(recordText, Does.Contain(Path.GetFileName(quarantinedPaths[0])),
                    "恢复记录必须在隔离之前写入并点名被移开的日志。");
        Assert.That(recordText, Does.Contain(SessionStoreDatabase.DatabaseFileName + "-wal"),
                    "恢复记录必须点名原日志文件名。");
        var recordPath = SessionStoreRestoreRecovery.RecordPathFor(fixture.StoreDirectory);
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(recordPath), Is.True, "隔离之后、替换之前被终止时必须留下恢复记录。");
            Assert.That(File.Exists(walPath), Is.False, "日志此时停在隔离名下。");
            Assert.That(Directory.EnumerateFiles(fixture.StoreDirectory, "*.restore-*.tmp").ToArray(),
                        Has.Length.EqualTo(1), "待提交副本必须保留，它是替换未发生的证据。");
        });

        // 下一次打开：恢复必须把隔离日志放回原日志名并清除记录，然后才允许按业务库缺失处理。
        await SessionStoreRestoreRecovery.RecoverAsync(fixture.StoreDirectory, CancellationToken.None);
        // 文件断言必须在打开业务库之前：SQLite 打开时会重置无法识别的 WAL，届时断言就会看错对象。
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(walPath), Is.True, "下一次打开必须把隔离日志放回原日志名。");
            Assert.That(File.ReadAllText(walPath), Is.EqualTo("stale-journal-of-the-replaced-generation"),
                        "放回的日志内容不得被改写。");
            Assert.That(File.Exists(recordPath), Is.False, "恢复收尾后必须删除恢复记录。");
            Assert.That(Directory.EnumerateFiles(fixture.StoreDirectory, "*.restore-*.tmp"), Is.Empty,
                        "恢复收尾后不得留下待提交副本。");
        });
    }

    /// <summary>
    ///     <para>按 UTF-8 解码记录字节，供断言读取真实写入的记录内容。</para>
    ///     <para>Decodes record bytes as UTF-8 so assertions read what was actually written.</para>
    /// </summary>
    private static string TextOf(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes);

    /// <summary>
    ///     <para>
    ///         待收尾的恢复记录必须先于文件叶子判断被处理：否则主文件缺失会被报成"尚未迁移"，调用方可能据此走首次
    ///         导入，把一次未收尾的恢复当成空 store。
    ///     </para>
    ///     <para>
    ///         A pending recovery record must be settled before the file leaf is judged: otherwise an absent main file
    ///         reports as "not migrated yet" and the caller may start a first import, turning an unsettled restore into
    ///         an empty store.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Availability_WithPendingRecordAndMissingDatabase_IsNotReportedAsMissing()
    {
        using var fixture = new BackupFixture();
        fixture.WriteDefaultSources();
        await fixture.WriteSessionAsync(fixture.BoundSessionId);
        await fixture.MigrateAsync();
        var databasePath = SessionStoreDatabase.PathFor(fixture.StoreDirectory);
        var missingCopy  = SessionStoreRestoreRecovery.NewPendingCopyPath(
            databasePath, SessionStoreRestoreRecovery.RestoreOperation);
        // 替换已提交（副本被消耗）但记录未清除；主文件随后被人工移走，正是"缺失但并非首次迁移"的组合。
        await SessionStoreRestoreRecovery.WriteAsync(fixture.StoreDirectory,
                                                      SessionStoreRestoreRecovery.RestoreOperation, missingCopy, [],
                                                      CancellationToken.None);
        fixture.DeleteDatabase();

        var availability = await new SessionStoreDatabase(fixture.StoreDirectory)
           .GetAvailabilityAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(availability.Status, Is.EqualTo(SessionStoreDatabaseStatus.Missing),
                        "记录收尾后主文件确实缺失，应如实报告缺失。");
            Assert.That(File.Exists(SessionStoreRestoreRecovery.RecordPathFor(fixture.StoreDirectory)), Is.False,
                        "可收尾的记录必须在报告状态之前被清除。");
        });
    }

    /// <summary>
    ///     <para>备份测试的隔离 store 夹具：唯一临时目录、合法旧来源、一次真实迁移与清理。</para>
    ///     <para>The isolated store fixture of the backup tests: a unique temporary directory, valid legacy sources, one real migration, and cleanup.</para>
    /// </summary>
    private sealed class BackupFixture : IDisposable
    {
        public BackupFixture(string prefix = "tinyharness-session-store-backup")
        {
            Root           = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
            StoreDirectory = Path.Combine(Root, "store");
            Workspace      = Path.GetFullPath(Path.Combine(Root, "workspace"));
            BackupDirectory = Path.Combine(Root, "backup");
            Directory.CreateDirectory(StoreDirectory);
            Directory.CreateDirectory(Workspace);
            ProjectId         = NewId();
            ArchivedSessionId = NewId();
            BoundSessionId    = NewId();
        }

        public string Root { get; }

        public string StoreDirectory { get; }

        public string Workspace { get; }

        public string BackupDirectory { get; }

        public string ProjectId { get; }

        public string ArchivedSessionId { get; }

        public string BoundSessionId { get; }

        public string MarkerStoreIdentity => SessionStoreMigration
                                            .ReadMarkerAsync(StoreDirectory, CancellationToken.None).GetAwaiter()
                                            .GetResult()
                                            .Marker!.StoreIdentity!;

        /// <summary>
        ///     <para>创建带 32 位小写十六进制格式的随机 ID。</para>
        ///     <para>Creates a random 32-character lowercase hexadecimal id.</para>
        /// </summary>
        public static string NewId() => Guid.NewGuid().ToString("N");

        /// <summary>
        ///     <para>写入默认旧来源：一个项目、置顶项目与会话、一个归档会话与侧栏顺序。</para>
        ///     <para>Writes the default legacy sources: one project, pinned project and session, one archived session, and sidebar order.</para>
        /// </summary>
        public void WriteDefaultSources()
        {
            var now = DateTimeOffset.UtcNow;
            var project = new Project
            {
                Id          = ProjectId,
                Name        = "Alpha",
                Roots       = [new ProjectRoot(Workspace)],
                PrimaryRoot = Workspace,
                Revision    = 1,
                CreatedUtc  = now,
                UpdatedUtc  = now
            };
            File.WriteAllBytes(ProjectStore.FilePath(StoreDirectory),
                               JsonSerializer.SerializeToUtf8Bytes(new ProjectCatalog
                               {
                                   Schema   = ProjectCatalog.CurrentSchema,
                                   Revision = 1,
                                   Projects = [project]
                               }, ProjectJsonContext.Default.ProjectCatalog));
            File.WriteAllBytes(SessionStorePinStore.FilePath(StoreDirectory),
                               JsonSerializer.SerializeToUtf8Bytes(new SessionStorePins
                               {
                                   Schema           = SessionStorePins.CurrentSchema,
                                   PinnedProjectIds = [ProjectId],
                                   PinnedSessionIds = [ArchivedSessionId]
                               }, SessionStorePinJsonContext.Default.SessionStorePins));
            File.WriteAllBytes(SessionStoreArchiveStore.FilePath(StoreDirectory),
                               JsonSerializer.SerializeToUtf8Bytes(new SessionStoreArchives
                               {
                                   Schema             = SessionStoreArchives.CurrentSchema,
                                   ArchivedSessionIds = [ArchivedSessionId]
                               }, SessionStoreArchiveJsonContext.Default.SessionStoreArchives));
            var sidebar = new JsonObject
            {
                ["schema"] = "tinyharness.desktop-sidebar/1",
                ["stores"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["storeDirectory"]     = StoreDirectory,
                        ["manualProjectOrder"] = new JsonArray { ProjectId },
                        ["manualSessionOrder"] = new JsonArray { BoundSessionId },
                        ["collapsedGroupKeys"] = new JsonArray { "group-a" }
                    }
                }
            };
            File.WriteAllText(Path.Combine(Root, "desktop-sidebar.json"),
                              sidebar.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>
        ///     <para>用真实写入器提交一条已归属项目的会话日志。</para>
        ///     <para>Commits a project-bound session log through the real writer.</para>
        /// </summary>
        public async Task WriteSessionAsync(string sessionId)
        {
            using var writer = new SessionStoreWriter(StoreDirectory, sessionId);
            var model = new SessionModelIdentity("test-model", "https://example.invalid/v1",
                                                 ChatApiKind.ChatCompletions);
            var session = new ChatSession(sessionId, Workspace, model, DateTimeOffset.UtcNow, ProjectId);
            await writer.CommitAsync(session, new ConversationContext(new ContextOptions()), CancellationToken.None);
        }

        /// <summary>
        ///     <para>执行一次真实迁移。</para>
        ///     <para>Runs one real migration.</para>
        /// </summary>
        public async Task MigrateAsync() =>
            await SessionStoreMigration.MigrateAsync(
                new SessionStoreMigrationOptions(StoreDirectory)
                {
                    SidebarPreferencesPath = Path.Combine(Root, "desktop-sidebar.json")
                }, CancellationToken.None);

        /// <summary>
        ///     <para>读取全部旧文件的字节，用于断言恢复没有改写它们。</para>
        ///     <para>Reads the bytes of every legacy file to assert that restore never rewrote them.</para>
        /// </summary>
        public Dictionary<string, byte[]> ReadLegacyBytes()
        {
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var path in new[]
                     {
                         ProjectStore.FilePath(StoreDirectory), SessionStorePinStore.FilePath(StoreDirectory),
                         SessionStoreArchiveStore.FilePath(StoreDirectory),
                         Path.Combine(Root, "desktop-sidebar.json")
                     })
                if (File.Exists(path))
                    result[path] = File.ReadAllBytes(path);
            return result;
        }

        /// <summary>
        ///     <para>删除业务库及其 WAL／SHM。</para>
        ///     <para>Deletes the business database and its WAL／SHM.</para>
        /// </summary>
        public void DeleteDatabase()
        {
            var path = SessionStoreDatabase.PathFor(StoreDirectory);
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(candidate))
                    File.Delete(candidate);
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

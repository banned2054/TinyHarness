using Microsoft.Data.Sqlite;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         索引版本 bump 契约测试（D9）：旧版本报告 NeedsRebuild 并整库重建、更高版本保持 Unsupported 且拒绝重建、
///         permission_mode 列的旧模式字符串容忍解析、未知值回落 null 不抛异常。
///     </para>
///     <para>
///         Contract tests for the index version bump (D9): older versions report NeedsRebuild and rebuild
///         wholesale, newer versions stay Unsupported and refuse rebuilds, the permission_mode column parses
///         legacy mode strings tolerantly, and unknown values fall back to null without throwing.
///     </para>
/// </summary>
public class SessionIndexStoreVersionTests
{
    private static string NewStoreDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-idx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<SessionIndexStore> InitializeAsync(string storeDir)
    {
        var store = new SessionIndexStore(storeDir);
        await store.InitializeAsync(CancellationToken.None);
        return store;
    }

    /// <summary>
    ///     打开测试用的原始 SQLite 连接；与索引存储一致禁用连接池，避免测试清理时文件仍被池持有。
    /// </summary>
    private static SqliteConnection OpenRawConnection(string storeDir)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = SessionIndexStore.PathFor(storeDir), Mode = SqliteOpenMode.ReadWrite, Pooling = false
        };
        return new SqliteConnection(builder.ToString());
    }

    /// <summary>把已初始化索引的 schema_version 改写为指定值，模拟旧程序或新程序写入的版本。</summary>
    private static async Task ForceSchemaVersionAsync(string storeDir, string version)
    {
        await using var connection = OpenRawConnection(storeDir);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE index_meta SET value = $version WHERE key = 'schema_version'";
        command.Parameters.AddWithValue("$version", version);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>直接插入一行健康摘要；permission_mode 用原始字符串以便构造旧值或损坏值。</summary>
    private static async Task InsertRawRowAsync(string storeDir, string sessionId, string? permissionMode)
    {
        await using var connection = OpenRawConnection(storeDir);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_summary (
                id, workspace_root, project_id, assignment_state, title, created_utc, updated_utc,
                last_activity_utc, turn_count, permission_mode, commit_sequence, indexed_bytes,
                source_length, source_modified_utc, entry_state, error, archived
            ) VALUES (
                $id, $root, NULL, 0, 'legacy', 1, 1, 1, 1, $permission, 1, 1, 1, NULL, 1, NULL, NULL
            );
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$root", storeDir);
        command.Parameters.AddWithValue("$permission", (object?)permissionMode ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task OlderSchemaVersion_ReportsNeedsRebuild_AndRebuildResetsToCurrent()
    {
        var storeDir = NewStoreDir();
        try
        {
            var store = await InitializeAsync(storeDir);
            await ForceSchemaVersionAsync(storeDir, "1");

            var availability = await store.GetAvailabilityAsync(CancellationToken.None);
            Assert.That(availability.Status, Is.EqualTo(SessionIndexAvailabilityStatus.NeedsRebuild));

            await store.RebuildSchemaAsync(CancellationToken.None);

            var rebuilt = await store.GetAvailabilityAsync(CancellationToken.None);
            Assert.That(rebuilt.Status, Is.EqualTo(SessionIndexAvailabilityStatus.Available));
            var rows = await store.ListRowsAsync(CancellationToken.None);
            Assert.That(rows, Is.Empty);
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    [Test]
    public async Task LegacyPermissionModeString_ParsesTolerantlyAndUnknownValueFallsBackToNull()
    {
        var storeDir = NewStoreDir();
        try
        {
            var store = await InitializeAsync(storeDir);
            await InsertRawRowAsync(storeDir, new string('a', 32), "ConfirmEveryAction");
            await InsertRawRowAsync(storeDir, new string('b', 32), "NotAModeAtAll");

            var legacyRow = await store.GetRowAsync(new string('a', 32), CancellationToken.None);
                        Assert.That(legacyRow!.Summary!.PermissionModeName,
                        Is.EqualTo(LegacyPermissionSelection.KnownLegacyModeNames[0]));

            var damagedRow = await store.GetRowAsync(new string('b', 32), CancellationToken.None);
            Assert.That(damagedRow!.Summary!.PermissionModeName, Is.Null);
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    [Test]
    public async Task NewerSchemaVersion_StaysUnsupported_AndRebuildRefuses()
    {
        var storeDir = NewStoreDir();
        try
        {
            var store = await InitializeAsync(storeDir);
            await ForceSchemaVersionAsync(storeDir, "99");

            var availability = await store.GetAvailabilityAsync(CancellationToken.None);
            Assert.That(availability.Status, Is.EqualTo(SessionIndexAvailabilityStatus.Unsupported));

            var ex = await TestAssert.ThrowsAsync<SessionIndexException>(
                () => store.RebuildSchemaAsync(CancellationToken.None));
            Assert.That(ex.Message, Does.Contain("must not be downgraded"));

            var unchanged = await store.GetAvailabilityAsync(CancellationToken.None);
            Assert.That(unchanged.Status, Is.EqualTo(SessionIndexAvailabilityStatus.Unsupported));
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    [Test]
    public async Task ReconcileAsync_RebuildsOlderVersionIndexAndReportsAvailable()
    {
        var storeDir = NewStoreDir();
        try
        {
            var store = await InitializeAsync(storeDir);
            await ForceSchemaVersionAsync(storeDir, "1");

            var reconciler = new SessionIndexReconciler(store);
            var report = await reconciler.ReconcileAsync(new SessionIndexReconcileOptions(),
                                                         CancellationToken.None);

            Assert.That(report.Availability.Status, Is.EqualTo(SessionIndexAvailabilityStatus.Available));
            var version = await ReadSchemaVersionAsync(storeDir);
            Assert.That(version, Is.EqualTo(SessionIndexStore.CurrentSchemaVersion.ToString()));
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    private static async Task<string?> ReadSchemaVersionAsync(string storeDir)
    {
        await using var connection = OpenRawConnection(storeDir);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM index_meta WHERE key = 'schema_version'";
        return await command.ExecuteScalarAsync() as string;
    }
}

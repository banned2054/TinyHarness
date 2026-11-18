using Microsoft.Data.Sqlite;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         业务数据库 schema 的冻结测试：建库只发生在缺失或尚空时、缺表与外来内容判损坏且不补建、
///         版本更高拒绝任何写入、版本更低只走升级、缺失库不得凭空建成空库，以及业务表约束与
///         冻结的表/列/索引清单。
///     </para>
///     <para>
///         Frozen-schema tests for the business database: creation happens only when missing or still empty,
///         missing tables and foreign content count as damage without repair, a newer version refuses every
///         write, an older one only upgrades, a missing database is never invented as empty, and the business
///         constraints plus the frozen table/column/index lists hold.
///     </para>
/// </summary>
public class SessionStoreDatabaseTests
{
    /// <summary>
    ///     <para>当前版本冻结的业务表清单；增删都意味着 schema 迁移与新旧测试同时更新。</para>
    ///     <para>The frozen business tables of the current schema; any addition or removal means a schema migration and an update here.</para>
    /// </summary>
    private static readonly string[] FrozenTables =
    [
        "archived_session",
        "collapsed_group",
        "pinned_project",
        "pinned_session",
        "project",
        "project_order",
        "session_placement",
        "session_order",
        "session_tombstone",
        "store_meta"
    ];

    private static string NewStoreDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-store-db-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>打开测试用的原始 SQLite 连接；与业务库一致禁用连接池，避免清理时文件仍被池持有。</summary>
    private static async Task<SqliteConnection> OpenRawAsync(
        string storeDir, SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = SessionStoreDatabase.PathFor(storeDir),
            Mode       = mode,
            Pooling    = false
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<SqliteConnection> OpenRawWithForeignKeysAsync(string storeDir)
    {
        var connection = await OpenRawAsync(storeDir);
        var pragma     = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON";
        await pragma.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task ExecuteAsync(string storeDir, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenRawAsync(storeDir);
        var             command    = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ForceSchemaVersionAsync(string storeDir, string version) =>
        await ExecuteAsync(storeDir,
                           $"UPDATE {SessionStoreDatabase.MetaTableName} SET value = $version " +
                           $"WHERE key = '{SessionStoreDatabase.SchemaVersionKey}'",
                           ("$version", version));

    private static async Task<string?> ReadMetaAsync(string storeDir, string key)
    {
        await using var connection = await OpenRawAsync(storeDir);
        var             command    = connection.CreateCommand();
        command.CommandText = $"SELECT value FROM {SessionStoreDatabase.MetaTableName} WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<HashSet<string>> ReadObjectNamesAsync(string storeDir, string type)
    {
        await using var connection = await OpenRawAsync(storeDir);
        var             command    = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type AND name NOT LIKE 'sqlite_%'";
        command.Parameters.AddWithValue("$type", type);
        await using var reader = await command.ExecuteReaderAsync();
        var             names  = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    /// <summary>读取元数据表的全部键，用于断言冻结结构与库身份仍然存在。</summary>
    private static async Task<HashSet<string>> ReadMetaKeysAsync(string storeDir)
    {
        await using var connection = await OpenRawAsync(storeDir);
        var             command    = connection.CreateCommand();
        command.CommandText = $"SELECT key FROM {SessionStoreDatabase.MetaTableName}";
        await using var reader = await command.ExecuteReaderAsync();
        var             keys   = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync()) keys.Add(reader.GetString(0));
        return keys;
    }

    private static async Task<HashSet<string>> ReadColumnNamesAsync(string storeDir, string table)
    {
        await using var connection = await OpenRawAsync(storeDir);
        var             command    = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await command.ExecuteReaderAsync();
        var             names  = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync()) names.Add(reader.GetString(1));
        return names;
    }

    /// <summary>插入一条满足全部约束的项目行，供后续断言"升级与初始化不丢业务数据"。</summary>
    private static Task InsertProjectAsync(string storeDir, string id = "00000000000000000000000000000001",
                                           string rootKey = @"c:\work\alpha") =>
        ExecuteAsync(storeDir, """
            INSERT INTO project (id, name, root_path, root_key, revision, is_deleting, created_utc, updated_utc)
            VALUES ($id, 'Alpha', $root, $key, 1, 0, 1000, 1000)
            """, ("$id", id), ("$root", rootKey), ("$key", rootKey));

    [Test]
    public async Task FreshStore_ReportsMissingWithoutCreatingFile()
    {
        var storeDir = NewStoreDir();
        try
        {
            var database = new SessionStoreDatabase(storeDir);
            var availability = await database.GetAvailabilityAsync(CancellationToken.None);

            Assert.That(availability.Status, Is.EqualTo(SessionStoreDatabaseStatus.Missing));
            Assert.That(File.Exists(database.DatabasePath), Is.False, "可用性探测不得创建业务库文件。");
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    [Test]
    public async Task Initialize_CreatesFrozenSchemaAndCanBeReopened()
    {
        var storeDir = NewStoreDir();
        try
        {
            var database = new SessionStoreDatabase(storeDir);
            await database.InitializeAsync(CancellationToken.None);

            var reopened = new SessionStoreDatabase(storeDir);
            Assert.That((await reopened.GetAvailabilityAsync(CancellationToken.None)).Status,
                        Is.EqualTo(SessionStoreDatabaseStatus.Available));
            Assert.That(SessionStoreDatabase.Exists(storeDir), Is.True);

            var metaKeys = await ReadMetaAsync(storeDir, SessionStoreDatabase.SchemaVersionKey);
            Assert.That(metaKeys, Is.EqualTo(SessionStoreDatabase.CurrentSchemaVersion.ToString()));
            Assert.That(await ReadMetaAsync(storeDir, SessionStoreDatabase.StoreIdentityKey),
                        Does.Match("^[0-9a-f]{32}$"));
            Assert.That(await ReadMetaAsync(storeDir, SessionStoreDatabase.CreatedUtcKey), Is.Not.Null);
            // 元数据键也是冻结契约：缺库身份或建库时间说明结构不完整。
            Assert.That(await ReadMetaKeysAsync(storeDir),
                        Is.EquivalentTo([SessionStoreDatabase.SchemaVersionKey,
                                         SessionStoreDatabase.StoreIdentityKey,
                                         SessionStoreDatabase.CreatedUtcKey]));
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    [Test]
    public async Task Schema_FreezesCurrentTablesColumnsAndIndexes()
    {
        var storeDir = NewStoreDir();
        try
        {
            await new SessionStoreDatabase(storeDir).InitializeAsync(CancellationToken.None);

            // 表与索引清单是冻结契约：增删改都必须同时改版本迁移与这份断言。
            Assert.That(await ReadObjectNamesAsync(storeDir, "table"), Is.EquivalentTo(FrozenTables));
            Assert.That(await ReadObjectNamesAsync(storeDir, "index"),
                        Is.EquivalentTo(["ix_session_placement_project", "ix_session_tombstone_state",
                                         "ux_project_root_key"]));

            Assert.That(await ReadColumnNamesAsync(storeDir, "project"),
                        Is.EquivalentTo(["id", "name", "root_path", "root_key", "revision", "is_deleting",
                                         "created_utc", "updated_utc"]));
            // 归属行存在即已决定：project_id 非空为项目，为空是显式无项目，整行缺失才表示尚未迁移。
            Assert.That(await ReadColumnNamesAsync(storeDir, "session_placement"),
                        Is.EquivalentTo(["session_id", "project_id", "updated_utc"]));
            Assert.That(await ReadColumnNamesAsync(storeDir, "archived_session"), Is.EquivalentTo(["session_id"]));
            Assert.That(await ReadColumnNamesAsync(storeDir, "collapsed_group"), Is.EquivalentTo(["group_key"]));
            // 版本 2 的删除墓碑：状态只有 pending/deleted，请求与更新时间都是整数毫秒。
            Assert.That(await ReadColumnNamesAsync(storeDir, SessionStoreDatabase.TombstoneTableName),
                        Is.EquivalentTo(["session_id", "state", "requested_utc", "updated_utc"]));
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    [Test]
    public async Task Upgrade_FromVersionOne_AddsTombstonesAndKeepsBusinessRows()
    {
        var storeDir = NewStoreDir();
        try
        {
            var database = new SessionStoreDatabase(storeDir);
            await database.InitializeAsync(CancellationToken.None);
            await InsertProjectAsync(storeDir);

            // 构造真实的版本 1 结构：删掉版本 2 新增的对象并把版本号退回 1，其余业务行保持原样。
            // 墓碑表自带的索引随表一起消失，无需单独删除。
            await ExecuteAsync(storeDir, $"DROP TABLE {SessionStoreDatabase.TombstoneTableName}");
            await ForceSchemaVersionAsync(storeDir, "1");

            var before = await database.GetAvailabilityAsync(CancellationToken.None);
            Assert.That(before.Status, Is.EqualTo(SessionStoreDatabaseStatus.NeedsUpgrade));

            await database.UpgradeAsync(CancellationToken.None);

            var after = await database.GetAvailabilityAsync(CancellationToken.None);
            Assert.That(after.Status, Is.EqualTo(SessionStoreDatabaseStatus.Available));
            Assert.That(await ReadMetaAsync(storeDir, SessionStoreDatabase.SchemaVersionKey),
                        Is.EqualTo(SessionStoreDatabase.CurrentSchemaVersion.ToString()));
            Assert.That(await ReadObjectNamesAsync(storeDir, "table"), Has.Member(SessionStoreDatabase.TombstoneTableName),
                        "升级必须补上版本 2 的墓碑表。");
            Assert.That(await ReadObjectNamesAsync(storeDir, "index"),
                        Has.Member(SessionStoreDatabase.TombstoneStateIndex));
            await using var connection = await OpenRawAsync(storeDir);
            var             command    = connection.CreateCommand();
            command.CommandText = "SELECT name FROM project";
            Assert.That(await command.ExecuteScalarAsync(), Is.EqualTo("Alpha"), "升级不得丢失既有业务行。");
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    [Test]
    public async Task Initialize_WhenAvailable_IsNoOpAndPreservesBusinessRows()
    {
        var storeDir = NewStoreDir();
        try
        {
            var database = new SessionStoreDatabase(storeDir);
            await database.InitializeAsync(CancellationToken.None);
            await InsertProjectAsync(storeDir);
            var identity = await ReadMetaAsync(storeDir, SessionStoreDatabase.StoreIdentityKey);

            await database.InitializeAsync(CancellationToken.None);

            Assert.That(await ReadMetaAsync(storeDir, SessionStoreDatabase.StoreIdentityKey), Is.EqualTo(identity),
                        "重复初始化不得重建库身份。");
            Assert.That(await ReadMetaAsync(storeDir, SessionStoreDatabase.SchemaVersionKey),
                        Is.EqualTo(SessionStoreDatabase.CurrentSchemaVersion.ToString()));
            await using var connection = await OpenRawAsync(storeDir);
            var             command    = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM project";
            Assert.That(Convert.ToInt64(await command.ExecuteScalarAsync()), Is.EqualTo(1L));
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    [Test]
    public async Task Upgrade_FromUnknownOlderStructure_IsRefusedAndLeavesTheDatabaseUntouched()
    {
        var storeDir = NewStoreDir();
        try
        {
            var database = new SessionStoreDatabase(storeDir);
            await database.InitializeAsync(CancellationToken.None);
            await InsertProjectAsync(storeDir);
            // 版本号退到 0 但表仍在：这不是任何已发布版本的结构。迁移脚本用 CREATE TABLE IF NOT EXISTS，
            // 若放行就会把缺失表补成空表并宣称升级成功，等于把数据丢失伪装成升级。
            await ForceSchemaVersionAsync(storeDir, "0");

            var before = await database.GetAvailabilityAsync(CancellationToken.None);
            Assert.That(before.Status, Is.EqualTo(SessionStoreDatabaseStatus.NeedsUpgrade));

            var exception = await TestAssert.ThrowsAsync<SessionStoreDatabaseException>(
                () => database.UpgradeAsync(CancellationToken.None));

            Assert.That(exception.Message, Does.Contain("Refusing to migrate an unknown structure"));
            Assert.That(await ReadMetaAsync(storeDir, SessionStoreDatabase.SchemaVersionKey), Is.EqualTo("0"),
                        "被拒绝的升级不得写入新版本号。");
            await using var connection = await OpenRawAsync(storeDir);
            var             command    = connection.CreateCommand();
            command.CommandText = "SELECT name FROM project";
            Assert.That(await command.ExecuteScalarAsync(), Is.EqualTo("Alpha"), "拒绝升级时业务行保持原样。");
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    [Test]
    public async Task Upgrade_WhenCurrentStructureIsIncomplete_IsRefusedAndNeverRecreatesTables()
    {
        var storeDir = NewStoreDir();
        try
        {
            var database = new SessionStoreDatabase(storeDir);
            await database.InitializeAsync(CancellationToken.None);
            await ExecuteAsync(storeDir, "DROP TABLE archived_session");

            // 当前版本已被判定为损坏：升级入口同样拒绝，且不会借 IF NOT EXISTS 把表补回来。
            var exception = await TestAssert.ThrowsAsync<SessionStoreDatabaseException>(
                () => database.UpgradeAsync(CancellationToken.None));

            Assert.That(exception.Message, Does.Contain("cannot be upgraded"));
            Assert.That(await ReadObjectNamesAsync(storeDir, "table"), Has.No.Member("archived_session"),
                        "拒绝升级时不得补建缺失表。");
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    [Test]
    public async Task Initialize_WhenMigrationProducesIncompleteStructure_RollsBackEverything()
    {
        var storeDir = NewStoreDir();
        try
        {
            // 故意漏建 project 的迁移夹具：用于确定性覆盖"结构核对在事务内、失败即回滚"。
            var broken = new SessionStoreDatabase.SchemaMigration(
                1,
                """
                CREATE TABLE IF NOT EXISTS store_meta (
                    key   TEXT NOT NULL PRIMARY KEY,
                    value TEXT NOT NULL
                ) WITHOUT ROWID;
                """,
                new SessionStoreDatabase.SchemaShape(["store_meta", "project"], []));
            var database = new SessionStoreDatabase(storeDir, [broken]);

            var exception = await TestAssert.ThrowsAsync<SessionStoreDatabaseException>(
                () => database.InitializeAsync(CancellationToken.None));

            Assert.That(exception.Message, Does.Contain("incomplete structure"));
            Assert.That(await ReadObjectNamesAsync(storeDir, "table"), Is.Empty,
                        "结构核对失败必须回滚整事务，连 store_meta 也不得留下。");
            // 回滚干净的库应重新被当作"尚无 schema"，说明没有永久记录新版本号。
            Assert.That((await new SessionStoreDatabase(storeDir).GetAvailabilityAsync(CancellationToken.None)).Status,
                        Is.EqualTo(SessionStoreDatabaseStatus.NeedsSchema));
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    [Test]
    public async Task Upgrade_MissingDatabase_IsRefusedAndCreatesNothing()
    {
        var storeDir = NewStoreDir();
        try
        {
            var database = new SessionStoreDatabase(storeDir);
            var exception = await TestAssert.ThrowsAsync<SessionStoreDatabaseException>(
                () => database.UpgradeAsync(CancellationToken.None));

            Assert.That(exception.Message, Does.Contain("missing"));
            Assert.That(File.Exists(database.DatabasePath), Is.False, "业务数据只能备份恢复或由后续阶段导入。");
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    [Test]
    public async Task NewerSchemaVersion_IsUnsupportedAndRefusesEveryEntryPoint()
    {
        var storeDir = NewStoreDir();
        try
        {
            var database = new SessionStoreDatabase(storeDir);
            await database.InitializeAsync(CancellationToken.None);
            await InsertProjectAsync(storeDir);
            await ForceSchemaVersionAsync(storeDir, "99");

            var availability = await database.GetAvailabilityAsync(CancellationToken.None);
            Assert.That(availability.Status, Is.EqualTo(SessionStoreDatabaseStatus.Unsupported));

            var initialize = await TestAssert.ThrowsAsync<SessionStoreDatabaseException>(
                () => database.InitializeAsync(CancellationToken.None));
            Assert.That(initialize.Message, Does.Contain("must not be downgraded"));
            var upgrade = await TestAssert.ThrowsAsync<SessionStoreDatabaseException>(
                () => database.UpgradeAsync(CancellationToken.None));
            Assert.That(upgrade.Message, Does.Contain("must not be downgraded"));

            Assert.That(await ReadMetaAsync(storeDir, SessionStoreDatabase.SchemaVersionKey), Is.EqualTo("99"),
                        "更高版本的库不得被降级或改写。");
            await using var connection = await OpenRawAsync(storeDir);
            var             command    = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM project";
            Assert.That(Convert.ToInt64(await command.ExecuteScalarAsync()), Is.EqualTo(1L));
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    [Test]
    public async Task DamagedFile_IsReportedAndNeverRewritten()
    {
        var storeDir = NewStoreDir();
        try
        {
            var database = new SessionStoreDatabase(storeDir);
            await File.WriteAllTextAsync(database.DatabasePath, "this is not a sqlite database");

            var availability = await database.GetAvailabilityAsync(CancellationToken.None);
            Assert.That(availability.Status, Is.EqualTo(SessionStoreDatabaseStatus.Damaged));

            await TestAssert.ThrowsAsync<SessionStoreDatabaseException>(
                () => database.InitializeAsync(CancellationToken.None));

            Assert.That(await File.ReadAllTextAsync(database.DatabasePath),
                        Is.EqualTo("this is not a sqlite database"), "损坏文件必须原样保留，交由备份恢复。");
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    [Test]
    public async Task ForeignDatabase_IsDamagedAndNeverInitialized()
    {
        var storeDir = NewStoreDir();
        try
        {
            var database = new SessionStoreDatabase(storeDir);
            await using (var connection = await OpenRawAsync(storeDir, SqliteOpenMode.ReadWriteCreate))
            {
                var create = connection.CreateCommand();
                create.CommandText = "CREATE TABLE unrelated (value TEXT NOT NULL)";
                await create.ExecuteNonQueryAsync();
            }

            var availability = await database.GetAvailabilityAsync(CancellationToken.None);
            Assert.That(availability.Status, Is.EqualTo(SessionStoreDatabaseStatus.Damaged));
            Assert.That(availability.Detail, Does.Contain(SessionStoreDatabase.MetaTableName));

            await TestAssert.ThrowsAsync<SessionStoreDatabaseException>(
                () => database.InitializeAsync(CancellationToken.None));

            Assert.That(await ReadObjectNamesAsync(storeDir, "table"), Is.EquivalentTo(["unrelated"]),
                        "已有表却缺本程序 schema 的文件不得被当作空库建表。");
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    [Test]
    public async Task MissingRequiredTable_IsDamagedAndNeverRecreated()
    {
        var storeDir = NewStoreDir();
        try
        {
            var database = new SessionStoreDatabase(storeDir);
            await database.InitializeAsync(CancellationToken.None);
            await ExecuteAsync(storeDir, "DROP TABLE pinned_session");

            var availability = await database.GetAvailabilityAsync(CancellationToken.None);
            Assert.That(availability.Status, Is.EqualTo(SessionStoreDatabaseStatus.Damaged));
            Assert.That(availability.Detail, Does.Contain("pinned_session"));

            await TestAssert.ThrowsAsync<SessionStoreDatabaseException>(
                () => database.InitializeAsync(CancellationToken.None));

            Assert.That(await ReadObjectNamesAsync(storeDir, "table"), Has.No.Member("pinned_session"),
                        "缺表不得按 IF NOT EXISTS 静默补建，否则损坏会被掩盖。");
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    [Test]
    public async Task MissingFrozenIndex_IsDamagedNotAvailable()
    {
        var storeDir = NewStoreDir();
        try
        {
            var database = new SessionStoreDatabase(storeDir);
            await database.InitializeAsync(CancellationToken.None);
            // 表名齐全但唯一索引被删：项目根目录唯一性已经失效，不能继续报告可用。
            await ExecuteAsync(storeDir, "DROP INDEX ux_project_root_key");

            var availability = await database.GetAvailabilityAsync(CancellationToken.None);

            Assert.That(availability.Status, Is.EqualTo(SessionStoreDatabaseStatus.Damaged));
            Assert.That(availability.Detail, Does.Contain("ux_project_root_key"));
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    [Test]
    public async Task MissingRequiredMetadataKey_IsDamagedNotAvailable()
    {
        var storeDir = NewStoreDir();
        try
        {
            var database = new SessionStoreDatabase(storeDir);
            await database.InitializeAsync(CancellationToken.None);
            await ExecuteAsync(storeDir,
                               $"DELETE FROM {SessionStoreDatabase.MetaTableName} WHERE key = $key",
                               ("$key", SessionStoreDatabase.StoreIdentityKey));

            var availability = await database.GetAvailabilityAsync(CancellationToken.None);

            Assert.That(availability.Status, Is.EqualTo(SessionStoreDatabaseStatus.Damaged));
            Assert.That(availability.Detail, Does.Contain(SessionStoreDatabase.StoreIdentityKey));
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    [Test]
    public async Task DirectoryPlaceholder_IsDamagedNotMissing()
    {
        var storeDir = NewStoreDir();
        try
        {
            var database = new SessionStoreDatabase(storeDir);
            Directory.CreateDirectory(database.DatabasePath);

            var availability = await database.GetAvailabilityAsync(CancellationToken.None);

            Assert.That(availability.Status, Is.EqualTo(SessionStoreDatabaseStatus.Damaged),
                        "同名目录不能被当成尚未迁移，否则调用方会走首次导入。");
            await TestAssert.ThrowsAsync<SessionStoreDatabaseException>(
                () => database.InitializeAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    [TestCase(11)]
    [TestCase(26)]
    public void DamageClassification_AcceptsOnlyDefinitiveCorruptionCodes(int errorCode) =>
        Assert.That(SessionStoreDatabase.IndicatesDamage(
                        new SessionStoreDatabaseException("wrapped",
                                                          new SqliteException("sqlite failure", errorCode))),
                    Is.True);

    [TestCase(5)]
    [TestCase(6)]
    [TestCase(8)]
    [TestCase(10)]
    [TestCase(14)]
    public void DamageClassification_RejectsOperationalFailures(int errorCode) =>
        Assert.That(SessionStoreDatabase.IndicatesDamage(
                        new SessionStoreDatabaseException("wrapped",
                                                          new SqliteException("sqlite failure", errorCode))),
                    Is.False, "锁等待、只读、权限与 IO 失败只说明暂时不可用。");

    [Test]
    public async Task ZeroByteFile_NeedsSchemaThenInitializes()
    {
        var storeDir = NewStoreDir();
        try
        {
            var database = new SessionStoreDatabase(storeDir);
            await File.WriteAllBytesAsync(database.DatabasePath, []);

            Assert.That((await database.GetAvailabilityAsync(CancellationToken.None)).Status,
                        Is.EqualTo(SessionStoreDatabaseStatus.NeedsSchema));

            await database.InitializeAsync(CancellationToken.None);

            Assert.That((await database.GetAvailabilityAsync(CancellationToken.None)).Status,
                        Is.EqualTo(SessionStoreDatabaseStatus.Available));
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    [Test]
    public async Task Schema_RejectsBusinessRowsThatBreakFrozenConstraints()
    {
        var storeDir = NewStoreDir();
        try
        {
            await new SessionStoreDatabase(storeDir).InitializeAsync(CancellationToken.None);
            await InsertProjectAsync(storeDir);

            // 同一目录（平台比较键）只能属于一个项目。
            await TestAssert.ThrowsAsync<SqliteException>(
                () => InsertProjectAsync(storeDir, "00000000000000000000000000000002", @"c:\work\alpha"));
            // 主键重复、ID 形态、删除标记取值都由约束拒绝，不依赖调用方自觉。
            await TestAssert.ThrowsAsync<SqliteException>(() => InsertProjectAsync(storeDir));
            await TestAssert.ThrowsAsync<SqliteException>(() => ExecuteAsync(storeDir, """
                INSERT INTO project (id, name, root_path, root_key, revision, is_deleting, created_utc, updated_utc)
                VALUES ('short', 'Beta', 'c:\work\beta', 'c:\work\beta', 1, 0, 1, 1)
                """));
            await TestAssert.ThrowsAsync<SqliteException>(() => ExecuteAsync(storeDir, """
                INSERT INTO project (id, name, root_path, root_key, revision, is_deleting, created_utc, updated_utc)
                VALUES ('00000000000000000000000000000003', 'Beta', 'c:\work\beta', 'c:\work\beta', 0, 2, 1, 1)
                """));
            await TestAssert.ThrowsAsync<SqliteException>(() => ExecuteAsync(storeDir,
                "INSERT INTO archived_session (session_id) VALUES ('not-a-session')"));
            // SQLite 的 INTEGER 是亲和性而非强类型：非数字文本会被存成 TEXT，而 TEXT 在比较中恒大于数值，
            // 旧的 revision >= 1 会因此放行。typeof 守卫必须把这类值挡在库外。
            await TestAssert.ThrowsAsync<SqliteException>(() => ExecuteAsync(storeDir, """
                INSERT INTO project (id, name, root_path, root_key, revision, is_deleting, created_utc, updated_utc)
                VALUES ('00000000000000000000000000000004', 'Gamma', 'c:\work\gamma', 'c:\work\gamma', 'not-a-revision', 0, 1, 1)
                """));
            await TestAssert.ThrowsAsync<SqliteException>(() => ExecuteAsync(storeDir, """
                INSERT INTO project (id, name, root_path, root_key, revision, is_deleting, created_utc, updated_utc)
                VALUES ('00000000000000000000000000000005', 'Delta', 'c:\work\delta', 'c:\work\delta', 1, 'maybe', 1, 1)
                """));
            await TestAssert.ThrowsAsync<SqliteException>(() => ExecuteAsync(storeDir,
                "INSERT INTO session_order (session_id, position) VALUES ('0000000000000000000000000000000d', 'first')"));
            await TestAssert.ThrowsAsync<SqliteException>(() => ExecuteAsync(storeDir,
                "INSERT INTO collapsed_group (group_key) VALUES ('')"));
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    [Test]
    public async Task Schema_EnforcesOwnershipForeignKeyAndRestrictsProjectDeletion()
    {
        var storeDir = NewStoreDir();
        try
        {
            await new SessionStoreDatabase(storeDir).InitializeAsync(CancellationToken.None);
            await InsertProjectAsync(storeDir);
            var projectId = "00000000000000000000000000000001";
            var sessionId = "0000000000000000000000000000000a";

            await using var connection = await OpenRawWithForeignKeysAsync(storeDir);
            const string placementSql = """
                INSERT INTO session_placement (session_id, project_id, updated_utc) VALUES ($id, $project, 1)
                """;
            var placement = connection.CreateCommand();
            placement.CommandText = placementSql;
            placement.Parameters.AddWithValue("$id", sessionId);
            placement.Parameters.AddWithValue("$project", projectId);
            Assert.That(await placement.ExecuteNonQueryAsync(), Is.EqualTo(1));

            // 归属外键：悬空项目必须在写入时被拒绝，而不是等到展示层发现。
            var dangling = connection.CreateCommand();
            dangling.CommandText = placementSql;
            dangling.Parameters.AddWithValue("$id", "0000000000000000000000000000000b");
            dangling.Parameters.AddWithValue("$project", "0000000000000000000000000000000c");
            await TestAssert.ThrowsAsync<SqliteException>(() => dangling.ExecuteNonQueryAsync());

            // 项目删除必须先去归属：目录册的 is_deleting + 解绑顺序由此成为数据库不变量。
            var deleteProject = connection.CreateCommand();
            deleteProject.CommandText = "DELETE FROM project WHERE id = $id";
            deleteProject.Parameters.AddWithValue("$id", projectId);
            await TestAssert.ThrowsAsync<SqliteException>(() => deleteProject.ExecuteNonQueryAsync());
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }

    [Test]
    public async Task Schema_RestrictsTombstoneStatesAndIdShapes()
    {
        var storeDir = NewStoreDir();
        try
        {
            await new SessionStoreDatabase(storeDir).InitializeAsync(CancellationToken.None);

            // 墓碑状态只有 pending/deleted：未知状态会让删除续作无法判定，必须在写入时拒绝。
            await TestAssert.ThrowsAsync<SqliteException>(() => ExecuteAsync(storeDir, """
                INSERT INTO session_tombstone (session_id, state, requested_utc, updated_utc)
                VALUES ('0000000000000000000000000000000a', 'deleting', 1, 1)
                """));
            await TestAssert.ThrowsAsync<SqliteException>(() => ExecuteAsync(storeDir, """
                INSERT INTO session_tombstone (session_id, state, requested_utc, updated_utc)
                VALUES ('short', 'pending', 1, 1)
                """));
            await TestAssert.ThrowsAsync<SqliteException>(() => ExecuteAsync(storeDir, """
                INSERT INTO session_tombstone (session_id, state, requested_utc, updated_utc)
                VALUES ('0000000000000000000000000000000a', 'pending', 'not-a-time', 1)
                """));

            await ExecuteAsync(storeDir, """
                INSERT INTO session_tombstone (session_id, state, requested_utc, updated_utc)
                VALUES ('0000000000000000000000000000000a', 'pending', 1, 1)
                """);
            Assert.That(await ReadObjectNamesAsync(storeDir, "table"), Has.Member(SessionStoreDatabase.TombstoneTableName));
        }
        finally
        {
            Directory.Delete(storeDir, true);
        }
    }
}

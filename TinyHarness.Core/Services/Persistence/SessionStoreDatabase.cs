using Microsoft.Data.Sqlite;
using System.Globalization;
using TinyHarness.Core.Exceptions;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>
///         业务数据库可用性：可用、从未建立、存在但尚无 schema、需要升级、版本高于本程序、损坏或其他失败。
///     </para>
///     <para>
///         Business-database availability: available, never created, present without schema, upgrade required,
///         newer than this build, damaged, or another failure.
///     </para>
/// </summary>
public enum SessionStoreDatabaseStatus
{
    Available,
    Missing,
    NeedsSchema,
    NeedsUpgrade,
    Unsupported,
    Damaged,
    Failed
}

/// <summary>
///     <para>带说明的可用性结果；说明不含凭据。</para>
///     <para>An availability result with a credential-free detail.</para>
/// </summary>
/// <param name="Status">
///     <para>可用性分类。</para>
///     <para>The availability classification.</para>
/// </param>
/// <param name="Detail">
///     <para>非可用时的原因说明；可用时为 null。</para>
///     <para>The reason when unavailable, or null while available.</para>
/// </param>
/// <param name="Error">
///     <para>导致失败的底层异常；用于区分锁等待、权限、只读与 IO 失败。可用或仅凭文件属性判定时为 null。</para>
///     <para>
///         The underlying exception behind a failure, used to tell lock waits, permissions, read-only media, and IO
///         failures apart; null while available or when only file attributes established the classification.
///     </para>
/// </param>
public sealed record SessionStoreDatabaseAvailability(
    SessionStoreDatabaseStatus Status,
    string?                    Detail,
    Exception?                 Error = null)
{
    /// <summary>
    ///     <para>可用的单例结果。</para>
    ///     <para>The singleton result for an available database.</para>
    /// </summary>
    public static readonly SessionStoreDatabaseAvailability Ok = new(SessionStoreDatabaseStatus.Available, null);
}

/// <summary>
///     <para>
///         单个 session store 的 SQLite 业务数据库：保存项目定义、会话归属、归档、置顶与组织状态等
///         无法从会话日志重建的数据，并提供版本化的建库与升级基础。
///     </para>
///     <para>
///         The SQLite business database of one session store: it holds project definitions, session ownership,
///         archive, pin, and organization state — data that cannot be rebuilt from session logs — together with
///         the versioned creation and upgrade foundation.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         本类型只冻结 schema 与版本迁移，不是读写权威：当前生产路径仍以 <c>projects.json</c>、
///         <c>pinned-items.json</c>、<c>archived-sessions.json</c>、桌面偏好与会话日志为准，
///         本类型没有生产调用方。
///     </para>
///     <para>
///         This type freezes the schema and the version migrations only; it is not a read/write authority.
///         Production still reads and writes <c>projects.json</c>, <c>pinned-items.json</c>,
///         <c>archived-sessions.json</c>, desktop preferences, and session logs, and nothing in production calls
///         this type.
///     </para>
///     <para>
///         业务库与会话列表索引分成两个文件：索引是可重建投影，允许整库清空重写；业务库不允许。
///         <c>session-store.db</c> 只由本类型按版本迁移演进，任何路径都不得 DROP 业务表、不得在缺失时
///         静默建立空库、不得把更高版本降级。缺失只表示"尚未迁移"，由后续阶段导入；迁移完成后缺失
///         必须按业务备份恢复处理，不得当作空 store。
///     </para>
///     <para>
///         The business database and the session-list index live in separate files: the index is a rebuildable
///         projection that may be cleared and rewritten wholesale, the business database is not.
///         <c>session-store.db</c> only evolves through the version migrations owned by this type; no path may
///         drop a business table, silently create an empty database when it is missing, or downgrade a newer
///         version. Absence only means "not imported yet" for the later import stage; once migration is done,
///         absence must be handled as business-backup restoration rather than an empty store.
///     </para>
///     <para>
///         连接按需打开并禁用连接池；业务库使用 WAL、<c>synchronous=FULL</c> 与启用外键，锁等待有界且
///         失败可见——这三处与索引库有意不同：索引丢数据可重建，业务数据丢失只能靠备份。迁移逐版本
///         在单个事务内执行并把版本写入同一事务；不支持或损坏的库保持原样并报告，由人工备份恢复。
///     </para>
///     <para>
///         Connections open per operation with pooling disabled. The business database uses WAL,
///         <c>synchronous=FULL</c>, and enforced foreign keys, with bounded lock waits and visible failure —
///         deliberately unlike the index in these three points: index loss is rebuildable, business loss is
///         recoverable only from backup. Each migration version runs in one transaction that also records the
///         version; unsupported or damaged databases are left untouched and reported for manual restore.
///     </para>
/// </remarks>
public sealed class SessionStoreDatabase
{
    /// <summary>
    ///     <para>session store 目录内的业务数据库文件名；与索引库 <c>session-index.db</c> 分属不同文件。</para>
    ///     <para>
    ///         The business database file name inside the session-store directory; a different file from the
    ///         index database <c>session-index.db</c>.
    ///     </para>
    /// </summary>
    public const string DatabaseFileName = "session-store.db";

    /// <summary>
    ///     <para>当前实现支持的 schema 版本；版本 1 是首个业务 schema，版本 2 增加会话删除墓碑。</para>
    ///     <para>
    ///         The schema version supported by this implementation; version 1 is the first business schema and
    ///         version 2 adds session deletion tombstones.
    ///     </para>
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    ///     <para>元数据表名，保存 schema 版本与库身份。</para>
    ///     <para>The metadata table name holding the schema version and the store identity.</para>
    /// </summary>
    public const string MetaTableName = "store_meta";

    /// <summary>
    ///     <para>schema 版本在元数据表中的键。</para>
    ///     <para>The schema-version key inside the metadata table.</para>
    /// </summary>
    public const string SchemaVersionKey = "schema_version";

    /// <summary>
    ///     <para>库身份在元数据表中的键；用于区分"已初始化的业务库"与"被替换或伪造的同名文件"。</para>
    ///     <para>
    ///         The store-identity key inside the metadata table, telling an initialized business database apart
    ///         from a replaced or foreign file of the same name.
    ///     </para>
    /// </summary>
    public const string StoreIdentityKey = "store_identity";

    /// <summary>
    ///     <para>建库时间在元数据表中的键，Unix 毫秒。</para>
    ///     <para>The creation-time key inside the metadata table, in Unix milliseconds.</para>
    /// </summary>
    public const string CreatedUtcKey = "created_utc";

    private const int BusyTimeoutMs = 5000;

    /// <summary>
    ///     <para>版本 2 引入的会话删除墓碑表名；删除先落墓碑，日志与投影清理随后收尾。</para>
    ///     <para>
    ///         The session-deletion tombstone table name introduced by version 2: a deletion lands its tombstone
    ///         first and the log plus projection cleanup follows.
    ///     </para>
    /// </summary>
    public const string TombstoneTableName = "session_tombstone";

    /// <summary>
    ///     <para>版本 2 新增的墓碑状态索引名；待收尾删除按状态顺序扫描。</para>
    ///     <para>The tombstone-state index name added by version 2; pending deletions scan in state order.</para>
    /// </summary>
    public const string TombstoneStateIndex = "ix_session_tombstone_state";

    /// <summary>
    ///     <para>
    ///         版本 1 的冻结 schema。约束按业务不变量设置：ID 形态、名称与路径长度、修订号下界、
    ///         删除标记取值、项目目录唯一（平台比较键）与会话归属外键；预算类上限（如 1000 个项目）
    ///         无法用 CHECK 表达，仍由调用方校验。
    ///     </para>
    ///     <para>
    ///         The frozen version-1 schema. Constraints follow business invariants: identifier shape, name and path
    ///         lengths, the revision lower bound, the deletion marker domain, project-root uniqueness on the
    ///         platform comparison key, and the session-ownership foreign key. Count budgets (for example 1000
    ///         projects) cannot be expressed as CHECKs and stay with the caller.
    ///     </para>
    /// </summary>
    private const string BaseSchemaSql = """
        CREATE TABLE IF NOT EXISTS store_meta (
            key   TEXT NOT NULL PRIMARY KEY,
            value TEXT NOT NULL,
            CHECK (typeof(key) = 'text'),
            CHECK (typeof(value) = 'text')
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS project (
            id          TEXT    NOT NULL PRIMARY KEY,
            name        TEXT    NOT NULL,
            root_path   TEXT    NOT NULL,
            root_key    TEXT    NOT NULL,
            revision    INTEGER NOT NULL,
            is_deleting INTEGER NOT NULL DEFAULT 0,
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL,
            CHECK (typeof(id) = 'text' AND length(id) = 32),
            CHECK (typeof(name) = 'text' AND length(name) BETWEEN 1 AND 200),
            CHECK (typeof(root_path) = 'text' AND length(root_path) BETWEEN 1 AND 2048),
            CHECK (typeof(root_key) = 'text' AND length(root_key) BETWEEN 1 AND 2048),
            CHECK (typeof(revision) = 'integer' AND revision >= 1),
            CHECK (typeof(is_deleting) = 'integer' AND is_deleting IN (0, 1)),
            CHECK (typeof(created_utc) = 'integer'),
            CHECK (typeof(updated_utc) = 'integer')
        ) WITHOUT ROWID;

        CREATE UNIQUE INDEX IF NOT EXISTS ux_project_root_key ON project (root_key);

        CREATE TABLE IF NOT EXISTS project_order (
            project_id TEXT    NOT NULL PRIMARY KEY,
            position   INTEGER NOT NULL,
            CHECK (typeof(project_id) = 'text' AND length(project_id) = 32),
            CHECK (typeof(position) = 'integer' AND position >= 0)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS pinned_project (
            project_id TEXT    NOT NULL PRIMARY KEY,
            position   INTEGER NOT NULL,
            CHECK (typeof(project_id) = 'text' AND length(project_id) = 32),
            CHECK (typeof(position) = 'integer' AND position >= 0)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS pinned_session (
            session_id TEXT    NOT NULL PRIMARY KEY,
            position   INTEGER NOT NULL,
            CHECK (typeof(session_id) = 'text' AND length(session_id) = 32),
            CHECK (typeof(position) = 'integer' AND position >= 0)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS archived_session (
            session_id TEXT NOT NULL PRIMARY KEY,
            CHECK (typeof(session_id) = 'text' AND length(session_id) = 32)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS session_order (
            session_id TEXT    NOT NULL PRIMARY KEY,
            position   INTEGER NOT NULL,
            CHECK (typeof(session_id) = 'text' AND length(session_id) = 32),
            CHECK (typeof(position) = 'integer' AND position >= 0)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS session_placement (
            session_id  TEXT    NOT NULL PRIMARY KEY,
            project_id  TEXT    NULL REFERENCES project (id) ON DELETE RESTRICT,
            updated_utc INTEGER NOT NULL,
            CHECK (typeof(session_id) = 'text' AND length(session_id) = 32),
            CHECK (project_id IS NULL OR typeof(project_id) = 'text'),
            CHECK (typeof(updated_utc) = 'integer')
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS ix_session_placement_project
            ON session_placement (project_id, session_id);

        CREATE TABLE IF NOT EXISTS collapsed_group (
            group_key TEXT NOT NULL PRIMARY KEY,
            CHECK (typeof(group_key) = 'text' AND length(group_key) >= 1)
        ) WITHOUT ROWID;
        """;

    /// <summary>
    ///     <para>
    ///         版本 2 的冻结 schema：每行代表一次会话删除。状态 <c>pending</c> 表示删除意图已提交、日志或投影
    ///         尚未收尾，下一次删除恢复必须继续；<c>deleted</c> 表示日志与投影已清理完成。墓碑行永久保留：
    ///         会话日志若因备份、拷贝或旧进程写入重新出现，墓碑仍能阻止它复活成列表条目。
    ///     </para>
    ///     <para>
    ///         The frozen version-2 schema: one row per session deletion. State <c>pending</c> means the deletion
    ///         intent committed while the log or projections still need finishing, so the next recovery must
    ///         continue; <c>deleted</c> means the log and projections are cleared. Tombstone rows are kept forever:
    ///         if a log reappears through a backup, copy, or an older program, the tombstone still keeps it from
    ///         coming back as a list entry.
    ///     </para>
    /// </summary>
    private const string TombstoneSchemaSql = """
        CREATE TABLE IF NOT EXISTS session_tombstone (
            session_id    TEXT    NOT NULL PRIMARY KEY,
            state         TEXT    NOT NULL,
            requested_utc INTEGER NOT NULL,
            updated_utc   INTEGER NOT NULL,
            CHECK (typeof(session_id) = 'text' AND length(session_id) = 32),
            CHECK (typeof(state) = 'text' AND state IN ('pending', 'deleted')),
            CHECK (typeof(requested_utc) = 'integer'),
            CHECK (typeof(updated_utc) = 'integer')
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS ix_session_tombstone_state
            ON session_tombstone (state, session_id);
        """;

    /// <summary>
    ///     <para>业务表的冻结清单；缺失任意一张即视为损坏，不补建。</para>
    ///     <para>The frozen business-table list; a missing table counts as damage and is never recreated.</para>
    /// </summary>
    private static readonly string[] RequiredTables =
    [
        MetaTableName,
        "project",
        "project_order",
        "pinned_project",
        "pinned_session",
        "archived_session",
        "session_order",
        "session_placement",
        "collapsed_group"
    ];

    /// <summary>
    ///     <para>
    ///         版本 1 必须存在的显式索引。缺索引时唯一性与查询约束已经失效，必须按损坏处理而不是继续读写。
    ///     </para>
    ///     <para>
    ///         The explicit indexes version 1 requires. Without them the uniqueness and query guarantees are gone,
    ///         so the database counts as damaged instead of continuing to serve reads and writes.
    ///     </para>
    /// </summary>
    private static readonly string[] RequiredIndexes = ["ux_project_root_key", "ix_session_placement_project"];

    /// <summary>
    ///     <para>版本 2 必须具备的完整表与索引清单：版本 1 的对象加删除墓碑及其状态索引。</para>
    ///     <para>
    ///         The complete table and index list version 2 requires: every version-1 object plus the deletion
    ///         tombstone and its state index.
    ///     </para>
    /// </summary>
    private static readonly SchemaShape Version2Shape =
        new([.. RequiredTables, TombstoneTableName], [.. RequiredIndexes, TombstoneStateIndex]);

    /// <summary>
    ///     <para>版本 1 必须存在的元数据键；缺失说明库结构不完整，不得按可用库继续。</para>
    ///     <para>
    ///         The metadata keys version 1 requires; a missing one means the structure is incomplete and must not
    ///         be served as available.
    ///     </para>
    /// </summary>
    private static readonly string[] RequiredMetaKeys = [SchemaVersionKey, StoreIdentityKey, CreatedUtcKey];

    /// <summary>
    ///     <para>版本 0 的空结构：任何表或索引都表示未知来源，不得据此升级。</para>
    ///     <para>The empty version-0 shape: any table or index means unknown origin and must never be upgraded.</para>
    /// </summary>
    private static readonly SchemaShape EmptyShape = new([], []);

    /// <summary>
    ///     <para>按版本号升序排列的迁移；必须从 1 起连续编号。</para>
    ///     <para>Migrations ordered by version; numbering must be contiguous and start at one.</para>
    /// </summary>
    private static readonly SchemaMigration[] Migrations = [CreateInitialSchema(), AddSessionTombstones()];

    /// <summary>
    ///     <para>本实例使用的迁移清单；生产固定为 <see cref="Migrations" />，测试可注入故意残缺的迁移。</para>
    ///     <para>
    ///         The migration list this instance uses; production always takes <see cref="Migrations" />, while tests
    ///         may inject a deliberately incomplete migration.
    ///     </para>
    /// </summary>
    private readonly IReadOnlyList<SchemaMigration> _migrations;

    /// <summary>
    ///     <para>绑定一个受保护 session store 目录；目录须为绝对路径，不立即访问文件系统。</para>
    ///     <para>
    ///         Binds one protected session-store directory; it must be absolute, and no file-system access
    ///         happens yet.
    ///     </para>
    /// </summary>
    /// <exception cref="SessionStoreDatabaseException">
    ///     <para>目录不是绝对路径。</para>
    ///     <para>The directory is not an absolute path.</para>
    /// </exception>
    public SessionStoreDatabase(string storeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        if (!Path.IsPathFullyQualified(storeDirectory))
            throw new SessionStoreDatabaseException($"Session-store directory '{storeDirectory}' " +
                                                    "must be an absolute path.");
        StoreDirectory = Path.GetFullPath(storeDirectory);
        DatabasePath   = PathFor(StoreDirectory);
        _migrations    = Migrations;
    }

    /// <summary>
    ///     <para>
    ///         只读检查用的重载：把目录当作普通数据库位置而不是本程序的业务库，因此可用性探测不会补完中断的恢复，
    ///         也不会为了恢复去申请迁移租约。备份校验用它检查备份目录，避免把用户备份目录当成业务库改写。
    ///     </para>
    ///     <para>
    ///         The read-only-check overload: the directory is treated as an ordinary database location rather than
    ///         this program's business store, so availability probing neither completes an interrupted restore nor takes
    ///         the migration lease for one. Backup verification uses it so a user's backup directory is never treated as
    ///         a business store to be repaired.
    ///     </para>
    /// </summary>
    internal SessionStoreDatabase(string storeDirectory, bool isBusinessStore)
        : this(storeDirectory) => IsBusinessStore = isBusinessStore;

    /// <summary>
    ///     <para>
    ///         测试专用：绑定目录并使用给定的迁移清单，用于确定性覆盖"迁移产物结构不符即回滚"。
    ///         生产不经过此重载。
    ///     </para>
    ///     <para>
    ///         Test-only: binds a directory with the given migration list so the "roll back when a migration produces
    ///         the wrong structure" path can be covered deterministically. Production never uses this overload.
    ///     </para>
    /// </summary>
    internal SessionStoreDatabase(string storeDirectory, IReadOnlyList<SchemaMigration> migrations)
        : this(storeDirectory)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        if (migrations.Count == 0)
            throw new ArgumentException("A test migration list must contain at least one version.", nameof(migrations));
        _migrations = migrations;
    }

    /// <summary>
    ///     <para>
    ///         该目录是否承载本程序的业务库；为 false 时可用性探测与打开都只按普通数据库处理，不做恢复、不申请租约。
    ///     </para>
    ///     <para>
    ///         Whether the directory carries this program's business store; when false both probing and opening treat it
    ///         as an ordinary database, without recovery or lease acquisition.
    ///     </para>
    /// </summary>
    private bool IsBusinessStore { get; } = true;

    /// <summary>
    ///     <para>绑定的 session store 目录（规范化绝对路径）。</para>
    ///     <para>The bound session-store directory (normalized absolute path).</para>
    /// </summary>
    public string StoreDirectory { get; }

    /// <summary>
    ///     <para>业务数据库文件路径。</para>
    ///     <para>The business database file path.</para>
    /// </summary>
    public string DatabasePath { get; }

    /// <summary>
    ///     <para>返回 session store 目录对应的业务数据库路径。</para>
    ///     <para>Returns the business database path for a session-store directory.</para>
    /// </summary>
    public static string PathFor(string storeDirectory) => Path.Combine(storeDirectory, DatabaseFileName);

    /// <summary>
    ///     <para>判断业务数据库文件是否已存在；不存在不代表 store 为空，只代表尚未迁移。</para>
    ///     <para>
    ///         Whether the business database file exists; absence never means an empty store, only that migration
    ///         has not happened yet.
    ///     </para>
    /// </summary>
    public static bool Exists(string storeDirectory) => File.Exists(PathFor(storeDirectory));

    /// <summary>
    ///     <para>
    ///         检查业务库可用性：缺文件不创建、有表但无本程序 schema 判损坏、版本更高判不受支持、
    ///         版本更低判待升级，冻结表缺失同样判损坏。
    ///     </para>
    ///     <para>
    ///         Checks business-database availability: a missing file is not created, tables without this
    ///         program's schema count as damage, a newer version is unsupported, an older one needs an upgrade,
    ///         and a missing frozen table also counts as damage.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         探测不切换 journal 模式、不建表、不写元数据，避免只读检查改动业务文件；损坏与不受支持
    ///         都保持文件原样，由调用方决定备份恢复。"文件缺失"与"无法判定"分开报告：只有确定不存在才是
    ///         <see cref="SessionStoreDatabaseStatus.Missing" />，权限或路径错误报告为失败；只有 SQLite 明确
    ///         报告损坏或不是数据库才算 <see cref="SessionStoreDatabaseStatus.Damaged" />，锁等待、只读与 IO
    ///         失败按暂时不可用报告。
    ///     </para>
    ///     <para>
    ///         Probing never switches the journal mode, creates tables, or writes metadata, so a read-only check
    ///         leaves the business file untouched; damaged and unsupported databases stay as they are for the
    ///         caller to restore from backup. "Absent" and "undeterminable" are reported separately: only a
    ///         definitive absence is <see cref="SessionStoreDatabaseStatus.Missing" />, while permission and path
    ///         errors report as failure; only a definitive SQLite corruption or not-a-database verdict is
    ///         <see cref="SessionStoreDatabaseStatus.Damaged" />, while lock waits, read-only, and IO failures
    ///         report as temporarily unavailable.
    ///     </para>
    /// </remarks>
    public async Task<SessionStoreDatabaseAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        // 必须在读取文件叶子之前补完中断的恢复：替换已提交但记录未清除时，主文件可能就在原地，而日志仍停在隔离名
        // 下；替换未提交时主文件甚至可能缺失。两种情形若被当成 Missing/Available 处理，都会让调用方基于不完整
        // 的一代继续。恢复失败时按不可用报告，不穿透成未分类异常。
        // The interrupted restore must be completed before the file leaf is read: when the replacement committed but
        // its record was not cleared the main file may be in place while its journal still sits under a quarantined
        // name, and when it did not commit the main file may even be absent. Treating either as Missing/Available would
        // let the caller continue on an incomplete generation. A failed recovery reports as unavailable.
        if (IsBusinessStore)
            try
            {
                await SessionStoreRestoreRecovery.RecoverAsync(StoreDirectory, cancellationToken).ConfigureAwait(false);
            }
            catch (SessionStoreBackupException ex)
            {
                return new SessionStoreDatabaseAvailability(SessionStoreDatabaseStatus.Failed, ex.Message, ex);
            }

        // 缺失与"无法判定"必须分开：File.Exists 在权限或路径错误时同样返回 false，会把不可读的业务库
        // 报成尚未迁移，从而误导调用方走首次导入。
        var leaf = ReadLeafState(out var leafDetail);
        switch (leaf)
        {
            case LeafState.Missing :
                return new SessionStoreDatabaseAvailability(SessionStoreDatabaseStatus.Missing, null);
            case LeafState.Unreadable :
                return new SessionStoreDatabaseAvailability(SessionStoreDatabaseStatus.Failed, leafDetail);
            case LeafState.NotRegular :
                return new SessionStoreDatabaseAvailability(SessionStoreDatabaseStatus.Damaged, leafDetail);
        }

        try
        {
            // 已存在的文件按 ReadWrite 打开：打开失败不会像 ReadWriteCreate 那样留下空文件。
            await using var connection = await OpenAsync(SqliteOpenMode.ReadWrite, false, cancellationToken)
               .ConfigureAwait(false);
            return await InspectAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (SessionStoreDatabaseException ex) when (IndicatesDamage(ex))
        {
            return new SessionStoreDatabaseAvailability(SessionStoreDatabaseStatus.Damaged, ex.Message, ex);
        }
        catch (SessionStoreDatabaseException ex)
        {
            // 锁等待耗尽、只读、权限与其余运行期失败只说明"现在读不了"，不能据此断言数据已损坏并引导恢复。
            return new SessionStoreDatabaseAvailability(SessionStoreDatabaseStatus.Failed, ex.Message, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                      or PathTooLongException)
        {
            return new SessionStoreDatabaseAvailability(SessionStoreDatabaseStatus.Failed, ex.Message, ex);
        }
    }

    /// <summary>
    ///     <para>
    ///         在库文件缺失或尚空时按当前版本建库；已可用时为无操作，待升级、版本更高或无 schema 但已有表
    ///         一律拒绝，绝不覆盖已有业务数据。
    ///     </para>
    ///     <para>
    ///         Creates the schema at the current version when the file is absent or still empty; it is a no-op
    ///         while already available, and it refuses an upgrade-required, newer, or table-bearing schema-less
    ///         database instead of overwriting existing business data.
    ///     </para>
    /// </summary>
    /// <exception cref="SessionStoreDatabaseException">
    ///     <para>库需要升级、版本不受支持、损坏、内容不属于本程序或存储失败。</para>
    ///     <para>
    ///         The database needs an upgrade, uses an unsupported version, is damaged, holds foreign content, or storage
    ///         failed.
    ///     </para>
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         缺文件既表示"尚未迁移"，也表示"迁移后丢失"，本类型无法区分。完成导入后，调用方必须先以业务库
    ///         之外的迁移标记确认归属，再决定走本方法（首次建库）还是拒绝并要求备份恢复；不得因为文件缺失
    ///         就把已迁移 store 建成空业务库。
    ///     </para>
    ///     <para>
    ///         A missing file means both "not migrated yet" and "lost after migration", which this type cannot
    ///         tell apart. After a completed import the caller must first confirm ownership through a migration
    ///         marker outside the business database, and only then choose this method (first creation) or refusal
    ///         with backup restoration. A migrated store must never become an empty business database just because
    ///         its file is absent.
    ///     </para>
    /// </remarks>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var availability = await GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        switch (availability.Status)
        {
            case SessionStoreDatabaseStatus.Available :
                return;
            case SessionStoreDatabaseStatus.Missing :
            case SessionStoreDatabaseStatus.NeedsSchema :
                break;
            case SessionStoreDatabaseStatus.NeedsUpgrade :
                throw new SessionStoreDatabaseException($"Business database '{DatabasePath}' uses an older " +
                                                        "schema version; upgrade it instead of creating it again.");
            case SessionStoreDatabaseStatus.Unsupported :
                throw new SessionStoreDatabaseException($"Business database '{DatabasePath}' is newer than supported " +
                                                        $"schema version {CurrentSchemaVersion}; it must not be downgraded.");
            default :
                throw new SessionStoreDatabaseException($"Business database '{DatabasePath}' cannot be created " +
                                                        $"while it is {availability.Status}: {availability.Detail ?? "no detail"}.");
        }

        await MigrateAsync(true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>
    ///         把一个已存在、版本更低的业务库升到当前版本；已可用时为无操作，库缺失、有表无 schema、
    ///         版本更高或损坏一律拒绝——业务数据只能由备份恢复，不能凭空创建。
    ///     </para>
    ///     <para>
    ///         Upgrades an existing older business database to the current version; it is a no-op while already
    ///         available and refuses a missing database, a table-bearing schema-less one, a newer version, or
    ///         damage — business data is restored from backup, never invented.
    ///     </para>
    /// </summary>
    /// <exception cref="SessionStoreDatabaseException">
    ///     <para>库缺失、无 schema、版本不受支持、损坏或迁移失败。</para>
    ///     <para>The database is missing, schema-less, unsupported, damaged, or a migration failed.</para>
    /// </exception>
    public async Task UpgradeAsync(CancellationToken cancellationToken)
    {
        var availability = await GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        switch (availability.Status)
        {
            case SessionStoreDatabaseStatus.Available :
                return;
            case SessionStoreDatabaseStatus.NeedsUpgrade :
                break;
            case SessionStoreDatabaseStatus.Missing :
                throw new SessionStoreDatabaseException($"Business database '{DatabasePath}' is missing; " +
                                                        "it is restored from backup or imported by a later stage, never upgraded from nothing.");
            case SessionStoreDatabaseStatus.NeedsSchema :
                throw new SessionStoreDatabaseException($"Business database '{DatabasePath}' has no schema yet; " +
                                                        "initialize it instead of upgrading.");
            case SessionStoreDatabaseStatus.Unsupported :
                throw new SessionStoreDatabaseException($"Business database '{DatabasePath}' is newer than supported " +
                                                        $"schema version {CurrentSchemaVersion}; it must not be downgraded.");
            default :
                throw new SessionStoreDatabaseException($"Business database '{DatabasePath}' cannot be upgraded " +
                                                        $"while it is {availability.Status}: {availability.Detail ?? "no detail"}.");
        }

        await MigrateAsync(false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>
    ///         读出库内的实际结构、版本与元数据键；只有版本相符、冻结的表/索引与元数据键齐全才算可用。
    ///     </para>
    ///     <para>
    ///         Reads the actual structure, the version, and the metadata keys; the database is available only when
    ///         the version matches and the frozen tables, indexes, and metadata keys are all present.
    ///     </para>
    /// </summary>
    private async Task<SessionStoreDatabaseAvailability> InspectAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        var objects = await ReadSchemaObjectsAsync(connection, cancellationToken).ConfigureAwait(false);
        if (objects.Tables.Count == 0)
            return new SessionStoreDatabaseAvailability(SessionStoreDatabaseStatus.NeedsSchema, null);
        if (!objects.Tables.Contains(MetaTableName))
            return new SessionStoreDatabaseAvailability(SessionStoreDatabaseStatus.Damaged,
                                                        $"The database has tables but no '{MetaTableName}' table, so it does not " +
                                                        "belong to this program.");

        var version = await ReadSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (version is null)
            return new SessionStoreDatabaseAvailability(SessionStoreDatabaseStatus.Damaged,
                                                        $"The '{MetaTableName}' table has no '{SchemaVersionKey}' row.");
        if (version.Value > CurrentSchemaVersion)
            return new SessionStoreDatabaseAvailability(SessionStoreDatabaseStatus.Unsupported,
                                                        $"Business schema version {version.Value} is newer than supported " +
                                                        $"version {CurrentSchemaVersion}.");
        if (version.Value < CurrentSchemaVersion)
            return new SessionStoreDatabaseAvailability(SessionStoreDatabaseStatus.NeedsUpgrade,
                                                        $"Business schema version {version.Value} is older than supported " +
                                                        $"version {CurrentSchemaVersion}.");

        // 版本号相符还不够：缺表或缺唯一索引时约束已经失效，按损坏报告而不是当成可用库继续。
        if (DescribeMissingObjects(objects, ShapeForVersion(version.Value)) is { } missing)
            return new SessionStoreDatabaseAvailability(SessionStoreDatabaseStatus.Damaged,
                                                        $"Business schema version {version.Value} is incomplete: {missing}.");

        var metaKeys    = await ReadMetaKeysAsync(connection, cancellationToken).ConfigureAwait(false);
        var missingKeys = RequiredMetaKeys.Where(key => !metaKeys.Contains(key)).ToArray();
        return missingKeys.Length == 0
            ? SessionStoreDatabaseAvailability.Ok
            : new SessionStoreDatabaseAvailability(SessionStoreDatabaseStatus.Damaged,
                                                   $"Business schema version {version.Value} is missing metadata key(s) " +
                                                   $"{string.Join(", ", missingKeys)}.");
    }

    /// <summary>
    ///     <para>
    ///         在单个事务内逐版本应用迁移并写入版本号；迁移前后都核对结构，未知或不完整结构一律拒绝，
    ///         避免 <c>CREATE TABLE IF NOT EXISTS</c> 把缺失表补成空表后宣称升级成功。
    ///     </para>
    ///     <para>
    ///         Applies migrations version by version inside one transaction each and records the version; the
    ///         structure is checked before and after every migration, and unknown or incomplete structures are
    ///         refused so <c>CREATE TABLE IF NOT EXISTS</c> can never re-create a missing table as empty and then
    ///         claim the upgrade succeeded.
    ///     </para>
    /// </summary>
    /// <param name="requireEmptySchema">
    ///     <para>true 表示只允许从未建库的状态开始（建库路径）；false 表示允许从既有版本继续（升级路径）。</para>
    ///     <para>
    ///         True allows starting only from a never-created state (creation path); false allows continuing from
    ///         an existing version (upgrade path).
    ///     </para>
    /// </param>
    private async Task MigrateAsync(bool requireEmptySchema, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(SqliteOpenMode.ReadWriteCreate, true, cancellationToken)
           .ConfigureAwait(false);

        var  objects = await ReadSchemaObjectsAsync(connection, cancellationToken).ConfigureAwait(false);
        int? version;
        if (objects.Tables.Count == 0) version = null;
        else
            version = await ReadSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false)
                   ?? throw new SessionStoreDatabaseException($"Business database '{DatabasePath}' has tables " +
                                                              "but no readable schema version; it must not be modified.");

        switch (requireEmptySchema)
        {
            case true when version is not null :
                throw new SessionStoreDatabaseException($"Business database '{DatabasePath}' was created " +
                                                        "concurrently or already holds data; it was left unchanged.");
            case false when version is null :
                throw new SessionStoreDatabaseException($"Business database '{DatabasePath}' has no schema version " +
                                                        "to upgrade from.");
        }

        switch (version)
        {
            case > CurrentSchemaVersion :
                throw new SessionStoreDatabaseException($"Business database '{DatabasePath}' uses schema " +
                                                        $"version {version}, newer than supported version {CurrentSchemaVersion}; " +
                                                        "it must not be downgraded.");
            // 迁移前该版本声明的表与索引必须齐全。迁移脚本用 CREATE TABLE IF NOT EXISTS：缺表被放行就会补成
            // 空表并写入新版本号，把数据丢失伪装成升级成功。版本 0 是空库，出现任何表都表示来源不明。
            case 0 when objects.Tables.Count != 0 :
                throw new SessionStoreDatabaseException($"Business database '{DatabasePath}' claims schema " +
                                                        $"version 0 but already holds table(s) " +
                                                        $"{string.Join(", ", objects.Tables.Order(StringComparer.Ordinal))}. " +
                                                        $"Refusing to migrate an unknown structure.");
        }

        if (version is not null && DescribeMissingObjects(objects, ShapeForVersion(version.Value)) is { } mismatch)
            throw new SessionStoreDatabaseException($"Business database '{DatabasePath}' does not match "        +
                                                    $"the known schema of version {version.Value}: {mismatch}. " +
                                                    "Refusing to migrate an unknown structure.");

        for (var next = (version ?? 0) + 1; next <= CurrentSchemaVersion; next++)
            // 结构核对与版本写入都在迁移事务内完成：不符即回滚，不会出现"版本已推进、结构不完整"的库。
            await ApplyMigrationAsync(connection, MigrationFor(next), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>返回指定版本应具备的结构；版本 0 为空结构。</para>
    ///     <para>Returns the structure one version must have; version 0 is the empty structure.</para>
    /// </summary>
    private SchemaShape ShapeForVersion(int version) =>
        version == 0 ? EmptyShape : MigrationFor(version).Shape;

    /// <summary>
    ///     <para>
    ///         只报告缺失的表与索引。可用性与迁移都只按"缺少"判定：缺失意味着约束或数据已经不在，
    ///         多出的对象既不清空也不阻止读取，由后续版本显式纳管。
    ///     </para>
    ///     <para>
    ///         Reports only missing tables and indexes. Availability and migration both decide on absence alone: a
    ///         missing object means constraints or data are gone, while extra objects neither get cleared nor block
    ///         reads and are adopted explicitly by a later version.
    ///     </para>
    /// </summary>
    private static string? DescribeMissingObjects(SchemaObjects objects, SchemaShape shape)
    {
        var missingTables  = shape.Tables.Where(table => !objects.Tables.Contains(table)).ToArray();
        var missingIndexes = shape.Indexes.Where(index => !objects.Indexes.Contains(index)).ToArray();
        if (missingTables.Length == 0 && missingIndexes.Length == 0) return null;

        var parts = new List<string>(2);
        if (missingTables.Length  != 0) parts.Add($"missing table(s) {string.Join(", ", missingTables)}");
        if (missingIndexes.Length != 0) parts.Add($"missing index(es) {string.Join(", ", missingIndexes)}");
        return string.Join("; ", parts);
    }

    /// <summary>
    ///     <para>
    ///         在单个事务内应用一个版本的迁移：执行其语句、写入身份与建库时间（缺失时）、核对目标结构，
    ///         最后写版本号并提交。结构不符时整事务回滚，不会留下"版本已推进但结构不完整"的库。
    ///     </para>
    ///     <para>
    ///         Applies one version migration inside a single transaction: its statements, the identity and creation
    ///         time when absent, the expected-structure check, and only then the version and the commit. A structure
    ///         mismatch rolls the whole transaction back, so no database is left at an advanced version with an
    ///         incomplete structure.
    ///     </para>
    /// </summary>
    private async Task ApplyMigrationAsync(
        SqliteConnection connection, SchemaMigration migration, CancellationToken cancellationToken)
    {
        await ExecuteCoreAsync(async () =>
        {
            await using var transaction = (SqliteTransaction)await connection
                                                                  .BeginTransactionAsync(cancellationToken)
                                                                  .ConfigureAwait(false);
            var schema = connection.CreateCommand();
            schema.Transaction = transaction;
            schema.CommandText = migration.Sql;
            await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // 同一连接在同一事务内可见刚执行的 DDL：结构不符必须在这里发现并回滚，而不是提交版本后再报错。
            var applied = await ReadSchemaObjectsAsync(connection, cancellationToken).ConfigureAwait(false);
            if (DescribeMissingObjects(applied, migration.Shape) is { } mismatch)
                throw new SessionStoreDatabaseException($"Migration to business schema version {migration.Version} " +
                                                        $"produced an incomplete structure: {mismatch}.");

            var identity = connection.CreateCommand();
            identity.Transaction = transaction;
            identity.CommandText = $"""
                INSERT OR IGNORE INTO {MetaTableName} (key, value) VALUES ($identity_key, $identity);
                INSERT OR IGNORE INTO {MetaTableName} (key, value) VALUES ($created_key, $created);
                INSERT OR REPLACE INTO {MetaTableName} (key, value) VALUES ($version_key, $version);
                """;
            identity.Parameters.AddWithValue("$identity_key", StoreIdentityKey);
            identity.Parameters.AddWithValue("$identity", Guid.NewGuid().ToString("N"));
            identity.Parameters.AddWithValue("$created_key", CreatedUtcKey);
            identity.Parameters.AddWithValue("$created",
                                             DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                                           .ToString(CultureInfo.InvariantCulture));
            identity.Parameters.AddWithValue("$version_key", SchemaVersionKey);
            identity.Parameters.AddWithValue("$version",
                                             migration.Version.ToString(CultureInfo.InvariantCulture));
            await identity.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>读取全部非内部表与显式索引；损坏文件在统一边界内报告。</para>
    ///     <para>Reads every non-internal table and explicit index; a damaged file reports through the unified boundary.</para>
    /// </summary>
    private static Task<SchemaObjects> ReadSchemaObjectsAsync(
        SqliteConnection connection, CancellationToken cancellationToken) =>
        ExecuteCoreAsync(async () =>
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT type, name FROM sqlite_master WHERE type IN ('table', 'index') " +
                                  "AND name NOT LIKE 'sqlite_%'";
            await using var reader  = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var             tables  = new HashSet<string>(StringComparer.Ordinal);
            var             indexes = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var names = reader.GetString(0) == "table" ? tables : indexes;
                names.Add(reader.GetString(1));
            }

            return new SchemaObjects(tables, indexes);
        });

    /// <summary>
    ///     <para>读取元数据表中的全部键。</para>
    ///     <para>Reads every key in the metadata table.</para>
    /// </summary>
    private static Task<HashSet<string>> ReadMetaKeysAsync(
        SqliteConnection connection, CancellationToken cancellationToken) =>
        ExecuteCoreAsync(async () =>
        {
            var command = connection.CreateCommand();
            command.CommandText = $"SELECT key FROM {MetaTableName}";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var             keys   = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) keys.Add(reader.GetString(0));
            return keys;
        });

    /// <summary>
    ///     <para>读取元数据表中的 schema 版本；行缺失或不是整数时返回 null。</para>
    ///     <para>Reads the schema version from the metadata table; null when the row is missing or not an integer.</para>
    /// </summary>
    internal static Task<int?> ReadSchemaVersionAsync(
        SqliteConnection connection, CancellationToken cancellationToken) =>
        ExecuteCoreAsync(async () =>
        {
            var command = connection.CreateCommand();
            command.CommandText = $"SELECT value FROM {MetaTableName} WHERE key = $key";
            command.Parameters.AddWithValue("$key", SchemaVersionKey);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is string text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                                        out var parsed)
                ? parsed
                : (int?)null;
        });

    /// <summary>
    ///     <para>取指定版本的迁移；编号断层属于编码错误，直接抛出而不是半途而废。</para>
    ///     <para>
    ///         Returns the migration for one version; a numbering gap is a coding error and throws instead of
    ///         stopping halfway.
    ///     </para>
    /// </summary>
    private SchemaMigration MigrationFor(int version)
    {
        // 同时校验数组位置与声明的版本号：只查位置会让"编号断层"静默套用相邻迁移。
        if (version >= 1 && version <= _migrations.Count && _migrations[version - 1].Version == version)
            return _migrations[version - 1];

        throw new SessionStoreDatabaseException($"No business-schema migration defines version {version}; " +
                                                "migrations must be contiguous from 1.");
    }

    /// <summary>
    ///     <para>供迁移、备份与其他 Core 内部消费者打开已有业务库；启用写配置（WAL、FULL、外键）。</para>
    ///     <para>
    ///         Opens an existing business database for migration, backup, and other Core consumers with write configuration
    ///         (WAL, FULL, foreign keys).
    ///     </para>
    /// </summary>
    internal Task<SqliteConnection> OpenExistingForInternalUseAsync(CancellationToken cancellationToken) =>
        OpenAsync(SqliteOpenMode.ReadWrite, true, cancellationToken);

    /// <summary>
    ///     <para>供迁移在首次建库事务前打开或创建业务库。</para>
    ///     <para>Opens or creates the business database for the migration's first creation step.</para>
    /// </summary>
    internal Task<SqliteConnection> OpenOrCreateForInternalUseAsync(CancellationToken cancellationToken) =>
        OpenAsync(SqliteOpenMode.ReadWriteCreate, true, cancellationToken);

    /// <summary>
    ///     <para>读取元数据表中一个键的值；行缺失或值不是文本时返回 null。</para>
    ///     <para>Reads one metadata value; null when the row is missing or the value is not text.</para>
    /// </summary>
    internal static Task<string?> ReadMetaValueAsync(
        SqliteConnection connection, string key, CancellationToken cancellationToken) =>
        ExecuteCoreAsync(async () =>
        {
            var command = connection.CreateCommand();
            command.CommandText = $"SELECT value FROM {MetaTableName} WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value as string;
        });

    /// <summary>
    ///     <para>写入（覆盖）元数据表中的键值；可加入调用方事务，使标记与业务写入处于同一提交边界。</para>
    ///     <para>
    ///         Writes or overwrites one metadata value; it may join the caller's transaction so the marker and business
    ///         writes share one commit boundary.
    ///     </para>
    /// </summary>
    internal static Task WriteMetaValueAsync(
        SqliteConnection  connection, SqliteTransaction? transaction, string key, string value,
        CancellationToken cancellationToken) =>
        ExecuteCoreAsync(async () =>
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT INTO {MetaTableName} (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        });

    /// <summary>
    ///     <para>读取业务库身份；尚未建库或缺少身份键时返回 null。</para>
    ///     <para>Reads the business-database identity; null before creation or when the identity key is missing.</para>
    /// </summary>
    internal static Task<string?> ReadStoreIdentityAsync(
        SqliteConnection connection, CancellationToken cancellationToken) =>
        ReadMetaValueAsync(connection, StoreIdentityKey, cancellationToken);

    /// <summary>
    ///     <para>执行 SQLite 完整性检查；通过时返回 null，否则返回检查输出。</para>
    ///     <para>Runs the SQLite integrity check; null when it passes, otherwise the check output.</para>
    /// </summary>
    internal static Task<string?> RunIntegrityCheckAsync(
        SqliteConnection connection, CancellationToken cancellationToken) =>
        ExecuteCoreAsync(async () =>
        {
            var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            await using var reader   = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var             failures = new List<string>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var line = reader.GetString(0);
                if (!string.Equals(line, "ok", StringComparison.Ordinal)) failures.Add(line);
            }

            return failures.Count == 0 ? null : string.Join("; ", failures.Take(5));
        });

    /// <summary>
    ///     <para>执行 SQLite 外键检查；通过时返回 null，否则返回第一处违规说明。</para>
    ///     <para>Runs the SQLite foreign-key check; null when it passes, otherwise the first violation.</para>
    /// </summary>
    internal static Task<string?> RunForeignKeyCheckAsync(
        SqliteConnection connection, CancellationToken cancellationToken) =>
        ExecuteCoreAsync(async () =>
        {
            var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            var table  = reader.GetString(0);
            var rowId  = reader.IsDBNull(1) ? "?" : reader.GetInt64(1).ToString(CultureInfo.InvariantCulture);
            var parent = reader.GetString(2);
            return $"foreign key violation in '{table}' row {rowId} referencing '{parent}'";
        });

    /// <summary>
    ///     <para>读取业务库的叶子状态。</para>
    ///     <para>Reads the leaf state of the business database.</para>
    /// </summary>
    private LeafState ReadLeafState(out string? detail)
    {
        detail = null;
        try
        {
            var attributes = File.GetAttributes(DatabasePath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0) return LeafState.Regular;
            detail = $"Business database '{DatabasePath}' is a directory or reparse point, not a regular file.";
            return LeafState.NotRegular;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return LeafState.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                      or PathTooLongException)
        {
            detail = $"Business database '{DatabasePath}' cannot be inspected: {ex.Message}";
            return LeafState.Unreadable;
        }
    }

    /// <summary>
    ///     <para>
    ///         判断异常是否构成损坏证据：只有 SQLite 明确报告"不是数据库"或"数据损坏"才算损坏，其余
    ///         运行期失败（锁等待、只读、权限、IO）只算暂时不可用，不能引导用户去恢复备份。
    ///     </para>
    ///     <para>
    ///         Whether an exception is evidence of damage: only a definitive SQLite "not a database" or "corrupt"
    ///         counts, while other runtime failures (lock waits, read-only, permissions, IO) are only temporarily
    ///         unavailable and must not send the user to backup restoration.
    ///     </para>
    /// </summary>
    internal static bool IndicatesDamage(Exception exception)
    {
        const int sqliteCorrupt      = 11; // SQLITE_CORRUPT
        const int sqliteNotADatabase = 26; // SQLITE_NOTADB
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is SqliteException { SqliteErrorCode: sqliteCorrupt or sqliteNotADatabase })
                return true;

        return false;
    }

    /// <summary>
    ///     <para>
    ///         打开一条连接并配置 PRAGMA；<paramref name="configureForWrite" /> 为 false 时只设置锁等待，
    ///         不切换 journal 模式，使可用性探测不修改业务文件。
    ///     </para>
    ///     <para>
    ///         Opens a connection and configures PRAGMAs; when <paramref name="configureForWrite" /> is false only
    ///         the lock wait is set and the journal mode is left alone, so probing never modifies the business file.
    ///     </para>
    /// </summary>
    internal async Task<SqliteConnection> OpenAsync(
        SqliteOpenMode mode, bool configureForWrite, CancellationToken cancellationToken)
    {
        // 业务库不使用共享缓存（Cache 保持默认的私有缓存）：WAL 已提供跨连接可见性，共享缓存反而
        // 引入额外的跨连接锁失败面。provider 默认会在命令超时内反复重试 SQLITE_BUSY（30 秒），
        // 与 busy_timeout 对齐才真正有界。
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource     = DatabasePath,
            Mode           = mode,
            Pooling        = false,
            DefaultTimeout = BusyTimeoutMs / 1000
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            // 打开前补完中断的恢复：隔离名下的旧日志不在 SQLite 的视野里，直接打开等于读到缺少最新数据的旧主库。
            // 探测与打开都经过这里，绕过可用性检查的调用方同样被拦住。
            // Completing an interrupted restore before opening: journals under quarantined names are invisible to
            // SQLite, so opening directly would read an old main file without its latest data. Probing and opening both
            // pass through here, so callers that skip the availability check are protected too.
            if (IsBusinessStore)
                await SessionStoreRestoreRecovery.RecoverAsync(StoreDirectory, cancellationToken).ConfigureAwait(false);
            // 叶子校验只在这里做一次：它可能在目录逃逸、链接或占位时拒绝，必须落在下面的包装内，
            // 否则 SessionStoreException 会绕过本类型的异常契约和可用性分类。
            SessionStorePaths.EnsureStoreLeaf(StoreDirectory, DatabasePath);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var pragma = connection.CreateCommand();
            // busy_timeout 必须先于 journal_mode：空库首开并发时 WAL 切换语句自身也可能争锁。
            // 业务库保存不可重建的组织状态，因此用 synchronous=FULL 换取落盘语义，不接受索引库的 NORMAL。
            // 外键单独开启：SQLite 默认关闭，缺失时"项目删除必须无残留归属"就只是约定而非约束。
            pragma.CommandText = configureForWrite
                ? $"PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; " +
                  "PRAGMA foreign_keys=ON;"
                : $"PRAGMA busy_timeout={BusyTimeoutMs};";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (Exception ex) when (ex is SessionStoreException or SqliteException or IOException
                                      or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new SessionStoreDatabaseException($"Business database '{DatabasePath}' " +
                                                    $"could not be opened: {ex.Message}", ex);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     <para>
    ///         在统一边界执行一次数据库操作，把 SQLite 失败与文件系统失败包装为
    ///         <see cref="SessionStoreDatabaseException" />；取消与参数错误原样穿透。
    ///     </para>
    ///     <para>
    ///         Runs one database operation behind a single boundary, wrapping SQLite and file-system failures into
    ///         <see cref="SessionStoreDatabaseException" />; cancellation and argument errors pass through.
    ///     </para>
    /// </summary>
    private static async Task ExecuteCoreAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException
                                      or NotSupportedException or PathTooLongException)
        {
            throw new SessionStoreDatabaseException($"Business database operation failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     <para>带返回值的统一边界执行；包装范围与无返回值重载一致。</para>
    ///     <para>The returning variant of the unified boundary; it wraps exactly what the void overload wraps.</para>
    /// </summary>
    private static async Task<T> ExecuteCoreAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException
                                      or NotSupportedException or PathTooLongException)
        {
            throw new SessionStoreDatabaseException($"Business database operation failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     <para>版本 1 的迁移定义：首个业务 schema，建立元数据表与全部业务表/索引。</para>
    ///     <para>
    ///         The version-1 migration: the first business schema creating the metadata table and every business table and
    ///         index.
    ///     </para>
    /// </summary>
    private static SchemaMigration CreateInitialSchema() =>
        new(1, BaseSchemaSql, new SchemaShape(RequiredTables, RequiredIndexes));

    /// <summary>
    ///     <para>
    ///         版本 2 的迁移定义：新增会话删除墓碑表与状态索引，承载"先落删除意图、再收尾日志与投影"的可续作删除。
    ///     </para>
    ///     <para>
    ///         The version-2 migration: adds the session-deletion tombstone table and its state index, carrying the
    ///         resumable deletion that records intent before the log and projections are cleaned up.
    ///     </para>
    /// </summary>
    private static SchemaMigration AddSessionTombstones() => new(2, TombstoneSchemaSql, Version2Shape);

    /// <summary>
    ///     <para>业务库文件名的叶子状态：普通文件、缺失、被目录或链接占用、无法判定。</para>
    ///     <para>
    ///         The leaf state of the business-database name: regular file, missing, occupied by a directory or link, or
    ///         undeterminable.
    ///     </para>
    /// </summary>
    private enum LeafState
    {
        Regular,
        Missing,
        NotRegular,
        Unreadable
    }

    /// <summary>
    ///     <para>单个版本的迁移定义：目标版本、要执行的 SQL，以及执行后必须呈现的结构。</para>
    ///     <para>One version's migration: the target version, its SQL, and the structure it must produce.</para>
    /// </summary>
    internal sealed record SchemaMigration(int Version, string Sql, SchemaShape Shape);

    /// <summary>
    ///     <para>一个版本应当具备的表与显式索引；迁移前后都按它核对，避免"补建空表"掩盖数据丢失。</para>
    ///     <para>
    ///         The tables and explicit indexes one version must have; checked before and after every migration so
    ///         re-created empty tables never disguise lost data.
    ///     </para>
    /// </summary>
    internal sealed record SchemaShape(string[] Tables, string[] Indexes);

    /// <summary>
    ///     <para>从 sqlite_master 读到的实际结构。</para>
    ///     <para>The structure actually read from sqlite_master.</para>
    /// </summary>
    private sealed record SchemaObjects(HashSet<string> Tables, HashSet<string> Indexes);
}

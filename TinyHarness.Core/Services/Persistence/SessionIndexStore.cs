using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Persistence;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>索引可用性：可用、尚无 schema、缺失、版本不受支持、损坏或其他失败。</para>
///     <para>Index availability: available, schema missing, absent, unsupported version, damaged, or other failure.</para>
/// </summary>
public enum SessionIndexAvailabilityStatus
{
    Available,
    NeedsSchema,
    Missing,
    Unsupported,
    Damaged,
    Failed
}

/// <summary>
///     <para>带说明的索引可用性结果；说明不含凭据。</para>
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
public sealed record SessionIndexAvailability(SessionIndexAvailabilityStatus Status, string? Detail)
{
    /// <summary>
    ///     <para>索引可用的单例结果。</para>
    ///     <para>The singleton result for an available index.</para>
    /// </summary>
    public static readonly SessionIndexAvailability Ok = new(SessionIndexAvailabilityStatus.Available, null);
}

/// <summary>
///     <para>
///         单个 session store 的 SQLite 列表索引：保存可从 JSONL 重建的会话摘要、来源标记、失败状态与归档投影，
///         供列表与启动路径以轻量查询替代全库完整读取。
///     </para>
///     <para>
///         A per-store SQLite list index holding the JSONL-rebuildable session summaries, source markers, failure
///         states, and archived projections, so list and startup paths replace full-store reads with light queries.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         权威数据仍是会话 JSONL、项目目录册与归档状态文件；本索引只是可重建投影。摘要更新永不改写归档投影，
///         归档更新永不改写摘要，任何路径都不写入无法从来源重建的状态。数据库、WAL 与 SHM 都位于受保护的
///         session store 目录内，不新增工作区或沙箱可写入口。
///     </para>
///     <para>
///         Authoritative data remains the session JSONL, project catalog, and archive-state file; this index is only
///         a rebuildable projection. Summary updates never touch the archived projection and archive updates never
///         touch summaries, and no path writes state that cannot be rebuilt from sources. The database, WAL, and SHM
///         all live inside the protected session-store directory, adding no workspace- or sandbox-writable entry.
///     </para>
///     <para>
///         每次操作独立打开连接（禁用连接池），WAL 加短事务配合 busy 超时支持 CLI／Desktop 双进程读写；
///         不实现跨 JSONL、sidecar 与 SQLite 的原子事务。条目更新按序号与字节条件提交，健康条目只被更新或
///         相同快照刷新，不被更旧快照回退；归档投影独立更新，不被日志摘要覆盖。锁等待耗尽与损坏包装为
///         <see cref="SessionIndexException" />，由调用方降级为直接扫描并给出可见原因；
///         未识别的新版本不得被旧程序清空或降级。
///     </para>
///     <para>
///         Every operation opens its own connection (pooling disabled); WAL with short transactions plus a busy
///         timeout supports CLI/Desktop dual-process reads and writes, with no atomic transaction across JSONL,
///         sidecars, and SQLite. Entry updates commit conditionally on sequence and bytes: healthy entries accept
///         only fresher or identical snapshots, never regressions from older ones, and the archived projection
///         updates independently without being overwritten by log summaries. Exhausted lock waits and damage surface
///         as <see cref="SessionIndexException" /> for callers to degrade to direct scans with a visible reason;
///         unrecognized newer versions are never cleared or downgraded by older programs.
///     </para>
/// </remarks>
public sealed class SessionIndexStore
{
    /// <summary>
    ///     <para>session store 目录内的索引数据库文件名。</para>
    ///     <para>The index database file name inside the session-store directory.</para>
    /// </summary>
    public const string DatabaseFileName = "session-index.db";

    /// <summary>
    ///     <para>当前实现支持的 schema 版本。</para>
    ///     <para>The schema version supported by this implementation.</para>
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    private const int BusyTimeoutMs = 3000;

    private const string RowSelectSql = """
        SELECT id, workspace_root, project_id, assignment_state, title, created_utc, updated_utc,
               last_activity_utc, turn_count, permission_mode, commit_sequence, indexed_bytes,
               source_length, source_modified_utc, entry_state, error, archived
        FROM session_summary
        """;

    /// <summary>
    ///     <para>绑定一个受保护 session store 目录；目录须为绝对路径，不立即访问文件系统。</para>
    ///     <para>
    ///         Binds one protected session-store directory; it must be absolute, and no file system access happens
    ///         yet.
    ///     </para>
    /// </summary>
    public SessionIndexStore(string storeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        if (!Path.IsPathFullyQualified(storeDirectory))
            throw new SessionIndexException($"Session-store directory '{storeDirectory}' must be an absolute path.");
        StoreDirectory = Path.GetFullPath(storeDirectory);
        DatabasePath   = PathFor(StoreDirectory);
    }

    /// <summary>
    ///     <para>绑定的 session store 目录（规范化绝对路径）。</para>
    ///     <para>The bound session-store directory (normalized absolute path).</para>
    /// </summary>
    public string StoreDirectory { get; }

    /// <summary>
    ///     <para>索引数据库文件路径。</para>
    ///     <para>The index database file path.</para>
    /// </summary>
    public string DatabasePath { get; }

    /// <summary>
    ///     <para>返回 session store 目录对应的索引数据库路径。</para>
    ///     <para>Returns the index database path for a session-store directory.</para>
    /// </summary>
    public static string PathFor(string storeDirectory) => Path.Combine(storeDirectory, DatabaseFileName);

    /// <summary>
    ///     <para>判断索引数据库文件是否已存在。</para>
    ///     <para>Whether the index database file already exists.</para>
    /// </summary>
    public static bool Exists(string storeDirectory) => File.Exists(PathFor(storeDirectory));

    /// <summary>
    ///     <para>检查索引可用性：缺失、缺 schema、版本不受支持、损坏或其他失败分别报告。</para>
    ///     <para>
    ///         Checks index availability: missing, schema-less, unsupported version, damaged, and other failures are
    ///         reported separately.
    ///     </para>
    /// </summary>
    public async Task<SessionIndexAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(DatabasePath))
            return new SessionIndexAvailability(SessionIndexAvailabilityStatus.Missing, null);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var             version = await ReadSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            return version is null
                ? new SessionIndexAvailability(SessionIndexAvailabilityStatus.NeedsSchema, null)
                : version.Value > CurrentSchemaVersion
                    ? new SessionIndexAvailability(SessionIndexAvailabilityStatus.Unsupported,
                                                   $"Index schema version {version.Value} is newer than supported version {CurrentSchemaVersion}.")
                    : SessionIndexAvailability.Ok;
        }
        catch (SessionIndexException ex) when (ex.InnerException is IOException or UnauthorizedAccessException
                                                or NotSupportedException or PathTooLongException)
        {
            return new SessionIndexAvailability(SessionIndexAvailabilityStatus.Failed, ex.Message);
        }
        catch (SessionIndexException ex)
        {
            return new SessionIndexAvailability(SessionIndexAvailabilityStatus.Damaged, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                      or PathTooLongException)
        {
            return new SessionIndexAvailability(SessionIndexAvailabilityStatus.Failed, ex.Message);
        }
    }

    /// <summary>
    ///     <para>创建或补齐索引 schema；已存在的更高版本直接拒绝，不得被旧程序清空或降级。</para>
    ///     <para>
    ///         Creates or completes the index schema; a newer existing version is refused outright and never cleared
    ///         or downgraded by older programs.
    ///     </para>
    /// </summary>
    /// <exception cref="SessionIndexException">
    ///     <para>数据库损坏、版本不受支持或存储失败。</para>
    ///     <para>The database is damaged, its version is unsupported, or storage failed.</para>
    /// </exception>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var existingVersion = await ReadSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (existingVersion > CurrentSchemaVersion)
            throw new SessionIndexException($"Session index '{DatabasePath}' uses schema version " +
                                            $"{existingVersion}, newer than supported version " +
                                            $"{CurrentSchemaVersion}; it must not be downgraded.");

        await ExecuteCoreAsync(async () =>
        {
            await using var transaction = await BeginWriteAsync(connection, cancellationToken).ConfigureAwait(false);
            var             create      = connection.CreateCommand();
            create.Transaction = transaction;
            create.CommandText = """
            CREATE TABLE IF NOT EXISTS index_meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS session_summary (
                id                  TEXT    PRIMARY KEY,
                workspace_root      TEXT    NOT NULL,
                project_id          TEXT,
                assignment_state    INTEGER NOT NULL,
                title               TEXT    NOT NULL,
                created_utc         INTEGER NOT NULL,
                updated_utc         INTEGER NOT NULL,
                last_activity_utc   INTEGER NOT NULL,
                turn_count          INTEGER NOT NULL,
                permission_mode     TEXT,
                commit_sequence     INTEGER NOT NULL,
                indexed_bytes       INTEGER NOT NULL,
                source_length       INTEGER NOT NULL,
                source_modified_utc INTEGER,
                entry_state         INTEGER NOT NULL,
                error               TEXT,
                archived            INTEGER
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS idx_summary_activity
                ON session_summary (entry_state, last_activity_utc DESC, id);
            CREATE INDEX IF NOT EXISTS idx_summary_project
                ON session_summary (entry_state, project_id, last_activity_utc DESC, id);
            INSERT OR REPLACE INTO index_meta (key, value) VALUES ('schema_version', $version);
            """;
            create.Parameters.AddWithValue("$version",
                                           CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>按游标分页查询健康条目的摘要；排序固定为活动时间降序加 ID 次键。</para>
    ///     <para>
    ///         Queries summaries of healthy entries behind a cursor; ordering is fixed to activity time descending
    ///         with the id tie-break.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>失败、过期或待建立条目不返回，由对账与状态展示负责；归档未知条目包含在非归档视图中。</para>
    ///     <para>
    ///         Failed, stale, and pending entries are not returned — reconciliation and status display own them;
    ///         entries with an unknown archive state stay in the non-archived view.
    ///     </para>
    /// </remarks>
    public async Task<IReadOnlyList<SessionSummary>> ListSummariesAsync(
        SessionSummaryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit < 1)
            throw new ArgumentOutOfRangeException(nameof(query), "The summary query limit must be positive.");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var sql = new StringBuilder("""
                                    SELECT id, workspace_root, project_id, assignment_state, title, created_utc, updated_utc,
                                           last_activity_utc, turn_count, permission_mode, commit_sequence, indexed_bytes
                                    FROM session_summary WHERE entry_state = $current
                                    """);
        if (query.Archived is true) sql.Append(" AND archived = 1");
        else if (query.Archived is false) sql.Append(" AND (archived = 0 OR archived IS NULL)");
        if (query.ProjectId is not null) sql.Append(" AND project_id = $project");
        if (query.AfterLastActivityUtcMs is not null)
            sql.Append(" AND (last_activity_utc < $after_activity OR (last_activity_utc = $after_activity AND id > $after_id))");
        sql.Append(" ORDER BY last_activity_utc DESC, id LIMIT $limit");

        var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        command.Parameters.AddWithValue("$current", (long)SessionIndexEntryState.Current);
        if (query.ProjectId is not null) command.Parameters.AddWithValue("$project", query.ProjectId);
        if (query.AfterLastActivityUtcMs is { } cursorActivity)
        {
            command.Parameters.AddWithValue("$after_activity", cursorActivity);
            command.Parameters.AddWithValue("$after_id", query.AfterId ?? string.Empty);
        }

        command.Parameters.AddWithValue("$limit", query.Limit);
        return await ExecuteCoreAsync(async () =>
        {
            await using var reader    = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var             summaries = new List<SessionSummary>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                summaries.Add(ReadSummary(reader));
            return (IReadOnlyList<SessionSummary>)summaries;
        }).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>返回健康条目总数。</para>
    ///     <para>Returns the healthy-entry count.</para>
    /// </summary>
    public async Task<int> CountSummariesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var             command    = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM session_summary WHERE entry_state = $current";
        command.Parameters.AddWithValue("$current", (long)SessionIndexEntryState.Current);
        return await ExecuteCoreAsync(async () =>
        {
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt32(result ?? 0L, CultureInfo.InvariantCulture);
        }).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>返回单个会话的索引行；不存在时为 null。</para>
    ///     <para>Returns one session's index row, or null when absent.</para>
    /// </summary>
    public async Task<SessionIndexRow?> GetRowAsync(string sessionId, CancellationToken cancellationToken)
    {
        sessionId = NormalizeSessionId(sessionId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var             command    = connection.CreateCommand();
        command.CommandText = RowSelectSql + " WHERE id = $id";
        command.Parameters.AddWithValue("$id", sessionId);
        return await ExecuteCoreAsync(async () =>
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            return ReadRow(reader);
        }).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>返回全部索引行状态，供后台对账与来源比较；无游标，调用方自行分批。</para>
    ///     <para>
    ///         Returns every index row for background reconciliation and source comparison; unpaginated, with callers
    ///         batching on their own.
    ///     </para>
    /// </summary>
    public async Task<IReadOnlyList<SessionIndexRow>> ListRowsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var             command    = connection.CreateCommand();
        command.CommandText = RowSelectSql;
        return await ExecuteCoreAsync(async () =>
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var             rows   = new List<SessionIndexRow>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rows.Add(ReadRow(reader));
            return (IReadOnlyList<SessionIndexRow>)rows;
        }).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>
    ///         打开一条常驻监测连接，供跨进程前后比较 data_version 发现数据库已变化；调用方负责释放。
    ///     </para>
    ///     <para>
    ///         Opens a standing monitor connection for cross-process data_version comparisons; the caller owns and
    ///         releases it.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         data_version 只在同一连接上前后比较才有变化语义，且不是全局业务版本号：仅作为需要重新查询的提示，
    ///         不用于排序、幂等判定或新旧比较。它发现不了“来源已提交、索引尚未更新”的窗口，须辅以对账。
    ///     </para>
    ///     <para>
    ///         data_version comparison is only meaningful across successive reads on this one connection, and the
    ///         value is a change hint, never a global business version: it does not order, deduplicate, or rank work.
    ///         It cannot see a committed source awaiting its index update, so reconciliation must back it.
    ///     </para>
    /// </remarks>
    public async Task<SessionIndexVersionWatcher> OpenVersionWatcherAsync(CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return new SessionIndexVersionWatcher(connection, DatabasePath);
    }

    /// <summary>
    ///     <para>
    ///         条件提交一条重建／扫描结果：健康条目只接受更新或相同的来源快照，不被更旧快照回退；
    ///         归档投影保持不变。
    ///     </para>
    ///     <para>
    ///         Conditionally commits a rebuild or scan result: healthy entries accept only fresher or identical source
    ///         snapshots and never regress; the archived projection is left untouched.
    ///     </para>
    /// </summary>
    public Task WriteSummaryAsync(
        SessionSummary    summary, long sourceLength, DateTimeOffset? sourceModifiedUtc,
        CancellationToken cancellationToken)
    {
        ValidateSummary(summary);
        return UpsertCoreAsync(summary, sourceLength, sourceModifiedUtc, SessionIndexEntryState.Current, null,
                               cancellationToken);
    }

    /// <summary>
    ///     <para>
    ///         条件提交一次已提交批次推进的摘要：仅当新序号更大，或序号相同且字节不少于既有值时应用；
    ///         归档投影保持不变。
    ///     </para>
    ///     <para>
    ///         Conditionally applies a summary advanced by one committed batch: only a larger sequence, or the same
    ///         sequence with no fewer bytes, is applied; the archived projection is left untouched.
    ///     </para>
    /// </summary>
    /// <returns>
    ///     <para>是否实际应用；被更旧快照拒绝时为 false。</para>
    ///     <para>Whether the update applied; false when an older snapshot was rejected.</para>
    /// </returns>
    public Task<bool> TryAdvanceFromCommitAsync(
        SessionSummary    summary, long sourceLength, DateTimeOffset? sourceModifiedUtc,
        CancellationToken cancellationToken)
    {
        ValidateSummary(summary);
        return UpsertCoreAsync(summary, sourceLength, sourceModifiedUtc, SessionIndexEntryState.Current, null,
                               cancellationToken, true);
    }

    /// <summary>
    ///     <para>
    ///         把条目标记为失败并记录不含凭据的原因；已有摘要保留为可展示但不可验证的旧值，归档投影保持不变。
    ///     </para>
    ///     <para>
    ///         Marks an entry failed with a credential-free reason; an existing summary stays displayed but
    ///         unverifiable, and the archived projection is left untouched.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>行不存在时插入仅含失败标记的占位行，使坏日志对状态可见且不阻塞其他条目。</para>
    ///     <para>
    ///         A missing row becomes a marker-only placeholder so damaged logs stay visible without blocking other
    ///         entries.
    ///     </para>
    /// </remarks>
    public Task MarkFailedAsync(string sessionId, string error, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        sessionId = NormalizeSessionId(sessionId);
        return UpsertFailureCoreAsync(sessionId, error, cancellationToken);
    }

    /// <summary>
    ///     <para>
    ///         同步归档投影：来自归档状态文件的 ID 集合更新各行的 archived 列，不改写摘要与来源标记；
    ///         未列出的行按 false 记录，缺失行不创建。
    ///     </para>
    ///     <para>
    ///         Syncs the archived projection: the id set from the archive-state file updates each row's archived
    ///         column without touching summaries or source markers; unlisted rows record false and missing rows are
    ///         not created.
    ///     </para>
    /// </summary>
    public async Task SyncArchiveProjectionAsync(
        IReadOnlySet<string> archivedSessionIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archivedSessionIds);
        foreach (var id in archivedSessionIds) ValidateSessionId(id);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteCoreAsync(async () =>
        {
            await using var transaction = await BeginWriteAsync(connection, cancellationToken).ConfigureAwait(false);
            var             setKnown    = connection.CreateCommand();
            setKnown.Transaction = transaction;
            // 失败条目不参与归档刷新：保留既有投影（占位行为 NULL 未知），不以本轮结果覆盖不可验证行。
            setKnown.CommandText = """
            UPDATE session_summary SET archived =
                CASE WHEN id IN (SELECT value FROM json_each($ids)) THEN 1 ELSE 0 END
            WHERE entry_state != $failed
            """;
            setKnown.Parameters.AddWithValue("$ids", SerializeIdSet(archivedSessionIds));
            setKnown.Parameters.AddWithValue("$failed", (long)SessionIndexEntryState.Failed);
            await setKnown.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>移除一个条目；来源日志已删除且索引不再引用它时使用。</para>
    ///     <para>Removes one entry; used when the source log is gone and the index should stop referencing it.</para>
    /// </summary>
    public async Task RemoveAsync(string sessionId, CancellationToken cancellationToken)
    {
        sessionId = NormalizeSessionId(sessionId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var             command    = connection.CreateCommand();
        command.CommandText = "DELETE FROM session_summary WHERE id = $id";
        command.Parameters.AddWithValue("$id", sessionId);
        await ExecuteCoreAsync(() => command.ExecuteNonQueryAsync(cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>清空全部条目与续扫状态，保留 schema 与版本；用于显式全量重建的起点。</para>
    ///     <para>
    ///         Clears every entry and rescan state while keeping the schema and version; the starting point of an
    ///         explicit full rebuild.
    ///     </para>
    /// </summary>
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteCoreAsync(async () =>
        {
            await using var transaction = await BeginWriteAsync(connection, cancellationToken).ConfigureAwait(false);
            var             clear       = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM session_summary; DELETE FROM index_meta WHERE key != 'schema_version';";
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>读取索引元数据值；缺失时为 null。</para>
    ///     <para>Reads an index metadata value, or null when absent.</para>
    /// </summary>
    public async Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var             command    = connection.CreateCommand();
        command.CommandText = "SELECT value FROM index_meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return await ExecuteCoreAsync(async () =>
        {
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is string value ? value : null;
        }).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>写入索引元数据值；续扫状态等版本化进度由此保存。</para>
    ///     <para>
    ///         Writes an index metadata value; versioned progress such as rescan state is stored here.
    ///     </para>
    /// </summary>
    public async Task SetMetaAsync(string key, string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteCoreAsync(async () =>
        {
            await using var transaction = await BeginWriteAsync(connection, cancellationToken).ConfigureAwait(false);
            var             command     = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR REPLACE INTO index_meta (key, value) VALUES ($key, $value)";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>
    ///         在统一边界执行一次数据库操作，把 SQLite 锁等待耗尽、损坏与文件系统失败包装为
    ///         <see cref="SessionIndexException" />；取消与参数错误原样穿透。
    ///     </para>
    ///     <para>
    ///         Runs one database operation behind a single boundary, wrapping exhausted SQLite lock waits, damage,
    ///         and file-system failures into <see cref="SessionIndexException" />; cancellation and argument errors
    ///         pass through unchanged.
    ///     </para>
    /// </summary>
    private async Task ExecuteCoreAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException
                                      or NotSupportedException or PathTooLongException)
        {
            throw new SessionIndexException($"Session index '{DatabasePath}' operation failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     <para>带返回值的统一边界执行；包装范围与无返回值重载一致。</para>
    ///     <para>The returning variant of the unified boundary; it wraps exactly what the void overload wraps.</para>
    /// </summary>
    private async Task<T> ExecuteCoreAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException
                                      or NotSupportedException or PathTooLongException)
        {
            throw new SessionIndexException($"Session index '{DatabasePath}' operation failed: {ex.Message}", ex);
        }
    }

    private async Task<bool> UpsertCoreAsync(
        SessionSummary         summary, long    sourceLength, DateTimeOffset?   sourceModifiedUtc,
        SessionIndexEntryState state,   string? error,        CancellationToken cancellationToken,
        bool                   requireProgress = false)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // 条件更新：健康条目只接受更新快照；非健康条目（待建立、过期、失败）接受任何重建结果。
        var condition = requireProgress
            ? """
            WHERE excluded.commit_sequence > session_summary.commit_sequence
               OR (excluded.commit_sequence = session_summary.commit_sequence AND
                   excluded.indexed_bytes >= session_summary.indexed_bytes)
            """
            : """
            WHERE excluded.commit_sequence > session_summary.commit_sequence
               OR (excluded.commit_sequence = session_summary.commit_sequence AND
                   excluded.indexed_bytes >= session_summary.indexed_bytes)
               OR session_summary.entry_state != $current
            """;
        var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO session_summary (
                id, workspace_root, project_id, assignment_state, title, created_utc, updated_utc,
                last_activity_utc, turn_count, permission_mode, commit_sequence, indexed_bytes,
                source_length, source_modified_utc, entry_state, error, archived
            ) VALUES (
                $id, $workspace, $project, $assignment, $title, $created, $updated,
                $activity, $turns, $permission, $sequence, $bytes,
                $source_length, $source_modified, $state, $error, NULL
            )
            ON CONFLICT(id) DO UPDATE SET
                workspace_root      = $workspace,
                project_id          = $project,
                assignment_state    = $assignment,
                title               = $title,
                created_utc         = $created,
                updated_utc         = $updated,
                last_activity_utc   = $activity,
                turn_count          = $turns,
                permission_mode     = $permission,
                commit_sequence     = $sequence,
                indexed_bytes       = $bytes,
                source_length       = $source_length,
                source_modified_utc = $source_modified,
                entry_state         = $state,
                error               = $error
            {condition}
            """;
        FillSummaryParameters(command.Parameters, summary, sourceLength, sourceModifiedUtc, state, error);
        if (!requireProgress) command.Parameters.AddWithValue("$current", (long)SessionIndexEntryState.Current);
        var affected = await ExecuteCoreAsync(() => command.ExecuteNonQueryAsync(cancellationToken))
           .ConfigureAwait(false);
        return affected > 0;
    }

    private async Task UpsertFailureCoreAsync(string sessionId, string error, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // 失败标记保留既有摘要列；无行时以会话 ID 建占位行，摘要列用哨兵值，消费方按 entry_state 判读。
        var             command    = connection.CreateCommand();
        command.CommandText = """
        INSERT INTO session_summary (
            id, workspace_root, project_id, assignment_state, title, created_utc, updated_utc,
            last_activity_utc, turn_count, permission_mode, commit_sequence, indexed_bytes,
            source_length, source_modified_utc, entry_state, error, archived
        ) VALUES (
            $id, '', NULL, $pending, '', 0, 0,
            0, 0, NULL, 0, 0,
            0, NULL, $failed, $error, NULL
        )
        ON CONFLICT(id) DO UPDATE SET
            entry_state = $failed,
            error       = $error
        """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$pending", (long)SessionProjectAssignmentState.PendingInitialization);
        command.Parameters.AddWithValue("$failed", (long)SessionIndexEntryState.Failed);
        command.Parameters.AddWithValue("$error", error);
        await ExecuteCoreAsync(() => command.ExecuteNonQueryAsync(cancellationToken)).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        SessionStorePaths.EnsureStoreLeaf(StoreDirectory, DatabasePath);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource     = DatabasePath,
            Mode           = SqliteOpenMode.ReadWriteCreate,
            Cache          = SqliteCacheMode.Shared,
            Pooling        = false,
            // provider 会在命令超时内反复重试 SQLITE_BUSY，默认 30 秒；与 busy_timeout 对齐为 3 秒，
            // 锁等待才真正有界，业务提交也不因他人持锁被拖长。
            DefaultTimeout = BusyTimeoutMs / 1000
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var pragma = connection.CreateCommand();
            // busy_timeout 必须先于 journal_mode：空库首开并发时 WAL 切换语句自身也可能争锁，
            // 先设超时才能按有界等待而非立刻 SQLITE_BUSY 失败。
            pragma.CommandText =
                $"PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException
                                      or NotSupportedException or PathTooLongException)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new SessionIndexException($"Session index '{DatabasePath}' could not be opened: {ex.Message}", ex);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<SqliteTransaction> BeginWriteAsync(
        SqliteConnection connection, CancellationToken cancellationToken) =>
        (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    ///     <para>读取 schema 版本；表尚未建立时返回 null，损坏文件包装为索引异常。</para>
    ///     <para>
    ///         Reads the schema version; null when the tables do not exist yet, and damaged files surface as index
    ///         exceptions.
    ///     </para>
    /// </summary>
    private static async Task<int?> ReadSchemaVersionAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM index_meta WHERE key = 'schema_version'";
        try
        {
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is string value ? int.Parse(value, CultureInfo.InvariantCulture) : null;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // SQLITE_ERROR（错误码 1）覆盖 "no such table"：文件存在但尚未初始化。
            return null;
        }
        catch (SqliteException ex)
        {
            throw new SessionIndexException($"Session index database is damaged: {ex.Message}", ex);
        }
        catch (FormatException ex)
        {
            throw new SessionIndexException($"Session index schema version is not an integer: {ex.Message}", ex);
        }
    }

    private static void FillSummaryParameters(
        SqliteParameterCollection parameters,        SessionSummary         summary, long    sourceLength,
        DateTimeOffset?           sourceModifiedUtc, SessionIndexEntryState state,   string? error)
    {
        // 行键统一小写，避免大写十六进制摘要插入成另一行。
        parameters.AddWithValue("$id", summary.Id.ToLowerInvariant());
        parameters.AddWithValue("$workspace", summary.WorkspaceRoot);
        parameters.AddWithValue("$project", (object?)summary.ProjectId ?? DBNull.Value);
        parameters.AddWithValue("$assignment", (long)summary.ProjectAssignmentState);
        parameters.AddWithValue("$title", summary.Title);
        parameters.AddWithValue("$created", summary.CreatedUtc.ToUnixTimeMilliseconds());
        parameters.AddWithValue("$updated", summary.UpdatedUtc.ToUnixTimeMilliseconds());
        parameters.AddWithValue("$activity", summary.LastActivityUtc.ToUnixTimeMilliseconds());
        parameters.AddWithValue("$turns", summary.TurnCount);
        parameters.AddWithValue("$permission", summary.PermissionMode is { } mode ? mode.ToString() : DBNull.Value);
        parameters.AddWithValue("$sequence", summary.CommitSequence);
        parameters.AddWithValue("$bytes", summary.IndexedBytes);
        parameters.AddWithValue("$source_length", sourceLength);
        parameters.AddWithValue("$source_modified",
                                sourceModifiedUtc?.ToUnixTimeMilliseconds() ?? (object)DBNull.Value);
        parameters.AddWithValue("$state", (long)state);
        parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
    }

    private static SessionSummary ReadSummary(SqliteDataReader reader) => new()
    {
        Id                     = reader.GetString(0),
        WorkspaceRoot          = reader.GetString(1),
        ProjectId              = reader.IsDBNull(2) ? null : reader.GetString(2),
        ProjectAssignmentState = (SessionProjectAssignmentState)reader.GetInt64(3),
        Title                  = reader.GetString(4),
        CreatedUtc             = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
        UpdatedUtc             = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
        LastActivityUtc        = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
        TurnCount              = reader.GetInt32(8),
        PermissionMode         = reader.IsDBNull(9) ? null : Enum.Parse<SessionPermissionMode>(reader.GetString(9)),
        CommitSequence         = reader.GetInt64(10),
        IndexedBytes           = reader.GetInt64(11)
    };

    private static SessionIndexRow ReadRow(SqliteDataReader reader)
    {
        var state      = (SessionIndexEntryState)reader.GetInt64(14);
        // 占位失败行的 commit_sequence 为 0 哨兵；健康摘要的序号恒不小于 1（ValidateSummary），据此判读。
        var hasSummary = reader.GetInt64(10) > 0;
        return new SessionIndexRow
        {
            Id           = reader.GetString(0),
            State        = state,
            Summary      = hasSummary ? ReadSummary(reader) : null,
            SourceLength = reader.GetInt64(12),
            SourceModifiedUtc = reader.IsDBNull(13)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(13)),
            Archived = reader.IsDBNull(16) ? null : reader.GetInt64(16) != 0,
            Error    = reader.IsDBNull(15) ? null : reader.GetString(15)
        };
    }

    /// <summary>
    ///     <para>把会话 ID 集合序列化为 JSON 数组文本；ID 恒为 32 位十六进制，无需转义，统一按小写输出。</para>
    ///     <para>
    ///         Serializes a session-id set as JSON array text; ids are always 32 hexadecimal characters needing no
    ///         escaping, emitted lowercase.
    ///     </para>
    /// </summary>
    private static string SerializeIdSet(IReadOnlySet<string> ids)
    {
        var builder = new StringBuilder(ids.Count * 35 + 2);
        builder.Append('[');
        var first = true;
        foreach (var id in ids)
        {
            if (!first) builder.Append(',');
            builder.Append('"').Append(id.ToLowerInvariant()).Append('"');
            first = false;
        }

        builder.Append(']');
        return builder.ToString();
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (!SessionStorePaths.IsValidSessionId(sessionId))
            throw new ArgumentException("A session id must contain exactly 32 hexadecimal characters.",
                                        nameof(sessionId));
    }

    /// <summary>
    ///     <para>校验会话 ID 并归一为小写；行键按小写 TEXT 比较，大写十六进制必须先归一才能命中既有行。</para>
    ///     <para>
    ///         Validates a session id and normalizes it to lowercase; row keys compare as lowercase TEXT, so uppercase
    ///         hexadecimal must normalize before it can hit existing rows.
    ///     </para>
    /// </summary>
    private static string NormalizeSessionId(string sessionId)
    {
        ValidateSessionId(sessionId);
        return sessionId.ToLowerInvariant();
    }

    private static void ValidateSummary(SessionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ValidateSessionId(summary.Id);
        if (summary.CommitSequence < 1)
            throw new ArgumentException("An indexed summary must reference at least batch sequence 1.",
                                        nameof(summary));
        if (summary.IndexedBytes < 0)
            throw new ArgumentException("Indexed bytes must not be negative.", nameof(summary));
    }
}

/// <summary>
///     <para>
///         索引数据库的常驻监测连接：在同一条连接上前后读取 data_version，发现其他连接的已提交变化后提示重查。
///     </para>
///     <para>
///         A standing monitor connection for the index database: successive data_version reads on this one
///         connection notice changes committed by other connections and hint at a due requery.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         监测连接不执行业务读写；被监测连接自身的提交不改变其 data_version，其他连接（含本进程其他连接与
///         跨进程 writer）的提交会反映进来。释放前连接保持打开，调用方以 using 或显式释放结束监测。
///     </para>
///     <para>
///         The monitor never performs business reads or writes; commits on the monitor itself leave its
///         data_version untouched, while other connections — including sibling in-process and cross-process
///         writers — show up in it. The connection stays open until release, and callers end monitoring through
///         using or explicit disposal.
///     </para>
/// </remarks>
public sealed class SessionIndexVersionWatcher : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string           _databasePath;

    internal SessionIndexVersionWatcher(SqliteConnection connection, string databasePath)
    {
        _connection   = connection;
        _databasePath = databasePath;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _connection.DisposeAsync().ConfigureAwait(false);

    /// <summary>
    ///     <para>读取当前 data_version 提示值；与上次读取不同即提示数据库已变化。</para>
    ///     <para>Reads the current data_version hint; a change from the previous read signals database changes.</para>
    /// </summary>
    public async Task<long> ReadAsync(CancellationToken cancellationToken)
    {
        // 监测连接长寿命：每次读取的命令就地释放，避免轮询积累未回收的原生语句。
        await using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA data_version";
        try
        {
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt64(result ?? 0L, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException
                                      or NotSupportedException or PathTooLongException)
        {
            throw new SessionIndexException($"Session index watcher for '{_databasePath}' failed: {ex.Message}", ex);
        }
    }
}

using Microsoft.Data.Sqlite;
using System.Diagnostics;
using TinyHarness.Core.Exceptions;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>会话删除墓碑的状态：删除意图已提交但尚未收尾，或日志与投影已清理完成。</para>
///     <para>
///         The state of a session-deletion tombstone: the deletion intent is committed but not finished yet, or the
///         log and projections are already cleared.
///     </para>
/// </summary>
public enum SessionDeletionState
{
    /// <summary>
    ///     <para>删除意图已提交，日志或投影清理仍待继续；不允许读取或复活该会话。</para>
    ///     <para>
    ///         The deletion intent is committed while log or projection cleanup still needs to continue; the session
    ///         must not be read or resurrected.
    ///     </para>
    /// </summary>
    Pending,

    /// <summary>
    ///     <para>日志与投影均已清理；墓碑继续阻止同名日志复活成列表条目。</para>
    ///     <para>
    ///         The log and projections are cleared; the tombstone keeps a log of the same id from returning as a list
    ///         entry.
    ///     </para>
    /// </summary>
    Deleted
}

/// <summary>
///     <para>一条会话删除墓碑：会话 ID、状态以及请求与更新时间。</para>
///     <para>One session-deletion tombstone: the session id, its state, and the request and update times.</para>
/// </summary>
public sealed record SessionDeletionTombstone(
    string               SessionId,
    SessionDeletionState State,
    DateTimeOffset       RequestedUtc,
    DateTimeOffset       UpdatedUtc);

/// <summary>
///     <para>
///         业务库中的会话删除墓碑：删除先提交墓碑，再在 writer 锁下删除日志并清理投影，最后把墓碑置为已删除；
///         中途中断的删除保持待收尾状态，由恢复入口继续。
///     </para>
///     <para>
///         Session-deletion tombstones inside the business database: a deletion commits its tombstone first, then
///         removes the log under the writer lock and clears projections, and only then marks the tombstone deleted;
///         an interrupted deletion stays pending for the recovery entry to continue.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         墓碑与日志文件不在同一事务内，因此协议不宣称跨文件原子：先提交的墓碑是唯一事实来源，任何读取方都
///         必须把墓碑中的会话视为已删除，即使日志仍然存在。墓碑行不随删除完成而移除——日志可能因备份恢复、
///         文件拷贝或旧版本程序重新出现，保留墓碑才能阻止它复活。
///     </para>
///     <para>
///         The tombstone and the log file never share a transaction, so the protocol makes no cross-file atomicity
///         claim: the tombstone committed first is the single source of truth, and every reader must treat a
///         tombstoned session as deleted even while its log still exists. Rows are not removed when the deletion
///         finishes — a log may reappear through a backup restore, a file copy, or an older program, and only the
///         kept tombstone can keep it from coming back.
///     </para>
/// </remarks>
public static class SessionStoreTombstoneStore
{
    private const string PendingState = "pending";
    private const string DeletedState = "deleted";

    /// <summary>
    ///     <para>判断一个会话是否已进入删除（待收尾或已完成）。</para>
    ///     <para>Whether a session already carries a deletion tombstone, pending or finished.</para>
    /// </summary>
    public static async Task<bool> IsTombstonedAsync(
        string storeDirectory, string sessionId, CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
            cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT 1 FROM {SessionStoreDatabase.TombstoneTableName} WHERE session_id = $session_id;";
        command.Parameters.AddWithValue("$session_id", sessionId);
        return await ExecuteAsync(() => command.ExecuteScalarAsync(cancellationToken))
           .ConfigureAwait(false) is not null;
    }

    /// <summary>
    ///     <para>
    ///         读取全部墓碑会话 ID（待收尾与已完成）；列表、对账与初始化用一次查询排除已删除会话。
    ///     </para>
    ///     <para>
    ///         Reads every tombstoned session id, pending or finished, so listing, reconciliation, and initialization
    ///         exclude deleted sessions with one query.
    ///     </para>
    /// </summary>
    public static async Task<IReadOnlySet<string>> LoadTombstonedSessionIdsAsync(
        string storeDirectory, CancellationToken cancellationToken)
    {
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
            cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT session_id FROM {SessionStoreDatabase.TombstoneTableName};";
        return await ExecuteAsync(ReadIdsAsync).ConfigureAwait(false);

        async Task<IReadOnlySet<string>> ReadIdsAsync()
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var             ids    = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(reader.GetString(0));
            return ids;
        }
    }

    /// <summary>
    ///     <para>读取仍需收尾的删除，按请求时间升序返回；恢复入口据此续作。</para>
    ///     <para>Reads the deletions still needing cleanup in request-time order; the recovery entry continues them.</para>
    /// </summary>
    public static async Task<IReadOnlyList<SessionDeletionTombstone>> ListPendingAsync(
        string storeDirectory, CancellationToken cancellationToken)
    {
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
            cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT session_id, state, requested_utc, updated_utc
            FROM {SessionStoreDatabase.TombstoneTableName}
            WHERE state = '{PendingState}'
            ORDER BY requested_utc, session_id;
            """;
        return await ExecuteAsync(ReadPendingAsync).ConfigureAwait(false);

        async Task<IReadOnlyList<SessionDeletionTombstone>> ReadPendingAsync()
        {
            await using var reader     = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var             tombstones = new List<SessionDeletionTombstone>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var requestedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
                var updatedUtc   = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3));
                tombstones.Add(new SessionDeletionTombstone(reader.GetString(0), ParseState(reader.GetString(1)),
                                                            requestedUtc, updatedUtc));
            }

            return tombstones;
        }
    }

    /// <summary>
    ///     <para>
    ///         提交一个会话的删除意图；返回 false 表示该会话此前已标记为已删除，调用方应把删除视为幂等完成。
    ///     </para>
    ///     <para>
    ///         Commits one session's deletion intent; false means the session was already marked deleted, so the
    ///         caller treats the deletion as idempotently complete.
    ///     </para>
    /// </summary>
    public static async Task<bool> MarkPendingAsync(
        string storeDirectory, string sessionId, CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
            cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var             now         = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var             existing    = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText =
            $"SELECT state FROM {SessionStoreDatabase.TombstoneTableName} WHERE session_id = $session_id;";
        existing.Parameters.AddWithValue("$session_id", sessionId);
        var state = await ExecuteAsync(() => existing.ExecuteScalarAsync(cancellationToken))
           .ConfigureAwait(false) as string;
        if (string.Equals(state, DeletedState, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = $"""
            INSERT INTO {SessionStoreDatabase.TombstoneTableName} (session_id, state, requested_utc, updated_utc)
            VALUES ($session_id, '{PendingState}', $now, $now)
            ON CONFLICT(session_id) DO UPDATE SET
                state       = '{PendingState}',
                updated_utc = excluded.updated_utc;
            """;
        upsert.Parameters.AddWithValue("$session_id", sessionId);
        upsert.Parameters.AddWithValue("$now", now);
        await ExecuteAsync(() => upsert.ExecuteNonQueryAsync(cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    ///     <para>把会话墓碑置为已删除；日志与投影清理完成后调用，重复调用幂等。</para>
    ///     <para>Marks a session tombstone deleted after the log and projections are cleared; repeating it is idempotent.</para>
    /// </summary>
    public static async Task MarkDeletedAsync(
        string storeDirectory, string sessionId, CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
            cancellationToken).ConfigureAwait(false);
        var now    = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var upsert = connection.CreateCommand();
        upsert.CommandText = $"""
            INSERT INTO {SessionStoreDatabase.TombstoneTableName} (session_id, state, requested_utc, updated_utc)
            VALUES ($session_id, '{DeletedState}', $now, $now)
            ON CONFLICT(session_id) DO UPDATE SET
                state       = '{DeletedState}',
                updated_utc = excluded.updated_utc;
            """;
        upsert.Parameters.AddWithValue("$session_id", sessionId);
        upsert.Parameters.AddWithValue("$now", now);
        await ExecuteAsync(() => upsert.ExecuteNonQueryAsync(cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>
    ///         供生产读取路径排除已删除会话：未迁移时返回 null（旧文件权威，墓碑不适用）；业务库暂时不可读时
    ///         返回 null 并记录诊断，读取按原有降级行为继续，不因墓碑查询失败把整个列表拒绝。
    ///     </para>
    ///     <para>
    ///         Excludes deleted sessions on production read paths: null when the store is unmigrated (legacy files are
    ///         authoritative and tombstones do not apply), and also null with a diagnostic when the business database is
    ///         temporarily unreadable, so reads keep their existing degraded behavior instead of failing the whole list
    ///         because the tombstone query failed.
    ///     </para>
    /// </summary>
    internal static async Task<IReadOnlySet<string>?> TryLoadForReadsAsync(
        string storeDirectory, CancellationToken cancellationToken)
    {
        if (!await SessionStoreAuthority.IsMigratedAsync(storeDirectory, cancellationToken).ConfigureAwait(false))
            return null;
        try
        {
            return await LoadTombstonedSessionIdsAsync(storeDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SessionStoreMigrationException or SessionStoreException
                                           or IOException or UnauthorizedAccessException or NotSupportedException
                                           or PathTooLongException)
        {
            // 墓碑只用于过滤：业务库此刻不可读时按原有降级路径继续，由上层暴露库不可用本身。
            Trace.TraceWarning("Session tombstones for '{0}' could not be read: {1}", storeDirectory,
                               exception.Message);
            return null;
        }
    }

    /// <summary>
    ///     <para>把墓碑状态字符串解析为枚举；未知值属于数据库损坏，按会话存储异常报告。</para>
    ///     <para>
    ///         Parses a tombstone state string; an unknown value is database damage and reports as a session-store
    ///         exception.
    ///     </para>
    /// </summary>
    private static SessionDeletionState ParseState(string state) => state switch
    {
        PendingState => SessionDeletionState.Pending,
        DeletedState => SessionDeletionState.Deleted,
        _            => throw new SessionStoreException($"Session tombstone carries the unknown state '{state}'.")
    };

    /// <summary>
    ///     <para>执行一次墓碑读写，把 SQLite 失败包装为带上下文的会话存储异常；取消原样传播。</para>
    ///     <para>
    ///         Runs one tombstone read or write, wrapping SQLite failures into a contextual session-store exception;
    ///         cancellation propagates unchanged.
    ///     </para>
    /// </summary>
    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException
                                                    or NotSupportedException or PathTooLongException)
        {
            throw new SessionStoreException($"Session tombstone operation failed: {exception.Message}", exception);
        }
    }

    /// <summary>
    ///     <para>校验会话 ID 为 32 位小写十六进制；墓碑键与其他业务表保持同一口径。</para>
    ///     <para>Validates the session id as 32 lowercase hexadecimal characters, matching every other business table key.</para>
    /// </summary>
    private static void ValidateSessionId(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!SessionStorePaths.IsValidSessionId(sessionId) || sessionId.Any(char.IsUpper))
            throw new
                ArgumentException($"Session id '{sessionId}' must contain exactly 32 lowercase hexadecimal characters.",
                                  nameof(sessionId));
    }
}

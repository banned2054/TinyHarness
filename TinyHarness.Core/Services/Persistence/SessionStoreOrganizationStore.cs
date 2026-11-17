using Microsoft.Data.Sqlite;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Persistence;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>
///         业务库中的每 store 组织状态：归档会话、置顶项目与会话、普通项目与会话顺序、折叠分组。
///         仅在迁移完成后作为生产权威；旧文件在迁移前由既有文件存储承担。
///     </para>
///     <para>
///         The per-store organization state inside the business database: archived sessions, pinned projects and
///         sessions, regular project and session order, and collapsed groups. It is the production authority only
///         after the migration completes; legacy files serve before that.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         每次变更在单个事务内重读当前快照再写回，顺序按位置压缩为从零开始的连续整数；重复置顶保持原顺序，
///         新置顶进入顶部，取消置顶移除。所有写入口先要求迁移完成，不把写失败伪装成空状态。
///     </para>
///     <para>
///         Every mutation reads the current snapshot and writes back inside one transaction, compacting positions to
///         contiguous integers from zero. Re-pinning keeps the current order, a new pin enters at the top, and
///         unpinning removes it. Every write first requires a completed migration and never disguises a write
///         failure as an empty state.
///     </para>
/// </remarks>
public static class SessionStoreOrganizationStore
{
    /// <summary>
    ///     <para>读取组织状态快照；各列表按持久化位置升序，集合类字段按序数排序。</para>
    ///     <para>Reads the organization snapshot; ordered lists follow stored positions and set-like fields are ordinal-sorted.</para>
    /// </summary>
    public static async Task<SessionStoreOrganizationState> LoadAsync(
        string storeDirectory, CancellationToken cancellationToken)
    {
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
                                                                  cancellationToken).ConfigureAwait(false);
        return await ReadStateAsync(connection, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>保存置顶项目的完整顺序；用于按整体顺序写回拖放结果。</para>
    ///     <para>Saves the complete pinned-project order; used to write a drag result back as a whole order.</para>
    /// </summary>
    public static Task SetProjectPinOrderAsync(
        string storeDirectory, IReadOnlyList<string> projectIds, CancellationToken cancellationToken) =>
        MutateAsync(storeDirectory, (connection, transaction, state) =>
        {
            ArgumentNullException.ThrowIfNull(projectIds);
            var order = NormalizeOrder(projectIds, []);
            return order.SequenceEqual(state.PinnedProjectIds, StringComparer.Ordinal)
                ? null
                : state with { PinnedProjectIds = order };
        }, cancellationToken);

    /// <summary>
    ///     <para>保存置顶会话的完整顺序；用于按整体顺序写回拖放结果。</para>
    ///     <para>Saves the complete pinned-session order; used to write a drag result back as a whole order.</para>
    /// </summary>
    public static Task SetSessionPinOrderAsync(
        string storeDirectory, IReadOnlyList<string> sessionIds, CancellationToken cancellationToken) =>
        MutateAsync(storeDirectory, (connection, transaction, state) =>
        {
            ArgumentNullException.ThrowIfNull(sessionIds);
            var order = NormalizeOrder(sessionIds, []);
            return order.SequenceEqual(state.PinnedSessionIds, StringComparer.Ordinal)
                ? null
                : state with { PinnedSessionIds = order };
        }, cancellationToken);

    /// <summary>
    ///     <para>设置会话归档状态；重复设置幂等，不改变其他会话。</para>
    ///     <para>Sets a session's archive state idempotently without touching other sessions.</para>
    /// </summary>
    public static Task SetSessionArchivedAsync(
        string storeDirectory, string sessionId, bool isArchived, CancellationToken cancellationToken) =>
        MutateAsync(storeDirectory, (connection, transaction, state) =>
        {
            var ids = state.ArchivedSessionIds.ToList();
            var contains = ids.Contains(sessionId, StringComparer.Ordinal);
            if (contains == isArchived) return null;
            if (isArchived) ids.Add(sessionId);
            else ids.Remove(sessionId);
            return state with { ArchivedSessionIds = ids.Order(StringComparer.Ordinal).ToArray() };
        }, cancellationToken);

    /// <summary>
    ///     <para>设置会话置顶状态；新置顶进入顶部，取消置顶移除，重复设置保持现有顺序。</para>
    ///     <para>Sets a session's pin state; a new pin enters at the top, unpinning removes it, and repeating the same value keeps its order.</para>
    /// </summary>
    public static Task SetSessionPinnedAsync(
        string storeDirectory, string sessionId, bool isPinned, CancellationToken cancellationToken) =>
        MutateAsync(storeDirectory, (connection, transaction, state) =>
        {
            var ids = state.PinnedSessionIds.ToList();
            var contains = ids.Contains(sessionId, StringComparer.Ordinal);
            if (contains == isPinned) return null;
            if (isPinned) ids.Insert(0, sessionId);
            else ids.Remove(sessionId);
            return state with { PinnedSessionIds = ids };
        }, cancellationToken);

    /// <summary>
    ///     <para>设置项目置顶状态；新置顶进入顶部，取消置顶移除，重复设置保持现有顺序。</para>
    ///     <para>Sets a project's pin state; a new pin enters at the top, unpinning removes it, and repeating the same value keeps its order.</para>
    /// </summary>
    public static Task SetProjectPinnedAsync(
        string storeDirectory, string projectId, bool isPinned, CancellationToken cancellationToken) =>
        MutateAsync(storeDirectory, (connection, transaction, state) =>
        {
            var ids = state.PinnedProjectIds.ToList();
            var contains = ids.Contains(projectId, StringComparer.Ordinal);
            if (contains == isPinned) return null;
            if (isPinned) ids.Insert(0, projectId);
            else ids.Remove(projectId);
            return state with { PinnedProjectIds = ids };
        }, cancellationToken);

    /// <summary>
    ///     <para>按拖放位置调整置顶项目顺序；任一项目未置顶时不写入。</para>
    ///     <para>Reorders pinned projects by a drop position; nothing is written unless both projects are pinned.</para>
    /// </summary>
    public static Task MovePinnedProjectAsync(
        string storeDirectory, string sourceProjectId, string targetProjectId, bool insertAfter,
        CancellationToken cancellationToken) =>
        MutateAsync(storeDirectory, (connection, transaction, state) =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetProjectId);
            var ids         = state.PinnedProjectIds.ToList();
            var sourceIndex = ids.IndexOf(sourceProjectId);
            var targetIndex = ids.IndexOf(targetProjectId);
            if (sourceIndex < 0 || targetIndex < 0) return null;

            ids.RemoveAt(sourceIndex);
            var insertIndex = targetIndex + (insertAfter ? 1 : 0);
            if (sourceIndex < insertIndex) insertIndex--;
            ids.Insert(Math.Clamp(insertIndex, 0, ids.Count), sourceProjectId);
            return state with { PinnedProjectIds = ids };
        }, cancellationToken);

    /// <summary>
    ///     <para>保存普通分类中的项目手动顺序；按传入顺序重排位置，未列出的项目排在已列出项目之后。</para>
    ///     <para>Saves the manual project order in the regular category; positions follow the given order and unlisted projects follow it.</para>
    /// </summary>
    public static Task SetManualProjectOrderAsync(
        string storeDirectory, IReadOnlyList<string> projectIds, CancellationToken cancellationToken) =>
        MutateAsync(storeDirectory, (connection, transaction, state) =>
        {
            ArgumentNullException.ThrowIfNull(projectIds);
            var order = NormalizeOrder(projectIds, state.ManualProjectOrder);
            return state with { ManualProjectOrder = order };
        }, cancellationToken);

    /// <summary>
    ///     <para>保存手工会话顺序；运行中会话的排列仍由界面在展示时决定。</para>
    ///     <para>Saves the manual session order; the display order of running sessions stays a UI concern.</para>
    /// </summary>
    public static Task SetManualSessionOrderAsync(
        string storeDirectory, IReadOnlyList<string> sessionIds, CancellationToken cancellationToken) =>
        MutateAsync(storeDirectory, (connection, transaction, state) =>
        {
            ArgumentNullException.ThrowIfNull(sessionIds);
            var order = NormalizeOrder(sessionIds, state.ManualSessionOrder);
            return state with { ManualSessionOrder = order };
        }, cancellationToken);

    /// <summary>
    ///     <para>设置一个分组键的折叠状态；未知键在折叠时新增、展开时移除。</para>
    ///     <para>Sets the collapsed state of one group key: an unknown key is added when collapsing and removed when expanding.</para>
    /// </summary>
    public static Task SetGroupCollapsedAsync(
        string storeDirectory, string groupKey, bool isCollapsed, CancellationToken cancellationToken) =>
        MutateAsync(storeDirectory, (connection, transaction, state) =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(groupKey);
            var keys = state.CollapsedGroupKeys.ToHashSet(StringComparer.Ordinal);
            var changed = isCollapsed ? keys.Add(groupKey) : keys.Remove(groupKey);
            return changed
                ? state with { CollapsedGroupKeys = keys.Order(StringComparer.Ordinal).ToArray() }
                : null;
        }, cancellationToken);

    /// <summary>
    ///     <para>清除已永久删除会话在归档、置顶与手动顺序中的引用；返回是否有变化。</para>
    ///     <para>Clears a permanently deleted session from archive, pins, and manual order; returns whether anything changed.</para>
    /// </summary>
    public static async Task<bool> RemoveSessionAsync(
        string storeDirectory, string sessionId, CancellationToken cancellationToken)
    {
        var changed = false;
        await MutateAsync(storeDirectory, (connection, transaction, state) =>
        {
            var archived = state.ArchivedSessionIds.Where(id => id != sessionId).ToArray();
            var pinned   = state.PinnedSessionIds.Where(id => id != sessionId).ToArray();
            var order    = state.ManualSessionOrder.Where(id => id != sessionId).ToArray();
            if (archived.Length == state.ArchivedSessionIds.Count &&
                pinned.Length == state.PinnedSessionIds.Count       &&
                order.Length == state.ManualSessionOrder.Count)
                return null;
            changed = true;
            return state with
            {
                ArchivedSessionIds = archived,
                PinnedSessionIds   = pinned,
                ManualSessionOrder = order
            };
        }, cancellationToken).ConfigureAwait(false);
        return changed;
    }

    /// <summary>
    ///     <para>
    ///         清除已删除项目的置顶与手动顺序引用；不处理折叠分组（其键格式属于界面层，由宿主钩子在迁移后清理）。
    ///     </para>
    ///     <para>
    ///         Clears a deleted project's pin and manual-order references without touching collapsed groups, whose key
    ///         format belongs to the UI layer and is cleaned by the host hook after migration.
    ///     </para>
    /// </summary>
    public static Task RemoveProjectReferencesAsync(
        string storeDirectory, string projectId, CancellationToken cancellationToken) =>
        MutateAsync(storeDirectory, (connection, transaction, state) =>
        {
            var pinned = state.PinnedProjectIds.Where(id => id != projectId).ToArray();
            var order  = state.ManualProjectOrder.Where(id => id != projectId).ToArray();
            if (pinned.Length == state.PinnedProjectIds.Count && order.Length == state.ManualProjectOrder.Count)
                return null;
            return state with
            {
                PinnedProjectIds   = pinned,
                ManualProjectOrder = order
            };
        }, cancellationToken);

    /// <summary>
    ///     <para>清除已删除项目在置顶、手动顺序与折叠分组中的引用；调用方显式给出该项目的分组键。</para>
    ///     <para>Clears a deleted project from pins, manual order, and collapsed groups; the caller supplies the project's group keys.</para>
    /// </summary>
    public static Task RemoveProjectAsync(
        string storeDirectory, string projectId, IReadOnlyList<string> groupKeys, CancellationToken cancellationToken) =>
        MutateAsync(storeDirectory, (connection, transaction, state) =>
        {
            var pinned = state.PinnedProjectIds.Where(id => id != projectId).ToArray();
            var order  = state.ManualProjectOrder.Where(id => id != projectId).ToArray();
            var keys   = state.CollapsedGroupKeys.Where(key => !groupKeys.Contains(key, StringComparer.Ordinal))
                                        .ToArray();
            if (pinned.Length == state.PinnedProjectIds.Count &&
                order.Length == state.ManualProjectOrder.Count   &&
                keys.Length == state.CollapsedGroupKeys.Count)
                return null;
            return state with
            {
                PinnedProjectIds   = pinned,
                ManualProjectOrder = order,
                CollapsedGroupKeys = keys
            };
        }, cancellationToken);

    /// <summary>
    ///     <para>在给定事务内用导入快照整体替换该 store 的组织状态；只由迁移引擎调用。</para>
    ///     <para>Replaces the store's organization state with the imported snapshot inside the given transaction; used only by the migration engine.</para>
    /// </summary>
    internal static async Task ReplaceAllAsync(
        SqliteConnection connection, SqliteTransaction transaction, SessionStoreOrganizationState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            DELETE FROM archived_session;
            DELETE FROM pinned_session;
            DELETE FROM pinned_project;
            DELETE FROM project_order;
            DELETE FROM session_order;
            DELETE FROM collapsed_group;
            """;
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        foreach (var id in state.ArchivedSessionIds)
            await InsertAsync(connection, transaction, "archived_session", "session_id", id,
                              cancellationToken).ConfigureAwait(false);
        for (var position = 0; position < state.PinnedSessionIds.Count; position++)
            await InsertPositionAsync(connection, transaction, "pinned_session", "session_id",
                                      state.PinnedSessionIds[position], position, cancellationToken)
               .ConfigureAwait(false);
        for (var position = 0; position < state.PinnedProjectIds.Count; position++)
            await InsertPositionAsync(connection, transaction, "pinned_project", "project_id",
                                      state.PinnedProjectIds[position], position, cancellationToken)
               .ConfigureAwait(false);
        for (var position = 0; position < state.ManualProjectOrder.Count; position++)
            await InsertPositionAsync(connection, transaction, "project_order", "project_id",
                                      state.ManualProjectOrder[position], position, cancellationToken)
               .ConfigureAwait(false);
        for (var position = 0; position < state.ManualSessionOrder.Count; position++)
            await InsertPositionAsync(connection, transaction, "session_order", "session_id",
                                      state.ManualSessionOrder[position], position, cancellationToken)
               .ConfigureAwait(false);
        foreach (var key in state.CollapsedGroupKeys)
            await InsertAsync(connection, transaction, "collapsed_group", "group_key", key, cancellationToken)
               .ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>在串行事务内读取当前状态、应用变更并仅在有变化时写回；无变化不产生写入。</para>
    ///     <para>Reads the current state, applies a mutation, and writes back only when it changed; no change produces no write.</para>
    /// </summary>
    private static async Task MutateAsync(
        string storeDirectory,
        Func<SqliteConnection, SqliteTransaction, SessionStoreOrganizationState, SessionStoreOrganizationState?> mutate,
        CancellationToken cancellationToken)
    {
        await using var connection = await SessionStoreBusinessConnection.OpenVerifiedAsync(storeDirectory,
                                                                  cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
                                                                    .BeginTransactionAsync(cancellationToken)
                                                                    .ConfigureAwait(false);
        var current   = await ReadStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var candidate = mutate(connection, transaction, current);
        if (candidate is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await ReplaceAllAsync(connection, transaction, candidate, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>在给定连接与事务内读取组织状态。</para>
    ///     <para>Reads the organization state on the given connection and transaction.</para>
    /// </summary>
    private static async Task<SessionStoreOrganizationState> ReadStateAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        var archived = await ReadIdsAsync(connection, transaction, "SELECT session_id FROM archived_session",
                                          cancellationToken).ConfigureAwait(false);
        var pinnedSessions = await ReadOrderedIdsAsync(connection, transaction, "pinned_session", "session_id",
                                                       cancellationToken).ConfigureAwait(false);
        var pinnedProjects = await ReadOrderedIdsAsync(connection, transaction, "pinned_project", "project_id",
                                                       cancellationToken).ConfigureAwait(false);
        var projectOrder = await ReadOrderedIdsAsync(connection, transaction, "project_order", "project_id",
                                                     cancellationToken).ConfigureAwait(false);
        var sessionOrder = await ReadOrderedIdsAsync(connection, transaction, "session_order", "session_id",
                                                     cancellationToken).ConfigureAwait(false);
        var groups = await ReadIdsAsync(connection, transaction, "SELECT group_key FROM collapsed_group",
                                        cancellationToken).ConfigureAwait(false);
        return new SessionStoreOrganizationState(archived, pinnedProjects, pinnedSessions, projectOrder, sessionOrder,
                                                  groups);
    }

    private static async Task<IReadOnlyList<string>> ReadIdsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql + " ORDER BY 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ids = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(reader.GetString(0));
        return ids;
    }

    private static async Task<IReadOnlyList<string>> ReadOrderedIdsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string table, string idColumn,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {idColumn} FROM {table} ORDER BY position, {idColumn};";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ids = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>
    ///     <para>把调用方顺序规范化为去重后的顺序，并把未列出的既有条目追加在后面。</para>
    ///     <para>Normalizes a caller order by removing duplicates and appending previously listed entries that are missing.</para>
    /// </summary>
    private static IReadOnlyList<string> NormalizeOrder(
        IReadOnlyList<string> requested, IReadOnlyList<string> existing)
    {
        var seen  = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>(requested.Count);
        foreach (var id in requested)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            if (seen.Add(id)) order.Add(id);
        }

        foreach (var id in existing)
            if (seen.Add(id))
                order.Add(id);
        return order;
    }

    private static async Task InsertAsync(
        SqliteConnection connection, SqliteTransaction transaction, string table, string column, string value,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {table} ({column}) VALUES ($value);";
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertPositionAsync(
        SqliteConnection connection, SqliteTransaction transaction, string table, string column, string value,
        int position, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {table} ({column}, position) VALUES ($value, $position);";
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$position", position);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

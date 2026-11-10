using System.Diagnostics;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Project;
using TinyHarness.Core.Services.ChatCompletions.Configuration;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>项目删除编排的宿主接缝：由界面层解绑草稿页与运行会话，并应用各阶段的存储事实。</para>
///     <para>
///         The host seam of project-deletion orchestration: the UI layer unbinds draft pages and running sessions
///         and applies stored facts at each stage.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         Core 不引用界面层；每个钩子在编排的固定位置调用一次，钩子抛出的异常中止后续编排并原样传播。
///         取消判定属于钩子实现；解绑完整性由 Core 强制把关：不完整结果以类型化异常中止编排。
///     </para>
///     <para>
///         Core never references the UI layer; each hook is invoked once at a fixed orchestration position, and a
///         hook's exception aborts the rest with its original propagation. Cancellation decisions belong to the
///         implementation, while Core enforces unassignment completeness: an incomplete result aborts the
///         orchestration with a typed exception.
///     </para>
/// </remarks>
public interface ISessionProjectDeletionGateway
{
    /// <summary>
    ///     <para>删除标记持久化后回调；实现把删除中的项目应用到分组与目录册快照。</para>
    ///     <para>
    ///         Invoked after the deletion marker is persisted; the implementation applies the deleting project to
    ///         groups and its catalog snapshot.
    ///     </para>
    /// </summary>
    Task DeletionMarkedAsync(Project deleting, CancellationToken cancellationToken);

    /// <summary>
    ///     <para>解绑仍指向该项目的空白草稿页；不触碰会话日志。</para>
    ///     <para>Unbinds blank draft pages still targeting the project; session logs are never touched.</para>
    /// </summary>
    Task UnbindDraftPagesAsync(string storeDirectory, string projectId, CancellationToken cancellationToken);

    /// <summary>
    ///     <para>经已打开的 runtime 解绑运行中会话；无打开 runtime 的会话留给解绑流程处理。</para>
    ///     <para>
    ///         Unassigns running sessions through their open runtimes; sessions without one stay with the
    ///         unassignment pass.
    ///     </para>
    /// </summary>
    Task UnbindOpenRuntimeSessionsAsync(string storeDirectory, string projectId, CancellationToken cancellationToken);

    /// <summary>
    ///     <para>解绑批次完成后回调；实现应用行投影事实并按宿主取消状态判定取消，不做完整性判定。</para>
    ///     <para>
    ///         Invoked after the unassignment pass; the implementation applies row projections and decides
    ///         cancellation from the host's state, without a completeness decision.
    ///     </para>
    /// </summary>
    Task UnassignmentAppliedAsync(SessionProjectUnassignmentResult result, CancellationToken cancellationToken);

    /// <summary>
    ///     <para>最终移除项目前回调；实现清理桌面侧栏偏好中的项目残留。</para>
    ///     <para>
    ///         Invoked before the project's final removal; the implementation clears the project's legacy sidebar
    ///         preferences.
    ///     </para>
    /// </summary>
    Task PrepareCompletionAsync(string storeDirectory, string projectId, CancellationToken cancellationToken);

    /// <summary>
    ///     <para>目录册移除完成后回调；实现应用最终移除并收尾界面状态。</para>
    ///     <para>
    ///         Invoked after the catalog removal; the implementation applies the final removal and finishes UI state.
    ///     </para>
    /// </summary>
    Task CompletedAsync(Project deleted, CancellationToken cancellationToken);
}

/// <summary>
///     <para>会话删除后需要尽力清理的投影区域。</para>
///     <para>The projection areas that need best-effort cleanup after a session deletion.</para>
/// </summary>
public enum SessionStoreCleanupArea
{
    /// <summary>
    ///     <para>归档状态文件。</para>
    ///     <para>The archive-state file.</para>
    /// </summary>
    ArchiveState,

    /// <summary>
    ///     <para>置顶状态文件。</para>
    ///     <para>The pin-state file.</para>
    /// </summary>
    PinState,

    /// <summary>
    ///     <para>SQLite 列表索引。</para>
    ///     <para>The SQLite list index.</para>
    /// </summary>
    SessionIndex,

    /// <summary>
    ///     <para>桌面侧栏偏好中的旧顺序与旧置顶引用。</para>
    ///     <para>Legacy order and pin references in desktop preferences.</para>
    /// </summary>
    LegacySidebarOrder
}

/// <summary>
///     <para>一处未成功的投影清理及其不含凭据的原因；不阻断删除结果。</para>
///     <para>One failed projection cleanup with its credential-free reason; it never blocks the deletion outcome.</para>
/// </summary>
public sealed record SessionStoreCleanupIssue(SessionStoreCleanupArea Area, string Detail);

/// <summary>
///     <para>按会话存储目录提供置顶、归档与删除操作的统一 Core 入口，并保存每 store 的快照与可用性。</para>
///     <para>
///         The unified Core entry for pin, archive, and deletion operations per session store, holding each
///         store's snapshots and availability.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         置顶与归档共享一把串行门：写入与准备互斥，查询按最新快照返回；快照写入点整体失效 ID 集合缓存。
///         操作失败抛出类型化异常且不改快照（置顶准备失败移除旧快照），可用性状态由本类型维护。
///     </para>
///     <para>
///         Pins and archives share one serial gate: writes and preparation mutually exclude, queries return the
///         latest snapshot, and snapshot writes invalidate the id-set caches wholesale. A failed operation throws a
///         typed exception without changing snapshots (pin preparation removes the stale one), and availability
///         state is maintained here.
///     </para>
/// </remarks>
public sealed class SessionStoreOperations
{
    private static readonly StringComparer StoreKeyComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly HashSet<string> _archiveStateUnavailableStores = new(StoreKeyComparer);

    private readonly Dictionary<string, SessionStoreArchives> _archiveStatesByStore = new(StoreKeyComparer);

    // 置顶/归档 ID 的按 store 懒构建查询缓存：快照写入点整体 Clear，下一次查询按当前快照重建。
    private readonly Dictionary<string, HashSet<string>> _archivedSessionIdsByStore = new(StoreKeyComparer);

    // 置顶与归档共用既有互斥语义：同一 store 的两类元数据写入必须串行。

    private readonly HashSet<string> _pinStateUnavailableStores = new(StoreKeyComparer);

    private readonly Dictionary<string, SessionStorePins> _pinStatesByStore = new(StoreKeyComparer);

    private readonly Dictionary<string, HashSet<string>> _pinnedSessionIdsByStore = new(StoreKeyComparer);

    /// <summary>
    ///     <para>置顶与归档共用的生产串行门：全部变更路径在门内互斥执行，测试经此验证互斥与让位顺序。</para>
    ///     <para>
    ///         The production serial gate shared by pins and archives: every mutation path runs mutually excluded
    ///         inside it, and tests use it to verify exclusion and yield ordering.
    ///     </para>
    /// </summary>
    internal SemaphoreSlim MutationGate { get; } = new(1, 1);

    /// <summary>
    ///     <para>返回会话存储目录的规范化比较键：去除结尾分隔符的完整路径，Windows 不区分大小写。</para>
    ///     <para>
    ///         Returns the normalized comparison key for a store directory: the full path without a trailing
    ///         separator, case-insensitive on Windows.
    ///     </para>
    /// </summary>
    public static string StoreKey(string storeDirectory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(storeDirectory));

    /// <summary>
    ///     <para>返回 store 的置顶快照；未知或未提供 store 时返回空状态。</para>
    ///     <para>Returns a store's pin snapshot, or an empty state when unknown or unset.</para>
    /// </summary>
    public SessionStorePins PinsForStore(string? storeDirectory)
    {
        if (storeDirectory is null) return SessionStorePins.Empty();
        return _pinStatesByStore.GetValueOrDefault(StoreKey(storeDirectory)) ?? SessionStorePins.Empty();
    }

    /// <summary>
    ///     <para>返回 store 的归档快照；未知或未提供 store 时返回空状态。</para>
    ///     <para>Returns a store's archive snapshot, or an empty state when unknown or unset.</para>
    /// </summary>
    public SessionStoreArchives ArchivesForStore(string? storeDirectory)
    {
        if (storeDirectory is null) return SessionStoreArchives.Empty();
        return _archiveStatesByStore.GetValueOrDefault(StoreKey(storeDirectory)) ?? SessionStoreArchives.Empty();
    }

    /// <summary>
    ///     <para>按 store 懒构建置顶会话 ID 集合；快照写入点整体失效后由下一次查询重建。</para>
    ///     <para>
    ///         Lazily builds a store's pinned-session id set; snapshot writes invalidate it wholesale and the next
    ///         query rebuilds it.
    ///     </para>
    /// </summary>
    public IReadOnlySet<string> PinnedSessionIdsForStore(string storeDirectory)
    {
        var storeKey = StoreKey(storeDirectory);
        if (_pinnedSessionIdsByStore.TryGetValue(storeKey, out var pinnedIds)) return pinnedIds;
        var ids = PinsForStore(storeDirectory).PinnedSessionIds.ToHashSet(StringComparer.Ordinal);
        _pinnedSessionIdsByStore[storeKey] = ids;
        return ids;
    }

    /// <summary>
    ///     <para>按 store 懒构建已归档会话 ID 集合；快照写入点整体失效后由下一次查询重建。</para>
    ///     <para>
    ///         Lazily builds a store's archived-session id set; snapshot writes invalidate it wholesale and the next
    ///         query rebuilds it.
    ///     </para>
    /// </summary>
    public IReadOnlySet<string> ArchivedSessionIdsForStore(string storeDirectory)
    {
        var storeKey = StoreKey(storeDirectory);
        if (_archivedSessionIdsByStore.TryGetValue(storeKey, out var archivedIds)) return archivedIds;
        var ids = ArchivesForStore(storeDirectory).ArchivedSessionIds.ToHashSet(StringComparer.Ordinal);
        _archivedSessionIdsByStore[storeKey] = ids;
        return ids;
    }

    /// <summary>
    ///     <para>store 的置顶状态是否因读取或迁移失败而不可用。</para>
    ///     <para>Whether a store's pin state is unavailable after a failed read or migration.</para>
    /// </summary>
    public bool IsPinStateUnavailable(string storeDirectory) =>
        _pinStateUnavailableStores.Contains(StoreKey(storeDirectory));

    /// <summary>
    ///     <para>store 的归档状态是否因读取失败而不可用。</para>
    ///     <para>Whether a store's archive state is unavailable after a failed read.</para>
    /// </summary>
    public bool IsArchiveStateUnavailable(string storeDirectory) =>
        _archiveStateUnavailableStores.Contains(StoreKey(storeDirectory));

    /// <summary>
    ///     <para>读取受保护会话存储目录：加载用户配置、解析受保护路径并拒绝工作区与沙箱可写根内的目录。</para>
    ///     <para>
    ///         Resolves the protected session-store directory: loads user config, resolves the protected path, and
    ///         rejects directories inside the workspace or sandbox-writable roots.
    ///     </para>
    /// </summary>
    /// <param name="workspaceRoot">
    ///     <para>会话存储必须与之隔离的工作区根目录。</para>
    ///     <para>The workspace root the session store must stay isolated from.</para>
    /// </param>
    /// <param name="userConfigPath">
    ///     <para>用户配置文件路径。</para>
    ///     <para>The user configuration file path.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消配置读取。</para>
    ///     <para>Cancels the configuration read.</para>
    /// </param>
    /// <exception cref="SessionStoreException">
    ///     <para>目录不是绝对路径、落入工作区或沙箱可写根。</para>
    ///     <para>The directory is relative or lies inside the workspace or a sandbox-writable root.</para>
    /// </exception>
    public static async Task<string> ResolveProtectedStoreDirectoryAsync(
        string workspaceRoot, string userConfigPath, CancellationToken cancellationToken)
    {
        var user      = await UserConfigStore.LoadAsync(userConfigPath, cancellationToken).ConfigureAwait(false);
        var directory = SessionStorePaths.ResolveProtected(user.Settings?.SessionStoreDirectory, workspaceRoot);
        if (user.Settings?.WindowsSandbox is { Enabled: true } sandbox)
            foreach (var writable in sandbox.AdditionalWriteRoots)
                SessionStorePaths.EnsureOutsideWorkspace(directory, writable, "settings.sessionStoreDirectory");
        return directory;
    }

    /// <summary>
    ///     <para>读取 store 的 Core 置顶状态并按传入旧列表执行一次性迁移；失败标记置顶不可用并抛出原异常。</para>
    ///     <para>
    ///         Loads a store's Core pin state with a one-time legacy migration; failure marks pins unavailable and
    ///         rethrows.
    ///     </para>
    /// </summary>
    /// <param name="storeDirectory">
    ///     <para>发起读取的会话存储目录。</para>
    ///     <para>The session-store directory the read was started for.</para>
    /// </param>
    /// <param name="resolveProtectedDirectory">
    ///     <para>受保护存储目录解析回调；在门内调用，解析结果与传入 store 不一致时拒绝写入。</para>
    ///     <para>
    ///         The protected-store resolution callback; invoked inside the gate, and a result mismatching the
    ///         requested store is refused.
    ///     </para>
    /// </param>
    /// <param name="legacyPinnedProjectIds">
    ///     <para>旧 Desktop 偏好中的有序项目 ID。</para>
    ///     <para>Ordered project IDs from legacy Desktop preferences.</para>
    /// </param>
    /// <param name="legacyPinnedSessionIds">
    ///     <para>旧 Desktop 偏好中的会话 ID。</para>
    ///     <para>Session IDs from legacy Desktop preferences.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消解析、锁等待、文件读取与迁移写入。</para>
    ///     <para>Cancels resolution, gate waits, file reads, and migration writes.</para>
    /// </param>
    /// <returns>
    ///     <para>标记旧置顶列表是否非空；调用方据此清空旧偏好并保存。</para>
    ///     <para>Whether the legacy pin lists were non-empty; callers clear and save legacy preferences on that fact.</para>
    /// </returns>
    /// <exception cref="SessionStoreException">
    ///     <para>受保护目录与传入 store 不一致。</para>
    ///     <para>The protected directory does not match the requested store.</para>
    /// </exception>
    public async Task<SessionPinStatePreparation> PreparePinStateAsync(
        string                                storeDirectory,
        Func<CancellationToken, Task<string>> resolveProtectedDirectory,
        IReadOnlyList<string>                 legacyPinnedProjectIds,
        IReadOnlyList<string>                 legacyPinnedSessionIds,
        CancellationToken                     cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolveProtectedDirectory);
        var storeKey = StoreKey(storeDirectory);
        await MutationGate.WaitAsync(cancellationToken);
        try
        {
            var protectedStoreDirectory = await resolveProtectedDirectory(cancellationToken);
            if (!ProjectRoots.AreEqual(protectedStoreDirectory, storeDirectory))
                throw new SessionStoreException(
                                                "The configured session-store directory changed before its pin state was loaded.");

            storeKey = StoreKey(protectedStoreDirectory);
            var pins = await SessionStorePinStore.LoadOrMigrateAsync(storeKey, legacyPinnedProjectIds,
                                                                     legacyPinnedSessionIds, cancellationToken);
            _pinStatesByStore[storeKey] = pins;
            _pinStateUnavailableStores.Remove(storeKey);
            return new SessionPinStatePreparation(legacyPinnedProjectIds.Count > 0 ||
                                                  legacyPinnedSessionIds.Count > 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SessionStorePinException or SessionStoreException
                                           or IOException or UnauthorizedAccessException)
        {
            _pinStatesByStore.Remove(storeKey);
            _pinStateUnavailableStores.Add(storeKey);
            throw;
        }
        finally
        {
            // 成功写入与失败移除都可能改快照：在 finally 整体失效两个 ID 集合缓存，未变化的轮次重建结果不变。
            _pinnedSessionIdsByStore.Clear();
            _archivedSessionIdsByStore.Clear();
            MutationGate.Release();
        }
    }

    /// <summary>
    ///     <para>读取 store 的归档状态；失败标记归档不可用并抛出原异常。</para>
    ///     <para>Loads a store's archive state; failure marks archives unavailable and rethrows.</para>
    /// </summary>
    /// <param name="storeDirectory">
    ///     <para>发起读取的会话存储目录。</para>
    ///     <para>The session-store directory the read was started for.</para>
    /// </param>
    /// <param name="resolveProtectedDirectory">
    ///     <para>受保护存储目录解析回调；在门内调用，解析结果与传入 store 不一致时拒绝读取。</para>
    ///     <para>
    ///         The protected-store resolution callback; invoked inside the gate, and a result mismatching the
    ///         requested store is refused.
    ///     </para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消解析、锁等待与文件读取。</para>
    ///     <para>Cancels resolution, gate waits, and file reads.</para>
    /// </param>
    /// <returns>
    ///     <para>读取到的归档快照。</para>
    ///     <para>The loaded archive snapshot.</para>
    /// </returns>
    /// <exception cref="SessionStoreException">
    ///     <para>受保护目录与传入 store 不一致。</para>
    ///     <para>The protected directory does not match the requested store.</para>
    /// </exception>
    public async Task<SessionStoreArchives> PrepareArchiveStateAsync(
        string                                storeDirectory,
        Func<CancellationToken, Task<string>> resolveProtectedDirectory,
        CancellationToken                     cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolveProtectedDirectory);
        var storeKey = StoreKey(storeDirectory);
        await MutationGate.WaitAsync(cancellationToken);
        try
        {
            var protectedStoreDirectory = await resolveProtectedDirectory(cancellationToken);
            if (!ProjectRoots.AreEqual(protectedStoreDirectory, storeDirectory))
                throw new SessionStoreException(
                                                "The configured session-store directory changed before its archive state was loaded.");

            storeKey = StoreKey(protectedStoreDirectory);
            var archives = await SessionStoreArchiveStore.LoadAsync(storeKey, cancellationToken);
            _archiveStatesByStore[storeKey] = archives;
            _archiveStateUnavailableStores.Remove(storeKey);
            return archives;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SessionStoreArchiveException or SessionStoreException
                                           or IOException or UnauthorizedAccessException)
        {
            _archiveStateUnavailableStores.Add(storeKey);
            throw;
        }
        finally
        {
            // 成功写入与失败移除都可能改快照：在 finally 整体失效两个 ID 集合缓存，未变化的轮次重建结果不变。
            _pinnedSessionIdsByStore.Clear();
            _archivedSessionIdsByStore.Clear();
            MutationGate.Release();
        }
    }

    /// <summary>
    ///     <para>在串行门内持久化会话置顶状态并更新快照；失败不改快照并抛出原异常。</para>
    ///     <para>
    ///         Persists a session's pin state and updates the snapshot under the serial gate; failure keeps the
    ///         snapshot and rethrows.
    ///     </para>
    /// </summary>
    public async Task SetSessionPinnedAsync(
        string storeDirectory, string sessionId, bool isPinned, CancellationToken cancellationToken)
    {
        await MutationGate.WaitAsync(cancellationToken);
        try
        {
            var pins = await SessionStorePinStore.SetSessionPinnedAsync(storeDirectory, sessionId, isPinned,
                                                                        cancellationToken);
            UpdatePinState(storeDirectory, pins);
        }
        finally
        {
            MutationGate.Release();
        }
    }

    /// <summary>
    ///     <para>在串行门内持久化项目置顶状态并更新快照；失败不改快照并抛出原异常。</para>
    ///     <para>
    ///         Persists a project's pin state and updates the snapshot under the serial gate; failure keeps the
    ///         snapshot and rethrows.
    ///     </para>
    /// </summary>
    /// <param name="shouldYield">
    ///     <para>
    ///         写入前置谓词：取得串行门后、任何写入与快照更新前在门内求值；返回 true 表示存在必须让位的
    ///         更高优先级变更（如项目删除正在进行），此时不写入、不更新快照并返回 false。
    ///     </para>
    ///     <para>
    ///         The write precondition: evaluated inside the gate after acquiring it and before any write or
    ///         snapshot update; true means a higher-priority change (such as a running project deletion) requires
    ///         yielding, skipping the write and snapshot update and returning false.
    ///     </para>
    /// </param>
    /// <returns>
    ///     <para>是否已写入；false 表示命中让位谓词而静默放弃。</para>
    ///     <para>Whether the write happened; false means the yield predicate hit and the write silently yielded.</para>
    /// </returns>
    public async Task<bool> SetProjectPinnedAsync(
        string            storeDirectory, string projectId, bool isPinned, Func<bool>? shouldYield,
        CancellationToken cancellationToken)
    {
        await MutationGate.WaitAsync(cancellationToken);
        try
        {
            // 门内复查让位谓词：写入在门上排队期间项目删除可能已启动并清空该项目置顶，命中时静默放弃。
            if (shouldYield is { } precondition && precondition()) return false;
            var pins = await SessionStorePinStore.SetProjectPinnedAsync(storeDirectory, projectId, isPinned,
                                                                        cancellationToken);
            UpdatePinState(storeDirectory, pins);
            return true;
        }
        finally
        {
            MutationGate.Release();
        }
    }

    /// <summary>
    ///     <para>在串行门内按拖放位置调整置顶项目顺序并更新快照；任一项目未置顶时不写入。</para>
    ///     <para>
    ///         Reorders pinned projects by a drop position and updates the snapshot under the serial gate; no write
    ///         occurs unless both projects are pinned.
    ///     </para>
    /// </summary>
    /// <param name="shouldYield">
    ///     <para>
    ///         写入前置谓词：取得串行门后、任何写入与快照更新前在门内求值；返回 true 表示存在必须让位的
    ///         更高优先级变更（如项目删除正在进行），此时不写入、不更新快照并返回 false。
    ///     </para>
    ///     <para>
    ///         The write precondition: evaluated inside the gate after acquiring it and before any write or
    ///         snapshot update; true means a higher-priority change (such as a running project deletion) requires
    ///         yielding, skipping the write and snapshot update and returning false.
    ///     </para>
    /// </param>
    /// <returns>
    ///     <para>是否已写入；false 表示命中让位谓词而静默放弃。</para>
    ///     <para>Whether the write happened; false means the yield predicate hit and the write silently yielded.</para>
    /// </returns>
    public async Task<bool> MovePinnedProjectAsync(
        string      storeDirectory, string            sourceProjectId, string targetProjectId, bool insertAfter,
        Func<bool>? shouldYield,    CancellationToken cancellationToken)
    {
        await MutationGate.WaitAsync(cancellationToken);
        try
        {
            // 门内复查让位谓词：写入在门上排队期间项目删除可能已启动并清空置顶状态，命中时静默放弃。
            if (shouldYield is { } precondition && precondition()) return false;
            var pins = await SessionStorePinStore.MovePinnedProjectAsync(storeDirectory, sourceProjectId,
                                                                         targetProjectId, insertAfter,
                                                                         cancellationToken);
            UpdatePinState(storeDirectory, pins);
            return true;
        }
        finally
        {
            MutationGate.Release();
        }
    }

    /// <summary>
    ///     <para>
    ///         在串行门内校验受保护目录、保存归档状态并尽力同步索引投影；索引缺失或不可用时不创建库文件，
    ///         同步失败只记诊断，不回滚已保存的归档权威。
    ///     </para>
    ///     <para>
    ///         Under the serial gate, validates the protected directory, saves archive state, and syncs the index
    ///         projection best-effort; a missing or unusable index is skipped without creating a database file, and
    ///         sync failures stay diagnostic without rolling the authoritative save back.
    ///     </para>
    /// </summary>
    /// <param name="storeDirectory">
    ///     <para>发起保存的会话存储目录。</para>
    ///     <para>The session-store directory the save was started for.</para>
    /// </param>
    /// <param name="resolveProtectedDirectory">
    ///     <para>受保护存储目录解析回调；在门内调用，解析结果与传入 store 不一致时拒绝保存。</para>
    ///     <para>
    ///         The protected-store resolution callback; invoked inside the gate, and a result mismatching the
    ///         requested store is refused.
    ///     </para>
    /// </param>
    /// <param name="sessionId">
    ///     <para>32 位十六进制会话 ID。</para>
    ///     <para>The 32-character hexadecimal session ID.</para>
    /// </param>
    /// <param name="isArchived">
    ///     <para>目标归档状态。</para>
    ///     <para>The requested archive state.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消解析、锁等待、归档保存与索引同步。</para>
    ///     <para>Cancels resolution, gate waits, the archive save, and index syncing.</para>
    /// </param>
    /// <returns>
    ///     <para>保存后的归档快照。</para>
    ///     <para>The archive snapshot after the save.</para>
    /// </returns>
    /// <exception cref="SessionStoreException">
    ///     <para>受保护目录与传入 store 不一致。</para>
    ///     <para>The protected directory does not match the requested store.</para>
    /// </exception>
    public async Task<SessionStoreArchives> SetSessionArchivedAsync(
        string                                storeDirectory,
        Func<CancellationToken, Task<string>> resolveProtectedDirectory,
        string                                sessionId,
        bool                                  isArchived,
        CancellationToken                     cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolveProtectedDirectory);
        await MutationGate.WaitAsync(cancellationToken);
        try
        {
            var protectedStoreDirectory = await resolveProtectedDirectory(cancellationToken);
            if (!ProjectRoots.AreEqual(protectedStoreDirectory, storeDirectory))
                throw new SessionStoreException(
                                                "The configured session-store directory changed before its archive state was saved.");

            var archives = await SessionStoreArchiveStore.SetArchivedAsync(storeDirectory, sessionId, isArchived,
                                                                           cancellationToken);
            UpdateArchiveState(storeDirectory, archives);

            // 权威归档状态已保存；索引投影尽力同步，失败不回滚归档保存，由后台对账从归档来源追平。
            // The authoritative archive state is saved; the index projection syncs best-effort — a failure never
            // rolls the save back, and background reconciliation catches up from the archive source.
            try
            {
                var index        = new SessionIndexStore(storeDirectory);
                // 索引缺失或不可用时不同步也不创建库文件：归档权威已保存，投影缺口留给后台对账追平。
                // A missing or unusable index skips the sync without creating a database file: the authoritative
                // archive state is saved, and background reconciliation closes the projection gap.
                var availability = await index.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
                if (availability.Status == SessionIndexAvailabilityStatus.Available)
                    await index.SyncArchiveProjectionAsync(new HashSet<string>(archives.ArchivedSessionIds,
                                                                               StringComparer.Ordinal),
                                                           cancellationToken)
                               .ConfigureAwait(false);
            }
            catch (Exception indexException) when (indexException is SessionIndexException or IOException or
                                                                     UnauthorizedAccessException)
            {
                Trace.TraceError("Session index archive projection for '{0}' failed to sync: {1}",
                                 storeDirectory, indexException.Message);
            }

            return archives;
        }
        finally
        {
            MutationGate.Release();
        }
    }

    /// <summary>
    ///     <para>在串行门内清除项目置顶状态并更新快照；用于项目删除的置顶清理。</para>
    ///     <para>
    ///         Clears a project's pin state and updates the snapshot under the serial gate; used by project-deletion
    ///         cleanup.
    ///     </para>
    /// </summary>
    public async Task ClearProjectPinStateAsync(
        string storeDirectory, string projectId, CancellationToken cancellationToken)
    {
        await MutationGate.WaitAsync(cancellationToken);
        try
        {
            var pins = await SessionStorePinStore.SetProjectPinnedAsync(storeDirectory, projectId, false,
                                                                        cancellationToken);
            UpdatePinState(storeDirectory, pins);
        }
        finally
        {
            MutationGate.Release();
        }
    }

    /// <summary>
    ///     <para>
    ///         编排项目删除：标记、解绑、置顶清理与最终移除按既有顺序执行，界面事实经 gateway 应用；
    ///         解绑不完整时 Core 在清理前中止。
    ///     </para>
    ///     <para>
    ///         Orchestrates project deletion: marker, unassignment, pin cleanup, and final removal run in the
    ///         established order, with UI facts applied through the gateway; an incomplete unassignment aborts
    ///         before cleanup.
    ///     </para>
    /// </summary>
    /// <param name="storeDirectory">
    ///     <para>调用方已解析并校验的受保护存储目录。</para>
    ///     <para>The caller-resolved and validated protected store directory.</para>
    /// </param>
    /// <param name="projectId">
    ///     <para>要删除的规范项目 ID。</para>
    ///     <para>The canonical project id to delete.</para>
    /// </param>
    /// <param name="projectRevision">
    ///     <para>发起删除时看到的目录册修订号。</para>
    ///     <para>The catalog revision observed when deletion was confirmed.</para>
    /// </param>
    /// <param name="gateway">
    ///     <para>宿主接缝；各阶段回调按固定顺序调用。</para>
    ///     <para>The host seam; stage callbacks run in a fixed order.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消目录册写入、解绑扫描与置顶清理。</para>
    ///     <para>Cancels catalog writes, the unassignment pass, and pin cleanup.</para>
    /// </param>
    /// <returns>
    ///     <para>持久化的删除标记项目。</para>
    ///     <para>The project as persisted with its deletion marker.</para>
    /// </returns>
    /// <exception cref="ProjectStoreException">
    ///     <para>项目不存在、修订冲突或未按删除标记完成。</para>
    ///     <para>The project is missing, a revision conflicts, or completion finds no marker.</para>
    /// </exception>
    /// <exception cref="SessionProjectUnassignmentIncompleteException">
    ///     <para>解绑结果不完整（仍有日志被占用或失败）；目录册删除标记保留，清理与移除未执行。</para>
    ///     <para>
    ///         The unassignment result is incomplete (journals still held or failed); the catalog's deletion
    ///         marker is kept and no cleanup or removal ran.
    ///     </para>
    /// </exception>
    public async Task<Project> DeleteProjectAsync(
        string                         storeDirectory,
        string                         projectId,
        long                           projectRevision,
        ISessionProjectDeletionGateway gateway,
        CancellationToken              cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        var catalogPath = ProjectStore.FilePath(storeDirectory);
        var deleting = await ProjectStore.BeginDeletionAsync(catalogPath, projectId, projectRevision,
                                                             cancellationToken : cancellationToken);
        await gateway.DeletionMarkedAsync(deleting, cancellationToken);
        await gateway.UnbindDraftPagesAsync(storeDirectory, projectId, cancellationToken);
        await gateway.UnbindOpenRuntimeSessionsAsync(storeDirectory, projectId, cancellationToken);

        // 解绑放工作线程：日志扫描与追加不占用调用方上下文，与既有调度语义一致。
        var result = await Task.Run(() => SessionProjectUnbinder.UnassignAsync(storeDirectory, projectId,
                                                                               cancellationToken),
                                    cancellationToken);
        await gateway.UnassignmentAppliedAsync(result, cancellationToken);

        // 完整性由 Core 把关：应用行投影后仍有占用或失败时中止，删除标记、置顶与日志归属保留供重试。
        if (!result.IsComplete)
            throw new SessionProjectUnassignmentIncompleteException(result);

        await ClearProjectPinStateAsync(storeDirectory, projectId, cancellationToken);
        await gateway.PrepareCompletionAsync(storeDirectory, projectId, cancellationToken);
        await ProjectStore.CompleteDeletionAsync(catalogPath, projectId, deleting.Revision, cancellationToken);
        await gateway.CompletedAsync(deleting, cancellationToken);
        return deleting;
    }

    /// <summary>
    ///     <para>持有会话 writer 锁永久删除日志，并尽力清理归档、置顶与索引投影。</para>
    ///     <para>
    ///         Permanently deletes the log under its writer lock and clears archive, pin, and index projections
    ///         best-effort.
    ///     </para>
    /// </summary>
    /// <param name="storeDirectory">
    ///     <para>调用方已解析的受保护存储目录。</para>
    ///     <para>The caller-resolved protected store directory.</para>
    /// </param>
    /// <param name="workspaceRoot">
    ///     <para>必须与会话 store 保持隔离的工作区根目录。</para>
    ///     <para>The workspace root that the session store must remain isolated from.</para>
    /// </param>
    /// <param name="sessionId">
    ///     <para>32 位十六进制会话 ID。</para>
    ///     <para>The 32-character hexadecimal session ID.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消删除；投影清理不随取消中断。</para>
    ///     <para>Cancels the deletion; projection cleanup never cancels.</para>
    /// </param>
    /// <returns>
    ///     <para>未成功的清理项；列表非空表示删除已成功但部分投影残留。</para>
    ///     <para>Failed cleanup items; a non-empty list means deletion succeeded with leftover projections.</para>
    /// </returns>
    /// <exception cref="SessionStoreException">
    ///     <para>writer 被占用、日志缺失或删除失败；此时不做投影清理。</para>
    ///     <para>The writer is held, the log is missing, or deletion failed; no cleanup runs in that case.</para>
    /// </exception>
    public async Task<IReadOnlyList<SessionStoreCleanupIssue>> DeleteSessionPermanentlyAsync(
        string storeDirectory, string workspaceRoot, string sessionId, CancellationToken cancellationToken)
    {
        await SessionStoreDeletion.DeleteAsync(storeDirectory, workspaceRoot, sessionId, cancellationToken);
        return await CleanupDeletedSessionProjectionsAsync(storeDirectory, sessionId);
    }

    /// <summary>
    ///     <para>清理已删除日志的归档、置顶与索引投影；单项失败记入结果继续其余清理，不抛出。</para>
    ///     <para>
    ///         Clears archive, pin, and index projections of a deleted log; a failed item is recorded and the rest
    ///         continue without throwing.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         清理不接收取消令牌：删除已成功后清理必须完成，即使窗口正在关闭；置顶与归档清理共享串行门，
    ///         索引清理按可用性尽力执行，缺失或不可用的索引不创建库文件。
    ///     </para>
    ///     <para>
    ///         Cleanup takes no cancellation token: it must complete once deletion succeeded, even while the window
    ///         is closing. Pin and archive cleanup share the serial gate, and index cleanup runs best-effort by
    ///         availability without creating a database file for a missing or unusable index.
    ///     </para>
    /// </remarks>
    public async Task<IReadOnlyList<SessionStoreCleanupIssue>> CleanupDeletedSessionProjectionsAsync(
        string storeDirectory, string sessionId)
    {
        List<SessionStoreCleanupIssue> issues = [];
        await MutationGate.WaitAsync(CancellationToken.None);
        try
        {
            try
            {
                var archives = await SessionStoreArchiveStore.SetArchivedAsync(storeDirectory, sessionId, false,
                                                                               CancellationToken.None);
                UpdateArchiveState(storeDirectory, archives);
            }
            catch (Exception exception)
            {
                issues.Add(new SessionStoreCleanupIssue(SessionStoreCleanupArea.ArchiveState, exception.Message));
                Trace.TraceError("Session archive cleanup for '{0}' failed: {1}", storeDirectory, exception.Message);
            }

            try
            {
                var pins = await SessionStorePinStore.SetSessionPinnedAsync(storeDirectory, sessionId, false,
                                                                            CancellationToken.None);
                UpdatePinState(storeDirectory, pins);
            }
            catch (Exception exception)
            {
                issues.Add(new SessionStoreCleanupIssue(SessionStoreCleanupArea.PinState, exception.Message));
                Trace.TraceError("Session pin cleanup for '{0}' failed: {1}", storeDirectory, exception.Message);
            }
        }
        finally
        {
            MutationGate.Release();
        }

        try
        {
            var index        = new SessionIndexStore(storeDirectory);
            var availability = await index.GetAvailabilityAsync(CancellationToken.None);
            if (availability.Status == SessionIndexAvailabilityStatus.Available)
                await index.RemoveAsync(sessionId, CancellationToken.None);
        }
        catch (Exception exception) when (exception is SessionIndexException or IOException or
                                                       UnauthorizedAccessException)
        {
            issues.Add(new SessionStoreCleanupIssue(SessionStoreCleanupArea.SessionIndex, exception.Message));
            Trace.TraceError("Session index cleanup for '{0}' failed: {1}", storeDirectory, exception.Message);
        }

        return issues;
    }

    private void UpdatePinState(string storeDirectory, SessionStorePins pins)
    {
        var storeKey = StoreKey(storeDirectory);
        _pinStatesByStore[storeKey] = pins;
        _pinStateUnavailableStores.Remove(storeKey);
        // 快照已变：整体失效两个 ID 集合缓存，懒重建成本可忽略。
        _pinnedSessionIdsByStore.Clear();
        _archivedSessionIdsByStore.Clear();
    }

    private void UpdateArchiveState(string storeDirectory, SessionStoreArchives archives)
    {
        var storeKey = StoreKey(storeDirectory);
        _archiveStatesByStore[storeKey] = archives;
        _archiveStateUnavailableStores.Remove(storeKey);
        // 快照已变：整体失效两个 ID 集合缓存，懒重建成本可忽略。
        _pinnedSessionIdsByStore.Clear();
        _archivedSessionIdsByStore.Clear();
    }
}

/// <summary>
///     <para>置顶准备结果：标记旧 Desktop 置顶列表是否参与了本轮读取。</para>
///     <para>The pin-preparation result: whether legacy Desktop pin lists took part in this read.</para>
/// </summary>
public sealed record SessionPinStatePreparation(bool LegacyPinsPresent);

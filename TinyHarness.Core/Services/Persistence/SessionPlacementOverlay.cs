using TinyHarness.Core.Models.Persistence;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>
///         把迁移完成后的业务库归属叠加到日志派生的投影（完整恢复与列表摘要）：行缺失表示尚未初始化，
///         空项目表示明确无项目，项目行给出当前归属；未迁移时保持日志派生值不变。
///     </para>
///     <para>
///         Overlays the post-migration business placement onto projections derived from logs (full recovery and list
///         summaries): a missing row means not initialized, a null project means explicitly no project, and a project
///         row gives the current placement; while not migrated the log-derived values stay unchanged.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         各叠加点（严格恢复、摘要构建、降级扫描、索引更新）共用同一映射，避免历史上 JSONL 归属在任一投影里复活；
///         迁移进行中或标记损坏时按类型化异常拒绝，而不是把日志推导值当作权威。
///     </para>
///     <para>
///         Every overlay point (strict recovery, summary building, the degraded scan, and index updates) shares one
///         mapping, so historical JSONL ownership cannot resurrect in any projection; a running migration or a damaged
///         marker is refused with a typed exception instead of treating log-derived values as authoritative.
///     </para>
/// </remarks>
internal static class SessionPlacementOverlay
{
    /// <summary>
    ///     <para>
    ///         迁移完成时一次读取全部归属行；未迁移返回 null，表示不做叠加；迁移进行中或标记损坏时按类型化异常
    ///         拒绝，绝不回退日志推导的归属。
    ///     </para>
    ///     <para>
    ///         Loads every placement row after migration; null while not migrated, meaning no overlay. A running
    ///         migration or a damaged marker is refused with a typed exception and never falls back to log-derived
    ///         ownership.
    ///     </para>
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, SessionStorePlacement>?> LoadAsync(
        string storeDirectory, CancellationToken cancellationToken)
    {
        var resolution = await SessionStoreAuthority.ResolveAsync(storeDirectory, cancellationToken).ConfigureAwait(false);
        SessionStoreAuthority.ThrowIfBlocked(storeDirectory, resolution);
        if (resolution.Mode != SessionStoreMode.Migrated) return null;
        return await SessionStorePlacementStore.LoadAllAsync(storeDirectory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>用全量归属快照覆盖完整恢复会话的归属字段；快照为 null 时原样返回。</para>
    ///     <para>Overrides a recovered session's placement fields from the full snapshot; a null snapshot returns the input unchanged.</para>
    /// </summary>
    public static PersistedSession? Apply(
        PersistedSession? session, IReadOnlyDictionary<string, SessionStorePlacement>? placements)
    {
        if (session is null || placements is null) return session;
        return ApplyPlacement(session, session.Id, placements);
    }

    /// <summary>
    ///     <para>用全量归属快照覆盖摘要的归属字段；快照为 null 时原样返回。</para>
    ///     <para>Overrides a summary's placement fields from the full snapshot; a null snapshot returns the input unchanged.</para>
    /// </summary>
    public static SessionSummary Apply(
        SessionSummary summary, string sessionId, IReadOnlyDictionary<string, SessionStorePlacement>? placements)
    {
        if (placements is null) return summary;
        return ApplyPlacement(summary, sessionId, placements);
    }

    /// <summary>
    ///     <para>
    ///         读取单个会话的当前归属并覆盖摘要；未迁移时原样返回，迁移后缺行为尚未初始化，迁移进行中或标记损坏时
    ///         按类型化异常拒绝。
    ///     </para>
    ///     <para>
    ///         Reads one session's current placement and overrides a summary; while not migrated the input returns
    ///         unchanged, after migration a missing row means not initialized, and a running migration or damaged marker
    ///         is refused with a typed exception.
    ///     </para>
    /// </summary>
    public static async Task<SessionSummary> ApplyAsync(
        string storeDirectory, string sessionId, SessionSummary summary, CancellationToken cancellationToken)
    {
        var resolution = await SessionStoreAuthority.ResolveAsync(storeDirectory, cancellationToken).ConfigureAwait(false);
        SessionStoreAuthority.ThrowIfBlocked(storeDirectory, resolution);
        if (resolution.Mode != SessionStoreMode.Migrated) return summary;
        var placement = await SessionStorePlacementStore.GetAsync(storeDirectory, sessionId, cancellationToken)
                                                       .ConfigureAwait(false);
        return overlay(placement);

        SessionSummary overlay(SessionStorePlacement applied) => applied.State switch
        {
            SessionProjectAssignmentState.Project => summary with
            {
                ProjectId              = applied.ProjectId,
                ProjectAssignmentState = SessionProjectAssignmentState.Project
            },
            SessionProjectAssignmentState.NoProject => summary with
            {
                ProjectId              = null,
                ProjectAssignmentState = SessionProjectAssignmentState.NoProject
            },
            _ => summary with
            {
                ProjectId              = null,
                ProjectAssignmentState = SessionProjectAssignmentState.PendingInitialization
            }
        };
    }

    private static PersistedSession ApplyPlacement(
        PersistedSession session, string sessionId,
        IReadOnlyDictionary<string, SessionStorePlacement> placements)
    {
        if (!placements.TryGetValue(sessionId, out var placement))
            return session with
            {
                ProjectId              = null,
                ProjectAssignmentState = SessionProjectAssignmentState.PendingInitialization
            };

        return placement.State switch
        {
            SessionProjectAssignmentState.Project => session with
            {
                ProjectId              = placement.ProjectId,
                ProjectAssignmentState = SessionProjectAssignmentState.Project
            },
            SessionProjectAssignmentState.NoProject => session with
            {
                ProjectId              = null,
                ProjectAssignmentState = SessionProjectAssignmentState.NoProject
            },
            _ => session with
            {
                ProjectId              = null,
                ProjectAssignmentState = SessionProjectAssignmentState.PendingInitialization
            }
        };
    }

    private static SessionSummary ApplyPlacement(
        SessionSummary summary, string sessionId,
        IReadOnlyDictionary<string, SessionStorePlacement> placements)
    {
        if (!placements.TryGetValue(sessionId, out var placement))
            return summary with
            {
                ProjectId              = null,
                ProjectAssignmentState = SessionProjectAssignmentState.PendingInitialization
            };

        return placement.State switch
        {
            SessionProjectAssignmentState.Project => summary with
            {
                ProjectId              = placement.ProjectId,
                ProjectAssignmentState = SessionProjectAssignmentState.Project
            },
            SessionProjectAssignmentState.NoProject => summary with
            {
                ProjectId              = null,
                ProjectAssignmentState = SessionProjectAssignmentState.NoProject
            },
            _ => summary with
            {
                ProjectId              = null,
                ProjectAssignmentState = SessionProjectAssignmentState.PendingInitialization
            }
        };
    }
}

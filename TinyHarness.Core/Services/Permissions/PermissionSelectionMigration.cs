using TinyHarness.Core.Models.Persistence;

namespace TinyHarness.Core.Services.Permissions;

/// <summary>
///     <para>会话恢复时审批模式选择的迁移判定结果。</para>
///     <para>The migration verdict for an approval-mode selection when a session resumes.</para>
/// </summary>
public enum PermissionSelectionMigrationStatus
{
    /// <summary>
    ///     <para>无需迁移：会话已有 /2 选择，或全新会话尚无任何历史运行。</para>
    ///     <para>No migration needed: the session has a /2 selection, or is new with no historical runs.</para>
    /// </summary>
    NotRequired,

    /// <summary>
    ///     <para>
    ///         等待迁移确认：会话含 /1 选择，或既无选择又存在历史运行。确认前禁止模型工具执行；
    ///         确认后写入 migration 记录与 /2 选择。
    ///     </para>
    ///     <para>
    ///         Migration confirmation is pending: the session carries a /1 selection, or has neither a selection
    ///         nor an empty history. Until confirmed the runtime blocks model tool execution;
    ///         confirmation writes the migration record and the /2 selection.
    ///     </para>
    /// </summary>
    MigrationPending
}

/// <summary>
///     <para>
///         恢复判定 API（纯函数）：由持久化侧输入会话事实，输出迁移判定；接线由持久化层完成。
///     </para>
///     <para>
///         The pure recovery-decision API: the persistence side feeds session facts in and wires the verdict out.
///     </para>
/// </summary>
public static class PermissionSelectionMigration
{
    /// <summary>
    ///     <para>
    ///         判定迁移需求：任何 /1 选择，或"无选择但存在历史运行"，都需要迁移确认；
    ///         已有 /2 选择或全新会话不需要。
    ///     </para>
    ///     <para>
    ///         Decides the migration need: any /1 selection, or no selection with historical runs, requires
    ///         confirmation; an existing /2 selection or a brand-new session does not.
    ///     </para>
    /// </summary>
    /// <param name="hasLegacySelection">
    ///     <para>会话是否含任何 /1 权限选择记录。</para>
    ///     <para>Whether the session carries any /1 selection record.</para>
    /// </param>
    /// <param name="hasSelection">
    ///     <para>会话是否已有 /2 审批模式选择。</para>
    ///     <para>Whether the session already carries a /2 approval-mode selection.</para>
    /// </param>
    /// <param name="hasHistoryRuns">
    ///     <para>会话是否存在历史运行（回合/运行记录）。</para>
    ///     <para>Whether the session has historical runs (turn or run records).</para>
    /// </param>
    public static PermissionSelectionMigrationStatus Evaluate(bool hasLegacySelection, bool hasSelection,
                                                              bool hasHistoryRuns)
    {
        if (hasLegacySelection) return PermissionSelectionMigrationStatus.MigrationPending;
        if (!hasSelection && hasHistoryRuns) return PermissionSelectionMigrationStatus.MigrationPending;
        return PermissionSelectionMigrationStatus.NotRequired;
    }
}

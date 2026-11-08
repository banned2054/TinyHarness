using TinyHarness.Core.Models.Tools;

namespace TinyHarness.Core.Models.Permissions;

/// <summary>
///     <para>审批授权的存活范围；单次授权覆盖一次尝试，Turn 授权存活到运行终态，Session 授权存活到会话关闭。</para>
///     <para>
///         How long an approval grant stays alive: single-operation covers one attempt, Turn lives until the run
///         terminates, and Session lives until the session closes.
///     </para>
/// </summary>
public enum GrantScope
{
    /// <summary>
    ///     <para>仅覆盖与指纹匹配的这一次调用；消费即失效。</para>
    ///     <para>Covers exactly this invocation; spent on first consumption.</para>
    /// </summary>
    SingleOperation,

    /// <summary>
    ///     <para>在本次运行内允许该能力与资源范围；运行终态撤销，不跨重启恢复。</para>
    ///     <para>Allows the capability and scope for the current run; revoked at run end and never restored.</para>
    /// </summary>
    Turn,

    /// <summary>
    ///     <para>由用户显式选择，存活到会话关闭或显式撤销；不持久化为可执行权限，不跨重启恢复。</para>
    ///     <para>
    ///         Explicitly selected by the user; lives until session close or revocation, is never persisted
    ///         as executable authority, and never survives restart.
    ///     </para>
    /// </summary>
    Session
}

/// <summary>
///     <para>按执行分类推导审批卡可提供的授权范围集合。</para>
///     <para>Derives the grant-scope options an approval card may offer for an execution level.</para>
/// </summary>
public static class GrantScopeOptions
{
    /// <summary>
    ///     <para>
    ///         Level3（宿主）只允许单次授权；Level2 与未分类（fail-closed）允许单次与 Turn，
    ///         Session 由用户显式选择；Level1 基线自动放行，没有可授权范围。
    ///     </para>
    ///     <para>
    ///         Level3 (host) allows only single-operation; Level2 and unclassified fail-closed asks allow
    ///         single-operation and Turn with Session as an explicit opt-in; the Level1 baseline runs without
    ///         grants.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<GrantScope> AllowedForLevel(PermissionLevel level) => level switch
    {
        PermissionLevel.Level1 => [GrantScope.SingleOperation],
        PermissionLevel.Level3 => [GrantScope.SingleOperation],
        _                      => [GrantScope.SingleOperation, GrantScope.Turn, GrantScope.Session]
    };

    /// <summary>
    ///     <para>
    ///         按冻结计划推导审批卡可提供的授权范围：携带 shell 写根申请的调用把写范围冻结进
    ///         授权与 OS 生效面，Session 范围会使模型在会话内以同一授权反复申请新命令写根——
    ///         因此只允许单次（缺省）与 Turn；其余计划沿用 <see cref="AllowedForLevel" />。
    ///     </para>
    ///     <para>
    ///         Derives the grant-scope options from the frozen plan: a call carrying shell write roots freezes
    ///         the write scope into both the grant and the OS effective surface, and a Session scope would let
    ///         the model re-request new command write roots under one approval for the whole session — so only
    ///         single-operation (the default) and Turn are allowed; every other plan keeps
    ///         <see cref="AllowedForLevel" />.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<GrantScope> AllowedForPreparation(PermissionLevel level, ToolPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (preparation.RequestedWriteRoots.Count == 0) return AllowedForLevel(level);
        if (level is PermissionLevel.Level1 or PermissionLevel.Level3) return [GrantScope.SingleOperation];

        return [GrantScope.SingleOperation, GrantScope.Turn];
    }
}

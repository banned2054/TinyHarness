using TinyHarness.Core.Models.Tools;

namespace TinyHarness.Core.Models.Permissions;

/// <summary>
///     <para>授权的生命周期状态。</para>
///     <para>Lifecycle states of a grant.</para>
/// </summary>
public enum GrantState
{
    /// <summary>可用于匹配；SingleOperation 授权在消费后离开此状态。Matchable; a SingleOperation grant leaves it on consumption.</summary>
    Active,

    /// <summary>SingleOperation 授权已被一次调用消费。A SingleOperation grant was spent on one invocation.</summary>
    Consumed,

    /// <summary>被显式撤销（补充生效、信任变化、模式切换或用户撤销）。Explicitly revoked (amendment, trust change, mode switch or user revoke).</summary>
    Revoked,

    /// <summary>自然到期：Turn 授权随运行终态到期，或超过 ExpiresUtc。Expired naturally: Turn grants end with the run, or ExpiresUtc passed.</summary>
    Expired,

    /// <summary>被同资源的新授权替代。Superseded by a newer grant over the same resource.</summary>
    Superseded
}

/// <summary>
///     <para>
///         一次审批建立的显式授权：绑定能力、授权约束、资源范围与执行策略身份，带生命周期状态。
///         它只存在于会话内的 <see cref="Services.Permissions.GrantRegistry" />，不持久化为可执行权限。
///     </para>
///     <para>
///         An explicit grant established by one approval: bound to the capability, grant constraint, resource scope
///         and execution-policy identity with a lifecycle state. It lives only in the session's
///         <see cref="Services.Permissions.GrantRegistry" /> and is never persisted as executable authority.
///     </para>
/// </summary>
public sealed record Grant(
    string                   GrantId,
    GrantScope               Scope,
    string                   Capability,
    string?                  Constraint,
    IReadOnlyList<string>    Targets,
    string?                  PolicyIdentity,
    DateTimeOffset           CreatedUtc,
    DateTimeOffset?          ExpiresUtc,
    GrantState               State,
    string?                  MissingCapability = null)
{
    /// <summary>
    ///     <para>
    ///         从已冻结计划建立活动授权；约束优先取授权约束（命令加执行策略身份），缺省回落到命令约束。
    ///     </para>
    ///     <para>
    ///         Creates an active grant from a frozen plan; the constraint prefers the grant constraint (command
    ///         plus execution-policy identity) and falls back to the bare command constraint.
    ///     </para>
    /// </summary>
    public static Grant Active(string grantId, GrantScope scope, ToolPreparation preparation,
                               DateTimeOffset createdUtc, TimeSpan? lifetime = null) =>
        new(grantId, scope, preparation.Capability,
            preparation.SessionGrantConstraint ?? preparation.SessionConstraint,
            preparation.TargetPaths, preparation.ExecutionPolicy,
            createdUtc, lifetime is { } span ? createdUtc + span : null, GrantState.Active);
}

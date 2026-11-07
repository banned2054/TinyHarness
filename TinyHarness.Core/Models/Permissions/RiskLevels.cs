namespace TinyHarness.Core.Models.Permissions;

/// <summary>
///     <para>待审操作的风险维度，供审批人参考；它不参与授权计算。</para>
///     <para>
///         The risk dimension of a pending invocation, offered for reviewer reference; it never takes part
///         in authorization.
///     </para>
/// </summary>
public enum RiskLevel
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
///     <para>审批人判定的用户授权强度维度；它同样不参与授权计算。</para>
///     <para>The user-authorization strength a reviewer assigns; likewise never part of authorization.</para>
/// </summary>
public enum UserAuthorization
{
    High,
    Medium,
    Low,
    Unknown
}

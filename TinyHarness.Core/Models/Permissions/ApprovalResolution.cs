namespace TinyHarness.Core.Models.Permissions;

/// <summary>
///     <para>审批通道对一次请求的结论种类。</para>
///     <para>The kinds of resolution an approval channel can return for one request.</para>
/// </summary>
public enum ApprovalResolutionKind
{
    /// <summary>已批准；Scope 记录批准的授权范围。Granted; Scope records the approved grant scope.</summary>
    Granted,

    /// <summary>已拒绝；操作保持未执行，Reason 携带拒绝理由。Denied; the operation stays unexecuted with a reason.</summary>
    Denied,

    /// <summary>
    ///     通道不可用（人审通道不可达、审核失败保持未执行）；不是拒绝，操作保持未执行等待明确处置。
    ///     The channel is unavailable (unreachable human channel, failed review); not a denial — the operation
    ///     stays unexecuted pending explicit handling.
    /// </summary>
    Unavailable
}

/// <summary>
///     <para>
///         审批结论：结论种类、批准的授权范围（仅 Granted 且选择了范围时非空）与面向展示的原因。
///         拒绝与不可用都不执行操作；它们是不同的结论种类。
///     </para>
///     <para>
///         One approval resolution: the kind, the approved grant scope (non-null only for Granted with a chosen
///         scope) and a display-facing reason. Denied and Unavailable both leave the operation unexecuted; they
///         are distinct kinds.
///     </para>
/// </summary>
public sealed record ApprovalResolution(
    ApprovalResolutionKind Kind,
    GrantScope?            Scope,
    string?                Reason)
{
    /// <summary>创建带授权范围的批准结论。Creates a granted resolution with the approved scope.</summary>
    public static ApprovalResolution Granted(GrantScope scope) =>
        new(ApprovalResolutionKind.Granted, scope, null);

    /// <summary>创建带理由的拒绝结论。Creates a denied resolution with a reason.</summary>
    public static ApprovalResolution Denied(string reason) =>
        new(ApprovalResolutionKind.Denied, null, reason);

    /// <summary>创建带理由的不可用结论。Creates an unavailable resolution with a reason.</summary>
    public static ApprovalResolution Unavailable(string reason) =>
        new(ApprovalResolutionKind.Unavailable, null, reason);
}

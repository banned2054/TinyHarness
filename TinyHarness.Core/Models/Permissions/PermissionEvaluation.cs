namespace TinyHarness.Core.Models.Permissions;

/// <summary>
///     <para>
///         权限引擎对一次已冻结调用的完整结论：三值决定、执行分类、缺失能力与面向展示的原因。
///         分类与缺失能力是审核维度，不是授权来源。
///     </para>
///     <para>
///         The engine's full verdict for one frozen invocation: the three-valued decision, execution level,
///         missing capabilities and a display-facing reason. Level and missing capabilities are review
///         dimensions, never sources of authority.
///     </para>
/// </summary>
public sealed record PermissionEvaluation(
    PermissionDecision     Decision,
    PermissionLevel        Level,
    IReadOnlyList<string>  MissingCapabilities,
    string                 Reason)
{
    /// <summary>
    ///     <para>创建带原因的拒绝结论；缺失能力默认为空。</para>
    ///     <para>Creates a denial with a reason; missing capabilities default to none.</para>
    /// </summary>
    public static PermissionEvaluation Deny(string reason, PermissionLevel level = PermissionLevel.None,
                                            IReadOnlyList<string>? missing = null) =>
        new(PermissionDecision.Deny, level, missing ?? [], reason);

    /// <summary>
    ///     <para>创建放行结论。</para>
    ///     <para>Creates an allow verdict.</para>
    /// </summary>
    public static PermissionEvaluation Allow(PermissionLevel level, string reason) =>
        new(PermissionDecision.Allow, level, [], reason);

    /// <summary>
    ///     <para>创建需审批结论。</para>
    ///     <para>Creates an approval-required verdict.</para>
    /// </summary>
    public static PermissionEvaluation Ask(PermissionLevel level, string reason,
                                           IReadOnlyList<string>? missing = null) =>
        new(PermissionDecision.Ask, level, missing ?? [], reason);
}

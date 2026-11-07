namespace TinyHarness.Core.Models.Permissions;

/// <summary>
///     <para>待审操作的执行分类，仅用于审批展示与审计；它不是权限来源。</para>
///     <para>
///         The execution classification of a pending invocation, used for approval display and audit
///         only; it is never a source of authority.
///     </para>
/// </summary>
public enum PermissionLevel
{
    /// <summary>
    ///     <para>无法分类（未知能力或硬限制拒绝）；展示为未分级。</para>
    ///     <para>Unclassifiable (unknown capability or hard-limit denial); shown as ungraded.</para>
    /// </summary>
    None,

    /// <summary>
    ///     <para>沙箱基线：工作区读写、读根与沙箱 Shell，引擎自动放行。</para>
    ///     <para>Sandbox baseline: workspace reads and writes, read roots and sandboxed shell; auto-allowed.</para>
    /// </summary>
    Level1,

    /// <summary>
    ///     <para>沙箱内附加能力：工作区外路径、受保护元数据写入与网络申请，需要审批。</para>
    ///     <para>Sandbox add-ons: outside-workspace paths, protected-metadata writes and network requests; approval required.</para>
    /// </summary>
    Level2,

    /// <summary>
    ///     <para>宿主执行：无操作系统隔离的单次申请，始终需要审批且只允许单次授权。</para>
    ///     <para>Host execution: one unisolated request; always approved and only ever single-operation.</para>
    /// </summary>
    Level3
}

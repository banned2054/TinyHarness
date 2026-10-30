namespace TinyHarness.Core.Services.Runtime;

/// <summary>
///     <para>工作区 ACL 检查发现的问题类别；缺少权限与管理拒绝可并存于同一问题。</para>
///     <para>
///         Categories of problems found by workspace ACL inspection;
///         missing access and management denial can coexist in one problem.
///     </para>
/// </summary>
public enum WorkspaceAclProblemCategory
{
    /// <summary>
    ///     <para>既有子项缺少父目录可继承 allow ACE 所要求的权限。</para>
    ///     <para>An existing child lacks access required by the parent's inheritable allow ACEs.</para>
    /// </summary>
    MissingInheritedAccess,

    /// <summary>
    ///     <para>普通权限无法打开传播继承所需的 ACL 管理句柄；这是事实观察，不代表必须提权。</para>
    ///     <para>
    ///         Normal privilege cannot open the ACL-management handle that propagation requires;
    ///         this is an observation, not a claim that elevation is required.
    ///     </para>
    /// </summary>
    ManagementDenied,

    /// <summary>
    ///     <para>检查本身失败：读取 DACL 被拒或出错、条目超限、目录身份变化等。</para>
    ///     <para>
    ///         The inspection itself failed: DACL reads denied or errored,
    ///         entry limits exceeded, or the directory identity changed.
    ///     </para>
    /// </summary>
    InspectionFailed
}

/// <summary>
///     <para>一条未满足的父 allow ACE 及其在子项上的所需、有效与向下传播掩码。</para>
///     <para>
///         One unsatisfied parent allow ACE with its required, effective
///         and downward-propagation masks on the child.
///     </para>
/// </summary>
public sealed record WorkspaceAclMissingAce(
    string Sid,
    uint   RequiredMask,
    uint   EffectiveMask,
    uint   ContainerPropagationMask,
    uint   ObjectPropagationMask);

/// <summary>
///     <para>结构化的工作区 ACL 问题：位置、类别、缺失 ACE 与管理探测结果。</para>
///     <para>A structured workspace ACL problem: location, category, missing ACEs and the management-probe outcome.</para>
/// </summary>
/// <remarks>
///     <para>
///         <see cref="CanManageAcl" /> 为 null 表示未探测或无法判定；false 只说明普通权限打不开管理句柄，
///         不构成“必须提权”的结论。缺少继承权限与管理拒绝可同时出现在同一问题中。
///     </para>
///     <para>
///         A null <see cref="CanManageAcl" /> means undetermined; false only states that normal privilege
///         could not open the management handle and never concludes that elevation is required.
///         Missing inherited access and management denial can coexist in one problem.
///     </para>
/// </remarks>
public sealed record WorkspaceAclProblem(
    string                                WriteRoot,
    string                                Path,
    WorkspaceAclProblemCategory           Category,
    IReadOnlyList<WorkspaceAclMissingAce> MissingAces,
    bool?                                 CanManageAcl,
    int?                                  Win32Error,
    string?                               Detail = null);

/// <summary>
///     <para>ACL 准备失败的结构化阻断信号；携带完整问题供上层精确呈现，不预设修复方式。</para>
///     <para>
///         A structured blocking signal for ACL preparation failures;
///         it carries the full problem for precise upstream reporting without prescribing a repair.
///     </para>
/// </summary>
public sealed class WorkspaceAclPreparationException : Exception
{
    public WorkspaceAclPreparationException(WorkspaceAclProblem problem)
        : base(Describe(problem ?? throw new ArgumentNullException(nameof(problem)))) =>
        Problem = problem;

    /// <summary>
    ///     <para>检查失败时的结构化问题：位置、类别、缺失掩码与管理探测结果。</para>
    ///     <para>The structured problem on failure: location, category, missing masks and the management probe.</para>
    /// </summary>
    public WorkspaceAclProblem Problem { get; }

    // 只陈述观察到的类别、掩码与管理探测结果；不输出修复命令，也不声称必须提权。
    private static string Describe(WorkspaceAclProblem problem)
    {
        var header = problem.Category switch
        {
            WorkspaceAclProblemCategory.MissingInheritedAccess =>
                $"Sandbox ACL inheritance is incomplete at '{problem.Path}' under write root '{problem.WriteRoot}'.",
            WorkspaceAclProblemCategory.ManagementDenied =>
                $"Sandbox ACL management is denied at '{problem.Path}' under write root '{problem.WriteRoot}'.",
            _ => $"Sandbox ACL inspection failed at '{problem.Path}' under write root '{problem.WriteRoot}'."
        };
        var detail = problem.Category switch
        {
            WorkspaceAclProblemCategory.MissingInheritedAccess =>
                "The setup helper returned success but existing children lack inherited allow ACEs" +
                DescribeEntries(problem.MissingAces)                                                + ".",
            WorkspaceAclProblemCategory.ManagementDenied =>
                "The current account cannot open the ACL-management handle required to propagate inheritance.",
            _ => string.IsNullOrEmpty(problem.Detail) ? "The check could not complete." : problem.Detail + "."
        };
        var observation = problem.CanManageAcl == false &&
                          problem.Category     != WorkspaceAclProblemCategory.ManagementDenied
            ? " The current account cannot open an ACL-management handle for the path."
            : string.Empty;
        var win32 = problem.Win32Error is { } code ? $" (Win32 error {code})" : string.Empty;
        return header + win32 + " " + detail + observation + " No command was started.";
    }

    private static string DescribeEntries(IReadOnlyList<WorkspaceAclMissingAce> entries) =>
        entries.Count == 0
            ? string.Empty
            : " (missing " + string.Join("; ", entries.Select(entry =>
                                                                  $"sid={entry.Sid} required=0x{entry.RequiredMask:X8} " +
                                                                  $"effective=0x{entry.EffectiveMask:X8} " +
                                                                  $"containers=0x{entry.ContainerPropagationMask:X8} " +
                                                                  $"objects=0x{entry.ObjectPropagationMask:X8}")) + ")";
}

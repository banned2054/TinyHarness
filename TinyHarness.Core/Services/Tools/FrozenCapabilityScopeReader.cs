using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Permissions;

namespace TinyHarness.Core.Services.Tools;

/// <summary>
///     <para>
///         从不可变准备计划提取冻结能力范围：穿透 Shell 工具链的包装计划
///         （工作区访问守卫、路由包装）读取 <see cref="PreparedProcessExecution.GrantScope" />，
///         映射为授权侧复核所需的 <see cref="FrozenCapabilityScope" />。
///     </para>
///     <para>
///         Extracts the frozen capability scope from an immutable prepared plan: it unwraps the
///         shell tool chain's wrapper plans (workspace-access guard, routing wrapper) to read
///         <see cref="PreparedProcessExecution.GrantScope" /> and maps it to the
///         <see cref="FrozenCapabilityScope" /> the authorization side revalidates against.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         映射规则：带根的 <see cref="SandboxGrantScope" /> 映射为对应的非 null 范围（派发复核
///         据此拒绝授权后的扩大）；<see cref="SandboxGrantScope.Empty" /> 与未携带字段的计划都映射
///         为 null——空冻结范围没有可被扩大的能力面，撤销语义由授权存活性检查覆盖，子集检查
///         只对实际携带 grant 根的计划激活。
///     </para>
///     <para>
///         Mapping rules: a <see cref="SandboxGrantScope" /> carrying roots maps to the corresponding
///         non-null scope (dispatch revalidation denies post-authorization expansion); both
///         <see cref="SandboxGrantScope.Empty" /> and plans without the field map to null — an empty
///         frozen scope has no capability surface to expand, revocation is covered by the grant-liveness
///         check, and the subset check activates only for plans actually carrying grant roots.
///     </para>
/// </remarks>
public static class FrozenCapabilityScopeReader
{
    /// <summary>
    ///     <para>
    ///         返回计划冻结的能力范围；计划未携带进程执行范围或冻结范围为空时为 null。
    ///         纯读取，不执行 I/O，不改变会话状态。
    ///     </para>
    ///     <para>
    ///         Returns the plan's frozen capability scope, or null when the plan carries no
    ///         process-execution scope or the frozen scope is empty. Pure read: no I/O and
    ///         no session state change.
    ///     </para>
    /// </summary>
    public static FrozenCapabilityScope? FromPreparation(ToolPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);

        var plan = UnwrapProcessPlan(preparation.ExecutionPlan);
        return plan?.GrantScope is { } scope && !scope.IsEmpty
            ? new FrozenCapabilityScope(scope.ReadRoots, scope.WriteRoots, scope.MetadataWritePaths)
            : null;
    }

    /// <summary>
    ///     <para>穿透包装计划定位冻结的进程执行计划；未知形态返回 null。</para>
    ///     <para>Unwraps wrapper plans to the frozen process-execution plan; unknown shapes yield null.</para>
    /// </summary>
    private static PreparedProcessExecution? UnwrapProcessPlan(object? executionPlan)
    {
        var current = executionPlan;
        for (var depth = 0; depth < 8 && current is not null; depth++)
        {
            switch (current)
            {
                case PreparedProcessExecution process:
                    return process;
                case PermissionShellTool.Plan routed:
                    current = routed.Preparation.ExecutionPlan;
                    break;
                case WorkspaceAccessState.GuardedTool.GuardedPlan guarded:
                    current = guarded.Preparation.ExecutionPlan;
                    break;
                default:
                    return null;
            }
        }

        return null;
    }
}

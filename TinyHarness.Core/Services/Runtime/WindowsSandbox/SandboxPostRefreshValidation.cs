using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         共享的后刷新校验步骤：先做写 ACL 继承校验，再做组读根就绪门，顺序固定。两个
///         refresh 点（<c>WindowsSandboxBackend.PreflightAsync</c> 与
///         <c>AuthorizedReadCoordinator</c> 恢复 refresh 后的内层启动前）共用同一顺序与同一
///         默认实现，任何一端都不单独漂移。
///     </para>
///     <para>
///         The shared post-refresh validation step: write-ACL inheritance validation first, then
///         the group read-root readiness gate, in a fixed order. Both refresh points
///         (<c>WindowsSandboxBackend.PreflightAsync</c> and the
///         <c>AuthorizedReadCoordinator</c>'s pre-launch moment after the recovery refresh) share
///         the same order and the same default implementation, so neither side drifts alone.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         注入形态对齐既有 <c>validateWriteAcls</c> 模式（便于测试与 stub）；生产默认的写校验
///         为 <see cref="WorkspaceAclInheritance.ValidateAsync" />，读就绪为
///         <see cref="SandboxReadRootReadiness.ValidateAsync" />。写校验失败沿用各调用点既有的
///         包装语义（结构化问题保留为 inner exception）；就绪门自身抛出启动期异常，调用点
///         不再二次包装。
///     </para>
///     <para>
///         The injection shape aligns with the existing <c>validateWriteAcls</c> pattern (for test
///         stubs); the production default composes
///         <see cref="WorkspaceAclInheritance.ValidateAsync" /> for writes and
///         <see cref="SandboxReadRootReadiness.ValidateAsync" /> for readiness. Write-validation
///         failures keep each call site's existing wrapping semantics (structured problems stay the
///         inner exception); the readiness gate throws the start exception itself and call sites
///         never wrap it a second time.
///     </para>
/// </remarks>
internal static class SandboxPostRefreshValidation
{
    /// <summary>
    ///     <para>生产默认：写 ACL 继承校验 + 组读根就绪门，顺序固定。</para>
    ///     <para>
    ///         The production default: write-ACL inheritance validation followed by the group
    ///         read-root readiness gate, in a fixed order.
    ///     </para>
    /// </summary>
    public static readonly Func<SandboxSetupPayload, CancellationToken, Task> Default =
        (payload, cancellationToken) =>
            ValidateAsync(payload, WorkspaceAclInheritance.ValidateAsync, SandboxReadRootReadiness.ValidateAsync,
                          cancellationToken);

    /// <summary>
    ///     <para>
    ///         执行共享的后刷新校验步骤：先写 ACL 继承校验，再组读根就绪门；任一失败即在命令
    ///         启动前终止本次执行。
    ///     </para>
    ///     <para>
    ///         Runs the shared post-refresh validation step: write-ACL inheritance validation
    ///         first, then the group read-root readiness gate; any failure stops this execution
    ///         before the command starts.
    ///     </para>
    /// </summary>
    public static async Task ValidateAsync(
        SandboxSetupPayload                                payload,
        Func<SandboxSetupPayload, CancellationToken, Task> validateWriteAcls,
        Func<SandboxSetupPayload, CancellationToken, Task> validateReadRootReadiness,
        CancellationToken                                  cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(validateWriteAcls);
        ArgumentNullException.ThrowIfNull(validateReadRootReadiness);
        await validateWriteAcls(payload, cancellationToken).ConfigureAwait(false);
        await validateReadRootReadiness(payload, cancellationToken).ConfigureAwait(false);
    }
}

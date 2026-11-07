using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         ShellTool 冻结 grant 范围所需的沙箱上下文：有效隔离策略与组件描述。两者由组合点在
///         组合期一次构建并冻结，ShellTool 在 Prepare 期用它把活动快照解析为
///         <see cref="SandboxGrantScope" />（含凭据重叠、组件保护与元数据白名单的 fail-closed 校验），
///         执行期不再查询任何实时源。
///     </para>
///     <para>
///         The sandbox context ShellTool needs to freeze grant scopes: the effective isolation policy
///         and the component description. Both are built and frozen once at composition; Prepare uses
///         them to resolve the active snapshot into a <see cref="SandboxGrantScope" /> (with the
///         fail-closed credential-overlap, component-protection, and metadata-whitelist validation),
///         and execution never queries any live source.
///     </para>
/// </summary>
/// <param name="Policy">冻结的有效沙箱隔离策略。 The frozen effective sandbox isolation policy.</param>
/// <param name="Components">沙箱组件与 home 描述（凭据校验与 SID 表定位所需）。 The sandbox components and home used for credential checks and SID-table lookup.</param>
public sealed record SandboxGrantContext(SandboxIsolationPolicy Policy, WindowsSandboxComponents Components);

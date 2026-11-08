using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         ShellTool 冻结 grant 范围所需的沙箱上下文：有效隔离策略、组件描述与 C1 权限系统实例
///         路径。三者由组合点在组合期一次构建并冻结，ShellTool 在 Prepare 期用它把活动快照解析为
///         <see cref="SandboxGrantScope" />（含凭据重叠、组件与 C1 保护及元数据白名单的
///         fail-closed 校验），执行期不再查询任何实时源。
///     </para>
///     <para>
///         The sandbox context ShellTool needs to freeze grant scopes: the effective isolation policy,
///         the component description, and the C1 permission-system instance paths. All three are built
///         and frozen once at composition; Prepare uses them to resolve the active snapshot into a
///         <see cref="SandboxGrantScope" /> (with the fail-closed credential-overlap, component and C1
///         protection, and metadata-whitelist validation), and execution never queries any live source.
///     </para>
/// </summary>
/// <param name="Policy">冻结的有效沙箱隔离策略。 The frozen effective sandbox isolation policy.</param>
/// <param name="Components">沙箱组件与 home 描述（凭据校验与 SID 表定位所需）。 The sandbox components and home used for credential checks and SID-table lookup.</param>
/// <param name="PermissionSystemPaths">
///     <para>
///         C1 权限系统实例路径（用户配置、共享信任文件与会话存储目录的实际路径，组合点传入）；
///         grant 写根与之双向重叠即拒绝。缺省 null 表示组合点没有此类实例，不做附加校验。
///     </para>
///     <para>
///         The C1 permission-system instance paths (the actual user-config, shared trust-file, and
///         session-store paths passed by the composition); a grant write root overlapping one
///         bidirectionally is refused. Null by default means the composition has no such instances and
///         no additional validation runs.
///     </para>
/// </param>
public sealed record SandboxGrantContext(
    SandboxIsolationPolicy   Policy,
    WindowsSandboxComponents Components,
    IReadOnlyList<string>?   PermissionSystemPaths = null);

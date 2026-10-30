namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>保留 CLI 修复顺序适配；权限修改及检查规则仅由 Core 实现。</para>
///     <para>Preserves CLI repair ordering adapters; Core alone implements permission changes and inspection.</para>
/// </summary>
internal static class WorkspaceSandboxAclRepair
{
    internal static Task RunAsync(
        Action grant, Action propagate, Func<CancellationToken, Task<string?>> inspect, CancellationToken token) =>
        Core.Services.Runtime.WorkspaceSandboxAclRepair.RunAsync(grant, propagate, inspect, token);
}

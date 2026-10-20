using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>绑定可信 home 中当前工作区的 capability 和本机沙箱组，不执行 setup 或 provisioning。</para>
///     <para>
///         Binds the workspace capability from the trusted home and the local sandbox group without setup or
///         provisioning.
///     </para>
/// </summary>
internal sealed record WorkspaceSandboxAclContext(
    string SandboxHome,
    string SandboxGroupSid,
    string WorkspaceCapabilitySid)
{
    /// <summary>
    ///     <para>在用户确认后，以普通权限通过既有 SID 存储解析当前工作区；不写任何 ACL。</para>
    ///     <para>After consent, resolves the workspace through the existing SID store at normal privilege without ACL writes.</para>
    /// </summary>
    internal static async Task<WorkspaceSandboxAclContext> CaptureAsync(
        WindowsSandboxSettings settings, WorkspaceDirectoryIdentity identity, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows sandbox SIDs are required.");
        var components = new WindowsSandboxComponents
        {
            AllowTcpLoopback = settings.AllowTcpLoopback,
            SetupExecutablePath = settings.SetupExecutablePath, RunnerExecutablePath = settings.RunnerExecutablePath,
            SandboxHome = WindowsSandboxComponents.ResolveSandboxHome(settings.SandboxHome, settings.AllowTcpLoopback)
        };
        var status = await new WindowsSandboxStateInspector(components).InspectAsync(token).ConfigureAwait(false);
        if (!status.Ready)
            throw new InvalidOperationException("Directory repair requires a ready TinyHarness sandbox: " +
                                                string.Join("; ", status.Problems));
        if (!File.Exists(components.CapabilitySidPath))
            throw new InvalidDataException("Directory repair requires an existing provisioned sandbox SID table.");
        WorkspaceDirectoryIdentity.Capture(components.SandboxHome);
        if (new FileInfo(components.CapabilitySidPath).LinkTarget is not null)
            throw new InvalidDataException("The sandbox capability table may not be a link.");
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, identity.Path,
                                                   new Dictionary<string, string>());
        var capabilities = await new SandboxCapabilitySidStore(components)
                                .ResolveForPolicy(policy, identity.Path, token).ConfigureAwait(false);
        var group =
            (SecurityIdentifier)
            new NTAccount(Environment.MachineName, "TinyHarnessUsers").Translate(typeof(SecurityIdentifier));
        return new WorkspaceSandboxAclContext(components.SandboxHome, group.Value, capabilities.Single());
    }

    /// <summary>
    ///     <para>提权后再次核对本机组和可信表中的精确映射，拒绝授予请求方任意 SID。</para>
    ///     <para>Rechecks the local group and exact trusted mapping after elevation, rejecting arbitrary requested SIDs.</para>
    /// </summary>
    internal async Task ValidateAsync(WorkspaceDirectoryIdentity identity, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows sandbox SIDs are required.");
        WorkspaceDirectoryIdentity.Capture(SandboxHome);
        var path = Path.Combine(SandboxHome, "cap_sid");
        if (new FileInfo(path).LinkTarget is not null)
            throw new InvalidDataException("The sandbox SID table may not be a link.");
        var sids = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, token).ConfigureAwait(false),
                                              WorkspaceAclJsonContext.Default.SandboxCapabilitySids);
        var key = Path.GetFullPath(identity.Path).Replace('\\', '/').ToLowerInvariant();
        var group =
            (SecurityIdentifier)
            new NTAccount(Environment.MachineName, "TinyHarnessUsers").Translate(typeof(SecurityIdentifier));
        ValidateMapping(group.Value, sids?.WorkspaceByCwd?.GetValueOrDefault(key));
    }

    /// <summary>
    ///     <para>拒绝确认后发生变化或缺失的组与 capability 映射。</para>
    ///     <para>Rejects changed or missing group and capability mappings after consent.</para>
    /// </summary>
    internal void ValidateMapping(string actualGroup, string? actualCapability)
    {
        if (string.IsNullOrWhiteSpace(WorkspaceCapabilitySid) ||
            actualGroup      != SandboxGroupSid               ||
            actualCapability != WorkspaceCapabilitySid)
            throw new InvalidDataException("Sandbox SID mapping changed after directory consent.");
    }

    /// <summary>
    ///     <para>只读判断已有 workspace capability 和沙箱组授权，缺失时不创建 SID 表。</para>
    ///     <para>Reads existing workspace capability and sandbox-group grants without creating a SID table.</para>
    /// </summary>
    internal static async Task<bool> HasPreparedRootAsync(
        WindowsSandboxSettings settings, WorkspaceDirectoryIdentity identity, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var path =
            Path.Combine(WindowsSandboxComponents.ResolveSandboxHome(settings.SandboxHome, settings.AllowTcpLoopback),
                         "cap_sid");
        if (!File.Exists(path)) return false;
        var json = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
        var sids = JsonSerializer.Deserialize(json, WorkspaceAclJsonContext.Default.SandboxCapabilitySids);
        var key  = Path.GetFullPath(identity.Path).Replace('\\', '/').ToLowerInvariant();
        if (sids?.WorkspaceByCwd is null || !sids.WorkspaceByCwd.TryGetValue(key, out var capability)) return false;
        var group = (SecurityIdentifier)new NTAccount(Environment.MachineName, "TinyHarnessUsers")
           .Translate(typeof(SecurityIdentifier));
        var rules = new DirectoryInfo(identity.Path).GetAccessControl(AccessControlSections.Access)
                                                    .GetAccessRules(true, true, typeof(SecurityIdentifier))
                                                    .Cast<FileSystemAccessRule>().ToArray();
        foreach (var sid in new[] { group.Value, capability })
        {
            var found = false;
            foreach (var rule in rules)
                if (rule.IdentityReference.Value == sid && rule.AccessControlType == AccessControlType.Allow &&
                    (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify &&
                    rule.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
                    found = true;
            if (!found) return false;
        }

        return true;
    }
}

/// <summary>
///     <para>显式修复先合并沙箱根规则，再传播既有 DACL 并检查结果，不运行进程或重试。</para>
///     <para>
///         Explicit repair merges sandbox root rules, propagates the existing DACL and checks results without processes
///         or retries.
///     </para>
/// </summary>
internal static class WorkspaceSandboxAclRepair
{
    /// <summary>
    ///     <para>按顺序执行已确认的根授权和继承传播，检查失败直接返回，不重试。</para>
    ///     <para>Runs consented root grants and inheritance propagation in order, failing inspection without retries.</para>
    /// </summary>
    internal static async Task RunAsync(
        Action grant, Action propagate, Func<CancellationToken, Task<string?>> inspect, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        grant();
        token.ThrowIfCancellationRequested();
        // 根 ACE 已存在也必须重新传播，不能只判断根目录条目。
        propagate();
        var missing = await inspect(token).ConfigureAwait(false);
        if (missing is not null)
            throw new
                InvalidOperationException($"Workspace ACL inheritance is still incomplete at '{missing}'. No command was started.");
    }
}

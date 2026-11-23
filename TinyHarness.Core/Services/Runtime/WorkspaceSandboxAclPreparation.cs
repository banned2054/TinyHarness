using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime;

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
    ///     <para>
    ///         在用户确认后，以普通权限经既有 SID 存储解析当前工作区；就绪与表完整性检查随
    ///         get-or-create 与整表写回一起在按 home 键控的拒读刷新门内进行——门外读 cap_sid 会
    ///         撞上门内整表写回的中间态（共享冲突/半 JSON），把另一端正在进行的 refresh 误报成
    ///         目录修复所需的就绪失败。与 backend 执行流、另一端 host 解析及门内 helper 的表写
    ///         互斥；不写任何 ACL。
    ///     </para>
    ///     <para>
    ///         After consent, resolves the workspace through the existing SID store at normal
    ///         privilege; the readiness and table-integrity checks run inside the home-keyed
    ///         deny-read refresh gate together with the get-or-create plus whole-table write-back —
    ///         an out-of-gate cap_sid read would hit the in-gate write's intermediate state (a
    ///         sharing violation or torn JSON) and misreport the other side's in-flight refresh as
    ///         a readiness failure required for directory repair. Mutually exclusive with the
    ///         backend execution flow, the other host's resolution and the helper's in-gate table
    ///         writes; no ACL is written.
    ///     </para>
    /// </summary>
    internal static async Task<WorkspaceSandboxAclContext> CaptureAsync(
        WindowsSandboxSettings settings, WorkspaceDirectoryIdentity identity, CancellationToken token,
        ISandboxDenyReadRefreshGate? denyReadRefreshGate = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows sandbox SIDs are required.");
        var components = new WindowsSandboxComponents
        {
            AllowTcpLoopback = settings.AllowTcpLoopback,
            SetupExecutablePath = settings.SetupExecutablePath, RunnerExecutablePath = settings.RunnerExecutablePath,
            SandboxHome = WindowsSandboxComponents.ResolveSandboxHome(settings.SandboxHome, settings.AllowTcpLoopback)
        };
        // home 缺失时不可能有同 home 的门内写者，而门键控对缺失目录必抛（最终路径解析失败），
        // 先走既有无门 InspectAsync 路径，保住 "ready sandbox" 的失败语义。
        if (!Directory.Exists(components.SandboxHome))
        {
            var missingHomeStatus = await new WindowsSandboxStateInspector(components).InspectAsync(token)
                                             .ConfigureAwait(false);
            throw new InvalidOperationException("Directory repair requires a ready TinyHarness sandbox: " +
                                                string.Join("; ", missingHomeStatus.Problems));
        }

        WorkspaceDirectoryIdentity.Capture(components.SandboxHome);
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, identity.Path,
                                                   new Dictionary<string, string>());
        // 前置检查与 get-or-create、整表写回同入门内：该表与 backend 执行流、另一端 host 解析及
        // 门内 helper 的表写共享，门外读会共享冲突或读到半 JSON。此处无 helper 参与，Dispose
        // 直接归还；等待超时按既有边界异常 fail-closed，不写任何 ACL。
        ISandboxDenyReadRefreshGateHandle? gate = null;
        string capabilitySid;
        try
        {
            gate = await (denyReadRefreshGate ?? new GlobalDenyReadRefreshGate())
                        .TryAcquireAsync(components.SandboxHome, GlobalDenyReadRefreshGate.DefaultAcquireTimeout, token)
                        .ConfigureAwait(false)
                   ?? throw new
                       InvalidOperationException("Directory repair requires the deny-read refresh gate for sandbox home " +
                                                $"'{components.SandboxHome}', which was not acquired within " +
                                                $"{(int)GlobalDenyReadRefreshGate.DefaultAcquireTimeout.TotalSeconds}s; no capability " +
                                                "SID was resolved and no ACL was changed. Retry once the concurrent refresh completes.",
                                                new TimeoutException("The deny-read refresh gate acquisition timed out."));
            var status = await new WindowsSandboxStateInspector(components).InspectAsync(token).ConfigureAwait(false);
            if (!status.Ready)
                throw new InvalidOperationException("Directory repair requires a ready TinyHarness sandbox: " +
                                                    string.Join("; ", status.Problems));
            if (!File.Exists(components.CapabilitySidPath))
                throw new InvalidDataException("Directory repair requires an existing provisioned sandbox SID table.");
            if (new FileInfo(components.CapabilitySidPath).LinkTarget is not null)
                throw new InvalidDataException("The sandbox capability table may not be a link.");
            var capabilities = await new SandboxCapabilitySidStore(components)
                                    .ResolveForPolicy(policy, identity.Path, token).ConfigureAwait(false);
            capabilitySid = capabilities.Single();
        }
        finally
        {
            gate?.Dispose();
        }
        var group =
            (SecurityIdentifier)
            new NTAccount(Environment.MachineName, "TinyHarnessUsers").Translate(typeof(SecurityIdentifier));
        return new WorkspaceSandboxAclContext(components.SandboxHome, group.Value, capabilitySid);
    }

    /// <summary>
    ///     <para>
    ///         提权后再次核对本机组和可信表中的精确映射，拒绝授予请求方任意 SID。此入口不取跨进程门，
    ///         供提权 helper 进程（<c>ExecuteHelperAsync</c>）使用：跨完整性级别的互斥体访问权限
    ///         未经验证，helper 内不等待刷新门；撕裂读的后果是授权判定 Failed（fail-closed，可重试）。
    ///         宿主侧验证须使用带门重载。
    ///     </para>
    ///     <para>
    ///         Rechecks the local group and exact trusted mapping after elevation, rejecting arbitrary
    ///         requested SIDs. This entry takes no cross-process gate and serves the elevated helper
    ///         process (<c>ExecuteHelperAsync</c>): cross-integrity-level mutex access is unverified,
    ///         so the helper never waits on the refresh gate; a torn read fails the grant verdict
    ///         (fail-closed, retryable). Host-side validation must use the gated overload.
    ///     </para>
    /// </summary>
    internal Task ValidateAsync(WorkspaceDirectoryIdentity identity, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows sandbox SIDs are required.");
        WorkspaceDirectoryIdentity.Capture(SandboxHome);
        var path = Path.Combine(SandboxHome, "cap_sid");
        if (new FileInfo(path).LinkTarget is not null)
            throw new InvalidDataException("The sandbox SID table may not be a link.");
        return ValidateTableAsync(identity, path, token);
    }

    /// <summary>
    ///     <para>
    ///         宿主侧验证入口：表存在时在按 home 键控的拒读刷新门内完成单表读（方法内自取门），
    ///         避免门外读撞上门内整表写回的中间态——共享冲突/半 JSON 或陈旧映射会被误报成
    ///         "Sandbox SID mapping changed"。表缺失不拿门，保持既有读缺失异常语义；门等待超时
    ///         fail-closed 拒绝授予，撕裂读绝不作为授权依据。
    ///     </para>
    ///     <para>
    ///         Host-side validation entry: while the table exists, the single-table read completes
    ///         inside the home-keyed deny-read refresh gate (the gate is acquired within this
    ///         method), so an out-of-gate read can never hit the in-gate write's intermediate state —
    ///         a sharing violation, torn JSON, or stale mapping would otherwise be misreported as
    ///         "Sandbox SID mapping changed". A missing table skips the gate and keeps the existing
    ///         read-missing exception semantics; a gate-wait timeout fails closed and refuses the
    ///         grant, and a torn read is never used as authorization evidence.
    ///     </para>
    /// </summary>
    internal async Task ValidateAsync(WorkspaceDirectoryIdentity identity, CancellationToken token,
                                      ISandboxDenyReadRefreshGate denyReadRefreshGate)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows sandbox SIDs are required.");
        WorkspaceDirectoryIdentity.Capture(SandboxHome);
        var path = Path.Combine(SandboxHome, "cap_sid");
        if (new FileInfo(path).LinkTarget is not null)
            throw new InvalidDataException("The sandbox SID table may not be a link.");
        // 表存在才拿门：缺失时无门内写者可言，直接走既有读缺失异常；超时 fail-closed 拒绝授予。
        if (!File.Exists(path))
        {
            await ValidateTableAsync(identity, path, token).ConfigureAwait(false);
            return;
        }

        ISandboxDenyReadRefreshGateHandle? gate = null;
        try
        {
            gate = await denyReadRefreshGate
                        .TryAcquireAsync(SandboxHome, WindowsSandboxBackendLimits.Default.DenyReadRefreshGateTimeout,
                                         token).ConfigureAwait(false)
                   ?? throw new
                       InvalidOperationException("Directory validation requires the deny-read refresh gate for sandbox home " +
                                                $"'{SandboxHome}', which was not acquired within " +
                                                $"{(int)WindowsSandboxBackendLimits.Default.DenyReadRefreshGateTimeout.TotalSeconds}s; " +
                                                "the trusted mapping is never validated against a table read mid-write. " +
                                                "Retry once the concurrent refresh completes.",
                                                new TimeoutException("The deny-read refresh gate acquisition timed out."));
            await ValidateTableAsync(identity, path, token).ConfigureAwait(false);
        }
        finally
        {
            gate?.Dispose();
        }
    }

    /// <summary>
    ///     <para>读取并核对一张已定位的 SID 表；是否持门由调用方决定，本方法只做纯读与映射核对。</para>
    ///     <para>
    ///         Reads and validates one located SID table; whether the gate is held is the caller's
    ///         decision — this method only performs the pure read and mapping validation.
    ///     </para>
    /// </summary>
    private async Task ValidateTableAsync(WorkspaceDirectoryIdentity identity, string path, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows sandbox SIDs are required.");
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
    ///     <para>
    ///         只读判断已有 workspace capability 和沙箱组授权，缺失时不创建 SID 表。表存在时在
    ///         按 home 键控的拒读刷新门内读取（方法内自取门）：门外读会撞上门内整表写回的中间态
    ///         （共享冲突/半 JSON），把另一端正在进行的 refresh 误读成未准备或半就绪；表缺失不拿门，
    ///         直接判未准备；门等待超时 fail-closed 抛出，不以门外读结果作为就绪依据。
    ///     </para>
    ///     <para>
    ///         Reads existing workspace capability and sandbox-group grants without creating a SID
    ///         table. While the table exists, the read completes inside the home-keyed deny-read
    ///         refresh gate (the gate is acquired within this method): an out-of-gate read would hit
    ///         the in-gate write's intermediate state (a sharing violation or torn JSON) and
    ///         misread the other side's in-flight refresh as unprepared or half-prepared; a missing
    ///         table skips the gate and returns false directly, and a gate-wait timeout fails
    ///         closed by throwing instead of trusting an out-of-gate read.
    ///     </para>
    /// </summary>
    internal static async Task<bool> HasPreparedRootAsync(
        WindowsSandboxSettings settings, WorkspaceDirectoryIdentity identity, CancellationToken token,
        ISandboxDenyReadRefreshGate? denyReadRefreshGate = null)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var home = WindowsSandboxComponents.ResolveSandboxHome(settings.SandboxHome, settings.AllowTcpLoopback);
        var path = Path.Combine(home, "cap_sid");
        // 表缺失直接判未准备：无门内写者可言，不拿门。
        if (!File.Exists(path)) return false;
        ISandboxDenyReadRefreshGateHandle? gate = null;
        try
        {
            gate = await (denyReadRefreshGate ?? new GlobalDenyReadRefreshGate())
                        .TryAcquireAsync(home, WindowsSandboxBackendLimits.Default.DenyReadRefreshGateTimeout, token)
                        .ConfigureAwait(false)
                   ?? throw new
                       InvalidOperationException("Sandbox root readiness requires the deny-read refresh gate for sandbox home " +
                                                $"'{home}', which was not acquired within " +
                                                $"{(int)WindowsSandboxBackendLimits.Default.DenyReadRefreshGateTimeout.TotalSeconds}s; " +
                                                "readiness is never judged from a table read mid-write, so the check fails " +
                                                "closed instead of reporting a stale result. Retry once the concurrent refresh completes.",
                                                new TimeoutException("The deny-read refresh gate acquisition timed out."));
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
                        HasRequiredWriteFlags(rule))
                        found = true;
                if (!found) return false;
            }

            return true;
        }
        finally
        {
            gate?.Dispose();
        }
    }

    /// <summary>
    ///     <para>核对根及后代均可使用的完整 WRITE_ALLOW_MASK，拒绝仅继承或停止传播标志。</para>
    ///     <para>Checks the full WRITE_ALLOW_MASK for the root and descendants, rejecting inherit-only or stopped propagation.</para>
    /// </summary>
    internal static bool HasRequiredWriteFlags(FileSystemAccessRule rule)
    {
        // ACE 标志判定只在 Windows 上有意义；其他平台视为不满足，保持只读检查保守失败。
        if (!OperatingSystem.IsWindows()) return false;
        return (unchecked((uint)rule.FileSystemRights) & 0x1301BF) == 0x1301BF &&
               rule.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) &&
               rule.PropagationFlags == PropagationFlags.None;
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

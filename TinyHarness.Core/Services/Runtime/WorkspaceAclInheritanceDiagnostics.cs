#if DEBUG
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime;

/// <summary>
///     <para>
///         仅 Debug 构建的继承检查诊断：在失败点详记父子 DACL 快照与逐条缺口差异，
///         并只读汇总 <c>&lt;home&gt;\cap_sid</c> 中与写根相关的 capability SID。
///     </para>
///     <para>
///         Debug-only inheritance diagnostics: a detailed parent/child DACL snapshot with the
///         per-rule missing diff at the failure point, plus a read-only summary of the capability
///         SIDs that <c>&lt;home&gt;\cap_sid</c> maps for the write roots.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         只做只读访问且仅覆盖失败点的父子两个描述符，不做全树遍历输出；文件缺失或解析失败只记录
///         异常类型，不读取、不输出任何凭据或 payload 内容。
///     </para>
///     <para>
///         Read-only access covering only the failing parent/child descriptors, never a
///         whole-tree dump; missing files or parse failures log only the exception type, without
///         reading or emitting credentials or payload content.
///     </para>
/// </remarks>
internal static class WorkspaceAclInheritanceDiagnostics
{
    /// <summary>
    ///     <para>记录继承/管理检查失败点：父子保护标志、所有者、全部 ACE 与逐条缺口差异。</para>
    ///     <para>
    ///         Logs the inheritance or management failure point: parent/child protection flags,
    ///         owners, every ACE, and the per-rule missing diff.
    ///     </para>
    /// </summary>
    internal static void LogInspectionFailure(
        bool                                management,
        string                              parentDirectory,
        string                              path,
        bool                                isDirectory,
        IReadOnlyList<FileSystemAccessRule> parentRules,
        IReadOnlyList<FileSystemAccessRule> childRules)
    {
        if (!OperatingSystem.IsWindows()) return;
        var (parentOwner, parentProtected) = DescribeDescriptor(parentDirectory, true);
        var (childOwner, childProtected)   = DescribeDescriptor(path, isDirectory);
        SandboxDiagnosticLog.Event("acl-failure",
                                   $"mode={(management ? "management" : "inheritance")} parent='{parentDirectory}' child='{path}' " +
                                   $"childIsDirectory={isDirectory} parentProtected={parentProtected} childProtected={childProtected} " +
                                   $"parentOwner={parentOwner} childOwner={childOwner} parentAceCount={parentRules.Count} " +
                                   $"childAceCount={childRules.Count}");
        // probe 在宿主进程内执行，显式标注 probeSubject=host，避免与 helper 实际身份混淆。
        SandboxDiagnosticLog.Event("acl-probe",
                                   $"probeSubject=host parent='{parentDirectory}' parentProbe[{DescribeAclProbe(parentDirectory)}] " +
                                   $"parentAttributes={DescribeAttributes(parentDirectory)} " +
                                   $"child='{path}' childProbe[{DescribeAclProbe(path)}] childAttributes={DescribeAttributes(path)}");
        for (var index = 0; index < parentRules.Count; index++)
            SandboxDiagnosticLog.Event("acl-failure", $"parent-ace[{index}] {DescribeRule(parentRules[index])}");
        for (var index = 0; index < childRules.Count; index++)
            SandboxDiagnosticLog.Event("acl-failure", $"child-ace[{index}] {DescribeRule(childRules[index])}");
        SandboxDiagnosticLog.Event("acl-failure",
                                   management
                                       ? "missing=<management: the current account cannot manage this object>"
                                       : $"missing={DescribeUnsatisfiedParentRules(parentRules, childRules, isDirectory)}");
    }

    /// <summary>
    ///     <para>
    ///         只读探测 ACL 管理访问：普通权限能否打开 READ_CONTROL|WRITE_DAC 句柄及 Win32 错误码；
    ///         句柄只开即关，绝不写 ACL，成功时附带卷/索引身份与 reparse 标志。
    ///     </para>
    ///     <para>
    ///         Read-only probe of ACL-management access: whether normal privilege can open a
    ///         READ_CONTROL|WRITE_DAC handle and the Win32 error code; the handle is opened and closed
    ///         immediately without ever writing an ACL, and on success reports the volume/index identity
    ///         and the reparse flag.
    ///     </para>
    /// </summary>
    internal static string DescribeAclProbe(string path)
    {
        if (!OperatingSystem.IsWindows()) return "<non-windows>";
        var (opened, win32Error, identity, reparse) = WorkspaceDirectoryAcl.ProbeAclManagementAccess(path);
        return $"opened={opened} win32Error={win32Error} identity={identity} reparse={reparse}";
    }

    /// <summary>
    ///     <para>格式化路径当前属性（十六进制掩码）并显式给出 reparse 标志；取不到时记录占位符。</para>
    ///     <para>
    ///         Formats the path's current attributes as a hexadecimal mask with an explicit reparse flag; records a
    ///         placeholder when unavailable.
    ///     </para>
    /// </summary>
    internal static string DescribeAttributes(string path)
    {
        if (!OperatingSystem.IsWindows()) return "<non-windows>";
        try
        {
            var attributes = File.GetAttributes(path);
            return $"0x{unchecked((uint)attributes):X8}/reparse={attributes.HasFlag(FileAttributes.ReparsePoint)}";
        }
        catch (Exception ex)
        {
            return $"<unavailable:{ex.GetType().Name}>";
        }
    }

    /// <summary>
    ///     <para>捕获全部写根当前的只读 ACL 快照（保护标志、所有者、SDDL）；目录缺失或读取失败记录占位符。</para>
    ///     <para>
    ///         Captures the current read-only ACL snapshot (protection flag, owner, SDDL) of every write root;
    ///         missing directories or read failures record placeholders.
    ///     </para>
    /// </summary>
    internal static Dictionary<string, string> CaptureWriteRootAcls(SandboxSetupPayload payload)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in payload.WriteRoots) snapshot[root] = DescribeRootAcl(root);
        return snapshot;
    }

    /// <summary>
    ///     <para>逐根记录一次写根 ACL 快照；仅描述观察到的状态，不解释 helper 内部行为。</para>
    ///     <para>
    ///         Logs one ACL snapshot line per write root; it states only observed state and never interprets helper
    ///         internals.
    ///     </para>
    /// </summary>
    internal static void LogWriteRootAclSnapshot(string phase, IReadOnlyDictionary<string, string> snapshot)
    {
        foreach (var (root, acl) in snapshot)
            SandboxDiagnosticLog.Event("helper-acl", $"phase={phase} root='{root}' acl={acl}");
    }

    /// <summary>
    ///     <para>
    ///         helper 结束后再次读取写根 ACL 并与启动前快照比较；快照差异只是宿主侧可观察到的目录状态变化，
    ///         不代表 helper 内部 API 的返回或决策。
    ///     </para>
    ///     <para>
    ///         Re-reads the write-root ACLs after the helper finishes and compares against the pre-launch snapshot;
    ///         a snapshot difference is only the host-side observable directory state change, not the helper's
    ///         internal API return or decision.
    ///     </para>
    /// </summary>
    internal static void LogWriteRootAclComparison(
        string phase, IReadOnlyDictionary<string, string> before, SandboxSetupPayload payload)
    {
        foreach (var root in payload.WriteRoots)
        {
            var previous = before.TryGetValue(root, out var value) ? value : "<not-captured>";
            var current  = DescribeRootAcl(root);
            var changed  = !string.Equals(previous, current, StringComparison.Ordinal);
            SandboxDiagnosticLog.Event("helper-acl",
                                       changed
                                           ? $"phase={phase} root='{root}' changed=True before={previous} after={current}"
                                           : $"phase={phase} root='{root}' changed=False");
        }
    }

    private static string DescribeRootAcl(string root)
    {
        if (!OperatingSystem.IsWindows()) return "<non-windows>";
        try
        {
            if (!Directory.Exists(root)) return "<missing>";
            var security = new DirectoryInfo(root).GetAccessControl(AccessControlSections.Owner |
                                                                    AccessControlSections.Access);
            var owner = security.GetOwner(typeof(SecurityIdentifier))?.ToString();
            return $"protected={security.AreAccessRulesProtected} owner={owner ?? "<null>"} " +
                   $"sddl={security.GetSecurityDescriptorSddlForm(AccessControlSections.Access)}";
        }
        catch (Exception ex)
        {
            return $"<unavailable:{ex.GetType().Name}>";
        }
    }

    /// <summary>
    ///     <para>只读汇总失败写根与 capability SID 表的映射；不创建文件，解析失败只记异常类型。</para>
    ///     <para>
    ///         Read-only summary of the failing write root against the capability-SID table;
    ///         creates no file and logs only the exception type on parse failures.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>读取遵守传入 token；产品取消原样抛出，不得被诊断捕获吞掉。</para>
    ///     <para>The read honors the given token; product cancellation propagates instead of being swallowed by diagnostics.</para>
    /// </remarks>
    internal static async Task LogCapabilityContextAsync(
        SandboxSetupPayload payload, string root, string missing, CancellationToken cancellationToken)
    {
        SandboxDiagnosticLog.Event("acl-capability",
                                   $"root='{root}' missing='{missing}' home='{payload.SandboxHome}' " +
                                   $"network={(payload.AllowTcpLoopback ? "tcp-loopback" : "restricted")} version={payload.Version}");
        var capabilitySidPath = Path.Combine(payload.SandboxHome, "cap_sid");
        if (!File.Exists(capabilitySidPath))
        {
            SandboxDiagnosticLog.Event("acl-capability", $"cap-sid=missing path='{capabilitySidPath}'");
            return;
        }

        try
        {
            var content = await File.ReadAllTextAsync(capabilitySidPath, cancellationToken).ConfigureAwait(false);
            var table   = JsonSerializer.Deserialize(content, WindowsSandboxJsonContext.Default.SandboxCapabilitySids);
            var byCwd   = table?.WorkspaceByCwd     ?? [];
            var byRoot  = table?.WritableRootByPath ?? [];
            SandboxDiagnosticLog.Event("acl-capability",
                                       $"workspace={table?.Workspace ?? "<null>"} readonly={table?.ReadOnly ?? "<null>"} " +
                                       $"cwdEntries={byCwd.Count} writableEntries={byRoot.Count}");
            foreach (var writeRoot in payload.WriteRoots)
            {
                var key     = SandboxCapabilitySidStore.CanonicalRootKey(writeRoot);
                var cwdSid  = byCwd.TryGetValue(key, out var byCwdSid) ? byCwdSid : null;
                var rootSid = byRoot.TryGetValue(key, out var byRootSid) ? byRootSid : null;
                SandboxDiagnosticLog.Event("acl-capability",
                                           $"writeRoot='{writeRoot}' key='{key}' cwdSid={cwdSid ?? "<none>"} writableSid={rootSid ?? "<none>"}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SandboxDiagnosticLog.Event("acl-capability", $"cap-sid=read-failed type={ex.GetType().Name}");
        }
    }

    /// <summary>
    ///     <para>把单个 ACE 格式化为含 SID、十六进制掩码、继承/传播标志的单行描述。</para>
    ///     <para>Formats one ACE as a single line with its SID, hexadecimal mask, and inheritance/propagation flags.</para>
    /// </summary>
    internal static string DescribeRule(FileSystemAccessRule rule)
    {
        if (!OperatingSystem.IsWindows()) return "<non-windows>";
        return
            $"{rule.AccessControlType} sid={rule.IdentityReference} mask=0x{unchecked((uint)rule.FileSystemRights):X8} " +
            $"inherited={rule.IsInherited} inheritance={rule.InheritanceFlags} propagation={rule.PropagationFlags}";
    }

    /// <summary>
    ///     <para>列出父目录中要求传播却未被子项满足的 allow ACE，附 required/effective/containers/objects 掩码。</para>
    ///     <para>
    ///         Lists the parent allow ACEs that demand propagation but are unsatisfied on the child,
    ///         with their required/effective/containers/objects masks.
    ///     </para>
    /// </summary>
    internal static string DescribeUnsatisfiedParentRules(
        IReadOnlyList<FileSystemAccessRule> parent, IReadOnlyList<FileSystemAccessRule> child, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows()) return "<non-windows>";
        var missing = new List<string>();
        foreach (var rule in parent)
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (!WorkspaceAclInheritance.IsInheritableToChild(rule, isDirectory)) continue;
            if (WorkspaceAclInheritance.IsSatisfiedOnChild(rule, child, isDirectory,
                                                           out var required, out var effective, out var containers,
                                                           out var objects)) continue;
            missing.Add($"sid={rule.IdentityReference} required=0x{required:X8} effective=0x{effective:X8} " +
                        $"containers=0x{containers:X8} objects=0x{objects:X8} "                              +
                        $"parentInheritance={rule.InheritanceFlags} parentPropagation={rule.PropagationFlags}");
        }

        return missing.Count == 0 ? "<none>" : string.Join(" ; ", missing);
    }

    private static (string Owner, bool Protected) DescribeDescriptor(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows()) return ("<non-windows>", false);
        try
        {
            FileSystemSecurity security = isDirectory
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner      | AccessControlSections.Access);
            var owner = security.GetOwner(typeof(SecurityIdentifier));
            return (owner?.ToString() ?? "<null>", security.AreAccessRulesProtected);
        }
        catch (Exception ex)
        {
            return ($"<unavailable:{ex.GetType().Name}>", false);
        }
    }
}
#endif

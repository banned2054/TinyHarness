using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Principal;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime;

/// <summary>
///     <para>只读检查既有子项的可继承 allow ACE；保留独立 DACL、deny 和元数据边界。</para>
///     <para>
///         Inspects inheritable allow ACEs on existing children without altering independent DACLs, denies or metadata
///         boundaries.
///     </para>
/// </summary>
public static class WorkspaceAclInheritance
{
    private const int MaximumEntries = 200_000;

    /// <summary>
    ///     <para>普通 refresh 后检查实际写根的继承，失败时以结构化问题在 runner 启动之前阻断。</para>
    ///     <para>
    ///         Checks inheritance on actual write roots after normal refresh,
    ///         blocking before runner launch with a structured problem on failure.
    ///     </para>
    /// </summary>
    public static async Task ValidateAsync(SandboxSetupPayload payload, CancellationToken cancellationToken)
    {
        foreach (var root in payload.WriteRoots)
        {
            if (!Directory.Exists(root)) continue;
            var problem = await InspectAsync(WorkspaceDirectoryIdentity.Capture(root), cancellationToken)
               .ConfigureAwait(false);
            if (problem is null) continue;
#if DEBUG
            await WorkspaceAclInheritanceDiagnostics
                 .LogCapabilityContextAsync(payload, root, problem.Path, cancellationToken).ConfigureAwait(false);
#endif
            throw new WorkspaceAclPreparationException(problem);
        }
    }

    /// <summary>
    ///     <para>只读检查工作区树的继承一致性，返回首个结构化问题；null 只表示一致，不证明有效写权限。</para>
    ///     <para>
    ///         Read-only inspection of the workspace tree's inheritance consistency, returning the first structured
    ///         problem; null establishes consistency, not effective write access.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         检查错误（读取 DACL 被拒或出错、条目超限、目录身份变化等）转换为
    ///         <see cref="WorkspaceAclProblemCategory.InspectionFailed" /> 的问题返回；取消令牌原样抛出。
    ///     </para>
    ///     <para>
    ///         Inspection errors (denied or failed DACL reads, entry limits, identity changes) return as an
    ///         <see cref="WorkspaceAclProblemCategory.InspectionFailed" /> problem; cancellation propagates instead.
    ///     </para>
    /// </remarks>
    public static Task<WorkspaceAclProblem?> InspectAsync(
        WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken) =>
        Task.Run(() => InspectProblem(identity, false, cancellationToken), cancellationToken);

    /// <summary>
    ///     <para>返回第一个缺少父目录继承权限的路径；null 只表示继承一致，不证明有效写权限。</para>
    ///     <para>
    ///         Returns the first path missing inherited parent permissions; null establishes consistency, not effective
    ///         write access.
    ///     </para>
    /// </summary>
    public static Task<string?> FindMissingAsync(
        WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken) =>
        Task.Run(() => Inspect(identity, false, cancellationToken), cancellationToken);

    /// <summary>
    ///     <para>检查普通 helper 能否管理需要传播的既有对象，不申请提权或写入权限。</para>
    ///     <para>
    ///         Checks whether the normal helper can manage existing propagation targets without requesting elevation or
    ///         changing permissions.
    ///     </para>
    /// </summary>
    public static async Task<bool> CanManageAsync(
        WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken) =>
        await Task.Run(() => Inspect(identity, true, cancellationToken), cancellationToken)
                  .ConfigureAwait(false) is null;

    /// <summary>
    ///     <para>只读确认授权树中不存在重解析点；不得通过链接扩展提权修改范围。</para>
    ///     <para>Checks that the grant tree contains no reparse points so elevation cannot expand its scope through links.</para>
    /// </summary>
    public static Task ValidateRepairTreeAsync(
        WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            identity.EnsureUnchanged();
            var count   = 0;
            var pending = new Stack<string>();
            pending.Push(identity.Path);
            while (pending.TryPop(out var directory))
                foreach (var child in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CheckLimit(++count);
                    var attributes = File.GetAttributes(child);
                    RequireNoLink(child, attributes);
                    var metadata = SandboxIsolationPolicy.ProtectedMetadataSubpaths.Any(name =>
                        Workspace.IsInside(Path.Combine(identity.Path, name), child));
                    WorkspaceDirectoryAcl.ValidateRepairObject(child, !metadata);
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
                }

            identity.EnsureUnchanged();
        }, cancellationToken);

    // 兼容旧语义：错误原样抛出，只把失败点降级为路径字符串。
    private static string? Inspect(WorkspaceDirectoryIdentity identity, bool management, CancellationToken token) =>
        Walk(identity, management, token)?.Path;

    /// <summary>
    ///     <para>执行检查并把检查期错误转换为结构化问题；取消令牌原样抛出。</para>
    ///     <para>Runs the inspection and converts inspection-time errors into a structured problem; cancellation propagates.</para>
    /// </summary>
    private static WorkspaceAclProblem? InspectProblem(
        WorkspaceDirectoryIdentity identity, bool management, CancellationToken token)
    {
        try
        {
            return Walk(identity, management, token);
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or IOException
                                      or InvalidDataException)
        {
            var (canManage, probeError) = ProbeManagement(identity.Path);
            return new WorkspaceAclProblem(identity.Path, identity.Path, WorkspaceAclProblemCategory.InspectionFailed,
                                           [], canManage, Win32Code(ex) ?? probeError,
                                           $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static WorkspaceAclProblem? Walk(WorkspaceDirectoryIdentity identity, bool management,
                                             CancellationToken          token)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows ACL inspection is required.");
        identity.EnsureUnchanged();
        var pending = new Stack<string>();
        pending.Push(identity.Path);
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            var parent      = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access);
            var parentRules = Rules(parent);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                CheckLimit(++count);
                var attributes = File.GetAttributes(path);
                // 只读检查不跟随链接；提权修复会另行拒绝含链接的树。
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                // 元数据 deny 是独立策略，不能据普通写根权限要求将其放宽。
                if (directory == identity.Path && SandboxIsolationPolicy.ProtectedMetadataSubpaths.Contains(
                     Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)) continue;
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                FileSystemSecurity child = isDirectory
                    ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                    : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
                if (child.AreAccessRulesProtected) continue;
                var childRules = Rules(child);
                if (management
                        ? !WorkspaceDirectoryAcl.CanChangeObjectPermissions(path)
                        : HasMissingAllow(parentRules, childRules, isDirectory))
                {
#if DEBUG
                    // 仅在失败点详记父子快照与差异；正常路径不产生逐 ACE 输出。
                    WorkspaceAclInheritanceDiagnostics
                       .LogInspectionFailure(management, directory, path, isDirectory, parentRules, childRules);
#endif
                    return ProblemAt(identity, management, path, isDirectory, parentRules, childRules);
                }

                if (isDirectory) pending.Push(path);
            }
        }

        identity.EnsureUnchanged();
        return null;
    }

    // 失败点补一次只读管理探测：缺少继承权限与管理拒绝可并存于同一问题。
    private static WorkspaceAclProblem ProblemAt(
        WorkspaceDirectoryIdentity          identity,    bool management, string path, bool isDirectory,
        IReadOnlyList<FileSystemAccessRule> parentRules, IReadOnlyList<FileSystemAccessRule> childRules)
    {
        var (canManage, win32Error) = ProbeManagement(path);
        return management
            ? new WorkspaceAclProblem(identity.Path, path, WorkspaceAclProblemCategory.ManagementDenied,
                                      [], canManage, win32Error ?? 5)
            : new WorkspaceAclProblem(identity.Path, path, WorkspaceAclProblemCategory.MissingInheritedAccess,
                                      MissingAces(parentRules, childRules, isDirectory), canManage, win32Error);
    }

    // 只读探测 ACL 管理句柄：opened 才可管理，错误 5 表示被拒，其余无法判定。
    private static (bool? CanManage, int? Win32Error) ProbeManagement(string path)
    {
        var (opened, error, _, _) = WorkspaceDirectoryAcl.ProbeAclManagementAccess(path);
        return (opened ? true : error == 5 ? false : null, opened ? null : error);
    }

    private static int? Win32Code(Exception ex) => ex switch
    {
        Win32Exception win32        => win32.NativeErrorCode,
        UnauthorizedAccessException => 5,
        _                           => null
    };

    private static FileSystemAccessRule[] Rules(FileSystemSecurity security)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows ACL inspection is required.");
        return security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
    }

    /// <summary>
    ///     <para>核对父 allow 权限及继续传播标志；不把 deny 当作 allow，也不判定最终有效权限。</para>
    ///     <para>
    ///         Checks parent allow permissions and propagation flags without treating denies as grants or claiming effective
    ///         access.
    ///     </para>
    /// </summary>
    internal static bool HasMissingAllow(
        IReadOnlyList<FileSystemAccessRule> parent, IReadOnlyList<FileSystemAccessRule> child, bool directory)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows ACL inspection is required.");
        foreach (var rule in parent)
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (!IsInheritableToChild(rule, directory)) continue;
            if (!IsSatisfiedOnChild(rule, child, directory, out _, out _, out _, out _)) return true;
        }

        return false;
    }

    /// <summary>
    ///     <para>列出父目录中要求传播却未被子项满足的 allow ACE 及其所需/有效/传播掩码。</para>
    ///     <para>
    ///         Lists the parent allow ACEs that demand propagation but are unsatisfied on the child,
    ///         with their required/effective/propagation masks.
    ///     </para>
    /// </summary>
    internal static IReadOnlyList<WorkspaceAclMissingAce> MissingAces(
        IReadOnlyList<FileSystemAccessRule> parent, IReadOnlyList<FileSystemAccessRule> child, bool directory)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows ACL inspection is required.");
        var missing = new List<WorkspaceAclMissingAce>();
        foreach (var rule in parent)
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (!IsInheritableToChild(rule, directory)) continue;
            if (IsSatisfiedOnChild(rule, child, directory,
                                   out var required, out var effective, out var containers, out var objects)) continue;
            missing.Add(new WorkspaceAclMissingAce(rule.IdentityReference.Value, required, effective, containers,
                                                   objects));
        }

        return missing;
    }

    /// <summary>
    ///     <para>判断父 ACE 按其继承标志是否应作用于该类子项。</para>
    ///     <para>Whether a parent ACE's inheritance flags apply to this kind of child.</para>
    /// </summary>
    internal static bool IsInheritableToChild(FileSystemAccessRule rule, bool directory)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows ACL inspection is required.");
        return directory
            ? rule.InheritanceFlags != InheritanceFlags.None &&
              !(rule.InheritanceFlags == InheritanceFlags.ObjectInherit &&
                rule.PropagationFlags.HasFlag(PropagationFlags.NoPropagateInherit))
            : rule.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit);
    }

    /// <summary>
    ///     <para>计算子项对该父 ACE 的有效掩码并核对缺口；out 掩码供诊断差异复用。</para>
    ///     <para>
    ///         Accumulates the child's effective masks for the parent ACE and checks the gap;
    ///         the out masks feed the diagnostic diff.
    ///     </para>
    /// </summary>
    internal static bool IsSatisfiedOnChild(
        FileSystemAccessRule rule,     IReadOnlyList<FileSystemAccessRule> child, bool directory,
        out uint             required, out uint effective, out uint containers, out uint objects)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows ACL inspection is required.");
        effective  = 0;
        containers = 0;
        objects    = 0;
        foreach (var candidate in child)
            if (candidate.AccessControlType == AccessControlType.Allow &&
                candidate.IdentityReference == rule.IdentityReference)
            {
                var mask = Expand(candidate.FileSystemRights);
                if (!candidate.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) effective |= mask;
                if (candidate.PropagationFlags.HasFlag(PropagationFlags.NoPropagateInherit)) continue;
                if (candidate.InheritanceFlags.HasFlag(InheritanceFlags.ContainerInherit)) containers |= mask;
                if (candidate.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit)) objects       |= mask;
            }

        required = Expand(rule.FileSystemRights);
        if ((!directory || rule.InheritanceFlags.HasFlag(InheritanceFlags.ContainerInherit)) &&
            (effective & required) != required) return false;
        if (!directory || rule.PropagationFlags.HasFlag(PropagationFlags.NoPropagateInherit)) return true;
        if (rule.InheritanceFlags.HasFlag(InheritanceFlags.ContainerInherit) &&
            (containers & required) != required) return false;
        if (rule.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit) &&
            (objects & required) != required) return false;
        return true;
    }

    // 父目录的 generic ACE 在子项上可能被 Windows 展开为文件权限位。
    private static uint Expand(FileSystemRights rights)
    {
        var mask     = unchecked((uint)rights);
        var expanded = mask & 0x0FFFFFFF;

        if ((mask & 0x80000000) != 0) expanded |= 0x120089;
        if ((mask & 0x40000000) != 0) expanded |= 0x120116;
        if ((mask & 0x20000000) != 0) expanded |= 0x1200A0;
        if ((mask & 0x10000000) != 0) expanded |= 0x1F01FF;
        return expanded;
    }

    private static void RequireNoLink(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"ACL preparation may not follow reparse points: {path}");
    }

    private static void CheckLimit(int count)
    {
        if (count > MaximumEntries)
            throw new
                InvalidDataException($"Workspace ACL inspection exceeds {MaximumEntries} entries; use a narrower workspace.");
    }
}

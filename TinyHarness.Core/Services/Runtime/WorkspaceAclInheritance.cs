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
    ///     <para>普通 refresh 后检查实际写根的继承，失败时在 runner 启动之前阻断。</para>
    ///     <para>Checks inheritance on actual write roots after normal refresh, blocking before runner launch on failure.</para>
    /// </summary>
    public static async Task ValidateAsync(SandboxSetupPayload payload, CancellationToken cancellationToken)
    {
        foreach (var root in payload.WriteRoots)
        {
            if (!Directory.Exists(root)) continue;
            var missing = await FindMissingAsync(WorkspaceDirectoryIdentity.Capture(root), cancellationToken)
               .ConfigureAwait(false);
            if (missing is not null)
                throw new
                    InvalidOperationException($"Sandbox ACL inheritance is incomplete at '{missing}' under write root '{root}'. " +
                                              "The setup helper returned success but existing children lack inherited allow ACEs. " +
                                              "Use the host /trust directory-authorization flow to repair inheritance; no command was started.");
        }
    }

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

    private static string? Inspect(WorkspaceDirectoryIdentity identity, bool management, CancellationToken token)
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
                if (management
                        ? !WorkspaceDirectoryAcl.CanChangeObjectPermissions(path)
                        : HasMissingAllow(parentRules, Rules(child), isDirectory)) return path;
                if (isDirectory) pending.Push(path);
            }
        }

        identity.EnsureUnchanged();
        return null;
    }

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
            var inherited = directory
                ? rule.InheritanceFlags != InheritanceFlags.None &&
                  !(rule.InheritanceFlags == InheritanceFlags.ObjectInherit &&
                    rule.PropagationFlags.HasFlag(PropagationFlags.NoPropagateInherit))
                : rule.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit);
            if (!inherited) continue;
            uint effective = 0, containers = 0, objects = 0;
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

            var required = Expand(rule.FileSystemRights);
            if ((!directory || rule.InheritanceFlags.HasFlag(InheritanceFlags.ContainerInherit)) &&
                (effective & required) != required) return true;
            if (!directory || rule.PropagationFlags.HasFlag(PropagationFlags.NoPropagateInherit)) continue;
            if (rule.InheritanceFlags.HasFlag(InheritanceFlags.ContainerInherit) &&
                (containers & required) != required) return true;
            if (rule.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit) &&
                (objects & required) != required) return true;
        }

        return false;
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

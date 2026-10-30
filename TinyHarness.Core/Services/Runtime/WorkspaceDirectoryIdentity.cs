using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.AccessControl;

namespace TinyHarness.Core.Services.Runtime;

/// <summary>
///     <para>绑定实际目录身份；拒绝路径祖先中的链接，Windows 同时绑定卷及文件标识。</para>
///     <para>Binds a directory identity; rejects ancestor links and binds Windows volume and file identifiers.</para>
/// </summary>
public sealed record WorkspaceDirectoryIdentity(string Path, string FileIdentity)
{
    /// <summary>
    ///     <para>只读捕获已存在的绝对工作区，不创建目录或修改权限。</para>
    ///     <para>Captures an existing absolute workspace without creating directories or changing permissions.</para>
    /// </summary>
    public static WorkspaceDirectoryIdentity Capture(string path)
    {
        if (!System.IO.Path.IsPathFullyQualified(path))
            throw new InvalidDataException("The workspace must be an absolute directory path.");
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Workspace not found: {full}");
        if (OperatingSystem.IsWindows())
        {
            using var opened = WorkspaceDirectoryAcl.Open(full, 0);
            return opened.Identity;
        }

        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Workspace paths may not contain links: {current.FullName}");
        return new WorkspaceDirectoryIdentity(full, full);
    }

    /// <summary>
    ///     <para>拒绝目录被替换或链接解析目标发生变化。</para>
    ///     <para>Rejects a replaced directory or changed link target.</para>
    /// </summary>
    public void EnsureUnchanged()
    {
        if (Capture(Path) != this)
            throw new InvalidDataException("The workspace directory identity changed; reopen and confirm trust again.");
    }
}

/// <summary>
///     <para>检查或补授仅工作区根目录的 DACL 管理权，保留所有者及既有 ACL 条目。</para>
///     <para>Checks or grants directory-only DACL management while preserving ownership and existing ACL entries.</para>
/// </summary>
public static partial class WorkspaceDirectoryAcl
{
    private const uint ReadControl = 0x20000;
    private const uint WriteDac    = 0x40000;

    /// <summary>
    ///     <para>固定已确认目录及祖先的身份，调用者负责释放；不修改 ACL。</para>
    ///     <para>Pins the confirmed directory and its ancestors without changing ACLs; the caller disposes the lease.</para>
    /// </summary>
    public static IDisposable Pin(WorkspaceDirectoryIdentity identity)
    {
        var opened = Open(identity.Path, 0);
        try
        {
            RequireIdentity(identity, opened.Identity);
            return opened;
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     <para>重新提交根目录的既有 DACL，触发 Windows 向未保护子项传播继承项，不改变所有者。</para>
    ///     <para>Resubmits the existing root DACL to propagate inheritance to unprotected children without changing ownership.</para>
    /// </summary>
    public static void ReapplyInheritance(WorkspaceDirectoryIdentity identity)
    {
        using var opened = Open(identity.Path, ReadControl | WriteDac);
        RequireIdentity(identity, opened.Identity);
        var descriptor = IntPtr.Zero;
        try
        {
            Check(GetSecurityInfo(opened.Handle, 1, 4, out _, out _, out var dacl, out _, out descriptor),
                  "read workspace DACL before inheritance repair");
            if (dacl == IntPtr.Zero) return;
            Check(SetSecurityInfo(opened.Handle, 1, 4, IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero),
                  "propagate workspace DACL inheritance");
        }
        finally
        {
            if (descriptor != IntPtr.Zero) LocalFree(descriptor);
        }
    }

    /// <summary>
    ///     <para>仅向工作区根合并沙箱写入组和 capability 的可继承 Modify ACE，不覆盖既有 deny。</para>
    ///     <para>Merges inheritable sandbox-group and capability Modify ACEs on the root without overriding existing denies.</para>
    /// </summary>
    public static void GrantSandboxWritePermissions(
        WorkspaceDirectoryIdentity identity, IReadOnlyList<string> accountSids)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows ACL preparation is required.");
        using var opened = Open(identity.Path, ReadControl | WriteDac);
        RequireIdentity(identity, opened.Identity);
        var security   = new DirectoryInfo(identity.Path).GetAccessControl(AccessControlSections.Access);
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is { } entries)
            foreach (var ace in entries)
                if (ace is QualifiedAce { AceQualifier: AceQualifier.AccessDenied } denied &&
                    (unchecked((uint)denied.AccessMask) & 0xF01701BF) != 0)
                    throw new
                        InvalidOperationException("Workspace root contains a deny ACE; refusing to broaden sandbox access.");
        IntPtr nativeDescriptor = IntPtr.Zero, mergedAcl = IntPtr.Zero;
        try
        {
            Check(GetSecurityInfo(opened.Handle, 1, 4, out _, out _, out var original, out _, out nativeDescriptor),
                  "read workspace DACL");
            if (original == IntPtr.Zero) return;
            var current = original;
            foreach (var accountSid in accountSids)
            {
                var sid = new SecurityIdentifier(accountSid);
                if (!sid.IsAccountSid())
                    throw new InvalidDataException("A sandbox account or capability SID is required.");
                var bytes = new byte[sid.BinaryLength];
                sid.GetBinaryForm(bytes, 0);
                var pointer = Marshal.AllocHGlobal(bytes.Length);
                try
                {
                    Marshal.Copy(bytes, 0, pointer, bytes.Length);
                    // 与 Rust WRITE_ALLOW_MASK 一致，不包含 WRITE_DAC 或 FILE_DELETE_CHILD。
                    var entry = new ExplicitAccess
                    {
                        Permissions = 0x1301BF, Mode = 1, Inheritance = 3,
                        Trustee     = new Trustee { Form = 0, Type = 0, Name = pointer }
                    };
                    Check(SetEntriesInAclW(1, in entry, current, out var next), "merge sandbox write grant");
                    if (mergedAcl != IntPtr.Zero) LocalFree(mergedAcl);
                    current = mergedAcl = next;
                }
                finally
                {
                    Marshal.FreeHGlobal(pointer);
                }
            }

            Check(SetSecurityInfo(opened.Handle, 1, 4, IntPtr.Zero, IntPtr.Zero, current, IntPtr.Zero),
                  "apply sandbox write grants");
        }
        finally
        {
            if (mergedAcl        != IntPtr.Zero) LocalFree(mergedAcl);
            if (nativeDescriptor != IntPtr.Zero) LocalFree(nativeDescriptor);
        }
    }

    internal static bool CanChangeObjectPermissions(string path)
    {
        using var handle = CreateFileW(path, ReadControl | WriteDac, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            return error == 5
                ? false
                : throw new Win32Exception(error, $"Cannot inspect ACL-management access for '{path}'.");
        }

        if (!GetFileInformationByHandle(handle, out var information))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot inspect '{path}'.");
        return (information.Attributes & 0x400) != 0
            ? throw new InvalidDataException($"ACL preparation may not follow reparse points: {path}")
            : true;
    }

    /// <summary>
    ///     <para>
    ///         以 READ_CONTROL|WRITE_DAC 打开既有路径并立即关闭，只读探测普通权限能否取得 ACL 管理句柄；
    ///         绝不写入 ACL，也不持有长句柄。
    ///     </para>
    ///     <para>
    ///         Opens an existing path with READ_CONTROL|WRITE_DAC and closes it immediately, probing read-only
    ///         whether normal privilege can obtain an ACL-management handle; it never writes an ACL and never
    ///         holds a long-lived handle.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         与 <see cref="CanChangeObjectPermissions" /> 相同的打开参数（不共享删除、OPEN_EXISTING、
    ///         BACKUP_SEMANTICS|OPEN_REPARSE_POINT，不跨越 reparse point）；生产结构化问题与 Debug 诊断共用，
    ///         失败只返回 Win32 错误码，不抛出。
    ///     </para>
    ///     <para>
    ///         The same open parameters as <see cref="CanChangeObjectPermissions" /> (no delete sharing,
    ///         OPEN_EXISTING, BACKUP_SEMANTICS|OPEN_REPARSE_POINT, never crossing a reparse point);
    ///         shared by production structured problems and Debug diagnostics, returning the
    ///         Win32 error code on failure instead of throwing.
    ///     </para>
    /// </remarks>
    internal static (bool Opened, int Win32Error, string FileIdentity, bool ReparsePoint)
        ProbeAclManagementAccess(string path)
    {
        using var handle = CreateFileW(path, ReadControl | WriteDac, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) return (false, Marshal.GetLastPInvokeError(), "<unavailable>", false);
        if (!GetFileInformationByHandle(handle, out var information))
            return (true, Marshal.GetLastPInvokeError(), "<unavailable>", false);

        var reparse = (information.Attributes & 0x400) != 0;
        return (true, 0, $"{information.Volume:X8}:{information.IndexHigh:X8}{information.IndexLow:X8}", reparse);
    }

    internal static void ValidateRepairObject(string path, bool checkInheritedDenies)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows ACL preparation is required.");
        using var handle = CreateFileW(path, ReadControl, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot validate ACL repair target '{path}'.");
        if ((information.Attributes & 0x400) != 0 || ((information.Attributes & 0x10) == 0 && information.Links > 1))
            throw new InvalidDataException($"ACL repair refuses reparse points or hard-linked files: {path}");
        if (!checkInheritedDenies) return;
        FileSystemSecurity security = (information.Attributes & 0x10) != 0
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        if (security.AreAccessRulesProtected) return;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.IsInherited && rule.AccessControlType            == AccessControlType.Deny &&
                (unchecked((uint)rule.FileSystemRights) & 0xF01701BF) != 0)
                throw new InvalidOperationException($"ACL repair refuses to outrank an inherited deny at '{path}'.");
    }

    /// <summary>
    ///     <para>仅尝试打开 ACL 管理句柄；权限不足返回 false，其他错误保留上下文。</para>
    ///     <para>Only opens an ACL-management handle; returns false on access denial and preserves other errors.</para>
    /// </summary>
    public static bool CanChangePermissions(WorkspaceDirectoryIdentity identity)
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            using var opened = Open(identity.Path, ReadControl | WriteDac);
            RequireIdentity(identity, opened.Identity);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            identity.EnsureUnchanged();
            return false;
        }
    }

    /// <summary>
    ///     <para>给固定账户 SID 补授仅本目录的 WRITE_DAC；调用者须已有 ACL 管理权。</para>
    ///     <para>Grants a fixed account SID directory-only WRITE_DAC; the caller must already manage its ACL.</para>
    /// </summary>
    public static void GrantChangePermissions(WorkspaceDirectoryIdentity identity, string accountSid)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Workspace ACL grants require Windows.");
        ValidateGrantTarget(identity.Path);
        using var opened = Open(identity.Path, ReadControl | WriteDac);
        RequireIdentity(identity, opened.Identity);
        var sid = new SecurityIdentifier(accountSid);
        if (!sid.IsAccountSid()) throw new InvalidDataException("An account SID is required.");
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        var    sidPointer = Marshal.AllocHGlobal(bytes.Length);
        IntPtr descriptor = IntPtr.Zero, newAcl = IntPtr.Zero;
        try
        {
            Marshal.Copy(bytes, 0, sidPointer, bytes.Length);
            Check(GetSecurityInfo(opened.Handle, 1, 4, out _, out _, out var oldAcl, out _, out descriptor),
                  "read workspace DACL");
            // null DACL 已允许全部访问，不能将其收紧为仅有新增条目的 DACL。
            if (oldAcl == IntPtr.Zero) return;
            var entry = new ExplicitAccess
            {
                Permissions = WriteDac,
                Mode        = 1,
                Inheritance = 0,
                Trustee     = new Trustee { Form = 0, Type = 1, Name = sidPointer }
            };
            Check(SetEntriesInAclW(1, in entry, oldAcl, out newAcl), "merge workspace WRITE_DAC grant");
            Check(SetSecurityInfo(opened.Handle, 1, 4, IntPtr.Zero, IntPtr.Zero, newAcl, IntPtr.Zero),
                  "apply workspace DACL");
        }
        finally
        {
            if (descriptor != IntPtr.Zero) LocalFree(descriptor);
            if (newAcl     != IntPtr.Zero) LocalFree(newAcl);
            Marshal.FreeHGlobal(sidPointer);
        }
    }

    /// <summary>
    ///     <para>拒绝卷根、UNC、用户主目录及系统安装目录的管理授权。</para>
    ///     <para>Rejects management grants for volume roots, UNC paths, the user home and system installation directories.</para>
    /// </summary>
    public static void ValidateGrantTarget(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Path.IsPathFullyQualified(path) || full.StartsWith(@"\\", StringComparison.Ordinal) ||
            string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!),
                          StringComparison.OrdinalIgnoreCase))
            throw new
                InvalidDataException("Workspace ACL grants require a specific local directory, not a volume root or UNC path.");
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.UserProfile
                 })
        {
            var protectedPath = Environment.GetFolderPath(folder);
            if (protectedPath.Length == 0) continue;
            if (Workspace.IsInside(full, protectedPath) ||
                (folder != Environment.SpecialFolder.UserProfile && Workspace.IsInside(protectedPath, full)))
                throw new
                    InvalidDataException($"Workspace ACL grants cannot cover protected directory '{protectedPath}'.");
        }
    }

    internal static OpenedDirectory Open(string path, uint access)
    {
        var handles = new List<SafeFileHandle>();
        try
        {
            var paths = new Stack<string>();
            for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
                paths.Push(current.FullName);
            FileInformation info = default;
            while (paths.TryPop(out var component))
            {
                // 保留每级句柄且不共享删除，校验到修改期间不能替换目录祖先。
                var handle = CreateFileW(component, paths.Count == 0 ? access : 0, 3,
                                         IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastPInvokeError();
                    handle.Dispose();
                    throw new Win32Exception(error,
                                             $"Cannot open workspace directory '{component}' (access=0x{access:X}).");
                }

                handles.Add(handle);
                if (!GetFileInformationByHandle(handle, out info))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(),
                                             $"Cannot identify directory '{component}'.");
                if ((info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0)
                    throw new
                        InvalidDataException($"Workspace path must be a directory without reparse points: {component}");
            }

            return new OpenedDirectory(handles,
                                       new WorkspaceDirectoryIdentity(path,
                                                                      $"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}"));
        }
        catch
        {
            foreach (var handle in handles) handle.Dispose();
            throw;
        }
    }

    private static void RequireIdentity(WorkspaceDirectoryIdentity expected, WorkspaceDirectoryIdentity actual)
    {
        if (expected != actual) throw new InvalidDataException("Workspace identity changed before ACL access.");
    }

    private static void Check(uint code, string operation)
    {
        if (code != 0) throw new Win32Exception((int)code, $"Failed to {operation}.");
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16,
                   SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(
        string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityInfo(SafeFileHandle handle, uint       type, uint flags, out IntPtr owner,
                                                out IntPtr     group,  out IntPtr dacl, out IntPtr sacl,
                                                out IntPtr     descriptor);

    [LibraryImport("advapi32.dll")]
    private static partial uint SetSecurityInfo(
        SafeFileHandle handle, uint type, uint flags, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [LibraryImport("advapi32.dll")]
    private static partial uint SetEntriesInAclW(uint count, in ExplicitAccess entry, IntPtr oldAcl, out IntPtr newAcl);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    internal sealed class OpenedDirectory(List<SafeFileHandle> handles, WorkspaceDirectoryIdentity identity)
        : IDisposable
    {
        public SafeFileHandle Handle => handles[^1];

        public WorkspaceDirectoryIdentity Identity { get; } = identity;

        public void Dispose()
        {
            foreach (var handle in handles) handle.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow,  WriteHigh;
        public uint Volume,      SizeHigh,     SizeLow,   Links,      IndexHigh, IndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Trustee
    {
        public IntPtr Multiple;
        public uint   Operation, Form, Type;
        public IntPtr Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExplicitAccess
    {
        public uint    Permissions, Mode, Inheritance;
        public Trustee Trustee;
    }
}

using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         槽位初始化、契约校验与回收共用的 Win32 互操作层：目录句柄打开、同句柄重开、ACL 读写、句柄相对
///         子项打开、有界句柄绑定枚举与删除标记。所有句柄以 <see cref="SafeFileHandle" /> 持有，调用点不接触
///         裸指针生命周期。
///     </para>
///     <para>
///         The Win32 interop layer shared by slot initialization, contract validation, and reclamation:
///         directory handles, same-handle reopens, ACL read/write, handle-relative child opens, bounded
///         handle-bound enumeration, and delete-on-close marking. Every handle is owned by a
///         <see cref="SafeFileHandle" />, so call sites never manage raw pointer lifetimes.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         句柄相对打开与同句柄权限重开均使用 <c>NtCreateFile</c>（Win32 没有等价的根目录相对打开），目录枚举
///         使用 <c>GetFileInformationByHandleEx</c>，均以已校验的目录句柄为基准，避免路径级 TOCTOU。
///     </para>
///     <para>
///         Handle-relative opens and same-handle access reopens both use <c>NtCreateFile</c> (Win32 has no
///         equivalent root-relative open), and enumeration uses <c>GetFileInformationByHandleEx</c>; all are
///         anchored on the validated directory handle to avoid path-level TOCTOU.
///     </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class SandboxRuntimeTempSlotNative
{
    internal const uint ReadControl        = 0x0002_0000;
    internal const uint WriteDac           = 0x0004_0000;
    internal const uint WriteOwner         = 0x0008_0000;
    internal const uint FileReadAttributes = 0x0000_0080;
    internal const uint FileListDirectory  = 0x0000_0001;
    internal const uint FileTraverse       = 0x0000_0020;
    internal const uint DeleteAccess       = 0x0001_0000;
    internal const uint Synchronize        = 0x0010_0000;
    internal const uint FileShareAll       = 0x0000_0007;
    internal const uint FileShareReadWrite = 0x0000_0003;
    internal const uint OpenExisting = 3;
    internal const uint FileOpen     = 1;
    internal const uint FileCreate   = 2;
    internal const uint FileOpenIf   = 3;
    private const  long IoStatusFileCreated = 2;

    internal const uint FileDirectoryFile          = 0x0000_0001;
    internal const uint FileFlagBackupSemantics    = 0x0200_0000;
    internal const uint FileFlagOpenReparsePoint   = 0x0020_0000;
    internal const uint OpenReparsePointOptions    = 0x0020_0000;
    internal const uint OpenForBackupIntentOptions = 0x0000_4000; // FILE_OPEN_FOR_BACKUP_INTENT
    internal const uint SynchronousIoOptions       = 0x0000_0020;
    internal const uint ObjCaseInsensitive         = 0x0000_0040;

    internal const uint DaclSecurityInformation          = 0x0000_0004;
    internal const uint OwnerSecurityInformation         = 0x0000_0001;
    internal const uint ProtectedDaclSecurityInformation = 0x8000_0000;

    internal const uint ObjectInheritAce    = 0x01;
    internal const uint ContainerInheritAce = 0x02;
    internal const uint InheritOnlyAce      = 0x08;
    internal const uint NoInherit           = 0x00;

    internal const uint AttributeDirectory    = 0x0000_0010;
    internal const uint AttributeReparsePoint = 0x0000_0400;
    internal const uint AttributeReadOnly     = 0x0000_0001;

    internal const int ErrorInvalidFunction   = 1;
    internal const int ErrorFileNotFound      = 2;
    internal const int ErrorPathNotFound      = 3;
    internal const int ErrorAccessDenied      = 5;
    internal const int ErrorNoMoreFiles       = 18;
    internal const int ErrorSharingViolation  = 32;
    internal const int ErrorLockViolation     = 33;
    internal const int ErrorNotSupported      = 50;
    internal const int ErrorInvalidParameter  = 87;
    internal const int ErrorDirectoryNotEmpty = 145;
    internal const int ErrorAlreadyExists     = 183;

    internal const int  SeFileObject                   = 1;
    internal const uint FileDispositionInfoClass       = 4;
    internal const uint FileDispositionInfoExClass     = 21;
    internal const uint FileRenameInfoExClass          = 22;
    internal const uint FileDispositionDelete          = 0x0000_0001;
    internal const uint FileDispositionPosixSemantics  = 0x0000_0002;
    internal const uint FileRenameReplaceIfExists      = 0x0000_0001;
    internal const uint FileRenamePosixSemantics       = 0x0000_0002;
    internal const uint FileIdBothDirectoryInfo        = 10;
    internal const uint FileIdBothDirectoryRestartInfo = 11;
    internal const uint FileIdInfoClass                = 18;
    private const  int  FileIdBothDirectoryFixedLength = 104;

    internal const uint AccessModeAllow = 2; // ACCESS_MODE.SET_ACCESS
    internal const uint AccessModeDeny  = 3; // ACCESS_MODE.DENY_ACCESS

    /// <summary>
    ///     <para>这些 Win32 句柄与 ACL 语义只在 Windows 存在；其他平台明确拒绝而不是静默降级。</para>
    ///     <para>
    ///         These Win32 handle and ACL semantics exist only on Windows; other platforms are refused instead
    ///         of degrading silently.
    ///     </para>
    /// </summary>
    internal static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Runtime temp slot isolation requires Windows directory handle and ACL semantics.");
    }

    /// <summary>
    ///     <para>
    ///         以 <c>FILE_FLAG_BACKUP_SEMANTICS</c> 打开目录；默认附加 <c>FILE_FLAG_OPEN_REPARSE_POINT</c> 不跟随
    ///         链接（受管目录必须拒绝重解析点），仅在打开可信 <c>sandboxHome</c> 起点别名时允许显式关闭以收敛
    ///         到真实目标目录。失败时抛出带路径与访问掩码的异常。
    ///     </para>
    ///     <para>
    ///         Opens a directory with <c>FILE_FLAG_BACKUP_SEMANTICS</c>; defaults to adding
    ///         <c>FILE_FLAG_OPEN_REPARSE_POINT</c> so managed directories never follow links, while allowing
    ///         callers to follow a trusted <c>sandboxHome</c> junction alias when explicitly set to false.
    ///         Failures throw with the path and access mask.
    ///     </para>
    /// </summary>
    internal static SafeFileHandle OpenDirectory(
        string path, uint desiredAccess, uint shareMode, string context, bool openReparsePoint = true)
    {
        var flags      = FileFlagBackupSemantics | (openReparsePoint ? FileFlagOpenReparsePoint : 0u);
        var nativePath = ToWin32ExtendedPath(path);
        var handle     = CreateFileW(nativePath, desiredAccess, shareMode, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, $"Cannot open {context} '{path}' (access=0x{desiredAccess:x}).");
        }

        return handle;
    }

    /// <summary>
    ///     <para>
    ///         在已校验的父目录句柄下以 <c>FILE_OPEN_IF | FILE_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT</c>
    ///         打开或创建直接子目录并返回其句柄（全程不经绝对路径解析）；失败抛出带路径的异常。
    ///     </para>
    ///     <para>
    ///         Opens or creates a direct child directory under a validated parent directory handle with
    ///         <c>FILE_OPEN_IF | FILE_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT</c> and returns its handle
    ///         (never resolving by absolute path); failures throw with the path.
    ///     </para>
    /// </summary>
    internal static SafeFileHandle OpenOrCreateChildDirectory(
        SafeFileHandle              parent,
        string                      name,
        uint                        desiredAccess,
        uint                        shareMode,
        string                      fullPath,
        string                      context,
        out bool                    created,
        IReadOnlyList<RawAclEntry>? initialProtectedDacl = null) =>
        CreateRelativeDirectory(parent, name, desiredAccess, shareMode, FileOpenIf, fullPath, context,
                                initialProtectedDacl, out created);

    /// <summary>
    ///     <para>
    ///         在已校验的父目录句柄下以 <c>FILE_CREATE | FILE_DIRECTORY_FILE</c> 原子创建全新直接子目录并直接
    ///         返回其句柄；目标已存在（含残留旧目录、普通文件或重解析点）时抛出 <see cref="IOException" /> 拒绝
    ///         复用，避免先建后开之间的路径替换窗口。传入 <paramref name="initialProtectedDacl" /> 时在创建瞬间
    ///         即写入受保护显式 DACL，消除创建与设权之间的继承窗口。
    ///     </para>
    ///     <para>
    ///         Atomically creates a brand-new direct child directory under a validated parent directory handle with
    ///         <c>FILE_CREATE | FILE_DIRECTORY_FILE</c> and returns its handle directly; throws
    ///         <see cref="IOException" /> when the target already exists (leftover directory, file, or reparse
    ///         point) instead of reusing it, eliminating any path-replacement window between create and open. When
    ///         <paramref name="initialProtectedDacl" /> is supplied, the protected explicit DACL is applied
    ///         atomically at creation time so no inherited-DACL race window exists.
    ///     </para>
    /// </summary>
    internal static SafeFileHandle CreateNewChildDirectory(
        SafeFileHandle              parent,
        string                      name,
        uint                        desiredAccess,
        uint                        shareMode,
        string                      fullPath,
        string                      context,
        IReadOnlyList<RawAclEntry>? initialProtectedDacl = null) =>
        CreateRelativeDirectory(parent, name, desiredAccess, shareMode, FileCreate, fullPath, context,
                                initialProtectedDacl, out _);

    private static SafeFileHandle CreateRelativeDirectory(
        SafeFileHandle              parent,
        string                      name,
        uint                        desiredAccess,
        uint                        shareMode,
        uint                        createDisposition,
        string                      fullPath,
        string                      context,
        IReadOnlyList<RawAclEntry>? initialProtectedDacl,
        out bool                    created)
    {
        created = false;
        if (name.Length is 0 or > 32767 || name.Contains('\\') || name.Contains('/') || name is "." or "..")
            throw new ArgumentException($"A child directory name must be a single path component: '{name}'.",
                                        nameof(name));

        var addedRef   = false;
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var nameStruct = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var sdBuffer   = IntPtr.Zero;
        try
        {
            if (initialProtectedDacl is { Count: > 0 })
            {
                var sdBytes = BuildSelfRelativeProtectedSecurityDescriptor(initialProtectedDacl);
                sdBuffer = Marshal.AllocHGlobal(sdBytes.Length);
                Marshal.Copy(sdBytes, 0, sdBuffer, sdBytes.Length);
            }

            parent.DangerousAddRef(ref addedRef);
            Marshal.StructureToPtr(new UnicodeString
            {
                Length        = (ushort)(name.Length * sizeof(char)),
                MaximumLength = (ushort)(name.Length * sizeof(char) + sizeof(char)),
                Buffer        = nameBuffer
            }, nameStruct, false);
            var attributes = new ObjectAttributes
            {
                Length                   = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory            = parent.DangerousGetHandle(),
                ObjectName               = nameStruct,
                Attributes               = ObjCaseInsensitive,
                SecurityDescriptor       = sdBuffer,
                SecurityQualityOfService = IntPtr.Zero
            };
            var status = IoStatusBlock.Empty;
            var result = NtCreateFile(out var fileHandle, desiredAccess | Synchronize, ref attributes, ref status,
                                      IntPtr.Zero, AttributeDirectory, shareMode, createDisposition,
                                      FileDirectoryFile | OpenReparsePointOptions | OpenForBackupIntentOptions |
                                      SynchronousIoOptions,
                                      IntPtr.Zero, 0);
            if (result < 0)
            {
                var error = (int)RtlNtStatusToDosError(result);
                if (createDisposition == FileCreate && error == ErrorAlreadyExists)
                    throw new IOException(
                        $"The {context} '{fullPath}' already exists; refusing to reuse an existing entry as a fresh allocation.");
                throw new Win32Exception(error, $"Cannot create or open {context} '{fullPath}'.");
            }

            created = status.Information.ToInt64() == IoStatusFileCreated;
            return new SafeFileHandle(fileHandle, ownsHandle: true);
        }
        finally
        {
            if (addedRef) parent.DangerousRelease();
            if (sdBuffer != IntPtr.Zero) Marshal.FreeHGlobal(sdBuffer);
            Marshal.FreeHGlobal(nameStruct);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static byte[] BuildSelfRelativeProtectedSecurityDescriptor(IReadOnlyList<RawAclEntry> entries)
    {
        var rawDacl = new RawAcl(2, entries.Count);
        foreach (var entry in entries)
        {
            var flags = AceFlags.None;
            if ((entry.Inheritance & ObjectInheritAce) != 0) flags |= AceFlags.ObjectInherit;
            if ((entry.Inheritance & ContainerInheritAce) != 0) flags |= AceFlags.ContainerInherit;
            if ((entry.Inheritance & InheritOnlyAce) != 0) flags |= AceFlags.InheritOnly;
            var qualifier = entry.Denied ? AceQualifier.AccessDenied : AceQualifier.AccessAllowed;
            rawDacl.InsertAce(rawDacl.Count,
                              new CommonAce(flags, qualifier, unchecked((int)entry.Mask), entry.Sid, false, null));
        }

        // owner 留空由内核默认设为当前宿主进程用户；SE_DACL_PROTECTED 阻断父目录继承 ACE 渗入新建目录。
        var descriptor = new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
            owner: null, group: null, systemAcl: null, discretionaryAcl: rawDacl);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    /// <summary>
    ///     <para>
    ///         基于已打开句柄经 <c>NtCreateFile</c> 空相对名重开同一内核对象（不经路径解析），以升级目录列举
    ///         或写 owner 权限；因底层使用 <c>FILE_SYNCHRONOUS_IO_NONALERT</c>，会自动补齐
    ///         <see cref="Synchronize" /> 位。失败返回 null 并输出 Win32 错误码。
    ///     </para>
    ///     <para>
    ///         Reopens the same kernel object from an already opened handle via <c>NtCreateFile</c> with an
    ///         empty relative name (without path resolution) to request list or write-owner access; automatically
    ///         includes <see cref="Synchronize" /> because <c>FILE_SYNCHRONOUS_IO_NONALERT</c> requires it.
    ///         Returns null with the Win32 error code on failure.
    ///     </para>
    /// </summary>
    internal static SafeFileHandle? TryReOpenHandle(
        SafeFileHandle original, uint desiredAccess, uint shareMode, out int win32Error)
    {
        var addedRef   = false;
        var nameStruct = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            original.DangerousAddRef(ref addedRef);
            Marshal.StructureToPtr(new UnicodeString { Length = 0, MaximumLength = 0, Buffer = IntPtr.Zero },
                                   nameStruct, false);
            var attributes = new ObjectAttributes
            {
                Length                   = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory            = original.DangerousGetHandle(),
                ObjectName               = nameStruct,
                Attributes               = ObjCaseInsensitive,
                SecurityDescriptor       = IntPtr.Zero,
                SecurityQualityOfService = IntPtr.Zero
            };
            var status = IoStatusBlock.Empty;
            var result = NtCreateFile(out var fileHandle, desiredAccess | Synchronize, ref attributes, ref status,
                                      IntPtr.Zero, 0, shareMode, FileOpen,
                                      OpenForBackupIntentOptions | SynchronousIoOptions, IntPtr.Zero, 0);
            if (result < 0)
            {
                win32Error = (int)RtlNtStatusToDosError(result);
                return null;
            }

            win32Error = 0;
            return new SafeFileHandle(fileHandle, ownsHandle: true);
        }
        finally
        {
            if (addedRef) original.DangerousRelease();
            Marshal.FreeHGlobal(nameStruct);
        }
    }

    /// <summary>
    ///     <para>读取目录句柄的文件信息；失败时返回 false 并输出 Win32 错误码，不抛出。</para>
    ///     <para>
    ///         Reads file information for a directory handle; returns false with the Win32 error code instead
    ///         of throwing.
    ///     </para>
    /// </summary>
    internal static bool TryGetFileInformation(
        SafeFileHandle directoryHandle, out ByHandleFileInformation information, out int win32Error)
    {
        if (GetFileInformationByHandle(directoryHandle, out information))
        {
            win32Error = 0;
            return true;
        }

        win32Error = Marshal.GetLastPInvokeError();
        return false;
    }

    /// <summary>
    ///     <para>
    ///         通过 <c>GetFileInformationByHandleEx(FileIdInfo)</c> 读取 64 位卷序列号与 128 位文件 ID（兼容 NTFS
    ///         与 ReFS）；不支持该信息类的文件系统或查询失败返回 false 并输出 Win32 错误码。
    ///     </para>
    ///     <para>
    ///         Reads the 64-bit volume serial number and 128-bit file ID via
    ///         <c>GetFileInformationByHandleEx(FileIdInfo)</c> (accurate on both NTFS and ReFS); returns false
    ///         with the Win32 error code if the filesystem does not support it or the query fails.
    ///     </para>
    /// </summary>
    internal static bool TryGetFileIdInformation(
        SafeFileHandle directoryHandle, out FileIdInfo information, out int win32Error)
    {
        if (GetFileInformationByHandleEx(directoryHandle, FileIdInfoClass, out information,
                                         (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            win32Error = 0;
            return true;
        }

        win32Error = Marshal.GetLastPInvokeError();
        return false;
    }

    /// <summary>
    ///     <para>要求句柄指向真实目录而不是普通文件或重解析点；违反时抛出带路径的异常。</para>
    ///     <para>
    ///         Requires the handle to point at a real directory rather than a file or reparse point;
    ///         violations throw with the path.
    ///     </para>
    /// </summary>
    internal static ByHandleFileInformation RequireDirectory(
        SafeFileHandle handle, string path, out uint attributes)
    {
        if (!TryGetFileInformation(handle, out var information, out var error))
            throw new Win32Exception(error, $"Cannot identify the directory '{path}'.");

        attributes = information.FileAttributes;
        if ((attributes & AttributeReparsePoint) != 0)
            throw new InvalidDataException($"The path must be a directory, not a reparse point: {path}");
        if ((attributes & AttributeDirectory) == 0)
            throw new InvalidDataException($"The path must be a directory: {path}");

        return information;
    }

    /// <summary>
    ///     <para>
    ///         以已校验句柄为基准打开子项（不跟随重解析点）；默认不共享 <c>FILE_SHARE_DELETE</c>，防止回收期间
    ///         其他进程移动或重命名已打开的子目录。失败返回 null 并输出 Win32 错误码。
    ///     </para>
    ///     <para>
    ///         Opens a child relative to a validated handle (never following reparse points); defaults to omitting
    ///         <c>FILE_SHARE_DELETE</c> so another process cannot move or rename an open subdirectory during
    ///         reclamation. Returns null with the Win32 error code on failure.
    ///     </para>
    /// </summary>
    internal static SafeFileHandle? OpenChild(
        SafeFileHandle parent, string name, uint desiredAccess, out int win32Error,
        uint           shareMode = FileShareReadWrite)
    {
        if (name.Length is 0 or > 32767 || name.Contains('\\') || name.Contains('/') || name is "." or "..")
            throw new ArgumentException($"A child name must be a single path component: '{name}'.", nameof(name));

        var addedRef   = false;
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var nameStruct = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            parent.DangerousAddRef(ref addedRef);
            Marshal.StructureToPtr(new UnicodeString
            {
                Length        = (ushort)(name.Length * sizeof(char)),
                MaximumLength = (ushort)(name.Length * sizeof(char) + sizeof(char)),
                Buffer        = nameBuffer
            }, nameStruct, false);
            var attributes = new ObjectAttributes
            {
                Length                   = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory            = parent.DangerousGetHandle(),
                ObjectName               = nameStruct,
                Attributes               = ObjCaseInsensitive,
                SecurityDescriptor       = IntPtr.Zero,
                SecurityQualityOfService = IntPtr.Zero
            };
            var status = IoStatusBlock.Empty;
            var result = NtCreateFile(out var fileHandle, desiredAccess, ref attributes, ref status, IntPtr.Zero, 0,
                                      shareMode, FileOpen,
                                      OpenReparsePointOptions | OpenForBackupIntentOptions | SynchronousIoOptions,
                                      IntPtr.Zero, 0);
            if (result < 0)
            {
                win32Error = (int)RtlNtStatusToDosError(result);
                return null;
            }

            win32Error = 0;
            return new SafeFileHandle(fileHandle, ownsHandle: true);
        }
        finally
        {
            if (addedRef) parent.DangerousRelease();
            Marshal.FreeHGlobal(nameStruct);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    /// <summary>
    ///     <para>
    ///         以已校验句柄为基准有界枚举直接子项名（跳过 <c>.</c> 与 <c>..</c>）；达到上限时提前停止并标记
    ///         截断，不在内存中无界缓存目录项。
    ///     </para>
    ///     <para>
    ///         Enumerates direct child names relative to a validated handle within an explicit cap (skipping
    ///         <c>.</c> and <c>..</c>); stops early and reports truncation once the cap is exceeded so entries
    ///         are never buffered unboundedly.
    ///     </para>
    /// </summary>
    internal static (List<string> Names, bool Truncated) EnumerateChildNames(
        SafeFileHandle    directory,
        string            path,
        int               maximumEntries,
        ref int           enumeratedEntries,
        CancellationToken cancellationToken)
    {
        if (IntPtr.Size != sizeof(long))
            throw new PlatformNotSupportedException(
                "Handle-bound slot reclamation depends on the 64-bit FILE_ID_BOTH_DIR_INFO layout.");

        const int bufferSize = 64 * 1024;
        var       buffer     = Marshal.AllocHGlobal(bufferSize);
        try
        {
            var names   = new List<string>();
            var restart = true;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var infoClass = restart ? FileIdBothDirectoryRestartInfo : FileIdBothDirectoryInfo;
                restart = false;
                if (!GetFileInformationByHandleEx(directory, infoClass, buffer, bufferSize))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == ErrorNoMoreFiles) break;
                    throw new Win32Exception(error, $"Cannot enumerate the directory '{path}'.");
                }

                var offset  = 0;
                var entries = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (offset < 0 || offset > bufferSize - FileIdBothDirectoryFixedLength)
                        throw new InvalidDataException($"Directory enumeration offset {offset} exceeded buffer bounds under '{path}'.");

                    var nextOffset = Marshal.ReadInt32(buffer, offset);
                    var nameLength = Marshal.ReadInt32(buffer, offset + 60);
                    if (nameLength is < 0 or > 512 || (nameLength & 1) != 0 ||
                        nameLength > bufferSize - offset - FileIdBothDirectoryFixedLength)
                        throw new InvalidDataException(
                            $"Unexpected directory entry name length {nameLength} under '{path}'.");

                    var name = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset + FileIdBothDirectoryFixedLength),
                                                      nameLength / sizeof(char));
                    if (!string.IsNullOrEmpty(name) && name is not ("." or ".."))
                    {
                        if (enumeratedEntries >= maximumEntries) return (names, true);
                        enumeratedEntries++;
                        names.Add(name);
                    }

                    entries++;
                    if (nextOffset == 0) break;
                    if (nextOffset < FileIdBothDirectoryFixedLength || nextOffset > bufferSize - offset)
                        throw new InvalidDataException(
                            $"Invalid next-entry offset {nextOffset} during directory enumeration under '{path}'.");
                    offset += nextOffset;
                }

                if (entries == 0) break;
            }

            return (names, false);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    ///     <para>
    ///         标记句柄删除：优先尝试 POSIX 语义（<c>FILE_DISPOSITION_INFO_EX</c>），底层文件系统不支持时回退
    ///         经典 <c>FILE_DISPOSITION_INFO</c>；失败输出 Win32 错误码。
    ///     </para>
    ///     <para>
    ///         Marks the handle for deletion: tries POSIX semantics (<c>FILE_DISPOSITION_INFO_EX</c>) first and
    ///         falls back to classic <c>FILE_DISPOSITION_INFO</c> when the filesystem does not support it;
    ///         failures output the Win32 error code.
    ///     </para>
    /// </summary>
    internal static bool TryMarkDeleteOnClose(SafeFileHandle handle, out int win32Error)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(buffer, unchecked((int)(FileDispositionDelete | FileDispositionPosixSemantics)));
            if (SetFileInformationByHandle(handle, FileDispositionInfoExClass, buffer, sizeof(uint)))
            {
                win32Error = 0;
                return true;
            }

            var exError = Marshal.GetLastPInvokeError();
            if (exError is not (ErrorInvalidFunction or ErrorNotSupported or ErrorInvalidParameter))
            {
                win32Error = exError;
                return false;
            }

            Marshal.WriteByte(buffer, 1);
            if (SetFileInformationByHandle(handle, FileDispositionInfoClass, buffer, 1))
            {
                win32Error = 0;
                return true;
            }

            win32Error = Marshal.GetLastPInvokeError();
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    ///     <para>判断路径是否已不存在；其他错误原样抛出，避免把访问拒绝误当成"已删除"。</para>
    ///     <para>
    ///         Reports whether the path no longer exists; other errors propagate so an access denial is never
    ///         mistaken for "removed".
    ///     </para>
    /// </summary>
    internal static bool IsPathMissing(string path)
    {
        var attributes = GetFileAttributesW(ToWin32ExtendedPath(path));
        if (attributes != uint.MaxValue) return false;
        var error = Marshal.GetLastPInvokeError();
        if (error is ErrorFileNotFound or ErrorPathNotFound) return true;
        throw new Win32Exception(error, $"Cannot query the directory attributes of '{path}'.");
    }

    /// <summary>
    ///     <para>
    ///         以 <c>FileRenameInfoEx</c> + <c>FILE_RENAME_FLAG_REPLACE_IF_EXISTS | FILE_RENAME_FLAG_POSIX_SEMANTICS</c>
    ///         原子替换目标文件：即使并发读端仍持有带 <c>FILE_SHARE_DELETE</c> 的目标文件句柄，NTFS 仍会原子摘除
    ///         旧目录项并挂入新文件，避免 <c>MoveFileExW</c> 因打开句柄失败。失败时通过
    ///         <paramref name="win32Error" /> 返回原生错误码，供调用方按卷支持情况降级或有限重试。
    ///     </para>
    ///     <para>
    ///         Atomically replaces the destination file using <c>FileRenameInfoEx</c> with
    ///         <c>FILE_RENAME_FLAG_REPLACE_IF_EXISTS | FILE_RENAME_FLAG_POSIX_SEMANTICS</c>: even while
    ///         concurrent readers keep handles open with <c>FILE_SHARE_DELETE</c>, NTFS atomically unlinks the
    ///         old directory entry and links the new file instead of failing like <c>MoveFileExW</c>. Returns
    ///         the native code in <paramref name="win32Error" /> on failure so callers can fall back or retry
    ///         within bounds.
    ///     </para>
    /// </summary>
    internal static bool TryReplaceFilePosix(string sourcePath, string destinationPath, out int win32Error)
    {
        var extendedSource = ToWin32ExtendedPath(sourcePath);
        using var handle = CreateFileW(extendedSource, DeleteAccess | Synchronize, FileShareAll, IntPtr.Zero,
                                       OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            win32Error = Marshal.GetLastPInvokeError();
            return false;
        }

        var targetPath = ToWin32ExtendedPath(destinationPath);
        // FILE_RENAME_INFO 布局：uint Flags（x64 补 4 字节对齐）+ IntPtr RootDirectory + uint FileNameLength + WCHAR[]。
        var fileNameOffset = IntPtr.Size * 2 + sizeof(uint);
        var fileNameBytes  = checked(targetPath.Length * sizeof(char));
        var bufferSize     = checked(fileNameOffset + (targetPath.Length + 1) * sizeof(char));
        var buffer         = Marshal.AllocHGlobal(bufferSize);
        try
        {
            // 先清零头部（含对齐填充）与末尾 NUL，再写入重命名标志与 UTF-16 目标路径。
            for (var i = 0; i < fileNameOffset; i++)
                Marshal.WriteByte(buffer, i, 0);

            Marshal.WriteInt32(buffer, 0, unchecked((int)(FileRenameReplaceIfExists | FileRenamePosixSemantics)));
            Marshal.WriteIntPtr(buffer, IntPtr.Size, IntPtr.Zero);
            Marshal.WriteInt32(buffer, IntPtr.Size * 2, fileNameBytes);
            Marshal.Copy(targetPath.ToCharArray(), 0, IntPtr.Add(buffer, fileNameOffset), targetPath.Length);
            Marshal.WriteInt16(buffer, fileNameOffset + fileNameBytes, 0);

            if (SetFileInformationByHandle(handle, FileRenameInfoExClass, buffer, (uint)bufferSize))
            {
                win32Error = 0;
                return true;
            }

            win32Error = Marshal.GetLastPInvokeError();
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    ///     <para>
    ///         在 Win32 原生路径调用边界补齐 <c>\\?\</c>（或 UNC 的 <c>\\?\UNC\</c>）扩展路径前缀，使展开后超过
    ///         <c>MAX_PATH</c> 的规范路径在未启用长路径支持的主机上也能可靠打开与查询，同时不污染对外返回路径。
    ///     </para>
    ///     <para>
    ///         Adds the Win32 extended-length prefix (<c>\\?\</c> or <c>\\?\UNC\</c>) at the native P/Invoke
    ///         boundary so normalized paths exceeding <c>MAX_PATH</c> open and query reliably even on hosts without
    ///         long-path opt-in, without polluting returned paths.
    ///     </para>
    /// </summary>
    internal static string ToWin32ExtendedPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return path;

        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full[2..]
            : @"\\?\" + full;
    }

    /// <summary>
    ///     <para>按模板写入受保护显式 DACL（<c>PROTECTED_DACL_SECURITY_INFORMATION</c>）；失败返回 Win32 错误码。</para>
    ///     <para>
    ///         Writes the protected explicit DACL from the template
    ///         (<c>PROTECTED_DACL_SECURITY_INFORMATION</c>); returns the Win32 error code on failure.
    ///     </para>
    /// </summary>
    internal static int ApplyProtectedDacl(SafeFileHandle handle, IReadOnlyList<RawAclEntry> entries)
    {
        if (entries.Count == 0)
            throw new ArgumentException("A slot DACL must contain at least one ACE.", nameof(entries));

        var entrySize = Marshal.SizeOf<ExplicitAccess>();
        var list      = Marshal.AllocHGlobal(entrySize * entries.Count);
        var sidBlocks = new List<IntPtr>(entries.Count);
        var newAcl    = IntPtr.Zero;
        try
        {
            for (var index = 0; index < entries.Count; index++)
            {
                var entry    = entries[index];
                var sidBytes = new byte[entry.Sid.BinaryLength];
                entry.Sid.GetBinaryForm(sidBytes, 0);
                var sidPointer = Marshal.AllocHGlobal(sidBytes.Length);
                Marshal.Copy(sidBytes, 0, sidPointer, sidBytes.Length);
                sidBlocks.Add(sidPointer);

                var explicitAccess = new ExplicitAccess
                {
                    Permissions = entry.Mask,
                    Mode        = entry.Denied ? AccessModeDeny : AccessModeAllow,
                    Inheritance = entry.Inheritance,
                    Trustee = new Trustee
                    {
                        Multiple          = IntPtr.Zero,
                        MultipleOperation = 0,
                        Form              = 0, // TRUSTEE_IS_SID
                        Type              = 0, // TRUSTEE_IS_UNKNOWN
                        Name              = sidPointer
                    }
                };
                Marshal.StructureToPtr(explicitAccess, IntPtr.Add(list, index * entrySize), false);
            }

            var entriesResult = SetEntriesInAclW((uint)entries.Count, list, IntPtr.Zero, out newAcl);
            if (entriesResult != 0) return (int)entriesResult;

            var setResult = SetSecurityInfo(handle, SeFileObject,
                                            DaclSecurityInformation | ProtectedDaclSecurityInformation, IntPtr.Zero,
                                            IntPtr.Zero, newAcl, IntPtr.Zero);
            return (int)setResult;
        }
        finally
        {
            if (newAcl != IntPtr.Zero) LocalFree(newAcl);
            foreach (var sidBlock in sidBlocks) Marshal.FreeHGlobal(sidBlock);
            Marshal.FreeHGlobal(list);
        }
    }

    /// <summary>
    ///     <para>把句柄指向的目录 owner 设为指定 SID；失败返回 Win32 错误码。</para>
    ///     <para>Assigns the directory owner through the handle; returns the Win32 error code on failure.</para>
    /// </summary>
    internal static int SetOwner(SafeFileHandle handle, SecurityIdentifier owner)
    {
        var sidBytes   = new byte[owner.BinaryLength];
        owner.GetBinaryForm(sidBytes, 0);
        var sidPointer = Marshal.AllocHGlobal(sidBytes.Length);
        try
        {
            Marshal.Copy(sidBytes, 0, sidPointer, sidBytes.Length);
            var result = SetSecurityInfo(handle, SeFileObject, OwnerSecurityInformation, sidPointer, IntPtr.Zero,
                                         IntPtr.Zero, IntPtr.Zero);
            return (int)result;
        }
        finally
        {
            Marshal.FreeHGlobal(sidPointer);
        }
    }

    /// <summary>
    ///     <para>从目录句柄读取 owner、受保护标志与全部 ACE；未知 ACE 类型或读取失败直接抛出。</para>
    ///     <para>
    ///         Reads the owner, protected flag, and all ACEs from the directory handle; unknown ACE types or
    ///         read failures throw.
    ///     </para>
    /// </summary>
    internal static RawSecurity ReadSecurity(SafeFileHandle handle, string path)
    {
        var result = GetSecurityInfo(handle, SeFileObject, OwnerSecurityInformation | DaclSecurityInformation,
                                     out _, out _, out _, out _, out var descriptor);
        if (result != 0)
            throw new Win32Exception((int)result, $"Cannot read the security descriptor of '{path}'.");

        try
        {
            var length = (int)GetSecurityDescriptorLength(descriptor);
            if (length is <= 0 or > 256 * 1024)
                throw new InvalidDataException(
                    $"Unexpected security descriptor length {length} for the directory '{path}'.");

            var bytes = new byte[length];
            Marshal.Copy(descriptor, bytes, 0, length);
            var raw           = new RawSecurityDescriptor(bytes, 0);
            var ownerSid      = raw.Owner ?? throw new InvalidDataException($"The directory '{path}' has no owner.");
            var daclProtected = (raw.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0;
            var aces          = new List<RawAce>();
            if (raw.DiscretionaryAcl is { } dacl)
                foreach (GenericAce ace in dacl)
                {
                    var qualified = ace as QualifiedAce;
                    var sid       = qualified?.SecurityIdentifier;
                    if (sid is null || ace.AceType is not (AceType.AccessAllowed or AceType.AccessDenied))
                        throw new InvalidDataException(
                            $"The directory '{path}' contains an unsupported ACE type {(int)ace.AceType}.");

                    aces.Add(new RawAce((int)ace.AceType, ace.AceType == AceType.AccessDenied, sid,
                                        (uint)qualified!.AccessMask, ace.AceFlags.HasFlag(AceFlags.Inherited),
                                        ace.AceFlags.HasFlag(AceFlags.InheritOnly),
                                        ace.AceFlags.HasFlag(AceFlags.ContainerInherit),
                                        ace.AceFlags.HasFlag(AceFlags.ObjectInherit),
                                        ace.AceFlags.HasFlag(AceFlags.NoPropagateInherit)));
                }

            return new RawSecurity(ownerSid, daclProtected, raw.DiscretionaryAcl is not null, aces);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    /// <summary>
    ///     <para>一条待写入 DACL 的显式 ACE：deny 优先于 allow，继承标志使用原生 ACE 位。</para>
    ///     <para>
    ///         One explicit ACE to be written into a DACL: deny precedes allow, and inheritance uses raw ACE
    ///         flags.
    ///     </para>
    /// </summary>
    internal readonly record struct RawAclEntry(bool Denied, SecurityIdentifier Sid, uint Mask, uint Inheritance);

    /// <summary>
    ///     <para>从目录句柄解析出的安全描述符快照；<paramref name="DaclPresent" /> 为 false 表示 DACL 缺失。</para>
    ///     <para>
    ///         Security-descriptor snapshot parsed from a directory handle;
    ///         <paramref name="DaclPresent" /> false means the DACL is absent.
    ///     </para>
    /// </summary>
    internal sealed record RawSecurity(
        SecurityIdentifier Owner, bool DaclProtected, bool DaclPresent, IReadOnlyList<RawAce> Aces);

    /// <summary>
    ///     <para>一条已解析的 ACE；<paramref name="AceType" /> 保留原生值以便拒绝未知类型。</para>
    ///     <para>
    ///         One parsed ACE; <paramref name="AceType" /> keeps the raw value so unknown types are refused.
    ///     </para>
    /// </summary>
    internal readonly record struct RawAce(
        int                AceType,
        bool               Denied,
        SecurityIdentifier Sid,
        uint               Mask,
        bool               Inherited,
        bool               InheritOnly,
        bool               ContainerInherit,
        bool               ObjectInherit,
        bool               NoPropagateInherit);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint   Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint   Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;

        public static IoStatusBlock Empty => new() { Status = IntPtr.Zero, Information = IntPtr.Zero };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Trustee
    {
        public IntPtr Multiple;
        public uint   MultipleOperation;
        public uint   Form;
        public uint   Type;
        public IntPtr Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExplicitAccess
    {
        public uint    Permissions;
        public uint    Mode;
        public uint    Inheritance;
        public Trustee Trustee;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16,
                   SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(
        string path, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition,
        uint   flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle file, out ByHandleFileInformation information);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(
        SafeFileHandle file, uint informationClass, IntPtr information, uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(
        SafeFileHandle file, uint informationClass, out FileIdInfo information, uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(
        SafeFileHandle file, uint informationClass, IntPtr information, uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesW", StringMarshalling = StringMarshalling.Utf16,
                   SetLastError = true)]
    private static partial uint GetFileAttributesW(string fileName);

    [LibraryImport("ntdll.dll", EntryPoint = "NtCreateFile")]
    private static partial int NtCreateFile(
        out IntPtr        fileHandle,       uint   desiredAccess, ref ObjectAttributes objectAttributes,
        ref IoStatusBlock ioStatusBlock,    IntPtr allocationSize, uint fileAttributes, uint shareAccess,
        uint              createDisposition, uint  createOptions,  IntPtr eaBuffer,     uint eaLength);

    [LibraryImport("ntdll.dll", EntryPoint = "RtlNtStatusToDosError")]
    private static partial uint RtlNtStatusToDosError(int status);

    [LibraryImport("advapi32.dll", EntryPoint = "GetSecurityInfo", SetLastError = true)]
    private static partial uint GetSecurityInfo(
        SafeFileHandle handle, int objectType, uint securityInfo, out IntPtr owner, out IntPtr group,
        out IntPtr     dacl,   out IntPtr sacl, out IntPtr descriptor);

    [LibraryImport("advapi32.dll", EntryPoint = "SetSecurityInfo", SetLastError = true)]
    private static partial uint SetSecurityInfo(
        SafeFileHandle handle, int objectType, uint securityInfo, IntPtr owner, IntPtr group, IntPtr dacl,
        IntPtr         sacl);

    [LibraryImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorLength")]
    private static partial uint GetSecurityDescriptorLength(IntPtr descriptor);

    [LibraryImport("advapi32.dll", EntryPoint = "SetEntriesInAclW", SetLastError = true)]
    private static partial uint SetEntriesInAclW(
        uint countOfExplicitEntries, IntPtr listOfExplicitEntries, IntPtr oldAcl, out IntPtr newAcl);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);
}

using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>可执行组件的文件身份快照：规范最终路径与 Windows 卷/文件标识，用于提权前复核目标未被替换。</para>
///     <para>
///         File-identity snapshot of an executable component: canonical final path plus Windows volume and
///         file identifiers, used to verify the target was not swapped before elevation.
///     </para>
/// </summary>
public sealed record SandboxFileIdentity(
    string FinalPath,
    uint   VolumeSerialNumber,
    uint   FileIndexHigh,
    uint   FileIndexLow);

/// <summary>
///     <para>
///         把路径解析为文件系统最终落点（展开 8.3 短名与 junction/symlink），供策略根的重叠校验
///         防止词法前缀比较被链接形态绕过；同时提供面向可执行组件的严格句柄解析与文件身份捕获。
///     </para>
///     <para>
///         Resolves a path to its final filesystem location (expanding 8.3 short names and
///         junctions/symlinks) so root-overlap checks cannot be bypassed through link forms, and also
///         provides strict handle-based resolution and file-identity capture for executable components.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         P/Invoke 仅在 Windows 调用（CreateFileW + GetFinalPathNameByHandleW /
///         GetFileInformationByHandle）；其他平台按保守语义处理，编译不受平台影响。
///     </para>
///     <para>
///         The P/Invoke runs on Windows only (CreateFileW + GetFinalPathNameByHandleW /
///         GetFileInformationByHandle); other platforms take conservative behavior, keeping compilation
///         platform-independent.
///     </para>
/// </remarks>
internal static partial class SandboxFinalPathResolver
{
    private const uint ReadAttributes     = 0x00000080;
    private const uint ShareReadWriteDel  = 7;
    private const uint OpenExisting       = 3;
    private const uint BackupSemantics    = 0x02000000;
    private const uint AttributeDirectory = 0x00000010;
    private const int  ErrorFileNotFound  = 2;
    private const int  ErrorPathNotFound  = 3;
    private const int  ErrorDirectory     = 267;

    /// <summary>
    ///     <para>
    ///         解析路径的最终落点；Windows 上返回去掉 <c>\\?\</c> 扩展前缀的规范长路径，路径不存在、
    ///         无权限或解析失败时返回 null。短路径（8.3）与链接链都会被展开。
    ///     </para>
    ///     <para>
    ///         Resolves the final location of a path; on Windows returns the canonical long path
    ///         without the <c>\\?\</c> extended prefix, or null when the path is missing,
    ///         inaccessible, or resolution fails. Short (8.3) names and link chains are expanded.
    ///     </para>
    /// </summary>
    internal static string? TryGetFinalPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            using var handle = CreateFileW(path, ReadAttributes, ShareReadWriteDel, IntPtr.Zero, OpenExisting,
                                           BackupSemantics, IntPtr.Zero);
            return handle.IsInvalid ? null : TryReadFinalPath(handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 解析失败必须回落词法路径而不是中断校验；更宽的失败（如编码缺陷）不得被吞掉。
            return null;
        }
    }

    /// <summary>
    ///     <para>
    ///         严格解析已有普通文件的最终落点与文件身份（拒绝目录）；打开或句柄解析失败返回 null，
    ///         调用方对必须存在的可执行组件不得回落词法路径。
    ///     </para>
    ///     <para>
    ///         Strictly resolves the final path and file identity of an existing regular file (rejecting
    ///         directories); returns null on open or handle-resolution failure so callers never fall back
    ///         to lexical paths for required executables.
    ///     </para>
    /// </summary>
    internal static SandboxFileIdentity? TryResolveFileIdentity(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        if (!OperatingSystem.IsWindows())
            return File.Exists(path) ? new SandboxFileIdentity(Path.GetFullPath(path), 0, 0, 0) : null;

        try
        {
            using var handle = CreateFileW(path, ReadAttributes, ShareReadWriteDel, IntPtr.Zero, OpenExisting,
                                           0, IntPtr.Zero);
            if (handle.IsInvalid) return null;
            if (!GetFileInformationByHandle(handle, out var info)) return null;
            if ((info.FileAttributes & AttributeDirectory) != 0) return null;

            var finalPath = TryReadFinalPath(handle);
            if (finalPath is null) return null;

            return new SandboxFileIdentity(finalPath, info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     <para>
    ///         严格解析已有目录的最终落点（拒绝普通文件）；打开或解析失败返回 null。
    ///     </para>
    ///     <para>
    ///         Strictly resolves the final path of an existing directory (rejecting regular files);
    ///         returns null when open or resolution fails.
    ///     </para>
    /// </summary>
    internal static string? TryGetFinalDirectoryPath(string path) => TryGetFinalDirectoryPath(path, out _);

    /// <summary>
    ///     <para>
    ///         严格解析已有目录的最终落点并返回失败时的 Win32 错误码，供允许缺失尾部段的规范化路径解析区分
    ///         "目录确实不存在"与"无权限/普通文件/链接异常"。
    ///     </para>
    ///     <para>
    ///         Strictly resolves an existing directory's final path and outputs the Win32 error code on
    ///         failure so canonical path resolution for not-yet-created homes can distinguish genuine
    ///         absence from access denial, regular files, or link errors.
    ///     </para>
    /// </summary>
    internal static string? TryGetFinalDirectoryPath(string path, out int win32Error)
    {
        win32Error = 0;
        if (string.IsNullOrWhiteSpace(path)) return null;

        if (!OperatingSystem.IsWindows())
            return Directory.Exists(path) ? Path.GetFullPath(path) : null;

        try
        {
            using var handle = CreateFileW(path, ReadAttributes, ShareReadWriteDel, IntPtr.Zero, OpenExisting,
                                           BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                win32Error = Marshal.GetLastPInvokeError();
                return null;
            }

            if (!GetFileInformationByHandle(handle, out var info))
            {
                win32Error = Marshal.GetLastPInvokeError();
                return null;
            }

            if ((info.FileAttributes & AttributeDirectory) == 0)
            {
                win32Error = ErrorDirectory;
                return null;
            }

            var finalPath = TryReadFinalPath(handle);
            if (finalPath is null) win32Error = Marshal.GetLastPInvokeError();
            return finalPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            win32Error = ex.HResult & 0xFFFF;
            return null;
        }
    }

    internal static bool IsPathMissingError(int win32Error) => win32Error is ErrorFileNotFound or ErrorPathNotFound;

    internal static string? TryReadFinalPath(SafeFileHandle handle)
    {
        // 项目未禁用运行时封送，char 缓冲区经 IntPtr 传递（可 blit），不引入封送开关。
        var buffer = IntPtr.Zero;
        try
        {
            var required = GetFinalPathNameByHandleW(handle, IntPtr.Zero, 0, 0);
            if (required is 0 or > 64 * 1024) return null;

            buffer = Marshal.AllocHGlobal((int)required * sizeof(char));
            var written = GetFinalPathNameByHandleW(handle, buffer, required, 0);
            if (written == 0 || written > required) return null;

            var final = Marshal.PtrToStringUni(buffer, (int)written);
            return final is null ? null : StripWin32ExtendedPrefix(final);
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    internal static string StripWin32ExtendedPrefix(string final)
    {
        // 去掉 \\?\ 扩展前缀，保持与词法比较同形；UNC 目标保留双反斜杠前缀 \\server\share。
        if (final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + final[8..];
        return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
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

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16,
                   SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(
        string path, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition,
        uint   flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static partial uint GetFinalPathNameByHandleW(
        SafeFileHandle file, IntPtr filePath, uint filePathLength, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle file, out ByHandleFileInformation fileInformation);
}

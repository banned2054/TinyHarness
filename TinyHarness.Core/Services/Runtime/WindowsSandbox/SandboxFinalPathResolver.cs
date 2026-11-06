using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         把路径解析为文件系统最终落点（展开 8.3 短名与 junction/symlink），供策略根的重叠校验
///         防止词法前缀比较被链接形态绕过。解析是尽力而为的：目标不存在或打开失败时返回 null，
///         调用方必须回落词法路径继续校验，绝不能因解析失败跳过校验。
///     </para>
///     <para>
///         Resolves a path to its final filesystem location (expanding 8.3 short names and
///         junctions/symlinks) so root-overlap checks cannot be bypassed through link forms.
///         Resolution is best effort: a missing target or a failed open returns null and the
///         caller must fall back to the lexical path and keep validating — resolution failure
///         never skips the check.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         P/Invoke 仅在 Windows 调用（CreateFileW + FILE_FLAG_BACKUP_SEMANTICS 打开目录句柄后
///         GetFinalPathNameByHandleW）；其他平台直接返回 null，编译不受平台影响。
///     </para>
///     <para>
///         The P/Invoke runs on Windows only (CreateFileW with FILE_FLAG_BACKUP_SEMANTICS to open
///         the directory handle, then GetFinalPathNameByHandleW); other platforms return null
///         immediately, keeping the compilation platform-independent.
///     </para>
/// </remarks>
internal static partial class SandboxFinalPathResolver
{
    private const uint GenericRead    = 0x80000000;
    private const uint ShareReadWrite = 3;
    private const uint OpenExisting   = 3;
    private const uint BackupSemantics = 0x02000000;

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

        // 项目未禁用运行时封送，char 缓冲区经 IntPtr 传递（可 blit），不引入封送开关。
        // The project keeps runtime marshalling enabled, so the char buffer travels
        // through an IntPtr (blittable) instead of introducing a marshalling switch.
        IntPtr buffer = IntPtr.Zero;
        try
        {
            using var handle = CreateFileW(path, GenericRead, ShareReadWrite, IntPtr.Zero, OpenExisting,
                                           BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid) return null;

            // 缓冲区过小时 API 返回含终止符的所需字符数；0 表示真实失败，上限防御异常返回值。
            // When the buffer is too small the API returns the required character count
            // including the terminator; zero means a real failure, and the cap guards
            // against a nonsensical return value.
            var required = GetFinalPathNameByHandleW(handle, IntPtr.Zero, 0, 0);
            if (required is 0 or > 64 * 1024) return null;

            buffer  = Marshal.AllocHGlobal((int)required * sizeof(char));
            var written = GetFinalPathNameByHandleW(handle, buffer, required, 0);
            if (written == 0 || written > required) return null;

            var final = Marshal.PtrToStringUni(buffer, (int)written);
            if (final is null) return null;

            // 去掉 \\?\ 扩展前缀，保持与词法比较同形；UNC 目标还原为 \\server\share。
            // Strip the \\?\ extended prefix so the shape matches lexical comparison;
            // UNC targets are restored to \\server\share.
            if (final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return "\\" + final[8..];
            return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 解析失败必须回落词法路径而不是中断校验；更宽的失败（如编码缺陷）不得被吞掉。
            // Resolution failure must fall back to the lexical path instead of aborting the
            // check; broader failures (such as coding defects) must not be swallowed.
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16,
                   SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(
        string path, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition,
        uint     flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static partial uint GetFinalPathNameByHandleW(
        SafeFileHandle file, IntPtr filePath, uint filePathLength, uint flags);
}

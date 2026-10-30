#if DEBUG
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         仅 Debug 构建的进程身份诊断：只读查询宿主与 helper 进程 token 的用户/SID/类型/提权/完整性级别，
///         并计算 helper 二进制的 SHA-256；任何查询失败只记录错误，不改变启动、等待或取消语义。
///     </para>
///     <para>
///         Debug-only process-identity diagnostics: read-only queries of the host and helper process tokens
///         (user/SID/type/elevation/integrity) plus a SHA-256 of the helper binary; any query failure only
///         logs an error and never changes launch, wait, or cancellation semantics.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         记录主体显式标注 host/helper：宿主 Desktop 的 token 与 probe 结果不得冒充 helper 实际身份，
///         helper 的子进程 token 在其存活期间用只读原生接口按 PID 查询，取不到时明确记录未取得。
///         只输出白名单字段（PID、SID、token 类型、完整性 RID、SHA-256），不读取环境、凭据或 payload。
///     </para>
///     <para>
///         Subjects are explicitly labeled host/helper: the Desktop host token and probe results never
///         impersonate the helper's actual identity; the helper child-process token is queried by PID through
///         read-only native interfaces while it is alive, and an unobtainable token is recorded as not obtained.
///         Only whitelisted fields are emitted (PID, SID, token type, integrity RID, SHA-256) — never
///         environments, credentials, or payloads.
///     </para>
/// </remarks>
internal static partial class SandboxProcessDiagnostics
{
    private const uint TokenQuery                     = 0x0008;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenUserClass                 = 1;
    private const uint TokenTypeClass                 = 8;
    private const uint TokenElevationClass            = 20;
    private const uint TokenIntegrityLevelClass       = 25;
    private const int  ErrorInsufficientBuffer        = 122;

    private static readonly object Sha256Gate = new();

    private static readonly Dictionary<string, string> Sha256ByPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     <para>记录宿主进程 token 的白名单身份字段；查询失败只记 win32Error，不抛出。</para>
    ///     <para>Logs whitelisted identity fields of the host process token; failures log the win32Error and never throw.</para>
    /// </summary>
    internal static void LogHostToken()
    {
        var (opened, error, description) = DescribeProcessToken((uint)Environment.ProcessId);
        SandboxDiagnosticLog.Event("host-token",
                                   opened
                                       ? $"subject=host pid={Environment.ProcessId} {description}"
                                       : $"subject=host pid={Environment.ProcessId} token=<not-obtained> win32Error={error}");
    }

    /// <summary>
    ///     <para>
    ///         在 helper 进程启动后立即记录其实际 token；进程已退出或查询失败时明确记录未取得与
    ///         Win32 错误码，不推断、不冒充，也不影响启动顺序、等待与取消。
    ///     </para>
    ///     <para>
    ///         Records the helper's actual token right after launch; if the process already exited or the query
    ///         failed, records explicitly not-obtained with the Win32 error code — never inferred, impersonated,
    ///         or allowed to affect launch order, waits, or cancellation.
    ///     </para>
    /// </summary>
    internal static void LogChildProcessToken(Process process)
    {
        var (opened, error, description) = DescribeProcessToken((uint)process.Id);
        SandboxDiagnosticLog.Event("helper-token",
                                   opened
                                       ? $"subject=helper pid={process.Id} {description}"
                                       : $"subject=helper pid={process.Id} token=<not-obtained> win32Error={error}");
    }

    /// <summary>
    ///     <para>按 PID 只读查询进程 token 并格式化为白名单字段；任何失败以 (false, Win32 错误码) 返回，不抛出。</para>
    ///     <para>
    ///         Queries a process token read-only by PID and formats whitelisted fields; any failure returns
    ///         (false, Win32 error) instead of throwing.
    ///     </para>
    /// </summary>
    internal static (bool Opened, int Win32Error, string Description) DescribeProcessToken(uint processId)
    {
        if (!OperatingSystem.IsWindows()) return (false, 0, "<non-windows>");
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return (false, Marshal.GetLastPInvokeError(), "<open-process-failed>");

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token))
                return (false, Marshal.GetLastPInvokeError(), "<open-process-token-failed>");

            using (token)
            {
                return (true, 0,
                        $"userSid={DescribeTokenSid(token, TokenUserClass)} " +
                        $"tokenType={DescribeTokenType(token)} "              +
                        $"elevation={DescribeTokenDword(token, TokenElevationClass,
                                                        value => value == 1 ? "elevated" : "notElevated")} " +
                        $"integrity={DescribeTokenIntegrity(token)}");
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>
    ///     <para>返回文件 SHA-256 的十六进制小写；失败记录异常类型，同一路径结果按进程缓存。</para>
    ///     <para>
    ///         Returns the lowercase hexadecimal SHA-256 of a file; failures record the exception type, cached per path per
    ///         process.
    ///     </para>
    /// </summary>
    internal static string DescribeFileSha256(string path)
    {
        lock (Sha256Gate)
        {
            if (Sha256ByPath.TryGetValue(path, out var cached)) return cached;
            string result;
            try
            {
                // 允许与杀毒扫描等并发读者共享；只读打开，不执行，不写入。
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                  FileShare.Read | FileShare.Write | FileShare.Delete);
                result = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
            catch (Exception ex)
            {
                result = $"<unavailable:{ex.GetType().Name}>";
            }

            Sha256ByPath[path] = result;
            return result;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string DescribeTokenSid(SafeAccessTokenHandle token, uint informationClass)
    {
        var buffer = QueryTokenBuffer(token, informationClass, out var error);
        if (buffer == IntPtr.Zero) return $"<unavailable:{error}>";
        try
        {
            // TOKEN_USER / TOKEN_MANDATORY_LABEL 的首成员都是 SID_AND_ATTRIBUTES；SID 指向缓冲区内。
            var sidPointer = Marshal.PtrToStructure<TokenSidAndAttributes>(buffer).Sid;
            return new SecurityIdentifier(sidPointer).ToString();
        }
        catch (Exception ex)
        {
            return $"<unavailable:{ex.GetType().Name}>";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string DescribeTokenType(SafeAccessTokenHandle token) =>
        DescribeTokenDword(token, TokenTypeClass, value => value switch
        {
            1 => "primary",
            2 => "impersonation",
            _ => $"<unknown:{value}>"
        });

    [SupportedOSPlatform("windows")]
    private static string DescribeTokenIntegrity(SafeAccessTokenHandle token)
    {
        var sid = DescribeTokenSid(token, TokenIntegrityLevelClass);
        if (sid.StartsWith('<')) return sid;
        // 完整性 SID 形如 S-1-16-XXXX；末段 RID 即完整性级别。
        var separator = sid.LastIndexOf('-');
        if (separator < 0 || !uint.TryParse(sid[(separator + 1)..], out var rid)) return $"<unparseable:{sid}>";
        var name = rid switch
        {
            0x0000 => "untrusted",
            0x1000 => "low",
            0x2000 => "medium",
            0x2100 => "mediumPlus",
            0x3000 => "high",
            0x4000 => "system",
            0x5000 => "protected",
            _      => "other"
        };
        return $"{name}(0x{rid:X})";
    }

    [SupportedOSPlatform("windows")]
    private static string DescribeTokenDword(
        SafeAccessTokenHandle token, uint informationClass, Func<uint, string> describe)
    {
        var buffer = QueryTokenBuffer(token, informationClass, out var error);
        if (buffer == IntPtr.Zero) return $"<unavailable:{error}>";
        try
        {
            return describe(unchecked((uint)Marshal.ReadInt32(buffer)));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IntPtr QueryTokenBuffer(SafeAccessTokenHandle token, uint informationClass, out int win32Error)
    {
        var length = 128u;
        while (true)
        {
            var buffer = Marshal.AllocHGlobal((int)length);
            if (GetTokenInformation(token, informationClass, buffer, length, out var needed))
            {
                win32Error = 0;
                return buffer;
            }

            Marshal.FreeHGlobal(buffer);
            win32Error = Marshal.GetLastPInvokeError();
            if (win32Error != ErrorInsufficientBuffer || needed <= length) return IntPtr.Zero;
            length = needed;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(
        uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        IntPtr processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,            uint     informationClass, IntPtr tokenInformation,
        uint                  tokenInformationLength, out uint returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenSidAndAttributes
    {
        public IntPtr Sid;

        public uint Attributes;
    }
}
#endif

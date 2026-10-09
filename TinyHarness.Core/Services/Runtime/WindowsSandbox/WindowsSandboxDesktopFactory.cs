using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     创建父进程拥有的私有桌面：名称为 CodexSandboxDesktop- + 32 位小写 hex；DACL 给父进程
///     用户 DESKTOP_ALL_ACCESS(0xf01ff)、给沙箱账户 DESKTOP_PARTICIPANT_ACCESS(0x201ff，排除
///     WRITE_DAC/WRITE_OWNER/DELETE)。句柄在命令生命周期内由父进程持有，跨命令不复用。
///     Creates the parent-owned private desktop: the name is CodexSandboxDesktop-
///     plus 32 lowercase hex digits; the DACL grants the parent user
///     DESKTOP_ALL_ACCESS (0xf01ff) and the sandbox account
///     DESKTOP_PARTICIPANT_ACCESS (0x201ff, excluding WRITE_DAC/WRITE_OWNER/
///     DELETE). The parent holds the handle for the command's lifetime and never
///     shares desktops across commands.
/// </summary>
public interface ISandboxDesktopFactory
{
    PrivateDesktop CreatePrivateDesktop(SecurityIdentifier sandboxAccountSid);
}

/// <summary>
///     私有桌面句柄；Dispose 关闭桌面。名称传入 spawn_request 时使用裸名（不带 WinSta0 前缀）。
///     A private desktop handle closed on Dispose. The bare name (without the
///     WinSta0 prefix) is what goes into spawn_request.
/// </summary>
public sealed partial class PrivateDesktop : IDisposable
{
    private bool _closed;

    public PrivateDesktop(string name, IntPtr handle)
    {
        Name   = name;
        Handle = handle;
    }

    public string Name { get; }

    internal IntPtr Handle { get; }

    public void Dispose()
    {
        if (_closed || Handle == IntPtr.Zero) return;

        _closed = true;
        if (!CloseDesktop(Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                                     $"Failed to close the private desktop '{Name}'.");
    }

    [LibraryImport("user32.dll", EntryPoint = "CloseDesktop", SetLastError = true)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(IntPtr desktopHandle);
}

/// <summary>
///     <see cref="ISandboxDesktopFactory" /> 的 Windows 实现（CreateDesktopW + SDDL DACL）。
///     The Windows implementation of <see cref="ISandboxDesktopFactory" />
///     (CreateDesktopW with an SDDL-built DACL).
/// </summary>
public sealed partial class WindowsSandboxDesktopFactory : ISandboxDesktopFactory
{
    internal const string DesktopNamePrefix        = "CodexSandboxDesktop-";
    internal const uint   DesktopAllAccess         = 0x000F_01FF;
    internal const uint   DesktopParticipantAccess = 0x0002_01FF;
    private const  uint   SddlRevision             = 1;

    public PrivateDesktop CreatePrivateDesktop(SecurityIdentifier sandboxAccountSid)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Private desktops are available on Windows only.");

        ArgumentNullException.ThrowIfNull(sandboxAccountSid);

        using var identity = WindowsIdentity.GetCurrent();
        var ownerSid = identity.User
                    ?? throw new InvalidOperationException("The current process has no user SID.");
        var name = DesktopNamePrefix + GenerateNameSuffix();
        var sddl = BuildSddl(ownerSid, sandboxAccountSid);
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SddlRevision,
                                                                  out var securityDescriptor) ||
            securityDescriptor == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                                     "Failed to build the private desktop security descriptor.");

        try
        {
            var attributes = new SecurityAttributesW
            {
                Length             = (uint)Marshal.SizeOf<SecurityAttributesW>(),
                SecurityDescriptor = securityDescriptor,
                InheritHandle      = 0
            };
            var handle = CreateDesktopW(name, null, IntPtr.Zero, 0, DesktopAllAccess, ref attributes);
            if (handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                                         $"Failed to create the private desktop '{name}'.");

            return new PrivateDesktop(name, handle);
        }
        finally
        {
            LocalFree(securityDescriptor);
        }
    }

    /// <summary>
    ///     32 位小写 hex 后缀，与库的 u128 零填充格式一致；使用加密随机源。
    ///     A 32-digit lowercase hex suffix matching the library's zero-padded u128
    ///     format, drawn from a cryptographic source.
    /// </summary>
    internal static string GenerateNameSuffix()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    /// <summary>
    ///     私有桌面 DACL 的 SDDL：给父进程用户 <see cref="DesktopAllAccess" />、给沙箱账户
    ///     <see cref="DesktopParticipantAccess" />；受保护、无继承。
    ///     The SDDL for the private desktop DACL: the parent user gets
    ///     <see cref="DesktopAllAccess" /> and the sandbox account
    ///     <see cref="DesktopParticipantAccess" />; protected, with no inheritance.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static string BuildSddl(SecurityIdentifier ownerSid, SecurityIdentifier sandboxAccountSid)
    {
        return
            $"D:P(A;;0x{DesktopAllAccess:x};;;{ownerSid.Value})(A;;0x{DesktopParticipantAccess:x};;;{sandboxAccountSid.Value})";
    }

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
                   SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string securityDescriptorString, uint revision, out IntPtr securityDescriptor);

    [LibraryImport("user32.dll", EntryPoint = "CreateDesktopW", SetLastError = true,
                   StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateDesktopW(string                  desktopName, string? device, IntPtr devMode,
                                                 uint                    flags,       uint    desiredAccess,
                                                 ref SecurityAttributesW securityAttributes);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributesW
    {
        public uint   Length;
        public IntPtr SecurityDescriptor;
        public uint   InheritHandle;
    }
}

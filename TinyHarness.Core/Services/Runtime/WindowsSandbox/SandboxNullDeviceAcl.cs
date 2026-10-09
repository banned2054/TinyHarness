using System.Runtime.InteropServices;
using System.Security.Principal;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
/// 给 capability SID 在 NUL 设备上追加 allow ACE（与库父进程及 runner 内的动作对齐）：
/// 打不开设备时静默跳过（与 Rust 行为一致），打开后授予失败则抛出——ACL 无法施加意味着
/// 沙箱可能失效，fail closed。
///
/// Appends an allow ACE for the capability SID on the NUL device (mirroring
/// both the library parent and the runner's own action): a failure to open
/// the device is skipped silently (matching Rust), while a failure to apply
/// the ACE after opening throws — an unenforceable ACL means the sandbox may
/// be broken, so we fail closed.
/// </summary>
internal static partial class SandboxNullDeviceAcl
{
    private const uint ReadControl = 0x0002_0000;
    private const uint WriteDac    = 0x0004_0000;
    private const uint FileShareRead  = 0x0000_0001;
    private const uint FileShareWrite = 0x0000_0002;
    private const uint FileGenericRead    = 0x0012_0089;
    private const uint FileGenericWrite   = 0x0012_0196;
    private const uint FileGenericExecute = 0x0012_00A0;
    private const uint SetAccessMode  = 2;    // ACCESS_MODE.SET_ACCESS
    private const uint NoInheritance  = 0;    // CONTAINER_INHERIT_ACE | OBJECT_INHERIT_ACE none
    private const uint DaclSecurityInformation = 0x0000_0004;

    public static void AllowCapabilitySid(SecurityIdentifier capabilitySid)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The NUL device ACL helper is available on Windows only.");
        }

        var sidBytes = new byte[capabilitySid.BinaryLength];
        capabilitySid.GetBinaryForm(sidBytes, 0);

        var deviceHandle = CreateFileW(@"\\.\NUL", ReadControl | WriteDac, FileShareRead | FileShareWrite,
                                       IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (deviceHandle == new IntPtr(-1))
        {
            // The Rust library also tolerates an unopenable NUL device here.
            return;
        }

        try
        {
            var getResult = GetSecurityInfo(deviceHandle, SeObjectType.KernelObject, DaclSecurityInformation,
                                            out _, out _, out var existingDacl, out _, out var securityDescriptor);
            if (getResult != 0)
            {
                throw new InvalidOperationException(
                                              $"Failed to read the NUL device DACL (Win32 error {getResult}).");
            }

            var sidPtr    = Marshal.AllocHGlobal(sidBytes.Length);
            var entryPtr  = Marshal.AllocHGlobal(Marshal.SizeOf<ExplicitAccessW>());
            var newDacl   = IntPtr.Zero;
            try
            {
                Marshal.Copy(sidBytes, 0, sidPtr, sidBytes.Length);
                var entry = new ExplicitAccessW
                {
                    AccessPermissions = FileGenericRead | FileGenericWrite | FileGenericExecute,
                    AccessMode        = SetAccessMode,
                    Inheritance       = NoInheritance,
                    Trustee           = BuildTrustee(sidPtr),
                };
                Marshal.StructureToPtr(entry, entryPtr, false);
                var entriesResult = SetEntriesInAclW(1, entryPtr, existingDacl, out newDacl);
                if (entriesResult != 0)
                {
                    throw new InvalidOperationException(
                                                  $"Failed to grant the NUL device ACE to capability SID {capabilitySid.Value} " +
                                                  $"(Win32 error {entriesResult}).");
                }

                var setResult = SetSecurityInfo(deviceHandle, SeObjectType.KernelObject,
                                                DaclSecurityInformation, IntPtr.Zero, IntPtr.Zero, newDacl,
                                                IntPtr.Zero);
                if (setResult != 0)
                {
                    throw new InvalidOperationException(
                                                  $"Failed to apply the NUL device DACL with the capability SID {capabilitySid.Value} " +
                                                  $"(Win32 error {setResult}).");
                }
            }
            finally
            {
                if (newDacl != IntPtr.Zero)
                {
                    LocalFree(newDacl);
                }

                LocalFree(securityDescriptor);
                Marshal.FreeHGlobal(sidPtr);
                Marshal.FreeHGlobal(entryPtr);
            }
        }
        finally
        {
            CloseHandle(deviceHandle);
        }
    }

    private static TrusteeW BuildTrustee(IntPtr sidPtr)
        => new()
        {
            MultipleTrustee          = IntPtr.Zero,
            MultipleTrusteeOperation = 0,
            TrusteeForm              = 0, // TRUSTEE_IS_SID
            TrusteeType              = 0, // TRUSTEE_IS_UNKNOWN
            Name                     = sidPtr,
        };

    private enum SeObjectType : uint
    {
        KernelObject = 0,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrusteeW
    {
        public IntPtr MultipleTrustee;
        public uint   MultipleTrusteeOperation;
        public uint   TrusteeForm;
        public uint   TrusteeType;
        public IntPtr Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExplicitAccessW
    {
        public uint     AccessPermissions;
        public uint     AccessMode;
        public uint     Inheritance;
        public TrusteeW Trustee;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
                   StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateFileW(string fileName, uint desiredAccess, uint shareMode,
                                              IntPtr securityAttributes, uint creationDisposition,
                                              uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial uint GetSecurityInfo(IntPtr handle, SeObjectType objectType,
                                                uint securityInfo, out IntPtr sidOwner, out IntPtr sidGroup,
                                                out IntPtr dacl, out IntPtr sacl, out IntPtr securityDescriptor);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial uint SetSecurityInfo(IntPtr handle, SeObjectType objectType,
                                                uint securityInfo, IntPtr sidOwner, IntPtr sidGroup,
                                                IntPtr dacl, IntPtr sacl);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial uint SetEntriesInAclW(uint countOfExplicitEntries,
                                                 IntPtr listOfExplicitEntries, IntPtr oldDacl,
                                                 out IntPtr newDacl);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);
}

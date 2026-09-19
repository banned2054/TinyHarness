using System.Runtime.InteropServices;
using System.Text;

namespace TinyHarness.Core.Services.Runtime;

/// <summary>
/// 基于 Windows 凭据管理器（Credential Manager，advapi32 CredRead/CredWrite/CredDelete）的凭据存储。
/// 条目以 GENERIC 凭据保存，密钥按 UTF-16LE 编码写入凭据 blob；只使用直接 P/Invoke，保持 NativeAOT 安全。
///
/// Credential store backed by Windows Credential Manager (advapi32 CredRead/CredWrite/CredDelete). Entries are
/// GENERIC credentials with the secret stored as a UTF-16LE blob; only direct P/Invoke is used, keeping NativeAOT safe.
/// </summary>
public sealed partial class WindowsCredentialStore : ICredentialStore
{
    private const uint GenericCredentialType = 1;
    private const uint PersistLocalMachine   = 2;

    /// <summary>
    /// 仅在 Windows 上可用。
    /// Available on Windows only.
    /// </summary>
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// 保存或覆盖凭据条目；目标名称与密钥非空校验在平台检查之后进行。
    /// Saves or overwrites a credential entry; target/secret validation happens after the platform check.
    /// </summary>
    public void Save(string targetName, string secret)
    {
        ThrowIfUnsupported();
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        ArgumentException.ThrowIfNullOrEmpty(secret);

        var blob      = Encoding.Unicode.GetBytes(secret);
        var targetPtr = Marshal.StringToHGlobalUni(targetName);
        var blobPtr   = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var credential = new CredentialW
            {
                Flags              = 0,
                Type               = GenericCredentialType,
                TargetName         = targetPtr,
                Comment            = IntPtr.Zero,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob     = blobPtr,
                Persist            = PersistLocalMachine,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new
                    InvalidOperationException($"Windows Credential Manager refused to save credential '{targetName}' (Win32 error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(targetPtr);
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    /// <summary>
    /// 读取凭据条目的密钥；条目不存在返回 <see langword="null"/>，其他 Win32 失败抛出异常。
    /// Reads a credential's secret; returns <see langword="null"/> when the entry is missing, throws on other Win32 failures.
    /// </summary>
    public string? Read(string targetName)
    {
        ThrowIfUnsupported();
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);

        if (!CredRead(targetName, GenericCredentialType, 0, out var credentialPtr))
        {
            var error = Marshal.GetLastWin32Error();
            // ERROR_NOT_FOUND (1168): the credential does not exist.
            return error == 1168
                ? null
                : throw new
                    InvalidOperationException($"Windows Credential Manager failed to read credential '{targetName}' (Win32 error {error}).");
        }

        try
        {
            var credential = Marshal.PtrToStructure<CredentialW>(credentialPtr);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize <= 0)
            {
                return null;
            }

            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            return Encoding.Unicode.GetString(blob);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    /// <summary>
    /// 删除凭据条目；返回该条目是否存在并被删除。
    /// Deletes a credential entry; returns whether it existed and was deleted.
    /// </summary>
    public bool Delete(string targetName)
    {
        ThrowIfUnsupported();
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);

        if (!CredDelete(targetName, GenericCredentialType, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168)
            {
                // ERROR_NOT_FOUND: nothing to delete.
                return false;
            }

            throw new
                InvalidOperationException($"Windows Credential Manager failed to delete credential '{targetName}' (Win32 error {error}).");
        }

        return true;
    }

    /// <summary>
    /// 非 Windows 平台直接失败，由调用方向用户提供环境变量等替代方案。
    /// Fails on non-Windows platforms so callers can offer environment-variable alternatives to the user.
    /// </summary>
    private void ThrowIfUnsupported()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                                                    "The Windows Credential Manager store is only available on Windows.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CredentialW
    {
        public uint     Flags;
        public uint     Type;
        public IntPtr   TargetName;
        public IntPtr   Comment;
        public FileTime LastWritten;
        public uint     CredentialBlobSize;
        public IntPtr   CredentialBlob;
        public uint     Persist;
        public uint     AttributeCount;
        public IntPtr   Attributes;
        public IntPtr   TargetAlias;
        public IntPtr   UserName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint DateTimeLow;
        public uint DateTimeHigh;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true,
                   StringMarshalling = StringMarshalling.Utf16)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string     targetName, uint credentialType, uint flags,
                                         out IntPtr credentialPtr);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(ref CredentialW credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true,
                   StringMarshalling = StringMarshalling.Utf16)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string targetName, uint credentialType, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr credential);
}

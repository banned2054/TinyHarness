using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         槽位容器的路径规则：容器固定为 &lt;sandboxHome&gt;\runtime-tmp-v2，槽位为容器下 slots\slot-XX。
///         父容器位于沙箱写权限之外，由普通宿主创建与管理。
///     </para>
///     <para>
///         Path rules for the slot container: the container is fixed at
///         &lt;sandboxHome&gt;\runtime-tmp-v2 and slots are slots\slot-XX inside it. The parent
///         container sits outside sandbox write permissions and is created and managed by the
///         ordinary host.
///     </para>
/// </summary>
/// <remarks>
///     <para>本类型只推导与解析路径及槽位名，不创建目录、不修改 ACL。</para>
///     <para>This type only derives and parses paths and slot names: it never creates directories or touches ACLs.</para>
/// </remarks>
internal static class SandboxRuntimeTempSlotLayout
{
    internal const string ContainerDirectoryName = "runtime-tmp-v2";
    internal const string SlotsDirectoryName     = "slots";
    internal const string SlotNamePrefix         = "slot-";
    internal const string WritableDirectoryName  = "work";
    internal const int    SingleSlotIndex        = 0;
    internal const int    MaximumSlotCount       = 100;

    /// <summary>
    ///     <para>返回容器根目录；拒绝相对路径与空白入参。</para>
    ///     <para>Returns the container root; relative paths and blank input are rejected.</para>
    /// </summary>
    internal static string ContainerDirectory(string sandboxHome) =>
        Path.Combine(RequireAbsoluteHome(sandboxHome), ContainerDirectoryName);

    /// <summary>
    ///     <para>返回槽位父目录（容器下 slots）；槽位目录只能由宿主在此创建。</para>
    ///     <para>
    ///         Returns the slot parent directory (slots under the container); only the host creates slots
    ///         there.
    ///     </para>
    /// </summary>
    internal static string SlotsDirectory(string sandboxHome) =>
        Path.Combine(ContainerDirectory(sandboxHome), SlotsDirectoryName);

    /// <summary>
    ///     <para>按索引格式化槽位名（slot-00 起两位十进制）；索引越界时拒绝。</para>
    ///     <para>
    ///         Formats the slot name for an index (slot-00 onwards, two decimal digits); out-of-range
    ///         indexes are rejected.
    ///     </para>
    /// </summary>
    internal static string SlotName(int slotIndex)
    {
        if (slotIndex is < 0 or >= MaximumSlotCount)
            throw new ArgumentOutOfRangeException(
                nameof(slotIndex), slotIndex,
                $"A runtime temp slot index must be between 0 and {MaximumSlotCount - 1}.");

        return SlotNamePrefix + slotIndex.ToString("D2", CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     <para>按索引返回槽位不可写根目录的绝对路径。</para>
    ///     <para>Returns the absolute path of the immutable slot root directory for an index.</para>
    /// </summary>
    internal static string SlotDirectory(string sandboxHome, int slotIndex) =>
        Path.Combine(SlotsDirectory(sandboxHome), SlotName(slotIndex));

    /// <summary>
    ///     <para>
    ///         按索引返回槽位下沙箱可写子目录（<c>slot-XX\work</c>）的绝对路径；槽位根自身对沙箱不可写，
    ///         防止运行期被原地改为重解析点。
    ///     </para>
    ///     <para>
    ///         Returns the absolute path of the sandbox-writable subdirectory (<c>slot-XX\work</c>) for an index;
    ///         the slot root itself is non-writable to the sandbox so it cannot be turned into a reparse point in
    ///         place at runtime.
    ///     </para>
    /// </summary>
    internal static string WritableDirectory(string sandboxHome, int slotIndex) =>
        Path.Combine(SlotDirectory(sandboxHome, slotIndex), WritableDirectoryName);

    /// <summary>
    ///     <para>解析槽位名；非法名称返回 false 而不抛异常，供池枚举与诊断使用。</para>
    ///     <para>
    ///         Parses a slot name; an invalid name returns false instead of throwing, for pool enumeration
    ///         and diagnostics.
    ///     </para>
    /// </summary>
    internal static bool TryParseSlotName(string? name, out int slotIndex)
    {
        slotIndex = -1;
        if (name is null || !name.StartsWith(SlotNamePrefix, StringComparison.Ordinal)) return false;

        var digits = name[SlotNamePrefix.Length..];
        if (digits.Length != 2 ||
            !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed is < 0 or >= MaximumSlotCount)
            return false;

        slotIndex = parsed;
        return true;
    }

    private static string RequireAbsoluteHome(string sandboxHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxHome);
        if (!Path.IsPathFullyQualified(sandboxHome))
            throw new ArgumentException($"The sandbox home must be a fully qualified absolute path: '{sandboxHome}'.",
                                        nameof(sandboxHome));

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(sandboxHome));
    }
}

/// <summary>
///     <para>
///         槽位目录的卷与 128 位文件标识快照（经 <c>FileIdInfo</c> 读取，在 NTFS 与 ReFS 上均唯一）：创建时
///         捕获，spawn 前校验与退役回收时复核，用于拒绝被替换的目录。
///     </para>
///     <para>
///         Volume and 128-bit file identity snapshot of a slot directory (read via <c>FileIdInfo</c>, unique on
///         both NTFS and ReFS): captured on creation, re-checked before spawn and again before retirement
///         reclamation, so a replaced directory is refused.
///     </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed record SandboxRuntimeTempSlotIdentity(string Path, ulong VolumeSerialNumber, UInt128 FileId)
{
    /// <summary>
    ///     <para>
    ///         从已打开的目录句柄捕获 64 位卷序列号与 128 位文件 ID；句柄必须指向目录本身而不是其链接目标，
    ///         不支持 <c>FileIdInfo</c> 的文件系统直接失败（fail-closed）。
    ///     </para>
    ///     <para>
    ///         Captures the 64-bit volume serial number and 128-bit file ID from an already opened directory
    ///         handle pointing at the directory itself; filesystems without <c>FileIdInfo</c> support fail closed.
    ///     </para>
    /// </summary>
    internal static SandboxRuntimeTempSlotIdentity Capture(SafeFileHandle directoryHandle, string path)
    {
        ArgumentNullException.ThrowIfNull(directoryHandle);
        if (!SandboxRuntimeTempSlotNative.TryGetFileIdInformation(directoryHandle, out var information, out var error))
            throw new Win32Exception(
                error,
                $"Failed to read the 128-bit FileIdInfo directory identity of '{path}' " +
                "(the volume must support FileIdInfo such as NTFS or ReFS).");

        return FromFileIdInformation(path, information);
    }

    internal static SandboxRuntimeTempSlotIdentity FromFileIdInformation(
        string path, SandboxRuntimeTempSlotNative.FileIdInfo information) =>
        new(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)),
            information.VolumeSerialNumber,
            new UInt128(information.FileIdHigh, information.FileIdLow));

    /// <summary>
    ///     <para>核对同一目录身份：64 位卷序列号与 128 位文件标识都必须一致。</para>
    ///     <para>
    ///         Checks identity equality: both the 64-bit volume serial number and the 128-bit file identifier
    ///         must match.
    ///     </para>
    /// </summary>
    internal bool Matches(SandboxRuntimeTempSlotIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return VolumeSerialNumber == other.VolumeSerialNumber && FileId == other.FileId;
    }

    internal string FormatForDiagnostics() => $"volume 0x{VolumeSerialNumber:x16} file 0x{FileId:x32}";
}

/// <summary>
///     <para>
///         已初始化的槽位：不可写槽位根目录（<c>slot-XX</c>）及其 128 位身份、沙箱可写子目录
///         （<c>slot-XX\work</c>）及其 128 位身份，以及生成其 DACL 的主体模板。
///     </para>
///     <para>
///         An initialized slot: the immutable slot root (<c>slot-XX</c>) and its 128-bit identity, the
///         sandbox-writable child directory (<c>slot-XX\work</c>) and its 128-bit identity, and the principal
///         template that produced their DACLs.
///     </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed record SandboxRuntimeTempSlot(
    string                         Directory,
    SandboxRuntimeTempSlotIdentity Identity,
    string                         WritableDirectory,
    SandboxRuntimeTempSlotIdentity WritableIdentity,
    SandboxRuntimeTempSlotAclSpec  AclSpec);

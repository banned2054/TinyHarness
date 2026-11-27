using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         槽位父容器（<c>&lt;sandboxHome&gt;\runtime-tmp-v2</c> 及其下 <c>slots</c>）的 DACL 主体模板：宿主
///         owner 与系统管理身份获得完全控制，共享沙箱身份仅获得最小穿越与读属性权限，不含任何单槽位
///         capability SID，因此多槽位并发初始化对父容器完全幂等、互不覆盖。本类型仅允许通过
///         <see cref="Create" /> 构造，保证模板始终处于有效状态。
///     </para>
///     <para>
///         The DACL principal template for the slot parent container (<c>&lt;sandboxHome&gt;\runtime-tmp-v2</c>
///         and its <c>slots</c> subdirectory): the host owner and system management identities get full control,
///         shared sandbox identities get minimal traverse and attribute-read rights only, and no per-slot
///         capability SID is embedded so initializing multiple slots stays idempotent and never clobbers another
///         slot. Instances can only be built through <see cref="Create" />, keeping every template valid.
///     </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SandboxRuntimeTempContainerAclSpec
{
    private SandboxRuntimeTempContainerAclSpec(
        SecurityIdentifier                hostOwner,
        IReadOnlyList<SecurityIdentifier> managementSids,
        IReadOnlyList<SecurityIdentifier> sandboxSharedSids)
    {
        HostOwner         = hostOwner;
        ManagementSids    = managementSids;
        SandboxSharedSids = sandboxSharedSids;
    }

    internal SecurityIdentifier HostOwner { get; }

    internal IReadOnlyList<SecurityIdentifier> ManagementSids { get; }

    internal IReadOnlyList<SecurityIdentifier> SandboxSharedSids { get; }

    /// <summary>
    ///     <para>创建并校验容器主体模板；空集合、重复主体与宽泛/特殊主体一律拒绝。</para>
    ///     <para>
    ///         Creates and validates the container principal template; empty sets, duplicate principals, and
    ///         overly broad or special principals are refused.
    ///     </para>
    /// </summary>
    internal static SandboxRuntimeTempContainerAclSpec Create(
        SecurityIdentifier                hostOwner,
        IReadOnlyList<SecurityIdentifier> managementSids,
        IReadOnlyList<SecurityIdentifier> sandboxSharedSids)
    {
        ArgumentNullException.ThrowIfNull(hostOwner);
        ArgumentNullException.ThrowIfNull(managementSids);
        ArgumentNullException.ThrowIfNull(sandboxSharedSids);

        if (managementSids.Count == 0)
            throw new ArgumentException("At least one management SID is required for a runtime temp container ACL.",
                                        nameof(managementSids));
        if (sandboxSharedSids.Count == 0)
            throw new ArgumentException("At least one shared sandbox SID is required for a runtime temp container ACL.",
                                        nameof(sandboxSharedSids));

        var all = new List<SecurityIdentifier>(1 + managementSids.Count + sandboxSharedSids.Count) { hostOwner };
        all.AddRange(managementSids);
        all.AddRange(sandboxSharedSids);
        SandboxRuntimeTempSlotAclSpec.ValidatePrincipals(all, nameof(sandboxSharedSids));

        return new SandboxRuntimeTempContainerAclSpec(hostOwner, [.. managementSids], [.. sandboxSharedSids]);
    }
}

/// <summary>
///     <para>
///         单槽位目录的 DACL 主体模板：宿主 owner 与系统管理身份获得完全控制，Windows 受限令牌两轮访问检查
///         的两侧主体——第一轮共享沙箱组（如 <c>TinyHarnessUsers</c>）与第二轮本槽位专属 capability SID——均
///         在槽位上显式获得槽位写权限（不含删除根自身）与根保护 deny，同时记录必填的实际沙箱账户 SID 以验证
///         <c>WRITE_RESTRICTED</c> 跨槽写隔离。本类型仅允许通过 <see cref="Create" /> 构造，保证模板始终有效。
///     </para>
///     <para>
///         The DACL principal template for one slot directory: the host owner and system management identities
///         get full control, both sides of Windows restricted-token two-round access checks — the first-round
///         shared sandbox group (such as <c>TinyHarnessUsers</c>) and the second-round per-slot capability SID —
///         explicitly receive slot write rights (excluding root deletion) plus the root-protection deny, and
///         required actual sandbox account SIDs are carried for <c>WRITE_RESTRICTED</c> cross-slot write
///         isolation validation. Instances can only be built through <see cref="Create" />.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         固定版本 Rust runner 使用 <c>CreateRestrictedToken(..., WRITE_RESTRICTED, ...)</c>，且把沙箱账户 SID
///         一并放入第二轮 restricting SIDs。<c>WRITE_RESTRICTED</c> 仅对写访问执行两轮交集检查：第一轮共享沙箱
///         主体（必须是共享沙箱组如 <c>TinyHarnessUsers</c>，严禁放沙箱账户 SID）与第二轮本槽位专属 capability
///         SID 必须在槽位上同时具备写权限；同账户另一并发槽位的 restricting SIDs 含沙箱账户 SID 与另一槽位
///         capability，均不匹配本槽位 DACL，从而在第二轮写检查中得到 0。同时槽位向子项传播
///         <c>OWNER RIGHTS</c>（<c>S-1-3-4</c>）仅授 <c>READ_CONTROL</c>，关闭 Windows 向子项所有者（共享沙箱
///         账户）默认隐式授予的 <c>WRITE_DAC</c>。
///     </para>
///     <para>
///         本模板提供的跨槽写隔离覆盖槽位根以及沿用槽位写权限规则的子项；若槽位内进程主动为子项设置受保护
///         DACL（<c>SE_DACL_PROTECTED</c> 阻断继承）或通过显式 ACE 改写写授权，此类独立 DACL 属于共享沙箱
///         账户模型下已接受的已知边界，根目录 ACL 无法约束。
///     </para>
///     <para>
///         The pinned Rust runner uses <c>CreateRestrictedToken(..., WRITE_RESTRICTED, ...)</c> and includes the
///         sandbox account SID in its restricting SIDs. <c>WRITE_RESTRICTED</c> enforces the two-round
///         intersection on write access: both the first-round shared sandbox group (such as
///         <c>TinyHarnessUsers</c> — never the sandbox account SID itself) and this slot's dedicated capability
///         SID must hold write rights on the slot, while another concurrent slot under the same account carries
///         only the account SID and its own capability SID in round 2 and gets zero write rights. In addition,
///         the slot propagates an <c>OWNER RIGHTS</c> (<c>S-1-3-4</c>) <c>READ_CONTROL</c>-only ACE to children
///         to strip Windows' default implicit owner <c>WRITE_DAC</c>.
///     </para>
///     <para>
///         This template's cross-slot write isolation covers the slot root and children that follow the slot's
///         inherited write-permission rules; children created with an independent protected DACL
///         (<c>SE_DACL_PROTECTED</c>) or explicit ACEs that alter write grants are an accepted boundary of the
///         shared-account model that root ACLs cannot enforce.
///     </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class SandboxRuntimeTempSlotAclSpec
{
    private SandboxRuntimeTempSlotAclSpec(
        SecurityIdentifier                hostOwner,
        IReadOnlyList<SecurityIdentifier> managementSids,
        IReadOnlyList<SecurityIdentifier> sandboxSharedSids,
        IReadOnlyList<SecurityIdentifier> sandboxAccountSids,
        SecurityIdentifier                slotCapabilitySid)
    {
        HostOwner          = hostOwner;
        ManagementSids     = managementSids;
        SandboxSharedSids  = sandboxSharedSids;
        SandboxAccountSids = sandboxAccountSids;
        SlotCapabilitySid  = slotCapabilitySid;
    }

    internal SecurityIdentifier HostOwner { get; }

    internal IReadOnlyList<SecurityIdentifier> ManagementSids { get; }

    internal IReadOnlyList<SecurityIdentifier> SandboxSharedSids { get; }

    internal IReadOnlyList<SecurityIdentifier> SandboxAccountSids { get; }

    internal SecurityIdentifier SlotCapabilitySid { get; }

    /// <summary>
    ///     <para>
    ///         创建并校验槽位主体模板；管理主体、共享沙箱组、实际沙箱账户 SID 集合均不得为空，且所有角色间
    ///         不得重复（从而在写入任何 DACL 前即拒绝沙箱账户 SID 混入共享写主体）、不得包含宽泛或特殊主体。
    ///     </para>
    ///     <para>
    ///         Creates and validates the slot principal template; management, shared sandbox group, and actual
    ///         sandbox account SID sets must all be non-empty, mutually disjoint (refusing any sandbox account SID
    ///         mixed into shared write principals before any DACL is written), and free of overly broad or special
    ///         principals.
    ///     </para>
    /// </summary>
    internal static SandboxRuntimeTempSlotAclSpec Create(
        SecurityIdentifier                hostOwner,
        IReadOnlyList<SecurityIdentifier> managementSids,
        IReadOnlyList<SecurityIdentifier> sandboxSharedSids,
        IReadOnlyList<SecurityIdentifier> sandboxAccountSids,
        SecurityIdentifier                slotCapabilitySid)
    {
        ArgumentNullException.ThrowIfNull(hostOwner);
        ArgumentNullException.ThrowIfNull(managementSids);
        ArgumentNullException.ThrowIfNull(sandboxSharedSids);
        ArgumentNullException.ThrowIfNull(sandboxAccountSids);
        ArgumentNullException.ThrowIfNull(slotCapabilitySid);

        if (managementSids.Count == 0)
            throw new ArgumentException("At least one management SID is required for a runtime temp slot ACL.",
                                        nameof(managementSids));
        if (sandboxSharedSids.Count == 0)
            throw new ArgumentException("At least one shared sandbox SID is required for a runtime temp slot ACL.",
                                        nameof(sandboxSharedSids));
        if (sandboxAccountSids.Count == 0)
            throw new ArgumentException(
                "At least one actual sandbox account SID is required to enforce WRITE_RESTRICTED cross-slot write isolation.",
                nameof(sandboxAccountSids));

        var all = new List<SecurityIdentifier>(
            2 + managementSids.Count + sandboxSharedSids.Count + sandboxAccountSids.Count)
        {
            hostOwner
        };
        all.AddRange(managementSids);
        all.AddRange(sandboxSharedSids);
        all.AddRange(sandboxAccountSids);
        all.Add(slotCapabilitySid);
        ValidatePrincipals(all, nameof(sandboxSharedSids));

        return new SandboxRuntimeTempSlotAclSpec(hostOwner, [.. managementSids], [.. sandboxSharedSids],
                                                 [.. sandboxAccountSids], slotCapabilitySid);
    }

    /// <summary>
    ///     <para>派生对应的容器级 ACL 模板（剥离单槽位 capability SID 与沙箱账户 SID）。</para>
    ///     <para>Derives the matching container-level ACL template without per-slot capability or account SIDs.</para>
    /// </summary>
    internal SandboxRuntimeTempContainerAclSpec ToContainerSpec() =>
        SandboxRuntimeTempContainerAclSpec.Create(HostOwner, ManagementSids, SandboxSharedSids);

    /// <summary>
    ///     <para>是否为不应出现在模板主体集合中的宽泛或特殊主体（Everyone、OWNER RIGHTS、CREATOR OWNER 等）。</para>
    ///     <para>
    ///         Whether this is a broad or special principal (Everyone, OWNER RIGHTS, CREATOR OWNER, etc.) that
    ///         must not be supplied as a template principal.
    ///     </para>
    /// </summary>
    internal static bool IsOverlyBroad(SecurityIdentifier sid)
    {
        ArgumentNullException.ThrowIfNull(sid);
        return sid.IsWellKnown(WellKnownSidType.WorldSid)             ||
               sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid) ||
               sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid)      ||
               sid.IsWellKnown(WellKnownSidType.BuiltinGuestsSid)     ||
               sid.IsWellKnown(WellKnownSidType.InteractiveSid)       ||
               sid.IsWellKnown(WellKnownSidType.AnonymousSid)         ||
               sid.IsWellKnown(WellKnownSidType.NetworkSid)           ||
               sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid)      ||
               string.Equals(sid.Value, SandboxRuntimeTempSlotAcl.OwnerRightsSid.Value,
                             StringComparison.OrdinalIgnoreCase);
    }

    internal static void ValidatePrincipals(IReadOnlyList<SecurityIdentifier> all, string parameterName)
    {
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sid in all)
        {
            ArgumentNullException.ThrowIfNull(sid);
            if (IsOverlyBroad(sid))
                throw new ArgumentException(
                    $"A runtime temp slot ACL must not name the overly broad or special principal '{sid.Value}'.",
                    parameterName);
            if (!distinct.Add(sid.Value))
                throw new ArgumentException($"Duplicate principal '{sid.Value}' in the runtime temp slot ACL template.",
                                            parameterName);
        }
    }
}

/// <summary>
///     <para>
///         槽位与父容器的受保护显式 DACL 模板与单句柄套用入口：模板集中定义权限位，校验器读取同一份常量
///         核对磁盘 DACL，避免授予与校验漂移；所有写入都绑定到调用方已校验的目录句柄。
///     </para>
///     <para>
///         The protected explicit DACL template for slots and their parent container, plus handle-bound apply
///         entry points: the template owns the rights masks and the validator checks the on-disk DACL against
///         the same constants so grants and checks cannot drift, and every write is bound to a caller-validated
///         directory handle.
///     </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SandboxRuntimeTempSlotAcl
{
    /// <summary>
    ///     <para>Windows <c>OWNER RIGHTS</c>（<c>S-1-3-4</c>）SID：用于限制子项所有者不再隐式获得 <c>WRITE_DAC</c>。</para>
    ///     <para>
    ///         The Windows <c>OWNER RIGHTS</c> (<c>S-1-3-4</c>) SID used to strip the implicit <c>WRITE_DAC</c>
    ///         right from child objects owned by a shared sandbox account.
    ///     </para>
    /// </summary>
    internal static readonly SecurityIdentifier OwnerRightsSid = new("S-1-3-4");

    /// <summary>
    ///     <para>
    ///         槽位内子项继承的 <c>OWNER RIGHTS</c> 权限：仅保留 <c>READ_CONTROL</c>，彻底关闭 Windows 对子项
    ///         Owner 的默认隐式 <c>WRITE_DAC</c>。宿主与管理身份仍经显式 <c>FullControl</c> 保持完整权限。
    ///     </para>
    ///     <para>
    ///         Child-inherited <c>OWNER RIGHTS</c> mask: <c>READ_CONTROL</c> only, stripping Windows' default
    ///         implicit owner <c>WRITE_DAC</c>. The host and management identities keep full control via their
    ///         explicit inheritable <c>FullControl</c> ACEs.
    ///     </para>
    /// </summary>
    internal const uint OwnerRightsChildRights = SandboxRuntimeTempSlotNative.ReadControl;

    /// <summary>
    ///     <para>
    ///         槽位根上的槽位写权限：读、写、执行、写属性、读控制与同步；刻意不含 <c>DELETE</c>、
    ///         <c>WRITE_DAC</c> 与 <c>WRITE_OWNER</c>，使沙箱能在槽位内建文件但无法删除或篡改槽位根自身。
    ///     </para>
    ///     <para>
    ///         Slot-root write rights: read, write, execute, write attributes, read control, and synchronize;
    ///         deliberately excluding <c>DELETE</c>, <c>WRITE_DAC</c>, and <c>WRITE_OWNER</c> so the sandbox can
    ///         create entries inside the slot without deleting or rewriting the slot root itself.
    ///     </para>
    /// </summary>
    internal const uint SlotRootWriteRights = 0x0012_01BF; // Modify minus DELETE

    /// <summary>
    ///     <para>槽位内子项的继承写权限：完整 Modify，允许清理自己创建的临时文件与子目录。</para>
    ///     <para>
    ///         Inherited write rights on children inside the slot: full Modify, so the sandbox can clean up the
    ///         temporary files and subdirectories it created.
    ///     </para>
    /// </summary>
    internal const uint SlotChildWriteRights = 0x0013_01BF; // Modify

    /// <summary>
    ///     <para>槽位根上对沙箱主体显式 deny 的权限：删除槽位根自身、改 DACL 与改 owner。</para>
    ///     <para>
    ///         Rights explicitly denied to sandbox principals on the slot root: delete the root itself, rewrite
    ///         the DACL, and change ownership.
    ///     </para>
    /// </summary>
    internal const uint RootDeniedRights = 0x000D_0000; // DELETE | WRITE_DAC | WRITE_OWNER

    /// <summary>
    ///     <para>容器目录上对共享沙箱主体显式 deny 的权限：全部写类权限（含建子项、删子项、删容器与改安全属性）。</para>
    ///     <para>
    ///         Rights explicitly denied to shared sandbox principals on the container: all write-class rights
    ///         (creating or deleting children, deleting the container, and mutating security metadata).
    ///     </para>
    /// </summary>
    internal const uint ContainerDeniedRights = SandboxRuntimeTempSlotContract.WriteBits;

    /// <summary>
    ///     <para>容器上的最小穿越权限：只允许穿越到槽位与读属性，不能列目录或写入容器。</para>
    ///     <para>
    ///         Minimal container rights: traverse to a slot and read attributes only, without listing or writing
    ///         container content.
    ///     </para>
    /// </summary>
    internal const uint ContainerTraverseRights = 0x0010_00A0; // FILE_TRAVERSE | FILE_READ_ATTRIBUTES | SYNCHRONIZE

    /// <summary>
    ///     <para>宿主与管理身份在槽位与容器上的完全控制权限。</para>
    ///     <para>Full-control rights granted to the host and management identities on slots and the container.</para>
    /// </summary>
    internal const uint ManagementRights = 0x001F_01FF; // FullControl

    private const uint InheritBoth = SandboxRuntimeTempSlotNative.ContainerInheritAce |
                                     SandboxRuntimeTempSlotNative.ObjectInheritAce;
    private const uint InheritToChildrenOnly = InheritBoth | SandboxRuntimeTempSlotNative.InheritOnlyAce;

    /// <summary>
    ///     <para>
    ///         生成槽位目录的显式 ACE 列表；deny 在前，allow 在后。两轮写主体（共享沙箱组与本槽位 capability
    ///         SID）获得对称的根保护 deny、根无删除写权限与子项继承 Modify 权限，同时向子项传播
    ///         <c>OWNER RIGHTS</c>（<c>S-1-3-4</c>）仅 <c>READ_CONTROL</c> 以剥夺子项 Owner 的隐式
    ///         <c>WRITE_DAC</c>。
    ///     </para>
    ///     <para>
    ///         Builds the explicit ACE list for a slot directory with denies before allows. Both rounds of write
    ///         principals (shared sandbox groups and this slot's capability SID) receive the same root-protection
    ///         deny, delete-free root write rights, and inherited child Modify rights, while a child-only
    ///         <c>OWNER RIGHTS</c> (<c>S-1-3-4</c>) <c>READ_CONTROL</c> ACE strips implicit owner
    ///         <c>WRITE_DAC</c> on child entries.
    ///     </para>
    /// </summary>
    internal static IReadOnlyList<SandboxRuntimeTempSlotNative.RawAclEntry> BuildSlotDacl(
        SandboxRuntimeTempSlotAclSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var entries = new List<SandboxRuntimeTempSlotNative.RawAclEntry>();
        foreach (var sid in SlotSandboxPrincipals(spec))
            entries.Add(new SandboxRuntimeTempSlotNative.RawAclEntry(true, sid, RootDeniedRights,
                                                                     SandboxRuntimeTempSlotNative.NoInherit));

        entries.Add(new SandboxRuntimeTempSlotNative.RawAclEntry(false, spec.HostOwner, ManagementRights, InheritBoth));
        foreach (var sid in spec.ManagementSids)
            entries.Add(new SandboxRuntimeTempSlotNative.RawAclEntry(false, sid, ManagementRights, InheritBoth));

        // 向子项传播 OWNER RIGHTS (S-1-3-4) 仅授 READ_CONTROL：关闭共享沙箱账户作为子项所有者时的隐式 WRITE_DAC。
        entries.Add(new SandboxRuntimeTempSlotNative.RawAclEntry(false, OwnerRightsSid, OwnerRightsChildRights,
                                                                 InheritToChildrenOnly));

        foreach (var sid in SlotSandboxPrincipals(spec))
        {
            // 槽位根只给无删除写，子项继承完整 Modify，保证沙箱既能正常读写临时文件又删不掉槽位根。
            entries.Add(new SandboxRuntimeTempSlotNative.RawAclEntry(false, sid, SlotRootWriteRights,
                                                                     SandboxRuntimeTempSlotNative.NoInherit));
            entries.Add(new SandboxRuntimeTempSlotNative.RawAclEntry(false, sid, SlotChildWriteRights,
                                                                     InheritToChildrenOnly));
        }

        return entries;
    }

    /// <summary>
    ///     <para>生成容器目录的显式 ACE 列表：管理身份完全控制，共享沙箱身份显式拒写且仅允许最小穿越。</para>
    ///     <para>
    ///         Builds the explicit ACE list for a container directory: management identities get full control,
    ///         while shared sandbox identities are explicitly denied writes and granted minimal traverse only.
    ///     </para>
    /// </summary>
    internal static IReadOnlyList<SandboxRuntimeTempSlotNative.RawAclEntry> BuildContainerDacl(
        SandboxRuntimeTempContainerAclSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var entries = new List<SandboxRuntimeTempSlotNative.RawAclEntry>();
        foreach (var sid in spec.SandboxSharedSids)
            entries.Add(new SandboxRuntimeTempSlotNative.RawAclEntry(true, sid, ContainerDeniedRights,
                                                                     SandboxRuntimeTempSlotNative.NoInherit));

        entries.Add(new SandboxRuntimeTempSlotNative.RawAclEntry(false, spec.HostOwner, ManagementRights, InheritBoth));
        foreach (var sid in spec.ManagementSids)
            entries.Add(new SandboxRuntimeTempSlotNative.RawAclEntry(false, sid, ManagementRights, InheritBoth));
        foreach (var sid in spec.SandboxSharedSids)
            entries.Add(new SandboxRuntimeTempSlotNative.RawAclEntry(false, sid, ContainerTraverseRights,
                                                                     SandboxRuntimeTempSlotNative.NoInherit));

        return entries;
    }

    /// <summary>
    ///     <para>
    ///         在已校验的非重解析点目录句柄上写入槽位受保护显式 DACL，并在 owner 不符时经同句柄重开固定为宿主
    ///         用户；全程不按路径重开。
    ///     </para>
    ///     <para>
    ///         Writes the slot's protected explicit DACL through a validated non-reparse directory handle and
    ///         pins the owner via same-handle reopen if it differs; no path-based reopen is performed.
    ///     </para>
    /// </summary>
    internal static void ApplySlotHandle(
        SafeFileHandle handle, string slotPath, SandboxRuntimeTempSlotAclSpec spec)
    {
        SandboxRuntimeTempSlotNative.EnsureWindows();
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotPath);
        ArgumentNullException.ThrowIfNull(spec);

        var result = SandboxRuntimeTempSlotNative.ApplyProtectedDacl(handle, BuildSlotDacl(spec));
        if (result != 0)
            throw new Win32Exception(result,
                                     $"Failed to write the protected DACL of the runtime temp slot '{slotPath}'.");

        EnsureOwner(handle, slotPath, spec.HostOwner);
    }

    /// <summary>
    ///     <para>在已校验的非重解析点目录句柄上写入容器受保护显式 DACL 并确认 owner 为宿主用户。</para>
    ///     <para>
    ///         Writes the container's protected explicit DACL through a validated non-reparse directory handle
    ///         and ensures the owner is pinned to the host user.
    ///     </para>
    /// </summary>
    internal static void ApplyContainerHandle(
        SafeFileHandle handle, string containerPath, SandboxRuntimeTempContainerAclSpec spec)
    {
        SandboxRuntimeTempSlotNative.EnsureWindows();
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerPath);
        ArgumentNullException.ThrowIfNull(spec);

        var result = SandboxRuntimeTempSlotNative.ApplyProtectedDacl(handle, BuildContainerDacl(spec));
        if (result != 0)
            throw new Win32Exception(
                result, $"Failed to write the protected DACL of the runtime temp slot container '{containerPath}'.");

        EnsureOwner(handle, containerPath, spec.HostOwner);
    }

    internal static IEnumerable<SecurityIdentifier> SlotSandboxPrincipals(SandboxRuntimeTempSlotAclSpec spec)
    {
        foreach (var sid in spec.SandboxSharedSids) yield return sid;
        yield return spec.SlotCapabilitySid;
    }

    private static void EnsureOwner(SafeFileHandle handle, string path, SecurityIdentifier expectedOwner)
    {
        // 已是预期宿主 owner 时避免不必要地申请 WRITE_OWNER；不符时通过同句柄重开尝试修正。
        var current = SandboxRuntimeTempSlotNative.ReadSecurity(handle, path);
        if (string.Equals(current.Owner.Value, expectedOwner.Value, StringComparison.OrdinalIgnoreCase)) return;

        using var ownerHandle = SandboxRuntimeTempSlotNative.TryReOpenHandle(
            handle, SandboxRuntimeTempSlotNative.WriteOwner | SandboxRuntimeTempSlotNative.Synchronize,
            SandboxRuntimeTempSlotNative.FileShareReadWrite, out var openError);
        if (ownerHandle is null)
            throw new Win32Exception(openError,
                                     $"Failed to reopen '{path}' with WRITE_OWNER to pin owner to '{expectedOwner.Value}'.");

        var setResult = SandboxRuntimeTempSlotNative.SetOwner(ownerHandle, expectedOwner);
        if (setResult != 0)
            throw new Win32Exception(setResult, $"Failed to pin the owner of '{path}' to '{expectedOwner.Value}'.");
    }
}

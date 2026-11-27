using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         槽位契约校验失败：携带槽位或容器路径与全部问题，调用方据此在 spawn 前 fail-closed 并如实报出原因。
///     </para>
///     <para>
///         A failed slot contract check: carries the slot or container path and every problem so callers fail
///         closed before spawn and report the real cause faithfully.
///     </para>
/// </summary>
internal sealed class SandboxRuntimeTempSlotException : InvalidOperationException
{
    internal SandboxRuntimeTempSlotException(
        string path, IReadOnlyList<string> problems, Exception? innerException = null)
        : base(BuildMessage(path, problems, innerException), innerException)
    {
        Path     = path;
        Problems = problems;
    }

    /// <summary>
    ///     <para>校验失败的槽位或容器路径。</para>
    ///     <para>The slot or container path that failed validation.</para>
    /// </summary>
    internal string Path { get; }

    /// <summary>
    ///     <para>全部问题项，顺序为校验顺序。</para>
    ///     <para>Every problem, in check order.</para>
    /// </summary>
    internal IReadOnlyList<string> Problems { get; }

    private static string BuildMessage(string path, IReadOnlyList<string> problems, Exception? innerException)
    {
        var message = $"The runtime temp slot contract check failed for '{path}': {string.Join("; ", problems)}";
        return innerException is null ? message : $"{message} (inspection error: {innerException.Message})";
    }
}

/// <summary>
///     <para>
///         槽位与容器契约校验器：直接核对目录的卷与文件标识、owner、受保护显式 DACL、继承阻断、规范 ACE 顺序、
///         显式 deny 与传播标志、主体白名单，以及按固定版本 runner 实际 <c>WRITE_RESTRICTED</c> 受限令牌语义
///         （含沙箱账户 SID 进入 restricting SIDs）评估的本槽位写权限与跨槽写隔离。
///     </para>
///     <para>
///         The slot and container contract validator: checks the directory's volume and file identity, owner,
///         protected explicit DACL, blocked inheritance, canonical ACE order, explicit denies and propagation
///         flags, a principal allowlist, and in-slot write / cross-slot write isolation under the pinned
///         runner's actual <c>WRITE_RESTRICTED</c> token semantics (including sandbox account SIDs in the
///         restricting SID list).
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         任何必需权限缺失、归属不可证明、身份不符、ACE 顺序违规、沙箱账户 SID 混入写主体、缺少或放宽
///         <c>OWNER RIGHTS</c> 子项约束、非白名单主体授权或检查本身出错都以
///         <see cref="SandboxRuntimeTempSlotException" /> 结束，绝不返回"看起来可用"的槽位。校验只读：它不修复
///         DACL，避免把被篡改的槽位静默修好后再启动命令。写隔离覆盖槽位根以及沿用槽位写权限规则的子项；
///         运行期子项若自行采用受保护 DACL 或显式 ACE 改写写授权，属共享账户模型下已接受的边界，不在本
///         根目录校验范围内。
///     </para>
///     <para>
///         Any missing required permission, unprovable owner, identity mismatch, ACE ordering violation, sandbox
///         account SID placed into write principals, missing or over-broad <c>OWNER RIGHTS</c> child constraint,
///         non-allowlisted grant, or inspection error ends in <see cref="SandboxRuntimeTempSlotException" />; a
///         slot is never reported as "probably usable". Validation is read-only and never repairs the DACL, so a
///         tampered slot is never silently patched before launching a command. Write isolation covers the slot
///         root and children that follow the slot's write-permission rules; children that later adopt an
///         independent protected DACL or explicit ACEs altering write grants are an accepted boundary of the
///         shared-account model outside root validation.
///     </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class SandboxRuntimeTempSlotContract
{
    /// <summary>
    ///     <para>
    ///         写类权限位：写数据、追加数据、删除子项、写扩展属性、写属性、删除、改 DACL、改 owner。
    ///     </para>
    ///     <para>
    ///         Write-class rights: write data, append data, delete child, write extended attributes, write
    ///         attributes, delete, write DACL, and write owner.
    ///     </para>
    /// </summary>
    internal const uint WriteBits = 0x0000_0002 | 0x0000_0004 | 0x0000_0040 | 0x0000_0010 | 0x0000_0100 |
                                    0x0001_0000 | 0x0004_0000 | 0x0008_0000;

    /// <summary>
    ///     <para>
    ///         Windows 在 DACL 不含 <c>OWNER RIGHTS</c>（<c>S-1-3-4</c>）ACE 时向对象所有者隐式授予的权限：
    ///         <c>READ_CONTROL | WRITE_DAC</c>。
    ///     </para>
    ///     <para>
    ///         The rights Windows implicitly grants to an object's owner when the DACL has no applicable
    ///         <c>OWNER RIGHTS</c> (<c>S-1-3-4</c>) ACE: <c>READ_CONTROL | WRITE_DAC</c>.
    ///     </para>
    /// </summary>
    internal const uint ImplicitOwnerRights = SandboxRuntimeTempSlotNative.ReadControl |
                                              SandboxRuntimeTempSlotNative.WriteDac;

    /// <summary>
    ///     <para>
    ///         打开并校验完整槽位（两层结构）：先核对不可写槽位根（<c>slot-XX</c>）的 128 位目录身份与
    ///         不可写受保护 DACL（防删除与原地重解析点），再从该已校验句柄相对打开沙箱可写子目录
    ///         （<c>slot-XX\work</c>），核对其 128 位目录身份、写权限上下限与跨槽写隔离。返回时句柄已释放。
    ///     </para>
    ///     <para>
    ///         Opens and validates the complete two-tier slot: first checks the immutable slot root
    ///         (<c>slot-XX</c>) for its 128-bit identity and non-writable protected DACL (blocking deletion and
    ///         in-place reparse points), then opens the sandbox-writable child (<c>slot-XX\work</c>) relative to
    ///         that validated handle and checks its 128-bit identity, write permission lower/upper bounds, and
    ///         cross-slot write isolation. Handles are released before returning.
    ///     </para>
    /// </summary>
    internal static void ValidateSlot(
        SandboxRuntimeTempSlot             slot,
        IReadOnlyList<SecurityIdentifier>? foreignCapabilitySids = null)
    {
        SandboxRuntimeTempSlotNative.EnsureWindows();
        ArgumentNullException.ThrowIfNull(slot);

        using var rootHandle = SandboxRuntimeTempSlotNative.OpenDirectory(
            slot.Directory,
            SandboxRuntimeTempSlotNative.ReadControl        | SandboxRuntimeTempSlotNative.FileReadAttributes |
            SandboxRuntimeTempSlotNative.FileTraverse       | SandboxRuntimeTempSlotNative.Synchronize,
            SandboxRuntimeTempSlotNative.FileShareReadWrite, "runtime temp slot root");

        SandboxRuntimeTempSlotIdentity actualRootIdentity;
        try
        {
            SandboxRuntimeTempSlotNative.RequireDirectory(rootHandle, slot.Directory, out _);
            actualRootIdentity = SandboxRuntimeTempSlotIdentity.Capture(rootHandle, slot.Directory);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidDataException)
        {
            throw new SandboxRuntimeTempSlotException(slot.Directory, [$"identity: {ex.Message}"], ex);
        }

        if (!slot.Identity.Matches(actualRootIdentity))
            throw new SandboxRuntimeTempSlotException(
                slot.Directory,
                ["identity: the slot root directory was replaced " +
                 $"(expected {slot.Identity.FormatForDiagnostics()}, actual {actualRootIdentity.FormatForDiagnostics()})"]);

        ValidateContainerHandle(rootHandle, slot.Directory, slot.AclSpec.ToContainerSpec());

        using var workHandle = SandboxRuntimeTempSlotNative.OpenChild(
            rootHandle, SandboxRuntimeTempSlotLayout.WritableDirectoryName,
            SandboxRuntimeTempSlotNative.ReadControl | SandboxRuntimeTempSlotNative.FileReadAttributes |
            SandboxRuntimeTempSlotNative.Synchronize,
            out var openWorkError);
        if (workHandle is null)
            throw new SandboxRuntimeTempSlotException(
                slot.WritableDirectory,
                [$"writable directory: cannot open '{SandboxRuntimeTempSlotLayout.WritableDirectoryName}' under slot root (Win32 error {openWorkError})"]);

        ValidateWritableHandle(workHandle, slot.WritableIdentity, slot.AclSpec, foreignCapabilitySids);
    }

    /// <summary>
    ///     <para>打开并校验槽位可写目录；返回时句柄已释放，身份与 DACL（含必填沙箱账户 SID 隔离与权限上限）均已通过。</para>
    ///     <para>
    ///         Opens and validates the slot's writable directory by path; the handle is released before returning,
    ///         with identity and DACL checks (including required sandbox account SID isolation and upper bounds)
    ///         passed.
    ///     </para>
    /// </summary>
    internal static void ValidateWritableDirectory(
        string                             writablePath,
        SandboxRuntimeTempSlotIdentity     expectedIdentity,
        SandboxRuntimeTempSlotAclSpec      spec,
        IReadOnlyList<SecurityIdentifier>? foreignCapabilitySids = null)
    {
        SandboxRuntimeTempSlotNative.EnsureWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(writablePath);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        ArgumentNullException.ThrowIfNull(spec);

        using var handle = SandboxRuntimeTempSlotNative.OpenDirectory(
            writablePath,
            SandboxRuntimeTempSlotNative.ReadControl | SandboxRuntimeTempSlotNative.FileReadAttributes |
            SandboxRuntimeTempSlotNative.Synchronize,
            SandboxRuntimeTempSlotNative.FileShareReadWrite, "runtime temp slot writable directory");
        ValidateWritableHandle(handle, expectedIdentity, spec, foreignCapabilitySids);
    }

    /// <summary>
    ///     <para>在已打开的槽位可写目录句柄上执行全部契约校验；句柄必须由调用方打开且仍指向目标目录。</para>
    ///     <para>
    ///         Runs every writable-slot check on an already opened directory handle, which must be opened by the
    ///         caller and still refer to the intended directory.
    ///     </para>
    /// </summary>
    /// <exception cref="SandboxRuntimeTempSlotException">
    ///     <para>任一校验项失败或检查出错。</para>
    ///     <para>Any check fails or the inspection itself fails.</para>
    /// </exception>
    internal static void ValidateWritableHandle(
        SafeFileHandle                     handle,
        SandboxRuntimeTempSlotIdentity     expectedIdentity,
        SandboxRuntimeTempSlotAclSpec      spec,
        IReadOnlyList<SecurityIdentifier>? foreignCapabilitySids = null)
    {
        SandboxRuntimeTempSlotNative.EnsureWindows();
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        ArgumentNullException.ThrowIfNull(spec);

        var problems = new List<string>();
        SandboxRuntimeTempSlotIdentity actualIdentity;
        try
        {
            SandboxRuntimeTempSlotNative.RequireDirectory(handle, expectedIdentity.Path, out _);
            actualIdentity = SandboxRuntimeTempSlotIdentity.Capture(handle, expectedIdentity.Path);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidDataException)
        {
            throw new SandboxRuntimeTempSlotException(expectedIdentity.Path, [$"identity: {ex.Message}"], ex);
        }

        if (!expectedIdentity.Matches(actualIdentity))
            problems.Add("identity: the slot directory was replaced " +
                         $"(expected {expectedIdentity.FormatForDiagnostics()}, " +
                         $"actual {actualIdentity.FormatForDiagnostics()})");

        SandboxRuntimeTempSlotNative.RawSecurity security;
        try
        {
            security = SandboxRuntimeTempSlotNative.ReadSecurity(handle, expectedIdentity.Path);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidDataException)
        {
            throw new SandboxRuntimeTempSlotException(expectedIdentity.Path, [.. problems, $"dacl: {ex.Message}"], ex);
        }

        CheckSlotSecurity(security, spec, problems, foreignCapabilitySids);
        if (problems.Count > 0) throw new SandboxRuntimeTempSlotException(expectedIdentity.Path, problems);
    }

    /// <summary>
    ///     <para>按路径打开并校验容器目录；DACL 受保护、无继承入口、owner 正确，且共享沙箱身份只能穿越、不能写或列目录。</para>
    ///     <para>
    ///         Opens and validates the container directory by path: protected DACL, no inherited entries,
    ///         correct owner, and shared sandbox identities limited to traverse with no write or list access.
    ///     </para>
    /// </summary>
    internal static void ValidateContainerDirectory(string containerPath, SandboxRuntimeTempContainerAclSpec spec)
    {
        SandboxRuntimeTempSlotNative.EnsureWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(containerPath);
        ArgumentNullException.ThrowIfNull(spec);

        using var handle = SandboxRuntimeTempSlotNative.OpenDirectory(
            containerPath,
            SandboxRuntimeTempSlotNative.ReadControl | SandboxRuntimeTempSlotNative.FileReadAttributes |
            SandboxRuntimeTempSlotNative.Synchronize,
            SandboxRuntimeTempSlotNative.FileShareReadWrite, "runtime temp slot container");
        ValidateContainerHandle(handle, containerPath, spec);
    }

    /// <summary>
    ///     <para>在已打开的容器目录句柄上执行全部容器契约校验。</para>
    ///     <para>Runs every container contract check on an already opened container directory handle.</para>
    /// </summary>
    internal static void ValidateContainerHandle(
        SafeFileHandle handle, string containerPath, SandboxRuntimeTempContainerAclSpec spec)
    {
        SandboxRuntimeTempSlotNative.EnsureWindows();
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerPath);
        ArgumentNullException.ThrowIfNull(spec);

        var problems = new List<string>();
        SandboxRuntimeTempSlotNative.RawSecurity security;
        try
        {
            SandboxRuntimeTempSlotNative.RequireDirectory(handle, containerPath, out _);
            security = SandboxRuntimeTempSlotNative.ReadSecurity(handle, containerPath);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidDataException)
        {
            throw new SandboxRuntimeTempSlotException(containerPath, [$"container: {ex.Message}"], ex);
        }

        CheckBaseSecurity(security, "container", spec.HostOwner, spec.SandboxSharedSids, problems);

        var management = ManagementPrincipals(spec.HostOwner, spec.ManagementSids);
        foreach (var sid in management)
            RequirePropagatingInheritableAllow(
                security, sid, SandboxRuntimeTempSlotAcl.ManagementRights, problems,
                string.Equals(sid, spec.HostOwner.Value, StringComparison.OrdinalIgnoreCase)
                    ? "host owner"
                    : "management identity");

        foreach (var sid in spec.SandboxSharedSids)
        {
            RequireExactNonPropagatingDeny(
                security.Aces, sid, SandboxRuntimeTempSlotAcl.ContainerDeniedRights, "container", problems);

            var mask = EffectiveMask(security.Aces, [sid]);
            if ((mask & SandboxRuntimeTempSlotAcl.ContainerTraverseRights) !=
                SandboxRuntimeTempSlotAcl.ContainerTraverseRights)
                problems.Add($"container: shared sandbox identity {sid.Value} cannot traverse the slot container");
            if ((mask & WriteBits) != 0)
                problems.Add($"container: shared sandbox identity {sid.Value} has write rights " +
                             $"0x{mask & WriteBits:x8} on the container");
            if ((mask & SandboxRuntimeTempSlotNative.FileListDirectory) != 0)
                problems.Add($"container: shared sandbox identity {sid.Value} must not list the slot container");
            var extraContainerRights = mask & ~SandboxRuntimeTempSlotAcl.ContainerTraverseRights;
            if (extraContainerRights != 0)
                problems.Add($"container: shared sandbox identity {sid.Value} has rights beyond minimal traverse " +
                             $"(extra 0x{extraContainerRights:x8})");

            // 容器与不可写槽位根上的共享沙箱 allow ACE 不得超出最小穿越上限，也不得向子项继承。
            foreach (var ace in security.Aces)
            {
                if (ace.Denied || !string.Equals(ace.Sid.Value, sid.Value, StringComparison.OrdinalIgnoreCase)) continue;
                var expanded = Expand(ace.Mask);
                if ((expanded & ~SandboxRuntimeTempSlotAcl.ContainerTraverseRights) != 0)
                    problems.Add($"container: shared sandbox identity {sid.Value} allow ACE grants rights beyond " +
                                 $"minimal traverse (extra 0x{expanded & ~SandboxRuntimeTempSlotAcl.ContainerTraverseRights:x8})");
                if (ace.ContainerInherit || ace.ObjectInherit || ace.InheritOnly)
                    problems.Add($"container: shared sandbox identity {sid.Value} allow ACE must not inherit from the container");
            }
        }

        var allowedPrincipals = new HashSet<string>(management, StringComparer.OrdinalIgnoreCase);
        foreach (var sid in spec.SandboxSharedSids) allowedPrincipals.Add(sid.Value);
        ReportNonAllowlistedPrincipals(security.Aces, allowedPrincipals, "container", problems);

        if (problems.Count > 0) throw new SandboxRuntimeTempSlotException(containerPath, problems);
    }

    /// <summary>
    ///     <para>
    ///         严格按 Windows <c>AccessCheck</c> 的 ACE 顺序计算单轮主体集合对目录对象本身的有效掩码：逐条扫描
    ///         非 inherit-only 的 ACE，每个权限位以首次命中的 Allow 或 Deny 为准，后续 ACE 不覆盖已决定的位。
    ///     </para>
    ///     <para>
    ///         Computes one round's effective mask for a principal set on the directory object itself following
    ///         Windows <c>AccessCheck</c> ACE order: scans non-inherit-only ACEs sequentially and locks each
    ///         right bit on the first matching Allow or Deny so later ACEs cannot override already-decided bits.
    ///     </para>
    /// </summary>
    internal static uint EffectiveMask(
        IReadOnlyList<SandboxRuntimeTempSlotNative.RawAce> aces, IReadOnlyList<SecurityIdentifier> trustees)
    {
        ArgumentNullException.ThrowIfNull(aces);
        ArgumentNullException.ThrowIfNull(trustees);
        uint allowed = 0;
        uint decided = 0;
        foreach (var ace in aces)
        {
            if (ace.InheritOnly || !Contains(trustees, ace.Sid)) continue;
            var newlyDecided = Expand(ace.Mask) & ~decided;
            if (!ace.Denied) allowed |= newlyDecided;
            decided |= newlyDecided;
        }

        return allowed;
    }

    /// <summary>
    ///     <para>
    ///         按 Windows <c>CreateRestrictedToken(..., WRITE_RESTRICTED, ...)</c> 语义计算目录对象本身的有效掩码：
    ///         非写权限仅由第一轮（主令牌用户与启用组）决定，写类权限（<see cref="WriteBits" />）取第一轮与第二轮
    ///         （restricting SIDs，含沙箱账户 SID 与本次 capability SIDs）的交集。
    ///     </para>
    ///     <para>
    ///         Computes the effective mask on the directory object under Windows
    ///         <c>CreateRestrictedToken(..., WRITE_RESTRICTED, ...)</c> semantics: non-write rights come from
    ///         round 1 (primary user and enabled groups) alone, while write-class rights
    ///         (<see cref="WriteBits" />) are intersected with round 2 (restricting SIDs, including the sandbox
    ///         account SID and the command's capability SIDs).
    ///     </para>
    /// </summary>
    internal static uint EffectiveWriteRestrictedMask(
        IReadOnlyList<SandboxRuntimeTempSlotNative.RawAce> aces,
        IReadOnlyList<SecurityIdentifier>                  firstRoundTrustees,
        IReadOnlyList<SecurityIdentifier>                  secondRoundTrustees)
    {
        var firstRound  = EffectiveMask(aces, firstRoundTrustees);
        var secondRound = EffectiveMask(aces, secondRoundTrustees);
        return (firstRound & ~WriteBits) | (firstRound & secondRound & WriteBits);
    }

    /// <summary>
    ///     <para>
    ///         严格按 Windows ACE 顺序计算主体集合在槽位内子项（文件或目录）上的单轮继承掩码，按
    ///         <c>OI</c>/<c>CI</c> 筛选适用的 ACE，每个权限位由首次命中的 ACE 决定。
    ///     </para>
    ///     <para>
    ///         Computes one round's inherited mask for a principal set on a child entry (file or directory) in
    ///         Windows ACE order, filtering by <c>OI</c>/<c>CI</c> with each right bit decided by the first
    ///         matching ACE.
    ///     </para>
    /// </summary>
    internal static uint InheritedMask(
        IReadOnlyList<SandboxRuntimeTempSlotNative.RawAce> aces,
        IReadOnlyList<SecurityIdentifier>                  trustees,
        bool                                               directory)
    {
        ArgumentNullException.ThrowIfNull(aces);
        ArgumentNullException.ThrowIfNull(trustees);
        uint allowed = 0;
        uint decided = 0;
        foreach (var ace in aces)
        {
            if (!Contains(trustees, ace.Sid)) continue;
            if (!(directory ? ace.ContainerInherit : ace.ObjectInherit)) continue;
            var newlyDecided = Expand(ace.Mask) & ~decided;
            if (!ace.Denied) allowed |= newlyDecided;
            decided |= newlyDecided;
        }

        return allowed;
    }

    /// <summary>
    ///     <para>
    ///         计算子项（文件或目录）所有者从 Windows 隐式所有权或继承的 <c>OWNER RIGHTS</c>（<c>S-1-3-4</c>）
    ///         ACE 获得的权限：若不存在适用于该子项类型的 <c>OWNER RIGHTS</c> 非 deny ACE，内核默认向所有者隐式
    ///         授予 <c>READ_CONTROL | WRITE_DAC</c>；否则仅授予 <c>OWNER RIGHTS</c> ACE 指定的权限。
    ///     </para>
    ///     <para>
    ///         Computes the rights a child entry's owner obtains from Windows implicit ownership or inherited
    ///         <c>OWNER RIGHTS</c> (<c>S-1-3-4</c>) ACEs: without an applicable non-deny <c>OWNER RIGHTS</c> ACE,
    ///         Windows implicitly grants <c>READ_CONTROL | WRITE_DAC</c>; otherwise only the rights granted by
    ///         <c>OWNER RIGHTS</c> apply.
    ///     </para>
    /// </summary>
    internal static uint EffectiveChildOwnerRights(
        IReadOnlyList<SandboxRuntimeTempSlotNative.RawAce> aces, bool directory)
    {
        ArgumentNullException.ThrowIfNull(aces);
        var hasApplicableOwnerRights = aces.Any(ace =>
            !ace.Denied &&
            (directory ? ace.ContainerInherit : ace.ObjectInherit) &&
            string.Equals(ace.Sid.Value, SandboxRuntimeTempSlotAcl.OwnerRightsSid.Value,
                          StringComparison.OrdinalIgnoreCase));
        if (!hasApplicableOwnerRights) return ImplicitOwnerRights;

        return InheritedMask(aces, [SandboxRuntimeTempSlotAcl.OwnerRightsSid], directory);
    }

    /// <summary>
    ///     <para>
    ///         按 <c>WRITE_RESTRICTED</c> 令牌语义计算子项（文件或目录）上的继承有效掩码：非写位取第一轮，写位取
    ///         第一轮与第二轮交集；当 <paramref name="ownerInBothRounds" /> 为 true（子项由共享沙箱账户拥有，且
    ///         该账户同时位于令牌第一轮与第二轮 restricting SIDs 中）时，同步叠加子项所有者的隐式或
    ///         <c>OWNER RIGHTS</c> 权限。
    ///     </para>
    ///     <para>
    ///         Computes the inherited effective mask on a child entry (file or directory) under
    ///         <c>WRITE_RESTRICTED</c> semantics: non-write bits follow round 1, and write bits are intersected
    ///         across both rounds; when <paramref name="ownerInBothRounds" /> is true (the child is owned by the
    ///         shared sandbox account present in both rounds of the token), the child owner's implicit or
    ///         <c>OWNER RIGHTS</c> mask is folded into both rounds.
    ///     </para>
    /// </summary>
    internal static uint InheritedWriteRestrictedMask(
        IReadOnlyList<SandboxRuntimeTempSlotNative.RawAce> aces,
        IReadOnlyList<SecurityIdentifier>                  firstRoundTrustees,
        IReadOnlyList<SecurityIdentifier>                  secondRoundTrustees,
        bool                                               directory,
        bool                                               ownerInBothRounds = false)
    {
        var ownerRights = ownerInBothRounds ? EffectiveChildOwnerRights(aces, directory) : 0u;
        var firstRound  = InheritedMask(aces, firstRoundTrustees, directory) | ownerRights;
        var secondRound = InheritedMask(aces, secondRoundTrustees, directory) | ownerRights;
        return (firstRound & ~WriteBits) | (firstRound & secondRound & WriteBits);
    }

    /// <summary>
    ///     <para>把 generic 访问位展开为具体文件权限位，避免模板与校验之间的掩码比较失真。</para>
    ///     <para>
    ///         Expands generic access bits into concrete file rights so mask comparisons between the template
    ///         and the check stay exact.
    ///     </para>
    /// </summary>
    internal static uint Expand(uint mask)
    {
        var expanded = mask & 0x0FFF_FFFF;
        if ((mask & 0x8000_0000) != 0) expanded |= 0x0012_0089; // GENERIC_READ
        if ((mask & 0x4000_0000) != 0) expanded |= 0x0012_0116; // GENERIC_WRITE
        if ((mask & 0x2000_0000) != 0) expanded |= 0x0012_00A0; // GENERIC_EXECUTE
        if ((mask & 0x1000_0000) != 0) expanded |= 0x001F_01FF; // GENERIC_ALL
        return expanded;
    }

    private static void CheckSlotSecurity(
        SandboxRuntimeTempSlotNative.RawSecurity security,
        SandboxRuntimeTempSlotAclSpec            spec,
        List<string>                             problems,
        IReadOnlyList<SecurityIdentifier>?       foreignCapabilitySids)
    {
        var slotSandboxPrincipals = SandboxRuntimeTempSlotAcl.SlotSandboxPrincipals(spec).ToArray();
        CheckBaseSecurity(security, "slot", spec.HostOwner, slotSandboxPrincipals, problems);

        // 固定版本 Rust runner 会把沙箱账户 SID 放入 restricting SIDs：沙箱账户 SID 为必填项，且绝不允许出现在
        // 共享写主体或槽位 DACL 写主体中，否则同账户另一并发槽位与 ReadOnly 令牌会直接通过第二轮写检查。
        var accountSids = spec.SandboxAccountSids;
        if (accountSids is null || accountSids.Count == 0)
        {
            problems.Add("sandbox account SIDs: at least one actual sandbox account SID is required to validate " +
                         "WRITE_RESTRICTED cross-slot write isolation");
            accountSids = [];
        }

        foreach (var accountSid in accountSids)
            if (Contains(spec.SandboxSharedSids, accountSid))
                problems.Add($"sandbox account in shared write principals: {accountSid.Value} is included in the " +
                             "runner's restricting SIDs and must not appear in SandboxSharedSids");

        var management = ManagementPrincipals(spec.HostOwner, spec.ManagementSids);
        foreach (var sid in management)
            RequirePropagatingInheritableAllow(
                security, sid, SandboxRuntimeTempSlotAcl.ManagementRights, problems,
                string.Equals(sid, spec.HostOwner.Value, StringComparison.OrdinalIgnoreCase)
                    ? "host owner"
                    : "management identity");

        foreach (var sid in slotSandboxPrincipals)
        {
            RequireExactNonPropagatingDeny(
                security.Aces, sid, SandboxRuntimeTempSlotAcl.RootDeniedRights, "slot", problems);

            var rootMask = EffectiveMask(security.Aces, [sid]);
            if ((rootMask & SandboxRuntimeTempSlotAcl.SlotRootWriteRights) !=
                SandboxRuntimeTempSlotAcl.SlotRootWriteRights)
                problems.Add($"required write: slot principal {sid.Value} lacks slot-root rights " +
                             $"(missing 0x{SandboxRuntimeTempSlotAcl.SlotRootWriteRights & ~rootMask:x8})");
            if ((rootMask & SandboxRuntimeTempSlotAcl.RootDeniedRights) != 0)
                problems.Add($"root protection: slot principal {sid.Value} can delete or rewrite the slot root " +
                             $"(granted 0x{rootMask & SandboxRuntimeTempSlotAcl.RootDeniedRights:x8})");
            var extraRootRights = rootMask & ~SandboxRuntimeTempSlotAcl.SlotRootWriteRights;
            if (extraRootRights != 0)
                problems.Add($"over-broad root rights: slot principal {sid.Value} has rights beyond the slot-root " +
                             $"template (extra 0x{extraRootRights:x8})");

            foreach (var directory in new[] { false, true })
            {
                var inherited = InheritedMask(security.Aces, [sid], directory);
                if ((inherited & SandboxRuntimeTempSlotAcl.SlotChildWriteRights) !=
                    SandboxRuntimeTempSlotAcl.SlotChildWriteRights)
                    problems.Add($"required child write: slot principal {sid.Value} lacks inherited rights on " +
                                 $"{(directory ? "child directories" : "child files")} " +
                                 $"(missing 0x{SandboxRuntimeTempSlotAcl.SlotChildWriteRights & ~inherited:x8})");
                var extraChildRights = inherited & ~SandboxRuntimeTempSlotAcl.SlotChildWriteRights;
                if (extraChildRights != 0)
                    problems.Add($"over-broad child rights: slot principal {sid.Value} inherits rights beyond the " +
                                 $"child template on {(directory ? "child directories" : "child files")} " +
                                 $"(extra 0x{extraChildRights:x8})");
            }

            // 单条 Allow ACE 权限上限：既防止子项继承被扩大为 FullControl（如 WRITE_DAC/WRITE_OWNER），也防止根 ACE 携带越权位。
            foreach (var ace in security.Aces)
            {
                if (ace.Denied || !string.Equals(ace.Sid.Value, sid.Value, StringComparison.OrdinalIgnoreCase)) continue;
                var expanded = Expand(ace.Mask);
                if (!ace.InheritOnly && (expanded & ~SandboxRuntimeTempSlotAcl.SlotRootWriteRights) != 0)
                    problems.Add($"over-broad root rights: slot principal {sid.Value} allow ACE on root grants rights " +
                                 $"beyond template (extra 0x{expanded & ~SandboxRuntimeTempSlotAcl.SlotRootWriteRights:x8})");
                if ((ace.ContainerInherit || ace.ObjectInherit) &&
                    (expanded & ~SandboxRuntimeTempSlotAcl.SlotChildWriteRights) != 0)
                    problems.Add($"over-broad child rights: slot principal {sid.Value} inheritable allow ACE grants " +
                                 $"rights beyond template (extra 0x{expanded & ~SandboxRuntimeTempSlotAcl.SlotChildWriteRights:x8})");
            }

            // 传播标志：子项写 ACE 必须同时覆盖子目录与子文件且不带 NoPropagateInherit，否则更深层临时目录会失权。
            var propagates = security.Aces.Any(ace =>
                !ace.Denied && ace.ContainerInherit && ace.ObjectInherit && !ace.NoPropagateInherit &&
                string.Equals(ace.Sid.Value, sid.Value, StringComparison.OrdinalIgnoreCase) &&
                (Expand(ace.Mask) & SandboxRuntimeTempSlotAcl.SlotChildWriteRights) ==
                SandboxRuntimeTempSlotAcl.SlotChildWriteRights);
            if (!propagates)
                problems.Add($"propagation flags: slot principal {sid.Value} has no propagating inheritable write ACE " +
                             "for deeper temporary files and directories");
        }

        // 模拟实际 runner 的 WRITE_RESTRICTED 令牌：第一轮 = 共享沙箱组 ∪ 沙箱账户，第二轮 = 沙箱账户 ∪ capability。
        SecurityIdentifier[] firstRound     = [.. spec.SandboxSharedSids, .. accountSids];
        SecurityIdentifier[] ownSecondRound = [.. accountSids, spec.SlotCapabilitySid];

        var effectiveRoot = EffectiveWriteRestrictedMask(security.Aces, firstRound, ownSecondRound);
        if ((effectiveRoot & SandboxRuntimeTempSlotAcl.SlotRootWriteRights) !=
            SandboxRuntimeTempSlotAcl.SlotRootWriteRights)
            problems.Add("write-restricted access check: the runner token for this slot lacks effective slot-root " +
                         $"write rights (missing 0x{SandboxRuntimeTempSlotAcl.SlotRootWriteRights & ~effectiveRoot:x8})");
        if ((effectiveRoot & SandboxRuntimeTempSlotAcl.RootDeniedRights) != 0)
            problems.Add("write-restricted access check: the runner token for this slot can delete or rewrite the " +
                         $"slot root (granted 0x{effectiveRoot & SandboxRuntimeTempSlotAcl.RootDeniedRights:x8})");

        foreach (var directory in new[] { false, true })
        {
            var effectiveChild = InheritedWriteRestrictedMask(security.Aces, firstRound, ownSecondRound, directory);
            if ((effectiveChild & SandboxRuntimeTempSlotAcl.SlotChildWriteRights) !=
                SandboxRuntimeTempSlotAcl.SlotChildWriteRights)
                problems.Add("write-restricted access check: the runner token for this slot lacks effective inherited " +
                             $"write rights on {(directory ? "child directories" : "child files")} " +
                             $"(missing 0x{SandboxRuntimeTempSlotAcl.SlotChildWriteRights & ~effectiveChild:x8})");
        }

        // 校验子项 OWNER RIGHTS (S-1-3-4) 继承约束：必须以 CI|OI|IO 且不带 NoPropagateInherit 向全部深层子项传播，
        // 且绝不得授予任何写类权限位（如 WRITE_DAC），确保同账户另一槽位无法通过对象所有者隐式 WRITE_DAC 篡改子项 DACL。
        CheckOwnerRightsInheritance(security.Aces, problems);

        // 主体白名单：拒绝任何非授权主体的 Allow ACE（无论读或写）；OWNER RIGHTS 仅允许以上述受控子项继承形态出现。
        var allowedPrincipals = new HashSet<string>(management, StringComparer.OrdinalIgnoreCase)
        {
            SandboxRuntimeTempSlotAcl.OwnerRightsSid.Value
        };
        foreach (var sid in slotSandboxPrincipals) allowedPrincipals.Add(sid.Value);
        ReportNonAllowlistedPrincipals(security.Aces, allowedPrincipals, "slot", problems);

        var ownerInBothRounds = accountSids.Count > 0;

        // ReadOnly 令牌校验（仅含沙箱账户 SID、不含本槽位 capability）：在根与子项（含所有者隐式权限）上写位必须为 0。
        if (ownerInBothRounds)
        {
            var readOnlyWrite = CollectWriteBits(security.Aces, firstRound, accountSids, ownerInBothRounds);
            if (readOnlyWrite != 0)
                problems.Add("cross-slot write: a runner token carrying only sandbox account restricting SID(s) " +
                             $"gains write rights 0x{readOnlyWrite:x8} on this slot");
        }

        // 跨槽写隔离：同账户下携带另一槽位 capability（+ 沙箱账户 SID）的 WRITE_RESTRICTED 令牌对本槽位及子项写位必须为 0。
        foreach (var foreign in foreignCapabilitySids ?? [])
        {
            SecurityIdentifier[] foreignSecondRound = [.. accountSids, foreign];
            var foreignWrite = CollectWriteBits(security.Aces, firstRound, foreignSecondRound, ownerInBothRounds);
            if (foreignWrite != 0)
                problems.Add($"cross-slot write: foreign slot capability {foreign.Value} gains write rights " +
                             $"0x{foreignWrite:x8} on this slot");
        }
    }

    private static void CheckOwnerRightsInheritance(
        IReadOnlyList<SandboxRuntimeTempSlotNative.RawAce> aces, List<string> problems)
    {
        var ownerRightsAces = aces
            .Where(ace => !ace.Denied &&
                          string.Equals(ace.Sid.Value, SandboxRuntimeTempSlotAcl.OwnerRightsSid.Value,
                                        StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (ownerRightsAces.Length == 0)
        {
            problems.Add("owner rights: missing inheritable OWNER RIGHTS (S-1-3-4) ACE to strip implicit child " +
                         "owner WRITE_DAC");
            return;
        }

        foreach (var ace in ownerRightsAces)
        {
            var expanded = Expand(ace.Mask);
            if ((expanded & WriteBits) != 0)
                problems.Add($"owner rights: OWNER RIGHTS (S-1-3-4) ACE must not grant write rights " +
                             $"0x{expanded & WriteBits:x8}");
            var extraOwnerRights = expanded & ~SandboxRuntimeTempSlotAcl.OwnerRightsChildRights;
            if (extraOwnerRights != 0)
                problems.Add($"owner rights: OWNER RIGHTS (S-1-3-4) ACE grants rights beyond READ_CONTROL " +
                             $"(extra 0x{extraOwnerRights:x8})");
            if (!ace.InheritOnly)
                problems.Add("owner rights: OWNER RIGHTS (S-1-3-4) ACE on the slot root must be inherit-only");
        }

        var propagatesToAllChildren = ownerRightsAces.Any(ace =>
            ace.InheritOnly && ace.ContainerInherit && ace.ObjectInherit && !ace.NoPropagateInherit &&
            (Expand(ace.Mask) & WriteBits) == 0);
        if (!propagatesToAllChildren)
            problems.Add("owner rights: OWNER RIGHTS (S-1-3-4) ACE must propagate to all child files and " +
                         "subdirectories without NoPropagateInherit");
    }

    private static uint CollectWriteBits(
        IReadOnlyList<SandboxRuntimeTempSlotNative.RawAce> aces,
        IReadOnlyList<SecurityIdentifier>                  firstRound,
        IReadOnlyList<SecurityIdentifier>                  secondRound,
        bool                                               ownerInBothRounds)
    {
        var root  = EffectiveWriteRestrictedMask(aces, firstRound, secondRound) & WriteBits;
        var dir   = InheritedWriteRestrictedMask(aces, firstRound, secondRound, directory: true, ownerInBothRounds) &
                    WriteBits;
        var files = InheritedWriteRestrictedMask(aces, firstRound, secondRound, directory: false, ownerInBothRounds) &
                    WriteBits;
        return root | dir | files;
    }

    private static void CheckBaseSecurity(
        SandboxRuntimeTempSlotNative.RawSecurity security,
        string                                   label,
        SecurityIdentifier                       expectedOwner,
        IReadOnlyList<SecurityIdentifier>        expectedDenyPrincipals,
        List<string>                             problems)
    {
        if (!security.DaclPresent)
            problems.Add($"{label}: the directory has no DACL (a null DACL grants everyone full control)");
        if (!security.DaclProtected)
            problems.Add($"{label}: the DACL still inherits from its ancestors (SE_DACL_PROTECTED is not set)");
        if (security.Aces.Count == 0) problems.Add($"{label}: the DACL is empty");
        if (!string.Equals(security.Owner.Value, expectedOwner.Value, StringComparison.OrdinalIgnoreCase))
            problems.Add($"{label}: owner is {security.Owner.Value}, expected the host identity {expectedOwner.Value}");

        var seenAllow = false;
        var denySids = new HashSet<string>(expectedDenyPrincipals.Select(sid => sid.Value),
                                           StringComparer.OrdinalIgnoreCase);
        foreach (var ace in security.Aces)
        {
            if (ace.Inherited)
                problems.Add($"{label}: inherited ACE for {ace.Sid.Value} (mask 0x{ace.Mask:x8}) " +
                             "proves the DACL is not fully explicit");

            if (ace.Denied)
            {
                if (!denySids.Contains(ace.Sid.Value))
                    problems.Add($"{label}: unexpected deny ACE for {ace.Sid.Value} (mask 0x{ace.Mask:x8})");
                if (seenAllow)
                    problems.Add($"{label}: canonical order violated — explicit deny ACE for {ace.Sid.Value} " +
                                 $"(mask 0x{ace.Mask:x8}) appears after an allow ACE");
            }
            else
            {
                seenAllow = true;
            }
        }
    }

    private static void RequireExactNonPropagatingDeny(
        IReadOnlyList<SandboxRuntimeTempSlotNative.RawAce> aces,
        SecurityIdentifier                                 sid,
        uint                                               expectedMask,
        string                                             label,
        List<string>                                       problems)
    {
        var denies = aces.Where(ace => ace.Denied &&
                                       string.Equals(ace.Sid.Value, sid.Value, StringComparison.OrdinalIgnoreCase))
                         .ToArray();
        if (denies.Length != 1)
        {
            problems.Add($"explicit deny: expected exactly one {label} deny ACE for {sid.Value}, found {denies.Length}");
            return;
        }

        var deny = denies[0];
        if (deny.Mask != expectedMask)
            problems.Add($"explicit deny: {label} principal {sid.Value} deny mask 0x{deny.Mask:X8} does not match " +
                         $"0x{expectedMask:X8}");
        if (deny.InheritOnly || deny.ContainerInherit || deny.ObjectInherit || deny.NoPropagateInherit)
            problems.Add($"explicit deny: the {label} deny ACE for {sid.Value} must not propagate");
    }

    private static void ReportNonAllowlistedPrincipals(
        IReadOnlyList<SandboxRuntimeTempSlotNative.RawAce> aces,
        HashSet<string>                                    allowlist,
        string                                             label,
        List<string>                                       problems)
    {
        foreach (var ace in aces)
        {
            if (ace.Denied || allowlist.Contains(ace.Sid.Value)) continue;
            var expanded = Expand(ace.Mask);
            var kind     = (expanded & WriteBits) != 0 ? "over-broad write" : "unauthorized read/access";
            problems.Add($"{kind}: {label} allow ACE for non-allowlisted principal {ace.Sid.Value} grants rights " +
                         $"0x{expanded:x8}");
        }
    }

    private static void RequirePropagatingInheritableAllow(
        SandboxRuntimeTempSlotNative.RawSecurity security,
        string                                   sidValue,
        uint                                     required,
        List<string>                             problems,
        string                                   role)
    {
        var matching = security.Aces
                               .Where(ace => !ace.Denied && !ace.InheritOnly && ace.ContainerInherit &&
                                             ace.ObjectInherit && !ace.NoPropagateInherit &&
                                             string.Equals(ace.Sid.Value, sidValue, StringComparison.OrdinalIgnoreCase))
                               .ToArray();
        if (matching.Length == 0)
        {
            problems.Add($"required {role}: no propagating inheritable allow ACE for {sidValue}");
            return;
        }

        var granted = matching.Aggregate(0u, (current, ace) => current | Expand(ace.Mask));
        if ((granted & required) != required)
            problems.Add($"required {role}: {sidValue} misses 0x{required & ~granted:x8} of the expected rights");
    }

    private static HashSet<string> ManagementPrincipals(
        SecurityIdentifier hostOwner, IReadOnlyList<SecurityIdentifier> managementSids)
    {
        var management = new HashSet<string>(managementSids.Select(sid => sid.Value), StringComparer.OrdinalIgnoreCase)
        {
            hostOwner.Value
        };
        return management;
    }

    private static bool Contains(IReadOnlyList<SecurityIdentifier> trustees, SecurityIdentifier sid) =>
        trustees.Any(trustee => string.Equals(trustee.Value, sid.Value, StringComparison.OrdinalIgnoreCase));
}

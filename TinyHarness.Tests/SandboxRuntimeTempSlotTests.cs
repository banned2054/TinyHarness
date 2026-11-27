using Microsoft.Win32.SafeHandles;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         离线验证槽位与父容器初始化及 fail-closed 契约校验：原子新建拒绝复用、受保护显式 DACL、继承阻断、
///         多槽位容器隔离、Windows ACE 顺序求值与规范顺序、缺失/越权写与非白名单读拒绝、显式 deny 与传播标志、
///         owner 归属与同句柄重开、目录与重解析点替换拒绝，以及固定版本 runner 实际 <c>WRITE_RESTRICTED</c>
///         受限令牌（含沙箱账户 SID）下的本槽位写与跨槽写隔离。
///     </para>
///     <para>
///         Offline validation of slot and container initialization and the fail-closed contract check: atomic
///         creation refusing reuse, protected explicit DACL, blocked inheritance, multi-slot container isolation,
///         Windows ACE-order evaluation and canonical ordering, missing/over-broad write and non-allowlisted read
///         refusal, explicit denies and propagation flags, owner attribution and same-handle reopen, replaced
///         directory and reparse-point refusal, and in-slot write / cross-slot write isolation under the pinned
///         runner's actual <c>WRITE_RESTRICTED</c> token (including sandbox account SIDs).
///     </para>
/// </summary>
[SupportedOSPlatform("windows")]
public class SandboxRuntimeTempSlotTests
{
    private const long WriteRightsMask = (long)(FileSystemRights.Write             | FileSystemRights.Delete |
                                                FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership |
                                                FileSystemRights.DeleteSubdirectoriesAndFiles);

    [Test]
    public void Layout_BuildsContainerSlotsAndSingleSlotPaths()
    {
        var home = Path.Combine(Path.GetTempPath(), "tinyharness-slot-home");

        Assert.That(SandboxRuntimeTempSlotLayout.ContainerDirectory(home),
                    Is.EqualTo(Path.Combine(home, "runtime-tmp-v2")));
        Assert.That(SandboxRuntimeTempSlotLayout.SlotsDirectory(home),
                    Is.EqualTo(Path.Combine(home, "runtime-tmp-v2", "slots")));
        Assert.That(SandboxRuntimeTempSlotLayout.SlotDirectory(home, 0),
                    Is.EqualTo(Path.Combine(home, "runtime-tmp-v2", "slots", "slot-00")));
        Assert.That(SandboxRuntimeTempSlotLayout.WritableDirectory(home, 0),
                    Is.EqualTo(Path.Combine(home, "runtime-tmp-v2", "slots", "slot-00", "work")));
        Assert.That(SandboxRuntimeTempSlotLayout.SlotName(7), Is.EqualTo("slot-07"));
        Assert.That(SandboxRuntimeTempSlotLayout.TryParseSlotName("slot-07", out var index), Is.True);
        Assert.That(index, Is.EqualTo(7));
        Assert.That(SandboxRuntimeTempSlotLayout.TryParseSlotName("slot-7", out _), Is.False);
        Assert.That(SandboxRuntimeTempSlotLayout.TryParseSlotName("runtime-tmp-v2", out _), Is.False);
        Assert.Throws<ArgumentOutOfRangeException>(() => SandboxRuntimeTempSlotLayout.SlotName(100));
        Assert.Throws<ArgumentException>(() => SandboxRuntimeTempSlotLayout.ContainerDirectory("relative-home"));
    }

    [Test]
    public void AclSpec_RejectsEmptyDuplicateAndOverlyBroadPrincipals()
    {
        if (!OperatingSystem.IsWindows()) return;

        var owner      = new SecurityIdentifier(SandboxRuntimeTempSlotTestSupport.HostUserSid());
        var shared     = new SecurityIdentifier(SandboxRuntimeTempSlotTestSupport.SandboxSharedSid);
        var account    = new SecurityIdentifier(SandboxRuntimeTempSlotTestSupport.SandboxAccountSid);
        var capability = new SecurityIdentifier(SandboxRuntimeTempSlotTestSupport.SlotCapabilitySid);
        var system     = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        Assert.Throws<ArgumentException>(() => SandboxRuntimeTempSlotAclSpec.Create(owner, [], [shared], [account], capability));
        Assert.Throws<ArgumentException>(() => SandboxRuntimeTempSlotAclSpec.Create(owner, [system], [], [account], capability));
        Assert.Throws<ArgumentException>(() => SandboxRuntimeTempSlotAclSpec.Create(owner, [system], [shared], [], capability));
        Assert.Throws<ArgumentException>(() => SandboxRuntimeTempSlotAclSpec.Create(owner, [system], [shared], [account], shared));
        Assert.Throws<ArgumentException>(() => SandboxRuntimeTempSlotAclSpec.Create(owner, [system], [shared, account], [account], capability));
        Assert.Throws<ArgumentException>(() => SandboxRuntimeTempSlotAclSpec.Create(
            owner, [system], [new SecurityIdentifier(WellKnownSidType.WorldSid, null)], [account], capability));
        Assert.Throws<ArgumentException>(() => SandboxRuntimeTempSlotAclSpec.Create(
            owner, [system], [SandboxRuntimeTempSlotAcl.OwnerRightsSid], [account], capability));
        Assert.Throws<ArgumentException>(() => SandboxRuntimeTempSlotAclSpec.Create(
            owner, [system], [shared], [account], new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null)));
        Assert.Throws<ArgumentException>(() => SandboxRuntimeTempContainerAclSpec.Create(owner, [], [shared]));
        Assert.Throws<ArgumentException>(() => SandboxRuntimeTempContainerAclSpec.Create(owner, [system], []));
        Assert.DoesNotThrow(() => SandboxRuntimeTempSlotAclSpec.Create(owner, [system], [shared], [account], capability));
    }

    [Test]
    public async Task Initialize_CreatesFreshSlotRefusesReuseAndAllowsRecreateAfterReclaim()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();
        var       slot = SandboxRuntimeTempSlotTestSupport.Initialize(home);

        Assert.That(Directory.Exists(slot.Directory), Is.True);
        Assert.That(Directory.Exists(slot.WritableDirectory), Is.True);

        // 槽位根（slot-XX）自身采用不可写受保护 DACL：显式拒绝全部写位（含 FILE_ADD_FILE/FILE_WRITE_ATTRIBUTES），
        // 防止运行期在原地被设为重解析点。
        var rootSecurity = SandboxRuntimeTempSlotTestSupport.ReadAccess(slot.Directory);
        Assert.That(rootSecurity.AreAccessRulesProtected, Is.True,
                    "the slot root DACL must not inherit AppData ACEs added later");
        Assert.That(rootSecurity.GetOwner(typeof(SecurityIdentifier))?.Value,
                    Is.EqualTo(SandboxRuntimeTempSlotTestSupport.HostUserSid()));
        var rootRules = rootSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier))
                                    .Cast<FileSystemAccessRule>()
                                    .ToArray();
        Assert.That(rootRules.Any(rule => rule.IsInherited), Is.False, "every slot-root ACE must be explicit");
        Assert.That(rootRules.Any(rule => rule.AccessControlType == AccessControlType.Deny &&
                                          rule.IdentityReference.Value ==
                                          SandboxRuntimeTempSlotTestSupport.SandboxSharedSid &&
                                          rule.FileSystemRights.HasFlag(FileSystemRights.WriteData) &&
                                          rule.FileSystemRights.HasFlag(FileSystemRights.WriteAttributes)), Is.True,
                    "the immutable slot root must deny WriteData and WriteAttributes to prevent in-place reparse points");

        // 可写子目录（slot-XX\work）承载两轮写主体（共享沙箱组 + 本槽位 capability）与 OWNER RIGHTS 继承约束。
        var security = SandboxRuntimeTempSlotTestSupport.ReadAccess(slot.WritableDirectory);
        Assert.That(security.AreAccessRulesProtected, Is.True,
                    "the writable slot directory DACL must not inherit AppData ACEs added later");
        Assert.That(security.GetOwner(typeof(SecurityIdentifier))?.Value,
                    Is.EqualTo(SandboxRuntimeTempSlotTestSupport.HostUserSid()));

        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                            .Cast<FileSystemAccessRule>()
                            .ToArray();
        Assert.That(rules.Any(rule => rule.IsInherited), Is.False, "every ACE must be explicit");

        // 两轮写主体（共享沙箱组 + 本槽位 capability）在槽位上均具备对称的无删除写与显式根保护 deny。
        foreach (var sid in new[]
                 {
                     SandboxRuntimeTempSlotTestSupport.SandboxSharedSid,
                     SandboxRuntimeTempSlotTestSupport.SlotCapabilitySid
                 })
        {
            var sidRules = rules.Where(rule => rule.IdentityReference.Value == sid).ToArray();
            Assert.That(sidRules.Any(rule => rule.AccessControlType == AccessControlType.Deny &&
                                             rule.FileSystemRights.HasFlag(FileSystemRights.Delete)), Is.True,
                        $"the slot root must carry an explicit deny of DELETE for {sid}");
            Assert.That(sidRules.Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                                             !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly) &&
                                             rule.FileSystemRights.HasFlag(FileSystemRights.WriteData) &&
                                             !rule.FileSystemRights.HasFlag(FileSystemRights.Delete)), Is.True,
                        $"the slot root must grant delete-free write to {sid}");
            Assert.That(sidRules.Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                                             rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly) &&
                                             rule.InheritanceFlags ==
                                             (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) &&
                                             rule.FileSystemRights.HasFlag(FileSystemRights.Delete)), Is.True,
                        $"children inside the slot must inherit Modify (including DELETE) for {sid}");
        }

        // OWNER RIGHTS (S-1-3-4) 必须以 CI|OI|IO 且仅 READ_CONTROL 传播给子项，剥夺子项 Owner 隐式 WRITE_DAC。
        var ownerRightsRules = rules.Where(rule => rule.IdentityReference.Value ==
                                                   SandboxRuntimeTempSlotAcl.OwnerRightsSid.Value)
                                    .ToArray();
        Assert.That(ownerRightsRules, Has.Length.EqualTo(1));
        Assert.That(ownerRightsRules[0].AccessControlType, Is.EqualTo(AccessControlType.Allow));
        Assert.That(ownerRightsRules[0].InheritanceFlags,
                    Is.EqualTo(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
        Assert.That(ownerRightsRules[0].PropagationFlags, Is.EqualTo(PropagationFlags.InheritOnly));
        Assert.That((long)ownerRightsRules[0].FileSystemRights & WriteRightsMask, Is.Zero);

        // 父容器只含共享沙箱穿越，不含任何单槽位 capability SID，且无写权限与列目录权限。
        var container = SandboxRuntimeTempSlotTestSupport.ReadAccess(
            SandboxRuntimeTempSlotLayout.SlotsDirectory(home.Root));
        Assert.That(container.AreAccessRulesProtected, Is.True);
        var containerRules = container.GetAccessRules(true, true, typeof(SecurityIdentifier))
                                      .Cast<FileSystemAccessRule>()
                                      .ToArray();
        Assert.That(containerRules.Any(rule => rule.IsInherited), Is.False);
        Assert.That(containerRules.Any(rule => rule.IdentityReference.Value ==
                                               SandboxRuntimeTempSlotTestSupport.SlotCapabilitySid), Is.False,
                    "the shared parent container must never embed per-slot capability SIDs");
        Assert.That(containerRules.Where(rule => rule.AccessControlType == AccessControlType.Allow &&
                                                 rule.IdentityReference.Value ==
                                                 SandboxRuntimeTempSlotTestSupport.SandboxSharedSid)
                                  .All(rule => ((long)rule.FileSystemRights & WriteRightsMask) == 0 &&
                                               !rule.FileSystemRights.HasFlag(FileSystemRights.ListDirectory)), Is.True,
                    "the container must grant neither write nor list access to the shared sandbox identity");

        // 未回收前禁止把已有槽位目录当作新分配复用。
        var reuseError = Assert.Throws<IOException>(() => SandboxRuntimeTempSlotTestSupport.Initialize(home));
        TestAssert.Contains("already exists", reuseError!.Message, StringComparison.OrdinalIgnoreCase);

        SandboxRuntimeTempSlotContract.ValidateSlot(
            slot, [new SecurityIdentifier(SandboxRuntimeTempSlotTestSupport.ForeignCapabilitySid)]);

        // 确认回收并移除旧目录后，才允许为同一索引重新创建干净目录。
        var reclaimed = await SandboxRuntimeTempSlotReclaimer.ReclaimAsync(slot, true, null, CancellationToken.None);
        Assert.That(reclaimed.Reclaimed, Is.True);
        var recreated = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        Assert.That(Directory.Exists(recreated.Directory), Is.True);
        Assert.That(Directory.Exists(recreated.WritableDirectory), Is.True);
    }

    [Test]
    public void Initialize_MultipleSlotsWithDistinctCapabilitiesDoNotClobberContainer()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home  = new TestTempDir();
        var       slot0 = SandboxRuntimeTempSlotTestSupport.Initialize(
            home, 0, SandboxRuntimeTempSlotTestSupport.SlotCapabilitySid);
        var       slot1 = SandboxRuntimeTempSlotTestSupport.Initialize(
            home, 1, SandboxRuntimeTempSlotTestSupport.SecondSlotCapabilitySid);

        // 初始化第二个槽位后，父容器与两个槽位的契约校验均保持通过，且彼此 capability 在必填账户 SID 下互相隔离写。
        SandboxRuntimeTempSlotContract.ValidateContainerDirectory(
            SandboxRuntimeTempSlotLayout.ContainerDirectory(home.Root), slot0.AclSpec.ToContainerSpec());
        SandboxRuntimeTempSlotContract.ValidateContainerDirectory(
            SandboxRuntimeTempSlotLayout.SlotsDirectory(home.Root), slot1.AclSpec.ToContainerSpec());
        SandboxRuntimeTempSlotContract.ValidateSlot(slot0, [slot1.AclSpec.SlotCapabilitySid]);
        SandboxRuntimeTempSlotContract.ValidateSlot(slot1, [slot0.AclSpec.SlotCapabilitySid]);
    }

    [Test]
    public void Initialize_RollsBackNewlyCreatedSlotOnFailureSoRetrySucceeds()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home         = new TestTempDir();
        var       resolvedHome = SandboxFinalPathResolver.TryGetFinalDirectoryPath(home.Root) ?? home.Root;
        var       slotPath     = SandboxRuntimeTempSlotLayout.SlotDirectory(resolvedHome, 0);
        var       spec         = SandboxRuntimeTempSlotTestSupport.CreateSpec();

        // 使用合法模板，在新建槽位目录后注入失败：初始化器必须通过同一新建句柄原子回滚该空目录，
        // 保留原始异常且使后续同一索引重试可直接成功。
        var failure = Assert.Throws<InvalidOperationException>(() => SandboxRuntimeTempSlotInitializer.Initialize(
            home.Root, 0, spec, CancellationToken.None,
            afterSlotCreated: _ => throw new InvalidOperationException("simulated post-create failure"),
            tryMarkDeleteOnClose: null,
            verifyPathMissing: null));
        TestAssert.Contains("simulated post-create failure", failure!.Message, StringComparison.Ordinal);
        Assert.That(Directory.Exists(slotPath), Is.False,
                    "a newly created empty slot must be rolled back on initialization failure");

        var retried = SandboxRuntimeTempSlotTestSupport.Initialize(home, 0);
        Assert.That(Directory.Exists(retried.Directory), Is.True);
    }

    [Test]
    public async Task Initialize_PreservesOriginalErrorAndIdentityWhenRollbackFails()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home         = new TestTempDir();
        var       resolvedHome = SandboxFinalPathResolver.TryGetFinalDirectoryPath(home.Root) ?? home.Root;
        var       slotPath     = SandboxRuntimeTempSlotLayout.SlotDirectory(resolvedHome, 0);
        var       spec         = SandboxRuntimeTempSlotTestSupport.CreateSpec();

        // 模拟新建目录后失败且回滚 delete-on-close 也失败（Win32 error 5）：必须保留原异常、错误码和目录身份。
        var rollbackFailure = Assert.Throws<SandboxRuntimeTempSlotRollbackException>(
            () => SandboxRuntimeTempSlotInitializer.Initialize(
                home.Root, 0, spec, CancellationToken.None,
                afterSlotCreated: _ => throw new InvalidOperationException("simulated acl write failure"),
                tryMarkDeleteOnClose: _ => (false, SandboxRuntimeTempSlotNative.ErrorAccessDenied),
                verifyPathMissing: null));

        Assert.That(rollbackFailure!.SlotPath, Is.EqualTo(slotPath).IgnoreCase);
        Assert.That(rollbackFailure.RollbackWin32Error, Is.EqualTo(SandboxRuntimeTempSlotNative.ErrorAccessDenied));
        Assert.That(rollbackFailure.DeletePendingOrRemaining, Is.False);
        Assert.That(rollbackFailure.InnerException, Is.InstanceOf<InvalidOperationException>());
        TestAssert.Contains("simulated acl write failure", rollbackFailure.InnerException!.Message,
                            StringComparison.Ordinal);
        Assert.That(rollbackFailure.LeftoverIdentity, Is.Not.Null);
        Assert.That(Directory.Exists(rollbackFailure.SlotPath), Is.True);

        // 保留下来的 LeftoverIdentity 足以让回收器安全认领并清理残留空槽位，随后同一索引可再次初始化。
        var leftoverSlot = new SandboxRuntimeTempSlot(
            rollbackFailure.SlotPath, rollbackFailure.LeftoverIdentity!,
            Path.Combine(rollbackFailure.SlotPath, SandboxRuntimeTempSlotLayout.WritableDirectoryName),
            rollbackFailure.LeftoverIdentity!, spec);
        var reclaimed    = await SandboxRuntimeTempSlotReclaimer.ReclaimAsync(
            leftoverSlot, wholeTreeExitConfirmed: true, options: null, CancellationToken.None);
        Assert.That(reclaimed.Reclaimed, Is.True);

        var retried = SandboxRuntimeTempSlotTestSupport.Initialize(home, 0);
        Assert.That(Directory.Exists(retried.Directory), Is.True);
    }

    [Test]
    public void Initialize_PreservesIdentityWhenRollbackLeavesDirectoryPendingDelete()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();
        var       spec = SandboxRuntimeTempSlotTestSupport.CreateSpec();

        // 模拟传统删除标记成功，但关闭新建句柄后因外部句柄占用导致目录仍停留在 delete-pending（Win32 error 5）：
        // 初始化器绝不能把它当成回滚成功，必须抛出带原异常和 LeftoverIdentity 的回滚异常。
        var pendingFailure = Assert.Throws<SandboxRuntimeTempSlotRollbackException>(
            () => SandboxRuntimeTempSlotInitializer.Initialize(
                home.Root, 0, spec, CancellationToken.None,
                afterSlotCreated: _ => throw new InvalidOperationException("simulated validation failure"),
                tryMarkDeleteOnClose: _ => (true, 0),
                verifyPathMissing: _ => (false, SandboxRuntimeTempSlotNative.ErrorAccessDenied)));

        Assert.That(pendingFailure!.DeletePendingOrRemaining, Is.True);
        Assert.That(pendingFailure.RollbackWin32Error, Is.EqualTo(SandboxRuntimeTempSlotNative.ErrorAccessDenied));
        Assert.That(pendingFailure.InnerException, Is.InstanceOf<InvalidOperationException>());
        TestAssert.Contains("simulated validation failure", pendingFailure.InnerException!.Message,
                            StringComparison.Ordinal);
        Assert.That(pendingFailure.LeftoverIdentity, Is.Not.Null);
        Assert.That(pendingFailure.LeftoverIdentity!.Path, Is.EqualTo(pendingFailure.SlotPath));
    }

    [Test]
    public async Task Initialize_ResolvesHomeJunctionAliasToFinalPathSoReclaimSurvivesAliasRemoval()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root       = new TestTempDir();
        var       actualHome = Path.Combine(root.Root, "actual-home");
        var       aliasHome  = Path.Combine(root.Root, "alias-home");
        Directory.CreateDirectory(actualHome);
        if (!SandboxRuntimeTempSlotTestSupport.TryCreateJunction(aliasHome, actualHome))
            Assert.Ignore("Directory junctions are unavailable on this machine.");

        var spec         = SandboxRuntimeTempSlotTestSupport.CreateSpec();
        var slot         = SandboxRuntimeTempSlotInitializer.Initialize(aliasHome, 0, spec);
        var resolvedHome = SandboxFinalPathResolver.TryGetFinalDirectoryPath(actualHome) ?? actualHome;
        var expectedPath = SandboxRuntimeTempSlotLayout.SlotDirectory(resolvedHome, 0);

        // 返回路径与身份必须绑定到从 home 句柄解析出的最终路径，而非易变的 junction 别名。
        Assert.That(slot.Directory, Is.EqualTo(expectedPath).IgnoreCase);
        Assert.That(slot.Identity.Path, Is.EqualTo(expectedPath).IgnoreCase);

        // 即使随后撤销 junction 别名，仍可通过返回的最终路径与身份完成契约校验与安全回收。
        Directory.Delete(aliasHome);
        Assert.That(Directory.Exists(aliasHome), Is.False);
        SandboxRuntimeTempSlotContract.ValidateSlot(slot);
        var reclaimed = await SandboxRuntimeTempSlotReclaimer.ReclaimAsync(slot, true, null, CancellationToken.None);
        Assert.That(reclaimed.Reclaimed, Is.True);

        // 受管子目录（如 slot-01）若被替换为 junction，即便通过 junction home 访问也必须严格拒绝。
        Assert.That(SandboxRuntimeTempSlotTestSupport.TryCreateJunction(aliasHome, actualHome), Is.True);
        var outside = Path.Combine(root.Root, "outside");
        Directory.CreateDirectory(outside);
        var badSlot = SandboxRuntimeTempSlotLayout.SlotDirectory(actualHome, 1);
        Assert.That(SandboxRuntimeTempSlotTestSupport.TryCreateJunction(badSlot, outside), Is.True);
        Assert.Throws<IOException>(() => SandboxRuntimeTempSlotInitializer.Initialize(aliasHome, 1, spec));
    }

    [Test]
    public async Task Initialize_AndReclaim_SucceedBeyondMaxPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestTempDir();
        // 构造超过 MAX_PATH (260) 的深层 home 路径，验证原生 Win32 I/O 边界自动补齐扩展路径前缀。
        var longHome = root.Root;
        while (longHome.Length < 250)
            longHome = Path.Combine(longHome, "long-sandbox-home-segment-0123456789");
        Directory.CreateDirectory(longHome);

        var spec = SandboxRuntimeTempSlotTestSupport.CreateSpec();
        var slot = SandboxRuntimeTempSlotInitializer.Initialize(longHome, 0, spec);

        Assert.That(slot.Directory.Length, Is.GreaterThan(260));
        Assert.That(slot.Directory.StartsWith(@"\\?\", StringComparison.Ordinal), Is.False,
                    "returned slot paths stay clean without extended prefixes");
        SandboxRuntimeTempSlotContract.ValidateSlot(slot);

        var fileInSlot = Path.Combine(slot.WritableDirectory, "probe.txt");
        File.WriteAllText(fileInSlot, "ok");

        var reclaimed = await SandboxRuntimeTempSlotReclaimer.ReclaimAsync(slot, true, null, CancellationToken.None);
        Assert.That(reclaimed.Reclaimed, Is.True);
        Assert.That(reclaimed.DeletedEntries, Is.EqualTo(1));
        Assert.That(Directory.Exists(slot.Directory), Is.False);
    }

    [Test]
    public void Initialize_RejectsMissingSandboxHome()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home    = new TestTempDir();
        var       missing = Path.Combine(home.Root, "not-provisioned");
        Assert.Throws<DirectoryNotFoundException>(() => SandboxRuntimeTempSlotInitializer.Initialize(
            missing, 0, SandboxRuntimeTempSlotTestSupport.CreateSpec()));
        Assert.That(Directory.Exists(SandboxRuntimeTempSlotLayout.ContainerDirectory(missing)), Is.False);
    }

    [Test]
    public void Initialize_RejectsSlotReplacedWithReparsePointBeforeTouchingTargetAcl()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home   = new TestTempDir();
        var       target = Path.Combine(home.Root, "external-target");
        Directory.CreateDirectory(target);
        // 先经合法初始化建立受保护容器（runtime-tmp-v2\slots），再把 slot-00 替换为指向外部的 junction。
        SandboxRuntimeTempSlotTestSupport.Initialize(home, 1, SandboxRuntimeTempSlotTestSupport.SecondSlotCapabilitySid);
        var slotPath = SandboxRuntimeTempSlotLayout.SlotDirectory(home.Root, 0);
        if (!SandboxRuntimeTempSlotTestSupport.TryCreateJunction(slotPath, target))
            Assert.Ignore("Directory junctions are unavailable on this machine.");

        var beforeProtected = SandboxRuntimeTempSlotTestSupport.ReadAccess(target).AreAccessRulesProtected;
        Assert.Throws<IOException>(() => SandboxRuntimeTempSlotTestSupport.Initialize(home, 0));
        var afterProtected = SandboxRuntimeTempSlotTestSupport.ReadAccess(target).AreAccessRulesProtected;
        Assert.That(afterProtected, Is.EqualTo(beforeProtected),
                    "rejecting a pre-existing reparse point must never touch the target directory's DACL");
    }

    [Test]
    public void Initialize_RefusesTamperedPreExistingContainerWithoutRepairingIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home  = new TestTempDir();
        var       slot0 = SandboxRuntimeTempSlotTestSupport.Initialize(home, 0);
        var       slots = SandboxRuntimeTempSlotLayout.SlotsDirectory(home.Root);

        // 篡改已存在的 slots 容器 DACL：给共享沙箱组增加 ListDirectory。
        SandboxRuntimeTempSlotTestSupport.Tamper(
            slots,
            security => security.AddAccessRule(
                SandboxRuntimeTempSlotTestSupport.AllowRule(
                    SandboxRuntimeTempSlotTestSupport.SandboxSharedSid,
                    FileSystemRights.ListDirectory,
                    InheritanceFlags.None, PropagationFlags.None)));

        // 初始化新槽位时不得静默修补被篡改的已有容器：必须 fail-closed 拒绝，且保持原 DACL 未被改写。
        var error = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotTestSupport.Initialize(
                home, 1, SandboxRuntimeTempSlotTestSupport.SecondSlotCapabilitySid));
        TestAssert.Contains("must not list", error!.Message, StringComparison.Ordinal);
        Assert.That(Directory.Exists(SandboxRuntimeTempSlotLayout.SlotDirectory(home.Root, 1)), Is.False);
        Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateContainerDirectory(slots, slot0.AclSpec.ToContainerSpec()),
            "existing tampered container must not have been silently repaired");
    }

    [Test]
    public void OpenAndInitializeContainer_RollsBackNewlyCreatedContainerOnFailureSoRetrySucceeds()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home          = new TestTempDir();
        var       resolvedHome  = SandboxFinalPathResolver.TryGetFinalDirectoryPath(home.Root) ?? home.Root;
        var       containerPath = SandboxRuntimeTempSlotLayout.ContainerDirectory(resolvedHome);
        var       containerSpec = SandboxRuntimeTempSlotTestSupport.CreateSpec().ToContainerSpec();

        using (var homeHandle = SandboxRuntimeTempSlotNative.OpenDirectory(
                   home.Root,
                   SandboxRuntimeTempSlotNative.FileTraverse | SandboxRuntimeTempSlotNative.FileReadAttributes |
                   SandboxRuntimeTempSlotNative.Synchronize,
                   SandboxRuntimeTempSlotNative.FileShareAll, "test home"))
        {
            var failure = Assert.Throws<InvalidOperationException>(
                () => SandboxRuntimeTempSlotInitializer.OpenAndInitializeContainer(
                    homeHandle, SandboxRuntimeTempSlotLayout.ContainerDirectoryName, containerPath, containerSpec,
                    afterOpened: (_, created) =>
                    {
                        Assert.That(created, Is.True);
                        throw new InvalidOperationException("simulated container init failure");
                    }));
            TestAssert.Contains("simulated container init failure", failure!.Message, StringComparison.Ordinal);
        }

        // 本次新建的半成品空容器必须已回滚移除，否则后续重试会撞上不完整 DACL 而永久自锁。
        Assert.That(Directory.Exists(containerPath), Is.False,
                    "a newly created container must be rolled back when initialization fails");

        var retried = SandboxRuntimeTempSlotTestSupport.Initialize(home, 0);
        Assert.That(Directory.Exists(retried.WritableDirectory), Is.True);
    }

    [Test]
    public void OpenAndInitializeContainer_PreservesOriginalErrorWhenRollbackFails()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home          = new TestTempDir();
        var       resolvedHome  = SandboxFinalPathResolver.TryGetFinalDirectoryPath(home.Root) ?? home.Root;
        var       containerPath = SandboxRuntimeTempSlotLayout.ContainerDirectory(resolvedHome);
        var       containerSpec = SandboxRuntimeTempSlotTestSupport.CreateSpec().ToContainerSpec();

        using var homeHandle = SandboxRuntimeTempSlotNative.OpenDirectory(
            home.Root,
            SandboxRuntimeTempSlotNative.FileTraverse | SandboxRuntimeTempSlotNative.FileReadAttributes |
            SandboxRuntimeTempSlotNative.Synchronize,
            SandboxRuntimeTempSlotNative.FileShareAll, "test home");

        var failure = Assert.Throws<InvalidOperationException>(
            () => SandboxRuntimeTempSlotInitializer.OpenAndInitializeContainer(
                homeHandle, SandboxRuntimeTempSlotLayout.ContainerDirectoryName, containerPath, containerSpec,
                afterOpened: (_, created) =>
                {
                    Assert.That(created, Is.True);
                    throw new InvalidOperationException("simulated container acl failure");
                },
                tryMarkDeleteOnClose: _ => (false, SandboxRuntimeTempSlotNative.ErrorAccessDenied)));

        Assert.That(failure!.InnerException, Is.InstanceOf<InvalidOperationException>());
        TestAssert.Contains("simulated container acl failure", failure.InnerException!.Message,
                            StringComparison.Ordinal);
        TestAssert.Contains("Win32 error 5", failure.Message, StringComparison.Ordinal);
        Assert.That(Directory.Exists(containerPath), Is.True);
    }

    [Test]
    public void OpenAndInitializeContainer_DoesNotDeleteOrRepairPreExistingTamperedContainer()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home          = new TestTempDir();
        var       slot0         = SandboxRuntimeTempSlotTestSupport.Initialize(home, 0);
        var       resolvedHome  = SandboxFinalPathResolver.TryGetFinalDirectoryPath(home.Root) ?? home.Root;
        var       containerPath = SandboxRuntimeTempSlotLayout.ContainerDirectory(resolvedHome);
        var       slotsPath     = SandboxRuntimeTempSlotLayout.SlotsDirectory(resolvedHome);
        var       containerSpec = slot0.AclSpec.ToContainerSpec();

        // 篡改已存在的 slots 容器 DACL。
        SandboxRuntimeTempSlotTestSupport.Tamper(
            slotsPath,
            security => security.AddAccessRule(
                SandboxRuntimeTempSlotTestSupport.AllowRule(
                    SandboxRuntimeTempSlotTestSupport.SandboxSharedSid,
                    FileSystemRights.ListDirectory,
                    InheritanceFlags.None, PropagationFlags.None)));

        using var containerHandle = SandboxRuntimeTempSlotNative.OpenDirectory(
            containerPath,
            SandboxRuntimeTempSlotNative.FileTraverse | SandboxRuntimeTempSlotNative.FileReadAttributes |
            SandboxRuntimeTempSlotNative.Synchronize,
            SandboxRuntimeTempSlotNative.FileShareAll, "test container");

        var error = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotInitializer.OpenAndInitializeContainer(
                containerHandle, SandboxRuntimeTempSlotLayout.SlotsDirectoryName, slotsPath, containerSpec));
        TestAssert.Contains("must not list", error!.Message, StringComparison.Ordinal);
        Assert.That(Directory.Exists(slotsPath), Is.True,
                    "a pre-existing container must never be deleted on validation failure");
        Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateContainerDirectory(slotsPath, containerSpec),
            "existing tampered container must remain unrepaired");
    }

    [Test]
    public void OpenAndInitializeContainer_ConcurrentHandleWithFileShareReadWriteBlocksUnlinkOnRollback()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home          = new TestTempDir();
        var       resolvedHome  = SandboxFinalPathResolver.TryGetFinalDirectoryPath(home.Root) ?? home.Root;
        var       containerPath = SandboxRuntimeTempSlotLayout.ContainerDirectory(resolvedHome);
        var       containerSpec = SandboxRuntimeTempSlotTestSupport.CreateSpec().ToContainerSpec();

        using var homeHandle = SandboxRuntimeTempSlotNative.OpenDirectory(
            home.Root,
            SandboxRuntimeTempSlotNative.FileTraverse | SandboxRuntimeTempSlotNative.FileReadAttributes |
            SandboxRuntimeTempSlotNative.Synchronize,
            SandboxRuntimeTempSlotNative.FileShareAll, "test home");

        SafeFileHandle? concurrentHandle = null;
        try
        {
            const uint containerReadAccess = SandboxRuntimeTempSlotNative.ReadControl        |
                                             SandboxRuntimeTempSlotNative.WriteDac           |
                                             SandboxRuntimeTempSlotNative.FileReadAttributes |
                                             SandboxRuntimeTempSlotNative.FileTraverse       |
                                             SandboxRuntimeTempSlotNative.Synchronize;

            var failure = Assert.Throws<InvalidOperationException>(
                () => SandboxRuntimeTempSlotInitializer.OpenAndInitializeContainer(
                    homeHandle, SandboxRuntimeTempSlotLayout.ContainerDirectoryName, containerPath, containerSpec,
                    afterOpened: (_, created) =>
                    {
                        Assert.That(created, Is.True);
                        concurrentHandle = SandboxRuntimeTempSlotNative.OpenChild(
                            homeHandle, SandboxRuntimeTempSlotLayout.ContainerDirectoryName,
                            containerReadAccess, out var openError,
                            SandboxRuntimeTempSlotNative.FileShareReadWrite);
                        Assert.That(concurrentHandle, Is.Not.Null,
                                    $"Concurrent FileShareReadWrite open failed with Win32 error {openError}.");
                        throw new InvalidOperationException("simulated post-create failure under concurrent open");
                    }));

            Assert.That(failure!.InnerException, Is.InstanceOf<InvalidOperationException>());
            TestAssert.Contains("simulated post-create failure under concurrent open",
                                failure.InnerException!.Message, StringComparison.Ordinal);
            TestAssert.Contains("rollback", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.That(Directory.Exists(containerPath), Is.True,
                        "rollback must not unlink the directory while another handle holds it without FILE_SHARE_DELETE");
            SandboxRuntimeTempSlotNative.RequireDirectory(concurrentHandle!, containerPath, out _);
        }
        finally
        {
            concurrentHandle?.Dispose();
        }
    }

    [Test]
    public void TryReOpenHandle_WithWriteOwnerAndSynchronizeSucceedsForCurrentOwner()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home    = new TestTempDir();
        var       slot    = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        var       hostSid = new SecurityIdentifier(SandboxRuntimeTempSlotTestSupport.HostUserSid());

        using var handle = SandboxRuntimeTempSlotNative.OpenDirectory(
            slot.Directory,
            SandboxRuntimeTempSlotNative.ReadControl | SandboxRuntimeTempSlotNative.WriteDac |
            SandboxRuntimeTempSlotNative.FileReadAttributes | SandboxRuntimeTempSlotNative.Synchronize,
            SandboxRuntimeTempSlotNative.FileShareReadWrite, "owner reopen probe");
        using var ownerHandle = SandboxRuntimeTempSlotNative.TryReOpenHandle(
            handle, SandboxRuntimeTempSlotNative.WriteOwner | SandboxRuntimeTempSlotNative.Synchronize,
            SandboxRuntimeTempSlotNative.FileShareReadWrite, out var openError);

        Assert.That(ownerHandle, Is.Not.Null, $"Reopening with WRITE_OWNER | SYNCHRONIZE failed (Win32 error {openError}).");
        Assert.That(SandboxRuntimeTempSlotNative.SetOwner(ownerHandle!, hostSid), Is.Zero);
    }

    [Test]
    public void Contract_RejectsSandboxAccountSidBeforeWriteAndOnTamperedDacl()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home       = new TestTempDir();
        var       accountSid = new SecurityIdentifier(SandboxRuntimeTempSlotTestSupport.SandboxAccountSid);
        var       sharedSid  = new SecurityIdentifier(SandboxRuntimeTempSlotTestSupport.SandboxSharedSid);
        var       ownCap     = new SecurityIdentifier(SandboxRuntimeTempSlotTestSupport.SlotCapabilitySid);
        var       foreignCap = new SecurityIdentifier(SandboxRuntimeTempSlotTestSupport.ForeignCapabilitySid);

        // 第一道防线：模板构造期在写入任何 DACL 前即拒绝沙箱账户 SID 混入共享写主体。
        Assert.Throws<ArgumentException>(() => SandboxRuntimeTempSlotInitializer.CreateHostTemplate(
            [sharedSid, accountSid], [accountSid], ownCap));

        // 第二道防线：即使磁盘 DACL 事后被篡改授予沙箱账户 SID 写权限，契约校验基于必填 spec.SandboxAccountSids 仍 fail-closed。
        var slot = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        SandboxRuntimeTempSlotTestSupport.Tamper(
            slot.WritableDirectory,
            security => security.AddAccessRule(
                SandboxRuntimeTempSlotTestSupport.AllowRule(SandboxRuntimeTempSlotTestSupport.SandboxAccountSid,
                                                            FileSystemRights.Modify)));

        var error = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateSlot(slot, [foreignCap]));
        TestAssert.Contains("cross-slot write", error!.Message, StringComparison.Ordinal);
        TestAssert.Contains("over-broad write", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Contract_RejectsMissingOrOverBroadOwnerRightsInheritanceAce()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home       = new TestTempDir();
        var       foreignCap = new SecurityIdentifier(SandboxRuntimeTempSlotTestSupport.ForeignCapabilitySid);

        // 场景 1：移除 OWNER RIGHTS (S-1-3-4) 继承 ACE → 子项所有者恢复隐式 WRITE_DAC，同时触发 owner rights 与 cross-slot write。
        var slot0 = SandboxRuntimeTempSlotTestSupport.Initialize(home, 0);
        SandboxRuntimeTempSlotTestSupport.RemoveAllowRules(slot0.WritableDirectory,
                                                           SandboxRuntimeTempSlotAcl.OwnerRightsSid.Value);
        var missingError = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateSlot(slot0, [foreignCap]));
        TestAssert.Contains("owner rights: missing", missingError!.Message, StringComparison.Ordinal);
        TestAssert.Contains("cross-slot write", missingError.Message, StringComparison.Ordinal);

        // 场景 2：给 OWNER RIGHTS (S-1-3-4) 授予 WRITE_DAC → 同账户另一槽位可改写子项 DACL，必须 fail-closed。
        var slot1 = SandboxRuntimeTempSlotTestSupport.Initialize(
            home, 1, SandboxRuntimeTempSlotTestSupport.SecondSlotCapabilitySid);
        SandboxRuntimeTempSlotTestSupport.Tamper(
            slot1.WritableDirectory,
            security => security.AddAccessRule(
                SandboxRuntimeTempSlotTestSupport.AllowRule(SandboxRuntimeTempSlotAcl.OwnerRightsSid.Value,
                                                            FileSystemRights.ChangePermissions,
                                                            InheritanceFlags.ContainerInherit |
                                                            InheritanceFlags.ObjectInherit,
                                                            PropagationFlags.InheritOnly)));
        var writeDacError = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateSlot(slot1, [foreignCap]));
        TestAssert.Contains("owner rights: OWNER RIGHTS", writeDacError!.Message, StringComparison.Ordinal);
        TestAssert.Contains("cross-slot write", writeDacError.Message, StringComparison.Ordinal);

        // 场景 3：OWNER RIGHTS 带 NoPropagateInherit → 深层子项将失去约束，必须拒绝。
        var slot2 = SandboxRuntimeTempSlotTestSupport.Initialize(
            home, 2, SandboxRuntimeTempSlotTestSupport.SlotCapabilitySid);
        SandboxRuntimeTempSlotTestSupport.RemoveAllowRules(slot2.WritableDirectory,
                                                           SandboxRuntimeTempSlotAcl.OwnerRightsSid.Value);
        SandboxRuntimeTempSlotTestSupport.Tamper(
            slot2.WritableDirectory,
            security => security.AddAccessRule(
                SandboxRuntimeTempSlotTestSupport.AllowRule(SandboxRuntimeTempSlotAcl.OwnerRightsSid.Value,
                                                            FileSystemRights.ReadPermissions,
                                                            InheritanceFlags.ContainerInherit |
                                                            InheritanceFlags.ObjectInherit,
                                                            PropagationFlags.InheritOnly |
                                                            PropagationFlags.NoPropagateInherit)));
        var propError = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateSlot(slot2, [foreignCap]));
        TestAssert.Contains("NoPropagateInherit", propError!.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Contract_RejectsUnprotectedInheritanceEntry()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();
        var       slot = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        SandboxRuntimeTempSlotTestSupport.Tamper(slot.WritableDirectory,
                                                 security => security.SetAccessRuleProtection(false, true));

        var error = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateSlot(slot));
        TestAssert.Contains("SE_DACL_PROTECTED", error!.Message, StringComparison.Ordinal);

        // 恢复保护标志供 TestTempDir 正常递归清理。
        SandboxRuntimeTempSlotTestSupport.Tamper(slot.WritableDirectory,
                                                 security => security.SetAccessRuleProtection(true, false));
    }

    [Test]
    public void Contract_RejectsMissingCapabilityWriteAce()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();
        var       slot = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        SandboxRuntimeTempSlotTestSupport.RemoveAllowRules(slot.WritableDirectory,
                                                           SandboxRuntimeTempSlotTestSupport.SlotCapabilitySid);

        var error = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateSlot(slot));
        TestAssert.Contains("required write", error!.Message, StringComparison.Ordinal);
        TestAssert.Contains("write-restricted access check", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Contract_RejectsMissingSharedSandboxWriteAce()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();
        var       slot = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        SandboxRuntimeTempSlotTestSupport.RemoveAllowRules(slot.WritableDirectory,
                                                           SandboxRuntimeTempSlotTestSupport.SandboxSharedSid);

        var error = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateSlot(slot));
        TestAssert.Contains("write-restricted access check", error!.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Contract_RejectsOverBroadWriteAce()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();
        var       slot = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        SandboxRuntimeTempSlotTestSupport.Tamper(
            slot.WritableDirectory,
            security => security.AddAccessRule(
                new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                                         FileSystemRights.Modify,
                                         InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                                         PropagationFlags.None, AccessControlType.Allow)));

        var error = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateSlot(slot));
        TestAssert.Contains("over-broad write", error!.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Contract_RejectsUnauthorizedReadForNonAllowlistedPrincipal()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();
        var       slot = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        SandboxRuntimeTempSlotTestSupport.Tamper(
            slot.WritableDirectory,
            security => security.AddAccessRule(
                new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                                         FileSystemRights.ReadAndExecute,
                                         InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                                         PropagationFlags.None, AccessControlType.Allow)));

        var error = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateSlot(slot));
        TestAssert.Contains("unauthorized read/access", error!.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Contract_RejectsDenyAcePlacedAfterAllowAce()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();
        var       slot = SandboxRuntimeTempSlotTestSupport.Initialize(home);

        // 直接写非规范顺序 DACL：把 FullControl Allow 放到 RootDeniedRights Deny 之前，模拟 Windows 先命中 Allow。
        var info   = new DirectoryInfo(slot.WritableDirectory);
        var raw    = new RawSecurityDescriptor(info.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm(), 0);
        var newAcl = new RawAcl(raw.DiscretionaryAcl!.Revision, raw.DiscretionaryAcl.Count + 1);
        newAcl.InsertAce(0, new CommonAce(
            AceFlags.None, AceQualifier.AccessAllowed,
            unchecked((int)SandboxRuntimeTempSlotAcl.ManagementRights),
            slot.AclSpec.SlotCapabilitySid, false, null));
        foreach (var existing in raw.DiscretionaryAcl)
            newAcl.InsertAce(newAcl.Count, existing);
        raw.DiscretionaryAcl = newAcl;
        var bytes = new byte[raw.BinaryLength];
        raw.GetBinaryForm(bytes, 0);
        var tampered = new DirectorySecurity();
        tampered.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access);
        info.SetAccessControl(tampered);

        var error = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateSlot(slot));
        TestAssert.Contains("canonical order violated", error!.Message, StringComparison.Ordinal);
        TestAssert.Contains("root protection", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Contract_RejectsNonPropagatingInheritanceFlag()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();
        var       slot = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        SandboxRuntimeTempSlotTestSupport.RemoveAllowRules(slot.WritableDirectory,
                                                           SandboxRuntimeTempSlotTestSupport.SlotCapabilitySid);
        SandboxRuntimeTempSlotTestSupport.Tamper(
            slot.WritableDirectory,
            security =>
            {
                security.AddAccessRule(SandboxRuntimeTempSlotTestSupport.AllowRule(
                    SandboxRuntimeTempSlotTestSupport.SlotCapabilitySid,
                    (FileSystemRights)SandboxRuntimeTempSlotAcl.SlotRootWriteRights,
                    InheritanceFlags.None, PropagationFlags.None));
                security.AddAccessRule(SandboxRuntimeTempSlotTestSupport.AllowRule(
                    SandboxRuntimeTempSlotTestSupport.SlotCapabilitySid,
                    FileSystemRights.Modify,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.InheritOnly | PropagationFlags.NoPropagateInherit));
            });

        var error = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateSlot(slot));
        TestAssert.Contains("propagation flags", error!.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Contract_RejectsMissingExplicitDeny()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();
        var       slot = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        SandboxRuntimeTempSlotTestSupport.RemoveDenyRules(slot.WritableDirectory,
                                                          SandboxRuntimeTempSlotTestSupport.SlotCapabilitySid);

        var error = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateSlot(slot));
        TestAssert.Contains("explicit deny", error!.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Contract_RejectsUnexpectedDenyAce()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();
        var       slot = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        SandboxRuntimeTempSlotTestSupport.Tamper(
            slot.WritableDirectory,
            security => security.AddAccessRule(
                new FileSystemAccessRule(new SecurityIdentifier(SandboxRuntimeTempSlotTestSupport.HostUserSid()),
                                         FileSystemRights.ReadData, AccessControlType.Deny)));

        var error = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateSlot(slot));
        TestAssert.Contains("unexpected deny", error!.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Contract_RejectsOwnerThatCannotBeProven()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();
        var       slot = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        var forgedOwner = SandboxRuntimeTempSlotAclSpec.Create(
            new SecurityIdentifier(SandboxRuntimeTempSlotTestSupport.ForeignCapabilitySid),
            slot.AclSpec.ManagementSids, slot.AclSpec.SandboxSharedSids, slot.AclSpec.SandboxAccountSids,
            slot.AclSpec.SlotCapabilitySid);

        var error = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateSlot(slot with { AclSpec = forgedOwner }));
        TestAssert.Contains("owner", error!.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Contract_RejectsReplacedSlotDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home  = new TestTempDir();
        var       first = SandboxRuntimeTempSlotTestSupport.Initialize(
            home, 0, SandboxRuntimeTempSlotTestSupport.SlotCapabilitySid);
        var       other = SandboxRuntimeTempSlotTestSupport.Initialize(
            home, 1, SandboxRuntimeTempSlotTestSupport.SecondSlotCapabilitySid);

        // 用另一槽位的身份校验本槽位根与可写子目录：卷/128位文件 ID 不一致必须 fail-closed。
        var rootError = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateSlot(first with { Identity = other.Identity }));
        TestAssert.Contains("was replaced", rootError!.Message, StringComparison.Ordinal);

        var workError = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateSlot(first with { WritableIdentity = other.WritableIdentity }));
        TestAssert.Contains("was replaced", workError!.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Contract_RejectsCrossSlotCapabilityWrite()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home    = new TestTempDir();
        var       slot    = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        var       foreign = new SecurityIdentifier(SandboxRuntimeTempSlotTestSupport.ForeignCapabilitySid);
        SandboxRuntimeTempSlotTestSupport.Tamper(
            slot.WritableDirectory,
            security => security.AddAccessRule(
                SandboxRuntimeTempSlotTestSupport.AllowRule(SandboxRuntimeTempSlotTestSupport.ForeignCapabilitySid,
                                                            FileSystemRights.Modify)));

        var error = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateSlot(slot, [foreign]));
        TestAssert.Contains("cross-slot write", error!.Message, StringComparison.Ordinal);
        TestAssert.Contains("over-broad write", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Contract_RejectsOverBroadPermissionsWithinAllowlistedPrincipals()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();

        // 场景 1：白名单内的共享组与本槽 capability 子项继承 Allow 被扩大为 FullControl（多出 WRITE_DAC/WRITE_OWNER/FILE_DELETE_CHILD）。
        var slot0 = SandboxRuntimeTempSlotTestSupport.Initialize(home, 0);
        foreach (var sid in new[]
                 {
                     SandboxRuntimeTempSlotTestSupport.SandboxSharedSid,
                     SandboxRuntimeTempSlotTestSupport.SlotCapabilitySid
                 })
        {
            SandboxRuntimeTempSlotTestSupport.Tamper(
                slot0.WritableDirectory,
                security => security.AddAccessRule(
                    SandboxRuntimeTempSlotTestSupport.AllowRule(
                        sid, FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.InheritOnly)));
        }

        var childOverBroad = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateSlot(slot0));
        TestAssert.Contains("over-broad child rights", childOverBroad!.Message, StringComparison.Ordinal);

        // 场景 2：可写根上额外授予 FILE_DELETE_CHILD（模板 SlotRootWriteRights 未包含）。
        var slot1 = SandboxRuntimeTempSlotTestSupport.Initialize(
            home, 1, SandboxRuntimeTempSlotTestSupport.SecondSlotCapabilitySid);
        SandboxRuntimeTempSlotTestSupport.Tamper(
            slot1.WritableDirectory,
            security => security.AddAccessRule(
                SandboxRuntimeTempSlotTestSupport.AllowRule(
                    SandboxRuntimeTempSlotTestSupport.SecondSlotCapabilitySid,
                    FileSystemRights.DeleteSubdirectoriesAndFiles,
                    InheritanceFlags.None, PropagationFlags.None)));

        var rootOverBroad = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateSlot(slot1));
        TestAssert.Contains("over-broad root rights", rootOverBroad!.Message, StringComparison.Ordinal);

        // 场景 3：不可写槽位根（slot-XX）上给共享组放宽写属性或多余读权限 → 必须拒绝（防原地重解析点与越权）。
        var slot2 = SandboxRuntimeTempSlotTestSupport.Initialize(home, 2);
        SandboxRuntimeTempSlotTestSupport.Tamper(
            slot2.Directory,
            security => security.AddAccessRule(
                SandboxRuntimeTempSlotTestSupport.AllowRule(
                    SandboxRuntimeTempSlotTestSupport.SandboxSharedSid,
                    FileSystemRights.ReadPermissions,
                    InheritanceFlags.None, PropagationFlags.None)));

        var immutableRootOverBroad = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateSlot(slot2));
        TestAssert.Contains("beyond minimal traverse", immutableRootOverBroad!.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Identity_UsesFull128BitFileIdAndDetectsHighBitDifference()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();
        var       slot = SandboxRuntimeTempSlotTestSupport.Initialize(home);

        Assert.That(slot.Identity.VolumeSerialNumber, Is.Not.Zero);
        Assert.That(slot.Identity.FileId, Is.Not.EqualTo(UInt128.Zero));
        Assert.That(slot.Identity.Matches(slot.WritableIdentity), Is.False);

        // 高 64 位不同（ReFS 128 位 FileId 区分场景）：低 64 位相同也不得误判为同一目录。
        var highBitChanged = slot.Identity with
        {
            FileId = slot.Identity.FileId ^ ((UInt128)1 << 64)
        };
        Assert.That(slot.Identity.Matches(highBitChanged), Is.False);
    }

    [Test]
    public void CreateNewChildDirectory_AppliesProtectedDaclAtomicallyAtCreationTime()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home          = new TestTempDir();
        var       spec          = SandboxRuntimeTempSlotTestSupport.CreateSpec();
        var       containerSpec = spec.ToContainerSpec();
        var       childPath     = Path.Combine(home.Root, "atomic-container");

        using var parentHandle = SandboxRuntimeTempSlotNative.OpenDirectory(
            home.Root,
            SandboxRuntimeTempSlotNative.FileTraverse | SandboxRuntimeTempSlotNative.FileReadAttributes |
            SandboxRuntimeTempSlotNative.Synchronize,
            SandboxRuntimeTempSlotNative.FileShareAll, "test parent");
        using var childHandle = SandboxRuntimeTempSlotNative.CreateNewChildDirectory(
            parentHandle, "atomic-container",
            SandboxRuntimeTempSlotNative.ReadControl | SandboxRuntimeTempSlotNative.FileReadAttributes |
            SandboxRuntimeTempSlotNative.Synchronize,
            SandboxRuntimeTempSlotNative.FileShareReadWrite,
            childPath, "atomic container",
            SandboxRuntimeTempSlotAcl.BuildContainerDacl(containerSpec));

        // 未调用 ApplyContainerHandle 之前即直接核对磁盘安全描述符：创建瞬间已受保护且无任何继承 ACE。
        var security = SandboxRuntimeTempSlotNative.ReadSecurity(childHandle, childPath);
        Assert.Multiple(() =>
        {
            Assert.That(security.DaclPresent, Is.True);
            Assert.That(security.DaclProtected, Is.True);
            Assert.That(security.Aces.Any(ace => ace.Inherited), Is.False);
        });
        SandboxRuntimeTempSlotContract.ValidateContainerHandle(childHandle, childPath, containerSpec);
    }

    [Test]
    public void FinalPathResolver_PreservesUncDoubleBackslashPrefix()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SandboxFinalPathResolver.StripWin32ExtendedPrefix(@"\\?\UNC\fileserver\share\sandbox"),
                        Is.EqualTo(@"\\fileserver\share\sandbox"));
            Assert.That(SandboxFinalPathResolver.StripWin32ExtendedPrefix(@"\\?\C:\Users\demo\sandbox"),
                        Is.EqualTo(@"C:\Users\demo\sandbox"));
            Assert.That(SandboxFinalPathResolver.StripWin32ExtendedPrefix(@"C:\Users\demo\sandbox"),
                        Is.EqualTo(@"C:\Users\demo\sandbox"));
        });
    }

    [Test]
    public void Contract_RejectsContainerWriteListAndMissingDeny()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home      = new TestTempDir();
        var       slot      = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        var       container = SandboxRuntimeTempSlotLayout.SlotsDirectory(home.Root);

        SandboxRuntimeTempSlotTestSupport.Tamper(
            container,
            security => security.AddAccessRule(
                SandboxRuntimeTempSlotTestSupport.AllowRule(SandboxRuntimeTempSlotTestSupport.SandboxSharedSid,
                                                            FileSystemRights.ListDirectory,
                                                            InheritanceFlags.None, PropagationFlags.None)));
        var listError = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateContainerDirectory(container,
                                                                            slot.AclSpec.ToContainerSpec()));
        TestAssert.Contains("must not list", listError!.Message, StringComparison.Ordinal);

        SandboxRuntimeTempSlotTestSupport.RemoveDenyRules(container, SandboxRuntimeTempSlotTestSupport.SandboxSharedSid);
        var denyError = Assert.Throws<SandboxRuntimeTempSlotException>(
            () => SandboxRuntimeTempSlotContract.ValidateContainerDirectory(container,
                                                                            slot.AclSpec.ToContainerSpec()));
        TestAssert.Contains("explicit deny", denyError!.Message, StringComparison.Ordinal);
    }

    [Test]
    public void EffectiveMask_HonorsWindowsAceOrder()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sid = new SecurityIdentifier(SandboxRuntimeTempSlotTestSupport.SlotCapabilitySid);
        var allowFull = new SandboxRuntimeTempSlotNative.RawAce(
            0, false, sid, SandboxRuntimeTempSlotAcl.ManagementRights, false, false, false, false, false);
        var denyRoot = new SandboxRuntimeTempSlotNative.RawAce(
            1, true, sid, SandboxRuntimeTempSlotAcl.RootDeniedRights, false, false, false, false, false);

        // Allow 在前、Deny 在后：Windows AccessCheck 先命中 Allow，DELETE/WRITE_DAC/WRITE_OWNER 已被授予。
        var allowFirst = SandboxRuntimeTempSlotContract.EffectiveMask([allowFull, denyRoot], [sid]);
        Assert.That(allowFirst & SandboxRuntimeTempSlotAcl.RootDeniedRights,
                    Is.EqualTo(SandboxRuntimeTempSlotAcl.RootDeniedRights));

        // Deny 在前、Allow 在后：Deny 先锁定这些位，后续 Allow 无法覆盖。
        var denyFirst = SandboxRuntimeTempSlotContract.EffectiveMask([denyRoot, allowFull], [sid]);
        Assert.That(denyFirst & SandboxRuntimeTempSlotAcl.RootDeniedRights, Is.Zero);
    }

    [Test]
    public void WriteRestrictedTokenSemantics_AllowsOwnSlotAndIsolatesForeignAndReadOnlyWrites()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home    = new TestTempDir();
        var       slot    = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        var       account = new SecurityIdentifier(SandboxRuntimeTempSlotTestSupport.SandboxAccountSid);
        var       shared  = new SecurityIdentifier(SandboxRuntimeTempSlotTestSupport.SandboxSharedSid);
        var       ownCap  = new SecurityIdentifier(SandboxRuntimeTempSlotTestSupport.SlotCapabilitySid);
        var       foreign = new SecurityIdentifier(SandboxRuntimeTempSlotTestSupport.ForeignCapabilitySid);
        var       readCap = new SecurityIdentifier(SandboxRuntimeTempSlotTestSupport.ReadOnlyCapabilitySid);

        SandboxRuntimeTempSlotNative.RawSecurity security;
        using (var handle = SandboxRuntimeTempSlotNative.OpenDirectory(
                   slot.WritableDirectory,
                   SandboxRuntimeTempSlotNative.ReadControl | SandboxRuntimeTempSlotNative.FileReadAttributes |
                   SandboxRuntimeTempSlotNative.Synchronize,
                   SandboxRuntimeTempSlotNative.FileShareReadWrite, "slot writable probe"))
        {
            security = SandboxRuntimeTempSlotNative.ReadSecurity(handle, slot.WritableDirectory);
        }

        SecurityIdentifier[] firstRound = [account, shared];

        // 本槽位 WRITE_RESTRICTED 令牌（第一轮=[账户, 共享组]，第二轮=[账户, 本槽位 capability]）：可写、不可删根。
        var ownRoot = SandboxRuntimeTempSlotContract.EffectiveWriteRestrictedMask(
            security.Aces, firstRound, [account, ownCap]);
        Assert.That(ownRoot & SandboxRuntimeTempSlotAcl.SlotRootWriteRights,
                    Is.EqualTo(SandboxRuntimeTempSlotAcl.SlotRootWriteRights));
        Assert.That(ownRoot & SandboxRuntimeTempSlotAcl.RootDeniedRights, Is.Zero);
        Assert.That(SandboxRuntimeTempSlotContract.InheritedWriteRestrictedMask(
                        security.Aces, firstRound, [account, ownCap], true) &
                    SandboxRuntimeTempSlotAcl.SlotChildWriteRights,
                    Is.EqualTo(SandboxRuntimeTempSlotAcl.SlotChildWriteRights));
        Assert.That(SandboxRuntimeTempSlotContract.InheritedWriteRestrictedMask(
                        security.Aces, firstRound, [account, ownCap], false) &
                    SandboxRuntimeTempSlotAcl.SlotChildWriteRights,
                    Is.EqualTo(SandboxRuntimeTempSlotAcl.SlotChildWriteRights));

        // 同账户并发另一槽位令牌（第二轮=[账户, 另一槽位 capability]，且同账户为子项 Owner）：写位（含隐式 WRITE_DAC）全为 0。
        Assert.That(SandboxRuntimeTempSlotContract.EffectiveWriteRestrictedMask(
                        security.Aces, firstRound, [account, foreign]) & SandboxRuntimeTempSlotContract.WriteBits,
                    Is.Zero);
        Assert.That(SandboxRuntimeTempSlotContract.InheritedWriteRestrictedMask(
                        security.Aces, firstRound, [account, foreign], directory: true, ownerInBothRounds: true) &
                    SandboxRuntimeTempSlotContract.WriteBits,
                    Is.Zero);
        Assert.That(SandboxRuntimeTempSlotContract.InheritedWriteRestrictedMask(
                        security.Aces, firstRound, [account, foreign], directory: false, ownerInBothRounds: true) &
                    SandboxRuntimeTempSlotContract.WriteBits,
                    Is.Zero);

        // ReadOnly 策略令牌（第二轮=[账户, readonly capability]，含子项 Owner 语义）：写位全为 0。
        Assert.That(SandboxRuntimeTempSlotContract.EffectiveWriteRestrictedMask(
                        security.Aces, firstRound, [account, readCap]) & SandboxRuntimeTempSlotContract.WriteBits,
                    Is.Zero);
        Assert.That(SandboxRuntimeTempSlotContract.InheritedWriteRestrictedMask(
                        security.Aces, firstRound, [account, readCap], directory: true, ownerInBothRounds: true) &
                    SandboxRuntimeTempSlotContract.WriteBits,
                    Is.Zero);
        Assert.That(SandboxRuntimeTempSlotContract.InheritedWriteRestrictedMask(
                        security.Aces, firstRound, [account, readCap], directory: false, ownerInBothRounds: true) &
                    SandboxRuntimeTempSlotContract.WriteBits,
                    Is.Zero);
    }
}

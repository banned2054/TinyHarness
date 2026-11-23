using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;
using ProcessWorkspaceAclGrant = TinyHarness.Core.Services.Runtime.ProcessWorkspaceAclGrant;
using WorkspaceGrantOutcome = TinyHarness.Core.Services.Runtime.WorkspaceGrantOutcome;
using WorkspaceSandboxAclRepair = TinyHarness.Cli.Services.WorkspaceSandboxAclRepair;

namespace TinyHarness.Tests;

/// <summary>
///     <para>离线验证继承缺口、显式修复顺序与固定 SID 请求；不写系统 ACL 或启动 setup。</para>
///     <para>
///         Verifies inheritance gaps, explicit repair ordering and fixed SID requests without ACL writes or setup
///         processes.
///     </para>
/// </summary>
public class WorkspaceAclInheritanceTests
{
    [Test]
    public void InheritableWriteGrantMissingFromExistingChild_IsDetected()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var parent = new FileSystemAccessRule(sid, FileSystemRights.Modify,
                                              InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                                              PropagationFlags.None, AccessControlType.Allow);
        var unrelated = new FileSystemAccessRule(new SecurityIdentifier("S-1-5-21-1-2-3-1002"),
                                                 FileSystemRights.FullControl, AccessControlType.Allow);
        Assert.That(WorkspaceAclInheritance.HasMissingAllow([parent], [unrelated], true), Is.True);
        Assert.That(WorkspaceAclInheritance.HasMissingAllow([parent], [unrelated], false), Is.True);
        var inherited = new FileSystemAccessRule(sid, FileSystemRights.Modify,
                                                 InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                                                 PropagationFlags.None, AccessControlType.Allow);
        var deny = new FileSystemAccessRule(sid, FileSystemRights.Write, AccessControlType.Deny);
        Assert.That(WorkspaceAclInheritance.HasMissingAllow([parent], [inherited, deny], true), Is.False,
                    "Inheritance consistency does not claim effective write access or remove a deny.");
        Assert.That(WorkspaceAclInheritance.HasMissingAllow([parent], [deny], true), Is.True);
        var nonInheritable = new FileSystemAccessRule(sid, FileSystemRights.Modify, AccessControlType.Allow);
        Assert.That(WorkspaceAclInheritance.HasMissingAllow([parent], [nonInheritable], true), Is.True,
                    "An existing child's direct allow must not hide a missing grant to grandchildren.");
        Assert.That(WorkspaceAclInheritance.HasMissingAllow([parent], [nonInheritable], false), Is.False);
    }

    [Test]
    public void DirectoryOnlyCallerGrant_DoesNotRequireChildManagementRights()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sid      = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var rootOnly = new FileSystemAccessRule(sid, FileSystemRights.ChangePermissions, AccessControlType.Allow);
        Assert.That(WorkspaceAclInheritance.HasMissingAllow([rootOnly], [], true), Is.False);
        Assert.That(WorkspaceAclInheritance.HasMissingAllow([rootOnly], [], false), Is.False);
    }

    [Test]
    public void GenericParentMask_IsComparedWithExpandedChildPermissions()
    {
        if (!OperatingSystem.IsWindows()) return;
        var descriptor = new DirectorySecurity();
        descriptor.SetSecurityDescriptorSddlForm("D:(A;OICIIO;0xe0010000;;;AU)");
        var parent = descriptor.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                               .ToArray();
        var sid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var child = new FileSystemAccessRule(sid, FileSystemRights.Modify | FileSystemRights.Synchronize,
                                             AccessControlType.Allow);
        Assert.That(WorkspaceAclInheritance.HasMissingAllow(parent, [child], false), Is.False);
    }

    [Test]
    public void TrustedSidMapping_RejectsChangedGroupCapabilityOrMissingEntry()
    {
        var context = new WorkspaceSandboxAclContext("home", "sandbox-group", "workspace-capability");
        Assert.DoesNotThrow(() => context.ValidateMapping("sandbox-group", "workspace-capability"));
        Assert.Throws<InvalidDataException>(() => context.ValidateMapping("different-group", "workspace-capability"));
        Assert.Throws<InvalidDataException>(() => context.ValidateMapping("sandbox-group", "different-capability"));
        Assert.Throws<InvalidDataException>(() => context.ValidateMapping("sandbox-group", null));
    }

    [Test]
    public async Task UnreadySandbox_IsRejectedBeforeCreatingWorkspaceSidMapping()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var       before    = await File.ReadAllTextAsync(home.Components.CapabilitySidPath);
        await File.WriteAllTextAsync(home.Components.MarkerPath, "{\"version\":0}");
        var settings = new WindowsSandboxSettings
        {
            SetupExecutablePath  = home.Components.SetupExecutablePath,
            RunnerExecutablePath = home.Components.RunnerExecutablePath,
            SandboxHome          = home.Components.SandboxHome
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                WorkspaceSandboxAclContext.CaptureAsync(settings,
                                                                         WorkspaceDirectoryIdentity
                                                                            .Capture(workspace.Root),
                                                                         CancellationToken.None));
        Assert.That(await File.ReadAllTextAsync(home.Components.CapabilitySidPath), Is.EqualTo(before));
    }

    [Test]
    public void ContainerOnlyAndNoPropagateFlags_AreRespected()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var container = new FileSystemAccessRule(sid, FileSystemRights.Modify,
                                                 InheritanceFlags.ContainerInherit, PropagationFlags.NoPropagateInherit,
                                                 AccessControlType.Allow);
        Assert.That(WorkspaceAclInheritance.HasMissingAllow([container], [], true), Is.True);
        Assert.That(WorkspaceAclInheritance.HasMissingAllow([container], [], false), Is.False);
        var objects = new FileSystemAccessRule(sid, FileSystemRights.Modify,
                                               InheritanceFlags.ObjectInherit, PropagationFlags.NoPropagateInherit,
                                               AccessControlType.Allow);
        Assert.That(WorkspaceAclInheritance.HasMissingAllow([objects], [], true), Is.False);
        Assert.That(WorkspaceAclInheritance.HasMissingAllow([objects], [], false), Is.True);
    }

    [Test]
    public void PreparedRootAce_RejectsInheritOnlyNoPropagateAndInsufficientMask()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var full = new FileSystemAccessRule(sid, (FileSystemRights)0x1301BF,
                                            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                                            PropagationFlags.None, AccessControlType.Allow);
        Assert.That(WorkspaceSandboxAclContext.HasRequiredWriteFlags(full), Is.True);
        Assert.That(WorkspaceSandboxAclContext.HasRequiredWriteFlags(new FileSystemAccessRule(
                                                                          sid, FileSystemRights.FullControl,
                                                                          InheritanceFlags.ContainerInherit |
                                                                          InheritanceFlags.ObjectInherit,
                                                                          PropagationFlags.None,
                                                                          AccessControlType.Allow)),
                    Is.True);
        var inheritOnly = new FileSystemAccessRule(sid, (FileSystemRights)0x1301BF,
                                                   InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                                                   PropagationFlags.InheritOnly, AccessControlType.Allow);
        Assert.That(WorkspaceSandboxAclContext.HasRequiredWriteFlags(inheritOnly), Is.False,
                    "Inherit-only ACE 不能作为根授权就绪证据。");
        var noPropagate = new FileSystemAccessRule(sid, (FileSystemRights)0x1301BF,
                                                   InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                                                   PropagationFlags.NoPropagateInherit, AccessControlType.Allow);
        Assert.That(WorkspaceSandboxAclContext.HasRequiredWriteFlags(noPropagate), Is.False,
                    "停止传播的 ACE 不能作为根授权就绪证据。");
        var containerOnly = new FileSystemAccessRule(sid, (FileSystemRights)0x1301BF, InheritanceFlags.ContainerInherit,
                                                     PropagationFlags.None, AccessControlType.Allow);
        Assert.That(WorkspaceSandboxAclContext.HasRequiredWriteFlags(containerOnly), Is.False);
        var insufficient = new FileSystemAccessRule(sid, (FileSystemRights)0x1301BE,
                                                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                                                    PropagationFlags.None, AccessControlType.Allow);
        Assert.That(WorkspaceSandboxAclContext.HasRequiredWriteFlags(insufficient), Is.False,
                    "缺少任一必需位的掩码不能视为完整写授权。");
    }

    [Test]
    public void GrantExchangeFailure_RequiresConfirmedExitBeforeFailedOutcome()
    {
        Assert.That(ProcessWorkspaceAclGrant.ClassifyFailedExchange(true),
                    Is.EqualTo(WorkspaceGrantOutcome.Failed));
        Assert.That(ProcessWorkspaceAclGrant.ClassifyFailedExchange(false),
                    Is.EqualTo(WorkspaceGrantOutcome.DidNotFinish),
                    "子进程已启动且无法确认退出时，结果必须保持未知，禁止自动重试。");
    }

    [Test]
    public async Task NativeInspection_IsReadOnlyAndHonorsCancellation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TestTempDir();
        directory.WriteFile("src/obj/cache.txt", "preserve");
        var identity = WorkspaceDirectoryIdentity.Capture(directory.Root);
        var before = new DirectoryInfo(directory.Root).GetAccessControl()
                                                      .GetSecurityDescriptorSddlForm(AccessControlSections.All);
        Assert.That(await WorkspaceAclInheritance.FindMissingAsync(identity, CancellationToken.None), Is.Null);
        Assert.That(new DirectoryInfo(directory.Root).GetAccessControl()
                                                     .GetSecurityDescriptorSddlForm(AccessControlSections.All),
                    Is.EqualTo(before));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.CatchAsync<OperationCanceledException>(() =>
                                                                WorkspaceAclInheritance
                                                                   .FindMissingAsync(identity, cancelled.Token));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(directory.Root, "src/obj/cache.txt")),
                    Is.EqualTo("preserve"));
    }

    [Test]
    public void RepairRequest_ContainsOnlyTrustedSandboxSidContext()
    {
        using var directory = new TestTempDir();
        var       identity  = WorkspaceDirectoryIdentity.Capture(directory.Root);
        var       context   = new WorkspaceSandboxAclContext("home", "group-sid", "workspace-capability-sid");
        var       request   = new WorkspaceAclRequest(identity, "original-sid", 1, 2, [], context);
        var       json      = JsonSerializer.Serialize(request, WorkspaceAclJsonContext.Default.WorkspaceAclRequest);
        var       roundtrip = JsonSerializer.Deserialize(json, WorkspaceAclJsonContext.Default.WorkspaceAclRequest)!;
        Assert.That(roundtrip.Sandbox, Is.EqualTo(context));
    }

    [Test]
    public async Task RepairAlwaysPropagatesEvenWhenRootGrantsAlreadyExist()
    {
        var steps = new List<string>();
        await WorkspaceSandboxAclRepair.RunAsync(() => steps.Add("grant-checked"), () => steps.Add("propagate"),
                                                 _ =>
                                                 {
                                                     steps.Add("inspect");
                                                     return Task.FromResult<string?>(null);
                                                 }, CancellationToken.None);
        Assert.That(steps, Is.EqualTo(["grant-checked", "propagate", "inspect"]));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task RepairFailureNeverRetriesOrReportsReadiness(bool grantFails)
    {
        var grants       = 0;
        var propagations = 0;
        var error = await Assert.CatchAsync(() => WorkspaceSandboxAclRepair.RunAsync(
                                                 () =>
                                                 {
                                                     grants++;
                                                     if (grantFails) throw new IOException("grant failed");
                                                 },
                                                 () => propagations++, _ => Task.FromResult<string?>("src/obj"),
                                                 CancellationToken.None));
        Assert.That(error, Is.Not.Null);
        Assert.That(grants, Is.EqualTo(1));
        Assert.That(propagations, Is.EqualTo(grantFails ? 0 : 1));
        if (!grantFails) Assert.That(error!.Message, Does.Contain("src/obj"));
    }

    [Test]
    public async Task RepairCancellationDoesNotStartAclActions()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.CatchAsync<OperationCanceledException>(() =>
                                                                WorkspaceSandboxAclRepair
                                                                   .RunAsync(() => Assert.Fail("must not grant"),
                                                                             () => Assert.Fail("must not propagate"),
                                                                             _ => throw new
                                                                                 InvalidOperationException("must not inspect"),
                                                                             cancelled.Token));
    }

    [Test]
    public async Task CaptureAsync_ResolvesCapabilitySidInsideDenyReadRefreshGateWindow()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            _ = (SecurityIdentifier)new NTAccount(Environment.MachineName, "TinyHarnessUsers")
                                   .Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            Assert.Ignore("The TinyHarnessUsers group does not exist on this machine.");
            return;
        }

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var       key       = SandboxCapabilitySidStore.CanonicalRootKey(workspace.Root);
        var       trace     = new List<string>();
        var       gate      = new ScriptedDenyReadGate(trace);
        var capabilityPresentAtRelease = false;
        // 探测句柄：门归还时读表，证明新键已写表——解析发生在 acquire/release 窗口之内。
        gate.Script = () => Task.FromResult<ISandboxDenyReadRefreshGateHandle?>(new ProbingDenyGateHandle(() =>
        {
            capabilityPresentAtRelease = ReadWorkspaceCapabilitySid(home.Components.SandboxHome, key) is not null;
            trace.Add("deny-gate-release");
        }));

        var settings = new WindowsSandboxSettings
        {
            SetupExecutablePath  = home.Components.SetupExecutablePath,
            RunnerExecutablePath = home.Components.RunnerExecutablePath,
            SandboxHome          = home.Components.SandboxHome
        };
        var context = await WorkspaceSandboxAclContext.CaptureAsync(
                          settings, WorkspaceDirectoryIdentity.Capture(workspace.Root), CancellationToken.None, gate);

        Assert.Multiple(() =>
        {
            Assert.That(trace, Is.EqualTo(["deny-gate-acquire", "deny-gate-release"]));
            Assert.That(capabilityPresentAtRelease, Is.True);
            Assert.That(ReadWorkspaceCapabilitySid(home.Components.SandboxHome, key),
                        Is.EqualTo(context.WorkspaceCapabilitySid));
        });
    }

    [Test]
    public async Task CaptureAsync_DenyReadRefreshGateTimeout_FailsClosedWithoutResolvingSidTable()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var       before    = File.ReadAllText(home.Components.CapabilitySidPath);
        var gate = new ScriptedDenyReadGate { Script = () => Task.FromResult<ISandboxDenyReadRefreshGateHandle?>(null) };

        var settings = new WindowsSandboxSettings
        {
            SetupExecutablePath  = home.Components.SetupExecutablePath,
            RunnerExecutablePath = home.Components.RunnerExecutablePath,
            SandboxHome          = home.Components.SandboxHome
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkspaceSandboxAclContext.CaptureAsync(settings, WorkspaceDirectoryIdentity.Capture(workspace.Root),
                                                    CancellationToken.None, gate));

        Assert.Multiple(() =>
        {
            TestAssert.Contains("deny-read refresh gate", error!.Message, StringComparison.Ordinal);
            TestAssert.Contains(home.Components.SandboxHome, error.Message, StringComparison.Ordinal);
            Assert.That(error.InnerException, Is.InstanceOf<TimeoutException>());
            // 门超时不解析：表内容原样保留，get-or-create 未发生，ACL 无任何变更。
            Assert.That(File.ReadAllText(home.Components.CapabilitySidPath), Is.EqualTo(before));
            Assert.That(gate.Trace, Is.EqualTo(["deny-gate-acquire"]));
        });
    }

    /// <summary>探测式门句柄：Dispose（即门归还）时执行回调并记录释放事件。 A probing gate handle: runs the probe and records the release event when Dispose (the gate return) happens.</summary>
    private sealed class ProbingDenyGateHandle(Action onRelease) : ISandboxDenyReadRefreshGateHandle
    {
        public void Dispose() => onRelease();

        public void ReleaseWhenConfirmed(Task<bool> helperExitConfirmation) =>
            throw new NotSupportedException("The repair path never transfers gate ownership.");
    }

    // ---------------------------------------------------------------- 门外读窗口回归（确定性交错）

    /// <summary>
    ///     <para>
    ///         门外读窗口交错的共享助手：A 持同一隔离 home 的真实刷新门并控制 cap_sid 写入窗口——
    ///         先写入撕裂/陈旧中间态，再放行 B 从被测入口进入。交给 B 的门是带进入信号的门装饰器，
    ///         助手等到 B 实际进入门等待的确定性信号后才做短负等待，确认 B 撞上 A 持有的门而未
    ///         完成；A 写回完整表并释放门后 B 才完成。修复前 B 在门外立即读表：撕裂表读出
    ///         JsonException/就绪误报，陈旧表读出错误映射结果——B 在进入门等待前就已结束，助手
    ///         在收尾后如实上报其真实终态。
    ///     </para>
    ///     <para>
    ///         Shared helper for the out-of-gate read window regressions: A holds the real refresh gate
    ///         for one isolated home and controls the cap_sid write window — the interim torn/stale
    ///         table is written first, then B enters through the entry under test. The gate handed to B
    ///         is the entry-signaling gate decorator, so the helper waits for the deterministic signal
    ///         that B has actually entered the gate wait before its short negative wait confirming B
    ///         is blocked on the gate A holds and has not completed; B completes only after A writes
    ///         the complete table and releases the gate. Pre-fix B reads the table outside the gate
    ///         immediately: torn JSON yields a JsonException or a readiness misfire, and a stale table
    ///         yields a wrong mapping result — B finishes before entering the gate wait, and the
    ///         helper reports its real outcome after draining.
    ///     </para>
    /// </summary>
    private static async Task RunBehindCapSidWriteWindowAsync(
        string sandboxHome, Action writeInterim, Action writeComplete,
        Func<GateEntrySignalingDenyReadRefreshGate, Task> runB, string blockedBecause)
    {
        var gate = new GlobalDenyReadRefreshGate();
        var held = await gate.TryAcquireAsync(sandboxHome, TimeSpan.FromSeconds(5), CancellationToken.None)
                             .WaitAsync(TimeSpan.FromSeconds(10));
        TestAssert.NotNull(held);
        // 装饰器只交给 B：A 自己持门走真实门实例，进入信号只会由 B 置位。
        var     probe   = new GateEntrySignalingDenyReadRefreshGate(gate);
        string? failure = null;
        var     failed  = false;
        Task    taskB   = Task.CompletedTask;
        try
        {
            writeInterim();
            taskB   = runB(probe);
            failure = await probe.ObserveGateWaitAsync(taskB).ConfigureAwait(false);
        }
        catch (Exception)
        {
            failed = true;
            throw;
        }
        finally
        {
            // 先写回完整表再释放门：B 随后在门内读到完整表；收尾等待只观察，不覆盖原始失败。
            writeComplete();
            held?.Dispose();
            failure = await probe.DrainAfterReleaseAsync(taskB, failure, failure is not null || failed)
                                 .ConfigureAwait(false);
        }

        if (failure is not null) Assert.Fail(blockedBecause + Environment.NewLine + failure);
    }

    /// <summary>把 cap_sid 表序列化为完整 JSON；workspace_by_cwd 可注入指定键的映射或保持为空。 Serializes the cap_sid table as complete JSON; workspace_by_cwd takes the optional mapping or stays empty.</summary>
    private static string SerializeCapSidTable(Dictionary<string, string>? workspaceByCwd = null) =>
        JsonSerializer.Serialize(new SandboxCapabilitySids
        {
            Workspace          = "S-1-5-21-11-11-11-11",
            ReadOnly           = "S-1-5-21-22-22-22-22",
            WorkspaceByCwd     = workspaceByCwd ?? [],
            WritableRootByPath = []
        }, WindowsSandboxJsonContext.Default.SandboxCapabilitySids);

    /// <summary>指向隔离测试 home 的可信沙箱设置。 Trusted sandbox settings pointing at the isolated test home.</summary>
    private static WindowsSandboxSettings SettingsFor(SandboxTestHome home) => new()
    {
        SetupExecutablePath  = home.Components.SetupExecutablePath,
        RunnerExecutablePath = home.Components.RunnerExecutablePath,
        SandboxHome          = home.Components.SandboxHome
    };

    [Test]
    public async Task CaptureAsync_WaitsForGateDuringConcurrentCapSidWriteWindow()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            _ = (SecurityIdentifier)new NTAccount(Environment.MachineName, "TinyHarnessUsers")
                                   .Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            Assert.Ignore("The TinyHarnessUsers group does not exist on this machine.");
            return;
        }

        using var home       = new SandboxTestHome();
        using var workspace  = new TestTempDir();
        var       capSidPath = home.Components.CapabilitySidPath;
        var       key        = SandboxCapabilitySidStore.CanonicalRootKey(workspace.Root);
        // 修复前 B 在门外立即读到撕裂表 → InspectAsync 判未就绪 → 误报 "Directory repair requires
        // a ready TinyHarness sandbox"；修复后 B 门内等待，读到 A 写回的完整表并解析出该表映射的 SID。
        const string mappedSid = "S-1-5-21-77-77-77-77";
        var tornTable        = """{"workspace":"S-1-5-21-11""";
        var completeTable    = SerializeCapSidTable(new Dictionary<string, string> { [key] = mappedSid });
        WorkspaceSandboxAclContext? captured = null;

        await RunBehindCapSidWriteWindowAsync(home.Components.SandboxHome,
                                              () => File.WriteAllText(capSidPath, tornTable),
                                              () => File.WriteAllText(capSidPath, completeTable),
                                              async gateProbe =>
                                                  captured = await WorkspaceSandboxAclContext.CaptureAsync(
                                                      SettingsFor(home), WorkspaceDirectoryIdentity.Capture(workspace.Root),
                                                      CancellationToken.None, gateProbe).ConfigureAwait(false),
                                              "CaptureAsync must wait for the deny-read refresh gate instead of reading " +
                                              "the torn cap_sid outside the gate (pre-fix: 'Directory repair requires a " +
                                              "ready TinyHarness sandbox').");

        // B 读到 A 写回后的完整表：get-or-create 命中 A 的映射，不再生成新 SID。
        Assert.Multiple(() =>
        {
            Assert.That(captured, Is.Not.Null);
            Assert.That(captured!.WorkspaceCapabilitySid, Is.EqualTo(mappedSid));
            Assert.That(ReadWorkspaceCapabilitySid(home.Components.SandboxHome, key), Is.EqualTo(mappedSid));
        });
    }

    [Test]
    public async Task ValidateAsync_WaitsForGateAndReadsCompletedTable()
    {
        if (!OperatingSystem.IsWindows()) return;
        SecurityIdentifier group;
        try
        {
            group = (SecurityIdentifier)new NTAccount(Environment.MachineName, "TinyHarnessUsers")
                                           .Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            Assert.Ignore("The TinyHarnessUsers group does not exist on this machine.");
            return;
        }

        using var home       = new SandboxTestHome();
        using var workspace  = new TestTempDir();
        var       capSidPath = home.Components.CapabilitySidPath;
        var       key        = SandboxCapabilitySidStore.CanonicalRootKey(workspace.Root);
        // 修复前 B 在门外立即读到陈旧表 → 误报 "Sandbox SID mapping changed after directory consent."；
        // 修复后 B 门内等待，读到 A 写回的完整表，映射核对通过。
        const string mappedSid = "S-1-5-21-77-77-77-77";
        const string staleSid  = "S-1-5-21-99-99-99-99";
        var staleTable     = SerializeCapSidTable(new Dictionary<string, string> { [key] = staleSid });
        var completeTable  = SerializeCapSidTable(new Dictionary<string, string> { [key] = mappedSid });
        var identity       = WorkspaceDirectoryIdentity.Capture(workspace.Root);
        var context        = new WorkspaceSandboxAclContext(home.Components.SandboxHome, group.Value, mappedSid);

        await RunBehindCapSidWriteWindowAsync(home.Components.SandboxHome,
                                              () => File.WriteAllText(capSidPath, staleTable),
                                              () => File.WriteAllText(capSidPath, completeTable),
                                              gateProbe => context.ValidateAsync(identity, CancellationToken.None, gateProbe),
                                              "ValidateAsync must wait for the deny-read refresh gate instead of reading " +
                                              "the stale table outside the gate (pre-fix: 'Sandbox SID mapping changed " +
                                              "after directory consent.').");

        Assert.That(File.ReadAllText(capSidPath), Is.EqualTo(completeTable));
    }

    [Test]
    public async Task HasPreparedRootAsync_WaitsForGateDuringConcurrentCapSidWriteWindow()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home       = new SandboxTestHome();
        using var workspace  = new TestTempDir();
        var       capSidPath = home.Components.CapabilitySidPath;
        // 修复前 B 在门外立即读到撕裂表 → JsonException 直接逃逸；修复后 B 门内等待，读到 A 写回的
        // 完整表。完整表不含该工作区键：表核对在组映射前返回 false，无需 TinyHarnessUsers 参与。
        var tornTable     = """{"workspace":"S-1-5-21-11""";
        var completeTable = SerializeCapSidTable();
        var prepared      = true;

        await RunBehindCapSidWriteWindowAsync(home.Components.SandboxHome,
                                              () => File.WriteAllText(capSidPath, tornTable),
                                              () => File.WriteAllText(capSidPath, completeTable),
                                              async gateProbe =>
                                                  prepared = await WorkspaceSandboxAclContext.HasPreparedRootAsync(
                                                      SettingsFor(home), WorkspaceDirectoryIdentity.Capture(workspace.Root),
                                                      CancellationToken.None, gateProbe).ConfigureAwait(false),
                                              "HasPreparedRootAsync must wait for the deny-read refresh gate instead of " +
                                              "reading the torn cap_sid outside the gate (pre-fix: JsonException escapes).");

        Assert.That(prepared, Is.False);
        Assert.That(File.ReadAllText(capSidPath), Is.EqualTo(completeTable));
    }

    [Test]
    public async Task ValidateAsync_DenyReadRefreshGateTimeout_FailsClosedWithoutReadingTable()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var       before    = File.ReadAllText(home.Components.CapabilitySidPath);
        var       gate      = new ScriptedDenyReadGate { Script = () => Task.FromResult<ISandboxDenyReadRefreshGateHandle?>(null) };
        var identity = WorkspaceDirectoryIdentity.Capture(workspace.Root);
        var context  = new WorkspaceSandboxAclContext(home.Components.SandboxHome, "S-1-5-21-1-2-3-1001",
                                                      "S-1-5-21-77-77-77-77");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.ValidateAsync(identity, CancellationToken.None, gate));

        // 门等待超时 fail-closed：拒绝核对（不读表、不误用门外读结果），表内容原样保留。
        Assert.Multiple(() =>
        {
            TestAssert.Contains("deny-read refresh gate", error!.Message, StringComparison.Ordinal);
            TestAssert.Contains(home.Components.SandboxHome, error.Message, StringComparison.Ordinal);
            Assert.That(error.InnerException, Is.InstanceOf<TimeoutException>());
            Assert.That(gate.AcquireCalls, Is.EqualTo(1));
            Assert.That(File.ReadAllText(home.Components.CapabilitySidPath), Is.EqualTo(before));
        });
    }

    [Test]
    public async Task HasPreparedRootAsync_DenyReadRefreshGateTimeout_FailsClosed()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var       before    = File.ReadAllText(home.Components.CapabilitySidPath);
        var       gate      = new ScriptedDenyReadGate { Script = () => Task.FromResult<ISandboxDenyReadRefreshGateHandle?>(null) };
        var identity = WorkspaceDirectoryIdentity.Capture(workspace.Root);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkspaceSandboxAclContext.HasPreparedRootAsync(SettingsFor(home), identity, CancellationToken.None, gate));

        // 就绪判断不以门外读为依据：门等待超时抛出并保留表内容。
        Assert.Multiple(() =>
        {
            TestAssert.Contains("deny-read refresh gate", error!.Message, StringComparison.Ordinal);
            TestAssert.Contains(home.Components.SandboxHome, error.Message, StringComparison.Ordinal);
            Assert.That(error.InnerException, Is.InstanceOf<TimeoutException>());
            Assert.That(gate.AcquireCalls, Is.EqualTo(1));
            Assert.That(File.ReadAllText(home.Components.CapabilitySidPath), Is.EqualTo(before));
        });
    }

    [Test]
    public async Task ValidateAsync_CancelledDuringGateWait_PropagatesCancellation()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var       identity  = WorkspaceDirectoryIdentity.Capture(workspace.Root);
        var       context   = new WorkspaceSandboxAclContext(home.Components.SandboxHome, "S-1-5-21-1-2-3-1001",
                                                             "S-1-5-21-77-77-77-77");
        // A 持真实门；B 的等待被取消 → OCE 从门等待传播，不转为其他结果。
        var held = await new GlobalDenyReadRefreshGate()
                        .TryAcquireAsync(home.Components.SandboxHome, TimeSpan.FromSeconds(5), CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(10));
        TestAssert.NotNull(held);
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.CatchAsync<OperationCanceledException>(() =>
                context.ValidateAsync(identity, cancellation.Token, new GlobalDenyReadRefreshGate()));
        }
        finally
        {
            held?.Dispose();
        }
    }

    [Test]
    public async Task ValidateAsync_MissingTable_DoesNotTakeGate()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        File.Delete(home.Components.CapabilitySidPath);
        var       gate     = new ScriptedDenyReadGate();
        var       identity = WorkspaceDirectoryIdentity.Capture(workspace.Root);
        var       context  = new WorkspaceSandboxAclContext(home.Components.SandboxHome, "S-1-5-21-1-2-3-1001",
                                                            "S-1-5-21-77-77-77-77");

        // 表缺失走现有读缺失分支：FileNotFoundException 照旧传播，门未被获取（AcquireCalls=0）。
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            context.ValidateAsync(identity, CancellationToken.None, gate));
        Assert.That(gate.AcquireCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task HasPreparedRootAsync_MissingTable_ReturnsFalseWithoutTakingGate()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        File.Delete(home.Components.CapabilitySidPath);
        var gate     = new ScriptedDenyReadGate();
        var identity = WorkspaceDirectoryIdentity.Capture(workspace.Root);

        // 表缺失直接判未准备且不拿门：不存在门内写者可言。
        var prepared = await WorkspaceSandboxAclContext.HasPreparedRootAsync(SettingsFor(home), identity,
                                                                             CancellationToken.None, gate);
        Assert.Multiple(() =>
        {
            Assert.That(prepared, Is.False);
            Assert.That(gate.AcquireCalls, Is.EqualTo(0));
        });
    }

    /// <summary>读取 cap_sid 表 workspace_by_cwd 中指定键的映射；缺失返回 null。 Reads the workspace_by_cwd mapping for a key from the cap_sid table; null when missing.</summary>
    private static string? ReadWorkspaceCapabilitySid(string sandboxHome, string key)
    {
        var path = Path.Combine(sandboxHome, "cap_sid");
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("workspace_by_cwd").TryGetProperty(key, out var sid)
                   ? sid.GetString()
                   : null;
    }
}

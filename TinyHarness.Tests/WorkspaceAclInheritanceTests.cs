using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.Runtime;

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
}

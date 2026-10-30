using System.Security.AccessControl;
using System.Security.Principal;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Tests;

/// <summary>
///     <para>离线验证结构化 ACL 问题模型与只读检查入口；不写系统 ACL、不提权、不运行 setup helper。</para>
///     <para>
///         Verifies the structured ACL problem model and the read-only inspection entry offline;
///         never writes real ACLs, elevates, or runs the setup helper.
///     </para>
/// </summary>
public class WorkspaceAclProblemTests
{
    [Test]
    public void MissingAces_ReportRequiredEffectiveAndPropagationMasks()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var parent = new FileSystemAccessRule(sid, FileSystemRights.Modify,
                                              InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                                              PropagationFlags.None, AccessControlType.Allow);
        var unrelated = new FileSystemAccessRule(new SecurityIdentifier("S-1-5-21-1-2-3-1002"),
                                                 FileSystemRights.FullControl, AccessControlType.Allow);
        var missing = WorkspaceAclInheritance.MissingAces([parent], [unrelated], true);
        Assert.That(missing, Has.Count.EqualTo(1));
        Assert.That(missing[0].Sid, Is.EqualTo(sid.Value));
        Assert.That(missing[0].RequiredMask, Is.EqualTo(0x1301BF));
        Assert.That(missing[0].EffectiveMask, Is.EqualTo(0));
        Assert.That(missing[0].ContainerPropagationMask, Is.EqualTo(0));
        Assert.That(missing[0].ObjectPropagationMask, Is.EqualTo(0));
        // 子项只有对象侧继承时，容器传播缺口必须单独出现在结果中。
        var objectsOnly = new FileSystemAccessRule(sid, FileSystemRights.Modify,
                                                   InheritanceFlags.ObjectInherit, PropagationFlags.None,
                                                   AccessControlType.Allow);
        var partial = WorkspaceAclInheritance.MissingAces([parent], [objectsOnly], true);
        Assert.That(partial, Has.Count.EqualTo(1));
        Assert.That(partial[0].EffectiveMask, Is.EqualTo(0x1301BF));
        Assert.That(partial[0].ContainerPropagationMask, Is.EqualTo(0));
        Assert.That(partial[0].ObjectPropagationMask, Is.EqualTo(0x1301BF));
        // 不可继承的父 ACE 不产生缺口条目。
        Assert.That(WorkspaceAclInheritance.MissingAces([unrelated], [unrelated], true), Is.Empty);
    }

    [Test]
    public void Problem_ExpressesMissingAccessAndManagementDenialTogether()
    {
        var entry = new WorkspaceAclMissingAce("S-1-5-21-1-2-3-1001", 0x1301BF, 0, 0, 0x1301BF);
        var problem = new WorkspaceAclProblem(@"C:\ws", @"C:\ws\src",
                                              WorkspaceAclProblemCategory.MissingInheritedAccess, [entry],
                                              false, null);
        Assert.That(problem.Category, Is.EqualTo(WorkspaceAclProblemCategory.MissingInheritedAccess));
        Assert.That(problem.MissingAces, Has.Count.EqualTo(1));
        Assert.That(problem.CanManageAcl, Is.False);
        Assert.That(problem.Win32Error, Is.Null);
        Assert.That(problem.Detail, Is.Null);
    }

    [Test]
    public void PreparationException_MessageReportsFactsWithoutPrescribingTrustCommand()
    {
        var entry = new WorkspaceAclMissingAce("S-1-5-21-1-2-3-1001", 0x1301BF, 0, 0, 0x1301BF);
        var problem = new WorkspaceAclProblem(@"C:\ws", @"C:\ws\src",
                                              WorkspaceAclProblemCategory.MissingInheritedAccess, [entry],
                                              false, null);
        var exception = new WorkspaceAclPreparationException(problem);
        Assert.That(exception.Problem, Is.SameAs(problem));
        Assert.That(exception.Message, Does.Contain(@"C:\ws\src"));
        Assert.That(exception.Message, Does.Contain(@"C:\ws"));
        Assert.That(exception.Message, Does.Contain("required=0x001301BF"));
        Assert.That(exception.Message, Does.Contain("No command was started"));
        Assert.That(exception.Message, Does.Contain("cannot open an ACL-management handle"));
        Assert.That(exception.Message, Does.Not.Contain("/trust"));

        var failed = new WorkspaceAclPreparationException(new WorkspaceAclProblem(
                                                               @"C:\ws", @"C:\ws",
                                                               WorkspaceAclProblemCategory.InspectionFailed,
                                                               [], null, 5, "DirectoryNotFoundException: gone"));
        Assert.That(failed.Message, Does.Contain("(Win32 error 5)"));
        Assert.That(failed.Message, Does.Contain("DirectoryNotFoundException"));
    }

    [Test]
    public async Task ConsistentTempTree_InspectsReadOnlyWithoutAclChanges()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TestTempDir();
        directory.WriteFile("src/obj/cache.txt", "preserve");
        var identity = WorkspaceDirectoryIdentity.Capture(directory.Root);
        var before = new DirectoryInfo(directory.Root).GetAccessControl()
                                                      .GetSecurityDescriptorSddlForm(AccessControlSections.All);
        Assert.That(await WorkspaceAclInheritance.InspectAsync(identity, CancellationToken.None), Is.Null);
        Assert.That(new DirectoryInfo(directory.Root).GetAccessControl()
                                                     .GetSecurityDescriptorSddlForm(AccessControlSections.All),
                    Is.EqualTo(before));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(directory.Root, "src/obj/cache.txt")),
                    Is.EqualTo("preserve"));
    }

    [Test]
    public async Task CancelledInspection_PropagatesCancellationInsteadOfProblem()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TestTempDir();
        var       identity  = WorkspaceDirectoryIdentity.Capture(directory.Root);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.CatchAsync<OperationCanceledException>(() =>
                                                                WorkspaceAclInheritance
                                                                   .InspectAsync(identity, cancelled.Token));
    }

    [Test]
    public async Task VanishedWorkspace_ReturnsStructuredInspectionFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TestTempDir();
        var       identity  = WorkspaceDirectoryIdentity.Capture(directory.Root);
        Directory.Delete(directory.Root, true);
        var problem = await WorkspaceAclInheritance.InspectAsync(identity, CancellationToken.None);
        Assert.That(problem, Is.Not.Null);
        Assert.That(problem!.Category, Is.EqualTo(WorkspaceAclProblemCategory.InspectionFailed));
        Assert.That(problem.Path, Is.EqualTo(identity.Path));
        Assert.That(problem.WriteRoot, Is.EqualTo(identity.Path));
        Assert.That(problem.Detail, Does.Contain("DirectoryNotFoundException"));
    }

    [Test]
    public async Task ValidateAsync_SkipsMissingRootsAndPassesConsistentTreeWithoutAclChanges()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        workspace.WriteFile("src/main.cs", "content");
        var payload = new SandboxSetupPayload
        {
            OfflineUsername         = "TinyHarnessOffline",
            OnlineUsername          = "TinyHarnessOnline",
            SandboxHome             = home.Root,
            CommandWorkingDirectory = workspace.Root,
            ReadRoots               = [workspace.Root],
            WriteRoots              = [Path.Combine(workspace.Root, "missing-root"), workspace.Root],
            RealUser                = "tester"
        };
        var before = new DirectoryInfo(workspace.Root).GetAccessControl()
                                                      .GetSecurityDescriptorSddlForm(AccessControlSections.All);
        await Assert.DoesNotThrowAsync(() => WorkspaceAclInheritance.ValidateAsync(payload, CancellationToken.None));
        Assert.That(new DirectoryInfo(workspace.Root).GetAccessControl()
                                                     .GetSecurityDescriptorSddlForm(AccessControlSections.All),
                    Is.EqualTo(before));
    }
}

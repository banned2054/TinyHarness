using NUnit.Framework;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Tests;

public sealed class WorkspaceGrantFailureTests
{
    [TestCase("protected-paths")]
    [TestCase("caller-identity")]
    [TestCase("repair-tree")]
    public async Task HelperProtocol_ReturnsFailureWithoutChangingAcl(string stage)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Windows helper protocol.");
            return;
        }

        using var workspace = new TestTempDir();
        using var caller    = Process.GetCurrentProcess();
        using var account   = WindowsIdentity.GetCurrent();
        using var channel   = new WorkspaceGrantChannel(account.User!.Value);
        using var timeout   = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var       identity  = WorkspaceDirectoryIdentity.Capture(workspace.Root);
        if (stage == "repair-tree")
        {
            var original = workspace.WriteFile("original.txt", "preserve");
            Assert.That(CreateHardLink(Path.Combine(workspace.Root, "linked.txt"), original, IntPtr.Zero), Is.True);
        }

        var before = new DirectoryInfo(workspace.Root).GetAccessControl()
                                                      .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var request = new WorkspaceAclRequest(identity, account.User.Value, caller.Id,
                                              caller.StartTime.ToUniversalTime().Ticks +
                                              (stage == "caller-identity" ? 1 : 0),
                                              stage == "protected-paths" ? [workspace.Root] : []);
        var                  helper = ProcessWorkspaceAclGrant.ExecuteHelperAsync(channel.Name);
        WorkspaceAclResponse response;
        try
        {
            response = await channel.SendAsync(caller.Id, request, timeout.Token);
        }
        catch
        {
            await helper.WaitAsync(timeout.Token);
            throw;
        }

        Assert.That(await helper.WaitAsync(timeout.Token), Is.EqualTo(1));
        Assert.That(response.Succeeded, Is.False);
        Assert.That(response.Error, Does.StartWith(stage + ":"));
        Assert.That(new DirectoryInfo(workspace.Root).GetAccessControl()
                                                     .GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                    Is.EqualTo(before));
    }

    [Test]
    public async Task HelperExit_AllowsBufferedResponseAndBoundsMissingResponse()
    {
        var pending =
            new TaskCompletionSource<WorkspaceAclResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receive =
            ProcessWorkspaceAclGrant.ReceiveBeforeExitAsync(pending.Task, Task.CompletedTask, CancellationToken.None);
        var response = new WorkspaceAclResponse(false, "confirmed failure");
        pending.SetResult(response);
        Assert.That(await receive, Is.EqualTo(response));
        var missing = new TaskCompletionSource<WorkspaceAclResponse>();
        await Assert.ThrowsAsync<TimeoutException>(() => ProcessWorkspaceAclGrant.ReceiveBeforeExitAsync(missing.Task,
                                                            Task.CompletedTask, CancellationToken.None));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newPath, string existingPath, IntPtr security);

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExecutableInsideWriteRoot_IsRejectedBeforeHelperStart(bool additionalRoot)
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows grant checks.");
        using var workspace = new TestTempDir();
        var       root      = Path.GetDirectoryName(Environment.ProcessPath!)!;
        var       identity  = WorkspaceDirectoryIdentity.Capture(additionalRoot ? workspace.Root : root);
        var       grant     = new ProcessWorkspaceAclGrant([], [root]);

        var preflight = Assert.Throws<InvalidDataException>(() => grant.ValidatePreparationScope(identity));
        Assert.That(preflight!.Message, Does.Contain("所有写根之外"));
        var failure = await Assert.ThrowsAsync<WorkspaceGrantFailureException>(() =>
                                                                                   grant.GrantAsync(identity,
                                                                                            CancellationToken.None));
        Assert.That(failure!.InnerException, Is.TypeOf<InvalidDataException>());
    }

    [TestCase(false, 0, "access denied")]
    [TestCase(false, 1, "access denied")]
    [TestCase(true, 1, null)]
    public void ConfirmedHelperFailure_PreservesDetail(bool succeeded, int exitCode, string? error)
    {
        var failure = Assert.Throws<WorkspaceGrantFailureException>(() =>
                                                                        ProcessWorkspaceAclGrant
                                                                           .ConfirmResponse(new
                                                                                         WorkspaceAclResponse(succeeded,
                                                                                                  error), exitCode));
        Assert.That(failure!.Message, Does.Contain(error ?? "未提供错误详情"));
        Assert.That(failure.Message, Does.Contain($"（{exitCode}）"));
    }

    [Test]
    public void ConfirmedHelperError_IsBounded()
    {
        var failure = Assert.Throws<WorkspaceGrantFailureException>(() =>
                                                                        ProcessWorkspaceAclGrant
                                                                           .ConfirmResponse(new
                                                                                         WorkspaceAclResponse(false,
                                                                                                  new string('x',
                                                                                                           3000)), 1));
        Assert.That(failure!.Message.Length, Is.LessThan(2100));
        Assert.That(ProcessWorkspaceAclGrant.ConfirmResponse(new WorkspaceAclResponse(true, null), 0),
                    Is.EqualTo(WorkspaceGrantOutcome.Completed));
    }
}

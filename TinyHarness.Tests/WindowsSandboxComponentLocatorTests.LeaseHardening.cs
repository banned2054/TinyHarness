using System.Diagnostics;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

public sealed partial class WindowsSandboxComponentLocatorTests
{
    [Test]
    public async Task Provisioner_EstablishesLeaseBeforeLaunchAndPreservesExistingLeaseWhenUpdateFails()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var packageDir = new TestTempDir();
        using var workspace  = new TestTempDir();
        using var home       = new SandboxTestHome();
        using var leaseDir   = new TestTempDir();
        WriteBundledComponents(packageDir.Root, includeSetup : true, includeRunner : true, includeProbe : false);

        var homeKey   = GlobalDenyReadRefreshGate.HomeKeyForHome(home.Components.SandboxHome, allowMissingHome : true);
        var leasePath = SandboxProvisionLeaseStore.ResolveLeasePath(homeKey, leaseDir.Root);
        var realGate  = new GlobalDenyReadRefreshGate(leaseDir.Root);

        // 1) 预启动租约必须在 ElevateAsync 启动前就已落盘；超时后二次更新租约失败时，
        //    必须保留既有写入者租约且绝不被 finally Dispose 误删。
        var leaseExistedBeforeStart = false;
        FileStream? lockToBreakSecondWrite = null;
        using var currentProcess = Process.GetCurrentProcess();
        var currentIdentity = SandboxElevatedProcessIdentity.TryCapture(currentProcess)!;
        var exitConfirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var elevator = new CallbackStubElevator((components, _, onStarted, _) =>
            {
                leaseExistedBeforeStart = File.Exists(leasePath);
                onStarted(currentIdentity);
                // 以不含 FileShare.Delete 的只读句柄锁定租约文件：File.Move(..., overwrite: true) 会失败，
                // 但 EvaluateAndCleanStaleLease 仍可正常读取该文件以核对已写入的 PID 租约。
                lockToBreakSecondWrite = new FileStream(leasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return Task.FromResult(SandboxSetupElevationResult.DidNotFinish(exitConfirmation.Task, currentIdentity));
            });

            var provisioner = new WindowsSandboxProvisioner(elevator, realGate, TimeSpan.FromSeconds(5));
            var plan = provisioner.BuildPlan(new WindowsSandboxSettings { SandboxHome = home.Components.SandboxHome },
                                             workspace.Root, "TestUser", packageDir.Root);

            var result = await provisioner.ProvisionAsync(plan, CancellationToken.None);
            Assert.That(result.Outcome, Is.EqualTo(SandboxProvisionOutcome.NotFinished));
            Assert.That(leaseExistedBeforeStart, Is.True,
                        "Durable pre-launch lease must already exist on disk before launching helper.");
            Assert.That(File.Exists(leasePath), Is.True,
                        "Failed lease update on timeout must preserve the previously persisted lease.");

            lockToBreakSecondWrite?.Dispose();
            lockToBreakSecondWrite = null;

            var leaseContent = await File.ReadAllTextAsync(leasePath);
            Assert.That(leaseContent, Does.Contain(currentIdentity.ProcessId.ToString()));

            var blocked = await realGate.TryAcquireAsync(home.Components.SandboxHome, TimeSpan.FromMilliseconds(200),
                                                         CancellationToken.None);
            Assert.That(blocked, Is.Null);

            // 确认退出后应清理租约并归还门。
            exitConfirmation.SetResult(true);
            using var reacquired = await realGate.TryAcquireAsync(home.Components.SandboxHome, TimeSpan.FromSeconds(5),
                                                                  CancellationToken.None);
            Assert.That(reacquired, Is.Not.Null);
            Assert.That(File.Exists(leasePath), Is.False);
        }
        finally
        {
            lockToBreakSecondWrite?.Dispose();
            exitConfirmation.TrySetResult(true);
            SandboxProvisionLeaseStore.TryDeleteLease(homeKey, leaseDir.Root);
        }

        // 2) UAC 拒绝（helper 确认未启动）：预启动租约必须被 cleanly 清除且门立即归还。
        var declinedProvisioner = new WindowsSandboxProvisioner(
            new StubElevator { Result = SandboxSetupElevationResult.Declined }, realGate, TimeSpan.FromSeconds(5));
        var declinedPlan = declinedProvisioner.BuildPlan(
            new WindowsSandboxSettings { SandboxHome = home.Components.SandboxHome },
            workspace.Root, "TestUser", packageDir.Root);
        var declinedResult = await declinedProvisioner.ProvisionAsync(declinedPlan, CancellationToken.None);
        Assert.That(declinedResult.Outcome, Is.EqualTo(SandboxProvisionOutcome.Declined));
        Assert.That(File.Exists(leasePath), Is.False);

        // 3) 预启动租约落盘本身失败（租约目录位置被普通文件占据）：绝不调用提权启动 helper。
        var blockedLeasePath = Path.Combine(leaseDir.Root, "not-a-directory");
        File.WriteAllText(blockedLeasePath, "blocker");
        var unstartableGate     = new GlobalDenyReadRefreshGate(blockedLeasePath);
        var neverCalledElevator = new StubElevator();
        var failingProvisioner  = new WindowsSandboxProvisioner(neverCalledElevator, unstartableGate,
                                                                TimeSpan.FromSeconds(5));
        var failingPlan = failingProvisioner.BuildPlan(
            new WindowsSandboxSettings { SandboxHome = home.Components.SandboxHome },
            workspace.Root, "TestUser", packageDir.Root);
        await TestAssert.ThrowsAsync<IOException>(() => failingProvisioner.ProvisionAsync(failingPlan, CancellationToken.None));
        Assert.That(neverCalledElevator.Requests, Is.Empty);
    }

    [Test]
    public void ProvisionLeaseDirectory_IsProtectedAgainstWorkspaceTempGrantAndAclWriteRootsIncludingViaJunction()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var packageDir = new TestTempDir();
        using var workspace  = new TestTempDir();
        using var home       = new SandboxTestHome();
        using var linkHolder = new TestTempDir();
        WriteBundledComponents(packageDir.Root, includeSetup : true, includeRunner : true, includeProbe : false);

        var leaseDir    = GlobalDenyReadRefreshGate.DefaultProvisionLeaseDirectory;
        var leaseParent = Path.GetDirectoryName(leaseDir)!;
        Directory.CreateDirectory(leaseParent);

        // 通过目录 junction 指向租约目录的父目录，验证即使租约目录本身尚未创建，最终路径互斥也能拦截。
        var parentJunction = Path.Combine(linkHolder.Root, "lease-parent-link");
        var mklink = new ProcessStartInfo("cmd.exe")
        {
            Arguments              = $"/d /c mklink /J \"{parentJunction}\" \"{leaseParent}\"",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        using (var proc = Process.Start(mklink)!)
        {
            proc.WaitForExit();
            Assert.That(proc.ExitCode, Is.EqualTo(0), proc.StandardError.ReadToEnd());
        }

        try
        {
            var leaseViaJunction = Path.Combine(parentJunction, Path.GetFileName(leaseDir));

            // 1) C1 清单必须包含持久租约目录。
            var manifest = SandboxProtectedPathsStore.ComposeManifest(home.Components, null);
            Assert.That(manifest.Any(p => string.Equals(p, Path.GetFullPath(leaseDir),
                                                        StringComparison.OrdinalIgnoreCase)),
                        Is.True);

            // 2) Composer 拒绝附加写根、extraEnvironment.TEMP 及经 junction 重叠租约目录的写根。
            var baseSettings = new WindowsSandboxSettings
            {
                Enabled     = true,
                SandboxHome = home.Components.SandboxHome
            };
            var additionalWriteError = TestAssert.Throws<InvalidOperationException>(() =>
                WindowsSandboxComposer.Compose(baseSettings with { AdditionalWriteRoots = [leaseViaJunction] },
                                               workspace.Root, appBaseDirectory : packageDir.Root));
            Assert.That(additionalWriteError.Message, Does.Contain("provisioning lease directory"));

            var tempOverrideError = TestAssert.Throws<InvalidOperationException>(() =>
                WindowsSandboxComposer.Compose(baseSettings with
                {
                    ExtraEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["TEMP"] = parentJunction
                    }
                }, workspace.Root, appBaseDirectory : packageDir.Root));
            Assert.That(tempOverrideError.Message, Does.Contain("provisioning lease directory"));

            // 3) Provisioner.BuildPlan 同样拒绝与租约目录重叠的写根。
            var provisioner = new WindowsSandboxProvisioner(new StubElevator());
            var planError = TestAssert.Throws<InvalidOperationException>(() =>
                provisioner.BuildPlan(baseSettings with { AdditionalWriteRoots = [leaseViaJunction] },
                                      workspace.Root, "TestUser", packageDir.Root));
            Assert.That(planError.Message, Does.Contain("provisioning lease directory"));

            // 4) GrantScope 同样拒绝将租约目录（含 junction 别名）加入 grant 写根。
            var validExec = WindowsSandboxComposer.Compose(baseSettings, workspace.Root,
                                                           appBaseDirectory : packageDir.Root);
            var grantError = TestAssert.Throws<InvalidOperationException>(() =>
                WindowsSandboxComposer.ResolveGrantScope(validExec.Policy, validExec.Components,
                                                         grantReadRoots : null,
                                                         grantWriteRoots : [leaseViaJunction],
                                                         grantMetadataWritePaths : null));
            Assert.That(grantError.Message, Does.Contain("provisioning lease directory"));

            // 5) ACL 准备范围检查（ProcessWorkspaceAclGrant）同样按最终落点拒绝暴露租约目录。
            var aclGrant = new Core.Services.Runtime.ProcessWorkspaceAclGrant([], [parentJunction], baseSettings,
                                                                              packageDir.Root);
            var workspaceIdentity = WorkspaceDirectoryIdentity.Capture(workspace.Root);
            var aclError = TestAssert.Throws<InvalidDataException>(() =>
                aclGrant.ValidatePreparationScope(workspaceIdentity));
            Assert.That(aclError.Message, Does.Contain("provisioning lease directory"));
        }
        finally
        {
            if (Directory.Exists(parentJunction)) Directory.Delete(parentJunction, false);
        }
    }

    [Test]
    public async Task ProvisionLeaseStore_UsesDefinedExitSignalAndSafelyRecoversStalePreLaunchLease()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var leaseDir = new TestTempDir();
        var probeDll = Path.Combine(AppContext.BaseDirectory, "TinyHarness.IndexProbe.dll");
        Assert.That(File.Exists(probeDll), Is.True, $"Expected probe assembly at '{probeDll}'.");

        // 1) 真实子进程存活期间 IsProcessConfirmedExited 必须为 false（基于 WaitForSingleObject 而非未定义 lpExitTime），
        //    退出后立刻返回 true。
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow  = true
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(probeDll);
        startInfo.ArgumentList.Add("sleep");
        startInfo.ArgumentList.Add("30000");

        SandboxElevatedProcessIdentity childIdentity;
        using (var child = Process.Start(startInfo)!)
        {
            try
            {
                childIdentity = SandboxElevatedProcessIdentity.TryCapture(child)!;
                Assert.That(childIdentity, Is.Not.Null);
                Assert.That(SandboxProvisionLeaseStore.IsProcessConfirmedExited(
                                childIdentity.ProcessId, childIdentity.CreationTimeUtcFileTime),
                            Is.False);

                child.Kill(true);
                await child.WaitForExitAsync();
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(true);
                    child.WaitForExit(5000);
                }
            }
        }

        Assert.That(SandboxProvisionLeaseStore.IsProcessConfirmedExited(
                        childIdentity.ProcessId, childIdentity.CreationTimeUtcFileTime),
                    Is.True);

        // 2) 同一开机周期内，缺少 HelperProcessId 且发起方 CLI 已退出的预启动租约：
        //    由于 UAC 提权请求可能仍停留在 AppInfo/consent.exe 尚未创建 helper，绝不能自动删除；
        //    门必须立刻 fail-closed 抛错并指明具体租约路径供人工确认后恢复。
        using var home = new SandboxTestHome();
        var homeKey    = GlobalDenyReadRefreshGate.HomeKeyForHome(home.Components.SandboxHome, allowMissingHome : true);
        var leasePath  = SandboxProvisionLeaseStore.ResolveLeasePath(homeKey, leaseDir.Root);
        var absentHelperPath = Path.Combine(leaseDir.Root, $"tinyharness-absent-setup-{Guid.NewGuid():N}.exe");
        SandboxProvisionLeaseStore.WriteLease(homeKey, null, absentHelperPath, leaseDir.Root,
                                              ownerProcess : childIdentity);
        Assert.That(SandboxProvisionLeaseStore.EvaluateAndCleanStaleLease(homeKey, leaseDir.Root),
                    Is.EqualTo(SandboxProvisionLeaseStatus.OrphanedPreLaunch));
        Assert.That(File.Exists(leasePath), Is.True,
                    "Pre-launch lease on the same boot must not be auto-cleared while a UAC request may still be pending.");

        var gate = new GlobalDenyReadRefreshGate(leaseDir.Root);
        var orphanedError = await TestAssert.ThrowsAsync<InvalidOperationException>(() =>
            gate.TryAcquireAsync(home.Components.SandboxHome, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.That(orphanedError.Message, Does.Contain(leasePath));
        Assert.That(orphanedError.Message, Does.Contain("elevation request has finished"));
        Assert.That(File.Exists(leasePath), Is.True);

        // 3) 确认为上一开机周期遗留的预启动租约（BootId 变化或单调 tick 回绕）：
        //    上一开机周期的 UAC 请求与 helper 必然已全部结束，可以安全自动清理并放行。
        var currentBootId = SandboxProvisionLeaseStore.TryReadSystemBootId();
        var previousBootId = currentBootId is { } boot ? boot - 1 : (int?)null;
        SandboxProvisionLeaseStore.WriteLease(homeKey, null, absentHelperPath, leaseDir.Root,
                                              ownerProcess : childIdentity,
                                              bootId : previousBootId,
                                              recordedTickCount64 : long.MaxValue);
        Assert.That(SandboxProvisionLeaseStore.EvaluateAndCleanStaleLease(homeKey, leaseDir.Root),
                    Is.EqualTo(SandboxProvisionLeaseStatus.Clear));
        Assert.That(File.Exists(leasePath), Is.False);

        using var recoveredHandle = await gate.TryAcquireAsync(home.Components.SandboxHome, TimeSpan.FromSeconds(5),
                                                               CancellationToken.None);
        Assert.That(recoveredHandle, Is.Not.Null);
    }

    [Test]
    public void OverlapsProtection_RefusesDanglingJunctionWriteRootFailClosed()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var packageDir = new TestTempDir();
        using var workspace  = new TestTempDir();
        using var home       = new SandboxTestHome();
        using var linkHolder = new TestTempDir();
        WriteBundledComponents(packageDir.Root, includeSetup : true, includeRunner : true, includeProbe : false);

        // 创建指向尚不存在目标的悬空目录 junction：无法确认最终落点时，所有写根/grant/ACL 入口必须 fail-closed 拒绝。
        var missingTarget  = Path.Combine(linkHolder.Root, "not-yet-created-target");
        var danglingLink   = Path.Combine(linkHolder.Root, "dangling-write-root");
        var mklink = new ProcessStartInfo("cmd.exe")
        {
            Arguments              = $"/d /c mklink /J \"{danglingLink}\" \"{missingTarget}\"",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        using (var proc = Process.Start(mklink)!)
        {
            proc.WaitForExit();
            Assert.That(proc.ExitCode, Is.EqualTo(0), proc.StandardError.ReadToEnd());
        }

        try
        {
            var baseSettings = new WindowsSandboxSettings
            {
                Enabled     = true,
                SandboxHome = home.Components.SandboxHome
            };

            TestAssert.Throws<InvalidOperationException>(() =>
                WindowsSandboxComposer.Compose(baseSettings with { AdditionalWriteRoots = [danglingLink] },
                                               workspace.Root, appBaseDirectory : packageDir.Root));

            var provisioner = new WindowsSandboxProvisioner(new StubElevator());
            TestAssert.Throws<InvalidOperationException>(() =>
                provisioner.BuildPlan(baseSettings with { AdditionalWriteRoots = [danglingLink] },
                                      workspace.Root, "TestUser", packageDir.Root));

            var validExec = WindowsSandboxComposer.Compose(baseSettings, workspace.Root,
                                                           appBaseDirectory : packageDir.Root);
            TestAssert.Throws<InvalidOperationException>(() =>
                WindowsSandboxComposer.ResolveGrantScope(validExec.Policy, validExec.Components,
                                                         grantReadRoots : null,
                                                         grantWriteRoots : [danglingLink],
                                                         grantMetadataWritePaths : null));

            var aclGrant = new Core.Services.Runtime.ProcessWorkspaceAclGrant([], [danglingLink], baseSettings,
                                                                              packageDir.Root);
            var workspaceIdentity = WorkspaceDirectoryIdentity.Capture(workspace.Root);
            TestAssert.Throws<InvalidDataException>(() => aclGrant.ValidatePreparationScope(workspaceIdentity));
        }
        finally
        {
            if (Directory.Exists(danglingLink)) Directory.Delete(danglingLink, false);
        }
    }

    private sealed class CallbackStubElevator(
        Func<WindowsSandboxComponents, string, Action<SandboxElevatedProcessIdentity>, CancellationToken,
            Task<SandboxSetupElevationResult>> handler)
        : ISandboxSetupElevator
    {
        public Task<SandboxSetupElevationResult> ElevateAsync(
            WindowsSandboxComponents components, string base64Payload, CancellationToken cancellationToken) =>
            handler(components, base64Payload, _ => { }, cancellationToken);

        public Task<SandboxSetupElevationResult> ElevateAsync(
            WindowsSandboxComponents               components,
            string                                 base64Payload,
            Action<SandboxElevatedProcessIdentity> onProcessStarted,
            CancellationToken                      cancellationToken) =>
            handler(components, base64Payload, onProcessStarted, cancellationToken);
    }
}

using System.Diagnostics;
using System.Text.Json;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

public sealed partial class WindowsSandboxComponentLocatorTests
{
    [Test]
    public async Task AuthorizedReadCoordinator_RestoreAndRecoveryRejectNewlyRegisteredPeerComponentsAndDisable()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var cliInstall = new TestTempDir();
        using var workspace  = new TestTempDir();
        using var readRoot   = new TestTempDir();
        using var home       = new SandboxTestHome();
        WriteBundledComponents(cliInstall.Root, includeSetup : true, includeRunner : true, includeProbe : false);

        var denyPath = Path.Combine(readRoot.Root, ".env");
        File.WriteAllText(denyPath, "SECRET=1");
        var baselineReadProtection = new SandboxBaselineReadProtection([readRoot.Root], [denyPath]);
        var sharedSettings = new WindowsSandboxSettings
        {
            Enabled     = true,
            SandboxHome = home.Components.SandboxHome
        };

        // CLI 先正常组合并冻结会话策略。
        var composed = WindowsSandboxComposer.Compose(sharedSettings, workspace.Root,
                                                      baselineReadProtection : baselineReadProtection,
                                                      appBaseDirectory : cliInstall.Root);
        var grantScope = WindowsSandboxComposer.ResolveGrantScope(composed.Policy, composed.Components,
                                                                  grantReadRoots : [denyPath],
                                                                  grantWriteRoots : null,
                                                                  grantMetadataWritePaths : null);
        var authorizedExecution = new PreparedProcessExecution("cmd.exe", ["/c", "echo hi"], workspace.Root, 30, false,
                                                               null, GrantScope : grantScope);

        var disableStore   = new AuthorizedReadDisableFile(composed.Components,
                                                           composed.Components.CredentialsDirectory);
        var restoreInvoker = new StubSetupInvoker();
        var innerBackend = new StubInnerBackend
        {
            OnExecute = () =>
            {
                // 模拟授权执行期间另一端在当前工作区子树内新登记冲突组件到共享清单。
                var laterPeerDir = Path.Combine(workspace.Root, "peer-desktop");
                WriteBundledComponents(laterPeerDir, includeSetup : true, includeRunner : true, includeProbe : false);
                var peerComponents = composed.Components with
                {
                    SetupExecutablePath  = Path.Combine(laterPeerDir, "sandbox",
                                                        WindowsSandboxComponentResolver.SetupExecutableFileName),
                    RunnerExecutablePath = Path.Combine(laterPeerDir, "sandbox",
                                                        WindowsSandboxComponentResolver.RunnerExecutableFileName)
                };
                SandboxProtectedPathsStore.WriteIfChanged(peerComponents,
                                                          SandboxProtectedPathsStore.ComposeManifest(peerComponents,
                                                              null));
            }
        };

        var coordinator = new AuthorizedReadCoordinator(
            innerBackend,
            composed.Components,
            composed.Policy,
            restoreInvoker : restoreInvoker,
            windowLock : new StubWindowLock(),
            disableStore : disableStore,
            capabilityProbe : _ => Task.FromResult((uint)WindowsSandboxComponents.AuthorizedSetupVersion),
            postRefreshValidation : (_, _) => Task.CompletedTask);

        // 1) 退出恢复（RestoreAsync）：复核共享清单命中冲突，拒绝下发恢复 refresh 并持久化禁用态。
        var result = await coordinator.ExecuteAsync(authorizedExecution,
                                                    new RecordingOutputCapture(),
                                                    new RecordingOutputCapture(),
                                                    CancellationToken.None);

        Assert.That(result.ExitCode, Is.EqualTo(0));
        Assert.That(innerBackend.CallCount, Is.EqualTo(1));
        Assert.That(restoreInvoker.RefreshPayloads, Is.Empty);

        var (disabled, reason) = await disableStore.ReadAsync(CancellationToken.None);
        Assert.That(disabled, Is.True);
        Assert.That(reason, Does.Contain("authorized-read restore write root"));
        Assert.That(reason, Does.Contain("shared-home registered protected path"));

        // 2) 下一次授权入口恢复尝试（RecoverInsideWindowAsync）：同样拦截，拒绝下发恢复 refresh 且保持禁用。
        var secondInner = new StubInnerBackend();
        var secondCoordinator = new AuthorizedReadCoordinator(
            secondInner,
            composed.Components,
            composed.Policy,
            restoreInvoker : restoreInvoker,
            windowLock : new StubWindowLock(),
            disableStore : disableStore,
            capabilityProbe : _ => Task.FromResult((uint)WindowsSandboxComponents.AuthorizedSetupVersion),
            postRefreshValidation : (_, _) => Task.CompletedTask);

        var recoveryError = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() =>
            secondCoordinator.ExecuteAsync(authorizedExecution,
                                           new RecordingOutputCapture(),
                                           new RecordingOutputCapture(),
                                           CancellationToken.None));

        Assert.That(recoveryError.Message, Does.Contain("Authorized execution is disabled"));
        Assert.That(recoveryError.Message, Does.Contain("authorized-read restore write root"));
        Assert.That(recoveryError.Message, Does.Contain("shared-home registered protected path"));
        Assert.That(restoreInvoker.RefreshPayloads, Is.Empty);
        Assert.That(secondInner.CallCount, Is.Zero);
        var (stillDisabled, _) = await disableStore.ReadAsync(CancellationToken.None);
        Assert.That(stillDisabled, Is.True);
    }

    [Test]
    public void Provisioner_BuildPlanReadsSharedProtectedPathsWithoutCreatingLockFile()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var packageDir = new TestTempDir();
        using var workspace  = new TestTempDir();
        using var home       = new SandboxTestHome();
        using var peerDir    = new TestTempDir();
        WriteBundledComponents(packageDir.Root, includeSetup : true, includeRunner : true, includeProbe : false);

        // 预先直接写入受保护清单文件（不创建 .lock），验证 BuildPlan 严格只读且无副作用。
        Directory.CreateDirectory(home.Components.CredentialsDirectory);
        var manifestPath = SandboxProtectedPathsStore.ResolvePath(home.Components);
        var lockPath     = manifestPath + ".lock";
        var peerSetup    = Path.Combine(peerDir.Root, "sandbox", WindowsSandboxComponentResolver.SetupExecutableFileName);
        File.WriteAllText(manifestPath,
                          JsonSerializer.Serialize(new[] { peerSetup }, WindowsSandboxJsonContext.Default.StringArray));
        Assert.That(File.Exists(lockPath), Is.False);

        var provisioner = new WindowsSandboxProvisioner(new StubElevator());
        var settings    = new WindowsSandboxSettings { SandboxHome = home.Components.SandboxHome };
        _ = provisioner.BuildPlan(settings, workspace.Root, "TestUser", packageDir.Root);

        Assert.That(File.Exists(lockPath), Is.False, "BuildPlan must remain side-effect free and never create .lock.");
        var readPaths = SandboxProtectedPathsStore.ReadRegisteredPathsForHome(home.Components.SandboxHome);
        Assert.That(readPaths, Is.EqualTo([peerSetup]));
        Assert.That(File.Exists(lockPath), Is.False);

        // 首次 provisioning（SandboxHome 尚不存在）同样不得创建目录或 .lock 文件。
        using var freshParent = new TestTempDir();
        var       freshHome   = Path.Combine(freshParent.Root, "uninitialized-home");
        _ = provisioner.BuildPlan(new WindowsSandboxSettings { SandboxHome = freshHome },
                                  workspace.Root, "TestUser", packageDir.Root);
        Assert.That(Directory.Exists(freshHome), Is.False);
    }

    [Test]
    public async Task Provisioner_HoldsHomeRefreshGateOnExistingHomeAndDefersReleaseWhenExitUnconfirmed()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var packageDir  = new TestTempDir();
        using var workspace   = new TestTempDir();
        using var home        = new SandboxTestHome();
        using var freshParent = new TestTempDir();
        WriteBundledComponents(packageDir.Root, includeSetup : true, includeRunner : true, includeProbe : false);

        // 1) 首次 provisioning（SandboxHome 尚不存在）：同样获取按规范 home 键控的刷新门，与后续组件登记共用边界。
        var freshHome = Path.Combine(freshParent.Root, "first-time-home");
        var freshGate = new RecordingDenyReadRefreshGate();
        var freshHeldDuringElevate = false;
        var freshElevator = new StubElevator
        {
            Result    = SandboxSetupElevationResult.Completed0,
            OnElevate = () => freshHeldDuringElevate = freshGate.ActiveHandles == 1
        };
        var freshProvisioner = new WindowsSandboxProvisioner(freshElevator, freshGate);
        var freshPlan = freshProvisioner.BuildPlan(new WindowsSandboxSettings { SandboxHome = freshHome },
                                                   workspace.Root, "TestUser", packageDir.Root);

        var freshResult = await freshProvisioner.ProvisionAsync(freshPlan, CancellationToken.None);
        Assert.That(freshResult.Outcome, Is.EqualTo(SandboxProvisionOutcome.Completed));
        Assert.That(freshGate.AcquireCalls, Is.EqualTo(1));
        Assert.That(freshHeldDuringElevate, Is.True);
        Assert.That(freshGate.ActiveHandles, Is.Zero);

        // 2) 已有 SandboxHome 正常完成：提权执行期间必须持有按 home 键控的刷新门，结束后立即释放。
        var recordingGate      = new RecordingDenyReadRefreshGate();
        var heldDuringElevate  = false;
        var normalElevator = new StubElevator
        {
            Result    = SandboxSetupElevationResult.Completed0,
            OnElevate = () => heldDuringElevate = recordingGate.ActiveHandles == 1
        };
        var normalProvisioner = new WindowsSandboxProvisioner(normalElevator, recordingGate);
        var plan = normalProvisioner.BuildPlan(new WindowsSandboxSettings { SandboxHome = home.Components.SandboxHome },
                                               workspace.Root, "TestUser", packageDir.Root);

        var completedResult = await normalProvisioner.ProvisionAsync(plan, CancellationToken.None);
        Assert.That(completedResult.Outcome, Is.EqualTo(SandboxProvisionOutcome.Completed));
        Assert.That(recordingGate.AcquireCalls, Is.EqualTo(1));
        Assert.That(recordingGate.LastHome, Is.EqualTo(home.Components.SandboxHome));
        Assert.That(heldDuringElevate, Is.True);
        Assert.That(recordingGate.ActiveHandles, Is.Zero);
        Assert.That(recordingGate.LastHandle!.DisposeCalls, Is.EqualTo(1));
        Assert.That(recordingGate.LastHandle.ReleaseWhenConfirmedCalls, Is.Zero);

        // 3) 已有 SandboxHome 上退出未确认（NotFinished + 后台退出确认任务）：
        // 使用真实 GlobalDenyReadRefreshGate 验证退出确认前门持续持有（阻止并发获取），确认退出后释放（不永久泄露）。
        var exitConfirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var realGate         = new GlobalDenyReadRefreshGate();
        var unconfirmedElevator = new StubElevator
        {
            Result = SandboxSetupElevationResult.DidNotFinish(exitConfirmation.Task)
        };
        var unconfirmedProvisioner = new WindowsSandboxProvisioner(unconfirmedElevator, realGate,
                                                                   TimeSpan.FromSeconds(5));
        var unconfirmedResult = await unconfirmedProvisioner.ProvisionAsync(plan, CancellationToken.None);
        Assert.That(unconfirmedResult.Outcome, Is.EqualTo(SandboxProvisionOutcome.NotFinished));

        var contendedHandle = await realGate.TryAcquireAsync(home.Components.SandboxHome,
                                                             TimeSpan.FromMilliseconds(150),
                                                             CancellationToken.None);
        Assert.That(contendedHandle, Is.Null,
                    "Refresh gate must stay held while elevated helper exit is still unconfirmed.");

        exitConfirmation.SetResult(true);
        using var reacquiredHandle = await realGate.TryAcquireAsync(home.Components.SandboxHome,
                                                                    TimeSpan.FromSeconds(5),
                                                                    CancellationToken.None);
        Assert.That(reacquiredHandle, Is.Not.Null,
                    "Refresh gate must be released once elevated helper exit is confirmed.");

        // 4) 退出未确认且无确认任务（ExitConfirmation == null）：经 PersistUntilProcessExits -> ReleaseWhenConfirmed(false) fail-closed 保持持有。
        var failClosedGate = new RecordingDenyReadRefreshGate();
        var failClosedProvisioner = new WindowsSandboxProvisioner(
            new StubElevator { Result = SandboxSetupElevationResult.TimedOut }, failClosedGate);
        var failClosedResult = await failClosedProvisioner.ProvisionAsync(plan, CancellationToken.None);
        Assert.That(failClosedResult.Outcome, Is.EqualTo(SandboxProvisionOutcome.NotFinished));
        Assert.That(failClosedGate.LastHandle!.ReleaseWhenConfirmedCalls, Is.EqualTo(1));
        Assert.That(failClosedGate.ActiveHandles, Is.EqualTo(1));
    }

    [Test]
    public async Task Provisioner_FirstTimeProvisioningBeforeHomeExistsSharesSameGateAsPostCreationCompose()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var packageDir  = new TestTempDir();
        using var workspace   = new TestTempDir();
        using var realParent  = new TestTempDir();
        using var linkHolder  = new TestTempDir();
        WriteBundledComponents(packageDir.Root, includeSetup : true, includeRunner : true, includeProbe : false);

        var junctionParent = Path.Combine(linkHolder.Root, "parent-junction");
        var mklink = new ProcessStartInfo("cmd.exe")
        {
            Arguments              = $"/d /c mklink /J \"{junctionParent}\" \"{realParent.Root}\"",
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

        var freshHomeViaJunction = Path.Combine(junctionParent, "first-time-home");
        var freshHomeDirect      = Path.Combine(realParent.Root, "first-time-home");
        try
        {
            // 首次 provisioning 前 home 尚不存在：通过最近已存在祖先展开最终路径后，junction 与真实路径产出同一互斥体名。
            var mutexBeforeViaJunction = GlobalDenyReadRefreshGate.MutexNameForHomeAllowMissing(freshHomeViaJunction);
            var mutexBeforeDirect      = GlobalDenyReadRefreshGate.MutexNameForHomeAllowMissing(freshHomeDirect);
            Assert.That(mutexBeforeViaJunction, Is.EqualTo(mutexBeforeDirect));

            var realGate              = new GlobalDenyReadRefreshGate();
            var peerBlockedMidCreate  = false;
            var mutexAfterCreation    = string.Empty;
            var creatingElevator = new AsyncStubElevator(async (_, _) =>
            {
                // 模拟提权 helper 在写 ACL 之前先创建 home 目录，另一端立刻尝试获取该目录的刷新门必须被互斥阻断。
                Directory.CreateDirectory(freshHomeDirect);
                mutexAfterCreation = GlobalDenyReadRefreshGate.MutexNameForHome(freshHomeDirect);
                var contended = await realGate.TryAcquireAsync(freshHomeDirect, TimeSpan.FromMilliseconds(200),
                                                               CancellationToken.None);
                peerBlockedMidCreate = contended is null;
                contended?.Dispose();
                return SandboxSetupElevationResult.Completed0;
            });

            var provisioner = new WindowsSandboxProvisioner(creatingElevator, realGate, TimeSpan.FromSeconds(5));
            var plan = provisioner.BuildPlan(new WindowsSandboxSettings { SandboxHome = freshHomeViaJunction },
                                             workspace.Root, "TestUser", packageDir.Root);

            var result = await provisioner.ProvisionAsync(plan, CancellationToken.None);
            Assert.That(result.Outcome, Is.EqualTo(SandboxProvisionOutcome.Completed));
            Assert.That(mutexAfterCreation, Is.EqualTo(mutexBeforeViaJunction));
            Assert.That(peerBlockedMidCreate, Is.True,
                        "Peer Compose/Refresh after home creation must share the exact same gate as first-time provisioning.");

            // provisioning 正常结束后，另一端可以立刻取得门并完成组合登记。
            using var postHandle = await realGate.TryAcquireAsync(freshHomeDirect, TimeSpan.FromSeconds(5),
                                                                  CancellationToken.None);
            Assert.That(postHandle, Is.Not.Null);
        }
        finally
        {
            SandboxProvisionLeaseStore.TryDeleteLeaseForHome(freshHomeDirect);
            if (Directory.Exists(junctionParent)) Directory.Delete(junctionParent, false);
        }
    }

    [Test]
    public async Task Provisioner_CliSubprocessExitOnTimeoutKeepsHomeGateProtectedUntilActualWriterExits()
    {
        if (!OperatingSystem.IsWindows()) return;

        // 同时验证 NtQuerySystemInformation 回落路径的 64 位结构偏移对当前活动进程识别准确。
        using (var currentProcess = Process.GetCurrentProcess())
        {
            var currentIdentity = SandboxElevatedProcessIdentity.TryCapture(currentProcess);
            Assert.That(currentIdentity, Is.Not.Null);
            Assert.That(SandboxProvisionLeaseStore.TryCheckExitedViaSystemProcessInformation(
                            currentIdentity!.ProcessId, currentIdentity.CreationTimeUtcFileTime),
                        Is.False);
            Assert.That(SandboxProvisionLeaseStore.TryCheckExitedViaSystemProcessInformation(
                            currentIdentity.ProcessId, (currentIdentity.CreationTimeUtcFileTime ?? 0) + 1),
                        Is.True);
        }

        using var packageDir  = new TestTempDir();
        using var workspace   = new TestTempDir();
        using var freshParent = new TestTempDir();
        WriteBundledComponents(packageDir.Root, includeSetup : true, includeRunner : true, includeProbe : false);

        var probeDll = Path.Combine(AppContext.BaseDirectory, "TinyHarness.IndexProbe.dll");
        Assert.That(File.Exists(probeDll), Is.True, $"Expected probe assembly at '{probeDll}'.");

        // 使用尚未创建的首次 home 同时覆盖“首次初始化 + 子进程超时退出后写入者仍在运行”两条边界。
        var freshHome = Path.Combine(freshParent.Root, "subprocess-provision-home");
        var homeKey   = GlobalDenyReadRefreshGate.HomeKeyForHome(freshHome, allowMissingHome : true);
        var leasePath = SandboxProvisionLeaseStore.ResolveLeasePath(homeKey);
        Process? writerProcess = null;
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add(probeDll);
            startInfo.ArgumentList.Add("sandbox-provision-timeout");
            startInfo.ArgumentList.Add(freshHome);
            startInfo.ArgumentList.Add(workspace.Root);
            startInfo.ArgumentList.Add(packageDir.Root);
            startInfo.ArgumentList.Add("30000");

            using var cliSubprocess = Process.Start(startInfo)!;
            // 按行读取 HELPER_PID 后直接等待 CLI 子进程退出，不调用 ReadToEndAsync（Windows 下孙进程可能继承管道句柄）。
            var pidLine = await cliSubprocess.StandardOutput.ReadLineAsync();
            await cliSubprocess.WaitForExitAsync();

            // CLI 子进程已彻底退出（后台线程全部终止，内核互斥体已被放弃），退出码为 1。
            Assert.That(cliSubprocess.ExitCode, Is.EqualTo(1), $"stdout='{pidLine}'");
            Assert.That(pidLine, Is.Not.Null.And.StartsWith("HELPER_PID="));
            var writerPid = int.Parse(pidLine!["HELPER_PID=".Length..]);

            writerProcess = Process.GetProcessById(writerPid);
            Assert.That(writerProcess.HasExited, Is.False, "Simulated helper writer should still be running after CLI exit.");
            Assert.That(File.Exists(leasePath), Is.True, "Durable provision lease must persist after CLI exits on timeout.");

            // 模拟仍在运行的写入者已创建 home 目录：此时即使前 CLI 进程已退出，另一端仍必须被门持续阻断。
            Directory.CreateDirectory(freshHome);
            var realGate = new GlobalDenyReadRefreshGate();
            var blockedRefreshGate = await realGate.TryAcquireAsync(freshHome, TimeSpan.FromMilliseconds(250),
                                                                    CancellationToken.None);
            Assert.That(blockedRefreshGate, Is.Null,
                        "Abandoned mutex after CLI exit must not release protection while the actual writer is still alive.");

            var blockedProvisionGate = await realGate.TryAcquireForProvisioningAsync(freshHome,
                                                                                     TimeSpan.FromMilliseconds(250),
                                                                                     CancellationToken.None);
            Assert.That(blockedProvisionGate, Is.Null);

            // 实际写入者确认退出后，另一端应立即取得门并自动清理持久化租约（不永久锁死）。
            writerProcess.Kill(true);
            Assert.That(writerProcess.WaitForExit(5000), Is.True);

            using var reacquired = await realGate.TryAcquireAsync(freshHome, TimeSpan.FromSeconds(5),
                                                                  CancellationToken.None);
            Assert.That(reacquired, Is.Not.Null,
                        "Gate must become available immediately once the actual writer process has exited.");
            Assert.That(File.Exists(leasePath), Is.False,
                        "Stale durable provision lease must be cleaned automatically after writer exit.");
        }
        finally
        {
            if (writerProcess is not null)
            {
                try
                {
                    if (!writerProcess.HasExited)
                    {
                        writerProcess.Kill(true);
                        writerProcess.WaitForExit(5000);
                    }
                }
                catch
                {
                }
                finally
                {
                    writerProcess.Dispose();
                }
            }

            SandboxProvisionLeaseStore.TryDeleteLease(homeKey);
        }
    }

    private sealed class AsyncStubElevator(
        Func<WindowsSandboxComponents, CancellationToken, Task<SandboxSetupElevationResult>> handler)
        : ISandboxSetupElevator
    {
        public Task<SandboxSetupElevationResult> ElevateAsync(
            WindowsSandboxComponents components, string base64Payload, CancellationToken cancellationToken) =>
            handler(components, cancellationToken);
    }

    private sealed class StubInnerBackend : IProcessExecutionBackend
    {
        public int     CallCount { get; private set; }
        public Action? OnExecute { get; set; }

        public Task<ProcessExecutionResult> ExecuteAsync(
            PreparedProcessExecution execution,
            IProcessOutputCapture    stdoutCapture,
            IProcessOutputCapture    stderrCapture,
            CancellationToken        cancellationToken)
        {
            CallCount++;
            OnExecute?.Invoke();
            return Task.FromResult(new ProcessExecutionResult(0, false, null));
        }
    }

    private sealed class StubWindowLock : IAuthorizedReadWindowLock
    {
        public Task<IAuthorizedReadWindowHandle?> TryAcquireAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult<IAuthorizedReadWindowHandle?>(new WindowHandle());

        private sealed class WindowHandle : IAuthorizedReadWindowHandle
        {
            public void Dispose() { }
        }
    }

    private sealed class RecordingDenyReadRefreshGate : ISandboxDenyReadRefreshGate
    {
        private int _activeHandles;

        public int                  AcquireCalls  { get; private set; }
        public string?              LastHome      { get; private set; }
        public RecordingGateHandle? LastHandle    { get; private set; }
        public int                  ActiveHandles => Volatile.Read(ref _activeHandles);

        public Task<ISandboxDenyReadRefreshGateHandle?> TryAcquireAsync(
            string sandboxHome, TimeSpan timeout, CancellationToken cancellationToken)
        {
            AcquireCalls++;
            LastHome = sandboxHome;
            Interlocked.Increment(ref _activeHandles);
            var handle = new RecordingGateHandle(this);
            LastHandle = handle;
            return Task.FromResult<ISandboxDenyReadRefreshGateHandle?>(handle);
        }

        private void OnReleased() => Interlocked.Decrement(ref _activeHandles);

        internal sealed class RecordingGateHandle(RecordingDenyReadRefreshGate owner) : ISandboxDenyReadRefreshGateHandle
        {
            private int _ownershipTransferred;
            private int _released;

            public int DisposeCalls              { get; private set; }
            public int ReleaseWhenConfirmedCalls { get; private set; }

            public void Dispose()
            {
                DisposeCalls++;
                if (Volatile.Read(ref _ownershipTransferred) != 0) return;
                ReleaseOnce();
            }

            public void ReleaseWhenConfirmed(Task<bool> helperExitConfirmation)
            {
                ReleaseWhenConfirmedCalls++;
                if (Interlocked.Exchange(ref _ownershipTransferred, 1) != 0) return;

                if (helperExitConfirmation.IsCompleted)
                {
                    if (helperExitConfirmation.Status == TaskStatus.RanToCompletion && helperExitConfirmation.Result)
                        ReleaseOnce();
                    return;
                }

                helperExitConfirmation.ContinueWith(task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion && task.Result)
                        ReleaseOnce();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            private void ReleaseOnce()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                    owner.OnReleased();
            }
        }
    }
}

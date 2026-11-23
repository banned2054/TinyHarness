using System.Text.Json;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         授权读协调器的三道门单元测试（假锁/假 refresh/假后端）：普通执行透传零变化、能力门
///         按部署代拒绝授权执行、串行门在窗口被占时可见超时、恢复刷新持窗口执行、恢复失败或回收
///         未知持久化禁用并在入口阻断、恢复成功清除禁用；恢复/退出 refresh 另持按 home 键控的
///         拒读刷新门，门等待失败按既有失败语义转禁用。全部离线，不触碰机器账户或真实 ACL。
///     </para>
///     <para>
///         Unit tests for the authorized-read coordinator's three gates (fake lock, fake refresh, fake
///         backend): normal executions pass through unchanged, the capability gate refuses authorized
///         executions by deployment generation, the serialization gate times out visibly while the
///         window is held, the recovery refresh runs inside the window, restore failures and unknown
///         reclamation persist the disable state and block later entries, and a successful restore
///         clears the disable; the recovery / exit refreshes additionally hold the home-keyed
///         deny-read refresh gate, and a failed gate wait turns into the existing disable semantics.
///         Everything runs offline without touching machine accounts or real ACLs.
///     </para>
/// </summary>
public class AuthorizedReadCoordinatorTests
{
    // ---------------------------------------------------------------- 普通执行透传

    [Test]
    public async Task ExecuteAsync_NormalExecution_PassesThroughWithoutAnyGate()
    {
        using var home = new SandboxTestHome();
        var restore = new FakeSetupInvoker();
        var (coordinator, inner, _, windowLock, _) = CreateCoordinator(home,
                                                                            probe : _ =>
                                                                                throw new InvalidOperationException(
                                                                                    "the capability probe must not run for normal executions"),
                                                                            restore : restore);

        var (stdout, stderr) = Captures();
        var result = await coordinator.ExecuteAsync(NormalPlan(home), stdout, stderr, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            TestAssert.NotNull(inner.ReceivedExecution);
            Assert.That(windowLock.AcquireCalls, Is.EqualTo(0));
            Assert.That(restore.RefreshPayloads, Is.Empty);
        });
    }

    [Test]
    public async Task ExecuteAsync_NormalExecution_UnaffectedByDisabledState()
    {
        using var home = new SandboxTestHome();
        var restore = new FakeSetupInvoker();
        var (coordinator, inner, _, windowLock, store) = CreateCoordinator(home, probe : ProvisionedV7,
                                                                           restore : restore);
        await store.DisableAsync("restore failed: earlier window", CancellationToken.None);

        var (stdout, stderr) = Captures();
        var result = await coordinator.ExecuteAsync(NormalPlan(home), stdout, stderr, CancellationToken.None);

        Assert.Multiple(() =>
        {
            // 禁用门只拦截授权执行；普通执行零行为变化。
            Assert.That(result.ExitCode, Is.EqualTo(0));
            TestAssert.NotNull(inner.ReceivedExecution);
            Assert.That(windowLock.AcquireCalls, Is.EqualTo(0));
            Assert.That(restore.RefreshPayloads, Is.Empty);
        });
    }

    // ---------------------------------------------------------------- 能力门

    [Test]
    public async Task ExecuteAsync_ProvisionedVersionBelowAuthorizedGeneration_RefusesVisibly()
    {
        using var home = new SandboxTestHome();
        var restore = new FakeSetupInvoker();
        var (coordinator, inner, _, windowLock, _) = CreateCoordinator(home, probe : _ => Task.FromResult(5u),
                                                                       restore : restore);

        var (stdout, stderr) = Captures();
        var ex = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() =>
                   coordinator.ExecuteAsync(AuthorizedPlan(home), stdout, stderr, CancellationToken.None));

        Assert.Multiple(() =>
        {
            // 未明确声明 v7 能力即拒绝授权执行：不降级、不静默去除授权路径后放行。
            TestAssert.Contains("does not declare the authorized-read capability", ex!.Message, StringComparison.Ordinal);
            Assert.That(inner.ReceivedExecution, Is.Null);
            Assert.That(windowLock.AcquireCalls, Is.EqualTo(0));
            Assert.That(restore.RefreshPayloads, Is.Empty);
        });
    }

    [Test]
    public async Task ExecuteAsync_ProvisionedVersionAtAuthorizedGeneration_DispatchesAuthorizedExecution()
    {
        using var home = new SandboxTestHome();
        var restore = new FakeSetupInvoker();
        var (coordinator, inner, _, windowLock, _) = CreateCoordinator(home, probe : ProvisionedV7,
                                                                       restore : restore);

        var (stdout, stderr) = Captures();
        var result = await coordinator.ExecuteAsync(AuthorizedPlan(home), stdout, stderr, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            TestAssert.NotNull(inner.ReceivedExecution);
            Assert.That(windowLock.AcquireCalls, Is.EqualTo(1));
            // 窗口关闭前完成恢复 refresh：v7 空 authorized_read_paths 重同步授权组全量 deny。
            Assert.That(restore.RefreshPayloads, Has.Count.EqualTo(1));
            var restorePayload = restore.RefreshPayloads[0];
            Assert.That(restorePayload.Version, Is.EqualTo(WindowsSandboxComponents.AuthorizedSetupVersion));
            Assert.That(restorePayload.AuthorizedReadPaths, Is.Not.Null.And.Empty);
            Assert.That(restorePayload.ReadRoots, Does.Not.Contain(DenyReadPath(home)));
        });
    }

    // ---------------------------------------------------------------- 串行门

    [Test]
    public async Task ExecuteAsync_WindowHeldByAnotherExecution_TimesOutVisibly()
    {
        using var home = new SandboxTestHome();
        var lockGate = new ScriptedWindowLock();
        lockGate.Script = () => Task.FromResult<IAuthorizedReadWindowHandle?>(null);
        var restore = new FakeSetupInvoker();
        var (coordinator, inner, _, _, _) = CreateCoordinator(home, probe : ProvisionedV7, windowLock : lockGate,
                                                              restore : restore);

        var (stdout, stderr) = Captures();
        var ex = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() =>
                   coordinator.ExecuteAsync(AuthorizedPlan(home), stdout, stderr, CancellationToken.None));

        Assert.Multiple(() =>
        {
            TestAssert.Contains("holds the authorization window", ex!.Message, StringComparison.Ordinal);
            Assert.That(lockGate.AcquireCalls, Is.EqualTo(1));
            Assert.That(lockGate.LastTimeout, Is.EqualTo(AuthorizedReadCoordinator.DefaultWindowTimeout));
            // 超时不消耗内层执行，也不做恢复 refresh。
            Assert.That(inner.ReceivedExecution, Is.Null);
            Assert.That(restore.RefreshPayloads, Is.Empty);
        });
    }

    [Test]
    public void ExecuteAsync_CancelledWhileWaitingForWindow_PropagatesCancellation()
    {
        using var home = new SandboxTestHome();
        var (coordinator, inner, _, _, _) = CreateCoordinator(home, probe : ProvisionedV7);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // 窗口获取尊重取消（真实锁与脚本锁同一契约）：取消即失败可见，不消耗内层执行。
        var (stdout, stderr) = Captures();
        Assert.ThrowsAsync<OperationCanceledException>(() =>
            coordinator.ExecuteAsync(AuthorizedPlan(home), stdout, stderr, cancellation.Token));
        Assert.That(inner.ReceivedExecution, Is.Null);
    }

    [Test]
    public void GlobalAuthorizedReadWindowLock_SecondAcquisitionTimesOut_ThenReleaseAllowsReacquisition()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The global window mutex is Windows-only.");

        var lockGate = new GlobalAuthorizedReadWindowLock();
        var first = lockGate.TryAcquireAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                             .GetAwaiter()
                             .GetResult();
        try
        {
            TestAssert.NotNull(first);

            // 第二个请求在短时限内拿不到窗口：串行门超时可见。
            var second = lockGate.TryAcquireAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None)
                                 .GetAwaiter()
                                 .GetResult();
            Assert.That(second, Is.Null);

            // 释放后第三个请求重新获得窗口：机器级互斥覆盖所有进程实例。
            first!.Dispose();
            var third = lockGate.TryAcquireAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                                .GetAwaiter()
                                .GetResult();
            TestAssert.NotNull(third);
            third!.Dispose();
            // 首个句柄只 Dispose 一次；此处置检查表达窗口释放是幂等信号而非重复归还。
            Assert.That(first, Is.Not.SameAs(third));
        }
        finally
        {
            first?.Dispose();
        }
    }

    // ---------------------------------------------------------------- 禁用状态与恢复

    [Test]
    public async Task ExecuteAsync_RestoreFailureAfterSuccess_PersistsDisableAndKeepsResult()
    {
        using var home = new SandboxTestHome();
        var restore = new FakeSetupInvoker();
        var (coordinator, inner, _, _, store) = CreateCoordinator(home, probe : ProvisionedV7, restore : restore);
        restore.ThrowOnRefresh = new InvalidOperationException("restore helper failed");

        var (stdout, stderr) = Captures();
        var result = await coordinator.ExecuteAsync(AuthorizedPlan(home), stdout, stderr, CancellationToken.None);
        var (disabled, reason) = await store.ReadAsync(CancellationToken.None);

        // 命令已完成：结果如实返回；恢复失败转持久化禁用，下一次授权执行在入口可见阻断。
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            Assert.That(disabled, Is.True);
            TestAssert.Contains("restore helper failed", reason!, StringComparison.Ordinal);
        });
    }

    [Test]
    public async Task ExecuteAsync_DisabledState_BlocksAtEntryUntilSuccessfulRecovery()
    {
        using var home = new SandboxTestHome();
        var trace       = new List<string>();
        var restoreGate = new FakeSetupInvoker { ThrowOnRefresh = new InvalidOperationException("recovery helper failed") };
        var (coordinator, inner, _, windowLock, store) = CreateCoordinator(home, probe : ProvisionedV7,
                                                                           restore : new TracedSetupInvoker(restoreGate, trace),
                                                                           windowLock : new ScriptedWindowLock(trace));
        await store.DisableAsync("restore failed: earlier window", CancellationToken.None);

        var (blockedStdout, blockedStderr) = Captures();
        var blocked = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() =>
                          coordinator.ExecuteAsync(AuthorizedPlan(home), blockedStdout, blockedStderr,
                                                   CancellationToken.None));
        Assert.Multiple(() =>
        {
            TestAssert.Contains("Authorized execution is disabled", blocked!.Message, StringComparison.Ordinal);
            TestAssert.Contains("recovery attempt failed", blocked.Message, StringComparison.Ordinal);
            Assert.That(inner.ReceivedExecution, Is.Null);
            // 恢复刷新是授权 ACL 变更：必须先取得串行窗口再执行恢复 refresh。
            Assert.That(trace, Is.EqualTo(["acquire", "restore"]));
        });

        // 同一实例不再重复恢复尝试：无新增窗口获取与恢复 refresh 的纯入口拒绝，
        // 错误文案区分于能力不足与串行超时。
        restoreGate.ThrowOnRefresh = null;
        var (againStdout, againStderr) = Captures();
        var blockedAgain = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() =>
                               coordinator.ExecuteAsync(AuthorizedPlan(home), againStdout, againStderr,
                                                        CancellationToken.None));
        Assert.Multiple(() =>
        {
            TestAssert.Contains("Authorized execution is disabled", blockedAgain!.Message, StringComparison.Ordinal);
            Assert.That(inner.ReceivedExecution, Is.Null);
            Assert.That(trace, Is.EqualTo(["acquire", "restore"]));
        });

        // 重新组合（新实例）恢复刷新成功：禁用清除，授权执行放行。
        var (recovered, recoveredInner, _, _, recoveredStore) = CreateCoordinator(home, probe : ProvisionedV7,
                                                                                  restore : new TracedSetupInvoker(restoreGate, trace),
                                                                                  windowLock : new ScriptedWindowLock(trace));
        var (recoveredStdout, recoveredStderr) = Captures();
        var result = await recovered.ExecuteAsync(AuthorizedPlan(home), recoveredStdout, recoveredStderr,
                                                  CancellationToken.None);
        var (recoveredDisabled, _) = await recoveredStore.ReadAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            TestAssert.NotNull(recoveredInner.ReceivedExecution);
            // 新实例时序：窗口先于窗口内恢复 refresh，执行完成后再做退出前的恢复 refresh。
            Assert.That(trace, Is.EqualTo(["acquire", "restore", "acquire", "restore", "restore"]));
            Assert.That(recoveredDisabled, Is.False);
            Assert.That(File.Exists(recoveredStore.StatePath), Is.False);
        });
    }

    [Test]
    public async Task ExecuteAsync_UnknownReclamation_DisablesSubjectAndRethrowsUncertainty()
    {
        using var home = new SandboxTestHome();
        var inner = new FakeProcessExecutionBackend
        {
            ThrowOnExecute = new ToolExecutionUncertainException("the runner could not be reclaimed")
        };
        var restore = new FakeSetupInvoker();
        var (coordinator, _, _, windowLock, store) = CreateCoordinator(home, probe : ProvisionedV7,
                                                                             restore : restore,
                                                                             innerBackend : inner);

        var (stdout, stderr) = Captures();
        var ex = await TestAssert.ThrowsAsync<ToolExecutionUncertainException>(() =>
                   coordinator.ExecuteAsync(AuthorizedPlan(home), stdout, stderr, CancellationToken.None));
        var (disabled, reason) = await store.ReadAsync(CancellationToken.None);

        // 回收结果未知：原始终止语义如实上抛，同时禁用持久化并在退出窗口前尝试恢复 refresh。
        Assert.Multiple(() =>
        {
            TestAssert.Contains("could not be reclaimed", ex!.Message, StringComparison.Ordinal);
            Assert.That(disabled, Is.True);
            TestAssert.Contains("reclamation", reason!, StringComparison.Ordinal);
            Assert.That(restore.RefreshPayloads, Has.Count.EqualTo(1));
            Assert.That(windowLock.AcquireCalls, Is.EqualTo(1));
        });
    }

    // ---------------------------------------------------------------- 拒读簿记并集与后刷新校验

    [Test]
    public async Task ExecuteAsync_RestorePayloadFoldsDenyReadBookkeepingUnion()
    {
        using var home = new SandboxTestHome();
        var       restore = new FakeSetupInvoker();
        var (coordinator, inner, _, _, _) = CreateCoordinator(home, probe : ProvisionedV7, restore : restore);
        var pairable = Path.Combine(home.Components.SandboxHome, "legacy-scan-hit");
        Directory.CreateDirectory(Path.Combine(home.Components.SandboxHome, ".sandbox"));
        File.WriteAllText(Path.Combine(home.Components.SandboxHome, ".sandbox", "deny_read_acl_state.json"),
                          JsonSerializer.Serialize(new
                          {
                              principals = new Dictionary<string, string[]>
                              {
                                  ["S-1-5-21-9001"] = [pairable]
                              }
                          }));

        var (stdout, stderr) = Captures();
        var result = await coordinator.ExecuteAsync(AuthorizedPlan(home), stdout, stderr, CancellationToken.None);

        // 恢复 refresh 的 deny_read_paths 同样叠加簿记只增并集：授权组与普通组的 deny 基面只收窄，
        // 恢复/退出 refresh 不撤销另一端写入簿记的条目；v7 恢复语义与读根集合不受影响。
        var restorePayload = TestAssert.Single(restore.RefreshPayloads);
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            TestAssert.NotNull(inner.ReceivedExecution);
            Assert.That(restorePayload.Version, Is.EqualTo(WindowsSandboxComponents.AuthorizedSetupVersion));
            Assert.That(restorePayload.AuthorizedReadPaths, Is.Not.Null.And.Empty);
            Assert.That(restorePayload.DenyReadPaths, Does.Contain(DenyReadPath(home)));
            Assert.That(restorePayload.DenyReadPaths, Does.Contain(pairable));
            Assert.That(restorePayload.ReadRoots, Does.Not.Contain(DenyReadPath(home)));
        });
    }

    [Test]
    public async Task ExecuteAsync_RecoveryRefresh_PostRefreshValidationRunsBeforeInnerExecution()
    {
        using var home = new SandboxTestHome();
        var       trace   = new List<string>();
        var       restore = new FakeSetupInvoker();
        var (coordinator, inner, _, windowLock, store) = CreateCoordinator(home, probe : ProvisionedV7,
                                                                           restore : new TracedSetupInvoker(restore, trace),
                                                                           windowLock : new ScriptedWindowLock(trace),
                                                                           innerBackend : new FakeProcessExecutionBackend
                                                                           {
                                                                               ExitCode = 0,
                                                                               Trace    = trace
                                                                           },
                                                                           postRefreshValidation : (_, _) =>
                                                                           {
                                                                               trace.Add("validate");
                                                                               return Task.CompletedTask;
                                                                           });
        await store.DisableAsync("restore failed: earlier window", CancellationToken.None);

        var (stdout, stderr) = Captures();
        var result = await coordinator.ExecuteAsync(AuthorizedPlan(home), stdout, stderr, CancellationToken.None);

        // 恢复链时序：窗口 → 恢复 refresh → 禁用清除 → 共享后刷新校验 → 内层执行 → 退出前恢复 refresh。
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            Assert.That(trace, Is.EqualTo(["acquire", "restore", "validate", "inner", "restore"]));
        });
    }

    [Test]
    public async Task ExecuteAsync_PostRefreshValidationFailure_FailsClosedAfterRecoveryCompleted()
    {
        using var home = new SandboxTestHome();
        var       restore = new FakeSetupInvoker();
        var (coordinator, inner, _, windowLock, store) = CreateCoordinator(home, probe : ProvisionedV7,
                                                                           restore : restore,
                                                                           postRefreshValidation : (_, _) =>
                                                                               throw new ProcessExecutionStartException(
                                                                                   "read-root readiness blocked by test stub",
                                                                                   new TimeoutException("stub")));
        await store.DisableAsync("restore failed: earlier window", CancellationToken.None);

        var (stdout, stderr) = Captures();
        var error = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() =>
                      coordinator.ExecuteAsync(AuthorizedPlan(home), stdout, stderr, CancellationToken.None));
        var (disabled, _) = await store.ReadAsync(CancellationToken.None);

        // 校验失败 fail-closed：命令保持未启动（内层零执行）；恢复本身已完成（禁用状态清除），
        // 错误保留就绪门的启动期语义而非二次包装。
        Assert.Multiple(() =>
        {
            Assert.That(error.Message, Does.Contain("read-root readiness"));
            Assert.That(inner.ReceivedExecution, Is.Null);
            Assert.That(windowLock.AcquireCalls, Is.EqualTo(1));
            Assert.That(restore.RefreshPayloads, Has.Count.EqualTo(1));
            Assert.That(disabled, Is.False);
        });
    }

    // ---------------------------------------------------------------- 拒读刷新门（恢复/退出串行化）

    [Test]
    public async Task ExecuteAsync_ExitRestoreRefresh_RunsInsideDenyReadGate()
    {
        using var home     = new SandboxTestHome();
        var       trace    = new List<string>();
        var       gate     = new ScriptedDenyReadGate(trace);
        var       restore  = new FakeSetupInvoker();
        var (coordinator, inner, _, windowLock, _) = CreateCoordinator(home, probe : ProvisionedV7,
                                                                       restore : new TracedSetupInvoker(restore, trace),
                                                                       windowLock : new ScriptedWindowLock(trace),
                                                                       innerBackend : new FakeProcessExecutionBackend
                                                                       {
                                                                           ExitCode = 0,
                                                                           Trace    = trace
                                                                       },
                                                                       postRefreshValidation : (_, _) =>
                                                                       {
                                                                           trace.Add("validate");
                                                                           return Task.CompletedTask;
                                                                       },
                                                                       denyReadRefreshGate : gate);

        var (stdout, stderr) = Captures();
        var result = await coordinator.ExecuteAsync(AuthorizedPlan(home), stdout, stderr, CancellationToken.None);

        // 退出 refresh 在门内：窗口 acquire → inner → 门 acquire → restore → 门 release；
        // 门按 home 键控并使用缺省门期限。
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            TestAssert.NotNull(inner.ReceivedExecution);
            Assert.That(trace, Is.EqualTo(["acquire", "inner", "deny-gate-acquire", "restore", "deny-gate-release"]));
            Assert.That(gate.LastHome, Is.EqualTo(home.Components.SandboxHome));
            Assert.That(gate.LastTimeout, Is.EqualTo(GlobalDenyReadRefreshGate.DefaultAcquireTimeout));
            Assert.That(windowLock.AcquireCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ExecuteAsync_RecoveryRefresh_RunsInsideDenyReadGate()
    {
        using var home = new SandboxTestHome();
        var       trace   = new List<string>();
        var       restore = new FakeSetupInvoker();
        var (coordinator, inner, _, windowLock, store) = CreateCoordinator(home, probe : ProvisionedV7,
                                                                           restore : new TracedSetupInvoker(restore, trace),
                                                                           windowLock : new ScriptedWindowLock(trace),
                                                                           innerBackend : new FakeProcessExecutionBackend
                                                                           {
                                                                               ExitCode = 0,
                                                                               Trace    = trace
                                                                           },
                                                                           postRefreshValidation : (_, _) =>
                                                                           {
                                                                               trace.Add("validate");
                                                                               return Task.CompletedTask;
                                                                           },
                                                                           denyReadRefreshGate : new ScriptedDenyReadGate(trace));
        await store.DisableAsync("restore failed: earlier window", CancellationToken.None);

        var (stdout, stderr) = Captures();
        var result = await coordinator.ExecuteAsync(AuthorizedPlan(home), stdout, stderr, CancellationToken.None);
        var (disabled, _) = await store.ReadAsync(CancellationToken.None);

        // 恢复 refresh 在门内（恢复段与退出段都持有门）：acquire → 门 acquire → restore →
        // 门 release → validate → inner → 门 acquire → restore → 门 release。
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            TestAssert.NotNull(inner.ReceivedExecution);
            Assert.That(disabled, Is.False);
            Assert.That(windowLock.AcquireCalls, Is.EqualTo(1));
            Assert.That(trace, Is.EqualTo(["acquire",
                                           "deny-gate-acquire", "restore", "deny-gate-release",
                                           "validate", "inner",
                                           "deny-gate-acquire", "restore", "deny-gate-release"]));
        });
    }

    [Test]
    public async Task ExecuteAsync_DenyGateTimeoutDuringRecovery_DisablesAndKeepsState()
    {
        using var home = new SandboxTestHome();
        var gate = new ScriptedDenyReadGate { Script = () => Task.FromResult<ISandboxDenyReadRefreshGateHandle?>(null) };
        var restore = new FakeSetupInvoker();
        var (coordinator, inner, _, _, store) = CreateCoordinator(home, probe : ProvisionedV7, restore : restore,
                                                                  denyReadRefreshGate : gate);
        await store.DisableAsync("restore failed: earlier window", CancellationToken.None);

        var (stdout, stderr) = Captures();
        var blocked = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() =>
                          coordinator.ExecuteAsync(AuthorizedPlan(home), stdout, stderr, CancellationToken.None));
        var (disabled, reason) = await store.ReadAsync(CancellationToken.None);

        // 门等待失败按既有恢复失败语义转禁用：命令未启动、禁用状态原样保留，
        // 错误注明恢复未能获得拒读刷新门（门等待超时）。
        Assert.Multiple(() =>
        {
            TestAssert.Contains("Authorized execution is disabled", blocked!.Message, StringComparison.Ordinal);
            TestAssert.Contains("recovery attempt failed", blocked.Message, StringComparison.Ordinal);
            TestAssert.Contains("deny-read refresh gate", blocked.Message, StringComparison.Ordinal);
            Assert.That(inner.ReceivedExecution, Is.Null);
            Assert.That(disabled, Is.True);
            TestAssert.Contains("earlier window", reason!, StringComparison.Ordinal);
            Assert.That(restore.RefreshPayloads, Is.Empty);
            Assert.That(gate.AcquireCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ExecuteAsync_DenyGateTimeoutDuringExitRestore_DisablesSubject()
    {
        using var home = new SandboxTestHome();
        var gate = new ScriptedDenyReadGate { Script = () => Task.FromResult<ISandboxDenyReadRefreshGateHandle?>(null) };
        var restore = new FakeSetupInvoker();
        var (coordinator, inner, _, _, store) = CreateCoordinator(home, probe : ProvisionedV7, restore : restore,
                                                                  denyReadRefreshGate : gate);

        var (stdout, stderr) = Captures();
        var result = await coordinator.ExecuteAsync(AuthorizedPlan(home), stdout, stderr, CancellationToken.None);
        var (disabled, reason) = await store.ReadAsync(CancellationToken.None);

        // 命令已完成、结果如实返回；退出 refresh 未能获得门按既有失败语义持久化禁用，
        // 授权组停留获批状态、禁用后续授权执行，禁用原因注明门等待超时。
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            TestAssert.NotNull(inner.ReceivedExecution);
            Assert.That(disabled, Is.True);
            TestAssert.Contains("deny-read refresh gate", reason!, StringComparison.Ordinal);
            Assert.That(restore.RefreshPayloads, Is.Empty);
            Assert.That(gate.AcquireCalls, Is.EqualTo(1));
            Assert.That(gate.LastTimeout, Is.EqualTo(GlobalDenyReadRefreshGate.DefaultAcquireTimeout));
        });
    }

    [Test]
    public async Task ExecuteAsync_RecoveryRefreshHelperExitUncertain_HoldsGateUntilConfirmedAndDisables()
    {
        using var home = new SandboxTestHome();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restore = new FakeSetupInvoker
        {
            ThrowOnRefresh = new SandboxRefreshExitUncertainException(
                "The sandbox setup helper did not finish refreshing ACLs within 5s. The helper exit state is unknown; " +
                "it may still be writing deny-read bookkeeping.", pending.Task, null)
        };
        var gate = new ScriptedDenyReadGate();
        var (coordinator, inner, _, _, store) = CreateCoordinator(home, probe : ProvisionedV7, restore : restore,
                                                                  denyReadRefreshGate : gate);
        await store.DisableAsync("restore failed: earlier window", CancellationToken.None);

        var (stdout, stderr) = Captures();
        var blocked = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() =>
                          coordinator.ExecuteAsync(AuthorizedPlan(home), stdout, stderr, CancellationToken.None));
        var (disabled, reason) = await store.ReadAsync(CancellationToken.None);

        // 退出未知：门经 ReleaseWhenConfirmed 移交而非立即归还（Dispose 未被调）；恢复失败按
        // 既有语义转禁用（拒绝信息携带退出未知上下文），持久化禁用原因原样保留。
        var handle = TestAssert.NotNull(gate.LastHandle);
        Assert.Multiple(() =>
        {
            TestAssert.Contains("Authorized execution is disabled", blocked!.Message, StringComparison.Ordinal);
            TestAssert.Contains("exit state is unknown", blocked.Message, StringComparison.Ordinal);
            Assert.That(inner.ReceivedExecution, Is.Null);
            Assert.That(disabled, Is.True);
            TestAssert.Contains("earlier window", reason!, StringComparison.Ordinal);
            Assert.That(handle.ConfirmationTasks, Has.Count.EqualTo(1));
            Assert.That(handle.DisposeCalls, Is.EqualTo(0));
            Assert.That(gate.AcquireCalls, Is.EqualTo(1));
        });

        // 确认任务由门侧消费：这里只完成它模拟 helper 退出已确认。
        pending.TrySetResult(true);
    }

    [Test]
    public async Task ExecuteAsync_ExitRestoreRefreshHelperExitUncertain_HoldsGateUntilConfirmedAndDisables()
    {
        using var home = new SandboxTestHome();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restore = new FakeSetupInvoker
        {
            ThrowOnRefresh = new SandboxRefreshExitUncertainException(
                "The sandbox setup helper did not finish refreshing ACLs within 5s. The helper exit state is unknown; " +
                "it may still be writing deny-read bookkeeping.", pending.Task, null)
        };
        var gate = new ScriptedDenyReadGate();
        var (coordinator, inner, _, _, store) = CreateCoordinator(home, probe : ProvisionedV7, restore : restore,
                                                                  denyReadRefreshGate : gate);

        var (stdout, stderr) = Captures();
        var result = await coordinator.ExecuteAsync(AuthorizedPlan(home), stdout, stderr, CancellationToken.None);
        var (disabled, reason) = await store.ReadAsync(CancellationToken.None);

        // 命令已完成、结果如实返回；退出 refresh 退出未知按既有失败语义持久化禁用（落盘语义
        // 不变），门经 ReleaseWhenConfirmed 移交而非立即归还（Dispose 未被调）。
        var handle = TestAssert.NotNull(gate.LastHandle);
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            TestAssert.NotNull(inner.ReceivedExecution);
            Assert.That(disabled, Is.True);
            TestAssert.Contains("exit state is unknown", reason!, StringComparison.Ordinal);
            Assert.That(handle.ConfirmationTasks, Has.Count.EqualTo(1));
            Assert.That(handle.DisposeCalls, Is.EqualTo(0));
            Assert.That(gate.AcquireCalls, Is.EqualTo(1));
        });

        pending.TrySetResult(true);
    }

    // ---------------------------------------------------------------- 装置

    /// <summary>授权执行使用的 deny 条目路径。 The deny entry path an authorized execution reads.</summary>
    private static string DenyReadPath(SandboxTestHome home) =>
        Path.Combine(home.Components.SandboxHome, "sensitive");

    /// <summary>固定 v7 部署代探测。 A probe reporting the authorized generation.</summary>
    private static Task<uint> ProvisionedV7(CancellationToken cancellationToken) =>
        Task.FromResult<uint>(WindowsSandboxComponents.AuthorizedSetupVersion);

    /// <summary>
    ///     构造协调器与其协作件（假锁/假 refresh/假后端/真实文件禁用存储）；Restore 返回传入的调用器
    ///     原样引用。后刷新校验默认 stub（无真实 DACL 依赖），共享校验行为由定向用例注入；拒读刷新门
    ///     默认进程实现（按 home 键控的 Global 互斥体），注入可替换。
    ///     Builds the coordinator with its collaborators (fake lock, fake refresh, fake backend, and
    ///     the real-file disable store); Restore returns the passed invoker as-is. The post-refresh
    ///     validation defaults to a stub (no real DACL dependency) with behavior injected by targeted
    ///     cases; the deny-read refresh gate defaults to the process-backed implementation (the
    ///     home-keyed Global mutex), replaceable by injection.
    /// </summary>
    private static (AuthorizedReadCoordinator Coordinator, FakeProcessExecutionBackend Inner,
        ISandboxSetupInvoker Restore, ScriptedWindowLock Lock, AuthorizedReadDisableFile Store) CreateCoordinator(
        SandboxTestHome                       home,
        Func<CancellationToken, Task<uint>>?  probe        = null,
        IAuthorizedReadWindowLock?            windowLock   = null,
        ISandboxSetupInvoker?                 restore      = null,
        IProcessExecutionBackend?             innerBackend = null,
        Func<SandboxSetupPayload, CancellationToken, Task>? postRefreshValidation = null,
        ISandboxDenyReadRefreshGate?          denyReadRefreshGate   = null,
        TimeSpan?                             denyReadGateTimeout   = null)
    {
        Directory.CreateDirectory(DenyReadPath(home));
        var policy = new SandboxIsolationPolicy
        {
            Kind                = SandboxPolicyKind.WorkspaceWrite,
            WorkspaceRoot       = home.Components.SandboxHome,
            SpawnProfile        = new SandboxPermissionProfile
            {
                FileSystem = new SandboxFileSystemRestriction { Entries = [] },
                Network    = "restricted"
            },
            EffectiveReadRoots  = [],
            EffectiveWriteRoots = [home.Components.SandboxHome],
            DenyWritePaths      = [],
            TempWriteRoots      = [],
            DenyReadPaths       = [DenyReadPath(home)]
        };
        var inner          = (FakeProcessExecutionBackend)(innerBackend ?? new FakeProcessExecutionBackend
        {
            // 成功执行的假后端以退出码 0 表示完成；注入的异常后端（回收未知等）不受影响。
            ExitCode = 0
        });
        var restoreInvoker = restore ?? new FakeSetupInvoker();
        var lockGate       = (ScriptedWindowLock)(windowLock ?? new ScriptedWindowLock());
        var store          = new AuthorizedReadDisableFile(home.Components);
        var coordinator = new AuthorizedReadCoordinator(inner, home.Components, policy,
                                                        restoreInvoker : restoreInvoker,
                                                        windowLock : lockGate,
                                                        disableStore : store,
                                                        capabilityProbe : probe,
                                                        postRefreshValidation : postRefreshValidation ??
                                                                                ((_, _) => Task.CompletedTask),
                                                        denyReadRefreshGate : denyReadRefreshGate,
                                                        denyReadGateTimeout : denyReadGateTimeout);
        return (coordinator, inner, restoreInvoker, lockGate, store);
    }

    private static PreparedProcessExecution AuthorizedPlan(SandboxTestHome home) =>
        NormalPlan(home) with
        {
            GrantScope = new SandboxGrantScope
            {
                ReadRoots          = [DenyReadPath(home)],
                WriteRoots         = [],
                MetadataWritePaths = []
            }
        };

    private static PreparedProcessExecution NormalPlan(SandboxTestHome home) => new("cmd.exe", [],
        home.Components.SandboxHome, 10, false, null)
    {
        GrantScope = SandboxGrantScope.Empty
    };

    private static (RecordingOutputCapture Stdout, RecordingOutputCapture Stderr) Captures() =>
        (new RecordingOutputCapture(), new RecordingOutputCapture());
}

/// <summary>脚本化窗口锁：按 Script 决定获取结果并记录调用。 A scripted window lock: Script decides each acquisition, calls are recorded.</summary>
internal sealed class ScriptedWindowLock(List<string>? trace = null) : IAuthorizedReadWindowLock
{
    public Func<Task<IAuthorizedReadWindowHandle?>> Script { get; set; } =
        () => Task.FromResult<IAuthorizedReadWindowHandle?>(new ScriptedWindowHandle());

    public int AcquireCalls { get; private set; }

    public TimeSpan? LastTimeout { get; private set; }

    /// <summary>获取事件写入的时序列表；可与其他假件共享以断言跨组件顺序。 The trace list acquisition events append to; shareable with other fakes for cross-component ordering.</summary>
    public List<string> Trace { get; } = trace ?? [];

    public Task<IAuthorizedReadWindowHandle?> TryAcquireAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        // 与真实窗口锁同一契约：等待尊重取消，已取消的等待抛出取消而非返回句柄。
        cancellationToken.ThrowIfCancellationRequested();
        AcquireCalls++;
        LastTimeout = timeout;
        Trace.Add("acquire");
        return Script();
    }
}

/// <summary>恢复 refresh 的时序记录包装：事件写入与窗口锁共享的序列表。 A traced wrapper around a setup invoker appending restore events to a shared sequence.</summary>
internal sealed class TracedSetupInvoker(ISandboxSetupInvoker inner, List<string> trace) : ISandboxSetupInvoker
{
    public async Task RefreshAsync(SandboxSetupPayload payload, CancellationToken cancellationToken)
    {
        trace.Add("restore");
        await inner.RefreshAsync(payload, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>记录 Dispose 次数的窗口句柄。 A window handle recording its Dispose calls.</summary>
internal sealed class ScriptedWindowHandle : IAuthorizedReadWindowHandle
{
    public int Disposed { get; private set; }

    public void Dispose() => Disposed++;
}

/// <summary>脚本化拒读刷新门：按 Script 决定获取结果，获取/释放事件写入共享时序。 A scripted deny-read refresh gate: Script decides each acquisition; acquire/release events append to the shared trace.</summary>
internal sealed class ScriptedDenyReadGate : ISandboxDenyReadRefreshGate
{
    // 默认脚本须在构造期捕获 this（属性初始化器上下文拿不到 this），故移入构造函数。
    public ScriptedDenyReadGate(List<string>? trace = null)
    {
        Trace  = trace ?? [];
        Script = () => Task.FromResult<ISandboxDenyReadRefreshGateHandle?>(new ScriptedDenyGateHandle(this));
    }

    public Func<Task<ISandboxDenyReadRefreshGateHandle?>> Script { get; set; }

    public int AcquireCalls { get; private set; }

    public TimeSpan? LastTimeout { get; private set; }

    public string? LastHome { get; private set; }

    /// <summary>最近一次脚本返回的脚本化句柄；供断言确认释放调用与 Dispose 次数。 The scripted handle returned by the latest acquisition; lets tests assert confirmed-release calls and Dispose counts.</summary>
    public ScriptedDenyGateHandle? LastHandle { get; private set; }

    /// <summary>获取/释放事件写入的时序列表；可与其他假件共享以断言跨组件顺序。 The trace list acquire/release events append to; shareable with other fakes for cross-component ordering.</summary>
    public List<string> Trace { get; }

    public async Task<ISandboxDenyReadRefreshGateHandle?> TryAcquireAsync(string sandboxHome, TimeSpan timeout,
                                                                          CancellationToken cancellationToken)
    {
        // 与真实门同一契约：等待尊重取消，已取消的等待抛出取消而非返回句柄。
        cancellationToken.ThrowIfCancellationRequested();
        AcquireCalls++;
        LastTimeout = timeout;
        LastHome    = sandboxHome;
        Trace.Add("deny-gate-acquire");
        var handle = await Script().ConfigureAwait(false);
        if (handle is ScriptedDenyGateHandle scripted) LastHandle = scripted;
        return handle;
    }
}

/// <summary>
///     把释放事件写回所属门时序的门句柄；确认释放记录移交的任务而不归还门，Dispose 计数供
///     fail-closed 断言。
///     A gate handle appending its release to the owning gate's trace; the confirmed release
///     records the handed-over task without returning the gate, and the Dispose count backs the
///     fail-closed assertions.
/// </summary>
internal sealed class ScriptedDenyGateHandle(ScriptedDenyReadGate owner) : ISandboxDenyReadRefreshGateHandle
{
    public int DisposeCalls { get; private set; }

    /// <summary>经 ReleaseWhenConfirmed 移交的确认任务列表。 The confirmation tasks handed over through ReleaseWhenConfirmed.</summary>
    public List<Task<bool>> ConfirmationTasks { get; } = [];

    public void Dispose()
    {
        DisposeCalls++;
        owner.Trace.Add("deny-gate-release");
    }

    public void ReleaseWhenConfirmed(Task<bool> helperExitConfirmation)
    {
        ConfirmationTasks.Add(helperExitConfirmation);
        owner.Trace.Add("deny-gate-release-when-confirmed");
    }
}

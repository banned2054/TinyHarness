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

    [Test]
    public async Task ExecuteAsync_UncertainAfterCommandCancelled_StillDisablesAndRestoresDeny()
    {
        using var home    = new SandboxTestHome();
        using var cts     = new CancellationTokenSource();
        var       restore = new FakeSetupInvoker();
        var       store   = new AuthorizedReadDisableFile(home.Components, home.Components.CredentialsDirectory);
        var       inner   = new CancelThenThrowUncertainBackend(cts);
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
        var coordinator = new AuthorizedReadCoordinator(inner, home.Components, policy,
                                                        restoreInvoker : restore,
                                                        windowLock : new ScriptedWindowLock(),
                                                        disableStore : store,
                                                        capabilityProbe : ProvisionedV7,
                                                        postRefreshValidation : (_, _) => Task.CompletedTask);

        var (stdout, stderr) = Captures();
        await TestAssert.ThrowsAsync<ToolExecutionUncertainException>(
            () => coordinator.ExecuteAsync(AuthorizedPlan(home), stdout, stderr, cts.Token));
        var (disabled, reason) = await store.ReadAsync(CancellationToken.None);

        // 即使命令令牌已取消，LeaveWindowAsync 仍必须以独立令牌落盘禁用并完成拒读恢复刷新。
        Assert.Multiple(() =>
        {
            Assert.That(disabled, Is.True);
            TestAssert.Contains("process reclamation could not be confirmed", reason!, StringComparison.Ordinal);
            Assert.That(restore.RefreshPayloads, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task ExecuteAsync_QueuedRequestReReadsDisableStateInsideWindowLock()
    {
        using var home    = new SandboxTestHome();
        var       restore = new FakeSetupInvoker();
        var       store   = new AuthorizedReadDisableFile(home.Components, home.Components.CredentialsDirectory);
        var       bootId  = 100;
        var       firstInsideWindow = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var       releaseFirst      = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var       secondQueued      = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var       windowLock        = new BlockingWindowLock(secondQueued);
        var       inner             = new ControlledUncertainBackend(firstInsideWindow, releaseFirst);
        var       coordinator       = CreateCoordinatorWithStore(home, inner, restore, windowLock, store, () => bootId);

        // 请求 A 进入窗口后阻塞在执行中；请求 B 随后到达并在窗口锁外读到“未禁用”，然后排队等锁。
        var (stdoutA, stderrA) = Captures();
        var taskA = coordinator.ExecuteAsync(AuthorizedPlan(home), stdoutA, stderrA, CancellationToken.None);
        await firstInsideWindow.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var (stdoutB, stderrB) = Captures();
        var taskB = coordinator.ExecuteAsync(AuthorizedPlan(home), stdoutB, stderrB, CancellationToken.None);
        await secondQueued.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 放行请求 A，使其抛出 ToolExecutionUncertainException 并落盘禁用、释放窗口锁。
        releaseFirst.TrySetResult(true);
        await TestAssert.ThrowsAsync<ToolExecutionUncertainException>(() => taskA);

        // 请求 B 拿到锁后必须复读禁用状态并拒绝执行，绝不能凭锁外旧读结果打开授权窗口。
        var blockedB = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() => taskB);
        Assert.Multiple(() =>
        {
            TestAssert.Contains("cannot be auto-cleared by an ACL refresh", blockedB!.Message, StringComparison.Ordinal);
            Assert.That(inner.CallCount, Is.EqualTo(1), "queued request B must never invoke the inner backend");
        });
    }

    [Test]
    public async Task ExecuteAsync_ReclamationUncertainDisable_BlocksOnSameBootAndRecoversOnlyAfterReboot()
    {
        using var home    = new SandboxTestHome();
        var       restore = new FakeSetupInvoker();
        var       store   = new AuthorizedReadDisableFile(home.Components, home.Components.CredentialsDirectory);
        var       bootId  = 200;

        var failingInner = new FakeProcessExecutionBackend
        {
            ThrowOnExecute = new ToolExecutionUncertainException("outer job still has active process")
        };
        var first = CreateCoordinatorWithStore(home, failingInner, restore, new ScriptedWindowLock(), store, () => bootId);
        var (stdout1, stderr1) = Captures();
        await TestAssert.ThrowsAsync<ToolExecutionUncertainException>(
            () => first.ExecuteAsync(AuthorizedPlan(home), stdout1, stderr1, CancellationToken.None));

        var persisted = await store.ReadStateAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(persisted.Disabled, Is.True);
            Assert.That(persisted.Kind, Is.EqualTo(AuthorizedReadDisableKind.ReclamationUncertain));
            Assert.That(persisted.BootId, Is.EqualTo(200));
        });

        // 同一开机周期（BootId 不变）下即使新建协调器且 restore 可成功，也绝不能仅凭 ACL 刷新解禁或打开窗口。
        var secondInner  = new FakeProcessExecutionBackend { ExitCode = 0 };
        var secondLock   = new ScriptedWindowLock();
        var second       = CreateCoordinatorWithStore(home, secondInner, restore, secondLock, store, () => bootId);
        var (stdout2, stderr2) = Captures();
        var blocked = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(
            () => second.ExecuteAsync(AuthorizedPlan(home), stdout2, stderr2, CancellationToken.None));
        var stillDisabled = await store.ReadStateAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            TestAssert.Contains("cannot be auto-cleared by an ACL refresh", blocked!.Message, StringComparison.Ordinal);
            Assert.That(secondInner.ReceivedExecution, Is.Null);
            Assert.That(secondLock.AcquireCalls, Is.EqualTo(0));
            Assert.That(stillDisabled.Disabled, Is.True);
        });

        // 系统重启（BootId 变化）证明旧授权进程已不存在：此时允许持锁执行恢复 refresh 并在成功后解禁放行。
        bootId = 201;
        var thirdInner = new FakeProcessExecutionBackend { ExitCode = 0 };
        var thirdLock  = new ScriptedWindowLock();
        var third      = CreateCoordinatorWithStore(home, thirdInner, restore, thirdLock, store, () => bootId);
        var (stdout3, stderr3) = Captures();
        var result  = await third.ExecuteAsync(AuthorizedPlan(home), stdout3, stderr3, CancellationToken.None);
        var cleared = await store.ReadStateAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            TestAssert.NotNull(thirdInner.ReceivedExecution);
            Assert.That(thirdLock.AcquireCalls, Is.EqualTo(1));
            Assert.That(cleared.Disabled, Is.False);
        });
    }

    [Test]
    public async Task ExecuteAsync_UncertainWithFailedRestore_DoesNotDowngradeDisableKindToRestoreFailed()
    {
        using var home    = new SandboxTestHome();
        var       restore = new FakeSetupInvoker { ThrowOnRefresh = new InvalidOperationException("restore failed too") };
        var       store   = new AuthorizedReadDisableFile(home.Components, home.Components.CredentialsDirectory);
        var       inner   = new FakeProcessExecutionBackend
        {
            ThrowOnExecute = new ToolExecutionUncertainException("runner reclamation unconfirmed")
        };
        var coordinator = CreateCoordinatorWithStore(home, inner, restore, new ScriptedWindowLock(), store, () => 300);

        var (stdout, stderr) = Captures();
        await TestAssert.ThrowsAsync<ToolExecutionUncertainException>(
            () => coordinator.ExecuteAsync(AuthorizedPlan(home), stdout, stderr, CancellationToken.None));

        var state = await store.ReadStateAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.Disabled, Is.True);
            Assert.That(state.Kind, Is.EqualTo(AuthorizedReadDisableKind.ReclamationUncertain));
            Assert.That(state.BootId, Is.EqualTo(300));
            TestAssert.Contains("restore refresh also failed", state.Reason!, StringComparison.Ordinal);
        });
    }

    [Test]
    public async Task ExecuteAsync_PreWindowGuardPersistsBeforeInnerAndClearsAfterRestore()
    {
        using var home    = new SandboxTestHome();
        var       restore = new FakeSetupInvoker();
        var       store   = new AuthorizedReadDisableFile(home.Components, home.Components.CredentialsDirectory);
        AuthorizedReadDisableState? observedInsideWindow = null;
        var inner = new CallbackBackend(async () =>
        {
            // 窗口已打开、内层执行期间，磁盘上必须已存在 ReclamationUncertain + BootId WAL 守卫。
            observedInsideWindow = await store.ReadStateAsync(CancellationToken.None);
            return new ProcessExecutionResult(0, false, null);
        });
        var coordinator = CreateCoordinatorWithStore(home, inner, restore, new ScriptedWindowLock(), store, () => 500);

        var (stdout, stderr) = Captures();
        var result = await coordinator.ExecuteAsync(AuthorizedPlan(home), stdout, stderr, CancellationToken.None);
        var afterRestore = await store.ReadStateAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            TestAssert.NotNull(observedInsideWindow);
            Assert.That(observedInsideWindow!.Disabled, Is.True);
            Assert.That(observedInsideWindow.Kind, Is.EqualTo(AuthorizedReadDisableKind.WindowOpen));
            Assert.That(observedInsideWindow.BootId, Is.EqualTo(500));
            Assert.That(afterRestore.Disabled, Is.False);
            Assert.That(File.Exists(store.StatePath), Is.False);
        });
    }

    [Test]
    public async Task ExecuteAsync_LeftoverWindowOpenGuardAfterHostCrash_BlocksOnSameBootAndRecoversAfterReboot()
    {
        using var home    = new SandboxTestHome();
        var       restore = new FakeSetupInvoker();
        var       store   = new AuthorizedReadDisableFile(home.Components, home.Components.CredentialsDirectory);
        var       bootId  = 600;

        // 模拟前宿主打开窗口前写入 WindowOpen 后崩溃：下一进程拿到锁后复读到 WindowOpen，同开机周期必须拒绝开窗。
        await store.DisableAsync("authorized-read window was opened without confirmed completion",
                                 AuthorizedReadDisableKind.WindowOpen, bootId, CancellationToken.None);

        var sameBootInner = new FakeProcessExecutionBackend { ExitCode = 0 };
        var sameBoot      = CreateCoordinatorWithStore(home, sameBootInner, restore, new ScriptedWindowLock(), store,
                                                       () => bootId);
        var (stdout1, stderr1) = Captures();
        var blocked = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(
            () => sameBoot.ExecuteAsync(AuthorizedPlan(home), stdout1, stderr1, CancellationToken.None));
        Assert.Multiple(() =>
        {
            TestAssert.Contains("cannot be auto-cleared by an ACL refresh", blocked!.Message, StringComparison.Ordinal);
            Assert.That(sameBootInner.ReceivedExecution, Is.Null);
        });

        // 系统重启（BootId 变化）后允许持锁恢复刷新并清除遗留的 WindowOpen 守卫。
        bootId = 601;
        var afterRebootInner = new FakeProcessExecutionBackend { ExitCode = 0 };
        var afterReboot      = CreateCoordinatorWithStore(home, afterRebootInner, restore, new ScriptedWindowLock(),
                                                          store, () => bootId);
        var (stdout2, stderr2) = Captures();
        var result  = await afterReboot.ExecuteAsync(AuthorizedPlan(home), stdout2, stderr2, CancellationToken.None);
        var cleared = await store.ReadStateAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            TestAssert.NotNull(afterRebootInner.ReceivedExecution);
            Assert.That(cleared.Disabled, Is.False);
        });
    }

    [Test]
    public async Task ExecuteAsync_CrossHomeCannotAutoClearDisableCreatedByOriginatingHome()
    {
        using var root      = new TestTempDir();
        var       sharedDir = Path.Combine(root.Root, "shared-auth-state");
        using var homeA     = new SandboxTestHome();
        using var homeB     = new SandboxTestHome();
        var       storeA    = new AuthorizedReadDisableFile(homeA.Components, sharedDir);
        var       storeB    = new AuthorizedReadDisableFile(homeB.Components, sharedDir);

        // home A 产生 RestoreFailed 禁用（授权组簿记在 home A 下）。
        await storeA.DisableAsync("restore failed in home A", AuthorizedReadDisableKind.RestoreFailed, null,
                                  CancellationToken.None);

        // home B 共享同一禁用文件，但绝不能通过刷新 home B 清除 home A 的禁用状态。
        var restoreB     = new FakeSetupInvoker();
        var innerB       = new FakeProcessExecutionBackend { ExitCode = 0 };
        var coordinatorB = CreateCoordinatorWithStore(homeB, innerB, restoreB, new ScriptedWindowLock(), storeB,
                                                      () => 700);
        var (stdoutB, stderrB) = Captures();
        var blockedB = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(
            () => coordinatorB.ExecuteAsync(AuthorizedPlan(homeB), stdoutB, stderrB, CancellationToken.None));
        var stillDisabled = await storeB.ReadStateAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            TestAssert.Contains("originating sandbox home", blockedB!.Message, StringComparison.Ordinal);
            Assert.That(innerB.ReceivedExecution, Is.Null);
            Assert.That(restoreB.RefreshPayloads, Is.Empty);
            Assert.That(stillDisabled.Disabled, Is.True);
        });

        // 回到产生禁用的源 home A：允许持锁执行恢复刷新并清除共享禁用。
        var restoreA     = new FakeSetupInvoker();
        var innerA       = new FakeProcessExecutionBackend { ExitCode = 0 };
        var coordinatorA = CreateCoordinatorWithStore(homeA, innerA, restoreA, new ScriptedWindowLock(), storeA,
                                                      () => 700);
        var (stdoutA, stderrA) = Captures();
        var resultA = await coordinatorA.ExecuteAsync(AuthorizedPlan(homeA), stdoutA, stderrA, CancellationToken.None);
        var cleared = await storeB.ReadStateAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(resultA.ExitCode, Is.EqualTo(0));
            TestAssert.NotNull(innerA.ReceivedExecution);
            Assert.That(cleared.Disabled, Is.False);
        });
    }

    [Test]
    public async Task ExecuteAsync_MergedDualReadPreservesEveryEntryExitConstraint()
    {
        using var root      = new TestTempDir();
        var       sharedDir = Path.Combine(root.Root, "shared-auth-state");
        using var home      = new SandboxTestHome();
        var       store     = new AuthorizedReadDisableFile(home.Components, sharedDir);
        var       bootId    = 801;

        // 共享文件记录上一开机周期（800）的 ReclamationUncertain，旧 home 文件记录当前开机周期（801）的 WindowOpen：
        // 合并后必须同时保留两份退出约束，当前周期（801）绝不能因共享文件 BootId=800 已过期而放行恢复。
        await store.DisableAsync("older boot uncertain", AuthorizedReadDisableKind.ReclamationUncertain, 800,
                                 CancellationToken.None);
        Directory.CreateDirectory(home.Components.CredentialsDirectory);
        await File.WriteAllTextAsync(
            store.LegacyHomeStatePath,
            """{"disabled":true,"reason":"current boot window open","kind":"window_open","boot_id":801}""");

        var restore     = new FakeSetupInvoker();
        var sameBootIn  = new FakeProcessExecutionBackend { ExitCode = 0 };
        var sameBoot    = CreateCoordinatorWithStore(home, sameBootIn, restore, new ScriptedWindowLock(), store,
                                                     () => bootId);
        var (stdout1, stderr1) = Captures();
        var blocked = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(
            () => sameBoot.ExecuteAsync(AuthorizedPlan(home), stdout1, stderr1, CancellationToken.None));
        Assert.Multiple(() =>
        {
            TestAssert.Contains("cannot be auto-cleared by an ACL refresh", blocked!.Message, StringComparison.Ordinal);
            Assert.That(sameBootIn.ReceivedExecution, Is.Null);
        });

        // 系统重启到 802 后，两份记录的退出约束均已满足，允许恢复并一并清除两处文件。
        bootId = 802;
        var nextBootIn = new FakeProcessExecutionBackend { ExitCode = 0 };
        var nextBoot   = CreateCoordinatorWithStore(home, nextBootIn, restore, new ScriptedWindowLock(), store,
                                                    () => bootId);
        var (stdout2, stderr2) = Captures();
        var result  = await nextBoot.ExecuteAsync(AuthorizedPlan(home), stdout2, stderr2, CancellationToken.None);
        var cleared = await store.ReadStateAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            TestAssert.NotNull(nextBootIn.ReceivedExecution);
            Assert.That(cleared.Disabled, Is.False);
            Assert.That(File.Exists(store.LegacyHomeStatePath), Is.False);
        });
    }

    [Test]
    public async Task ExecuteAsync_PreWindowGuardPersistFailure_FailsClosedBeforeInvokingInner()
    {
        using var home    = new SandboxTestHome();
        var       restore = new FakeSetupInvoker();
        var       store   = new FailingDisableStore(new IOException("disk full before window"), failAfterCalls : 0);
        var       inner   = new FakeProcessExecutionBackend { ExitCode = 0 };
        var coordinator = CreateCoordinatorWithStore(home, inner, restore, new ScriptedWindowLock(), store, () => 501);

        var (stdout, stderr) = Captures();
        var error = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(
            () => coordinator.ExecuteAsync(AuthorizedPlan(home), stdout, stderr, CancellationToken.None));

        Assert.Multiple(() =>
        {
            TestAssert.Contains("pre-window authorized-read guard", error!.Message, StringComparison.Ordinal);
            Assert.That(inner.ReceivedExecution, Is.Null, "inner backend must never run if pre-window guard fails");
            Assert.That(restore.RefreshPayloads, Is.Empty);
        });
    }

    [Test]
    public async Task ExecuteAsync_UncertainWithDisablePersistFailure_PreservesUncertainExceptionAndLatchesInstance()
    {
        using var home    = new SandboxTestHome();
        var       restore = new FakeSetupInvoker();
        // 第 1 次 DisableAsync（窗口前 WAL 落盘）成功，第 2 次（LeaveWindowAsync 写入具体不确定原因）失败。
        var       store   = new FailingDisableStore(new IOException("disk full while persisting disable state"),
                                                    failAfterCalls : 1);
        var       inner   = new ThrowOnceThenSucceedBackend(
            new ToolExecutionUncertainException("outer job still active"));
        var coordinator = CreateCoordinatorWithStore(home, inner, restore, new ScriptedWindowLock(), store, () => 400);

        var (stdout1, stderr1) = Captures();
        var uncertain = await TestAssert.ThrowsAsync<ToolExecutionUncertainException>(
            () => coordinator.ExecuteAsync(AuthorizedPlan(home), stdout1, stderr1, CancellationToken.None));

        Assert.Multiple(() =>
        {
            TestAssert.Contains("outer job still active", uncertain!.Message, StringComparison.Ordinal);
            TestAssert.Contains("disk full", uncertain.Message, StringComparison.Ordinal);
            Assert.That(uncertain.InnerException, Is.InstanceOf<AggregateException>());
            Assert.That(restore.RefreshPayloads, Has.Count.EqualTo(1),
                        "restore refresh must still run in finally even when disable persistence fails");
        });

        // 即使磁盘落盘失败，实例内存锁死也必须阻止同实例再次打开授权窗口。
        var (stdout2, stderr2) = Captures();
        var blocked = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(
            () => coordinator.ExecuteAsync(AuthorizedPlan(home), stdout2, stderr2, CancellationToken.None));
        Assert.Multiple(() =>
        {
            TestAssert.Contains("cannot be auto-cleared by an ACL refresh", blocked!.Message, StringComparison.Ordinal);
            Assert.That(inner.CallCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ExecuteAsync_JunctionDisabledHomeRecoversViaDirectFinalPathAndClearsSharedDisable()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var root         = new TestTempDir();
        var       sharedDir    = Path.Combine(root.Root, "shared-auth-state");
        var       actualHome   = Path.Combine(root.Root, "actual-home");
        var       junctionHome = Path.Combine(root.Root, "junction-home");
        var       actualComp   = SeedTestSandboxHome(actualHome);
        if (!SandboxRuntimeTempSlotTestSupport.TryCreateJunction(junctionHome, actualHome))
            Assert.Ignore("Directory junctions are unavailable on this machine.");

        var aliasComp = new WindowsSandboxComponents
        {
            SetupExecutablePath  = actualComp.SetupExecutablePath,
            RunnerExecutablePath = actualComp.RunnerExecutablePath,
            SandboxHome          = junctionHome
        };
        var storeAlias = new AuthorizedReadDisableFile(aliasComp, sharedDir);
        await storeAlias.DisableAsync("restore failed via junction", AuthorizedReadDisableKind.RestoreFailed, null,
                                      CancellationToken.None);

        var recorded      = await storeAlias.ReadStateAsync(CancellationToken.None);
        var expectedFinal = Path.TrimEndingDirectorySeparator(
            SandboxFinalPathResolver.TryGetFinalDirectoryPath(actualHome)!);
        Assert.That(recorded.SourceSandboxHome, Is.EqualTo(expectedFinal).IgnoreCase);

        // 同一真实目录改用直接物理路径恢复：SourceSandboxHome 与当前物理最终路径一致，允许恢复并清除共享禁用。
        var restore     = new FakeSetupInvoker();
        var inner       = new FakeProcessExecutionBackend { ExitCode = 0 };
        var storeDirect = new AuthorizedReadDisableFile(actualComp, sharedDir);
        var coordinator = CreateCoordinatorWithStoreForComponents(
            actualComp, inner, restore, new ScriptedWindowLock(), storeDirect, () => 710);

        var (stdout, stderr) = Captures();
        var result  = await coordinator.ExecuteAsync(AuthorizedPlanForHome(actualHome), stdout, stderr, CancellationToken.None);
        var cleared = await storeAlias.ReadStateAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            TestAssert.NotNull(inner.ReceivedExecution);
            Assert.That(restore.RefreshPayloads, Has.Count.EqualTo(2));
            Assert.That(cleared.Disabled, Is.False);
        });
    }

    [Test]
    public async Task ExecuteAsync_RepointedJunctionHomeIsRefusedFailClosedAndStaysDisabled()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var root         = new TestTempDir();
        var       sharedDir    = Path.Combine(root.Root, "shared-auth-state");
        var       actualHomeA  = Path.Combine(root.Root, "actual-home-a");
        var       actualHomeB  = Path.Combine(root.Root, "actual-home-b");
        var       junctionHome = Path.Combine(root.Root, "junction-home");
        var       compA        = SeedTestSandboxHome(actualHomeA);
        _ = SeedTestSandboxHome(actualHomeB);
        if (!SandboxRuntimeTempSlotTestSupport.TryCreateJunction(junctionHome, actualHomeA))
            Assert.Ignore("Directory junctions are unavailable on this machine.");

        var aliasComp = new WindowsSandboxComponents
        {
            SetupExecutablePath  = compA.SetupExecutablePath,
            RunnerExecutablePath = compA.RunnerExecutablePath,
            SandboxHome          = junctionHome
        };
        var storeAlias = new AuthorizedReadDisableFile(aliasComp, sharedDir);
        await storeAlias.DisableAsync("restore failed in home A", AuthorizedReadDisableKind.RestoreFailed, null,
                                      CancellationToken.None);

        // 将同一 junction 别名改指到另一个 home 目录 B：词法路径相同但物理最终路径已变，恢复必须 fail-closed 拒绝。
        Directory.Delete(junctionHome);
        Assert.That(SandboxRuntimeTempSlotTestSupport.TryCreateJunction(junctionHome, actualHomeB), Is.True);

        var restoreB     = new FakeSetupInvoker();
        var innerB       = new FakeProcessExecutionBackend { ExitCode = 0 };
        var coordinatorB = CreateCoordinatorWithStoreForComponents(
            aliasComp, innerB, restoreB, new ScriptedWindowLock(), storeAlias, () => 720);

        var (stdoutB, stderrB) = Captures();
        var blockedB = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(
            () => coordinatorB.ExecuteAsync(AuthorizedPlanForHome(junctionHome), stdoutB, stderrB, CancellationToken.None));
        var stillDisabled = await storeAlias.ReadStateAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            TestAssert.Contains("originating sandbox home", blockedB!.Message, StringComparison.Ordinal);
            Assert.That(innerB.ReceivedExecution, Is.Null);
            Assert.That(restoreB.RefreshPayloads, Is.Empty);
            Assert.That(stillDisabled.Disabled, Is.True);
        });

        // 悬空 junction 导致物理最终路径无法解析时，同样必须 fail-closed 拒绝，绝不回落词法路径放行。
        Directory.Delete(junctionHome);
        var innerDangling = new FakeProcessExecutionBackend { ExitCode = 0 };
        var coordinatorDangling = CreateCoordinatorWithStoreForComponents(
            aliasComp, innerDangling, restoreB, new ScriptedWindowLock(), storeAlias, () => 720);
        var (stdoutD, stderrD) = Captures();
        var blockedDangling = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(
            () => coordinatorDangling.ExecuteAsync(AuthorizedPlanForHome(junctionHome), stdoutD, stderrD,
                                                   CancellationToken.None));
        Assert.Multiple(() =>
        {
            TestAssert.Contains("could not be captured", blockedDangling!.Message, StringComparison.Ordinal);
            Assert.That(innerDangling.ReceivedExecution, Is.Null);
            Assert.That(restoreB.RefreshPayloads, Is.Empty);
        });
    }

    [Test]
    public async Task ExecuteAsync_GuardClearFailureAfterSuccess_PreservesResultAndLatchesFailClosed()
    {
        using var home      = new SandboxTestHome();
        var       restore   = new FakeSetupInvoker();
        var       realStore  = new AuthorizedReadDisableFile(home.Components, home.Components.CredentialsDirectory);
        var       store      = new ThrowOnClearDisableStore(realStore, new IOException("cannot remove pre-window guard"));
        var       firstInner = new FakeProcessExecutionBackend { ExitCode = 0 };
        var coordinator = CreateCoordinatorWithStore(
            home, firstInner, restore, new ScriptedWindowLock(), store, () => 900);

        var (stdout1, stderr1) = Captures();
        var result = await coordinator.ExecuteAsync(AuthorizedPlan(home), stdout1, stderr1, CancellationToken.None);
        var persistedAfterClearFail = await realStore.ReadStateAsync(CancellationToken.None);

        // ClearAsync 失败绝不能覆盖已确认的命令结果；磁盘保留 WindowOpen 守卫，同实例后续请求也必须 fail-closed。
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            TestAssert.NotNull(firstInner.ReceivedExecution);
            Assert.That(restore.RefreshPayloads, Has.Count.EqualTo(1));
            Assert.That(persistedAfterClearFail.Disabled, Is.True);
            Assert.That(persistedAfterClearFail.Kind, Is.EqualTo(AuthorizedReadDisableKind.WindowOpen));
        });

        var (stdout2, stderr2) = Captures();
        var blocked = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(
            () => coordinator.ExecuteAsync(AuthorizedPlan(home), stdout2, stderr2, CancellationToken.None));
        Assert.Multiple(() =>
        {
            TestAssert.Contains("cannot be auto-cleared by an ACL refresh", blocked!.Message, StringComparison.Ordinal);
            Assert.That(restore.RefreshPayloads, Has.Count.EqualTo(1),
                        "blocked follow-up request must not run restore or re-open the window");
        });
    }

    [Test]
    public async Task ExecuteAsync_RecreatedHomeAtSamePathWithNewFileId_IsRefusedFailClosedAndStaysDisabled()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var root      = new TestTempDir();
        var       sharedDir = Path.Combine(root.Root, "shared-auth-state");
        var       homePath  = Path.Combine(root.Root, "recreated-home");
        var       comp      = SeedTestSandboxHome(homePath);
        var       store     = new AuthorizedReadDisableFile(comp, sharedDir);
        await store.DisableAsync("restore failed before delete", AuthorizedReadDisableKind.RestoreFailed, null,
                                 CancellationToken.None);

        // 删除原目录并在完全相同路径重建新目录：路径字符串未变，但 128 位 FileId 已变，必须 fail-closed 拒绝恢复。
        Directory.Delete(homePath, recursive: true);
        _ = SeedTestSandboxHome(homePath);

        var restore     = new FakeSetupInvoker();
        var inner       = new FakeProcessExecutionBackend { ExitCode = 0 };
        var coordinator = CreateCoordinatorWithStoreForComponents(
            comp, inner, restore, new ScriptedWindowLock(), store, () => 730);

        var (stdout, stderr) = Captures();
        var blocked = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(
            () => coordinator.ExecuteAsync(AuthorizedPlanForHome(homePath), stdout, stderr, CancellationToken.None));
        var stillDisabled = await store.ReadStateAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            TestAssert.Contains("cannot be auto-cleared by an ACL refresh", blocked!.Message, StringComparison.Ordinal);
            Assert.That(inner.ReceivedExecution, Is.Null);
            Assert.That(restore.RefreshPayloads, Is.Empty);
            Assert.That(stillDisabled.Disabled, Is.True);
        });
    }

    [Test]
    public async Task ExecuteAsync_HomeReplacedInsideDenyReadGateAcquire_IsRefusedInsideGateWithoutRefreshing()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var root         = new TestTempDir();
        var       sharedDir    = Path.Combine(root.Root, "shared-auth-state");
        var       actualHomeA  = Path.Combine(root.Root, "actual-home-a");
        var       actualHomeB  = Path.Combine(root.Root, "actual-home-b");
        var       junctionHome = Path.Combine(root.Root, "junction-home");
        var       compA        = SeedTestSandboxHome(actualHomeA);
        _ = SeedTestSandboxHome(actualHomeB);
        if (!SandboxRuntimeTempSlotTestSupport.TryCreateJunction(junctionHome, actualHomeA))
            Assert.Ignore("Directory junctions are unavailable on this machine.");

        var aliasComp = new WindowsSandboxComponents
        {
            SetupExecutablePath  = compA.SetupExecutablePath,
            RunnerExecutablePath = compA.RunnerExecutablePath,
            SandboxHome          = junctionHome
        };
        var storeAlias = new AuthorizedReadDisableFile(aliasComp, sharedDir);
        await storeAlias.DisableAsync("restore failed before gate swap", AuthorizedReadDisableKind.RestoreFailed, null,
                                      CancellationToken.None);

        // 模拟进门前瞬间将 junction 改指到 home B：入口门时仍匹配 home A，但持刷新门后复核必须拦下，绝不下发刷新。
        var swapped = false;
        var gate    = new ScriptedDenyReadGate();
        gate.Script = () =>
        {
            if (!swapped)
            {
                swapped = true;
                Directory.Delete(junctionHome);
                Assert.That(OperatingSystem.IsWindows() &&
                            SandboxRuntimeTempSlotTestSupport.TryCreateJunction(junctionHome, actualHomeB),
                            Is.True);
            }

            return Task.FromResult<ISandboxDenyReadRefreshGateHandle?>(new ScriptedDenyGateHandle(gate));
        };

        var restore     = new FakeSetupInvoker();
        var inner       = new FakeProcessExecutionBackend { ExitCode = 0 };
        var coordinator = CreateCoordinatorWithStoreForComponents(
            aliasComp, inner, restore, new ScriptedWindowLock(), storeAlias, () => 740,
            denyReadRefreshGate: gate);

        var (stdout, stderr) = Captures();
        var blocked = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(
            () => coordinator.ExecuteAsync(AuthorizedPlanForHome(junctionHome), stdout, stderr, CancellationToken.None));
        var stillDisabled = await storeAlias.ReadStateAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            TestAssert.Contains("cannot be auto-cleared by an ACL refresh", blocked!.Message, StringComparison.Ordinal);
            Assert.That(swapped, Is.True);
            Assert.That(gate.AcquireCalls, Is.EqualTo(1));
            Assert.That(gate.LastHandle!.DisposeCalls, Is.EqualTo(1));
            Assert.That(restore.RefreshPayloads, Is.Empty);
            Assert.That(inner.ReceivedExecution, Is.Null);
            Assert.That(stillDisabled.Disabled, Is.True);
        });
    }

    [Test]
    public async Task ExecuteAsync_CancellationWithGuardClearFailure_PreservesCancellationAndLatchesFailClosed()
    {
        using var home      = new SandboxTestHome();
        var       restore   = new FakeSetupInvoker();
        var       realStore = new AuthorizedReadDisableFile(home.Components, home.Components.CredentialsDirectory);
        var       store     = new ThrowOnClearDisableStore(realStore, new IOException("cannot remove guard during cancel"));
        var       inner     = new ThrowOnceThenSucceedBackend(new OperationCanceledException("command cancelled"));
        var coordinator = CreateCoordinatorWithStore(
            home, inner, restore, new ScriptedWindowLock(), store, () => 910);

        var (stdout1, stderr1) = Captures();
        await Assert.CatchAsync<OperationCanceledException>(
            () => coordinator.ExecuteAsync(AuthorizedPlan(home), stdout1, stderr1, CancellationToken.None));
        var persistedAfterCancel = await realStore.ReadStateAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(restore.RefreshPayloads, Has.Count.EqualTo(1));
            Assert.That(persistedAfterCancel.Disabled, Is.True);
            Assert.That(persistedAfterCancel.Kind, Is.EqualTo(AuthorizedReadDisableKind.WindowOpen));
        });

        // 同一实例后续授权请求必须被内存锁死拦截，且不触发新的内层执行或刷新。
        var (stdout2, stderr2) = Captures();
        var blocked = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(
            () => coordinator.ExecuteAsync(AuthorizedPlan(home), stdout2, stderr2, CancellationToken.None));
        Assert.Multiple(() =>
        {
            TestAssert.Contains("cannot be auto-cleared by an ACL refresh", blocked!.Message, StringComparison.Ordinal);
            Assert.That(inner.CallCount, Is.EqualTo(1));
            Assert.That(restore.RefreshPayloads, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task ExecuteAsync_JunctionRepointedAfterPreCheck_RecoveryGateBookkeepingAndPayloadStayPinnedToVerifiedHome()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var root         = new TestTempDir();
        var       sharedDir    = Path.Combine(root.Root, "shared-auth-state");
        var       actualHomeA  = Path.Combine(root.Root, "actual-home-a");
        var       actualHomeB  = Path.Combine(root.Root, "actual-home-b");
        var       junctionHome = Path.Combine(root.Root, "junction-home");
        var       compA        = SeedTestSandboxHome(actualHomeA);
        _ = SeedTestSandboxHome(actualHomeB);
        if (!SandboxRuntimeTempSlotTestSupport.TryCreateJunction(junctionHome, actualHomeA))
            Assert.Ignore("Directory junctions are unavailable on this machine.");

        var canonicalHomeA = AuthorizedReadDisableFile.TryResolveCanonicalHomePath(actualHomeA, allowMissing: false)!;
        var canonicalHomeB = AuthorizedReadDisableFile.TryResolveCanonicalHomePath(actualHomeB, allowMissing: false)!;
        var legacyFileB    = Path.Combine(actualHomeB, ".sandbox-secrets", "authorized_read_state.json");
        await File.WriteAllTextAsync(
            legacyFileB,
            """{"disabled":true,"reason":"legacy disable in home B","kind":"reclamation_uncertain","boot_id":750}""");

        // 给 home A 与 home B 各自放入不同簿记条目：恢复必须只读取已验证源目录 home A 的簿记。
        var bookkeepingFromA = Path.Combine(junctionHome, "bookkeeping-from-a");
        var bookkeepingFromB = Path.Combine(junctionHome, "bookkeeping-from-b");
        File.WriteAllText(Path.Combine(actualHomeA, ".sandbox", "deny_read_acl_state.json"),
                          JsonSerializer.Serialize(new
                          {
                              principals = new Dictionary<string, string[]>
                              {
                                  ["S-1-5-21-1"] = [bookkeepingFromA]
                              }
                          }));
        File.WriteAllText(Path.Combine(actualHomeB, ".sandbox", "deny_read_acl_state.json"),
                          JsonSerializer.Serialize(new
                          {
                              principals = new Dictionary<string, string[]>
                              {
                                  ["S-1-5-21-1"] = [bookkeepingFromB]
                              }
                          }));

        var aliasComp = new WindowsSandboxComponents
        {
            SetupExecutablePath  = compA.SetupExecutablePath,
            RunnerExecutablePath = compA.RunnerExecutablePath,
            SandboxHome          = junctionHome
        };
        var storeAlias = new AuthorizedReadDisableFile(aliasComp, sharedDir);
        await storeAlias.DisableAsync("restore failed in home A before repoint",
                                      AuthorizedReadDisableKind.RestoreFailed, null, CancellationToken.None);

        var gate          = new ScriptedDenyReadGate();
        var repointed     = false;
        var deleteBlocked = false;
        var restore       = new FakeSetupInvoker();
        var callbackRestore = new CallbackSetupInvoker(restore, (payload, _) =>
        {
            if (!repointed)
            {
                repointed = true;
                // 模拟复核之后、helper 实际运行期间 junction 被改指到 home B：payload 与门已绑定物理 home A。
                Directory.Delete(junctionHome);
                Assert.That(OperatingSystem.IsWindows() &&
                            SandboxRuntimeTempSlotTestSupport.TryCreateJunction(junctionHome, actualHomeB),
                            Is.True);

                // 同时验证协调器在刷新期间持有的非删除共享句柄阻断对真实源目录 home A 的删除/改名。
                try
                {
                    Directory.Move(actualHomeA, actualHomeA + "-moved");
                }
                catch (IOException)
                {
                    deleteBlocked = true;
                }
            }

            return Task.CompletedTask;
        });

        AuthorizedReadWindowHome? observedWindowHome = null;
        var inner = new FakeProcessExecutionBackend { ExitCode = 0 };
        var windowRecordingInner = new CaptureWindowContextBackend(inner, home => observedWindowHome = home);
        var coordinator = CreateCoordinatorWithStoreForComponents(
            aliasComp, windowRecordingInner, callbackRestore, new ScriptedWindowLock(), storeAlias, () => 750,
            denyReadRefreshGate: gate);

        var (stdout, stderr) = Captures();
        var result = await coordinator.ExecuteAsync(AuthorizedPlanForHome(junctionHome), stdout, stderr,
                                                    CancellationToken.None);
        var storeA        = new AuthorizedReadDisableFile(compA, sharedDir);
        var afterRecovery = await storeA.ReadStateAsync(CancellationToken.None);

        // 持窗口锁后一次性锁定物理 home A；即使恢复期间别名改指 B，恢复、内层执行与退出恢复全程仍绑定 A，
        // 绝不绕过 B 的入口禁用检查；随后经已改指的别名发起新请求，必须被 B 的禁用记录阻断。
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            TestAssert.NotNull(observedWindowHome);
            Assert.That(observedWindowHome!.CanonicalPath, Is.EqualTo(canonicalHomeA).IgnoreCase);
            Assert.That(observedWindowHome.CanonicalPath, Is.Not.EqualTo(canonicalHomeB).IgnoreCase);
            Assert.That(repointed, Is.True);
            Assert.That(deleteBlocked, Is.True, "recovery must pin the verified source home against rename/delete");
            Assert.That(gate.AcquiredHomes, Is.EqualTo([canonicalHomeA, canonicalHomeA]).IgnoreCase,
                        "recovery and exit gates must both key on verified canonical home A, not the repointed alias");
            Assert.That(restore.RefreshPayloads, Has.Count.EqualTo(2));
            foreach (var payload in restore.RefreshPayloads)
            {
                Assert.That(payload.SandboxHome, Is.EqualTo(canonicalHomeA).IgnoreCase,
                            "both recovery and exit restore payloads must target verified canonical home A");
                Assert.That(payload.DenyReadPaths, Does.Contain(bookkeepingFromA));
                Assert.That(payload.DenyReadPaths, Does.Not.Contain(bookkeepingFromB));
            }

            Assert.That(File.Exists(storeAlias.StatePath), Is.False);
            Assert.That(afterRecovery.Disabled, Is.False);
            Assert.That(File.Exists(legacyFileB), Is.True);
        });

        var followUpRestore = new FakeSetupInvoker();
        var followUpInner   = new FakeProcessExecutionBackend { ExitCode = 0 };
        var followUpCoordinator = CreateCoordinatorWithStoreForComponents(
            aliasComp, followUpInner, followUpRestore, new ScriptedWindowLock(), storeAlias, () => 750);
        var (stdout2, stderr2) = Captures();
        var blockedByB = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(
            () => followUpCoordinator.ExecuteAsync(AuthorizedPlanForHome(junctionHome), stdout2, stderr2,
                                                   CancellationToken.None));
        Assert.Multiple(() =>
        {
            TestAssert.Contains("cannot be auto-cleared by an ACL refresh", blockedByB!.Message,
                                StringComparison.Ordinal);
            Assert.That(followUpInner.ReceivedExecution, Is.Null);
            Assert.That(followUpRestore.RefreshPayloads, Is.Empty);
        });
    }

    [Test]
    public async Task ExecuteAsync_PostRefreshHomeIdentityMismatch_RetainsDisableStateAndBlocksExecution()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var root         = new TestTempDir();
        var       sharedDir    = Path.Combine(root.Root, "shared-auth-state");
        var       actualHomeA  = Path.Combine(root.Root, "actual-home-a");
        var       junctionHome = Path.Combine(root.Root, "junction-home");
        var       compA        = SeedTestSandboxHome(actualHomeA);
        if (!SandboxRuntimeTempSlotTestSupport.TryCreateJunction(junctionHome, actualHomeA))
            Assert.Ignore("Directory junctions are unavailable on this machine.");

        var aliasComp = new WindowsSandboxComponents
        {
            SetupExecutablePath  = compA.SetupExecutablePath,
            RunnerExecutablePath = compA.RunnerExecutablePath,
            SandboxHome          = junctionHome
        };
        var storeAlias = new AuthorizedReadDisableFile(aliasComp, sharedDir);
        await storeAlias.DisableAsync("restore failed in home A", AuthorizedReadDisableKind.RestoreFailed, null,
                                      CancellationToken.None);

        // 模拟刷新返回后无法再确认源目录身份一致：协调器必须保留共享禁用且绝不放行内层命令。
        var refreshRan = false;
        var restore    = new FakeSetupInvoker();
        var callbackRestore = new CallbackSetupInvoker(restore, (_, _) =>
        {
            refreshRan = true;
            return Task.CompletedTask;
        });

        (string FinalPath, ulong VolumeSerialNumber, string FileIdHex)? CaptureIdentity(string path)
        {
            var real = AuthorizedReadDisableFile.TryCaptureCanonicalHomeIdentity(path);
            if (!refreshRan || real is null) return real;
            return (real.Value.FinalPath, real.Value.VolumeSerialNumber, new string('f', 32));
        }

        var inner = new FakeProcessExecutionBackend { ExitCode = 0 };
        var coordinator = CreateCoordinatorWithStoreForComponents(
            aliasComp, inner, callbackRestore, new ScriptedWindowLock(), storeAlias, () => 760,
            captureHomeIdentity: CaptureIdentity);

        var (stdout, stderr) = Captures();
        var blocked = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(
            () => coordinator.ExecuteAsync(AuthorizedPlanForHome(junctionHome), stdout, stderr, CancellationToken.None));
        var stillDisabled = await storeAlias.ReadStateAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            TestAssert.Contains("cannot be auto-cleared by an ACL refresh", blocked!.Message, StringComparison.Ordinal);
            Assert.That(refreshRan, Is.True);
            Assert.That(inner.ReceivedExecution, Is.Null);
            Assert.That(stillDisabled.Disabled, Is.True, "disable state must remain persisted when post-refresh check fails");
        });
    }

    [Test]
    public async Task ExecuteAsync_AliasRepointedBetweenAuthorizedRefreshAndExitRestore_BindsBothToOriginatingHome()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var root         = new TestTempDir();
        var       sharedDir    = Path.Combine(root.Root, "shared-auth-state");
        var       actualHomeA  = Path.Combine(root.Root, "actual-home-a");
        var       actualHomeB  = Path.Combine(root.Root, "actual-home-b");
        var       junctionHome = Path.Combine(root.Root, "junction-home");
        var       compA        = SeedTestSandboxHome(actualHomeA);
        _ = SeedTestSandboxHome(actualHomeB);
        if (!SandboxRuntimeTempSlotTestSupport.TryCreateJunction(junctionHome, actualHomeA))
            Assert.Ignore("Directory junctions are unavailable on this machine.");

        var expectedFinalA = AuthorizedReadDisableFile.TrimStoredTrailingSeparators(
            SandboxFinalPathResolver.TryGetFinalDirectoryPath(actualHomeA)!);
        var expectedIdA = AuthorizedReadDisableFile.TryCaptureCanonicalHomeIdentity(actualHomeA)!.Value;
        var scanHitA    = Path.Combine(actualHomeA, "legacy-scan-a");
        var scanHitB    = Path.Combine(actualHomeA, "legacy-scan-b");
        File.WriteAllText(Path.Combine(actualHomeA, ".sandbox", "deny_read_acl_state.json"),
                          JsonSerializer.Serialize(new
                          {
                              principals = new Dictionary<string, string[]> { ["S-1-5-21-9001"] = [scanHitA] }
                          }));
        File.WriteAllText(Path.Combine(actualHomeB, ".sandbox", "deny_read_acl_state.json"),
                          JsonSerializer.Serialize(new
                          {
                              principals = new Dictionary<string, string[]> { ["S-1-5-21-9001"] = [scanHitB] }
                          }));

        var aliasComp = new WindowsSandboxComponents
        {
            SetupExecutablePath  = compA.SetupExecutablePath,
            RunnerExecutablePath = compA.RunnerExecutablePath,
            SandboxHome          = junctionHome
        };
        var storeAlias   = new AuthorizedReadDisableFile(aliasComp, sharedDir);
        var backendSetup = new FakeSetupInvoker();
        var restoreSetup = new FakeSetupInvoker();
        var backendGate  = new ScriptedDenyReadGate();
        var restoreGate  = new ScriptedDenyReadGate();
        var movedProbe   = Path.Combine(root.Root, "actual-home-a-moved");
        var preWindowObservedDuringRun = (AuthorizedReadDisableState?)null;

        var backendCallbackSetup = new CallbackSetupInvoker(backendSetup, (_, _) =>
        {
            Assert.Throws<IOException>(() => Directory.Move(expectedFinalA, movedProbe));
            return Task.CompletedTask;
        });
        var restoreCallbackSetup = new CallbackSetupInvoker(restoreSetup, (_, _) =>
        {
            Assert.Throws<IOException>(() => Directory.Move(expectedFinalA, movedProbe));
            return Task.CompletedTask;
        });

        var runnerLauncher = new FakeSandboxRunnerLauncher
        {
            RunnerBody = async (downstream, upstream, end) =>
            {
                await RunnerFrameCodec.ReadFrameAsync(downstream, end);
                await RunnerFrameCodec.WriteFrameAsync(upstream, "spawn_ready", new RunnerSpawnReady { ProcessId = 71 },
                                                       end);
                preWindowObservedDuringRun = await storeAlias.ReadStateAsync(CancellationToken.None);

                // 授权刷新已完成且命令即将退出：将 junctionHome 改指到 actualHomeB，模拟窗口中途别名切换。
                Directory.Delete(junctionHome);
                Assert.That(OperatingSystem.IsWindows() &&
                            SandboxRuntimeTempSlotTestSupport.TryCreateJunction(junctionHome, actualHomeB),
                            Is.True);

                await RunnerFrameCodec.WriteFrameAsync(upstream, "exit",
                                                       new RunnerExitPayload { ExitCode = 0, TimedOut = false }, end);
            }
        };

        var targetEnv = new Dictionary<string, string> { ["TEMP"] = junctionHome };
        var basePolicy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, junctionHome, targetEnv);
        var denyPath   = Path.Combine(junctionHome, "sensitive");
        var policy = basePolicy with
        {
            EffectiveReadRoots = [actualHomeA],
            DenyReadPaths      = [denyPath]
        };
        var backend = new WindowsSandboxBackend(
            aliasComp, policy, targetEnv,
            setupInvoker: backendCallbackSetup,
            credentialSource: new FakeCredentialSource(),
            desktopFactory: new FakeDesktopFactory(),
            runnerLauncher: runnerLauncher,
            limits: new WindowsSandboxBackendLimits
            {
                RefreshTimeout          = TimeSpan.FromSeconds(5),
                SpawnReadyTimeout       = TimeSpan.FromSeconds(3),
                CommunicationGrace      = TimeSpan.FromMilliseconds(300),
                TerminationDrainTimeout = TimeSpan.FromMilliseconds(500),
                RunnerExitTimeout       = TimeSpan.FromMilliseconds(500),
                TerminationKillGrace    = TimeSpan.FromSeconds(1),
                TerminateWriteTimeout   = TimeSpan.FromSeconds(1)
            },
            allowNullDeviceAccess: _ => { },
            validateWriteAcls: (_, _) => Task.CompletedTask,
            validateReadRootReadiness: (_, _) => Task.CompletedTask,
            denyReadRefreshGate: backendGate);

        var coordinator = new AuthorizedReadCoordinator(
            backend, aliasComp, policy,
            restoreInvoker: restoreCallbackSetup,
            windowLock: new ScriptedWindowLock(),
            disableStore: storeAlias,
            capabilityProbe: ProvisionedV7,
            postRefreshValidation: (_, _) => Task.CompletedTask,
            denyReadRefreshGate: restoreGate);

        var (stdout, stderr) = Captures();
        var result = await coordinator.ExecuteAsync(AuthorizedPlanForHome(junctionHome), stdout, stderr,
                                                    CancellationToken.None);
        var afterClear = await storeAlias.ReadStateAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            TestAssert.NotNull(preWindowObservedDuringRun);
            Assert.That(preWindowObservedDuringRun!.Kind, Is.EqualTo(AuthorizedReadDisableKind.WindowOpen));
            Assert.That(preWindowObservedDuringRun.SourceSandboxHome, Is.EqualTo(expectedFinalA).IgnoreCase);
            Assert.That(preWindowObservedDuringRun.SourceHomeVolumeSerial, Is.EqualTo(expectedIdA.VolumeSerialNumber));
            Assert.That(preWindowObservedDuringRun.SourceHomeFileId, Is.EqualTo(expectedIdA.FileIdHex).IgnoreCase);

            // 后端授权刷新与退出恢复必须全程绑定到开窗时的物理源目录 actualHomeA（gate、payload 与簿记一致）。
            Assert.That(backendSetup.RefreshPayloads, Has.Count.EqualTo(1));
            Assert.That(backendSetup.RefreshPayloads[0].SandboxHome, Is.EqualTo(expectedFinalA).IgnoreCase);
            Assert.That(backendSetup.RefreshPayloads[0].DenyReadPaths, Does.Contain(scanHitA));
            Assert.That(backendSetup.RefreshPayloads[0].DenyReadPaths, Does.Not.Contain(scanHitB));
            Assert.That(backendGate.AcquiredHomes[0], Is.EqualTo(expectedFinalA).IgnoreCase);

            Assert.That(restoreSetup.RefreshPayloads, Has.Count.EqualTo(1));
            Assert.That(restoreSetup.RefreshPayloads[0].SandboxHome, Is.EqualTo(expectedFinalA).IgnoreCase);
            Assert.That(restoreSetup.RefreshPayloads[0].AuthorizedReadPaths, Is.Empty);
            Assert.That(restoreSetup.RefreshPayloads[0].DenyReadPaths, Does.Contain(scanHitA));
            Assert.That(restoreSetup.RefreshPayloads[0].DenyReadPaths, Does.Not.Contain(scanHitB));
            Assert.That(restoreGate.AcquiredHomes, Is.EqualTo([expectedFinalA]));

            Assert.That(afterClear.Disabled, Is.False);
        });
    }

    [Test]
    public async Task ExecuteAsync_AliasRepointedBeforeFailedExitRestore_PersistsOriginatingHomeIdentityAndRefusesOtherHome()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var root         = new TestTempDir();
        var       sharedDir    = Path.Combine(root.Root, "shared-auth-state");
        var       actualHomeA  = Path.Combine(root.Root, "actual-home-a");
        var       actualHomeB  = Path.Combine(root.Root, "actual-home-b");
        var       junctionHome = Path.Combine(root.Root, "junction-home");
        var       compA        = SeedTestSandboxHome(actualHomeA);
        var       compB        = SeedTestSandboxHome(actualHomeB);
        if (!SandboxRuntimeTempSlotTestSupport.TryCreateJunction(junctionHome, actualHomeA))
            Assert.Ignore("Directory junctions are unavailable on this machine.");

        var expectedIdA = AuthorizedReadDisableFile.TryCaptureCanonicalHomeIdentity(actualHomeA)!.Value;
        var aliasComp = new WindowsSandboxComponents
        {
            SetupExecutablePath  = compA.SetupExecutablePath,
            RunnerExecutablePath = compA.RunnerExecutablePath,
            SandboxHome          = junctionHome
        };
        var storeAlias = new AuthorizedReadDisableFile(aliasComp, sharedDir);
        var inner = new CallbackBackend(() =>
        {
            Directory.Delete(junctionHome);
            Assert.That(OperatingSystem.IsWindows() &&
                        SandboxRuntimeTempSlotTestSupport.TryCreateJunction(junctionHome, actualHomeB),
                        Is.True);
            return Task.FromResult(new ProcessExecutionResult(0, false, null));
        });
        var failingRestore = new FakeSetupInvoker { ThrowOnRefresh = new InvalidOperationException("exit restore failed") };
        var coordinator = CreateCoordinatorWithStoreForComponents(
            aliasComp, inner, failingRestore, new ScriptedWindowLock(), storeAlias, () => 770);

        var (stdout, stderr) = Captures();
        var result    = await coordinator.ExecuteAsync(AuthorizedPlanForHome(junctionHome), stdout, stderr,
                                                       CancellationToken.None);
        var persisted = await storeAlias.ReadStateAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            Assert.That(persisted.Disabled, Is.True);
            Assert.That(persisted.Kind, Is.EqualTo(AuthorizedReadDisableKind.RestoreFailed));
            Assert.That(persisted.SourceSandboxHome, Is.EqualTo(expectedIdA.FinalPath).IgnoreCase);
            Assert.That(persisted.SourceHomeVolumeSerial, Is.EqualTo(expectedIdA.VolumeSerialNumber));
            Assert.That(persisted.SourceHomeFileId, Is.EqualTo(expectedIdA.FileIdHex).IgnoreCase);
        });

        // 改指后的 junctionHome（指向 B）与直接路径 B 均不得自动恢复 A 留下的禁用。
        var restoreB = new FakeSetupInvoker();
        var innerB   = new FakeProcessExecutionBackend { ExitCode = 0 };
        var coordinatorB = CreateCoordinatorWithStoreForComponents(
            compB, innerB, restoreB, new ScriptedWindowLock(), new AuthorizedReadDisableFile(compB, sharedDir),
            () => 770);
        var (stdoutB, stderrB) = Captures();
        var blockedB = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(
            () => coordinatorB.ExecuteAsync(AuthorizedPlanForHome(actualHomeB), stdoutB, stderrB,
                                            CancellationToken.None));
        Assert.Multiple(() =>
        {
            TestAssert.Contains("cannot be auto-cleared by an ACL refresh", blockedB!.Message, StringComparison.Ordinal);
            Assert.That(restoreB.RefreshPayloads, Is.Empty);
            Assert.That(innerB.ReceivedExecution, Is.Null);
        });

        // 回到原 home A 可以正常恢复并清除共享禁用。
        var restoreA = new FakeSetupInvoker();
        var innerA   = new FakeProcessExecutionBackend { ExitCode = 0 };
        var coordinatorA = CreateCoordinatorWithStoreForComponents(
            compA, innerA, restoreA, new ScriptedWindowLock(), new AuthorizedReadDisableFile(compA, sharedDir),
            () => 770);
        var (stdoutA, stderrA) = Captures();
        var recoveredResult = await coordinatorA.ExecuteAsync(AuthorizedPlanForHome(actualHomeA), stdoutA, stderrA,
                                                              CancellationToken.None);
        var cleared = await storeAlias.ReadStateAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(recoveredResult.ExitCode, Is.EqualTo(0));
            Assert.That(cleared.Disabled, Is.False);
        });
    }

    [Test]
    public async Task ExecuteAsync_ExitRestorePostRefreshIdentityMismatch_DoesNotClearPreWindowGuardAndBlocksFollowUp()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var root         = new TestTempDir();
        var       sharedDir    = Path.Combine(root.Root, "shared-auth-state");
        var       actualHomeA  = Path.Combine(root.Root, "actual-home-a");
        var       junctionHome = Path.Combine(root.Root, "junction-home");
        var       compA        = SeedTestSandboxHome(actualHomeA);
        if (!SandboxRuntimeTempSlotTestSupport.TryCreateJunction(junctionHome, actualHomeA))
            Assert.Ignore("Directory junctions are unavailable on this machine.");

        var expectedIdA = AuthorizedReadDisableFile.TryCaptureCanonicalHomeIdentity(actualHomeA)!.Value;
        var aliasComp = new WindowsSandboxComponents
        {
            SetupExecutablePath  = compA.SetupExecutablePath,
            RunnerExecutablePath = compA.RunnerExecutablePath,
            SandboxHome          = junctionHome
        };
        var storeAlias    = new AuthorizedReadDisableFile(aliasComp, sharedDir);
        var exitRestoreRan = false;
        var restore       = new FakeSetupInvoker();
        var callbackRestore = new CallbackSetupInvoker(restore, (_, _) =>
        {
            exitRestoreRan = true;
            return Task.CompletedTask;
        });

        (string FinalPath, ulong VolumeSerialNumber, string FileIdHex)? CaptureIdentity(string path)
        {
            var real = AuthorizedReadDisableFile.TryCaptureCanonicalHomeIdentity(path);
            if (!exitRestoreRan || real is null) return real;
            return (real.Value.FinalPath, real.Value.VolumeSerialNumber, new string('e', 32));
        }

        var inner = new FakeProcessExecutionBackend { ExitCode = 0 };
        var coordinator = CreateCoordinatorWithStoreForComponents(
            aliasComp, inner, callbackRestore, new ScriptedWindowLock(), storeAlias, () => 780,
            captureHomeIdentity: CaptureIdentity);

        var (stdout1, stderr1) = Captures();
        var result = await coordinator.ExecuteAsync(AuthorizedPlanForHome(junctionHome), stdout1, stderr1,
                                                    CancellationToken.None);
        var persisted = await storeAlias.ReadStateAsync(CancellationToken.None);

        // 退出恢复的后刷新校验失败：已确认的命令结果如实返回，但绝不清除守卫/禁用状态，且记录指向原 home A 的身份。
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            Assert.That(exitRestoreRan, Is.True);
            Assert.That(persisted.Disabled, Is.True);
            Assert.That(persisted.Kind, Is.EqualTo(AuthorizedReadDisableKind.RestoreFailed));
            Assert.That(persisted.SourceSandboxHome, Is.EqualTo(expectedIdA.FinalPath).IgnoreCase);
            Assert.That(persisted.SourceHomeVolumeSerial, Is.EqualTo(expectedIdA.VolumeSerialNumber));
            Assert.That(persisted.SourceHomeFileId, Is.EqualTo(expectedIdA.FileIdHex).IgnoreCase);
        });

        var (stdout2, stderr2) = Captures();
        var blocked = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(
            () => coordinator.ExecuteAsync(AuthorizedPlanForHome(junctionHome), stdout2, stderr2,
                                           CancellationToken.None));
        Assert.Multiple(() =>
        {
            TestAssert.Contains("Authorized execution is disabled", blocked!.Message, StringComparison.Ordinal);
            Assert.That(restore.RefreshPayloads, Has.Count.EqualTo(1),
                        "mismatched post-refresh check must prevent re-opening the authorization window");
        });
    }

    [Test]
    public async Task ExecuteAsync_AliasRepointedDuringNormalWindow_ClearsSharedGuardWithoutTouchingRepointedLegacyFile()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var root         = new TestTempDir();
        var       sharedDir    = Path.Combine(root.Root, "shared-auth-state");
        var       actualHomeA  = Path.Combine(root.Root, "actual-home-a");
        var       actualHomeB  = Path.Combine(root.Root, "actual-home-b");
        var       junctionHome = Path.Combine(root.Root, "junction-home");
        var       compA        = SeedTestSandboxHome(actualHomeA);
        var       compB        = SeedTestSandboxHome(actualHomeB);
        if (!SandboxRuntimeTempSlotTestSupport.TryCreateJunction(junctionHome, actualHomeA))
            Assert.Ignore("Directory junctions are unavailable on this machine.");

        var legacyFileB = Path.Combine(compB.CredentialsDirectory, "authorized_read_state.json");
        await File.WriteAllTextAsync(
            legacyFileB,
            """{"disabled":true,"reason":"legacy disable in home B","kind":"restore_failed"}""");

        var aliasComp = new WindowsSandboxComponents
        {
            SetupExecutablePath  = compA.SetupExecutablePath,
            RunnerExecutablePath = compA.RunnerExecutablePath,
            SandboxHome          = junctionHome
        };
        var storeAlias = new AuthorizedReadDisableFile(aliasComp, sharedDir);
        var restore    = new FakeSetupInvoker();
        var inner = new CallbackBackend(() =>
        {
            Directory.Delete(junctionHome);
            Assert.That(OperatingSystem.IsWindows() &&
                        SandboxRuntimeTempSlotTestSupport.TryCreateJunction(junctionHome, actualHomeB),
                        Is.True);
            return Task.FromResult(new ProcessExecutionResult(0, false, null));
        });
        var coordinator = CreateCoordinatorWithStoreForComponents(
            aliasComp, inner, restore, new ScriptedWindowLock(), storeAlias, () => 810);

        var (stdout, stderr) = Captures();
        var result = await coordinator.ExecuteAsync(AuthorizedPlanForHome(junctionHome), stdout, stderr,
                                                    CancellationToken.None);

        // 正常退出只清除共享守卫 StatePath，绝不能经已改指的可变别名删除 home B 下未读取、未恢复的旧状态文件。
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            Assert.That(File.Exists(storeAlias.StatePath), Is.False);
            Assert.That(File.Exists(legacyFileB), Is.True);
            TestAssert.Contains("legacy disable in home B", File.ReadAllText(legacyFileB), StringComparison.Ordinal);
        });
    }

    [Test]
    public async Task ExecuteAsync_EntryRecoveryOfLegacyRecord_ClearsOnlyVerifiedOriginatingLegacyFileAndLeavesOtherHomeUntouched()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var root         = new TestTempDir();
        var       sharedDir    = Path.Combine(root.Root, "shared-auth-state");
        var       actualHomeA  = Path.Combine(root.Root, "actual-home-a");
        var       actualHomeB  = Path.Combine(root.Root, "actual-home-b");
        var       junctionHome = Path.Combine(root.Root, "junction-home");
        var       compA        = SeedTestSandboxHome(actualHomeA);
        var       compB        = SeedTestSandboxHome(actualHomeB);
        if (!SandboxRuntimeTempSlotTestSupport.TryCreateJunction(junctionHome, actualHomeA))
            Assert.Ignore("Directory junctions are unavailable on this machine.");

        var canonicalHomeA = AuthorizedReadDisableFile.TryResolveCanonicalHomePath(actualHomeA, allowMissing: false)!;
        var canonicalHomeB = AuthorizedReadDisableFile.TryResolveCanonicalHomePath(actualHomeB, allowMissing: false)!;
        var legacyFileA    = Path.Combine(compA.CredentialsDirectory, "authorized_read_state.json");
        var legacyFileB    = Path.Combine(compB.CredentialsDirectory, "authorized_read_state.json");
        await File.WriteAllTextAsync(
            legacyFileA,
            """{"disabled":true,"reason":"legacy restore failed in home A","kind":"restore_failed"}""");
        await File.WriteAllTextAsync(
            legacyFileB,
            """{"disabled":true,"reason":"legacy disable in home B","kind":"reclamation_uncertain","boot_id":820}""");

        var aliasComp = new WindowsSandboxComponents
        {
            SetupExecutablePath  = compA.SetupExecutablePath,
            RunnerExecutablePath = compA.RunnerExecutablePath,
            SandboxHome          = junctionHome
        };
        var storeAlias   = new AuthorizedReadDisableFile(aliasComp, sharedDir);
        var restore      = new FakeSetupInvoker();
        var refreshCount = 0;
        var callbackRestore = new CallbackSetupInvoker(restore, (_, _) =>
        {
            // 模拟入口恢复刷新刚执行完、准备清除已验证旧状态前瞬间将别名改指到 home B。
            if (Interlocked.Increment(ref refreshCount) == 1)
            {
                Directory.Delete(junctionHome);
                Assert.That(OperatingSystem.IsWindows() &&
                            SandboxRuntimeTempSlotTestSupport.TryCreateJunction(junctionHome, actualHomeB),
                            Is.True);
            }

            return Task.CompletedTask;
        });

        var gate                      = new ScriptedDenyReadGate();
        AuthorizedReadWindowHome? observedWindowHome = null;
        var inner = new FakeProcessExecutionBackend { ExitCode = 0 };
        var windowRecordingInner = new CaptureWindowContextBackend(inner, home => observedWindowHome = home);
        var coordinator = CreateCoordinatorWithStoreForComponents(
            aliasComp, windowRecordingInner, callbackRestore, new ScriptedWindowLock(), storeAlias, () => 820,
            denyReadRefreshGate: gate);

        var (stdout, stderr) = Captures();
        var result = await coordinator.ExecuteAsync(AuthorizedPlanForHome(junctionHome), stdout, stderr,
                                                    CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            TestAssert.NotNull(observedWindowHome);
            Assert.That(observedWindowHome!.CanonicalPath, Is.EqualTo(canonicalHomeA).IgnoreCase);
            Assert.That(observedWindowHome.CanonicalPath, Is.Not.EqualTo(canonicalHomeB).IgnoreCase);
            Assert.That(gate.AcquiredHomes, Is.EqualTo([canonicalHomeA, canonicalHomeA]).IgnoreCase);
            Assert.That(restore.RefreshPayloads, Has.Count.EqualTo(2));
            Assert.That(restore.RefreshPayloads.All(
                            p => string.Equals(p.SandboxHome, canonicalHomeA, StringComparison.OrdinalIgnoreCase)),
                        Is.True, "both recovery and exit restore must stay pinned to verified canonical home A");
            Assert.That(File.Exists(legacyFileA), Is.False,
                        "verified originating legacy file in home A must be cleared after recovery");
            Assert.That(File.Exists(legacyFileB), Is.True,
                        "legacy file in repointed home B must remain untouched");
        });

        // 随后以已改指到 home B 的同一别名发起新请求：必须走完整入口检查并被 home B 的旧禁用记录阻断。
        var followUpRestore = new FakeSetupInvoker();
        var followUpInner   = new FakeProcessExecutionBackend { ExitCode = 0 };
        var followUpCoordinator = CreateCoordinatorWithStoreForComponents(
            aliasComp, followUpInner, followUpRestore, new ScriptedWindowLock(), storeAlias, () => 820);
        var (stdout2, stderr2) = Captures();
        var blockedByB = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(
            () => followUpCoordinator.ExecuteAsync(AuthorizedPlanForHome(junctionHome), stdout2, stderr2,
                                                   CancellationToken.None));
        Assert.Multiple(() =>
        {
            TestAssert.Contains("cannot be auto-cleared by an ACL refresh", blockedByB!.Message,
                                StringComparison.Ordinal);
            Assert.That(followUpInner.ReceivedExecution, Is.Null);
            Assert.That(followUpRestore.RefreshPayloads, Is.Empty);
        });
    }

    [Test]
    public async Task ExecuteAsync_NoRecoveryBranch_AliasRepointedBetweenLockedReadAndWindowOpen_StaysPinnedToCheckedHomeAAndRespectsHomeBDisable()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var root         = new TestTempDir();
        var       sharedDir    = Path.Combine(root.Root, "shared-auth-state");
        var       actualHomeA  = Path.Combine(root.Root, "actual-home-a");
        var       actualHomeB  = Path.Combine(root.Root, "actual-home-b");
        var       junctionHome = Path.Combine(root.Root, "junction-home");
        var       compA        = SeedTestSandboxHome(actualHomeA);
        var       compB        = SeedTestSandboxHome(actualHomeB);
        if (!SandboxRuntimeTempSlotTestSupport.TryCreateJunction(junctionHome, actualHomeA))
            Assert.Ignore("Directory junctions are unavailable on this machine.");

        var canonicalHomeA = AuthorizedReadDisableFile.TryResolveCanonicalHomePath(actualHomeA, allowMissing: false)!;
        var canonicalHomeB = AuthorizedReadDisableFile.TryResolveCanonicalHomePath(actualHomeB, allowMissing: false)!;
        var legacyFileB    = Path.Combine(compB.CredentialsDirectory, "authorized_read_state.json");
        await File.WriteAllTextAsync(
            legacyFileB,
            """{"disabled":true,"reason":"legacy disable in home B","kind":"reclamation_uncertain","boot_id":830}""");

        var aliasComp = new WindowsSandboxComponents
        {
            SetupExecutablePath  = compA.SetupExecutablePath,
            RunnerExecutablePath = compA.RunnerExecutablePath,
            SandboxHome          = junctionHome
        };
        var realStore = new AuthorizedReadDisableFile(aliasComp, sharedDir);
        var repointed = false;
        var wrappedStore = new HookAfterWindowReadDisableStore(realStore, _ =>
        {
            if (repointed) return;
            repointed = true;
            // 模拟锁内禁用读取刚完成、即将写守卫与开窗前，无恢复分支下 junction 被改指到已禁用的 home B。
            Directory.Delete(junctionHome);
            Assert.That(OperatingSystem.IsWindows() &&
                        SandboxRuntimeTempSlotTestSupport.TryCreateJunction(junctionHome, actualHomeB),
                        Is.True);
        });

        var gate                               = new ScriptedDenyReadGate();
        var restore                            = new FakeSetupInvoker();
        AuthorizedReadWindowHome?  observedWindowHome   = null;
        SandboxAuthorizedReadState? observedPreWindowWal = null;
        var inner = new CallbackBackend(async () =>
        {
            observedWindowHome = AuthorizedReadWindowContext.Current;
            var walJson = await File.ReadAllTextAsync(realStore.StatePath, CancellationToken.None)
                                    .ConfigureAwait(false);
            observedPreWindowWal = JsonSerializer.Deserialize(
                walJson, WindowsSandboxJsonContext.Default.SandboxAuthorizedReadState);
            return new ProcessExecutionResult(0, false, null);
        });
        var coordinator = CreateCoordinatorWithStoreForComponents(
            aliasComp, inner, restore, new ScriptedWindowLock(), wrappedStore, () => 830,
            denyReadRefreshGate: gate);

        var (stdout, stderr) = Captures();
        var result = await coordinator.ExecuteAsync(AuthorizedPlanForHome(junctionHome), stdout, stderr,
                                                    CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            Assert.That(repointed, Is.True);
            TestAssert.NotNull(observedWindowHome);
            Assert.That(observedWindowHome!.CanonicalPath, Is.EqualTo(canonicalHomeA).IgnoreCase);
            Assert.That(observedWindowHome.CanonicalPath, Is.Not.EqualTo(canonicalHomeB).IgnoreCase);
            TestAssert.NotNull(observedPreWindowWal);
            Assert.That(observedPreWindowWal!.Kind, Is.EqualTo(SandboxAuthorizedReadState.WindowOpenKind));
            Assert.That(observedPreWindowWal.SourceSandboxHome, Is.EqualTo(canonicalHomeA).IgnoreCase);
            Assert.That(gate.AcquiredHomes, Is.EqualTo([canonicalHomeA]).IgnoreCase);
            var exitPayload = TestAssert.Single(restore.RefreshPayloads);
            Assert.That(exitPayload.SandboxHome, Is.EqualTo(canonicalHomeA).IgnoreCase);
            Assert.That(File.Exists(legacyFileB), Is.True);
        });

        // 随后以已改指到 home B 的同一别名发起新请求：必须被 home B 的旧禁用记录阻断。
        var followUpRestore = new FakeSetupInvoker();
        var followUpInner   = new FakeProcessExecutionBackend { ExitCode = 0 };
        var followUpCoordinator = CreateCoordinatorWithStoreForComponents(
            aliasComp, followUpInner, followUpRestore, new ScriptedWindowLock(), realStore, () => 830);
        var (stdout2, stderr2) = Captures();
        var blockedByB = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(
            () => followUpCoordinator.ExecuteAsync(AuthorizedPlanForHome(junctionHome), stdout2, stderr2,
                                                   CancellationToken.None));
        Assert.Multiple(() =>
        {
            TestAssert.Contains("cannot be auto-cleared by an ACL refresh", blockedByB!.Message,
                                StringComparison.Ordinal);
            Assert.That(followUpInner.ReceivedExecution, Is.Null);
            Assert.That(followUpRestore.RefreshPayloads, Is.Empty);
        });
    }

    [Test]
    public async Task ExecuteAsync_AliasRepointedRightAfterPreflightRefresh_BindsCredentialsCapabilitySidAndSpawnRequestToOriginatingHome()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var root         = new TestTempDir();
        var       sharedDir    = Path.Combine(root.Root, "shared-auth-state");
        var       actualHomeA  = Path.Combine(root.Root, "actual-home-a");
        var       actualHomeB  = Path.Combine(root.Root, "actual-home-b");
        var       junctionHome = Path.Combine(root.Root, "junction-home");
        var       compA        = SeedTestSandboxHome(actualHomeA);
        _ = SeedTestSandboxHome(actualHomeB);
        if (!SandboxRuntimeTempSlotTestSupport.TryCreateJunction(junctionHome, actualHomeA))
            Assert.Ignore("Directory junctions are unavailable on this machine.");

        var expectedFinalA = AuthorizedReadDisableFile.TrimStoredTrailingSeparators(
            SandboxFinalPathResolver.TryGetFinalDirectoryPath(actualHomeA)!);
        const string readonlySidA = "S-1-5-21-111-111-111-111";
        const string readonlySidB = "S-1-5-21-222-222-222-222";
        File.WriteAllText(Path.Combine(actualHomeA, "cap_sid"),
                          JsonSerializer.Serialize(new SandboxCapabilitySids
                          {
                              Workspace          = "S-1-5-21-11-11-11-11",
                              ReadOnly           = readonlySidA,
                              WorkspaceByCwd     = [],
                              WritableRootByPath = []
                          }));
        File.WriteAllText(Path.Combine(actualHomeB, "cap_sid"),
                          JsonSerializer.Serialize(new SandboxCapabilitySids
                          {
                              Workspace          = "S-1-5-21-22-22-22-22",
                              ReadOnly           = readonlySidB,
                              WorkspaceByCwd     = [],
                              WritableRootByPath = []
                          }));

        var aliasComp = new WindowsSandboxComponents
        {
            SetupExecutablePath  = compA.SetupExecutablePath,
            RunnerExecutablePath = compA.RunnerExecutablePath,
            SandboxHome          = junctionHome
        };
        var storeAlias   = new AuthorizedReadDisableFile(aliasComp, sharedDir);
        var backendSetup = new FakeSetupInvoker();
        var restoreSetup = new FakeSetupInvoker();
        var seenCredentialComponents = new List<WindowsSandboxComponents>();
        RunnerSpawnRequest? capturedSpawn = null;

        // 模拟在 PreflightAsync 的授权刷新刚完成后、加载凭据与解析 capability SID 前将别名改指到 home B。
        var backendCallbackSetup = new CallbackSetupInvoker(backendSetup, (_, _) =>
        {
            Directory.Delete(junctionHome);
            Assert.That(OperatingSystem.IsWindows() &&
                        SandboxRuntimeTempSlotTestSupport.TryCreateJunction(junctionHome, actualHomeB),
                        Is.True);
            return Task.CompletedTask;
        });

        var runnerLauncher = new FakeSandboxRunnerLauncher
        {
            RunnerBody = async (downstream, upstream, end) =>
            {
                var frame = await RunnerFrameCodec.ReadFrameAsync(downstream, end);
                capturedSpawn = RunnerFrameCodec.ParsePayload<RunnerSpawnRequest>(frame!);
                await RunnerFrameCodec.WriteFrameAsync(upstream, "spawn_ready", new RunnerSpawnReady { ProcessId = 73 },
                                                       end);
                await RunnerFrameCodec.WriteFrameAsync(upstream, "exit",
                                                       new RunnerExitPayload { ExitCode = 0, TimedOut = false }, end);
            }
        };

        var targetEnv = new Dictionary<string, string> { ["TEMP"] = junctionHome };
        var basePolicy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.ReadOnly, junctionHome, targetEnv);
        var policy = basePolicy with
        {
            DenyReadPaths = [Path.Combine(junctionHome, "sensitive")]
        };
        var backend = new WindowsSandboxBackend(
            aliasComp, policy, targetEnv,
            knownSecrets: null,
            setupInvoker: backendCallbackSetup,
            credentialSource: new FakeCredentialSource(),
            capabilitySidStore: null,
            desktopFactory: new FakeDesktopFactory(),
            runnerLauncher: runnerLauncher,
            limits: new WindowsSandboxBackendLimits
            {
                RefreshTimeout          = TimeSpan.FromSeconds(5),
                SpawnReadyTimeout       = TimeSpan.FromSeconds(3),
                CommunicationGrace      = TimeSpan.FromMilliseconds(300),
                TerminationDrainTimeout = TimeSpan.FromMilliseconds(500),
                RunnerExitTimeout       = TimeSpan.FromMilliseconds(500),
                TerminationKillGrace    = TimeSpan.FromSeconds(1),
                TerminateWriteTimeout   = TimeSpan.FromSeconds(1)
            },
            allowNullDeviceAccess: _ => { },
            validateWriteAcls: (_, _) => Task.CompletedTask,
            validateReadRootReadiness: (_, _) => Task.CompletedTask,
            standingNetworkAccessEnabled: false,
            proxyEnvironment: null,
            inspectOnlineAccount: null,
            denyReadRefreshGate: new ScriptedDenyReadGate(),
            credentialSourceFactory: components =>
            {
                seenCredentialComponents.Add(components);
                return new FakeCredentialSource();
            });

        var coordinator = new AuthorizedReadCoordinator(
            backend, aliasComp, policy,
            restoreInvoker: restoreSetup,
            windowLock: new ScriptedWindowLock(),
            disableStore: storeAlias,
            capabilityProbe: ProvisionedV7,
            postRefreshValidation: (_, _) => Task.CompletedTask,
            denyReadRefreshGate: new ScriptedDenyReadGate());

        var (stdout, stderr) = Captures();
        var result = await coordinator.ExecuteAsync(AuthorizedPlanForHome(junctionHome), stdout, stderr,
                                                    CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            var credComp = TestAssert.Single(seenCredentialComponents);
            Assert.That(credComp.SandboxHome, Is.EqualTo(expectedFinalA).IgnoreCase);
            Assert.That(credComp.UsersFilePath, Is.EqualTo(compA.UsersFilePath).IgnoreCase);

            TestAssert.NotNull(capturedSpawn);
            Assert.That(capturedSpawn!.CapabilitySids, Is.EqualTo([readonlySidA]));
            Assert.That(capturedSpawn.RealSandboxHome, Is.EqualTo(expectedFinalA).IgnoreCase);
            Assert.That(capturedSpawn.SandboxDirectory, Is.EqualTo(compA.SandboxDirectory).IgnoreCase);

            var exitPayload = TestAssert.Single(restoreSetup.RefreshPayloads);
            Assert.That(exitPayload.SandboxHome, Is.EqualTo(expectedFinalA).IgnoreCase);
        });
    }

    private static AuthorizedReadCoordinator CreateCoordinatorWithStore(
        SandboxTestHome             home,
        IProcessExecutionBackend    inner,
        ISandboxSetupInvoker        restore,
        IAuthorizedReadWindowLock   windowLock,
        IAuthorizedReadDisableStore store,
        Func<int?>                  readSystemBootId) =>
        CreateCoordinatorWithStoreForComponents(
            home.Components, inner, restore, windowLock, store, readSystemBootId);

    private static AuthorizedReadCoordinator CreateCoordinatorWithStoreForComponents(
        WindowsSandboxComponents     components,
        IProcessExecutionBackend     inner,
        ISandboxSetupInvoker         restore,
        IAuthorizedReadWindowLock    windowLock,
        IAuthorizedReadDisableStore  store,
        Func<int?>                   readSystemBootId,
        ISandboxDenyReadRefreshGate? denyReadRefreshGate = null,
        Func<string, (string FinalPath, ulong VolumeSerialNumber, string FileIdHex)?>? captureHomeIdentity = null)
    {
        var denyReadPath = Path.Combine(components.SandboxHome, "sensitive");
        if (Directory.Exists(components.SandboxHome)) Directory.CreateDirectory(denyReadPath);
        var policy = new SandboxIsolationPolicy
        {
            Kind                = SandboxPolicyKind.WorkspaceWrite,
            WorkspaceRoot       = components.SandboxHome,
            SpawnProfile        = new SandboxPermissionProfile
            {
                FileSystem = new SandboxFileSystemRestriction { Entries = [] },
                Network    = "restricted"
            },
            EffectiveReadRoots  = [],
            EffectiveWriteRoots = [components.SandboxHome],
            DenyWritePaths      = [],
            TempWriteRoots      = [],
            DenyReadPaths       = [denyReadPath]
        };
        return new AuthorizedReadCoordinator(inner, components, policy,
                                             restoreInvoker : restore,
                                             windowLock : windowLock,
                                             disableStore : store,
                                             capabilityProbe : ProvisionedV7,
                                             windowTimeout : null,
                                             postRefreshValidation : (_, _) => Task.CompletedTask,
                                             denyReadRefreshGate : denyReadRefreshGate,
                                             denyReadGateTimeout : null,
                                             readSystemBootId : readSystemBootId,
                                             captureHomeIdentity : captureHomeIdentity);
    }

    private static WindowsSandboxComponents SeedTestSandboxHome(string homePath)
    {
        Directory.CreateDirectory(homePath);
        Directory.CreateDirectory(Path.Combine(homePath, ".sandbox"));
        Directory.CreateDirectory(Path.Combine(homePath, ".sandbox-secrets"));
        Directory.CreateDirectory(Path.Combine(homePath, "sensitive"));
        var setupExe  = Path.Combine(homePath, "setup.exe");
        var runnerExe = Path.Combine(homePath, "runner.exe");
        File.WriteAllBytes(setupExe, [0x4d, 0x5a]);
        File.WriteAllBytes(runnerExe, [0x4d, 0x5a]);
        File.WriteAllText(Path.Combine(homePath, ".sandbox", "setup_marker.json"),
                          """{"version":5,"offline_username":"TinyHarnessOffline","online_username":"TinyHarnessOnline","created_at":"2026-10-09T00:00:00Z","proxy_ports":[],"allow_local_binding":false,"read_roots":[],"write_roots":[]}""");
        File.WriteAllText(Path.Combine(homePath, "cap_sid"),
                          """{"workspace":"S-1-5-21-11-11-11-11","readonly":"S-1-5-21-22-22-22-22","workspace_by_cwd":{},"writable_root_by_path":{}}""");
        File.WriteAllText(Path.Combine(homePath, ".sandbox-secrets", "sandbox_users.json"),
                          """{"version":7,"offline":{"username":"TinyHarnessOffline","password":"QUJDRA=="},"online":{"username":"TinyHarnessOnline","password":"QUJDRA=="}}""");
        return new WindowsSandboxComponents
        {
            SetupExecutablePath  = setupExe,
            RunnerExecutablePath = runnerExe,
            SandboxHome          = homePath
        };
    }

    private static PreparedProcessExecution AuthorizedPlanForHome(string sandboxHome) =>
        new("cmd.exe", [], sandboxHome, 10, false, null)
        {
            GrantScope = new SandboxGrantScope
            {
                ReadRoots          = [Path.Combine(sandboxHome, "sensitive")],
                WriteRoots         = [],
                MetadataWritePaths = []
            }
        };

    private sealed class BlockingWindowLock(TaskCompletionSource<bool> secondWaiterEntered) : IAuthorizedReadWindowLock
    {
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private          int           _callCount;

        public async Task<IAuthorizedReadWindowHandle?> TryAcquireAsync(
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _callCount) > 1) secondWaiterEntered.TrySetResult(true);
            if (!await _semaphore.WaitAsync(timeout, cancellationToken).ConfigureAwait(false)) return null;
            return new ReleaseHandle(_semaphore);
        }

        private sealed class ReleaseHandle(SemaphoreSlim semaphore) : IAuthorizedReadWindowHandle
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) semaphore.Release();
            }
        }
    }

    private sealed class ControlledUncertainBackend(
        TaskCompletionSource<bool> entered, TaskCompletionSource<bool> release) : IProcessExecutionBackend
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<ProcessExecutionResult> ExecuteAsync(
            PreparedProcessExecution execution, IProcessOutputCapture stdoutCapture,
            IProcessOutputCapture stderrCapture, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            entered.TrySetResult(true);
            await release.Task.ConfigureAwait(false);
            throw new ToolExecutionUncertainException("first command reclamation unconfirmed");
        }
    }

    private sealed class ThrowOnceThenSucceedBackend(Exception firstFailure) : IProcessExecutionBackend
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ProcessExecutionResult> ExecuteAsync(
            PreparedProcessExecution execution, IProcessOutputCapture stdoutCapture,
            IProcessOutputCapture stderrCapture, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _callCount) == 1) return Task.FromException<ProcessExecutionResult>(firstFailure);
            return Task.FromResult(new ProcessExecutionResult(0, false, null));
        }
    }

    private sealed class CallbackBackend(Func<Task<ProcessExecutionResult>> callback) : IProcessExecutionBackend
    {
        public Task<ProcessExecutionResult> ExecuteAsync(
            PreparedProcessExecution execution, IProcessOutputCapture stdoutCapture,
            IProcessOutputCapture stderrCapture, CancellationToken cancellationToken) =>
            callback();
    }

    private sealed class FailingDisableStore(Exception failure, int failAfterCalls = 0) : IAuthorizedReadDisableStore
    {
        private int _disableCalls;

        public Task<(bool Disabled, string? Reason)> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<(bool, string?)>((false, null));

        public Task<AuthorizedReadDisableState> ReadStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(AuthorizedReadDisableState.NotDisabled);

        public Task DisableAsync(string reason, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _disableCalls) > failAfterCalls
                ? Task.FromException(failure)
                : Task.CompletedTask;

        public Task DisableAsync(
            string reason, AuthorizedReadDisableKind kind, int? bootId, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _disableCalls) > failAfterCalls
                ? Task.FromException(failure)
                : Task.CompletedTask;

        public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowOnClearDisableStore(
        IAuthorizedReadDisableStore inner, Exception clearFailure) : IAuthorizedReadDisableStore
    {
        public Task<(bool Disabled, string? Reason)> ReadAsync(CancellationToken cancellationToken) =>
            inner.ReadAsync(cancellationToken);

        public Task<AuthorizedReadDisableState> ReadStateAsync(CancellationToken cancellationToken) =>
            inner.ReadStateAsync(cancellationToken);

        Task<AuthorizedReadDisableState> IAuthorizedReadDisableStore.ReadStateAsync(
            AuthorizedReadWindowHome? windowHome, CancellationToken cancellationToken) =>
            inner.ReadStateAsync(windowHome, cancellationToken);

        public Task DisableAsync(string reason, CancellationToken cancellationToken) =>
            inner.DisableAsync(reason, cancellationToken);

        public Task DisableAsync(
            string reason, AuthorizedReadDisableKind kind, int? bootId, CancellationToken cancellationToken) =>
            inner.DisableAsync(reason, kind, bootId, cancellationToken);

        Task IAuthorizedReadDisableStore.DisableAsync(
            string                    reason,
            AuthorizedReadDisableKind kind,
            int?                      bootId,
            AuthorizedReadWindowHome? originatingHome,
            CancellationToken         cancellationToken) =>
            inner.DisableAsync(reason, kind, bootId, originatingHome, cancellationToken);

        public Task ClearAsync(CancellationToken cancellationToken) => Task.FromException(clearFailure);
    }

    private sealed class HookAfterWindowReadDisableStore(
        IAuthorizedReadDisableStore        inner,
        Action<AuthorizedReadDisableState> afterWindowRead) : IAuthorizedReadDisableStore
    {
        public Task<(bool Disabled, string? Reason)> ReadAsync(CancellationToken cancellationToken) =>
            inner.ReadAsync(cancellationToken);

        public Task<AuthorizedReadDisableState> ReadStateAsync(CancellationToken cancellationToken) =>
            inner.ReadStateAsync(cancellationToken);

        async Task<AuthorizedReadDisableState> IAuthorizedReadDisableStore.ReadStateAsync(
            AuthorizedReadWindowHome? windowHome, CancellationToken cancellationToken)
        {
            var state = await inner.ReadStateAsync(windowHome, cancellationToken).ConfigureAwait(false);
            afterWindowRead(state);
            return state;
        }

        public Task DisableAsync(string reason, CancellationToken cancellationToken) =>
            inner.DisableAsync(reason, cancellationToken);

        public Task DisableAsync(
            string reason, AuthorizedReadDisableKind kind, int? bootId, CancellationToken cancellationToken) =>
            inner.DisableAsync(reason, kind, bootId, cancellationToken);

        Task IAuthorizedReadDisableStore.DisableAsync(
            string                    reason,
            AuthorizedReadDisableKind kind,
            int?                      bootId,
            AuthorizedReadWindowHome? originatingHome,
            CancellationToken         cancellationToken) =>
            inner.DisableAsync(reason, kind, bootId, originatingHome, cancellationToken);

        public Task ClearAsync(CancellationToken cancellationToken) => inner.ClearAsync(cancellationToken);

        Task IAuthorizedReadDisableStore.ClearSharedGuardAsync(CancellationToken cancellationToken) =>
            inner.ClearSharedGuardAsync(cancellationToken);

        Task IAuthorizedReadDisableStore.ClearRecoveredStateAsync(
            string? verifiedLegacyStatePath, CancellationToken cancellationToken) =>
            inner.ClearRecoveredStateAsync(verifiedLegacyStatePath, cancellationToken);
    }

    private sealed class CaptureWindowContextBackend(
        IProcessExecutionBackend         inner,
        Action<AuthorizedReadWindowHome?> capture) : IProcessExecutionBackend
    {
        public Task<ProcessExecutionResult> ExecuteAsync(
            PreparedProcessExecution execution, IProcessOutputCapture stdoutCapture,
            IProcessOutputCapture stderrCapture, CancellationToken cancellationToken)
        {
            capture(AuthorizedReadWindowContext.Current);
            return inner.ExecuteAsync(execution, stdoutCapture, stderrCapture, cancellationToken);
        }
    }

    private sealed class CancelThenThrowUncertainBackend(CancellationTokenSource cts) : IProcessExecutionBackend
    {
        public Task<ProcessExecutionResult> ExecuteAsync(
            PreparedProcessExecution execution, IProcessOutputCapture stdoutCapture,
            IProcessOutputCapture stderrCapture, CancellationToken cancellationToken)
        {
            cts.Cancel();
            throw new ToolExecutionUncertainException("whole-tree exit confirmation was cancelled");
        }
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
        var store          = new AuthorizedReadDisableFile(home.Components, home.Components.CredentialsDirectory);
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

/// <summary>在每次 refresh 调用时执行回调并转发给内层 invoker 的测试包装。 A test wrapper running a callback on each refresh before forwarding to the inner invoker.</summary>
internal sealed class CallbackSetupInvoker(
    ISandboxSetupInvoker                                       inner,
    Func<SandboxSetupPayload, CancellationToken, Task>         onRefresh) : ISandboxSetupInvoker
{
    public async Task RefreshAsync(SandboxSetupPayload payload, CancellationToken cancellationToken)
    {
        await onRefresh(payload, cancellationToken).ConfigureAwait(false);
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

    public List<string> AcquiredHomes { get; } = [];

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
        AcquiredHomes.Add(sandboxHome);
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

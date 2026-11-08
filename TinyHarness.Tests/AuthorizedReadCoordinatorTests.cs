using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         授权读协调器的三道门单元测试（假锁/假 refresh/假后端）：普通执行透传零变化、能力门
///         按部署代拒绝授权执行、串行门在窗口被占时可见超时、恢复刷新持窗口执行、恢复失败或回收
///         未知持久化禁用并在入口阻断、恢复成功清除禁用。全部离线，不触碰机器账户或真实 ACL。
///     </para>
///     <para>
///         Unit tests for the authorized-read coordinator's three gates (fake lock, fake refresh, fake
///         backend): normal executions pass through unchanged, the capability gate refuses authorized
///         executions by deployment generation, the serialization gate times out visibly while the
///         window is held, the recovery refresh runs inside the window, restore failures and unknown
///         reclamation persist the disable state and block later entries, and a successful restore
///         clears the disable. Everything runs offline without touching machine accounts or real ACLs.
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

    // ---------------------------------------------------------------- 装置

    /// <summary>授权执行使用的 deny 条目路径。 The deny entry path an authorized execution reads.</summary>
    private static string DenyReadPath(SandboxTestHome home) =>
        Path.Combine(home.Components.SandboxHome, "sensitive");

    /// <summary>固定 v7 部署代探测。 A probe reporting the authorized generation.</summary>
    private static Task<uint> ProvisionedV7(CancellationToken cancellationToken) =>
        Task.FromResult<uint>(WindowsSandboxComponents.AuthorizedSetupVersion);

    /// <summary>构造协调器与其协作件（假锁/假 refresh/假后端/真实文件禁用存储）；Restore 返回传入的调用器原样引用。</summary>
    private static (AuthorizedReadCoordinator Coordinator, FakeProcessExecutionBackend Inner,
        ISandboxSetupInvoker Restore, ScriptedWindowLock Lock, AuthorizedReadDisableFile Store) CreateCoordinator(
        SandboxTestHome                       home,
        Func<CancellationToken, Task<uint>>?  probe        = null,
        IAuthorizedReadWindowLock?            windowLock   = null,
        ISandboxSetupInvoker?                 restore      = null,
        IProcessExecutionBackend?             innerBackend = null)
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
                                                        capabilityProbe : probe);
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

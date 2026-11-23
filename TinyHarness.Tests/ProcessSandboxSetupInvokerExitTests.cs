using System.Diagnostics;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         每命令 refresh 调用器的退出契约定向测试（真实子进程 stub）：正常退出与非零退出行为
///         不变；超时/取消路径在 Kill 与确认等待成功后保持原 TimeoutException/OCE 语义，终止失败
///         但退出已确认（接缝注入）时同样保持原语义；终止或确认等待失败（测试接缝注入）时退出
///         状态未知——抛出携带后台确认任务的不确定异常，stub 存活期间确认任务不完成，stub 退出后
///         完成 true；初次等待的非取消异常（接缝注入）同样收敛到三态——终止后确认退出则原异常
///         原样上抛，未确认则按退出未知处理。全部离线，不触碰机器状态。
///     </para>
///     <para>
///         Targeted tests for the per-command refresh invoker's exit contract (real subprocess
///         stubs): normal and nonzero exits keep their behavior; the timeout and cancellation paths
///         keep the original TimeoutException/OCE semantics once the kill and the confirmation wait
///         succeed, and so does a termination failure with a confirmed exit (seam-injected); with
///         termination or confirmation-wait failures injected through the test seam the exit state
///         is unknown — an uncertainty exception carrying the background confirmation task is
///         thrown, the task stays incomplete while the stub lives and completes true after the stub
///         exits; a non-cancellation failure of the initial wait (seam-injected) converges onto the
///         same three states — the original exception is rethrown as-is once the exit is confirmed
///         after termination, and is handled as exit-unknown otherwise. Everything runs offline
///         without touching machine state.
///     </para>
/// </summary>
public class ProcessSandboxSetupInvokerExitTests
{
    [Test]
    public async Task RefreshAsync_FastExitZero_ReturnsNormally()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The process-backed invoker is Windows-only.");

        using var directory = new TestTempDir();
        WriteStub(directory, aliveSeconds: 0, exitCode: 0);
        var (invoker, _) = CreateInvoker(directory, TimeSpan.FromSeconds(5));

        await invoker.RefreshAsync(Payload(directory.Root), CancellationToken.None);
    }

    [Test]
    public async Task RefreshAsync_NonZeroExit_ThrowsInvalidOperationExceptionWithExitCode()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The process-backed invoker is Windows-only.");

        using var directory = new TestTempDir();
        WriteStub(directory, aliveSeconds: 0, exitCode: 3);
        var (invoker, _) = CreateInvoker(directory, TimeSpan.FromSeconds(5));

        var error = await TestAssert.ThrowsAsync<InvalidOperationException>(
            () => invoker.RefreshAsync(Payload(directory.Root), CancellationToken.None));

        TestAssert.Contains("exit code 3", error!.Message, StringComparison.Ordinal);
    }

    [Test]
    public async Task RefreshAsync_TimeoutWithConfirmedExit_ThrowsTimeoutExceptionWithOriginalMessage()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The process-backed invoker is Windows-only.");

        using var directory = new TestTempDir();
        WriteStub(directory, aliveSeconds: 15, exitCode: 0);
        var (invoker, _) = CreateInvoker(directory, TimeSpan.FromMilliseconds(300));

        var error = await TestAssert.ThrowsAsync<TimeoutException>(
            () => invoker.RefreshAsync(Payload(directory.Root), CancellationToken.None));

        // 确认退出：保持原 TimeoutException 语义与原消息，不携带确认任务。
        Assert.Multiple(() =>
        {
            TestAssert.Contains("did not finish refreshing ACLs within", error!.Message, StringComparison.Ordinal);
            Assert.That(error, Is.Not.InstanceOf<SandboxRefreshExitUncertainException>());
            Assert.That(error.InnerException, Is.Null);
        });
    }

    [Test]
    public async Task RefreshAsync_UserCancelWithConfirmedExit_ThrowsOriginalCancellation()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The process-backed invoker is Windows-only.");

        using var directory = new TestTempDir();
        WriteStub(directory, aliveSeconds: 15, exitCode: 0);
        var (invoker, _) = CreateInvoker(directory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var error = await Assert.CatchAsync<OperationCanceledException>(
            () => invoker.RefreshAsync(Payload(directory.Root), cancellation.Token));

        // 确认退出：原取消语义原样上抛，不换成不确定取消子类。
        Assert.That(error, Is.Not.InstanceOf<SandboxRefreshExitUncertainCancellationException>());
    }

    [Test]
    public async Task RefreshAsync_TerminationFailureInjection_ThrowsUncertainTimeoutAndConfirmsAfterKill()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The process-backed invoker is Windows-only.");

        using var directory = new TestTempDir();
        WriteStub(directory, aliveSeconds: 15, exitCode: 0);
        var (invoker, _) = CreateInvoker(directory, TimeSpan.FromMilliseconds(300));
        var injected     = new InvalidOperationException("termination failed by injection");
        Process? captured = null;
        invoker.TerminationForTesting = process =>
        {
            captured = process;
            return Task.FromResult(new SandboxHelperTermination(false, injected));
        };

        var uncertain = await TestAssert.ThrowsAsync<SandboxRefreshExitUncertainException>(
            () => invoker.RefreshAsync(Payload(directory.Root), CancellationToken.None));

        // 退出未知：超时派生、原消息加退出未知说明、注入异常作 inner。
        Assert.Multiple(() =>
        {
            Assert.That(uncertain, Is.InstanceOf<TimeoutException>());
            TestAssert.Contains("did not finish refreshing ACLs within", uncertain!.Message, StringComparison.Ordinal);
            TestAssert.Contains("exit state is unknown", uncertain.Message, StringComparison.Ordinal);
            Assert.That(uncertain.InnerException, Is.SameAs(injected));
        });

        // stub 存活期间确认任务不完成；测试终止 stub 后在时限内完成 true（真实后台确认仍工作）。
        TestAssert.NotNull(captured);
        Assert.That(uncertain.ExitConfirmation.IsCompleted, Is.False);
        await Task.Delay(500);
        Assert.That(uncertain.ExitConfirmation.IsCompleted, Is.False);
        captured!.Kill();
        Assert.That(await uncertain.ExitConfirmation.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);
    }

    [Test]
    public async Task RefreshAsync_ConfirmationWaitFailureInjection_ThrowsUncertainTimeoutWithNullInner()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The process-backed invoker is Windows-only.");

        using var directory = new TestTempDir();
        WriteStub(directory, aliveSeconds: 15, exitCode: 0);
        var (invoker, _) = CreateInvoker(directory, TimeSpan.FromMilliseconds(300));
        Process? captured = null;
        invoker.TerminationForTesting = process =>
        {
            captured = process;
            return Task.FromResult(new SandboxHelperTermination(false, null));
        };

        var uncertain = await TestAssert.ThrowsAsync<SandboxRefreshExitUncertainException>(
            () => invoker.RefreshAsync(Payload(directory.Root), CancellationToken.None));

        // 确认等待失败（无终止失败）：同样抛不确定异常且 inner 为 null；真实后台确认仍工作。
        Assert.Multiple(() =>
        {
            TestAssert.Contains("exit state is unknown", uncertain!.Message, StringComparison.Ordinal);
            Assert.That(uncertain.InnerException, Is.Null);
        });

        TestAssert.NotNull(captured);
        Assert.That(uncertain.ExitConfirmation.IsCompleted, Is.False);
        await Task.Delay(500);
        Assert.That(uncertain.ExitConfirmation.IsCompleted, Is.False);
        captured!.Kill();
        Assert.That(await uncertain.ExitConfirmation.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);
    }

    [Test]
    public async Task RefreshAsync_CancelWithTerminationFailureInjection_ThrowsUncertainCancellationPreservingToken()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The process-backed invoker is Windows-only.");

        using var directory = new TestTempDir();
        WriteStub(directory, aliveSeconds: 15, exitCode: 0);
        var (invoker, _) = CreateInvoker(directory);
        var injected     = new InvalidOperationException("termination failed by injection");
        Process? captured = null;
        invoker.TerminationForTesting = process =>
        {
            captured = process;
            return Task.FromResult(new SandboxHelperTermination(false, injected));
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var uncertain = await TestAssert.ThrowsAsync<SandboxRefreshExitUncertainCancellationException>(
            () => invoker.RefreshAsync(Payload(directory.Root), cancellation.Token));

        // 退出未知的取消形态：保留原 token（上层取消语义不变）、消息含退出未知说明、注入异常作 inner。
        Assert.Multiple(() =>
        {
            Assert.That(uncertain, Is.InstanceOf<OperationCanceledException>());
            TestAssert.Contains("exit state is unknown", uncertain!.Message, StringComparison.Ordinal);
            Assert.That(uncertain.CancellationToken, Is.EqualTo(cancellation.Token));
            Assert.That(uncertain.InnerException, Is.Null);
        });

        TestAssert.NotNull(captured);
        captured!.Kill();
        Assert.That(await uncertain.ExitConfirmation.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);
    }

    [Test]
    public async Task RefreshAsync_InitialWaitFailureWithConfirmedExit_RethrowsOriginalException()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The process-backed invoker is Windows-only.");

        using var directory = new TestTempDir();
        WriteStub(directory, aliveSeconds: 15, exitCode: 0);
        var (invoker, _) = CreateInvoker(directory, TimeSpan.FromSeconds(5));
        var injected     = new InvalidOperationException("initial wait failed by injection");
        var capturedPid  = 0;
        invoker.InitialWaitForTesting = (_, _) => throw injected;
        invoker.TerminationForTesting = process =>
        {
            capturedPid = process.Id;
            return Task.FromResult(new SandboxHelperTermination(true, null));
        };

        var error = await TestAssert.ThrowsAsync<InvalidOperationException>(
            () => invoker.RefreshAsync(Payload(directory.Root), CancellationToken.None));

        // 初次等待失败但终止后确认退出：原异常原样上抛（退出已确认，门释放安全），不换成不确定异常。
        Assert.Multiple(() =>
        {
            Assert.That(error, Is.SameAs(injected));
            Assert.That(error, Is.Not.InstanceOf<SandboxRefreshExitUncertainException>());
        });

        // 终止步骤被接缝替换，stub 未被真实 Kill；确认退出路径的进程对象已随 finally 释放，
        // 收尾按 PID 终止并等待退出，不留存活进程。
        Assert.That(capturedPid, Is.Not.EqualTo(0));
        using var leftover = Process.GetProcessById(capturedPid);
        leftover.Kill(true);
        Assert.That(leftover.WaitForExit(TimeSpan.FromSeconds(10)), Is.True);
    }

    [Test]
    public async Task RefreshAsync_InitialWaitFailureWithUnconfirmedExit_ThrowsUncertainTimeoutCarryingOriginalInner()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The process-backed invoker is Windows-only.");

        using var directory = new TestTempDir();
        WriteStub(directory, aliveSeconds: 15, exitCode: 0);
        var (invoker, _) = CreateInvoker(directory, TimeSpan.FromSeconds(5));
        var injected     = new InvalidOperationException("initial wait failed by injection");
        Process? captured = null;
        invoker.InitialWaitForTesting = (_, _) => throw injected;
        invoker.TerminationForTesting = process =>
        {
            captured = process;
            return Task.FromResult(new SandboxHelperTermination(false, null));
        };

        var uncertain = await TestAssert.ThrowsAsync<SandboxRefreshExitUncertainException>(
            () => invoker.RefreshAsync(Payload(directory.Root), CancellationToken.None));

        // 初次等待失败且退出未确认：超时形态派生、消息含退出未知说明、原等待异常作 inner，
        // 并携带后台确认任务（与超时路径的退出未知形态一致）。
        Assert.Multiple(() =>
        {
            Assert.That(uncertain, Is.InstanceOf<TimeoutException>());
            TestAssert.Contains("exit state is unknown", uncertain!.Message, StringComparison.Ordinal);
            Assert.That(uncertain.InnerException, Is.SameAs(injected));
        });

        // stub 存活期间确认任务不完成；测试终止 stub 后在时限内完成 true（真实后台确认仍工作）。
        TestAssert.NotNull(captured);
        Assert.That(uncertain.ExitConfirmation.IsCompleted, Is.False);
        await Task.Delay(500);
        Assert.That(uncertain.ExitConfirmation.IsCompleted, Is.False);
        captured!.Kill();
        Assert.That(await uncertain.ExitConfirmation.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);
    }

    [Test]
    public async Task RefreshAsync_CancelledWaitViaInitialSeam_KeepsOriginalCancellationSemantics()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The process-backed invoker is Windows-only.");

        using var directory = new TestTempDir();
        WriteStub(directory, aliveSeconds: 15, exitCode: 0);
        var (invoker, _) = CreateInvoker(directory, TimeSpan.FromSeconds(5));
        var capturedPid  = 0;
        invoker.InitialWaitForTesting = (_, token) => throw new OperationCanceledException(token);
        invoker.TerminationForTesting = process =>
        {
            capturedPid = process.Id;
            return Task.FromResult(new SandboxHelperTermination(true, null));
        };

        var error = await Assert.CatchAsync<OperationCanceledException>(
            () => invoker.RefreshAsync(Payload(directory.Root), CancellationToken.None));

        // 经同一接缝注入的取消仍命中取消分支：原取消语义不变，不进入初次等待失败分支。
        Assert.That(error, Is.Not.InstanceOf<SandboxRefreshExitUncertainCancellationException>());

        // 终止步骤被接缝替换，stub 未被真实 Kill；确认退出路径的进程对象已随 finally 释放，
        // 收尾按 PID 终止并等待退出，不留存活进程。
        Assert.That(capturedPid, Is.Not.EqualTo(0));
        using var leftover = Process.GetProcessById(capturedPid);
        leftover.Kill(true);
        Assert.That(leftover.WaitForExit(TimeSpan.FromSeconds(10)), Is.True);
    }

    [Test]
    public async Task RefreshAsync_TerminationFailureInjectionWithConfirmedExit_KeepsTimeoutSemantics()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The process-backed invoker is Windows-only.");

        using var directory = new TestTempDir();
        WriteStub(directory, aliveSeconds: 15, exitCode: 0);
        var (invoker, _) = CreateInvoker(directory, TimeSpan.FromMilliseconds(300));
        var injected     = new InvalidOperationException("termination failed by injection");
        var capturedPid  = 0;
        invoker.TerminationForTesting = process =>
        {
            capturedPid = process.Id;
            return Task.FromResult(new SandboxHelperTermination(true, injected));
        };

        var error = await TestAssert.ThrowsAsync<TimeoutException>(
            () => invoker.RefreshAsync(Payload(directory.Root), CancellationToken.None));

        // 终止失败但确认退出：原 TimeoutException 语义与原消息保持不变，失败按契约丢弃不转为
        // 不确定异常，也不作为 inner 上抛（真实路径此时补记一条诊断事件）。
        Assert.Multiple(() =>
        {
            TestAssert.Contains("did not finish refreshing ACLs within", error!.Message, StringComparison.Ordinal);
            Assert.That(error, Is.Not.InstanceOf<SandboxRefreshExitUncertainException>());
            Assert.That(error.InnerException, Is.Null);
        });

        // 终止步骤被接缝替换，stub 未被真实 Kill；确认退出路径的进程对象已随 finally 释放，
        // 收尾按 PID 终止并等待退出，不留存活进程。
        Assert.That(capturedPid, Is.Not.EqualTo(0));
        using var leftover = Process.GetProcessById(capturedPid);
        leftover.Kill(true);
        Assert.That(leftover.WaitForExit(TimeSpan.FromSeconds(10)), Is.True);
    }

    // ---------------------------------------------------------------- 装置

    /// <summary>
    ///     <para>
    ///         写入真实子进程 stub（cmd 批处理）：ping -n N+1 每秒一次使进程存活约 N 秒后按指定
    ///         退出码退出；stub 接收 invoker 传入的 base64 payload 参数但不使用。
    ///     </para>
    ///     <para>
    ///         Writes the real subprocess stub (a cmd batch file): ping -n N+1 keeps the process
    ///         alive for roughly N seconds before exiting with the given code; the stub receives the
    ///         invoker's base64 payload argument but ignores it.
    ///     </para>
    /// </summary>
    private static void WriteStub(TestTempDir directory, int aliveSeconds, int exitCode) =>
        directory.WriteFile("helper-stub.cmd",
                            "@echo off\r\n" +
                            (aliveSeconds > 0 ? $"ping -n {aliveSeconds + 1} 127.0.0.1 >nul\r\n" : string.Empty) +
                            $"exit /b {exitCode}\r\n");

    /// <summary>构造指向 stub 的进程调用器。 Builds the process invoker pointing at the stub.</summary>
    private static (ProcessSandboxSetupInvoker Invoker, WindowsSandboxComponents Components) CreateInvoker(
        TestTempDir directory, TimeSpan? refreshTimeout = null)
    {
        var components = new WindowsSandboxComponents
        {
            SetupExecutablePath  = Path.Combine(directory.Root, "helper-stub.cmd"),
            RunnerExecutablePath = Path.Combine(directory.Root, "runner.exe"),
            SandboxHome          = directory.Root
        };
        return (new ProcessSandboxSetupInvoker(components, refreshTimeout), components);
    }

    /// <summary>构造最小 refresh payload（refresh_only=true）。 Builds a minimal refresh payload (refresh_only=true).</summary>
    private static SandboxSetupPayload Payload(string workingDirectory) => new()
    {
        OfflineUsername         = "TinyHarnessOffline",
        OnlineUsername          = "TinyHarnessOnline",
        RealUser                = "TinyHarnessHost",
        SandboxHome             = workingDirectory,
        CommandWorkingDirectory = workingDirectory,
        ReadRoots               = [],
        WriteRoots              = [workingDirectory],
        RefreshOnly             = true
    };
}

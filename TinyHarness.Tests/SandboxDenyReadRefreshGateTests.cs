using System.Diagnostics;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         拒读刷新门的单元测试：同一 home 的互斥获取与释放后重获、不同 home 的键控独立、
///         home 归一化（大小写与尾部分隔符形态）同名、命名确定性、等待取消、junction 别名与
///         原路径同锁收敛、不可解析 home 抛错拒绝 refresh，以及 helper 退出确认的释放语义
///         （true 归还、false/取消/失败 fail-closed 保持持有、转移后 Dispose no-op）。
///         放弃互斥体（AbandonedMutexException）路径不做进程内单测——同进程无法可靠制造放弃，
///         实现复用已验证的授权窗口锁 worker 模式即可。
///     </para>
///     <para>
///         Unit tests for the deny-read refresh gate: mutual exclusion on one home with reacquisition
///         after release, key isolation across homes, the same mutex name across casing and
///         trailing-separator home forms, deterministic naming, wait cancellation, junction-alias
///         convergence onto one mutex with the original path, an unresolvable home throwing and
///         refusing the refresh, and the helper-exit-confirmation release semantics (true returns
///         the gate; false, cancellation, and faults keep it held fail-closed; Dispose is a no-op
///         after the ownership transfer). The abandoned mutex (AbandonedMutexException) path has no
///         in-process unit test — the same process cannot reliably manufacture an abandonment; the
///         implementation reuses the already-verified authorization window lock's worker pattern.
///     </para>
/// </summary>
public class SandboxDenyReadRefreshGateTests
{
    [Test]
    public void TryAcquireAsync_SameHome_SecondAcquireTimesOutAndReleaseAllowsReacquisition()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The deny-read refresh gate mutex is Windows-only.");
        using var home = new TestTempDir();
        var       gate = new GlobalDenyReadRefreshGate();
        var first = gate.TryAcquireAsync(home.Root, TimeSpan.FromSeconds(5), CancellationToken.None)
                             .GetAwaiter()
                             .GetResult();
        try
        {
            TestAssert.NotNull(first);

            // 同一 home 的第二个请求在短时限内拿不到门：互斥成立，超时返回 null 由调用方 fail-closed。
            var second = gate.TryAcquireAsync(home.Root, TimeSpan.FromMilliseconds(300), CancellationToken.None)
                             .GetAwaiter()
                             .GetResult();
            Assert.That(second, Is.Null);

            // 释放后第三个请求重新获得门：Dispose 归还成立。
            first!.Dispose();
            var third = gate.TryAcquireAsync(home.Root, TimeSpan.FromSeconds(5), CancellationToken.None)
                            .GetAwaiter()
                            .GetResult();
            TestAssert.NotNull(third);
            third!.Dispose();
            // 首个句柄在 try 内已 Dispose，finally 再 Dispose 检查释放是幂等信号而非重复归还。
        }
        finally
        {
            first?.Dispose();
        }
    }

    [Test]
    public void TryAcquireAsync_DifferentHomes_AreHeldConcurrentlyWithoutBlocking()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The deny-read refresh gate mutex is Windows-only.");
        using var homeA = new TestTempDir();
        using var homeB = new TestTempDir();
        var       gate  = new GlobalDenyReadRefreshGate();
        var a = gate.TryAcquireAsync(homeA.Root, TimeSpan.FromSeconds(5), CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
        var b = gate.TryAcquireAsync(homeB.Root, TimeSpan.FromMilliseconds(300), CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
        try
        {
            // 键控按 home：A 持有期间 B 以短时限仍获得自己的门，两个门并发持有互不阻塞；
            // 键控身份为最终落点，两个不同实际 home 经最终路径解析仍各自独立。
            TestAssert.NotNull(a);
            TestAssert.NotNull(b);
        }
        finally
        {
            a?.Dispose();
            b?.Dispose();
        }
    }

    [Test]
    public void MutexNameForHome_IsDeterministicAndNormalizesCasingAndTrailingSeparator()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The deny-read refresh gate mutex is Windows-only.");
        using var home      = new TestTempDir();
        var       canonical = GlobalDenyReadRefreshGate.MutexNameForHome(home.Root);
        var       prefix    = @"Global\TinyHarness-DenyRead-Refresh-";
        Assert.Multiple(() =>
        {
            // 名字计算确定性：同输入同名（最终路径解析幂等 + SHA-256 键控）。
            Assert.That(GlobalDenyReadRefreshGate.MutexNameForHome(home.Root), Is.EqualTo(canonical));
            // 大小写与尾部分隔符形态归一到同一互斥体名；哈希后缀为 16 位大写十六进制。
            Assert.That(GlobalDenyReadRefreshGate.MutexNameForHome(home.Root.ToUpperInvariant()), Is.EqualTo(canonical));
            Assert.That(GlobalDenyReadRefreshGate.MutexNameForHome(home.Root.ToLowerInvariant()), Is.EqualTo(canonical));
            Assert.That(GlobalDenyReadRefreshGate.MutexNameForHome(home.Root + Path.DirectorySeparatorChar),
                        Is.EqualTo(canonical));
            Assert.That(canonical, Does.StartWith(prefix));
            Assert.That(canonical.Length, Is.EqualTo(prefix.Length + 16));
        });
    }

    [Test]
    public async Task TryAcquireAsync_CancelledWhileWaiting_ThrowsOperationCanceled()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The deny-read refresh gate mutex is Windows-only.");
        using var home = new TestTempDir();
        var       gate = new GlobalDenyReadRefreshGate();
        var first = gate.TryAcquireAsync(home.Root, TimeSpan.FromSeconds(5), CancellationToken.None)
                             .GetAwaiter()
                             .GetResult();
        TestAssert.NotNull(first);
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

            // 等待尊重取消：50ms 轮询粒度内抛出取消，而非占用等待全程。
            await Assert.CatchAsync<OperationCanceledException>(() => gate.TryAcquireAsync(home.Root,
                                                                                           TimeSpan.FromSeconds(30),
                                                                                           cancellation.Token));
        }
        finally
        {
            first?.Dispose();
        }
    }

    [Test]
    public void TryAcquireAsync_JunctionAliasAndOriginalPath_ShareTheSameMutex()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The deny-read refresh gate mutex is Windows-only.");
        using var home     = new TestTempDir();
        using var linkHome = new TestTempDir();
        var       junction = Path.Combine(linkHome.Root, "alias");
        CreateDirectoryJunction(junction, home.Root);
        try
        {
            // 名字收敛：junction 别名与原路径解析到同一最终落点，取得同一互斥体名。
            Assert.That(GlobalDenyReadRefreshGate.MutexNameForHome(junction),
                        Is.EqualTo(GlobalDenyReadRefreshGate.MutexNameForHome(home.Root)));

            var gate  = new GlobalDenyReadRefreshGate();
            var first = gate.TryAcquireAsync(home.Root, TimeSpan.FromSeconds(5), CancellationToken.None)
                             .GetAwaiter()
                             .GetResult();
            try
            {
                TestAssert.NotNull(first);

                // 原路径持门时 junction 别名短时限获取返回 null：两个形态确实共用同一互斥体。
                var viaAlias = gate.TryAcquireAsync(junction, TimeSpan.FromMilliseconds(300), CancellationToken.None)
                                  .GetAwaiter()
                                  .GetResult();
                Assert.That(viaAlias, Is.Null);
            }
            finally
            {
                first?.Dispose();
            }

            // 释放后经 junction 别名可获得同一把门：别名与原路径互斥同体。
            var reacquired = gate.TryAcquireAsync(junction, TimeSpan.FromSeconds(5), CancellationToken.None)
                                 .GetAwaiter()
                                 .GetResult();
            try
            {
                TestAssert.NotNull(reacquired);
            }
            finally
            {
                reacquired?.Dispose();
            }
        }
        finally
        {
            Directory.Delete(junction, false);
        }
    }

    [Test]
    public void MutexNameForHome_UnresolvableHome_ThrowsInvalidOperationException()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The deny-read refresh gate mutex is Windows-only.");
        var missing = Path.Combine(Path.GetTempPath(), "tinyharness-gate-missing-" + Guid.NewGuid().ToString("N"));

        // 目录身份无法确定（最终路径解析失败）：抛出包含 home 原路径与拒绝 refresh 语义的异常。
        var exception =
            TestAssert.NotNull(Assert.Catch<InvalidOperationException>(
                () => GlobalDenyReadRefreshGate.MutexNameForHome(missing)));
        Assert.Multiple(() =>
        {
            Assert.That(exception.Message, Does.Contain(missing));
            Assert.That(exception.Message, Does.Contain("refused"));
        });
    }

    [Test]
    public void TryAcquireAsync_UnresolvableHome_PropagatesInvalidOperationException()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The deny-read refresh gate mutex is Windows-only.");
        var missing = Path.Combine(Path.GetTempPath(), "tinyharness-gate-missing-" + Guid.NewGuid().ToString("N"));
        var gate    = new GlobalDenyReadRefreshGate();

        // 同一异常从 TryAcquireAsync 同步传播：调用方（backend/授权协调器）据此拒绝本次 refresh。
        var exception =
            TestAssert.NotNull(Assert.Catch<InvalidOperationException>(() => gate.TryAcquireAsync(missing,
                                                                                                  TimeSpan.FromSeconds(1),
                                                                                                  CancellationToken.None)));
        Assert.Multiple(() =>
        {
            Assert.That(exception.Message, Does.Contain(missing));
            Assert.That(exception.Message, Does.Contain("refused"));
        });
    }

    [Test]
    public void ReleaseWhenConfirmed_HelperExitConfirmedTrue_ReturnsTheGate()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The deny-read refresh gate mutex is Windows-only.");
        using var home = new TestTempDir();
        var       gate = new GlobalDenyReadRefreshGate();
        var first = gate.TryAcquireAsync(home.Root, TimeSpan.FromSeconds(5), CancellationToken.None)
                             .GetAwaiter()
                             .GetResult();
        TestAssert.NotNull(first);
        try
        {
            // 已完成确认（true）同步处理：所有权转移后 Dispose 是 no-op，门经确认路径归还，
            // 第二次获取（第三者视角）随后可获得。
            first!.ReleaseWhenConfirmed(Task.FromResult(true));
            first.Dispose();

            var second = gate.TryAcquireAsync(home.Root, TimeSpan.FromSeconds(5), CancellationToken.None)
                             .GetAwaiter()
                             .GetResult();
            TestAssert.NotNull(second);
            second!.Dispose();
        }
        finally
        {
            // 所有权已转移：Dispose 为 no-op，不会重复归还或破坏上面的获取-释放序列。
            first?.Dispose();
        }
    }

    [Test]
    public void ReleaseWhenConfirmed_HelperExitResultFalse_KeepsTheGateHeld()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The deny-read refresh gate mutex is Windows-only.");
        using var home = new TestTempDir();
        var       gate = new GlobalDenyReadRefreshGate();
        var first = gate.TryAcquireAsync(home.Root, TimeSpan.FromSeconds(5), CancellationToken.None)
                             .GetAwaiter()
                             .GetResult();
        TestAssert.NotNull(first);
        try
        {
            // false 不是确认退出：fail-closed 保持持有，转移后的 Dispose 也无法把门放掉。
            first!.ReleaseWhenConfirmed(Task.FromResult(false));
            first.Dispose();

            var second = gate.TryAcquireAsync(home.Root, TimeSpan.FromMilliseconds(300), CancellationToken.None)
                             .GetAwaiter()
                             .GetResult();
            Assert.That(second, Is.Null);
        }
        finally
        {
            // no-op（所有权已转移）：该 home 唯一，fail-closed 持锁到进程退出；worker 是后台线程
            // 不阻塞测试进程退出，也不与其他用例的互斥体串扰。
            first?.Dispose();
        }
    }

    [Test]
    public void ReleaseWhenConfirmed_FaultedConfirmation_KeepsTheGateHeld()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The deny-read refresh gate mutex is Windows-only.");
        using var home = new TestTempDir();
        var       gate = new GlobalDenyReadRefreshGate();
        var first = gate.TryAcquireAsync(home.Root, TimeSpan.FromSeconds(5), CancellationToken.None)
                             .GetAwaiter()
                             .GetResult();
        TestAssert.NotNull(first);
        try
        {
            // 任务失败同样不是确认退出：fail-closed 保持持有，短时限获取拿不到门。
            first!.ReleaseWhenConfirmed(Task.FromException<bool>(new InvalidOperationException("helper exit unconfirmed")));
            first.Dispose();

            var second = gate.TryAcquireAsync(home.Root, TimeSpan.FromMilliseconds(300), CancellationToken.None)
                             .GetAwaiter()
                             .GetResult();
            Assert.That(second, Is.Null);
        }
        finally
        {
            // no-op（所有权已转移）：持锁语义与 false 结果一致，见上一用例说明。
            first?.Dispose();
        }
    }

    [Test]
    public void ReleaseWhenConfirmed_CanceledConfirmation_KeepsTheGateHeld()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The deny-read refresh gate mutex is Windows-only.");
        using var home = new TestTempDir();
        var       gate = new GlobalDenyReadRefreshGate();
        var first = gate.TryAcquireAsync(home.Root, TimeSpan.FromSeconds(5), CancellationToken.None)
                             .GetAwaiter()
                             .GetResult();
        TestAssert.NotNull(first);
        try
        {
            // 已取消的确认任务同样不是确认退出：fail-closed 保持持有，短时限获取拿不到门。
            first!.ReleaseWhenConfirmed(Task.FromCanceled<bool>(new CancellationToken(true)));
            first.Dispose();

            var second = gate.TryAcquireAsync(home.Root, TimeSpan.FromMilliseconds(300), CancellationToken.None)
                             .GetAwaiter()
                             .GetResult();
            Assert.That(second, Is.Null);
        }
        finally
        {
            // no-op（所有权已转移）：持锁语义与 false/失败结果一致，见上两用例说明。
            first?.Dispose();
        }
    }

    [Test]
    public void ReleaseWhenConfirmed_PendingConfirmationThenDispose_GateNotReleasedEarlyAndConfirmTrueReturnsIt()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The deny-read refresh gate mutex is Windows-only.");
        using var home = new TestTempDir();
        var       gate = new GlobalDenyReadRefreshGate();
        var first = gate.TryAcquireAsync(home.Root, TimeSpan.FromSeconds(5), CancellationToken.None)
                             .GetAwaiter()
                             .GetResult();
        TestAssert.NotNull(first);
        var confirmation = new TaskCompletionSource<bool>();
        try
        {
            // 未完成确认 + 立刻 Dispose：所有权已转移，Dispose 是 no-op，门不被提前放掉。
            first!.ReleaseWhenConfirmed(confirmation.Task);
            first.Dispose();

            var premature = gate.TryAcquireAsync(home.Root, TimeSpan.FromMilliseconds(500), CancellationToken.None)
                                .GetAwaiter()
                                .GetResult();
            Assert.That(premature, Is.Null);

            // 确认完成后门归还：转移后的释放时机由确认任务决定。
            confirmation.SetResult(true);
            var reacquired = gate.TryAcquireAsync(home.Root, TimeSpan.FromSeconds(5), CancellationToken.None)
                                 .GetAwaiter()
                                 .GetResult();
            TestAssert.NotNull(reacquired);
            reacquired!.Dispose();
        }
        finally
        {
            // 兜底放行确认，保证 worker 线程退出、互斥体不遗留持锁状态（断言失败提前离开时同样成立）。
            confirmation.TrySetResult(true);
            first?.Dispose();
        }
    }

    /// <summary>
    ///     <para>用 mklink /J 创建指向目标的目录 junction（不需要管理员权限）。</para>
    ///     <para>Creates a directory junction pointing at the target via mklink /J (no administrator privileges required).</para>
    /// </summary>
    private static void CreateDirectoryJunction(string link, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow         = true,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        using var process = Process.Start(startInfo)!;
        var       error   = process.StandardError.ReadToEnd().Trim();
        Assert.That(process.WaitForExit(10_000), Is.True, $"mklink /J did not finish in time: {error}");
        Assert.That(process.ExitCode == 0, Is.True, $"mklink /J failed: {error}");
    }
}

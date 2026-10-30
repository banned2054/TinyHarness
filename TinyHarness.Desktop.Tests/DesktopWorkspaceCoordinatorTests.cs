using NUnit.Framework;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Desktop.Runtime;

namespace TinyHarness.Desktop.Tests;

/// <summary>
///     <para>离线验证修复互斥、未知授权闩锁与关闭取消；不启动 UAC 或修改 ACL。</para>
///     <para>
///         Verifies repair exclusion, unknown-grant latching and shutdown cancellation offline without UAC or ACL
///         changes.
///     </para>
/// </summary>
public sealed class DesktopWorkspaceCoordinatorTests
{
    [Test]
    public void OverlappingRoots_RunAndRepairAreMutuallyExclusive()
    {
        var scope = new DesktopWorkspaceOperationScope();
        var root  = Path.Combine(Path.GetTempPath(), "tinyharness-coordinator-" + Guid.NewGuid().ToString("N"));
        var child = Path.Combine(root, "child");
        Directory.CreateDirectory(child);
        var outside = Path.Combine(Path.GetTempPath(), "tinyharness-unrelated-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var run = scope.EnterRun(child))
            {
                Assert.Throws<InvalidOperationException>(() => scope.EnterRepair(root),
                                                         "父根修复必须等待子根运行结束。");
                Assert.Throws<InvalidOperationException>(() => scope.EnterRepair(child),
                                                         "同根修复必须等待运行结束。");
                using var parallelRun = scope.EnterRun(Path.Combine(root, "other"));
            }

            using (var repair = scope.EnterRepair(root))
            {
                Assert.Throws<InvalidOperationException>(() => scope.EnterRun(child),
                                                         "运行不得进入正在修复的子目录。");
                Assert.Throws<InvalidOperationException>(() => scope.EnterRun(root),
                                                         "运行不得进入正在修复的根目录。");
                Assert.Throws<InvalidOperationException>(() => scope.EnterRepair(root),
                                                         "重叠修复不可重入。");
                using var unrelated = scope.EnterRun(outside);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ElevationGrant_ThrowingMidFlight_LatchesUnknownAndBlocksRuns()
    {
        var root = Path.Combine(Path.GetTempPath(), "tinyharness-coordinator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var identity = WorkspaceDirectoryIdentity.Capture(root);
            var scope    = new DesktopWorkspaceOperationScope();
            var coordinator = new DesktopWorkspaceCoordinator(identity,
                                                              new DesktopWorkspaceRepairServices(_ =>
                                                                      Task.FromResult(false), new ThrowingGrant(),
                                                                  _ => Task.CompletedTask),
                                                              _ => ValueTask.CompletedTask, scope);
            await coordinator.ReportAsync(new WorkspaceAclProblem(root, root,
                                                                  WorkspaceAclProblemCategory.MissingInheritedAccess,
                                                                  [], null, null));
            await coordinator.RepairAsync(coordinator.State.Revision);
            Assert.That(coordinator.State.NeedsElevation, Is.True);

            await coordinator.RepairAsync(coordinator.State.Revision, true);

            Assert.That(coordinator.State.Phase, Is.EqualTo("unknown"));
            Assert.That(coordinator.State.CanRepair, Is.False);
            Assert.That(scope.GrantUncertain, Is.True);
            Assert.Throws<InvalidOperationException>(() => scope.EnterRun(root),
                                                     "未知授权后本进程不得再进入同根运行。");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ScopeValidationFailure_BlocksPreparationAndGrantWithoutLatchingUnknown()
    {
        var root = Path.Combine(Path.GetTempPath(), "tinyharness-coordinator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var prepared = 0;
            var grants   = 0;
            var scope    = new DesktopWorkspaceOperationScope();
            var coordinator = new DesktopWorkspaceCoordinator(WorkspaceDirectoryIdentity.Capture(root),
                                                              new DesktopWorkspaceRepairServices(_ =>
                                                                  {
                                                                      prepared++;
                                                                      return Task.FromResult(true);
                                                                  }, new DelegateGrant((_, _) =>
                                                                  {
                                                                      grants++;
                                                                      return Task.FromResult(WorkspaceGrantOutcome
                                                                         .Completed);
                                                                  }), _ => Task.CompletedTask,
                                                                  () =>
                                                                      throw new
                                                                          InvalidDataException("scope is blocked")),
                                                              _ => ValueTask.CompletedTask, scope);
            await coordinator.ReportAsync(new WorkspaceAclProblem(root, root,
                                                                  WorkspaceAclProblemCategory.MissingInheritedAccess,
                                                                  [], null, null));

            await coordinator.RepairAsync(coordinator.State.Revision);

            Assert.That(coordinator.State.Phase, Is.EqualTo("failed"));
            Assert.That(coordinator.State.Message, Does.Contain("scope is blocked"));
            Assert.That(coordinator.State.Message, Does.Not.Contain("可能已有部分权限变更"));
            Assert.That(coordinator.State.CanRepair, Is.True);
            Assert.That(coordinator.State.NeedsElevation, Is.False);
            Assert.That(prepared, Is.Zero);
            Assert.That(grants, Is.Zero);
            Assert.That(scope.GrantUncertain, Is.False);
            using var run = scope.EnterRun(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task KnownGrantFailure_PreservesDetailAndAllowsRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), "tinyharness-coordinator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var attempts = 0;
            var scope    = new DesktopWorkspaceOperationScope();
            var coordinator = new DesktopWorkspaceCoordinator(WorkspaceDirectoryIdentity.Capture(root),
                                                              new DesktopWorkspaceRepairServices(_ =>
                                                                      Task.FromResult(false),
                                                                  new DelegateGrant((_, _) => ++attempts == 1
                                                                      ? Task
                                                                         .FromException<
                                                                              WorkspaceGrantOutcome>(new
                                                                              WorkspaceGrantFailureException("helper rejected request"))
                                                                      : Task
                                                                         .FromResult(WorkspaceGrantOutcome
                                                                             .Completed)),
                                                                  _ => Task.CompletedTask),
                                                              _ => ValueTask.CompletedTask, scope);
            await coordinator.ReportAsync(new WorkspaceAclProblem(root, root,
                                                                  WorkspaceAclProblemCategory.MissingInheritedAccess,
                                                                  [], null, null));
            await coordinator.RepairAsync(coordinator.State.Revision);
            await coordinator.RepairAsync(coordinator.State.Revision, true);

            Assert.That(coordinator.State.Phase, Is.EqualTo("failed"));
            Assert.That(coordinator.State.Message, Does.Contain("helper rejected request"));
            Assert.That(coordinator.State.CanRepair, Is.True);
            Assert.That(scope.GrantUncertain, Is.False);

            await coordinator.RepairAsync(coordinator.State.Revision);
            await coordinator.RepairAsync(coordinator.State.Revision, true);
            Assert.That(coordinator.State.Phase, Is.EqualTo("completed"));
            Assert.That(attempts, Is.EqualTo(2));
            Assert.That(scope.GrantUncertain, Is.False);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestCase(WorkspaceGrantOutcome.Declined, "cancelled", false)]
    [TestCase(WorkspaceGrantOutcome.Failed, "failed", false)]
    [TestCase(WorkspaceGrantOutcome.DidNotFinish, "unknown", true)]
    public async Task GrantOutcomes_AreClassifiedConservatively(
        WorkspaceGrantOutcome outcome, string phase, bool grantUncertain)
    {
        var root = Path.Combine(Path.GetTempPath(), "tinyharness-coordinator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scope = new DesktopWorkspaceOperationScope();
            var coordinator = new DesktopWorkspaceCoordinator(WorkspaceDirectoryIdentity.Capture(root),
                                                              new DesktopWorkspaceRepairServices(_ =>
                                                                      Task.FromResult(false),
                                                                  new DelegateGrant((_, _) =>
                                                                      Task.FromResult(outcome)),
                                                                  _ => Task.CompletedTask),
                                                              _ => ValueTask.CompletedTask, scope);
            await coordinator.ReportAsync(new WorkspaceAclProblem(root, root,
                                                                  WorkspaceAclProblemCategory.MissingInheritedAccess,
                                                                  [], null, null));
            await coordinator.RepairAsync(coordinator.State.Revision);
            await coordinator.RepairAsync(coordinator.State.Revision, true);

            Assert.That(coordinator.State.Phase, Is.EqualTo(phase));
            Assert.That(scope.GrantUncertain, Is.EqualTo(grantUncertain));
            Assert.That(coordinator.State.CanRepair, Is.EqualTo(!grantUncertain));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ValidationFailure_DoesNotClaimRepairCompleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "tinyharness-coordinator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var coordinator = new DesktopWorkspaceCoordinator(WorkspaceDirectoryIdentity.Capture(root),
                                                              new DesktopWorkspaceRepairServices(_ =>
                                                                      Task.FromResult(true), new ThrowingGrant(),
                                                                  _ =>
                                                                      Task
                                                                         .FromException(new
                                                                              IOException("verification failed"))),
                                                              _ => ValueTask.CompletedTask,
                                                              new DesktopWorkspaceOperationScope());
            await coordinator.ReportAsync(new WorkspaceAclProblem(root, root,
                                                                  WorkspaceAclProblemCategory.MissingInheritedAccess,
                                                                  [], null, null));

            await coordinator.RepairAsync(coordinator.State.Revision);

            Assert.That(coordinator.State.Phase, Is.EqualTo("failed"));
            Assert.That(coordinator.State.Phase, Is.Not.EqualTo("completed"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Dispose_CancelsWaitingRepairAndEndsOperation()
    {
        var root = Path.Combine(Path.GetTempPath(), "tinyharness-coordinator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var identity  = WorkspaceDirectoryIdentity.Capture(root);
            var started   = new TaskCompletionSource();
            var cancelled = new TaskCompletionSource();
            var coordinator = new DesktopWorkspaceCoordinator(identity,
                                                              new DesktopWorkspaceRepairServices(async token =>
                                                              {
                                                                  started.SetResult();
                                                                  try
                                                                  {
                                                                      await Task.Delay(Timeout.Infinite, token);
                                                                  }
                                                                  catch (OperationCanceledException)
                                                                  {
                                                                      cancelled.SetResult();
                                                                      throw;
                                                                  }

                                                                  return false;
                                                              }, new ThrowingGrant(), _ => Task.CompletedTask),
                                                              _ => ValueTask.CompletedTask,
                                                              new DesktopWorkspaceOperationScope());
            await coordinator.ReportAsync(new WorkspaceAclProblem(root, root,
                                                                  WorkspaceAclProblemCategory.MissingInheritedAccess,
                                                                  [], null, null));
            var repair = coordinator.RepairAsync(coordinator.State.Revision);
            await started.Task;

            await coordinator.DisposeAsync();

            Assert.That(cancelled.Task.IsCompleted, Is.True, "关闭必须取消等待中的准备。");
            Assert.That(repair.IsCompleted, Is.True, "关闭必须等待修复任务收尾。");
            Assert.That(coordinator.State.Phase, Is.EqualTo("cancelled"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task CloseReleasesLeaseWhenCancellationNotificationThrows()
    {
        var root = Path.Combine(Path.GetTempPath(), "tinyharness-coordinator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scope = new DesktopWorkspaceOperationScope();
            var coordinator = new DesktopWorkspaceCoordinator(WorkspaceDirectoryIdentity.Capture(root),
                                                              new DesktopWorkspaceRepairServices(_ =>
                                                                      Task.FromResult(false), new ThrowingGrant(),
                                                                  _ => Task.CompletedTask),
                                                              state =>
                                                              {
                                                                  if (state.Phase != "cancelled")
                                                                      return ValueTask.CompletedTask;
                                                                  return ValueTask.FromException(
                                                                   new IOException("notification failure"));
                                                              }, scope);
            await coordinator.ReportAsync(new WorkspaceAclProblem(root, root,
                                                                  WorkspaceAclProblemCategory.MissingInheritedAccess,
                                                                  [], null, null));
            await coordinator.RepairAsync(coordinator.State.Revision);
            Assert.That(coordinator.State.NeedsElevation, Is.True);
            var close = coordinator.DisposeAsync().AsTask();
            var error = await Assert.CatchAsync<AggregateException>(async () =>
            {
                await close.WaitAsync(TimeSpan.FromSeconds(10));
            });
            Assert.That(error!.Flatten().InnerExceptions.Any(ex => ex.Message == "notification failure"), Is.True);
            using var run = scope.EnterRun(root);
            Assert.That(coordinator.DisposeAsync().AsTask(), Is.SameAs(close));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task CloseWaitsForRepairDespiteThrowingCancellationCallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "tinyharness-coordinator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var scope   = new DesktopWorkspaceOperationScope();
            var coordinator = new DesktopWorkspaceCoordinator(WorkspaceDirectoryIdentity.Capture(root),
                                                              new DesktopWorkspaceRepairServices(async token =>
                                                              {
                                                                  using var registration =
                                                                      token.Register(() =>
                                                                          throw new
                                                                              IOException("repair callback failure"));
                                                                  entered.TrySetResult();
                                                                  try
                                                                  {
                                                                      await Task.Delay(Timeout.Infinite, token);
                                                                  }
                                                                  finally
                                                                  {
                                                                      cleanup.TrySetResult();
                                                                      await release.Task;
                                                                  }

                                                                  return true;
                                                              }, new ThrowingGrant(), _ => Task.CompletedTask),
                                                              _ => ValueTask.CompletedTask, scope);
            await coordinator.ReportAsync(new WorkspaceAclProblem(root, root,
                                                                  WorkspaceAclProblemCategory.MissingInheritedAccess,
                                                                  [], null, null));
            var operation = coordinator.RepairAsync(coordinator.State.Revision);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var close = coordinator.DisposeAsync().AsTask();
            await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(close.IsCompleted, Is.False);
            release.TrySetResult();
            var error = await Assert.CatchAsync<AggregateException>(async () =>
            {
                await close.WaitAsync(TimeSpan.FromSeconds(10));
            });
            Assert.That(error!.Flatten().InnerExceptions.Any(ex => ex.Message == "repair callback failure"), Is.True);
            Assert.That(operation.IsCompleted, Is.True);
            using var run = scope.EnterRun(root);
        }
        finally
        {
            release.TrySetResult();
            Directory.Delete(root, true);
        }
    }

    private sealed class ThrowingGrant : IWorkspaceAclGrant
    {
        public Task<bool> CanChangePermissionsAsync(
            WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task PrepareWithoutElevationAsync(
            WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> IsReadyAsync(WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<WorkspaceGrantOutcome> GrantAsync(
            WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken) =>
            throw new IOException("fake channel failure after grant start");
    }

    private sealed class DelegateGrant(
        Func<WorkspaceDirectoryIdentity, CancellationToken, Task<WorkspaceGrantOutcome>> grant)
        : IWorkspaceAclGrant
    {
        public Task<bool> CanChangePermissionsAsync(WorkspaceDirectoryIdentity identity,
                                                    CancellationToken          cancellationToken) =>
            Task.FromResult(false);

        public Task PrepareWithoutElevationAsync(WorkspaceDirectoryIdentity identity,
                                                 CancellationToken          cancellationToken) =>
            Task.CompletedTask;

        public Task<bool> IsReadyAsync(WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<WorkspaceGrantOutcome> GrantAsync(WorkspaceDirectoryIdentity identity,
                                                      CancellationToken          cancellationToken) =>
            grant(identity, cancellationToken);
    }
}

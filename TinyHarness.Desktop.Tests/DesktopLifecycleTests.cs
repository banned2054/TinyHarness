using NUnit.Framework;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;
using TinyHarness.Desktop.Runtime;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class DesktopLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task DifferentSessionsRunInParallel_AndCancellingOneLeavesTheOtherRunning()
    {
        var firstEntered = Signal();
        var secondEntered = Signal();
        var finishSecond = Signal();
        await using var manager = new DesktopSessionManager();
        var first = await manager.OpenAsync(null, _ => Task.FromResult(Create(new WaitingModel(firstEntered))));
        var second =
            await manager.OpenAsync(null, _ => Task.FromResult(Create(new WaitingModel(secondEntered, finishSecond))));
        var firstRun  = first.SubmitAsync("first");
        var secondRun = second.SubmitAsync("second");
        await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(Timeout);
        Assert.That(first.Cancel(), Is.True);
        var cancelled = await firstRun.WaitAsync(Timeout);
        Assert.That(cancelled.Result!.Status, Is.EqualTo(AgentStatus.Cancelled));
        Assert.That(secondRun.IsCompleted, Is.False);
        finishSecond.SetResult();
        Assert.That((await secondRun.WaitAsync(Timeout)).Result!.Status, Is.EqualTo(AgentStatus.Completed));
    }

    [Test]
    public async Task DuplicateSubmitIsRejectedBeforeCreatingAnotherTurn()
    {
        var             entered = Signal();
        await using var session = Create(new WaitingModel(entered));
        var             run     = session.SubmitAsync("first");
        await entered.Task.WaitAsync(Timeout);
        Assert.Throws<InvalidOperationException>(() => session.SubmitAsync("duplicate"));
        Assert.That(session.Host.Session.Turns, Has.Count.EqualTo(1));
        session.Cancel();
        await run.WaitAsync(Timeout);
    }

    [Test]
    public async Task ShutdownWaitsForModelCleanupBeforeDisposingClient()
    {
        var entered        = Signal();
        var cleanup        = Signal();
        var releaseCleanup = Signal();
        var model = new WaitingModel(entered, cleanup : async () =>
        {
            cleanup.SetResult();
            await releaseCleanup.Task;
        });
        var manager = new DesktopSessionManager();
        var session = await manager.OpenAsync(null, _ => Task.FromResult(Create(model)));
        _ = session.SubmitAsync("first");
        await entered.Task.WaitAsync(Timeout);
        var shutdown = manager.DisposeAsync().AsTask();
        await cleanup.Task.WaitAsync(Timeout);
        Assert.That(shutdown.IsCompleted, Is.False);
        Assert.That(model.Disposed, Is.False);
        releaseCleanup.SetResult();
        await shutdown.WaitAsync(Timeout);
        Assert.That(model.Disposed, Is.True);
        Assert.Throws<ObjectDisposedException>(() => session.SubmitAsync("late"));
    }

    [Test]
    public async Task ManagerReusesOpenSessionWithoutCallingAnotherFactory()
    {
        await using var manager = new DesktopSessionManager();
        var session = await manager.OpenAsync(null, _ => Task.FromResult(Create(new WaitingModel(Signal()))));
        var same = await manager.OpenAsync(session.Id, _ => throw new AssertionException("Must reuse the open host."));
        Assert.That(same, Is.SameAs(session));
    }

    [Test]
    public async Task ShutdownCancelsAnInFlightOpenAndDoesNotLeakTheReturnedClient()
    {
        var entered = Signal();
        var model   = new WaitingModel(Signal());
        var manager = new DesktopSessionManager();
        var open = manager.OpenAsync(null, async token =>
        {
            entered.SetResult();
            try
            {
                await Task.Delay(System.Threading.Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
            }

            return Create(model);
        });
        await entered.Task.WaitAsync(Timeout);
        var shutdown = manager.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await open.WaitAsync(Timeout));
        await shutdown.WaitAsync(Timeout);
        Assert.That(model.Disposed, Is.True);
    }

    [Test]
    public async Task CancelledApprovalCannotApproveAFutureInvocation()
    {
        DesktopApproval? pending = null;
        var provider = new DesktopApprovalProvider(value =>
        {
            pending = value;
            return ValueTask.CompletedTask;
        }, value => value);
        using var cancellation = new CancellationTokenSource();
        var       first        = provider.PromptAsync(Preparation(), cancellation.Token);
        var       expiredId    = pending!.Id;
        cancellation.Cancel();
        await Assert.ThatAsync(async () => await first, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(pending, Is.Null);
        var second = provider.PromptAsync(Preparation(), CancellationToken.None);
        Assert.That(provider.Respond(expiredId, ApprovalAction.AllowOnce), Is.False);
        Assert.That(second.IsCompleted, Is.False);
        Assert.That(provider.Respond(pending!.Id, ApprovalAction.Deny), Is.True);
        Assert.That(await second, Is.EqualTo(ApprovalAction.Deny));
    }

    [Test]
    public async Task ApprovalCancellationInOneSessionDoesNotWithdrawAnotherSessionsRequest()
    {
        DesktopApproval? left  = null;
        DesktopApproval? right = null;
        var first = new DesktopApprovalProvider(value =>
        {
            left = value;
            return ValueTask.CompletedTask;
        }, value => value);
        var second = new DesktopApprovalProvider(value =>
        {
            right = value;
            return ValueTask.CompletedTask;
        }, value => value);
        using var cancellation = new CancellationTokenSource();
        var       firstWait    = first.PromptAsync(Preparation(), cancellation.Token);
        var       secondWait   = second.PromptAsync(Preparation(), CancellationToken.None);
        cancellation.Cancel();
        await Assert.ThatAsync(async () => await firstWait, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(left, Is.Null);
        Assert.That(right, Is.Not.Null);
        second.Respond(right!.Id, ApprovalAction.AllowOnce);
        Assert.That(await secondWait, Is.EqualTo(ApprovalAction.AllowOnce));
    }

    [Test]
    public async Task ARunOnlyFinishesAfterItsTerminalSaveAndShutdownPreservesSaveWarnings()
    {
        var             enteredSave = Signal();
        var             releaseSave = Signal();
        var             writer      = new DelayedFailingWriter(enteredSave, releaseSave);
        var             model       = new WaitingModel(Signal(), CompletedSignal());
        await using var session     = Create(model, writer);
        var             run         = session.SubmitAsync("save");
        await enteredSave.Task.WaitAsync(Timeout);
        var shutdown = session.DisposeAsync().AsTask();
        Assert.That(shutdown.IsCompleted, Is.False);
        releaseSave.SetResult();
        await shutdown.WaitAsync(Timeout);
        await run.WaitAsync(Timeout);
        Assert.That(session.Host.PersistenceWarning, Does.Contain("fixture save failed"));
        Assert.That(writer.Disposed, Is.True);
    }

    [Test]
    public async Task ClosingWaitsForSelectionCleanupAndRejectsLateOperations()
    {
        var entered = Signal();
        var cleanup = Signal();
        var release = Signal();
        var model   = new WaitingModel(Signal());
        var session = Create(model, changeModel : async (_, _, _, _, token) =>
        {
            entered.SetResult();
            try
            {
                await Task.Delay(System.Threading.Timeout.Infinite, token);
            }
            finally
            {
                cleanup.SetResult();
                await release.Task;
            }

            throw new OperationCanceledException(token);
        });
        var change = session.ChangeModelAsync("provider", "model", null, session.Host.Session.ModelSelection!.Revision);
        await entered.Task.WaitAsync(Timeout);
        Assert.Throws<InvalidOperationException>(() => session.SubmitAsync("overlap"));
        var shutdown = session.DisposeAsync().AsTask();
        await cleanup.Task.WaitAsync(Timeout);
        Assert.That(shutdown.IsCompleted, Is.False);
        Assert.That(model.Disposed, Is.False);
        release.SetResult();
        await Assert.CatchAsync<OperationCanceledException>(async () => await change);
        await shutdown.WaitAsync(Timeout);
        Assert.That(model.Disposed, Is.True);
        Assert.Throws<ObjectDisposedException>(() => session.ChangeModelAsync("provider", "model", null, 0));
    }

    [Test]
    public async Task ClosingCancelsAndWaitsForConfigurationRefreshBeforeDisposingClient()
    {
        var entered = Signal();
        var cleanup = Signal();
        var release = Signal();
        var model   = new WaitingModel(Signal());
        var session = Create(model, validateCurrent : async token =>
        {
            entered.SetResult();
            try
            {
                await Task.Delay(System.Threading.Timeout.Infinite, token);
            }
            finally
            {
                cleanup.SetResult();
                await release.Task;
            }
        });
        var send = session.SubmitAsync("refresh first");
        await entered.Task.WaitAsync(Timeout);
        Assert.Throws<InvalidOperationException>(() => session.SubmitAsync("overlap"));
        var shutdown = session.DisposeAsync().AsTask();
        await cleanup.Task.WaitAsync(Timeout);
        Assert.That(shutdown.IsCompleted, Is.False);
        Assert.That(model.Disposed, Is.False);
        Assert.That(session.Host.Session.Turns, Is.Empty);
        release.SetResult();
        await Assert.CatchAsync<OperationCanceledException>(async () => await send);
        await shutdown.WaitAsync(Timeout);
        Assert.That(model.Disposed, Is.True);
    }

    private static DesktopSession Create(IChatCompletionClient model, ISessionStoreWriter? writer = null,
                                         Func<string, string, ModelReasoningEffort?, long, CancellationToken,
                                             Task<SessionModelSelection>>? changeModel = null,
                                         Func<CancellationToken, Task>? validateCurrent = null)
    {
        var approvals = new DesktopApprovalProvider(_ => ValueTask.CompletedTask, value => value);
        var options   = new AgentOptions { Model = "offline", MaxAgentSteps = 2, DefaultToolTimeoutSeconds = 10 };
        var host = new ChatSessionHost(Path.GetTempPath(),
                                       new SessionModelIdentity("offline", "https://offline.invalid", default),
                                       options, model, new ToolRegistry([]), "offline desktop test",
                                       approver : approvals,
                                       sessionWriterFactory : writer is null ? null : _ => writer);
        return new DesktopSession(host, approvals, changeModel, validateCurrent);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = Signal();
        signal.SetResult();
        return signal;
    }

    private static ToolPreparation Preparation() => new()
    {
        ToolName   = "offline",
        CallId     = "call",
        Capability = "filesystem.write",
        Summary    = "offline approval",
        Arguments  = new JsonObject()
    };

    private sealed class WaitingModel(
        TaskCompletionSource  entered,
        TaskCompletionSource? finish  = null,
        Func<Task>?           cleanup = null) : IChatCompletionClient, IDisposable
    {
        public bool Disposed { get; private set; }

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
                                                                     [EnumeratorCancellation]
                                                                     CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            try
            {
                if (finish is null) await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
                else await finish.Task.WaitAsync(cancellationToken);
                yield return new ChatStreamEvent
                    { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "offline result" };
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" };
            }
            finally
            {
                if (cleanup is not null) await cleanup();
            }
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class DelayedFailingWriter(TaskCompletionSource entered, TaskCompletionSource release)
        : ISessionStoreWriter
    {
        public string SnapshotPath   => "offline";
        public long   CommitSequence => 0;
        public bool Disposed { get; private set; }
        public async Task CommitAsync(ChatSession       session, Core.Services.Context.ConversationContext context,
                                      CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await release.Task;
            throw new IOException("fixture save failed");
        }

        public void Dispose() => Disposed = true;
    }
}

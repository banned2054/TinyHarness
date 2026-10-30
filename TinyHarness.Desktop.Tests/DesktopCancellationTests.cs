using NUnit.Framework;
using System.Runtime.CompilerServices;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;
using TinyHarness.Desktop.Runtime;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class DesktopCancellationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestCase(false)]
    [TestCase(true)]
    public async Task StopCommandReportsCallbackFailureAndStillWaitsForStreamCleanup(bool throwCallback)
    {
        var             model   = new CancellableModel(throwCallback);
        await using var manager = new DesktopSessionManager();
        var             session = await manager.OpenAsync(null, _ => Task.FromResult(Create(model)));
        var page = new SessionViewModel(manager, new DesktopSessionFactory(), Path.GetTempPath(), session.Id,
                                        dispatch : action =>
                                        {
                                            action();
                                            return ValueTask.CompletedTask;
                                        });
        page.Draft = "cancel offline";
        await page.SendAsync();
        await model.Entered.Task.WaitAsync(Timeout);
        try
        {
            Assert.DoesNotThrow(() => page.CancelCommand.Execute(null));
            await model.CleanupEntered.Task.WaitAsync(Timeout);
            if (throwCallback) Assert.That(page.Notice, Does.Contain("callback failure"));
            else Assert.That(page.Notice, Is.Empty);
            Assert.That(page.IsRunning, Is.True);
            Assert.That(page.Completion.IsCompleted, Is.False);
            Assert.DoesNotThrow(() => page.CancelCommand.Execute(null));
        }
        finally
        {
            model.ReleaseCleanup.TrySetResult();
        }

        await page.Completion.WaitAsync(Timeout);
        Assert.That(page.Status, Is.EqualTo("Cancelled"));
        Assert.That(model.CleanupFinished, Is.True);
        Assert.That(model.Disposed, Is.False);
        if (throwCallback) Assert.That(page.Notice, Does.Contain("callback failure"));
        Assert.DoesNotThrow(() => page.CancelCommand.Execute(null));

        model.Finish.TrySetResult();
        page.Draft = "continue after cancellation";
        await page.SendAsync();
        await page.Completion.WaitAsync(Timeout);
        Assert.That(page.Status, Is.EqualTo("Completed"));
        Assert.That(page.Notice, Is.Empty);
    }

    [Test]
    public async Task ClosePreservesCallbackFailureButStillWaitsAndDisposesClient()
    {
        var model   = new CancellableModel(true);
        var session = Create(model);
        var run     = session.SubmitAsync("cancel offline");
        await model.Entered.Task.WaitAsync(Timeout);
        var close = session.DisposeAsync().AsTask();
        try
        {
            await model.CleanupEntered.Task.WaitAsync(Timeout);
            Assert.That(close.IsCompleted, Is.False);
            Assert.That(model.Disposed, Is.False);
            Assert.DoesNotThrow(() => session.Cancel());
        }
        finally
        {
            model.ReleaseCleanup.TrySetResult();
        }

        var failure = await Assert.CatchAsync<AggregateException>(async () => await close.WaitAsync(Timeout));
        Assert.That(failure!.Flatten().InnerExceptions.Any(ex => ex.Message == "callback failure"), Is.True);
        Assert.That((await run.WaitAsync(Timeout)).Result!.Status, Is.EqualTo(AgentStatus.Cancelled));
        Assert.That(model.CleanupFinished && model.Disposed, Is.True);
        Assert.That(session.DisposeAsync().AsTask(), Is.SameAs(close));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StreamCleanupFailureIsFailedRatherThanSuccessfulOrUnobserved(bool cancel)
    {
        var             model   = new CancellableModel(throwCleanup : true);
        await using var session = Create(model);
        var             run     = session.SubmitAsync("offline cleanup failure");
        await model.Entered.Task.WaitAsync(Timeout);
        if (cancel) session.Cancel();
        else model.Finish.TrySetResult();
        await model.CleanupEntered.Task.WaitAsync(Timeout);
        model.ReleaseCleanup.TrySetResult();
        var result = (await run.WaitAsync(Timeout)).Result!;
        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(result.Error, Does.Contain("stream cleanup failure"));
    }

    [Test]
    public async Task SdkTimeoutWithoutUserCancellationRemainsFailed()
    {
        var             model   = new CancellableModel(timeout : true);
        await using var session = Create(model);
        model.ReleaseCleanup.TrySetResult();
        var result = (await session.SubmitAsync("offline timeout").WaitAsync(Timeout)).Result!;
        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(result.Error, Does.Contain("SDK timeout"));
        Assert.That(result.Budget!.LimitReason, Is.Null);
    }

    private static DesktopSession Create(IChatCompletionClient model)
    {
        var approvals = new DesktopApprovalProvider(_ => ValueTask.CompletedTask, text => text);
        var options   = new AgentOptions { Model = "offline", MaxAgentSteps = 2, DefaultToolTimeoutSeconds = 10 };
        var host = new ChatSessionHost(Path.GetTempPath(),
                                       new SessionModelIdentity("offline", "https://offline.invalid", default),
                                       options, model, new ToolRegistry([]), "offline", approver : approvals);
        return new DesktopSession(host, approvals);
    }

    private sealed class CancellableModel(bool throwCallback = false, bool throwCleanup = false, bool timeout = false)
        : IChatCompletionClient, IDisposable
    {
        public TaskCompletionSource Entered         { get; } = Signal();
        public TaskCompletionSource Finish          { get; } = Signal();
        public TaskCompletionSource CleanupEntered  { get; } = Signal();
        public TaskCompletionSource ReleaseCleanup  { get; } = Signal();
        public bool                 CleanupFinished { get; private set; }
        public bool                 Disposed        { get; private set; }

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
                                                                     [EnumeratorCancellation]
                                                                     CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() =>
            {
                if (throwCallback) throw new InvalidOperationException("callback failure");
            });
            Entered.TrySetResult();
            try
            {
                if (timeout) throw new TaskCanceledException("SDK timeout");
                await Finish.Task.WaitAsync(cancellationToken);
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "answer" };
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason          = "stop" };
            }
            finally
            {
                CleanupEntered.TrySetResult();
                await ReleaseCleanup.Task;
                CleanupFinished = true;
                if (throwCleanup) throw new IOException("stream cleanup failure");
            }
        }

        public         void                 Dispose() => Disposed = true;
        private static TaskCompletionSource Signal()  => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

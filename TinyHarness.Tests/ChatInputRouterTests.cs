using System.Threading.Channels;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public class ChatInputRouterTests
{
    [Test]
    public async Task RealConsoleHostRoute_AppliesAmendmentDuringInlineApproval()
    {
        using var source       = new QueuedReader();
        using var router       = new ChatInputRouter(source);
        using var cancellation = new ChatCancellation(false);
        var       output       = new StringWriter();
        var       writer       = TextWriter.Synchronized(output);
        var       model        = new FakeChatClient();
        model.Enqueue(FakeChatClient.ToolCall("probe", "{}"));
        model.Enqueue(FakeChatClient.Text("discussion only"));
        var tool   = new FakeTool("probe");
        var events = new ApprovalSink();
        using var host = new ChatSessionHost(Path.GetTempPath(),
                                             new SessionModelIdentity("test", "https://example.invalid",
                                                                      ChatApiKind.ChatCompletions),
                                             new AgentOptions
                                                 { Model = "test", MaxAgentSteps = 5, DefaultToolTimeoutSeconds = 30 },
                                             model,
                                             new ToolRegistry([tool]), "sys", new PermissionEngine(Path.GetTempPath()),
                                             new ConsoleApprovalProvider(router, writer), events);
        using var console = new ChatSessionConsole(host, router, writer, cancellation);
        var       running = console.RunAsync();
        await source.Lines.Writer.WriteAsync("initial request");
        await events.Approval.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.Lines.Writer.WriteAsync("only discuss; do not change files");
        await events.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.Lines.Writer.TryComplete();
        Assert.That(await running.WaitAsync(TimeSpan.FromSeconds(5)), Is.Zero);
        Assert.That(host.Session.Turns.Count, Is.EqualTo(1));
        Assert.That(host.Session.Turns.Single().Runs.Single().Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(host.Session.Amendments.Single().CanonicalMessageIndex, Is.Not.Null);
        Assert.That(tool.ExecuteCount, Is.Zero);
        Assert.That(source.MaximumReaders, Is.EqualTo(1));
        Assert.That(output.ToString(), Does.Contain("saved and queued").And.Contain("turn 1 finished: completed"));
    }

    [Test]
    public async Task RoutesControlsAndNaturalAmendmentsSeparatelyFromApprovalReplies()
    {
        using var source  = new QueuedReader();
        using var router  = new ChatInputRouter(source);
        var       initial = router.ReadLineAsync(default).AsTask();
        await source.Lines.Writer.WriteAsync("first request");
        Assert.That(await initial, Is.EqualTo("first request"));
        var routed = Channel.CreateUnbounded<string?>();
        router.SetActiveHandler(line =>
        {
            routed.Writer.TryWrite(line);
            return Task.CompletedTask;
        });
        var approval = router.ReadLineAsync(default).AsTask();
        await source.Lines.Writer.WriteAsync("/pause");
        await source.Lines.Writer.WriteAsync("preserve the public API");
        Assert.That(await routed.Reader.ReadAsync(), Is.EqualTo("/pause"));
        Assert.That(await routed.Reader.ReadAsync(), Is.EqualTo("preserve the public API"));
        Assert.That(approval.IsCompleted, Is.False);
        await source.Lines.Writer.WriteAsync("a");
        Assert.That(await approval, Is.EqualTo("a"));
        Assert.That(source.MaximumReaders, Is.EqualTo(1));
    }

    [Test]
    public async Task CancelledPrompt_DoesNotReuseItsReplyAsApprovalForTheNextRun()
    {
        using var source = new QueuedReader();
        using var router = new ChatInputRouter(source);
        using var cancel = new CancellationTokenSource();
        var       routed = Channel.CreateUnbounded<string?>();
        router.SetActiveHandler(line =>
        {
            routed.Writer.TryWrite(line);
            return Task.CompletedTask;
        });
        var oldPrompt = router.ReadLineAsync(cancel.Token).AsTask();
        cancel.Cancel();
        await TestAssert.ThrowsAsync<TaskCanceledException>(() => oldPrompt);
        await source.Lines.Writer.WriteAsync("a");
        Assert.That(await routed.Reader.ReadAsync(), Is.EqualTo("a"));
        var nextPrompt = router.ReadLineAsync(default).AsTask();
        Assert.That(nextPrompt.IsCompleted, Is.False);
        await source.Lines.Writer.WriteAsync("d");
        Assert.That(await nextPrompt, Is.EqualTo("d"));
        Assert.That(source.MaximumReaders, Is.EqualTo(1));
    }

    [Test]
    public async Task Eof_DeliversCancellationControlAndEndsPrompt()
    {
        using var source = new QueuedReader();
        using var router = new ChatInputRouter(source);
        var       eof    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.SetActiveHandler(line =>
        {
            if (line is null) eof.TrySetResult();
            return Task.CompletedTask;
        });
        var prompt = router.ReadLineAsync(default).AsTask();
        source.Lines.Writer.TryComplete();
        Assert.That(await prompt, Is.Null);
        await eof.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task RejectsConcurrentPromptConsumers()
    {
        using var source = new QueuedReader();
        using var router = new ChatInputRouter(source);
        using var cancel = new CancellationTokenSource();
        var       first  = router.ReadLineAsync(cancel.Token).AsTask();
        await TestAssert.ThrowsAsync<InvalidOperationException>(() => router.ReadLineAsync(default).AsTask());
        cancel.Cancel();
        await TestAssert.ThrowsAsync<TaskCanceledException>(() => first);
    }

    private sealed class QueuedReader : TextReader
    {
        private  int             _readers;
        internal Channel<string> Lines          { get; } = Channel.CreateUnbounded<string>();
        internal int             MaximumReaders { get; private set; }

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var readers = Interlocked.Increment(ref _readers);
            MaximumReaders = Math.Max(MaximumReaders, readers);
            try
            {
                return await Lines.Reader.WaitToReadAsync(cancellationToken)
                    ? await Lines.Reader.ReadAsync(cancellationToken)
                    : null;
            }
            finally
            {
                Interlocked.Decrement(ref _readers);
            }
        }
    }

    private sealed class ApprovalSink : IHostEventSink
    {
        internal TaskCompletionSource Approval { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask PublishAsync(HostEvent value, CancellationToken cancellationToken)
        {
            if (value.Kind == HostEventKind.ApprovalRequested) Approval.TrySetResult();
            if (value.Kind == HostEventKind.RunFinished) Finished.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}

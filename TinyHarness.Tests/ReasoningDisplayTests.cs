using TinyHarness.Cli.Models;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public class ReasoningDisplayTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task RawReasoningEventsRequireExplicitOptIn(bool include)
    {
        var model = new FakeChatClient();
        model.Enqueue(FakeChatClient.ReasonedText("readable", "rs_test", "opaque", "answer"));
        var sink = new TestHostEventSink { IncludeReasoningText = include };
        var loop = new AgentLoop(model, new ToolRegistry([]),
                                 new AgentOptions { Model = "test", MaxAgentSteps = 4, DefaultToolTimeoutSeconds = 30 },
                                 null, null, events : sink);
        Assert.That((await loop.RunAsync("sys", "go", CancellationToken.None)).Status,
                    Is.EqualTo(AgentStatus.Completed));
        var deltas = sink.Events.Where(e => e.Kind == HostEventKind.AssistantReasoningDelta).Select(e => e.Payload);
        Assert.That(deltas, Is.EqualTo(include ? new[] { "readable" } : Array.Empty<string>()));
    }

    [TestCase("status")]
    [TestCase("content")]
    public async Task SessionForwardsOptInReasoningWithoutChangingReplay(string display)
    {
        var             mode   = display == "content" ? ReasoningDisplayMode.Content : ReasoningDisplayMode.Status;
        using var       dir    = new TestTempDir();
        using var       output = new StringWriter { NewLine = "\n" };
        await using var sink   = new ConsoleHostEventSink(output : output, reasoningDisplay : mode);
        var             model  = new FakeChatClient();
        model.Enqueue([
            new()
                { Kind = ChatStreamEventKind.ReasoningDelta, ReasoningDelta = "inspect ", ReasoningItemId = "rs_1" },
            new()
                { Kind = ChatStreamEventKind.ReasoningDelta, ReasoningDelta = "files", ReasoningItemId = "rs_1" },
            new()
            {
                Kind            = ChatStreamEventKind.ReasoningItem, ReasoningDelta = "inspect files carefully",
                ReasoningItemId = "rs_1", ReasoningProtectedData                    = "opaque-never-display"
            },
            new() { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "answer" },
            new() { Kind = ChatStreamEventKind.End, FinishReason          = "stop" }
        ]);
        model.Enqueue(FakeChatClient.Text("second"));
        using var host = new ChatSessionHost(dir.Root,
                                             new SessionModelIdentity("test", "https://example.invalid/v1",
                                                                      ChatApiKind.Responses),
                                             new AgentOptions
                                                 { Model = "test", MaxAgentSteps = 4, DefaultToolTimeoutSeconds = 30 },
                                             model, new ToolRegistry([]), "sys",
                                             events : sink);
        Assert.That((await host.SubmitTurnAsync("go", CancellationToken.None)).Result!.Status,
                    Is.EqualTo(AgentStatus.Completed));
        Assert.That((await host.SubmitTurnAsync("again", CancellationToken.None)).Result!.Status,
                    Is.EqualTo(AgentStatus.Completed));
        var shown = output.ToString();
        Assert.That(shown, Does.Not.Contain("opaque-never-display"));
        Assert.That(shown,
                    Does.Not.Contain("model request").And.Not.Contain("usage:").And.Not.Contain("cached input tokens"));
        if (mode == ReasoningDisplayMode.Content)
        {
            Assert.That(shown, Does.Contain("思考：\ninspect files carefully\n"));
            Assert.That(shown.Split("inspect files").Length - 1, Is.EqualTo(1));
            Assert.That(shown, Does.Contain("回答：\nanswer"));
        }
        else
        {
            Assert.That(shown, Does.Contain("思考中...").And.Not.Contain("inspect files"));
        }

        var history = model.RequestLog[1].Messages.Single(m => m.Reasoning is { Count: > 0 });
        Assert.That(history.Reasoning![0].Text, Is.EqualTo("inspect files carefully"));
        Assert.That(history.Reasoning[0].ProtectedData, Is.EqualTo("opaque-never-display"));
    }

    [Test]
    public async Task LocalReasoningAndAnswerPreserveTextAndEscapeControls()
    {
        using var output = new StringWriter { NewLine = "\n" };
        await using var sink = new ConsoleHostEventSink(new Dictionary<string, string> { ["test"] = "sk-secret-value" },
                                                        output, ReasoningDisplayMode.Content);
        await sink.PublishAsync(Reasoning("a\u001b[2J sk-secret-"), CancellationToken.None);
        await sink.PublishAsync(new HostEvent { Kind = HostEventKind.AssistantTextDelta, Payload = "value done" },
                                CancellationToken.None);
        await sink.PublishAsync(new HostEvent { Kind = HostEventKind.RunFinished }, CancellationToken.None);
        Assert.That(output.ToString(), Is.EqualTo("思考：\na\\u001B[2J sk-secret-\n回答：\nvalue done\n"));
    }

    [Test]
    public void FullReasoningItemsOnlyDisplayTheirUnseenSuffix()
    {
        var accumulator = new StreamAccumulator();
        accumulator.Append(new ChatStreamEvent { Kind = ChatStreamEventKind.ReasoningDelta, ReasoningDelta = "abc" });
        Assert.That(accumulator.ReasoningDisplayDelta, Is.EqualTo("abc"));
        accumulator.Append(new ChatStreamEvent { Kind = ChatStreamEventKind.ReasoningItem, ReasoningDelta = "abcdef" });
        Assert.That(accumulator.ReasoningDisplayDelta, Is.EqualTo("def"));
        accumulator.Append(new ChatStreamEvent
                               { Kind = ChatStreamEventKind.ReasoningItem, ReasoningDelta = "only item" });
        Assert.That(accumulator.ReasoningDisplayDelta, Is.EqualTo("only item"));
        accumulator.Append(new ChatStreamEvent { Kind = ChatStreamEventKind.ReasoningDelta, ReasoningDelta = "abc" });
        accumulator.Append(new ChatStreamEvent
                               { Kind = ChatStreamEventKind.ReasoningItem, ReasoningDelta = "rewritten" });
        Assert.That(accumulator.ReasoningDisplayDelta, Is.Null);
        accumulator.Append(new ChatStreamEvent
                               { Kind = ChatStreamEventKind.ReasoningItem, ReasoningProtectedData = "opaque" });
        Assert.That(accumulator.ReasoningDisplayDelta, Is.Null);
    }

    [TestCase(HostEventKind.AssistantTextDelta)]
    [TestCase(HostEventKind.ApprovalRequested)]
    [TestCase(HostEventKind.ModelRequestFinished)]
    [TestCase(HostEventKind.RunFinished)]
    public async Task AnimationCyclesWithoutSdkEventsAndStopsBeforeOtherOutput(HostEventKind ending)
    {
        var       clock  = new ManualAnimationTimeProvider();
        using var output = new StringWriter { NewLine = "\n" };
        var       sink   = new ConsoleHostEventSink(output : output, animateThinking : true, timeProvider : clock);
        try
        {
            await sink.PublishAsync(Progress(), CancellationToken.None);
            Assert.That(output.ToString(), Is.EqualTo("\r思考中.   0s"));
            foreach (var (dots, seconds) in new[] { (".. ", 0), ("...", 1), (".  ", 1) })
            {
                clock.Tick();
                // 等待真实动画任务写完该帧，再推进下一个计时信号。
                await WaitForAsync(() => output.ToString()
                                               .EndsWith($"\r思考中{dots} {seconds}s", StringComparison.Ordinal));
            }

            await sink.PublishAsync(new HostEvent
            {
                Kind = ending,
                Payload = ending == HostEventKind.ModelRequestFinished
                    ? new ModelRequestProgress(1, false, 100, "cancelled")
                    : "answer"
            }, CancellationToken.None);
            var stopped = output.ToString();
            clock.Tick();
            await sink.DisposeAsync();
            Assert.That(output.ToString(), Does.StartWith(stopped));
            Assert.That(output.ToString()[stopped.Length..], Does.Not.Contain("思考中"));
            Assert.That(stopped, Does.Contain("\r            \r"));
        }
        finally
        {
            // 测试断言失败时也须回收动画任务。
            if (!clock.Disposed) await sink.DisposeAsync();
        }
    }

    [Test]
    public async Task RequestStatusTracksElapsedTimeAcrossPhasesAndResetsForNextRequest()
    {
        var             clock = new ManualAnimationTimeProvider();
        using var       output = new StringWriter { NewLine = "\n" };
        await using var sink = new ConsoleHostEventSink(output : output, animateThinking : true, timeProvider : clock);
        var start = new HostEvent
            { Kind = HostEventKind.ModelRequestStarted, Payload = new ModelRequestProgress(1, false, 0) };
        await sink.PublishAsync(start, CancellationToken.None);
        Assert.That(output.ToString(), Is.EqualTo("\r等待中.   0s"));
        clock.Tick();
        await WaitForAsync(() => output.ToString().EndsWith("\r等待中..  0s", StringComparison.Ordinal));

        await sink.PublishAsync(Progress() with
        {
            Payload = new ModelRequestProgress(1, false, 12_345) { Phase = ModelRequestPhase.ReceivingReasoning }
        }, CancellationToken.None);
        Assert.That(output.ToString(), Does.EndWith("\r思考中.   12s"));
        clock.Tick();
        await WaitForAsync(() => output.ToString().EndsWith("\r思考中..  12s", StringComparison.Ordinal));
        var beforeUsage = output.ToString();
        await sink.PublishAsync(new HostEvent
                                {
                                    Kind = HostEventKind.ModelUsage, Payload = new ModelUsage(100, 10, "stop", false)
                                },
                                CancellationToken.None);
        Assert.That(output.ToString(), Is.EqualTo(beforeUsage));
        await sink.PublishAsync(new HostEvent
                                {
                                    Kind    = HostEventKind.ModelRequestFinished,
                                    Payload = new ModelRequestProgress(1, false, 13_000, "completed")
                                },
                                CancellationToken.None);
        await sink.PublishAsync(start, CancellationToken.None);
        Assert.That(output.ToString(), Does.EndWith("\r等待中.   0s"));
    }

    [Test]
    public async Task HiddenUsageDoesNotBreakAnswerLine()
    {
        using var       output = new StringWriter { NewLine = "\n" };
        await using var sink   = new ConsoleHostEventSink(output : output);
        await sink.PublishAsync(new HostEvent { Kind = HostEventKind.AssistantTextDelta, Payload = "hel" },
                                CancellationToken.None);
        await sink.PublishAsync(new HostEvent
                                {
                                    Kind = HostEventKind.ModelUsage, Payload = new ModelUsage(100, 10, "stop", false)
                                },
                                CancellationToken.None);
        await sink.PublishAsync(new HostEvent { Kind = HostEventKind.AssistantTextDelta, Payload = "lo" },
                                CancellationToken.None);
        Assert.That(output.ToString(), Is.EqualTo("hello"));
    }

    [Test]
    public async Task RedirectedStatusPrintsOnceAndNeverLeaksReasoning()
    {
        using var       output = new StringWriter { NewLine = "\n" };
        await using var sink   = new ConsoleHostEventSink(output : output);
        await sink.PublishAsync(Progress(), CancellationToken.None);
        await sink.PublishAsync(Reasoning("private"), CancellationToken.None);
        await sink.PublishAsync(Progress() with { Kind = HostEventKind.ModelRequestWaiting }, CancellationToken.None);
        Assert.That(output.ToString(), Is.EqualTo("思考中... 0s\n"));
        Assert.That(sink.IncludeReasoningText, Is.False);
    }

    private static HostEvent Reasoning(string text) =>
        new() { Kind = HostEventKind.AssistantReasoningDelta, Payload = text };

    private static HostEvent Progress() => new()
    {
        Kind    = HostEventKind.ModelRequestActivity,
        Payload = new ModelRequestProgress(1, false, 1) { Phase = ModelRequestPhase.ReceivingReasoning }
    };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    private sealed class ManualAnimationTimeProvider : TimeProvider
    {
        private  long           _timestamp;
        private  TimerCallback? _callback;
        private  object?        _state;
        internal bool           Disposed { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state    = state;
            return new AnimationTimer(this);
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        internal void Tick()
        {
            Interlocked.Add(ref _timestamp, TimeSpan.TicksPerSecond / 2);
            _callback?.Invoke(_state);
        }

        private sealed class AnimationTimer(ManualAnimationTimeProvider owner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
                owner.Disposed  = true;
                owner._callback = null;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}

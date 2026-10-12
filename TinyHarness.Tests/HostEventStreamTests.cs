using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     M12.2 事件贯通契约：文本增量按流发布、工具生命周期与审批结论有序发布、usage 按请求
///     发布并累计、失败终态先发 RunError、发布内联串行（有界消费）且接收方故障不终止运行。
///     The M12.2 event-wiring contract: text deltas publish per stream fragment, tool
///     lifecycle and approval resolutions publish in order, usage publishes per request
///     and accumulates, a failed terminal publishes RunError first, publication is inline
///     and serial (bounded consumption), and a failing sink never aborts the run.
/// </summary>
public class HostEventStreamTests
{
    private static AgentOptions Options(int maxSteps = 10) => new()
    {
        Model                     = "test-model",
        MaxAgentSteps             = maxSteps,
        DefaultToolTimeoutSeconds = 30
    };

    private static IReadOnlyList<ChatStreamEvent> TextWithUsage(
        string content, int inputTokens, int outputTokens, string finishReason = "stop") =>
    [
        new() { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = content },
        new()
        {
            Kind         = ChatStreamEventKind.End,
            InputTokens  = inputTokens,
            OutputTokens = outputTokens,
            FinishReason = finishReason
        }
    ];

    [Fact]
    public async Task TextDeltas_PublishOneEventPerContentFragmentInStreamOrder()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("He", "llo ", "world"));
        var sink = new TestHostEventSink();
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), null, null, events : sink);

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        var deltas = sink.Events.Where(@event => @event.Kind == HostEventKind.AssistantTextDelta)
                         .Select(@event => Assert.IsType<string>(@event.Payload))
                         .ToArray();
        Assert.Equal(["He", "llo ", "world"], deltas);
        // Sequence numbers stay strictly increasing across kinds inside the run.
        Assert.Equal(sink.Events.Select(@event => @event.Sequence).OrderBy(number => number),
                     sink.Events.Select(@event => @event.Sequence));
        Assert.Equal(1, sink.Events[0].Sequence);
    }

    [Fact]
    public async Task InteractiveToolCall_PublishesPreparedRequestedResolvedStartedFinishedInOrder()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("f.txt", "old\n");
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("apply_patch",
                                               new JsonObject
                                                   {
                                                       ["patch"] = "--- a/f.txt\n+++ b/f.txt\n@@ -1 +1 @@\n" +
                                                                   "-old\n+new\n"
                                                   }
                                                  .ToJsonString(),
                                               "p1"));
        client.Enqueue(FakeChatClient.Text("patched"));
        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalAction.AllowOnce);
        var sink = new TestHostEventSink();
        var loop = new AgentLoop(client,
                                 new ToolRegistry([new ApplyPatchTool(dir.Workspace)]),
                                 Options(),
                                 new PermissionEngine(dir.Root),
                                 approver,
                                 events : sink);

        var result = await loop.RunAsync("sys", "patch", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal([
                         HostEventKind.RunStarted,
                         HostEventKind.ToolPrepared,
                         HostEventKind.ApprovalRequested,
                         HostEventKind.ApprovalResolved,
                         HostEventKind.ToolStarted,
                         HostEventKind.ToolFinished,
                         HostEventKind.AssistantTextDelta,
                         HostEventKind.RunFinished
                     ],
                     sink.Events.Select(@event => @event.Kind).ToArray());

        var requested = Assert.IsType<ToolPreparation>(sink.Events[2].Payload);
        Assert.Equal("apply_patch", requested.ToolName);

        var resolution = Assert.IsType<ApprovalResolution>(sink.Events[3].Payload);
        Assert.Equal(PermissionDecision.Ask, resolution.Decision);
        Assert.Equal(ApprovalAction.AllowOnce, resolution.Action);
        Assert.Equal("allow once", resolution.Outcome);

        var outcome = Assert.IsType<ToolExecutionOutcome>(sink.Events[5].Payload);
        Assert.Equal("apply_patch", outcome.Preparation.ToolName);
        Assert.True(outcome.Result.Succeeded);
    }

    [Fact]
    public async Task RuleAllowedTool_PublishesResolutionWithoutApprovalRequest()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("note.txt", "hello\n");
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("read_file", new JsonObject { ["path"] = "note.txt" }.ToJsonString()));
        client.Enqueue(FakeChatClient.Text("read it"));
        var sink = new TestHostEventSink();
        var loop = new AgentLoop(client,
                                 new ToolRegistry([new ReadFileTool(dir.Workspace)]),
                                 Options(),
                                 new PermissionEngine(dir.Root),
                                 null,
                                 events : sink);

        await loop.RunAsync("sys", "read", CancellationToken.None);

        // filesystem.read is allowed by the default policy: the resolution publishes
        // with a null action and no ApprovalRequested precedes it.
        Assert.DoesNotContain(sink.Events, @event => @event.Kind == HostEventKind.ApprovalRequested);
        var resolution = Assert.IsType<ApprovalResolution>(
                                                           sink.Events.Single(@event => @event.Kind ==
                                                                                  HostEventKind.ApprovalResolved)
                                                               .Payload);
        Assert.Equal(PermissionDecision.Allow, resolution.Decision);
        Assert.Null(resolution.Action);
        Assert.Equal("allow", resolution.Outcome);
    }

    [Fact]
    public async Task DeniedApproval_PublishesResolutionWithoutToolLifecycle()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("f.txt", "old\n");
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("apply_patch",
                                               new JsonObject
                                                   {
                                                       ["patch"] = "--- a/f.txt\n+++ b/f.txt\n@@ -1 +1 @@\n" +
                                                                   "-old\n+new\n"
                                                   }
                                                  .ToJsonString()));
        client.Enqueue(FakeChatClient.Text("understood"));
        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalAction.Deny);
        var sink = new TestHostEventSink();
        var loop = new AgentLoop(client,
                                 new ToolRegistry([new ApplyPatchTool(dir.Workspace)]),
                                 Options(),
                                 new PermissionEngine(dir.Root),
                                 approver,
                                 events : sink);

        var result = await loop.RunAsync("sys", "patch", CancellationToken.None);

        Assert.Equal(0, result.ToolExecutions);
        var resolution = Assert.IsType<ApprovalResolution>(
                                                           sink.Events.Single(@event => @event.Kind ==
                                                                                  HostEventKind.ApprovalResolved)
                                                               .Payload);
        Assert.Equal(PermissionDecision.Ask, resolution.Decision);
        Assert.Equal(ApprovalAction.Deny, resolution.Action);
        Assert.StartsWith("Permission denied by user:", resolution.Outcome);
        Assert.DoesNotContain(sink.Events, @event => @event.Kind is HostEventKind.ToolStarted
                                                                 or HostEventKind.ToolFinished);
    }

    [Fact]
    public async Task ModelUsage_PublishesPerRequestAndAccumulatesIntoTerminalResult()
    {
        var client = new FakeChatClient();
        // A tool-call round keeps the loop going so a second request follows.
        client.Enqueue([
            new ChatStreamEvent
            {
                Kind                   = ChatStreamEventKind.ToolCallDelta,
                ToolCallIndex          = 0,
                ToolCallId             = "c1",
                ToolCallFunctionName   = "t",
                ToolCallArgumentsDelta = "{}"
            },
            new ChatStreamEvent
            {
                Kind         = ChatStreamEventKind.End,
                InputTokens  = 120,
                OutputTokens = 30,
                FinishReason = "tool_calls"
            }
        ]);
        client.Enqueue(TextWithUsage("second", 150, 45));
        var sink = new TestHostEventSink();
        var loop = new AgentLoop(client, new ToolRegistry([new FakeTool("t")]), Options(), null, null, events : sink);

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        var usages = sink.Events.Where(@event => @event.Kind == HostEventKind.ModelUsage)
                         .Select(@event => Assert.IsType<ModelUsage>(@event.Payload))
                         .ToArray();
        Assert.Equal(2, usages.Length);
        Assert.All(usages, usage => Assert.False(usage.SummaryRequest));
        Assert.Equal(120, usages[0].InputTokens);
        Assert.Equal(45, usages[1].OutputTokens);
        Assert.Equal("stop", usages[1].FinishReason);
        Assert.Equal(270, result.InputTokens);
        Assert.Equal(75, result.OutputTokens);
    }

    [Fact]
    public async Task FailedRun_PublishesRunErrorBeforeRunFinishedAndCancelledDoesNot()
    {
        var failing = new FakeChatClient { ThrownError = new HttpRequestException("endpoint unreachable") };
        var sink    = new TestHostEventSink();
        var loop    = new AgentLoop(failing, new ToolRegistry([]), Options(), null, null, events : sink);

        var failed = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.Equal(AgentStatus.Failed, failed.Status);
        Assert.Equal([HostEventKind.RunStarted, HostEventKind.RunError, HostEventKind.RunFinished],
                     sink.Events.Select(@event => @event.Kind).ToArray());
        Assert.Equal("endpoint unreachable", Assert.IsType<string>(sink.Events[1].Payload));

        // Cancellation is not an error: its terminal event stays the only ending.
        var cancelling = new FakeChatClient();
        cancelling.Enqueue(FakeChatClient.Text("partial"));
        var       cancelSink = new TestHostEventSink();
        using var cts        = new CancellationTokenSource();
        cancelling.CancelMidStream = cts.Cancel;
        var cancelLoop = new AgentLoop(cancelling, new ToolRegistry([]), Options(), null, null, events : cancelSink);
        var cancelled  = await cancelLoop.RunAsync("sys", "go", cts.Token);

        Assert.Equal(AgentStatus.Cancelled, cancelled.Status);
        // The first delta was already delivered before the cancel hit the stream;
        // the terminal event still arrives and no RunError precedes it.
        Assert.Equal([HostEventKind.RunStarted, HostEventKind.AssistantTextDelta, HostEventKind.RunFinished],
                     cancelSink.Events.Select(@event => @event.Kind).ToArray());
        Assert.DoesNotContain(cancelSink.Events, @event => @event.Kind == HostEventKind.RunError);
    }

    [Fact]
    public async Task SinkFaultOnDelta_LatchesFaultNoteAndStillDeliversTerminal()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("hi"));
        var sink = new ThrowingSink(HostEventKind.AssistantTextDelta);
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), null, null, events : sink);

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal("hi", result.FinalMessage);
        Assert.Contains("sink exploded", result.Error);
        Assert.Equal(3, sink.Published); // RunStarted, the failing delta, RunFinished.
    }

    [Fact]
    public async Task Publish_IsAwaitedInlineSoSinksNeverOverlapAndOrderingHolds()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("a", "b", "c"));
        var sink = new SlowSink();
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), null, null, events : sink);

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        // Inline-await delivery is the bounded-consumption strategy: no queue, a slow
        // consumer throttles the publisher, and events never overlap or reorder.
        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(1, sink.MaxConcurrent);
        Assert.Equal([
                         HostEventKind.RunStarted,
                         HostEventKind.AssistantTextDelta,
                         HostEventKind.AssistantTextDelta,
                         HostEventKind.AssistantTextDelta,
                         HostEventKind.RunFinished
                     ],
                     sink.Kinds.ToArray());
    }

    [Fact]
    public async Task UsageTotals_RestartForEachTurnOnAReusedLoop()
    {
        var client = new FakeChatClient();
        client.Enqueue(TextWithUsage("one", 100, 10));
        client.Enqueue(TextWithUsage("two", 200, 20));
        var sink    = new TestHostEventSink();
        var context = new ConversationContext();
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), null, null, events : sink,
                                 context : context);

        var first  = await loop.RunTurnAsync("sys", "u1", CancellationToken.None);
        var second = await loop.RunTurnAsync("sys", "u2", CancellationToken.None);

        Assert.Equal(100, first.InputTokens);
        Assert.Equal(200, second.InputTokens);
    }

    [Fact]
    public async Task SummaryRequest_UsageIsFlaggedAndItsStreamNeverPublishesDeltas()
    {
        var tool   = new FakeTool("t") { ResultContent = new string('x', 1600) };
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", "a_1"));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", "b_2"));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", "c_3"));
        // The summarizer answer is internal text: its usage must publish flagged as a
        // summary request, and its stream must never surface as answer deltas.
        client.Enqueue(TextWithUsage(StateJson("goal-one", "a.cs", "decision-x"), 500, 40));
        client.Enqueue(TextWithUsage("done", 300, 25));
        var sink = new TestHostEventSink();
        var loop = new AgentLoop(client, new ToolRegistry([tool]), TightBudgetOptions(), null, null, events : sink);

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        var usages = sink.Events.Where(@event => @event.Kind == HostEventKind.ModelUsage)
                         .Select(@event => Assert.IsType<ModelUsage>(@event.Payload))
                         .ToArray();
        Assert.Equal(2, usages.Length);
        Assert.True(usages[0].SummaryRequest);
        Assert.False(usages[1].SummaryRequest);
        // Only the final answer's own deltas surfaced; the summary JSON never did.
        Assert.Equal(["done"],
                     sink.Events.Where(@event => @event.Kind == HostEventKind.AssistantTextDelta)
                         .Select(@event => Assert.IsType<string>(@event.Payload))
                         .ToArray());
        Assert.Equal(800, result.InputTokens);
        Assert.Equal(65, result.OutputTokens);
    }

    private static AgentOptions TightBudgetOptions() => new()
    {
        Model                     = "test-model",
        MaxAgentSteps             = 10,
        DefaultToolTimeoutSeconds = 30,
        Context = new ContextOptions
        {
            ContextWindowTokens  = 1000,
            ReservedOutputTokens = 30
        }
    };

    private static string StateJson(string goal, string modified, string decision) => new JsonObject
    {
        ["goal"]               = goal,
        ["constraints"]        = new JsonArray(),
        ["decisions"]          = new JsonArray(decision),
        ["filesInspected"]     = new JsonArray("src"),
        ["filesModified"]      = new JsonArray(modified),
        ["commandsAndResults"] = new JsonArray(),
        ["pendingWork"]        = new JsonArray()
    }.ToJsonString();

    private sealed class ScriptedApprover : IApprovalProvider
    {
        private readonly Queue<ApprovalAction> _actions = new();

        public Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken) =>
            Task.FromResult(_actions.Dequeue());

        public void Enqueue(ApprovalAction action)
        {
            _actions.Enqueue(action);
        }
    }

    private sealed class ThrowingSink(HostEventKind failOn) : IHostEventSink
    {
        public int Published { get; private set; }

        public ValueTask PublishAsync(HostEvent @event, CancellationToken cancellationToken)
        {
            Published++;
            return @event.Kind == failOn
                ? throw new InvalidOperationException("sink exploded")
                : ValueTask.CompletedTask;
        }
    }

    /// <summary>
    ///     每次发布小睡的 sink，用于钉住内联串行交付：最大并发必须为 1，顺序保持不变。
    ///     A sink that naps on every publication, pinning the inline-serial delivery: max
    ///     concurrency must stay at 1 and order must hold.
    /// </summary>
    private sealed class SlowSink : IHostEventSink
    {
        private int _inside;

        public int MaxConcurrent { get; private set; }

        public List<HostEventKind> Kinds { get; } = [];

        public async ValueTask PublishAsync(HostEvent @event, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _inside);
            MaxConcurrent = Math.Max(MaxConcurrent, now);
            try
            {
                await Task.Delay(10, cancellationToken);
                Kinds.Add(@event.Kind);
            }
            finally
            {
                Interlocked.Decrement(ref _inside);
            }
        }
    }
}

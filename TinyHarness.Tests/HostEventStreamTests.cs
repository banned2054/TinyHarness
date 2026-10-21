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
///     <para>
///         事件贯通契约：文本增量按流发布、工具生命周期与审批结论有序发布、usage 按请求
///         发布并累计、失败终态先发 RunError、发布内联串行（有界消费）且接收方故障不终止运行。
///     </para>
///     <para>
///         The event-wiring contract: text deltas publish per stream fragment, tool
///         lifecycle and approval resolutions publish in order, usage publishes per request
///         and accumulates, a failed terminal publishes RunError first, publication is inline
///         and serial (bounded consumption), and a failing sink never aborts the run.
///     </para>
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

    [Test]
    public async Task TextDeltas_PublishOneEventPerContentFragmentInStreamOrder()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("He", "llo ", "world"));
        var sink = new TestHostEventSink();
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), null, null, events : sink);

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        var deltas = sink.Events.Where(@event => @event.Kind == HostEventKind.AssistantTextDelta)
                         .Select(@event => TestAssert.TypeOf<string>(@event.Payload)).ToArray();
        Assert.That(deltas, Is.EqualTo(["He", "llo ", "world"]));
        // 运行内各事件种类的序号保持严格递增。
        Assert.That(sink.Events.Select(@event => @event.Sequence),
                    Is.EqualTo(sink.Events.Select(@event => @event.Sequence).OrderBy(number => number)));
        Assert.That(sink.Events[0].Sequence, Is.EqualTo(1));
    }

    [Test]
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
                                                   }.ToJsonString(),
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

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(sink.Events.Select(@event => @event.Kind).ToArray(), Is.EqualTo([
            HostEventKind.RunStarted,
            HostEventKind.ToolPrepared,
            HostEventKind.ApprovalRequested,
            HostEventKind.ApprovalResolved,
            HostEventKind.ToolStarted,
            HostEventKind.ToolFinished,
            HostEventKind.AssistantTextDelta,
            HostEventKind.RunFinished
        ]));

        var requested = TestAssert.TypeOf<ToolPreparation>(sink.Events[2].Payload);
        Assert.That(requested.ToolName, Is.EqualTo("apply_patch"));

        var resolution = TestAssert.TypeOf<ApprovalResolution>(sink.Events[3].Payload);
        Assert.That(resolution.Decision, Is.EqualTo(PermissionDecision.Ask));
        Assert.That(resolution.Action, Is.EqualTo(ApprovalAction.AllowOnce));
        Assert.That(resolution.Outcome, Is.EqualTo("allow once"));

        var outcome = TestAssert.TypeOf<ToolExecutionOutcome>(sink.Events[5].Payload);
        Assert.That(outcome.Preparation.ToolName, Is.EqualTo("apply_patch"));
        Assert.That(outcome.Result.Succeeded, Is.True);
    }

    [Test]
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

        // filesystem.read 被默认策略允许：结论以 null action 发布，之前没有 ApprovalRequested。
        TestAssert.DoesNotContainAny(@event => @event.Kind == HostEventKind.ApprovalRequested, sink.Events);
        var resolution = TestAssert.TypeOf<ApprovalResolution>(sink.Events.Single(@event =>
            @event.Kind == HostEventKind.ApprovalResolved)
                                                                   .Payload);
        Assert.That(resolution.Decision, Is.EqualTo(PermissionDecision.Allow));
        Assert.That(resolution.Action, Is.Null);
        Assert.That(resolution.Outcome, Is.EqualTo("allow"));
    }

    [Test]
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
                                                   }.ToJsonString()));
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

        Assert.That(result.ToolExecutions, Is.EqualTo(0));
        var resolution = TestAssert.TypeOf<ApprovalResolution>(sink.Events.Single(@event =>
            @event.Kind == HostEventKind.ApprovalResolved)
                                                                   .Payload);
        Assert.That(resolution.Decision, Is.EqualTo(PermissionDecision.Ask));
        Assert.That(resolution.Action, Is.EqualTo(ApprovalAction.Deny));
        Assert.That(resolution.Outcome, Does.StartWith("Permission denied by user:"));
        TestAssert.DoesNotContainAny(@event => @event.Kind is HostEventKind.ToolStarted or HostEventKind.ToolFinished, sink.Events);
    }

    [Test]
    public async Task ModelUsage_PublishesPerRequestAndAccumulatesIntoTerminalResult()
    {
        var client = new FakeChatClient();
        // 工具调用回合让循环继续，从而产生第二个请求。
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
                         .Select(@event => TestAssert.TypeOf<ModelUsage>(@event.Payload)).ToArray();
        Assert.That(usages.Length, Is.EqualTo(2));
        TestAssert.All(usages, usage => Assert.That(usage.SummaryRequest, Is.False));
        Assert.That(usages[0].InputTokens, Is.EqualTo(120));
        Assert.That(usages[1].OutputTokens, Is.EqualTo(45));
        Assert.That(usages[1].FinishReason, Is.EqualTo("stop"));
        Assert.That(result.InputTokens, Is.EqualTo(270));
        Assert.That(result.OutputTokens, Is.EqualTo(75));
    }

    [Test]
    public async Task FailedRun_PublishesRunErrorBeforeRunFinishedAndCancelledDoesNot()
    {
        var failing = new FakeChatClient { ThrownError = new HttpRequestException("endpoint unreachable") };
        var sink    = new TestHostEventSink();
        var loop    = new AgentLoop(failing, new ToolRegistry([]), Options(), null, null, events : sink);

        var failed = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.That(failed.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(sink.Events.Select(@event => @event.Kind).ToArray(),
                    Is.EqualTo([HostEventKind.RunStarted, HostEventKind.RunError, HostEventKind.RunFinished]));
        Assert.That(TestAssert.TypeOf<string>(sink.Events[1].Payload), Is.EqualTo("endpoint unreachable"));

        // 取消不是错误：它的终态事件是唯一结尾。
        var cancelling = new FakeChatClient();
        cancelling.Enqueue(FakeChatClient.Text("partial"));
        var       cancelSink = new TestHostEventSink();
        using var cts        = new CancellationTokenSource();
        cancelling.CancelMidStream = cts.Cancel;
        var cancelLoop = new AgentLoop(cancelling, new ToolRegistry([]), Options(), null, null, events : cancelSink);
        var cancelled  = await cancelLoop.RunAsync("sys", "go", cts.Token);

        Assert.That(cancelled.Status, Is.EqualTo(AgentStatus.Cancelled));
        // 取消到达流之前第一个增量已交付；终态事件仍会到达，之前没有 RunError。
        Assert.That(cancelSink.Events.Select(@event => @event.Kind).ToArray(),
                    Is.EqualTo([
                        HostEventKind.RunStarted, HostEventKind.AssistantTextDelta, HostEventKind.RunFinished
                    ]));
        TestAssert.DoesNotContainAny(@event => @event.Kind == HostEventKind.RunError, cancelSink.Events);
    }

    [Test]
    public async Task SinkFaultOnDelta_LatchesFaultNoteAndStillDeliversTerminal()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("hi"));
        var sink = new ThrowingSink(HostEventKind.AssistantTextDelta);
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), null, null, events : sink);

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(result.FinalMessage, Is.EqualTo("hi"));
        Assert.That(result.Error, Does.Contain("sink exploded"));
        Assert.That(sink.Published, Is.EqualTo(3)); // RunStarted、失败的增量、RunFinished。
    }

    [Test]
    public async Task Publish_IsAwaitedInlineSoSinksNeverOverlapAndOrderingHolds()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("a", "b", "c"));
        var sink = new SlowSink();
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), null, null, events : sink);

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        // 内联 await 交付即有界消费策略：无队列，慢消费者反压发布者，事件不会重叠或乱序。
        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(sink.MaxConcurrent, Is.EqualTo(1));
        Assert.That(sink.Kinds.ToArray(), Is.EqualTo([
            HostEventKind.RunStarted,
            HostEventKind.AssistantTextDelta,
            HostEventKind.AssistantTextDelta,
            HostEventKind.AssistantTextDelta,
            HostEventKind.RunFinished
        ]));
    }

    [Test]
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

        Assert.That(first.InputTokens, Is.EqualTo(100));
        Assert.That(second.InputTokens, Is.EqualTo(200));
    }

    [Test]
    public async Task SummaryRequest_UsageIsFlaggedAndItsStreamNeverPublishesDeltas()
    {
        var tool   = new FakeTool("t") { ResultContent = new string('x', 1600) };
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", "a_1"));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", "b_2"));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", "c_3"));
        // 摘要回答是内部文本：其 usage 必须以摘要请求标记发布，其流不得作为回答增量展示。
        client.Enqueue(TextWithUsage(Handoff("goal-one", "a.cs", "decision-x"), 500, 40));
        client.Enqueue(TextWithUsage("done", 300, 25));
        var sink = new TestHostEventSink();
        var loop = new AgentLoop(client, new ToolRegistry([tool]), TightBudgetOptions(), null, null, events : sink);

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        var usages = sink.Events.Where(@event => @event.Kind == HostEventKind.ModelUsage)
                         .Select(@event => TestAssert.TypeOf<ModelUsage>(@event.Payload)).ToArray();
        Assert.That(usages.Length, Is.EqualTo(2));
        Assert.That(usages[0].SummaryRequest, Is.True);
        Assert.That(usages[1].SummaryRequest, Is.False);
        // 只有最终回答自身的增量被展示；内部交接摘要 从未出现。
        Assert.That(sink.Events.Where(@event => @event.Kind == HostEventKind.AssistantTextDelta)
                        .Select(@event => TestAssert.TypeOf<string>(@event.Payload)).ToArray(), Is.EqualTo(["done"]));
        Assert.That(result.InputTokens, Is.EqualTo(800));
        Assert.That(result.OutputTokens, Is.EqualTo(65));
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

    private static string Handoff(string goal, string modified, string decision) =>
        $"Goal: {goal}. Modified: {modified}. Decision: {decision}. Continue pending work.";

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
    ///     <para>每次发布小睡的 sink，用于钉住内联串行交付：最大并发必须为 1，顺序保持不变。</para>
    ///     <para>
    ///         A sink that naps on every publication, pinning the inline-serial delivery: max
    ///         concurrency must stay at 1 and order must hold.
    ///     </para>
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

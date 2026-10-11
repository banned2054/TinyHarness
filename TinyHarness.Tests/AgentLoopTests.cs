using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public class AgentLoopTests
{
    private static AgentOptions Options(int maxSteps = 10)
    {
        return new AgentOptions
        {
            Model                     = "test-model",
            MaxAgentSteps             = maxSteps,
            DefaultToolTimeoutSeconds = 30
        };
    }

    [Fact]
    public async Task PlainText_Completes()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("Hello from ", "the model."));
        var loop = new AgentLoop(client, new ToolRegistry([]), Options());

        var result = await loop.RunAsync("sys", "hi", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal("Hello from the model.", result.FinalMessage);
        Assert.Equal(1, client.Requests);
        Assert.Equal("test-model", client.LastRequestModel);
        Assert.Contains(loop.History, m => m is { Role: ChatRole.Assistant, Content: "Hello from the model." });
    }

    [Fact]
    public async Task SingleToolCall_ExecutesAndReturnsToolResult()
    {
        var client = new FakeChatClient();
        var tool   = new FakeTool("echo_test");
        client.Enqueue(FakeChatClient.ToolCall("echo_test", "{\"value\":\"x\"}"));
        client.Enqueue(FakeChatClient.Text("done"));
        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options());

        var result = await loop.RunAsync("sys", "run it", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(1, tool.ExecuteCount);
        Assert.Equal(2, client.Requests);
        Assert.Contains(loop.History, m => m is { Role: ChatRole.Tool, Content: "echo_test ok" });
    }

    [Fact]
    public async Task SplitArguments_AreAssembledCorrectly()
    {
        var client = new FakeChatClient();
        var tool   = new FakeTool("echo_test");
        client.Enqueue(FakeChatClient.ToolCall("echo_test", "{\"value\":\"abc\"}", "c1"));
        client.Enqueue(FakeChatClient.Text("ok"));
        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options());

        var result = await loop.RunAsync("sys", "run", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(1, tool.ExecuteCount);

        var toolExecution = loop.History.Single(m => m.Role == ChatRole.Tool);
        Assert.Equal("echo_test ok", toolExecution.Content);
    }

    [Fact]
    public async Task MultipleToolCalls_ExecuteSequentiallyInOneTurn()
    {
        var client = new FakeChatClient();
        var a      = new FakeTool("tool_a");
        var b      = new FakeTool("tool_b");
        var calls = new List<ChatStreamEvent>
        {
            new()
            {
                Kind                 = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 0, ToolCallId = "a",
                ToolCallFunctionName = "tool_a", ToolCallArgumentsDelta                 = "{}"
            },
            new()
            {
                Kind                 = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 1, ToolCallId = "b",
                ToolCallFunctionName = "tool_b", ToolCallArgumentsDelta                 = "{}"
            },
            new() { Kind = ChatStreamEventKind.End }
        };
        client.Enqueue(calls);
        client.Enqueue(FakeChatClient.Text("all done"));
        var loop = new AgentLoop(client, new ToolRegistry([a, b]), Options());

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(1, a.ExecuteCount);
        Assert.Equal(1, b.ExecuteCount);
        Assert.Equal(2, result.ToolExecutions);
    }

    [Fact]
    public async Task UnknownTool_ReturnsErrorToModelAndContinues()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("missing_tool", "{}"));
        client.Enqueue(FakeChatClient.Text("ok"));
        var loop = new AgentLoop(client, new ToolRegistry([]), Options());

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(0, result.ToolExecutions);
        Assert.Contains(loop.History, m => m.Role == ChatRole.Tool && m.Content.Contains("Unknown tool"));
    }

    [Fact]
    public async Task StepLimit_IsReached()
    {
        var client = new FakeChatClient();
        var tool   = new FakeTool("loop");
        var loop   = new AgentLoop(client, new ToolRegistry([tool]), Options(3));

        for (var i = 0; i < 10; i++) client.Enqueue(FakeChatClient.ToolCall("loop", "{}"));

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.Equal(AgentStatus.StepLimitReached, result.Status);
        Assert.Equal(3, result.Steps);
    }

    [Fact]
    public async Task InvalidJson_StreamThrows()
    {
        var client = new FakeChatClient();
        client.Enqueue(new List<ChatStreamEvent>
        {
            new()
            {
                Kind = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 0, ToolCallFunctionName = "t",
                ToolCallArgumentsDelta = "{not json"
            },
            new() { Kind = ChatStreamEventKind.End }
        });
        var loop = new AgentLoop(client, new ToolRegistry([]), Options());

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.Equal(AgentStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Cancellation_ReturnsCancelled()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("never replied")); // cancelled before consumed
        var loop = new AgentLoop(client, new ToolRegistry([]), Options());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await loop.RunAsync("sys", "go", cts.Token);

        Assert.Equal(AgentStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task CancellationDuringStream_ReturnsCancelled()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("a", "b", "c"));
        var loop = new AgentLoop(client, new ToolRegistry([]), Options());

        using var cts = new CancellationTokenSource();
        client.CancelMidStream = cts.Cancel;

        var result = await loop.RunAsync("sys", "go", cts.Token);

        Assert.Equal(AgentStatus.Cancelled, result.Status);
        Assert.Single(loop.History, m => m.Role         == ChatRole.User);
        Assert.DoesNotContain(loop.History, m => m.Role == ChatRole.Assistant);
    }

    /// <summary>
    ///     一个文本片段加携带 usage 的 End 事件的脚本化响应；用于验证审计链。
    ///     One text fragment plus an End event carrying usage; drives the audit chain.
    /// </summary>
    private static IReadOnlyList<ChatStreamEvent> TextWithUsage(
        string text, int inputTokens, int outputTokens, string finishReason) =>
    [
        new() { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = text },
        new()
        {
            Kind         = ChatStreamEventKind.End,
            InputTokens  = inputTokens,
            OutputTokens = outputTokens,
            FinishReason = finishReason
        }
    ];


    [Fact]
    public async Task ModelUsage_EndCarryingUsageIsSentToTheRecorder()
    {
        var client   = new FakeChatClient();
        var recorder = new CapturingRecorder();
        client.Enqueue(TextWithUsage("hello", 120, 45, "stop"));
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), null, null,
                                 recorder);

        var result = await loop.RunAsync("sys", "hi", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal((120, 45, "stop"), Assert.Single(recorder.UsageRecords));
    }

    [Fact]
    public async Task ModelUsage_FinishReasonAloneIsStillRecorded()
    {
        var client   = new FakeChatClient();
        var recorder = new CapturingRecorder();
        client.Enqueue(
        [
            new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "hello" },
            new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason          = "stop" }
        ]);
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), null, null,
                                 recorder);

        var result = await loop.RunAsync("sys", "hi", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        // 只要有任一字段非空就写审计行；usage 缺失保持 null。
        // Any non-null field writes the audit line; missing usage stays null.
        Assert.Equal((null, null, "stop"), Assert.Single(recorder.UsageRecords));
    }

    [Fact]
    public async Task ModelUsage_StreamWithoutUsageOrFinishReasonRecordsNothing()
    {
        var client   = new FakeChatClient();
        var recorder = new CapturingRecorder();
        client.Enqueue(FakeChatClient.Text("hello")); // plain End: no usage, no finish reason
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), null, null,
                                 recorder);

        var result = await loop.RunAsync("sys", "hi", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Empty(recorder.UsageRecords);
    }

    [Fact]
    public async Task RunTurn_ContinuesSharedContextAndRejectsChangedSystemPrompt()
    {
        var client  = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("one"));
        client.Enqueue(FakeChatClient.Text("two"));
        var context = new ConversationContext();
        var loop    = new AgentLoop(client, new ToolRegistry([]), Options(), null, null, context: context);

        await loop.RunTurnAsync("sys", "u1", CancellationToken.None);

        // A different system prompt is refused before anything is appended; the
        // context still ends with turn 1's assistant reply.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => loop.RunTurnAsync("different", "u2", CancellationToken.None));
        Assert.Equal(3, context.Messages.Count);
        Assert.Equal("one", context.Messages[2].Content);

        // The matching system prompt continues on the shared context: the second
        // request carries the first exchange and only one system message.
        await loop.RunTurnAsync("sys", "u2", CancellationToken.None);
        Assert.Equal(2, client.Requests);
        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User],
                     client.RequestLog[1].Messages.Select(m => m.Role).ToArray());
        Assert.Single(client.RequestLog[1].Messages, m => m.Role == ChatRole.System);
    }

    [Fact]
    public async Task RunTurn_RefusesToContinueWhenHistoryEndsWithoutToolResults()
    {
        var client  = new FakeChatClient();
        var context = new ConversationContext();
        context.Append(ChatMessage.System("sys"));
        context.Append(ChatMessage.User("u1"));
        context.Append(ChatMessage.Assistant(string.Empty, [new ChatToolCall("call_1", "t", "{}")]));
        var loop = new AgentLoop(client, new ToolRegistry([new FakeTool("t")]), Options(), null, null,
                                 context: context);

        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loop.RunTurnAsync("sys", "u2", CancellationToken.None));

        Assert.Contains("call_1", rejected.Message);
        Assert.Equal(0, client.Requests);
        // Nothing was appended for the rejected turn.
        Assert.Equal(3, context.Messages.Count);
    }

    [Fact]
    public async Task RunEvents_SinkFailureOnNonTerminalEventDoesNotFailTheRun()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("done"));
        var sink = new ThrowingSink(HostEventKind.RunStarted);
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), null, null, events: sink);

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        // A display failure never aborts the run: the real outcome is preserved, the
        // fault is surfaced on the terminal result, and the terminal delivery happens.
        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal("done", result.FinalMessage);
        Assert.Contains("sink exploded", result.Error);
        Assert.Equal(2, sink.Published);
    }

    [Fact]
    public async Task RunEvents_SinkFailureOnTerminalEventPreservesTheOutcome()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("done"));
        var sink = new ThrowingSink(HostEventKind.RunFinished);
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), null, null, events: sink);

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal("done", result.FinalMessage);
        Assert.Contains("sink exploded", result.Error);
        Assert.Equal(2, sink.Published);
    }

    [Fact]
    public async Task RunTurn_EventSequenceRestartsAtOneForEachTurn()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("one"));
        client.Enqueue(FakeChatClient.Text("two"));
        var sink    = new TestHostEventSink();
        var context = new ConversationContext();
        var loop    = new AgentLoop(client, new ToolRegistry([]), Options(), null, null,
                                    events: sink, context: context);

        await loop.RunTurnAsync("sys", "u1", CancellationToken.None);
        await loop.RunTurnAsync("sys", "u2", CancellationToken.None);

        // Each turn is one run: the run-scoped sequence restarts at 1 instead of
        // accumulating across turns on a reused loop.
        Assert.Equal([1L, 2L, 1L, 2L], sink.Events.Select(@event => @event.Sequence).ToArray());
    }

    /// <summary>
    ///     在指定事件种类上抛出的 sink，用于钉住发布点的容错规则。
    ///     A sink that throws on one event kind, pinning the publish-site fault rules.
    /// </summary>
    private sealed class ThrowingSink(HostEventKind failOn) : IHostEventSink
    {
        public int Published { get; private set; }

        public ValueTask PublishAsync(HostEvent @event, CancellationToken cancellationToken)
        {
            Published++;
            if (@event.Kind == failOn) throw new InvalidOperationException("sink exploded");

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    ///     捕获 model_usage 审计调用的 recorder；其余方法均为空实现。
    ///     Captures model-usage audit calls; every other recorder method is a no-op.
    /// </summary>
    private sealed class CapturingRecorder : IRunRecorder
    {
        public List<(int? InputTokens, int? OutputTokens, string? FinishReason)> UsageRecords { get; } = [];

        public Task StartAsync(string systemPrompt, string userInput, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task RecordPreparedAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task RecordPermissionAsync(ToolPreparation   preparation, PermissionDecision decision, string outcome,
                                          CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task RecordResultAsync(
            ToolPreparation preparation, ToolResult result, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task RecordCompactionAsync(ContextChange change, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task RecordModelUsageAsync(
            int? inputTokens, int? outputTokens, string? finishReason, CancellationToken cancellationToken)
        {
            UsageRecords.Add((inputTokens, outputTokens, finishReason));
            return Task.CompletedTask;
        }

        public Task CompleteAsync(
            AgentResult       result, IReadOnlyList<ChatMessage> messages, StructuredState state,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}

using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public class AgentLoopTests
{
    private static AgentOptions Options(int maxSteps = 10) => new()
    {
        Model                     = "test-model",
        MaxAgentSteps             = maxSteps,
        DefaultToolTimeoutSeconds = 30
    };

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
        client.Enqueue(FakeChatClient.Text("never replied")); // 响应消费前已取消。
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
    ///     <para>生成含文本片段与 usage 终态事件的响应，用于验证审计记录。</para>
    ///     <para>Creates a response with text and a terminal usage event to verify audit records.</para>
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
        // 任一字段非空即写审计行；缺失的 usage 保持 null。
        Assert.Equal((null, null, "stop"), Assert.Single(recorder.UsageRecords));
    }

    [Fact]
    public async Task ModelUsage_StreamWithoutUsageOrFinishReasonRecordsNothing()
    {
        var client   = new FakeChatClient();
        var recorder = new CapturingRecorder();
        client.Enqueue(FakeChatClient.Text("hello")); // 终态不含 usage 或结束原因。
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), null, null,
                                 recorder);

        var result = await loop.RunAsync("sys", "hi", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Empty(recorder.UsageRecords);
    }

    [Fact]
    public async Task RunTurn_ContinuesSharedContextAndRejectsChangedSystemPrompt()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("one"));
        client.Enqueue(FakeChatClient.Text("two"));
        var context = new ConversationContext();
        var loop    = new AgentLoop(client, new ToolRegistry([]), Options(), null, null, context : context);

        await loop.RunTurnAsync("sys", "u1", CancellationToken.None);

        // 系统提示不一致时拒绝追加，保留上一回合的完整历史。
        await Assert.ThrowsAsync<InvalidOperationException>(() => loop.RunTurnAsync("different", "u2",
                                                                     CancellationToken.None));
        Assert.Equal(3, context.Messages.Count);
        Assert.Equal("one", context.Messages[2].Content);

        // 匹配的系统提示复用上下文；后续请求保留旧回合且只有一条系统消息。
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
                                 context : context);

        var rejected =
            await Assert.ThrowsAsync<InvalidOperationException>(() => loop.RunTurnAsync("sys", "u2",
                                                                         CancellationToken.None));

        Assert.Contains("call_1", rejected.Message);
        Assert.Equal(0, client.Requests);
        // 被拒绝的回合不追加历史。
        Assert.Equal(3, context.Messages.Count);
    }

    [Fact]
    public void LastCompleteBoundary_ClassifiesCompleteRewindableAndDamagedHistories()
    {
        IReadOnlyList<ChatMessage> Complete() =>
        [
            ChatMessage.System("sys"), ChatMessage.User("u"),
            ChatMessage.Assistant(string.Empty, [new ChatToolCall("call_1", "t", "{}")]),
            ChatMessage.Tool("t", "call_1", "ok"),
            ChatMessage.Assistant("done")
        ];

        Assert.Equal(5, ChatHistoryInvariants.LastCompleteBoundary(Complete()));

        // 尾部工具组即使已有部分结果，也须整体回退到发起调用前，不能拆分原子组。
        var rewound = new List<ChatMessage>(Complete())
        {
            ChatMessage.User("u2"),
            ChatMessage.Assistant(string.Empty,
                                  [new ChatToolCall("call_2", "t", "{}"), new ChatToolCall("call_3", "t", "{}")]),
            ChatMessage.Tool("t", "call_2", "partial")
        };
        Assert.Equal(6, ChatHistoryInvariants.LastCompleteBoundary(rewound));

        // 非尾部的不完整工具组属于损坏，不能按尾部回退。
        var damaged = new List<ChatMessage>
        {
            ChatMessage.System("sys"),
            ChatMessage.Assistant(string.Empty, [new ChatToolCall("call_1", "t", "{}")]),
            ChatMessage.User("after the gap")
        };
        Assert.Equal(-1, ChatHistoryInvariants.LastCompleteBoundary(damaged));
    }

    [Fact]
    public async Task RunTurn_RecordsAPrepareFailureAsKnownNotStarted()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("missing", "{}"));
        client.Enqueue(FakeChatClient.Text("done"));
        var loop = new AgentLoop(client, new ToolRegistry([]), Options());

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        var record = Assert.Single(result.ToolCalls!);
        Assert.Equal("missing", record.ToolName);
        Assert.Null(record.Capability);
        Assert.False(record.SideEffects);
        Assert.Equal(ToolCallExecutionState.KnownNotStarted, record.State);
        Assert.Null(record.Approval);
        Assert.Contains("Unknown tool", record.PreparationError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTurn_RecordsApprovalOutcomesAndConfirmedResults()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("t", "{}"));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}"));
        client.Enqueue(FakeChatClient.Text("done"));
        using var dir      = new TestTempDir();
        var       tool     = new FakeTool("t");
        var       engine   = new PermissionEngine(dir.Root);
        var       approver = new ScriptedApprover(ApprovalAction.AllowOnce, ApprovalAction.Deny);
        var       loop     = new AgentLoop(client, new ToolRegistry([tool]), Options(), engine, approver);

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(2, result.ToolCalls!.Count);

        var approved = result.ToolCalls[0];
        Assert.Equal(ToolCallExecutionState.ConfirmedResult, approved.State);
        Assert.Equal("allow once", approved.Approval);
        Assert.True(approved.SideEffects);

        var denied = result.ToolCalls[1];
        Assert.Equal(ToolCallExecutionState.KnownNotStarted, denied.State);
        Assert.Contains("Permission denied by user", denied.Approval, StringComparison.Ordinal);
        Assert.Equal(1, tool.ExecuteCount);
    }

    [Fact]
    public async Task RunEvents_SinkFailureOnNonTerminalEventDoesNotFailTheRun()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("done"));
        var sink = new ThrowingSink(HostEventKind.RunStarted);
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), null, null, events : sink);

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        // 展示故障不改写执行终态；错误随终态报告，仍尝试交付终态事件。
        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal("done", result.FinalMessage);
        Assert.Contains("sink exploded", result.Error);
        Assert.Equal(3, sink.Published); // 开始、失败的文本增量、结束。
    }

    [Fact]
    public async Task RunEvents_SinkFailureOnTerminalEventPreservesTheOutcome()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("done"));
        var sink = new ThrowingSink(HostEventKind.RunFinished);
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), null, null, events : sink);

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal("done", result.FinalMessage);
        Assert.Contains("sink exploded", result.Error);
        Assert.Equal(3, sink.Published); // 开始、文本增量、失败的结束事件。
    }

    [Fact]
    public async Task RunTurn_EventSequenceRestartsAtOneForEachTurn()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("one"));
        client.Enqueue(FakeChatClient.Text("two"));
        var sink    = new TestHostEventSink();
        var context = new ConversationContext();
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), null, null,
                                 events : sink, context : context);

        await loop.RunTurnAsync("sys", "u1", CancellationToken.None);
        await loop.RunTurnAsync("sys", "u2", CancellationToken.None);

        // 事件序号按运行重置；每回合依次发布开始、文本增量和结束事件。
        Assert.Equal([1L, 2L, 3L, 1L, 2L, 3L], sink.Events.Select(@event => @event.Sequence).ToArray());
    }

    /// <summary>
    ///     <para>按序返回预置审批选择；耗尽后拒绝。</para>
    ///     <para>Returns preset approval choices in order; denies once they are exhausted.</para>
    /// </summary>
    private sealed class ScriptedApprover(params ApprovalAction[] actions) : IApprovalProvider
    {
        private int _next;

        public Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken) =>
            Task.FromResult(_next < actions.Length ? actions[_next++] : ApprovalAction.Deny);
    }

    /// <summary>
    ///     <para>在指定事件种类上抛出异常，用于验证事件发布的容错边界。</para>
    ///     <para>Throws for a selected event kind to verify fault handling at the publishing boundary.</para>
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
    ///     <para>捕获模型 usage 审计调用；其余方法不执行操作。</para>
    ///     <para>Captures model usage audit calls; all other methods are no-ops.</para>
    /// </summary>
    private sealed class CapturingRecorder : IRunRecorder
    {
        public List<(int? InputTokens, int? OutputTokens, string? FinishReason)> UsageRecords { get; } = [];

        public Task StartAsync(string systemPrompt, string userInput, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordPreparedAsync(ToolPreparation preparation, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordPermissionAsync(ToolPreparation   preparation, PermissionDecision decision, string outcome,
                                          CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordResultAsync(
            ToolPreparation preparation, ToolResult result, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordCompactionAsync(ContextChange change, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordModelUsageAsync(
            int? inputTokens, int? outputTokens, string? finishReason, CancellationToken cancellationToken)
        {
            UsageRecords.Add((inputTokens, outputTokens, finishReason));
            return Task.CompletedTask;
        }

        public Task CompleteAsync(AgentResult       result, IReadOnlyList<ChatMessage> messages, StructuredState state,
                                  CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

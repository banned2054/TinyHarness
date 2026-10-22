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

    [Test]
    public async Task PlainText_Completes()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("Hello from ", "the model."));
        var loop = new AgentLoop(client, new ToolRegistry([]), Options());

        var result = await loop.RunAsync("sys", "hi", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(result.FinalMessage, Is.EqualTo("Hello from the model."));
        Assert.That(client.Requests, Is.EqualTo(1));
        Assert.That(client.LastRequestModel, Is.EqualTo("test-model"));
        TestAssert.ContainsAll(m => m is { Role: ChatRole.Assistant, Content: "Hello from the model." }, loop.History);
    }

    [Test]
    public async Task SingleToolCall_ExecutesAndReturnsToolResult()
    {
        var client = new FakeChatClient();
        var tool   = new FakeTool("echo_test");
        client.Enqueue(FakeChatClient.ToolCall("echo_test", "{\"value\":\"x\"}"));
        client.Enqueue(FakeChatClient.Text("done"));
        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options());

        var result = await loop.RunAsync("sys", "run it", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(tool.ExecuteCount, Is.EqualTo(1));
        Assert.That(client.Requests, Is.EqualTo(2));
        TestAssert.ContainsAll(m => m is { Role: ChatRole.Tool, Content: "echo_test ok" }, loop.History);
    }

    [Test]
    public async Task SplitArguments_AreAssembledCorrectly()
    {
        var client = new FakeChatClient();
        var tool   = new FakeTool("echo_test");
        client.Enqueue(FakeChatClient.ToolCall("echo_test", "{\"value\":\"abc\"}", "c1"));
        client.Enqueue(FakeChatClient.Text("ok"));
        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options());

        var result = await loop.RunAsync("sys", "run", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(tool.ExecuteCount, Is.EqualTo(1));

        var toolExecution = loop.History.Single(m => m.Role == ChatRole.Tool);
        Assert.That(toolExecution.Content, Is.EqualTo("echo_test ok"));
    }

    [Test]
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
            new() { Kind = ChatStreamEventKind.End, FinishReason = "tool_calls" }
        };
        client.Enqueue(calls);
        client.Enqueue(FakeChatClient.Text("all done"));
        var loop = new AgentLoop(client, new ToolRegistry([a, b]), Options());

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(a.ExecuteCount, Is.EqualTo(1));
        Assert.That(b.ExecuteCount, Is.EqualTo(1));
        Assert.That(result.ToolExecutions, Is.EqualTo(2));
    }

    [Test]
    public async Task ResultJournalFailure_StopsTheNextToolAndKeepsUnknownState()
    {
        var client   = new FakeChatClient();
        var first    = new FakeTool("tool_a");
        var second   = new FakeTool("tool_b");
        var recorder = new CapturingRecorder { FailResultRecording = true };
        client.Enqueue([
            new ChatStreamEvent
            {
                Kind                 = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 0, ToolCallId = "a",
                ToolCallFunctionName = "tool_a", ToolCallArgumentsDelta                 = "{}"
            },
            new ChatStreamEvent
            {
                Kind                 = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 1, ToolCallId = "b",
                ToolCallFunctionName = "tool_b", ToolCallArgumentsDelta                 = "{}"
            },
            new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "tool_calls" }
        ]);
        var loop = new AgentLoop(client, new ToolRegistry([first, second]), Options(), null, null,
                                 recorder);

        var result = await loop.RunAsync("sys", "run", CancellationToken.None);

        Assert.That(first.ExecuteCount, Is.EqualTo(1));
        Assert.That(second.ExecuteCount, Is.Zero);
        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(result.PersistenceWarning, Is.EqualTo("injected result journal failure"));
        Assert.That(result.ToolCalls!.Single(call => call.CallId == "a").State,
                    Is.EqualTo(ToolCallExecutionState.UnknownResult));
    }

    [Test]
    public async Task UnknownPatchChange_RecordsPartialFactAndStopsTheNextTool()
    {
        var client = new FakeChatClient();
        var first = new FakeTool("patch_tool")
        {
            ResultOverride = new ToolResult
            {
                Succeeded    = false,
                Content      = "second file write outcome is unknown",
                PatchChanges = [new PatchChange("two.cs", "modify", "@@ partial @@", PatchChange.UnknownState)]
            }
        };
        var second   = new FakeTool("tool_b");
        var recorder = new CapturingRecorder();
        client.Enqueue([
            new ChatStreamEvent
            {
                Kind                 = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 0, ToolCallId = "a",
                ToolCallFunctionName = "patch_tool", ToolCallArgumentsDelta             = "{}"
            },
            new ChatStreamEvent
            {
                Kind                 = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 1, ToolCallId = "b",
                ToolCallFunctionName = "tool_b", ToolCallArgumentsDelta                 = "{}"
            },
            new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "tool_calls" }
        ]);
        var loop = new AgentLoop(client, new ToolRegistry([first, second]), Options(), null, null, recorder);

        var result = await loop.RunAsync("sys", "run", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(first.ExecuteCount, Is.EqualTo(1));
        Assert.That(second.ExecuteCount, Is.Zero);
        Assert.That(result.ToolCalls!.Single(call => call.CallId == "a").State,
                    Is.EqualTo(ToolCallExecutionState.UnknownResult));
        Assert.That(recorder.ResultRecords.Single().PatchChanges!.Single().State, Is.EqualTo(PatchChange.UnknownState));
    }

    [Test]
    public async Task TypedToolCancellation_RecordsPartialFactsAndKeepsCancelledStatus()
    {
        var       client       = new FakeChatClient();
        var       first        = new FakeTool("patch_tool");
        var       second       = new FakeTool("tool_b");
        var       recorder     = new CapturingRecorder();
        using var cancellation = new CancellationTokenSource();
        first = new FakeTool("patch_tool")
        {
            ExecuteOverride = token =>
            {
                cancellation.Cancel();
                throw new ToolExecutionCanceledException(new ToolResult
                {
                    Succeeded    = false,
                    Content      = "partial write facts",
                    PatchChanges = [new PatchChange("one.cs", "modify", "@@ partial @@", PatchChange.ConfirmedState)]
                }, token);
            }
        };
        client.Enqueue(FakeChatClient.ToolCall("patch_tool", "{}", "a"));
        var loop = new AgentLoop(client, new ToolRegistry([first, second]), Options(), null, null, recorder);

        var result = await loop.RunAsync("sys", "run", cancellation.Token);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Cancelled));
        Assert.That(first.ExecuteCount, Is.EqualTo(1));
        Assert.That(second.ExecuteCount, Is.Zero);
        Assert.That(result.ToolCalls!.Single(call => call.CallId == "a").State,
                    Is.EqualTo(ToolCallExecutionState.UnknownResult));
        Assert.That(recorder.ResultRecords.Single().PatchChanges!.Single().State,
                    Is.EqualTo(PatchChange.ConfirmedState));
    }

    [Test]
    public async Task UnknownTool_ReturnsErrorToModelAndContinues()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("missing_tool", "{}"));
        client.Enqueue(FakeChatClient.Text("ok"));
        var loop = new AgentLoop(client, new ToolRegistry([]), Options());

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(result.ToolExecutions, Is.EqualTo(0));
        TestAssert.ContainsAll(m => m.Role == ChatRole.Tool && m.Content.Contains("Unknown tool"), loop.History);
    }

    [Test]
    public async Task StepLimit_IsReached()
    {
        var client = new FakeChatClient();
        var tool   = new FakeTool("loop");
        var loop   = new AgentLoop(client, new ToolRegistry([tool]), Options(3));

        for (var i = 0; i < 10; i++) client.Enqueue(FakeChatClient.ToolCall("loop", "{}"));

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.StepLimitReached));
        Assert.That(result.Steps, Is.EqualTo(3));
    }

    [Test]
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
            new() { Kind = ChatStreamEventKind.End, FinishReason = "tool_calls" }
        });
        var loop = new AgentLoop(client, new ToolRegistry([]), Options());

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        TestAssert.NotNull(result.Error);
    }

    [Test]
    public async Task Cancellation_ReturnsCancelled()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("never replied")); // 响应消费前已取消。
        var loop = new AgentLoop(client, new ToolRegistry([]), Options());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await loop.RunAsync("sys", "go", cts.Token);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Cancelled));
    }

    [Test]
    public async Task CancellationDuringStream_ReturnsCancelled()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("a", "b", "c"));
        var loop = new AgentLoop(client, new ToolRegistry([]), Options());

        using var cts = new CancellationTokenSource();
        client.CancelMidStream = cts.Cancel;

        var result = await loop.RunAsync("sys", "go", cts.Token);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Cancelled));
        TestAssert.Single(loop.History, m => m.Role == ChatRole.User);
        TestAssert.DoesNotContainAny(m => m.Role    == ChatRole.Assistant, loop.History);
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


    [Test]
    public async Task ModelUsage_EndCarryingUsageIsSentToTheRecorder()
    {
        var client   = new FakeChatClient();
        var recorder = new CapturingRecorder();
        client.Enqueue(TextWithUsage("hello", 120, 45, "stop"));
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), null, null,
                                 recorder);

        var result = await loop.RunAsync("sys", "hi", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(TestAssert.Single(recorder.UsageRecords), Is.EqualTo((120, 45, "stop")));
    }

    [Test]
    public async Task ModelUsage_FinishReasonAloneIsStillRecorded()
    {
        var client   = new FakeChatClient();
        var recorder = new CapturingRecorder();
        client.Enqueue([
            new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "hello" },
            new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason          = "stop" }
        ]);
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), null, null,
                                 recorder);

        var result = await loop.RunAsync("sys", "hi", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        // 任一字段非空即写审计行；缺失的 usage 保持 null。
        Assert.That(TestAssert.Single(recorder.UsageRecords), Is.EqualTo(((int?)null, (int?)null, "stop")));
    }

    [Test]
    public async Task ModelUsage_StreamWithoutUsageOrFinishReasonRecordsNothing()
    {
        var client   = new FakeChatClient();
        var recorder = new CapturingRecorder();
        client.Enqueue([
            new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "hello" },
            new ChatStreamEvent { Kind = ChatStreamEventKind.End }
        ]);
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), null, null,
                                 recorder);

        var result = await loop.RunAsync("sys", "hi", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(result.Error, Does.Contain("finish reason is missing"));
        Assert.That(recorder.UsageRecords, Is.Empty);
    }

    [Test]
    public async Task RunTurn_ContinuesSharedContextAndRejectsChangedSystemPrompt()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("one"));
        client.Enqueue(FakeChatClient.Text("two"));
        var context = new ConversationContext();
        var loop    = new AgentLoop(client, new ToolRegistry([]), Options(), null, null, context : context);

        await loop.RunTurnAsync("sys", "u1", CancellationToken.None);

        // 系统提示不一致时拒绝追加，保留上一回合的完整历史。
        await TestAssert.ThrowsAsync<InvalidOperationException>(() =>
            loop.RunTurnAsync("different", "u2", CancellationToken.None));
        Assert.That(context.Messages.Count, Is.EqualTo(3));
        Assert.That(context.Messages[2].Content, Is.EqualTo("one"));

        // 匹配的系统提示复用上下文；后续请求保留旧回合且只有一条系统消息。
        await loop.RunTurnAsync("sys", "u2", CancellationToken.None);
        Assert.That(client.Requests, Is.EqualTo(2));
        Assert.That(client.RequestLog[1].Messages.Select(m => m.Role).ToArray(),
                    Is.EqualTo([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User]));
        TestAssert.Single(client.RequestLog[1].Messages, m => m.Role == ChatRole.System);
    }

    [Test]
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
            await TestAssert.ThrowsAsync<InvalidOperationException>(() =>
                loop.RunTurnAsync("sys", "u2", CancellationToken.None));

        Assert.That(rejected.Message, Does.Contain("call_1"));
        Assert.That(client.Requests, Is.EqualTo(0));
        // 被拒绝的回合不追加历史。
        Assert.That(context.Messages.Count, Is.EqualTo(3));
    }

    [Test]
    public void LastCompleteBoundary_ClassifiesCompleteRewindableAndDamagedHistories()
    {
        IReadOnlyList<ChatMessage> Complete() =>
        [
            ChatMessage.System("sys"), ChatMessage.User("u"),
            ChatMessage.Assistant(string.Empty, [new ChatToolCall("call_1", "t", "{}")]),
            ChatMessage.Tool("t", "call_1", "ok"),
            ChatMessage.Assistant("done")
        ];

        Assert.That(ChatHistoryInvariants.LastCompleteBoundary(Complete()), Is.EqualTo(5));

        // 尾部工具组即使已有部分结果，也须整体回退到发起调用前，不能拆分原子组。
        var rewound = new List<ChatMessage>(Complete())
        {
            ChatMessage.User("u2"),
            ChatMessage.Assistant(string.Empty,
                                  [new ChatToolCall("call_2", "t", "{}"), new ChatToolCall("call_3", "t", "{}")]),
            ChatMessage.Tool("t", "call_2", "partial")
        };
        Assert.That(ChatHistoryInvariants.LastCompleteBoundary(rewound), Is.EqualTo(6));

        // 非尾部的不完整工具组属于损坏，不能按尾部回退。
        var damaged = new List<ChatMessage>
        {
            ChatMessage.System("sys"),
            ChatMessage.Assistant(string.Empty, [new ChatToolCall("call_1", "t", "{}")]),
            ChatMessage.User("after the gap")
        };
        Assert.That(ChatHistoryInvariants.LastCompleteBoundary(damaged), Is.EqualTo(-1));
    }

    [Test]
    public async Task RunTurn_RecordsAPrepareFailureAsKnownNotStarted()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("missing", "{}"));
        client.Enqueue(FakeChatClient.Text("done"));
        var loop = new AgentLoop(client, new ToolRegistry([]), Options());

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        var record = TestAssert.Single(result.ToolCalls!);
        Assert.That(record.ToolName, Is.EqualTo("missing"));
        Assert.That(record.Capability, Is.Null);
        Assert.That(record.SideEffects, Is.False);
        Assert.That(record.State, Is.EqualTo(ToolCallExecutionState.KnownNotStarted));
        Assert.That(record.Approval, Is.Null);
        TestAssert.Contains("Unknown tool", record.PreparationError, StringComparison.Ordinal);
    }

    [Test]
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

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(result.ToolCalls!.Count, Is.EqualTo(2));

        var approved = result.ToolCalls[0];
        Assert.That(approved.State, Is.EqualTo(ToolCallExecutionState.ConfirmedResult));
        Assert.That(approved.Approval, Is.EqualTo("allow once"));
        Assert.That(approved.SideEffects, Is.True);

        var denied = result.ToolCalls[1];
        Assert.That(denied.State, Is.EqualTo(ToolCallExecutionState.KnownNotStarted));
        TestAssert.Contains("Permission denied by user", denied.Approval, StringComparison.Ordinal);
        Assert.That(tool.ExecuteCount, Is.EqualTo(1));
    }

    [Test]
    public async Task RunEvents_SinkFailureOnNonTerminalEventDoesNotFailTheRun()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("done"));
        var sink = new ThrowingSink(HostEventKind.RunStarted);
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), null, null, events : sink);

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        // 展示故障不改写执行终态；错误随终态报告，仍尝试交付终态事件。
        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(result.FinalMessage, Is.EqualTo("done"));
        Assert.That(result.Error, Does.Contain("sink exploded"));
        Assert.That(sink.Published, Is.EqualTo(6)); // 运行、请求、正文、usage 和两级终态。
    }

    [Test]
    public async Task RunEvents_SinkFailureOnTerminalEventPreservesTheOutcome()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("done"));
        var sink = new ThrowingSink(HostEventKind.RunFinished);
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), null, null, events : sink);

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(result.FinalMessage, Is.EqualTo("done"));
        Assert.That(result.Error, Does.Contain("sink exploded"));
        Assert.That(sink.Published, Is.EqualTo(6)); // 运行、请求、正文、usage 和两级终态。
    }

    [Test]
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

        // 序号按运行重置，请求状态和 usage 也参与有序交付。
        Assert.That(sink.Events.Select(@event => @event.Sequence).ToArray(),
                    Is.EqualTo([1L, 2L, 3L, 4L, 5L, 6L, 1L, 2L, 3L, 4L, 5L, 6L]));
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

        public List<ToolResult> ResultRecords { get; } = [];

        public bool FailResultRecording { get; init; }

        public Task StartAsync(string systemPrompt, string userInput, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordPreparedAsync(ToolPreparation preparation, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordPermissionAsync(ToolPreparation   preparation, PermissionDecision decision, string outcome,
                                          CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordResultAsync(ToolPreparation   preparation, ToolResult result,
                                      CancellationToken cancellationToken)
        {
            if (FailResultRecording) throw new IOException("injected result journal failure");
            ResultRecords.Add(result);
            return Task.CompletedTask;
        }

        public Task RecordCompactionAsync(ContextChange change, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordModelUsageAsync(
            int? inputTokens, int? outputTokens, string? finishReason, CancellationToken cancellationToken)
        {
            UsageRecords.Add((inputTokens, outputTokens, finishReason));
            return Task.CompletedTask;
        }

        public Task CompleteAsync(AgentResult       result, IReadOnlyList<ChatMessage> messages,
                                  CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

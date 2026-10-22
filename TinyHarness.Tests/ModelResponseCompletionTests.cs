using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public sealed class ModelResponseCompletionTests
{
    private static AgentOptions Options() => new()
    {
        Model = "offline-test", MaxAgentSteps = 8, DefaultToolTimeoutSeconds = 30
    };

    /// <summary>
    ///     <para>空白、usage 或推理均不能单独构成最终回答；失败仍保留已报告 usage。</para>
    ///     <para>Whitespace, usage and reasoning alone cannot complete a turn; failed responses retain reported usage.</para>
    /// </summary>
    [TestCase("", false, false, null)]
    [TestCase("", false, false, "stop")]
    [TestCase(" \n\t", false, false, "stop")]
    [TestCase("", true, false, "stop")]
    [TestCase("", false, true, "stop")]
    [TestCase("", true, true, null)]
    public async Task UnusableAnswerFailsWithoutAppendingAssistant(
        string text, bool usage, bool reasoning, string? finishReason)
    {
        var model = new FakeChatClient();
        var events = new List<ChatStreamEvent>
            { new() { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = text } };
        if (reasoning)
            events.Add(new ChatStreamEvent
                           { Kind = ChatStreamEventKind.ReasoningDelta, ReasoningDelta = "private reasoning" });
        events.Add(new ChatStreamEvent
        {
            Kind        = ChatStreamEventKind.End, FinishReason = finishReason,
            InputTokens = usage ? 12 : null, OutputTokens       = usage ? 7 : null
        });
        model.Enqueue(events);
        var sink = new TestHostEventSink();
        var loop = new AgentLoop(model, new ToolRegistry([]), Options(), null, null, events : sink);

        var result = await loop.RunAsync("sys", "go", default);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(result.ToolExecutions, Is.Zero);
        Assert.That(result.InputTokens, Is.EqualTo(usage ? (long?)12 : null));
        Assert.That(result.OutputTokens, Is.EqualTo(usage ? (long?)7 : null));
        Assert.That(model.Requests, Is.EqualTo(1));
        Assert.That(loop.History.Select(message => message.Role), Is.EqualTo([ChatRole.System, ChatRole.User]));
        Assert.That(sink.Events.Count(e => e.Kind == HostEventKind.ModelRequestStarted), Is.EqualTo(1));
        Assert.That(sink.Events.Count(e => e.Kind == HostEventKind.ModelRequestFinished), Is.EqualTo(1));
        Assert.That(((ModelRequestProgress)
                        sink.Events.Single(e => e.Kind == HostEventKind.ModelRequestFinished).Payload!).Outcome,
                    Is.EqualTo("failed"));
    }

    /// <summary>
    ///     <para>截断、未知和矛盾的终止信息均在工具准备前拒绝，即使参数 JSON 完整。</para>
    ///     <para>Truncated, unknown and inconsistent termination is rejected before tool preparation, even with complete JSON.</para>
    /// </summary>
    [TestCase(null, true, true)]
    [TestCase("tool_calls", true, false)]
    [TestCase("stop", true, true)]
    [TestCase("length", true, true)]
    [TestCase("content_filter", true, true)]
    [TestCase("incomplete", true, true)]
    [TestCase("max_tokens", true, true)]
    [TestCase("future_reason", true, true)]
    [TestCase("tool_calls", false, true)]
    [TestCase(null, false, true)]
    public async Task InvalidTerminationNeverStartsTools(string? reason, bool tools, bool end)
    {
        var model = new FakeChatClient();
        var events = (tools ? FakeChatClient.ToolCall("probe", "{}") : FakeChatClient.Text("partial"))
                    .Where(e => e.Kind != ChatStreamEventKind.End).ToList();
        if (end) events.Add(new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = reason });
        model.Enqueue(events);
        var tool = new FakeTool("probe");
        var sink = new TestHostEventSink();
        var loop = new AgentLoop(model, new ToolRegistry([tool]), Options(), null, null, events : sink);

        var result = await loop.RunAsync("sys", "go", default);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(tool.ExecuteCount, Is.Zero);
        Assert.That(result.ToolCalls, Is.Empty);
        Assert.That(sink.Events.Any(e => e.Kind == HostEventKind.ToolPrepared), Is.False);
        var finished =
            (ModelRequestProgress)sink.Events.Single(e => e.Kind == HostEventKind.ModelRequestFinished).Payload!;
        Assert.That(finished.PartialText, Is.EqualTo(!tools));
    }

    /// <summary>
    ///     <para>失败回合与已确认工具保存在唯一日志；恢复后新回合成功，不重放工具。</para>
    ///     <para>
    ///         A failed turn and confirmed tool persist in the single journal; a resumed turn succeeds without replaying
    ///         tools.
    ///     </para>
    /// </summary>
    [Test]
    public async Task FailureAfterConfirmedToolSurvivesRestoreAndContinues()
    {
        using var directory = new TestTempDir();
        var identity =
            new SessionModelIdentity("offline-test", "https://example.invalid/v1", ChatApiKind.ChatCompletions);
        var model = new FakeChatClient();
        model.Enqueue(FakeChatClient.ToolCall("probe", "{}"));
        model.Enqueue(FakeChatClient.ReasonedText("private reasoning", "rs_1", "opaque"));
        var    tool = new FakeTool("probe");
        string sessionId;
        using (var host = new ChatSessionHost(directory.Root, identity, Options(), model, new ToolRegistry([tool]),
                                              "sys",
                                              sessionWriterFactory : id => new SessionStoreWriter(directory.Root, id)))
        {
            var failed = (await host.SubmitTurnAsync("go", default)).Result!;
            Assert.That(failed.Status, Is.EqualTo(AgentStatus.Failed));
            Assert.That(failed.ToolCalls!.Single().State, Is.EqualTo(ToolCallExecutionState.ConfirmedResult));
            Assert.That(host.InspectionPending, Is.False);
            Assert.That(host.PersistenceWarning, Is.Null);
            sessionId = host.Session.Id;
        }

        var snapshot = (await SessionStoreReader.ReadAsync(directory.Root, sessionId, default))!;
        var restored = SessionResume.Restore(snapshot, directory.Root, identity, "sys");
        var next     = new FakeChatClient();
        next.Enqueue(FakeChatClient.Text("continued"));
        using (var writer = await SessionStoreWriter.OpenExistingAsync(directory.Root, sessionId, null, default))
        using (var host = new ChatSessionHost(restored.Session, restored.Context, Options(), next,
                                              new ToolRegistry([tool]), "sys", sessionWriter : writer))
        {
            var completed = (await host.SubmitTurnAsync("continue", default)).Result!;
            Assert.That(completed.Status, Is.EqualTo(AgentStatus.Completed));
            Assert.That(tool.ExecuteCount, Is.EqualTo(1));
            Assert.That(completed.ToolExecutions, Is.Zero);
            Assert.That(next.RequestLog.Single().Messages.Count(m => m.Role == ChatRole.Tool), Is.EqualTo(1));
            Assert.That(next.RequestLog.Single().Messages.Any(m => m.Reasoning is { Count: > 0 }), Is.False);
            Assert.That(host.PersistenceWarning, Is.Null);
        }

        var final = (await SessionStoreReader.ReadAsync(directory.Root, sessionId, default))!;
        Assert.That(final.Turns.SelectMany(turn => turn.Runs).Select(run => run.Status),
                    Is.EqualTo(["Failed", "Completed"]));
    }

    /// <summary>
    ///     <para>SDK 自身的取消异常是失败，调用方取消才是 Cancelled；两者均关闭请求状态。</para>
    ///     <para>An SDK cancellation is a failure while caller cancellation is Cancelled; both close request progress.</para>
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task TimeoutAndCallerCancellationRemainDistinct(bool callerCancelled)
    {
        using var cancellation = new CancellationTokenSource();
        var model = new FakeChatClient { ThrownError = new OperationCanceledException("synthetic NetworkTimeout") };
        var sink = new CancellingSink(callerCancelled ? cancellation.Cancel : null);
        var loop = new AgentLoop(model, new ToolRegistry([]), Options(), null, null, events : sink);
        var result = await loop.RunAsync("sys", "go", cancellation.Token);
        Assert.That(result.Status, Is.EqualTo(callerCancelled ? AgentStatus.Cancelled : AgentStatus.Failed));
        Assert.That(sink.Events.Events.Last().Kind, Is.EqualTo(HostEventKind.RunFinished));
        var finished =
            (ModelRequestProgress)sink.Events.Events.Single(e => e.Kind == HostEventKind.ModelRequestFinished).Payload!;
        Assert.That(finished.Outcome, Is.EqualTo(callerCancelled ? "cancelled" : "failed"));
    }

    private sealed class CancellingSink(Action? cancel) : IHostEventSink
    {
        internal TestHostEventSink Events { get; } = new();

        public ValueTask PublishAsync(HostEvent value, CancellationToken cancellationToken)
        {
            if (value.Kind == HostEventKind.ModelRequestStarted) cancel?.Invoke();
            return Events.PublishAsync(value, cancellationToken);
        }
    }
}

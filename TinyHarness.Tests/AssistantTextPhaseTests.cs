using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public sealed class AssistantTextPhaseTests
{
    [Test]
    public async Task OneResponseSeparatesCommentaryAndAnswersInHistoryAndResult()
    {
        using var directory = new TestTempDir();
        var       model     = new FakeChatClient();
        model.Enqueue(TextItems(new AssistantTextItem("checking", "msg_1", "commentary"),
                                new AssistantTextItem("first finding; ", "msg_2", "partial_answer"),
                                new AssistantTextItem("conclusion", "msg_3", "final_answer")));
        var sink = new TestHostEventSink();
        using var host = new ChatSessionHost(directory.Root,
                                             new SessionModelIdentity("test", "https://example.invalid/v1",
                                                                      ChatApiKind.Responses),
                                             Options(), model, new ToolRegistry([]), "sys", events : sink,
                                             sessionWriterFactory : id => new SessionStoreWriter(directory.Root, id));

        var result = (await host.SubmitTurnAsync("question", default)).Result!;

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(result.FinalMessage, Is.EqualTo("first finding; conclusion"));
        Assert.That(host.PersistenceWarning, Is.Null);
        var history =
            await new SessionHistoryReader().ReadPageAsync(directory.Root, host.Session.Id, 200, null, default);
        var assistant = history.Items.Where(item => item.Kind != SessionHistoryKind.User).ToArray();
        Assert.That(assistant.Select(item => item.Kind), Is.EqualTo(["process", "final", "final"]));
        Assert.That(assistant.Select(item => item.Text), Is.EqualTo(["checking", "first finding; ", "conclusion"]));
        Assert.That(string.Concat(sink.Events.Where(item => item.Kind == HostEventKind.AssistantTextDelta)
                                      .Select(item => item.Payload)), Is.EqualTo("checkingfirst finding; conclusion"));
    }

    [TestCase("commentary", false)]
    [TestCase("final_answer", true)]
    [TestCase("partial_answer", true)]
    [TestCase("future_phase", false)]
    [TestCase(null, false)]
    public async Task ExplicitPhaseOverridesToolCallInference(string? phase, bool isAnswer)
    {
        using var directory = new TestTempDir();
        var       model     = new FakeChatClient();
        var       events    = TextItems(new AssistantTextItem("first", "msg_1", phase)).ToList();
        events.RemoveAt(events.Count - 1);
        events.AddRange(FakeChatClient.ToolCall("probe", "{}"));
        model.Enqueue(events);
        model.Enqueue(FakeChatClient.Text("done"));
        using var host = new ChatSessionHost(directory.Root,
                                             new SessionModelIdentity("test", "https://example.invalid/v1",
                                                                      ChatApiKind.Responses),
                                             Options(), model, new ToolRegistry([new FakeTool("probe")]), "sys",
                                             sessionWriterFactory : id => new SessionStoreWriter(directory.Root, id));

        var result = (await host.SubmitTurnAsync("question", default)).Result!;

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(result.ToolExecutions, Is.EqualTo(1));
        var history =
            await new SessionHistoryReader().ReadPageAsync(directory.Root, host.Session.Id, 200, null, default);
        var first = history.Items.Single(item => item.Text == "first");
        Assert.That(first.Kind, Is.EqualTo(isAnswer ? SessionHistoryKind.Final : SessionHistoryKind.Process));
    }

    [TestCase("commentary", "")]
    [TestCase("future_phase", "")]
    [TestCase(null, "text")]
    [TestCase("partial_answer", "text")]
    [TestCase("final_answer", "text")]
    public async Task MissingPhaseFallsBackButKnownProcessNeverBecomesFinal(string? phase, string answer)
    {
        var model = new FakeChatClient();
        model.Enqueue(TextItems(new AssistantTextItem("text", "msg_1", phase)));
        var loop = new AgentLoop(model, new ToolRegistry([]), Options());

        var result = await loop.RunAsync("sys", "question", default);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(result.FinalMessage, Is.EqualTo(answer));
        Assert.That(model.Requests, Is.EqualTo(1));
    }

    [Test]
    public void CompletedMessageKeepsBoundariesAndUsesItsFinalTextAndPhase()
    {
        var accumulator = new StreamAccumulator();
        foreach (var item in TextItems(new AssistantTextItem("first", "msg_1", "commentary"),
                                       new AssistantTextItem("second", "msg_2", "final_answer")))
            accumulator.Append(item);
        accumulator.Append(new ChatStreamEvent
        {
            Kind          = ChatStreamEventKind.TextMessageCompleted,
            TextMessageId = "msg_2",
            TextPhase     = "partial_answer",
            ContentDelta  = "corrected second"
        });
        accumulator.Finish();

        Assert.That(accumulator.Content, Is.EqualTo("firstcorrected second"));
        Assert.That(accumulator.TextItems, Is.EqualTo(new[]
        {
            new AssistantTextItem("first", "msg_1", "commentary"),
            new AssistantTextItem("corrected second", "msg_2", "partial_answer")
        }));
    }

    [Test]
    public void CompletedMessageWithoutPhaseUsesLegacyInferenceRatherThanStaleStartedPhase()
    {
        var accumulator = new StreamAccumulator();
        accumulator.Append(new ChatStreamEvent
        {
            Kind          = ChatStreamEventKind.TextMessageStarted,
            TextMessageId = "msg_1",
            TextPhase     = "commentary"
        });
        accumulator.Append(new ChatStreamEvent
        {
            Kind          = ChatStreamEventKind.TextMessageCompleted,
            TextMessageId = "msg_1",
            ContentDelta  = "answer"
        });
        accumulator.Finish();

        Assert.That(accumulator.TextItems!.Single(), Is.EqualTo(new AssistantTextItem("answer", "msg_1")));
        Assert.That(accumulator.TextItems!.Single().IsAnswer(false), Is.True);
    }

    [Test]
    public async Task CompletedMessageDoesNotChargeOrPublishTextTwice()
    {
        var plainModel = new FakeChatClient();
        plainModel.Enqueue(FakeChatClient.Text("answer"));
        var plain =
            await new AgentLoop(plainModel, new ToolRegistry([]), Options()).RunAsync("sys", "question", default);
        var phaseModel = new FakeChatClient();
        phaseModel.Enqueue(TextItems(new AssistantTextItem("answer", "msg_1", "final_answer")));
        var phase =
            await new AgentLoop(phaseModel, new ToolRegistry([]), Options()).RunAsync("sys", "question", default);

        Assert.That(phase.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(phase.Budget!.AccountedTokens, Is.EqualTo(plain.Budget!.AccountedTokens));
    }

    private static AgentOptions Options() =>
        new() { Model = "test", MaxAgentSteps = 8, DefaultToolTimeoutSeconds = 30 };

    private static IReadOnlyList<ChatStreamEvent> TextItems(params AssistantTextItem[] items)
    {
        var events = new List<ChatStreamEvent>();
        foreach (var item in items)
        {
            events.Add(new ChatStreamEvent
            {
                Kind          = ChatStreamEventKind.TextMessageStarted,
                TextMessageId = item.MessageId,
                TextPhase     = item.Phase
            });
            events.Add(new ChatStreamEvent
            {
                Kind          = ChatStreamEventKind.ContentDelta,
                TextMessageId = item.MessageId,
                ContentDelta  = item.Text
            });
            events.Add(new ChatStreamEvent
            {
                Kind          = ChatStreamEventKind.TextMessageCompleted,
                TextMessageId = item.MessageId,
                TextPhase     = item.Phase,
                ContentDelta  = item.Text
            });
        }

        events.Add(new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" });
        return events;
    }
}

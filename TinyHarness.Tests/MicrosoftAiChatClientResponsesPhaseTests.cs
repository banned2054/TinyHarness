using System.Text;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions;

namespace TinyHarness.Tests;

/// <summary>
///     <para>通过真实 OpenAI/M.E.AI SDK 与本地 SSE 验证 Responses 消息分界、阶段和无状态回传。</para>
///     <para>
///         Verifies Responses message boundaries, phases, and stateless replay through the real OpenAI/M.E.AI SDK
///         and local SSE.
///     </para>
/// </summary>
public class MicrosoftAiChatClientResponsesPhaseTests
{
    private const string Model = "phase-test-model";

    [Test]
    public async Task CommentaryAndFinalInOneResponseRetainSeparateBoundaries()
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueRaw(ResponsesSse([
            new TextFixture("msg_commentary", "commentary", "commentary", "Checking the file."),
            new TextFixture("msg_final", "final_answer", "final_answer", "The answer is 42.")
        ]));
        using var client = NewClient(server);

        var events = await CollectAsync(client, ChatMessage.User("inspect"));

        Assert.That(events.First().Kind, Is.EqualTo(ChatStreamEventKind.ResponseStarted));
        Assert.That(events.Last().Kind, Is.EqualTo(ChatStreamEventKind.End));
        Assert.That(events.Last().FinishReason, Is.Not.Null.And.Not.Empty);
        Assert.That(events.Where(e => e.Kind == ChatStreamEventKind.TextMessageStarted)
                          .Select(e => (e.TextMessageId, e.TextPhase, e.ContentDelta)), Is.EqualTo(new[]
        {
            ("msg_commentary", "commentary", ""), ("msg_final", "final_answer", "")
        }));
        Assert.That(events.Where(e => e.Kind == ChatStreamEventKind.TextMessageCompleted)
                          .Select(e => (e.TextMessageId, e.TextPhase, e.ContentDelta)), Is.EqualTo(new[]
        {
            ("msg_commentary", "commentary", "Checking the file."),
            ("msg_final", "final_answer", "The answer is 42.")
        }));

        var textEvents = events.Where(e => e.Kind is ChatStreamEventKind.TextMessageStarted or
                                                     ChatStreamEventKind.ContentDelta or
                                                     ChatStreamEventKind.TextMessageCompleted).ToArray();
        Assert.That(textEvents.Select(e => e.Kind), Is.EqualTo(new[]
        {
            ChatStreamEventKind.TextMessageStarted, ChatStreamEventKind.ContentDelta,
            ChatStreamEventKind.ContentDelta, ChatStreamEventKind.TextMessageCompleted,
            ChatStreamEventKind.TextMessageStarted, ChatStreamEventKind.ContentDelta,
            ChatStreamEventKind.ContentDelta, ChatStreamEventKind.TextMessageCompleted
        }));
        Assert.That(textEvents.Take(4).Select(e => e.TextMessageId), Is.All.EqualTo("msg_commentary"));
        Assert.That(textEvents.Skip(4).Select(e => e.TextMessageId), Is.All.EqualTo("msg_final"));
        Assert.That(textEvents.Take(4).Select(e => e.TextPhase), Is.All.EqualTo("commentary"));
        Assert.That(textEvents.Skip(4).Select(e => e.TextPhase), Is.All.EqualTo("final_answer"));
        Assert.That(string.Concat(events.Where(e => e.Kind == ChatStreamEventKind.ContentDelta)
                                        .Select(e => e.ContentDelta)),
                    Is.EqualTo("Checking the file.The answer is 42."));
    }

    [Test]
    public async Task InterleavedMessagesUseDeltaItemIdInsteadOfLastAddedMessageId()
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueRaw(ResponsesSse([
            new TextFixture("msg_first", "commentary", "commentary", "first"),
            new TextFixture("msg_second", "final_answer", "final_answer", "second")
        ], true));
        using var client = NewClient(server);

        var events = await CollectAsync(client, ChatMessage.User("go"));

        var deltas = events.Where(e => e.Kind == ChatStreamEventKind.ContentDelta).ToArray();
        Assert.That(deltas.Select(e => (e.TextMessageId, e.TextPhase)), Is.EqualTo(new[]
        {
            ("msg_first", "commentary"), ("msg_first", "commentary"),
            ("msg_second", "final_answer"), ("msg_second", "final_answer")
        }));
        Assert.That(string.Concat(deltas.Take(2).Select(e => e.ContentDelta)), Is.EqualTo("first"));
        Assert.That(string.Concat(deltas.Skip(2).Select(e => e.ContentDelta)), Is.EqualTo("second"));
    }

    [TestCase(null, null)]
    [TestCase(null, "final_answer")]
    [TestCase("commentary", "final_answer")]
    [TestCase("commentary", null)]
    [TestCase("future_phase", "another_phase")]
    public async Task DoneCarriesAuthoritativeTextAndPhaseWithoutRepeatingDeltas(
        string? startedPhase, string? completedPhase)
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueRaw(ResponsesSse([
            new TextFixture("msg_corrected", startedPhase, completedPhase, "authoritative text", "draft")
        ]));
        using var client = NewClient(server);

        var events = await CollectAsync(client, ChatMessage.User("go"));

        var started = events.Single(e => e.Kind == ChatStreamEventKind.TextMessageStarted);
        Assert.That(started.TextMessageId, Is.EqualTo("msg_corrected"));
        Assert.That(started.TextPhase, Is.EqualTo(startedPhase));
        Assert.That(started.ContentDelta, Is.Empty);
        var deltas = events.Where(e => e.Kind == ChatStreamEventKind.ContentDelta).ToArray();
        Assert.That(deltas.Select(e => e.TextMessageId), Is.All.EqualTo("msg_corrected"));
        Assert.That(deltas.Select(e => e.TextPhase), Is.All.EqualTo(startedPhase));
        Assert.That(string.Concat(deltas.Select(e => e.ContentDelta)), Is.EqualTo("draft"));
        var completed = events.Single(e => e.Kind == ChatStreamEventKind.TextMessageCompleted);
        Assert.That(completed.TextMessageId, Is.EqualTo("msg_corrected"));
        Assert.That(completed.TextPhase, Is.EqualTo(completedPhase));
        Assert.That(completed.ContentDelta, Is.EqualTo("authoritative text"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MissingOrNullPhaseRemainsNull(bool explicitNullPhase)
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueRaw(ResponsesSse([new TextFixture("msg_legacy", null, null, "legacy text")],
                                       explicitNullPhase : explicitNullPhase));
        using var client = NewClient(server);

        var events = await CollectAsync(client, ChatMessage.User("go"));

        var textEvents = events.Where(e => e.Kind is ChatStreamEventKind.TextMessageStarted or
                                                     ChatStreamEventKind.ContentDelta or
                                                     ChatStreamEventKind.TextMessageCompleted).ToArray();
        Assert.That(textEvents.Length, Is.EqualTo(4));
        Assert.That(textEvents.Select(e => e.TextMessageId), Is.All.EqualTo("msg_legacy"));
        Assert.That(textEvents.Select(e => e.TextPhase), Is.All.Null);
    }

    [Test]
    public async Task DeltaWithoutAddedStillUsesWireItemIdAndDoesNotInventPhase()
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueRaw(ResponsesSse([new TextFixture("msg_without_added", null, "final_answer", "answer")],
                                       omitAdded : true));
        using var client = NewClient(server);

        var events = await CollectAsync(client, ChatMessage.User("go"));

        Assert.That(events.Any(e => e.Kind    == ChatStreamEventKind.TextMessageStarted), Is.False);
        var deltas = events.Where(e => e.Kind == ChatStreamEventKind.ContentDelta).ToArray();
        Assert.That(deltas.Select(e => e.TextMessageId), Is.All.EqualTo("msg_without_added"));
        Assert.That(deltas.Select(e => e.TextPhase), Is.All.Null);
        Assert.That(events.Single(e => e.Kind == ChatStreamEventKind.TextMessageCompleted).TextPhase,
                    Is.EqualTo("final_answer"));
    }

    [Test]
    public async Task TextItemsReplaySeparatelyWithReasoningAndToolCalls()
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueRaw(ResponsesSse([
            new TextFixture("msg_commentary", "commentary", "commentary", "Checking."),
            new TextFixture("msg_final", null, "final_answer", "Done.")
        ]));
        server.EnqueueRaw(ResponsesSse([new TextFixture("msg_next", null, null, "next")]));
        using var client = NewClient(server);
        var       events = await CollectAsync(client, ChatMessage.User("go"));
        var items = events.Where(e => e.Kind == ChatStreamEventKind.TextMessageCompleted)
                          .Select(e => new AssistantTextItem(e.ContentDelta, e.TextMessageId, e.TextPhase))
                          .Concat([
                               new AssistantTextItem("legacy"), new AssistantTextItem("", "msg_empty", "future_phase")
                           ])
                          .ToArray();
        var assistant = ChatMessage.Assistant(string.Concat(items.Select(item => item.Text)),
                                              [new ChatToolCall("call_1", "read_file", """{"path":"a"}""")],
                                              [new ReasoningContent("thought", "opaque", "rs_1")]) with
        {
            TextItems = items
        };

        await CollectAsync(client, ChatMessage.User("go"), assistant,
                           ChatMessage.Tool("read_file", "call_1", "file contents"));

        var request = JsonNode.Parse(server.Requests[1].Body)!.AsObject();
        Assert.That(request["store"]!.GetValue<bool>(), Is.False);
        Assert.That(request.ContainsKey("previous_response_id"), Is.False);
        var input = request["input"]!.AsArray();
        Assert.That(input.Select(item => item!["type"]!.GetValue<string>()), Is.EqualTo(new[]
        {
            "message", "reasoning", "message", "message", "message", "message",
            "function_call", "function_call_output"
        }));
        Assert.That(input[1]!["id"]!.GetValue<string>(), Is.EqualTo("rs_1"));
        Assert.That(input[1]!["encrypted_content"]!.GetValue<string>(), Is.EqualTo("opaque"));
        var messages = input.Where(item => item?["role"]?.GetValue<string>() == "assistant").ToArray();
        Assert.That(messages.Length, Is.EqualTo(items.Length));
        for (var i = 0; i < items.Length; i++)
        {
            var wire = messages[i]!.AsObject();
            Assert.That(wire["id"]?.GetValue<string>(), Is.EqualTo(items[i].MessageId));
            Assert.That(wire["phase"]?.GetValue<string>(), Is.EqualTo(items[i].Phase));
            Assert.That(wire.ContainsKey("phase"), Is.EqualTo(items[i].Phase is not null));
            Assert.That(wire["content"]!.AsArray().Count, Is.EqualTo(1));
            Assert.That(wire["content"]![0]!["text"]!.GetValue<string>(), Is.EqualTo(items[i].Text));
        }

        Assert.That(input[^2]!["call_id"]!.GetValue<string>(), Is.EqualTo("call_1"));
        Assert.That(input[^2]!["name"]!.GetValue<string>(), Is.EqualTo("read_file"));
        Assert.That(JsonNode.Parse(input[^2]!["arguments"]!.GetValue<string>())!["path"]!.GetValue<string>(),
                    Is.EqualTo("a"));
        Assert.That(input[^1]!["call_id"]!.GetValue<string>(), Is.EqualTo("call_1"));
        Assert.That(input[^1]!["output"]!.GetValue<string>(), Is.EqualTo("file contents"));
    }

    [Test]
    public async Task ChatCompletionsKeepsLegacyTextAndDoesNotSendPhaseOrMessageIds()
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueRaw("""
                          data: {"id":"chatcmpl_1","object":"chat.completion.chunk","created":1,"model":"mock","choices":[{"index":0,"delta":{"role":"assistant","content":"answer"},"finish_reason":null}]}

                          data: {"id":"chatcmpl_1","object":"chat.completion.chunk","created":1,"model":"mock","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                          data: [DONE]


                          """);
        using var client = (MicrosoftAiChatClient)ModelClientFactory.Create(Model, server.BaseUrl, "test-key");
        var assistant = ChatMessage.Assistant("Checking.Done.") with
        {
            TextItems =
            [
                new AssistantTextItem("Checking.", "msg_commentary", "commentary"),
                new AssistantTextItem("Done.", "msg_final", "final_answer")
            ]
        };

        var events = await CollectAsync(client, ChatMessage.User("go"), assistant);

        Assert.That(events.Select(e => e.Kind), Is.EqualTo(new[]
        {
            ChatStreamEventKind.ResponseStarted, ChatStreamEventKind.ContentDelta, ChatStreamEventKind.End
        }));
        var delta = events.Single(e => e.Kind == ChatStreamEventKind.ContentDelta);
        Assert.That(delta.ContentDelta, Is.EqualTo("answer"));
        Assert.That(delta.TextMessageId, Is.Null);
        Assert.That(delta.TextPhase, Is.Null);
        Assert.That(server.Requests.Single().Path, Is.EqualTo("/v1/chat/completions"));
        var input = JsonNode.Parse(server.Requests.Single().Body)!["messages"]!.AsArray();
        Assert.That(input.Count, Is.EqualTo(2));
        var wire = input[1]!.AsObject();
        Assert.That(wire["content"]!.GetValue<string>(), Is.EqualTo("Checking.Done."));
        Assert.That(wire.ContainsKey("phase"), Is.False);
        Assert.That(wire.ContainsKey("id"), Is.False);
    }

    private static MicrosoftAiChatClient NewClient(MockSseServer server) =>
        (MicrosoftAiChatClient)ModelClientFactory.Create(Model, server.BaseUrl, "test-key", ChatApiKind.Responses);

    private static async Task<List<ChatStreamEvent>> CollectAsync(
        IChatCompletionClient client, params ChatMessage[] messages)
    {
        var events  = new List<ChatStreamEvent>();
        var request = new ChatCompletionRequest { Model = Model, Messages = messages };
        await foreach (var e in client.CompleteAsync(request, CancellationToken.None)) events.Add(e);
        return events;
    }

    /// <summary>
    ///     <para>构造带消息及内容分界的 SSE；可交错 added 或省略它，验证 SDK 的真实标识映射。</para>
    ///     <para>
    ///         Builds SSE with message and content boundaries; added events can interleave or be omitted
    ///         to test real SDK identity mapping.
    ///     </para>
    /// </summary>
    private static string ResponsesSse(
        IReadOnlyList<TextFixture> messages, bool interleave = false, bool explicitNullPhase = false,
        bool                       omitAdded = false)
    {
        var stream   = new StringBuilder();
        var sequence = 0;
        var response = new JsonObject
        {
            ["id"]     = "resp_phase", ["object"] = "response", ["created_at"] = 1,
            ["status"] = "in_progress", ["model"] = Model, ["output"]          = new JsonArray(), ["store"] = false
        };
        Append("response.created", new JsonObject { ["response"] = response.DeepClone() });
        if (interleave && !omitAdded)
            for (var i = 0; i < messages.Count; i++)
                Start(messages[i], i);

        for (var i = 0; i < messages.Count; i++)
        {
            var fixture = messages[i];
            if (!interleave && !omitAdded) Start(fixture, i);
            Append("response.content_part.added", new JsonObject
            {
                ["item_id"] = fixture.Id, ["output_index"] = i, ["content_index"] = 0, ["part"] = Part("")
            });
            var text = fixture.StreamedText ?? fixture.Text;
            foreach (var delta in new[] { text[..(text.Length / 2)], text[(text.Length / 2)..] })
                Append("response.output_text.delta", new JsonObject
                {
                    ["item_id"] = fixture.Id, ["output_index"] = i, ["content_index"] = 0,
                    ["delta"]   = delta, ["logprobs"]          = new JsonArray()
                });
            Append("response.output_text.done", new JsonObject
            {
                ["item_id"] = fixture.Id, ["output_index"] = i, ["content_index"] = 0,
                ["text"]    = fixture.Text, ["logprobs"]   = new JsonArray()
            });
            Append("response.content_part.done", new JsonObject
            {
                ["item_id"] = fixture.Id, ["output_index"] = i, ["content_index"] = 0, ["part"] = Part(fixture.Text)
            });
            var item = Message(fixture.Id, "completed", fixture.CompletedPhase, fixture.Text);
            Append("response.output_item.done", new JsonObject { ["output_index"] = i, ["item"] = item });
            response["output"]!.AsArray().Add(item.DeepClone());
        }

        response["status"] = "completed";
        Append("response.completed", new JsonObject { ["response"] = response });
        return stream.ToString();

        void Append(string type, JsonObject body)
        {
            body["type"]            = type;
            body["sequence_number"] = sequence++;
            stream.Append($"event: {type}\ndata: {body.ToJsonString()}\n\n");
        }

        void Start(TextFixture fixture, int index) => Append("response.output_item.added", new JsonObject
        {
            ["output_index"] = index, ["item"] = Message(fixture.Id, "in_progress", fixture.StartedPhase, null)
        });

        JsonObject Message(string id, string status, string? phase, string? text)
        {
            var item = new JsonObject
            {
                ["type"]    = "message", ["id"] = id, ["role"] = "assistant", ["status"] = status,
                ["content"] = text is null ? new JsonArray() : new JsonArray(Part(text))
            };
            if (phase is not null || explicitNullPhase) item["phase"] = phase;
            return item;
        }
    }

    private static JsonObject Part(string text) => new()
    {
        ["type"] = "output_text", ["text"] = text, ["annotations"] = new JsonArray(), ["logprobs"] = new JsonArray()
    };

    private sealed record TextFixture(
        string  Id,
        string? StartedPhase,
        string? CompletedPhase,
        string  Text,
        string? StreamedText = null);
}

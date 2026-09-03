using System.ClientModel;
using System.Text.Json.Nodes;
using TinyHarness.Core.Agent;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Tools;

namespace TinyHarness.Tests;

/// <summary>
/// Contract tests for the M2 real transport: drive the OpenAI-SDK-backed client
/// against a local scripted SSE server and verify the protocol DTOs, streaming
/// translation, tool-call assembly and error/cancellation semantics — all
/// offline, no key, no network beyond loopback.
/// </summary>
public class OpenAiChatCompletionClientTests
{
    private static OpenAiChatCompletionClient NewClient(MockSseServer server)
        => new("mock-model", server.BaseUrl, "test-key");

    private static ChatCompletionRequest Request(params ChatMessage[] messages) => new()
    {
        Model    = "mock-model",
        Messages = messages,
    };

    private static async Task<List<ChatStreamEvent>> CollectAsync(IChatCompletionClient client,
                                                                  ChatCompletionRequest request,
                                                                  CancellationToken     cancellationToken = default)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var @event in client.CompleteAsync(request, cancellationToken))
        {
            events.Add(@event);
        }

        return events;
    }

    // ---- SSE body construction helpers --------------------------------------

    private static string Chunk(string deltaJson, string? finishReason = null)
    {
        var finish = finishReason is null ? "null" : $"\"{finishReason}\"";
        return "data: {\"id\":\"chatcmpl-mock\",\"object\":\"chat.completion.chunk\",\"created\":1700000000," +
               $"\"model\":\"mock\",\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":{finish}}}]}}\n\n";
    }

    private static string Sse(params string[] chunks) => string.Concat(chunks) + "data: [DONE]\n\n";

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static string RoleDelta() => Chunk("""{"role":"assistant","content":""}""");

    private static string ContentDelta(string text) => Chunk($"{{\"content\":{Json(text)}}}");

    private static string ToolCallHead(int index, string id, string name) => Chunk(
         $"{{\"tool_calls\":[{{\"index\":{index},\"id\":{Json(id)},\"type\":\"function\"," +
         $"\"function\":{{\"name\":{Json(name)},\"arguments\":\"\"}}}}]}}");

    private static string ToolArgumentsDelta(int index, string argumentsFragment) => Chunk(
         $"{{\"tool_calls\":[{{\"index\":{index},\"function\":{{\"arguments\":{Json(argumentsFragment)}}}}}]}}");

    private static string StopDelta() => Chunk("{}", finishReason : "stop");

    private static string ToolCallsFinishDelta() => Chunk("{}", finishReason : "tool_calls");

    // ---- tests ---------------------------------------------------------------

    [Fact]
    public async Task StreamingText_TranslatesToContentDeltasAndEnd()
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueRaw(Sse(RoleDelta(), ContentDelta("Hello from "), ContentDelta("the mock."), StopDelta()));
        var client = NewClient(server);

        var events = await CollectAsync(client, Request(ChatMessage.User("Hi there")));

        Assert.Equal(3, events.Count);
        Assert.Equal(ChatStreamEventKind.ContentDelta, events[0].Kind);
        Assert.Equal("Hello from ", events[0].ContentDelta);
        Assert.Equal(ChatStreamEventKind.ContentDelta, events[1].Kind);
        Assert.Equal("the mock.", events[1].ContentDelta);
        Assert.Equal(ChatStreamEventKind.End, events[2].Kind);

        // The accumulator still assembles the same text it would for a fake client.
        var accumulator = new StreamAccumulator();
        foreach (var @event in events)
        {
            accumulator.Append(@event);
        }

        accumulator.Finish();
        Assert.Equal("Hello from the mock.", accumulator.Content);

        // The request reached the right path and carried model + messages.
        var sent = Assert.Single(server.Requests);
        Assert.EndsWith("/chat/completions", sent.Path);
        Assert.Contains("mock-model", sent.Body);
        Assert.Contains("Hi there", sent.Body);
    }

    [Fact]
    public async Task ToolCall_SplitArguments_AreTranslatedAndAssembled()
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueRaw(Sse(RoleDelta(), ToolCallHead(0, "call_1", "read_file"),
                              ToolArgumentsDelta(0, """{"path":"/tmp/"""), ToolArgumentsDelta(0, """a.txt"}"""),
                              ToolCallsFinishDelta()));
        var client = NewClient(server);

        var events = await CollectAsync(client, Request(ChatMessage.User("read it")));

        Assert.Equal(4, events.Count);
        Assert.All(events.Take(3), e => Assert.Equal(ChatStreamEventKind.ToolCallDelta, e.Kind));
        Assert.Equal(ChatStreamEventKind.End, events[3].Kind);

        // Only the first fragment carries id/name, matching the fake-client shape.
        Assert.Equal(0, events[0].ToolCallIndex);
        Assert.Equal("call_1", events[0].ToolCallId);
        Assert.Equal("read_file", events[0].ToolCallFunctionName);
        Assert.Null(events[1].ToolCallId);
        Assert.Null(events[1].ToolCallFunctionName);

        var accumulator = new StreamAccumulator();
        foreach (var @event in events)
        {
            accumulator.Append(@event);
        }

        accumulator.Finish();
        var call = Assert.Single(accumulator.ToolCalls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("read_file", call.FunctionName);
        Assert.Equal("""{"path":"/tmp/a.txt"}""", call.ArgumentsJson);
    }

    [Fact]
    public async Task MultipleToolCalls_AreAccumulatedPerIndex()
    {
        using var server = new MockSseServer();
        server.Start();

        // Both calls announce in one chunk; argument fragments then interleave
        // per index, as real providers do.
        server.EnqueueRaw(Sse(
                              RoleDelta(),
                              Chunk("""{"tool_calls":[{"index":0,"id":"a","type":"function","function":{"name":"tool_a","arguments":""}},{"index":1,"id":"b","type":"function","function":{"name":"tool_b","arguments":""}}]}"""),
                              ToolArgumentsDelta(0, """{"x":1}"""),
                              ToolArgumentsDelta(1, """{"y":2}"""),
                              ToolCallsFinishDelta()));
        var client = NewClient(server);

        var events = await CollectAsync(client, Request(ChatMessage.User("run both")));

        var accumulator = new StreamAccumulator();
        foreach (var @event in events)
        {
            accumulator.Append(@event);
        }

        accumulator.Finish();
        Assert.Equal(2, accumulator.ToolCalls.Count);
        Assert.Equal("a", accumulator.ToolCalls[0].Id);
        Assert.Equal("tool_a", accumulator.ToolCalls[0].FunctionName);
        Assert.Equal("""{"x":1}""", accumulator.ToolCalls[0].ArgumentsJson);
        Assert.Equal("b", accumulator.ToolCalls[1].Id);
        Assert.Equal("tool_b", accumulator.ToolCalls[1].FunctionName);
        Assert.Equal("""{"y":2}""", accumulator.ToolCalls[1].ArgumentsJson);
    }

    [Fact]
    public async Task ToolsAreSentInTheRequest()
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueRaw(Sse(RoleDelta(), ContentDelta("ok"), StopDelta()));
        var client = NewClient(server);

        var tool = new ToolDefinition
        {
            Name        = "read_file",
            Description = "Read a text file.",
            Parameters =
                JsonNode.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""") as
                    JsonObject ?? throw new InvalidOperationException("bad fixture"),
        };

        await CollectAsync(client, new ChatCompletionRequest
        {
            Model    = "mock-model",
            Messages = [ChatMessage.User("read it")],
            Tools    = [tool],
        });

        var sent = Assert.Single(server.Requests);
        Assert.Contains("\"tools\"", sent.Body);
        Assert.Contains("read_file", sent.Body);
        Assert.Contains("\"description\"", sent.Body);
    }

    [Fact]
    public async Task HttpError_SurfacesAsClientModelExceptionWithStatus()
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueError(404);
        var client = NewClient(server);

        var ex =
            await Assert.ThrowsAsync<ClientResultException>(() =>
                                                                CollectAsync(client,
                                                                             Request(ChatMessage.User("boom"))));

        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task CancellationMidRequest_ThrowsOperationCanceled()
    {
        using var server = new MockSseServer();
        server.Start();
        // The server stalls before answering so the client is still waiting.
        server.Enqueue(_ =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(15));
            return new MockSseServer.HttpResponse(200, "text/event-stream", Sse(StopDelta()));
        });
        var client = NewClient(server);

        using var cts     = new CancellationTokenSource();
        var       collect = Task.Run(() => CollectAsync(client, Request(ChatMessage.User("slow")), cts.Token));
        await Task.Delay(200);
        await cts.CancelAsync();

        var completed = await Task.WhenAny(collect, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(collect, completed);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collect);
    }

    [Fact]
    public async Task TwoTurnAgentLoop_RunsOverTheRealTransport()
    {
        using var server = new MockSseServer();
        server.Start();

        // Turn 1: the model requests a tool call.
        server.EnqueueRaw(Sse(
                              RoleDelta(),
                              ContentDelta("I will read it."),
                              ToolCallHead(0, "call_1", "read_file"),
                              ToolArgumentsDelta(0, """{"path":"/tmp/a.txt"}"""),
                              ToolCallsFinishDelta()));

        // Turn 2: after the tool result it answers in plain text.
        server.EnqueueRaw(Sse(RoleDelta(), ContentDelta("The file read fine."), StopDelta()));

        var client = NewClient(server);
        var tool   = new FakeTool("read_file");
        var loop = new AgentLoop(client, new ToolRegistry([tool]), new AgentOptions
        {
            Model                     = "mock-model",
            MaxAgentSteps             = 10,
            DefaultToolTimeoutSeconds = 30,
        });

        var result = await loop.RunAsync("sys", "inspect /tmp/a.txt", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal("The file read fine.", result.FinalMessage);
        Assert.Equal(1, tool.ExecuteCount);
        Assert.Equal(2, server.Requests.Count);

        // The second request carries the tool result back to the model.
        var second = server.Requests[1];
        Assert.Contains("\"role\":\"tool\"", second.Body);
        Assert.Contains("\"tool_call_id\":\"call_1\"", second.Body);
        Assert.Contains("read_file ok", second.Body);

        // The first request declared the tool to the model.
        Assert.Contains("read_file", server.Requests[0].Body);
    }
}

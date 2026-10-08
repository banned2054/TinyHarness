using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Tools;
using ChatMessage = TinyHarness.Core.Models.ChatCompletions.ChatMessage;
using MicrosoftChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace TinyHarness.Tests;

/// <summary>
/// M.E.AI 传输层契约测试：经 ModelClientFactory 构造的 MicrosoftAiChatClient 驱动真实
/// OpenAI/M.E.AI SDK 打本地脚本 SSE 服务，验证请求形状、流式翻译、工具调用拼装、usage、
/// 结束原因与错误/取消语义——全部离线，无真实 key，无 loopback 之外的网络。
///
/// Contract tests for the M.E.AI transport: the MicrosoftAiChatClient built by
/// ModelClientFactory drives the real OpenAI/M.E.AI SDK against a local scripted
/// SSE server, verifying request shape, streaming translation, tool-call assembly,
/// usage, finish reason, and error/cancellation semantics — all offline, no key,
/// no network beyond loopback.
/// </summary>
public class MicrosoftAiChatClientTests
{
    private static IChatCompletionClient NewClient(MockSseServer server) =>
        ModelClientFactory.Create("mock-model", server.BaseUrl, "test-key");

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

    private static StreamAccumulator Accumulate(IEnumerable<ChatStreamEvent> events)
    {
        var accumulator = new StreamAccumulator();
        foreach (var @event in events)
        {
            accumulator.Append(@event);
        }

        accumulator.Finish();
        return accumulator;
    }

    // ---- SSE body construction helpers --------------------------------------

    private static string Chunk(string deltaJson, string? finishReason = null, string? usageJson = null)
    {
        var finish = finishReason is null ? "null" : $"\"{finishReason}\"";
        var usage  = usageJson is null ? string.Empty : $",\"usage\":{usageJson}";
        return "data: {\"id\":\"chatcmpl-mock\",\"object\":\"chat.completion.chunk\",\"created\":1700000000," +
               $"\"model\":\"mock\",\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":{finish}}}]{usage}}}\n\n";
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

    private static string UsageStopDelta(int promptTokens, int completionTokens) =>
        Chunk("{}", finishReason : "stop", usageJson : $"{{\"prompt_tokens\":{promptTokens}," +
                                                      $"\"completion_tokens\":{completionTokens}," +
                                                      $"\"total_tokens\":{promptTokens + completionTokens}}}");

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
        var accumulator = Accumulate(events);
        Assert.Equal("Hello from the mock.", accumulator.Content);

        // The request reached the right path and carried model + messages.
        var sent = Assert.Single(server.Requests);
        Assert.EndsWith("/chat/completions", sent.Path);
        Assert.Contains("mock-model", sent.Body);
        Assert.Contains("Hi there", sent.Body);
    }

    [Fact]
    public async Task ToolCall_SplitWireArguments_ArriveAssembledAndValid()
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueRaw(Sse(RoleDelta(), ToolCallHead(0, "call_1", "read_file"),
                              ToolArgumentsDelta(0, """{"path":"/tmp/"""), ToolArgumentsDelta(0, """a.txt"}"""),
                              ToolCallsFinishDelta()));
        var client = NewClient(server);

        var events = await CollectAsync(client, Request(ChatMessage.User("read it")));

        // The M.E.AI adapter assembles the wire fragments itself, so the adapter
        // surfaces one complete call event plus the terminal event.
        Assert.Equal(2, events.Count);
        Assert.Equal(ChatStreamEventKind.ToolCallDelta, events[0].Kind);
        Assert.Equal(ChatStreamEventKind.End, events[1].Kind);
        Assert.Equal(0, events[0].ToolCallIndex);
        Assert.Equal("call_1", events[0].ToolCallId);
        Assert.Equal("read_file", events[0].ToolCallFunctionName);

        // The single fragment already carries the complete, valid argument JSON.
        var call = Assert.Single(Accumulate(events).ToolCalls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("read_file", call.FunctionName);
        using var expected = JsonDocument.Parse("""{"path":"/tmp/a.txt"}""");
        using var actual   = JsonDocument.Parse(call.ArgumentsJson);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement), call.ArgumentsJson);
    }

    [Fact]
    public async Task MultipleToolCalls_AreAccumulatedPerIndex()
    {
        using var server = new MockSseServer();
        server.Start();

        // Both calls announce in one chunk; argument fragments then interleave
        // per index, as real providers do.
        server.EnqueueRaw(Sse(RoleDelta(),
                              Chunk("""{"tool_calls":[{"index":0,"id":"a","type":"function","function":{"name":"tool_a","arguments":""}},{"index":1,"id":"b","type":"function","function":{"name":"tool_b","arguments":""}}]}"""),
                              ToolArgumentsDelta(0, """{"x":1}"""), ToolArgumentsDelta(1, """{"y":2}"""),
                              ToolCallsFinishDelta()));
        var client = NewClient(server);

        var events = await CollectAsync(client, Request(ChatMessage.User("run both")));

        var toolCallEvents = events.Where(e => e.Kind == ChatStreamEventKind.ToolCallDelta).ToArray();
        Assert.Equal(2, toolCallEvents.Length);
        Assert.Equal([0, 1], toolCallEvents.Select(e => e.ToolCallIndex).OrderBy(i => i).ToArray());

        var accumulator = Accumulate(events);
        Assert.Equal(2, accumulator.ToolCalls.Count);
        Assert.Equal("a", accumulator.ToolCalls[0].Id);
        Assert.Equal("tool_a", accumulator.ToolCalls[0].FunctionName);
        Assert.Equal("""{"x":1}""", accumulator.ToolCalls[0].ArgumentsJson);
        Assert.Equal("b", accumulator.ToolCalls[1].Id);
        Assert.Equal("tool_b", accumulator.ToolCalls[1].FunctionName);
        Assert.Equal("""{"y":2}""", accumulator.ToolCalls[1].ArgumentsJson);
    }

    [Fact]
    public async Task ToolCallsWithoutCallId_GetDistinctIndexesAndValidJson()
    {
        // MockSseServer 与真实 SDK 在 wire 上无法产生缺失 id 的工具调用，这里用直接产出
        // ChatResponseUpdate 的 IChatClient fake 构造两个 CallId 为空串的调用：空 id 不注册
        // 索引字典，修复前第二次分配读到原地踏步的计数，两个调用叠进同一累加缓冲区，
        // 参数 JSON 被拼接成非法文本。M.E.AI 的 FunctionCallContent 构造器拒绝 null CallId
        // （ArgumentNullException），空串是可构造的等价"无 id"形态。
        //
        // MockSseServer and the real SDK cannot produce a tool call without an id on the wire,
        // so an IChatClient fake yielding prebuilt ChatResponseUpdate instances constructs two
        // calls with an empty CallId: an absent id registers nothing, so before the fix the
        // second allocation read a stuck counter and both calls folded into one accumulator
        // buffer, concatenating their argument JSON into invalid text. The M.E.AI
        // FunctionCallContent constructor rejects a null CallId (ArgumentNullException); the
        // empty string is the constructible equivalent of "no id".
        var updates = new List<ChatResponseUpdate>
        {
            new(null, new List<AIContent>
            {
                new FunctionCallContent(string.Empty, "tool_a", new Dictionary<string, object?> { ["x"] = 1 }),
            }),
            new(null, new List<AIContent>
            {
                new FunctionCallContent(string.Empty, "tool_b", new Dictionary<string, object?> { ["y"] = 2 }),
            }),
        };
        using var client = new MicrosoftAiChatClient(new ScriptedUpdateChatClient(updates));

        var events = await CollectAsync(client, Request(ChatMessage.User("run both")));

        var toolCallEvents = events.Where(e => e.Kind == ChatStreamEventKind.ToolCallDelta).ToArray();
        Assert.Equal(2, toolCallEvents.Length);
        Assert.NotEqual(toolCallEvents[0].ToolCallIndex, toolCallEvents[1].ToolCallIndex);

        var accumulator = Accumulate(events);
        Assert.Equal(2, accumulator.ToolCalls.Count);
        Assert.Equal("tool_a", accumulator.ToolCalls[0].FunctionName);
        Assert.Equal("""{"x":1}""", accumulator.ToolCalls[0].ArgumentsJson);
        Assert.Equal("tool_b", accumulator.ToolCalls[1].FunctionName);
        Assert.Equal("""{"y":2}""", accumulator.ToolCalls[1].ArgumentsJson);
    }

    [Fact]
    public async Task RequestsCarryToolsStreamFlagAndToolResultRoundTrip()
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueRaw(Sse(RoleDelta(), ContentDelta("I will read it."), ToolCallHead(0, "call_1", "read_file"),
                              ToolArgumentsDelta(0, """{"path":"/tmp/a.txt"}"""), ToolCallsFinishDelta()));
        server.EnqueueRaw(Sse(RoleDelta(), ContentDelta("done"), StopDelta()));
        var client = NewClient(server);

        var tool = ReadFileTool();
        await CollectAsync(client, new ChatCompletionRequest
        {
            Model    = "mock-model",
            Messages = [ChatMessage.User("read it")],
            Tools    = [tool],
        });

        // Replay the round an Agent loop would build: the assistant's tool call
        // plus the tool result linked to the same call id.
        await CollectAsync(client, new ChatCompletionRequest
        {
            Model = "mock-model",
            Messages =
            [
                ChatMessage.User("read it"),
                ChatMessage.Assistant("I will read it.",
                                      [new ChatToolCall("call_1", "read_file", """{"path":"/tmp/a.txt"}""")]),
                ChatMessage.Tool("read_file", "call_1", "read_file ok"),
            ],
            Tools = [tool],
        });

        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, r => Assert.Equal("POST", r.Method));
        Assert.All(server.Requests, r => Assert.Equal("/v1/chat/completions", r.Path));

        // Round 1 declared the tool, enabled streaming, and kept the schema.
        var first = JsonNode.Parse(server.Requests[0].Body)!.AsObject();
        Assert.True(first["stream"]!.GetValue<bool>());
        var tools = first["tools"]!.AsArray();
        var tool0 = Assert.Single(tools)!;
        Assert.Equal("read_file", tool0["function"]!["name"]!.GetValue<string>());
        Assert.Equal("Read a text file.", tool0["function"]!["description"]!.GetValue<string>());
        var parameters = tool0["function"]!["parameters"]!.AsObject();
        Assert.Equal("object", parameters["type"]!.GetValue<string>());
        Assert.Equal("string", parameters["properties"]!["path"]!["type"]!.GetValue<string>());

        // Round 2 replayed the assistant tool call and the matching tool result.
        var second   = JsonNode.Parse(server.Requests[1].Body)!.AsObject();
        var messages = second["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        var assistant = messages[1]!;
        Assert.Equal("assistant", assistant["role"]!.GetValue<string>());
        var wireCall = Assert.Single(assistant["tool_calls"]!.AsArray())!;
        Assert.Equal("call_1", wireCall["id"]!.GetValue<string>());
        Assert.Equal("read_file", wireCall["function"]!["name"]!.GetValue<string>());
        var wireArguments = JsonNode.Parse(wireCall["function"]!["arguments"]!.GetValue<string>())!.AsObject();
        Assert.Equal("/tmp/a.txt", wireArguments["path"]!.GetValue<string>());
        var toolMessage = messages[2]!;
        Assert.Equal("tool", toolMessage["role"]!.GetValue<string>());
        Assert.Equal("call_1", toolMessage["tool_call_id"]!.GetValue<string>());
        Assert.Equal("read_file ok", toolMessage["content"]!.GetValue<string>());
    }

    private static ToolDefinition ReadFileTool() => new()
    {
        Name        = "read_file",
        Description = "Read a text file.",
        Parameters =
            JsonNode.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""") as
                JsonObject ?? throw new InvalidOperationException("bad fixture"),
    };

    [Fact]
    public async Task FinalUsageChunk_FillsAccumulatorTokenCounts()
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueRaw(Sse(RoleDelta(), ContentDelta("counted"), UsageStopDelta(12, 7)));
        var client = NewClient(server);

        var events = await CollectAsync(client, Request(ChatMessage.User("count")));

        var accumulator = Accumulate(events);
        Assert.Equal("counted", accumulator.Content);
        Assert.Equal(12, accumulator.InputTokens);
        Assert.Equal(7, accumulator.OutputTokens);
        Assert.Equal("stop", accumulator.FinishReason);
    }

    [Fact]
    public async Task ToolCallsFinishReason_IsCarriedOnTheEndEvent()
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueRaw(Sse(RoleDelta(), ToolCallHead(0, "call_1", "read_file"),
                              ToolArgumentsDelta(0, "{}"), ToolCallsFinishDelta()));
        var client = NewClient(server);

        var events = await CollectAsync(client, Request(ChatMessage.User("read")));

        var end = events.Single(e => e.Kind == ChatStreamEventKind.End);
        Assert.Equal("tool_calls", end.FinishReason);
    }

    [Fact]
    public async Task HttpError_SurfacesAsClientResultExceptionWithStatus()
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

        using var cts = new CancellationTokenSource();
        var collect = Task.Run(() => CollectAsync(client, Request(ChatMessage.User("slow")), cts.Token));
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
        server.EnqueueRaw(Sse(RoleDelta(), ContentDelta("I will read it."), ToolCallHead(0, "call_1", "read_file"),
                              ToolArgumentsDelta(0, """{"path":"/tmp/a.txt"}"""), ToolCallsFinishDelta()));

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

    /// <summary>
    /// 逐条回放预构造 <see cref="ChatResponseUpdate"/> 的最小 IChatClient：用于 wire 协议无法
    /// 表达的离线场景（如缺失工具调用 id），绕过 SSE 与真实 SDK。
    ///
    /// A minimal IChatClient replaying prebuilt <see cref="ChatResponseUpdate"/> instances
    /// one by one; used for offline shapes the wire protocol cannot express (such as a
    /// missing tool-call id), bypassing SSE and the real SDK.
    /// </summary>
    private sealed class ScriptedUpdateChatClient(IReadOnlyList<ChatResponseUpdate> updates) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<MicrosoftChatMessage> messages,
                                                   ChatOptions? options,
                                                   CancellationToken cancellationToken)
        {
            throw new NotSupportedException("This fake only supports streaming.");
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MicrosoftChatMessage> messages,
            ChatOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var update in updates)
            {
                await Task.Yield();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

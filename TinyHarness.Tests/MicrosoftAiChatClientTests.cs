using Microsoft.Extensions.AI;
using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
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
///     <para>
///         M.E.AI 传输层契约测试：经 ModelClientFactory 构造的 MicrosoftAiChatClient 驱动真实
///         OpenAI/M.E.AI SDK 打本地脚本 SSE 服务，验证请求形状、流式翻译、工具调用拼装、usage、
///         结束原因与错误/取消语义——全部离线，无真实 key，无 loopback 之外的网络。
///     </para>
///     <para>
///         Contract tests for the M.E.AI transport: the MicrosoftAiChatClient built by
///         ModelClientFactory drives the real OpenAI/M.E.AI SDK against a local scripted
///         SSE server, verifying request shape, streaming translation, tool-call assembly,
///         usage, finish reason, and error/cancellation semantics — all offline, no key,
///         no network beyond loopback.
///     </para>
/// </summary>
public class MicrosoftAiChatClientTests
{
    private static IChatCompletionClient NewClient(MockSseServer server) =>
        ModelClientFactory.Create("mock-model", server.BaseUrl, "test-key");

    private static ChatCompletionRequest Request(params ChatMessage[] messages) => new()
    {
        Model    = "mock-model",
        Messages = messages
    };

    private static async Task<List<ChatStreamEvent>> CollectAsync(
        IChatCompletionClient client, ChatCompletionRequest request, CancellationToken cancellationToken = default)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var @event in client.CompleteAsync(request, cancellationToken)) events.Add(@event);

        return events;
    }

    private static StreamAccumulator Accumulate(IEnumerable<ChatStreamEvent> events)
    {
        var accumulator = new StreamAccumulator();
        foreach (var @event in events) accumulator.Append(@event);

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

    private static string Json(string value) => JsonSerializer.Serialize(value);

    private static string RoleDelta() => Chunk("""{"role":"assistant","content":""}""");

    private static string ContentDelta(string text) => Chunk($"{{\"content\":{Json(text)}}}");

    private static string ToolCallHead(int index, string id, string name) =>
        Chunk(
              $"{{\"tool_calls\":[{{\"index\":{index},\"id\":{Json(id)},\"type\":\"function\"," +
              $"\"function\":{{\"name\":{Json(name)},\"arguments\":\"\"}}}}]}}");

    private static string ToolArgumentsDelta(int index, string argumentsFragment) =>
        Chunk(
              $"{{\"tool_calls\":[{{\"index\":{index},\"function\":{{\"arguments\":{Json(argumentsFragment)}}}}}]}}");

    private static string StopDelta() => Chunk("{}", "stop");

    private static string ToolCallsFinishDelta() => Chunk("{}", "tool_calls");

    private static string UsageStopDelta(int promptTokens, int completionTokens) =>
        Chunk("{}", "stop", $"{{\"prompt_tokens\":{promptTokens},"       +
                            $"\"completion_tokens\":{completionTokens}," +
                            $"\"total_tokens\":{promptTokens + completionTokens}}}");

    // ---- tests ---------------------------------------------------------------

    /// <summary>
    ///     <para>空流或缺少终止信息的真实 SDK 响应不得变为完成或执行工具。</para>
    ///     <para>Empty or unterminated streams through the real SDK cannot complete a turn or execute tools.</para>
    /// </summary>
    [TestCase("empty")]
    [TestCase("done-only")]
    [TestCase("role-only")]
    [TestCase("text-eof")]
    [TestCase("tool-eof")]
    public async Task MissingCompletionFailsThroughSdk(string scenario)
    {
        using var server = new MockSseServer();
        server.Start();
        var body = scenario switch
        {
            "done-only" => Sse(),
            "role-only" => Sse(RoleDelta()),
            "text-eof"  => RoleDelta() + ContentDelta("partial"),
            "tool-eof"  => RoleDelta() + ToolCallHead(0, "call_1", "probe") + ToolArgumentsDelta(0, "{}"),
            _           => string.Empty
        };
        server.EnqueueRaw(body);
        var tool = new FakeTool("probe");
        var loop = new AgentLoop(NewClient(server), new ToolRegistry([tool]), new AgentOptions
        {
            Model = "mock-model", MaxAgentSteps = 3, DefaultToolTimeoutSeconds = 30
        });

        var result = await loop.RunAsync("sys", "go", default);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(tool.ExecuteCount, Is.Zero);
        Assert.That(server.Requests.Count, Is.EqualTo(1));
        Assert.That(loop.History.Any(m => m.Role == TinyHarness.Core.Models.ChatCompletions.ChatRole.Assistant), Is.False);
    }

    [Test]
    public async Task StreamingText_TranslatesToContentDeltasAndEnd()
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueRaw(Sse(RoleDelta(), ContentDelta("Hello from "), ContentDelta("the mock."), StopDelta()));
        var client = NewClient(server);

        var events = await CollectAsync(client, Request(ChatMessage.User("Hi there")));

        Assert.That(events.Count, Is.EqualTo(4));
        Assert.That(events[0].Kind, Is.EqualTo(ChatStreamEventKind.ResponseStarted));
        Assert.That(events[1].Kind, Is.EqualTo(ChatStreamEventKind.ContentDelta));
        Assert.That(events[1].ContentDelta, Is.EqualTo("Hello from "));
        Assert.That(events[2].Kind, Is.EqualTo(ChatStreamEventKind.ContentDelta));
        Assert.That(events[2].ContentDelta, Is.EqualTo("the mock."));
        Assert.That(events[3].Kind, Is.EqualTo(ChatStreamEventKind.End));

        // 累加器仍拼装出与 fake client 场景相同的文本。
        var accumulator = Accumulate(events);
        Assert.That(accumulator.Content, Is.EqualTo("Hello from the mock."));

        // 请求到达正确路径并携带 model 与 messages。
        var sent = TestAssert.Single(server.Requests);
        Assert.That(sent.Path, Does.EndWith("/chat/completions"));
        Assert.That(sent.Body, Does.Contain("mock-model"));
        Assert.That(sent.Body, Does.Contain("Hi there"));
    }

    [Test]
    public async Task ToolCall_SplitWireArguments_ArriveAssembledAndValid()
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueRaw(Sse(RoleDelta(), ToolCallHead(0, "call_1", "read_file"),
                              ToolArgumentsDelta(0, """{"path":"/tmp/"""), ToolArgumentsDelta(0, """a.txt"}"""),
                              ToolCallsFinishDelta()));
        var client = NewClient(server);

        var events = await CollectAsync(client, Request(ChatMessage.User("read it")));

        // M.E.AI 适配器自行拼装 wire 分片，因此适配层呈现一个完整调用事件加终态事件。
        Assert.That(events.Count, Is.EqualTo(3));
        Assert.That(events[0].Kind, Is.EqualTo(ChatStreamEventKind.ResponseStarted));
        Assert.That(events[1].Kind, Is.EqualTo(ChatStreamEventKind.ToolCallDelta));
        Assert.That(events[2].Kind, Is.EqualTo(ChatStreamEventKind.End));
        Assert.That(events[1].ToolCallIndex, Is.EqualTo(0));
        Assert.That(events[1].ToolCallId, Is.EqualTo("call_1"));
        Assert.That(events[1].ToolCallFunctionName, Is.EqualTo("read_file"));

        // 单个分片已携带完整、合法的参数 JSON。
        var call = TestAssert.Single(Accumulate(events).ToolCalls);
        Assert.That(call.Id, Is.EqualTo("call_1"));
        Assert.That(call.FunctionName, Is.EqualTo("read_file"));
        using var expected = JsonDocument.Parse("""{"path":"/tmp/a.txt"}""");
        using var actual   = JsonDocument.Parse(call.ArgumentsJson);
        Assert.That(JsonElement.DeepEquals(expected.RootElement, actual.RootElement), Is.True, call.ArgumentsJson);
    }

    [Test]
    public async Task MultipleToolCalls_AreAccumulatedPerIndex()
    {
        using var server = new MockSseServer();
        server.Start();

        // 两个调用在同一块中声明；参数分片随后按 index 交错，与真实供应商一致。
        server.EnqueueRaw(Sse(RoleDelta(),
                              Chunk("""{"tool_calls":[{"index":0,"id":"a","type":"function","function":{"name":"tool_a","arguments":""}},{"index":1,"id":"b","type":"function","function":{"name":"tool_b","arguments":""}}]}"""),
                              ToolArgumentsDelta(0, """{"x":1}"""), ToolArgumentsDelta(1, """{"y":2}"""),
                              ToolCallsFinishDelta()));
        var client = NewClient(server);

        var events = await CollectAsync(client, Request(ChatMessage.User("run both")));

        var toolCallEvents = events.Where(e => e.Kind == ChatStreamEventKind.ToolCallDelta).ToArray();
        Assert.That(toolCallEvents.Length, Is.EqualTo(2));
        Assert.That(toolCallEvents.Select(e => e.ToolCallIndex).OrderBy(i => i).ToArray(), Is.EqualTo([0, 1]));

        var accumulator = Accumulate(events);
        Assert.That(accumulator.ToolCalls.Count, Is.EqualTo(2));
        Assert.That(accumulator.ToolCalls[0].Id, Is.EqualTo("a"));
        Assert.That(accumulator.ToolCalls[0].FunctionName, Is.EqualTo("tool_a"));
        Assert.That(accumulator.ToolCalls[0].ArgumentsJson, Is.EqualTo("""{"x":1}"""));
        Assert.That(accumulator.ToolCalls[1].Id, Is.EqualTo("b"));
        Assert.That(accumulator.ToolCalls[1].FunctionName, Is.EqualTo("tool_b"));
        Assert.That(accumulator.ToolCalls[1].ArgumentsJson, Is.EqualTo("""{"y":2}"""));
    }

    [Test]
    public async Task ToolCallsWithoutCallId_GetDistinctIndexesAndValidJson()
    {
        // MockSseServer 与真实 SDK 在 wire 上无法产生缺失 id 的工具调用，这里用直接产出
        // ChatResponseUpdate 的 IChatClient fake 构造两个 CallId 为空串的调用：空 id 不注册
        // 索引字典，修复前第二次分配读到原地踏步的计数，两个调用叠进同一累加缓冲区，
        // 参数 JSON 被拼接成非法文本。M.E.AI 的 FunctionCallContent 构造器拒绝 null CallId
        // （ArgumentNullException），空串是可构造的等价"无 id"形态。
        //
        // MockSseServer 与真实 SDK 无法在 wire 上产生没有 id 的工具调用，因此用逐条回放预构造
        // ChatResponseUpdate 的 IChatClient fake 构造两个 CallId 为空的调用：缺失 id 时不登记
        // 任何内容，两个调用必须各自获得独立累加缓冲，折叠进同一缓冲会把参数 JSON 拼接成非法
        // 文本。M.E.AI 的 FunctionCallContent 构造函数拒绝 null CallId（ArgumentNullException），
        // 空字符串是"无 id"的可构造等价物。
        var updates = new List<ChatResponseUpdate>
        {
            new(null, new List<AIContent>
            {
                new FunctionCallContent(string.Empty, "tool_a", new Dictionary<string, object?> { ["x"] = 1 })
            }),
            new(null, new List<AIContent>
            {
                new FunctionCallContent(string.Empty, "tool_b", new Dictionary<string, object?> { ["y"] = 2 })
            })
        };
        using var client = new MicrosoftAiChatClient(new ScriptedUpdateChatClient(updates));

        var events = await CollectAsync(client, Request(ChatMessage.User("run both")));

        var toolCallEvents = events.Where(e => e.Kind == ChatStreamEventKind.ToolCallDelta).ToArray();
        Assert.That(toolCallEvents.Length, Is.EqualTo(2));
        Assert.That(toolCallEvents[1].ToolCallIndex, Is.Not.EqualTo(toolCallEvents[0].ToolCallIndex));

        var accumulator = Accumulate(events);
        Assert.That(accumulator.ToolCalls.Count, Is.EqualTo(2));
        Assert.That(accumulator.ToolCalls[0].FunctionName, Is.EqualTo("tool_a"));
        Assert.That(accumulator.ToolCalls[0].ArgumentsJson, Is.EqualTo("""{"x":1}"""));
        Assert.That(accumulator.ToolCalls[1].FunctionName, Is.EqualTo("tool_b"));
        Assert.That(accumulator.ToolCalls[1].ArgumentsJson, Is.EqualTo("""{"y":2}"""));
    }

    [Test]
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
            Tools    = [tool]
        });

        // 回放 Agent 循环会构建的回合：assistant 的工具调用加同一 call id 的工具结果。
        await CollectAsync(client, new ChatCompletionRequest
        {
            Model = "mock-model",
            Messages =
            [
                ChatMessage.User("read it"),
                ChatMessage.Assistant("I will read it.",
                                      [new ChatToolCall("call_1", "read_file", """{"path":"/tmp/a.txt"}""")]),
                ChatMessage.Tool("read_file", "call_1", "read_file ok")
            ],
            Tools = [tool]
        });

        Assert.That(server.Requests.Count, Is.EqualTo(2));
        TestAssert.All(server.Requests, r => Assert.That(r.Method, Is.EqualTo("POST")));
        TestAssert.All(server.Requests, r => Assert.That(r.Path, Is.EqualTo("/v1/chat/completions")));

        // 第 1 轮声明了工具、启用流式并保留 schema。
        var first = JsonNode.Parse(server.Requests[0].Body)!.AsObject();
        Assert.That(first["stream"]!.GetValue<bool>(), Is.True);
        var tools = first["tools"]!.AsArray();
        var tool0 = TestAssert.Single(tools)!;
        Assert.That(tool0["function"]!["name"]!.GetValue<string>(), Is.EqualTo("read_file"));
        Assert.That(tool0["function"]!["description"]!.GetValue<string>(), Is.EqualTo("Read a text file."));
        var parameters = tool0["function"]!["parameters"]!.AsObject();
        Assert.That(parameters["type"]!.GetValue<string>(), Is.EqualTo("object"));
        Assert.That(parameters["properties"]!["path"]!["type"]!.GetValue<string>(), Is.EqualTo("string"));

        // 第 2 轮回放了 assistant 工具调用与匹配的工具结果。
        var second   = JsonNode.Parse(server.Requests[1].Body)!.AsObject();
        var messages = second["messages"]!.AsArray();
        Assert.That(messages.Count, Is.EqualTo(3));
        var assistant = messages[1]!;
        Assert.That(assistant["role"]!.GetValue<string>(), Is.EqualTo("assistant"));
        var wireCall = TestAssert.Single(assistant["tool_calls"]!.AsArray())!;
        Assert.That(wireCall["id"]!.GetValue<string>(), Is.EqualTo("call_1"));
        Assert.That(wireCall["function"]!["name"]!.GetValue<string>(), Is.EqualTo("read_file"));
        var wireArguments = JsonNode.Parse(wireCall["function"]!["arguments"]!.GetValue<string>())!.AsObject();
        Assert.That(wireArguments["path"]!.GetValue<string>(), Is.EqualTo("/tmp/a.txt"));
        var toolMessage = messages[2]!;
        Assert.That(toolMessage["role"]!.GetValue<string>(), Is.EqualTo("tool"));
        Assert.That(toolMessage["tool_call_id"]!.GetValue<string>(), Is.EqualTo("call_1"));
        Assert.That(toolMessage["content"]!.GetValue<string>(), Is.EqualTo("read_file ok"));
    }

    private static ToolDefinition ReadFileTool() => new()
    {
        Name        = "read_file",
        Description = "Read a text file.",
        Parameters =
            JsonNode.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""") as
                JsonObject ?? throw new InvalidOperationException("bad fixture")
    };

    [Test]
    public async Task FinalUsageChunk_FillsAccumulatorTokenCounts()
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueRaw(Sse(RoleDelta(), ContentDelta("counted"), UsageStopDelta(12, 7)));
        var client = NewClient(server);

        var events = await CollectAsync(client, Request(ChatMessage.User("count")));

        var accumulator = Accumulate(events);
        Assert.That(accumulator.Content, Is.EqualTo("counted"));
        Assert.That(accumulator.InputTokens, Is.EqualTo(12));
        Assert.That(accumulator.OutputTokens, Is.EqualTo(7));
        Assert.That(accumulator.FinishReason, Is.EqualTo("stop"));
    }

    /// <summary>
    ///     <para>真实 SDK 映射缓存及推理子计数，缺失、显式零和非零三种情况保持区分。</para>
    ///     <para>The real SDK maps cache and reasoning subsets while distinguishing missing, explicit zero and nonzero values.</para>
    /// </summary>
    [TestCase(null)]
    [TestCase(0)]
    [TestCase(8)]
    public async Task UsageSubsetsRemainOptionalThroughSdk(int? cached)
    {
        using var server = new MockSseServer();
        server.Start();
        var details = cached is { } count ? $",\"prompt_tokens_details\":{{\"cached_tokens\":{count}}}" : string.Empty;
        var usage = $"{{\"prompt_tokens\":12,\"completion_tokens\":20,\"total_tokens\":32{details}," +
                    "\"completion_tokens_details\":{\"reasoning_tokens\":7}}";
        server.EnqueueRaw(Sse(RoleDelta(), ContentDelta("done"), Chunk("{}", "stop", usage)));

        var events = await CollectAsync(NewClient(server), Request(ChatMessage.User("go")));

        Assert.That(events.Count(e => e.Kind == ChatStreamEventKind.ResponseStarted), Is.EqualTo(1));
        var end = events.Single(e => e.Kind  == ChatStreamEventKind.End);
        Assert.That(end.CachedInputTokens, Is.EqualTo(cached));
        Assert.That(end.ReasoningTokens, Is.EqualTo(7));
        Assert.That(Accumulate(events).CachedInputTokens, Is.EqualTo(cached));
    }

    [Test]
    public async Task ToolCallsFinishReason_IsCarriedOnTheEndEvent()
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueRaw(Sse(RoleDelta(), ToolCallHead(0, "call_1", "read_file"),
                              ToolArgumentsDelta(0, "{}"), ToolCallsFinishDelta()));
        var client = NewClient(server);

        var events = await CollectAsync(client, Request(ChatMessage.User("read")));

        var end = events.Single(e => e.Kind == ChatStreamEventKind.End);
        Assert.That(end.FinishReason, Is.EqualTo("tool_calls"));
    }

    [Test]
    public async Task HttpError_SurfacesAsClientResultExceptionWithStatus()
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueError(404);
        var client = NewClient(server);

        var ex =
            await TestAssert.ThrowsAsync<ClientResultException>(() =>
                                                                    CollectAsync(client,
                                                                             Request(ChatMessage.User("boom"))));

        Assert.That(ex.Message, Does.Contain("404"));
    }

    [Test]
    public async Task CancellationMidRequest_ThrowsOperationCanceled()
    {
        using var server = new MockSseServer();
        server.Start();
        // 服务端应答前停顿，客户端仍在等待。
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
        Assert.That(completed, Is.SameAs(collect));

        await Assert.CatchAsync<OperationCanceledException>(() => collect);
    }

    [Test]
    public async Task TwoTurnAgentLoop_RunsOverTheRealTransport()
    {
        using var server = new MockSseServer();
        server.Start();

        // 第 1 回合：模型请求工具调用。
        server.EnqueueRaw(Sse(RoleDelta(), ContentDelta("I will read it."), ToolCallHead(0, "call_1", "read_file"),
                              ToolArgumentsDelta(0, """{"path":"/tmp/a.txt"}"""), ToolCallsFinishDelta()));

        // 第 2 回合：收到工具结果后以纯文本回答。
        server.EnqueueRaw(Sse(RoleDelta(), ContentDelta("The file read fine."), StopDelta()));

        var client = NewClient(server);
        var tool   = new FakeTool("read_file");
        var loop = new AgentLoop(client, new ToolRegistry([tool]), new AgentOptions
        {
            Model                     = "mock-model",
            MaxAgentSteps             = 10,
            DefaultToolTimeoutSeconds = 30
        });

        var result = await loop.RunAsync("sys", "inspect /tmp/a.txt", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(result.FinalMessage, Is.EqualTo("The file read fine."));
        Assert.That(tool.ExecuteCount, Is.EqualTo(1));
        Assert.That(server.Requests.Count, Is.EqualTo(2));

        // 第二个请求把工具结果回传给模型。
        var second = server.Requests[1];
        Assert.That(second.Body, Does.Contain("\"role\":\"tool\""));
        Assert.That(second.Body, Does.Contain("\"tool_call_id\":\"call_1\""));
        Assert.That(second.Body, Does.Contain("read_file ok"));

        // 第一个请求向模型声明了工具。
        Assert.That(server.Requests[0].Body, Does.Contain("read_file"));
    }

    /// <summary>
    ///     <para>
    ///         逐条回放预构造 <see cref="ChatResponseUpdate" /> 的最小 IChatClient：用于 wire 协议无法
    ///         表达的离线场景（如缺失工具调用 id），绕过 SSE 与真实 SDK。
    ///     </para>
    ///     <para>
    ///         A minimal IChatClient replaying prebuilt <see cref="ChatResponseUpdate" /> instances
    ///         one by one; used for offline shapes the wire protocol cannot express (such as a
    ///         missing tool-call id), bypassing SSE and the real SDK.
    ///     </para>
    /// </summary>
    private sealed class ScriptedUpdateChatClient(IReadOnlyList<ChatResponseUpdate> updates) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MicrosoftChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake only supports streaming.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MicrosoftChatMessage>          messages, ChatOptions? options,
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

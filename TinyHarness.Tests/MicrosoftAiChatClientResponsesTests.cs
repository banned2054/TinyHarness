using System.ClientModel;
using System.Text;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Tools;
using ChatFinishReason = Microsoft.Extensions.AI.ChatFinishReason;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         Responses 协议传输契约测试：经 ModelClientFactory 以 ChatApiKind.Responses 构造的
///         MicrosoftAiChatClient 驱动真实 OpenAI/M.E.AI SDK 打本地脚本 SSE 服务，验证流式拼装、
///         reasoning 条目累积、无状态请求（store:false、无 previous_response_id）、reasoning 与工具
///         结果回传、AgentLoop 两轮闭环与错误语义——全部离线，fixture 形状照抄
///         artifacts/protocol-compat/ProviderProbe.cs 的 ResponsesSse。
///     </para>
///     <para>
///         Contract tests for the Responses protocol transport: the MicrosoftAiChatClient built by
///         ModelClientFactory with ChatApiKind.Responses drives the real OpenAI/M.E.AI SDK against a
///         local scripted SSE server, verifying stream assembly, reasoning entry accumulation,
///         stateless requests (store:false, no previous_response_id), reasoning and tool-result
///         replay, a two-turn AgentLoop, and error semantics — all offline, with fixtures shaped
///         after ResponsesSse in artifacts/protocol-compat/ProviderProbe.cs.
///     </para>
/// </summary>
public class MicrosoftAiChatClientResponsesTests
{
    private const string Model = "resp-model";

    private static IChatCompletionClient NewClient(MockSseServer server) =>
        ModelClientFactory.Create(Model, server.BaseUrl, "test-key", ChatApiKind.Responses);

    private static ChatCompletionRequest Request(params ChatMessage[] messages) => new()
    {
        Model    = Model,
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

    // ---- Responses SSE fixtures（照抄 ProviderProbe.ResponsesSse 的事件形状） ----

    private static string Event(string type, JsonObject body)
    {
        body["type"] = type;
        return $"event: {type}\ndata: {body.ToJsonString()}\n\n";
    }

    /// <summary>
    ///     <para>
    ///         构造一轮 Responses SSE：reasoning 摘要增量 + 条目完成（encrypted_content）、文本增量、
    ///         count 个 function_call 分片参数与完成、带 usage 的 response.completed。
    ///     </para>
    ///     <para>
    ///         Builds one Responses SSE round: a reasoning summary delta plus item done
    ///         (encrypted_content), text deltas, count function calls with split argument
    ///         fragments and done events, and a response.completed carrying usage.
    ///     </para>
    /// </summary>
    private static string ResponsesSse(int calls)
    {
        var s = new StringBuilder();
        var response =
            JsonNode.Parse("""{"id":"resp_1","object":"response","created_at":1,"status":"in_progress","model":"mock","output":[],"store":false}""")
                !.AsObject();
        s.Append(Event("response.created",
                       new JsonObject { ["sequence_number"] = 0, ["response"] = response.DeepClone() }));

        s.Append(Event("response.reasoning_summary_text.delta",
                       JsonNode.Parse("""{"sequence_number":1,"item_id":"rs_1","output_index":0,"summary_index":0,"delta":"thought"}""")
                           !.AsObject()));
        s.Append(Event("response.output_item.done",
                       JsonNode.Parse(
                                      """{"sequence_number":2,"output_index":0,"item":{"type":"reasoning","id":"rs_1","summary":[{"type":"summary_text","text":"thought"}],"encrypted_content":"opaque"}}""")
                           !.AsObject()));

        foreach (var text in new[] { "Hello ", "world" })
            s.Append(Event("response.output_text.delta", new JsonObject
            {
                ["sequence_number"] = 3, ["item_id"] = "msg_1", ["output_index"] = 1,
                ["content_index"]   = 0, ["delta"]   = text
            }));

        for (var i = 0; i < calls; i++)
        {
            var item = new JsonObject
            {
                ["type"] = "function_call", ["id"]    = $"fc_{i}", ["call_id"] = $"call_{i}",
                ["name"] = "read_file", ["arguments"] = "", ["status"]         = "in_progress"
            };
            s.Append(Event("response.output_item.added",
                           new JsonObject
                               { ["sequence_number"] = 4, ["output_index"] = i + 2, ["item"] = item.DeepClone() }));
            s.Append(Event("response.function_call_arguments.delta", new JsonObject
            {
                ["sequence_number"] = 5, ["item_id"] = $"fc_{i}", ["output_index"] = i + 2, ["delta"] = "{\"path\":"
            }));
            s.Append(Event("response.function_call_arguments.delta", new JsonObject
            {
                ["sequence_number"] = 6, ["item_id"] = $"fc_{i}", ["output_index"] = i + 2, ["delta"] = "\"a\"}"
            }));
            item["arguments"] = "{\"path\":\"a\"}";
            item["status"]    = "completed";
            s.Append(Event("response.output_item.done",
                           new JsonObject { ["sequence_number"] = 7, ["output_index"] = i + 2, ["item"] = item }));
        }

        response["status"] = "completed";
        response["usage"] =
            JsonNode.Parse("""{"input_tokens":9,"output_tokens":5,"total_tokens":14,"input_tokens_details":{"cached_tokens":2},"output_tokens_details":{"reasoning_tokens":1}}""");
        s.Append(Event("response.completed", new JsonObject { ["sequence_number"] = 8, ["response"] = response }));
        return s.ToString();
    }

    /// <summary>
    ///     <para>纯文本终答一轮：无 reasoning、无工具调用。</para>
    ///     <para>A plain-text terminal round: no reasoning, no calls.</para>
    /// </summary>
    private static string TextSse(string text, int? cached = null, bool detailedUsage = false)
    {
        var s = new StringBuilder();
        var response =
            JsonNode.Parse("""{"id":"resp_2","object":"response","created_at":2,"status":"in_progress","model":"mock","output":[],"store":false}""")
                !.AsObject();
        s.Append(Event("response.created",
                       new JsonObject { ["sequence_number"] = 0, ["response"] = response.DeepClone() }));
        s.Append(Event("response.output_text.delta", new JsonObject
        {
            ["sequence_number"] = 1, ["item_id"] = "msg_1", ["output_index"] = 0, ["content_index"] = 0,
            ["delta"]           = text
        }));
        response["status"] = "completed";
        response["usage"]  = JsonNode.Parse("""{"input_tokens":9,"output_tokens":5,"total_tokens":14}""");
        if (cached is { } count)
            response["usage"]!["input_tokens_details"] = new JsonObject { ["cached_tokens"] = count };
        if (detailedUsage)
            response["usage"]!["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 3 };
        s.Append(Event("response.completed", new JsonObject { ["sequence_number"] = 2, ["response"] = response }));
        return s.ToString();
    }

    private static IReadOnlyList<ToolDefinition> ReadFileTool() => [new FakeTool("read_file").Definition];

    // ---- tests ---------------------------------------------------------------

    /// <summary>
    ///     <para>Responses 的缓存和推理 usage 按 SDK 子计数传递，缺失缓存不补零。</para>
    ///     <para>Responses cache and reasoning usage retain SDK subset counts without filling missing cache values with zero.</para>
    /// </summary>
    [TestCase(null)]
    [TestCase(0)]
    [TestCase(6)]
    public async Task ResponseUsageSubsetsRemainOptional(int? cached)
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueRaw(TextSse("done", cached, true));
        var events = await CollectAsync(NewClient(server), new ChatCompletionRequest
        {
            Model = Model, Messages = [ChatMessage.User("go")]
        });
        var end = events.Single(e => e.Kind == ChatStreamEventKind.End);
        Assert.That(end.CachedInputTokens, Is.EqualTo(cached));
        Assert.That(end.ReasoningTokens, Is.EqualTo(3));
    }

    /// <summary>
    ///     <para>Responses 的正文或完整工具条目仍须有 response.completed 才能接受。</para>
    ///     <para>Responses text or complete function items still require response.completed before acceptance.</para>
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task MissingResponseCompletedFailsBeforeTools(bool toolResponse)
    {
        using var server = new MockSseServer();
        server.Start();
        var body     = toolResponse ? ResponsesSse(1) : TextSse("partial");
        var terminal = body.IndexOf("event: response.completed", StringComparison.Ordinal);
        Assert.That(terminal, Is.GreaterThanOrEqualTo(0));
        server.EnqueueRaw(body[..terminal]);
        var tool = new FakeTool("read_file");
        var loop = new AgentLoop(NewClient(server), new ToolRegistry([tool]), new AgentOptions
        {
            Model = Model, MaxAgentSteps = 3, DefaultToolTimeoutSeconds = 30
        });

        var result = await loop.RunAsync("sys", "go", default);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(tool.ExecuteCount, Is.Zero);
        Assert.That(server.Requests.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task StreamingReasoningTextAndTools_AreAssembled()
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueRaw(ResponsesSse(2));
        var client = NewClient(server);

        var events = await CollectAsync(client,
                                        new ChatCompletionRequest
                                        {
                                            Model    = Model,
                                            Messages = [ChatMessage.User("read it")],
                                            Tools    = ReadFileTool()
                                        });

        // 文本增量逐条翻译并拼接为完整正文。
        // 文本增量逐条翻译并拼装成完整内容。
        Assert.That(string.Concat(events.Where(e => e.Kind == ChatStreamEventKind.ContentDelta)
                                        .Select(e => e.ContentDelta)), Is.EqualTo("Hello world"));

        // reasoning 增量与条目完成合并成恰一条带不透明载荷的条目。
        // reasoning 增量与 item-done 合并成恰好一个携带 opaque 载荷的条目。
        var reasoning = TestAssert.Single(Accumulate(events).Reasoning!);
        Assert.That(reasoning.Text, Is.EqualTo("thought"));
        Assert.That(reasoning.ProtectedData, Is.EqualTo("opaque"));
        Assert.That(reasoning.ItemId, Is.EqualTo("rs_1"));

        // 两个 function_call 分片被适配器拼装后以完整调用各到达一次，参数 JSON 合法。
        // 两个 function call 的分片参数由适配器拼装，各完整到达一次，参数 JSON 合法。
        var accumulator = Accumulate(events);
        Assert.That(accumulator.ToolCalls.Count, Is.EqualTo(2));
        for (var i = 0; i < 2; i++)
        {
            Assert.That(accumulator.ToolCalls[i].Id, Is.EqualTo($"call_{i}"));
            Assert.That(accumulator.ToolCalls[i].FunctionName, Is.EqualTo("read_file"));
            Assert.That(accumulator.ToolCalls[i].ArgumentsJson, Is.EqualTo("""{"path":"a"}"""));
        }

        var end = events.Single(e => e.Kind == ChatStreamEventKind.End);
        Assert.That(end.FinishReason, Is.EqualTo(ChatFinishReason.ToolCalls.ToString()));
        Assert.That(end.InputTokens, Is.EqualTo(9));
        Assert.That(end.OutputTokens, Is.EqualTo(5));

        var sent = TestAssert.Single(server.Requests);
        Assert.That(sent.Method, Is.EqualTo("POST"));
        Assert.That(sent.Path, Is.EqualTo("/v1/responses"));
    }

    [Test]
    public async Task SecondRound_IsStatelessAndReplaysReasoningAndToolOutputs()
    {
        using var server = new MockSseServer();
        server.Start();
        server.EnqueueRaw(ResponsesSse(2));
        server.EnqueueRaw(TextSse("done"));
        var client = NewClient(server);

        var first = Accumulate(await CollectAsync(client,
                                                  new ChatCompletionRequest
                                                  {
                                                      Model    = Model,
                                                      Messages = [ChatMessage.User("read it")],
                                                      Tools    = ReadFileTool()
                                                  }));

        // 重放 Agent 会构建的历史：assistant（文本 + reasoning + 两个工具调用）+ 两条工具结果。
        // 回放 agent 会构建的历史：assistant（文本、reasoning、两个工具调用）加两个工具结果。
        await CollectAsync(client, new ChatCompletionRequest
        {
            Model = Model,
            Messages =
            [
                ChatMessage.User("read it"),
                ChatMessage.Assistant("Hello world", first.ToolCalls, first.Reasoning),
                ChatMessage.Tool("read_file", "call_0", "read_file ok #0"),
                ChatMessage.Tool("read_file", "call_1", "read_file ok #1")
            ],
            Tools = ReadFileTool()
        });

        Assert.That(server.Requests.Count, Is.EqualTo(2));
        TestAssert.All(server.Requests, r => Assert.That(r.Path, Is.EqualTo("/v1/responses")));

        var second = JsonNode.Parse(server.Requests[1].Body)!.AsObject();
        Assert.That(second["model"]!.GetValue<string>(), Is.EqualTo(Model));

        // 无状态：不存储输出，也不依赖 previous_response_id 会话续接。
        // 无状态：不存储输出，也不使用 previous_response_id 会话延续。
        Assert.That(second["store"]!.GetValue<bool>(), Is.False);
        Assert.That(!second.ContainsKey("previous_response_id") || second["previous_response_id"] is null, Is.True);

        var input = second["input"]!.AsArray();

        // 恰一个 reasoning 条目，id 与加密载荷原样回传。
        // 恰好一个 reasoning item，其 id 与加密载荷原样回传。
        var reasoningItems = input.Where(i => i?["type"]?.GetValue<string>() == "reasoning").ToArray();
        var reasoningItem  = TestAssert.Single(reasoningItems)!;
        Assert.That(reasoningItem["id"]!.GetValue<string>(), Is.EqualTo("rs_1"));
        Assert.That(reasoningItem["encrypted_content"]!.GetValue<string>(), Is.EqualTo("opaque"));

        // 两条工具结果按 call_id 对应回传。
        // 两个工具输出按匹配的 call id 回传。
        var outputs = input.Where(i => i?["type"]?.GetValue<string>() == "function_call_output").ToArray();
        Assert.That(outputs.Length, Is.EqualTo(2));
        Assert.That(outputs.Select(o => o!["call_id"]!.GetValue<string>()).OrderBy(id => id).ToArray(),
                    Is.EqualTo(["call_0", "call_1"]));
    }

    [Test]
    public async Task TwoTurnAgentLoop_ReplaysReasoningInSecondRequest()
    {
        using var server = new MockSseServer();
        server.Start();

        // 第一轮：reasoning + 文本 + 一个工具调用；第二轮：纯文本终答。
        // 第 1 回合：reasoning、文本与一次工具调用；第 2 回合：纯文本回答。
        server.EnqueueRaw(ResponsesSse(1));
        server.EnqueueRaw(TextSse("The file read fine."));

        var client = NewClient(server);
        var tool   = new FakeTool("read_file");
        var loop = new AgentLoop(client, new ToolRegistry([tool]), new AgentOptions
        {
            Model                     = Model,
            MaxAgentSteps             = 10,
            DefaultToolTimeoutSeconds = 30
        });

        var result = await loop.RunAsync("sys", "inspect a.txt", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(result.FinalMessage, Is.EqualTo("The file read fine."));
        Assert.That(tool.ExecuteCount, Is.EqualTo(1));
        Assert.That(server.Requests.Count, Is.EqualTo(2));

        // reasoning 在 AgentLoop 历史中存活并在第二轮请求中重放。
        // reasoning 在 AgentLoop 历史中存活，并在第二个请求中回传。
        var second    = JsonNode.Parse(server.Requests[1].Body)!.AsObject();
        var input     = second["input"]!.AsArray();
        var reasoning = TestAssert.Single(input, i => i?["type"]?.GetValue<string>() == "reasoning")!;
        Assert.That(reasoning["id"]!.GetValue<string>(), Is.EqualTo("rs_1"));
        Assert.That(reasoning["encrypted_content"]!.GetValue<string>(), Is.EqualTo("opaque"));
        TestAssert.Single(input, i => i?["type"]?.GetValue<string>() == "function_call_output");
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
}

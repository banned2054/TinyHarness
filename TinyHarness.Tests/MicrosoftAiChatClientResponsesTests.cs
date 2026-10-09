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
///     Responses 协议传输契约测试：经 ModelClientFactory 以 ChatApiKind.Responses 构造的
///     MicrosoftAiChatClient 驱动真实 OpenAI/M.E.AI SDK 打本地脚本 SSE 服务，验证流式拼装、
///     reasoning 条目累积、无状态请求（store:false、无 previous_response_id）、reasoning 与工具
///     结果回传、AgentLoop 两轮闭环与错误语义——全部离线，fixture 形状照抄
///     artifacts/protocol-compat/ProviderProbe.cs 的 ResponsesSse。
///     Contract tests for the Responses protocol transport: the MicrosoftAiChatClient built by
///     ModelClientFactory with ChatApiKind.Responses drives the real OpenAI/M.E.AI SDK against a
///     local scripted SSE server, verifying stream assembly, reasoning entry accumulation,
///     stateless requests (store:false, no previous_response_id), reasoning and tool-result
///     replay, a two-turn AgentLoop, and error semantics — all offline, with fixtures shaped
///     after ResponsesSse in artifacts/protocol-compat/ProviderProbe.cs.
/// </summary>
public class MicrosoftAiChatClientResponsesTests
{
    private const string Model = "resp-model";

    private static IChatCompletionClient NewClient(MockSseServer server)
    {
        return ModelClientFactory.Create(Model, server.BaseUrl, "test-key", ChatApiKind.Responses);
    }

    private static ChatCompletionRequest Request(params ChatMessage[] messages)
    {
        return new ChatCompletionRequest
        {
            Model    = Model,
            Messages = messages
        };
    }

    private static async Task<List<ChatStreamEvent>> CollectAsync(IChatCompletionClient client,
                                                                  ChatCompletionRequest request,
                                                                  CancellationToken     cancellationToken = default)
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
    ///     构造一轮 Responses SSE：reasoning 摘要增量 + 条目完成（encrypted_content）、文本增量、
    ///     count 个 function_call 分片参数与完成、带 usage 的 response.completed。
    ///     Builds one Responses SSE round: a reasoning summary delta plus item done
    ///     (encrypted_content), text deltas, count function calls with split argument
    ///     fragments and done events, and a response.completed carrying usage.
    /// </summary>
    private static string ResponsesSse(int calls)
    {
        var s = new StringBuilder();
        var response = JsonNode.Parse(
                                      """{"id":"resp_1","object":"response","created_at":1,"status":"in_progress","model":"mock","output":[],"store":false}""")
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
        response["usage"] = JsonNode.Parse(
                                           """{"input_tokens":9,"output_tokens":5,"total_tokens":14,"input_tokens_details":{"cached_tokens":2},"output_tokens_details":{"reasoning_tokens":1}}""");
        s.Append(Event("response.completed", new JsonObject { ["sequence_number"] = 8, ["response"] = response }));
        return s.ToString();
    }

    /// <summary>纯文本终答一轮：无 reasoning、无工具调用。A plain-text terminal round: no reasoning, no calls.</summary>
    private static string TextSse(string text)
    {
        var s = new StringBuilder();
        var response = JsonNode.Parse(
                                      """{"id":"resp_2","object":"response","created_at":2,"status":"in_progress","model":"mock","output":[],"store":false}""")
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
        s.Append(Event("response.completed", new JsonObject { ["sequence_number"] = 2, ["response"] = response }));
        return s.ToString();
    }

    private static IReadOnlyList<ToolDefinition> ReadFileTool()
    {
        return [new FakeTool("read_file").Definition];
    }

    // ---- tests ---------------------------------------------------------------

    [Fact]
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
        // Text deltas are translated one-by-one and assemble into the full content.
        Assert.Equal("Hello world",
                     string.Concat(events.Where(e => e.Kind == ChatStreamEventKind.ContentDelta)
                                         .Select(e => e.ContentDelta)));

        // reasoning 增量与条目完成合并成恰一条带不透明载荷的条目。
        // The reasoning delta and item-done merge into exactly one entry with
        // the opaque payload.
        var reasoning = Assert.Single(Accumulate(events).Reasoning!);
        Assert.Equal("thought", reasoning.Text);
        Assert.Equal("opaque", reasoning.ProtectedData);
        Assert.Equal("rs_1", reasoning.ItemId);

        // 两个 function_call 分片被适配器拼装后以完整调用各到达一次，参数 JSON 合法。
        // The two function calls' split fragments are assembled by the adapter
        // and arrive once each, complete, with valid argument JSON.
        var accumulator = Accumulate(events);
        Assert.Equal(2, accumulator.ToolCalls.Count);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal($"call_{i}", accumulator.ToolCalls[i].Id);
            Assert.Equal("read_file", accumulator.ToolCalls[i].FunctionName);
            Assert.Equal("""{"path":"a"}""", accumulator.ToolCalls[i].ArgumentsJson);
        }

        var end = events.Single(e => e.Kind == ChatStreamEventKind.End);
        Assert.Equal(ChatFinishReason.ToolCalls.ToString(), end.FinishReason);
        Assert.Equal(9, end.InputTokens);
        Assert.Equal(5, end.OutputTokens);

        var sent = Assert.Single(server.Requests);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/v1/responses", sent.Path);
    }

    [Fact]
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
        // Replay the history an agent would build: the assistant (text,
        // reasoning, two tool calls) plus two tool results.
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

        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, r => Assert.Equal("/v1/responses", r.Path));

        var second = JsonNode.Parse(server.Requests[1].Body)!.AsObject();
        Assert.Equal(Model, second["model"]!.GetValue<string>());

        // 无状态：不存储输出，也不依赖 previous_response_id 会话续接。
        // Stateless: output is not stored and no previous_response_id session
        // continuation is used.
        Assert.False(second["store"]!.GetValue<bool>());
        Assert.True(!second.ContainsKey("previous_response_id") || second["previous_response_id"] is null);

        var input = second["input"]!.AsArray();

        // 恰一个 reasoning 条目，id 与加密载荷原样回传。
        // Exactly one reasoning item with its id and encrypted payload replayed
        // verbatim.
        var reasoningItems = input.Where(i => i?["type"]?.GetValue<string>() == "reasoning").ToArray();
        var reasoningItem  = Assert.Single(reasoningItems)!;
        Assert.Equal("rs_1", reasoningItem["id"]!.GetValue<string>());
        Assert.Equal("opaque", reasoningItem["encrypted_content"]!.GetValue<string>());

        // 两条工具结果按 call_id 对应回传。
        // The two tool outputs replay under their matching call ids.
        var outputs = input.Where(i => i?["type"]?.GetValue<string>() == "function_call_output").ToArray();
        Assert.Equal(2, outputs.Length);
        Assert.Equal(["call_0", "call_1"],
                     outputs.Select(o => o!["call_id"]!.GetValue<string>()).OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task TwoTurnAgentLoop_ReplaysReasoningInSecondRequest()
    {
        using var server = new MockSseServer();
        server.Start();

        // 第一轮：reasoning + 文本 + 一个工具调用；第二轮：纯文本终答。
        // Turn 1: reasoning, text, and one tool call; turn 2: a plain-text answer.
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

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal("The file read fine.", result.FinalMessage);
        Assert.Equal(1, tool.ExecuteCount);
        Assert.Equal(2, server.Requests.Count);

        // reasoning 在 AgentLoop 历史中存活并在第二轮请求中重放。
        // The reasoning survives in the AgentLoop history and is replayed in
        // the second request.
        var second    = JsonNode.Parse(server.Requests[1].Body)!.AsObject();
        var input     = second["input"]!.AsArray();
        var reasoning = Assert.Single(input, i => i?["type"]?.GetValue<string>() == "reasoning")!;
        Assert.Equal("rs_1", reasoning["id"]!.GetValue<string>());
        Assert.Equal("opaque", reasoning["encrypted_content"]!.GetValue<string>());
        Assert.Single(input, i => i?["type"]?.GetValue<string>() == "function_call_output");
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
}

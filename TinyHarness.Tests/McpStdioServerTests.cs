using System.Text.Json;
using TinyHarness.Core.Models.Mcp;
using TinyHarness.Core.Models.Worker;
using TinyHarness.Core.Services.Mcp;
using TinyHarness.Core.Services.Worker;

namespace TinyHarness.Tests;

/// <summary>
/// MCP stdio 协议与 ask_glm 闭环的离线测试。全部通过注入的 TextReader/TextWriter 和
/// fake worker 驱动，无进程、无网络。
///
/// Offline tests for MCP stdio and the ask_glm path, driven through injected readers/writers and
/// a fake worker — no processes and no network.
/// </summary>
public class McpStdioServerTests
{
    private const string Initialize2025 = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"codex","version":"1.0.0"}}}
        """;

    private const string InitializedNotification = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    // ---- handshake ---------------------------------------------------------

    [Fact]
    public async Task FullHandshake_AnswersInitializeAndToolsList_NotificationsStaySilent()
    {
        var responses = await RunAsync(Initialize2025, InitializedNotification,
                                       """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        Assert.Equal(2, responses.Count);

        var initialize = responses[0];
        Assert.Equal(1, initialize.GetProperty("id").GetInt32());
        var initializeResult = initialize.GetProperty("result");
        Assert.Equal(McpProtocolVersion.Latest, initializeResult.GetProperty("protocolVersion").GetString());
        Assert.True(initializeResult.TryGetProperty("capabilities", out var capabilities));
        Assert.True(capabilities.TryGetProperty("tools", out _));
        Assert.Equal("tinyharness", initializeResult.GetProperty("serverInfo").GetProperty("name").GetString());

        var tools = responses[1].GetProperty("result").GetProperty("tools").EnumerateArray();
        Assert.Equal("ask_glm", Assert.Single(tools).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Initialize_WithUnsupportedVersion_FallsBackToLatestSupported()
    {
        var responses = await RunAsync("""
                                       {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"1999-01-01","capabilities":{},"clientInfo":{"name":"codex","version":"1.0.0"}}}
                                       """);

        Assert.Equal(McpProtocolVersion.Latest,
                     responses[0].GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task Initialize_WithoutProtocolVersion_FallsBackToLatestSupported()
    {
        var responses = await RunAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");

        Assert.Equal(McpProtocolVersion.Latest,
                     responses[0].GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task Initialize_WithStringId_PreservesIdVerbatim()
    {
        var responses = await RunAsync("""
                                       {"jsonrpc":"2.0","id":"handshake-1","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"codex","version":"1.0.0"}}}
                                       """);

        Assert.Equal(JsonValueKind.String, responses[0].GetProperty("id").ValueKind);
        Assert.Equal("handshake-1", responses[0].GetProperty("id").GetString());
        Assert.True(responses[0].TryGetProperty("result", out _));
    }

    // ---- tools/list --------------------------------------------------------

    [Fact]
    public async Task ToolsList_PublishesFixedAskGlmSchema()
    {
        var responses = await RunAsync(Initialize2025, InitializedNotification,
                                       """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        Assert.Equal(2, responses.Count);
        var askGlm = Assert.Single(responses[1].GetProperty("result").GetProperty("tools").EnumerateArray());
        Assert.Equal("ask_glm", askGlm.GetProperty("name").GetString());
        Assert.Equal("Ask GLM", askGlm.GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(askGlm.GetProperty("description").GetString()));

        var schema = askGlm.GetProperty("inputSchema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var required = schema.GetProperty("required").EnumerateArray()
                             .Select(value => value.GetString()).ToList();
        Assert.Contains("task", required);

        var properties = schema.GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("task").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("expectedOutput").GetProperty("type").GetString());
        Assert.Equal("string",
                     properties.GetProperty("knownFacts").GetProperty("items").GetProperty("type").GetString());
        Assert.Equal("string",
                     properties.GetProperty("focusPaths").GetProperty("items").GetProperty("type").GetString());
    }

    // ---- lifecycle guardrails ----------------------------------------------

    [Fact]
    public async Task ToolsList_BeforeInitialize_IsRejectedAsNotInitialized()
    {
        var responses = await RunAsync("""{"jsonrpc":"2.0","id":5,"method":"tools/list"}""");

        Assert.Equal(-32002, responses[0].GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Ping_BeforeInitialize_IsAnsweredWithEmptyResult()
    {
        var responses = await RunAsync("""{"jsonrpc":"2.0","id":9,"method":"ping"}""");

        var response = Assert.Single(responses);
        Assert.Equal(9, response.GetProperty("id").GetInt32());
        Assert.Empty(response.GetProperty("result").EnumerateObject());
    }

    [Fact]
    public async Task ToolsCall_BeforeInitialize_IsRejectedAsNotInitialized()
    {
        var responses = await RunAsync(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"ask_glm","arguments":{"task":"test"}}}""");

        Assert.Single(responses);
        Assert.Equal(-32002, responses[0].GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ToolsCall_AskGlmReturnsBoundedStructuredWorkerJson()
    {
        WorkerRequest? received = null;
        var responses = await RunAsync(
            [Initialize2025,
             """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"ask_glm","arguments":{"task":"find retry handling","knownFacts":["retry count is configurable"],"focusPaths":["src"],"expectedOutput":"short"}}}"""],
            (request, _) =>
            {
                received = request;
                return Task.FromResult(new WorkerResult
                {
                    Status = WorkerResultStatus.Completed,
                    Conclusion = "Retry count is bounded in RetryPolicy.",
                    Evidence = [new WorkerEvidence { Path = "src/RetryPolicy.cs", LineStart = 18, LineEnd = 25, Note = "Applies the configured retry limit." }],
                });
            });

        Assert.Equal("find retry handling", received!.TaskPrompt);
        Assert.Equal(["retry count is configurable"], received.KnownFacts);
        Assert.Equal(["src"], received.FocusPaths);
        var call = responses[1].GetProperty("result");
        Assert.False(call.GetProperty("isError").GetBoolean());
        var block = Assert.Single(call.GetProperty("content").EnumerateArray());
        Assert.Equal("text", block.GetProperty("type").GetString());
        using var workerResult = JsonDocument.Parse(block.GetProperty("text").GetString()!);
        Assert.Equal("Completed", workerResult.RootElement.GetProperty("status").GetString());
        Assert.Equal("Retry count is bounded in RetryPolicy.",
                     workerResult.RootElement.GetProperty("conclusion").GetString());
        Assert.Equal("src/RetryPolicy.cs",
                     workerResult.RootElement.GetProperty("evidence")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task ToolsCall_IgnoresMcpRequestMetadata()
    {
        var invoked = false;
        var responses = await RunAsync(
            [Initialize2025,
             """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"ask_glm","arguments":{"task":"inspect the worker budget"},"_meta":{"progressToken":7}}}"""],
            (_, _) =>
            {
                invoked = true;
                return Task.FromResult(new WorkerResult
                {
                    Status = WorkerResultStatus.Completed,
                    Conclusion = "The worker returned a bounded result.",
                });
            });

        Assert.True(invoked);
        Assert.False(responses[1].GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task OfflineMcpRequest_RunsFakeWorkerThroughFileToolsAndReturnsEvidence()
    {
        using var dir = new TestTempDir();
        var filePath = dir.WriteFile("src/RetryPolicy.cs", "public sealed class RetryPolicy { public int RetryBudget = 3; }");
        var fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.ToolCall("list_files", """{"path":"src"}""", "mcp-list"));
        fake.Enqueue(FakeChatClient.ToolCall("search_text", """{"pattern":"RetryBudget","path":"src"}""", "mcp-search"));
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"src/RetryPolicy.cs"}""", "mcp-read"));
        fake.Enqueue(FakeChatClient.Text("""
            {"conclusion":"RetryPolicy declares a fixed retry budget of 3.","evidence":[{"path":"src/RetryPolicy.cs","lineStart":1,"lineEnd":1,"note":"The field is initialized to 3."}],"suggestedChanges":[],"testSuggestions":[],"uncertainties":[]}
            """));
        var options = new WorkerExecutionOptions
        {
            Model = "fake-mcp-model",
            RunTimeout = TimeSpan.FromSeconds(15),
            MaxAgentSteps = 6,
            DefaultToolTimeoutSeconds = 10,
            MaxTaskPackageCharacters = 8_000,
            MaxToolCalls = 12,
            MaxToolOutputCharacters = 100_000,
            MaxContextTokensPerRequest = 200_000,
            MaxCumulativeContextTokens = 200_000,
            MaxModelResponseCharacters = 64_000,
        };
        var runner = new WorkerRunner(fake, dir.Root, options);
        var responses = await RunAsync(
            [Initialize2025,
             """{"jsonrpc":"2.0","id":13,"method":"tools/call","params":{"name":"ask_glm","arguments":{"task":"Find and explain the retry budget.","focusPaths":["src"]}}}"""],
            runner.RunAsync);

        Assert.Equal(4, fake.Requests);
        Assert.Equal("public sealed class RetryPolicy { public int RetryBudget = 3; }",
                     await File.ReadAllTextAsync(filePath));
        var call = responses[1].GetProperty("result");
        Assert.False(call.GetProperty("isError").GetBoolean());
        var text = call.GetProperty("content")[0].GetProperty("text").GetString()!;
        using var workerResult = JsonDocument.Parse(text);
        var result = workerResult.RootElement;
        Assert.Equal("Completed", result.GetProperty("status").GetString());
        Assert.Equal("RetryPolicy declares a fixed retry budget of 3.", result.GetProperty("conclusion").GetString());
        Assert.Equal("src/RetryPolicy.cs", result.GetProperty("evidence")[0].GetProperty("path").GetString());
        Assert.Equal(3, result.GetProperty("statistics").GetProperty("toolCalls").GetInt32());
        Assert.Equal(4, result.GetProperty("statistics").GetProperty("modelRequests").GetInt32());
        Assert.DoesNotContain("public sealed class", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolsCall_RejectsUnknownArgumentsWithoutCallingWorker()
    {
        var invoked = false;
        var responses = await RunAsync(
            [Initialize2025,
             """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"ask_glm","arguments":{"task":"read a file","workspaceRoot":"C:/outside"}}}"""],
            (_, _) =>
            {
                invoked = true;
                return Task.FromResult(new WorkerResult { Status = WorkerResultStatus.Completed, Conclusion = "should not run" });
            });

        Assert.False(invoked);
        Assert.Equal(-32602, responses[1].GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ToolsCall_ValidationFailureAndWorkerExceptionAreBoundedAndWithheld()
    {
        var invoked = 0;
        var tooLongTask = new string('x', WorkerRequestLimits.MaxTaskPromptLength + 1);
        var responses = await RunAsync(
            [Initialize2025,
             "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"ask_glm\",\"arguments\":{\"task\":\"" + tooLongTask + "\"}}}",
             """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"ask_glm","arguments":{"task":"model failure case"}}}"""],
            (_, _) =>
            {
                invoked++;
                throw new InvalidOperationException("sk-live-MCP-SENTINEL internal response fragment");
            });

        Assert.Equal(1, invoked);
        var invalid = Assert.Single(responses, response => response.GetProperty("id").GetInt32() == 5)
                      .GetProperty("result");
        Assert.True(invalid.GetProperty("isError").GetBoolean());
        var invalidText = invalid.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.DoesNotContain(tooLongTask, invalidText, StringComparison.Ordinal);

        var failed = Assert.Single(responses, response => response.GetProperty("id").GetInt32() == 6)
                     .GetProperty("result");
        Assert.True(failed.GetProperty("isError").GetBoolean());
        var failedText = failed.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.DoesNotContain("sk-live-MCP-SENTINEL", failedText, StringComparison.Ordinal);
        using var resultDocument = JsonDocument.Parse(failedText);
        Assert.Equal("Failed", resultDocument.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ToolsCall_CancelledNotificationCancelsActiveWorker()
    {
        var input = new ChannelTextReader();
        using var output = new StringWriter();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new McpStdioServer(input, output, async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new WorkerResult { Status = WorkerResultStatus.Completed, Conclusion = "unreachable" };
        });

        var serverTask = server.RunAsync(CancellationToken.None);
        await input.SendLineAsync(Initialize2025);
        await input.SendLineAsync("""{"jsonrpc":"2.0","id":42,"method":"tools/call","params":{"name":"ask_glm","arguments":{"task":"inspect cancellation"}}}""");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await input.SendLineAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":42,"reason":"caller stopped"}}""");
        input.Complete();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

        var responses = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(ToJsonElement).ToArray();
        var call = Assert.Single(responses, response => response.TryGetProperty("id", out var id) &&
                                                        id.ValueKind == JsonValueKind.Number && id.GetInt32() == 42)
                   .GetProperty("result");
        Assert.True(call.GetProperty("isError").GetBoolean());
        using var result = JsonDocument.Parse(call.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal("Cancelled", result.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task UnknownNotification_ProducesNoResponseAndServerKeepsWorking()
    {
        var responses = await RunAsync("""{"jsonrpc":"2.0","method":"notifications/unknown"}""",
                                       """{"jsonrpc":"2.0","id":6,"method":"ping"}""");

        var response = Assert.Single(responses);
        Assert.Equal(6, response.GetProperty("id").GetInt32());
    }

    // ---- invalid requests --------------------------------------------------

    [Fact]
    public async Task MalformedJson_ReturnsParseErrorWithNullId()
    {
        var responses = await RunAsync("this is not json");

        Assert.Equal(-32700, responses[0].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, responses[0].GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task WrongJsonRpcVersion_ReturnsInvalidRequestWithEchoedId()
    {
        var responses = await RunAsync("""{"jsonrpc":"1.0","id":7,"method":"tools/list"}""");

        Assert.Equal(-32600, responses[0].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(7, responses[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task MissingMethod_ReturnsInvalidRequest()
    {
        var responses = await RunAsync("""{"jsonrpc":"2.0","id":3}""");

        Assert.Equal(-32600, responses[0].GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task BatchArray_ReturnsInvalidRequest()
    {
        // 2025-06-18 移除了 JSON-RPC 批处理；数组不是合法的单条消息。
        var responses = await RunAsync("""[{"jsonrpc":"2.0","id":1,"method":"ping"}]""");

        Assert.Equal(-32600, responses[0].GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task OversizedInputLine_IsRejectedWithoutParsingOrEchoingContent()
    {
        var sentinel = "input-sentinel-" + new string('x', 70_000);
        var responses = await RunAsync(sentinel);

        var response = Assert.Single(responses);
        Assert.Equal(-32600, response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.DoesNotContain("input-sentinel", response.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolsCall_UnknownToolNameFailsWithoutCallingWorker()
    {
        var invoked = false;
        var responses = await RunAsync(
            [Initialize2025,
             """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"shell","arguments":{"task":"run command"}}}"""],
            (_, _) =>
            {
                invoked = true;
                return Task.FromResult(new WorkerResult { Status = WorkerResultStatus.Completed, Conclusion = "unexpected" });
            });

        Assert.False(invoked);
        var call = responses[1].GetProperty("result");
        Assert.True(call.GetProperty("isError").GetBoolean());
        using var workerResult = JsonDocument.Parse(call.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal("Failed", workerResult.RootElement.GetProperty("status").GetString());
    }

    // ---- helpers -----------------------------------------------------------

    private static Task<IReadOnlyList<JsonElement>> RunAsync(params string[] messages) =>
        RunAsync((IReadOnlyList<string>)messages, null);

    private static async Task<IReadOnlyList<JsonElement>> RunAsync(
        IReadOnlyList<string> messages,
        Func<WorkerRequest, CancellationToken, Task<WorkerResult>>? worker)
    {
        using var input  = new StringReader(string.Join('\n', messages) + "\n");
        using var output = new StringWriter();
        await new McpStdioServer(input, output, worker).RunAsync(CancellationToken.None);

        return output.ToString()
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(ToJsonElement)
                     .ToList();
    }

    private sealed class ChannelTextReader : TextReader
    {
        private readonly System.Threading.Channels.Channel<char> _characters =
            System.Threading.Channels.Channel.CreateUnbounded<char>();

        public async Task SendLineAsync(string line)
        {
            foreach (var character in line + "\n")
                await _characters.Writer.WriteAsync(character);
        }

        public void Complete() => _characters.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0) return 0;
            if (!await _characters.Reader.WaitToReadAsync(cancellationToken)) return 0;

            var count = 0;
            while (count < buffer.Length && _characters.Reader.TryRead(out var character))
                buffer.Span[count++] = character;
            return count;
        }
    }

    private static JsonElement ToJsonElement(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }
}

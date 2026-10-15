using System.Text.Json;
using System.Threading.Channels;
using TinyHarness.Core.Models.Mcp;
using TinyHarness.Core.Models.Worker;
using TinyHarness.Core.Services.Mcp;
using TinyHarness.Core.Services.Worker;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         MCP stdio 协议与 ask_glm 闭环的离线测试。全部通过注入的 TextReader/TextWriter 和
///         fake worker 驱动，无进程、无网络。
///     </para>
///     <para>
///         Offline tests for MCP stdio and the ask_glm path, driven through injected readers/writers and
///         a fake worker — no processes and no network.
///     </para>
/// </summary>
public class McpStdioServerTests
{
    private const string Initialize2025 = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"codex","version":"1.0.0"}}}
        """;

    private const string InitializedNotification = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    // ---- handshake ---------------------------------------------------------

    [Test]
    public async Task FullHandshake_AnswersInitializeAndToolsList_NotificationsStaySilent()
    {
        var responses = await RunAsync(Initialize2025, InitializedNotification,
                                       """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        Assert.That(responses.Count, Is.EqualTo(2));

        var initialize = responses[0];
        Assert.That(initialize.GetProperty("id").GetInt32(), Is.EqualTo(1));
        var initializeResult = initialize.GetProperty("result");
        Assert.That(initializeResult.GetProperty("protocolVersion").GetString(), Is.EqualTo(McpProtocolVersion.Latest));
        Assert.That(initializeResult.TryGetProperty("capabilities", out var capabilities), Is.True);
        Assert.That(capabilities.TryGetProperty("tools", out _), Is.True);
        Assert.That(initializeResult.GetProperty("serverInfo").GetProperty("name").GetString(),
                    Is.EqualTo("tinyharness"));

        var tools = responses[1].GetProperty("result").GetProperty("tools").EnumerateArray();
        Assert.That(TestAssert.Single(tools).GetProperty("name").GetString(), Is.EqualTo("ask_glm"));
    }

    [Test]
    public async Task Initialize_WithUnsupportedVersion_FallsBackToLatestSupported()
    {
        var responses = await RunAsync("""
                                       {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"1999-01-01","capabilities":{},"clientInfo":{"name":"codex","version":"1.0.0"}}}
                                       """);

        Assert.That(responses[0].GetProperty("result").GetProperty("protocolVersion").GetString(),
                    Is.EqualTo(McpProtocolVersion.Latest));
    }

    [Test]
    public async Task Initialize_WithoutProtocolVersion_FallsBackToLatestSupported()
    {
        var responses = await RunAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");

        Assert.That(responses[0].GetProperty("result").GetProperty("protocolVersion").GetString(),
                    Is.EqualTo(McpProtocolVersion.Latest));
    }

    [Test]
    public async Task Initialize_WithStringId_PreservesIdVerbatim()
    {
        var responses = await RunAsync("""
                                       {"jsonrpc":"2.0","id":"handshake-1","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"codex","version":"1.0.0"}}}
                                       """);

        Assert.That(responses[0].GetProperty("id").ValueKind, Is.EqualTo(JsonValueKind.String));
        Assert.That(responses[0].GetProperty("id").GetString(), Is.EqualTo("handshake-1"));
        Assert.That(responses[0].TryGetProperty("result", out _), Is.True);
    }

    // ---- tools/list --------------------------------------------------------

    [Test]
    public async Task ToolsList_PublishesFixedAskGlmSchema()
    {
        var responses = await RunAsync(Initialize2025, InitializedNotification,
                                       """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        Assert.That(responses.Count, Is.EqualTo(2));
        var askGlm = TestAssert.Single(responses[1].GetProperty("result").GetProperty("tools").EnumerateArray());
        Assert.That(askGlm.GetProperty("name").GetString(), Is.EqualTo("ask_glm"));
        Assert.That(askGlm.GetProperty("title").GetString(), Is.EqualTo("Ask GLM"));
        Assert.That(string.IsNullOrWhiteSpace(askGlm.GetProperty("description").GetString()), Is.False);

        var schema = askGlm.GetProperty("inputSchema");
        Assert.That(schema.GetProperty("type").GetString(), Is.EqualTo("object"));
        Assert.That(schema.GetProperty("additionalProperties").GetBoolean(), Is.False);
        var required = schema.GetProperty("required").EnumerateArray()
                             .Select(value => value.GetString()).ToList();
        Assert.That(required, Does.Contain("task"));

        var properties = schema.GetProperty("properties");
        Assert.That(properties.GetProperty("task").GetProperty("type").GetString(), Is.EqualTo("string"));
        Assert.That(properties.GetProperty("expectedOutput").GetProperty("type").GetString(), Is.EqualTo("string"));
        Assert.That(properties.GetProperty("knownFacts").GetProperty("items").GetProperty("type").GetString(),
                    Is.EqualTo("string"));
        Assert.That(properties.GetProperty("focusPaths").GetProperty("items").GetProperty("type").GetString(),
                    Is.EqualTo("string"));
    }

    // ---- lifecycle guardrails ----------------------------------------------

    [Test]
    public async Task ToolsList_BeforeInitialize_IsRejectedAsNotInitialized()
    {
        var responses = await RunAsync("""{"jsonrpc":"2.0","id":5,"method":"tools/list"}""");

        Assert.That(responses[0].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32002));
    }

    [Test]
    public async Task Ping_BeforeInitialize_IsAnsweredWithEmptyResult()
    {
        var responses = await RunAsync("""{"jsonrpc":"2.0","id":9,"method":"ping"}""");

        var response = TestAssert.Single(responses);
        Assert.That(response.GetProperty("id").GetInt32(), Is.EqualTo(9));
        Assert.That(response.GetProperty("result").EnumerateObject(), Is.Empty);
    }

    [Test]
    public async Task ToolsCall_BeforeInitialize_IsRejectedAsNotInitialized()
    {
        var responses = await RunAsync(
                                       """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"ask_glm","arguments":{"task":"test"}}}""");

        TestAssert.Single(responses);
        Assert.That(responses[0].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32002));
    }

    [Test]
    public async Task ToolsCall_AskGlmReturnsBoundedStructuredWorkerJson()
    {
        WorkerRequest? received = null;
        var responses = await RunAsync(
                                       [
                                           Initialize2025,
                                           """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"ask_glm","arguments":{"task":"find retry handling","knownFacts":["retry count is configurable"],"focusPaths":["src"],"expectedOutput":"short"}}}"""
                                       ],
                                       (request, _) =>
                                       {
                                           received = request;
                                           return Task.FromResult(new WorkerResult
                                           {
                                               Status     = WorkerResultStatus.Completed,
                                               Conclusion = "Retry count is bounded in RetryPolicy.",
                                               Evidence =
                                               [
                                                   new WorkerEvidence
                                                   {
                                                       Path = "src/RetryPolicy.cs", LineStart = 18, LineEnd = 25,
                                                       Note = "Applies the configured retry limit."
                                                   }
                                               ]
                                           });
                                       });

        Assert.That(received!.TaskPrompt, Is.EqualTo("find retry handling"));
        Assert.That(received.KnownFacts, Is.EqualTo(["retry count is configurable"]));
        Assert.That(received.FocusPaths, Is.EqualTo(["src"]));
        var call = responses[1].GetProperty("result");
        Assert.That(call.GetProperty("isError").GetBoolean(), Is.False);
        var block = TestAssert.Single(call.GetProperty("content").EnumerateArray());
        Assert.That(block.GetProperty("type").GetString(), Is.EqualTo("text"));
        using var workerResult = JsonDocument.Parse(block.GetProperty("text").GetString()!);
        Assert.That(workerResult.RootElement.GetProperty("status").GetString(), Is.EqualTo("Completed"));
        Assert.That(workerResult.RootElement.GetProperty("conclusion").GetString(),
                    Is.EqualTo("Retry count is bounded in RetryPolicy."));
        Assert.That(workerResult.RootElement.GetProperty("evidence")[0].GetProperty("path").GetString(),
                    Is.EqualTo("src/RetryPolicy.cs"));
    }

    [Test]
    public async Task ToolsCall_IgnoresMcpRequestMetadata()
    {
        var invoked = false;
        var responses = await RunAsync(
                                       [
                                           Initialize2025,
                                           """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"ask_glm","arguments":{"task":"inspect the worker budget"},"_meta":{"progressToken":7}}}"""
                                       ],
                                       (_, _) =>
                                       {
                                           invoked = true;
                                           return Task.FromResult(new WorkerResult
                                           {
                                               Status     = WorkerResultStatus.Completed,
                                               Conclusion = "The worker returned a bounded result."
                                           });
                                       });

        Assert.That(invoked, Is.True);
        Assert.That(responses[1].GetProperty("result").GetProperty("isError").GetBoolean(), Is.False);
    }

    [Test]
    public async Task OfflineMcpRequest_RunsFakeWorkerThroughFileToolsAndReturnsEvidence()
    {
        using var dir = new TestTempDir();
        var filePath = dir.WriteFile("src/RetryPolicy.cs",
                                     "public sealed class RetryPolicy { public int RetryBudget = 3; }");
        var fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.ToolCall("list_files", """{"path":"src"}""", "mcp-list"));
        fake.Enqueue(FakeChatClient.ToolCall("search_text", """{"pattern":"RetryBudget","path":"src"}""",
                                             "mcp-search"));
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"src/RetryPolicy.cs"}""", "mcp-read"));
        fake.Enqueue(FakeChatClient.Text("""
                                         {"conclusion":"RetryPolicy declares a fixed retry budget of 3.","evidence":[{"path":"src/RetryPolicy.cs","lineStart":1,"lineEnd":1,"note":"The field is initialized to 3."}],"suggestedChanges":[],"testSuggestions":[],"uncertainties":[]}
                                         """));
        var options = new WorkerExecutionOptions
        {
            Model                      = "fake-mcp-model",
            RunTimeout                 = TimeSpan.FromSeconds(15),
            MaxAgentSteps              = 6,
            DefaultToolTimeoutSeconds  = 10,
            MaxTaskPackageCharacters   = 8_000,
            MaxToolCalls               = 12,
            MaxToolOutputCharacters    = 100_000,
            MaxContextTokensPerRequest = 200_000,
            MaxCumulativeContextTokens = 200_000,
            MaxModelResponseCharacters = 64_000
        };
        var runner = new WorkerRunner(fake, dir.Root, options);
        var responses = await RunAsync(
                                       [
                                           Initialize2025,
                                           """{"jsonrpc":"2.0","id":13,"method":"tools/call","params":{"name":"ask_glm","arguments":{"task":"Find and explain the retry budget.","focusPaths":["src"]}}}"""
                                       ],
                                       runner.RunAsync);

        Assert.That(fake.Requests, Is.EqualTo(4));
        Assert.That(await File.ReadAllTextAsync(filePath),
                    Is.EqualTo("public sealed class RetryPolicy { public int RetryBudget = 3; }"));
        var call = responses[1].GetProperty("result");
        Assert.That(call.GetProperty("isError").GetBoolean(), Is.False);
        var       text         = call.GetProperty("content")[0].GetProperty("text").GetString()!;
        using var workerResult = JsonDocument.Parse(text);
        var       result       = workerResult.RootElement;
        Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("Completed"));
        Assert.That(result.GetProperty("conclusion").GetString(),
                    Is.EqualTo("RetryPolicy declares a fixed retry budget of 3."));
        Assert.That(result.GetProperty("evidence")[0].GetProperty("path").GetString(),
                    Is.EqualTo("src/RetryPolicy.cs"));
        Assert.That(result.GetProperty("statistics").GetProperty("toolCalls").GetInt32(), Is.EqualTo(3));
        Assert.That(result.GetProperty("statistics").GetProperty("modelRequests").GetInt32(), Is.EqualTo(4));
        TestAssert.DoesNotContain("public sealed class", text, StringComparison.Ordinal);
    }

    [Test]
    public async Task ToolsCall_RejectsUnknownArgumentsWithoutCallingWorker()
    {
        var invoked = false;
        var responses = await RunAsync(
                                       [
                                           Initialize2025,
                                           """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"ask_glm","arguments":{"task":"read a file","workspaceRoot":"C:/outside"}}}"""
                                       ],
                                       (_, _) =>
                                       {
                                           invoked = true;
                                           return Task.FromResult(new WorkerResult
                                           {
                                               Status = WorkerResultStatus.Completed, Conclusion = "should not run"
                                           });
                                       });

        Assert.That(invoked, Is.False);
        Assert.That(responses[1].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32602));
    }

    [Test]
    public async Task ToolsCall_ValidationFailureAndWorkerExceptionAreBoundedAndWithheld()
    {
        var invoked     = 0;
        var tooLongTask = new string('x', WorkerRequestLimits.MaxTaskPromptLength + 1);
        var responses = await RunAsync(
                                       [
                                           Initialize2025,
                                           "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"ask_glm\",\"arguments\":{\"task\":\"" +
                                           tooLongTask + "\"}}}",
                                           """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"ask_glm","arguments":{"task":"model failure case"}}}"""
                                       ],
                                       (_, _) =>
                                       {
                                           invoked++;
                                           throw new
                                               InvalidOperationException("sk-live-MCP-SENTINEL internal response fragment");
                                       });

        Assert.That(invoked, Is.EqualTo(1));
        var invalid = TestAssert.Single(responses, response => response.GetProperty("id").GetInt32() == 5)
                                .GetProperty("result");
        Assert.That(invalid.GetProperty("isError").GetBoolean(), Is.True);
        var invalidText = invalid.GetProperty("content")[0].GetProperty("text").GetString()!;
        TestAssert.DoesNotContain(tooLongTask, invalidText, StringComparison.Ordinal);

        var failed = TestAssert.Single(responses, response => response.GetProperty("id").GetInt32() == 6)
                               .GetProperty("result");
        Assert.That(failed.GetProperty("isError").GetBoolean(), Is.True);
        var failedText = failed.GetProperty("content")[0].GetProperty("text").GetString()!;
        TestAssert.DoesNotContain("sk-live-MCP-SENTINEL", failedText, StringComparison.Ordinal);
        using var resultDocument = JsonDocument.Parse(failedText);
        Assert.That(resultDocument.RootElement.GetProperty("status").GetString(), Is.EqualTo("Failed"));
    }

    [Test]
    public async Task ToolsCall_CancelledNotificationCancelsActiveWorker()
    {
        var       input   = new ChannelTextReader();
        using var output  = new StringWriter();
        var       started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new McpStdioServer(input, output, async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new WorkerResult { Status = WorkerResultStatus.Completed, Conclusion = "unreachable" };
        });

        var serverTask = server.RunAsync(CancellationToken.None);
        await input.SendLineAsync(Initialize2025);
        await
            input.SendLineAsync("""{"jsonrpc":"2.0","id":42,"method":"tools/call","params":{"name":"ask_glm","arguments":{"task":"inspect cancellation"}}}""");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await
            input.SendLineAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":42,"reason":"caller stopped"}}""");
        input.Complete();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

        var responses = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(ToJsonElement)
                              .ToArray();
        var call = TestAssert.Single(responses, response => response.TryGetProperty("id", out var id) &&
                                                            id.ValueKind == JsonValueKind.Number && id.GetInt32() == 42)
                             .GetProperty("result");
        Assert.That(call.GetProperty("isError").GetBoolean(), Is.True);
        using var result = JsonDocument.Parse(call.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.That(result.RootElement.GetProperty("status").GetString(), Is.EqualTo("Cancelled"));
    }

    [Test]
    public async Task UnknownNotification_ProducesNoResponseAndServerKeepsWorking()
    {
        var responses = await RunAsync("""{"jsonrpc":"2.0","method":"notifications/unknown"}""",
                                       """{"jsonrpc":"2.0","id":6,"method":"ping"}""");

        var response = TestAssert.Single(responses);
        Assert.That(response.GetProperty("id").GetInt32(), Is.EqualTo(6));
    }

    // ---- invalid requests --------------------------------------------------

    [Test]
    public async Task MalformedJson_ReturnsParseErrorWithNullId()
    {
        var responses = await RunAsync("this is not json");

        Assert.That(responses[0].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32700));
        Assert.That(responses[0].GetProperty("id").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    [Test]
    public async Task WrongJsonRpcVersion_ReturnsInvalidRequestWithEchoedId()
    {
        var responses = await RunAsync("""{"jsonrpc":"1.0","id":7,"method":"tools/list"}""");

        Assert.That(responses[0].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32600));
        Assert.That(responses[0].GetProperty("id").GetInt32(), Is.EqualTo(7));
    }

    [Test]
    public async Task MissingMethod_ReturnsInvalidRequest()
    {
        var responses = await RunAsync("""{"jsonrpc":"2.0","id":3}""");

        Assert.That(responses[0].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32600));
    }

    [Test]
    public async Task BatchArray_ReturnsInvalidRequest()
    {
        // 2025-06-18 移除了 JSON-RPC 批处理；数组不是合法的单条消息。
        var responses = await RunAsync("""[{"jsonrpc":"2.0","id":1,"method":"ping"}]""");

        Assert.That(responses[0].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32600));
    }

    [Test]
    public async Task OversizedInputLine_IsRejectedWithoutParsingOrEchoingContent()
    {
        var sentinel  = "input-sentinel-" + new string('x', 70_000);
        var responses = await RunAsync(sentinel);

        var response = TestAssert.Single(responses);
        Assert.That(response.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32600));
        TestAssert.DoesNotContain("input-sentinel", response.GetRawText(), StringComparison.Ordinal);
    }

    [Test]
    public async Task ToolsCall_UnknownToolNameFailsWithoutCallingWorker()
    {
        var invoked = false;
        var responses = await RunAsync(
                                       [
                                           Initialize2025,
                                           """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"shell","arguments":{"task":"run command"}}}"""
                                       ],
                                       (_, _) =>
                                       {
                                           invoked = true;
                                           return Task.FromResult(new WorkerResult
                                           {
                                               Status = WorkerResultStatus.Completed, Conclusion = "unexpected"
                                           });
                                       });

        Assert.That(invoked, Is.False);
        var call = responses[1].GetProperty("result");
        Assert.That(call.GetProperty("isError").GetBoolean(), Is.True);
        using var workerResult = JsonDocument.Parse(call.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.That(workerResult.RootElement.GetProperty("status").GetString(), Is.EqualTo("Failed"));
    }

    // ---- helpers -----------------------------------------------------------

    private static Task<IReadOnlyList<JsonElement>> RunAsync(params string[] messages) => RunAsync(messages, null);

    private static async Task<IReadOnlyList<JsonElement>> RunAsync(
        IReadOnlyList<string>                                       messages,
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

    private static JsonElement ToJsonElement(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    private sealed class ChannelTextReader : TextReader
    {
        private readonly Channel<char> _characters =
            Channel.CreateUnbounded<char>();

        public async Task SendLineAsync(string line)
        {
            foreach (var character in line + "\n")
                await _characters.Writer.WriteAsync(character);
        }

        public void Complete()
        {
            _characters.Writer.TryComplete();
        }

        public override async ValueTask<int> ReadAsync(Memory<char>      buffer,
                                                       CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0) return 0;
            if (!await _characters.Reader.WaitToReadAsync(cancellationToken)) return 0;

            var count = 0;
            while (count < buffer.Length && _characters.Reader.TryRead(out var character))
                buffer.Span[count++] = character;
            return count;
        }
    }
}

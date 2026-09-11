using System.Text.Json;
using TinyHarness.Core.Agent;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Context;
using TinyHarness.Core.Permissions;
using TinyHarness.Core.Persistence;
using TinyHarness.Core.Tools;

namespace TinyHarness.Tests;

public sealed class RunPersistenceTests
{
    [Fact]
    public async Task FileRecorderWritesJsonlAuditAndCompleteSessionSnapshot()
    {
        using var temp     = new TestTempDir();
        var       recorder = new FileRunRecorder(temp.Root, "run-1");
        var preparation = new ToolPreparation
        {
            ToolName  = "read_file", CallId              = "call-1", Capability = "filesystem.read",
            Summary   = "Read file 'a.txt'", TargetPaths = [Path.Combine(temp.Root, "a.txt")],
            Arguments = new System.Text.Json.Nodes.JsonObject(),
        };
        var result      = new ToolResult { Succeeded = true, Content                       = "contents" };
        var agentResult = new AgentResult { Status   = AgentStatus.Completed, FinalMessage = "done", Steps = 1 };

        await recorder.StartAsync("system", "inspect", CancellationToken.None);
        await recorder.RecordPreparedAsync(preparation, CancellationToken.None);
        await recorder.RecordPermissionAsync(preparation, Core.Permissions.PermissionDecision.Allow,
                                             "allow", CancellationToken.None);
        await recorder.RecordResultAsync(preparation, result, CancellationToken.None);
        await recorder.RecordCompactionAsync(new ContextChange(100, 50), CancellationToken.None);
        await recorder.CompleteAsync(agentResult, [ChatMessage.System("system"), ChatMessage.User("inspect")],
                                     new StructuredState { Goal = "inspect" }, CancellationToken.None);

        Assert.True(File.Exists(recorder.AuditPath));
        Assert.True(File.Exists(recorder.SessionPath));
        var auditLines = await File.ReadAllLinesAsync(recorder.AuditPath);
        Assert.Equal(6, auditLines.Length);
        var session = await File.ReadAllTextAsync(recorder.SessionPath);
        Assert.Contains("\"runId\":\"run-1\"", session, StringComparison.Ordinal);
        Assert.Contains("\"goal\":\"inspect\"", session, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"Completed\"", session, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinalMessageKnownSecretIsRedactedFromEntireSession()
    {
        const string secret = "synthetic-final-secret-42";
        using var temp = new TestTempDir();
        var recorder = new FileRunRecorder(temp.Root, "final-secret",
            new Dictionary<string, string> { ["TEST_KEY"] = secret });

        await recorder.CompleteAsync(
            new AgentResult { Status = AgentStatus.Completed, FinalMessage = $"answer {secret}" },
            [ChatMessage.Assistant($"answer {secret}")], StructuredState.Empty, CancellationToken.None);

        var raw = await File.ReadAllTextAsync(recorder.SessionPath);
        var snapshot = DeserializeSession(raw);
        Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
        Assert.Equal("answer [REDACTED]", snapshot.Result.FinalMessage);
    }

    [Fact]
    public async Task RunErrorKnownSecretIsRedactedFromSessionAndCompletedAudit()
    {
        const string secret = "synthetic-error-secret-42";
        using var temp = new TestTempDir();
        var recorder = new FileRunRecorder(temp.Root, "error-secret",
            new Dictionary<string, string> { ["TEST_KEY"] = secret });

        await recorder.CompleteAsync(
            new AgentResult { Status = AgentStatus.Failed, Error = $"failure {secret}" },
            [ChatMessage.User("run")], StructuredState.Empty, CancellationToken.None);

        var session = await File.ReadAllTextAsync(recorder.SessionPath);
        var audit = await File.ReadAllTextAsync(recorder.AuditPath);
        Assert.DoesNotContain(secret, session, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, audit, StringComparison.Ordinal);
        Assert.Equal("failure [REDACTED]", DeserializeSession(session).Result.Error);
        Assert.Contains("\"outcome\":\"failure [REDACTED]\"", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnownSecretsAreRedactedFromMessagesToolCallsSummariesPathsAndState()
    {
        const string shorterSecret = "overlap-secret";
        const string longerSecret  = "overlap-secret-value";
        using var temp = new TestTempDir();
        var recorder = new FileRunRecorder(temp.Root, "all-fields", new Dictionary<string, string>
        {
            ["SHORT_KEY"] = shorterSecret, ["LONG_KEY"] = longerSecret,
        });
        var preparation = new ToolPreparation
        {
            ToolName = "read_file", CallId = "call-1", Capability = "filesystem.read",
            Summary = $"read {longerSecret}", TargetPaths = [$"C:\\fixture\\{longerSecret}.txt"],
            Arguments = new System.Text.Json.Nodes.JsonObject(),
        };
        var messages = new[]
        {
            ChatMessage.User($"message {longerSecret}"),
            ChatMessage.Assistant("tool", [new ChatToolCall("call-1", "read_file",
                $$"""{"path":"{{longerSecret}}"}""")]),
        };
        var state = new StructuredState
        {
            Goal = $"goal {longerSecret}", Constraints = [$"constraint {longerSecret}"],
            Decisions = [$"decision {longerSecret}"], FilesInspected = [$"inspected {longerSecret}"],
            FilesModified = [$"modified {longerSecret}"], CommandsAndResults = [$"command {longerSecret}"],
            PendingWork = [$"pending {longerSecret}"],
        };

        await recorder.StartAsync($"system {longerSecret}", $"input {longerSecret}", CancellationToken.None);
        await recorder.RecordPreparedAsync(preparation, CancellationToken.None);
        await recorder.RecordPermissionAsync(preparation, PermissionDecision.Allow,
                                              $"allowed {longerSecret}", CancellationToken.None);
        await recorder.CompleteAsync(new AgentResult { Status = AgentStatus.Completed }, messages, state,
                                     CancellationToken.None);

        var persisted = await File.ReadAllTextAsync(recorder.AuditPath)
                      + await File.ReadAllTextAsync(recorder.SessionPath);
        Assert.DoesNotContain(longerSecret, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(shorterSecret, persisted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SensitiveJsonFieldsStayParseableAndNullableMessageFieldsStayNull()
    {
        using var temp = new TestTempDir();
        var recorder = new FileRunRecorder(temp.Root, "structured-redaction");
        var arguments = """{"password":"synthetic-password","nested":{"api_key":"synthetic-key"},"optional":null}""";
        var message = ChatMessage.Assistant("{}", [new ChatToolCall("call-1", "shell", arguments)]);

        await recorder.CompleteAsync(new AgentResult { Status = AgentStatus.Completed }, [message],
                                     StructuredState.Empty, CancellationToken.None);

        var snapshot = DeserializeSession(await File.ReadAllTextAsync(recorder.SessionPath));
        var persistedMessage = Assert.Single(snapshot.Messages);
        Assert.Null(persistedMessage.Name);
        Assert.Null(persistedMessage.ToolCallId);
        Assert.Null(snapshot.Result.Error);
        var persistedCall = Assert.Single(persistedMessage.ToolCalls!);
        using var document = JsonDocument.Parse(persistedCall.ArgumentsJson);
        Assert.Equal("[REDACTED]", document.RootElement.GetProperty("password").GetString());
        Assert.Equal("[REDACTED]", document.RootElement.GetProperty("nested").GetProperty("api_key").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("optional").ValueKind);
    }

    [Fact]
    public async Task RecorderCompletionIOExceptionPreservesRunFailureAndAttemptsFinalizationOnce()
    {
        var client   = new FakeChatClient();
        var recorder = new ThrowingRecorder { ThrowOnComplete = true };
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), permissions : null, approver : null,
                                 recorder);

        var result = await loop.RunAsync("sys", "run", CancellationToken.None);

        Assert.Equal(AgentStatus.Failed, result.Status);
        Assert.Equal(1, result.Steps);
        Assert.Equal(0, result.ToolExecutions);
        Assert.Contains("No scripted response", result.Error, StringComparison.Ordinal);
        Assert.Contains("persistence failed: injected completion failure", result.Error, StringComparison.Ordinal);
        Assert.Equal(1, recorder.CompleteCalls);
    }

    [Fact]
    public async Task ResultAuditIOExceptionKeepsToolResultInMemoryAndDoesNotReplayTool()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("side_effect", "{}"));
        var tool = new FakeTool("side_effect");
        var recorder = new ThrowingRecorder { ThrowOnResult = true };
        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options(), permissions : null, approver : null,
                                 recorder);

        var result = await loop.RunAsync("sys", "run", CancellationToken.None);

        Assert.Equal(AgentStatus.Failed, result.Status);
        Assert.Equal(1, result.Steps);
        Assert.Equal(1, result.ToolExecutions);
        Assert.Equal(1, tool.ExecuteCount);
        Assert.Equal(1, client.Requests);
        Assert.Equal(1, recorder.ResultCalls);
        Assert.Equal(1, recorder.CompleteCalls);
        Assert.Contains(loop.History, message =>
            message is { Role: ChatRole.Tool, Content: "side_effect ok" });
    }

    [Theory]
    [InlineData("{\"value\":\"synthetic-\\u0073ecret\"}", "synthetic-secret", "{\"value\":\"[REDACTED]\"}")]
    [InlineData("[null,42,true,{\"items\":[\"prefix synthetic-\\u0073ecret suffix\"]}]", "synthetic-secret",
                "[null,42,true,{\"items\":[\"prefix [REDACTED] suffix\"]}]")]
    [InlineData("\"synthetic-\\u0073ecret\"", "synthetic-secret", "\"[REDACTED]\"")]
    [InlineData("{\"value\":\"synthetic-\\\"secret\"}", "synthetic-\"secret", "{\"value\":\"[REDACTED]\"}")]
    [InlineData("{\"value\":\"synthetic-\\\\secret\"}", "synthetic-\\secret", "{\"value\":\"[REDACTED]\"}")]
    [InlineData("{\"value\":\"synthetic-\\/secret\"}", "synthetic-/secret", "{\"value\":\"[REDACTED]\"}")]
    [InlineData("{\"synthetic-\\u0073ecret\":\"safe\"}", "synthetic-secret", "{\"[REDACTED]\":\"safe\"}")]
    public async Task EscapedKnownSecretsAreRedactedAfterJsonDecoding(string input, string secret, string expected)
    {
        using var temp = new TestTempDir();
        var recorder = new FileRunRecorder(temp.Root, "escaped-json",
            new Dictionary<string, string> { ["TEST_KEY"] = secret });
        var message = ChatMessage.Assistant(input, [new ChatToolCall("call-1", "shell", input)]);
        var result = new AgentResult { Status = AgentStatus.Failed, FinalMessage = input, Error = input };
        var state = new StructuredState { Goal = input };

        await recorder.StartAsync(input, input, CancellationToken.None);
        await recorder.CompleteAsync(result, [message], state, CancellationToken.None);

        var snapshot = DeserializeSession(await File.ReadAllTextAsync(recorder.SessionPath));
        var persistedMessage = Assert.Single(snapshot.Messages);
        var persistedCall = Assert.Single(persistedMessage.ToolCalls!);
        var audit = (await File.ReadAllLinesAsync(recorder.AuditPath))
           .Select(line => JsonSerializer.Deserialize(line, PersistenceJsonContext.Default.AuditRecord)!).ToArray();
        using var expectedJson = JsonDocument.Parse(expected);
        foreach (var value in new[]
                 {
                     snapshot.Result.FinalMessage, snapshot.Result.Error!, persistedMessage.Content,
                     persistedCall.ArgumentsJson, snapshot.State.Goal, audit[0].SystemPrompt!,
                     audit[0].UserInput!, audit[1].Outcome!,
                 })
        {
            using var actualJson = JsonDocument.Parse(value);
            Assert.True(JsonElement.DeepEquals(expectedJson.RootElement, actualJson.RootElement), value);
        }

        Assert.Equal(input, message.Content);
        Assert.Equal(input, message.ToolCalls![0].ArgumentsJson);
        Assert.Equal(input, result.Error);
        Assert.Equal(input, state.Goal);
    }

    private static AgentOptions Options() => new()
    {
        Model = "test-model", MaxAgentSteps = 10, DefaultToolTimeoutSeconds = 30,
    };

    private static SessionSnapshot DeserializeSession(string json) =>
        JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.SessionSnapshot)
     ?? throw new InvalidDataException("Session snapshot was null.");

    private sealed class ThrowingRecorder : IRunRecorder
    {
        public bool ThrowOnComplete { get; init; }
        public bool ThrowOnResult { get; init; }
        public int CompleteCalls { get; private set; }
        public int ResultCalls { get; private set; }

        public Task StartAsync(string systemPrompt, string userInput, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordPreparedAsync(ToolPreparation preparation, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordPermissionAsync(ToolPreparation preparation, PermissionDecision decision, string outcome,
                                          CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordResultAsync(ToolPreparation preparation, ToolResult result,
                                      CancellationToken cancellationToken)
        {
            ResultCalls++;
            return ThrowOnResult
                ? Task.FromException(new IOException("injected result audit failure"))
                : Task.CompletedTask;
        }

        public Task RecordCompactionAsync(ContextChange change, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CompleteAsync(AgentResult result, IReadOnlyList<ChatMessage> messages, StructuredState state,
                                  CancellationToken cancellationToken)
        {
            CompleteCalls++;
            return ThrowOnComplete
                ? Task.FromException(new IOException("injected completion failure"))
                : Task.CompletedTask;
        }
    }
}

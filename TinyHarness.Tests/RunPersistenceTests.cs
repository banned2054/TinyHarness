using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Tools;
using PersistenceJsonContext = TinyHarness.Core.Services.Persistence.PersistenceJsonContext;

namespace TinyHarness.Tests;

public sealed class RunPersistenceTests
{
    [Test]
    public async Task FileRecorderWritesJsonlAuditAndCompleteSessionSnapshot()
    {
        using var temp     = new TestTempDir();
        var       recorder = new FileRunRecorder(temp.Root, "run-1");
        var preparation = new ToolPreparation
        {
            ToolName    = "read_file",
            CallId      = "call-1",
            Capability  = "filesystem.read",
            Summary     = "Read file 'a.txt'",
            TargetPaths = [Path.Combine(temp.Root, "a.txt")],
            Arguments   = new JsonObject()
        };
        var result      = new ToolResult { Succeeded = true, Content                       = "contents" };
        var agentResult = new AgentResult { Status   = AgentStatus.Completed, FinalMessage = "done", Steps = 1 };

        await recorder.StartAsync("system", "inspect", CancellationToken.None);
        await recorder.RecordPreparedAsync(preparation, CancellationToken.None);
        await recorder.RecordPermissionAsync(preparation, PermissionDecision.Allow,
                                             "allow", CancellationToken.None);
        await recorder.RecordResultAsync(preparation, result, CancellationToken.None);
        await recorder.RecordCompactionAsync(new ContextChange(100, 50), CancellationToken.None);
        await recorder.CompleteAsync(agentResult, [ChatMessage.System("system"), ChatMessage.User("inspect")],
                                     new StructuredState { Goal = "inspect" }, CancellationToken.None);

        Assert.That(File.Exists(recorder.AuditPath), Is.True);
        Assert.That(File.Exists(recorder.SessionPath), Is.True);
        var auditLines = await File.ReadAllLinesAsync(recorder.AuditPath);
        Assert.That(auditLines.Length, Is.EqualTo(6));
        var session = await File.ReadAllTextAsync(recorder.SessionPath);
        TestAssert.Contains("\"runId\":\"run-1\"", session, StringComparison.Ordinal);
        TestAssert.Contains("\"goal\":\"inspect\"", session, StringComparison.Ordinal);
        TestAssert.Contains("\"status\":\"Completed\"", session, StringComparison.Ordinal);
    }

    [Test]
    public async Task FinalMessageKnownSecretIsRedactedFromEntireSession()
    {
        const string secret = "synthetic-final-secret-42";
        using var    temp   = new TestTempDir();
        var recorder = new FileRunRecorder(temp.Root, "final-secret",
                                           new Dictionary<string, string> { ["TEST_KEY"] = secret });

        await recorder.CompleteAsync(new AgentResult
                                         { Status = AgentStatus.Completed, FinalMessage = $"answer {secret}" },
                                     [ChatMessage.Assistant($"answer {secret}")], StructuredState.Empty,
                                     CancellationToken.None);

        var raw      = await File.ReadAllTextAsync(recorder.SessionPath);
        var snapshot = DeserializeSession(raw);
        TestAssert.DoesNotContain(secret, raw, StringComparison.Ordinal);
        Assert.That(snapshot.Result.FinalMessage, Is.EqualTo("answer [REDACTED]"));
    }

    [Test]
    public async Task RunErrorKnownSecretIsRedactedFromSessionAndCompletedAudit()
    {
        const string secret = "synthetic-error-secret-42";
        using var    temp   = new TestTempDir();
        var recorder = new FileRunRecorder(temp.Root, "error-secret",
                                           new Dictionary<string, string> { ["TEST_KEY"] = secret });

        await recorder.CompleteAsync(new AgentResult { Status = AgentStatus.Failed, Error = $"failure {secret}" },
                                     [ChatMessage.User("run")], StructuredState.Empty, CancellationToken.None);

        var session = await File.ReadAllTextAsync(recorder.SessionPath);
        var audit   = await File.ReadAllTextAsync(recorder.AuditPath);
        TestAssert.DoesNotContain(secret, session, StringComparison.Ordinal);
        TestAssert.DoesNotContain(secret, audit, StringComparison.Ordinal);
        Assert.That(DeserializeSession(session).Result.Error, Is.EqualTo("failure [REDACTED]"));
        TestAssert.Contains("\"outcome\":\"failure [REDACTED]\"", audit, StringComparison.Ordinal);
    }

    [Test]
    public async Task KnownSecretsAreRedactedFromMessagesToolCallsSummariesPathsAndState()
    {
        const string shorterSecret = "overlap-secret";
        const string longerSecret  = "overlap-secret-value";
        using var    temp          = new TestTempDir();
        var recorder = new FileRunRecorder(temp.Root, "all-fields", new Dictionary<string, string>
        {
            ["SHORT_KEY"] = shorterSecret,
            ["LONG_KEY"]  = longerSecret
        });
        var preparation = new ToolPreparation
        {
            ToolName    = "read_file",
            CallId      = "call-1",
            Capability  = "filesystem.read",
            Summary     = $"read {longerSecret}",
            TargetPaths = [$"C:\\fixture\\{longerSecret}.txt"],
            Arguments   = new JsonObject()
        };
        var messages = new[]
        {
            ChatMessage.User($"message {longerSecret}"),
            ChatMessage.Assistant("tool", [
                new ChatToolCall("call-1", "read_file",
                                 $$"""{"path":"{{longerSecret}}"}""")
            ])
        };
        var state = new StructuredState
        {
            Goal               = $"goal {longerSecret}",
            Constraints        = [$"constraint {longerSecret}"],
            Decisions          = [$"decision {longerSecret}"],
            FilesInspected     = [$"inspected {longerSecret}"],
            FilesModified      = [$"modified {longerSecret}"],
            CommandsAndResults = [$"command {longerSecret}"],
            PendingWork        = [$"pending {longerSecret}"]
        };

        await recorder.StartAsync($"system {longerSecret}", $"input {longerSecret}", CancellationToken.None);
        await recorder.RecordPreparedAsync(preparation, CancellationToken.None);
        await recorder.RecordPermissionAsync(preparation, PermissionDecision.Allow,
                                             $"allowed {longerSecret}", CancellationToken.None);
        await recorder.CompleteAsync(new AgentResult { Status = AgentStatus.Completed }, messages, state,
                                     CancellationToken.None);

        var persisted = await File.ReadAllTextAsync(recorder.AuditPath)
                      + await File.ReadAllTextAsync(recorder.SessionPath);
        TestAssert.DoesNotContain(longerSecret, persisted, StringComparison.Ordinal);
        TestAssert.DoesNotContain(shorterSecret, persisted, StringComparison.Ordinal);
        TestAssert.Contains("[REDACTED]", persisted, StringComparison.Ordinal);
    }

    [Test]
    public async Task SensitiveJsonFieldsStayParseableAndNullableMessageFieldsStayNull()
    {
        using var temp = new TestTempDir();
        var recorder = new FileRunRecorder(temp.Root, "structured-redaction");
        var arguments = """{"password":"synthetic-password","nested":{"api_key":"synthetic-key"},"optional":null}""";
        var message = ChatMessage.Assistant("{}", [new ChatToolCall("call-1", "shell", arguments)]);

        await recorder.CompleteAsync(new AgentResult { Status = AgentStatus.Completed }, [message],
                                     StructuredState.Empty, CancellationToken.None);

        var snapshot         = DeserializeSession(await File.ReadAllTextAsync(recorder.SessionPath));
        var persistedMessage = TestAssert.Single(snapshot.Messages);
        Assert.That(persistedMessage.Name, Is.Null);
        Assert.That(persistedMessage.ToolCallId, Is.Null);
        Assert.That(snapshot.Result.Error, Is.Null);
        var       persistedCall = TestAssert.Single(persistedMessage.ToolCalls!);
        using var document      = JsonDocument.Parse(persistedCall.ArgumentsJson);
        Assert.That(document.RootElement.GetProperty("password").GetString(), Is.EqualTo("[REDACTED]"));
        Assert.That(document.RootElement.GetProperty("nested").GetProperty("api_key").GetString(),
                    Is.EqualTo("[REDACTED]"));
        Assert.That(document.RootElement.GetProperty("optional").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    [Test]
    public async Task ReasoningContentSurvivesSessionSnapshotRoundTrip()
    {
        using var temp     = new TestTempDir();
        var       recorder = new FileRunRecorder(temp.Root, "reasoning-roundtrip");
        var message = ChatMessage.Assistant("done",
                                            null,
                                            [new ReasoningContent("thinking", "opaque-payload", "rs_1")]);

        await recorder.CompleteAsync(new AgentResult { Status = AgentStatus.Completed }, [message],
                                     StructuredState.Empty, CancellationToken.None);

        var persistedMessage = TestAssert.Single(DeserializeSession(await File.ReadAllTextAsync(recorder.SessionPath))
                                                    .Messages);
        var reasoning = TestAssert.Single(persistedMessage.Reasoning!);
        Assert.That(reasoning.Text, Is.EqualTo("thinking"));
        Assert.That(reasoning.ProtectedData, Is.EqualTo("opaque-payload"));
        Assert.That(reasoning.ItemId, Is.EqualTo("rs_1"));
    }

    [Test]
    public async Task ReasoningRedactsTextAndKnownSecretsButKeepsOpaqueIdentifiers()
    {
        const string secret = "synthetic-reasoning-secret-42";
        using var    temp   = new TestTempDir();
        var recorder = new FileRunRecorder(temp.Root, "reasoning-redaction",
                                           new Dictionary<string, string> { ["TEST_KEY"] = secret });
        var message = ChatMessage.Assistant("done",
                                            null,
                                            [
                                                new ReasoningContent($"reasoned about {secret}", secret, secret),
                                                new ReasoningContent("plain", "opaque-keep", "rs_2")
                                            ]);

        await recorder.CompleteAsync(new AgentResult { Status = AgentStatus.Completed }, [message],
                                     StructuredState.Empty, CancellationToken.None);

        var raw = await File.ReadAllTextAsync(recorder.SessionPath);
        TestAssert.DoesNotContain(secret, raw, StringComparison.Ordinal);
        var persisted = TestAssert.Single(DeserializeSession(raw).Messages);
        Assert.That(persisted.Reasoning!.Count, Is.EqualTo(2));
        // 文本与命中已知 secret 的字段被替换；其余不透明标识原样保留。
        // 命中已知密钥的文本与字段被替换；其余 opaque 标识符按原样保留。
        Assert.That(persisted.Reasoning[0].Text, Is.EqualTo("reasoned about [REDACTED]"));
        Assert.That(persisted.Reasoning[0].ProtectedData, Is.EqualTo("[REDACTED]"));
        Assert.That(persisted.Reasoning[0].ItemId, Is.EqualTo("[REDACTED]"));
        Assert.That(persisted.Reasoning[1].Text, Is.EqualTo("plain"));
        Assert.That(persisted.Reasoning[1].ProtectedData, Is.EqualTo("opaque-keep"));
        Assert.That(persisted.Reasoning[1].ItemId, Is.EqualTo("rs_2"));
    }

    [Test]
    public async Task RecorderCompletionIOExceptionPreservesRunFailureAndAttemptsFinalizationOnce()
    {
        var client   = new FakeChatClient();
        var recorder = new ThrowingRecorder { ThrowOnComplete = true };
        var loop = new AgentLoop(client, new ToolRegistry([]), Options(), null, null,
                                 recorder);

        var result = await loop.RunAsync("sys", "run", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(result.Steps, Is.EqualTo(1));
        Assert.That(result.ToolExecutions, Is.EqualTo(0));
        TestAssert.Contains("No scripted response", result.Error, StringComparison.Ordinal);
        TestAssert.Contains("persistence failed: injected completion failure", result.Error, StringComparison.Ordinal);
        Assert.That(recorder.CompleteCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task ResultAuditIoExceptionKeepsToolResultInMemoryAndDoesNotReplayTool()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("side_effect", "{}"));
        var tool     = new FakeTool("side_effect");
        var recorder = new ThrowingRecorder { ThrowOnResult = true };
        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options(), null, null,
                                 recorder);

        var result = await loop.RunAsync("sys", "run", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(result.Steps, Is.EqualTo(1));
        Assert.That(result.ToolExecutions, Is.EqualTo(1));
        Assert.That(tool.ExecuteCount, Is.EqualTo(1));
        Assert.That(client.Requests, Is.EqualTo(1));
        Assert.That(recorder.ResultCalls, Is.EqualTo(1));
        Assert.That(recorder.CompleteCalls, Is.EqualTo(1));
        TestAssert.ContainsAll(message => message is { Role: ChatRole.Tool, Content: "side_effect ok" }, loop.History);
    }


    [TestCase("{\"value\":\"synthetic-\\u0073ecret\"}", "synthetic-secret", "{\"value\":\"[REDACTED]\"}")]
    [TestCase("[null,42,true,{\"items\":[\"prefix synthetic-\\u0073ecret suffix\"]}]", "synthetic-secret",
              "[null,42,true,{\"items\":[\"prefix [REDACTED] suffix\"]}]")]
    [TestCase("\"synthetic-\\u0073ecret\"", "synthetic-secret", "\"[REDACTED]\"")]
    [TestCase("{\"value\":\"synthetic-\\\"secret\"}", "synthetic-\"secret", "{\"value\":\"[REDACTED]\"}")]
    [TestCase("{\"value\":\"synthetic-\\\\secret\"}", "synthetic-\\secret", "{\"value\":\"[REDACTED]\"}")]
    [TestCase("{\"value\":\"synthetic-\\/secret\"}", "synthetic-/secret", "{\"value\":\"[REDACTED]\"}")]
    [TestCase("{\"synthetic-\\u0073ecret\":\"safe\"}", "synthetic-secret", "{\"[REDACTED]\":\"safe\"}")]
    public async Task EscapedKnownSecretsAreRedactedAfterJsonDecoding(string input, string secret, string expected)
    {
        using var temp = new TestTempDir();
        var recorder = new FileRunRecorder(temp.Root, "escaped-json",
                                           new Dictionary<string, string> { ["TEST_KEY"] = secret });
        var message = ChatMessage.Assistant(input, [new ChatToolCall("call-1", "shell", input)]);
        var result  = new AgentResult { Status   = AgentStatus.Failed, FinalMessage = input, Error = input };
        var state   = new StructuredState { Goal = input };

        await recorder.StartAsync(input, input, CancellationToken.None);
        await recorder.CompleteAsync(result, [message], state, CancellationToken.None);

        var snapshot         = DeserializeSession(await File.ReadAllTextAsync(recorder.SessionPath));
        var persistedMessage = TestAssert.Single(snapshot.Messages);
        var persistedCall    = TestAssert.Single(persistedMessage.ToolCalls!);
        var audit = (await File.ReadAllLinesAsync(recorder.AuditPath))
                   .Select(line => JsonSerializer.Deserialize(line, PersistenceJsonContext.Default.AuditRecord)!)
                   .ToArray();
        using var expectedJson = JsonDocument.Parse(expected);
        foreach (var value in new[]
                 {
                     snapshot.Result.FinalMessage, snapshot.Result.Error!, persistedMessage.Content,
                     persistedCall.ArgumentsJson, snapshot.State.Goal, audit[0].SystemPrompt!,
                     audit[0].UserInput!, audit[1].Outcome!
                 })
        {
            using var actualJson = JsonDocument.Parse(value);
            Assert.That(JsonElement.DeepEquals(expectedJson.RootElement, actualJson.RootElement), Is.True, value);
        }

        Assert.That(message.Content, Is.EqualTo(input));
        Assert.That(message.ToolCalls![0].ArgumentsJson, Is.EqualTo(input));
        Assert.That(result.Error, Is.EqualTo(input));
        Assert.That(state.Goal, Is.EqualTo(input));
    }

    [Test]
    public async Task ModelUsageIsAppendedAsCamelCaseAuditJsonl()
    {
        using var temp     = new TestTempDir();
        var       recorder = new FileRunRecorder(temp.Root, "usage-1");

        await recorder.RecordModelUsageAsync(120, 45, "stop", CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(recorder.AuditPath);
        TestAssert.Contains("\"kind\":\"model_usage\"", lines[0], StringComparison.Ordinal);
        TestAssert.Contains("\"inputTokens\":120", lines[0], StringComparison.Ordinal);
        var usage = TestAssert.Single(lines.Select(line =>
                                                       JsonSerializer.Deserialize(line,
                                                                PersistenceJsonContext.Default
                                                                   .AuditRecord)!));
        Assert.That(usage.RunId, Is.EqualTo("usage-1"));
        Assert.That(usage.Kind, Is.EqualTo("model_usage"));
        Assert.That(usage.InputTokens, Is.EqualTo(120));
        Assert.That(usage.OutputTokens, Is.EqualTo(45));
        Assert.That(usage.FinishReason, Is.EqualTo("stop"));
    }

    private static AgentOptions Options() => new()
    {
        Model                     = "test-model",
        MaxAgentSteps             = 10,
        DefaultToolTimeoutSeconds = 30
    };

    private static SessionSnapshot DeserializeSession(string json) =>
        JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.SessionSnapshot)
     ?? throw new InvalidDataException("Session snapshot was null.");

    private sealed class ThrowingRecorder : IRunRecorder
    {
        public bool ThrowOnComplete { get; init; }
        public bool ThrowOnResult   { get; init; }
        public int  CompleteCalls   { get; private set; }
        public int  ResultCalls     { get; private set; }

        public Task StartAsync(string systemPrompt, string userInput, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordPreparedAsync(ToolPreparation preparation, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordPermissionAsync(ToolPreparation   preparation, PermissionDecision decision, string outcome,
                                          CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordResultAsync(
            ToolPreparation preparation, ToolResult result, CancellationToken cancellationToken)
        {
            ResultCalls++;
            return ThrowOnResult
                ? Task.FromException(new IOException("injected result audit failure"))
                : Task.CompletedTask;
        }

        public Task RecordCompactionAsync(ContextChange change, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordModelUsageAsync(
            int? inputTokens, int? outputTokens, string? finishReason, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CompleteAsync(AgentResult       result, IReadOnlyList<ChatMessage> messages, StructuredState state,
                                  CancellationToken cancellationToken)
        {
            CompleteCalls++;
            return ThrowOnComplete
                ? Task.FromException(new IOException("injected completion failure"))
                : Task.CompletedTask;
        }
    }
}

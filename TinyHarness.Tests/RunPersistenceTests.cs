using System.Text.Json;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public sealed class RunPersistenceTests
{
    [Test]
    public void SecretRedactorRedactsKnownSecretsFromMessagesToolsAndStateWithoutMutation()
    {
        const string secret   = "synthetic-secret-value";
        var          redactor = new SecretRedactor(new Dictionary<string, string> { ["TEST_KEY"] = secret });
        var message = ChatMessage.Assistant("answer " + secret,
                                            [new ChatToolCall("call", "shell", $$"""{"password":"{{secret}}"}""")]);
        var summary = "handoff " + secret;

        var redactedMessage = redactor.Redact(message);
        var redactedSummary = redactor.Redact(summary);

        Assert.That(redactedMessage.Content, Is.EqualTo("answer [REDACTED]"));
        using var arguments = JsonDocument.Parse(redactedMessage.ToolCalls![0].ArgumentsJson);
        Assert.That(arguments.RootElement.GetProperty("password").GetString(), Is.EqualTo("[REDACTED]"));
        Assert.That(redactedSummary, Is.EqualTo("handoff [REDACTED]"));
        Assert.That(message.Content, Is.EqualTo("answer " + secret));
        Assert.That(summary, Is.EqualTo("handoff "        + secret));
    }

    [Test]
    public void SecretRedactorRedactsTextItemTextAndMetadataWithoutMutatingCanonicalItems()
    {
        const string secret   = "synthetic-secret";
        var          redactor = new SecretRedactor(new Dictionary<string, string> { ["TEST_KEY"] = secret });
        var message = ChatMessage.Assistant("answer " + secret + " tail") with
        {
            TextItems =
            [
                new AssistantTextItem("answer " + secret, "msg-" + secret, "future-" + secret),
                new AssistantTextItem(" tail", null, "future-phase")
            ]
        };

        var redacted = redactor.Redact(message);

        Assert.That(redacted.TextItems, Is.EqualTo(new AssistantTextItem[]
        {
            new("answer [REDACTED]", "msg-[REDACTED]", "future-[REDACTED]"),
            new(" tail", null, "future-phase")
        }));
        Assert.That(redacted.Content, Is.EqualTo(string.Concat(redacted.TextItems!.Select(item => item.Text))));
        Assert.That(message.Content, Is.EqualTo("answer " + secret + " tail"));
        Assert.That(message.TextItems![0], Is.EqualTo(new AssistantTextItem("answer " + secret, "msg-" + secret,
                                                                            "future-" + secret)));
    }

    [TestCase("synthetic-", "secret", "synthetic-secret")]
    [TestCase("{\"token\":", "\"hidden\"}", "unrelated")]
    public void SecretRedactorHidesTextWhenCrossItemRedactionCannotPreserveBoundaries(
        string first, string second, string secret)
    {
        var redactor = new SecretRedactor(new Dictionary<string, string> { ["TEST_KEY"] = secret });
        var message = ChatMessage.Assistant(first + second) with
        {
            TextItems = [new AssistantTextItem(first, "msg-1", "commentary"), new AssistantTextItem(second)]
        };

        var redacted = redactor.Redact(message);

        Assert.That(redacted.TextItems!.Select(item => item.Text), Is.EqualTo(new[] { "[REDACTED]", "[REDACTED]" }));
        Assert.That(redacted.Content, Is.EqualTo(string.Concat(redacted.TextItems!.Select(item => item.Text))));
        Assert.That(redacted.TextItems![0].MessageId, Is.EqualTo("msg-1"));
        Assert.That(redacted.TextItems![0].Phase, Is.EqualTo("commentary"));
        Assert.That(message.TextItems![0].Text, Is.EqualTo(first));
        Assert.That(message.TextItems[1].Text, Is.EqualTo(second));
    }

    [TestCase("{\"value\":\"synthetic-\\u0073ecret\"}", "synthetic-secret", "{\"value\":\"[REDACTED]\"}")]
    [TestCase("[null,42,true,{\"items\":[\"prefix synthetic-\\u0073ecret suffix\"]}]", "synthetic-secret",
              "[null,42,true,{\"items\":[\"prefix [REDACTED] suffix\"]}]")]
    [TestCase("\"synthetic-\\u0073ecret\"", "synthetic-secret", "\"[REDACTED]\"")]
    [TestCase("{\"value\":\"synthetic-\\\"secret\"}", "synthetic-\"secret", "{\"value\":\"[REDACTED]\"}")]
    [TestCase("{\"value\":\"synthetic-\\\\secret\"}", "synthetic-\\secret", "{\"value\":\"[REDACTED]\"}")]
    [TestCase("{\"value\":\"synthetic-\\/secret\"}", "synthetic-/secret", "{\"value\":\"[REDACTED]\"}")]
    [TestCase("{\"synthetic-\\u0073ecret\":\"safe\"}", "synthetic-secret", "{\"[REDACTED]\":\"safe\"}")]
    public void SecretRedactorHandlesEscapedSecretsInJson(string input, string secret, string expected)
    {
        var redactor = new SecretRedactor(new Dictionary<string, string> { ["TEST_KEY"] = secret });

        var redacted = redactor.Redact(input);

        using var actualDocument   = JsonDocument.Parse(redacted);
        using var expectedDocument = JsonDocument.Parse(expected);
        Assert.That(JsonElement.DeepEquals(actualDocument.RootElement, expectedDocument.RootElement), Is.True);
    }

    [Test]
    public async Task RecorderCompletionIOExceptionPreservesExecutionFailureAndReportsPersistenceWarning()
    {
        var recorder = new ThrowingRecorder { ThrowOnComplete = true };
        var loop     = new AgentLoop(new FakeChatClient(), new ToolRegistry([]), Options(), null, null, recorder);

        var result = await loop.RunAsync("sys", "run", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(result.Steps, Is.EqualTo(1));
        Assert.That(result.ToolExecutions, Is.Zero);
        TestAssert.Contains("No scripted response", result.Error, StringComparison.Ordinal);
        Assert.That(result.Error, Does.Not.Contain("persistence failed"));
        Assert.That(result.PersistenceWarning, Is.EqualTo("injected completion failure"));
        Assert.That(recorder.CompleteCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task ResultAuditIOExceptionPreservesUnknownResultAndStopsLaterTools()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("side_effect", "{}"));
        client.Enqueue(FakeChatClient.ToolCall("side_effect", "{}"));
        var tool     = new FakeTool("side_effect");
        var recorder = new ThrowingRecorder { ThrowOnResult = true };
        var loop     = new AgentLoop(client, new ToolRegistry([tool]), Options(), null, null, recorder);

        var result = await loop.RunAsync("sys", "run", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(result.Error, Is.EqualTo(
                                             "The tool ran but its result was not reliably recorded; later tools were stopped."));
        Assert.That(result.PersistenceWarning, Is.EqualTo("injected result audit failure"));
        Assert.That(result.ToolExecutions, Is.EqualTo(1));
        Assert.That(tool.ExecuteCount, Is.EqualTo(1));
        Assert.That(client.Requests, Is.EqualTo(1));
        Assert.That(recorder.ResultCalls, Is.EqualTo(1));
        Assert.That(recorder.CompleteCalls, Is.EqualTo(1));
        Assert.That(result.ToolCalls!.Any(call => call.State == ToolCallExecutionState.UnknownResult), Is.True);
        TestAssert.ContainsAll(message => message is { Role: ChatRole.Tool, Content: "side_effect ok" }, loop.History);
    }

    private static AgentOptions Options() => new()
    {
        Model                     = "test-model",
        MaxAgentSteps             = 10,
        DefaultToolTimeoutSeconds = 30
    };

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
                                          CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordResultAsync(ToolPreparation   preparation, ToolResult result,
                                      CancellationToken cancellationToken)
        {
            ResultCalls++;
            return ThrowOnResult
                ? Task.FromException(new IOException("injected result audit failure"))
                : Task.CompletedTask;
        }

        public Task RecordCompactionAsync(ContextChange change, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordModelUsageAsync(int?              inputTokens, int? outputTokens, string? finishReason,
                                          CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CompleteAsync(AgentResult       result, IReadOnlyList<ChatMessage> messages,
                                  CancellationToken cancellationToken)
        {
            CompleteCalls++;
            return ThrowOnComplete
                ? Task.FromException(new IOException("injected completion failure"))
                : Task.CompletedTask;
        }
    }
}

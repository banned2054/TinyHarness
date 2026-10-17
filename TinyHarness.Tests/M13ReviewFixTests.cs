using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public class M13ReviewFixTests
{
    private static SessionModelIdentity Identity =>
        new("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions);

    private static AgentOptions Options() => new()
        { Model = "test-model", MaxAgentSteps = 8, DefaultToolTimeoutSeconds = 30 };


    [TestCase("")]
    [TestCase("same")]
    public async Task InvalidCallIdsRejectTheWholeRoundBeforeExecution(string id)
    {
        var client = new FakeChatClient();
        client.Enqueue([
            new ChatStreamEvent
            {
                Kind       = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 0,
                ToolCallId = id, ToolCallFunctionName                         = "write", ToolCallArgumentsDelta = "{}"
            },
            new ChatStreamEvent
            {
                Kind       = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 1,
                ToolCallId = id, ToolCallFunctionName                         = "read", ToolCallArgumentsDelta = "{}"
            },
            new ChatStreamEvent { Kind = ChatStreamEventKind.End }
        ]);
        var write  = new FakeTool("write");
        var read   = new FakeTool("read");
        var loop   = new AgentLoop(client, new ToolRegistry([write, read]), Options());
        var result = await loop.RunAsync("sys", "go", CancellationToken.None);
        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(write.ExecuteCount, Is.EqualTo(0));
        Assert.That(read.ExecuteCount, Is.EqualTo(0));
        Assert.That(result.ToolCalls!, Is.Empty);
        TestAssert.DoesNotContainAny(message => message.ToolCalls is { Count: > 0 }, loop.History);
    }

    [Test]
    public async Task UncertainExecutionStopsSiblingsAndBlocksTheNextTurnWithoutAnEngine()
    {
        using var ws     = new TestTempDir();
        var       client = new FakeChatClient();
        client.Enqueue([
            new ChatStreamEvent
            {
                Kind       = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 0,
                ToolCallId = "first", ToolCallFunctionName = "uncertain", ToolCallArgumentsDelta = "{}"
            },
            new ChatStreamEvent
            {
                Kind       = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 1,
                ToolCallId = "second", ToolCallFunctionName                   = "write", ToolCallArgumentsDelta = "{}"
            },
            new ChatStreamEvent { Kind = ChatStreamEventKind.End }
        ]);
        client.Enqueue(FakeChatClient.ToolCall("write", "{}", "next"));
        client.Enqueue(FakeChatClient.Text("blocked"));
        var tool  = new UncertainTool();
        var write = new FakeTool("write");
        using var host = new ChatSessionHost(ws.Root, Identity, Options(), client,
                                             new ToolRegistry([tool, write]), "sys");
        var first = await host.SubmitTurnAsync("go", CancellationToken.None);
        Assert.That(first.Result!.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(first.Result.ToolCalls![0].State, Is.EqualTo(ToolCallExecutionState.UnknownResult));
        Assert.That(first.Result.ToolCalls[1].State, Is.EqualTo(ToolCallExecutionState.KnownNotStarted));
        Assert.That(host.InspectionPending, Is.True);
        Assert.That(write.ExecuteCount, Is.EqualTo(0));
        await host.SubmitTurnAsync("continue", CancellationToken.None);
        Assert.That(write.ExecuteCount, Is.EqualTo(0));
        Assert.That(host.InspectionPending, Is.True);
        await host.AcknowledgeInspectionAsync(CancellationToken.None);
        Assert.That(host.InspectionPending, Is.False);
    }


    [TestCase(ToolCallExecutionState.UnknownResult)]
    [TestCase(ToolCallExecutionState.ConfirmedResult)]
    public async Task RunAndSessionInspectionRecordsRedactEveryStringAndRoundTripState(ToolCallExecutionState state)
    {
        using var    ws      = new TestTempDir();
        using var    dir     = new TestTempDir();
        const string secret  = "synthetic-tool-secret";
        var          secrets = new Dictionary<string, string> { ["TEST_KEY"] = secret };
        var record = new ToolCallRecord
        {
            CallId           = secret, ToolName = secret, Capability  = secret, Summary = secret,
            PreparationError = secret, Approval = secret, SideEffects = true, State     = state
        };
        var redactor       = new SecretRedactor(secrets);
        var redactedRecord = redactor.Redact(record);
        Assert.That(JsonSerializer.Serialize(redactedRecord), Does.Not.Contain(secret));
        Assert.That(redactedRecord.State, Is.EqualTo(state));
        Assert.That(redactedRecord.Approval, Is.EqualTo("[REDACTED]"));
        Assert.That(record.Approval, Is.EqualTo(secret));

        var session = new ChatSession(Guid.NewGuid().ToString("N"), ws.Root, Identity)
            { PendingInspectionReason = "inspect " + secret };
        var turn = new SessionTurn(Guid.NewGuid().ToString("N"), "input");
        var run  = new SessionRun(Guid.NewGuid().ToString("N"), session.Id, turn.Id);
        turn.AddRun(run);
        session.AddTurn(turn);
        run.Finish(new AgentResult { Status = AgentStatus.Failed, ToolCalls = [record] });
        var context = new ConversationContext();
        context.Append(ChatMessage.System("sys"));
        string stored;
        using (var writer = new SessionStoreWriter(dir.Root, session.Id, secrets))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
            stored = await ReadSharedTextAsync(writer.SnapshotPath);
        }

        Assert.That(stored, Does.Not.Contain(secret));
        var snapshot = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        Assert.That(snapshot!.PendingInspectionReason, Is.EqualTo("inspect [REDACTED]"));
        var call = TestAssert.Single(snapshot.Turns[0].Runs[0].ToolCalls!);
        Assert.That(call.CallId, Is.EqualTo("[REDACTED]"));
        Assert.That(call.ToolName, Is.EqualTo("[REDACTED]"));
        Assert.That(call.Capability, Is.EqualTo("[REDACTED]"));
        Assert.That(call.State, Is.EqualTo(state.ToString()));
    }

    [Test]
    public async Task LegacyConservativeBlockSurvivesReadOnlyCommitAndClearsOnlyAfterAcknowledgement()
    {
        using var ws  = new TestTempDir();
        using var dir = new TestTempDir();
        var snapshot = new PersistedSession
        {
            Id = Guid.NewGuid().ToString("N"), WorkspaceRoot = ws.Root, Model = Identity,
            Conversation = new PersistedConversation
            {
                Messages =
                [
                    new PersistedMessage { Role = "System", Content = "sys" },
                    new PersistedMessage { Role = "User", Content   = "old" },
                    new PersistedMessage
                        { Role = "Assistant", ToolCalls = [new PersistedToolCall("old", "shell", "{}")] }
                ]
            }
        };
        var restored = SessionResume.Restore(snapshot, ws.Root, Identity, "sys");
        var client   = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("read-only answer"));
        using (var host = new ChatSessionHost(restored.Session, restored.Context, Options(), client,
                                              new ToolRegistry([]), "sys",
                                              sessionWriter : new SessionStoreWriter(dir.Root, snapshot.Id),
                                              needsInspection : restored.NeedsInspection))
        {
            await host.SubmitTurnAsync("discuss only", CancellationToken.None);
            Assert.That(host.InspectionPending, Is.True);
        }

        using var writer =
            await SessionStoreWriter.OpenExistingAsync(dir.Root, snapshot.Id, null, CancellationToken.None);
        var resumed = SessionResume.Restore(writer.LoadedSnapshot!, ws.Root, Identity, "sys");
        Assert.That(resumed.RewoundTail, Is.False);
        TestAssert.NotNull(resumed.NeedsInspection);
        using var next = new ChatSessionHost(resumed.Session, resumed.Context, Options(), new FakeChatClient(),
                                             new ToolRegistry([]), "sys", sessionWriter : writer,
                                             needsInspection : resumed.NeedsInspection);
        await next.AcknowledgeInspectionAsync(CancellationToken.None);
        writer.Dispose();
        var acknowledged = await SessionStoreReader.ReadAsync(dir.Root, snapshot.Id, CancellationToken.None);
        Assert.That(acknowledged!.PendingInspectionReason, Is.Null);
        TestAssert.NotNull(acknowledged.InspectionAcknowledgedUtc);
        Assert.That(SessionResume.Restore(acknowledged, ws.Root, Identity, "sys").NeedsInspection, Is.Null);
    }

    private static async Task<string> ReadSharedTextAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var       reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }


    [TestCase(null)]
    [TestCase("Completed")]
    public void UnknownNumericExecutionStateIsRejectedEvenForUnfinishedRuns(string? status)
    {
        using var ws = new TestTempDir();
        var snapshot = new PersistedSession
        {
            Id = Guid.NewGuid().ToString("N"), WorkspaceRoot = ws.Root, Model = Identity,
            Conversation = new PersistedConversation
                { Messages = [new PersistedMessage { Role = "System", Content = "sys" }] },
            Turns =
            [
                new PersistedTurn
                {
                    Id = "turn", UserInput = "input", Runs =
                    [
                        new PersistedRun
                        {
                            Id = "run", Status = status,
                            ToolCalls =
                            [
                                new PersistedToolCallRecord { CallId = "id", ToolName = "shell", State = "999" }
                            ]
                        }
                    ]
                }
            ]
        };
        TestAssert.Throws<SessionStoreException>(() => SessionResume.Restore(snapshot, ws.Root, Identity, "sys"));
    }

    [Test]
    public async Task ExecutionCheckpointReceivesDetachedIntentBeforeExecute()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("write", "{}"));
        client.Enqueue(FakeChatClient.Text("done"));
        var                            tool     = new FakeTool("write");
        IReadOnlyList<ToolCallRecord>? captured = null;
        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options(), null, null,
                                 executionCheckpoint : (records, _) =>
                                 {
                                     Assert.That(tool.ExecuteCount, Is.EqualTo(0));
                                     captured = records;
                                     Assert.That(TestAssert.Single(records).State,
                                                 Is.EqualTo(ToolCallExecutionState.UnknownResult));
                                     return Task.CompletedTask;
                                 });
        var result = await loop.RunAsync("sys", "go", CancellationToken.None);
        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(TestAssert.Single(result.ToolCalls!).State, Is.EqualTo(ToolCallExecutionState.ConfirmedResult));
        Assert.That(TestAssert.Single(captured!).State, Is.EqualTo(ToolCallExecutionState.UnknownResult));
    }

    private sealed class UncertainTool : ITool
    {
        public ToolDefinition Definition { get; } = new()
        {
            Name = "uncertain", Description = "Cleanup failure", Parameters = new JsonObject { ["type"] = "object" }
        };

        public ToolPreparation Prepare(ChatToolCall call) => new()
        {
            ToolName  = Definition.Name, CallId = call.Id, Capability = "process.execute", Summary = "uncertain()",
            Arguments = new JsonObject()
        };

        public Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken token) =>
            throw new ToolExecutionUncertainException("Process cleanup could not be confirmed.");
    }
}

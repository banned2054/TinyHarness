using System.Text.Json.Nodes;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
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

    [Theory]
    [InlineData("")]
    [InlineData("same")]
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
        Assert.Equal(AgentStatus.Failed, result.Status);
        Assert.Equal(0, write.ExecuteCount);
        Assert.Equal(0, read.ExecuteCount);
        Assert.Empty(result.ToolCalls!);
        Assert.DoesNotContain(loop.History, message => message.ToolCalls is { Count: > 0 });
    }

    [Fact]
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
        Assert.Equal(AgentStatus.Failed, first.Result!.Status);
        Assert.Equal(ToolCallExecutionState.UnknownResult, first.Result.ToolCalls![0].State);
        Assert.Equal(ToolCallExecutionState.KnownNotStarted, first.Result.ToolCalls[1].State);
        Assert.True(host.InspectionPending);
        Assert.Equal(0, write.ExecuteCount);
        await host.SubmitTurnAsync("continue", CancellationToken.None);
        Assert.Equal(0, write.ExecuteCount);
        Assert.True(host.InspectionPending);
        await host.AcknowledgeInspectionAsync(CancellationToken.None);
        Assert.False(host.InspectionPending);
    }

    [Theory]
    [InlineData(ToolCallExecutionState.UnknownResult)]
    [InlineData(ToolCallExecutionState.ConfirmedResult)]
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
        var result   = new AgentResult { Status = AgentStatus.Failed, ToolCalls = [record] };
        var recorder = new FileRunRecorder(dir.Root, Guid.NewGuid().ToString("N"), secrets);
        await recorder.CompleteAsync(result, [], StructuredState.Empty, CancellationToken.None);
        var raw = await File.ReadAllTextAsync(recorder.SessionPath);
        Assert.DoesNotContain(secret, raw);
        var legacy = await SessionStoreReader.ReadLegacyAsync(dir.Root, recorder.RunId, CancellationToken.None);
        Assert.Equal(state, legacy!.Result.ToolCalls![0].State);
        Assert.Equal("[REDACTED]", legacy.Result.ToolCalls[0].Approval);
        Assert.Equal(secret, record.Approval);

        var session = new ChatSession(Guid.NewGuid().ToString("N"), ws.Root, Identity)
            { PendingInspectionReason = "inspect " + secret };
        var turn = new SessionTurn(Guid.NewGuid().ToString("N"), "input");
        var run  = new SessionRun(Guid.NewGuid().ToString("N"), session.Id, turn.Id);
        turn.AddRun(run);
        session.AddTurn(turn);
        run.Finish(result);
        var context = new ConversationContext();
        context.Append(ChatMessage.System("sys"));
        using var writer = new SessionStoreWriter(dir.Root, session.Id, secrets);
        await writer.CommitAsync(session, context, CancellationToken.None);
        var stored = await File.ReadAllTextAsync(writer.SnapshotPath);
        Assert.DoesNotContain(secret, stored);
        var snapshot = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        Assert.Equal("inspect [REDACTED]", snapshot!.PendingInspectionReason);
        var call = Assert.Single(snapshot.Turns[0].Runs[0].ToolCalls!);
        Assert.Equal("[REDACTED]", call.CallId);
        Assert.Equal("[REDACTED]", call.ToolName);
        Assert.Equal("[REDACTED]", call.Capability);
        Assert.Equal(state.ToString(), call.State);
    }

    [Fact]
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
            Assert.True(host.InspectionPending);
        }

        using var writer =
            await SessionStoreWriter.OpenExistingAsync(dir.Root, snapshot.Id, null, CancellationToken.None);
        var resumed = SessionResume.Restore(writer.LoadedSnapshot!, ws.Root, Identity, "sys");
        Assert.False(resumed.RewoundTail);
        Assert.NotNull(resumed.NeedsInspection);
        using var next = new ChatSessionHost(resumed.Session, resumed.Context, Options(), new FakeChatClient(),
                                             new ToolRegistry([]), "sys", sessionWriter : writer,
                                             needsInspection : resumed.NeedsInspection);
        await next.AcknowledgeInspectionAsync(CancellationToken.None);
        var acknowledged = await SessionStoreReader.ReadAsync(dir.Root, snapshot.Id, CancellationToken.None);
        Assert.Null(acknowledged!.PendingInspectionReason);
        Assert.NotNull(acknowledged.InspectionAcknowledgedUtc);
        Assert.Null(SessionResume.Restore(acknowledged, ws.Root, Identity, "sys").NeedsInspection);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Completed")]
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
        Assert.Throws<SessionStoreException>(() => SessionResume.Restore(snapshot, ws.Root, Identity, "sys"));
    }

    [Fact]
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
                                     Assert.Equal(0, tool.ExecuteCount);
                                     captured = records;
                                     Assert.Equal(ToolCallExecutionState.UnknownResult, Assert.Single(records).State);
                                     return Task.CompletedTask;
                                 });
        var result = await loop.RunAsync("sys", "go", CancellationToken.None);
        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(ToolCallExecutionState.ConfirmedResult, Assert.Single(result.ToolCalls!).State);
        Assert.Equal(ToolCallExecutionState.UnknownResult, Assert.Single(captured!).State);
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

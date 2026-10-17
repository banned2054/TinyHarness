using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public sealed class SessionHistoryTests
{
    [Test]
    public void SessionHistoryItemRoundTripsThroughGeneratedJsonMetadata()
    {
        var item = new SessionHistoryItem
        {
            Id               = "session:run:call:tool",
            Sequence         = 17,
            SessionId        = "session",
            TurnId           = "turn",
            RunId            = "run",
            Kind             = SessionHistoryKind.Tool,
            Text             = "Applied patch to one file.",
            CallId           = "call",
            InvocationId     = "call-00000001",
            ToolName         = "apply_patch",
            ArgumentsJson    = "{\"patch\":\"redacted presentation input\"}",
            Capability       = "filesystem.write",
            Summary          = "update src/example.cs",
            TargetPaths      = ["src/example.cs"],
            ExecutionPolicy  = "backend=host",
            Approval         = "allow once",
            State            = "confirmed result",
            Succeeded        = true,
            ExitCode         = 0,
            TimedOut         = false,
            OutputTruncated  = true,
            Stdout           = "bounded stdout",
            Stderr           = "bounded stderr",
            PatchChanges     = [new PatchChange("src/example.cs", "updated", "@@ -1 +1 @@", "applied")],
            OutputIncomplete = true
        };

        var json     = JsonSerializer.Serialize(item, SessionHistoryJsonContext.Default.SessionHistoryItem);
        var restored = JsonSerializer.Deserialize(json, SessionHistoryJsonContext.Default.SessionHistoryItem);

        Assert.That(restored, Is.Not.Null);
        var roundTripJson = JsonSerializer.Serialize(restored, SessionHistoryJsonContext.Default.SessionHistoryItem);
        using var document = JsonDocument.Parse(json);
        using var roundTripDocument = JsonDocument.Parse(roundTripJson);
        Assert.That(JsonElement.DeepEquals(document.RootElement, roundTripDocument.RootElement), Is.True);
        Assert.That(document.RootElement.TryGetProperty("payload", out _), Is.False);
        Assert.That(document.RootElement.GetProperty("kind").GetString(), Is.EqualTo("tool"));
        Assert.That(document.RootElement.GetProperty("patchChanges")[0].GetProperty("path").GetString(),
                    Is.EqualTo("src/example.cs"));
    }

    [Test]
    public async Task Recorder_AppendsRedactedBoundHistoryAndInlinesBoundedArtifact()
    {
        using var    dir        = new TestTempDir();
        const string secret     = "artifact-secret-value";
        const string fullOutput = "before " + secret + " after";
        var          sessionId  = Guid.NewGuid().ToString("N");
        var session = new ChatSession(sessionId, dir.Root,
                                      new SessionModelIdentity("model", "https://example.invalid/v1",
                                                               ChatApiKind.ChatCompletions));
        var turn = new SessionTurn("turn-id", "question " + secret);
        var run  = new SessionRun("run-id", session.Id, turn.Id);
        session.AddTurn(turn);
        turn.AddRun(run);
        run.Finish(new AgentResult { Status = AgentStatus.Completed, FinalMessage = "answer" });
        var context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        context.Append(ChatMessage.User("question " + secret));
        context.Append(ChatMessage.Assistant("answer"));
        var       secrets       = new Dictionary<string, string> { ["KEY"] = secret };
        using var writer        = new SessionStoreWriter(dir.Root, session.Id, secrets);
        var       published     = new List<SessionHistoryItem>();
        var       publishErrors = new List<Exception>();
        var       artifactPath  = Path.Combine(dir.Root, "full-output.txt");
        await File.WriteAllTextAsync(artifactPath, fullOutput);
        var recorder = new SessionLogRunRecorder(writer, session.Id, turn.Id, run.Id,
                                                 token => writer.CommitAsync(session, context, token), secrets,
                                                 (item, _) =>
                                                 {
                                                     published.Add(item);
                                                     throw new InvalidOperationException("display unavailable");
                                                 }, publishErrors.Add);

        await recorder.StartAsync("system", "question " + secret, CancellationToken.None);
        var call = new ChatToolCall("call-id", "shell", "{\"command\":\"echo\",\"_plan\":\"private\"}");
        await recorder.RecordAssistantAsync(ChatMessage.Assistant("processing", [call]), CancellationToken.None);
        var preparation = new ToolPreparation
        {
            ToolName        = "shell", CallId            = call.Id, Arguments         = new JsonObject(),
            Capability      = "process.execute", Summary = "run command", TargetPaths = [],
            ExecutionPolicy = "backend=test"
        };
        await recorder.RecordPreparedAsync(preparation, CancellationToken.None);
        await recorder.RecordPermissionAsync(preparation, PermissionDecision.Allow, "allowed", CancellationToken.None);
        var firstResult = new ToolResult
        {
            Succeeded          = false,
            OutputTruncated    = true,
            Stdout             = "preview",
            StdoutArtifactPath = artifactPath,
            PatchChanges =
            [
                new PatchChange("confirmed.txt", "modify", "confirmed diff", PatchChange.ConfirmedState),
                new PatchChange("unknown.txt", "modify", "partial diff", PatchChange.UnknownState)
            ]
        };
        await recorder.RecordResultAsync(preparation, firstResult, CancellationToken.None);
        var sequenceAfterResult = writer.CommitSequence;
        await TestAssert.ThrowsAsync<InvalidOperationException>(() => recorder.RecordResultAsync(preparation,
                                                                         firstResult, CancellationToken.None));
        await TestAssert.ThrowsAsync<InvalidOperationException>(() =>
                                                                    recorder
                                                                       .RecordAssistantAsync(ChatMessage
                                                                                    .Assistant("duplicate",
                                                                                              [call, call]),
                                                                                 CancellationToken.None));
        Assert.That(writer.CommitSequence, Is.EqualTo(sequenceAfterResult));
        File.Delete(artifactPath);
        var missingCall = new ChatToolCall(call.Id, "shell", "{\"command\":\"echo\"}");
        await recorder.RecordAssistantAsync(ChatMessage.Assistant("another process", [missingCall]),
                                            CancellationToken.None);
        var missingPreparation = new ToolPreparation
        {
            ToolName        = preparation.ToolName,
            CallId          = missingCall.Id,
            Arguments       = preparation.Arguments,
            Capability      = preparation.Capability,
            Summary         = preparation.Summary,
            TargetPaths     = preparation.TargetPaths,
            ExecutionPolicy = preparation.ExecutionPolicy
        };
        await recorder.RecordPreparedAsync(missingPreparation, CancellationToken.None);
        var sequenceBeforeLateConfirmation = writer.CommitSequence;
        var duplicate =
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => recorder.RecordResultAsync(preparation,
                                                                             firstResult, CancellationToken.None));
        Assert.That(duplicate.Message, Does.Contain("call-00000001"));
        Assert.That(writer.CommitSequence, Is.EqualTo(sequenceBeforeLateConfirmation));
        await recorder.RecordResultAsync(missingPreparation, new ToolResult
        {
            Succeeded          = true,
            OutputTruncated    = true,
            Stdout             = "bounded preview",
            StdoutArtifactPath = Path.Combine(dir.Root, "missing-output.txt")
        }, CancellationToken.None);
        var sequenceAfterSecondResult = writer.CommitSequence;
        duplicate =
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => recorder.RecordResultAsync(missingPreparation,
                                                                             firstResult, CancellationToken.None));
        Assert.That(duplicate.Message, Does.Contain("call-00000002"));
        Assert.That(writer.CommitSequence, Is.EqualTo(sequenceAfterSecondResult));
        var oversizedCall = new ChatToolCall("call-oversized", "shell", "{\"command\":\"echo\"}");
        await recorder.RecordAssistantAsync(ChatMessage.Assistant("oversized process", [oversizedCall]),
                                            CancellationToken.None);
        var oversizedPreparation = new ToolPreparation
        {
            ToolName        = preparation.ToolName,
            CallId          = oversizedCall.Id,
            Arguments       = preparation.Arguments,
            Capability      = preparation.Capability,
            Summary         = preparation.Summary,
            TargetPaths     = preparation.TargetPaths,
            ExecutionPolicy = preparation.ExecutionPolicy
        };
        await recorder.RecordPreparedAsync(oversizedPreparation, CancellationToken.None);
        var oversizedArtifactPath = Path.Combine(dir.Root, "oversized-output.txt");
        await File.WriteAllBytesAsync(oversizedArtifactPath, new byte[2 * 1024 * 1024 + 1]);
        await recorder.RecordResultAsync(oversizedPreparation, new ToolResult
        {
            Succeeded          = true,
            OutputTruncated    = true,
            Stdout             = "bounded preview",
            StdoutArtifactPath = oversizedArtifactPath
        }, CancellationToken.None);
        writer.Dispose();

        var lines = await File.ReadAllLinesAsync(writer.SnapshotPath);
        var batches = lines
                     .Select(line => JsonSerializer.Deserialize(line, SessionLogJsonContext.Default.SessionLogBatch)!)
                     .ToArray();
        var historyRecords = batches.SelectMany(batch => batch.Records)
                                    .Where(record => record.Kind == "history").ToArray();
        var auditRecords = batches.SelectMany(batch => batch.Records)
                                  .Where(record => record.Kind == "audit")
                                  .Select(record =>
                                              record.Payload.Deserialize(PersistenceJsonContext.Default.AuditRecord)!)
                                  .ToArray();
        var savedItems = historyRecords.Select(record => record.Payload.Deserialize(
                                                    SessionHistoryJsonContext.Default.SessionHistoryItem)!).ToArray();
        var savedTools = savedItems.Where(item => item.Kind == SessionHistoryKind.Tool).ToArray();
        var savedTool  = savedTools[0];

        Assert.That(published.Any(item => item.Kind == SessionHistoryKind.User), Is.True);
        Assert.That(publishErrors, Has.Count.EqualTo(7));
        Assert.That(savedItems.Select(item => item.Kind),
                    Is.EqualTo(["user", "process", "tool", "process", "tool", "process", "tool"]));
        Assert.That(savedTool.State, Is.EqualTo(nameof(ToolCallExecutionState.UnknownResult)));
        Assert.That(savedTool.Succeeded, Is.False);
        Assert.That(savedTool.Stdout, Is.EqualTo("before [REDACTED] after"));
        Assert.That(savedTool.OutputIncomplete, Is.False);
        Assert.That(savedTool.OutputWarning, Is.Null);
        Assert.That(savedTool.PatchChanges!.Select(change => change.State),
                    Is.EqualTo([PatchChange.ConfirmedState, PatchChange.UnknownState]));
        Assert.That(savedTool.ArgumentsJson, Is.EqualTo("{\"command\":\"echo\"}"));
        Assert.That(savedTools.Select(item => item.InvocationId),
                    Is.EqualTo(["call-00000001", "call-00000002", "call-00000003"]));
        Assert.That(savedItems.All(item => item.Sequence > 1 && !string.IsNullOrWhiteSpace(item.Id)), Is.True);
        Assert.That(savedTools[1].State, Is.EqualTo(nameof(ToolCallExecutionState.ConfirmedResult)));
        Assert.That(savedTools[1].OutputIncomplete, Is.True);
        Assert.That(savedTools[1].OutputWarning, Is.EqualTo("stdout: artifact is missing"));
        Assert.That(savedTools[1].Stdout, Is.EqualTo("bounded preview"));
        Assert.That(savedTools[2].OutputIncomplete, Is.True);
        Assert.That(savedTools[2].OutputWarning, Is.EqualTo("stdout: artifact exceeds 2 MiB limit"));
        Assert.That(savedTools[2].Stdout, Is.EqualTo("bounded preview"));
        var liveToolJson = JsonSerializer.SerializeToElement(published.Single(item => item.Id == savedTool.Id),
                                                             SessionHistoryJsonContext.Default.SessionHistoryItem);
        var savedToolJson = JsonSerializer.SerializeToElement(savedTool,
                                                              SessionHistoryJsonContext.Default.SessionHistoryItem);
        Assert.That(JsonElement.DeepEquals(liveToolJson, savedToolJson), Is.True);
        Assert.That(auditRecords.Any(item => item is
        {
            Kind: "tool.permission", Decision: "Allow", ExecutionPolicy: "backend=test"
        }), Is.True);
        Assert.That(auditRecords.Any(item => item is { Kind: "tool.result", ExecutionPolicy: "backend=test" }),
                    Is.True);
        Assert.That(auditRecords.Where(item => item.Kind == "tool.result").Select(item => item.InvocationId),
                    Is.EqualTo(savedTools.Select(item => item.InvocationId)));
        var historyJson = string.Join(Environment.NewLine,
                                      historyRecords.Select(record => record.Payload.GetRawText()));
        TestAssert.DoesNotContain(secret, historyJson, StringComparison.Ordinal);
        TestAssert.DoesNotContain("_plan", historyJson, StringComparison.Ordinal);
    }

    [Test]
    public async Task HostPersistsPrepareFailureHistoryAsKnownNotStartedAfterRestart()
    {
        using var dir    = new TestTempDir();
        var       client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("missing", "{\"token\":\"hidden\"}"));
        client.Enqueue(FakeChatClient.Text("tool unavailable"));
        using var host = NewHost(dir.Root, client, new ToolRegistry([]));

        var run       = await host.SubmitTurnAsync("try missing tool", CancellationToken.None);
        var sessionId = host.Session.Id;
        host.Dispose();
        var restored = await RestoreStoredSessionAsync(dir.Root, sessionId);
        var page = await new SessionHistoryReader().ReadPageAsync(dir.Root, sessionId,
                                                                  SessionHistoryReader.MaxPageItems, null,
                                                                  CancellationToken.None);
        var item = page.Items.Single(entry => entry.Kind == SessionHistoryKind.Tool);

        Assert.That(run.Result!.ToolExecutions, Is.Zero);
        Assert.That(run.Result.PersistenceWarning, Is.Null);
        Assert.That(restored.NeedsInspection, Is.Null);
        Assert.That(restored.Session.Turns.Single().Runs.Single().Result!.ToolCalls!.Single().State,
                    Is.EqualTo(ToolCallExecutionState.KnownNotStarted));
        Assert.That(item.State, Is.EqualTo(nameof(ToolCallExecutionState.KnownNotStarted)));
        Assert.That(item.PreparationError, Does.Contain("Unknown tool"));
        Assert.That(item.Succeeded, Is.Null);
        Assert.That(item.ExitCode, Is.Null);
        TestAssert.DoesNotContain("hidden", item.ArgumentsJson, StringComparison.Ordinal);
    }

    [Test]
    public async Task HostPersistsPermissionDenialImmediatelyWithoutStartingTool()
    {
        using var dir    = new TestTempDir();
        var       client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("write_test", "{\"path\":\"file.txt\"}"));
        client.Enqueue(FakeChatClient.Text("permission denied"));
        var       tool = new HistoryTestTool(dir.Root);
        using var host = NewHost(dir.Root, client, new ToolRegistry([tool]), new DenyHistoryApprover());

        var run       = await host.SubmitTurnAsync("try write", CancellationToken.None);
        var sessionId = host.Session.Id;
        host.Dispose();
        var restored = await RestoreStoredSessionAsync(dir.Root, sessionId);
        var page = await new SessionHistoryReader().ReadPageAsync(dir.Root, sessionId,
                                                                  SessionHistoryReader.MaxPageItems, null,
                                                                  CancellationToken.None);
        var item = page.Items.Single(entry => entry.Kind == SessionHistoryKind.Tool);

        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(run.Result.PersistenceWarning, Is.Null);
        Assert.That(restored.NeedsInspection, Is.Null);
        Assert.That(restored.Session.Turns.Single().Runs.Single().Result!.ToolCalls!.Single().State,
                    Is.EqualTo(ToolCallExecutionState.KnownNotStarted));
        Assert.That(tool.ExecuteCount, Is.Zero);
        Assert.That(item.State, Is.EqualTo(nameof(ToolCallExecutionState.KnownNotStarted)));
        Assert.That(item.Approval, Does.Contain("Permission denied by user"));
        Assert.That(item.Succeeded, Is.Null);
        Assert.That(item.ExitCode, Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task HostPersistsReusedRawCallIdAcrossRoundsAndRestores(bool firstPreparationFails)
    {
        using var    dir       = new TestTempDir();
        const string rawCallId = "provider-reused-call";
        var          client    = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall(firstPreparationFails ? "missing" : "write_test", "{}", rawCallId));
        client.Enqueue(FakeChatClient.ToolCall("write_test", "{}", rawCallId));
        client.Enqueue(FakeChatClient.Text("finished"));
        var       tool   = new HistoryTestTool(dir.Root);
        var       events = new HistoryCollectingSink();
        using var host   = NewHost(dir.Root, client, new ToolRegistry([tool]), new AllowHistoryApprover(), events);

        var run         = await host.SubmitTurnAsync("repeat provider call id", CancellationToken.None);
        var sessionId   = host.Session.Id;
        var journalPath = host.PersistencePath!;
        host.Dispose();
        var restored = await RestoreStoredSessionAsync(dir.Root, sessionId);
        var page = await new SessionHistoryReader().ReadPageAsync(dir.Root, sessionId,
                                                                  SessionHistoryReader.MaxPageItems, null,
                                                                  CancellationToken.None);
        var tools      = page.Items.Where(item => item.Kind == SessionHistoryKind.Tool).ToArray();
        var executions = restored.Session.Turns.Single().Runs.Single().Result!.ToolCalls!;

        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(run.Result.PersistenceWarning, Is.Null);
        Assert.That(tool.ExecuteCount, Is.EqualTo(firstPreparationFails ? 1 : 2));
        Assert.That(page.Warning, Is.Null);
        Assert.That(page.NextCursor, Is.Null);
        Assert.That(page.Items.Select(item => item.Kind),
                    Is.EqualTo(["user", "process", "tool", "process", "tool", "final"]));
        Assert.That(events.Items.Select(item => item.Id), Is.EqualTo(page.Items.Select(item => item.Id)));
        Assert.That(restored.NeedsInspection, Is.Null);
        Assert.That(restored.RewoundTail, Is.False);
        Assert.That(tools.Select(item => item.InvocationId),
                    Is.EqualTo(["call-00000001", "call-00000002"]));
        Assert.That(tools.Select(item => item.CallId), Is.EqualTo([rawCallId, rawCallId]));
        Assert.That(executions.Select(item => item.InvocationId), Is.EqualTo(tools.Select(item => item.InvocationId)));
        Assert.That(executions.Select(item => item.State.ToString()), Is.EqualTo(tools.Select(item => item.State)));
        Assert.That(tools[0].State, Is.EqualTo(firstPreparationFails
                                                   ? nameof(ToolCallExecutionState.KnownNotStarted)
                                                   : nameof(ToolCallExecutionState.ConfirmedResult)));
        Assert.That(tools[0].PreparationError, firstPreparationFails ? Does.Contain("Unknown tool") : Is.Null);
        Assert.That(tools[0].Succeeded, firstPreparationFails ? Is.Null : Is.True);
        Assert.That(tools[0].ExitCode, firstPreparationFails ? Is.Null : Is.EqualTo(0));
        Assert.That(tools[1].State, Is.EqualTo(nameof(ToolCallExecutionState.ConfirmedResult)));
        Assert.That(tools[1].Succeeded, Is.True);
        Assert.That(restored.Context.Messages.SelectMany(message => message.ToolCalls ?? [])
                            .Select(call => call.Id), Is.EqualTo([rawCallId, rawCallId]));
        foreach (var saved in page.Items)
        {
            var live = events.Items.Single(item => item.Id == saved.Id);
            Assert.That(JsonElement.DeepEquals(
                                               JsonSerializer.SerializeToElement(live,
                                                        SessionHistoryJsonContext.Default.SessionHistoryItem),
                                               JsonSerializer.SerializeToElement(saved,
                                                        SessionHistoryJsonContext.Default.SessionHistoryItem)),
                        Is.True);
        }

        var savedRecords = (await File.ReadAllLinesAsync(journalPath))
                          .Select(line =>
                                      JsonSerializer.Deserialize(line, SessionLogJsonContext.Default.SessionLogBatch)!)
                          .SelectMany(batch => batch.Records).ToArray();
        Assert.That(savedRecords.Where(record => record.Kind == "history").Select(record => record.Id),
                    Is.EqualTo(page.Items.Select(item => item.Id)));
        var audit = savedRecords.Where(record => record.Kind == "audit")
                                .Select(record =>
                                            record.Payload.Deserialize(PersistenceJsonContext.Default.AuditRecord)!)
                                .Where(record => record.Kind.StartsWith("tool.", StringComparison.Ordinal)).ToArray();
        foreach (var item in tools.Where(item => item.Succeeded is not null))
        {
            var invocationAudit = audit.Where(record => record.InvocationId == item.InvocationId).ToArray();
            Assert.That(invocationAudit.Select(record => record.Kind),
                        Is.EqualTo(["tool.prepared", "tool.permission", "tool.result"]));
            Assert.That(invocationAudit.All(record => record.CallId == rawCallId), Is.True);
        }

        if (firstPreparationFails)
        {
            var notStarted = audit.Single(record => record.Kind == "tool.not-started");
            Assert.That(notStarted.InvocationId, Is.EqualTo("call-00000001"));
            Assert.That(notStarted.Status, Is.EqualTo(nameof(ToolCallExecutionState.KnownNotStarted)));
        }
    }

    private static async Task<SessionRestoration> RestoreStoredSessionAsync(string directory, string sessionId)
    {
        var snapshot = await SessionStoreReader.ReadAsync(directory, sessionId, CancellationToken.None);
        Assert.That(snapshot, Is.Not.Null);
        return SessionResume.Restore(snapshot!, directory,
                                     new SessionModelIdentity("test-model", "https://example.invalid/v1",
                                                              ChatApiKind.ChatCompletions),
                                     "history test system prompt");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task HostPersistsEveryNotStartedCallInRoundOrderBeforeRetry(bool invalidFirst)
    {
        using var dir    = new TestTempDir();
        var       client = new FakeChatClient();
        client.Enqueue(
        [
            new ChatStreamEvent
            {
                Kind                   = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 0,
                ToolCallId             = invalidFirst ? "invalid" : "reused",
                ToolCallFunctionName   = invalidFirst ? "missing" : "write_test",
                ToolCallArgumentsDelta = "{}"
            },
            new ChatStreamEvent
            {
                Kind                   = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 1,
                ToolCallId             = invalidFirst ? "reused" : "invalid",
                ToolCallFunctionName   = invalidFirst ? "write_test" : "missing",
                ToolCallArgumentsDelta = "{}"
            },
            new ChatStreamEvent { Kind = ChatStreamEventKind.End }
        ]);
        client.Enqueue(FakeChatClient.ToolCall("write_test", "{}", "reused"));
        client.Enqueue(FakeChatClient.Text("finished"));
        var       tool   = new HistoryTestTool(dir.Root);
        var       events = new HistoryCollectingSink();
        using var host   = NewHost(dir.Root, client, new ToolRegistry([tool]), new AllowHistoryApprover(), events);

        var run       = await host.SubmitTurnAsync("retry the invalid round", CancellationToken.None);
        var sessionId = host.Session.Id;
        host.Dispose();
        var restored = await RestoreStoredSessionAsync(dir.Root, sessionId);
        var page = await new SessionHistoryReader().ReadPageAsync(dir.Root, sessionId,
                                                                  SessionHistoryReader.MaxPageItems, null,
                                                                  CancellationToken.None);
        var tools = page.Items.Where(item => item.Kind == SessionHistoryKind.Tool).ToArray();

        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(run.Result.PersistenceWarning, Is.Null);
        Assert.That(tool.ExecuteCount, Is.EqualTo(1));
        Assert.That(restored.NeedsInspection, Is.Null);
        Assert.That(page.Warning, Is.Null);
        Assert.That(page.Items.Select(item => item.Kind),
                    Is.EqualTo(["user", "process", "tool", "tool", "process", "tool", "final"]));
        Assert.That(events.Items.Select(item => item.Id), Is.EqualTo(page.Items.Select(item => item.Id)));
        Assert.That(tools.Select(item => item.InvocationId),
                    Is.EqualTo(["call-00000001", "call-00000002", "call-00000003"]));
        Assert.That(tools.Select(item => item.State), Is.EqualTo([
            nameof(ToolCallExecutionState.KnownNotStarted), nameof(ToolCallExecutionState.KnownNotStarted),
            nameof(ToolCallExecutionState.ConfirmedResult)
        ]));
        Assert.That(tools.Take(2)
                         .All(item => item.Succeeded is null && item.ExitCode is null && !item.OutputIncomplete),
                    Is.True);
        Assert.That(tools[invalidFirst ? 0 : 1].PreparationError, Does.Contain("Unknown tool"));
        Assert.That(tools[invalidFirst ? 1 : 0].PreparationError, Is.Null);
        Assert.That(tools[2].Succeeded, Is.True);
        Assert.That(restored.Session.Turns.Single().Runs.Single().Result!.ToolCalls!.Select(item => item.InvocationId),
                    Is.EqualTo(tools.Select(item => item.InvocationId)));
    }

    [Test]
    public async Task HostRetainsConfirmedFileFactsButRestoresIncompleteInvocationAsUnknown()
    {
        using var dir    = new TestTempDir();
        var       client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCalls("write_test", 2, "partial"));
        var       tool = new HistoryTestTool(dir.Root, true);
        using var host = NewHost(dir.Root, client, new ToolRegistry([tool]), new AllowHistoryApprover());

        var run         = await host.SubmitTurnAsync("cancel between files", CancellationToken.None);
        var sessionId   = host.Session.Id;
        var journalPath = host.PersistencePath!;
        host.Dispose();
        var restored = await RestoreStoredSessionAsync(dir.Root, sessionId);
        var page = await new SessionHistoryReader().ReadPageAsync(dir.Root, sessionId,
                                                                  SessionHistoryReader.MaxPageItems, null,
                                                                  CancellationToken.None);
        var tools = page.Items.Where(item => item.Kind == SessionHistoryKind.Tool).ToArray();

        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Cancelled));
        Assert.That(run.Result.PersistenceWarning, Is.Null);
        Assert.That(run.Result.ToolExecutions, Is.EqualTo(1));
        Assert.That(tool.ExecuteCount, Is.EqualTo(1));
        Assert.That(page.Warning, Is.Null);
        Assert.That(tools, Has.Length.EqualTo(2));
        Assert.That(tools[0].InvocationId, Is.EqualTo("call-00000001"));
        Assert.That(tools[0].State, Is.EqualTo(nameof(ToolCallExecutionState.UnknownResult)));
        Assert.That(tools[0].Succeeded, Is.False);
        Assert.That(tools[0].OutputIncomplete, Is.True);
        Assert.That(tools[0].OutputWarning,
                    Is.EqualTo("tool execution is incomplete; retained output does not confirm overall completion"));
        Assert.That(tools[0].PatchChanges, Has.Count.EqualTo(1));
        Assert.That(tools[0].PatchChanges![0].State, Is.EqualTo(PatchChange.ConfirmedState));
        Assert.That(tools[0].PatchChanges![0].Diff, Is.EqualTo("confirmed first-file diff"));
        Assert.That(tools[1].State, Is.EqualTo(nameof(ToolCallExecutionState.KnownNotStarted)));
        Assert.That(restored.NeedsInspection, Is.Not.Null);
        Assert.That(restored.Session.Turns.Single().Runs.Single().Result!.ToolCalls!.Select(item => item.State),
                    Is.EqualTo([ToolCallExecutionState.UnknownResult, ToolCallExecutionState.KnownNotStarted]));
        var audit = (await File.ReadAllLinesAsync(journalPath))
                   .Select(line => JsonSerializer.Deserialize(line, SessionLogJsonContext.Default.SessionLogBatch)!)
                   .SelectMany(batch => batch.Records).Where(record => record.Kind == "audit")
                   .Select(record => record.Payload.Deserialize(PersistenceJsonContext.Default.AuditRecord)!)
                   .Single(record => record.Kind == "tool.result");
        Assert.That(audit.InvocationId, Is.EqualTo(tools[0].InvocationId));
        Assert.That(audit.Status, Is.EqualTo(tools[0].State));
    }

    private static ChatSessionHost NewHost(string             directory, FakeChatClient client, ToolRegistry tools,
                                           IApprovalProvider? approver = null, IHostEventSink? events = null) => new(
         directory,
         new SessionModelIdentity("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions),
         new AgentOptions { Model = "test-model", MaxAgentSteps = 10, DefaultToolTimeoutSeconds = 30 },
         client, tools, "history test system prompt", new PermissionEngine(directory), approver, events,
         sessionWriterFactory : id => new SessionStoreWriter(directory, id));

    private sealed class DenyHistoryApprover : IApprovalProvider
    {
        public Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken) =>
            Task.FromResult(ApprovalAction.Deny);
    }

    private sealed class AllowHistoryApprover : IApprovalProvider
    {
        public Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken) =>
            Task.FromResult(ApprovalAction.AllowOnce);
    }

    private sealed class HistoryCollectingSink : IHostEventSink
    {
        public List<SessionHistoryItem> Items { get; } = [];

        public ValueTask PublishAsync(HostEvent @event, CancellationToken cancellationToken)
        {
            if (@event.Kind == HostEventKind.HistoryItemCompleted && @event.Payload is SessionHistoryItem item)
                Items.Add(item);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HistoryTestTool(string workspaceRoot, bool cancelAfterConfirmedChange = false) : ITool
    {
        public int ExecuteCount { get; private set; }

        public ToolDefinition Definition { get; } = new()
        {
            Name        = "write_test",
            Description = "History test tool.",
            Parameters  = new JsonObject { ["type"] = "object" }
        };

        public ToolPreparation Prepare(ChatToolCall call) => new()
        {
            ToolName        = Definition.Name,
            CallId          = call.Id,
            Arguments       = new JsonObject(),
            Capability      = "filesystem.write",
            Summary         = "test write",
            TargetPaths     = [Path.Combine(workspaceRoot, "file.txt")],
            ExecutionPolicy = "backend=test"
        };

        public Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            if (cancelAfterConfirmedChange)
                throw new ToolExecutionCanceledException(new ToolResult
                {
                    Content = "cancelled between files",
                    PatchChanges =
                    [
                        new PatchChange("first.txt", "modify", "confirmed first-file diff", PatchChange.ConfirmedState)
                    ]
                }, cancellationToken);
            return Task.FromResult(new ToolResult { Succeeded = true, Content = "executed", ExitCode = 0 });
        }
    }
}

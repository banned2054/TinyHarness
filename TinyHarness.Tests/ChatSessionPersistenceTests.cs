using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         验证回合终态后的版本化快照提交；保存失败单独报告为 PersistenceWarning，
///         不改写执行终态或中断会话。
///     </para>
///     <para>
///         Verifies versioned snapshot commits after turn completion; save failures become PersistenceWarning
///         without changing the execution outcome or interrupting the session.
///     </para>
/// </summary>
public class ChatSessionPersistenceTests
{
    private const string SystemPrompt = "You are a test harness inspecting a workspace.";

    private static AgentOptions Options() => new()
    {
        Model                     = "test-model",
        MaxAgentSteps             = 10,
        DefaultToolTimeoutSeconds = 30
    };

    private static ChatSessionHost NewHost(IChatCompletionClient              client,
                                           Func<string, ISessionStoreWriter>? sessionWriterFactory,
                                           Func<string, IRunRecorder>?        recorderFactory = null,
                                           IHostEventSink?                    events          = null,
                                           ToolRegistry?                      tools           = null,
                                           PermissionEngine?                  permissions     = null,
                                           IApprovalProvider?                 approver        = null) =>
        new(Path.Combine(Path.GetTempPath(), "tinyharness-session-persistence-tests"),
            new SessionModelIdentity("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions),
            Options(),
            client,
            tools ?? new ToolRegistry([]),
            SystemPrompt,
            permissions,
            approver,
            events,
            recorderFactory,
            sessionWriterFactory);

    [Test]
    public async Task SubmitTurn_HistoryEventsMatchPagedJournalItemsFieldForField()
    {
        using var dir    = new TestTempDir();
        var       client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("stored answer"));
        var       events = new TestHostEventSink();
        using var host   = NewHost(client, id => new SessionStoreWriter(dir.Root, id), events : events);

        var run = await host.SubmitTurnAsync("question", CancellationToken.None);
        var page = await new SessionHistoryReader().ReadPageAsync(dir.Root, host.Session.Id,
                                                                  SessionHistoryReader.MaxPageItems, null,
                                                                  CancellationToken.None);
        var liveItems = events.Events.Where(@event => @event.Kind == HostEventKind.HistoryItemCompleted)
                              .Select(@event => TestAssert.TypeOf<SessionHistoryItem>(@event.Payload)).ToArray();

        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(page.Items.Select(item => item.Kind), Is.EqualTo(new[] { "user", "final" }));
        Assert.That(liveItems, Is.EqualTo(page.Items));
        Assert.That(page.Items.All(item => item.Sequence > 0 && item.Id.Length > 0), Is.True);
        Assert.That(host.PersistenceWarning, Is.Null);
        Assert.That(host.DisplayWarning, Is.Null);
    }

    [Test]
    public async Task SubmitTurn_HistoryDisplayFailureIsRedactedAndSeparateFromPersistence()
    {
        using var    dir    = new TestTempDir();
        const string secret = "display-only-secret";
        var          client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("stored answer"));
        var secrets = new Dictionary<string, string> { ["KEY"] = secret };
        using var host = NewHost(client, id => new SessionStoreWriter(dir.Root, id, secrets),
                                 events : new ThrowOnHistoryItemSink(secret));

        var run = await host.SubmitTurnAsync("question", CancellationToken.None);
        var page = await new SessionHistoryReader().ReadPageAsync(dir.Root, host.Session.Id,
                                                                  SessionHistoryReader.MaxPageItems, null,
                                                                  CancellationToken.None);

        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(host.PersistenceWarning, Is.Null);
        Assert.That(host.DisplayWarning, Does.Contain("[REDACTED]"));
        TestAssert.DoesNotContain(secret, host.DisplayWarning!, StringComparison.Ordinal);
        Assert.That(page.Items.Select(item => item.Kind), Is.EqualTo(new[] { "user", "final" }));
        Assert.That(page.Items[^1].Text, Is.EqualTo("stored answer"));
    }

    [Test]
    public async Task SubmitTurn_PreparationFailureIsSavedAsKnownNotStartedHistoryAfterRestart()
    {
        using var dir    = new TestTempDir();
        var       client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("missing", "{\"token\":\"hidden\"}"));
        client.Enqueue(FakeChatClient.Text("tool unavailable"));
        var host      = NewHost(client, id => new SessionStoreWriter(dir.Root, id));
        var run       = await host.SubmitTurnAsync("try missing tool", CancellationToken.None);
        var callId    = run.Result!.ToolCalls!.Single().CallId;
        var sessionId = host.Session.Id;
        host.Dispose();

        var page = await new SessionHistoryReader().ReadPageAsync(dir.Root, sessionId,
                                                                  SessionHistoryReader.MaxPageItems, null,
                                                                  CancellationToken.None);
        var toolItem = page.Items.Single(item => item.Kind == SessionHistoryKind.Tool);

        Assert.That(run.Result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(run.Result.ToolExecutions, Is.Zero);
        Assert.That(toolItem.CallId, Is.EqualTo(callId));
        Assert.That(toolItem.State, Is.EqualTo(nameof(ToolCallExecutionState.KnownNotStarted)));
        Assert.That(toolItem.PreparationError, Does.Contain("Unknown tool"));
        Assert.That(toolItem.Succeeded, Is.Null);
        Assert.That(toolItem.ExitCode, Is.Null);
        TestAssert.DoesNotContain("hidden", toolItem.ArgumentsJson, StringComparison.Ordinal);
    }

    [Test]
    public async Task SubmitTurn_DenialIsSavedImmediatelyAndToolNeverStarts()
    {
        using var dir    = new TestTempDir();
        var       client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("write_test", "{\"path\":\"file.txt\"}"));
        client.Enqueue(FakeChatClient.Text("permission denied"));
        var tool = new AuditTestTool(dir.Root, "filesystem.write");
        var host = NewHost(client, id => new SessionStoreWriter(dir.Root, id),
                           tools : new ToolRegistry([tool]), permissions : new PermissionEngine(dir.Root),
                           approver : new DenyApprover());

        var run       = await host.SubmitTurnAsync("try write", CancellationToken.None);
        var sessionId = host.Session.Id;
        host.Dispose();
        var page = await new SessionHistoryReader().ReadPageAsync(dir.Root, sessionId,
                                                                  SessionHistoryReader.MaxPageItems, null,
                                                                  CancellationToken.None);
        var denied = page.Items.Single(item => item.Kind == SessionHistoryKind.Tool);

        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(tool.ExecuteCount, Is.Zero);
        Assert.That(denied.State, Is.EqualTo(nameof(ToolCallExecutionState.KnownNotStarted)));
        Assert.That(denied.Approval, Does.Contain("Permission denied by user"));
        Assert.That(denied.Succeeded, Is.Null);
        Assert.That(denied.ExitCode, Is.Null);
    }

    [Test]
    public async Task SubmitTurn_PersistsHostToolPolicyPermissionAndResultAuditInSharedLog()
    {
        using var dir    = new TestTempDir();
        var       client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("audit_test", "{}"));
        client.Enqueue(FakeChatClient.Text("done"));
        var tool = new AuditTestTool(dir.Root, "filesystem.read");
        var host = NewHost(client, id => new SessionStoreWriter(dir.Root, id),
                           tools : new ToolRegistry([tool]), permissions : new PermissionEngine(dir.Root));

        var run       = await host.SubmitTurnAsync("run read", CancellationToken.None);
        var sessionId = host.Session.Id;
        host.Dispose();
        var log = await File.ReadAllTextAsync(Path.Combine(dir.Root, sessionId + SessionStoreWriter.LogFileSuffix));
        var auditRecords = log.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                              .Select(line =>
                                          JsonSerializer.Deserialize(line,
                                                                     SessionLogJsonContext.Default.SessionLogBatch)!)
                              .SelectMany(batch => batch.Records)
                              .Where(record => record.Kind == "audit")
                              .Select(record => record.Payload.Deserialize(PersistenceJsonContext.Default.AuditRecord)!)
                              .ToArray();

        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(tool.ExecuteCount, Is.EqualTo(1));
        Assert.That(auditRecords.Any(record => record.Kind == "tool.prepared" &&
                                               record.Capability == "filesystem.read" &&
                                               record.ExecutionPolicy == "backend=test"), Is.True);
        Assert.That(auditRecords.Any(record => record.Kind     == "tool.permission" &&
                                               record.Decision == "Allow" && record.ExecutionPolicy == "backend=test"),
                    Is.True);
        Assert.That(auditRecords.Any(record => record.Kind      == "tool.result" &&
                                               record.Succeeded == true && record.ExecutionPolicy == "backend=test"),
                    Is.True);
    }

    [Test]
    public async Task SubmitTurn_CommitsASnapshotAfterEachFinishedTurn()
    {
        using var dir    = new TestTempDir();
        var       client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("first answer"));
        client.Enqueue(FakeChatClient.Text("second answer"));

        using var host = NewHost(client,
                                 id => new SessionStoreWriter(dir.Root, id));
        var run1 = await host.SubmitTurnAsync("what is this repo?", CancellationToken.None);
        var run2 = await host.SubmitTurnAsync("and why?", CancellationToken.None);

        Assert.That(run1.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(run2.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(host.PersistenceWarning, Is.Null);

        var read = await SessionStoreReader.ReadAsync(dir.Root, host.Session.Id, CancellationToken.None);
        TestAssert.NotNull(read);
        Assert.That(read!.CommitSequence, Is.GreaterThan(2));
        Assert.That(read.Turns.Count, Is.EqualTo(2));
        Assert.That(read.Turns[0].UserInput, Is.EqualTo("what is this repo?"));
        Assert.That(read.Turns[0].Runs.Single().FinalMessage, Is.EqualTo("first answer"));
        Assert.That(read.Turns[0].Runs.Single().Status, Is.EqualTo("Completed"));
        Assert.That(read.Turns[1].UserInput, Is.EqualTo("and why?"));
    }

    [Test]
    public async Task SubmitTurn_PreservesProtocolSecretsWhileInspectionAndAuditStayRedacted()
    {
        using var    dir           = new TestTempDir();
        const string secret        = "sk-test-secret-value";
        const string numericSecret = "424242";
        const string opaque        = " \n{ \"encrypted_content\" : \"sk-test-secret-value\", \"number\" : 424242 }\n ";
        const string itemId        = "item_sk-test-secret-value_424242";
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MY_API_KEY"] = secret, ["NUMERIC_SECRET"] = numericSecret
        };
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ReasonedText("reason " + secret, itemId, opaque,
                                                   "answer " + secret + " " + numericSecret));
        client.Enqueue(FakeChatClient.Text("next answer"));
        using var host = NewHost(client, id => new SessionStoreWriter(dir.Root, id, secrets));
        await host.SubmitTurnAsync("question " + secret + " " + numericSecret, CancellationToken.None);
        await host.SubmitTurnAsync("follow up", CancellationToken.None);
        Assert.That(host.PersistenceWarning, Is.Null);

        var read = await SessionStoreReader.ReadAsync(dir.Root, host.Session.Id, CancellationToken.None);
        TestAssert.NotNull(read);
        Assert.That(read.Turns[0].UserInput, Is.EqualTo("question [REDACTED] [REDACTED]"));
        Assert.That(read.Turns[0].Runs.Single().FinalMessage, Is.EqualTo("answer [REDACTED] [REDACTED]"));
        var conversation = read.Conversation!;
        TestAssert.ContainsAll(m => m.Role    == "User" &&
                                    m.Content == "question " + secret + " " + numericSecret, conversation.Messages);
        var stored          = conversation.Messages.Single(m => m.Content == "answer " + secret + " " + numericSecret);
        var storedReasoning = TestAssert.Single(stored.Reasoning!);
        Assert.That(storedReasoning.ProtectedData, Is.EqualTo(opaque));
        Assert.That(storedReasoning.ItemId, Is.EqualTo(itemId));
        Assert.That(storedReasoning.Text, Is.EqualTo("reason " + secret));
        var sent = client.RequestLog[1].Messages.Single(m => m.Role == ChatRole.Assistant);
        Assert.That(sent.Content, Is.EqualTo(stored.Content));
        var sentReasoning = TestAssert.Single(sent.Reasoning!);
        Assert.That(sentReasoning.ProtectedData, Is.EqualTo(opaque));
        Assert.That(sentReasoning.ItemId, Is.EqualTo(itemId));
        Assert.That(sentReasoning.Text, Is.EqualTo("reason "     + secret));
        var journalPath = Path.Combine(dir.Root, host.Session.Id + SessionStoreWriter.LogFileSuffix);
        Assert.That(File.Exists(journalPath), Is.True);
        var journal = await ReadSharedTextAsync(journalPath);
        var lines   = journal.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var batch = JsonNode.Parse(line)!;
            foreach (var record in batch["records"]!.AsArray())
            {
                if (record!["kind"]!.GetValue<string>() is not ("audit" or "history")) continue;
                var payload = record["payload"]!.ToJsonString();
                TestAssert.DoesNotContain(secret, payload, StringComparison.Ordinal);
                TestAssert.DoesNotContain(numericSecret, payload, StringComparison.Ordinal);
                TestAssert.DoesNotContain("encrypted_content", payload, StringComparison.Ordinal);
            }
        }
    }

    private static async Task<string> ReadSharedTextAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var       reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [Test]
    public async Task SubmitTurn_FailedTurnIsStillCommittedAtItsBoundary()
    {
        using var dir    = new TestTempDir();
        var       client = new FakeChatClient { ThrownError = new InvalidOperationException("model exploded") };

        using var host = NewHost(client,
                                 id => new SessionStoreWriter(dir.Root, id));
        var run = await host.SubmitTurnAsync("please fail", CancellationToken.None);

        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Failed));
        var read = await SessionStoreReader.ReadAsync(dir.Root, host.Session.Id, CancellationToken.None);
        TestAssert.NotNull(read);
        TestAssert.Single(read.Turns);
        Assert.That(read.Turns[0].Runs.Single().Status, Is.EqualTo("Failed"));
        TestAssert.NotNull(read.Turns[0].Runs.Single().Error);
    }

    [Test]
    public async Task SubmitTurn_PersistenceFailureBecomesWarningAndKeepsTheTerminalState()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("answer"));

        using var host = NewHost(client, _ => new ThrowingSessionWriter());
        var       run  = await host.SubmitTurnAsync("anything", CancellationToken.None);

        // 保存故障单独报告，不把已完成的执行改写为失败。
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        TestAssert.NotNull(host.PersistenceWarning);
        TestAssert.Contains("disk on fire", host.PersistenceWarning, StringComparison.Ordinal);
    }


    [TestCase(false)]
    [TestCase(true)]
    public async Task SubmitTurn_SideEffectExecuteSeesDurableInflightIntentAtACompleteBoundary(
        bool previouslyAcknowledged)
    {
        using var ws  = new TestTempDir();
        using var dir = new TestTempDir();
        var identity =
            new SessionModelIdentity("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions);
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("previous answer"));
        client.Enqueue(FakeChatClient.ToolCall("write", "{}", "call_confirmed"));
        client.Enqueue(FakeChatClient.ToolCall("slow", "{}", "call_inflight"));
        var     write     = new FakeTool("write");
        string? sessionId = null;
        var     gate      = new SnapshotGateTool(token => SessionStoreReader.ReadAsync(dir.Root, sessionId!, token));
        using var host = new ChatSessionHost(ws.Root, identity, Options(), client,
                                             new ToolRegistry([write, gate]), SystemPrompt,
                                             sessionWriterFactory : id => new SessionStoreWriter(dir.Root, id));
        sessionId = host.Session.Id;
        if (previouslyAcknowledged) host.Session.InspectionAcknowledgedUtc = DateTimeOffset.UtcNow;
        await host.SubmitTurnAsync("previous turn", CancellationToken.None);
        var previous = await SessionStoreReader.ReadAsync(dir.Root, sessionId, CancellationToken.None);
        TestAssert.NotNull(previous);

        using var        cts    = new CancellationTokenSource();
        using var        wait   = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var              active = host.SubmitTurnAsync("write twice", cts.Token);
        PersistedSession captured;
        try
        {
            // 在 Execute 报告启动前读取快照，模拟该边界崩溃；
            // 捕获内容不受后续结算影响，也不终止进程或重放工具。
            captured = await gate.WaitCapturedAsync(wait.Token);
            Assert.That(active.IsCompleted, Is.False);
            Assert.That(gate.ExecuteCount, Is.EqualTo(1));
            Assert.That(captured.CommitSequence > previous.CommitSequence, Is.True);
            Assert.That(captured.InspectionAcknowledgedUtc, Is.Null);
            TestAssert.NotNull(captured.PendingInspectionReason);
            var inflight = captured.Turns[^1].Runs.Single();
            Assert.That(inflight.Status, Is.Null);
            Assert.That(inflight.FinishedUtc, Is.Null);
            Assert.That(inflight.FinalMessage, Is.Null);
            Assert.That(inflight.Error, Is.Null);
            Assert.That(inflight.NeedsInspection, Is.True);
            Assert.That(inflight.ToolCalls!.Count, Is.EqualTo(2));
            Assert.That(inflight.ToolCalls[0].State, Is.EqualTo(nameof(ToolCallExecutionState.ConfirmedResult)));
            var intent = inflight.ToolCalls[1];
            Assert.That(intent.CallId, Is.EqualTo("call_inflight"));
            Assert.That(intent.ToolName, Is.EqualTo("slow"));
            Assert.That(intent.Capability, Is.EqualTo("filesystem.write"));
            Assert.That(intent.SideEffects, Is.True);
            Assert.That(intent.State, Is.EqualTo(nameof(ToolCallExecutionState.UnknownResult)));

            var canonical = captured.Conversation!.Messages;
            Assert.That(canonical.Select(m => m.Role).ToArray(),
                        Is.EqualTo(["System", "User", "Assistant", "User", "Assistant", "Tool"]));
            Assert.That(canonical[2].Content, Is.EqualTo("previous answer"));
            Assert.That(TestAssert.Single(canonical[4].ToolCalls!).Id, Is.EqualTo("call_confirmed"));
            Assert.That(canonical[5].ToolCallId, Is.EqualTo("call_confirmed"));
            TestAssert.DoesNotContainAny(m => m.ToolCalls?.Any(c => c.Id == "call_inflight") == true, canonical);
            TestAssert.DoesNotContainAny(m => m.ToolCallId == "call_inflight", canonical);

            var restored = SessionResume.Restore(captured, ws.Root, identity, SystemPrompt);
            Assert.That(restored.RewoundTail, Is.False);
            TestAssert.NotNull(restored.NeedsInspection);
            var unfinished = restored.Session.Turns[^1].Runs.Single();
            Assert.That(unfinished.IsFinished, Is.False);
            Assert.That(unfinished.Result, Is.Null);
            Assert.That(unfinished.FinishedUtc, Is.Null);
            Assert.That(unfinished.ExecutionRecords![1].State, Is.EqualTo(ToolCallExecutionState.UnknownResult));
            Assert.That(ChatHistoryInvariants.LastCompleteBoundary(restored.Context.Messages),
                        Is.EqualTo(restored.Context.Messages.Count));

            var nextClient = new FakeChatClient();
            nextClient.Enqueue(FakeChatClient.ToolCall("write", "{}", "call_new"));
            nextClient.Enqueue(FakeChatClient.Text("blocked after crash"));
            var nextWrite = new FakeTool("write");
            using var resumed = new ChatSessionHost(restored.Session, restored.Context, Options(), nextClient,
                                                    new ToolRegistry([nextWrite]), SystemPrompt,
                                                    needsInspection : restored.NeedsInspection);
            Assert.That(resumed.InspectionPending, Is.True);
            var blocked = await resumed.SubmitTurnAsync("continue after crash", CancellationToken.None);
            Assert.That(blocked.Result!.Status, Is.EqualTo(AgentStatus.Completed));
            Assert.That(nextWrite.ExecuteCount, Is.EqualTo(0));
            TestAssert.Contains("pending inspection", TestAssert.Single(blocked.Result.ToolCalls!).Approval,
                                StringComparison.Ordinal);
        }
        finally
        {
            cts.Cancel();
            await active;
        }

        var terminal = await SessionStoreReader.ReadAsync(dir.Root, sessionId, CancellationToken.None);
        TestAssert.NotNull(terminal);
        Assert.That(terminal.CommitSequence > captured.CommitSequence, Is.True);
        Assert.That(terminal.Turns[^1].Runs.Single().Status, Is.EqualTo(nameof(AgentStatus.Cancelled)));
        // 结算不得修改已捕获的中断快照或补造终态。
        Assert.That(captured.Turns[^1].Runs.Single().Status, Is.Null);
        Assert.That(captured.Turns[^1].Runs.Single().ToolCalls![1].State,
                    Is.EqualTo(nameof(ToolCallExecutionState.UnknownResult)));
    }

    [Test]
    public async Task SubmitTurn_CheckpointFailureNeverExecutesTheSideEffectAndRecordsKnownNotStarted()
    {
        using var ws = new TestTempDir();
        var identity =
            new SessionModelIdentity("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions);
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("write", "{}"));
        var tool = new FakeTool("write");
        using var host = new ChatSessionHost(ws.Root, identity, Options(), client,
                                             new ToolRegistry([tool]), SystemPrompt,
                                             sessionWriterFactory : _ => new ThrowingSessionWriter());

        var run = await host.SubmitTurnAsync("write only after durable intent", CancellationToken.None);

        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(run.IsFinished, Is.True);
        Assert.That(run.Result.ToolExecutions, Is.EqualTo(0));
        Assert.That(tool.ExecuteCount, Is.EqualTo(0));
        Assert.That(client.Requests, Is.EqualTo(1));
        TestAssert.Contains("not reliably saved", run.Result.Error, StringComparison.Ordinal);
        TestAssert.Contains("disk on fire", run.Result.Error, StringComparison.Ordinal);
        var record = TestAssert.Single(run.Result.ToolCalls!);
        Assert.That(record.SideEffects, Is.True);
        Assert.That(record.State, Is.EqualTo(ToolCallExecutionState.KnownNotStarted));
        Assert.That(host.InspectionPending, Is.False);
        Assert.That(host.Session.PendingInspectionReason, Is.Null);
        TestAssert.NotNull(host.PersistenceWarning);
    }

    [Test]
    public async Task SubmitTurn_PersistenceWarningIsClearedByTheNextSuccessfulCommit()
    {
        using var dir    = new TestTempDir();
        var       client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("first answer"));
        client.Enqueue(FakeChatClient.Text("second answer"));

        using var host = NewHost(client, _ => new FlakySessionWriter(dir.Root));
        await host.SubmitTurnAsync("first", CancellationToken.None);
        TestAssert.NotNull(host.PersistenceWarning);
        TestAssert.Contains("flaky storage", host.PersistenceWarning, StringComparison.Ordinal);

        await host.SubmitTurnAsync("second", CancellationToken.None);
        Assert.That(host.PersistenceWarning, Is.Null);

        var read = await SessionStoreReader.ReadAsync(dir.Root, host.Session.Id, CancellationToken.None);
        TestAssert.NotNull(read);
        Assert.That(read!.CommitSequence, Is.EqualTo(1));
        Assert.That(read.Turns.Count, Is.EqualTo(2));
    }

    private sealed class ThrowOnHistoryItemSink(string secret) : IHostEventSink
    {
        public ValueTask PublishAsync(HostEvent @event, CancellationToken cancellationToken)
        {
            if (@event.Kind == HostEventKind.HistoryItemCompleted)
                throw new InvalidOperationException("display failed: " + secret);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DenyApprover : IApprovalProvider
    {
        public Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken) =>
            Task.FromResult(ApprovalAction.Deny);
    }

    private sealed class AuditTestTool(string workspaceRoot, string capability) : ITool
    {
        public int ExecuteCount { get; private set; }

        public ToolDefinition Definition { get; } = new()
        {
            Name        = capability == "filesystem.read" ? "audit_test" : "write_test",
            Description = "Test tool for persistent audit coverage.",
            Parameters  = new JsonObject { ["type"] = "object" }
        };

        public ToolPreparation Prepare(ChatToolCall call) => new()
        {
            ToolName        = Definition.Name,
            CallId          = call.Id,
            Arguments       = new JsonObject(),
            Capability      = capability,
            Summary         = "test operation",
            TargetPaths     = [Path.Combine(workspaceRoot, "file.txt")],
            ExecutionPolicy = "backend=test"
        };

        public Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            return Task.FromResult(new ToolResult { Succeeded = true, Content = "executed", ExitCode = 0 });
        }
    }

    private sealed class SnapshotGateTool(Func<CancellationToken, Task<PersistedSession?>> readSnapshot) : ITool
    {
        private readonly TaskCompletionSource<PersistedSession> _captured =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExecuteCount { get; private set; }

        public ToolDefinition Definition { get; } = new()
        {
            Name = "slow", Description = "Capture durable intent", Parameters = new JsonObject { ["type"] = "object" }
        };

        public ToolPreparation Prepare(ChatToolCall call) => new()
        {
            ToolName   = Definition.Name, CallId     = call.Id, Arguments = new JsonObject(),
            Capability = "filesystem.write", Summary = "slow()"
        };

        public async Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            _captured.TrySetResult(await readSnapshot(cancellationToken) ??
                                   throw new InvalidOperationException("No snapshot"));
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ToolResult { Succeeded = true, Content = "unreachable" };
        }

        public Task<PersistedSession> WaitCapturedAsync(CancellationToken token) => _captured.Task.WaitAsync(token);
    }

    private sealed class ThrowingSessionWriter : ISessionStoreWriter
    {
        public string SnapshotPath => throw new NotSupportedException();

        public long CommitSequence => 0;

        public Task CommitAsync(
            ChatSession session, ConversationContext context, CancellationToken cancellationToken) =>
            throw new SessionStoreException("disk on fire");

        public void Dispose()
        {
        }
    }

    /// <summary>
    ///     <para>首次提交失败、后续提交成功的写入端，用于验证成功保存后清除警告。</para>
    ///     <para>Fails the first commit and succeeds thereafter to verify that a successful save clears the warning.</para>
    /// </summary>
    private sealed class FlakySessionWriter(string directory) : ISessionStoreWriter
    {
        private bool                _failed;
        private SessionStoreWriter? _writer;

        public string SnapshotPath => _writer?.SnapshotPath ?? throw new NotSupportedException();

        public long CommitSequence => _writer?.CommitSequence ?? 0;

        public async Task CommitAsync(
            ChatSession session, ConversationContext context, CancellationToken cancellationToken)
        {
            if (!_failed)
            {
                _failed = true;
                throw new SessionStoreException("flaky storage");
            }

            _writer ??= new SessionStoreWriter(directory, session.Id);
            await _writer.CommitAsync(session, context, cancellationToken);
        }

        public void Dispose() => _writer?.Dispose();
    }
}

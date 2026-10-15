using System.Text.Json.Nodes;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Context;
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
                                           Func<string, IRunRecorder>?        recorderFactory = null) =>
        new(Path.Combine(Path.GetTempPath(), "tinyharness-session-persistence-tests"),
            new SessionModelIdentity("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions),
            Options(),
            client,
            new ToolRegistry([]),
            SystemPrompt,
            recorderFactory : recorderFactory,
            sessionWriterFactory : sessionWriterFactory);

    [Fact]
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

        Assert.Equal(AgentStatus.Completed, run1.Result!.Status);
        Assert.Equal(AgentStatus.Completed, run2.Result!.Status);
        Assert.Null(host.PersistenceWarning);

        var read = await SessionStoreReader.ReadAsync(dir.Root, host.Session.Id, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(2, read!.CommitSequence);
        Assert.Equal(2, read.Turns.Count);
        Assert.Equal("what is this repo?", read.Turns[0].UserInput);
        Assert.Equal("first answer", read.Turns[0].Runs.Single().FinalMessage);
        Assert.Equal("Completed", read.Turns[0].Runs.Single().Status);
        Assert.Equal("and why?", read.Turns[1].UserInput);
    }

    [Fact]
    public async Task SubmitTurn_PreservesProtocolSecretsWhileInspectionAndAuditStayRedacted()
    {
        using var    dir           = new TestTempDir();
        using var    runDir        = new TestTempDir();
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
        using var host = NewHost(client, id => new SessionStoreWriter(dir.Root, id, secrets),
                                 id => new FileRunRecorder(runDir.Root, id, secrets));
        await host.SubmitTurnAsync("question " + secret + " " + numericSecret, CancellationToken.None);
        await host.SubmitTurnAsync("follow up", CancellationToken.None);
        Assert.Null(host.PersistenceWarning);

        var read = await SessionStoreReader.ReadAsync(dir.Root, host.Session.Id, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(3, read.FormatVersion);
        Assert.Equal("question [REDACTED] [REDACTED]", read.Turns[0].UserInput);
        Assert.Equal("answer [REDACTED] [REDACTED]", read.Turns[0].Runs.Single().FinalMessage);
        var conversation = read.Conversation!;
        Assert.False(conversation.OpaqueRedacted);
        Assert.Contains(conversation.Messages, m => m.Role    == "User" &&
                                                    m.Content == "question " + secret + " " + numericSecret);
        var stored          = conversation.Messages.Single(m => m.Content == "answer " + secret + " " + numericSecret);
        var storedReasoning = Assert.Single(stored.Reasoning!);
        Assert.Equal(opaque, storedReasoning.ProtectedData);
        Assert.Equal(itemId, storedReasoning.ItemId);
        Assert.Equal("reason " + secret, storedReasoning.Text);
        var sent = client.RequestLog[1].Messages.Single(m => m.Role == ChatRole.Assistant);
        Assert.Equal(stored.Content, sent.Content);
        var sentReasoning = Assert.Single(sent.Reasoning!);
        Assert.Equal(opaque, sentReasoning.ProtectedData);
        Assert.Equal(itemId, sentReasoning.ItemId);
        Assert.Equal("reason " + secret, sentReasoning.Text);
        var audit = await File.ReadAllTextAsync(Path.Combine(dir.Root, $"{host.Session.Id}.audit.jsonl"));
        Assert.DoesNotContain(secret, audit, StringComparison.Ordinal);
        Assert.DoesNotContain(numericSecret, audit, StringComparison.Ordinal);
        var runAudits = Directory.GetFiles(runDir.Root, "*.audit.jsonl");
        Assert.Equal(2, runAudits.Length);
        foreach (var path in runAudits)
        {
            var runAudit = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(secret, runAudit, StringComparison.Ordinal);
            Assert.DoesNotContain(numericSecret, runAudit, StringComparison.Ordinal);
            Assert.DoesNotContain("encrypted_content", runAudit, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SubmitTurn_FailedTurnIsStillCommittedAtItsBoundary()
    {
        using var dir    = new TestTempDir();
        var       client = new FakeChatClient { ThrownError = new InvalidOperationException("model exploded") };

        using var host = NewHost(client,
                                 id => new SessionStoreWriter(dir.Root, id));
        var run = await host.SubmitTurnAsync("please fail", CancellationToken.None);

        Assert.Equal(AgentStatus.Failed, run.Result!.Status);
        var read = await SessionStoreReader.ReadAsync(dir.Root, host.Session.Id, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Single(read.Turns);
        Assert.Equal("Failed", read.Turns[0].Runs.Single().Status);
        Assert.NotNull(read.Turns[0].Runs.Single().Error);
    }

    [Fact]
    public async Task SubmitTurn_PersistenceFailureBecomesWarningAndKeepsTheTerminalState()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("answer"));

        using var host = NewHost(client, _ => new ThrowingSessionWriter());
        var       run  = await host.SubmitTurnAsync("anything", CancellationToken.None);

        // 保存故障单独报告，不把已完成的执行改写为失败。
        Assert.Equal(AgentStatus.Completed, run.Result!.Status);
        Assert.NotNull(host.PersistenceWarning);
        Assert.Contains("disk on fire", host.PersistenceWarning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
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
        Assert.NotNull(previous);

        using var        cts    = new CancellationTokenSource();
        using var        wait   = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var              active = host.SubmitTurnAsync("write twice", cts.Token);
        PersistedSession captured;
        try
        {
            // 在 Execute 报告启动前读取快照，模拟该边界崩溃；
            // 捕获内容不受后续结算影响，也不终止进程或重放工具。
            captured = await gate.WaitCapturedAsync(wait.Token);
            Assert.False(active.IsCompleted);
            Assert.Equal(1, gate.ExecuteCount);
            Assert.True(captured.CommitSequence > previous.CommitSequence);
            Assert.Null(captured.InspectionAcknowledgedUtc);
            Assert.NotNull(captured.PendingInspectionReason);
            var inflight = captured.Turns[^1].Runs.Single();
            Assert.Null(inflight.Status);
            Assert.Null(inflight.FinishedUtc);
            Assert.Null(inflight.FinalMessage);
            Assert.Null(inflight.Error);
            Assert.True(inflight.NeedsInspection);
            Assert.Equal(2, inflight.ToolCalls!.Count);
            Assert.Equal(nameof(ToolCallExecutionState.ConfirmedResult), inflight.ToolCalls[0].State);
            var intent = inflight.ToolCalls[1];
            Assert.Equal("call_inflight", intent.CallId);
            Assert.Equal("slow", intent.ToolName);
            Assert.Equal("filesystem.write", intent.Capability);
            Assert.True(intent.SideEffects);
            Assert.Equal(nameof(ToolCallExecutionState.UnknownResult), intent.State);

            var canonical = captured.Conversation!.Messages;
            Assert.Equal(["System", "User", "Assistant", "User", "Assistant", "Tool"],
                         canonical.Select(m => m.Role).ToArray());
            Assert.Equal("previous answer", canonical[2].Content);
            Assert.Equal("call_confirmed", Assert.Single(canonical[4].ToolCalls!).Id);
            Assert.Equal("call_confirmed", canonical[5].ToolCallId);
            Assert.DoesNotContain(canonical, m => m.ToolCalls?.Any(c => c.Id == "call_inflight") == true);
            Assert.DoesNotContain(canonical, m => m.ToolCallId                                   == "call_inflight");

            var restored = SessionResume.Restore(captured, ws.Root, identity, SystemPrompt);
            Assert.False(restored.RewoundTail);
            Assert.NotNull(restored.NeedsInspection);
            var unfinished = restored.Session.Turns[^1].Runs.Single();
            Assert.False(unfinished.IsFinished);
            Assert.Null(unfinished.Result);
            Assert.Null(unfinished.FinishedUtc);
            Assert.Equal(ToolCallExecutionState.UnknownResult, unfinished.ExecutionRecords![1].State);
            Assert.Equal(restored.Context.Messages.Count,
                         ChatHistoryInvariants.LastCompleteBoundary(restored.Context.Messages));

            var nextClient = new FakeChatClient();
            nextClient.Enqueue(FakeChatClient.ToolCall("write", "{}", "call_new"));
            nextClient.Enqueue(FakeChatClient.Text("blocked after crash"));
            var nextWrite = new FakeTool("write");
            using var resumed = new ChatSessionHost(restored.Session, restored.Context, Options(), nextClient,
                                                    new ToolRegistry([nextWrite]), SystemPrompt,
                                                    needsInspection : restored.NeedsInspection);
            Assert.True(resumed.InspectionPending);
            var blocked = await resumed.SubmitTurnAsync("continue after crash", CancellationToken.None);
            Assert.Equal(AgentStatus.Completed, blocked.Result!.Status);
            Assert.Equal(0, nextWrite.ExecuteCount);
            Assert.Contains("pending inspection", Assert.Single(blocked.Result.ToolCalls!).Approval,
                            StringComparison.Ordinal);
        }
        finally
        {
            cts.Cancel();
            await active;
        }

        var terminal = await SessionStoreReader.ReadAsync(dir.Root, sessionId, CancellationToken.None);
        Assert.NotNull(terminal);
        Assert.True(terminal.CommitSequence > captured.CommitSequence);
        Assert.Equal(nameof(AgentStatus.Cancelled), terminal.Turns[^1].Runs.Single().Status);
        // 结算不得修改已捕获的中断快照或补造终态。
        Assert.Null(captured.Turns[^1].Runs.Single().Status);
        Assert.Equal(nameof(ToolCallExecutionState.UnknownResult),
                     captured.Turns[^1].Runs.Single().ToolCalls![1].State);
    }

    [Fact]
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

        Assert.Equal(AgentStatus.Failed, run.Result!.Status);
        Assert.True(run.IsFinished);
        Assert.Equal(0, run.Result.ToolExecutions);
        Assert.Equal(0, tool.ExecuteCount);
        Assert.Equal(1, client.Requests);
        Assert.Contains("not reliably saved", run.Result.Error, StringComparison.Ordinal);
        Assert.Contains("disk on fire", run.Result.Error, StringComparison.Ordinal);
        var record = Assert.Single(run.Result.ToolCalls!);
        Assert.True(record.SideEffects);
        Assert.Equal(ToolCallExecutionState.KnownNotStarted, record.State);
        Assert.False(host.InspectionPending);
        Assert.Null(host.Session.PendingInspectionReason);
        Assert.NotNull(host.PersistenceWarning);
    }

    [Fact]
    public async Task SubmitTurn_PersistenceWarningIsClearedByTheNextSuccessfulCommit()
    {
        using var dir    = new TestTempDir();
        var       client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("first answer"));
        client.Enqueue(FakeChatClient.Text("second answer"));

        using var host = NewHost(client, _ => new FlakySessionWriter(dir.Root));
        await host.SubmitTurnAsync("first", CancellationToken.None);
        Assert.NotNull(host.PersistenceWarning);
        Assert.Contains("flaky storage", host.PersistenceWarning, StringComparison.Ordinal);

        await host.SubmitTurnAsync("second", CancellationToken.None);
        Assert.Null(host.PersistenceWarning);

        var read = await SessionStoreReader.ReadAsync(dir.Root, host.Session.Id, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(1, read!.CommitSequence);
        Assert.Equal(2, read.Turns.Count);
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

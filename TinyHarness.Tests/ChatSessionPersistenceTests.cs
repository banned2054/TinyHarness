using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     M13.1 会话持久化集成测试：ChatSessionHost 在每个回合唱到终态后把版本化快照提交到
///     受保护存储（完整回合边界，PLAN §7.1）；保存失败只成为 PersistenceWarning，
///     不改写回合终态、不中断会话。
///     M13.1 session-persistence integration tests: ChatSessionHost commits a
///     versioned snapshot to the protected store after every turn reaches its
///     terminal state (the complete turn boundary, PLAN §7.1); a failed save
///     only becomes a PersistenceWarning — it never rewrites the turn's terminal
///     state or interrupts the session.
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

        // The turn's terminal state is the execution truth; the storage fault is
        // reported separately and never turns a completed turn into a failure.
        Assert.Equal(AgentStatus.Completed, run.Result!.Status);
        Assert.NotNull(host.PersistenceWarning);
        Assert.Contains("disk on fire", host.PersistenceWarning, StringComparison.Ordinal);
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
    ///     首次提交失败的写入端：故障先成为 PersistenceWarning，第二次提交成功后警告必须被清除。
    ///     A writer whose first commit fails: the fault becomes the PersistenceWarning first,
    ///     and the warning must be cleared once the second commit succeeds.
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

using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;
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
                                           Func<string, ISessionStoreWriter>? sessionWriterFactory) =>
        new(
            Path.Combine(Path.GetTempPath(), "tinyharness-session-persistence-tests"),
            new SessionModelIdentity("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions),
            Options(),
            client,
            new ToolRegistry([]),
            SystemPrompt,
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

        public Task CommitAsync(ChatSession session, CancellationToken cancellationToken) =>
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

        public async Task CommitAsync(ChatSession session, CancellationToken cancellationToken)
        {
            if (!_failed)
            {
                _failed = true;
                throw new SessionStoreException("flaky storage");
            }

            _writer ??= new SessionStoreWriter(directory, session.Id);
            await _writer.CommitAsync(session, cancellationToken);
        }

        public void Dispose() => _writer?.Dispose();
    }
}

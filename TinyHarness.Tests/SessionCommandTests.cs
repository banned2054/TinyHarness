using System.Text;
using System.Text.Json;
using TinyHarness.Cli.Commands;
using TinyHarness.Cli.Models;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         验证严格离线的 session list/show：不解析凭据、调用模型或启动工具；
///         报告或拒绝损坏条目，缺失 ID 返回 1。
///     </para>
///     <para>
///         Verifies strictly offline session list/show without resolving credentials, calling models or starting tools;
///         reports or rejects damaged entries and returns 1 for missing IDs.
///     </para>
/// </summary>
public class SessionCommandTests
{
    [Test]
    public async Task Show_PreservesLocalContentWithoutReadingCredentialsOrOpaque()
    {
        using var    fixture = new SessionCliFixture();
        const string secret  = "synthetic-local-history-key";
        var          client  = new FakeChatClient();
        client.Enqueue(FakeChatClient.ReasonedText("inspect " + secret, "opaque-id", "opaque-value",
                                                   "answer "  + secret + "\u001b[2J"));
        string sessionId;
        using (var host = new TinyHarness.Core.Services.Session.ChatSessionHost(
                                                                                fixture.Workspace,
                                                                                new SessionModelIdentity("test-model",
                                                                                    "https://example.invalid/v1",
                                                                                    ChatApiKind.ChatCompletions),
                                                                                new AgentOptions
                                                                                {
                                                                                    Model = "test-model",
                                                                                    MaxAgentSteps = 4,
                                                                                    DefaultToolTimeoutSeconds = 30
                                                                                },
                                                                                client,
                                                                                new TinyHarness.Core.Services.Tools.
                                                                                    ToolRegistry([]), "sys",
                                                                                sessionWriterFactory : id =>
                                                                                    new SessionStoreWriter(fixture
                                                                                           .StoreDirectory, id,
                                                                                        new Dictionary<string,
                                                                                            string>
                                                                                        {
                                                                                            ["key"] = secret
                                                                                        })))
        {
            sessionId = host.Session.Id;
            await host.SubmitTurnAsync("token=example " + secret, CancellationToken.None);
            Assert.That(host.PersistenceWarning, Is.Null);
        }

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("show", sessionId),
                                                     CancellationToken.None);
        Assert.That(exit, Is.Zero);
        Assert.That(fixture.Io.Output, Does.Contain("token=example " + secret));
        Assert.That(fixture.Io.Output, Does.Contain("answer "        + secret + "\\u001B[2J"));
        Assert.That(fixture.Io.Output, Does.Not.Contain("opaque-id").And.Not.Contain("opaque-value"));
    }

    [Test]
    public async Task List_ShowsStoredSessionsOffline()
    {
        using var fixture = new SessionCliFixture();
        var       session = await CommitStoredSessionAsync(fixture.StoreDirectory, "why did it fail?", "the fix");

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("list"), CancellationToken.None);

        Assert.That(exit, Is.EqualTo(0));
        TestAssert.Contains(session.Id, fixture.Io.Output, StringComparison.Ordinal);
        TestAssert.Contains("turns 1", fixture.Io.Output, StringComparison.Ordinal);
        TestAssert.DoesNotContain("Run snapshots", fixture.Io.Output, StringComparison.Ordinal);
    }

    [Test]
    public async Task List_ReportsDamagedEntriesInsteadOfFailing()
    {
        using var fixture = new SessionCliFixture();
        Directory.CreateDirectory(fixture.StoreDirectory);
        await File.WriteAllTextAsync(Path.Combine(fixture.StoreDirectory,
                                                  $"{new string('f', 32)}{SessionStoreWriter.LogFileSuffix}"),
                                     "not json");

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("list"), CancellationToken.None);

        Assert.That(exit, Is.EqualTo(0));
        TestAssert.Contains("damaged", fixture.Io.Output, StringComparison.Ordinal);
    }

    [Test]
    public async Task List_ReportsJournalFilesWhoseNameIsNotASessionId()
    {
        using var fixture = new SessionCliFixture();
        Directory.CreateDirectory(fixture.StoreDirectory);
        await File.WriteAllTextAsync(Path.Combine(fixture.StoreDirectory, "not-a-session-id.session.jsonl"), "{}");

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("list"), CancellationToken.None);

        Assert.That(exit, Is.EqualTo(0));
        TestAssert.Contains("not-a-session-id", fixture.Io.Output, StringComparison.Ordinal);
        TestAssert.Contains("not a valid session id", fixture.Io.Output, StringComparison.Ordinal);
        TestAssert.DoesNotContain("(none)", fixture.Io.Output, StringComparison.Ordinal);
    }

    [Test]
    public async Task Show_PrintsTheStoredSessionDetails()
    {
        using var fixture = new SessionCliFixture();
        var       session = await CommitStoredSessionAsync(fixture.StoreDirectory, "why did it fail?", "the fix");

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("show", session.Id),
                                                     CancellationToken.None);

        Assert.That(exit, Is.EqualTo(0));
        TestAssert.Contains($"session         : {session.Id}", fixture.Io.Output, StringComparison.Ordinal);
        TestAssert.Contains($"format          : {SessionLogFormat.FormatName}", fixture.Io.Output,
                            StringComparison.Ordinal);
        TestAssert.Contains("conversation    : 0 messages (verbatim protocol stored; resume checks required)",
                            fixture.Io.Output, StringComparison.Ordinal);
        TestAssert.Contains("input : why did it fail?", fixture.Io.Output, StringComparison.Ordinal);
        TestAssert.Contains("final : the fix", fixture.Io.Output, StringComparison.Ordinal);
    }

    [Test]
    public async Task Show_RendersPagedHistoryIncludingToolDiff()
    {
        using var fixture = new SessionCliFixture();
        var session = await CommitStoredSessionAsync(fixture.StoreDirectory, "question", "answer",
        [
            new ToolCallRecord
            {
                CallId = "call", ToolName = "apply_patch", SideEffects = true,
                State  = ToolCallExecutionState.ConfirmedResult
            }
        ]);
        var turn = session.Turns.Single();
        var run  = turn.Runs.Single();
        using (var writer = await SessionStoreWriter.OpenExistingAsync(fixture.StoreDirectory, session.Id, null,
                                                                       CancellationToken.None))
        {
            var items = new[]
                {
                    new SessionHistoryItem
                    {
                        Id   = "", Sequence = 0, SessionId = session.Id, TurnId = turn.Id, RunId = run.Id,
                        Kind = SessionHistoryKind.User, Text = "question"
                    },
                    new SessionHistoryItem
                    {
                        Id   = "", Sequence = 0, SessionId = session.Id, TurnId = turn.Id, RunId = run.Id,
                        Kind = SessionHistoryKind.Process, Text = "checking"
                    },
                    new SessionHistoryItem
                    {
                        Id   = "", Sequence = 0, SessionId = session.Id, TurnId = turn.Id, RunId = run.Id,
                        Kind = SessionHistoryKind.Final, Text = "answer"
                    },
                    new SessionHistoryItem
                    {
                        Id    = "", Sequence = 0, SessionId = session.Id, TurnId = turn.Id, RunId = run.Id,
                        Kind  = SessionHistoryKind.Tool, CallId = "call", ToolName = "apply_patch",
                        State = nameof(ToolCallExecutionState.ConfirmedResult), Succeeded = true,
                        PatchChanges =
                        [
                            new PatchChange("src/file.cs", "modify", "@@ -1 +1 @@\n-old\n+new",
                                            PatchChange.ConfirmedState)
                        ]
                    }
                }.Select(item =>
                             new SessionLogRecord("", "history",
                                                  JsonSerializer.SerializeToElement(
                                                   item,
                                                   SessionHistoryJsonContext.Default.SessionHistoryItem)))
                 .ToArray();
            await writer.AppendRecordsAsync(items, CancellationToken.None);
        }

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("show", session.Id),
                                                     CancellationToken.None);

        Assert.That(exit, Is.EqualTo(0), fixture.Io.Error);
        TestAssert.Contains("history (append-only JSONL)", fixture.Io.Output, StringComparison.Ordinal);
        TestAssert.Contains("process", fixture.Io.Output, StringComparison.Ordinal);
        TestAssert.Contains("final", fixture.Io.Output, StringComparison.Ordinal);
        TestAssert.Contains("apply_patch", fixture.Io.Output, StringComparison.Ordinal);
        TestAssert.Contains("src/file.cs", fixture.Io.Output, StringComparison.Ordinal);
        TestAssert.Contains("@@ -1 +1 @@", fixture.Io.Output, StringComparison.Ordinal);
        TestAssert.Contains("+new", fixture.Io.Output, StringComparison.Ordinal);
    }


    [Test]
    public async Task Show_NeverPrintsVerbatimConversationSecrets()
    {
        using var    fixture = new SessionCliFixture();
        const string secret  = "synthetic-private-conversation-value";
        var          session = await CommitStoredSessionAsync(fixture.StoreDirectory, "question", "answer");
        var stored = session with
        {
            Conversation = new PersistedConversation
            {
                Messages = [new PersistedMessage { Role = nameof(ChatRole.User), Content = secret }],
                State    = new CompactionState { Summary = secret }
            }
        };
        DeleteJournal(session.Id, fixture.StoreDirectory);
        await WriteJournalProjectionAsync(fixture.StoreDirectory, stored);

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("show", session.Id),
                                                     CancellationToken.None);

        Assert.That(exit, Is.EqualTo(0));
        TestAssert.DoesNotContain(secret, fixture.Io.Output, StringComparison.Ordinal);
        TestAssert.DoesNotContain(secret, fixture.Io.Error, StringComparison.Ordinal);
        TestAssert.Contains("input : question", fixture.Io.Output, StringComparison.Ordinal);
    }

    [Test]
    public async Task Show_PrintsExecutionRecordsAndTheNeedsInspectionState()
    {
        using var fixture = new SessionCliFixture();
        var       session = await CommitStoredSessionAsync(fixture.StoreDirectory, "fix it", "interrupted work");
        var stored = session with
        {
            Turns =
            [
                session.Turns[0] with
                {
                    Runs =
                    [
                        new PersistedRun
                        {
                            Id              = session.Turns[0].Runs[0].Id,
                            StartedUtc      = DateTimeOffset.UtcNow.AddMinutes(-4),
                            FinishedUtc     = DateTimeOffset.UtcNow.AddMinutes(-3),
                            Status          = nameof(AgentStatus.Cancelled),
                            NeedsInspection = true,
                            ToolCalls =
                            [
                                new PersistedToolCallRecord
                                {
                                    CallId      = "call_1",
                                    ToolName    = "apply_patch",
                                    Capability  = "filesystem.write",
                                    SideEffects = true,
                                    Summary     = "write src/a.cs",
                                    Approval    = "allow once",
                                    State       = nameof(ToolCallExecutionState.UnknownResult)
                                },
                                new PersistedToolCallRecord
                                {
                                    CallId     = "call_2",
                                    ToolName   = "read_file",
                                    Capability = "filesystem.read",
                                    Summary    = "read src/b.cs",
                                    Approval   = "allow",
                                    State      = nameof(ToolCallExecutionState.KnownNotStarted)
                                }
                            ]
                        }
                    ]
                }
            ]
        };
        DeleteJournal(session.Id, fixture.StoreDirectory);
        await WriteJournalProjectionAsync(fixture.StoreDirectory, stored);

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("show", session.Id),
                                                     CancellationToken.None);

        Assert.That(exit, Is.EqualTo(0));
        TestAssert.Contains("inspection      : needs inspection (pending; side effects blocked until confirmed on resume)",
                            fixture.Io.Output, StringComparison.Ordinal);
        TestAssert.Contains("inspection : a side effect was possibly executed with an unknown result",
                            fixture.Io.Output, StringComparison.Ordinal);
        TestAssert.Contains("tool apply_patch: allow once — result unknown; side effect possible", fixture.Io.Output,
                            StringComparison.Ordinal);
        TestAssert.Contains("tool read_file: allow — known not started", fixture.Io.Output, StringComparison.Ordinal);
    }

    [Test]
    public async Task Show_AcknowledgedInspectionDoesNotClaimCurrentBlocking()
    {
        using var fixture = new SessionCliFixture();
        var       session = await CommitStoredSessionAsync(fixture.StoreDirectory, "q", "a");
        var snapshot = session with
        {
            InspectionAcknowledgedUtc = DateTimeOffset.UtcNow,
            Turns = [session.Turns[0] with { Runs = [session.Turns[0].Runs[0] with { NeedsInspection = true }] }]
        };
        DeleteJournal(session.Id, fixture.StoreDirectory);
        await WriteJournalProjectionAsync(fixture.StoreDirectory, snapshot);
        Assert.That(await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("show", session.Id),
                                                      CancellationToken.None), Is.EqualTo(0));
        Assert.That(fixture.Io.Output, Does.Contain("acknowledged"));
        Assert.That(fixture.Io.Output, Does.Contain("historical fact"));
        Assert.That(fixture.Io.Output, Does.Not.Contain("blocked on resume until"));
    }

    [Test]
    public async Task Show_RejectsDamagedJournal()
    {
        using var fixture = new SessionCliFixture();
        var       session = await CommitStoredSessionAsync(fixture.StoreDirectory, "q", "a");
        var       journal = Path.Combine(fixture.StoreDirectory, session.Id + SessionStoreWriter.LogFileSuffix);
        await File.WriteAllTextAsync(journal, "not a session log\n");

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("show", session.Id),
                                                     CancellationToken.None);

        Assert.That(exit, Is.EqualTo(1));
        TestAssert.Contains("damaged", fixture.Io.Error, StringComparison.OrdinalIgnoreCase);
        TestAssert.DoesNotContain("legacy; inspection-only", fixture.Io.Output, StringComparison.Ordinal);
    }

    [Test]
    public async Task ListAndShow_IgnoreOldFilesAndLeaveThemUntouched()
    {
        using var fixture = new SessionCliFixture();
        var       id      = Guid.NewGuid().ToString("N");
        var paths = new[]
        {
            Path.Combine(fixture.StoreDirectory, id     + ".session.json"),
            Path.Combine(fixture.StoreDirectory, id     + ".audit.jsonl"),
            Path.Combine(fixture.RunOutputDirectory, id + ".session.json")
        };
        foreach (var path in paths)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "unreadable old data");
        }

        Assert.That(await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("list"),
                                                      CancellationToken.None), Is.Zero);
        Assert.That(fixture.Io.Output, Does.Contain("no stored sessions"));
        Assert.That(fixture.Io.Output, Does.Not.Contain(id));
        fixture.Io.Clear();
        Assert.That(await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("show", id),
                                                      CancellationToken.None), Is.EqualTo(1));
        Assert.That(fixture.Io.Error, Does.Contain("No session"));
        foreach (var path in paths)
            Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo("unreadable old data"));
    }

    [Test]
    public async Task Show_UnknownIdReportsTheStoreDirectory()
    {
        using var fixture = new SessionCliFixture();

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("show", new string('0', 32)),
                                                     CancellationToken.None);

        Assert.That(exit, Is.EqualTo(1));
        TestAssert.Contains("No session", fixture.Io.Error, StringComparison.Ordinal);
        TestAssert.Contains(fixture.StoreDirectory, fixture.Io.Error, StringComparison.Ordinal);
    }

    private static CliOptions Options(string subcommand, string? primary = null) => new()
        { Kind = CliCommandKind.Session, Subcommand = subcommand, Primary = primary };

    private static async Task<PersistedSession> CommitStoredSessionAsync(
        string                         storeDirectory,          string  userInput, string finalMessage,
        IReadOnlyList<ToolCallRecord>? executionRecords = null, string? workspaceRoot = null)
    {
        var chatSession = new ChatSession(Guid.NewGuid().ToString("N"),
                                          workspaceRoot ??
                                          Path.Combine(Path.GetTempPath(), "tinyharness-session-command-ws"),
                                          new SessionModelIdentity("test-model", "https://example.invalid/v1",
                                                                   ChatApiKind.ChatCompletions));
        var turn = new SessionTurn(Guid.NewGuid().ToString("N"), userInput);
        var run  = new SessionRun(Guid.NewGuid().ToString("N"), chatSession.Id, turn.Id);
        chatSession.AddTurn(turn);
        turn.AddRun(run);
        if (executionRecords is { Count: > 0 }) run.RecordExecutionIntent(executionRecords);
        run.Finish(new AgentResult { Status = AgentStatus.Completed, FinalMessage = finalMessage, Steps = 1 });

        using var writer = new SessionStoreWriter(storeDirectory, chatSession.Id);
        await writer.CommitAsync(chatSession, new ConversationContext(), CancellationToken.None);
        var read = await SessionStoreReader.ReadAsync(storeDirectory, chatSession.Id, CancellationToken.None);
        return read!;
    }

    private static void DeleteJournal(string sessionId, string storeDirectory)
    {
        foreach (var path in new[]
                 {
                     Path.Combine(storeDirectory, sessionId + SessionStoreWriter.LogFileSuffix),
                     Path.Combine(storeDirectory, sessionId + ".lock")
                 })
            if (File.Exists(path))
                File.Delete(path);
    }

    private static async Task WriteJournalProjectionAsync(string directory, PersistedSession session)
    {
        var records = new List<SessionLogRecord>
        {
            new("", "identity",
                JsonSerializer.SerializeToElement(
                                                  new SessionLogIdentity(session.Id, session.WorkspaceRoot,
                                                                         session.Model!, session.CreatedUtc),
                                                  SessionLogJsonContext.Default.SessionLogIdentity))
        };
        foreach (var turn in session.Turns)
        {
            records.Add(new SessionLogRecord("", "turn", JsonSerializer.SerializeToElement(
                                              new SessionLogTurn(turn.Id, turn.UserInput, turn.CreatedUtc),
                                              SessionLogJsonContext.Default.SessionLogTurn)));
            foreach (var run in turn.Runs)
                records.Add(new SessionLogRecord("", "run", JsonSerializer.SerializeToElement(
                                                  new SessionLogRun(turn.Id, run),
                                                  SessionLogJsonContext.Default.SessionLogRun)));
        }

        if (session.Conversation is { } conversation)
        {
            foreach (var message in conversation.Messages)
                records.Add(new SessionLogRecord("", "message", JsonSerializer.SerializeToElement(
                                                  new SessionLogMessage(message),
                                                  SessionLogJsonContext.Default.SessionLogMessage)));
            records.Add(new SessionLogRecord("", "compaction-checkpoint", JsonSerializer.SerializeToElement(
                                              new SessionLogCheckpoint(conversation.State,
                                                                       conversation.HasState,
                                                                       conversation.FoldStartMessageIndex,
                                                                       conversation.FoldEndMessageIndex),
                                              SessionLogJsonContext.Default.SessionLogCheckpoint)));
        }

        records.Add(new SessionLogRecord("", "session-state", JsonSerializer.SerializeToElement(
                                          new SessionLogSessionState(session.InspectionAcknowledgedUtc,
                                                                     session.PendingInspectionReason),
                                          SessionLogJsonContext.Default.SessionLogSessionState)));
        // CLI 读取测试直接构造完整日志批次，避免借用旧快照格式。
        var batch = new SessionLogBatch
        {
            Format     = SessionLogFormat.FormatName,
            SessionId  = session.Id,
            Sequence   = 1,
            BatchId    = SessionStoreWriter.BatchId(session.Id, 1),
            WrittenUtc = session.UpdatedUtc,
            Records = records.Select((record, index) =>
                                         record with
                                         {
                                             Id = SessionStoreWriter.RecordId(session.Id, 1, index)
                                         }).ToArray()
        };
        await File.WriteAllTextAsync(Path.Combine(directory, session.Id + SessionStoreWriter.LogFileSuffix),
                                     JsonSerializer.Serialize(batch, SessionLogJsonContext.Default.SessionLogBatch) +
                                     "\n");
    }

    private sealed class SessionCliFixture : IDisposable
    {
        public SessionCliFixture()
        {
            Root               = Path.Combine(Path.GetTempPath(), $"tinyharness-session-cli-{Guid.NewGuid():N}");
            StoreDirectory     = Path.Combine(Root, "store");
            RunOutputDirectory = Path.Combine(Root, "run-output");
            Workspace          = Path.Combine(Root, "workspace");
            UserConfigPath     = Path.Combine(Root, "user-config.json");
            Io                 = new FakeCliConsole();
            Directory.CreateDirectory(Workspace);

            // 项目配置指定工作区和旧 run 快照目录；
            // 可信用户配置指定工作区外的受保护会话存储。
            File.WriteAllText(Path.Combine(Root, "tinyharness.json"),
                              $$"""
                              {
                                "endpoint": "https://example.invalid/v1",
                                "model": "test-model",
                                "workspaceRoot": {{JsonSerializer.Serialize(Workspace)}},
                                "sessionDirectory": {{JsonSerializer.Serialize(RunOutputDirectory)}}
                              }
                              """);
            File.WriteAllText(UserConfigPath,
                              $$"""
                              {
                                "settings": {
                                  "sessionStoreDirectory": {{JsonSerializer.Serialize(StoreDirectory)}}
                                }
                              }
                              """);
        }

        public string Root { get; }

        public string StoreDirectory { get; }

        public string RunOutputDirectory { get; }

        public string Workspace { get; }

        public string UserConfigPath { get; }

        public FakeCliConsole Io { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }

        public CommandContext CreateContext() => new()
        {
            Io               = Io,
            Credentials      = new FakeCredentialStore(),
            UserConfigPath   = UserConfigPath,
            WorkingDirectory = Root
        };
    }

    private sealed class FakeCliConsole : ICliConsole
    {
        private readonly StringBuilder _error  = new();
        private readonly StringBuilder _output = new();

        public string Output => _output.ToString();

        public string Error => _error.ToString();

        public bool IsInteractive => false;

        public Task WriteAsync(string text, CancellationToken cancellationToken)
        {
            _output.Append(text);
            return Task.CompletedTask;
        }

        public Task WriteLineAsync(string text, CancellationToken cancellationToken)
        {
            _output.AppendLine(text);
            return Task.CompletedTask;
        }

        public Task WriteErrorLineAsync(string text, CancellationToken cancellationToken)
        {
            _error.AppendLine(text);
            return Task.CompletedTask;
        }

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> ReadHiddenLineAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public void Clear()
        {
            _error.Clear();
            _output.Clear();
        }
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public bool IsSupported => false;

        public void Save(string targetName, string secret)
        {
        }

        public string? Read(string targetName) => null;

        public bool Delete(string targetName) => false;
    }
}

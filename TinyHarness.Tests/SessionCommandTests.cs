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
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         验证严格离线的 session list/show：不解析凭据、调用模型或启动工具；
///         报告或拒绝损坏条目，旧 run 快照只读展示，缺失 ID 返回 1。
///     </para>
///     <para>
///         Verifies strictly offline session list/show without resolving credentials, calling models or starting tools;
///         reports or rejects damaged entries, renders legacy run snapshots read-only and returns 1 for missing IDs.
///     </para>
/// </summary>
public class SessionCommandTests
{
    [Test]
    public async Task List_ShowsStoredSessionsAndLegacySnapshotsOffline()
    {
        using var fixture = new SessionCliFixture();
        var       session = await CommitStoredSessionAsync(fixture.StoreDirectory, "why did it fail?", "the fix");
        WriteLegacySnapshot(fixture.LegacyDirectory);

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("list"), CancellationToken.None);

        Assert.That(exit, Is.EqualTo(0));
        TestAssert.Contains(session.Id, fixture.Io.Output, StringComparison.Ordinal);
        TestAssert.Contains("turns 1", fixture.Io.Output, StringComparison.Ordinal);
        TestAssert.Contains("Run snapshots (legacy, read-only)", fixture.Io.Output, StringComparison.Ordinal);
    }

    [Test]
    public async Task List_ReportsDamagedEntriesInsteadOfFailing()
    {
        using var fixture = new SessionCliFixture();
        Directory.CreateDirectory(fixture.StoreDirectory);
        await File.WriteAllTextAsync(Path.Combine(fixture.StoreDirectory, $"{new string('f', 32)}.session.json"),
                                     "not json");

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("list"), CancellationToken.None);

        Assert.That(exit, Is.EqualTo(0));
        TestAssert.Contains("damaged", fixture.Io.Output, StringComparison.Ordinal);
    }

    [Test]
    public async Task List_ReportsLegacyFilesWhoseNameIsNotASessionId()
    {
        using var fixture = new SessionCliFixture();
        Directory.CreateDirectory(fixture.LegacyDirectory);
        await File.WriteAllTextAsync(Path.Combine(fixture.LegacyDirectory, "not-a-session-id.session.json"), "{}");

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("list"), CancellationToken.None);

        Assert.That(exit, Is.EqualTo(0));
        TestAssert.Contains("not-a-session-id.session.json", fixture.Io.Output, StringComparison.Ordinal);
        TestAssert.Contains("skipped: file name is not a session id", fixture.Io.Output, StringComparison.Ordinal);
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
        TestAssert.Contains($"format version  : {PersistedSession.CurrentFormatVersion}", fixture.Io.Output,
                            StringComparison.Ordinal);
        TestAssert.Contains("conversation    : 0 messages (verbatim protocol stored; resume checks required)",
                            fixture.Io.Output, StringComparison.Ordinal);
        TestAssert.Contains("input : why did it fail?", fixture.Io.Output, StringComparison.Ordinal);
        TestAssert.Contains("final : the fix", fixture.Io.Output, StringComparison.Ordinal);
    }


    [TestCase(2, false)]
    [TestCase(2, true)]
    [TestCase(3, true)]
    public async Task Show_LegacyRedactedConversationIsInspectionOnly(int formatVersion, bool opaqueRedacted)
    {
        using var fixture = new SessionCliFixture();
        var       session = await CommitStoredSessionAsync(fixture.StoreDirectory, "question", "answer");
        var legacy = session with
        {
            FormatVersion = formatVersion,
            Conversation = session.Conversation! with { OpaqueRedacted = opaqueRedacted }
        };
        await File.WriteAllTextAsync(Path.Combine(fixture.StoreDirectory, $"{session.Id}.session.json"),
                                     JsonSerializer.Serialize(legacy,
                                                              SessionStoreJsonContext.Default.PersistedSession));

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("show", session.Id),
                                                     CancellationToken.None);

        Assert.That(exit, Is.EqualTo(0));
        TestAssert.Contains("inspection only; legacy redaction", fixture.Io.Output, StringComparison.Ordinal);
        TestAssert.DoesNotContain("verbatim protocol stored", fixture.Io.Output, StringComparison.Ordinal);
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
                Messages  = [new PersistedMessage { Role = nameof(ChatRole.User), Content = secret }],
                StateJson = new StructuredState { Goal = secret }.ToJson()
            }
        };
        await File.WriteAllTextAsync(Path.Combine(fixture.StoreDirectory, $"{session.Id}.session.json"),
                                     JsonSerializer.Serialize(stored,
                                                              SessionStoreJsonContext.Default.PersistedSession));

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
        await File.WriteAllTextAsync(Path.Combine(fixture.StoreDirectory, $"{session.Id}.session.json"),
                                     JsonSerializer.Serialize(stored,
                                                              SessionStoreJsonContext.Default.PersistedSession));

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
    public async Task Show_FallsBackToALegacyRunSnapshotReadonlyView()
    {
        using var fixture  = new SessionCliFixture();
        var       legacyId = WriteLegacySnapshot(fixture.LegacyDirectory);

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("show", legacyId),
                                                     CancellationToken.None);

        Assert.That(exit, Is.EqualTo(0));
        TestAssert.Contains("legacy run snapshot (read-only; cannot be resumed)", fixture.Io.Output,
                            StringComparison.Ordinal);
        TestAssert.Contains($"run id          : {legacyId}", fixture.Io.Output, StringComparison.Ordinal);
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
        await File.WriteAllTextAsync(Path.Combine(fixture.StoreDirectory, $"{session.Id}.session.json"),
                                     JsonSerializer.Serialize(snapshot,
                                                              SessionStoreJsonContext.Default.PersistedSession));
        Assert.That(await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("show", session.Id),
                                                      CancellationToken.None), Is.EqualTo(0));
        Assert.That(fixture.Io.Output, Does.Contain("acknowledged"));
        Assert.That(fixture.Io.Output, Does.Contain("historical fact"));
        Assert.That(fixture.Io.Output, Does.Not.Contain("blocked on resume until"));
    }

    [Test]
    public async Task Show_LegacyRunRendersNumericExecutionStateAndApprovalAfterRoundTrip()
    {
        using var fixture  = new SessionCliFixture();
        var       id       = Guid.NewGuid().ToString("N");
        var       recorder = new FileRunRecorder(fixture.LegacyDirectory, id);
        await recorder.CompleteAsync(new AgentResult
        {
            Status = AgentStatus.Failed, ToolCalls =
            [
                new ToolCallRecord
                {
                    CallId = "call", ToolName = "shell", SideEffects = true, Approval = "allow once",
                    State  = ToolCallExecutionState.UnknownResult
                }
            ]
        }, [], StructuredState.Empty, CancellationToken.None);
        Assert.That(await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("show", id),
                                                      CancellationToken.None), Is.EqualTo(0));
        Assert.That(fixture.Io.Output, Does.Contain("tool shell: allow once — result unknown; side effect possible"));
    }

    [Test]
    public async Task Show_InconsistentSnapshotIsRefusedWithItsReason()
    {
        using var fixture    = new SessionCliFixture();
        var       session    = await CommitStoredSessionAsync(fixture.StoreDirectory, "q", "a");
        var       auditPath  = Path.Combine(fixture.StoreDirectory, $"{session.Id}.audit.jsonl");
        var       auditLines = (await File.ReadAllLinesAsync(auditPath)).ToList();
        auditLines.RemoveAt(auditLines.Count - 1);
        await File.WriteAllLinesAsync(auditPath, auditLines);

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("show", session.Id),
                                                     CancellationToken.None);

        Assert.That(exit, Is.EqualTo(1));
        TestAssert.Contains("inconsistent", fixture.Io.Error, StringComparison.Ordinal);
    }

    [Test]
    public async Task Show_UnknownIdFailsWithBothLocations()
    {
        using var fixture = new SessionCliFixture();

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("show", new string('0', 32)),
                                                     CancellationToken.None);

        Assert.That(exit, Is.EqualTo(1));
        TestAssert.Contains("No session", fixture.Io.Error, StringComparison.Ordinal);
        TestAssert.Contains(fixture.StoreDirectory, fixture.Io.Error, StringComparison.Ordinal);
        TestAssert.Contains(fixture.LegacyDirectory, fixture.Io.Error, StringComparison.Ordinal);
    }

    private static CliOptions Options(string subcommand, string? primary = null) =>
        new() { Kind = CliCommandKind.Session, Subcommand = subcommand, Primary = primary };

    private static async Task<PersistedSession> CommitStoredSessionAsync(
        string storeDirectory, string userInput, string finalMessage)
    {
        var chatSession = new ChatSession(Guid.NewGuid().ToString("N"),
                                          Path.Combine(Path.GetTempPath(), "tinyharness-session-command-ws"),
                                          new SessionModelIdentity("test-model", "https://example.invalid/v1",
                                                                   ChatApiKind.ChatCompletions));
        var turn = new SessionTurn(Guid.NewGuid().ToString("N"), userInput);
        var run  = new SessionRun(Guid.NewGuid().ToString("N"), chatSession.Id, turn.Id);
        chatSession.AddTurn(turn);
        turn.AddRun(run);
        run.Finish(new AgentResult { Status = AgentStatus.Completed, FinalMessage = finalMessage, Steps = 1 });

        using var writer = new SessionStoreWriter(storeDirectory, chatSession.Id);
        await writer.CommitAsync(chatSession, new ConversationContext(), CancellationToken.None);
        var read = await SessionStoreReader.ReadAsync(storeDirectory, chatSession.Id, CancellationToken.None);
        return read!;
    }

    /// <summary>
    ///     <para>写入只供脱敏检查的旧格式 run 快照，返回 run ID。</para>
    ///     <para>Writes a legacy run snapshot for redacted inspection only and returns its run ID.</para>
    /// </summary>
    private static string WriteLegacySnapshot(string legacyDirectory)
    {
        var runId = Guid.NewGuid().ToString("N");
        var snapshot = new SessionSnapshot
        {
            RunId        = runId,
            StartedUtc   = DateTimeOffset.UtcNow.AddMinutes(-2),
            CompletedUtc = DateTimeOffset.UtcNow,
            Messages =
            [
                new ChatMessage { Role = ChatRole.System, Content    = "system" },
                new ChatMessage { Role = ChatRole.User, Content      = "run this" },
                new ChatMessage { Role = ChatRole.Assistant, Content = "done" }
            ],
            State  = StructuredState.Empty,
            Result = new AgentResult { Status = AgentStatus.Completed, FinalMessage = "done", Steps = 1 }
        };
        var json = JsonSerializer.Serialize(snapshot, PersistenceJsonContext.Default.SessionSnapshot);
        Directory.CreateDirectory(legacyDirectory);
        File.WriteAllText(Path.Combine(legacyDirectory, $"{runId}.session.json"), json);
        return runId;
    }

    private sealed class SessionCliFixture : IDisposable
    {
        public SessionCliFixture()
        {
            Root            = Path.Combine(Path.GetTempPath(), $"tinyharness-session-cli-{Guid.NewGuid():N}");
            StoreDirectory  = Path.Combine(Root, "store");
            LegacyDirectory = Path.Combine(Root, "legacy-runs");
            Workspace       = Path.Combine(Root, "workspace");
            UserConfigPath  = Path.Combine(Root, "user-config.json");
            Io              = new FakeCliConsole();
            Directory.CreateDirectory(Workspace);

            // 项目配置指定工作区和旧 run 快照目录；
            // 可信用户配置指定工作区外的受保护会话存储。
            File.WriteAllText(Path.Combine(Root, "tinyharness.json"),
                              $$"""
                              {
                                "endpoint": "https://example.invalid/v1",
                                "model": "test-model",
                                "workspaceRoot": {{JsonSerializer.Serialize(Workspace)}},
                                "sessionDirectory": {{JsonSerializer.Serialize(LegacyDirectory)}}
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

        public string LegacyDirectory { get; }

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

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
///     M13.1 `session list` / `session show` 命令测试（严格离线，PLAN §7.1）：不解析凭据、
///     不调用模型、不启动工具；list 报告损坏条目，show 拒绝损坏/不一致，旧 run 快照只读展示，
///     找不到时返回 1。
///     M13.1 `session list` / `session show` command tests (strictly offline,
///     PLAN §7.1): no credentials are resolved, no model is called and no tool
///     runs; list reports damaged entries, show refuses damaged or inconsistent
///     ones, legacy run snapshots render read-only, and a missing id exits 1.
/// </summary>
public class SessionCommandTests
{
    [Fact]
    public async Task List_ShowsStoredSessionsAndLegacySnapshotsOffline()
    {
        using var fixture = new SessionCliFixture();
        var       session = await CommitStoredSessionAsync(fixture.StoreDirectory, "why did it fail?", "the fix");
        WriteLegacySnapshot(fixture.LegacyDirectory);

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("list"), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(session.Id, fixture.Io.Output, StringComparison.Ordinal);
        Assert.Contains("turns 1", fixture.Io.Output, StringComparison.Ordinal);
        Assert.Contains("Run snapshots (legacy, read-only)", fixture.Io.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_ReportsDamagedEntriesInsteadOfFailing()
    {
        using var fixture = new SessionCliFixture();
        Directory.CreateDirectory(fixture.StoreDirectory);
        await File.WriteAllTextAsync(Path.Combine(fixture.StoreDirectory, $"{new string('f', 32)}.session.json"),
                                     "not json");

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("list"), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("damaged", fixture.Io.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_ReportsLegacyFilesWhoseNameIsNotASessionId()
    {
        using var fixture = new SessionCliFixture();
        Directory.CreateDirectory(fixture.LegacyDirectory);
        await File.WriteAllTextAsync(Path.Combine(fixture.LegacyDirectory, "not-a-session-id.session.json"), "{}");

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("list"), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("not-a-session-id.session.json", fixture.Io.Output, StringComparison.Ordinal);
        Assert.Contains("skipped: file name is not a session id", fixture.Io.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("(none)", fixture.Io.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_PrintsTheStoredSessionDetails()
    {
        using var fixture = new SessionCliFixture();
        var       session = await CommitStoredSessionAsync(fixture.StoreDirectory, "why did it fail?", "the fix");

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("show", session.Id),
                                                     CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains($"session         : {session.Id}", fixture.Io.Output, StringComparison.Ordinal);
        Assert.Contains($"format version  : {PersistedSession.CurrentFormatVersion}", fixture.Io.Output,
                        StringComparison.Ordinal);
        Assert.Contains("conversation    : 0 messages (verbatim protocol stored; resume checks required)",
                        fixture.Io.Output, StringComparison.Ordinal);
        Assert.Contains("input : why did it fail?", fixture.Io.Output, StringComparison.Ordinal);
        Assert.Contains("final : the fix", fixture.Io.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
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

        Assert.Equal(0, exit);
        Assert.Contains("inspection only; legacy redaction", fixture.Io.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("verbatim protocol stored", fixture.Io.Output, StringComparison.Ordinal);
    }

    [Fact]
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

        Assert.Equal(0, exit);
        Assert.DoesNotContain(secret, fixture.Io.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, fixture.Io.Error, StringComparison.Ordinal);
        Assert.Contains("input : question", fixture.Io.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_FallsBackToALegacyRunSnapshotReadonlyView()
    {
        using var fixture  = new SessionCliFixture();
        var       legacyId = WriteLegacySnapshot(fixture.LegacyDirectory);

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("show", legacyId),
                                                     CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("legacy run snapshot (read-only; cannot be resumed)", fixture.Io.Output,
                        StringComparison.Ordinal);
        Assert.Contains($"run id          : {legacyId}", fixture.Io.Output, StringComparison.Ordinal);
    }

    [Fact]
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

        Assert.Equal(1, exit);
        Assert.Contains("inconsistent", fixture.Io.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_UnknownIdFailsWithBothLocations()
    {
        using var fixture = new SessionCliFixture();

        var exit = await SessionCommand.ExecuteAsync(fixture.CreateContext(), Options("show", new string('0', 32)),
                                                     CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("No session", fixture.Io.Error, StringComparison.Ordinal);
        Assert.Contains(fixture.StoreDirectory, fixture.Io.Error, StringComparison.Ordinal);
        Assert.Contains(fixture.LegacyDirectory, fixture.Io.Error, StringComparison.Ordinal);
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
    ///     写入一个旧格式 run 快照（M13 之前的脱敏检查产物），返回其 run id。
    ///     Writes one legacy run snapshot (the pre-M13 redacted inspection artifact) and returns its run id.
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

            // The project config pins workspaceRoot and the legacy run-snapshot
            // directory; the user config (the trusted layer) pins the protected
            // session store, which sits outside the workspace as a sibling.
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

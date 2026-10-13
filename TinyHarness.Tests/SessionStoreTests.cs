using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Tests;

/// <summary>
///     M13.1 受保护会话存储测试（PLAN §7.1/§12）：版本化快照、原子替换、提交序号与审计对账、
///     跨进程排他锁、损坏/未知版本/深度/大小/路径逃逸拒绝，以及存储目录的边界与链接保护检查。
///     M13.1 protected session-store tests (PLAN §7.1/§12): the versioned
///     snapshot, atomic replacement, commit-sequence/audit reconciliation, the
///     cross-process exclusive lock, rejections of damage/unknown
///     versions/depth/size/path escapes, and the boundary and link checks of the
///     store directory.
/// </summary>
public class SessionStoreTests
{
    private static readonly IReadOnlyDictionary<string, string> KnownSecrets =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["MY_API_KEY"] = "sk-test-secret-value" };

    private static string WorkspaceRoot(TestTempDir dir) => dir.Root;

    private static ChatSession NewSession(string? id = null)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "tinyharness-session-store-ws");
        return new ChatSession(id ?? Guid.NewGuid().ToString("N"), workspace,
                               new SessionModelIdentity("test-model", "https://example.invalid/v1",
                                                        ChatApiKind.ChatCompletions));
    }

    private static void AddFinishedTurn(ChatSession session, string userInput, string finalMessage,
                                        AgentStatus status = AgentStatus.Completed)
    {
        var turn = new SessionTurn(Guid.NewGuid().ToString("N"), userInput);
        var run  = new SessionRun(Guid.NewGuid().ToString("N"), session.Id, turn.Id);
        session.AddTurn(turn);
        turn.AddRun(run);
        run.Finish(new AgentResult { Status = status, FinalMessage = finalMessage, Steps = 2, ToolExecutions = 1 });
    }

    [Fact]
    public async Task Commit_WritesVersionedSnapshotAndAppendsCommitAudit()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddFinishedTurn(session, "first question", "first answer");

        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, CancellationToken.None);
        }

        var snapshotPath = Path.Combine(dir.Root, $"{session.Id}.session.json");
        Assert.True(File.Exists(snapshotPath));

        var root = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath))!.AsObject();
        Assert.Equal(1, (int)root["formatVersion"]!);
        Assert.Equal(session.Id, (string)root["id"]!);
        Assert.Equal(1L, (long)root["commitSequence"]!);
        Assert.Single(root["turns"]!.AsArray());
        Assert.Equal("first question", (string)root["turns"]![0]!["userInput"]!);
        Assert.Equal("Completed", (string)root["turns"]![0]!["runs"]![0]!["status"]!);
        Assert.Equal("ChatCompletions", (string)root["model"]!["chatApi"]!);

        var auditLines = await File.ReadAllLinesAsync(Path.Combine(dir.Root, $"{session.Id}.audit.jsonl"));
        var record     = JsonNode.Parse(auditLines.Single())!.AsObject();
        Assert.Equal("session.committed", (string)record["kind"]!);
        Assert.Equal(session.Id, (string)record["sessionId"]!);
        Assert.Equal(1L, (long)record["sequence"]!);
    }

    [Fact]
    public async Task Commit_SecondTurnAdvancesSequenceAndReplacesAtomically()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddFinishedTurn(session, "q1", "a1");

        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, CancellationToken.None);
            Assert.Equal(1, writer.CommitSequence);

            AddFinishedTurn(session, "q2", "a2");
            await writer.CommitAsync(session, CancellationToken.None);
            Assert.Equal(2, writer.CommitSequence);
        }

        var snapshotPath = Path.Combine(dir.Root, $"{session.Id}.session.json");
        Assert.Equal(2, JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath))!["turns"]!.AsArray().Count);
        Assert.Empty(Directory.GetFiles(dir.Root, "*.tmp"));
        var auditLines = await File.ReadAllLinesAsync(Path.Combine(dir.Root, $"{session.Id}.audit.jsonl"));
        Assert.Equal(2, auditLines.Length);
        Assert.Equal(2L, (long)JsonNode.Parse(auditLines[1])!["sequence"]!);
    }

    [Fact]
    public async Task Commit_RedactsKnownSecretsInTurnContent()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddFinishedTurn(session, "use key sk-test-secret-value please", "done with sk-test-secret-value");

        using (var writer = new SessionStoreWriter(dir.Root, session.Id, KnownSecrets))
        {
            await writer.CommitAsync(session, CancellationToken.None);
        }

        var json = await File.ReadAllTextAsync(Path.Combine(dir.Root, $"{session.Id}.session.json"));
        Assert.DoesNotContain("sk-test-secret-value", json, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commit_FailedWriteKeepsPreviousValidSnapshot()
    {
        using var dir      = new TestTempDir();
        var       session1 = NewSession();
        AddFinishedTurn(session1, "q1", "a1");

        using (var writer = new SessionStoreWriter(dir.Root, session1.Id))
        {
            await writer.CommitAsync(session1, CancellationToken.None);
        }

        // Fault injection: occupy the temp path of a second session with a
        // directory, so its temp-file write fails before any replacement. The
        // first session's snapshot must survive untouched.
        var session2 = NewSession();
        AddFinishedTurn(session2, "q2", "a2");
        Directory.CreateDirectory(Path.Combine(dir.Root, $"{session2.Id}.session.json.tmp"));
        using (var writer2 = new SessionStoreWriter(dir.Root, session2.Id))
        {
            var error =
                await Assert.ThrowsAsync<SessionStoreException>(() => writer2.CommitAsync(session2,
                                                                         CancellationToken.None));
            Assert.Contains("not reliably saved", error.Message, StringComparison.Ordinal);
        }

        var readBack = await SessionStoreReader.ReadAsync(dir.Root, session1.Id, CancellationToken.None);
        Assert.NotNull(readBack);
        Assert.Single(readBack.Turns);
        Assert.False(File.Exists(Path.Combine(dir.Root, $"{session2.Id}.session.json")));
    }

    [Fact]
    public void OpenSession_SecondWriterForSameSessionFailsExclusively()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();

        using var writer = new SessionStoreWriter(dir.Root, session.Id);
        var       error  = Assert.Throws<SessionStoreException>(() => new SessionStoreWriter(dir.Root, session.Id));
        Assert.Contains("already open", error.Message, StringComparison.Ordinal);
        Assert.Contains(session.Id, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenSession_AfterDisposeAllowsTheNextWriter()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();

        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
        }

        // The handle was released; a second writer may now open the same session id
        // (the snapshot still does not exist, so a fresh open stays legal).
        using var second = new SessionStoreWriter(dir.Root, session.Id);
    }

    [Fact]
    public async Task OpenSession_RefusesExistingSnapshotAndInvalidIds()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, CancellationToken.None);
        }

        var existsError = Assert.Throws<SessionStoreException>(() => new SessionStoreWriter(dir.Root, session.Id));
        Assert.Contains("already exists", existsError.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => new SessionStoreWriter(dir.Root, "../escape"));
        Assert.Throws<ArgumentException>(() => new SessionStoreWriter(dir.Root, "UPPER-CASE-NOT-HEX-0000000000"));
    }

    [Fact]
    public async Task Read_RoundTripsACommittedSession()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddFinishedTurn(session, "what changed?", "the patch applied");
        AddFinishedTurn(session, "and tests?", "they failed", AgentStatus.Failed);

        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, CancellationToken.None);
            await writer.CommitAsync(session, CancellationToken.None);
        }

        var read = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(PersistedSession.CurrentFormatVersion, read!.FormatVersion);
        Assert.Equal(session.Id, read.Id);
        Assert.Equal(session.WorkspaceRoot, read.WorkspaceRoot);
        Assert.Equal(2, read.CommitSequence);
        Assert.Equal(2, read.Turns.Count);
        Assert.Equal("what changed?", read.Turns[0].UserInput);
        Assert.Equal("the patch applied", read.Turns[0].Runs.Single().FinalMessage);
        Assert.Equal("Failed", read.Turns[1].Runs.Single().Status);
        Assert.Equal("test-model", read.Model!.ModelId);
        Assert.Equal(ChatApiKind.ChatCompletions, read.Model.ChatApi);
    }

    [Fact]
    public async Task Read_MissingSnapshotReturnsNullButInvalidIdIsRejected()
    {
        using var dir = new TestTempDir();

        Assert.Null(await SessionStoreReader.ReadAsync(dir.Root, new string('a', 32), CancellationToken.None));

        var error =
            await Assert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadAsync(dir.Root, "..\\escape",
                                                                     CancellationToken.None));
        Assert.Contains("not a valid session id", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_AcceptsAnUppercaseSessionIdInput()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddFinishedTurn(session, "q", "a");
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, CancellationToken.None);
        }

        var read = await SessionStoreReader.ReadAsync(dir.Root, session.Id.ToUpperInvariant(), CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(session.Id, read!.Id);
    }

    [Fact]
    public async Task Commit_CanonicalizesAnUppercaseIdToLowerCaseStorage()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddFinishedTurn(session, "q", "a");

        using (var writer = new SessionStoreWriter(dir.Root, session.Id.ToUpperInvariant()))
        {
            Assert.Equal(session.Id, writer.SessionId);
            await writer.CommitAsync(session, CancellationToken.None);
        }

        // The actual file name — not a case-insensitive File.Exists probe — proves
        // the on-disk form is lower case on every file system.
        var storedName = Directory.GetFiles(dir.Root, "*.session.json").Single();
        Assert.Equal($"{session.Id}.session.json", Path.GetFileName(storedName));

        var read = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(session.Id, read!.Id);
    }

    [Fact]
    public async Task ReadLegacy_AcceptsAnUppercaseSessionIdInput()
    {
        using var dir   = new TestTempDir();
        var       runId = Guid.NewGuid().ToString("N");
        var snapshot = new SessionSnapshot
        {
            RunId        = runId,
            StartedUtc   = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedUtc = DateTimeOffset.UtcNow,
            Messages     = [],
            State        = StructuredState.Empty,
            Result       = new AgentResult { Status = AgentStatus.Completed, Steps = 1 }
        };
        await File.WriteAllTextAsync(Path.Combine(dir.Root, $"{runId}.session.json"),
                                     JsonSerializer.Serialize(snapshot,
                                                              PersistenceJsonContext.Default.SessionSnapshot));

        var read = await SessionStoreReader.ReadLegacyAsync(dir.Root, runId.ToUpperInvariant(), CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(runId, read!.RunId);
    }

    [Fact]
    public async Task Read_RejectsUnknownFormatVersion()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        await CommitRawSnapshotAsync(dir.Root, session.Id, new JsonObject
        {
            ["formatVersion"]  = 99,
            ["id"]             = session.Id,
            ["commitSequence"] = 1
        });

        var error =
            await Assert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadAsync(dir.Root, session.Id,
                                                                     CancellationToken.None));
        Assert.Contains("format version", error.Message, StringComparison.Ordinal);
        Assert.Contains("99", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_RejectsCorruptJson()
    {
        using var dir = new TestTempDir();
        var       id  = new string('b', 32);
        await File.WriteAllTextAsync(Path.Combine(dir.Root, $"{id}.session.json"), "{ not valid json");

        var error =
            await Assert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadAsync(dir.Root, id,
                                                                     CancellationToken.None));
        Assert.Contains("damaged JSON", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_RejectsSnapshotAheadOfItsAudit()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddFinishedTurn(session, "q", "a");
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, CancellationToken.None);
            AddFinishedTurn(session, "q2", "a2");
            await writer.CommitAsync(session, CancellationToken.None);
        }

        // Simulate a crash between replacement and audit: drop the last commit line.
        var auditPath = Path.Combine(dir.Root, $"{session.Id}.audit.jsonl");
        var lines     = (await File.ReadAllLinesAsync(auditPath)).ToList();
        lines.RemoveAt(lines.Count - 1);
        await File.WriteAllLinesAsync(auditPath, lines);

        var error =
            await Assert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadAsync(dir.Root, session.Id,
                                                                     CancellationToken.None));
        Assert.Contains("inconsistent", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_RejectsMissingAuditTrail()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddFinishedTurn(session, "q", "a");
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, CancellationToken.None);
        }

        File.Delete(Path.Combine(dir.Root, $"{session.Id}.audit.jsonl"));

        var error =
            await Assert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadAsync(dir.Root, session.Id,
                                                                     CancellationToken.None));
        Assert.Contains("no commit audit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_RejectsHostileDeepJson()
    {
        using var dir    = new TestTempDir();
        var       id     = new string('c', 32);
        var       nested = new StringBuilder();
        for (var i = 0; i < 100; i++) nested.Append("{\"a\":");
        nested.Append("1");
        for (var i = 0; i < 100; i++) nested.Append('}');
        await File.WriteAllTextAsync(Path.Combine(dir.Root, $"{id}.session.json"), nested.ToString());

        var error =
            await Assert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadAsync(dir.Root, id,
                                                                     CancellationToken.None));
        Assert.Contains("damaged JSON", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_RejectsOversizedSnapshot()
    {
        using var dir   = new TestTempDir();
        var       id    = new string('d', 32);
        var       path  = Path.Combine(dir.Root, $"{id}.session.json");
        var       bytes = new byte[SessionStoreWriter.MaxSnapshotBytes + 1];
        Array.Fill(bytes, (byte)' ');
        await File.WriteAllBytesAsync(path, bytes);

        var error =
            await Assert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadAsync(dir.Root, id,
                                                                     CancellationToken.None));
        Assert.Contains("exceeds", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_ReportsDamagedEntriesAndOrdersNewestFirst()
    {
        using var dir   = new TestTempDir();
        var       older = NewSession();
        var       newer = NewSession();
        AddFinishedTurn(older, "old", "old answer");
        AddFinishedTurn(newer, "new", "new answer");

        foreach (var session in new[] { older, newer })
        {
            using var writer = new SessionStoreWriter(dir.Root, session.Id);
            await writer.CommitAsync(session, CancellationToken.None);
        }

        var damagedId = new string('e', 32);
        await File.WriteAllTextAsync(Path.Combine(dir.Root, $"{damagedId}.session.json"), "not json");

        var entries = await SessionStoreReader.ListAsync(dir.Root, CancellationToken.None);
        Assert.Equal(3, entries.Count);
        Assert.Equal(newer.Id, entries[0].Id);
        Assert.Equal(older.Id, entries[1].Id);
        Assert.Null(entries[0].Error);
        Assert.Equal(damagedId, entries[2].Id);
        Assert.NotNull(entries[2].Error);
        Assert.Contains("damaged JSON", entries[2].Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveProtected_RejectsWorkspaceChildrenAndRelativePaths()
    {
        using var dir = new TestTempDir();

        var childError =
            Assert.Throws<SessionStoreException>(() => SessionStorePaths.ResolveProtected(Path.Combine(dir.Root,
                                                                   "sessions"), WorkspaceRoot(dir)));
        Assert.Contains("lies inside the workspace", childError.Message, StringComparison.Ordinal);

        var relativeError =
            Assert.Throws<SessionStoreException>(() => SessionStorePaths.ResolveProtected("relative/sessions",
                                                          WorkspaceRoot(dir)));
        Assert.Contains("absolute path", relativeError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveProtected_AcceptsAnAbsoluteDirectoryOutsideTheWorkspace()
    {
        using var dir  = new TestTempDir();
        using var home = new TestTempDir();

        var resolved = SessionStorePaths.ResolveProtected(home.Root, WorkspaceRoot(dir));
        Assert.Equal(Path.GetFullPath(home.Root), resolved);
    }

    [Fact]
    public void ResolveProtected_RejectsALinkThatLandsInsideTheWorkspace()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var dir      = new TestTempDir();
        using var home     = new TestTempDir();
        var       linkPath = Path.Combine(home.Root, "linked-store");
        if (!TryCreateJunction(linkPath, dir.Root)) return; // No junction privilege; nothing to verify.

        var error =
            Assert.Throws<SessionStoreException>(() => SessionStorePaths
                                                    .ResolveProtected(linkPath, WorkspaceRoot(dir)));
        Assert.Contains("resolves through links into the workspace", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultDirectory_IsAbsoluteAndOutsideAnyWorkspace()
    {
        using var dir = new TestTempDir();

        var directory = SessionStorePaths.DefaultDirectory();
        Assert.True(Path.IsPathFullyQualified(directory));
        Assert.False(Workspace.IsInside(dir.Root, directory));
    }

    [Fact]
    public async Task ReadLegacy_RejectsASnapshotWithNullResultOrMessages()
    {
        using var dir        = new TestTempDir();
        var       noResult   = new string('9', 32);
        var       noMessages = new string('8', 32);
        await File.WriteAllTextAsync(Path.Combine(dir.Root, $"{noResult}.session.json"),
                                     "{\"runId\":\"x\",\"result\":null,\"messages\":[]}");
        await File.WriteAllTextAsync(Path.Combine(dir.Root, $"{noMessages}.session.json"),
                                     "{\"runId\":\"x\",\"messages\":null}");

        foreach (var id in new[] { noResult, noMessages })
        {
            var error =
                await Assert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadLegacyAsync(dir.Root, id,
                                                                         CancellationToken.None));
            Assert.Contains("no run result or message list", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Read_RejectsADamagedAuditLine()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddFinishedTurn(session, "q", "a");
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, CancellationToken.None);
        }

        await File.AppendAllTextAsync(Path.Combine(dir.Root, $"{session.Id}.audit.jsonl"), "{ broken");

        var error =
            await Assert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadAsync(dir.Root, session.Id,
                                                                     CancellationToken.None));
        Assert.Contains("damaged line", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commit_CancelledWriteIsRejectedAsNotReliablySaved()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddFinishedTurn(session, "q", "a");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var writer = new SessionStoreWriter(dir.Root, session.Id);
        var       error = await Assert.ThrowsAsync<SessionStoreException>(() => writer.CommitAsync(session, cts.Token));
        Assert.Contains("not reliably saved", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(dir.Root, $"{session.Id}.session.json")));
    }

    private static async Task CommitRawSnapshotAsync(string directory, string sessionId, JsonObject snapshot)
    {
        snapshot["id"] = sessionId;
        await File.WriteAllTextAsync(Path.Combine(directory, $"{sessionId}.session.json"),
                                     snapshot.ToJsonString());
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            CreateNoWindow         = true,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };

        using var process = Process.Start(psi);
        if (process is null) return false;

        process.WaitForExit(5_000);
        return process.ExitCode == 0;
    }
}

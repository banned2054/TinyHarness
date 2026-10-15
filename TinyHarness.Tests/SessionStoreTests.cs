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
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         受保护会话存储测试（PLAN §7.1/§12）：版本化快照、原子替换、提交序号与审计对账、
///         跨进程排他锁、损坏/未知版本/深度/大小/路径逃逸拒绝，以及存储目录的边界与链接保护检查。
///     </para>
///     <para>
///         Protected session-store tests (PLAN §7.1/§12): the versioned
///         snapshot, atomic replacement, commit-sequence/audit reconciliation, the
///         cross-process exclusive lock, rejections of damage/unknown
///         versions/depth/size/path escapes, and the boundary and link checks of the
///         store directory.
///     </para>
/// </summary>
public class SessionStoreTests
{
    private static readonly IReadOnlyDictionary<string, string> KnownSecrets =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["MY_API_KEY"] = "sk-test-secret-value" };

    private static string WorkspaceRoot(TestTempDir dir) => dir.Root;

    private static ConversationContext NewContext() => new();

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

    [Test]
    public async Task Commit_WritesVersionedSnapshotAndAppendsCommitAudit()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddFinishedTurn(session, "first question", "first answer");

        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, NewContext(), CancellationToken.None);
        }

        var snapshotPath = Path.Combine(dir.Root, $"{session.Id}.session.json");
        Assert.That(File.Exists(snapshotPath), Is.True);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath))!.AsObject();
        Assert.That((int)root["formatVersion"]!, Is.EqualTo(PersistedSession.CurrentFormatVersion));
        Assert.That((string)root["id"]!, Is.EqualTo(session.Id));
        Assert.That((long)root["commitSequence"]!, Is.EqualTo(1L));
        TestAssert.Single(root["turns"]!.AsArray());
        Assert.That((string)root["turns"]![0]!["userInput"]!, Is.EqualTo("first question"));
        Assert.That((string)root["turns"]![0]!["runs"]![0]!["status"]!, Is.EqualTo("Completed"));
        Assert.That((string)root["model"]!["chatApi"]!, Is.EqualTo("ChatCompletions"));

        var auditLines = await File.ReadAllLinesAsync(Path.Combine(dir.Root, $"{session.Id}.audit.jsonl"));
        var record     = JsonNode.Parse(auditLines.Single())!.AsObject();
        Assert.That((string)record["kind"]!, Is.EqualTo("session.committed"));
        Assert.That((string)record["sessionId"]!, Is.EqualTo(session.Id));
        Assert.That((long)record["sequence"]!, Is.EqualTo(1L));
    }

    [Test]
    public async Task Commit_SecondTurnAdvancesSequenceAndReplacesAtomically()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddFinishedTurn(session, "q1", "a1");

        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, NewContext(), CancellationToken.None);
            Assert.That(writer.CommitSequence, Is.EqualTo(1));

            AddFinishedTurn(session, "q2", "a2");
            await writer.CommitAsync(session, NewContext(), CancellationToken.None);
            Assert.That(writer.CommitSequence, Is.EqualTo(2));
        }

        var snapshotPath = Path.Combine(dir.Root, $"{session.Id}.session.json");
        Assert.That(JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath))!["turns"]!.AsArray().Count,
                    Is.EqualTo(2));
        Assert.That(Directory.GetFiles(dir.Root, "*.tmp"), Is.Empty);
        var auditLines = await File.ReadAllLinesAsync(Path.Combine(dir.Root, $"{session.Id}.audit.jsonl"));
        Assert.That(auditLines.Length, Is.EqualTo(2));
        Assert.That((long)JsonNode.Parse(auditLines[1])!["sequence"]!, Is.EqualTo(2L));
    }

    [Test]
    public async Task Commit_RedactsKnownSecretsInTurnContent()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddFinishedTurn(session, "use key sk-test-secret-value please", "done with sk-test-secret-value");

        var context = NewContext();
        context.Append(ChatMessage.User("use key sk-test-secret-value please"));
        context.Append(ChatMessage.Assistant("done with sk-test-secret-value"));
        using (var writer = new SessionStoreWriter(dir.Root, session.Id, KnownSecrets))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        var snapshot = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(snapshot);
        Assert.That(snapshot.Turns.Single().UserInput, Is.EqualTo("use key [REDACTED] please"));
        Assert.That(snapshot.Turns.Single().Runs.Single().FinalMessage, Is.EqualTo("done with [REDACTED]"));
        Assert.That(snapshot.Conversation!.Messages[0].Content, Is.EqualTo("use key sk-test-secret-value please"));
        Assert.That(snapshot.Conversation.Messages[1].Content, Is.EqualTo("done with sk-test-secret-value"));
        Assert.That(snapshot.Conversation.OpaqueRedacted, Is.False);
        var audit = await File.ReadAllTextAsync(Path.Combine(dir.Root, $"{session.Id}.audit.jsonl"));
        TestAssert.DoesNotContain("sk-test-secret-value", audit, StringComparison.Ordinal);
    }

    [Test]
    public async Task Commit_FailedWriteKeepsPreviousValidSnapshot()
    {
        using var dir      = new TestTempDir();
        var       session1 = NewSession();
        AddFinishedTurn(session1, "q1", "a1");

        using (var writer = new SessionStoreWriter(dir.Root, session1.Id))
        {
            await writer.CommitAsync(session1, NewContext(), CancellationToken.None);
        }

        // 故障注入：用目录占据第二个会话的临时路径，使其临时文件写入在替换前失败。
        // 第一个会话的快照必须完好保留。
        var session2 = NewSession();
        AddFinishedTurn(session2, "q2", "a2");
        Directory.CreateDirectory(Path.Combine(dir.Root, $"{session2.Id}.session.json.tmp"));
        using (var writer2 = new SessionStoreWriter(dir.Root, session2.Id))
        {
            var error =
                await TestAssert.ThrowsAsync<SessionStoreException>(() => writer2.CommitAsync(session2, NewContext(),
                                                                             CancellationToken.None));
            TestAssert.Contains("not reliably saved", error.Message, StringComparison.Ordinal);
        }

        var readBack = await SessionStoreReader.ReadAsync(dir.Root, session1.Id, CancellationToken.None);
        TestAssert.NotNull(readBack);
        TestAssert.Single(readBack.Turns);
        Assert.That(File.Exists(Path.Combine(dir.Root, $"{session2.Id}.session.json")), Is.False);
    }

    [Test]
    public void OpenSession_SecondWriterForSameSessionFailsExclusively()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();

        using var writer = new SessionStoreWriter(dir.Root, session.Id);
        var       error  = TestAssert.Throws<SessionStoreException>(() => new SessionStoreWriter(dir.Root, session.Id));
        TestAssert.Contains("already open", error.Message, StringComparison.Ordinal);
        TestAssert.Contains(session.Id, error.Message, StringComparison.Ordinal);
    }

    [Test]
    public void OpenSession_AfterDisposeAllowsTheNextWriter()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();

        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
        }

        // 句柄已释放；第二个写入者现在可以打开同一 session id
        // （快照仍不存在，全新打开依然合法）。
        using var second = new SessionStoreWriter(dir.Root, session.Id);
    }

    [Test]
    public async Task OpenSession_RefusesExistingSnapshotAndInvalidIds()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, NewContext(), CancellationToken.None);
        }

        var existsError = TestAssert.Throws<SessionStoreException>(() => new SessionStoreWriter(dir.Root, session.Id));
        TestAssert.Contains("already exists", existsError.Message, StringComparison.Ordinal);

        TestAssert.Throws<ArgumentException>(() => new SessionStoreWriter(dir.Root, "../escape"));
        TestAssert.Throws<ArgumentException>(() => new SessionStoreWriter(dir.Root, "UPPER-CASE-NOT-HEX-0000000000"));
    }

    [Test]
    public async Task Read_RoundTripsACommittedSession()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddFinishedTurn(session, "what changed?", "the patch applied");
        AddFinishedTurn(session, "and tests?", "they failed", AgentStatus.Failed);

        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, NewContext(), CancellationToken.None);
            await writer.CommitAsync(session, NewContext(), CancellationToken.None);
        }

        var read = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(read);
        Assert.That(read!.FormatVersion, Is.EqualTo(PersistedSession.CurrentFormatVersion));
        Assert.That(read.Id, Is.EqualTo(session.Id));
        Assert.That(read.WorkspaceRoot, Is.EqualTo(session.WorkspaceRoot));
        Assert.That(read.CommitSequence, Is.EqualTo(2));
        Assert.That(read.Turns.Count, Is.EqualTo(2));
        Assert.That(read.Turns[0].UserInput, Is.EqualTo("what changed?"));
        Assert.That(read.Turns[0].Runs.Single().FinalMessage, Is.EqualTo("the patch applied"));
        Assert.That(read.Turns[1].Runs.Single().Status, Is.EqualTo("Failed"));
        Assert.That(read.Model!.ModelId, Is.EqualTo("test-model"));
        Assert.That(read.Model.ChatApi, Is.EqualTo(ChatApiKind.ChatCompletions));
    }


    [TestCase(1)]
    [TestCase(2)]
    public async Task Read_OlderFormatsRemainCompatibleForOfflineInspection(int formatVersion)
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddFinishedTurn(session, "inspect old question", "inspect old answer");
        var context = NewContext();
        context.Append(ChatMessage.System("old instructions"));
        context.Append(ChatMessage.User("old question"));
        context.Append(ChatMessage.Assistant("old answer"));
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        var path = Path.Combine(dir.Root, $"{session.Id}.session.json");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        root["formatVersion"] = formatVersion;
        if (formatVersion == 1) root.Remove("conversation");
        await File.WriteAllTextAsync(path, root.ToJsonString());

        var read = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(read);
        Assert.That(read.FormatVersion, Is.EqualTo(formatVersion));
        Assert.That(read.Turns.Single().UserInput, Is.EqualTo("inspect old question"));
        Assert.That(read.Turns.Single().Runs.Single().FinalMessage, Is.EqualTo("inspect old answer"));
        if (formatVersion == 2) TestAssert.NotNull(read.Conversation);
        else Assert.That(read.Conversation, Is.Null);
        var entry = TestAssert.Single(await SessionStoreReader.ListAsync(dir.Root, CancellationToken.None));
        Assert.That(entry.Error, Is.Null);
        Assert.That(entry.Session!.FormatVersion, Is.EqualTo(formatVersion));
    }

    [Test]
    public async Task Read_MissingSnapshotReturnsNullButInvalidIdIsRejected()
    {
        using var dir = new TestTempDir();

        Assert.That(await SessionStoreReader.ReadAsync(dir.Root, new string('a', 32), CancellationToken.None), Is.Null);

        var error =
            await TestAssert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadAsync(dir.Root,
                                                                         "..\\escape",
                                                                         CancellationToken.None));
        TestAssert.Contains("not a valid session id", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public async Task Read_AcceptsAnUppercaseSessionIdInput()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddFinishedTurn(session, "q", "a");
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, NewContext(), CancellationToken.None);
        }

        var read = await SessionStoreReader.ReadAsync(dir.Root, session.Id.ToUpperInvariant(), CancellationToken.None);

        TestAssert.NotNull(read);
        Assert.That(read!.Id, Is.EqualTo(session.Id));
    }

    [Test]
    public async Task Commit_CanonicalizesAnUppercaseIdToLowerCaseStorage()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddFinishedTurn(session, "q", "a");

        using (var writer = new SessionStoreWriter(dir.Root, session.Id.ToUpperInvariant()))
        {
            Assert.That(writer.SessionId, Is.EqualTo(session.Id));
            await writer.CommitAsync(session, NewContext(), CancellationToken.None);
        }

        // 用实际文件名——而不是大小写不敏感的 File.Exists 探测——证明磁盘形态在任意
        // 文件系统上都是小写。
        var storedName = Directory.GetFiles(dir.Root, "*.session.json").Single();
        Assert.That(Path.GetFileName(storedName), Is.EqualTo($"{session.Id}.session.json"));

        var read = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(read);
        Assert.That(read!.Id, Is.EqualTo(session.Id));
    }

    [Test]
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

        TestAssert.NotNull(read);
        Assert.That(read!.RunId, Is.EqualTo(runId));
    }

    [Test]
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
            await TestAssert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadAsync(dir.Root, session.Id,
                                                                         CancellationToken.None));
        TestAssert.Contains("format version", error.Message, StringComparison.Ordinal);
        TestAssert.Contains("99", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public async Task Read_RejectsCorruptJson()
    {
        using var dir = new TestTempDir();
        var       id  = new string('b', 32);
        await File.WriteAllTextAsync(Path.Combine(dir.Root, $"{id}.session.json"), "{ not valid json");

        var error =
            await TestAssert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadAsync(dir.Root, id,
                                                                         CancellationToken.None));
        TestAssert.Contains("damaged JSON", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public async Task Read_RejectsSnapshotAheadOfItsAudit()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddFinishedTurn(session, "q", "a");
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, NewContext(), CancellationToken.None);
            AddFinishedTurn(session, "q2", "a2");
            await writer.CommitAsync(session, NewContext(), CancellationToken.None);
        }

        // 模拟替换与审计之间的崩溃：删掉最后一行提交记录。
        var auditPath = Path.Combine(dir.Root, $"{session.Id}.audit.jsonl");
        var lines     = (await File.ReadAllLinesAsync(auditPath)).ToList();
        lines.RemoveAt(lines.Count - 1);
        await File.WriteAllLinesAsync(auditPath, lines);

        var error =
            await TestAssert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadAsync(dir.Root, session.Id,
                                                                         CancellationToken.None));
        TestAssert.Contains("inconsistent", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public async Task Read_RejectsMissingAuditTrail()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddFinishedTurn(session, "q", "a");
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, NewContext(), CancellationToken.None);
        }

        File.Delete(Path.Combine(dir.Root, $"{session.Id}.audit.jsonl"));

        var error =
            await TestAssert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadAsync(dir.Root, session.Id,
                                                                         CancellationToken.None));
        TestAssert.Contains("no commit audit", error.Message, StringComparison.Ordinal);
    }

    [Test]
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
            await TestAssert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadAsync(dir.Root, id,
                                                                         CancellationToken.None));
        TestAssert.Contains("damaged JSON", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public async Task Read_RejectsOversizedSnapshot()
    {
        using var dir   = new TestTempDir();
        var       id    = new string('d', 32);
        var       path  = Path.Combine(dir.Root, $"{id}.session.json");
        var       bytes = new byte[SessionStoreWriter.MaxSnapshotBytes + 1];
        Array.Fill(bytes, (byte)' ');
        await File.WriteAllBytesAsync(path, bytes);

        var error =
            await TestAssert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadAsync(dir.Root, id,
                                                                         CancellationToken.None));
        TestAssert.Contains("exceeds", error.Message, StringComparison.Ordinal);
    }

    [Test]
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
            await writer.CommitAsync(session, NewContext(), CancellationToken.None);
        }

        var damagedId = new string('e', 32);
        await File.WriteAllTextAsync(Path.Combine(dir.Root, $"{damagedId}.session.json"), "not json");

        var entries = await SessionStoreReader.ListAsync(dir.Root, CancellationToken.None);
        Assert.That(entries.Count, Is.EqualTo(3));
        Assert.That(entries[0].Id, Is.EqualTo(newer.Id));
        Assert.That(entries[1].Id, Is.EqualTo(older.Id));
        Assert.That(entries[0].Error, Is.Null);
        Assert.That(entries[2].Id, Is.EqualTo(damagedId));
        TestAssert.NotNull(entries[2].Error);
        TestAssert.Contains("damaged JSON", entries[2].Error, StringComparison.Ordinal);
    }

    [Test]
    public void ResolveProtected_RejectsWorkspaceChildrenAndRelativePaths()
    {
        using var dir = new TestTempDir();

        var childError =
            TestAssert.Throws<SessionStoreException>(() => SessionStorePaths.ResolveProtected(Path.Combine(dir.Root,
                                                                       "sessions"), WorkspaceRoot(dir)));
        TestAssert.Contains("lies inside the workspace", childError.Message, StringComparison.Ordinal);

        var relativeError =
            TestAssert.Throws<SessionStoreException>(() => SessionStorePaths.ResolveProtected("relative/sessions",
                                                              WorkspaceRoot(dir)));
        TestAssert.Contains("absolute path", relativeError.Message, StringComparison.Ordinal);
    }

    [Test]
    public void ResolveProtected_AcceptsAnAbsoluteDirectoryOutsideTheWorkspace()
    {
        using var dir  = new TestTempDir();
        using var home = new TestTempDir();

        var resolved = SessionStorePaths.ResolveProtected(home.Root, WorkspaceRoot(dir));
        Assert.That(resolved, Is.EqualTo(Path.GetFullPath(home.Root)));
    }

    [Test]
    public void ResolveProtected_RejectsALinkThatLandsInsideTheWorkspace()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var dir      = new TestTempDir();
        using var home     = new TestTempDir();
        var       linkPath = Path.Combine(home.Root, "linked-store");
        if (!TryCreateJunction(linkPath, dir.Root)) return; // 无 junction 权限；无可验证内容。

        var error =
            TestAssert.Throws<SessionStoreException>(() => SessionStorePaths
                                                        .ResolveProtected(linkPath, WorkspaceRoot(dir)));
        TestAssert.Contains("resolves through links into the workspace", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public void DefaultDirectory_IsAbsoluteAndOutsideAnyWorkspace()
    {
        using var dir = new TestTempDir();

        var directory = SessionStorePaths.DefaultDirectory();
        Assert.That(Path.IsPathFullyQualified(directory), Is.True);
        Assert.That(Workspace.IsInside(dir.Root, directory), Is.False);
    }

    [Test]
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
                await TestAssert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadLegacyAsync(dir.Root,
                                                                             id,
                                                                             CancellationToken.None));
            TestAssert.Contains("no run result or message list", error.Message, StringComparison.Ordinal);
        }
    }

    [Test]
    public async Task Read_RejectsADamagedAuditLine()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddFinishedTurn(session, "q", "a");
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, NewContext(), CancellationToken.None);
        }

        await File.AppendAllTextAsync(Path.Combine(dir.Root, $"{session.Id}.audit.jsonl"), "{ broken");

        var error =
            await TestAssert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadAsync(dir.Root, session.Id,
                                                                         CancellationToken.None));
        TestAssert.Contains("damaged line", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public async Task Commit_CancelledWriteIsRejectedAsNotReliablySaved()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddFinishedTurn(session, "q", "a");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var writer = new SessionStoreWriter(dir.Root, session.Id);
        var error =
            await TestAssert.ThrowsAsync<SessionStoreException>(() => writer.CommitAsync(session, NewContext(),
                                                                         cts.Token));
        TestAssert.Contains("not reliably saved", error.Message, StringComparison.Ordinal);
        Assert.That(File.Exists(Path.Combine(dir.Root, $"{session.Id}.session.json")), Is.False);
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

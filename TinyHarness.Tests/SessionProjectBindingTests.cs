using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.ProjectInstructions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;

namespace TinyHarness.Tests;

public class SessionProjectBindingTests
{
    private static readonly SessionModelIdentity Model =
        new("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions);

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ChatSession NewSession(string? projectId = null, string? id = null) =>
        new(id ?? NewId(), Path.GetTempPath(), Model, null, projectId);

    private static void AddTurn(ChatSession session, string input, string answer)
    {
        var turn = new SessionTurn(Guid.NewGuid().ToString("N"), input);
        var run  = new SessionRun(Guid.NewGuid().ToString("N"), session.Id, turn.Id);
        session.AddTurn(turn);
        turn.AddRun(run);
        run.Finish(new AgentResult { Status = AgentStatus.Completed, FinalMessage = answer, Steps = 1 });
    }

    private static SessionLogRecord BindingRecord(string projectId) =>
        new(string.Empty, "project-binding",
            JsonSerializer.SerializeToElement(
                new SessionLogProjectBinding(SessionLogProjectBinding.SchemaName, projectId),
                SessionLogJsonContext.Default.SessionLogProjectBinding));

    private static SessionLogRecord RawBindingRecord(JsonObject payload) =>
        new(string.Empty, "project-binding", JsonSerializer.SerializeToElement(payload));

    private static AuditRecord AuditNote() => new() { RunId = "run-note", Kind = "note" };

    private static ProjectInstructionSnapshot CreateSnapshot(string content) =>
        ProjectInstructionRenderer.Create(
        [
            new ProjectInstructionDocument
            {
                SourcePath     = "AGENTS.md",
                ScopeDirectory = ".",
                Content        = content,
                ContentHash    = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
                ByteCount      = Encoding.UTF8.GetByteCount(content),
                LoadedUtc      = DateTimeOffset.UtcNow
            }
        ], ["."]);

    /// <summary>
    ///     <para>直接追加归属批次以测试读取端的拒绝行为。</para>
    ///     <para>Appends a raw ownership batch to test reader rejection behavior.</para>
    /// </summary>
    private static async Task AppendRawBindingBatchAsync(string path, string sessionId, long sequence, JsonNode payload)
    {
        var batch = new JsonObject
        {
            ["format"]     = SessionLogFormat.FormatName,
            ["sessionId"]  = sessionId,
            ["sequence"]   = sequence,
            ["batchId"]    = $"{sessionId}:{sequence:D20}",
            ["writtenUtc"] = DateTimeOffset.UtcNow,
            ["records"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"]      = $"{sessionId}:{sequence:D20}:0000",
                    ["kind"]    = "project-binding",
                    ["payload"] = payload
                }
            }
        };
        await File.AppendAllTextAsync(path, batch.ToJsonString() + Environment.NewLine);
    }

    private static async Task<int> CountBindingRecordsAsync(string path)
    {
        var count = 0;
        foreach (var line in await File.ReadAllLinesAsync(path))
        foreach (var record in JsonNode.Parse(line)!["records"]!.AsArray())
            if (record!["kind"]!.GetValue<string>() == "project-binding")
                count++;
        return count;
    }

    private static async Task<string> SingleBindingProjectIdAsync(string path)
    {
        string? found = null;
        foreach (var line in await File.ReadAllLinesAsync(path))
        foreach (var record in JsonNode.Parse(line)!["records"]!.AsArray())
            if (record!["kind"]!.GetValue<string>() == "project-binding")
            {
                Assert.That(found, Is.Null, "Expected exactly one project-binding record.");
                found = record["payload"]!["projectId"]!.GetValue<string>();
            }

        Assert.That(found, Is.Not.Null, "Expected exactly one project-binding record.");
        return found!;
    }

    private static string Serialized(PersistedMessage message) =>
        JsonSerializer.Serialize(message, SessionLogJsonContext.Default.PersistedMessage);

    /// <summary>
    ///     <para>按游标读取全部历史以逐项比较归属追加前后的内容。</para>
    ///     <para>Reads all history pages for item-by-item comparison across an ownership append.</para>
    /// </summary>
    private static async Task<List<SessionHistoryItem>> ReadAllHistoryAsync(string directory, string sessionId)
    {
        var                   reader = new SessionHistoryReader();
        var                   items  = new List<SessionHistoryItem>();
        SessionHistoryCursor? cursor = null;
        do
        {
            var page = await reader.ReadPageAsync(directory, sessionId, 2, cursor, CancellationToken.None);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);

        return items;
    }

    private static async Task CommitSimpleSessionWithHistoryAsync(
        TestTempDir dir, ChatSession session, ConversationContext context)
    {
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
            var turnId = session.Turns[0].Id;
            await writer.AppendRecordsAsync(
            [
                new SessionLogRecord(string.Empty, "history",
                                     JsonSerializer.SerializeToElement(new SessionHistoryItem
                                     {
                                         Id        = string.Empty,
                                         Sequence  = 0,
                                         SessionId = session.Id,
                                         TurnId    = turnId,
                                         Kind      = SessionHistoryKind.User,
                                         Text      = "question"
                                     }, SessionHistoryJsonContext.Default.SessionHistoryItem)),
                new SessionLogRecord(string.Empty, "history",
                                     JsonSerializer.SerializeToElement(new SessionHistoryItem
                                     {
                                         Id        = string.Empty,
                                         Sequence  = 0,
                                         SessionId = session.Id,
                                         TurnId    = turnId,
                                         Kind      = SessionHistoryKind.Final,
                                         Text      = "answer"
                                     }, SessionHistoryJsonContext.Default.SessionHistoryItem))
            ], CancellationToken.None);
        }
    }

    private static async Task<PersistedSession> CommitSimpleSessionAsync(TestTempDir dir, string? projectId = null)
    {
        var session = NewSession(projectId);
        var context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        AddTurn(session, "question", "answer");
        context.Append(ChatMessage.User("question"));
        context.Append(ChatMessage.Assistant("answer"));
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        return (await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None))!;
    }

    [Test]
    public async Task NewSessionWithProject_CommitsBindingInsideIdentityBatch()
    {
        using var dir       = new TestTempDir();
        var       projectId = NewId();
        var       session   = NewSession(projectId);
        var       context   = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        AddTurn(session, "question", "answer");
        context.Append(ChatMessage.User("question"));
        context.Append(ChatMessage.Assistant("answer"));
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
            Assert.That(writer.CommitSequence, Is.EqualTo(1));
        }

        var path  = Path.Combine(dir.Root, session.Id + SessionStoreWriter.LogFileSuffix);
        var lines = await File.ReadAllLinesAsync(path);
        Assert.That(lines.Length, Is.EqualTo(1));
        using var batchDoc = JsonDocument.Parse(lines[0]);
        var       records  = batchDoc.RootElement.GetProperty("records");
        Assert.That(records[0]!.GetProperty("kind").GetString(), Is.EqualTo("identity"));
        Assert.That(records[1]!.GetProperty("kind").GetString(), Is.EqualTo("project-binding"));
        Assert.That(records[1]!.GetProperty("payload").GetProperty("schema").GetString(),
                    Is.EqualTo("tinyharness.project-binding/1"));
        Assert.That(records[1]!.GetProperty("payload").GetProperty("projectId").GetString(), Is.EqualTo(projectId));

        var restored = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(restored);
        Assert.That(restored!.ProjectId, Is.EqualTo(projectId));
        Assert.That(restored.LastActivityUtc, Is.EqualTo(restored.UpdatedUtc));
    }

    [Test]
    public async Task NewSessionWithoutProject_StaysUnboundAndReadable()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        var       context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        AddTurn(session, "question", "answer");
        context.Append(ChatMessage.User("question"));
        context.Append(ChatMessage.Assistant("answer"));
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        var restored = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(restored);
        Assert.That(restored!.ProjectId, Is.Null);
        Assert.That(restored.LastActivityUtc, Is.EqualTo(restored.UpdatedUtc));
        var resumed = SessionResume.Restore(restored, Path.GetTempPath(), "system");
        Assert.That(resumed.Session.ProjectId, Is.Null);
    }

    [Test]
    public async Task AppendBinding_PreservesHistoryOpaqueRulesInspectionAndBytePrefix()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        var       context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        AddTurn(session, "question", "answer");
        var rules = CreateSnapshot("RULE_BODY_MUST_SURVIVE_BINDING");
        context.StageProjectInstructions(rules, "initial", session.Turns[0].Id, session.Turns[0].Runs[0].Id);
        context.Append(ChatMessage.User("question"));
        context.Append(new ChatMessage
        {
            Role      = ChatRole.Assistant,
            Content   = "answer",
            Reasoning = [new ReasoningContent("opaque summary", "protected-opaque-data", "item-1")]
        });
        session.PendingInspectionReason = "manual inspection pending";
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
            context.ConfirmProjectInstructions();
        }

        var path   = Path.Combine(dir.Root, session.Id + SessionStoreWriter.LogFileSuffix);
        var before = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(before);
        Assert.That(before!.ProjectId, Is.Null);
        Assert.That(before.LastActivityUtc, Is.EqualTo(before.UpdatedUtc));
        var prefix = await File.ReadAllBytesAsync(path);

        var projectId = NewId();
        using (var writer = await SessionStoreWriter.OpenExistingAsync(dir.Root, session.Id, null,
                                                                       CancellationToken.None))
        {
            await writer.AppendRecordsAsync([BindingRecord(projectId)], CancellationToken.None);
        }

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.That(bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix), Is.True);
        Assert.That(bytes.Length, Is.GreaterThan(prefix.Length));

        var after = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(after);
        Assert.That(after!.ProjectId, Is.EqualTo(projectId));
        Assert.That(after.CommitSequence, Is.EqualTo(before.CommitSequence + 1));
        Assert.That(after.UpdatedUtc, Is.GreaterThanOrEqualTo(before.UpdatedUtc));
        Assert.That(after.LastActivityUtc, Is.EqualTo(before.UpdatedUtc));
        Assert.That(after.PendingInspectionReason, Is.EqualTo(before.PendingInspectionReason));
        Assert.That(after.InspectionAcknowledgedUtc, Is.EqualTo(before.InspectionAcknowledgedUtc));
        Assert.That(after.Turns.Count, Is.EqualTo(before.Turns.Count));
        Assert.That(after.Turns[0].Runs.Single().FinalMessage,
                    Is.EqualTo(before.Turns[0].Runs.Single().FinalMessage));
        var beforeConversation = before.Conversation!;
        var afterConversation  = after.Conversation!;
        Assert.That(afterConversation.Messages.Count, Is.EqualTo(beforeConversation.Messages.Count));
        for (var i = 0; i < afterConversation.Messages.Count; i++)
            Assert.That(Serialized(afterConversation.Messages[i]),
                        Is.EqualTo(Serialized(beforeConversation.Messages[i])));
        Assert.That(afterConversation.ProjectInstructionRecords.Count,
                    Is.EqualTo(beforeConversation.ProjectInstructionRecords.Count));
        Assert.That(afterConversation.ProjectInstructionRecords[0].Revision,
                    Is.EqualTo(beforeConversation.ProjectInstructionRecords[0].Revision));
        Assert.That(afterConversation.ProjectInstructionRecords[0].Snapshot.Documents.Single().Content,
                    Is.EqualTo("RULE_BODY_MUST_SURVIVE_BINDING"));
        Assert.That(afterConversation.State, Is.EqualTo(beforeConversation.State));
        Assert.That(afterConversation.FoldStartMessageIndex, Is.EqualTo(beforeConversation.FoldStartMessageIndex));
        Assert.That(afterConversation.FoldEndMessageIndex, Is.EqualTo(beforeConversation.FoldEndMessageIndex));

        var restoration = SessionResume.Restore(after, Path.GetTempPath(), "system");
        Assert.That(restoration.Session.ProjectId, Is.EqualTo(projectId));
        Assert.That(restoration.NeedsInspection, Is.EqualTo("manual inspection pending"));
        Assert.That(restoration.Context.ProjectInstructions!.Documents.Single().Content,
                    Is.EqualTo("RULE_BODY_MUST_SURVIVE_BINDING"));
    }

    [Test]
    public async Task Reader_RejectsConflictingDuplicateBinding_InFullAndPagingReads()
    {
        using var dir       = new TestTempDir();
        var       projectId = NewId();
        var       session   = NewSession(projectId);
        var       context   = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        AddTurn(session, "question", "answer");
        context.Append(ChatMessage.User("question"));
        context.Append(ChatMessage.Assistant("answer"));
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        var path = Path.Combine(dir.Root, session.Id + SessionStoreWriter.LogFileSuffix);
        await AppendRawBindingBatchAsync(path, session.Id, 2, new JsonObject
        {
            ["schema"]    = SessionLogProjectBinding.SchemaName,
            ["projectId"] = NewId()
        });

        var error = await TestAssert.ThrowsAsync<SessionStoreException>(
            () => SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None));
        TestAssert.Contains("project binding", error.Message, StringComparison.Ordinal);

        error = await TestAssert.ThrowsAsync<SessionStoreException>(() => new SessionHistoryReader().ReadPageAsync(
                                                                        dir.Root, session.Id,
                                                                        SessionHistoryReader.MaxPageItems, null,
                                                                        CancellationToken.None));
        TestAssert.Contains("project binding", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public async Task Reader_RejectsSameIdDuplicateBinding()
    {
        using var dir       = new TestTempDir();
        var       projectId = NewId();
        var       session   = NewSession(projectId);
        var       context   = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        AddTurn(session, "question", "answer");
        context.Append(ChatMessage.User("question"));
        context.Append(ChatMessage.Assistant("answer"));
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        var path = Path.Combine(dir.Root, session.Id + SessionStoreWriter.LogFileSuffix);
        await AppendRawBindingBatchAsync(path, session.Id, 2, new JsonObject
        {
            ["schema"]    = SessionLogProjectBinding.SchemaName,
            ["projectId"] = projectId
        });

        var error = await TestAssert.ThrowsAsync<SessionStoreException>(
            () => SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None));
        TestAssert.Contains("project binding", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public async Task Reader_RejectsMalformedBindingPayloads()
    {
        var projectId = NewId();
        JsonObject Payload(string schema, string id) =>
            new() { ["schema"] = schema, ["projectId"] = id };
        var extraProperty = Payload(SessionLogProjectBinding.SchemaName, projectId);
        extraProperty["note"] = "extra";
        var cases = new (string Name, JsonObject Payload)[]
        {
            ("unknown-schema", Payload("tinyharness.project-binding/2", projectId)),
            ("short-id", Payload(SessionLogProjectBinding.SchemaName, "abc")),
            ("uppercase-id", Payload(SessionLogProjectBinding.SchemaName, projectId.ToUpperInvariant())),
            ("missing-project-id", new JsonObject { ["schema"] = SessionLogProjectBinding.SchemaName }),
            ("extra-property", extraProperty),
            ("non-string-schema", new JsonObject { ["schema"] = 1, ["projectId"] = projectId })
        };

        foreach (var (name, payload) in cases)
        {
            using var dir = new TestTempDir();
            await CommitSimpleSessionAsync(dir);
            var sessionIds = Directory.GetFiles(dir.Root, "*" + SessionStoreWriter.LogFileSuffix);
            var sessionId  = Path.GetFileName(sessionIds[0])[..^SessionStoreWriter.LogFileSuffix.Length];
            await AppendRawBindingBatchAsync(sessionIds[0], sessionId, 2, payload);
            var error = await TestAssert.ThrowsAsync<SessionStoreException>(
                () => SessionStoreReader.ReadAsync(dir.Root, sessionId, CancellationToken.None));
            TestAssert.Contains("project-binding", error.Message, StringComparison.Ordinal);
        }
    }

    [Test]
    public void BindingParse_RejectsMalformedStructures()
    {
        var projectId = NewId();
        Assert.Throws<InvalidDataException>(() => SessionLogProjectBinding.Parse(JsonDocument.Parse("null").RootElement));
        Assert.Throws<InvalidDataException>(() => SessionLogProjectBinding.Parse(
            JsonDocument.Parse(
                $"{{\"schema\":\"{SessionLogProjectBinding.SchemaName}\",\"schema\":\"x\",\"projectId\":\"{projectId}\"}}")
                .RootElement));
        Assert.Throws<InvalidDataException>(() => SessionLogProjectBinding.Parse(
            JsonDocument.Parse(
                $"{{\"schema\":\"{SessionLogProjectBinding.SchemaName}\",\"projectId\":null}}")
                .RootElement));
        Assert.That(SessionLogProjectBinding.Parse(JsonDocument.Parse(
                                               $"{{\"schema\":\"{SessionLogProjectBinding.SchemaName}\",\"projectId\":\"{projectId}\"}}")
                                               .RootElement),
                    Is.EqualTo(projectId));
    }

    [Test]
    public async Task Writer_RejectsRebinding_KeepsLogUnchangedAndHealthy()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        var       context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        AddTurn(session, "question", "answer");
        context.Append(ChatMessage.User("question"));
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        var path = Path.Combine(dir.Root, session.Id + SessionStoreWriter.LogFileSuffix);
        using (var writer = await SessionStoreWriter.OpenExistingAsync(dir.Root, session.Id, null,
                                                                       CancellationToken.None))
        {
            var projectId = NewId();
            await writer.AppendRecordsAsync([BindingRecord(projectId)], CancellationToken.None);
            Assert.That(writer.CommitSequence, Is.EqualTo(2));
            var length = new FileInfo(path).Length;

            var error = await TestAssert.ThrowsAsync<SessionStoreException>(
                () => writer.AppendRecordsAsync([BindingRecord(NewId())], CancellationToken.None));
            TestAssert.Contains("already carries a project binding", error.Message, StringComparison.Ordinal);
            Assert.That(new FileInfo(path).Length, Is.EqualTo(length));
            Assert.That(writer.CommitSequence, Is.EqualTo(2));
            Assert.That(writer.IsHealthy, Is.True);

            await writer.AppendRecordsAsync(
            [
                new SessionLogRecord(string.Empty, "audit",
                                     JsonSerializer.SerializeToElement(AuditNote(),
                                                                       PersistenceJsonContext.Default.AuditRecord))
            ], CancellationToken.None);
            Assert.That(writer.CommitSequence, Is.EqualTo(3));
        }

        var restored = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(restored);
        Assert.That(restored!.ProjectId, Is.EqualTo(await SingleBindingProjectIdAsync(path)));
        Assert.That(await CountBindingRecordsAsync(path), Is.EqualTo(1));
    }

    [Test]
    public async Task Writer_RejectsTwoBindingsInOneAppendBatch_WithoutStateAdvance()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        var       context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        AddTurn(session, "question", "answer");
        context.Append(ChatMessage.User("question"));
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        var path   = Path.Combine(dir.Root, session.Id + SessionStoreWriter.LogFileSuffix);
        var length = new FileInfo(path).Length;
        using (var writer = await SessionStoreWriter.OpenExistingAsync(dir.Root, session.Id, null,
                                                                       CancellationToken.None))
        {
            var projectId = NewId();
            var error     = await TestAssert.ThrowsAsync<SessionStoreException>(
                () => writer.AppendRecordsAsync([BindingRecord(projectId), BindingRecord(projectId)],
                                                CancellationToken.None));
            TestAssert.Contains("already carries a project binding", error.Message, StringComparison.Ordinal);
            Assert.That(new FileInfo(path).Length, Is.EqualTo(length));
            Assert.That(writer.CommitSequence, Is.EqualTo(1));
            Assert.That(writer.IsHealthy, Is.True);

            // 验证失败的批次不推进任何 writer 状态，同值绑定随后仍可正常提交。
            await writer.AppendRecordsAsync([BindingRecord(projectId)], CancellationToken.None);
            Assert.That(writer.CommitSequence, Is.EqualTo(2));
        }

        Assert.That(await CountBindingRecordsAsync(path), Is.EqualTo(1));
    }

    [Test]
    public async Task Writer_RejectsMalformedBindingPayloads_WithoutStateAdvance()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        var       context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        AddTurn(session, "question", "answer");
        context.Append(ChatMessage.User("question"));
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        var projectId = NewId();
        var malformed = new List<JsonObject>
        {
            new() { ["schema"] = "tinyharness.project-binding/2", ["projectId"]     = projectId },
            new() { ["schema"] = SessionLogProjectBinding.SchemaName, ["projectId"] = "abc" },
            new() { ["schema"] = SessionLogProjectBinding.SchemaName },
            new() { ["schema"] = SessionLogProjectBinding.SchemaName, ["projectId"] = projectId, ["note"] = "extra" }
        };

        var path = Path.Combine(dir.Root, session.Id + SessionStoreWriter.LogFileSuffix);
        using (var writer = await SessionStoreWriter.OpenExistingAsync(dir.Root, session.Id, null,
                                                                       CancellationToken.None))
        {
            foreach (var payload in malformed)
            {
                var error = await TestAssert.ThrowsAsync<SessionStoreException>(
                    () => writer.AppendRecordsAsync([RawBindingRecord(payload)], CancellationToken.None));
                TestAssert.Contains("project-binding", error.Message, StringComparison.Ordinal);
                Assert.That(writer.CommitSequence, Is.EqualTo(1));
                Assert.That(writer.IsHealthy, Is.True);
            }

            await writer.AppendRecordsAsync([BindingRecord(projectId)], CancellationToken.None);
            Assert.That(writer.CommitSequence, Is.EqualTo(2));
        }

        var restored = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(restored);
        Assert.That(restored!.ProjectId, Is.EqualTo(projectId));
    }

    [Test]
    public async Task Writer_Commit_RejectsTransferOrDrop_AfterInitialBinding()
    {
        using var dir        = new TestTempDir();
        var       projectIdA = NewId();
        var       projectIdB = NewId();
        var       session    = NewSession(projectIdA);
        var       context    = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        AddTurn(session, "question", "answer");
        context.Append(ChatMessage.User("question"));
        context.Append(ChatMessage.Assistant("answer"));
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        var path = Path.Combine(dir.Root, session.Id + SessionStoreWriter.LogFileSuffix);
        using (var writer = await SessionStoreWriter.OpenExistingAsync(dir.Root, session.Id, null,
                                                                       CancellationToken.None))
        {
            var transferred = NewSession(projectIdB, session.Id);
            var error       = await TestAssert.ThrowsAsync<SessionStoreException>(
                () => writer.CommitAsync(transferred, new ConversationContext(), CancellationToken.None));
            TestAssert.Contains("never transferred", error.Message, StringComparison.Ordinal);

            var dropped = NewSession(id : session.Id);
            error = await TestAssert.ThrowsAsync<SessionStoreException>(
                () => writer.CommitAsync(dropped, new ConversationContext(), CancellationToken.None));
            TestAssert.Contains("never transferred", error.Message, StringComparison.Ordinal);

            AddTurn(session, "question two", "answer two");
            context.Append(ChatMessage.User("question two"));
            context.Append(ChatMessage.Assistant("answer two"));
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        var restored = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(restored);
        Assert.That(restored!.ProjectId, Is.EqualTo(projectIdA));
        Assert.That(await CountBindingRecordsAsync(path), Is.EqualTo(1));
    }

    [Test]
    public async Task Writer_Commit_RequiresAppendBeforeInMemoryBinding()
    {
        using var dir       = new TestTempDir();
        var       projectId = NewId();
        var       session   = NewSession();
        var       context   = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        AddTurn(session, "question", "answer");
        context.Append(ChatMessage.User("question"));
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        session.BindProject(projectId);
        AddTurn(session, "question two", "answer two");
        context.Append(ChatMessage.User("question two"));
        context.Append(ChatMessage.Assistant("answer two"));
        using (var writer = await SessionStoreWriter.OpenExistingAsync(dir.Root, session.Id, null,
                                                                       CancellationToken.None))
        {
            var error = await TestAssert.ThrowsAsync<SessionStoreException>(
                () => writer.CommitAsync(session, context, CancellationToken.None));
            TestAssert.Contains("never transferred", error.Message, StringComparison.Ordinal);

            await writer.AppendRecordsAsync([BindingRecord(projectId)], CancellationToken.None);
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        var restored = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(restored);
        Assert.That(restored!.ProjectId, Is.EqualTo(projectId));
        Assert.That(restored.Turns.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task OpenExisting_SeedsBinding_NextCommitDoesNotDuplicate()
    {
        using var dir       = new TestTempDir();
        var       projectId = NewId();
        var       session   = NewSession(projectId);
        var       context   = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        AddTurn(session, "question", "answer");
        context.Append(ChatMessage.User("question"));
        context.Append(ChatMessage.Assistant("answer"));
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        AddTurn(session, "question two", "answer two");
        context.Append(ChatMessage.User("question two"));
        context.Append(ChatMessage.Assistant("answer two"));
        using (var writer = await SessionStoreWriter.OpenExistingAsync(dir.Root, session.Id, null,
                                                                       CancellationToken.None))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        var path     = Path.Combine(dir.Root, session.Id + SessionStoreWriter.LogFileSuffix);
        var restored = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(restored);
        Assert.That(restored!.ProjectId, Is.EqualTo(projectId));
        Assert.That(restored.CommitSequence, Is.EqualTo(2));
        Assert.That(restored.LastActivityUtc, Is.EqualTo(restored.UpdatedUtc));
        Assert.That(await CountBindingRecordsAsync(path), Is.EqualTo(1));

        var resumed = SessionResume.Restore(restored, Path.GetTempPath(), "system");
        Assert.That(resumed.Session.ProjectId, Is.EqualTo(projectId));
    }

    [Test]
    public void ChatSession_ProjectBindingContract()
    {
        Assert.That(NewSession().ProjectId, Is.Null);
        Assert.Throws<ArgumentException>(
            () => new ChatSession(NewId(), Path.GetTempPath(), Model, null, "abc"));
        Assert.Throws<ArgumentException>(
            () => new ChatSession(NewId(), Path.GetTempPath(), Model, null, "0123456789ABCDEF0123456789ABCDEF"));
        Assert.Throws<ArgumentException>(
            () => new ChatSession(NewId(), Path.GetTempPath(), Model, null, new string('z', 32)));

        var projectId = NewId();
        var session   = NewSession(projectId);
        Assert.That(session.ProjectId, Is.EqualTo(projectId));
        // 同值重申幂等，不视为转移。
        session.BindProject(projectId);
        Assert.That(session.ProjectId, Is.EqualTo(projectId));
        Assert.Throws<InvalidOperationException>(() => session.BindProject(NewId()));
        Assert.Throws<ArgumentException>(() => session.BindProject("bad"));

        var unbound = NewSession();
        unbound.BindProject(projectId);
        Assert.That(unbound.ProjectId, Is.EqualTo(projectId));
    }

    [Test]
    public void ChatSession_PreservesLegacyConstructorSignature()
    {
        var flags     = BindingFlags.Instance | BindingFlags.Public;
        var binder    = default(Binder);
        var modifiers = Array.Empty<ParameterModifier>();
        var legacy = typeof(ChatSession).GetConstructor(flags, binder,
                                                       [typeof(string), typeof(string), typeof(SessionModelIdentity),
                                                        typeof(DateTimeOffset?)], modifiers);
        Assert.That(legacy, Is.Not.Null,
                    "The original four-parameter constructor must keep its CLR signature for binary compatibility.");
        var withProject = typeof(ChatSession).GetConstructor(flags, binder,
                                                             [typeof(string), typeof(string),
                                                              typeof(SessionModelIdentity), typeof(DateTimeOffset?),
                                                              typeof(string)], modifiers);
        Assert.That(withProject, Is.Not.Null);
    }

    [Test]
    public async Task BindingAppend_WriteFaultBeforeWrite_PoisonsWithoutWriting()
    {
        using var dir       = new TestTempDir();
        var       session   = NewSession();
        var       projectId = NewId();
        var       context   = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        AddTurn(session, "question", "answer");
        context.Append(ChatMessage.User("question"));
        using var writer = new SessionStoreWriter(dir.Root, session.Id)
        {
            InjectWriteFault = (batch, phase) =>
            {
                if (phase == "before-write" && batch.Records.Any(record => record.Kind == "project-binding"))
                    throw new IOException("injected binding write fault");
            }
        };
        await writer.CommitAsync(session, context, CancellationToken.None);
        var path   = writer.SnapshotPath;
        var length = new FileInfo(path).Length;

        var error = await TestAssert.ThrowsAsync<SessionStoreException>(
            () => writer.AppendRecordsAsync([BindingRecord(projectId)], CancellationToken.None));
        TestAssert.Contains("poisoned", error.Message, StringComparison.Ordinal);
        Assert.That(writer.IsHealthy, Is.False);
        Assert.That(new FileInfo(path).Length, Is.EqualTo(length));

        // poison 后再次追加按既有契约直接拒绝。
        var retry = await TestAssert.ThrowsAsync<SessionStoreException>(
            () => writer.AppendRecordsAsync([BindingRecord(projectId)], CancellationToken.None));
        TestAssert.Contains("poisoned", retry.Message, StringComparison.Ordinal);
        writer.Dispose();

        var restored = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(restored);
        Assert.That(restored!.ProjectId, Is.Null);
    }

    [Test]
    public async Task BindingAppend_WriteFaultAfterFlush_UncertainWriteLands_RereadSeesBound()
    {
        using var dir       = new TestTempDir();
        var       session   = NewSession();
        var       projectId = NewId();
        var       context   = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        AddTurn(session, "question", "answer");
        context.Append(ChatMessage.User("question"));
        using var writer = new SessionStoreWriter(dir.Root, session.Id)
        {
            InjectWriteFault = (batch, phase) =>
            {
                if (phase == "after-flush" && batch.Records.Any(record => record.Kind == "project-binding"))
                    throw new IOException("injected binding flush fault");
            }
        };
        await writer.CommitAsync(session, context, CancellationToken.None);
        var length = new FileInfo(writer.SnapshotPath).Length;

        // flush 完成后的故障：报告不确定失败，但字节实际落盘；可靠事实以下一轮严格重读为准。
        var error = await TestAssert.ThrowsAsync<SessionStoreException>(
            () => writer.AppendRecordsAsync([BindingRecord(projectId)], CancellationToken.None));
        TestAssert.Contains("poisoned", error.Message, StringComparison.Ordinal);
        Assert.That(writer.IsHealthy, Is.False);
        Assert.That(new FileInfo(writer.SnapshotPath).Length, Is.GreaterThan(length));
        writer.Dispose();

        var restored = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(restored);
        Assert.That(restored!.ProjectId, Is.EqualTo(projectId));
    }

    [Test]
    public async Task BindingAppend_PreCancelledToken_ThrowsCancellationWithoutPoison()
    {
        using var dir       = new TestTempDir();
        var       session   = NewSession();
        var       projectId = NewId();
        var       context   = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        AddTurn(session, "question", "answer");
        context.Append(ChatMessage.User("question"));
        using var writer = new SessionStoreWriter(dir.Root, session.Id);
        await writer.CommitAsync(session, context, CancellationToken.None);
        var length = new FileInfo(writer.SnapshotPath).Length;

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // 写盘前的取消原样传播为取消（含派生类型），不 poison、不写字节。
        OperationCanceledException? cancelled = null;
        try
        {
            await writer.AppendRecordsAsync([BindingRecord(projectId)], cts.Token);
        }
        catch (OperationCanceledException ex)
        {
            cancelled = ex;
        }

        Assert.That(cancelled, Is.Not.Null);
        Assert.That(writer.IsHealthy, Is.True);
        Assert.That(new FileInfo(writer.SnapshotPath).Length, Is.EqualTo(length));
    }

    [Test]
    public async Task AppendBinding_PreservesNonEmptyCompactionCheckpoint()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        var       context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        AddTurn(session, "question", "answer");
        context.Append(ChatMessage.User("question"));
        context.Append(ChatMessage.Assistant("answer"));
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
            var checkpointMessages = context.Messages.ToArray();
            context.Restore(checkpointMessages, new CompactionState { Summary = "checkpoint handoff" }, true, 1, 2);
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        var before = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(before);
        Assert.That(before!.Conversation!.HasState, Is.True);
        Assert.That(before.Conversation.State!.Summary, Is.EqualTo("checkpoint handoff"));
        Assert.That(before.Conversation.FoldStartMessageIndex, Is.EqualTo(1));
        Assert.That(before.Conversation.FoldEndMessageIndex, Is.EqualTo(2));
        Assert.That(before.LastActivityUtc, Is.EqualTo(before.UpdatedUtc));
        var prefix = await File.ReadAllBytesAsync(Path.Combine(dir.Root, session.Id + SessionStoreWriter.LogFileSuffix));

        var projectId = NewId();
        using (var writer = await SessionStoreWriter.OpenExistingAsync(dir.Root, session.Id, null,
                                                                       CancellationToken.None))
        {
            await writer.AppendRecordsAsync([BindingRecord(projectId)], CancellationToken.None);
        }

        var after = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(after);
        Assert.That(after!.ProjectId, Is.EqualTo(projectId));
        Assert.That(after.Conversation!.HasState, Is.True);
        Assert.That(after.Conversation.State!.Summary, Is.EqualTo("checkpoint handoff"));
        Assert.That(after.Conversation.FoldStartMessageIndex, Is.EqualTo(1));
        Assert.That(after.Conversation.FoldEndMessageIndex, Is.EqualTo(2));
        Assert.That(after.Conversation.CheckpointModelSelectionRevision,
                    Is.EqualTo(before.Conversation.CheckpointModelSelectionRevision));
        Assert.That(after.Conversation.Messages.Count, Is.EqualTo(before.Conversation.Messages.Count));
        Assert.That(after.LastActivityUtc, Is.EqualTo(before.UpdatedUtc));
        Assert.That(after.UpdatedUtc, Is.GreaterThanOrEqualTo(before.UpdatedUtc));
        var bytes = await File.ReadAllBytesAsync(Path.Combine(dir.Root, session.Id + SessionStoreWriter.LogFileSuffix));
        Assert.That(bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix), Is.True);
    }

    [Test]
    public async Task AppendBinding_PagedHistoryUnchangedItemByItem()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        var       context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        AddTurn(session, "question", "answer");
        context.Append(ChatMessage.User("question"));
        await CommitSimpleSessionWithHistoryAsync(dir, session, context);
        var before = await ReadAllHistoryAsync(dir.Root, session.Id);
        Assert.That(before.Count, Is.EqualTo(2));

        var projectId = NewId();
        using (var writer = await SessionStoreWriter.OpenExistingAsync(dir.Root, session.Id, null,
                                                                       CancellationToken.None))
        {
            await writer.AppendRecordsAsync([BindingRecord(projectId)], CancellationToken.None);
        }

        var after = await ReadAllHistoryAsync(dir.Root, session.Id);
        Assert.That(after.Count, Is.EqualTo(before.Count));
        for (var i = 0; i < after.Count; i++)
        {
            Assert.That(after[i].Id, Is.EqualTo(before[i].Id));
            Assert.That(after[i].Sequence, Is.EqualTo(before[i].Sequence));
            Assert.That(after[i].Kind, Is.EqualTo(before[i].Kind));
            Assert.That(after[i].Text, Is.EqualTo(before[i].Text));
            Assert.That(after[i].TurnId, Is.EqualTo(before[i].TurnId));
        }

        var restored = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(restored);
        Assert.That(restored!.ProjectId, Is.EqualTo(projectId));
    }
}

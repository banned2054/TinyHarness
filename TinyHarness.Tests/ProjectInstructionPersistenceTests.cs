using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.ProjectInstructions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;

namespace TinyHarness.Tests;

public class ProjectInstructionPersistenceTests
{
    private static readonly SessionModelIdentity Model = new("test-model", "https://example.invalid/v1",
                                                             ChatApiKind.ChatCompletions);

    [Test]
    public async Task ReadLegacyUnifiedLogWithoutInstructionRecords_RestoresUnmanagedProjection()
    {
        using var directory = new TestTempDir();
        var       sessionId = Guid.NewGuid().ToString("N");
        var session = new ChatSession(sessionId, Path.GetTempPath(),
                                      new SessionModelIdentity("test-model", "https://example.invalid/v1",
                                                               ChatApiKind.ChatCompletions));
        using (var writer = new SessionStoreWriter(directory.Root, sessionId))
        {
            await writer.CommitAsync(session, new ConversationContext(), CancellationToken.None);
        }

        var restored = await SessionStoreReader.ReadAsync(directory.Root, sessionId, CancellationToken.None);

        TestAssert.NotNull(restored);
        Assert.That(restored!.Conversation!.ProjectInstructionRecords, Is.Empty);
    }

    [Test]
    public async Task AppendRecords_RejectsProjectInstructionHistoryOutsideStructuredCommit()
    {
        using var directory = new TestTempDir();
        var       sessionId = Guid.NewGuid().ToString("N");
        var session = new ChatSession(sessionId, Path.GetTempPath(),
                                      new SessionModelIdentity("test-model", "https://example.invalid/v1",
                                                               ChatApiKind.ChatCompletions));
        using var writer = new SessionStoreWriter(directory.Root, sessionId);
        await writer.CommitAsync(session, new ConversationContext(), CancellationToken.None);

        var payload = JsonSerializer.SerializeToElement(new SessionHistoryItem
        {
            Id        = string.Empty,
            Sequence  = 0,
            SessionId = sessionId,
            TurnId    = "turn",
            RunId     = "run",
            Kind      = SessionHistoryKind.ProjectInstructions,
            Text      = "revision summary"
        }, SessionHistoryJsonContext.Default.SessionHistoryItem);

        var error = await TestAssert.ThrowsAsync<SessionStoreException>(() => writer.AppendRecordsAsync(
        [
            new SessionLogRecord(string.Empty, "history", payload)
        ], CancellationToken.None));

        Assert.That(error.Message, Does.Contain("generic journal API"));
        Assert.That(writer.CommitSequence, Is.EqualTo(1));
    }

    [Test]
    public async Task AppendRecords_RejectsProjectInstructionsAsAnUnrecognizedRecordKind()
    {
        using var directory = new TestTempDir();
        var       sessionId = Guid.NewGuid().ToString("N");
        var session = new ChatSession(sessionId, Path.GetTempPath(),
                                      new SessionModelIdentity("test-model", "https://example.invalid/v1",
                                                               ChatApiKind.ChatCompletions));
        using var writer = new SessionStoreWriter(directory.Root, sessionId);
        await writer.CommitAsync(session, new ConversationContext(), CancellationToken.None);

        var error = await TestAssert.ThrowsAsync<SessionStoreException>(() => writer.AppendRecordsAsync([
            new SessionLogRecord(string.Empty, "project-instructions", JsonSerializer.SerializeToElement(new { }))
        ], CancellationToken.None));

        Assert.That(error.Message, Does.Contain("Unknown required journal record kind"));
        Assert.That(writer.CommitSequence, Is.EqualTo(1));
    }

    [Test]
    public async Task RootRulesRoundTrip_ScopesTrackingOmitsBodiesAndRestoresCurrentSnapshot()
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        var       initial   = CreateSnapshot("ROOT_RULE_BODY");
        var       context   = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        var session = NewSession(workspace.Root);
        AddTurn(session, "turn-1", "run-1", "first task");
        context.StageProjectInstructions(initial, "initial", "turn-1", "run-1");
        context.Append(ChatMessage.User("first task"));
        context.Append(ChatMessage.Assistant("answer"));

        var tracked = CreateSnapshot("ROOT_RULE_BODY", probedDirectories : [".", "nested"]);
        Assert.That(tracked.Revision, Is.EqualTo(initial.Revision));
        string snapshotPath;
        using (var writer = new SessionStoreWriter(store.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
            context.ConfirmProjectInstructions();
            AddTurn(session, "turn-2", "run-2", "second task");
            context.StageProjectInstructions(tracked, "scope-discovered", "turn-2", "run-2");
            context.Append(ChatMessage.User("second task"));
            context.Append(ChatMessage.Assistant("second answer"));
            await writer.CommitAsync(session, context, CancellationToken.None);
            context.ConfirmProjectInstructions();
            snapshotPath = writer.SnapshotPath;
        }

        var       lines       = await File.ReadAllLinesAsync(snapshotPath);
        using var secondBatch = JsonDocument.Parse(lines[1]);
        var scopesRecord = secondBatch.RootElement.GetProperty("records").EnumerateArray()
                                      .Single(record => record.GetProperty("kind").GetString() ==
                                                        "project-instructions");
        Assert.That(scopesRecord.GetProperty("payload").GetProperty("action").GetString(),
                    Is.EqualTo("scopes-tracked"));
        Assert.That(scopesRecord.GetProperty("payload").GetProperty("snapshot").GetProperty("documents")
                                .GetArrayLength(), Is.Zero);

        var restored = await SessionStoreReader.ReadAsync(store.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(restored);
        var records = restored!.Conversation!.ProjectInstructionRecords;
        Assert.That(records.Count, Is.EqualTo(2));
        Assert.That(records[0].Snapshot.Documents.Single().Content, Is.EqualTo("ROOT_RULE_BODY"));
        Assert.That(records[1].Snapshot.Documents.Single().Content, Is.EqualTo("ROOT_RULE_BODY"));
        Assert.That(records[1].Snapshot.ProbedDirectories, Is.EquivalentTo(tracked.ProbedDirectories));

        var resumed = SessionResume.Restore(restored, workspace.Root, Model, "system");
        Assert.That(resumed.Context.ProjectInstructions!.Revision, Is.EqualTo(initial.Revision));
        Assert.That(resumed.Context.ProjectInstructions.Documents.Single().Content, Is.EqualTo("ROOT_RULE_BODY"));
    }

    [Test]
    public async Task EmptyInitialRules_AreStillBoundAndPersistedBeforeTheTaskMessage()
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        var       snapshot  = CreateSnapshot();
        var       session   = NewSession(workspace.Root);
        AddTurn(session, "turn-1", "run-1", "task");
        var context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        var record = context.StageProjectInstructions(snapshot, "initial", "turn-1", "run-1");
        context.Append(ChatMessage.User("task"));
        context.Append(ChatMessage.Assistant("answer"));

        using (var writer = new SessionStoreWriter(store.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        var restored = await SessionStoreReader.ReadAsync(store.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(restored);
        Assert.That(restored!.Conversation!.ProjectInstructionRecords.Single().Revision, Is.EqualTo(record.Revision));
        Assert.That(restored.Conversation.ProjectInstructionRecords.Single().Snapshot.Documents, Is.Empty);
        Assert.That(restored.Conversation.Messages[1].Content, Is.EqualTo(ProjectInstructionRenderer.Render(snapshot)));
    }

    [Test]
    public async Task ReaderRejectsNullDocumentsInScopesTrackedRecordWithLogPathContext()
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        Directory.CreateDirectory(Path.Combine(workspace.Root, "nested"));
        var initial = CreateSnapshot("ROOT_RULE_FOR_SCOPES_TEST");
        var tracked = CreateSnapshot("ROOT_RULE_FOR_SCOPES_TEST", probedDirectories : [".", "nested"]);
        var session = NewSession(workspace.Root);
        var context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        AddTurn(session, "turn-1", "run-1", "first task");

        string logPath;
        using (var writer = new SessionStoreWriter(store.Root, session.Id))
        {
            context.StageProjectInstructions(initial, "initial", "turn-1", "run-1");
            context.Append(ChatMessage.User("first task"));
            context.Append(ChatMessage.Assistant("first answer"));
            await writer.CommitAsync(session, context, CancellationToken.None);
            context.ConfirmProjectInstructions();
            AddTurn(session, "turn-2", "run-2", "next task");
            context.StageProjectInstructions(tracked, "scope-discovered", "turn-2", "run-2");
            context.Append(ChatMessage.User("next task"));
            context.Append(ChatMessage.Assistant("next answer"));
            await writer.CommitAsync(session, context, CancellationToken.None);
            logPath = writer.SnapshotPath;
        }

        var batches = await File.ReadAllLinesAsync(logPath);
        var batch   = JsonNode.Parse(batches[1])!.AsObject();
        var scopesRecord = batch["records"]!.AsArray()
                                            .Single(item =>
                                                        item!["kind"]!.GetValue<string>() == "project-instructions" &&
                                                        item["payload"]!["action"]!.GetValue<string>() ==
                                                        "scopes-tracked")!;
        scopesRecord["payload"]!["snapshot"]!["documents"] = null;
        batches[1]                                         = batch.ToJsonString();
        await File.WriteAllTextAsync(logPath, string.Join("\n", batches) + "\n");

        var error = await TestAssert.ThrowsAsync<SessionStoreException>(() =>
                                                                            SessionStoreReader.ReadAsync(store.Root,
                                                                                session.Id,
                                                                                CancellationToken.None));
        Assert.That(error.Message, Does.Contain(logPath));
        Assert.That(error.Message, Does.Contain("documents"));
    }

    [Test]
    public async Task CommittedRuleHistory_LiveCallbackAndPageShareIdSequenceAndRedactedMetadata()
    {
        using var    workspace       = new TestTempDir();
        using var    store           = new TestTempDir();
        const string syntheticSecret = "synthetic-secret-71";
        var snapshot = CreateSnapshot("RULE_BODY_MUST_STAY_CANONICAL", $"{syntheticSecret}/AGENTS.md",
                                      syntheticSecret, [".", syntheticSecret]);
        var session = NewSession(workspace.Root);
        AddTurn(session, "turn-1", "run-1", "task");
        var context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        context.StageProjectInstructions(snapshot, "initial", "turn-1", "run-1");
        context.Append(ChatMessage.User("task"));
        context.Append(ChatMessage.Assistant("answer"));
        var canonicalRuleMessage = context.Messages[1].Content;

        var       liveItems = new List<SessionHistoryItem>();
        var       secrets   = new Dictionary<string, string> { ["test"] = syntheticSecret };
        using var writer    = new SessionStoreWriter(store.Root, session.Id, secrets);
        writer.CreateRunRecorder(session.Id, "turn-1", "run-1", _ => Task.CompletedTask,
                                 (item, _) =>
                                 {
                                     liveItems.Add(item);
                                     return ValueTask.CompletedTask;
                                 });
        await writer.CommitAsync(session, context, CancellationToken.None);
        context.ConfirmProjectInstructions();

        var page = await new SessionHistoryReader().ReadPageAsync(store.Root, session.Id, 20, null,
                                                                  CancellationToken.None);
        var live       = liveItems.Single(item => item.Kind  == SessionHistoryKind.ProjectInstructions);
        var historical = page.Items.Single(item => item.Kind == SessionHistoryKind.ProjectInstructions);
        Assert.That((live.Id, live.Sequence), Is.EqualTo((historical.Id, historical.Sequence)));
        Assert.That(historical.Text, Does.Contain("source:").And.Contain("scope:"));
        Assert.That(historical.Text, Does.Contain("[REDACTED]").And.Not.Contain(syntheticSecret));
        Assert.That(historical.Text, Does.Not.Contain("RULE_BODY_MUST_STAY_CANONICAL"));
        Assert.That(canonicalRuleMessage, Does.Contain("RULE_BODY_MUST_STAY_CANONICAL").And.Contain(syntheticSecret));
        var restored = await SessionStoreReader.ReadAsync(store.Root, session.Id, CancellationToken.None);
        Assert.That(restored!.Conversation!.Messages.Single(message => message.Content == canonicalRuleMessage).Content,
                    Is.EqualTo(canonicalRuleMessage));
    }

    [Test]
    public async Task LiveHistoryCallbackFailureBecomesRecorderDisplayWarning()
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        var       snapshot  = CreateSnapshot("rule");
        var       session   = NewSession(workspace.Root);
        AddTurn(session, "turn-1", "run-1", "task");
        var context = ContextWithRules(snapshot, "turn-1", "run-1");
        var writer  = new SessionStoreWriter(store.Root, session.Id);
        var recorder = (SessionLogRunRecorder)writer.CreateRunRecorder(session.Id, "turn-1", "run-1",
                                                                       _ => Task.CompletedTask,
                                                                       (_, _) =>
                                                                           ValueTask
                                                                              .FromException(new
                                                                                   IOException("history sink failed")))
            !;
        using (writer)
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
            Assert.That(recorder.DisplayWarning, Does.Contain("Live history display failed"));
            Assert.That(recorder.DisplayWarning, Does.Contain("history sink failed"));
        }
    }

    [TestCase("revision")]
    [TestCase("hash")]
    [TestCase("index")]
    [TestCase("schema")]
    [TestCase("renderer")]
    [TestCase("documents-null")]
    [TestCase("probed-directories-null")]
    [TestCase("probed-directory-null")]
    [TestCase("document-null")]
    [TestCase("snapshot-null")]
    [TestCase("revision-null")]
    [TestCase("discovery-fingerprint-null")]
    [TestCase("source-null")]
    [TestCase("scope-null")]
    [TestCase("content-null")]
    [TestCase("hash-null")]
    [TestCase("byte-count-null")]
    [TestCase("bom-null")]
    [TestCase("loaded-utc-null")]
    [TestCase("hash-format")]
    [TestCase("source-traversal")]
    [TestCase("scope-mismatch")]
    [TestCase("byte-count")]
    [TestCase("rendered-hash-null")]
    [TestCase("renderer-version-null")]
    public async Task ReaderRejectsTamperedProjectInstructionSource(string field)
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        var       snapshot  = CreateSnapshot("RULE_FOR_TAMPER_TEST");
        var       session   = NewSession(workspace.Root);
        AddTurn(session, "turn-1", "run-1", "task");
        var    context = ContextWithRules(snapshot, "turn-1", "run-1");
        string logPath;
        using (var writer = new SessionStoreWriter(store.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
            logPath = writer.SnapshotPath;
        }

        var batch = JsonNode.Parse(await File.ReadAllTextAsync(logPath))!.AsObject();
        var record = batch["records"]!.AsArray()
                                      .Single(item => item!["kind"]!.GetValue<string>() == "project-instructions")!;
        var payload      = record["payload"]!.AsObject();
        var snapshotNode = payload["snapshot"]!.AsObject();
        var documentNode = snapshotNode["documents"]![0]!.AsObject();
        switch (field)
        {
            case "revision" :                   payload["revision"] = "forged-revision"; break;
            case "hash" :                       documentNode["contentHash"] = new string('0', 64); break;
            case "index" :                      payload["canonicalMessageIndex"] = 999; break;
            case "schema" :                     payload["schema"] = "tinyharness.project-instructions/999"; break;
            case "renderer" :                   snapshotNode["rendererVersion"] = "future-renderer"; break;
            case "documents-null" :             snapshotNode["documents"] = null; break;
            case "probed-directories-null" :    snapshotNode["probedDirectories"] = null; break;
            case "probed-directory-null" :      snapshotNode["probedDirectories"]![0] = null; break;
            case "document-null" :              snapshotNode["documents"]![0] = null; break;
            case "snapshot-null" :              payload["snapshot"] = null; break;
            case "revision-null" :              snapshotNode["revision"] = null; break;
            case "discovery-fingerprint-null" : snapshotNode["discoveryFingerprint"] = null; break;
            case "source-null" :                documentNode["sourcePath"] = null; break;
            case "scope-null" :                 documentNode["scopeDirectory"] = null; break;
            case "content-null" :               documentNode["content"] = null; break;
            case "hash-null" :                  documentNode["contentHash"] = null; break;
            case "byte-count-null" :            documentNode["byteCount"] = null; break;
            case "bom-null" :                   documentNode["hasUtf8Bom"] = null; break;
            case "loaded-utc-null" :            documentNode["loadedUtc"] = null; break;
            case "hash-format" :                documentNode["contentHash"] = "not-a-sha256"; break;
            case "source-traversal" :           documentNode["sourcePath"] = "nested/../AGENTS.md"; break;
            case "scope-mismatch" :             documentNode["scopeDirectory"] = "other"; break;
            case "byte-count" :                 documentNode["byteCount"] = -1; break;
            case "rendered-hash-null" :         snapshotNode["renderedHash"] = null; break;
            case "renderer-version-null" :      snapshotNode["rendererVersion"] = null; break;
        }

        await File.WriteAllTextAsync(logPath, batch.ToJsonString() + "\n");

        var error = await TestAssert.ThrowsAsync<SessionStoreException>(() =>
                                                                            SessionStoreReader.ReadAsync(store.Root,
                                                                                session.Id,
                                                                                CancellationToken.None));
        Assert.That(error.Message, Does.Contain(logPath));
        Assert.That(error.Message, Does.Contain(session.Id));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ProjectInstructionWriteFailureDoesNotAdvanceOrPublishCandidate(bool failInitial)
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        var       first     = CreateSnapshot("FIRST_RULE");
        var       context   = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        var session = NewSession(workspace.Root);
        AddTurn(session, "turn-1", "run-1", "task");
        var shouldFail = failInitial;
        using var writer = new SessionStoreWriter(store.Root, session.Id)
        {
            InjectWriteFault = (batch, phase) =>
            {
                if (shouldFail && phase == "before-write" &&
                    batch.Records.Any(record => record.Kind == "project-instructions"))
                    throw new IOException("injected project-instructions write fault");
            }
        };
        context.StageProjectInstructions(first, "initial", "turn-1", "run-1");
        context.Append(ChatMessage.User("task"));
        context.Append(ChatMessage.Assistant("answer"));
        if (failInitial)
        {
            await TestAssert.ThrowsAsync<SessionStoreException>(() => writer.CommitAsync(session, context,
                                                                    CancellationToken.None));
            Assert.That(writer.CommitSequence, Is.Zero);
            Assert.That(context.ProjectInstructions, Is.Null);
            context.RejectProjectInstructions();
            Assert.That(context.ProjectInstructionRecords, Is.Empty);
            return;
        }

        await writer.CommitAsync(session, context, CancellationToken.None);
        context.ConfirmProjectInstructions();
        shouldFail = true;
        var second = CreateSnapshot("SECOND_RULE");
        AddTurn(session, "turn-2", "run-2", "next task");
        context.StageProjectInstructions(second, "changed", "turn-2", "run-2");
        context.Append(ChatMessage.User("next task"));
        context.Append(ChatMessage.Assistant("next answer"));
        await TestAssert.ThrowsAsync<SessionStoreException>(() => writer.CommitAsync(session, context,
                                                                CancellationToken.None));
        Assert.That(writer.CommitSequence, Is.EqualTo(1));
        Assert.That(context.ProjectInstructions!.Revision, Is.EqualTo(first.Revision));
        context.RejectProjectInstructions();
        var persisted = await SessionStoreReader.ReadAsync(store.Root, session.Id, CancellationToken.None);
        Assert.That(persisted!.Conversation!.ProjectInstructionRecords.Count, Is.EqualTo(1));
        Assert.That(persisted.Conversation.ProjectInstructionRecords.Single().Revision, Is.EqualTo(first.Revision));
    }

    private static ChatSession NewSession(string workspaceRoot) =>
        new(Guid.NewGuid().ToString("N"), workspaceRoot, Model);

    private static ProjectInstructionSnapshot CreateSnapshot(
        string?                content           = null, string sourcePath = "AGENTS.md", string scopeDirectory = ".",
        IReadOnlyList<string>? probedDirectories = null)
    {
        IReadOnlyList<ProjectInstructionDocument> documents = content is null
            ? []
            :
            [
                new ProjectInstructionDocument
                {
                    SourcePath     = sourcePath,
                    ScopeDirectory = scopeDirectory,
                    Content        = content,
                    ContentHash    = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
                    ByteCount      = Encoding.UTF8.GetByteCount(content),
                    LoadedUtc      = DateTimeOffset.UtcNow
                }
            ];
        return ProjectInstructionRenderer.Create(documents, probedDirectories ?? ["."]);
    }

    private static void AddTurn(ChatSession session, string turnId, string runId, string userInput)
    {
        var turn = new SessionTurn(turnId, userInput);
        turn.AddRun(new SessionRun(runId, session.Id, turnId));
        session.AddTurn(turn);
    }

    private static ConversationContext ContextWithRules(
        ProjectInstructionSnapshot snapshot, string turnId, string runId)
    {
        var context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        context.StageProjectInstructions(snapshot, "initial", turnId, runId);
        context.Append(ChatMessage.User("task"));
        context.Append(ChatMessage.Assistant("answer"));
        return context;
    }
}

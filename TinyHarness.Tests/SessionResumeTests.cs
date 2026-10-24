using System.Text.Json.Nodes;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         验证保存后重建宿主的续聊保真：旧回合、目标和 Responses opaque 内容原样传递。
///         覆盖身份、格式与完整性拒绝边界，以及中断工具组回退和检查阻断。
///     </para>
///     <para>
///         Verifies resume fidelity after rebuilding the host: earlier turns, goals and Responses opaque content remain
///         verbatim.
///         Covers identity, format and integrity rejection, interrupted-group rewind and inspection blocking.
///     </para>
/// </summary>
public class SessionResumeTests
{
    private const string SystemPrompt = "You are a test harness inspecting a workspace.";

    private static SessionModelIdentity Identity { get; } =
        new("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions);

    private static string WorkspaceRoot => Path.Combine(Path.GetTempPath(), "tinyharness-session-resume-ws");

    private static AgentOptions Options() => new()
    {
        Model                     = "test-model",
        MaxAgentSteps             = 10,
        DefaultToolTimeoutSeconds = 30
    };

    private static ChatSessionHost NewHost(IChatCompletionClient client, string workspaceRoot, string storeDirectory) =>
        new(workspaceRoot,
            Identity,
            Options(),
            client,
            new ToolRegistry([]),
            SystemPrompt,
            sessionWriterFactory : id => new SessionStoreWriter(storeDirectory, id));

    [Test]
    public async Task Resume_RoundTripsConversationAndReasoningAcrossProcesses()
    {
        using var ws     = new TestTempDir();
        using var dir    = new TestTempDir();
        var       client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ReasonedText("thinking it over", "item_1", "opaque-payload-1", "first answer"));

        string sessionId;
        using (var host = NewHost(client, ws.Root, dir.Root))
        {
            sessionId = host.Session.Id;
            var run = await host.SubmitTurnAsync("what is this repo?", CancellationToken.None);
            Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        }

        // 用新宿主模拟重启：排他打开存储、续种提交序号、恢复上下文后继续。
        using var writer =
            await SessionStoreWriter.OpenExistingAsync(dir.Root, sessionId, null, CancellationToken.None);
        var initialSequence = writer.CommitSequence;
        var restored        = SessionResume.Restore(writer.LoadedSnapshot!, ws.Root, Identity, SystemPrompt);
        Assert.That(initialSequence, Is.GreaterThan(1));
        Assert.That(restored.Session.Id, Is.EqualTo(sessionId));
        TestAssert.Single(restored.Session.Turns);

        var client2 = new FakeChatClient();
        client2.Enqueue(FakeChatClient.Text("second answer"));
        using var host2 = new ChatSessionHost(restored.Session, restored.Context, Options(), client2,
                                              new ToolRegistry([]), SystemPrompt, sessionWriter : writer);
        var run2 = await host2.SubmitTurnAsync("and why?", CancellationToken.None);
        Assert.That(run2.Result!.Status, Is.EqualTo(AgentStatus.Completed));

        // 旧用户输入、回答和 opaque reasoning 载荷须原样进入后续请求。
        var messages = client2.RequestLog.Single().Messages;
        TestAssert.ContainsAll(m => m is { Role: ChatRole.System, Content: SystemPrompt }, messages);
        TestAssert.ContainsAll(m => m is { Role: ChatRole.User, Content  : "what is this repo?" }, messages);
        TestAssert.ContainsAll(m => m is { Role: ChatRole.User, Content  : "and why?" }, messages);
        var assistant = messages.Single(m => m.Role == ChatRole.Assistant);
        Assert.That(assistant.Content, Is.EqualTo("first answer"));
        var reasoning = TestAssert.Single(assistant.Reasoning!);
        Assert.That(reasoning.Text, Is.EqualTo("thinking it over"));
        Assert.That(reasoning.ProtectedData, Is.EqualTo("opaque-payload-1"));
        Assert.That(reasoning.ItemId, Is.EqualTo("item_1"));

        // 续聊提交递增序号并保留旧回合，不能覆盖既有历史。
        Assert.That(writer.CommitSequence, Is.GreaterThan(initialSequence));
        var read = await SessionStoreReader.ReadAsync(dir.Root, sessionId, CancellationToken.None);
        TestAssert.NotNull(read);
        Assert.That(read!.CommitSequence, Is.EqualTo(writer.CommitSequence));
        Assert.That(read.Turns.Count, Is.EqualTo(2));
        Assert.That(read.Turns[0].UserInput, Is.EqualTo("what is this repo?"));
        Assert.That(read.Turns[1].UserInput, Is.EqualTo("and why?"));
    }

    [Test]
    public async Task Resume_SecondExclusiveOpenFailsAndMissingSessionsAreRefused()
    {
        using var ws     = new TestTempDir();
        using var dir    = new TestTempDir();
        var       client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("answer"));

        string sessionId;
        using (var host = NewHost(client, ws.Root, dir.Root))
        {
            sessionId = host.Session.Id;
            await host.SubmitTurnAsync("one turn", CancellationToken.None);
        }

        long committedSequence;
        using (var writer =
               await SessionStoreWriter.OpenExistingAsync(dir.Root, sessionId, null, CancellationToken.None))
        {
            TestAssert.NotNull(writer.LoadedSnapshot);
            committedSequence = writer.CommitSequence;

            var second =
                await TestAssert.ThrowsAsync<SessionStoreException>(() =>
                    SessionStoreWriter.OpenExistingAsync(dir.Root, sessionId, null, CancellationToken.None));
            TestAssert.Contains("already open", second.Message, StringComparison.Ordinal);
        }

        // 释放排他句柄后允许再次打开会话。
        using (var reopened =
               await SessionStoreWriter.OpenExistingAsync(dir.Root, sessionId, null, CancellationToken.None))
        {
            Assert.That(reopened.CommitSequence, Is.EqualTo(committedSequence));
        }

        var missing =
            await TestAssert.ThrowsAsync<SessionStoreException>(() =>
                SessionStoreWriter.OpenExistingAsync(dir.Root, new string('a', 32), null, CancellationToken.None));
        TestAssert.Contains("No JSONL session log", missing.Message, StringComparison.Ordinal);
    }

    [Test]
    public async Task Resume_TamperedOpaquePayloadIsRejectedAfterJournalRead()
    {
        using var ws     = new TestTempDir();
        using var dir    = new TestTempDir();
        var       client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ReasonedText("hmm", "item_1", "opaque-payload-1", "answer"));

        string sessionId;
        using (var host = NewHost(client, ws.Root, dir.Root))
        {
            sessionId = host.Session.Id;
            await host.SubmitTurnAsync("turn", CancellationToken.None);
        }

        // 只篡改 canonical message 的 opaque 值而不重算哈希；恢复仍须拒绝。
        var journalPath = Path.Combine(dir.Root, sessionId + SessionStoreWriter.LogFileSuffix);
        var lines       = (await File.ReadAllLinesAsync(journalPath)).ToArray();
        var changed     = false;
        for (var i = 0; i < lines.Length && !changed; i++)
        {
            var batch = JsonNode.Parse(lines[i])!.AsObject();
            foreach (var record in batch["records"]!.AsArray())
            {
                if (record!["kind"]!.GetValue<string>() != "message") continue;
                var message = record["payload"]!["message"]!;
                if (message["role"]!.GetValue<string>() != "Assistant") continue;
                message["reasoning"]![0]!["protectedData"] = "tampered-payload";
                lines[i]                                   = batch.ToJsonString();
                changed                                    = true;
                break;
            }
        }

        Assert.That(changed, Is.True);
        await File.WriteAllTextAsync(journalPath, string.Join('\n', lines) + "\n");

        using var writer =
            await SessionStoreWriter.OpenExistingAsync(dir.Root, sessionId, null, CancellationToken.None);
        var error =
            TestAssert.Throws<SessionStoreException>(() =>
                SessionResume.Restore(writer.LoadedSnapshot!, ws.Root, Identity, SystemPrompt));
        TestAssert.Contains("integrity check", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Restore_RejectsSnapshotsWithoutResumableConversation()
    {
        var error = TestAssert.Throws<SessionStoreException>(() =>
            SessionResume.Restore(ValidSnapshot() with { Conversation = null },
                                  WorkspaceRoot, Identity, SystemPrompt));
        TestAssert.Contains("no resumable conversation", error.Message, StringComparison.Ordinal);
    }


    [Test]
    public void Restore_RejectsWorkspaceAndModelIdentityMismatches()
    {
        var workspaceError = TestAssert.Throws<SessionStoreException>(() =>
            SessionResume.Restore(ValidSnapshot(), Path.Combine(Path.GetTempPath(), "another-workspace"),
                                  Identity, SystemPrompt));
        TestAssert.Contains("fixed to one workspace", workspaceError.Message, StringComparison.Ordinal);

        var endpointError = TestAssert.Throws<SessionStoreException>(() =>
            SessionResume.Restore(ValidSnapshot(), WorkspaceRoot,
                                  Identity with { Endpoint = "https://other.invalid/v1" }, SystemPrompt));
        TestAssert.Contains("never silently replayed", endpointError.Message, StringComparison.Ordinal);

        var modelError = TestAssert.Throws<SessionStoreException>(() =>
            SessionResume.Restore(ValidSnapshot(), WorkspaceRoot,
                                  Identity with { ModelId = "other-model" }, SystemPrompt));
        TestAssert.Contains("never silently replayed", modelError.Message, StringComparison.Ordinal);

        var protocolError = TestAssert.Throws<SessionStoreException>(() =>
            SessionResume.Restore(ValidSnapshot(), WorkspaceRoot,
                                  Identity with { ChatApi = ChatApiKind.Responses }, SystemPrompt));
        TestAssert.Contains("never silently replayed", protocolError.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Restore_RejectsADifferentSystemPrompt()
    {
        var error = TestAssert.Throws<SessionStoreException>(() =>
            SessionResume.Restore(ValidSnapshot(), WorkspaceRoot, Identity, "a different instruction set"));
        TestAssert.Contains("same system prompt", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Restore_RewindsAnIncompleteTailAndBlocksPendingInspection()
    {
        // 旧快照尾部缺少工具结果且无执行记录，无法确认副作用是否发生。
        var conversation = ValidConversation(messages :
        [
            .. DefaultMessages(),
            new PersistedMessage { Role = nameof(ChatRole.User), Content = "run something" },
            new PersistedMessage
            {
                Role      = nameof(ChatRole.Assistant),
                ToolCalls = [new PersistedToolCall("call_9", "shell", "{}")]
            }
        ]);

        var restored = SessionResume.Restore(ValidSnapshot(conversation), WorkspaceRoot, Identity, SystemPrompt);

        // 整体移除不完整工具原子组，保留该回合用户输入并回到最近完整边界。
        Assert.That(restored.RewoundTail, Is.True);
        Assert.That(restored.Context.Messages.Count, Is.EqualTo(4));
        Assert.That(restored.Context.Messages[^1].Role, Is.EqualTo(ChatRole.User));
        Assert.That(restored.Context.Messages[^1].Content, Is.EqualTo("run something"));

        // 无执行记录时不能证明没有副作用，须保守触发检查阻断。
        TestAssert.NotNull(restored.NeedsInspection);
        TestAssert.Contains("cannot be proven absent", restored.NeedsInspection, StringComparison.Ordinal);
    }

    [Test]
    public void Restore_DerivesPendingInspectionFromExecutionRecordsAndHonoursAcknowledgement()
    {
        PersistedRun RunWithRecords(bool sideEffects) => new()
        {
            Id              = new string('3', 32),
            StartedUtc      = DateTimeOffset.UtcNow.AddMinutes(-4),
            FinishedUtc     = DateTimeOffset.UtcNow.AddMinutes(-3),
            Status          = nameof(AgentStatus.Cancelled),
            NeedsInspection = true,
            ToolCalls =
            [
                new PersistedToolCallRecord
                {
                    CallId      = "call_9",
                    ToolName    = "apply_patch",
                    Capability  = "filesystem.write",
                    SideEffects = sideEffects,
                    Summary     = "write src/a.cs",
                    Approval    = "allow once",
                    State       = nameof(ToolCallExecutionState.UnknownResult)
                }
            ]
        };

        PersistedSession Snapshot(PersistedRun run, DateTimeOffset? acknowledged) => ValidSnapshot() with
        {
            InspectionAcknowledgedUtc = acknowledged,
            Turns = [ValidSnapshot().Turns[0] with { Runs = [run] }]
        };

        var pending = SessionResume.Restore(Snapshot(RunWithRecords(true), null), WorkspaceRoot, Identity,
                                            SystemPrompt);
        Assert.That(pending.RewoundTail, Is.False);
        TestAssert.NotNull(pending.NeedsInspection);
        TestAssert.Contains("apply_patch", pending.NeedsInspection, StringComparison.Ordinal);
        TestAssert.Contains("unknown results", pending.NeedsInspection, StringComparison.Ordinal);
        var rebuilt = TestAssert.Single(pending.Session.Turns[0].Runs[0].Result!.ToolCalls!);
        Assert.That(rebuilt.State, Is.EqualTo(ToolCallExecutionState.UnknownResult));
        Assert.That(rebuilt.Capability, Is.EqualTo("filesystem.write"));
        Assert.That(rebuilt.Approval, Is.EqualTo("allow once"));

        // 明确确认覆盖已有中断，不重复触发阻断。
        var acknowledged = SessionResume.Restore(Snapshot(RunWithRecords(true), DateTimeOffset.UtcNow),
                                                 WorkspaceRoot, Identity, SystemPrompt);
        Assert.That(acknowledged.NeedsInspection, Is.Null);

        // 没有副作用的未知结果不触发阻断。
        var readOnly = SessionResume.Restore(Snapshot(RunWithRecords(false), null), WorkspaceRoot, Identity,
                                             SystemPrompt);
        Assert.That(readOnly.NeedsInspection, Is.Null);
    }

    [Test]
    public void Restore_RejectsAnIncompleteToolGroupThatIsNotTheTrailingRound()
    {
        var conversation = ValidConversation(messages :
        [
            .. DefaultMessages(),
            new PersistedMessage
            {
                Role      = nameof(ChatRole.Assistant),
                ToolCalls = [new PersistedToolCall("call_9", "shell", "{}")]
            },
            new PersistedMessage { Role = nameof(ChatRole.User), Content = "after the gap" }
        ]);

        var error = TestAssert.Throws<SessionStoreException>(() =>
            SessionResume.Restore(ValidSnapshot(conversation), WorkspaceRoot, Identity, SystemPrompt));
        TestAssert.Contains("not the trailing round", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Restore_RejectsAnUnrecognizableExecutionState()
    {
        var snapshot = ValidSnapshot() with
        {
            Turns =
            [
                ValidSnapshot().Turns[0] with
                {
                    Runs =
                    [
                        new PersistedRun
                        {
                            Id          = new string('3', 32),
                            StartedUtc  = DateTimeOffset.UtcNow.AddMinutes(-4),
                            FinishedUtc = DateTimeOffset.UtcNow.AddMinutes(-3),
                            Status      = nameof(AgentStatus.Completed),
                            ToolCalls =
                            [
                                new PersistedToolCallRecord
                                {
                                    CallId = "call_9", ToolName = "shell", State = "Exploded"
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var error =
            TestAssert.Throws<SessionStoreException>(() =>
                SessionResume.Restore(snapshot, WorkspaceRoot, Identity, SystemPrompt));
        TestAssert.Contains("unknown execution state", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Restore_RejectsDamagedConversationRecords()
    {
        var unknownRole =
            TestAssert.Throws<SessionStoreException>(() =>
                SessionResume.Restore(ValidSnapshot(ValidConversation(messages :
                [
                    new PersistedMessage
                    {
                        Role    = "Wizard",
                        Content = "hi"
                    }
                ])), WorkspaceRoot, Identity, SystemPrompt));
        TestAssert.Contains("not a known conversation role", unknownRole.Message, StringComparison.Ordinal);

        var emptyConversation =
            TestAssert.Throws<SessionStoreException>(() =>
                SessionResume.Restore(ValidSnapshot(ValidConversation(messages : [])),
                                      WorkspaceRoot, Identity, SystemPrompt));
        TestAssert.Contains("empty conversation", emptyConversation.Message, StringComparison.Ordinal);

        var badState =
            TestAssert.Throws<SessionStoreException>(() =>
                SessionResume.Restore(ValidSnapshot(ValidConversation(new CompactionState
                {
                    Schema  = "unknown",
                    Summary = "bad"
                }, true, 1, 2)), WorkspaceRoot, Identity, SystemPrompt));
        TestAssert.Contains("Invalid compaction checkpoint schema", badState.Message, StringComparison.Ordinal);

        var badFold =
            TestAssert.Throws<SessionStoreException>(() =>
                SessionResume.Restore(ValidSnapshot(ValidConversation(foldStart : 2, foldEnd : 5)),
                                      WorkspaceRoot, Identity, SystemPrompt));
        TestAssert.Contains("fold position", badFold.Message, StringComparison.Ordinal);

        var badStatus = TestAssert.Throws<SessionStoreException>(() =>
            SessionResume.Restore(ValidSnapshot(ValidConversation(), "Exploded"), WorkspaceRoot, Identity, SystemPrompt));
        TestAssert.Contains("unknown terminal status", badStatus.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Restore_RebuildsTurnsRunTerminalsAndTheCompactedView()
    {
        var state = new CompactionState { Summary = "inspect the fixture; use read_file" };
        var snapshot = ValidSnapshot(ValidConversation(state, true, 1,
                                                       2));

        var restored = SessionResume.Restore(snapshot, WorkspaceRoot, Identity, SystemPrompt,
                                             new ContextOptions
                                             {
                                                 ContextWindowTokens  = 100_000,
                                                 ReservedOutputTokens = 1_000
                                             });

        // 恢复回合和已记录的运行终态，不补造终态。
        var turn = TestAssert.Single(restored.Session.Turns);
        var run  = TestAssert.Single(turn.Runs);
        Assert.That(run.IsFinished, Is.True);
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(run.Result.FinalMessage, Is.EqualTo("it is a fixture"));
        Assert.That(run.Result.Steps, Is.EqualTo(2));

        // 恢复折叠位置；模型视图只含头部、状态和未折叠尾部，中间历史仅本地保留。
        Assert.That(restored.Context.Position, Is.EqualTo(new ConversationContext.CompactionPosition(true, 1, 2)));
        var view = restored.Context.BuildModelView();
        Assert.That(view[0].Role, Is.EqualTo(ChatRole.System));
        Assert.That(view[1].Role, Is.EqualTo(ChatRole.User));
        TestAssert.Contains("inspect the fixture", view[1].Content, StringComparison.Ordinal);
        TestAssert.DoesNotContainAny(m => m.Content == "what is here?", view);
    }

    [Test]
    public async Task Commit_PreservesKnownSecretTextForLocalHistoryAndRedactsAuditOnly()
    {
        using var ws     = new TestTempDir();
        using var dir    = new TestTempDir();
        var       client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ReasonedText("hmm", "item_1", "opaque-payload-1",
                                                   "done with sk-test-secret-value"));

        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
            { ["MY_API_KEY"] = "sk-test-secret-value" };
        string sessionId;
        using (var host = new ChatSessionHost(ws.Root, Identity, Options(), client, new ToolRegistry([]), SystemPrompt,
                                              sessionWriterFactory : id => new SessionStoreWriter(dir.Root, id, secrets)))
        {
            sessionId = host.Session.Id;
            await host.SubmitTurnAsync("use key sk-test-secret-value", CancellationToken.None);
        }

        var json = await File.ReadAllTextAsync(Path.Combine(dir.Root, sessionId + SessionStoreWriter.LogFileSuffix));
        TestAssert.Contains("sk-test-secret-value", json, StringComparison.Ordinal);
        TestAssert.Contains("opaque-payload-1", json, StringComparison.Ordinal);

        using var writer =
            await SessionStoreWriter.OpenExistingAsync(dir.Root, sessionId, secrets, CancellationToken.None);
        var snapshot = writer.LoadedSnapshot!;
        Assert.That(snapshot.Turns.Single().UserInput, Is.EqualTo("use key sk-test-secret-value"));
        Assert.That(snapshot.Turns.Single().Runs.Single().FinalMessage, Is.EqualTo("done with sk-test-secret-value"));
        var restored = SessionResume.Restore(snapshot, ws.Root, Identity, SystemPrompt);
        Assert.That(restored.Session.Id, Is.EqualTo(sessionId));

        var nextClient = new FakeChatClient();
        nextClient.Enqueue(FakeChatClient.Text("continued"));
        using var nextHost = new ChatSessionHost(restored.Session, restored.Context, Options(), nextClient,
                                                 new ToolRegistry([]), SystemPrompt, sessionWriter : writer);
        await nextHost.SubmitTurnAsync("continue", CancellationToken.None);
        var sent = nextClient.RequestLog.Single().Messages;
        TestAssert.ContainsAll(message => message is { Role: ChatRole.User, Content: "use key sk-test-secret-value" },
                               sent);
        TestAssert.ContainsAll(message =>
            message is { Role: ChatRole.Assistant, Content: "done with sk-test-secret-value" }, sent);
        var retainedAudit =
            string.Join('\n', (await ReadLinesSharedAsync(
                Path.Combine(dir.Root, sessionId + SessionStoreWriter.LogFileSuffix)))
                .SelectMany(line => JsonNode.Parse(line)!["records"]!.AsArray()
                                           .Where(record => record!["kind"]!.GetValue<string>() == "audit")
                                           .Select(record => record!["payload"]!.ToJsonString())));
        TestAssert.DoesNotContain("sk-test-secret-value", retainedAudit, StringComparison.Ordinal);
        TestAssert.DoesNotContain("opaque-payload-1", retainedAudit, StringComparison.Ordinal);
    }


    [TestCase("sk-test-secret-value")]
    [TestCase(" \n{ \"encrypted_content\" : \"sk-test-secret-value\", \"number\" : 424242 }\n ")]
    [TestCase(" \n{ \"encrypted_content\" : \"opaque-payload-1\", \"number\" : 7 }\n ")]
    public async Task Commit_PreservesOpaquePayloadFormattingWithoutMarkingItRedacted(string protectedData)
    {
        using var    ws     = new TestTempDir();
        using var    dir    = new TestTempDir();
        var          client = new FakeChatClient();
        const string itemId = "item_sk-test-secret-value";
        client.Enqueue(FakeChatClient.ReasonedText("hmm sk-test-secret-value", itemId, protectedData, "answer"));

        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
            { ["MY_API_KEY"] = "sk-test-secret-value" };
        string sessionId;
        using (var host = new ChatSessionHost(ws.Root, Identity, Options(), client, new ToolRegistry([]), SystemPrompt,
                                              sessionWriterFactory : id => new SessionStoreWriter(dir.Root, id, secrets)))
        {
            sessionId = host.Session.Id;
            await host.SubmitTurnAsync("turn", CancellationToken.None);
        }

        using var writer =
            await SessionStoreWriter.OpenExistingAsync(dir.Root, sessionId, secrets, CancellationToken.None);
        var conversation = writer.LoadedSnapshot!.Conversation!;
        var stored       = TestAssert.Single(conversation.Messages.Single(m => m.Role == "Assistant").Reasoning!);
        Assert.That(stored.ProtectedData, Is.EqualTo(protectedData));
        Assert.That(stored.ItemId, Is.EqualTo(itemId));
        Assert.That(stored.Text, Is.EqualTo("hmm sk-test-secret-value"));
        Assert.That(stored.OpaqueHash, Is.EqualTo(PersistedReasoning.ComputeOpaqueHash(protectedData, itemId)));

        var restored   = SessionResume.Restore(writer.LoadedSnapshot!, ws.Root, Identity, SystemPrompt);
        var nextClient = new FakeChatClient();
        nextClient.Enqueue(FakeChatClient.Text("continued"));
        using var nextHost = new ChatSessionHost(restored.Session, restored.Context, Options(), nextClient,
                                                 new ToolRegistry([]), SystemPrompt, sessionWriter : writer);
        await nextHost.SubmitTurnAsync("continue", CancellationToken.None);
        var sent = TestAssert.Single(nextClient.RequestLog.Single().Messages
                                               .Single(m => m.Role == ChatRole.Assistant).Reasoning!);
        Assert.That(sent.ProtectedData, Is.EqualTo(protectedData));
        Assert.That(sent.ItemId, Is.EqualTo(itemId));
        Assert.That(sent.Text, Is.EqualTo("hmm sk-test-secret-value"));
    }


    [TestCase(false)]
    [TestCase(true)]
    public async Task CommitAndResume_PreserveProtocolFieldsAndCompactionStateForTheNextRequest(bool compacted)
    {
        using var    ws = new TestTempDir();
        using var    dir = new TestTempDir();
        const string secret = "sk-test-secret-value";
        const string numericSecret = "424242";
        const string callId = "call_sk-test-secret-value_424242";
        const string toolName = "tool_sk-test-secret-value_424242";
        const string arguments = " \n{ \"sk-test-secret-value\" : \"sk-test-secret-value\", \"number\" : 424242 }\n ";
        const string opaque = " \n{ \"encrypted_content\" : \"sk-test-secret-value\", \"number\" : 424242 }\n ";
        const string itemId = "item_sk-test-secret-value_424242";
        var          systemPrompt = SystemPrompt + " " + secret + " " + numericSecret;
        var          state = new CompactionState { Summary = "goal " + secret + " " + numericSecret };
        ChatMessage[] messages =
        [
            ChatMessage.System(systemPrompt),
            ChatMessage.User("old "             + secret + " " + numericSecret),
            ChatMessage.Assistant("old answer " + secret),
            ChatMessage.User("current "         + secret + " " + numericSecret),
            ChatMessage.Assistant("calling " + secret,
                                  [new ChatToolCall(callId, toolName, arguments)],
                                  [
                                      new ReasoningContent("reason " + secret + " " + numericSecret, opaque, itemId),
                                      new ReasoningContent(null, null, "item-only_" + secret)
                                  ]),
            ChatMessage.Tool(toolName, callId, "result " + secret + " " + numericSecret),
            ChatMessage.Assistant("done "                + secret, [], [])
        ];
        var contextOptions = new ContextOptions { ContextWindowTokens = 100_000, ReservedOutputTokens = 1_000 };
        var context        = new ConversationContext(contextOptions);
        context.Restore(messages, compacted ? state : CompactionState.Empty, compacted,
                        compacted ? 1 : -1, compacted ? 3 : 0);
        var session = new ChatSession(Guid.NewGuid().ToString("N"), ws.Root, Identity);
        var turn    = new SessionTurn(Guid.NewGuid().ToString("N"), messages[3].Content);
        var run     = new SessionRun(Guid.NewGuid().ToString("N"), session.Id, turn.Id);
        session.AddTurn(turn);
        turn.AddRun(run);
        run.Finish(new AgentResult
        {
            Status       = AgentStatus.Completed,
            FinalMessage = messages[^1].Content,
            Error        = "error " + secret
        });
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MY_API_KEY"]     = secret,
            ["NUMERIC_SECRET"] = numericSecret
        };
        using (var writer = new SessionStoreWriter(dir.Root, session.Id, secrets))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        using var reopened =
            await SessionStoreWriter.OpenExistingAsync(dir.Root, session.Id, secrets, CancellationToken.None);
        var snapshot     = reopened.LoadedSnapshot!;
        var conversation = snapshot.Conversation!;
        Assert.That(conversation.State?.ToJson(), Is.EqualTo(compacted ? state.ToJson() : null));
        Assert.That(conversation.HasState, Is.EqualTo(compacted));
        Assert.That(conversation.FoldStartMessageIndex, Is.EqualTo(context.Position.FoldStartMessageIndex));
        Assert.That(conversation.FoldEndMessageIndex, Is.EqualTo(context.Position.FoldEndMessageIndex));
        Assert.That(conversation.Messages.Count, Is.EqualTo(messages.Length));
        for (var i = 0; i < messages.Length; i++)
        {
            var original = messages[i];
            var stored   = conversation.Messages[i];
            Assert.That(stored.Role, Is.EqualTo(original.Role.ToString()));
            Assert.That(stored.Content, Is.EqualTo(original.Content));
            Assert.That(stored.Name, Is.EqualTo(original.Name));
            Assert.That(stored.ToolCallId, Is.EqualTo(original.ToolCallId));
            Assert.That(stored.ToolCalls?.Select(c => (c.Id, c.Name, c.Arguments)),
                        Is.EqualTo(original.ToolCalls?.Select(c => (c.Id, c.FunctionName, c.ArgumentsJson))));
            Assert.That(stored.Reasoning?.Select(r => (r.Text, r.ProtectedData, r.ItemId)),
                        Is.EqualTo(original.Reasoning?.Select(r => (r.Text, r.ProtectedData, r.ItemId))));
            foreach (var entry in stored.Reasoning ?? [])
                Assert.That(entry.OpaqueHash,
                            Is.EqualTo(PersistedReasoning.ComputeOpaqueHash(entry.ProtectedData, entry.ItemId)));
        }

        Assert.That(snapshot.Turns.Single().UserInput, Is.EqualTo("current " + secret + " " + numericSecret));
        var storedRun = snapshot.Turns.Single().Runs.Single();
        Assert.That(storedRun.FinalMessage, Is.EqualTo("done " + secret));
        Assert.That(storedRun.Error, Is.EqualTo("error [REDACTED]"));
        var restored = SessionResume.Restore(snapshot, ws.Root, Identity, systemPrompt, contextOptions);
        Assert.That(restored.Context.Position, Is.EqualTo(context.Position));
        Assert.That(restored.Context.State.ToJson(), Is.EqualTo(context.State.ToJson()));
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("continued"));
        using var host = new ChatSessionHost(restored.Session, restored.Context, Options(), client,
                                             new ToolRegistry([]), systemPrompt, sessionWriter : reopened);
        var continued = await host.SubmitTurnAsync("continue", CancellationToken.None);
        Assert.That(continued.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(host.PersistenceWarning, Is.Null);
        var sent = client.RequestLog.Single().Messages;
        Assert.That(sent[0].Content, Is.EqualTo(systemPrompt));
        TestAssert.ContainsAll(m => m.Role == ChatRole.User && m.Content == messages[3].Content, sent);
        var sentCallMessage = sent.Single(m => m.ToolCalls is { Count: > 0 });
        var sentCall        = TestAssert.Single(sentCallMessage.ToolCalls!);
        Assert.That(sentCall.Id, Is.EqualTo(callId));
        Assert.That(sentCall.FunctionName, Is.EqualTo(toolName));
        Assert.That(sentCall.ArgumentsJson, Is.EqualTo(arguments));
        Assert.That(sentCallMessage.Content, Is.EqualTo(messages[4].Content));
        Assert.That(sentCallMessage.Reasoning, Is.EqualTo(messages[4].Reasoning));
        var sentTool = sent.Single(m => m.Role == ChatRole.Tool);
        Assert.That(sentTool.Name, Is.EqualTo(toolName));
        Assert.That(sentTool.ToolCallId, Is.EqualTo(callId));
        Assert.That(sentTool.Content, Is.EqualTo(messages[5].Content));
        if (compacted)
        {
            TestAssert.ContainsAll(m =>
                m.Role == ChatRole.User && m.Content.EndsWith(state.Summary, StringComparison.Ordinal), sent);
            TestAssert.DoesNotContainAny(m => m.Content == messages[1].Content, sent);
        }
        else
        {
            TestAssert.ContainsAll(m => m.Role == ChatRole.User && m.Content == messages[1].Content, sent);
        }
    }

    private static async Task<string[]> ReadLinesSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var       reader = new StreamReader(stream);
        var             lines  = new List<string>();
        while (await reader.ReadLineAsync() is { } line) lines.Add(line);
        return lines.ToArray();
    }

    [Test]
    public void Commit_OpaqueHashCoversBothProtectedDataAndItemId()
    {
        Assert.That(PersistedReasoning.ComputeOpaqueHash("same", "one"),
                    Is.EqualTo(PersistedReasoning.ComputeOpaqueHash("same", "one")));
        Assert.That(PersistedReasoning.ComputeOpaqueHash("same", "two"),
                    Is.Not.EqualTo(PersistedReasoning.ComputeOpaqueHash("same", "one")));
        Assert.That(PersistedReasoning.ComputeOpaqueHash("ones", "ame"),
                    Is.Not.EqualTo(PersistedReasoning.ComputeOpaqueHash("one", "same")));
        Assert.That(PersistedReasoning.ComputeOpaqueHash(null, null).Length, Is.EqualTo(64));
    }

    /// <summary>
    ///     <para>构造最小完整的可续聊快照；可替换会话内容或运行状态以验证指定边界。</para>
    ///     <para>
    ///         Builds a minimal complete resumable snapshot; conversation content or run status
    ///         can be replaced for targeted cases.
    ///     </para>
    /// </summary>
    private static PersistedSession ValidSnapshot(
        PersistedConversation? conversation = null, string? runStatus = null) => new()
    {
        Id             = new string('1', 32),
        WorkspaceRoot  = WorkspaceRoot,
        Model          = Identity,
        CreatedUtc     = DateTimeOffset.UtcNow.AddMinutes(-5),
        UpdatedUtc     = DateTimeOffset.UtcNow,
        CommitSequence = 3,
        Turns =
        [
            new PersistedTurn
            {
                Id         = new string('2', 32),
                UserInput  = "what is here?",
                CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-4),
                Runs =
                [
                    new PersistedRun
                    {
                        Id             = new string('3', 32),
                        StartedUtc     = DateTimeOffset.UtcNow.AddMinutes(-4),
                        FinishedUtc    = DateTimeOffset.UtcNow.AddMinutes(-3),
                        Status         = runStatus ?? nameof(AgentStatus.Completed),
                        FinalMessage   = "it is a fixture",
                        Steps          = 2,
                        ToolExecutions = 1
                    }
                ]
            }
        ],
        Conversation = conversation ?? ValidConversation()
    };

    private static PersistedConversation ValidConversation(CompactionState?                 state     = null,
                                                           bool                             hasState  = false,
                                                           int                              foldStart = -1,
                                                           int                              foldEnd   = 0,
                                                           IReadOnlyList<PersistedMessage>? messages  = null) => new()
    {
        Messages              = messages ?? DefaultMessages(),
        State                 = state,
        HasState              = hasState,
        FoldStartMessageIndex = foldStart,
        FoldEndMessageIndex   = foldEnd
    };

    private static List<PersistedMessage> DefaultMessages() =>
    [
        new() { Role = nameof(ChatRole.System), Content = SystemPrompt },
        new() { Role = nameof(ChatRole.User), Content   = "what is here?" },
        new()
        {
            Role    = nameof(ChatRole.Assistant),
            Content = "it is a fixture",
            Reasoning =
            [
                new PersistedReasoning
                {
                    Text          = "looked around",
                    ProtectedData = "opaque-payload-1",
                    ItemId        = "item_1",
                    OpaqueHash    = PersistedReasoning.ComputeOpaqueHash("opaque-payload-1", "item_1")
                }
            ]
        }
    ];
}

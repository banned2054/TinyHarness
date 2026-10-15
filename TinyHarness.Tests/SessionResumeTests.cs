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

    [Fact]
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
            Assert.Equal(AgentStatus.Completed, run.Result!.Status);
        }

        // 用新宿主模拟重启：排他打开存储、续种提交序号、恢复上下文后继续。
        using var writer =
            await SessionStoreWriter.OpenExistingAsync(dir.Root, sessionId, null, CancellationToken.None);
        var restored = SessionResume.Restore(writer.LoadedSnapshot!, ws.Root, Identity, SystemPrompt);
        Assert.Equal(1, writer.CommitSequence);
        Assert.Equal(sessionId, restored.Session.Id);
        Assert.Single(restored.Session.Turns);

        var client2 = new FakeChatClient();
        client2.Enqueue(FakeChatClient.Text("second answer"));
        using var host2 = new ChatSessionHost(restored.Session, restored.Context, Options(), client2,
                                              new ToolRegistry([]), SystemPrompt, sessionWriter : writer);
        var run2 = await host2.SubmitTurnAsync("and why?", CancellationToken.None);
        Assert.Equal(AgentStatus.Completed, run2.Result!.Status);

        // 旧用户输入、回答和 opaque reasoning 载荷须原样进入后续请求。
        var messages = client2.RequestLog.Single().Messages;
        Assert.Contains(messages, m => m.Role == ChatRole.System && m.Content == SystemPrompt);
        Assert.Contains(messages, m => m.Role == ChatRole.User   && m.Content == "what is this repo?");
        Assert.Contains(messages, m => m.Role == ChatRole.User   && m.Content == "and why?");
        var assistant = messages.Single(m => m.Role == ChatRole.Assistant);
        Assert.Equal("first answer", assistant.Content);
        var reasoning = Assert.Single(assistant.Reasoning!);
        Assert.Equal("thinking it over", reasoning.Text);
        Assert.Equal("opaque-payload-1", reasoning.ProtectedData);
        Assert.Equal("item_1", reasoning.ItemId);

        // 续聊提交递增序号并保留旧回合，不能覆盖既有历史。
        Assert.Equal(2, writer.CommitSequence);
        var read = await SessionStoreReader.ReadAsync(dir.Root, sessionId, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(2, read!.CommitSequence);
        Assert.Equal(2, read.Turns.Count);
        Assert.Equal("what is this repo?", read.Turns[0].UserInput);
        Assert.Equal("and why?", read.Turns[1].UserInput);
    }

    [Fact]
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

        using (var writer =
               await SessionStoreWriter.OpenExistingAsync(dir.Root, sessionId, null, CancellationToken.None))
        {
            Assert.NotNull(writer.LoadedSnapshot);

            var second =
                await Assert.ThrowsAsync<SessionStoreException>(() =>
                                                                    SessionStoreWriter.OpenExistingAsync(dir.Root,
                                                                             sessionId, null, CancellationToken.None));
            Assert.Contains("already open", second.Message, StringComparison.Ordinal);
        }

        // 释放排他句柄后允许再次打开会话。
        using (var reopened =
               await SessionStoreWriter.OpenExistingAsync(dir.Root, sessionId, null, CancellationToken.None))
        {
            Assert.Equal(1, reopened.CommitSequence);
        }

        var missing =
            await Assert.ThrowsAsync<SessionStoreException>(() =>
                                                                SessionStoreWriter.OpenExistingAsync(dir.Root,
                                                                         new string('a', 32), null,
                                                                         CancellationToken.None));
        Assert.Contains("nothing to resume", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resume_TamperedOpaquePayloadIsRejectedAfterAuditReconciliation()
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

        // 只篡改 opaque 载荷而不改提交序号；审计对账通过后仍须由恢复哈希校验拒绝。
        var snapshotPath = Path.Combine(dir.Root, $"{sessionId}.session.json");
        var root         = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath))!.AsObject();
        root["conversation"]!["messages"]!.AsArray()
                                          .Single(m => m!["role"]!.GetValue<string>() == "Assistant")!["reasoning"]![0]!
                                           ["protectedData"] =
            "tampered-payload";
        await File.WriteAllTextAsync(snapshotPath, root.ToJsonString());

        using var writer =
            await SessionStoreWriter.OpenExistingAsync(dir.Root, sessionId, null, CancellationToken.None);
        var error =
            Assert.Throws<SessionStoreException>(() => SessionResume.Restore(writer.LoadedSnapshot!, ws.Root, Identity,
                                                                             SystemPrompt));
        Assert.Contains("integrity check", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_RejectsSnapshotsWithoutResumableConversation()
    {
        var error = Assert.Throws<SessionStoreException>(() => SessionResume.Restore(ValidSnapshot() with
                                                                  {
                                                                      FormatVersion = 1, Conversation = null
                                                                  }, WorkspaceRoot,
                                                                  Identity, SystemPrompt));
        Assert.Contains("no resumable conversation", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Restore_RejectsConversationsWhoseOpaqueContentWasRedacted(int formatVersion)
    {
        var snapshot = ValidSnapshot(ValidConversation() with { OpaqueRedacted = true }) with
        {
            FormatVersion = formatVersion
        };
        var error =
            Assert.Throws<SessionStoreException>(() => SessionResume.Restore(snapshot, WorkspaceRoot, Identity,
                                                                             SystemPrompt));
        Assert.Contains("cannot be resumed losslessly", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Restore_RejectsOlderFormatsEvenWithConversationContent(int formatVersion)
    {
        var snapshot = ValidSnapshot() with { FormatVersion = formatVersion };
        Assert.NotNull(snapshot.Conversation);
        var error =
            Assert.Throws<SessionStoreException>(() => SessionResume.Restore(snapshot, WorkspaceRoot, Identity,
                                                                             SystemPrompt));
        Assert.Contains("inspection-only", error.Message, StringComparison.Ordinal);
        Assert.Contains($"format version {formatVersion}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_RejectsWorkspaceAndModelIdentityMismatches()
    {
        var workspaceError = Assert.Throws<SessionStoreException>(() => SessionResume.Restore(ValidSnapshot(),
                                                                           Path.Combine(Path.GetTempPath(),
                                                                                    "another-workspace"), Identity,
                                                                           SystemPrompt));
        Assert.Contains("fixed to one workspace", workspaceError.Message, StringComparison.Ordinal);

        var endpointError = Assert.Throws<SessionStoreException>(() => SessionResume.Restore(ValidSnapshot(),
                                                                          WorkspaceRoot,
                                                                          Identity with
                                                                          {
                                                                              Endpoint = "https://other.invalid/v1"
                                                                          }, SystemPrompt));
        Assert.Contains("never silently replayed", endpointError.Message, StringComparison.Ordinal);

        var modelError = Assert.Throws<SessionStoreException>(() => SessionResume.Restore(ValidSnapshot(),
                                                                       WorkspaceRoot,
                                                                       Identity with { ModelId = "other-model" },
                                                                       SystemPrompt));
        Assert.Contains("never silently replayed", modelError.Message, StringComparison.Ordinal);

        var protocolError = Assert.Throws<SessionStoreException>(() => SessionResume.Restore(ValidSnapshot(),
                                                                          WorkspaceRoot,
                                                                          Identity with
                                                                          {
                                                                              ChatApi = ChatApiKind.Responses
                                                                          },
                                                                          SystemPrompt));
        Assert.Contains("never silently replayed", protocolError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_RejectsADifferentSystemPrompt()
    {
        var error = Assert.Throws<SessionStoreException>(() => SessionResume.Restore(ValidSnapshot(), WorkspaceRoot,
                                                                  Identity, "a different instruction set"));
        Assert.Contains("same system prompt", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_RewindsAnIncompleteLegacyTailAndBlocksPendingInspection()
    {
        // 旧快照尾部缺少工具结果且无执行记录，无法确认副作用是否发生。
        var conversation = ValidConversation(messages :
        [
            .. DefaultMessages(),
            new PersistedMessage { Role = ChatRole.User.ToString(), Content = "run something" },
            new PersistedMessage
            {
                Role      = ChatRole.Assistant.ToString(),
                ToolCalls = [new PersistedToolCall("call_9", "shell", "{}")]
            }
        ]);

        var restored = SessionResume.Restore(ValidSnapshot(conversation), WorkspaceRoot, Identity, SystemPrompt);

        // 整体移除不完整工具原子组，保留该回合用户输入并回到最近完整边界。
        Assert.True(restored.RewoundTail);
        Assert.Equal(4, restored.Context.Messages.Count);
        Assert.Equal(ChatRole.User, restored.Context.Messages[^1].Role);
        Assert.Equal("run something", restored.Context.Messages[^1].Content);

        // 无执行记录时不能证明没有副作用，须保守触发检查阻断。
        Assert.NotNull(restored.NeedsInspection);
        Assert.Contains("cannot be proven absent", restored.NeedsInspection, StringComparison.Ordinal);
    }

    [Fact]
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
        Assert.False(pending.RewoundTail);
        Assert.NotNull(pending.NeedsInspection);
        Assert.Contains("apply_patch", pending.NeedsInspection, StringComparison.Ordinal);
        Assert.Contains("unknown results", pending.NeedsInspection, StringComparison.Ordinal);
        var rebuilt = Assert.Single(pending.Session.Turns[0].Runs[0].Result!.ToolCalls!);
        Assert.Equal(ToolCallExecutionState.UnknownResult, rebuilt.State);
        Assert.Equal("filesystem.write", rebuilt.Capability);
        Assert.Equal("allow once", rebuilt.Approval);

        // 明确确认覆盖已有中断，不重复触发阻断。
        var acknowledged = SessionResume.Restore(Snapshot(RunWithRecords(true), DateTimeOffset.UtcNow),
                                                 WorkspaceRoot, Identity, SystemPrompt);
        Assert.Null(acknowledged.NeedsInspection);

        // 没有副作用的未知结果不触发阻断。
        var readOnly = SessionResume.Restore(Snapshot(RunWithRecords(false), null), WorkspaceRoot, Identity,
                                             SystemPrompt);
        Assert.Null(readOnly.NeedsInspection);
    }

    [Fact]
    public void Restore_RejectsAnIncompleteToolGroupThatIsNotTheTrailingRound()
    {
        var conversation = ValidConversation(messages :
        [
            .. DefaultMessages(),
            new PersistedMessage
            {
                Role      = ChatRole.Assistant.ToString(),
                ToolCalls = [new PersistedToolCall("call_9", "shell", "{}")]
            },
            new PersistedMessage { Role = ChatRole.User.ToString(), Content = "after the gap" }
        ]);

        var error = Assert.Throws<SessionStoreException>(() => SessionResume.Restore(ValidSnapshot(conversation),
                                                                  WorkspaceRoot, Identity, SystemPrompt));
        Assert.Contains("not the trailing round", error.Message, StringComparison.Ordinal);
    }

    [Fact]
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

        var error = Assert.Throws<SessionStoreException>(() =>
                                                             SessionResume.Restore(snapshot, WorkspaceRoot,
                                                                      Identity, SystemPrompt));
        Assert.Contains("unknown execution state", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_RejectsDamagedConversationRecords()
    {
        var unknownRole =
            Assert.Throws<SessionStoreException>(() => SessionResume.Restore(ValidSnapshot(ValidConversation(
                                                                                           messages :
                                                                                           [
                                                                                               new PersistedMessage
                                                                                               {
                                                                                                   Role    = "Wizard",
                                                                                                   Content = "hi"
                                                                                               }
                                                                                           ])),
                                                                             WorkspaceRoot, Identity, SystemPrompt));
        Assert.Contains("not a known conversation role", unknownRole.Message, StringComparison.Ordinal);

        var emptyConversation =
            Assert.Throws<SessionStoreException>(() =>
                                                     SessionResume.Restore(ValidSnapshot(ValidConversation(messages :
                                                                                             [])), WorkspaceRoot,
                                                                           Identity,
                                                                           SystemPrompt));
        Assert.Contains("empty conversation", emptyConversation.Message, StringComparison.Ordinal);

        var badState =
            Assert.Throws<SessionStoreException>(() =>
                                                     SessionResume
                                                        .Restore(ValidSnapshot(ValidConversation("{ not state", true)),
                                                                 WorkspaceRoot, Identity, SystemPrompt));
        Assert.Contains("compacted state cannot be parsed", badState.Message, StringComparison.Ordinal);

        var badFold =
            Assert.Throws<SessionStoreException>(() =>
                                                     SessionResume
                                                        .Restore(ValidSnapshot(ValidConversation(foldStart : 2,
                                                                                        foldEnd : 5)), WorkspaceRoot,
                                                                 Identity, SystemPrompt));
        Assert.Contains("fold position", badFold.Message, StringComparison.Ordinal);

        var badStatus = Assert.Throws<SessionStoreException>(() => SessionResume.Restore(ValidSnapshot(
                                                                           ValidConversation(),
                                                                           "Exploded"),
                                                                      WorkspaceRoot, Identity, SystemPrompt));
        Assert.Contains("unknown terminal status", badStatus.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_RebuildsTurnsRunTerminalsAndTheCompactedView()
    {
        var state = new StructuredState { Goal = "inspect the fixture", Decisions = ["use read_file"] };
        var snapshot = ValidSnapshot(ValidConversation(state.ToJson(), true, 1,
                                                       2));

        var restored = SessionResume.Restore(snapshot, WorkspaceRoot, Identity, SystemPrompt,
                                             new ContextOptions
                                             {
                                                 ContextWindowTokens = 100_000, ReservedOutputTokens = 1_000
                                             });

        // 恢复回合和已记录的运行终态，不补造终态。
        var turn = Assert.Single(restored.Session.Turns);
        var run  = Assert.Single(turn.Runs);
        Assert.True(run.IsFinished);
        Assert.Equal(AgentStatus.Completed, run.Result!.Status);
        Assert.Equal("it is a fixture", run.Result.FinalMessage);
        Assert.Equal(2, run.Result.Steps);

        // 恢复折叠位置；模型视图只含头部、状态和未折叠尾部，中间历史仅本地保留。
        Assert.Equal(new ConversationContext.CompactionPosition(true, 1, 2), restored.Context.Position);
        var view = restored.Context.BuildModelView();
        Assert.Equal(ChatRole.System, view[0].Role);
        Assert.Equal(ChatRole.User, view[1].Role);
        Assert.Contains("inspect the fixture", view[1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain(view, m => m.Content == "what is here?");
    }

    [Fact]
    public async Task Commit_PreservesKnownSecretTextForResumeAndRedactsInspectionOnly()
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
                                              sessionWriterFactory : id =>
                                                  new SessionStoreWriter(dir.Root, id, secrets)))
        {
            sessionId = host.Session.Id;
            await host.SubmitTurnAsync("use key sk-test-secret-value", CancellationToken.None);
        }

        var json = await File.ReadAllTextAsync(Path.Combine(dir.Root, $"{sessionId}.session.json"));
        Assert.Contains("sk-test-secret-value", json, StringComparison.Ordinal);
        Assert.Contains("opaque-payload-1", json, StringComparison.Ordinal);

        using var writer =
            await SessionStoreWriter.OpenExistingAsync(dir.Root, sessionId, secrets, CancellationToken.None);
        var snapshot = writer.LoadedSnapshot!;
        Assert.False(snapshot.Conversation!.OpaqueRedacted);
        Assert.Equal("use key [REDACTED]", snapshot.Turns.Single().UserInput);
        Assert.Equal("done with [REDACTED]", snapshot.Turns.Single().Runs.Single().FinalMessage);
        var restored = SessionResume.Restore(snapshot, ws.Root, Identity, SystemPrompt);
        Assert.Equal(sessionId, restored.Session.Id);

        var nextClient = new FakeChatClient();
        nextClient.Enqueue(FakeChatClient.Text("continued"));
        using var nextHost = new ChatSessionHost(restored.Session, restored.Context, Options(), nextClient,
                                                 new ToolRegistry([]), SystemPrompt, sessionWriter : writer);
        await nextHost.SubmitTurnAsync("continue", CancellationToken.None);
        var sent = nextClient.RequestLog.Single().Messages;
        Assert.Contains(sent, message => message.Role    == ChatRole.User &&
                                         message.Content == "use key sk-test-secret-value");
        Assert.Contains(sent, message => message.Role    == ChatRole.Assistant &&
                                         message.Content == "done with sk-test-secret-value");
        var audit = await File.ReadAllTextAsync(Path.Combine(dir.Root, $"{sessionId}.audit.jsonl"));
        Assert.DoesNotContain("sk-test-secret-value", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-payload-1", audit, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sk-test-secret-value")]
    [InlineData(" \n{ \"encrypted_content\" : \"sk-test-secret-value\", \"number\" : 424242 }\n ")]
    [InlineData(" \n{ \"encrypted_content\" : \"opaque-payload-1\", \"number\" : 7 }\n ")]
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
                                              sessionWriterFactory : id =>
                                                  new SessionStoreWriter(dir.Root, id, secrets)))
        {
            sessionId = host.Session.Id;
            await host.SubmitTurnAsync("turn", CancellationToken.None);
        }

        using var writer =
            await SessionStoreWriter.OpenExistingAsync(dir.Root, sessionId, secrets, CancellationToken.None);
        var conversation = writer.LoadedSnapshot!.Conversation!;
        Assert.False(conversation.OpaqueRedacted);
        var stored = Assert.Single(conversation.Messages.Single(m => m.Role == "Assistant").Reasoning!);
        Assert.Equal(protectedData, stored.ProtectedData);
        Assert.Equal(itemId, stored.ItemId);
        Assert.Equal("hmm sk-test-secret-value", stored.Text);
        Assert.Equal(PersistedReasoning.ComputeOpaqueHash(protectedData, itemId), stored.OpaqueHash);

        var restored   = SessionResume.Restore(writer.LoadedSnapshot!, ws.Root, Identity, SystemPrompt);
        var nextClient = new FakeChatClient();
        nextClient.Enqueue(FakeChatClient.Text("continued"));
        using var nextHost = new ChatSessionHost(restored.Session, restored.Context, Options(), nextClient,
                                                 new ToolRegistry([]), SystemPrompt, sessionWriter : writer);
        await nextHost.SubmitTurnAsync("continue", CancellationToken.None);
        var sent = Assert.Single(nextClient.RequestLog.Single().Messages
                                           .Single(m => m.Role == ChatRole.Assistant).Reasoning!);
        Assert.Equal(protectedData, sent.ProtectedData);
        Assert.Equal(itemId, sent.ItemId);
        Assert.Equal("hmm sk-test-secret-value", sent.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
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
        var state = new StructuredState
        {
            Goal               = "goal " + secret + " " + numericSecret,
            Constraints        = ["constraint " + secret],
            Decisions          = ["decision "   + numericSecret],
            FilesInspected     = ["read/"       + secret],
            FilesModified      = ["write/"      + secret],
            CommandsAndResults = ["command "    + secret + " " + numericSecret],
            PendingWork        = ["pending "    + secret]
        };
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
        context.Restore(messages, compacted ? state : StructuredState.Empty, compacted,
                        compacted ? 1 : -1, compacted ? 3 : 0);
        var session = new ChatSession(Guid.NewGuid().ToString("N"), ws.Root, Identity);
        var turn    = new SessionTurn(Guid.NewGuid().ToString("N"), messages[3].Content);
        var run     = new SessionRun(Guid.NewGuid().ToString("N"), session.Id, turn.Id);
        session.AddTurn(turn);
        turn.AddRun(run);
        run.Finish(new AgentResult
        {
            Status = AgentStatus.Completed, FinalMessage = messages[^1].Content, Error = "error " + secret
        });
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MY_API_KEY"] = secret, ["NUMERIC_SECRET"] = numericSecret
        };
        using (var writer = new SessionStoreWriter(dir.Root, session.Id, secrets))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        using var reopened =
            await SessionStoreWriter.OpenExistingAsync(dir.Root, session.Id, secrets, CancellationToken.None);
        var snapshot = reopened.LoadedSnapshot!;
        Assert.Equal(3, snapshot.FormatVersion);
        var conversation = snapshot.Conversation!;
        Assert.False(conversation.OpaqueRedacted);
        Assert.Equal(compacted ? state.ToJson() : null, conversation.StateJson);
        Assert.Equal(compacted, conversation.HasState);
        Assert.Equal(context.Position.FoldStartMessageIndex, conversation.FoldStartMessageIndex);
        Assert.Equal(context.Position.FoldEndMessageIndex, conversation.FoldEndMessageIndex);
        Assert.Equal(messages.Length, conversation.Messages.Count);
        for (var i = 0; i < messages.Length; i++)
        {
            var original = messages[i];
            var stored   = conversation.Messages[i];
            Assert.Equal(original.Role.ToString(), stored.Role);
            Assert.Equal(original.Content, stored.Content);
            Assert.Equal(original.Name, stored.Name);
            Assert.Equal(original.ToolCallId, stored.ToolCallId);
            Assert.Equal(original.ToolCalls?.Select(c => (c.Id, c.FunctionName, c.ArgumentsJson)),
                         stored.ToolCalls?.Select(c => (c.Id, c.Name, c.Arguments)));
            Assert.Equal(original.Reasoning?.Select(r => (r.Text, r.ProtectedData, r.ItemId)),
                         stored.Reasoning?.Select(r => (r.Text, r.ProtectedData, r.ItemId)));
            foreach (var entry in stored.Reasoning ?? [])
                Assert.Equal(PersistedReasoning.ComputeOpaqueHash(entry.ProtectedData, entry.ItemId), entry.OpaqueHash);
        }

        Assert.Equal("current [REDACTED] [REDACTED]", snapshot.Turns.Single().UserInput);
        var storedRun = snapshot.Turns.Single().Runs.Single();
        Assert.Equal("done [REDACTED]", storedRun.FinalMessage);
        Assert.Equal("error [REDACTED]", storedRun.Error);
        var audit = await File.ReadAllTextAsync(reopened.AuditPath);
        Assert.DoesNotContain(secret, audit, StringComparison.Ordinal);
        Assert.DoesNotContain(numericSecret, audit, StringComparison.Ordinal);

        var restored = SessionResume.Restore(snapshot, ws.Root, Identity, systemPrompt, contextOptions);
        Assert.Equal(context.Position, restored.Context.Position);
        Assert.Equal(context.State.ToJson(), restored.Context.State.ToJson());
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("continued"));
        using var host = new ChatSessionHost(restored.Session, restored.Context, Options(), client,
                                             new ToolRegistry([]), systemPrompt, sessionWriter : reopened);
        var continued = await host.SubmitTurnAsync("continue", CancellationToken.None);
        Assert.Equal(AgentStatus.Completed, continued.Result!.Status);
        Assert.Null(host.PersistenceWarning);
        var sent = client.RequestLog.Single().Messages;
        Assert.Equal(systemPrompt, sent[0].Content);
        Assert.Contains(sent, m => m.Role == ChatRole.User && m.Content == messages[3].Content);
        var sentCallMessage = sent.Single(m => m.ToolCalls is { Count: > 0 });
        var sentCall        = Assert.Single(sentCallMessage.ToolCalls!);
        Assert.Equal(callId, sentCall.Id);
        Assert.Equal(toolName, sentCall.FunctionName);
        Assert.Equal(arguments, sentCall.ArgumentsJson);
        Assert.Equal(messages[4].Content, sentCallMessage.Content);
        Assert.Equal(messages[4].Reasoning, sentCallMessage.Reasoning);
        var sentTool = sent.Single(m => m.Role == ChatRole.Tool);
        Assert.Equal(toolName, sentTool.Name);
        Assert.Equal(callId, sentTool.ToolCallId);
        Assert.Equal(messages[5].Content, sentTool.Content);
        if (compacted)
        {
            Assert.Contains(sent, m => m.Role == ChatRole.User && m.Content == state.ToJson());
            Assert.DoesNotContain(sent, m => m.Content == messages[1].Content);
        }
        else
        {
            Assert.Contains(sent, m => m.Role == ChatRole.User && m.Content == messages[1].Content);
        }
    }

    [Fact]
    public void Commit_OpaqueHashCoversBothProtectedDataAndItemId()
    {
        Assert.Equal(PersistedReasoning.ComputeOpaqueHash("same", "one"),
                     PersistedReasoning.ComputeOpaqueHash("same", "one"));
        Assert.NotEqual(PersistedReasoning.ComputeOpaqueHash("same", "one"),
                        PersistedReasoning.ComputeOpaqueHash("same", "two"));
        Assert.NotEqual(PersistedReasoning.ComputeOpaqueHash("one", "same"),
                        PersistedReasoning.ComputeOpaqueHash("ones", "ame"));
        Assert.Equal(64, PersistedReasoning.ComputeOpaqueHash(null, null).Length);
    }

    /// <summary>
    ///     <para>构造最小完整的可续聊快照；可替换会话内容或运行状态以验证指定边界。</para>
    ///     <para>
    ///         Builds a minimal complete resumable snapshot; conversation content or run status
    ///         can be replaced for targeted cases.
    ///     </para>
    /// </summary>
    private static PersistedSession ValidSnapshot(PersistedConversation? conversation = null,
                                                  string?                runStatus    = null) => new()
    {
        FormatVersion  = PersistedSession.CurrentFormatVersion,
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

    private static PersistedConversation ValidConversation(string?                          stateJson = null,
                                                           bool                             hasState  = false,
                                                           int                              foldStart = -1,
                                                           int                              foldEnd   = 0,
                                                           IReadOnlyList<PersistedMessage>? messages  = null) => new()
    {
        Messages              = messages ?? DefaultMessages(),
        StateJson             = stateJson,
        HasState              = hasState,
        FoldStartMessageIndex = foldStart,
        FoldEndMessageIndex   = foldEnd
    };

    private static List<PersistedMessage> DefaultMessages() =>
    [
        new() { Role = ChatRole.System.ToString(), Content = SystemPrompt },
        new() { Role = ChatRole.User.ToString(), Content   = "what is here?" },
        new()
        {
            Role    = ChatRole.Assistant.ToString(),
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

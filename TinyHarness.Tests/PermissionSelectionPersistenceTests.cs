using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         权限选择 /2 与迁移记录的存储接线测试：修订链与迁移确认、无迁移需求和已完成迁移拒绝、
///         失败与取消状态保留、并发/运行中确认及模式切换期间的迁移门禁。
///     </para>
///     <para>
///         Storage-wiring tests for the /2 approval-mode selection and the migration record: ChangeApprovalModeAsync
///         writes a readable /2 selection, /1 and /2 share one contiguous revision chain, confirmation writes the
///         migration confirmation, rejection without a migration requirement or after completion, state retention
///         after failure or cancellation, concurrent and active-run confirmation guards, and the migration gate
///         during approval-mode changes.
///     </para>
/// </summary>
public class PermissionSelectionPersistenceTests
{
    private const string SystemPrompt = "system prompt";

    private static readonly SessionModelIdentity Identity =
        new("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions);

    private static AgentOptions Options() => new()
    {
        Model = Identity.ModelId, MaxAgentSteps = 10, DefaultToolTimeoutSeconds = 30
    };

    private static ChatSessionHost NewHost(TestTempDir workspace,    TestTempDir        store, FakeChatClient client,
                                           ITool[]?    tools = null, IApprovalProvider? approver = null) =>
        new(workspace.Root, Identity, Options(), client, new ToolRegistry(tools ?? []), SystemPrompt,
            approver : approver,
            sessionWriterFactory : id => new SessionStoreWriter(store.Root, id));

    [Test]
    public async Task ChangeApprovalModeAsync_WritesSelectionV2AndSurvivesReopen()
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        using var host      = NewHost(workspace, store, new FakeChatClient());

        await host.ChangeApprovalModeAsync(SessionApprovalMode.AIAutoReview);

        var snapshot = await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, CancellationToken.None);
        Assert.That(snapshot!.PermissionSelectionV2, Is.Not.Null);
        Assert.That(snapshot.PermissionSelectionV2!.Revision, Is.EqualTo(1));
        Assert.That(snapshot.PermissionSelectionV2.ApprovalMode, Is.EqualTo(SessionApprovalMode.AIAutoReview));
        Assert.That(snapshot.PermissionSelection, Is.Null);
        Assert.That(snapshot.Turns, Is.Empty);

        var restored = SessionResume.Restore(snapshot, workspace.Root, SystemPrompt);
        Assert.That(restored.PermissionMigrationPending, Is.False);
        Assert.That(restored.Session.PermissionSelectionV2!.ApprovalMode, Is.EqualTo(SessionApprovalMode.AIAutoReview));
    }

    [Test]
    public async Task LegacySelectionThenApprovalSwitch_RequiresMigration_AndSharesOneContiguousChain()
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        using var host      = await LegacyHostAsync(workspace, store, "ReadOnly");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                host.ChangeApprovalModeAsync(SessionApprovalMode
                                                                            .AIAutoReview));

        var snapshot = await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, CancellationToken.None);
        Assert.That(snapshot!.PermissionSelection!.Revision, Is.EqualTo(1));
        Assert.That(snapshot.PermissionSelection!.Mode, Is.EqualTo("ReadOnly"));
        Assert.That(snapshot.PermissionSelectionV2, Is.Null);
        Assert.That(snapshot.PermissionMigration, Is.Null);
        Assert.That(host.Session.PermissionMigrationPending, Is.True);
        Assert.That(host.Session.PermissionSelectionV2, Is.Null);

        // 未迁移的 /1 选择仍然在链上：恢复即迁移待确认（D9）。
        var restored = SessionResume.Restore(snapshot, workspace.Root, SystemPrompt);
        Assert.That(restored.PermissionMigrationPending, Is.True);
        Assert.That(restored.Session.PermissionSelectionV2, Is.Null);

        await host.ConfirmPermissionMigrationAsync(SessionApprovalMode.HumanApproval);
        Assert.That(host.Session.PermissionMigration!.Revision, Is.EqualTo(2));
        Assert.That(host.Session.PermissionSelectionV2!.Revision, Is.EqualTo(2));
        await host.ChangeApprovalModeAsync(SessionApprovalMode.AIAutoReview);

        var switchedSnapshot = await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, CancellationToken.None);
        Assert.That(switchedSnapshot!.PermissionSelection!.Revision, Is.EqualTo(1));
        Assert.That(switchedSnapshot.PermissionMigration!.Revision, Is.EqualTo(2));
        Assert.That(switchedSnapshot.PermissionSelectionV2!.Revision, Is.EqualTo(3));
        var switchedResume = SessionResume.Restore(switchedSnapshot, workspace.Root, SystemPrompt);
        Assert.That(switchedResume.PermissionMigrationPending, Is.False);
        Assert.That(switchedResume.Session.PermissionSelectionV2!.ApprovalMode,
                    Is.EqualTo(SessionApprovalMode.AIAutoReview));
    }

    [Test]
    public async Task ConfirmMigration_WritesSharedRevision_RejectsRepeat_AndClearsPending()
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        using var host      = await LegacyHostAsync(workspace, store, "FullAccess");
        var       mode      = await host.ConfirmPermissionMigrationAsync(SessionApprovalMode.HumanApproval);

        Assert.That(mode, Is.EqualTo(SessionApprovalMode.HumanApproval));
        var snapshot = await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, CancellationToken.None);
        Assert.That(snapshot!.PermissionMigration, Is.Not.Null);
        Assert.That(snapshot.PermissionMigration!.Revision, Is.EqualTo(2));
        Assert.That(snapshot.PermissionMigration.PreviousMode, Is.EqualTo("FullAccess"));
        Assert.That(snapshot.PermissionSelectionV2!.Revision, Is.EqualTo(2));
        Assert.That(snapshot.PermissionSelectionV2.ApprovalMode, Is.EqualTo(SessionApprovalMode.HumanApproval));

        // 每个会话至多一条迁移记录：重复确认在写入前拒绝，不产生读取端不可读的日志。
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                host.ConfirmPermissionMigrationAsync(SessionApprovalMode
                                                                            .AIAutoReview));

        var restored = SessionResume.Restore(snapshot, workspace.Root, SystemPrompt);
        Assert.That(restored.PermissionMigrationPending, Is.False);
    }

    [Test]
    public async Task ConfirmMigration_RejectsNewSessionWithoutChangingState()
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        using var host      = NewHost(workspace, store, new FakeChatClient());
        var       revision  = host.Session.PermissionSelectionV2?.Revision;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                host.ConfirmPermissionMigrationAsync(SessionApprovalMode
                                                                            .AIAutoReview));

        Assert.Multiple(() =>
        {
            Assert.That(host.Session.PermissionMigrationPending, Is.False);
            Assert.That(host.Session.PermissionMigration, Is.Null);
            Assert.That(host.Session.PermissionSelectionV2?.Revision, Is.EqualTo(revision));
        });
    }

    [Test]
    public async Task ConfirmMigration_FailedCommitLeavesPendingSessionAndEngineUnchanged()
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        var       failWrite = false;
        var       engine    = new PermissionEngine(workspace.Root);
        using var host = await LegacyHostAsync(workspace, store, "ReadOnly", engine,
                                               (_, phase) =>
                                               {
                                                   if (failWrite && phase == "before-write")
                                                       throw new IOException("injected migration save failure");
                                               });
        var before = await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, CancellationToken.None);

        failWrite = true;
        await Assert.ThrowsAsync<SessionStoreException>(() =>
                                                            host.ConfirmPermissionMigrationAsync(SessionApprovalMode
                                                                        .AIAutoReview));

        var after = await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(host.Session.PermissionMigrationPending, Is.True);
            Assert.That(host.Session.PermissionMigration, Is.Null);
            Assert.That(host.Session.PermissionSelectionV2, Is.Null);
            Assert.That(engine.SelectedApprovalMode, Is.Null);
            Assert.That(after!.CommitSequence, Is.EqualTo(before!.CommitSequence));
            Assert.That(after.PermissionMigration, Is.Null);
            Assert.That(after.PermissionSelectionV2, Is.Null);
        });
    }

    [Test]
    public async Task ConfirmMigration_PreCancelledRequestLeavesPendingSessionAndEngineUnchanged()
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        var       engine    = new PermissionEngine(workspace.Root);
        using var host      = await LegacyHostAsync(workspace, store, "ReadOnly", engine);
        using var source    = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                                                                 await host
                                                                    .ConfirmPermissionMigrationAsync(SessionApprovalMode
                                                                                 .AIAutoReview, source.Token));

        Assert.Multiple(() =>
        {
            Assert.That(host.Session.PermissionMigrationPending, Is.True);
            Assert.That(host.Session.PermissionMigration, Is.Null);
            Assert.That(host.Session.PermissionSelectionV2, Is.Null);
            Assert.That(engine.SelectedApprovalMode, Is.Null);
        });
    }

    [Test]
    public async Task ConfirmMigration_RejectsWhileRunIsActiveAndKeepsPendingGate()
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        var       client    = new GatedChatClient();
        var       engine    = new PermissionEngine(workspace.Root);
        using var host      = await LegacyHostAsync(workspace, store, "ReadOnly", engine, client : client);
        var       run       = host.SubmitTurnAsync("wait for the model", CancellationToken.None);
        try
        {
            await client.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var selectedMode = engine.SelectedApprovalMode;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                    host
                                                                       .ConfirmPermissionMigrationAsync(SessionApprovalMode
                                                                                    .AIAutoReview));
            Assert.Multiple(() =>
            {
                Assert.That(host.Session.PermissionMigrationPending, Is.True);
                Assert.That(host.Session.PermissionMigration, Is.Null);
                Assert.That(host.Session.PermissionSelectionV2, Is.Null);
                Assert.That(engine.SelectedApprovalMode, Is.EqualTo(selectedMode));
            });
        }
        finally
        {
            client.Release();
        }

        var completed = await run;
        Assert.That(completed.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(host.Session.PermissionMigrationPending, Is.True);
    }

    [Test]
    public async Task ConfirmMigration_ConcurrentConfirmationsAllowExactlyOneCommit()
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        using var host      = await LegacyHostAsync(workspace, store, "FullAccess");
        var       first     = CaptureConfirmationAsync(host, SessionApprovalMode.HumanApproval);
        var       second    = CaptureConfirmationAsync(host, SessionApprovalMode.AIAutoReview);
        var       results   = await Task.WhenAll(first, second);

        Assert.Multiple(() =>
        {
            Assert.That(results.Count(result => result.Succeeded), Is.EqualTo(1));
            Assert.That(results.Count(result => result.Error is InvalidOperationException), Is.EqualTo(1));
            Assert.That(host.Session.PermissionMigrationPending, Is.False);
            Assert.That(host.Session.PermissionMigration, Is.Not.Null);
        });
        var snapshot = await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, CancellationToken.None);
        Assert.That(snapshot!.PermissionMigration, Is.Not.Null);
    }

    [Test]
    public async Task MigrationPendingGate_BlocksAllToolsUntilConfirmed()
    {
        using var workspace  = new TestTempDir();
        using var store      = new TestTempDir();
        var       readProbe  = new ProbeTool("read_probe", "filesystem.read");
        var       writeProbe = new ProbeTool("write_probe", "filesystem.write");
        var       client     = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("write_probe", "{}", "call_write"));
        client.Enqueue(FakeChatClient.Text("first done"));
        using var host = NewHost(workspace, store, client, [readProbe, writeProbe],
                                 new FixedApprover());

        // 全新会话无迁移待确认：写工具经审批照常执行。
        var first = await host.SubmitTurnAsync("use write probe", CancellationToken.None);
        Assert.That(first.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(writeProbe.ExecuteCount, Is.EqualTo(1));

        // 恢复"无审批模式选择但有历史运行"的会话：迁移待确认——所有模型工具能力一律拒绝
        // （含只读，D9 严格口径），拒绝理由固定；工具保持注册，文本轮正常。
        var snapshot = await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, CancellationToken.None);
        host.Dispose();
        using var resumedWriter = await SessionStoreWriter.OpenExistingAsync(store.Root, host.Session.Id, null,
                                                                             CancellationToken.None);
        var restored = SessionResume.Restore(snapshot!, workspace.Root, SystemPrompt);
        Assert.That(restored.PermissionMigrationPending, Is.True);

        var resumedClient = new FakeChatClient();
        resumedClient.Enqueue(FakeChatClient.ToolCall("write_probe", "{}", "call_blocked"));
        resumedClient.Enqueue(FakeChatClient.ToolCall("read_probe", "{}", "call_read"));
        resumedClient.Enqueue(FakeChatClient.Text("blocked turn done"));
        using var resumed = new ChatSessionHost(restored.Session, restored.Context, Options(),
                                                resumedClient, new ToolRegistry([readProbe, writeProbe]),
                                                SystemPrompt, approver : new FixedApprover(),
                                                sessionWriter : resumedWriter);
        var blocked = await resumed.SubmitTurnAsync("try probes while pending", CancellationToken.None);
        Assert.That(blocked.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(writeProbe.ExecuteCount, Is.EqualTo(1), "迁移待确认期间任何工具不得执行。");
        Assert.That(readProbe.ExecuteCount, Is.EqualTo(0), "只读工具同样拒绝，无例外。");
        var blockedCalls = blocked.Result!.ToolCalls ?? [];
        Assert.That(blockedCalls.Select(c => c.PreparationError),
                    Is.All.Contains("permission migration pending; confirm to enable tool execution"),
                    $"拒绝理由固定：{string.Join(" | ", blockedCalls.Select(c => c.PreparationError))}");
        Assert.That(resumed.Session.PermissionMigrationPending, Is.True);

        // 待确认会话拒绝切换审批模式，不能通过写入 /2 选择绕过迁移确认。
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                resumed.ChangeApprovalModeAsync(SessionApprovalMode
                                                                            .AIAutoReview));
        Assert.That(resumed.Session.PermissionMigrationPending, Is.True);
        Assert.That(resumed.Session.PermissionSelectionV2, Is.Null);
        resumedClient.Enqueue(FakeChatClient.ToolCall("write_probe", "{}", "call_after_mode_switch"));
        resumedClient.Enqueue(FakeChatClient.Text("still blocked"));
        var stillBlocked = await resumed.SubmitTurnAsync("try probe after mode switch", CancellationToken.None);
        Assert.That(stillBlocked.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(writeProbe.ExecuteCount, Is.EqualTo(1), "被拒绝的审批模式切换不得解除迁移门禁。");
        var switchedSnapshot = await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, CancellationToken.None);
        Assert.That(switchedSnapshot!.PermissionSelectionV2, Is.Null);
        Assert.That(switchedSnapshot.PermissionMigration, Is.Null);
        var switchedResume = SessionResume.Restore(switchedSnapshot!, workspace.Root, SystemPrompt);
        Assert.That(switchedResume.PermissionMigrationPending, Is.True);
        Assert.That(switchedResume.Session.PermissionSelectionV2, Is.Null);

        // 确认迁移解除门禁：工具保持注册，只读直接恢复、写经审批恢复可执行。
        resumedClient.Enqueue(FakeChatClient.ToolCall("read_probe", "{}", "call_read_after"));
        resumedClient.Enqueue(FakeChatClient.Text("read after confirm"));
        resumedClient.Enqueue(FakeChatClient.ToolCall("write_probe", "{}", "call_write_after"));
        resumedClient.Enqueue(FakeChatClient.Text("write after confirm"));
        await resumed.ConfirmPermissionMigrationAsync(SessionApprovalMode.HumanApproval);
        Assert.That(resumed.Session.PermissionMigrationPending, Is.False);
        var readTurn = await resumed.SubmitTurnAsync("use read probe after confirm", CancellationToken.None);
        Assert.That(readTurn.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(readProbe.ExecuteCount, Is.EqualTo(1));
        var writeTurn = await resumed.SubmitTurnAsync("use write probe after confirm", CancellationToken.None);
        Assert.That(writeTurn.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(writeProbe.ExecuteCount, Is.EqualTo(2));
    }

    /// <summary>
    ///     <para>先写入旧会话夹具，再恢复宿主并移交唯一写入端。</para>
    ///     <para>Writes a legacy session fixture, then restores its host and transfers the sole writer.</para>
    /// </summary>
    private static async Task<ChatSessionHost> LegacyHostAsync(
        TestTempDir            workspace,     TestTempDir                      store, string legacyModeName,
        PermissionEngine?      engine = null, Action<SessionLogBatch, string>? injectWriteFault = null,
        IChatCompletionClient? client = null)
    {
        var session = new ChatSession(Guid.NewGuid().ToString("N"), workspace.Root, Identity);
        var context = new ConversationContext();
        context.Append(ChatMessage.System(SystemPrompt));
        var selection = new LegacyPermissionSelection { Revision = 1, Mode = legacyModeName };
        var writer    = new SessionStoreWriter(store.Root, session.Id) { InjectWriteFault = injectWriteFault };
        try
        {
            await writer.CommitControlAsync(session, context,
            [
                new SessionLogRecord(string.Empty, "permission-selection",
                                     JsonSerializer.SerializeToElement(selection,
                                                                       PermissionPersistenceJsonContext.Default
                                                                          .LegacyPermissionSelection))
            ], CancellationToken.None);
            var snapshot = await SessionStoreReader.ReadAsync(store.Root, session.Id, CancellationToken.None);
            var restored = SessionResume.Restore(snapshot!, workspace.Root, SystemPrompt);
            return new ChatSessionHost(restored.Session, restored.Context, Options(), client ?? new FakeChatClient(),
                                       new ToolRegistry([]), SystemPrompt, engine, sessionWriter : writer);
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    private static async Task<(bool Succeeded, Exception? Error)> CaptureConfirmationAsync(
        ChatSessionHost host, SessionApprovalMode mode)
    {
        try
        {
            await host.ConfirmPermissionMigrationAsync(mode);
            return (true, null);
        }
        catch (Exception exception)
        {
            return (false, exception);
        }
    }

    private sealed class GatedChatClient : IChatCompletionClient
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest                      request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "done" };
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason          = "stop" };
        }

        public void Release() => _release.TrySetResult();
    }

    /// <summary>始终授予单次授权的审批器：验证确认后副作用路径恢复可执行。</summary>
    private sealed class FixedApprover : IApprovalProvider
    {
        public Task<ApprovalResolution> PromptAsync(ApprovalRequest   request,
                                                    CancellationToken cancellationToken) =>
            Task.FromResult(ApprovalResolution.Granted(GrantScope.SingleOperation));
    }

    /// <summary>可配置能力的探针工具：用于观察迁移门禁前后的执行差异。</summary>
    private sealed class ProbeTool(string name, string capability) : ITool
    {
        public int ExecuteCount { get; private set; }

        public ToolDefinition Definition { get; } = new()
        {
            Name = name, Description = name, Parameters = new JsonObject { ["type"] = "object" }
        };

        public ToolPreparation Prepare(ChatToolCall call) => new()
        {
            ToolName   = Definition.Name, CallId = call.Id, Arguments = new JsonObject(),
            Capability = capability, Summary     = $"{name}()"
        };

        public Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            return Task.FromResult(new ToolResult { Succeeded = true, Content = "probe ok" });
        }
    }
}

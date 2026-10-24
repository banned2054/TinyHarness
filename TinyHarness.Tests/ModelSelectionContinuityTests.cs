using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         验证凭据路由变化的推理连续性与空持久会话首次模型选择提交的可恢复性。
///     </para>
///     <para>
///         Verifies reasoning continuity across credential-route changes and the resumability of a first
///         model-selection commit on an empty persisted session.
///     </para>
/// </summary>
public class ModelSelectionContinuityTests
{
    private const string SystemPrompt = "You are a test harness inspecting a workspace.";

    private static readonly SessionModelIdentity Identity =
        new("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions);

    private static AgentOptions Options() => new()
    {
        Model                     = "test-model",
        MaxAgentSteps             = 10,
        DefaultToolTimeoutSeconds = 30
    };

    private static readonly string WorkspaceRoot =
        Path.Combine(Path.GetTempPath(), "tinyharness-model-selection-continuity-tests");

    private static ChatSessionHost NewHost(TestTempDir store, FakeChatClient client, string? credentialRoute) =>
        new(WorkspaceRoot,
            Identity, Options(), client, new ToolRegistry([]), SystemPrompt,
            sessionWriterFactory : id => new SessionStoreWriter(store.Root, id),
            credentialRoute : credentialRoute);

    /// <summary>
    ///     <para>按恢复入口的形状重开日志并接管会话；不做路由核对，由各测试显式调用。</para>
    ///     <para>Reopens the log and takes over the session the way resume entries do; route reconciliation stays explicit per test.</para>
    /// </summary>
    private static async Task<ChatSessionHost> ResumeHostAsync(
        TestTempDir store, string sessionId, FakeChatClient client)
    {
        var writer = await SessionStoreWriter.OpenExistingAsync(store.Root, sessionId, null, CancellationToken.None);
        var restored = SessionResume.Restore(writer.LoadedSnapshot!, WorkspaceRoot, Identity, SystemPrompt,
                                             Options().Context);
        return new ChatSessionHost(restored.Session, restored.Context, Options(), client, new ToolRegistry([]),
                                   SystemPrompt, sessionWriter : writer);
    }

    [Test]
    public async Task ChangeModel_SameRouteKeepsReasoningContinuity()
    {
        using var store = new TestTempDir();
        var       first = new FakeChatClient();
        first.Enqueue(FakeChatClient.ReasonedText("reason one", "item_1", "opaque-one", "answer one"));
        var second = new FakeChatClient();
        second.Enqueue(FakeChatClient.Text("answer two"));
        using var host = NewHost(store, first, "work|env:WORK_KEY");

        await host.SubmitTurnAsync("first question", CancellationToken.None);
        var selection = await host.ChangeModelAsync(Identity, second, Options().Context, null,
                                                    CancellationToken.None, credentialRoute : "work|env:WORK_KEY");
        var run = await host.SubmitTurnAsync("second question", CancellationToken.None);

        // 同一路由重选：epoch 与推理起点不变，新客户端仍回传旧回合的 opaque 推理。
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed), run.Result.Error);
        Assert.That(selection.CredentialRoute, Is.EqualTo("work|env:WORK_KEY"));
        Assert.That(selection.ContinuationEpoch, Is.EqualTo(0));
        Assert.That(selection.ReasoningStartMessageIndex, Is.EqualTo(0));
        var sent      = second.RequestLog.Single().Messages.Single(message => message.Role == ChatRole.Assistant);
        var reasoning = TestAssert.Single(sent.Reasoning!);
        Assert.That(reasoning.ProtectedData, Is.EqualTo("opaque-one"));
        Assert.That(reasoning.ItemId, Is.EqualTo("item_1"));
    }

    [Test]
    public async Task ChangeModel_RouteChangeWithSameModelIdentityIsolatesOldReasoning()
    {
        using var store = new TestTempDir();
        var       first = new FakeChatClient();
        first.Enqueue(FakeChatClient.ReasonedText("reason one", "item_1", "opaque-one", "answer one"));
        var second = new FakeChatClient();
        second.Enqueue(FakeChatClient.Text("answer two"));
        using var host = NewHost(store, first, "work|env:WORK_KEY");

        await host.SubmitTurnAsync("first question", CancellationToken.None);
        var selection = await host.ChangeModelAsync(Identity, second, Options().Context, null,
                                                    CancellationToken.None, credentialRoute : "alt|credential:ALT");
        var run = await host.SubmitTurnAsync("second question", CancellationToken.None);

        // 模型三元组不变但凭据路由变化：正文保留，旧账户的 opaque 推理不再进入新客户端请求。
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed), run.Result.Error);
        Assert.That(selection.CredentialRoute, Is.EqualTo("alt|credential:ALT"));
        Assert.That(selection.ContinuationEpoch, Is.EqualTo(1));
        Assert.That(selection.ReasoningStartMessageIndex, Is.EqualTo(3));
        var sent = second.RequestLog.Single().Messages.Single(message => message.Role == ChatRole.Assistant);
        Assert.That(sent.Content, Is.EqualTo("answer one"));
        Assert.That(sent.Reasoning, Is.Null);
    }

    [Test]
    public async Task ChangeReasoningEffort_PreservesRouteAndContinuity()
    {
        using var store  = new TestTempDir();
        var       client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ReasonedText("reason one", "item_1", "opaque-one", "answer one"));
        client.Enqueue(FakeChatClient.Text("answer two"));
        using var host = NewHost(store, client, "work|env:WORK_KEY");

        await host.SubmitTurnAsync("first question", CancellationToken.None);
        var selection = await host.ChangeReasoningEffortAsync(ModelReasoningEffort.High, CancellationToken.None);
        var run       = await host.SubmitTurnAsync("second question", CancellationToken.None);

        // 仅调整强度：路由、epoch 与推理起点全部保留，下一请求仍回传旧 opaque 推理。
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed), run.Result.Error);
        Assert.That(selection.ReasoningEffort, Is.EqualTo(ModelReasoningEffort.High));
        Assert.That(selection.CredentialRoute, Is.EqualTo("work|env:WORK_KEY"));
        Assert.That(selection.ContinuationEpoch, Is.EqualTo(0));
        Assert.That(selection.ReasoningStartMessageIndex, Is.EqualTo(0));
        var sent = client.RequestLog[1].Messages.Single(message => message.Role == ChatRole.Assistant);
        Assert.That(TestAssert.Single(sent.Reasoning!).ProtectedData, Is.EqualTo("opaque-one"));
    }

    [Test]
    public async Task ChangeModel_FirstSelectionOnEmptySessionEstablishesResumableContext()
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        string    sessionId;
        using (var host = new ChatSessionHost(workspace.Root, Identity, Options(), new FakeChatClient(),
                                              new ToolRegistry([]), SystemPrompt,
                                              sessionWriterFactory : id => new SessionStoreWriter(store.Root, id),
                                              credentialRoute : "work|env:WORK_KEY"))
        {
            sessionId = host.Session.Id;
            var selection = await host.ChangeModelAsync(Identity, new FakeChatClient(), Options().Context,
                                                        ModelReasoningEffort.High, CancellationToken.None,
                                                        credentialRoute : "work|env:WORK_KEY");

            // 空持久会话首次提交选择：确立 system 上下文后边界为 1，不伪造用户回合。
            Assert.That(selection.MessageBoundary, Is.EqualTo(1));
            Assert.That(selection.CredentialRoute, Is.EqualTo("work|env:WORK_KEY"));
            Assert.That(host.Session.Turns, Is.Empty);
            Assert.That(host.PersistenceWarning, Is.Null);
        }

        // 提交后的日志必须可恢复：不再因空会话被拒绝续聊。
        using var writer = await SessionStoreWriter.OpenExistingAsync(store.Root, sessionId, null,
                                                                      CancellationToken.None);
        var restored = SessionResume.Restore(writer.LoadedSnapshot!, workspace.Root, Identity, SystemPrompt,
                                             Options().Context);
        Assert.That(restored.Context.Messages.Count, Is.EqualTo(1));
        Assert.That(restored.Context.Messages[0].Role, Is.EqualTo(ChatRole.System));
        Assert.That(restored.Context.Messages[0].Content, Is.EqualTo(SystemPrompt));
        Assert.That(restored.Session.ModelSelection!.ReasoningEffort, Is.EqualTo(ModelReasoningEffort.High));
        Assert.That(restored.Session.ModelSelection.CredentialRoute, Is.EqualTo("work|env:WORK_KEY"));
        Assert.That(restored.Session.Turns, Is.Empty);
    }

    [Test]
    public async Task ChangeModel_WithoutWriterKeepsEmptySessionAsDraft()
    {
        using var workspace = new TestTempDir();
        using var host = new ChatSessionHost(workspace.Root, Identity, Options(), new FakeChatClient(),
                                             new ToolRegistry([]), SystemPrompt);

        var selection = await host.ChangeModelAsync(Identity, new FakeChatClient(), Options().Context,
                                                    ModelReasoningEffort.High, CancellationToken.None,
                                                    credentialRoute : "work|env:WORK_KEY");

        // 无写入端：不确立 system 上下文，选择只是内存草稿，边界保持 0。
        Assert.That(selection.MessageBoundary, Is.EqualTo(0));
        Assert.That(host.Session.Turns, Is.Empty);
    }

    [Test]
    public void SessionModelSelection_RoundTripsCredentialRouteAndReadsLegacyPayloadAsUntracked()
    {
        var routed = new SessionModelSelection
        {
            Revision = 2, Model = Identity, CredentialRoute = "work|env:WORK_KEY", MessageBoundary = 1
        };

        var roundTripped = JsonSerializer.SerializeToNode(routed, SessionLogJsonContext.Default.SessionModelSelection)!
                                         .Deserialize(SessionLogJsonContext.Default.SessionModelSelection)!;
        Assert.That(roundTripped.CredentialRoute, Is.EqualTo("work|env:WORK_KEY"));

        // 历史日志不含 credentialRoute 字段：读取为 null（未跟踪），不拒绝续聊。
        var legacy = (JsonObject)JsonSerializer.SerializeToNode(routed with { CredentialRoute = null },
                                                                SessionLogJsonContext.Default.SessionModelSelection)!;
        legacy.Remove("credentialRoute");
        var untracked = legacy.Deserialize(SessionLogJsonContext.Default.SessionModelSelection)!;
        Assert.That(untracked.CredentialRoute, Is.Null);
    }

    [Test]
    public async Task Resume_SameRouteKeepsReasoningContinuity()
    {
        using var store = new TestTempDir();
        var       first = new FakeChatClient();
        first.Enqueue(FakeChatClient.ReasonedText("reason one", "item_1", "opaque-one", "answer one"));
        string sessionId;
        using (var host = NewHost(store, first, "work|env:WORK_KEY"))
        {
            sessionId = host.Session.Id;
            await host.SubmitTurnAsync("first question", CancellationToken.None);
        }

        var second = new FakeChatClient();
        second.Enqueue(FakeChatClient.Text("answer two"));
        using var resumed = await ResumeHostAsync(store, sessionId, second);

        var before  = resumed.Session.ModelSelection!;
        var aligned = await resumed.EnsureCredentialRouteAsync("work|env:WORK_KEY", CancellationToken.None);
        var run     = await resumed.SubmitTurnAsync("second question", CancellationToken.None);

        // 历史路由与当前配置一致：不新增修订、不开新 epoch，请求仍回传旧 opaque 推理。
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed), run.Result.Error);
        Assert.That(aligned.Revision, Is.EqualTo(before.Revision));
        Assert.That(aligned.CredentialRoute, Is.EqualTo("work|env:WORK_KEY"));
        Assert.That(aligned.ContinuationEpoch, Is.EqualTo(0));
        var sent = second.RequestLog.Single().Messages.Single(message => message.Role == ChatRole.Assistant);
        Assert.That(TestAssert.Single(sent.Reasoning!).ProtectedData, Is.EqualTo("opaque-one"));
    }

    [Test]
    public async Task Resume_RouteChangeWithSameIdentityIsolatesOldReasoning()
    {
        using var store = new TestTempDir();
        var       first = new FakeChatClient();
        first.Enqueue(FakeChatClient.ReasonedText("reason one", "item_1", "opaque-one", "answer one"));
        string sessionId;
        using (var host = NewHost(store, first, "work|env:WORK_KEY"))
        {
            sessionId = host.Session.Id;
            await host.SubmitTurnAsync("first question", CancellationToken.None);
        }

        var second = new FakeChatClient();
        second.Enqueue(FakeChatClient.Text("answer two"));
        using var resumed = await ResumeHostAsync(store, sessionId, second);

        var before  = resumed.Session.ModelSelection!;
        var aligned = await resumed.EnsureCredentialRouteAsync("alt|credential:ALT", CancellationToken.None);
        var run     = await resumed.SubmitTurnAsync("second question", CancellationToken.None);

        // 同一模型三元组、当前配置解析出不同凭据路由：按选择事务采纳并开新 epoch，旧 opaque 不再回传。
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed), run.Result.Error);
        Assert.That(aligned.Revision, Is.EqualTo(before.Revision + 1));
        Assert.That(aligned.CredentialRoute, Is.EqualTo("alt|credential:ALT"));
        Assert.That(aligned.ContinuationEpoch, Is.EqualTo(before.ContinuationEpoch + 1));
        var sent = second.RequestLog.Single().Messages.Single(message => message.Role == ChatRole.Assistant);
        Assert.That(sent.Content, Is.EqualTo("answer one"));
        Assert.That(sent.Reasoning, Is.Null);
    }

    [Test]
    public async Task Resume_LegacyLogWithoutRouteAdoptsCurrentRouteConservatively()
    {
        using var store = new TestTempDir();
        var       first = new FakeChatClient();
        first.Enqueue(FakeChatClient.ReasonedText("reason one", "item_1", "opaque-one", "answer one"));
        string sessionId;
        using (var host = NewHost(store, first, null))
        {
            // 无路由记录的历史日志（credentialRoute 缺省）：路由来源不可考。
            sessionId = host.Session.Id;
            await host.SubmitTurnAsync("first question", CancellationToken.None);
        }

        var second = new FakeChatClient();
        second.Enqueue(FakeChatClient.Text("answer two"));
        using var resumed = await ResumeHostAsync(store, sessionId, second);

        var before = resumed.Session.ModelSelection!;
        Assert.That(before.CredentialRoute, Is.Null);
        var aligned = await resumed.EnsureCredentialRouteAsync("work|env:WORK_KEY", CancellationToken.None);
        var run     = await resumed.SubmitTurnAsync("second question", CancellationToken.None);

        // 旧日志不补写“历史由当前路由产生”：采纳视为路由变化，保守隔离旧 opaque 推理。
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed), run.Result.Error);
        Assert.That(aligned.Revision, Is.EqualTo(before.Revision + 1));
        Assert.That(aligned.CredentialRoute, Is.EqualTo("work|env:WORK_KEY"));
        Assert.That(aligned.ContinuationEpoch, Is.EqualTo(before.ContinuationEpoch + 1));
        var sent = second.RequestLog.Single().Messages.Single(message => message.Role == ChatRole.Assistant);
        Assert.That(sent.Reasoning, Is.Null);
    }

    [Test]
    public async Task ChangeModel_IdenticalFirstSelectionStillEstablishesAndResumes()
    {
        using var store  = new TestTempDir();
        var       client = new FakeChatClient();
        string    sessionId;
        using (var host = new ChatSessionHost(WorkspaceRoot, Identity, Options(), client, new ToolRegistry([]),
                                              SystemPrompt,
                                              sessionWriterFactory : id => new SessionStoreWriter(store.Root, id),
                                              credentialRoute : "work|env:WORK_KEY"))
        {
            sessionId = host.Session.Id;

            // 与内存种子完全相同的首次选择：不得因“无变化”跳过首次持久化，否则日志保持为空。
            var selection = await host.ChangeModelAsync(Identity, client, Options().Context, null,
                                                        CancellationToken.None,
                                                        credentialRoute : "work|env:WORK_KEY");
            Assert.That(selection.Revision, Is.EqualTo(2));
            Assert.That(selection.MessageBoundary, Is.EqualTo(1));
            Assert.That(host.PersistenceWarning, Is.Null);
        }

        var reopenedWriter = await SessionStoreWriter.OpenExistingAsync(store.Root, sessionId, null,
                                                                        CancellationToken.None);
        var restored = SessionResume.Restore(reopenedWriter.LoadedSnapshot!, WorkspaceRoot, Identity, SystemPrompt,
                                             Options().Context);
        using var resumed = new ChatSessionHost(restored.Session, restored.Context, Options(), new FakeChatClient(),
                                                new ToolRegistry([]), SystemPrompt,
                                                sessionWriter : reopenedWriter);
        Assert.That(resumed.Session.ModelSelection!.Revision, Is.EqualTo(2));
        Assert.That(resumed.Session.ModelSelection.CredentialRoute, Is.EqualTo("work|env:WORK_KEY"));
        Assert.That(restored.Context.Messages.Count, Is.EqualTo(1));
        Assert.That(restored.Context.Messages[0].Role, Is.EqualTo(ChatRole.System));
    }

    [Test]
    public async Task ChangeModel_IdenticalSelectionAfterDurableSaveAddsNoRevision()
    {
        using var store  = new TestTempDir();
        var       client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ReasonedText("reason one", "item_1", "opaque-one", "answer one"));
        using var host = NewHost(store, client, "work|env:WORK_KEY");

        // 首回合提交使种子选择可靠保存；随后相同选择保持 no-op。
        await host.SubmitTurnAsync("first question", CancellationToken.None);
        var selection = await host.ChangeModelAsync(Identity, client, Options().Context, null, CancellationToken.None,
                                                    credentialRoute : "work|env:WORK_KEY");

        Assert.That(selection.Revision, Is.EqualTo(1));
        Assert.That(selection.ContinuationEpoch, Is.EqualTo(0));
        Assert.That(host.Session.ModelSelections.Count, Is.EqualTo(1));
    }
}

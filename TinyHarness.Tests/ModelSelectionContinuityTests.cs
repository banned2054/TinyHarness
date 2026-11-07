using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;
using MicrosoftChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = TinyHarness.Core.Models.ChatCompletions.ChatRole;
using TextContent = Microsoft.Extensions.AI.TextContent;

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
        var selection = await host.ChangeModelAsync(
            new SessionModelBinding(Identity, second, Options().Context, ModelReasoningEffort.High,
                                    "work|env:WORK_KEY"),
            CancellationToken.None);
        var run = await host.SubmitTurnAsync("second question", CancellationToken.None);

        // 同一路由仅改强度：epoch 与推理起点不变，新客户端仍回传旧回合的 opaque 推理；
        // 完全相同的策略重复选择按契约是 no-op，不采纳等价新客户端。
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed), run.Result.Error);
        Assert.That(selection.CredentialRoute, Is.EqualTo("work|env:WORK_KEY"));
        Assert.That(selection.ReasoningEffort, Is.EqualTo(ModelReasoningEffort.High));
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
        var selection = await host.ChangeModelAsync(
            new SessionModelBinding(Identity, second, Options().Context, null, "alt|credential:ALT"),
            CancellationToken.None);
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
    public async Task ChangeModel_TimeoutPolicyChangeAdoptsNewClientAndKeepsContinuity()
    {
        using var store     = new TestTempDir();
        var       first     = new FakeChatClient();
        first.Enqueue(FakeChatClient.Text("answer one"));
        var second = new FakeChatClient();
        second.Enqueue(FakeChatClient.Text("answer two"));
        var redundant = new FakeChatClient();
        var fourth    = new FakeChatClient();
        fourth.Enqueue(FakeChatClient.Text("answer four"));
        using var host = NewHost(store, first, "work|env:WORK_KEY");

        await host.SubmitTurnAsync("first question", CancellationToken.None);
        var materialized = await host.ChangeModelAsync(
            new SessionModelBinding(Identity, second, Options().Context, null, "work|env:WORK_KEY"),
            CancellationToken.None);
        await host.SubmitTurnAsync("second question", CancellationToken.None);

        // 相同策略（含默认超时）的等价候选仍按 no-op 丢弃：不产生修订，冗余客户端从未被使用。
        var noop = await host.ChangeModelAsync(
            new SessionModelBinding(Identity, redundant, Options().Context, null, "work|env:WORK_KEY"),
            CancellationToken.None);
        // 超时策略变化必须真实替换客户端，但不重置推理连续性。
        var switched = await host.ChangeModelAsync(
            new SessionModelBinding(Identity, fourth, Options().Context, null, "work|env:WORK_KEY",
                                    effectiveTimeouts : new ModelTimeouts { ResponseTimeoutSeconds = 30 }),
            CancellationToken.None);
        var run = await host.SubmitTurnAsync("third question", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed), run.Result.Error);
            Assert.That(ReferenceEquals(noop, materialized), Is.True, "相同策略重复选择应返回当前选择（no-op）");
            Assert.That(redundant.RequestLog, Is.Empty);
            Assert.That(switched.Revision, Is.EqualTo(materialized.Revision + 1));
            Assert.That(switched.ContinuationEpoch, Is.EqualTo(materialized.ContinuationEpoch));
            Assert.That(switched.ReasoningStartMessageIndex, Is.EqualTo(materialized.ReasoningStartMessageIndex));
            Assert.That(host.EffectiveModelTimeouts.ResponseTimeoutSeconds, Is.EqualTo(30));
            Assert.That(fourth.RequestLog, Has.Count.EqualTo(1), "超时策略变化后新客户端应接管后续请求");
        });
    }

    /// <summary>
    ///     <para>
    ///         run 入口的初始快照契约（组合缝）：宿主以带非默认超时策略的真实 MicrosoftAiChatClient
    ///         构造并传入 <c>modelTimeouts:</c> 时，EffectiveModelTimeouts 与初始绑定标签必须等于
    ///         该客户端实际策略，首个请求落在该客户端上。组合层（Program 的 run 入口）传参本身
    ///         由独立审查覆盖，此处固化 ChatSessionHost 构造契约。
    ///     </para>
    ///     <para>
    ///         The run entry's initial snapshot contract (composition seam): when the host is constructed
    ///         with a real MicrosoftAiChatClient carrying non-default timeouts plus the
    ///         <c>modelTimeouts:</c> argument, EffectiveModelTimeouts and the initial binding tag must equal
    ///         the client's actual policy, and the first request lands on that client. The composition's own
    ///         argument passing (Program's run entry) is covered by separate review; this pins the
    ///         ChatSessionHost construction contract.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Constructor_SnapshotsClientTimeoutPolicyAsInitialBinding()
    {
        using var store = new TestTempDir();
        var timeouts = new ModelTimeouts
        {
            StreamIdleTimeoutSeconds = 42, ResponseTimeoutSeconds = 17, ApprovalTimeoutSeconds = 61
        };
        var inner = new ScriptedSdkClient();
        using var client = new MicrosoftAiChatClient(inner, ChatApiKind.ChatCompletions, null, null,
                                                     timeouts, null);
        using var host = new ChatSessionHost(WorkspaceRoot, Identity, Options(), client, new ToolRegistry([]),
                                             SystemPrompt,
                                             sessionWriterFactory : id => new SessionStoreWriter(store.Root, id),
                                             credentialRoute : "work|env:WORK_KEY",
                                             modelTimeouts : client.EffectiveTimeouts);

        Assert.That(host.EffectiveModelTimeouts, Is.EqualTo(timeouts));
        var run = await host.SubmitTurnAsync("first question", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed), run.Result.Error);
            Assert.That(inner.Requests, Is.EqualTo(1));
            Assert.That(host.EffectiveModelTimeouts, Is.EqualTo(timeouts));
        });
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
            var selection = await host.ChangeModelAsync(
                new SessionModelBinding(Identity, new FakeChatClient(), Options().Context,
                                        ModelReasoningEffort.High, "work|env:WORK_KEY"),
                CancellationToken.None);

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

        var selection = await host.ChangeModelAsync(
            new SessionModelBinding(Identity, new FakeChatClient(), Options().Context,
                                    ModelReasoningEffort.High, "work|env:WORK_KEY"),
            CancellationToken.None);

        // 无写入端：不确立 system 上下文，选择只是内存草稿，边界保持 0。
        Assert.That(selection.MessageBoundary, Is.EqualTo(0));
        Assert.That(host.Session.Turns, Is.Empty);
    }

    [Test]
    public void SessionModelSelection_RoundTripsCredentialRouteAndReadsLegacyPayloadAsUntracked()
    {
        var routed = new SessionModelSelection
        {
            Revision        = 2,
            Model           = Identity,
            CredentialRoute = "work|env:WORK_KEY",
            MessageBoundary = 1
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
            var selection = await host.ChangeModelAsync(
                new SessionModelBinding(Identity, client, Options().Context, null, "work|env:WORK_KEY"),
                CancellationToken.None);
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
        var selection = await host.ChangeModelAsync(
            new SessionModelBinding(Identity, client, Options().Context, null, "work|env:WORK_KEY"),
            CancellationToken.None);

        Assert.That(selection.Revision, Is.EqualTo(1));
        Assert.That(selection.ContinuationEpoch, Is.EqualTo(0));
        Assert.That(host.Session.ModelSelections.Count, Is.EqualTo(1));
    }

    /// <summary>脚本化 M.E.AI 底层客户端：每次流请求立即产出一段文本与 stop 收尾，并统计请求数。</summary>
    private sealed class ScriptedSdkClient : IChatClient
    {
        public int Requests { get; private set; }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MicrosoftChatMessage> messages, ChatOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests++;
            await Task.Yield();
            yield return new ChatResponseUpdate(null, new List<AIContent> { new TextContent("scripted answer") });
            yield return new ChatResponseUpdate(null, new List<AIContent>())
            {
                FinishReason = ChatFinishReason.Stop
            };
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MicrosoftChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake only supports streaming.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

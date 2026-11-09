using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Persistence;
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
///         授权事件审计接线（B6-1）：AgentLoop 轮边界 drain（granted/consumed 落盘）、宿主运行终态
///         （ExpireTurnGrants → expired 落盘、drain 失败转 DisplayWarning 不阻断收尾）、空闲边界
///         （模式切换 → revoked 落盘、迁移确认 → authorization-changed+拒绝记忆整段解除）。全部断言
///         读真实会话日志 JSONL 的 permission-grant-event/1 控制记录。
///     </para>
///     <para>
///         Grant-event audit wiring (B6-1): the AgentLoop's turn-boundary drain (granted/consumed persisted),
///         the host run terminal (ExpireTurnGrants → expired persisted, drain failures becoming a
///         DisplayWarning without blocking finalization), and idle boundaries (mode switch → revoked
///         persisted, migration confirmation → authorization-changed plus the wholesale denial-memory
///         release). Every assertion reads permission-grant-event/1 control records from the real session
///         log JSONL.
///     </para>
/// </summary>
public class GrantEventWiringTests
{
    private const string SystemPrompt = "system prompt";

    private static readonly SessionModelIdentity Identity =
        new("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions);

    private static AgentOptions Options() => new()
    {
        Model = Identity.ModelId, MaxAgentSteps = 10, DefaultToolTimeoutSeconds = 30
    };

    // ---- 运行终态：Turn 授权 granted → run 终态 expired，全部经 recorder 落盘 ----

    [Test]
    public async Task TurnGrantLifecycle_PersistsGrantedAndExpiredEvents_AtTheRunTerminal()
    {
        using var dir      = new TestTempDir();
        var       writeProbe = new WriteProbeTool(dir.Root);
        var       engine   = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        var       client   = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("write_probe", "{}"));
        client.Enqueue(FakeChatClient.Text("write done"));
        var approver = new FixedApprover(ApprovalResolution.Granted(GrantScope.Turn));
        using var host = NewHost(dir.Root, client, writeProbe, engine, approver,
                                 id => new SessionStoreWriter(dir.Root, id));

        var run = await host.SubmitTurnAsync("write outside", CancellationToken.None);

        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed),
                    $"status={run.Result.Status} error={run.Result.Error} " +
                    $"warning={host.DisplayWarning} executed={writeProbe.ExecuteCount}");
        Assert.That(writeProbe.ExecuteCount, Is.EqualTo(1));
        host.Dispose();

        var records = await ReadGrantEventsAsync(dir.Root, host.Session.Id);
        Assert.Multiple(() =>
        {
            Assert.That(records.Any(record => record.Event == "granted" && record.Scope == "turn" &&
                                              record.Capability == "filesystem.write" &&
                                              record.PolicyIdentityRevision.Length > 0),
                        Is.True, "批准 Turn 授权必须落盘 granted 事件（含策略身份修订）。");
            Assert.That(records.Any(record => record.Event == "expired" && record.Scope == "turn"),
                        Is.True, "运行终态 ExpireTurnGrants 必须落盘 expired 事件。");
            Assert.That(records.All(record => record.Schema ==
                                              PermissionGrantEventRecord.CurrentSchema), Is.True);
        });
    }

    // ---- 轮边界：登记表单次授权 granted + 派发消费 consumed ----

    [Test]
    public async Task RegistrySingleOperationGrant_PersistsGrantedAndConsumed_AtTheLoopBoundary()
    {
        using var dir        = new TestTempDir();
        var       writeProbe = new WriteProbeTool(dir.Root);
        var       engine     = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        // 宿主直接登记的单次授权（与 Shell 审批同一冻结形态）：Add 产生 granted，派发消费产生
        // consumed；轮边界 drain 两者一起落盘。审批卡的 SingleOperation 批准本身走派发凭据
        // （不入登记表、无 granted 事件），其审计面是 tool.permission 记录——该形态单独固化。
        var preparation = writeProbe.Prepare(new ChatToolCall("seed", "write_probe", "{}"));
        engine.Grants.Add(GrantScope.SingleOperation, preparation);
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("write_probe", "{}"));
        client.Enqueue(FakeChatClient.Text("single done"));

        var recorder = new CollectingRecorder();
        var loop = new AgentLoop(client, new ToolRegistry([writeProbe]), Options(), engine, null, recorder);
        var result = await loop.RunAsync("sys", "write outside once", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(writeProbe.ExecuteCount, Is.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(recorder.Events.Any(record => record.Event == "granted" &&
                                                      record.Scope == "single-operation"), Is.True);
            Assert.That(recorder.Events.Any(record => record.Event == "consumed" &&
                                                      record.Scope == "single-operation"), Is.True,
                        "派发消费必须落盘 consumed 事件（单次原子消费的审计面）。");
        });
    }

    [Test]
    public async Task SingleOperationApproval_RidesTheDispatchCredential_WithoutRegistryGrantEvents()
    {
        using var dir        = new TestTempDir();
        var       writeProbe = new WriteProbeTool(dir.Root);
        var       engine     = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        var       client     = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("write_probe", "{}"));
        client.Enqueue(FakeChatClient.Text("single done"));
        var approver = new FixedApprover(ApprovalResolution.Granted(GrantScope.SingleOperation));

        var recorder = new CollectingRecorder();
        var loop = new AgentLoop(client, new ToolRegistry([writeProbe]), Options(), engine, approver, recorder);
        var result = await loop.RunAsync("sys", "write outside once", CancellationToken.None);

        // 审批卡的 SingleOperation 批准经派发凭据授权（不入登记表）：无 granted/consumed grant 事件，
        // 其审计面是 tool.permission 记录；登记表授权事件矩阵不覆盖该形态。
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
            Assert.That(writeProbe.ExecuteCount, Is.EqualTo(1));
            Assert.That(recorder.Events, Is.Empty,
                        "单次批准授权凭据形态不得产生登记表授权事件。");
        });
    }

    // ---- 空闲边界：模式切换撤销 → revoked 事件直写日志 ----

    [Test]
    public async Task ApprovalModeSwitch_PersistsRevokedEvents_AtTheIdleBoundary()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        // 预置活动授权：模式切换撤销全部授权，撤销事件经空闲边界 drain 直写日志。
        engine.Grants.Add(GrantScope.Session, new ToolPreparation
        {
            ToolName = "write_test", CallId = "seed", Arguments = new JsonObject(),
            Capability = "filesystem.write", Summary = "seed grant", TargetPaths = [dir.Root],
            ExecutionPolicy = "backend=windows-sandbox\npolicyVersion=6"
        });
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("idle"));
        using var host = NewHost(dir.Root, client, null, engine, new FixedApprover(null),
                                 id => new SessionStoreWriter(dir.Root, id));

        await host.ChangeApprovalModeAsync(SessionApprovalMode.AIAutoReview);
        host.Dispose();

        var records = await ReadGrantEventsAsync(dir.Root, host.Session.Id);
        Assert.That(records.Any(record => record.Event == "revoked" && record.Scope == "session"),
                    Is.True, "模式切换撤销活动授权必须落盘 revoked 事件。");
    }

    // ---- 迁移确认：authorization-changed + 拒绝记忆整段解除 ----

    [Test]
    public async Task MigrationConfirmation_ReleasesDenials_AndPersistsAuthorizationChanged()
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        var engine = new PermissionEngine(workspace.Root);
        // 预置拒绝记忆：迁移确认为可信授权变更，整段解除并落盘 authorization-changed。
        var denied = new ToolPreparation
        {
            ToolName = "write_test", CallId = "denied", Arguments = new JsonObject(),
            Capability = "filesystem.write", Summary = "denied once",
            SessionConstraint = "denied-identity", TargetPaths = [workspace.Root]
        };
        engine.RecordDenial(denied);
        Assert.That(engine.Evaluate(denied).Decision, Is.EqualTo(PermissionDecision.Deny));

        // 旧形状迁移夹具：legacy permission-selection 落盘 → 恢复宿主 → 确认迁移。
        var session = new ChatSession(Guid.NewGuid().ToString("N"), workspace.Root, Identity);
        var context = new ConversationContext();
        context.Append(ChatMessage.System(SystemPrompt));
        var selection = new LegacyPermissionSelection { Revision = 1, Mode = "ReadOnly" };
        var writer = new SessionStoreWriter(store.Root, session.Id);
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
            using var host = new ChatSessionHost(restored.Session, restored.Context, Options(),
                                                 new FakeChatClient(), new ToolRegistry([]), SystemPrompt,
                                                 engine, approver : null, sessionWriter : writer);
            await host.ConfirmPermissionMigrationAsync(SessionApprovalMode.HumanApproval);
        }
        finally
        {
            writer.Dispose();
        }

        Assert.Multiple(() =>
        {
            Assert.That(engine.Evaluate(denied).Decision, Is.EqualTo(PermissionDecision.Ask),
                        "迁移确认必须整段解除拒绝记忆。");
            var records = ReadGrantEvents(store.Root, session.Id);
            Assert.That(records.Any(record => record.Event == "authorization-changed" &&
                                              record.Reason == "permission migration confirmed" &&
                                              record.GrantId.Length == 0 && record.Scope == "none"),
                        Is.True, "迁移确认必须落盘 authorization-changed 审计事件。");
        });
    }

    // ---- drain 失败：DisplayWarning 呈现且不阻断收尾 ----

    [Test]
    public async Task DrainFailure_BecomesADisplayWarning_WithoutBlockingFinalization()
    {
        using var dir        = new TestTempDir();
        var       writeProbe = new WriteProbeTool(dir.Root);
        var       engine     = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        var       client     = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("write_probe", "{}"));
        client.Enqueue(FakeChatClient.Text("done despite audit failure"));
        var approver = new FixedApprover(ApprovalResolution.Granted(GrantScope.Turn));
        // recorder 在第二次 grant 事件落盘点（宿主运行终态）注入故障：轮边界 drain 正常落盘 granted，
        // 终态 expired 的 drain 失败转 DisplayWarning，运行收尾不被阻断。
        var recorder = new ThrowingGrantRecorder(throwOnCall : 2);
        using var host = NewHost(dir.Root, client, writeProbe, engine, approver,
                                 id => new SessionStoreWriter(dir.Root, id),
                                 recorder : recorder);

        var run = await host.SubmitTurnAsync("write outside", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed),
                        "审计失败不阻断运行收尾。");
            Assert.That(recorder.Calls, Is.GreaterThanOrEqualTo(1), "轮边界 drain 已正常落盘 granted。");
            Assert.That(host.DisplayWarning, Does.Contain("Grant-event audit is incomplete"),
                        "审计失败必须以警告如实呈现，不静默。");
        });
    }

    // ---- 夹具与工具 ----

    private static ChatSessionHost NewHost(string root, IChatCompletionClient client, ITool? tool,
                                           PermissionEngine? permissions, IApprovalProvider? approver,
                                           Func<string, ISessionStoreWriter>? sessionWriterFactory,
                                           IRunRecorder? recorder = null) =>
        new(Path.Combine(Path.GetTempPath(), "tinyharness-grant-event-tests"),
            Identity, Options(), client, new ToolRegistry(tool is null ? [] : [tool]), SystemPrompt,
            permissions, approver, null,
            recorder is null ? null : _ => recorder, sessionWriterFactory);

    /// <summary>工作区外写入探针：filesystem.write → 引擎按 L2 逐次审批。</summary>
    private sealed class WriteProbeTool(string workspaceRoot) : ITool
    {
        public int ExecuteCount { get; private set; }

        public ToolDefinition Definition { get; } = new()
        {
            Name        = "write_probe",
            Description = "Writes outside the workspace for grant-event coverage.",
            Parameters  = new JsonObject { ["type"] = "object" }
        };

        public ToolPreparation Prepare(ChatToolCall call) => new()
        {
            ToolName        = Definition.Name,
            CallId          = call.Id,
            Arguments       = new JsonObject(),
            Capability      = "filesystem.write",
            Summary         = "outside write probe",
            ExecutionPolicy = "backend=windows-sandbox\npolicyVersion=6",
            TargetPaths     = [Path.Combine(Path.GetDirectoryName(workspaceRoot)!, "grant-event-outside.txt")]
        };

        public Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            return Task.FromResult(new ToolResult { Succeeded = true, Content = "written" });
        }
    }

    private static async Task<PermissionGrantEventRecord[]> ReadGrantEventsAsync(string storeRoot,
        string sessionId)
    {
        var log = await File.ReadAllTextAsync(Path.Combine(storeRoot,
                                                           sessionId + SessionStoreWriter.LogFileSuffix));
        return ParseGrantEvents(log);
    }

    private static PermissionGrantEventRecord[] ReadGrantEvents(string storeRoot, string sessionId)
    {
        var log = File.ReadAllTextAsync(Path.Combine(storeRoot,
                                                     sessionId + SessionStoreWriter.LogFileSuffix))
                         .GetAwaiter().GetResult();
        return ParseGrantEvents(log);
    }

    private static PermissionGrantEventRecord[] ParseGrantEvents(string log) =>
        log.Split('\n', StringSplitOptions.RemoveEmptyEntries)
           .Select(line => JsonSerializer.Deserialize(line, SessionLogJsonContext.Default.SessionLogBatch)!)
           .SelectMany(batch => batch.Records)
           .Where(record => record.Kind == "permission-grant-event")
           .Select(record => record.Payload.Deserialize(PermissionPersistenceJsonContext.Default
                                                                          .PermissionGrantEventRecord)!)
           .ToArray();

    /// <summary>按脚本回复固定审批结论的审批器；null 表示自动拒绝（deny）。</summary>
    private sealed class FixedApprover(ApprovalResolution? resolution) : IApprovalProvider
    {
        public Task<ApprovalResolution> PromptAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(resolution ?? ApprovalResolution.Denied("denied by test"));
    }

    /// <summary>收集 grant 事件记录的 recorder（其余成员走默认实现之外的空实现）。</summary>
    private sealed class CollectingRecorder : IRunRecorder
    {
        public List<PermissionGrantEventRecord> Events { get; } = [];

        public Task StartAsync(string systemPrompt, string userInput, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordPreparedAsync(ToolPreparation preparation, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordPermissionAsync(ToolPreparation preparation, PermissionDecision decision,
                                          string outcome, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordResultAsync(ToolPreparation preparation, ToolResult result,
                                      CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordCompactionAsync(ContextChange change, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordModelUsageAsync(int? inputTokens, int? outputTokens, string? finishReason,
                                          CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CompleteAsync(AgentResult result, IReadOnlyList<ChatMessage> messages,
                                  CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordGrantEventsAsync(IReadOnlyList<PermissionGrantEventRecord> events,
                                           CancellationToken                         cancellationToken)
        {
            Events.AddRange(events);
            return Task.CompletedTask;
        }
    }

    /// <summary>在第 N 次 grant 事件落盘点抛出故障的 recorder（其余成员走空实现）。</summary>
    private sealed class ThrowingGrantRecorder(int throwOnCall) : IRunRecorder
    {
        private int _calls;

        public int Calls => _calls;

        public Task StartAsync(string systemPrompt, string userInput, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordPreparedAsync(ToolPreparation preparation, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordPermissionAsync(ToolPreparation preparation, PermissionDecision decision,
                                          string outcome, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordResultAsync(ToolPreparation preparation, ToolResult result,
                                      CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordCompactionAsync(ContextChange change, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordModelUsageAsync(int? inputTokens, int? outputTokens, string? finishReason,
                                          CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CompleteAsync(AgentResult result, IReadOnlyList<ChatMessage> messages,
                                  CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordGrantEventsAsync(IReadOnlyList<PermissionGrantEventRecord> events,
                                           CancellationToken                         cancellationToken)
        {
            _calls++;
            if (_calls == throwOnCall) throw new IOException("injected grant-event audit failure");
            return Task.CompletedTask;
        }
    }
}

using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         验证 Session/Turn/Run 生命周期、共享上下文、单一活动运行和宿主事件，
///         以及取消后的完整边界回退、副作用阻断与明确确认。
///     </para>
///     <para>
///         Verifies Session/Turn/Run lifecycles, shared context, a single active run and host events,
///         plus complete-boundary rewind, side-effect blocking and explicit acknowledgement after cancellation.
///     </para>
/// </summary>
public class ChatSessionTests
{
    private const string SystemPrompt = "You are a test harness inspecting a workspace.";

    private static AgentOptions Options(int maxSteps = 10) => new()
    {
        Model                     = "test-model",
        MaxAgentSteps             = maxSteps,
        DefaultToolTimeoutSeconds = 30
    };

    private static ChatSessionHost NewHost(IChatCompletionClient       client,
                                           ToolRegistry?               tools           = null,
                                           IHostEventSink?             events          = null,
                                           Func<string, IRunRecorder>? recorderFactory = null,
                                           int                         maxSteps        = 10) =>
        new(
            Path.Combine(Path.GetTempPath(), "tinyharness-session-tests"),
            new SessionModelIdentity("test-model", "https://example.invalid/v1",
                                     ChatApiKind.ChatCompletions),
            Options(maxSteps),
            client,
            tools ?? new ToolRegistry([]),
            SystemPrompt,
            events : events,
            recorderFactory : recorderFactory);

    [Test]
    public async Task SubmitTurn_TwoTurnsShareContextAndRecordLifecycle()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("first answer"));
        client.Enqueue(FakeChatClient.Text("second answer"));
        var host = NewHost(client);

        var run1 = await host.SubmitTurnAsync("what is this repo?", CancellationToken.None);
        var run2 = await host.SubmitTurnAsync("and why?", CancellationToken.None);

        // 运行仅记录一次由实际执行决定的终态。
        Assert.That(run1.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(run1.Result.FinalMessage, Is.EqualTo("first answer"));
        Assert.That(run1.IsFinished, Is.True);
        TestAssert.NotNull(run1.FinishedUtc);
        Assert.That(run1.FinishedUtc >= run1.StartedUtc, Is.True);
        Assert.That(run2.Result!.Status, Is.EqualTo(AgentStatus.Completed));

        var session = host.Session;
        Assert.That(session.Id, Is.Not.Empty);
        Assert.That(session.WorkspaceRoot, Is.Not.Empty);
        Assert.That(session.Model.ModelId, Is.EqualTo("test-model"));
        Assert.That(session.Turns.Count, Is.EqualTo(2));
        Assert.That(session.Turns[0].UserInput, Is.EqualTo("what is this repo?"));
        Assert.That(session.Turns[1].UserInput, Is.EqualTo("and why?"));
        Assert.That(session.Turns[0].Runs.Single(), Is.SameAs(run1));
        Assert.That(session.Turns[1].Runs.Single(), Is.SameAs(run2));
        Assert.That(run1.SessionId, Is.EqualTo(session.Id));
        Assert.That(run1.TurnId, Is.EqualTo(session.Turns[0].Id));
        Assert.That(run2.Id, Is.Not.EqualTo(run1.Id));

        // 后续请求保留旧回合且只有一条系统提示。
        Assert.That(client.RequestLog.Count, Is.EqualTo(2));
        var first = client.RequestLog[0].Messages;
        Assert.That(first.Select(m => m.Role).ToArray(), Is.EqualTo([ChatRole.System, ChatRole.User]));
        Assert.That(first[0].Content, Is.EqualTo(SystemPrompt));
        var second = client.RequestLog[1].Messages;
        Assert.That(second.Select(m => m.Role).ToArray(),
                    Is.EqualTo([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User]));
        Assert.That(second[1].Content, Is.EqualTo("what is this repo?"));
        Assert.That(second[2].Content, Is.EqualTo("first answer"));
        Assert.That(second[3].Content, Is.EqualTo("and why?"));
        Assert.That(second.Count(m => m.Role == ChatRole.System), Is.EqualTo(1));
    }

    [Test]
    public async Task SubmitTurn_PublishesCorrelatedOrderedEvents()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("one"));
        client.Enqueue(FakeChatClient.Text("two"));
        var sink = new TestHostEventSink();
        var host = NewHost(client, events : sink);

        var run1 = await host.SubmitTurnAsync("u1", CancellationToken.None);
        var run2 = await host.SubmitTurnAsync("u2", CancellationToken.None);

        var session = host.Session;
        // 每个运行依次发布开始、文本增量和结束事件。
        Assert.That(sink.Events.Count, Is.EqualTo(6));
        for (var i = 1; i < sink.Events.Count; i++)
            Assert.That(sink.Events[i].TimestampUtc >= sink.Events[i - 1].TimestampUtc, Is.True);

        // 各类事件均关联宿主身份，序号限定在当前运行。
        var first = sink.Events.Take(3).ToArray();
        Assert.That(first[0].Kind, Is.EqualTo(HostEventKind.RunStarted));
        Assert.That(first[1].Kind, Is.EqualTo(HostEventKind.AssistantTextDelta));
        Assert.That(first[2].Kind, Is.EqualTo(HostEventKind.RunFinished));
        Assert.That(first[0].Sequence, Is.EqualTo(1));
        Assert.That(first[2].Sequence, Is.EqualTo(3));
        TestAssert.All(first, @event => Assert.That(@event.SessionId, Is.EqualTo(session.Id)));
        TestAssert.All(first, @event => Assert.That(@event.TurnId, Is.EqualTo(session.Turns[0].Id)));
        TestAssert.All(first, @event => Assert.That(@event.RunId, Is.EqualTo(run1.Id)));
        Assert.That(TestAssert.TypeOf<string>(first[1].Payload), Is.EqualTo("one"));
        Assert.That(first[2].Payload, Is.SameAs(run1.Result));

        // 后续运行复用身份关联规则，但独立计数事件序号。
        var second = sink.Events.Skip(3).ToArray();
        Assert.That(second[0].Kind, Is.EqualTo(HostEventKind.RunStarted));
        Assert.That(second[1].Kind, Is.EqualTo(HostEventKind.AssistantTextDelta));
        Assert.That(second[2].Kind, Is.EqualTo(HostEventKind.RunFinished));
        Assert.That(second[0].Sequence, Is.EqualTo(1));
        Assert.That(second[2].Sequence, Is.EqualTo(3));
        Assert.That(second[0].TurnId, Is.EqualTo(session.Turns[1].Id));
        Assert.That(second[0].RunId, Is.EqualTo(run2.Id));
        Assert.That(second[0].SessionId, Is.EqualTo(session.Id));
        TestAssert.All(second, @event => Assert.That(@event.RunId, Is.EqualTo(run2.Id)));
    }

    [Test]
    public async Task SubmitTurn_SecondConcurrentSubmitIsRejectedWithoutRecords()
    {
        var client = new GateChatClient();
        var host   = NewHost(client);

        var first = host.SubmitTurnAsync("first", CancellationToken.None);
        await client.WaitStartedAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        Assert.That(host.HasActiveRun, Is.True);

        var rejected =
            await TestAssert.ThrowsAsync<InvalidOperationException>(() =>
                                                                        host.SubmitTurnAsync("second",
                                                                            CancellationToken.None));
        Assert.That(rejected.Message, Does.Contain("active run"));

        client.Open();
        var run1 = await first;
        Assert.That(run1.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(host.HasActiveRun, Is.False);

        // 拒绝并发提交不产生记录，也不妨碍后续回合。
        TestAssert.Single(host.Session.Turns);
        var run2 = await host.SubmitTurnAsync("third", CancellationToken.None);
        Assert.That(run2.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(host.Session.Turns.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task SubmitTurn_CancelBeforeToolCalls_StillAllowsNextTurn()
    {
        var       client = new GateChatClient();
        using var cts    = new CancellationTokenSource();
        var       host   = NewHost(client);

        var first = host.SubmitTurnAsync("first", cts.Token);
        await client.WaitStartedAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        cts.Cancel();
        var run1 = await first;
        Assert.That(run1.Result!.Status, Is.EqualTo(AgentStatus.Cancelled));
        Assert.That(run1.IsFinished, Is.True);

        // 没有待完成工具调用时，取消后可启动新运行；不得重启旧运行。
        client.Open();
        var run2 = await host.SubmitTurnAsync("second", CancellationToken.None);
        Assert.That(run2.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(host.Session.Turns.Count, Is.EqualTo(2));
        Assert.That(client.Requests, Is.EqualTo(2));
    }

    [Test]
    public async Task SubmitTurn_CancelDuringToolExecution_RewindsTheHistoryAndBlocksSideEffects()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("slow", "{}"));
        client.Enqueue(FakeChatClient.Text("recovered"));
        var       tool = new GateTool();
        using var cts  = new CancellationTokenSource();
        var       host = NewHost(client, new ToolRegistry([tool]));

        var first = host.SubmitTurnAsync("go", cts.Token);
        await tool.WaitStartedAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        cts.Cancel();
        var run1 = await first;
        Assert.That(run1.Result!.Status, Is.EqualTo(AgentStatus.Cancelled));

        // 可能已执行的副作用保持结果未知，并触发检查阻断，不补造结果。
        var record = TestAssert.Single(run1.Result!.ToolCalls!);
        Assert.That(record.CallId, Is.EqualTo("call_1"));
        Assert.That(record.State, Is.EqualTo(ToolCallExecutionState.UnknownResult));
        Assert.That(record.SideEffects, Is.True);
        Assert.That(host.InspectionPending, Is.True);
        TestAssert.Contains("unknown results", host.InspectionPendingNote, StringComparison.Ordinal);

        // 后续回合从完整边界继续，不重放工具或伪造工具结果。
        var run2 = await host.SubmitTurnAsync("second", CancellationToken.None);
        Assert.That(run2.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(client.Requests, Is.EqualTo(2));
        Assert.That(tool.ExecuteCount, Is.EqualTo(1));
        Assert.That(host.Session.Turns.Count, Is.EqualTo(2));
        var sent = client.RequestLog[1].Messages;
        TestAssert.ContainsAll(m => m.Role == ChatRole.User && m.Content == "go", sent);
        TestAssert.ContainsAll(m => m.Role == ChatRole.User && m.Content == "second", sent);
        TestAssert.DoesNotContainAny(m => m.ToolCalls is { Count: > 0 }, sent);
        TestAssert.DoesNotContainAny(m => m.Role == ChatRole.Tool, sent);
    }

    [Test]
    public async Task SubmitTurn_WithoutPermissions_UnknownSideEffectDeniesTheNextWriteUntilAcknowledged()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("slow", "{}"));
        client.Enqueue(FakeChatClient.ToolCall("write", "{}", "call_denied"));
        client.Enqueue(FakeChatClient.Text("write was blocked"));
        client.Enqueue(FakeChatClient.ToolCall("write", "{}", "call_allowed"));
        client.Enqueue(FakeChatClient.Text("write completed"));
        var       slow  = new GateTool("filesystem.write");
        var       write = new FakeTool("write");
        using var cts   = new CancellationTokenSource();
        using var wait  = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var host  = NewHost(client, new ToolRegistry([slow, write]));

        // 未提供权限引擎且无检查阻断时，保留直接执行的宿主兼容行为。
        var active = host.SubmitTurnAsync("start a write", cts.Token);
        try
        {
            await slow.WaitStartedAsync(wait.Token);
        }
        finally
        {
            cts.Cancel();
            await active;
        }

        Assert.That((await active).Result!.Status, Is.EqualTo(AgentStatus.Cancelled));
        Assert.That(slow.ExecuteCount, Is.EqualTo(1));
        Assert.That(host.InspectionPending, Is.True);
        var blocked = await host.SubmitTurnAsync("try another write", CancellationToken.None);
        Assert.That(blocked.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(blocked.Result.ToolExecutions, Is.EqualTo(0));
        Assert.That(write.ExecuteCount, Is.EqualTo(0));
        var denied = TestAssert.Single(blocked.Result.ToolCalls!);
        Assert.That(denied.State, Is.EqualTo(ToolCallExecutionState.KnownNotStarted));
        TestAssert.Contains("pending inspection", denied.Approval, StringComparison.Ordinal);
        Assert.That(host.InspectionPending, Is.True);
        var denialMessage = client.RequestLog[2].Messages.Single(m => m.Role == ChatRole.Tool);
        Assert.That(denialMessage.ToolCallId, Is.EqualTo("call_denied"));
        TestAssert.Contains("Blocked pending inspection", denialMessage.Content, StringComparison.Ordinal);

        // 确认仅解除检查阻断；未提供权限引擎的宿主仍可直接执行新调用。
        await host.AcknowledgeInspectionAsync(CancellationToken.None);
        Assert.That(host.InspectionPending, Is.False);
        var allowed = await host.SubmitTurnAsync("write after inspection", CancellationToken.None);
        Assert.That(allowed.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(write.ExecuteCount, Is.EqualTo(1));
        Assert.That(TestAssert.Single(allowed.Result.ToolCalls!).State,
                    Is.EqualTo(ToolCallExecutionState.ConfirmedResult));
    }

    [Test]
    public async Task SubmitTurn_CancelDuringAReadOnlyTool_ContinuesWithoutBlocking()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("slow", "{}"));
        client.Enqueue(FakeChatClient.Text("recovered"));
        var       tool = new GateTool("filesystem.read");
        using var cts  = new CancellationTokenSource();
        var       host = NewHost(client, new ToolRegistry([tool]));

        var first = host.SubmitTurnAsync("go", cts.Token);
        await tool.WaitStartedAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        cts.Cancel();
        var run1 = await first;
        Assert.That(run1.Result!.Status, Is.EqualTo(AgentStatus.Cancelled));

        // 只读调用中断只回退历史，不触发副作用阻断。
        var record = TestAssert.Single(run1.Result!.ToolCalls!);
        Assert.That(record.State, Is.EqualTo(ToolCallExecutionState.UnknownResult));
        Assert.That(record.SideEffects, Is.False);
        Assert.That(host.InspectionPending, Is.False);

        var run2 = await host.SubmitTurnAsync("second", CancellationToken.None);
        Assert.That(run2.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(client.Requests, Is.EqualTo(2));
    }

    [Test]
    public async Task SubmitTurn_CancelDuringApproval_LeavesEveryCallNotStartedAndContinues()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("slow", "{}"));
        client.Enqueue(FakeChatClient.Text("recovered"));
        var       tool     = new GateTool();
        var       approver = new GateApprover();
        using var cts      = new CancellationTokenSource();
        using var ws       = new TestTempDir();
        var       engine   = new PermissionEngine(ws.Root);
        var host = new ChatSessionHost(ws.Root,
                                       new SessionModelIdentity("test-model", "https://example.invalid/v1",
                                                                ChatApiKind.ChatCompletions),
                                       Options(),
                                       client,
                                       new ToolRegistry([tool]),
                                       SystemPrompt,
                                       engine,
                                       approver);

        var first = host.SubmitTurnAsync("go", cts.Token);
        await approver.WaitStartedAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        cts.Cancel();
        var run1 = await first;
        Assert.That(run1.Result!.Status, Is.EqualTo(AgentStatus.Cancelled));
        Assert.That(tool.ExecuteCount, Is.EqualTo(0));

        // 审批等待中取消属于已知未开始，不触发检查阻断。
        var record = TestAssert.Single(run1.Result!.ToolCalls!);
        Assert.That(record.State, Is.EqualTo(ToolCallExecutionState.KnownNotStarted));
        Assert.That(record.SideEffects, Is.True);
        Assert.That(record.Approval, Is.Null);
        Assert.That(host.InspectionPending, Is.False);
        Assert.That(engine.NonReadOnlyBlockReason, Is.Null);

        var run2 = await host.SubmitTurnAsync("second", CancellationToken.None);
        Assert.That(run2.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(client.Requests, Is.EqualTo(2));
    }

    [Test]
    public async Task AcknowledgeInspection_ClearsTheBlockAndPersistsTheAcknowledgement()
    {
        using var ws     = new TestTempDir();
        using var dir    = new TestTempDir();
        var       client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("slow", "{}"));
        var       tool   = new GateTool();
        using var cts    = new CancellationTokenSource();
        var       engine = new PermissionEngine(ws.Root);
        var host = new ChatSessionHost(ws.Root,
                                       new SessionModelIdentity("test-model", "https://example.invalid/v1",
                                                                ChatApiKind.ChatCompletions),
                                       Options(),
                                       client,
                                       new ToolRegistry([tool]),
                                       SystemPrompt,
                                       engine,
                                       new InlineApprover(ApprovalAction.AllowOnce),
                                       sessionWriterFactory : id => new SessionStoreWriter(dir.Root, id));

        var first = host.SubmitTurnAsync("go", cts.Token);
        await tool.WaitStartedAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        cts.Cancel();
        var run1 = await first;
        Assert.That(run1.Result!.Status, Is.EqualTo(AgentStatus.Cancelled));
        Assert.That(host.InspectionPending, Is.True);
        TestAssert.NotNull(engine.NonReadOnlyBlockReason);

        // 明确确认须落盘，避免恢复时再次阻断同一中断。
        await host.AcknowledgeInspectionAsync(CancellationToken.None);
        Assert.That(host.InspectionPending, Is.False);
        Assert.That(engine.NonReadOnlyBlockReason, Is.Null);

        var snapshot = await SessionStoreReader.ReadAsync(dir.Root, host.Session.Id, CancellationToken.None);
        TestAssert.NotNull(snapshot);
        TestAssert.NotNull(snapshot!.InspectionAcknowledgedUtc);
        var storedRun = snapshot.Turns.Single().Runs.Single();
        Assert.That(storedRun.NeedsInspection, Is.True);
        var stored = TestAssert.Single(storedRun.ToolCalls!);
        Assert.That(stored.State, Is.EqualTo(nameof(ToolCallExecutionState.UnknownResult)));
        Assert.That(stored.SideEffects, Is.True);

        // canonical 历史只提交完整边界，不保存中断工具组。
        var roles = snapshot.Conversation!.Messages.Select(message => message.Role).ToArray();
        Assert.That(roles, Is.EqualTo(["System", "User"]));
    }

    [Test]
    public async Task SubmitTurn_PersistsExecutionRecordsRedactedForInspection()
    {
        using var    ws     = new TestTempDir();
        using var    dir    = new TestTempDir();
        const string secret = "sk-test-secret-value";
        var          client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("slow", "{}"));
        client.Enqueue(FakeChatClient.Text("done"));
        var tool = new FakeTool("slow", summary : $"patch({secret})");
        var host = new ChatSessionHost(ws.Root,
                                       new SessionModelIdentity("test-model", "https://example.invalid/v1",
                                                                ChatApiKind.ChatCompletions),
                                       Options(),
                                       client,
                                       new ToolRegistry([tool]),
                                       SystemPrompt,
                                       sessionWriterFactory : id =>
                                           new SessionStoreWriter(dir.Root, id,
                                                                  new Dictionary<string, string>(StringComparer.Ordinal)
                                                                      { ["MY_API_KEY"] = secret }));

        var run = await host.SubmitTurnAsync($"use {secret}", CancellationToken.None);
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));

        var snapshot = await SessionStoreReader.ReadAsync(dir.Root, host.Session.Id, CancellationToken.None);
        var stored   = TestAssert.Single(snapshot!.Turns.Single().Runs.Single().ToolCalls!);
        // 检查投影脱敏，canonical 用户内容保持原样。
        Assert.That(stored.Summary, Is.EqualTo("patch([REDACTED])"));
        Assert.That(Enum.Parse<ToolCallExecutionState>(stored.State),
                    Is.EqualTo(ToolCallExecutionState.ConfirmedResult));
        Assert.That(snapshot.Turns.Single().Runs.Single().NeedsInspection, Is.False);
        TestAssert.Contains(secret, snapshot.Conversation!.Messages.Single(m => m.Role == "User").Content,
                            StringComparison.Ordinal);

        host.Dispose();
        using var reopened = await SessionStoreWriter.OpenExistingAsync(dir.Root, host.Session.Id, null,
                                                                        CancellationToken.None);
        var restoration = SessionResume.Restore(reopened.LoadedSnapshot!, ws.Root,
                                                new SessionModelIdentity("test-model", "https://example.invalid/v1",
                                                                         ChatApiKind.ChatCompletions),
                                                SystemPrompt);
        Assert.That(restoration.NeedsInspection, Is.Null);
    }

    [Test]
    public async Task SubmitTurn_CreatesOneRecorderPerRunWithTheRunId()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("one"));
        client.Enqueue(FakeChatClient.Text("two"));
        var created = new List<(string RunId, CapturingRunRecorder Recorder)>();
        var host = NewHost(client, recorderFactory : runId =>
        {
            var recorder = new CapturingRunRecorder();
            created.Add((runId, recorder));
            return recorder;
        });

        var run1 = await host.SubmitTurnAsync("u1", CancellationToken.None);
        var run2 = await host.SubmitTurnAsync("u2", CancellationToken.None);

        Assert.That(created.Select(entry => entry.RunId).ToArray(), Is.EqualTo([run1.Id, run2.Id]));
        TestAssert.All(created, entry => TestAssert.Single(entry.Recorder.CompletedResults));
    }

    [Test]
    public async Task SubmitTurn_BlankInputIsRejectedWithoutRecords()
    {
        var host = NewHost(new FakeChatClient());

        await TestAssert.ThrowsAsync<ArgumentException>(() => host.SubmitTurnAsync("   ", CancellationToken.None));

        Assert.That(host.Session.Turns, Is.Empty);
    }

    [Test]
    public void Constructor_ModelIdentityMismatchIsRejected()
    {
        var options = Options() with { Model = "other-model" };

        TestAssert.Throws<ArgumentException>(() => new ChatSessionHost(Path.Combine(Path.GetTempPath(),
                                                                           "tinyharness-session-tests"),
                                                                       new SessionModelIdentity("test-model",
                                                                           "https://example.invalid/v1",
                                                                           ChatApiKind.ChatCompletions),
                                                                       options,
                                                                       new FakeChatClient(),
                                                                       new ToolRegistry([]),
                                                                       SystemPrompt));
    }

    [Test]
    public void SessionRun_DoubleFinishIsRejected()
    {
        var run = new SessionRun("run-1", "session-1", "turn-1");
        Assert.That(run.IsFinished, Is.False);

        run.Finish(new AgentResult { Status = AgentStatus.Completed });

        Assert.That(run.IsFinished, Is.True);
        TestAssert.Throws<InvalidOperationException>(() => run.Finish(new AgentResult { Status = AgentStatus.Failed }));
    }

    /// <summary>
    ///     <para>报告执行开始后等待取消；可配置能力以区分只读调用与副作用调用。</para>
    ///     <para>
    ///         Reports execution start, then waits for cancellation; configurable capabilities distinguish reads from side
    ///         effects.
    ///     </para>
    /// </summary>
    private sealed class GateTool(string capability = "test.slow", string? summary = null) : ITool
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExecuteCount { get; private set; }

        public ToolDefinition Definition { get; } = new()
        {
            Name        = "slow",
            Description = "Gate tool for mid-execution cancellation",
            Parameters  = new JsonObject { ["type"] = "object" }
        };

        public ToolPreparation Prepare(ChatToolCall call) => new()
        {
            ToolName   = Definition.Name,
            CallId     = call.Id,
            Arguments  = new JsonObject(),
            Capability = capability,
            Summary    = summary ?? $"{Definition.Name}()"
        };

        public async Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new ToolResult { Succeeded = true, Content = "never reached" };
        }

        public Task WaitStartedAsync(CancellationToken cancellationToken) => _started.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    ///     <para>进入审批提示后等待取消，用于模拟审批等待中取消。</para>
    ///     <para>Waits for cancellation inside the approval prompt to simulate cancellation while approval is pending.</para>
    /// </summary>
    private sealed class GateApprover : IApprovalProvider
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return ApprovalAction.Deny;
        }

        public Task WaitStartedAsync(CancellationToken cancellationToken) => _started.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    ///     <para>每次审批均返回同一预置选择。</para>
    ///     <para>Returns the same preset choice for every approval prompt.</para>
    /// </summary>
    private sealed class InlineApprover(ApprovalAction action) : IApprovalProvider
    {
        public Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken) =>
            Task.FromResult(action);
    }

    /// <summary>
    ///     <para>捕获运行完成结果；其余记录方法不执行操作。</para>
    ///     <para>Captures completed run results; all other recorder methods are no-ops.</para>
    /// </summary>
    private sealed class CapturingRunRecorder : IRunRecorder
    {
        public List<AgentResult> CompletedResults { get; } = [];

        public Task StartAsync(string systemPrompt, string userInput, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordPreparedAsync(ToolPreparation preparation, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordPermissionAsync(
            ToolPreparation   preparation, PermissionDecision decision, string outcome,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordResultAsync(
            ToolPreparation preparation, ToolResult result, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordCompactionAsync(ContextChange change, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordModelUsageAsync(
            int? inputTokens, int? outputTokens, string? finishReason, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CompleteAsync(
            AgentResult       result, IReadOnlyList<ChatMessage> messages, StructuredState state,
            CancellationToken cancellationToken)
        {
            CompletedResults.Add(result);
            return Task.CompletedTask;
        }
    }
}

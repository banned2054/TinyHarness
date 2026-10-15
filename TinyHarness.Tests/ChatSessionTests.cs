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

    [Fact]
    public async Task SubmitTurn_TwoTurnsShareContextAndRecordLifecycle()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("first answer"));
        client.Enqueue(FakeChatClient.Text("second answer"));
        var host = NewHost(client);

        var run1 = await host.SubmitTurnAsync("what is this repo?", CancellationToken.None);
        var run2 = await host.SubmitTurnAsync("and why?", CancellationToken.None);

        // 运行仅记录一次由实际执行决定的终态。
        Assert.Equal(AgentStatus.Completed, run1.Result!.Status);
        Assert.Equal("first answer", run1.Result.FinalMessage);
        Assert.True(run1.IsFinished);
        Assert.NotNull(run1.FinishedUtc);
        Assert.True(run1.FinishedUtc >= run1.StartedUtc);
        Assert.Equal(AgentStatus.Completed, run2.Result!.Status);

        var session = host.Session;
        Assert.NotEmpty(session.Id);
        Assert.NotEmpty(session.WorkspaceRoot);
        Assert.Equal("test-model", session.Model.ModelId);
        Assert.Equal(2, session.Turns.Count);
        Assert.Equal("what is this repo?", session.Turns[0].UserInput);
        Assert.Equal("and why?", session.Turns[1].UserInput);
        Assert.Same(run1, session.Turns[0].Runs.Single());
        Assert.Same(run2, session.Turns[1].Runs.Single());
        Assert.Equal(session.Id, run1.SessionId);
        Assert.Equal(session.Turns[0].Id, run1.TurnId);
        Assert.NotEqual(run1.Id, run2.Id);

        // 后续请求保留旧回合且只有一条系统提示。
        Assert.Equal(2, client.RequestLog.Count);
        var first = client.RequestLog[0].Messages;
        Assert.Equal([ChatRole.System, ChatRole.User], first.Select(m => m.Role).ToArray());
        Assert.Equal(SystemPrompt, first[0].Content);
        var second = client.RequestLog[1].Messages;
        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User],
                     second.Select(m => m.Role).ToArray());
        Assert.Equal("what is this repo?", second[1].Content);
        Assert.Equal("first answer", second[2].Content);
        Assert.Equal("and why?", second[3].Content);
        Assert.Equal(1, second.Count(m => m.Role == ChatRole.System));
    }

    [Fact]
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
        Assert.Equal(6, sink.Events.Count);
        for (var i = 1; i < sink.Events.Count; i++)
            Assert.True(sink.Events[i].TimestampUtc >= sink.Events[i - 1].TimestampUtc);

        // 各类事件均关联宿主身份，序号限定在当前运行。
        var first = sink.Events.Take(3).ToArray();
        Assert.Equal(HostEventKind.RunStarted, first[0].Kind);
        Assert.Equal(HostEventKind.AssistantTextDelta, first[1].Kind);
        Assert.Equal(HostEventKind.RunFinished, first[2].Kind);
        Assert.Equal(1, first[0].Sequence);
        Assert.Equal(3, first[2].Sequence);
        Assert.All(first, @event => Assert.Equal(session.Id, @event.SessionId));
        Assert.All(first, @event => Assert.Equal(session.Turns[0].Id, @event.TurnId));
        Assert.All(first, @event => Assert.Equal(run1.Id, @event.RunId));
        Assert.Equal("one", Assert.IsType<string>(first[1].Payload));
        Assert.Same(run1.Result, first[2].Payload);

        // 后续运行复用身份关联规则，但独立计数事件序号。
        var second = sink.Events.Skip(3).ToArray();
        Assert.Equal(HostEventKind.RunStarted, second[0].Kind);
        Assert.Equal(HostEventKind.AssistantTextDelta, second[1].Kind);
        Assert.Equal(HostEventKind.RunFinished, second[2].Kind);
        Assert.Equal(1, second[0].Sequence);
        Assert.Equal(3, second[2].Sequence);
        Assert.Equal(session.Turns[1].Id, second[0].TurnId);
        Assert.Equal(run2.Id, second[0].RunId);
        Assert.Equal(session.Id, second[0].SessionId);
        Assert.All(second, @event => Assert.Equal(run2.Id, @event.RunId));
    }

    [Fact]
    public async Task SubmitTurn_SecondConcurrentSubmitIsRejectedWithoutRecords()
    {
        var client = new GateChatClient();
        var host   = NewHost(client);

        var first = host.SubmitTurnAsync("first", CancellationToken.None);
        await client.WaitStartedAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        Assert.True(host.HasActiveRun);

        var rejected =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                    host.SubmitTurnAsync("second",
                                                                             CancellationToken.None));
        Assert.Contains("active run", rejected.Message);

        client.Open();
        var run1 = await first;
        Assert.Equal(AgentStatus.Completed, run1.Result!.Status);
        Assert.False(host.HasActiveRun);

        // 拒绝并发提交不产生记录，也不妨碍后续回合。
        Assert.Single(host.Session.Turns);
        var run2 = await host.SubmitTurnAsync("third", CancellationToken.None);
        Assert.Equal(AgentStatus.Completed, run2.Result!.Status);
        Assert.Equal(2, host.Session.Turns.Count);
    }

    [Fact]
    public async Task SubmitTurn_CancelBeforeToolCalls_StillAllowsNextTurn()
    {
        var       client = new GateChatClient();
        using var cts    = new CancellationTokenSource();
        var       host   = NewHost(client);

        var first = host.SubmitTurnAsync("first", cts.Token);
        await client.WaitStartedAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        cts.Cancel();
        var run1 = await first;
        Assert.Equal(AgentStatus.Cancelled, run1.Result!.Status);
        Assert.True(run1.IsFinished);

        // 没有待完成工具调用时，取消后可启动新运行；不得重启旧运行。
        client.Open();
        var run2 = await host.SubmitTurnAsync("second", CancellationToken.None);
        Assert.Equal(AgentStatus.Completed, run2.Result!.Status);
        Assert.Equal(2, host.Session.Turns.Count);
        Assert.Equal(2, client.Requests);
    }

    [Fact]
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
        Assert.Equal(AgentStatus.Cancelled, run1.Result!.Status);

        // 可能已执行的副作用保持结果未知，并触发检查阻断，不补造结果。
        var record = Assert.Single(run1.Result!.ToolCalls!);
        Assert.Equal("call_1", record.CallId);
        Assert.Equal(ToolCallExecutionState.UnknownResult, record.State);
        Assert.True(record.SideEffects);
        Assert.True(host.InspectionPending);
        Assert.Contains("unknown results", host.InspectionPendingNote, StringComparison.Ordinal);

        // 后续回合从完整边界继续，不重放工具或伪造工具结果。
        var run2 = await host.SubmitTurnAsync("second", CancellationToken.None);
        Assert.Equal(AgentStatus.Completed, run2.Result!.Status);
        Assert.Equal(2, client.Requests);
        Assert.Equal(1, tool.ExecuteCount);
        Assert.Equal(2, host.Session.Turns.Count);
        var sent = client.RequestLog[1].Messages;
        Assert.Contains(sent, m => m.Role == ChatRole.User && m.Content == "go");
        Assert.Contains(sent, m => m.Role == ChatRole.User && m.Content == "second");
        Assert.DoesNotContain(sent, m => m.ToolCalls is { Count: > 0 });
        Assert.DoesNotContain(sent, m => m.Role == ChatRole.Tool);
    }

    [Fact]
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

        Assert.Equal(AgentStatus.Cancelled, (await active).Result!.Status);
        Assert.Equal(1, slow.ExecuteCount);
        Assert.True(host.InspectionPending);
        var blocked = await host.SubmitTurnAsync("try another write", CancellationToken.None);
        Assert.Equal(AgentStatus.Completed, blocked.Result!.Status);
        Assert.Equal(0, blocked.Result.ToolExecutions);
        Assert.Equal(0, write.ExecuteCount);
        var denied = Assert.Single(blocked.Result.ToolCalls!);
        Assert.Equal(ToolCallExecutionState.KnownNotStarted, denied.State);
        Assert.Contains("pending inspection", denied.Approval, StringComparison.Ordinal);
        Assert.True(host.InspectionPending);
        var denialMessage = client.RequestLog[2].Messages.Single(m => m.Role == ChatRole.Tool);
        Assert.Equal("call_denied", denialMessage.ToolCallId);
        Assert.Contains("Blocked pending inspection", denialMessage.Content, StringComparison.Ordinal);

        // 确认仅解除检查阻断；未提供权限引擎的宿主仍可直接执行新调用。
        await host.AcknowledgeInspectionAsync(CancellationToken.None);
        Assert.False(host.InspectionPending);
        var allowed = await host.SubmitTurnAsync("write after inspection", CancellationToken.None);
        Assert.Equal(AgentStatus.Completed, allowed.Result!.Status);
        Assert.Equal(1, write.ExecuteCount);
        Assert.Equal(ToolCallExecutionState.ConfirmedResult, Assert.Single(allowed.Result.ToolCalls!).State);
    }

    [Fact]
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
        Assert.Equal(AgentStatus.Cancelled, run1.Result!.Status);

        // 只读调用中断只回退历史，不触发副作用阻断。
        var record = Assert.Single(run1.Result!.ToolCalls!);
        Assert.Equal(ToolCallExecutionState.UnknownResult, record.State);
        Assert.False(record.SideEffects);
        Assert.False(host.InspectionPending);

        var run2 = await host.SubmitTurnAsync("second", CancellationToken.None);
        Assert.Equal(AgentStatus.Completed, run2.Result!.Status);
        Assert.Equal(2, client.Requests);
    }

    [Fact]
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
        Assert.Equal(AgentStatus.Cancelled, run1.Result!.Status);
        Assert.Equal(0, tool.ExecuteCount);

        // 审批等待中取消属于已知未开始，不触发检查阻断。
        var record = Assert.Single(run1.Result!.ToolCalls!);
        Assert.Equal(ToolCallExecutionState.KnownNotStarted, record.State);
        Assert.True(record.SideEffects);
        Assert.Null(record.Approval);
        Assert.False(host.InspectionPending);
        Assert.Null(engine.NonReadOnlyBlockReason);

        var run2 = await host.SubmitTurnAsync("second", CancellationToken.None);
        Assert.Equal(AgentStatus.Completed, run2.Result!.Status);
        Assert.Equal(2, client.Requests);
    }

    [Fact]
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
        Assert.Equal(AgentStatus.Cancelled, run1.Result!.Status);
        Assert.True(host.InspectionPending);
        Assert.NotNull(engine.NonReadOnlyBlockReason);

        // 明确确认须落盘，避免恢复时再次阻断同一中断。
        await host.AcknowledgeInspectionAsync(CancellationToken.None);
        Assert.False(host.InspectionPending);
        Assert.Null(engine.NonReadOnlyBlockReason);

        var snapshot = await SessionStoreReader.ReadAsync(dir.Root, host.Session.Id, CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot!.InspectionAcknowledgedUtc);
        var storedRun = snapshot.Turns.Single().Runs.Single();
        Assert.True(storedRun.NeedsInspection);
        var stored = Assert.Single(storedRun.ToolCalls!);
        Assert.Equal(nameof(ToolCallExecutionState.UnknownResult), stored.State);
        Assert.True(stored.SideEffects);

        // canonical 历史只提交完整边界，不保存中断工具组。
        var roles = snapshot.Conversation!.Messages.Select(message => message.Role).ToArray();
        Assert.Equal(["System", "User"], roles);
    }

    [Fact]
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
        Assert.Equal(AgentStatus.Completed, run.Result!.Status);

        var snapshot = await SessionStoreReader.ReadAsync(dir.Root, host.Session.Id, CancellationToken.None);
        var stored   = Assert.Single(snapshot!.Turns.Single().Runs.Single().ToolCalls!);
        // 检查投影脱敏，canonical 用户内容保持原样。
        Assert.Equal("patch([REDACTED])", stored.Summary);
        Assert.Equal(ToolCallExecutionState.ConfirmedResult,
                     Enum.Parse<ToolCallExecutionState>(stored.State));
        Assert.False(snapshot.Turns.Single().Runs.Single().NeedsInspection);
        Assert.Contains(secret, snapshot.Conversation!.Messages.Single(m => m.Role == "User").Content,
                        StringComparison.Ordinal);
    }

    [Fact]
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

        Assert.Equal([run1.Id, run2.Id], created.Select(entry => entry.RunId).ToArray());
        Assert.All(created, entry => Assert.Single(entry.Recorder.CompletedResults));
    }

    [Fact]
    public async Task SubmitTurn_BlankInputIsRejectedWithoutRecords()
    {
        var host = NewHost(new FakeChatClient());

        await Assert.ThrowsAsync<ArgumentException>(() => host.SubmitTurnAsync("   ", CancellationToken.None));

        Assert.Empty(host.Session.Turns);
    }

    [Fact]
    public void Constructor_ModelIdentityMismatchIsRejected()
    {
        var options = Options() with { Model = "other-model" };

        Assert.Throws<ArgumentException>(() => new ChatSessionHost(Path.Combine(Path.GetTempPath(),
                                                                                    "tinyharness-session-tests"),
                                                                   new SessionModelIdentity("test-model",
                                                                            "https://example.invalid/v1",
                                                                            ChatApiKind.ChatCompletions),
                                                                   options,
                                                                   new FakeChatClient(),
                                                                   new ToolRegistry([]),
                                                                   SystemPrompt));
    }

    [Fact]
    public void SessionRun_DoubleFinishIsRejected()
    {
        var run = new SessionRun("run-1", "session-1", "turn-1");
        Assert.False(run.IsFinished);

        run.Finish(new AgentResult { Status = AgentStatus.Completed });

        Assert.True(run.IsFinished);
        Assert.Throws<InvalidOperationException>(() => run.Finish(new AgentResult { Status = AgentStatus.Failed }));
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

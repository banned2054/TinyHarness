using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     M12.1 会话生命周期测试：Session/Turn/Run 记录、跨回合共享上下文、单一活动运行、
///     取消后的安全边界（不完整工具组拒绝续聊）与宿主事件契约（PLAN §5/§6/§10 M12.1）。
///     M12.1 session lifecycle tests: Session/Turn/Run records, context shared across
///     turns, the single-active-run rule, the post-cancel safety boundary (incomplete
///     tool rounds refuse continuation) and the host event contract
///     (PLAN §5/§6/§10 M12.1).
/// </summary>
public class ChatSessionTests
{
    private const string SystemPrompt = "You are a test harness inspecting a workspace.";

    private static AgentOptions Options(int maxSteps = 10)
    {
        return new AgentOptions
        {
            Model                     = "test-model",
            MaxAgentSteps             = maxSteps,
            DefaultToolTimeoutSeconds = 30
        };
    }

    private static ChatSessionHost NewHost(IChatCompletionClient       client,
                                           ToolRegistry?               tools           = null,
                                           IHostEventSink?             events          = null,
                                           Func<string, IRunRecorder>? recorderFactory = null,
                                           int                         maxSteps        = 10)
    {
        return new ChatSessionHost(
                                   Path.Combine(Path.GetTempPath(), "tinyharness-session-tests"),
                                   new SessionModelIdentity("test-model", "https://example.invalid/v1",
                                                            ChatApiKind.ChatCompletions),
                                   Options(maxSteps),
                                   client,
                                   tools ?? new ToolRegistry([]),
                                   SystemPrompt,
                                   events : events,
                                   recorderFactory : recorderFactory);
    }

    [Fact]
    public async Task SubmitTurn_TwoTurnsShareContextAndRecordLifecycle()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("first answer"));
        client.Enqueue(FakeChatClient.Text("second answer"));
        var host = NewHost(client);

        var run1 = await host.SubmitTurnAsync("what is this repo?", CancellationToken.None);
        var run2 = await host.SubmitTurnAsync("and why?", CancellationToken.None);

        // Every run records exactly one terminal result derived from the real execution.
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

        // The second request continues the first turn's exchange with exactly one
        // system prompt, proving the context is shared across turns.
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
        // M12.2: each turn now also publishes its streamed content deltas between the
        // lifecycle events, so one scripted text answer yields three events per run.
        Assert.Equal(6, sink.Events.Count);
        for (var i = 1; i < sink.Events.Count; i++)
            Assert.True(sink.Events[i].TimestampUtc >= sink.Events[i - 1].TimestampUtc);

        // Run 1: started, its one text delta, then finished — run-scoped sequence 1..3,
        // identity stamped by the host on every kind.
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

        // Run 2 keeps the same identity scheme with its own run-scoped sequence.
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

        // The rejected submission created no records; a follow-up turn still works.
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

        // The cancelled turn ended on a complete protocol boundary (no pending tool
        // calls), so a NEW run may continue the session; the old run is never restarted.
        client.Open();
        var run2 = await host.SubmitTurnAsync("second", CancellationToken.None);
        Assert.Equal(AgentStatus.Completed, run2.Result!.Status);
        Assert.Equal(2, host.Session.Turns.Count);
        Assert.Equal(2, client.Requests);
    }

    [Fact]
    public async Task SubmitTurn_CancelDuringToolExecution_BlocksNextTurnWithExplicitError()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("slow", "{}"));
        var       tool = new GateTool();
        using var cts  = new CancellationTokenSource();
        var       host = NewHost(client, new ToolRegistry([tool]));

        var first = host.SubmitTurnAsync("go", cts.Token);
        await tool.WaitStartedAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        cts.Cancel();
        var run1 = await first;
        Assert.Equal(AgentStatus.Cancelled, run1.Result!.Status);

        // The history now ends with an assistant tool call that never received a result.
        // Continuation is refused at the boundary and recorded as a failed run; nothing
        // is fabricated, and the tool is not re-executed.
        Assert.Equal(1, tool.ExecuteCount);
        var run2 = await host.SubmitTurnAsync("second", CancellationToken.None);
        Assert.Equal(AgentStatus.Failed, run2.Result!.Status);
        Assert.Contains("incomplete tool round", run2.Result.Error);
        Assert.Contains("call_1", run2.Result.Error);
        Assert.Equal(1, client.Requests);
        Assert.Equal(1, tool.ExecuteCount);
        Assert.Equal(2, host.Session.Turns.Count);
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

        Assert.Throws<ArgumentException>(() => new ChatSessionHost(
                                                                   Path.Combine(Path.GetTempPath(),
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
    ///     门控工具：开始执行后挂起，直到取消令牌触发，用于复现"执行中途取消"。
    ///     A gate tool: suspends after starting execution until the cancellation token
    ///     fires, reproducing a mid-execution cancellation.
    /// </summary>
    private sealed class GateTool : ITool
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExecuteCount { get; private set; }

        public ToolDefinition Definition { get; } = new()
        {
            Name        = "slow",
            Description = "Gate tool for mid-execution cancellation",
            Parameters  = new JsonObject { ["type"] = "object" }
        };

        public ToolPreparation Prepare(ChatToolCall call)
        {
            return new ToolPreparation
            {
                ToolName   = Definition.Name,
                CallId     = call.Id,
                Arguments  = new JsonObject(),
                Capability = $"test.{Definition.Name}",
                Summary    = $"{Definition.Name}()"
            };
        }

        public async Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new ToolResult { Succeeded = true, Content = "never reached" };
        }

        public Task WaitStartedAsync(CancellationToken cancellationToken)
        {
            return _started.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    ///     记录启动与完成调用的空转 recorder，其余方法均为无操作。
    ///     A no-op recorder capturing start/complete calls; every other method is a no-op.
    /// </summary>
    private sealed class CapturingRunRecorder : IRunRecorder
    {
        public List<AgentResult> CompletedResults { get; } = [];

        public Task StartAsync(string systemPrompt, string userInput, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task RecordPreparedAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task RecordPermissionAsync(
            ToolPreparation   preparation, PermissionDecision decision, string outcome,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task RecordResultAsync(
            ToolPreparation preparation, ToolResult result, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task RecordCompactionAsync(ContextChange change, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task RecordModelUsageAsync(
            int? inputTokens, int? outputTokens, string? finishReason, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task CompleteAsync(
            AgentResult       result, IReadOnlyList<ChatMessage> messages, StructuredState state,
            CancellationToken cancellationToken)
        {
            CompletedResults.Add(result);
            return Task.CompletedTask;
        }
    }
}

using System.Text.Json.Nodes;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     M12.3 交互 chat 会话测试：连续用户回合共享上下文、当前目标变化、流式回答不重复打印、
///     Ctrl+C 的回合/空闲分发、EOF、重复提交拒绝与拒绝工具后的完成链（PLAN §10 M12.3 与
///     M12 验收：解释 → 追问 → 要求修改 → 拒绝一个工具 → 完成）。
///     M12.3 interactive chat tests: continuous user turns sharing one context, current-goal
///     changes, answer text streamed exactly once, Ctrl+C turn/idle dispatch, EOF, duplicate
///     submission rejection, and the M12 acceptance chain (explain → follow-up → modify →
///     deny one tool → complete) (PLAN §10 M12.3).
/// </summary>
public class ChatSessionConsoleTests
{
    private const string SystemPrompt = "You are a test harness inspecting a workspace.";

    private static CancellationToken TestToken()
    {
        return new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;
    }

    private static ChatSessionHost NewHost(IChatCompletionClient client,
                                           ToolRegistry?         tools       = null,
                                           PermissionEngine?     permissions = null,
                                           IApprovalProvider?    approver    = null,
                                           IHostEventSink?       events      = null)
    {
        return new ChatSessionHost(
                                   Path.Combine(Path.GetTempPath(), "tinyharness-chat-console-tests"),
                                   new SessionModelIdentity("test-model", "https://example.invalid/v1",
                                                            ChatApiKind.ChatCompletions),
                                   new AgentOptions
                                       { Model = "test-model", MaxAgentSteps = 8, DefaultToolTimeoutSeconds = 30 },
                                   client,
                                   tools ?? new ToolRegistry([]),
                                   SystemPrompt,
                                   permissions,
                                   approver,
                                   events);
    }

    private static int Count(string text, string needle)
    {
        return text.Split(needle).Length - 1;
    }

    [Fact]
    public async Task RunAsync_TwoTurns_StreamAnswerOnceAndShareContext()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("first answer"));
        client.Enqueue(FakeChatClient.Text("second answer"));
        var output = new StringWriter();
        var host   = NewHost(client, events : new ConsoleHostEventSink(null, output));
        var input  = new ScriptedInput("解释一下这个仓库", "那测试为什么失败？");
        input.Complete();
        using var cancellation = new ChatCancellation(false);
        var       console      = new ChatSessionConsole(host, input, output, cancellation);

        var exit = await console.RunAsync();

        Assert.Equal(0, exit);
        // Each answer is streamed by the event display exactly once; the footer never repeats it.
        Assert.Equal(1, Count(output.ToString(), "first answer"));
        Assert.Equal(1, Count(output.ToString(), "second answer"));
        // Two turns plus the final prompt that observes EOF before exiting.
        Assert.Equal(3, Count(output.ToString(), "you> "));
        Assert.Contains("turn 1 finished: completed (steps: 1, tools: 0)", output.ToString());
        Assert.Contains("turn 2 finished: completed (steps: 1, tools: 0)", output.ToString());
        Assert.Contains("Session ended.", output.ToString());
        Assert.Equal(2, host.Session.Turns.Count);

        // The second request continues the first exchange: one system prompt, full prior
        // history, and the newest input as the current goal.
        Assert.Equal(2, client.RequestLog.Count);
        var second = client.RequestLog[1].Messages;
        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User],
                     second.Select(m => m.Role).ToArray());
        Assert.Equal("解释一下这个仓库", second[1].Content);
        Assert.Equal("那测试为什么失败？", second[3].Content);
        Assert.Equal(1, second.Count(m => m.Role == ChatRole.System));
    }

    [Fact]
    public async Task RunAsync_AcceptanceChain_ExplainFollowUpModifyDenyToolComplete()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ReasonedText("用户想了解仓库结构", "rs_1", "enc_1",
                                                   "这是一个本地 coding harness 仓库。"));
        client.Enqueue(FakeChatClient.ReasonedText("用户在追问测试失败的原因", "rs_2", "enc_2",
                                                   "测试失败是因为夹具路径断言。"));
        client.Enqueue(FakeChatClient.ToolCall("fix_tool", "{}", "call_fix"));
        client.Enqueue(FakeChatClient.Text("已收到拒绝；改用手工步骤完成修复。"));

        var tool   = new FakeTool("fix_tool");
        var output = new StringWriter();
        var scripted = new ScriptedInput("解释一下这个仓库的结构", "那测试为什么失败？",
                                         "按这个方案修复它", "d");
        scripted.Complete();
        var reader = new ExclusiveInputReader(scripted);
        var host = NewHost(client, new ToolRegistry([tool]),
                           new PermissionEngine(Path.Combine(Path.GetTempPath(),
                                                             "tinyharness-chat-console-tests")),
                           new ConsoleApprovalProvider(reader, output),
                           new ConsoleHostEventSink(null, output));
        using var cancellation = new ChatCancellation(false);
        var       console      = new ChatSessionConsole(host, reader, output, cancellation);

        var exit = await console.RunAsync();

        Assert.Equal(0, exit);
        // The approval answer "d" was consumed by the approval prompt, never as a user turn.
        Assert.Equal(3, host.Session.Turns.Count);
        Assert.All(host.Session.Turns, turn => Assert.Single(turn.Runs));
        Assert.Equal(4, client.RequestLog.Count);
        Assert.Equal(0, tool.ExecuteCount);

        // Turn 2 keeps turn 1's facts and reasoning; Responses opaque content rides verbatim.
        var second = client.RequestLog[1].Messages;
        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User],
                     second.Select(m => m.Role).ToArray());
        Assert.Equal("那测试为什么失败？", second[3].Content);
        var reasoning = Assert.Single(second[2].Reasoning!);
        Assert.Equal("rs_1", reasoning.ItemId);
        Assert.Equal("enc_1", reasoning.ProtectedData);
        Assert.Equal("用户想了解仓库结构", reasoning.Text);

        // Turn 3's request carries the whole history; the modification request is the newest
        // user message (the current goal), not a replacement of earlier turns.
        var third = client.RequestLog[2].Messages;
        Assert.Equal("按这个方案修复它", third[^1].Content);
        Assert.Contains("解释一下这个仓库的结构", third.Select(m => m.Content));
        Assert.Contains("这是一个本地 coding harness 仓库。", third.Select(m => m.Content));

        // The user denial is returned to the model as the tool result; the tool group stays
        // complete and the turn finishes with a plain-text completion.
        var fourth = client.RequestLog[3].Messages;
        var denial = fourth.Last(m => m.Role == ChatRole.Tool);
        Assert.Equal("call_fix", denial.ToolCallId);
        Assert.Contains("Permission denied by user", denial.Content);
        Assert.Contains("turn 3 finished: completed", output.ToString());

        // The streamed answers appear exactly once; the approval UI drew its own prompt.
        Assert.Equal(1, Count(output.ToString(), "这是一个本地 coding harness 仓库。"));
        Assert.Equal(1, Count(output.ToString(), "已收到拒绝；改用手工步骤完成修复。"));
        Assert.Contains("! Agent requests a side effect:", output.ToString());
    }

    [Fact]
    public async Task RunAsync_BlankLinesAreSkippedWithoutCreatingTurns()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("only answer"));
        var output = new StringWriter();
        var host   = NewHost(client, events : new ConsoleHostEventSink(null, output));
        var input  = new ScriptedInput("", "   ", "问一下");
        input.Complete();
        using var cancellation = new ChatCancellation(false);
        var       console      = new ChatSessionConsole(host, input, output, cancellation);

        var exit = await console.RunAsync();

        Assert.Equal(0, exit);
        Assert.Single(host.Session.Turns);
        Assert.Equal("问一下", host.Session.Turns[0].UserInput);
        Assert.Single(client.RequestLog);
    }

    [Fact]
    public async Task RunAsync_CancelDuringTurn_CancelsTheRunAndTheSessionContinues()
    {
        var       client       = new GateChatClient();
        var       host         = NewHost(client);
        var       output       = new StringWriter();
        var       input        = new ScriptedInput("开始一个任务");
        using var cancellation = new ChatCancellation(false);
        var       console      = new ChatSessionConsole(host, input, output, cancellation);

        var runTask = console.RunAsync();
        await client.WaitStartedAsync(TestToken());
        cancellation.RaiseCancel();

        client.Open();
        input.Enqueue("继续新回合");
        input.Complete();
        var exit = await runTask;

        Assert.Equal(0, exit);
        Assert.False(cancellation.IsSessionCancelled);
        Assert.Equal(2, host.Session.Turns.Count);
        Assert.Equal(AgentStatus.Cancelled, host.Session.Turns[0].Runs.Single().Result!.Status);
        Assert.Equal(AgentStatus.Completed, host.Session.Turns[1].Runs.Single().Result!.Status);
        Assert.Equal(2, client.Requests);
        Assert.Contains("turn 1 finished: cancelled", output.ToString());
        Assert.Contains("turn 2 finished: completed", output.ToString());
    }

    [Fact]
    public async Task RunAsync_CancelWhileIdle_EndsTheSessionWith130()
    {
        var       host         = NewHost(new FakeChatClient());
        var       output       = new StringWriter();
        var       input        = new BlockingInput();
        using var cancellation = new ChatCancellation(false);
        var       console      = new ChatSessionConsole(host, input, output, cancellation);

        var runTask = console.RunAsync();
        await input.Started.WaitAsync(TestToken());
        cancellation.RaiseCancel();

        var exit = await runTask;

        Assert.Equal(130, exit);
        Assert.True(cancellation.IsSessionCancelled);
        Assert.Empty(host.Session.Turns);
        Assert.Contains("Session ended.", output.ToString());
    }

    [Fact]
    public async Task RunAsync_CancelDuringToolExecution_NextTurnIsRefusedAndSessionContinues()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("slow_tool", "{}"));
        var       tool = new GateTool();
        var       output = new StringWriter();
        var       host = NewHost(client, new ToolRegistry([tool]), events : new ConsoleHostEventSink(null, output));
        var       input = new ScriptedInput("go");
        using var cancellation = new ChatCancellation(false);
        var       console = new ChatSessionConsole(host, input, output, cancellation);

        var runTask = console.RunAsync();
        await tool.WaitStartedAsync(TestToken());
        cancellation.RaiseCancel();

        input.Enqueue("second");
        input.Complete();
        var exit = await runTask;

        Assert.Equal(0, exit);
        // The interrupted tool is never re-executed and the refused turn never reaches the
        // model; the failed run records the explicit boundary error.
        Assert.Equal(1, tool.ExecuteCount);
        Assert.Equal(1, client.Requests);
        Assert.Equal(2, host.Session.Turns.Count);
        Assert.Equal(AgentStatus.Cancelled, host.Session.Turns[0].Runs.Single().Result!.Status);
        var refused = host.Session.Turns[1].Runs.Single().Result!;
        Assert.Equal(AgentStatus.Failed, refused.Status);
        Assert.Contains("incomplete tool round", refused.Error);
        Assert.Contains("turn 2 finished: failed", output.ToString());
    }

    [Fact]
    public async Task RunAsync_SubmissionRejectedWhileRunActive_ReportsAndKeepsSessionUsable()
    {
        var client = new GateChatClient();
        var host   = NewHost(client);
        var output = new StringWriter();
        var input  = new ScriptedInput("first");
        input.Complete();
        using var cancellation = new ChatCancellation(false);
        var       console      = new ChatSessionConsole(host, input, output, cancellation);

        var external = host.SubmitTurnAsync("external", CancellationToken.None);
        await client.WaitStartedAsync(TestToken());

        var exit = await console.RunAsync();

        Assert.Equal(0, exit);
        Assert.Contains("cannot start a turn", output.ToString());
        Assert.Contains("already has an active run", output.ToString());
        Assert.Single(host.Session.Turns);
        client.Open();
        var run = await external;
        Assert.Equal(AgentStatus.Completed, run.Result!.Status);
    }

    [Fact]
    public async Task RunAsync_FailedTurnsAreReportedAndTheSessionContinues()
    {
        var client = new FakeChatClient { ThrownError = new InvalidOperationException("model transport exploded") };
        var output = new StringWriter();
        var host   = NewHost(client, events : new ConsoleHostEventSink(null, output));
        var input  = new ScriptedInput("第一问", "第二问");
        input.Complete();
        using var cancellation = new ChatCancellation(false);
        var       console      = new ChatSessionConsole(host, input, output, cancellation);

        var exit = await console.RunAsync();

        Assert.Equal(0, exit);
        Assert.Equal(2, host.Session.Turns.Count);
        Assert.All(host.Session.Turns,
                   turn => Assert.Equal(AgentStatus.Failed, turn.Runs.Single().Result!.Status));
        Assert.Contains("turn 1 finished: failed", output.ToString());
        Assert.Contains("turn 2 finished: failed", output.ToString());
        Assert.Contains("model transport exploded", output.ToString());
    }

    [Fact]
    public async Task RunAsync_UsageTotalsAppearOnTheEventLineAndTheTurnFooter()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.TextWithUsage(120, 45, "回答文本"));
        var output = new StringWriter();
        var host   = NewHost(client, events : new ConsoleHostEventSink(null, output));
        var input  = new ScriptedInput("问一下");
        input.Complete();
        using var cancellation = new ChatCancellation(false);
        var       console      = new ChatSessionConsole(host, input, output, cancellation);

        var exit = await console.RunAsync();

        Assert.Equal(0, exit);
        Assert.Contains("usage: 120 in / 45 out tokens", output.ToString());
        Assert.Contains("turn 1 finished: completed (steps: 1, tools: 0, 120 in / 45 out tokens)",
                        output.ToString());
    }

    /// <summary>
    ///     按行分发的脚本输入：逐行返回预置行；行耗尽且未标记完成时挂起，直到新的输入、EOF
    ///     标记或取消。测试用它控制回合执行中途的输入时机。
    ///     A line-dispatched scripted input: returns the prepared lines one by one; suspends
    ///     when the lines run out until new input, the EOF mark, or cancellation arrives.
    ///     Tests use it to time input around mid-turn control flow.
    /// </summary>
    private sealed class ScriptedInput : TextReader
    {
        private readonly object               _gate  = new();
        private readonly Queue<string>        _lines = new();
        private          bool                 _completed;
        private          TaskCompletionSource _signal = NewSignal();

        public ScriptedInput(params string[] lines)
        {
            foreach (var line in lines) _lines.Enqueue(line);
            if (lines.Length > 0) _signal.TrySetResult();
        }

        public void Enqueue(params string[] lines)
        {
            lock (_gate)
            {
                foreach (var line in lines) _lines.Enqueue(line);
            }

            _signal.TrySetResult();
        }

        public void Complete()
        {
            lock (_gate)
            {
                _completed = true;
            }

            _signal.TrySetResult();
        }

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                TaskCompletionSource signal;
                lock (_gate)
                {
                    if (_lines.Count > 0) return _lines.Dequeue();

                    if (_completed) return null;

                    signal = _signal;
                }

                await Task.WhenAny(signal.Task,
                                   Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    _signal = NewSignal();
                }
            }
        }

        private static TaskCompletionSource NewSignal()
        {
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>
    ///     永不产出输入的读取器：首次读取报告已开始后挂起，直到取消。用于复现空闲 Ctrl+C。
    ///     A reader that never yields input: reports the first read and suspends until
    ///     cancellation, reproducing the idle Ctrl+C path.
    /// </summary>
    private sealed class BlockingInput : TextReader
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    ///     门控工具：开始执行后挂起，直到取消令牌触发，用于复现"执行中途取消"。
    ///     A gate tool: suspends after starting execution until the cancellation token fires.
    /// </summary>
    private sealed class GateTool : ITool
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExecuteCount { get; private set; }

        public ToolDefinition Definition { get; } = new()
        {
            Name        = "slow_tool",
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
}

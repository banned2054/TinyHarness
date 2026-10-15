using System.Text.Json.Nodes;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         验证交互会话的共享上下文、当前目标、单次流式展示、取消与 EOF 分发，
///         以及审批输入隔离和拒绝工具后继续完成回合。
///     </para>
///     <para>
///         Verifies shared context, current goals, single-pass streaming, cancellation and EOF dispatch,
///         and approval-input isolation with turn completion after a tool denial.
///     </para>
/// </summary>
public class ChatSessionConsoleTests
{
    private const string SystemPrompt = "You are a test harness inspecting a workspace.";

    private static CancellationToken TestToken() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static ChatSessionHost NewHost(IChatCompletionClient client,
                                           ToolRegistry?         tools       = null,
                                           PermissionEngine?     permissions = null,
                                           IApprovalProvider?    approver    = null,
                                           IHostEventSink?       events      = null) =>
        new(Path.Combine(Path.GetTempPath(), "tinyharness-chat-console-tests"),
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

    private static int Count(string text, string needle) => text.Split(needle).Length - 1;

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
        // 流式回答只展示一次，回合尾部不重复输出。
        Assert.Equal(1, Count(output.ToString(), "first answer"));
        Assert.Equal(1, Count(output.ToString(), "second answer"));
        // 两个回合后还需一次提示以读取 EOF。
        Assert.Equal(3, Count(output.ToString(), "you> "));
        Assert.Contains("turn 1 finished: completed (steps: 1, tools: 0)", output.ToString());
        Assert.Contains("turn 2 finished: completed (steps: 1, tools: 0)", output.ToString());
        Assert.Contains("Session ended.", output.ToString());
        Assert.Equal(2, host.Session.Turns.Count);

        // 后续请求保留完整旧历史和单条系统提示，并以最新输入作为当前目标。
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
        // 审批选择由审批提示消费，不能成为用户回合。
        Assert.Equal(3, host.Session.Turns.Count);
        Assert.All(host.Session.Turns, turn => Assert.Single(turn.Runs));
        Assert.Equal(4, client.RequestLog.Count);
        Assert.Equal(0, tool.ExecuteCount);

        // 旧回合的事实和 reasoning 保留，Responses opaque 内容原样传递。
        var second = client.RequestLog[1].Messages;
        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User],
                     second.Select(m => m.Role).ToArray());
        Assert.Equal("那测试为什么失败？", second[3].Content);
        var reasoning = Assert.Single(second[2].Reasoning!);
        Assert.Equal("rs_1", reasoning.ItemId);
        Assert.Equal("enc_1", reasoning.ProtectedData);
        Assert.Equal("用户想了解仓库结构", reasoning.Text);

        // 修改请求作为最新目标追加，不替换旧回合。
        var third = client.RequestLog[2].Messages;
        Assert.Equal("按这个方案修复它", third[^1].Content);
        Assert.Contains("解释一下这个仓库的结构", third.Select(m => m.Content));
        Assert.Contains("这是一个本地 coding harness 仓库。", third.Select(m => m.Content));

        // 拒绝以工具结果回传，保持工具组完整并允许回合结束。
        var fourth = client.RequestLog[3].Messages;
        var denial = fourth.Last(m => m.Role == ChatRole.Tool);
        Assert.Equal("call_fix", denial.ToolCallId);
        Assert.Contains("Permission denied by user", denial.Content);
        Assert.Contains("turn 3 finished: completed", output.ToString());

        // 审批提示不应造成流式回答重复。
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
    public async Task RunAsync_CancelDuringToolExecution_PromptsInspectionAndContinuesBlocked()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("slow_tool", "{}"));
        client.Enqueue(FakeChatClient.Text("second answer"));
        var       tool = new GateTool();
        var       output = new StringWriter();
        var       host = NewHost(client, new ToolRegistry([tool]), events : new ConsoleHostEventSink(null, output));
        var       input = new ScriptedInput("go");
        using var cancellation = new ChatCancellation(false);
        var       console = new ChatSessionConsole(host, input, output, cancellation);

        var runTask = console.RunAsync();
        await tool.WaitStartedAsync(TestToken());
        cancellation.RaiseCancel();

        // 取消后先消费保留阻断的选择，再从完整边界处理追问，最后由 EOF 退出。
        input.Enqueue("k", "second");
        input.Complete();
        var exit = await runTask;

        Assert.Equal(0, exit);
        // 中断工具不得重放；保留副作用阻断仍允许从完整边界请求模型。
        Assert.Equal(1, tool.ExecuteCount);
        Assert.Equal(2, client.Requests);
        Assert.Equal(2, host.Session.Turns.Count);
        Assert.Equal(AgentStatus.Cancelled, host.Session.Turns[0].Runs.Single().Result!.Status);
        Assert.Equal(AgentStatus.Completed, host.Session.Turns[1].Runs.Single().Result!.Status);
        Assert.True(host.InspectionPending);
        var text = output.ToString();
        Assert.Contains("Needs inspection", text);
        Assert.Contains("side effects stay blocked", text);
        Assert.Contains("turn 2 finished: completed", text);
    }

    [Fact]
    public async Task RunAsync_InspectionConfirmationUnblocksSideEffects()
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

        input.Enqueue("c");
        input.Complete();
        var exit = await runTask;

        Assert.Equal(0, exit);
        // 明确确认解除阻断；确认状态须保留供后续恢复使用。
        Assert.False(host.InspectionPending);
        Assert.Contains("inspection confirmed; side effects are allowed again", output.ToString());
    }

    [Fact]
    public async Task RunAsync_RestoredKeepBlockedPromptsOnceAndDoesNotConsumeFollowUp()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("one"));
        client.Enqueue(FakeChatClient.Text("two"));
        var session = new ChatSession(Guid.NewGuid().ToString("N"), Path.GetTempPath(),
                                      new SessionModelIdentity("test-model", "https://example.invalid/v1",
                                                               ChatApiKind.ChatCompletions))
            { PendingInspectionReason = "old interrupted operation" };
        using var host = new ChatSessionHost(session, new ConversationContext(),
                                             new AgentOptions
                                             {
                                                 Model = "test-model", MaxAgentSteps = 8, DefaultToolTimeoutSeconds = 30
                                             }, client, new ToolRegistry([]), SystemPrompt);
        var input = new ScriptedInput("k", "first", "yes");
        input.Complete();
        var       output       = new StringWriter();
        using var cancellation = new ChatCancellation(false);
        var       console      = new ChatSessionConsole(host, input, output, cancellation);
        Assert.Equal(0, await console.RunAsync());
        Assert.Equal(1, Count(output.ToString(), "Needs inspection"));
        Assert.Equal(["first", "yes"], host.Session.Turns.Select(turn => turn.UserInput).ToArray());
        Assert.True(host.InspectionPending);
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
    ///     <para>
    ///         逐行返回预置输入；耗尽后等待新输入、EOF 标记或取消，
    ///         用于控制回合执行期间的输入时机。
    ///     </para>
    ///     <para>
    ///         Returns preset lines, then waits for new input, an EOF marker or cancellation
    ///         to control input timing during a turn.
    ///     </para>
    /// </summary>
    private sealed class ScriptedInput : TextReader
    {
        private readonly Lock          _gate  = new();
        private readonly Queue<string> _lines = new();

        private bool                 _completed;
        private TaskCompletionSource _signal = NewSignal();

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

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    ///     <para>报告首次读取后等待取消，不产生输入，用于模拟空闲 Ctrl+C。</para>
    ///     <para>Reports the first read, then waits for cancellation without yielding input to simulate idle Ctrl+C.</para>
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
    ///     <para>报告执行开始后等待取消，用于模拟执行中途取消。</para>
    ///     <para>Reports execution start, then waits for cancellation to simulate mid-execution cancellation.</para>
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

        public ToolPreparation Prepare(ChatToolCall call) => new()
        {
            ToolName   = Definition.Name,
            CallId     = call.Id,
            Arguments  = new JsonObject(),
            Capability = $"test.{Definition.Name}",
            Summary    = $"{Definition.Name}()"
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
}

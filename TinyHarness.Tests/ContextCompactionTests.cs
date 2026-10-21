using NUnit.Framework.Internal;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         上下文压缩集成测试：压缩触发、tools 禁用、原子组完整性、失败回滚、交接替换、
///         模型视图裁剪，以及压缩仍无法容纳窗口时主循环的显式失败。
///     </para>
///     <para>
///         Context-compaction integration tests: compaction triggering, disabled tools,
///         atomic-group integrity, rollback on failure, handoff replacement, model-view
///         trimming, and the explicit failure when compaction still cannot fit the window.
///     </para>
/// </summary>
public class ContextCompactionTests
{
    // 窗口取值要足够紧，使三个超限回合触发压缩；又要足够宽，让压缩后视图（头部 + 交接摘要 +
    // 最新回合）容纳在窗口内；主循环拒发仍超出窗口的请求。
    private const int TightWindow = 1000;

    private static AgentOptions TightBudgetOptions(int maxSteps = 10) => new()
    {
        Model                     = "test-model",
        MaxAgentSteps             = maxSteps,
        DefaultToolTimeoutSeconds = 30,
        Context = new ContextOptions
        {
            ContextWindowTokens  = TightWindow,
            ReservedOutputTokens = 30
        }
    };

    private static AgentOptions NoContextOptions(int maxSteps = 10) => new()
    {
        Model                     = "test-model",
        MaxAgentSteps             = maxSteps,
        DefaultToolTimeoutSeconds = 30
    };

    private static string Handoff(string goal, string modified, string decision) =>
        $"Goal: {goal}. Modified: {modified}. Decision: {decision}. Continue pending work.";

    private static string ToolCallId(string prefix, int round) => $"{prefix}_{round}";

    private static void AppendTurn(
        ConversationContext context, string toolName, string id, string arguments, string result)
    {
        context.Append(ChatMessage.Assistant(string.Empty, [new ChatToolCall(id, toolName, arguments)]));
        context.Append(ChatMessage.Tool(toolName, id, result));
    }

    private static TwoBatchCase FindTwoBatchCase(ToolDefinition longTool, ToolDefinition shortTool)
    {
        const string arguments = "{\"path\":\"src/file.cs\"}";
        var          handoff   = Handoff("goal", "a.cs", "decision");

        for (var window = 700; window <= 5000; window += 50)
        for (var longLength = 80; longLength <= 1600; longLength += 20)
        for (var shortLength = 40; shortLength <= 800; shortLength += 20)
        {
            var options = new ContextOptions
            {
                ContextWindowTokens       = window,
                ReservedOutputTokens      = 30,
                CompactionThresholdTokens = window,
                ToolResultViewCharacters  = 12_000
            };
            var context = new ConversationContext(options);
            context.Append(ChatMessage.System("sys"));
            context.Append(ChatMessage.User("go"));
            var a = new string('A', longLength);
            var b = new string('B', shortLength);
            var c = new string('C', shortLength);
            var d = new string('D', longLength);
            AppendTurn(context, longTool.Name, "a", arguments, a);
            AppendTurn(context, shortTool.Name, "b", arguments, b);
            AppendTurn(context, shortTool.Name, "c", arguments, c);
            var beforeD = context.EstimateViewTokens();
            AppendTurn(context, longTool.Name, "d", arguments, d);
            var afterD      = context.EstimateViewTokens();
            var definitions = new[] { longTool, shortTool };
            var fixedCost   = TokenEstimator.EstimateToolDefinitions(definitions) + options.ReservedOutputTokens;
            if (beforeD + fixedCost > window || afterD + fixedCost <= window) continue;

            var first = context.BuildCompactionMessages(definitions);
            if (first.Count                                                           == 0 ||
                TokenEstimator.EstimateMessages(first) + options.ReservedOutputTokens > window)
                continue;

            var bMessages = new[]
            {
                ChatMessage.Assistant(string.Empty,
                                      [new ChatToolCall("b", shortTool.Name, arguments)]),
                ChatMessage.Tool(shortTool.Name, "b", b)
            };
            if (TokenEstimator.EstimateMessages(first.Concat(bMessages)) + options.ReservedOutputTokens <=
                window)
                continue;

            if (!context.TryApplyCompaction(handoff)) continue;

            var afterFirst = context.EstimateViewTokens() + fixedCost;
            if (afterFirst <= options.CompactionThresholdTokens) continue;

            var second = context.BuildCompactionMessages(definitions);
            if (second.Count                                                           == 0 ||
                TokenEstimator.EstimateMessages(second) + options.ReservedOutputTokens > window)
                continue;

            if (!context.TryApplyCompaction(handoff)) continue;

            var afterSecond = context.EstimateViewTokens() + fixedCost;
            if (afterSecond > options.ContextWindowTokens) continue;

            return new TwoBatchCase(options, arguments, a, b, c, d, first, second, beforeD + fixedCost,
                                    afterD + fixedCost, afterFirst, afterSecond);
        }

        throw new AssertionException("No deterministic two-batch compaction fixture was found.");
    }

    [Test]
    public async Task Compaction_DisablesTools_FoldsOldestTurnAndKeepsNewestCompleteTurn()
    {
        var tool   = new FakeTool("t") { ResultContent = new string('x', 1600) };
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("a", 1)));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("b", 2)));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("c", 3)));
        client.Enqueue(FakeChatClient.Text(Handoff("goal-one", "a.cs", "decision-x")));
        client.Enqueue(FakeChatClient.Text("done"));

        var sink = new TestHostEventSink();
        var loop = new AgentLoop(client, new ToolRegistry([tool]), TightBudgetOptions(), null, null, events : sink);

        var result  = await loop.RunAsync("sys", "go", CancellationToken.None);
        var changes = sink.Compactions();

        Assert.That(result.Status == AgentStatus.Completed, Is.True,
                    $"{result.Error} requests={client.Requests} compactions={result.Compactions} steps={result.Steps}");
        Assert.That(result.ToolExecutions, Is.EqualTo(3));
        Assert.That(result.Compactions, Is.EqualTo(1));
        Assert.That(client.Requests, Is.EqualTo(5));

        // 摘要请求必须禁用 tools，首次折叠携带原始任务与约束，且恰好折叠最旧的
        // 完整回合：assistant a_1 及其被截断的工具消息。
        var summaryRequest = client.RequestLog[3];
        Assert.That(summaryRequest.Tools, Is.Null);
        Assert.That(summaryRequest.Messages.Select(m => m.Role).ToArray(),
                    Is.EqualTo([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User]));
        Assert.That(summaryRequest.Messages[^1].Content, Does.Contain("CONTEXT CHECKPOINT COMPACTION"));
        Assert.That(summaryRequest.Messages[2].ToolCalls!.Single().Id, Is.EqualTo("a_1"));
        Assert.That(summaryRequest.Messages[3].Content, Does.Contain("#1"));

        // 下一个 agent 请求保留 tools，用状态消息替换被折叠回合，并按原样、原顺序保留
        // 最新完整回合（b_2）与进行中尾部（c_3）。
        var nextRequest = client.RequestLog[4];
        TestAssert.NotNull(nextRequest.Tools);
        var toolDefinition = TestAssert.Single(nextRequest.Tools!);
        Assert.That(nextRequest.Messages.Select(m => m.Role).ToArray(), Is.EqualTo([
            ChatRole.System, ChatRole.User, ChatRole.User, ChatRole.Assistant, ChatRole.Tool,
            ChatRole.Assistant, ChatRole.Tool
        ]));

        Assert.That(nextRequest.Messages[2].Content, Does.Contain(Handoff("goal-one", "a.cs", "decision-x")));

        Assert.That(nextRequest.Messages[3].ToolCalls!.Single().Id, Is.EqualTo("b_2"));
        Assert.That(nextRequest.Messages[5].ToolCalls!.Single().Id, Is.EqualTo("c_3"));
        var joined = string.Join('\n', nextRequest.Messages.Select(m => m.Content));
        Assert.That(joined, Does.Contain("#2"));
        Assert.That(joined, Does.Contain("#3"));
        Assert.That(joined, Does.Not.Contain("#1"));

        // 本地完整历史不受影响：三个回合、头部与最终纯文本 assistant 消息都保留。
        Assert.That(loop.History.Count, Is.EqualTo(9));
        TestAssert.ContainsAll(m => m.Role == ChatRole.Tool && m.Content.Contains("#1"), loop.History);

        var change = TestAssert.Single(changes);
        Assert.That(change.BeforeTokens > change.AfterTokens && change.AfterTokens > 0, Is.True);
    }

    [Test]
    public async Task CompactionFailure_OverWindowView_FailsRunWithClearError()
    {
        var tool   = new FakeTool("t") { ResultContent = new string('x', 1600) };
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("a", 1)));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("b", 2)));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("c", 3)));
        client.Enqueue(FakeChatClient.Text("   ")); // 摘要为空，必须拒绝
        client.Enqueue(FakeChatClient.Text("done"));

        var loop   = new AgentLoop(client, new ToolRegistry([tool]), TightBudgetOptions());
        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        // 压缩已失败（闩锁，不再重试）且视图仍超出窗口：主循环拒绝发送超窗请求，
        // 以显式错误失败，而不是静默继续。
        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(result.Compactions, Is.EqualTo(0));
        Assert.That(client.Requests, Is.EqualTo(4)); // 三次 agent 请求 + 恰好一次失败的摘要尝试
        Assert.That(result.Error, Does.Contain("window"));
        Assert.That(loop.History.Count, Is.EqualTo(8)); // 头部 + 三个回合，未改动
    }

    [Test]
    public async Task CompactionFailure_RollsBackAndContinues_WhenViewStillFitsTheWindow()
    {
        var tool   = new FakeTool("t") { ResultContent = new string('x', 1600) };
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("a", 1)));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("b", 2)));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("c", 3)));
        client.Enqueue(FakeChatClient.Text("   ")); // 摘要为空，必须拒绝
        client.Enqueue(FakeChatClient.Text("done"));

        // 压缩阈值（400）低于窗口，失败摘要可以回滚并让主循环继续：超阈值的视图
        // 仍容纳在声明的窗口内，按原样发送。
        var options = new AgentOptions
        {
            Model                     = "test-model",
            MaxAgentSteps             = 10,
            DefaultToolTimeoutSeconds = 30,
            Context = new ContextOptions
            {
                ContextWindowTokens       = 1500,
                ReservedOutputTokens      = 30,
                CompactionThresholdTokens = 1100
            }
        };
        var loop   = new AgentLoop(client, new ToolRegistry([tool]), options);
        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.That(result.Status == AgentStatus.Completed, Is.True,
                    $"{result.Error} requests={client.Requests} steps={result.Steps} compactions={result.Compactions}");
        Assert.That(result.Compactions, Is.EqualTo(0));
        Assert.That(client.Requests, Is.EqualTo(5)); // 恰好一次失败的摘要尝试，不再重试

        // 失败的压缩保持原视图不变：未插入状态消息，最旧回合仍按原样发送。
        var nextRequest = client.RequestLog[4];
        Assert.That(nextRequest.Messages.Count(m => m.Role == ChatRole.User), Is.EqualTo(1));
        var joined = string.Join('\n', nextRequest.Messages.Select(m => m.Content));
        Assert.That(joined, Does.Contain("#1"));
        Assert.That(joined, Does.Not.Contain("goal-one"));
        Assert.That(loop.History.Count, Is.EqualTo(9)); // 三个回合 + 头部 + 最终 assistant 消息
    }

    [Test]
    public async Task TwoCompactions_FeedPreviousHandoffForwardAndReplaceIt()
    {
        var tool   = new FakeTool("t") { ResultContent = new string('x', 1600) };
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("a", 1)));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("b", 2)));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("c", 3)));
        client.Enqueue(FakeChatClient.Text(Handoff("goal-one", "a.cs", "decision-x")));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("d", 4)));
        // 新摘要可合并旧事实和待办，不要求逐字复制列表。
        client.Enqueue(FakeChatClient.Text(Handoff("goal-two", "a.cs b.cs", "decision-x decision-y")));
        client.Enqueue(FakeChatClient.Text("done"));

        var sink = new TestHostEventSink();
        var loop = new AgentLoop(client, new ToolRegistry([tool]), TightBudgetOptions(12), null, null, events : sink);

        var result  = await loop.RunAsync("sys", "go", CancellationToken.None);
        var changes = sink.Compactions();

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(result.ToolExecutions, Is.EqualTo(4));
        Assert.That(result.Compactions, Is.EqualTo(2));
        Assert.That(client.Requests, Is.EqualTo(7));

        // 首次摘要看到原始任务并折叠 a_1；第二次摘要以首个交接为输入，
        // 折叠下一个最旧完整回合 b_2。
        var firstSummary = client.RequestLog[3];
        Assert.That(firstSummary.Messages.Select(m => m.Role).ToArray(),
                    Is.EqualTo([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User]));

        var secondSummary = client.RequestLog[5];
        Assert.That(secondSummary.Tools, Is.Null);
        Assert.That(secondSummary.Messages[2].Content, Does.Contain("goal-one"));
        Assert.That(secondSummary.Messages[3].ToolCalls!.Single().Id, Is.EqualTo("b_2"));
        Assert.That(secondSummary.Messages[4].Content, Does.Contain("#2"));

        // 第二个交接替换第一个；示例模型选择合并文件与决策，宿主不逐字校验旧列表。
        // 被折叠的原始回合（a_1、b_2）退出模型视图，近期回合仍在。
        var finalRequest = client.RequestLog[6];
        Assert.That(finalRequest.Messages.Select(m => m.Role).ToArray(), Is.EqualTo([
            ChatRole.System, ChatRole.User, ChatRole.User, ChatRole.Assistant, ChatRole.Tool,
            ChatRole.Assistant, ChatRole.Tool
        ]));
        Assert.That(finalRequest.Messages[2].Content,
                    Does.Contain(Handoff("goal-two", "a.cs b.cs", "decision-x decision-y")));

        var joined = string.Join('\n', finalRequest.Messages.Select(m => m.Content));
        Assert.That(joined, Does.Contain("#3"));
        Assert.That(joined, Does.Contain("#4"));
        Assert.That(joined, Does.Not.Contain("#1"));
        Assert.That(joined, Does.Not.Contain("#2"));

        Assert.That(changes.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task MultipleToolCallRounds_CompactionThenCompleteSuccessfully()
    {
        var tool          = new FakeTool("t") { ResultContent = new string('x', 1400) };
        var client        = new FakeChatClient();
        var argumentsJson = $"{{\"padding\":\"{new string('a', 300)}\"}}";
        for (var round = 1; round <= 3; round++)
            client.Enqueue(FakeChatClient.ToolCalls("t", 3, $"round{round}", argumentsJson));

        var summary = Handoff("goal", "file.cs", "inspect the remaining files");

        client.Enqueue(FakeChatClient.Text(summary));

        client.Enqueue(FakeChatClient.Text("done"));

        var sink = new TestHostEventSink();
        var loop = new AgentLoop(client, new ToolRegistry([tool]), new AgentOptions
        {
            Model                     = "test-model",
            MaxAgentSteps             = 5,
            DefaultToolTimeoutSeconds = 30,
            Context = new ContextOptions
            {
                ContextWindowTokens       = 2900,
                ReservedOutputTokens      = 30,
                CompactionThresholdTokens = 2800
            }
        }, null, null, events : sink);

        var result  = await loop.RunAsync("sys", "go", CancellationToken.None);
        var changes = sink.Compactions();

        Assert.That(result.Status == AgentStatus.Completed, Is.True,
                    $"{result.Error} requests={client.Requests} compactions={result.Compactions} steps={result.Steps}");
        Assert.That(result.ToolExecutions, Is.EqualTo(9));
        Assert.That(result.Compactions, Is.EqualTo(1));
        Assert.That(result.Steps, Is.EqualTo(4));    // 3 次工具请求 + 1 次最终文本请求。
        Assert.That(client.Requests, Is.EqualTo(5)); // 3 次工具 + 摘要 + 最终。
        TestAssert.Single(changes);

        // 摘要请求不推进 Agent 步数且永不携带 tools；其后第一个请求恢复正常形态。
        Assert.That(client.RequestLog[3].Tools, Is.Null);
        TestAssert.NotNull(client.RequestLog[4].Tools);
        Assert.That(result.FinalMessage, Is.EqualTo("done"));
        Assert.That(loop.History.Count, Is.EqualTo(15)); // 头部 + 3 个完整回合 + 最终 assistant。
        TestAssert.ContainsAll(m =>
            m.Role             == ChatRole.Assistant &&
            m.ToolCalls!.Count == 3 &&
            m.ToolCalls.All(call => call.FunctionName == "t"), loop.History);
        Assert.That(loop.History.Count(m => m.Role == ChatRole.Tool), Is.EqualTo(9));
        TestAssert.All(loop.History.Where(m => m.Role == ChatRole.Tool), message =>
            Assert.That(message.Content, Does.Contain("#")));
    }

    [Test]
    public async Task SameStep_TwoCompactionBatches_CompleteSuccessfully()
    {
        var definitions = (new FakeTool("long"), new FakeTool("short"));
        var fixture     = FindTwoBatchCase(definitions.Item1.Definition, definitions.Item2.Definition);
        var longTool    = new FakeTool("long") { ResultContent  = fixture.A };
        var shortTool   = new FakeTool("short") { ResultContent = fixture.B };
        var client      = new FakeChatClient();

        client.Enqueue(FakeChatClient.ToolCall("long", fixture.Arguments, "a"));
        client.Enqueue(FakeChatClient.ToolCall("short", fixture.Arguments, "b"));
        client.Enqueue(FakeChatClient.ToolCall("short", fixture.Arguments, "c"));
        client.Enqueue(FakeChatClient.ToolCall("long", fixture.Arguments, "d"));
        var handoff = Handoff("goal", "a.cs", "decision");
        client.Enqueue(FakeChatClient.Text(handoff));
        client.Enqueue(FakeChatClient.Text(handoff));
        client.Enqueue(FakeChatClient.Text("done"));

        var loop = new AgentLoop(client, new ToolRegistry([longTool, shortTool]), new AgentOptions
        {
            Model                     = "test-model",
            MaxAgentSteps             = 10,
            DefaultToolTimeoutSeconds = 30,
            Context                   = fixture.Options
        });

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.That(result.Status == AgentStatus.Completed, Is.True,
                    $"{result.Error} requests={client.Requests} steps={result.Steps} compactions={result.Compactions}");
        Assert.That(result.Steps, Is.EqualTo(5));
        Assert.That(result.Compactions, Is.EqualTo(2));
        Assert.That(client.Requests, Is.EqualTo(7));
        Assert.That(result.FinalMessage, Is.EqualTo("done"));

        Assert.That(client.RequestLog[4].Tools, Is.Null);
        Assert.That(client.RequestLog[5].Tools, Is.Null);
        TestAssert.NotNull(client.RequestLog[6].Tools);
        var firstSummaryInput = string.Join('\n', client.RequestLog[4].Messages.Select(m => m.Content));
        Assert.That(firstSummaryInput, Does.Contain(fixture.A));
        Assert.That(firstSummaryInput, Does.Not.Contain(fixture.B));
        var secondSummaryInput = string.Join('\n', client.RequestLog[5].Messages.Select(m => m.Content));
        Assert.That(secondSummaryInput, Does.Contain(handoff));
        Assert.That(secondSummaryInput, Does.Contain(fixture.B));

        Assert.That(TokenEstimator.EstimateMessages(client.RequestLog[4].Messages) +
                    fixture.Options.ReservedOutputTokens <= fixture.Options.ContextWindowTokens, Is.True);
        Assert.That(TokenEstimator.EstimateMessages(client.RequestLog[5].Messages) +
                    fixture.Options.ReservedOutputTokens <= fixture.Options.ContextWindowTokens, Is.True);
        Assert.That(fixture.BeforeD     <= fixture.Options.CompactionThresholdTokens, Is.True);
        Assert.That(fixture.AfterD      > fixture.Options.CompactionThresholdTokens, Is.True);
        Assert.That(fixture.AfterFirst  > fixture.Options.CompactionThresholdTokens, Is.True);
        Assert.That(fixture.AfterSecond <= fixture.Options.CompactionThresholdTokens, Is.True);

        TestAssert.ContainsAll(m =>
            m.Role                              == ChatRole.Assistant &&
            m.ToolCalls!.Single().Id            == "a" &&
            m.ToolCalls!.Single().ArgumentsJson == fixture.Arguments, loop.History);
        TestAssert.ContainsAll(m =>
            m.Role                              == ChatRole.Assistant &&
            m.ToolCalls!.Single().Id            == "b" &&
            m.ToolCalls!.Single().ArgumentsJson == fixture.Arguments, loop.History);
        TestAssert.ContainsAll(m => m is { Role: ChatRole.Tool, ToolCallId: "a" } && m.Content == fixture.A + "#1",
                               loop.History);
        TestAssert.ContainsAll(m => m is { Role: ChatRole.Tool, ToolCallId: "b" } && m.Content == fixture.B + "#1",
                               loop.History);
        TestAssert.ContainsAll(m => m is { Role: ChatRole.Tool, ToolCallId: "c" } && m.Content == fixture.B + "#2",
                               loop.History);
        TestAssert.ContainsAll(m => m is { Role: ChatRole.Tool, ToolCallId: "d" } && m.Content == fixture.A + "#2",
                               loop.History);
    }

    [Test]
    public async Task ViewTruncatesOversizedToolResult_WhileHistoryKeepsTheFullText()
    {
        var tool   = new FakeTool("t") { ResultContent = new string('x', 5000) };
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("a", 1)));
        client.Enqueue(FakeChatClient.Text("done"));

        var options = new AgentOptions
        {
            Model                     = "test-model",
            MaxAgentSteps             = 10,
            DefaultToolTimeoutSeconds = 30,
            Context = new ContextOptions
            {
                ContextWindowTokens      = 1000,
                ReservedOutputTokens     = 100,
                ToolResultViewCharacters = 96
            }
        };
        var loop = new AgentLoop(client, new ToolRegistry([tool]), options);

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(result.Compactions, Is.EqualTo(0));

        var modelView = client.RequestLog[1].Messages.Single(m => m.Role == ChatRole.Tool).Content;
        Assert.That(modelView, Does.Contain("[model view truncated to 96 of 5002 chars"));
        Assert.That(modelView.Length < 5002, Is.True, "the model view must be bounded");

        var retained = loop.History.Single(m => m.Role == ChatRole.Tool).Content;
        Assert.That(retained.Length, Is.EqualTo(5002)); // 5000 个填充字符 + "#1"
        Assert.That(retained, Does.Contain("#1"));
    }

    [Test]
    public async Task FittingHistory_IsSentWithoutCompaction()
    {
        var tool   = new FakeTool("t");
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("a", 1)));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("b", 2)));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("c", 3)));
        client.Enqueue(FakeChatClient.Text("done"));

        var options = new AgentOptions
        {
            Model                     = "test-model",
            MaxAgentSteps             = 10,
            DefaultToolTimeoutSeconds = 30,
            Context                   = new ContextOptions { ContextWindowTokens = 4000, ReservedOutputTokens = 200 }
        };
        var loop = new AgentLoop(client, new ToolRegistry([tool]), options);

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(result.Compactions, Is.EqualTo(0));
        Assert.That(client.Requests, Is.EqualTo(4));    // 无额外摘要请求
        Assert.That(loop.History.Count, Is.EqualTo(9)); // 三个回合 + 头部 + 最终 assistant 消息
    }

    [Test]
    public async Task WithoutContextOptions_HistoryIsSentVerbatim()
    {
        var tool   = new FakeTool("t");
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("a", 1)));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("b", 2)));
        client.Enqueue(FakeChatClient.Text("done"));

        var loop = new AgentLoop(client, new ToolRegistry([tool]), NoContextOptions());

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(result.Compactions, Is.EqualTo(0));
        Assert.That(client.Requests, Is.EqualTo(3));
        Assert.That(loop.History.Count, Is.EqualTo(7)); // 两个回合 + 头部 + 最终 assistant 消息
    }

    /// <summary>
    ///     <para>把一段脚本化响应的末尾 End 事件替换为携带 usage 的版本，模拟服务端实测数据。</para>
    ///     <para>
    ///         Replaces the trailing End event of a scripted response with one carrying usage,
    ///         simulating server-measured data.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<ChatStreamEvent> WithUsage(IReadOnlyList<ChatStreamEvent> events,
                                                            int inputTokens, int outputTokens, string finishReason)
    {
        var withUsage = events.ToList();
        withUsage[^1] = withUsage[^1] with
        {
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            FinishReason = finishReason
        };
        return withUsage;
    }

    [Test]
    public async Task CalibratedUsageEstimates_StillCompactAndCompleteWithTheShrinkInvariant()
    {
        var tool   = new FakeTool("t") { ResultContent = new string('x', 1600) };
        var client = new FakeChatClient();
        // 每个 agent 响应上报的输入 usage 都恰好是 harness 估算的两倍，因此从第一条流起
        // 预算刻度被校准为 2.0。
        client.Enqueue(WithUsage(FakeChatClient.ToolCall("t", "{}", ToolCallId("a", 1)), 50, 10, "tool_calls"));
        client.Enqueue(WithUsage(FakeChatClient.ToolCall("t", "{}", ToolCallId("b", 2)), 878, 10, "tool_calls"));
        client.Enqueue(WithUsage(FakeChatClient.ToolCall("t", "{}", ToolCallId("c", 3)), 1706, 10, "tool_calls"));
        // 摘要流不带 usage：校准保持最近一次观测，与摘要调用省略 usage 的服务行为一致。
        client.Enqueue(FakeChatClient.Text(Handoff("goal-one", "a.cs", "decision-x")));
        client.Enqueue(WithUsage(FakeChatClient.Text("done"), 1790, 6, "stop"));

        var sink = new TestHostEventSink();
        var loop = new AgentLoop(client, new ToolRegistry([tool]), new AgentOptions
        {
            Model                     = "test-model",
            MaxAgentSteps             = 10,
            DefaultToolTimeoutSeconds = 30,
            Context = new ContextOptions
            {
                ContextWindowTokens       = 2000,
                ReservedOutputTokens      = 30,
                CompactionThresholdTokens = 1400
            }
        }, null, null, events : sink);

        var result  = await loop.RunAsync("sys", "go", CancellationToken.None);
        var changes = sink.Compactions();

        Assert.That(result.Status == AgentStatus.Completed, Is.True,
                    $"{result.Error} requests={client.Requests} compactions={result.Compactions} steps={result.Steps}");
        Assert.That(result.ToolExecutions, Is.EqualTo(3));
        Assert.That(client.Requests, Is.EqualTo(5));      // 三次 agent 请求 + 一次摘要 + 最终
        Assert.That(client.RequestLog[3].Tools, Is.Null); // 第 4 个请求是禁用 tools 的摘要请求

        // 不校准时三个回合后的原始估算仍低于 1400 阈值，因此这次压缩由校准（2x）估算驱动；
        // 严格变小不变量在校准刻度上仍然成立。
        Assert.That(result.Compactions, Is.EqualTo(1));
        var change = TestAssert.Single(changes);
        Assert.That(change.BeforeTokens > change.AfterTokens && change.AfterTokens > 0, Is.True,
                    $"calibrated compaction must still shrink the view: {change.BeforeTokens} -> {change.AfterTokens}");
    }

    private sealed record TwoBatchCase(
        ContextOptions             Options,
        string                     Arguments,
        string                     A,
        string                     B,
        string                     C,
        string                     D,
        IReadOnlyList<ChatMessage> FirstSummary,
        IReadOnlyList<ChatMessage> SecondSummary,
        int                        BeforeD,
        int                        AfterD,
        int                        AfterFirst,
        int                        AfterSecond);
}

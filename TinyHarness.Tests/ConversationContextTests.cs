using System.Text.Json.Nodes;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Services.Context;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         验证上下文压缩的头部计费、原始任务与历史事实保留、提交前收益和计划有效性，
///         以及摘要请求裁剪、自身预算和阈值配置边界。
///     </para>
///     <para>
///         Verifies head-token accounting, original-task and historical-fact retention, pre-commit benefit
///         and plan validity, plus summary capping, request budgets and threshold boundaries.
///     </para>
/// </summary>
public class ConversationContextTests
{
    private static ContextOptions Options(int window, int reserved, int toolResultCap = 12_000) => new()
    {
        ContextWindowTokens      = window,
        ReservedOutputTokens     = reserved,
        ToolResultViewCharacters = toolResultCap
    };

    private static string StateJson(string goal, string[] decisions, string[] inspected, string[] modified) =>
        new JsonObject
        {
            ["goal"]        = goal,
            ["constraints"] = new JsonArray(),
            ["decisions"] =
                new JsonArray(decisions.Select(d => (JsonNode)d!)
                                       .ToArray()),
            ["filesInspected"] =
                new JsonArray(inspected.Select(d => (JsonNode)d!)
                                       .ToArray()),
            ["filesModified"] =
                new JsonArray(modified.Select(d => (JsonNode)d!)
                                      .ToArray()),
            ["commandsAndResults"] = new JsonArray(),
            ["pendingWork"]        = new JsonArray()
        }.ToJsonString();

    private static void AppendRound(ConversationContext context, string callId, string toolResult)
    {
        context.Append(ChatMessage.Assistant(string.Empty, [new ChatToolCall(callId, "t", "{}")]));
        context.Append(ChatMessage.Tool("t", callId, toolResult));
    }

    private static ConversationContext ContextWithRounds(int    window,
                                                         int    reserved,
                                                         int    rounds,
                                                         string toolResult,
                                                         string systemPrompt = "sys")
    {
        var context = new ConversationContext(Options(window, reserved));
        context.Append(ChatMessage.System(systemPrompt));
        context.Append(ChatMessage.User("go"));
        for (var round = 1; round <= rounds; round++) AppendRound(context, $"c{round}", toolResult);

        return context;
    }

    [Test]
    public void EmptySummary_IsRejected_AndFirstFoldCarriesTheOriginalTask()
    {
        var context = ContextWithRounds(1000, 30, 3, new string('x', 1600));
        Assert.That(context.RequiresCompaction([]), Is.True);

        // 首次摘要请求须包含原始任务和约束，并裁剪待折叠消息。
        var summaryRequest = context.BuildCompactionMessages([]);
        Assert.That(summaryRequest.Select(m => m.Role).ToArray(),
                    Is.EqualTo([ChatRole.System, ChatRole.User, ChatRole.User, ChatRole.Assistant, ChatRole.Tool]));
        Assert.That(summaryRequest[2].Content, Does.Contain("Original task"));
        Assert.That(summaryRequest[2].Content, Does.Contain("[User] go"));

        // 全空摘要会抹去工具工作，必须拒绝且保留当前视图。
        var viewBefore = context.EstimateViewTokens();
        Assert.That(context.TryApplyCompaction("{}"), Is.False);
        Assert.That(context.EstimateViewTokens(), Is.EqualTo(viewBefore));
        TestAssert.Single(context.BuildModelView(), m => m.Role == ChatRole.User); // 不插入状态消息。
        Assert.That(context.RequiresCompaction([]), Is.False);                     // 新消息到来前不重试失败的压缩。
    }

    [Test]
    public void FirstFold_AcceptsAGoalOnlySummary()
    {
        var context = ContextWithRounds(1000, 30, 3, new string('x', 1600));
        Assert.That(context.BuildCompactionMessages([]), Is.Not.Empty);

        // 尚无列表事实时，仅含目标的摘要仍有效，不能丢失任务。
        Assert.That(context.TryApplyCompaction("""{"goal":"g"}"""), Is.True);
        var view = context.BuildModelView();
        Assert.That(view.Count(m => m.Role == ChatRole.User), Is.EqualTo(2)); // 原始用户输入与状态消息。
        Assert.That(view[2].Content, Does.Contain("\"goal\":\"g\""));
    }

    [Test]
    public void Compaction_AllowsCurrentStateToAdvance_ButKeepsHistoricalFacts()
    {
        var context = ContextWithRounds(1000, 30, 3, new string('x', 1600));
        Assert.That(context.BuildCompactionMessages([]), Is.Not.Empty);
        var first = new JsonObject
        {
            ["goal"]               = "goal-g",
            ["constraints"]        = new JsonArray("old constraint"),
            ["decisions"]          = new JsonArray("run tests"),
            ["filesInspected"]     = new JsonArray("src"),
            ["filesModified"]      = new JsonArray("a.cs"),
            ["commandsAndResults"] = new JsonArray("build passed"),
            ["pendingWork"]        = new JsonArray("run tests")
        }.ToJsonString();
        Assert.That(context.TryApplyCompaction(first), Is.True);

        AppendRound(context, "c4", new string('x', 1600));
        Assert.That(context.RequiresCompaction([]), Is.True);
        var secondRequest = context.BuildCompactionMessages([]);
        Assert.That(secondRequest[1].Content, Does.Contain("run tests")); // 先前状态须传给摘要模型。

        // 当前约束和待办可更新；历史决策、文件与命令事实必须保留。
        var completed = new JsonObject
        {
            ["goal"]               = "goal-g",
            ["constraints"]        = new JsonArray("current constraint"),
            ["decisions"]          = new JsonArray("run tests", "tests passed"),
            ["filesInspected"]     = new JsonArray("src"),
            ["filesModified"]      = new JsonArray("a.cs"),
            ["commandsAndResults"] = new JsonArray("build passed", "tests passed"),
            ["pendingWork"]        = new JsonArray("completed: run tests")
        }.ToJsonString();
        Assert.That(context.TryApplyCompaction(completed), Is.True);
        Assert.That(StructuredState.TryParse(context.BuildModelView()[2].Content, out var state), Is.True);
        Assert.That(state.Decisions, Is.EqualTo(["run tests", "tests passed"]));
        Assert.That(state.PendingWork, Is.EqualTo(["completed: run tests"]));
        Assert.That(state.CommandsAndResults, Does.Contain("build passed"));
    }

    [Test]
    public void Compaction_RejectsSummaryThatDropsHistoricalFilesOrCommands()
    {
        var context = ContextWithRounds(1000, 30, 3, new string('x', 1600));
        Assert.That(context.BuildCompactionMessages([]), Is.Not.Empty);
        Assert.That(context.TryApplyCompaction(new JsonObject
        {
            ["goal"]               = "goal-g",
            ["constraints"]        = new JsonArray(),
            ["decisions"]          = new JsonArray(),
            ["filesInspected"]     = new JsonArray("src"),
            ["filesModified"]      = new JsonArray("a.cs"),
            ["commandsAndResults"] = new JsonArray("build passed"),
            ["pendingWork"]        = new JsonArray()
        }.ToJsonString()), Is.True);

        AppendRound(context, "c4", new string('x', 1600));
        Assert.That(context.BuildCompactionMessages([]), Is.Not.Empty);
        var droppingFacts = StateJson("goal-g", [], ["src"], []);
        Assert.That(context.TryApplyCompaction(droppingFacts), Is.False);
        Assert.That(context.BuildModelView()[2].Content, Does.Contain("a.cs"));
        Assert.That(context.BuildModelView()[2].Content, Does.Contain("build passed"));
    }

    [Test]
    public void Compaction_RejectsSummaryThatSilentlyDropsDecisionOrPendingWork()
    {
        var context = ContextWithRounds(1000, 30, 3, new string('x', 1600));
        Assert.That(context.BuildCompactionMessages([]), Is.Not.Empty);
        Assert.That(context.TryApplyCompaction(new JsonObject
        {
            ["goal"]               = "goal-g",
            ["constraints"]        = new JsonArray("stay in workspace"),
            ["decisions"]          = new JsonArray("run tests"),
            ["filesInspected"]     = new JsonArray("src"),
            ["filesModified"]      = new JsonArray(),
            ["commandsAndResults"] = new JsonArray(),
            ["pendingWork"]        = new JsonArray("rerun tests")
        }.ToJsonString()), Is.True);

        AppendRound(context, "c4", new string('x', 1600));
        Assert.That(context.BuildCompactionMessages([]), Is.Not.Empty);
        var droppingState = new JsonObject
        {
            ["goal"]               = "goal-g",
            ["constraints"]        = new JsonArray("updated"),
            ["decisions"]          = new JsonArray(),
            ["filesInspected"]     = new JsonArray("src"),
            ["filesModified"]      = new JsonArray(),
            ["commandsAndResults"] = new JsonArray(),
            ["pendingWork"]        = new JsonArray()
        }.ToJsonString();

        Assert.That(context.TryApplyCompaction(droppingState), Is.False);
    }

    [Test]
    public void Compaction_RejectsSummaryThatDoesNotShrinkTheView()
    {
        var context = ContextWithRounds(1000, 30, 3, new string('x', 1600));
        Assert.That(context.BuildCompactionMessages([]), Is.Not.Empty);

        // 结构合法但不能缩小视图的摘要不得提交。
        var viewBefore = context.EstimateViewTokens();
        var bloated = $"{{\"goal\":\"{new string('g', 5000)}\",\"constraints\":[],\"decisions\":[]," +
                      $"\"filesInspected\":[],\"filesModified\":[],\"commandsAndResults\":[],\"pendingWork\":[]}}";
        Assert.That(context.TryApplyCompaction(bloated), Is.False);
        Assert.That(context.EstimateViewTokens(), Is.EqualTo(viewBefore));
        TestAssert.Single(context.BuildModelView(), m => m.Role == ChatRole.User);
    }

    [Test]
    public void TryApplyCompaction_RejectsWhenMessagesWereAppendedAfterThePlan()
    {
        var context = ContextWithRounds(1000, 30, 3, new string('x', 1600));
        Assert.That(context.BuildCompactionMessages([]), Is.Not.Empty);

        // 折叠计划绑定原始消息索引；规划后追加消息必须使计划失效。
        context.Append(ChatMessage.User("late"));
        var viewBefore = context.EstimateViewTokens();
        Assert.That(context.TryApplyCompaction("""{"goal":"g"}"""), Is.False);
        Assert.That(context.EstimateViewTokens(), Is.EqualTo(viewBefore));
        TestAssert.DoesNotContainAny(m => m.Role == ChatRole.User && m.Content.Contains("\"goal\""),
                                     context.BuildModelView());
    }

    [Test]
    public void FirstCompaction_ChargesHeadTokens_WhenDecidingWhetherToFold()
    {
        // 首次折叠后仍保留头部消息，规划必须计入其 token，不能误判为无需折叠。
        var context = ContextWithRounds(1000, 30, 3,
                                        new string('x', 1600), new string('x', 400));
        Assert.That(context.EstimateViewTokens() + 30 > 1000, Is.True, "the total estimate must exceed the window");
        Assert.That(context.RequiresCompaction([]), Is.True);
        Assert.That(context.BuildCompactionMessages([]), Is.Not.Empty);
    }

    [Test]
    public void SummaryRequest_CapsOversizedToolResults_AndFitsItsOwnWindow()
    {
        var context = new ConversationContext(Options(1000, 30, 2048));
        context.Append(ChatMessage.System("sys"));
        context.Append(ChatMessage.User("go"));
        var huge = new string('x', 200_000);
        for (var round = 1; round <= 3; round++) AppendRound(context, $"c{round}", huge);

        Assert.That(context.RequiresCompaction([]), Is.True);
        var summary = context.BuildCompactionMessages([]);

        // 摘要请求裁剪超大工具输出，本地完整历史保持原样。
        var toolMessage = TestAssert.Single(summary, m => m.Role == ChatRole.Tool);
        Assert.That(toolMessage.Content, Does.Contain("truncated"));
        Assert.That(toolMessage.Content.Length < 5000, Is.True);
        TestAssert.All(context.Messages.Where(m => m.Role == ChatRole.Tool),
                       m => Assert.That(m.Content.Length, Is.EqualTo(200_000)));

        // 摘要请求按自身窗口预算，不能只检查主模型视图。
        Assert.That(TokenEstimator.EstimateMessages(summary) + 30 <= 1000, Is.True,
                    "the summarizer input must fit its own window");
    }

    [Test]
    public void ThreeCompactionBatches_FitTheWindowWithoutAppendingHistory()
    {
        var context              = ContextWithRounds(1000, 30, 8, new string('x', 1200));
        var originalMessageCount = context.Messages.Count;

        for (var batch = 0; batch < 3; batch++)
        {
            Assert.That(context.RequiresCompaction([]), Is.True);
            var summary = context.BuildCompactionMessages([]);
            Assert.That(summary, Is.Not.Empty);
            Assert.That(TokenEstimator.EstimateMessages(summary) + 30 <= 1000, Is.True,
                        "each summary request must leave its reserved output inside the window");
            Assert.That(context.TryApplyCompaction("""{"goal":"g"}"""), Is.True);
            Assert.That(context.Messages.Count, Is.EqualTo(originalMessageCount));
        }

        Assert.That(context.EstimateViewTokens() + 30 <= 1000, Is.True);
    }

    [Test]
    public void OversizedAtomicGroup_IsNotForcedIntoAnOverBudgetSummary()
    {
        var context = ContextWithRounds(500, 30, 3, new string('x', 1200));

        Assert.That(context.RequiresCompaction([]), Is.False);
        Assert.That(context.BuildCompactionMessages([]), Is.Empty);
    }

    [Test]
    public void ContextOptions_RejectCompactionThresholdAboveTheWindow()
    {
        var above = new ContextOptions
        {
            ContextWindowTokens       = 1000,
            ReservedOutputTokens      = 100,
            CompactionThresholdTokens = 2000
        };
        TestAssert.Throws<ArgumentOutOfRangeException>(() => above.Validate());

        var equal = new ContextOptions
        {
            ContextWindowTokens       = 1000,
            ReservedOutputTokens      = 100,
            CompactionThresholdTokens = 1000
        };
        Assert.That(equal.Validate(), Is.SameAs(equal));
    }

    [Test]
    public void RecordModelUsage_CalibratesViewEstimatesByTheObservedRatio()
    {
        var context = new ConversationContext();
        context.Append(ChatMessage.System("sys"));
        context.Append(ChatMessage.User("go"));
        var uncalibrated = context.EstimateViewTokens(); // 初始校准系数为 1.0。
        Assert.That(uncalibrated > 0, Is.True);

        // 实测与估算比为 1.5 时，后续估算按该比率缩放并向上取整。
        context.RecordModelUsage(100, 150);
        Assert.That(context.EstimateViewTokens(), Is.EqualTo((int)Math.Ceiling(uncalibrated * 1.5)));

        // 仅最近一次有效观测参与校准。
        context.RecordModelUsage(100, 300);
        Assert.That(context.EstimateViewTokens(), Is.EqualTo((int)Math.Ceiling(uncalibrated * 3.0)));
    }

    [Test]
    public void RecordModelUsage_ClampsTheRatioOnBothSides()
    {
        var context = new ConversationContext();
        context.Append(ChatMessage.User("go"));
        var uncalibrated = context.EstimateViewTokens();

        context.RecordModelUsage(100, 20); // 比率 0.2 限制为 0.5。
        Assert.That(context.EstimateViewTokens(), Is.EqualTo((int)Math.Ceiling(uncalibrated * 0.5)));

        context.RecordModelUsage(100, 900); // 比率 9.0 限制为 4.0。
        Assert.That(context.EstimateViewTokens(), Is.EqualTo((int)Math.Ceiling(uncalibrated * 4.0)));
    }

    [Test]
    public void RecordModelUsage_IgnoresNullOrNonPositiveObservations()
    {
        var context = new ConversationContext();
        context.Append(ChatMessage.User("go"));
        var before = context.EstimateViewTokens();

        context.RecordModelUsage(100, null); // 未报告可靠 usage。
        context.RecordModelUsage(100, 0);
        context.RecordModelUsage(100, -5);
        context.RecordModelUsage(0, 150); // 非正估算不可用于校准。
        context.RecordModelUsage(-10, 150);

        Assert.That(context.EstimateViewTokens(), Is.EqualTo(before)); // 无效观测不改变校准系数。
    }

    [Test]
    public void RecordModelUsage_KeepsTheCompactionShrinkInvariant()
    {
        var context = ContextWithRounds(1000, 30, 3, new string('x', 1600));
        Assert.That(context.BuildCompactionMessages([]), Is.Not.Empty);

        // 校准同时缩放收益检查两端，不能破坏压缩必须缩小视图的约束。
        context.RecordModelUsage(100, 400);
        var before = context.EstimateViewTokens();
        Assert.That(context.TryApplyCompaction("""{"goal":"g"}"""), Is.True);
        var after = context.EstimateViewTokens();
        Assert.That(after < before, Is.True, $"calibrated compaction must still shrink the view: {after} >= {before}");
    }

    [Test]
    public void TruncateToMessageCount_RemovesTheTailWithoutTouchingTheFoldRegion()
    {
        var context = new ConversationContext(Options(100_000, 1_000));
        context.Restore(
        [
            ChatMessage.System("sys"),
            ChatMessage.User("u1"),
            ChatMessage.Assistant(string.Empty, [new ChatToolCall("c1", "t", "{}")]),
            ChatMessage.Tool("t", "c1", "r1"),
            ChatMessage.Assistant("a1"),
            ChatMessage.User("u2"),
            ChatMessage.Assistant(string.Empty, [new ChatToolCall("c2", "t", "{}")]),
            ChatMessage.Tool("t", "c2", "partial")
        ], new StructuredState { Goal = "g" }, true, 2, 4);

        // 中断尾部整体回退到完整边界，不能改动较早的完整折叠区。
        context.TruncateToMessageCount(6);

        Assert.That(context.Messages.Count, Is.EqualTo(6));
        Assert.That(context.Messages[^1].Role, Is.EqualTo(ChatRole.User));
        Assert.That(context.Position, Is.EqualTo(new ConversationContext.CompactionPosition(true, 2, 4)));

        TestAssert.Throws<ArgumentOutOfRangeException>(() => context.TruncateToMessageCount(0));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => context.TruncateToMessageCount(6));
        TestAssert.Throws<ArgumentException>(() => context.TruncateToMessageCount(3));
    }
}

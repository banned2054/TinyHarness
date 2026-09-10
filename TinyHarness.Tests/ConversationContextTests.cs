using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Context;

namespace TinyHarness.Tests;

/// <summary>
/// ConversationContext 直接 API 回归测试，覆盖 M6 review 修复：首次规划计入头部消息、
/// 空摘要与丢事实摘要被拒绝、提交前收益校验、计划后追加消息拒绝、摘要请求裁剪与自身预算、
/// 首次摘要携带原始任务，以及阈值/窗口配置校验。
///
/// Direct ConversationContext API regression tests for the M6 review fixes: first-plan
/// head accounting, rejection of empty or fact-dropping summaries, the pre-commit
/// benefit check, rejection after plan-time appends, summary-request capping and its
/// own budget, the original task on the first fold, and threshold/window validation.
/// </summary>
public class ConversationContextTests
{
    private static ContextOptions Options(int window, int reserved, int toolResultCap = 12_000) => new()
    {
        ContextWindowTokens      = window,
        ReservedOutputTokens     = reserved,
        ToolResultViewCharacters = toolResultCap,
    };

    private static string StateJson(string goal, string[] decisions, string[] inspected, string[] modified) =>
        new System.Text.Json.Nodes.JsonObject
        {
            ["goal"]        = goal,
            ["constraints"] = new System.Text.Json.Nodes.JsonArray(),
            ["decisions"] =
                new System.Text.Json.Nodes.JsonArray(decisions.Select(d => (System.Text.Json.Nodes.JsonNode)d!)
                                                              .ToArray()),
            ["filesInspected"] =
                new System.Text.Json.Nodes.JsonArray(inspected.Select(d => (System.Text.Json.Nodes.JsonNode)d!)
                                                              .ToArray()),
            ["filesModified"] =
                new System.Text.Json.Nodes.JsonArray(modified.Select(d => (System.Text.Json.Nodes.JsonNode)d!)
                                                             .ToArray()),
            ["commandsAndResults"] = new System.Text.Json.Nodes.JsonArray(),
            ["pendingWork"]        = new System.Text.Json.Nodes.JsonArray(),
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
        for (var round = 1; round <= rounds; round++)
        {
            AppendRound(context, $"c{round}", toolResult);
        }

        return context;
    }

    [Fact]
    public void EmptySummary_IsRejected_AndFirstFoldCarriesTheOriginalTask()
    {
        var context = ContextWithRounds(window : 1000, reserved : 30, rounds : 3, toolResult : new string('x', 1600));
        Assert.True(context.RequiresCompaction([]));

        // First fold: instructions, "no previous summary", the original task and
        // constraints, then the capped fold turns.
        var summaryRequest = context.BuildCompactionMessages([]);
        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.User, ChatRole.Assistant, ChatRole.Tool],
                     summaryRequest.Select(m => m.Role).ToArray());
        Assert.Contains("Original task", summaryRequest[2].Content);
        Assert.Contains("[User] go", summaryRequest[2].Content);

        // An all-empty summary would wipe the folded tool work from the model view:
        // it must be rejected and leave the current view untouched.
        var viewBefore = context.EstimateViewTokens();
        Assert.False(context.TryApplyCompaction("{}"));
        Assert.Equal(viewBefore, context.EstimateViewTokens());
        Assert.Single(context.BuildModelView(), m => m.Role == ChatRole.User); // no state message inserted
        Assert.False(context.RequiresCompaction([])); // failure is latched until new messages arrive
    }

    [Fact]
    public void FirstFold_AcceptsAGoalOnlySummary()
    {
        var context = ContextWithRounds(window : 1000, reserved : 30, rounds : 3, toolResult : new string('x', 1600));
        Assert.NotEmpty(context.BuildCompactionMessages([]));

        // A goal-only summary is not degenerate (the goal is present), so it is
        // accepted: the task survives even when no list facts were gathered yet.
        Assert.True(context.TryApplyCompaction("""{"goal":"g"}"""));
        var view = context.BuildModelView();
        Assert.Equal(2, view.Count(m => m.Role == ChatRole.User)); // head user + state message
        Assert.Contains("\"goal\":\"g\"", view[2].Content);
    }

    [Fact]
    public void Compaction_AllowsCurrentStateToAdvance_ButKeepsHistoricalFacts()
    {
        var context = ContextWithRounds(window : 1000, reserved : 30, rounds : 3, toolResult : new string('x', 1600));
        Assert.NotEmpty(context.BuildCompactionMessages([]));
        var first = new System.Text.Json.Nodes.JsonObject
        {
            ["goal"]               = "goal-g",
            ["constraints"]        = new System.Text.Json.Nodes.JsonArray("old constraint"),
            ["decisions"]          = new System.Text.Json.Nodes.JsonArray("run tests"),
            ["filesInspected"]     = new System.Text.Json.Nodes.JsonArray("src"),
            ["filesModified"]      = new System.Text.Json.Nodes.JsonArray("a.cs"),
            ["commandsAndResults"] = new System.Text.Json.Nodes.JsonArray("build passed"),
            ["pendingWork"]        = new System.Text.Json.Nodes.JsonArray("run tests"),
        }.ToJsonString();
        Assert.True(context.TryApplyCompaction(first));

        AppendRound(context, "c4", new string('x', 1600));
        Assert.True(context.RequiresCompaction([]));
        var secondRequest = context.BuildCompactionMessages([]);
        Assert.Contains("run tests", secondRequest[1].Content); // prior state is forwarded to the summarizer

        // Constraints and pending work may be replaced by a non-empty current state,
        // while prior decisions and historical facts must still be carried forward.
        var completed = new System.Text.Json.Nodes.JsonObject
        {
            ["goal"]               = "goal-g",
            ["constraints"]        = new System.Text.Json.Nodes.JsonArray("current constraint"),
            ["decisions"]          = new System.Text.Json.Nodes.JsonArray("run tests", "tests passed"),
            ["filesInspected"]     = new System.Text.Json.Nodes.JsonArray("src"),
            ["filesModified"]      = new System.Text.Json.Nodes.JsonArray("a.cs"),
            ["commandsAndResults"] = new System.Text.Json.Nodes.JsonArray("build passed", "tests passed"),
            ["pendingWork"]        = new System.Text.Json.Nodes.JsonArray("completed: run tests"),
        }.ToJsonString();
        Assert.True(context.TryApplyCompaction(completed));
        Assert.True(StructuredState.TryParse(context.BuildModelView()[2].Content, out var state));
        Assert.Equal(["run tests", "tests passed"], state.Decisions);
        Assert.Equal(["completed: run tests"], state.PendingWork);
        Assert.Contains("build passed", state.CommandsAndResults);
    }

    [Fact]
    public void Compaction_RejectsSummaryThatDropsHistoricalFilesOrCommands()
    {
        var context = ContextWithRounds(window : 1000, reserved : 30, rounds : 3, toolResult : new string('x', 1600));
        Assert.NotEmpty(context.BuildCompactionMessages([]));
        Assert.True(context.TryApplyCompaction(new System.Text.Json.Nodes.JsonObject
        {
            ["goal"]               = "goal-g",
            ["constraints"]        = new System.Text.Json.Nodes.JsonArray(),
            ["decisions"]          = new System.Text.Json.Nodes.JsonArray(),
            ["filesInspected"]     = new System.Text.Json.Nodes.JsonArray("src"),
            ["filesModified"]      = new System.Text.Json.Nodes.JsonArray("a.cs"),
            ["commandsAndResults"] = new System.Text.Json.Nodes.JsonArray("build passed"),
            ["pendingWork"]        = new System.Text.Json.Nodes.JsonArray(),
        }.ToJsonString()));

        AppendRound(context, "c4", new string('x', 1600));
        Assert.NotEmpty(context.BuildCompactionMessages([]));
        var droppingFacts = StateJson("goal-g", [], ["src"], []);
        Assert.False(context.TryApplyCompaction(droppingFacts));
        Assert.Contains("a.cs", context.BuildModelView()[2].Content);
        Assert.Contains("build passed", context.BuildModelView()[2].Content);
    }

    [Fact]
    public void Compaction_RejectsSummaryThatSilentlyDropsDecisionOrPendingWork()
    {
        var context = ContextWithRounds(window : 1000, reserved : 30, rounds : 3, toolResult : new string('x', 1600));
        Assert.NotEmpty(context.BuildCompactionMessages([]));
        Assert.True(context.TryApplyCompaction(new System.Text.Json.Nodes.JsonObject
        {
            ["goal"]               = "goal-g",
            ["constraints"]        = new System.Text.Json.Nodes.JsonArray("stay in workspace"),
            ["decisions"]          = new System.Text.Json.Nodes.JsonArray("run tests"),
            ["filesInspected"]     = new System.Text.Json.Nodes.JsonArray("src"),
            ["filesModified"]      = new System.Text.Json.Nodes.JsonArray(),
            ["commandsAndResults"] = new System.Text.Json.Nodes.JsonArray(),
            ["pendingWork"]        = new System.Text.Json.Nodes.JsonArray("rerun tests"),
        }.ToJsonString()));

        AppendRound(context, "c4", new string('x', 1600));
        Assert.NotEmpty(context.BuildCompactionMessages([]));
        var droppingState = new System.Text.Json.Nodes.JsonObject
        {
            ["goal"]               = "goal-g",
            ["constraints"]        = new System.Text.Json.Nodes.JsonArray("updated"),
            ["decisions"]          = new System.Text.Json.Nodes.JsonArray(),
            ["filesInspected"]     = new System.Text.Json.Nodes.JsonArray("src"),
            ["filesModified"]      = new System.Text.Json.Nodes.JsonArray(),
            ["commandsAndResults"] = new System.Text.Json.Nodes.JsonArray(),
            ["pendingWork"]        = new System.Text.Json.Nodes.JsonArray(),
        }.ToJsonString();

        Assert.False(context.TryApplyCompaction(droppingState));
    }

    [Fact]
    public void Compaction_RejectsSummaryThatDoesNotShrinkTheView()
    {
        var context = ContextWithRounds(window : 1000, reserved : 30, rounds : 3, toolResult : new string('x', 1600));
        Assert.NotEmpty(context.BuildCompactionMessages([]));

        // A structurally valid but enormous summary would grow the view instead of
        // shrinking it, so committing it must fail and keep the original view.
        var viewBefore = context.EstimateViewTokens();
        var bloated = $"{{\"goal\":\"{new string('g', 5000)}\",\"constraints\":[],\"decisions\":[]," +
                      $"\"filesInspected\":[],\"filesModified\":[],\"commandsAndResults\":[],\"pendingWork\":[]}}";
        Assert.False(context.TryApplyCompaction(bloated));
        Assert.Equal(viewBefore, context.EstimateViewTokens());
        Assert.Single(context.BuildModelView(), m => m.Role == ChatRole.User);
    }

    [Fact]
    public void TryApplyCompaction_RejectsWhenMessagesWereAppendedAfterThePlan()
    {
        var context = ContextWithRounds(window : 1000, reserved : 30, rounds : 3, toolResult : new string('x', 1600));
        Assert.NotEmpty(context.BuildCompactionMessages([]));

        // The fold plan addresses raw message indexes; an append after planning
        // invalidates it, so the commit is refused and nothing is committed.
        context.Append(ChatMessage.User("late"));
        var viewBefore = context.EstimateViewTokens();
        Assert.False(context.TryApplyCompaction("""{"goal":"g"}"""));
        Assert.Equal(viewBefore, context.EstimateViewTokens());
        Assert.DoesNotContain(context.BuildModelView(), m => m.Role == ChatRole.User && m.Content.Contains("\"goal\""));
    }

    [Fact]
    public void FirstCompaction_ChargesHeadTokens_WhenDecidingWhetherToFold()
    {
        // Before the first fold the head (system + user) stays in the model view after
        // the fold, so planning must charge it. With a large head the planner must
        // conclude there is something to fold instead of assuming everything fits:
        // the total estimate exceeds the window but would previously have been
        // misjudged as foldable-nothing because the head was not charged.
        var context = ContextWithRounds(window : 1000, reserved : 30, rounds : 3,
                                        toolResult : new string('x', 1600), systemPrompt : new string('x', 400));
        Assert.True(context.EstimateViewTokens() + 30 > 1000, "the total estimate must exceed the window");
        Assert.True(context.RequiresCompaction([]));
        Assert.NotEmpty(context.BuildCompactionMessages([]));
    }

    [Fact]
    public void SummaryRequest_CapsOversizedToolResults_AndFitsItsOwnWindow()
    {
        var context = new ConversationContext(Options(1000, 30, toolResultCap : 2048));
        context.Append(ChatMessage.System("sys"));
        context.Append(ChatMessage.User("go"));
        var huge = new string('x', 200_000);
        for (var round = 1; round <= 3; round++)
        {
            AppendRound(context, $"c{round}", huge);
        }

        Assert.True(context.RequiresCompaction([]));
        var summary = context.BuildCompactionMessages([]);

        // The summarizer input must not re-send the raw oversized tool outputs:
        // every folded message is capped like the model view, while the local
        // history keeps the full text.
        var toolMessage = Assert.Single(summary, m => m.Role == ChatRole.Tool);
        Assert.Contains("truncated", toolMessage.Content);
        Assert.True(toolMessage.Content.Length < 5000);
        Assert.All(context.Messages.Where(m => m.Role == ChatRole.Tool),
                   m => Assert.Equal(200_000, m.Content.Length));

        // The summary request is budgeted on its own: its estimated input stays
        // within the declared window.
        Assert.True(TokenEstimator.EstimateMessages(summary) + 30 <= 1000,
                    "the summarizer input must fit its own window");
    }

    [Fact]
    public void ThreeCompactionBatches_FitTheWindowWithoutAppendingHistory()
    {
        var context = ContextWithRounds(window : 1000, reserved : 30, rounds : 8, toolResult : new string('x', 1200));
        var originalMessageCount = context.Messages.Count;

        for (var batch = 0; batch < 3; batch++)
        {
            Assert.True(context.RequiresCompaction([]));
            var summary = context.BuildCompactionMessages([]);
            Assert.NotEmpty(summary);
            Assert.True(TokenEstimator.EstimateMessages(summary) + 30 <= 1000,
                        "each summary request must leave its reserved output inside the window");
            Assert.True(context.TryApplyCompaction("""{"goal":"g"}"""));
            Assert.Equal(originalMessageCount, context.Messages.Count);
        }

        Assert.True(context.EstimateViewTokens() + 30 <= 1000);
    }

    [Fact]
    public void OversizedAtomicGroup_IsNotForcedIntoAnOverBudgetSummary()
    {
        var context = ContextWithRounds(window : 500, reserved : 30, rounds : 3, toolResult : new string('x', 1200));

        Assert.False(context.RequiresCompaction([]));
        Assert.Empty(context.BuildCompactionMessages([]));
    }

    [Fact]
    public void ContextOptions_RejectCompactionThresholdAboveTheWindow()
    {
        var above = new ContextOptions
        {
            ContextWindowTokens       = 1000,
            ReservedOutputTokens      = 100,
            CompactionThresholdTokens = 2000,
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => above.Validate());

        var equal = new ContextOptions
        {
            ContextWindowTokens       = 1000,
            ReservedOutputTokens      = 100,
            CompactionThresholdTokens = 1000,
        };
        Assert.Same(equal, equal.Validate());
    }
}

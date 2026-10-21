using System.Text.Json;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Services.Context;

namespace TinyHarness.Tests;

/// <summary>
///     <para>验证有界交接、原话来源、预算及恢复不变量。</para>
///     <para>Verifies bounded handoffs, original sources, budgets and restoration invariants.</para>
/// </summary>
public class ConversationContextTests
{
    /// <summary>
    ///     <para>建立包含完整工具组的隔离上下文。</para>
    ///     <para>Creates an isolated context containing complete tool groups.</para>
    /// </summary>
    private static ConversationContext Rounds(ContextOptions? options = null, int count = 6, int length = 1600)
    {
        var context = new ConversationContext(options ?? new ContextOptions
        {
            ContextWindowTokens       = 2000,
            ReservedOutputTokens      = 100,
            CompactionThresholdTokens = 1200,
            SummaryTokens             = 128,
            RetainedUserMessageTokens = 64
        });
        context.Append(ChatMessage.System("system"));
        context.Append(ChatMessage.User("Original task: discuss only; do not modify files."));
        for (var i = 0; i < count; i++) AddTool(context, i.ToString(), length);
        return context;
    }

    /// <summary>
    ///     <para>追加一个完整工具原子组。</para>
    ///     <para>Appends one complete atomic tool group.</para>
    /// </summary>
    private static void AddTool(ConversationContext context, string id, int length)
    {
        context.Append(ChatMessage.Assistant("", [new ChatToolCall(id, "read_file", "{}")]));
        context.Append(ChatMessage.Tool("read_file", id, new string('x', length)));
    }

    [Test]
    public void WithoutBudget_PreservesHistoryAndDoesNotPlanCompaction()
    {
        var context = new ConversationContext();
        context.Append(ChatMessage.System("sys"));
        context.Append(ChatMessage.User("No previous summary. Actual user text."));
        Assert.That(context.BuildModelView(), Is.EqualTo(context.Messages));
        Assert.That(context.BuildCompactionMessages([]), Is.Empty);
        Assert.That(context.RequiresCompaction([]), Is.False);
    }

    [Test]
    public void SummaryTaskIsLast_AndToolPairsStayCompleteWithinInputBudget()
    {
        var context = Rounds();
        var request = context.BuildCompactionMessages([]);
        Assert.That(request[0], Is.EqualTo(context.Messages[0]));
        Assert.That(request[1].Content, Does.Contain("Original task"));
        Assert.That(request[^1].Role, Is.EqualTo(ChatRole.User));
        Assert.That(request[^1].Content, Does.Contain("CONTEXT CHECKPOINT COMPACTION"));
        Assert.That(request[^1].Content, Does.Not.Contain("JSON"));
        Assert.That(TokenEstimator.EstimateMessages(request) + 100, Is.LessThanOrEqualTo(2000));
        var calls = request.SelectMany(message => message.ToolCalls ?? []).Select(call => call.Id).ToArray();
        Assert.That(request.Where(message => message.Role == ChatRole.Tool).Select(message => message.ToolCallId),
                    Is.EqualTo(calls));
        Assert.That(calls, Does.Not.Contain("5"));
        Assert.That(calls, Does.Not.Contain("4"));
    }

    [Test]
    public void PlainHandoff_ReplacesTheViewAndPreservesCanonicalHistory()
    {
        var context  = Rounds();
        var original = context.Messages.ToArray();
        var before   = context.EstimateViewTokens();
        context.BuildCompactionMessages([]);
        Assert.That(context.TryApplyCompaction("Only discuss. File A inspected; command denied. Next: confirm requirements."),
                    Is.True);
        Assert.That(context.Messages, Is.EqualTo(original));
        Assert.That(context.EstimateViewTokens(), Is.LessThan(before));
        Assert.That(context.State.RetainedUserMessages.Single().Content, Does.Contain("do not modify"));
        var restored = new ConversationContext();
        restored.Restore(context.Messages, context.State, true, context.Position.FoldStartMessageIndex,
                         context.Position.FoldEndMessageIndex);
        Assert.That(restored.BuildModelView(), Is.EqualTo(context.BuildModelView()));
    }

    [TestCase("")]
    [TestCase("   ")]
    public void EmptySummary_RollsBackAndLatchesUntilHistoryGrows(string text)
    {
        var context = Rounds();
        var before  = context.BuildModelView().ToArray();
        context.BuildCompactionMessages([]);
        Assert.That(context.TryApplyCompaction(text), Is.False);
        Assert.That(context.LastCompactionFailure, Is.EqualTo(CompactionFailureReason.EmptySummary));
        Assert.That(context.BuildModelView(), Is.EqualTo(before));
        Assert.That(context.RequiresCompaction([]), Is.False);
        context.Append(ChatMessage.User("Continue discussion."));
        Assert.That(context.RequiresCompaction([]), Is.True);
    }

    [Test]
    public void OversizedSummary_DoesNotChangeTheCommittedHandoff()
    {
        var context = Rounds();
        context.BuildCompactionMessages([]);
        context.TryApplyCompaction("Initial handoff.");
        AddTool(context, "new1", 1600);
        AddTool(context, "new2", 1600);
        context.BuildCompactionMessages([]);
        var before   = context.State;
        var position = context.Position;
        Assert.That(context.TryApplyCompaction(new string('中', 129)), Is.False);
        Assert.That(context.LastCompactionFailure, Is.EqualTo(CompactionFailureReason.SummaryTooLarge));
        Assert.That(context.State, Is.SameAs(before));
        Assert.That(context.Position, Is.EqualTo(position));
    }

    [Test]
    public void AppendingAfterPlanning_RejectsTheStalePlan()
    {
        var context = Rounds();
        context.BuildCompactionMessages([]);
        context.Append(ChatMessage.User("New restriction."));
        Assert.That(context.TryApplyCompaction("handoff"), Is.False);
        Assert.That(context.LastCompactionFailure, Is.EqualTo(CompactionFailureReason.InvalidPlan));
    }

    [Test]
    public void NoBudgetBenefit_IsRejected()
    {
        var context = Rounds(new ContextOptions
        {
            ContextWindowTokens       = 4000,
            ReservedOutputTokens      = 100,
            CompactionThresholdTokens = 100,
            RetainedUserMessageTokens = 0
        }, length : 10);
        context.BuildCompactionMessages([]);
        Assert.That(context.TryApplyCompaction(new string('z', 2000)), Is.False);
        Assert.That(context.LastCompactionFailure, Is.EqualTo(CompactionFailureReason.NoBudgetBenefit));
    }

    [Test]
    public void UserRetention_PrefersRecentOriginalSuffixAndMarksClipping()
    {
        var context = Rounds(new ContextOptions
        {
            ContextWindowTokens       = 4000,
            ReservedOutputTokens      = 100,
            CompactionThresholdTokens = 600,
            RetainedUserMessageTokens = 32,
            SummaryTokens             = 128
        }, count : 0);
        AddTool(context, "a", 1600);
        context.Append(ChatMessage.User(new string('中', 200) + "LATEST: do not write"));
        for (var i = 0; i < 5; i++) AddTool(context, "b" + i, 1600);
        context.BuildCompactionMessages([]);
        Assert.That(context.TryApplyCompaction("Current goal: discussion only."), Is.True);
        Assert.That(context.State.RetainedUserMessages[^1].Content, Does.EndWith("LATEST: do not write"));
        Assert.That(context.BuildModelView()
                           .Any(message => message.Content.Contains("only its retained suffix follows")), Is.True);
    }

    [Test]
    public void OldSummaryIsNotSelectedAsOriginalUserInput()
    {
        var context = Rounds();
        context.BuildCompactionMessages([]);
        context.TryApplyCompaction("OLD_HANDOFF_MARKER");
        context.Append(ChatMessage.User("New real user restriction."));
        for (var i = 0; i < 5; i++) AddTool(context, "new" + i, 1600);
        var request = context.BuildCompactionMessages([]);
        Assert.That(request.Count(message => message.Content.Contains("OLD_HANDOFF_MARKER")), Is.EqualTo(1));
        Assert.That(context.TryApplyCompaction("UPDATED_HANDOFF_MARKER"), Is.True);
        Assert.That(context.State.RetainedUserMessages.All(entry => !entry.Content.Contains("OLD_HANDOFF_MARKER")),
                    Is.True);
    }

    [Test]
    public void FiftyGrowthCycles_KeepActiveViewBoundedWhileHistoryGrows()
    {
        var context = Rounds(new ContextOptions
        {
            ContextWindowTokens       = 4000,
            ReservedOutputTokens      = 100,
            CompactionThresholdTokens = 2000,
            SummaryTokens             = 96,
            RetainedUserMessageTokens = 64
        }, count : 0);
        var compactions = 0;
        for (var cycle = 0; cycle < 50; cycle++)
        {
            context.Append(ChatMessage.User($"Cycle {cycle}: discuss only, do not write."));
            for (var step = 0; step < 3; step++) AddTool(context, $"{cycle}-{step}", 1600);
            for (var batch = 0; batch < 10 && context.RequiresCompaction([]); batch++)
            {
                context.BuildCompactionMessages([]);
                Assert.That(context.TryApplyCompaction($"Current cycle {cycle}. Only discussion is authorized. Await an explicit modification request."),
                            Is.True);
                compactions++;
            }

            Assert.That(context.EstimateViewTokens() + 100, Is.LessThanOrEqualTo(2500));
        }

        Assert.That(compactions, Is.GreaterThan(20));
        Assert.That(context.Messages.Count, Is.EqualTo(352));
        Assert.That(context.BuildModelView().Count, Is.LessThan(20));
        Assert.That(context.Messages.Count(message => message.Role == ChatRole.Tool), Is.EqualTo(150));
    }

    [Test]
    public void MultipleBatches_RespectEachSummaryInputWindow()
    {
        var context = Rounds(new ContextOptions
        {
            ContextWindowTokens       = 1000,
            ReservedOutputTokens      = 30,
            SummaryTokens             = 64,
            RetainedUserMessageTokens = 0
        }, count : 6, length : 1400);
        var batches = 0;
        while (context.RequiresCompaction([]) && batches < 10)
        {
            var request = context.BuildCompactionMessages([]);
            Assert.That(TokenEstimator.EstimateMessages(request) + 30, Is.LessThanOrEqualTo(1000));
            Assert.That(context.TryApplyCompaction("Goal unchanged; continue inspection."), Is.True);
            batches++;
        }

        Assert.That(batches, Is.GreaterThanOrEqualTo(2));
        Assert.That(context.EstimateViewTokens() + 30, Is.LessThanOrEqualTo(1000));
    }

    [TestCase("schema")]
    [TestCase("assistant-source")]
    [TestCase("forged-text")]
    [TestCase("duplicate-source")]
    public void RestoreRejectsInvalidCheckpointSources(string kind)
    {
        var context = Rounds();
        context.BuildCompactionMessages([]);
        context.TryApplyCompaction("handoff");
        var state = context.State;
        state = kind switch
        {
            "schema" => state with { Schema = "unknown" },
            "assistant-source" => state with { RetainedUserMessages = [new RetainedUserMessage(2, "forged")] },
            "forged-text" => state with { RetainedUserMessages = [new RetainedUserMessage(1, "forged")] },
            _ => state with { RetainedUserMessages = [state.RetainedUserMessages[0], state.RetainedUserMessages[0]] }
        };
        Assert.That(() =>
            new ConversationContext().Restore(context.Messages, state, true, 1, context.Position.FoldEndMessageIndex), Throws.Exception);
    }

    [Test]
    public void CheckpointJson_RoundTripsTextAndSources()
    {
        var state = new CompactionState
        {
            Summary              = "handoff",
            RetainedUserMessages = [new RetainedUserMessage(1, "source")]
        };
        var parsed = JsonSerializer.Deserialize(state.ToJson(), CompactionStateJsonContext.Default.CompactionState)!;
        Assert.That(parsed.Schema, Is.EqualTo(CompactionState.CurrentSchema));
        Assert.That(parsed.Summary, Is.EqualTo(state.Summary));
        Assert.That(parsed.RetainedUserMessages, Is.EqualTo(state.RetainedUserMessages));
    }

    [Test]
    public void UsageCalibrationResetAndTailTruncation_KeepIndependentContracts()
    {
        var context = Rounds();
        var before  = context.EstimateViewTokens();
        context.RecordModelUsage(100, null);
        context.RecordModelUsage(0, 100);
        Assert.That(context.EstimateViewTokens(), Is.EqualTo(before));
        context.RecordModelUsage(100, 200);
        Assert.That(context.EstimateViewTokens(), Is.EqualTo(before * 2));
        context.BuildCompactionMessages([]);
        Assert.That(context.TryApplyCompaction("handoff"), Is.True);
        var position = context.Position;
        context.Append(ChatMessage.User("new input"));
        context.TruncateToMessageCount(context.Messages.Count - 1);
        Assert.That(context.Position, Is.EqualTo(position));
        Assert.That(() => context.TruncateToMessageCount(position.FoldEndMessageIndex - 1), Throws.ArgumentException);
        context.Reset();
        Assert.That(context.Messages, Is.Empty);
        Assert.That(context.Position.HasState, Is.False);
    }

    [TestCase(0, 64)]
    [TestCase(64, -1)]
    public void InvalidHandoffBudgetsAreRejected(int summary, int retained)
    {
        Assert.That(() =>
            new ContextOptions { SummaryTokens = summary, RetainedUserMessageTokens = retained }.Validate(),
                    Throws.TypeOf<ArgumentOutOfRangeException>());
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
}

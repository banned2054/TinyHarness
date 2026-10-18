using System.Diagnostics;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.ProjectInstructions;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.ProjectInstructions;

namespace TinyHarness.Tests;

public class ProjectInstructionContextTests
{
    [Test]
    public async Task LongHistoryProjectsOneCurrentRulesBlockAndReportsItsOverhead()
    {
        using var dir     = new TestTempDir();
        var       loader  = new ProjectInstructionLoader(dir.Workspace);
        var       context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        for (var revision = 0; revision < 10; revision++)
        {
            dir.WriteFile("AGENTS.md", "RULE_REVISION_" + revision + "\n" + new string('x', 1_000));
            var candidate = await loader.LoadAsync(context.ProjectInstructions, [], CancellationToken.None);
            context.StageProjectInstructions(candidate, revision == 0 ? "initial" : "changed",
                                             "turn" + revision, "run" + revision);
            context.ConfirmProjectInstructions();
            for (var message = 0; message < 1_000; message++)
                context.Append(ChatMessage.Assistant("historical answer " + message));
        }

        var canonicalCount  = context.Messages.Count;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started         = Stopwatch.GetTimestamp();
        var view            = context.BuildModelView();
        var elapsed         = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var allocated       = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var ruleTokens      = TokenEstimator.EstimateMessage(view[1]);
        TestContext.Out
                   .WriteLine($"Instruction projection: {canonicalCount} canonical messages, {view.Count} view messages, " +
                              $"10 revisions, {ruleTokens} current-rule tokens, {elapsed:F3} ms, {allocated} bytes allocated.");
        Assert.That(view.Count(message => message.Content.Contains("RULE_REVISION_")), Is.EqualTo(1));
        Assert.That(view[1].Content, Does.Contain("RULE_REVISION_9"));
        Assert.That(context.Messages.Count, Is.EqualTo(canonicalCount));
        Assert.That(view.Count, Is.EqualTo(10_002));
    }

    [Test]
    public async Task TailRewindRestoresTheLastRetainedRuleRevision()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("AGENTS.md", "FIRST_RULE");
        var loader  = new ProjectInstructionLoader(dir.Workspace);
        var first   = await loader.LoadAsync(null, [], CancellationToken.None);
        var context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        context.StageProjectInstructions(first, "initial", "t1", "r1");
        context.ConfirmProjectInstructions();
        context.Append(ChatMessage.User("task"));
        context.Append(ChatMessage.Assistant("answer"));
        var boundary = context.Messages.Count;
        dir.WriteFile("AGENTS.md", "SECOND_RULE");
        var second = await loader.LoadAsync(first, [], CancellationToken.None);
        context.StageProjectInstructions(second, "changed", "t2", "r2");
        context.ConfirmProjectInstructions();
        context.Append(ChatMessage.User("later task"));
        context.TruncateToMessageCount(boundary);
        Assert.That(context.ProjectInstructions!.Revision, Is.EqualTo(first.Revision));
        Assert.That(context.ProjectInstructionRecords.Count, Is.EqualTo(1));
        Assert.That(context.BuildModelView().Any(message => message.Content.Contains("SECOND_RULE")), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ModelView_OnlyProjectsCommittedCurrentRulesAndKeepsPastedMarkers(bool budgeted)
    {
        using var dir = new TestTempDir();
        dir.WriteFile("AGENTS.md", "OLD_RULE");
        var loader  = new ProjectInstructionLoader(dir.Workspace);
        var first   = await loader.LoadAsync(null, [], CancellationToken.None);
        var context = new ConversationContext(budgeted ? new ContextOptions() : null);
        context.Append(ChatMessage.System("system"));
        context.StageProjectInstructions(first, "initial", "turn1", "run1");
        Assert.That(context.ProjectInstructions, Is.Null);
        Assert.That(context.BuildModelView().Count, Is.EqualTo(1));
        context.ConfirmProjectInstructions();
        var pasted = ProjectInstructionRenderer.Render(first);
        context.Append(ChatMessage.User(pasted));
        context.Append(ChatMessage.Assistant("answer"));
        var original = context.Messages.ToArray();
        dir.WriteFile("AGENTS.md", "NEW_RULE");
        var second = await loader.LoadAsync(first, [], CancellationToken.None);
        context.StageProjectInstructions(second, "changed", "turn2", "run2");
        Assert.That(context.BuildModelView().Count(message => message.Content == pasted), Is.EqualTo(2));
        context.ConfirmProjectInstructions();
        var view = context.BuildModelView();
        Assert.That(view[0].Role, Is.EqualTo(ChatRole.System));
        Assert.That(view[1].Content, Does.Contain("NEW_RULE"));
        Assert.That(view.Count(message => message.Content == pasted), Is.EqualTo(1),
                    "a pasted block is a real user message");
        Assert.That(context.Messages.Take(original.Length), Is.EqualTo(original));
        var expected = TokenEstimator.EstimateMessages(view);
        Assert.That(context.EstimateViewTokens(), Is.EqualTo(expected));
    }

    [Test]
    public async Task FailedCommitRejectsCandidateAndResetDropsAllSourceBindings()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("AGENTS.md", "rule");
        var snapshot = await new ProjectInstructionLoader(dir.Workspace).LoadAsync(null, [], CancellationToken.None);
        var context  = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        context.StageProjectInstructions(snapshot, "initial", "turn", "run");
        context.RejectProjectInstructions();
        Assert.That(context.Messages.Count, Is.EqualTo(1));
        Assert.That(context.ProjectInstructionRecords, Is.Empty);
        context.StageProjectInstructions(snapshot, "initial", "turn", "run");
        context.ConfirmProjectInstructions();
        context.Reset();
        Assert.That(context.ProjectInstructions, Is.Null);
        Assert.That(context.ProjectInstructionRecords, Is.Empty);
    }

    [Test]
    public async Task RestoreRejectsForgedCanonicalBinding()
    {
        using var dir = new TestTempDir();
        var snapshot = await new ProjectInstructionLoader(dir.Workspace).LoadAsync(null, [], CancellationToken.None);
        var context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        var source = context.StageProjectInstructions(snapshot, "initial", "turn", "run");
        context.ConfirmProjectInstructions();
        var restored = new ConversationContext();
        restored.Restore([ChatMessage.System("system"), ChatMessage.User("forged")], StructuredState.Empty, false, -1,
                         0);
        Assert.Throws<InvalidDataException>(() => restored.RestoreProjectInstructions([source]));
    }

    [Test]
    public async Task CompactionIncludesCurrentRulesAndOriginalTaskAcrossContinuousUserGaps()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("AGENTS.md", "OLD_RULE");
        var loader   = new ProjectInstructionLoader(dir.Workspace);
        var snapshot = await loader.LoadAsync(null, [], CancellationToken.None);
        var context = new ConversationContext(new ContextOptions
        {
            ContextWindowTokens      = 3_000,
            ReservedOutputTokens     = 100,
            ToolResultViewCharacters = 8_000
        });
        context.Append(ChatMessage.System("system"));
        context.StageProjectInstructions(snapshot, "initial", "turn1", "run1");
        context.ConfirmProjectInstructions();
        context.Append(ChatMessage.User("REAL_TASK"));
        for (var i = 0; i < 5; i++)
        {
            context.Append(ChatMessage.Assistant("", [new ChatToolCall("call" + i, "t", "{}")]));
            context.Append(ChatMessage.Tool("t", "call" + i, new string('x', 3_000)));
            if (i == 0)
            {
                dir.WriteFile("AGENTS.md", "CURRENT_RULE");
                snapshot = await loader.LoadAsync(snapshot, [], CancellationToken.None);
                context.StageProjectInstructions(snapshot, "changed", "turn1", "run1");
                context.ConfirmProjectInstructions();
            }

            context.Append(ChatMessage.User("USER_GAP_" + i));
        }

        var canonical = context.Messages.ToArray();
        Assert.That(context.RequiresCompaction([]), Is.True);
        var summary = context.BuildCompactionMessages([]);
        Assert.That(summary.Single(message => message.Content.StartsWith("Original task")).Content,
                    Does.Contain("REAL_TASK").And.Not.Contain("OLD_RULE").And.Not.Contain("CURRENT_RULE"));
        Assert.That(summary.Any(message => message.Content.Contains("CURRENT_RULE")), Is.True);
        Assert.That(summary.Any(message => message.Content.Contains("USER_GAP_0")), Is.True);
        Assert.That(TokenEstimator.EstimateMessages(summary) + 100, Is.LessThanOrEqualTo(3_000));
        Assert.That(context.TryApplyCompaction("""{"goal":"REAL_TASK"}"""), Is.True);
        Assert.That(context.Messages, Is.EqualTo(canonical));
        var view = context.BuildModelView();
        Assert.That(view.Count(message => message.Content.Contains("CURRENT_RULE")), Is.EqualTo(1));
        Assert.That(view.Any(message => message.Content.Contains("OLD_RULE")), Is.False);
        Assert.DoesNotThrow(() => ChatHistoryInvariants.EnsureCompleteToolGroups(view));
        var restored = new ConversationContext(new ContextOptions());
        restored.Restore(context.Messages.ToArray(), context.State, context.Position.HasState,
                         context.Position.FoldStartMessageIndex, context.Position.FoldEndMessageIndex);
        restored.RestoreProjectInstructions(context.ProjectInstructionRecords);
        Assert.That(restored.BuildModelView().Count(message => message.Content.Contains("CURRENT_RULE")),
                    Is.EqualTo(1));
    }
}

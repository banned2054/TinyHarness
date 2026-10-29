using NUnit.Framework;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Desktop.Runtime;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class DesktopProcessGroupTests
{
    [Test]
    public async Task MixedHistoryKeepsEveryFinalOutsideOneStableProcessGroup()
    {
        await using var manager = new DesktopSessionManager();
        var             page    = new SessionViewModel(manager, new DesktopSessionFactory(), Path.GetTempPath());
        page.ApplyHistory(History("user", "run", SessionHistoryKind.User, "question"));
        page.ApplyHistory(History("commentary", "run", SessionHistoryKind.Process, "checking files"));
        page.ApplyHistory(History("partial-answer", "run", SessionHistoryKind.Final, "first answer"));
        var group = page.Items.OfType<ProcessGroupItemViewModel>().Single();
        Assert.That(group.IsExpanded, Is.False);
        group.ToggleCommand.Execute(null);
        page.ApplyHistory(History("commentary-2", "run", SessionHistoryKind.Process, "checking more"));
        page.ApplyHistory(History("answer", "run", SessionHistoryKind.Final, "last answer") with
        {
            Reasoning = "final reasoning"
        });
        page.ApplyHistory(History("answer", "run", SessionHistoryKind.Final, "last answer"));

        Assert.That(page.Items.OfType<ProcessGroupItemViewModel>().Single(), Is.SameAs(group));
        Assert.That(group.IsExpanded, Is.True, "后续历史页不得重置用户展开状态。");
        Assert.That(group.Items.OfType<MessageItemViewModel>().Select(item => item.Content),
                    Is.EqualTo(new[] { "checking files", "checking more", "" }));
        Assert.That(group.Items.OfType<MessageItemViewModel>().All(item => item.IsInProcessGroup), Is.True);
        Assert.That(group.Items.OfType<MessageItemViewModel>().Last().Reasoning, Is.EqualTo("final reasoning"));
        Assert.That(page.Items.OfType<MessageItemViewModel>().Select(item => item.Content),
                    Is.EqualTo(new[] { "question", "first answer", "last answer" }));
        Assert.That(page.Items.OfType<MessageItemViewModel>().Last().HasVisibleReasoning, Is.False);
        Assert.That(page.Items.OfType<MessageItemViewModel>().Last().HasResponseMetadata, Is.True);
    }

    [Test]
    public async Task ToolFirstHistoryAndRepeatedCallIdsRemainIsolatedByRun()
    {
        await using var manager = new DesktopSessionManager();
        var             page    = new SessionViewModel(manager, new DesktopSessionFactory(), Path.GetTempPath());
        page.ApplyHistory(History("tool-1", "run-1", SessionHistoryKind.Tool) with
        {
            InvocationId = "call-1", ToolName = "read_file", State = "UnknownResult"
        });
        var first = page.Items.OfType<ProcessGroupItemViewModel>().Single();
        var tool  = first.Items.OfType<ToolActivityItemViewModel>().Single();
        tool.IsExpanded = true;
        page.ApplyHistory(History("tool-2", "run-1", SessionHistoryKind.Tool, "file contents") with
        {
            InvocationId = "call-1", ToolName = "read_file", State = "Completed", Succeeded = true
        });
        page.ApplyHistory(History("tool-3", "run-2", SessionHistoryKind.Tool) with
        {
            InvocationId = "call-1", ToolName = "read_file", State = "KnownNotStarted"
        });
        var groups = page.Items.OfType<ProcessGroupItemViewModel>().ToArray();
        Assert.That(groups, Has.Length.EqualTo(2));
        Assert.That(first.Items.Single(), Is.SameAs(tool));
        Assert.That(tool.IsExpanded && tool.IsInProcessGroup && tool.IsSucceeded, Is.True);
        Assert.That(tool.ResultText, Is.EqualTo("file contents"));
        Assert.That(groups[1].Items.Single(), Is.Not.SameAs(tool));
        Assert.That(page.Items.OfType<ToolActivityItemViewModel>(), Is.Empty);
    }

    [TestCase(0, false)]
    [TestCase(1, false)]
    [TestCase(2, true)]
    public void CompletionPreservesUserExpansionChoice(int toggles, bool expanded)
    {
        var group = new ProcessGroupItemViewModel(1, "run", "model · high", true);
        Assert.That(group.IsExpanded && group.HasRunModelLabel, Is.True);
        for (var index = 0; index < toggles; index++) group.ToggleCommand.Execute(null);
        group.Complete();
        Assert.That(group.IsRunning, Is.False);
        Assert.That(group.IsExpanded, Is.EqualTo(expanded));
        group.ToggleCommand.Execute(null);
        group.Complete();
        Assert.That(group.IsExpanded, Is.EqualTo(!expanded), "重复终态不得覆盖运行后手动选择。");
    }

    [Test]
    public async Task MissingRunIdUsesTurnBoundaryAndFinalOnlyRunHasNoEmptyDisclosure()
    {
        await using var manager = new DesktopSessionManager();
        var             page    = new SessionViewModel(manager, new DesktopSessionFactory(), Path.GetTempPath());
        page.ApplyHistory(History("process", "", SessionHistoryKind.Process, "process") with { RunId = null });
        page.ApplyHistory(History("answer", "", SessionHistoryKind.Final, "answer") with { RunId = null });
        page.ApplyHistory(History("answer-2", "next", SessionHistoryKind.Final, "next answer"));
        var groups = page.Items.OfType<ProcessGroupItemViewModel>().ToArray();
        Assert.That(groups, Has.Length.EqualTo(2));
        Assert.That(groups[0].RunId, Is.EqualTo("turn"));
        Assert.That(groups[0].HasItems, Is.True);
        Assert.That(groups[1].HasItems, Is.False);
        Assert.That(page.Items.OfType<MessageItemViewModel>().Select(item => item.Content),
                    Is.EqualTo(new[] { "answer", "next answer" }));
    }

    [Test]
    public async Task ProcessGroupKeepsStatusTailForEveryNonSuccessToolState()
    {
        await using var manager = new DesktopSessionManager();
        var             page    = new SessionViewModel(manager, new DesktopSessionFactory(), Path.GetTempPath());
        page.ApplyHistory(History("tool-unknown", "run", SessionHistoryKind.Tool) with
        {
            InvocationId = "call-1", ToolName = "apply_patch", State = "UnknownResult"
        });
        page.ApplyHistory(History("tool-denied", "run", SessionHistoryKind.Tool) with
        {
            InvocationId = "call-2", ToolName = "shell",
            State = "KnownNotStarted", Approval = "Permission denied"
        });
        page.ApplyHistory(History("tool-notstarted", "run", SessionHistoryKind.Tool) with
        {
            InvocationId = "call-3", ToolName = "read_file", State = "KnownNotStarted"
        });
        page.ApplyHistory(History("tool-failed", "run", SessionHistoryKind.Tool) with
        {
            InvocationId = "call-4", ToolName = "shell", State = "Failed"
        });
        page.ApplyHistory(History("tool-done", "run", SessionHistoryKind.Tool, "contents") with
        {
            InvocationId = "call-5", ToolName = "read_file", State = "Completed", Succeeded = true
        });
        var tools = page.Items.OfType<ProcessGroupItemViewModel>().Single()
                        .Items.OfType<ToolActivityItemViewModel>().ToArray();
        Assert.That(tools.Select(item => item.StatusText),
                    Is.EqualTo(new[] { "结果未知", "已拒绝", "未开始", "失败", "已完成" }));
        Assert.That(tools.Take(4).All(item => item.IsInProcessGroup && item.ShowStatusText), Is.True,
                    "组内异常与未定状态必须保留状态尾注。");
        Assert.That(tools[4].ShowStatusText, Is.False, "组内成功工具仍隐藏状态尾注。");
    }

    private static SessionHistoryItem History(string id, string run, string kind, string? text = null) => new()
    {
        Id = id, SessionId = "session", TurnId = "turn", RunId = run, Kind = kind, Text = text
    };
}

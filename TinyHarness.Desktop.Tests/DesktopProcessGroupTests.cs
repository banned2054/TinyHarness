using NUnit.Framework;
using System.Collections.Specialized;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Desktop.Models;
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

    [Test]
    public void AdjacentReasoningAndToolsFormCollapsedSegmentsAroundBodyMessages()
    {
        var group         = new ProcessGroupItemViewModel(1, "run", "model", true);
        var firstThought  = Message(1, string.Empty, "inspect the request");
        var firstTool     = Tool(2, "read_file", ToolActivityStatus.Failed);
        var separator     = Message(3, "I found the relevant file.", "check the path");
        var secondTool    = Tool(4, "search_text");
        var secondThought = Message(5, string.Empty, "compare the results");

        group.Items.Add(firstThought);
        group.Items.Add(firstTool);
        group.Items.Add(separator);
        group.Items.Add(secondTool);
        group.Items.Add(secondThought);

        Assert.That(group.SegmentedItems, Has.Count.EqualTo(3));
        var segments = group.SegmentedItems.OfType<ProcessSegmentItemViewModel>().ToArray();
        Assert.That(segments, Has.Length.EqualTo(2));
        Assert.That(group.SegmentedItems[1], Is.SameAs(separator));
        Assert.That(segments.All(segment => !segment.IsExpanded), Is.True);
        Assert.That(segments[0].Items[0], Is.SameAs(firstThought));
        Assert.That(segments[0].Items[1], Is.SameAs(firstTool));
        Assert.That(segments[0].Items[2], Is.SameAs(separator.GetProcessReasoningProjection()));
        Assert.That(separator.HasVisibleReasoning, Is.False);
        Assert.That(segments[0].SummaryText, Does.Contain(firstTool.Title));
        Assert.That(segments[0].SummaryText, Does.Not.Contain(firstTool.StatusText));
        Assert.That(firstTool.StatusText, Is.EqualTo("失败"));
        Assert.That(segments[1].Items, Does.Contain(secondTool));
        Assert.That(segments[1].Items, Does.Contain(secondThought));

        foreach (var segment in segments) segment.ToggleCommand.Execute(null);
        group.Items.Add(Message(6, "Next update."));
        group.Items.Add(Tool(7, "apply_patch"));

        var rebuiltSegments = group.SegmentedItems.OfType<ProcessSegmentItemViewModel>().ToArray();
        Assert.That(rebuiltSegments, Has.Length.EqualTo(3));
        Assert.That(rebuiltSegments.Take(2).All(segment => segment.IsExpanded), Is.True,
                    "重建分段时保留用户对已有段的展开选择。");
        Assert.That(rebuiltSegments[2].IsExpanded, Is.False, "新过程段默认收起。");
    }

    [Test]
    public void StreamingBodyBecomesBoundaryAndItsReasoningStaysInCollapsedSegment()
    {
        var group  = new ProcessGroupItemViewModel(1, "run", "model", true);
        var stream = MessageItemViewModel.CreateStreaming();
        group.Items.Add(stream);
        var tool = Tool(2, "read_file");
        group.Items.Add(tool);

        stream.AppendReasoning("thinking ");
        var segment = group.SegmentedItems.OfType<ProcessSegmentItemViewModel>().Single();
        segment.ToggleCommand.Execute(null);

        stream.AppendText("intermediate update");

        Assert.That(group.SegmentedItems, Has.Count.EqualTo(3));
        Assert.That(group.SegmentedItems[1], Is.SameAs(stream));
        Assert.That(stream.HasVisibleReasoning, Is.False);
        var rebuiltSegments = group.SegmentedItems.OfType<ProcessSegmentItemViewModel>().ToArray();
        Assert.That(rebuiltSegments, Has.Length.EqualTo(2));
        Assert.That(rebuiltSegments[1].Items, Does.Contain(tool));
        Assert.That(rebuiltSegments.All(item => item.IsExpanded), Is.True,
                    "流式正文拆开已展开的段时，原段中的两部分都保留展开选择。");
        var reasoningProjection = rebuiltSegments[0].Items.OfType<MessageItemViewModel>().Single();
        Assert.That(reasoningProjection.IsReasoningExpanded, Is.False);
        Assert.That(reasoningProjection.Reasoning, Is.EqualTo("thinking "));

        stream.AppendReasoning("more");
        Assert.That(reasoningProjection.Reasoning, Is.EqualTo("thinking more"));
    }

    [Test]
    public void MergedSegmentKeepsExpandedChoiceFromAnyPreviousSegment()
    {
        var group     = new ProcessGroupItemViewModel(1, "run", "model", true);
        var firstTool = Tool(1, "read_file");
        var separator = Message(2, "Intermediate update.");
        var lastTool  = Tool(3, "search_text");
        group.Items.Add(firstTool);
        group.Items.Add(separator);
        group.Items.Add(lastTool);

        group.SegmentedItems.OfType<ProcessSegmentItemViewModel>().Last().ToggleCommand.Execute(null);
        group.Items.Remove(separator);

        Assert.That(group.SegmentedItems.OfType<ProcessSegmentItemViewModel>().Single().IsExpanded, Is.True,
                    "合并段时保留任一旧段的展开选择。");
    }

    [Test]
    public void SegmentSummaryDeduplicatesToolTitlesAndKeepsStatusesOnContentItems()
    {
        var items = new ConversationItemViewModel[]
        {
            Message(1, string.Empty, "thinking"),
            Tool(2, "shell"),
            Tool(3, "pwsh", ToolActivityStatus.NotStarted),
            Tool(4, "bash", ToolActivityStatus.Failed),
            Tool(5, "read_file", ToolActivityStatus.NotStarted),
            Tool(6, "read_file"),
            Tool(7, "search_text", ToolActivityStatus.Unknown),
            Tool(8, "search_text", ToolActivityStatus.Unknown)
        };
        var segment = new ProcessSegmentItemViewModel(items);

        Assert.That(segment.SummaryText,
                    Is.EqualTo("已思考 · 运行命令 · 读取文件 · 搜索文本"));
        Assert.That(segment.Items.OfType<ToolActivityItemViewModel>().Select(tool => tool.StatusText),
                    Is.EqualTo(new[] { "已完成", "未开始", "失败", "未开始", "已完成", "结果未知", "结果未知" }));
    }

    [Test]
    public void AppendingToolOnlyAddsItsChildAndKeepsExistingSegments()
    {
        var group = new ProcessGroupItemViewModel(1, "run", "model", true);
        group.Items.Add(Message(1, string.Empty, "thinking"));
        group.Items.Add(Message(2, "update"));
        group.Items.Add(Tool(3, "read_file"));
        var first = (ProcessSegmentItemViewModel)group.SegmentedItems[0];
        var last  = (ProcessSegmentItemViewModel)group.SegmentedItems[2];
        last.IsExpanded = true;
        var outerChanges = new List<NotifyCollectionChangedAction>();
        var childChanges = new List<NotifyCollectionChangedAction>();
        group.SegmentedItems.CollectionChanged += (_, change) => outerChanges.Add(change.Action);
        last.Items.CollectionChanged           += (_, change) => childChanges.Add(change.Action);

        var next = Tool(4, "search_text");
        group.Items.Add(next);

        Assert.That(group.SegmentedItems[0], Is.SameAs(first));
        Assert.That(group.SegmentedItems[2], Is.SameAs(last));
        Assert.That(last.IsExpanded, Is.True);
        Assert.That(last.Items.Last(), Is.SameAs(next));
        Assert.That(outerChanges, Is.Empty);
        Assert.That(childChanges, Is.EqualTo(new[] { NotifyCollectionChangedAction.Add }));
        Assert.That(last.SummaryText, Is.EqualTo("读取文件 · 搜索文本"));
    }

    [Test]
    public void SettlingToolUpdatesItsSummaryWithoutChangingAnyCollection()
    {
        var group = new ProcessGroupItemViewModel(1, "run", "model", true);
        group.Items.Add(Message(1, string.Empty, "thinking"));
        var body = Message(2, "already rendered body");
        group.Items.Add(body);
        var tool = Tool(3, "read_file", ToolActivityStatus.Unknown);
        group.Items.Add(tool);
        var before  = group.SegmentedItems.ToArray();
        var changes = new List<NotifyCollectionChangedAction>();
        group.SegmentedItems.CollectionChanged += (_, change) => changes.Add(change.Action);
        foreach (var segment in before.OfType<ProcessSegmentItemViewModel>())
        {
            segment.IsExpanded              =  true;
            segment.Items.CollectionChanged += (_, change) => changes.Add(change.Action);
        }

        tool.Settle(new ToolActivity(3, "call-3", "search_text", "{}", ToolActivityStatus.Succeeded,
                                     "result", null, DateTimeOffset.UnixEpoch));

        Assert.That(changes, Is.Empty);
        for (var index = 0; index < before.Length; index++)
            Assert.That(group.SegmentedItems[index], Is.SameAs(before[index]));
        Assert.That(group.SegmentedItems[1], Is.SameAs(body));
        Assert.That(((ProcessSegmentItemViewModel)group.SegmentedItems[2]).SummaryText, Is.EqualTo("搜索文本"));
        Assert.That(tool.IsSucceeded, Is.True);
    }

    [TestCase(0, false)]
    [TestCase(1, true)]
    [TestCase(2, false)]
    public void FirstBodyPreservesReasoningChoiceAndReusesItsSegment(int toggles, bool expanded)
    {
        var group  = new ProcessGroupItemViewModel(1, "run", "model", true);
        var stream = MessageItemViewModel.CreateStreaming();
        group.Items.Add(stream);
        stream.AppendReasoning("thinking");
        var segment = (ProcessSegmentItemViewModel)group.SegmentedItems.Single();
        segment.IsExpanded = true;
        for (var index = 0; index < toggles; index++) stream.ToggleReasoningCommand.Execute(null);
        var changes = new List<NotifyCollectionChangedAction>();
        group.SegmentedItems.CollectionChanged += (_, change) => changes.Add(change.Action);

        stream.AppendText("body");

        Assert.That(group.SegmentedItems[0], Is.SameAs(segment));
        Assert.That(segment.IsExpanded, Is.True);
        var reasoning = segment.Items.OfType<MessageItemViewModel>().Single();
        Assert.That(reasoning.IsReasoningExpanded, Is.EqualTo(expanded));
        Assert.That(group.SegmentedItems[1], Is.SameAs(stream));
        Assert.That(changes, Is.EqualTo(new[] { NotifyCollectionChangedAction.Add }));
        stream.AppendReasoning(" more");
        Assert.That(reasoning.Reasoning, Is.EqualTo("thinking more"));
    }

    [Test]
    public void ReplacingBodyKeepsNeighboringSegmentsAndTheirSeparateChoices()
    {
        var group = new ProcessGroupItemViewModel(1, "run", "model", true);
        group.Items.Add(Tool(1, "read_file"));
        group.Items.Add(Message(2, "live body"));
        group.Items.Add(Tool(3, "search_text"));
        var first = (ProcessSegmentItemViewModel)group.SegmentedItems[0];
        var last  = (ProcessSegmentItemViewModel)group.SegmentedItems[2];
        first.IsExpanded = true;
        var changes = new List<NotifyCollectionChangedAction>();
        group.SegmentedItems.CollectionChanged += (_, change) => changes.Add(change.Action);

        var history = Message(4, "recorded body");
        group.Items[1] = history;

        Assert.That(group.SegmentedItems[0], Is.SameAs(first));
        Assert.That(group.SegmentedItems[1], Is.SameAs(history));
        Assert.That(group.SegmentedItems[2], Is.SameAs(last));
        Assert.That(first.IsExpanded, Is.True);
        Assert.That(last.IsExpanded, Is.False);
        Assert.That(changes, Is.EqualTo(new[] { NotifyCollectionChangedAction.Replace }));
    }

    private static MessageItemViewModel Message(long seq, string content, string? reasoning = null) =>
        new(new ConversationMessage(seq, $"message-{seq}", MessageRole.Assistant, content,
                                    DateTimeOffset.UnixEpoch, Reasoning : reasoning));

    private static ToolActivityItemViewModel Tool(
        long seq, string name, ToolActivityStatus status = ToolActivityStatus.Succeeded) =>
        new(new ToolActivity(seq, $"call-{seq}", name, "{}", status, null, null, DateTimeOffset.UnixEpoch));

    private static SessionHistoryItem History(string id, string run, string kind, string? text = null) => new()
    {
        Id = id, SessionId = "session", TurnId = "turn", RunId = run, Kind = kind, Text = text
    };
}

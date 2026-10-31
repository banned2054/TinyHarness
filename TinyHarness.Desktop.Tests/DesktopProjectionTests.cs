using NUnit.Framework;
using System.Runtime.CompilerServices;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Session;
using TinyHarness.Desktop.Runtime;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class DesktopProjectionTests
{
    [Test]
    public async Task IndependentPagesKeepNewDraftsAndNeverRouteAnotherSessionsOutput()
    {
        var fixture  = await Fixture.CreateAsync();
        var enteredA = Signal();
        var enteredB = Signal();
        var finishA  = Signal();
        var finishB  = Signal();
        var factory = new DesktopSessionFactory((config, _) => config.WorkspaceRoot == fixture.First
                                                    ? new Model(enteredA, finishA, "response-a")
                                                    : new Model(enteredB, finishB, "response-b"),
                                                new Credentials(), fixture.Config);
        await using var manager = new DesktopSessionManager();
        var             first   = Page(manager, factory, fixture.First);
        var             second  = Page(manager, factory, fixture.Second);
        first.Draft = "request-a";
        await first.SendAsync();
        await enteredA.Task.WaitAsync(TimeSpan.FromSeconds(10));
        second.Draft = "request-b";
        await second.SendAsync();
        await enteredB.Task.WaitAsync(TimeSpan.FromSeconds(10));
        first.Draft  = "next-a";
        second.Draft = "next-b";
        finishA.SetResult();
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(second.IsRunning, Is.True);
        Assert.That(first.Draft, Is.EqualTo("next-a"));
        Assert.That(second.Draft, Is.EqualTo("next-b"));
        finishB.SetResult();
        await second.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(Text(first), Does.Contain("response-a").And.Not.Contain("response-b"));
        Assert.That(Text(second), Does.Contain("response-b").And.Not.Contain("response-a"));
    }

    [Test]
    public async Task PersistedAnswerReplacesStreamWithOriginalTextWithoutDuplicatesAndSurvivesHistoryReload()
    {
        var fixture = await Fixture.CreateAsync();
        var finish  = Signal();
        finish.SetResult();
        var factory = new DesktopSessionFactory((_, _) => new Model(Signal(), finish, "answer fixture-secret-key done"),
                                                new Credentials(), fixture.Config);
        await using var manager = new DesktopSessionManager();
        var             page    = Page(manager, factory, fixture.First);
        page.Draft = "request";
        await page.SendAsync();
        await page.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var messages = page.Items.OfType<MessageItemViewModel>().ToArray();
        Assert.That(messages, Has.Length.EqualTo(2));
        Assert.That(messages[^1].Content, Is.EqualTo("answer fixture-secret-key done"));
        Assert.That(messages.Any(message => message.IsStreaming || message.IsInterrupted), Is.False);

        var browse = new SessionViewModel(manager, factory, fixture.First, page.Id, fixture.Store,
                                          dispatch : action =>
                                          {
                                              action();
                                              return ValueTask.CompletedTask;
                                          });
        await browse.LoadHistoryAsync();
        var reloaded = browse.Items.OfType<MessageItemViewModel>().ToArray();
        Assert.That(reloaded, Has.Length.EqualTo(2));
        Assert.That(reloaded[^1].Content, Is.EqualTo(messages[^1].Content));
    }

    [Test]
    public async Task LiveTextAndReasoningPreserveOriginalContentWhileDiagnosticsRedactKeys()
    {
        var fixture = await Fixture.CreateAsync();
        var finish  = Signal();
        finish.SetResult();
        var requests = 0;
        var factory = new DesktopSessionFactory((_, _) => new Model(Signal(), finish,
                                                                    "answer fixture-secret-key done",
                                                                    "thinking fixture-secret-key done", _ =>
                                                                    {
                                                                        if (++requests > 1)
                                                                            throw new
                                                                                InvalidOperationException("failed fixture-secret-key");
                                                                    }), new Credentials(), fixture.Config);
        var events = new List<DesktopEvent>();
        await using var session =
            await factory.OpenAsync(new DesktopSessionRequest(fixture.First, ShowReasoning : true),
                                    update =>
                                    {
                                        events.Add(update);
                                        return ValueTask.CompletedTask;
                                    }, _ => ValueTask.CompletedTask, CancellationToken.None);
        await session.SubmitAsync("first");
        Assert.That(string.Concat(events.Where(item => item.Kind == "text").Select(item => item.Text)),
                    Is.EqualTo("answer fixture-secret-key done"));
        Assert.That(string.Concat(events.Where(item => item.Kind == "reasoning").Select(item => item.Text)),
                    Is.EqualTo("thinking fixture-secret-key done"));
        events.Clear();
        await session.SubmitAsync("second");
        var errors = events.Where(item => item.Kind == "error").Select(item => item.Text).ToArray();
        Assert.That(errors, Is.Not.Empty);
        Assert.That(string.Join('\n', errors), Does.Contain("[REDACTED]").And.Not.Contain("fixture-secret-key"));
    }

    [Test]
    public async Task ActiveSendUsesDurableAmendmentAndDoesNotCreateAnOverlappingTurn()
    {
        var fixture = await Fixture.CreateAsync();
        var entered = Signal();
        var finish  = Signal();
        var factory = new DesktopSessionFactory((_, _) => new Model(entered, finish, "offline answer"),
                                                new Credentials(), fixture.Config);
        await using var manager = new DesktopSessionManager();
        var             page    = Page(manager, factory, fixture.First);
        page.Draft = "original";
        await page.SendAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        page.Draft = "additional constraint";
        await page.SendAsync();
        Assert.That(page.Draft, Is.Empty);
        finish.SetResult();
        await page.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var saved = await SessionStoreReader.ReadAsync(fixture.Store, page.Id!, CancellationToken.None);
        Assert.That(saved!.Turns, Has.Count.EqualTo(1));
        Assert.That(saved.Amendments, Has.Count.EqualTo(1));
        Assert.That(saved.Amendments[0].Text, Is.EqualTo("additional constraint"));
        Assert.That(saved.Amendments[0].CanonicalMessageIndex, Is.Not.Null);
    }

    [TestCase(2, false)]
    [TestCase(2, true)]
    [TestCase(101, false)]
    public async Task DividerAppearsOncePerRunAcrossToolsStreamingCancellationAndHistoryPages(
        int toolRounds, bool cancel)
    {
        var fixture      = await Fixture.CreateAsync();
        var finalEntered = Signal();
        var finish       = Signal();
        var factory = new DesktopSessionFactory((_, _) => new MultiStepModel(toolRounds, finalEntered, finish),
                                                new Credentials(), fixture.Config);
        await using var manager = new DesktopSessionManager();
        var             page    = Page(manager, factory, fixture.First);
        var             streams = new List<MessageItemViewModel>();
        page.Items.CollectionChanged += (_, change) =>
        {
            if (change.NewItems is null) return;
            foreach (var group in change.NewItems.OfType<ProcessGroupItemViewModel>())
                group.Items.CollectionChanged += (_, added) =>
                {
                    if (added.NewItems is not null)
                        streams.AddRange(added.NewItems.OfType<MessageItemViewModel>()
                                              .Where(message => message.IsStreaming));
                };
        };
        page.Draft = "first run";
        await page.SendAsync();
        await finalEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var firstGroup = page.Items.OfType<ProcessGroupItemViewModel>().Single();
        Assert.That(firstGroup.HasRunModelLabel, Is.True);
        Assert.That(firstGroup.IsExpanded && firstGroup.IsRunning, Is.True);
        Assert.That(streams, Has.Count.EqualTo(toolRounds + 1));
        Assert.That(streams.All(message => message.IsInProcessGroup), Is.True);
        if (cancel) page.CancelCommand.Execute(null);
        else finish.SetResult();
        await page.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.That(page.Status, Is.EqualTo(cancel ? "Cancelled" : "Completed"));
        Assert.That(page.Items.OfType<ProcessGroupItemViewModel>().Single(), Is.SameAs(firstGroup));
        Assert.That(firstGroup.IsExpanded || firstGroup.IsRunning, Is.False);
        Assert.That(firstGroup.Items.OfType<ToolActivityItemViewModel>().Count(), Is.EqualTo(toolRounds));
        Assert.That(firstGroup.Items.OfType<MessageItemViewModel>().Any(message => message.IsInterrupted),
                    Is.EqualTo(cancel));
        Assert.That(page.Items.OfType<MessageItemViewModel>().Count(message => message.IsAssistantMessage),
                    Is.EqualTo(cancel ? 0 : 1));
        finish.TrySetResult();
        page.Draft = "second run";
        await page.SendAsync();
        await page.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.That(page.Status, Is.EqualTo("Completed"));
        Assert.That(page.Items.OfType<ProcessGroupItemViewModel>().Count(), Is.EqualTo(2));
        Assert.That(page.Items.OfType<ProcessGroupItemViewModel>().Count(group => group.HasRunModelLabel),
                    Is.EqualTo(2));
        var browse = new SessionViewModel(manager, factory, fixture.First, page.Id, fixture.Store,
                                          dispatch : action =>
                                          {
                                              action();
                                              return ValueTask.CompletedTask;
                                          });
        var pages = 0;
        do
        {
            await browse.LoadHistoryAsync();
            pages++;
        } while (browse.HasMoreHistory);

        Assert.That(browse.Items.OfType<ProcessGroupItemViewModel>().Count(), Is.EqualTo(2));
        Assert.That(browse.Items.OfType<ProcessGroupItemViewModel>().Count(group => group.HasRunModelLabel),
                    Is.EqualTo(2));
        Assert.That(browse.Items.OfType<MessageItemViewModel>().Count(message => message.IsAssistantMessage),
                    Is.EqualTo(cancel ? 1 : 2));
        Assert.That(browse.Items.OfType<ProcessGroupItemViewModel>()
                          .Sum(group => group.Items.OfType<ToolActivityItemViewModel>().Count()),
                    Is.EqualTo(2 * toolRounds));
        Assert.That(browse.Items.OfType<ProcessGroupItemViewModel>().All(group => !group.IsExpanded), Is.True);
        if (!cancel)
        {
            var liveProcess = page.Items.OfType<ProcessGroupItemViewModel>()
                                  .SelectMany(group => group.Items).OfType<MessageItemViewModel>()
                                  .Select(message => (message.Content, message.Reasoning)).ToArray();
            var savedProcess = browse.Items.OfType<ProcessGroupItemViewModel>()
                                     .SelectMany(group => group.Items).OfType<MessageItemViewModel>()
                                     .Select(message => (message.Content, message.Reasoning)).ToArray();
            Assert.That(savedProcess, Is.EqualTo(liveProcess));
        }

        if (toolRounds > 100) Assert.That(pages, Is.GreaterThan(1));
    }

    [TestCase(0, false, false)]
    [TestCase(1, true, false)]
    [TestCase(2, false, false)]
    [TestCase(0, false, true)]
    [TestCase(1, true, true)]
    [TestCase(2, false, true)]
    public async Task ReasoningIsShownByDefaultAndPreservesOriginalTextAcrossHistoryReload(
        int toggles, bool expanded, bool chooseAfterBody)
    {
        var fixture = await Fixture.CreateAsync();
        var finish  = Signal();
        finish.SetResult();
        var factory = new DesktopSessionFactory((_, _) => new Model(Signal(), finish, "offline answer",
                                                                    "thinking about fixture-secret-key before answering"),
                                                new Credentials(), fixture.Config);
        await using var              manager                 = new DesktopSessionManager();
        var                          page                    = Page(manager, factory, fixture.First);
        string?                      streamedReasoning       = null;
        var                          expandedStreamedSegment = false;
        ProcessSegmentItemViewModel? streamedSegment         = null;
        var                          choseProjectedReasoning = false;
        page.Items.CollectionChanged += (_, args) =>
        {
            if (args.NewItems is null) return;
            foreach (var group in args.NewItems.OfType<ProcessGroupItemViewModel>())
                group.Items.CollectionChanged += (_, added) =>
                {
                    if (added.NewItems is null) return;
                    foreach (var message in added.NewItems.OfType<MessageItemViewModel>())
                        message.PropertyChanged += (_, change) =>
                        {
                            if (message.IsStreaming && change.PropertyName == nameof(MessageItemViewModel.Reasoning))
                            {
                                streamedReasoning = message.Reasoning;
                                var segment = page.Items.OfType<ProcessGroupItemViewModel>().Single()
                                                  .SegmentedItems.OfType<ProcessSegmentItemViewModel>().Single();
                                if (!segment.IsExpanded) segment.ToggleCommand.Execute(null);
                                if (!expandedStreamedSegment)
                                {
                                    streamedSegment = segment;
                                    if (!chooseAfterBody)
                                        for (var index = 0; index < toggles; index++)
                                            message.ToggleReasoningCommand.Execute(null);
                                }

                                expandedStreamedSegment = true;
                            }

                            if (chooseAfterBody        &&
                                message.IsStreaming    &&
                                message.HasProcessBody &&
                                change.PropertyName == nameof(MessageItemViewModel.HasProcessBody))
                            {
                                var reasoning = message.GetProcessReasoningProjection()!;
                                for (var index = 0; index < toggles; index++)
                                    reasoning.ToggleReasoningCommand.Execute(null);
                                choseProjectedReasoning = true;
                            }
                        };
                };
        };
        page.Draft = "request";
        await page.SendAsync();
        await page.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var settled = page.Items.OfType<MessageItemViewModel>().Last(message => message.IsAssistantMessage);
        Assert.That(settled.HasReasoning, Is.True);
        Assert.That(settled.HasVisibleReasoning, Is.False);
        var processReasoning = page.Items.OfType<ProcessGroupItemViewModel>().Single().Items
                                   .OfType<MessageItemViewModel>().Single();
        Assert.That(expandedStreamedSegment, Is.True, "流式推理期间展开了过程段。");
        Assert.That(choseProjectedReasoning, Is.EqualTo(chooseAfterBody));
        Assert.That(page.Items.OfType<ProcessGroupItemViewModel>().Single().SegmentedItems.Single(),
                    Is.SameAs(streamedSegment), "正式历史替换临时流时保留过程段实例。");
        Assert.That(page.Items.OfType<ProcessGroupItemViewModel>().Single().SegmentedItems
                        .OfType<ProcessSegmentItemViewModel>().Single().IsExpanded,
                    Is.True, "历史推理条目替换临时流时保留过程段展开选择。");
        Assert.That(processReasoning.HasVisibleReasoning, Is.True);
        Assert.That(processReasoning.Content, Is.Empty);
        Assert.That(processReasoning.Reasoning, Is.EqualTo(settled.Reasoning));
        Assert.That(settled.Reasoning, Is.EqualTo("thinking about fixture-secret-key before answering"));
        Assert.That(streamedReasoning, Is.EqualTo(settled.Reasoning));
        Assert.That(settled.ReasoningSummary, Is.Not.Null);
        Assert.That(processReasoning.IsReasoningExpanded, Is.EqualTo(expanded));
        processReasoning.ToggleReasoningCommand.Execute(null);
        Assert.That(processReasoning.IsReasoningExpanded, Is.EqualTo(!expanded));
        processReasoning.ToggleReasoningCommand.Execute(null);
        Assert.That(processReasoning.IsReasoningExpanded, Is.EqualTo(expanded));
        Assert.That(settled.Reasoning, Is.EqualTo("thinking about fixture-secret-key before answering"));
        var browse = new SessionViewModel(manager, factory, fixture.First, page.Id, fixture.Store,
                                          dispatch : action =>
                                          {
                                              action();
                                              return ValueTask.CompletedTask;
                                          });
        await browse.LoadHistoryAsync();
        var reloaded = browse.Items.OfType<MessageItemViewModel>().Last(message => message.IsAssistantMessage);
        Assert.That(reloaded.HasReasoning, Is.True);
        Assert.That(reloaded.HasVisibleReasoning, Is.False);
        Assert.That(browse.Items.OfType<ProcessGroupItemViewModel>().Single().Items
                          .OfType<MessageItemViewModel>().Single().Reasoning, Is.EqualTo(settled.Reasoning));
        Assert.That(reloaded.Reasoning, Is.EqualTo(settled.Reasoning));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task InterruptedStreamStaysInProcessAndNeverBecomesAFinalAnswer(bool cancel)
    {
        var fixture = await Fixture.CreateAsync();
        var entered = Signal();
        var finish  = Signal();
        var factory = new DesktopSessionFactory((_, _) => new InterruptedModel(entered, finish),
                                                new Credentials(), fixture.Config);
        await using var manager = new DesktopSessionManager();
        var             page    = Page(manager, factory, fixture.First);
        page.Draft = "request";
        await page.SendAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var group  = page.Items.OfType<ProcessGroupItemViewModel>().Single();
        var stream = group.Items.OfType<MessageItemViewModel>().Single();
        Assert.That(group.IsExpanded && group.IsRunning && stream.IsStreaming, Is.True);
        Assert.That(stream.Content, Is.EqualTo("partial text"));
        Assert.That(stream.Reasoning, Is.EqualTo("partial thinking"));
        group.ToggleCommand.Execute(null);
        group.ToggleCommand.Execute(null);
        if (cancel) page.CancelCommand.Execute(null);
        else finish.SetResult();
        await page.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(group.IsExpanded, Is.True, "用户显式展开必须跨终态保留。");
        Assert.That(group.IsRunning || stream.IsStreaming, Is.False);
        Assert.That(stream.IsInterrupted && stream.IsInProcessGroup, Is.True);
        Assert.That(stream.Content, Is.EqualTo("partial text"));
        Assert.That(page.Items.OfType<MessageItemViewModel>().Any(item => item.IsAssistantMessage), Is.False);
        Assert.That(page.Status, Is.EqualTo(cancel ? "Cancelled" : "Failed"));
        if (!cancel)
        {
            Assert.That(page.Notice, Does.Contain("offline failure"));
            var browse = new SessionViewModel(manager, factory, fixture.First, page.Id, fixture.Store,
                                              dispatch : action =>
                                              {
                                                  action();
                                                  return ValueTask.CompletedTask;
                                              });
            await browse.LoadHistoryAsync();
            Assert.That(browse.Notice, Does.Contain("offline failure"));
            page.Draft = "retry";
            await page.SendAsync();
            await page.Completion;
            Assert.That(page.Status, Is.EqualTo("Completed"));
            Assert.That(page.Notice, Is.Empty);
        }
    }

    [TestCase(false, false, false)]
    [TestCase(false, true, false)]
    [TestCase(true, false, false)]
    [TestCase(true, true, false)]
    [TestCase(true, false, true)]
    [TestCase(true, true, true)]
    public async Task ResponseTextItemsKeepAllAnswersWhenNavigatingAwayAndReloading(
        bool phases, bool tools, bool cancel)
    {
        var fixture = await Fixture.CreateAsync();
        var entered = Signal();
        var finish  = Signal();
        var factory = new DesktopSessionFactory((_, _) => new PhaseModel(phases, tools, entered, finish),
                                                new Credentials(), fixture.Config);
        await using var window = new MainWindowViewModel(factory, fixture.Config, action =>
        {
            action();
            return ValueTask.CompletedTask;
        });
        var page = window.SelectedSession!;
        await page.SelectProjectAsync(null, fixture.First, fixture.Store);
        var removedStreams = 0;
        page.Items.CollectionChanged += (_, added) =>
        {
            if (added.NewItems is null) return;
            foreach (var group in added.NewItems.OfType<ProcessGroupItemViewModel>())
                group.Items.CollectionChanged += (_, change) =>
                {
                    if (change.OldItems is not null)
                        removedStreams += change.OldItems.OfType<MessageItemViewModel>()
                                                .Count(item => item.IsStreaming);
                };
        };
        page.Draft = "question";
        await page.SendAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var process = page.Items.OfType<ProcessGroupItemViewModel>().Single();
        Assert.That(process.IsExpanded && process.IsRunning, Is.True);
        window.NewSessionCommand.Execute(null);
        var other = window.SelectedSession!;
        Assert.That(other, Is.Not.SameAs(page));
        other.Draft = "keep this draft";
        if (cancel) page.CancelCommand.Execute(null);
        else finish.SetResult();
        await page.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(other.Items, Is.Empty);
        Assert.That(other.Draft, Is.EqualTo("keep this draft"));
        window.SelectSessionCommand.Execute(page);
        Assert.That(page.Items.OfType<ProcessGroupItemViewModel>().Single(), Is.SameAs(process));
        Assert.That(process.IsExpanded || process.IsRunning, Is.False);

        var answers = page.Items.OfType<MessageItemViewModel>().Where(item => item.IsAssistantMessage)
                          .Select(item => item.Content).ToArray();
        string[] expected = cancel ? []
            : phases ? tools ? ["first answer", "last answer", "after tool"] : ["first answer", "last answer"]
            : tools ? ["after tool"] : ["checking", "future", "first answer", "last answer"];
        Assert.That(answers, Is.EqualTo(expected));
        Assert.That(removedStreams, Is.EqualTo(cancel ? 0 : tools ? 2 : 1),
                    "同一响应拆成多条 history 时，只能移除一次临时 stream。");
        var processText = process.Items.OfType<MessageItemViewModel>().Where(item => item.Content.Length > 0)
                                 .Select(item => item.Content).ToArray();
        Assert.That(processText, Is.EqualTo(cancel   ? new[] { "checkingfuturefirst answerlast answer" }
                                            : phases ? new[] { "checking", "future" }
                                            : tools  ? new[] { "checking", "future", "first answer", "last answer" }
                                                       : Array.Empty<string>()));
        Assert.That(process.Items.OfType<MessageItemViewModel>().Any(item => item.IsInterrupted), Is.EqualTo(cancel));

        await using var manager = new DesktopSessionManager();
        var browse = new SessionViewModel(manager, factory, fixture.First, page.Id, fixture.Store,
                                          dispatch : action =>
                                          {
                                              action();
                                              return ValueTask.CompletedTask;
                                          });
        await browse.LoadHistoryAsync();
        Assert.That(browse.Items.OfType<MessageItemViewModel>().Where(item => item.IsAssistantMessage)
                          .Select(item => item.Content), Is.EqualTo(expected));
        if (!cancel)
        {
            var savedProcess = browse.Items.OfType<ProcessGroupItemViewModel>().Single();
            Assert.That(savedProcess.IsExpanded, Is.False);
            Assert.That(savedProcess.ModelLabel, Is.EqualTo(process.ModelLabel));
            Assert.That(savedProcess.Items.OfType<MessageItemViewModel>()
                                    .Select(item => (item.Content, item.Reasoning)),
                        Is.EqualTo(process.Items.OfType<MessageItemViewModel>()
                                          .Select(item => (item.Content, item.Reasoning))));
        }
    }

    [Test]
    public async Task AnswerWithoutReasoningKeepsReasoningRowHidden()
    {
        var fixture = await Fixture.CreateAsync();
        var finish  = Signal();
        finish.SetResult();
        var factory = new DesktopSessionFactory((_, _) => new Model(Signal(), finish, "offline answer"),
                                                new Credentials(), fixture.Config);
        await using var manager = new DesktopSessionManager();
        var             page    = Page(manager, factory, fixture.First);
        page.Draft = "request";
        await page.SendAsync();
        await page.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var settled = page.Items.OfType<MessageItemViewModel>().Last(message => message.IsAssistantMessage);
        Assert.That(settled.HasReasoning, Is.False);
        var browse = new SessionViewModel(manager, factory, fixture.First, page.Id, fixture.Store,
                                          dispatch : action =>
                                          {
                                              action();
                                              return ValueTask.CompletedTask;
                                          });
        await browse.LoadHistoryAsync();
        Assert.That(browse.Items.OfType<MessageItemViewModel>()
                          .Last(message => message.IsAssistantMessage).HasReasoning, Is.False);
    }

    [Test]
    public void StreamingBubbleAccumulatesReasoningFragmentsWithChangeNotifications()
    {
        var bubble = MessageItemViewModel.CreateStreaming();
        Assert.That(bubble.HasReasoning, Is.False);
        bubble.AppendReasoning("first ");
        bubble.AppendReasoning("thought");
        Assert.That(bubble.Reasoning, Is.EqualTo("first thought"));
        Assert.That(bubble.HasReasoning, Is.True);
        Assert.That(bubble.ReasoningSummary, Is.EqualTo("first thought"));
    }

    [Test]
    public async Task HistoryBrowsingUsesNoCredentialOrClientAndDoesNotTakeTheWriterLock()
    {
        var fixture = await Fixture.CreateAsync();
        var finish  = Signal();
        finish.SetResult();
        var factory = new DesktopSessionFactory((_, _) => new Model(Signal(), finish, "saved response"),
                                                new Credentials(), fixture.Config);
        await using var manager  = new DesktopSessionManager();
        var             original = Page(manager, factory, fixture.First);
        original.Draft = "stored request";
        await original.SendAsync();
        await original.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var noOpen =
            new DesktopSessionFactory((_, _) => throw new AssertionException("Browsing must not create a client."),
                                      new Credentials(), fixture.Config);
        var browse = new SessionViewModel(manager, noOpen, fixture.First, original.Id, fixture.Store,
                                          dispatch : action =>
                                          {
                                              action();
                                              return ValueTask.CompletedTask;
                                          });
        await browse.LoadHistoryAsync();
        Assert.That(Text(browse), Does.Contain("stored request").And.Contain("saved response"));
    }

    [Test]
    public async Task WorkspaceCanChangeOnlyInDraft_StoredSessionRejectsChangesBeforeOpening()
    {
        await using var manager = new DesktopSessionManager();
        var             factory = new DesktopSessionFactory();
        var             first   = Path.Combine(Path.GetTempPath(), "desktop-draft-a");
        var             second  = Path.Combine(Path.GetTempPath(), "desktop-draft-b");
        var             draft   = Page(manager, factory, first);
        draft.WorkspaceRoot = second;
        Assert.That(draft.WorkspaceLabel, Is.EqualTo("desktop-draft-b"));
        var history = new SessionViewModel(manager, factory, first, new string('a', 32));
        Assert.That(history.CanChooseWorkspace, Is.False);
        Assert.Throws<InvalidOperationException>(() => history.WorkspaceRoot = second);
        Assert.That(history.WorkspaceRoot, Is.EqualTo(first));
    }

    [Test]
    public async Task StartedSessionRejectsRootChangesAndStaleMenuActionsCannotChangeItsSettings()
    {
        var fixture = await Fixture.CreateAsync();
        var entered = Signal();
        var finish  = Signal();
        var factory = new DesktopSessionFactory((_, _) => new Model(entered, finish, "offline answer"),
                                                new Credentials(), fixture.Config);
        await using var manager = new DesktopSessionManager();
        var page = new SessionViewModel(manager, factory, fixture.First,
                                        dispatch : action =>
                                        {
                                            action();
                                            return ValueTask.CompletedTask;
                                        })
            { ModelOptions = [new ModelChoice("offline", "offline")] };
        page.IsModelMenuOpen = true;
        var staleModel = page.ModelGroups[0].Options[0];
        page.IsPermissionMenuOpen = true;
        var stalePermission = page.PermissionOptions[1];
        page.Draft = "start";
        await page.SendAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(page.IsModelMenuOpen    || page.IsPermissionMenuOpen, Is.False);
        Assert.That(page.CanChooseWorkspace || page.CanSelectModel, Is.False);
        Assert.That(staleModel.SelectCommand.CanExecute(null), Is.False);
        staleModel.SelectCommand.Execute(null);
        stalePermission.SelectCommand.Execute(null);
        Assert.That(page.SelectedModel, Is.Null);
        Assert.That(page.AllowChanges, Is.False);
        Assert.Throws<InvalidOperationException>(() => page.WorkspaceRoot = fixture.Second);
        finish.SetResult();
        await page.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var saved = await SessionStoreReader.ReadAsync(fixture.Store, page.Id!, CancellationToken.None);
        Assert.That(saved!.WorkspaceRoot, Is.EqualTo(fixture.First));
    }

    [Test]
    public async Task ComposerMenusPreselectModelsAndPermissionsButLeaveUnsupportedEntriesDisabled()
    {
        await using var manager = new DesktopSessionManager();
        var             choice  = new ModelChoice("provider", "model");
        var page = new SessionViewModel(manager, new DesktopSessionFactory(), Path.GetTempPath())
            { ModelOptions = [choice] };
        page.IsModelMenuOpen = true;
        page.ModelGroups[0].Options[0].SelectCommand.Execute(null);
        Assert.That(page.SelectedModel, Is.EqualTo(choice));
        Assert.That(page.ModelPickerLabel, Is.EqualTo("model · 默认"));
        Assert.That(page.CurrentModelLabel, Is.EqualTo("model"));
        Assert.That(page.ModelGroups[0].Name, Is.EqualTo("provider"));
        Assert.That(page.ModelGroups[0].Options[0].Label, Is.EqualTo("model"));
        Assert.That(page.IsModelMenuOpen, Is.False);
        page.IsPermissionMenuOpen = true;
        page.PermissionOptions[1].SelectCommand.Execute(null);
        Assert.That(page.AllowChanges, Is.True);
        Assert.That(page.PermissionPickerLabel, Is.EqualTo("按规则审批"));
        foreach (var option in page.EffortOptions)
        {
            Assert.That(option.SelectCommand.CanExecute(null), Is.False);
            option.SelectCommand.Execute(null);
        }

        Assert.That(page.CurrentEffortText, Is.EqualTo("默认"));
        page.IsPermissionMenuOpen = true;
        Assert.That(page.PermissionOptions.Skip(2).All(option => !option.SelectCommand.CanExecute(null)), Is.True);
    }

    [Test]
    public async Task DraftDefaultDisplaysModelAndEffortWithoutOpeningSession()
    {
        var fixture = await Fixture.CreateAsync();
        var user    = await UserConfigStore.LoadAsync(fixture.Config, CancellationToken.None);
        var model = user.Profiles[0].Models[0] with
        {
            ReasoningEfforts = [ModelReasoningEffort.High], DefaultReasoningEffort = ModelReasoningEffort.High
        };
        await UserConfigStore.SaveAsync(fixture.Config, user with
        {
            Profiles = [user.Profiles[0] with { Models = [model] }]
        }, CancellationToken.None);
        var factory =
            new DesktopSessionFactory((_, _) => throw new AssertionException("Draft must not create a client."),
                                      new Credentials(_ =>
                                                          throw new
                                                              AssertionException("Draft must not read credentials.")),
                                      fixture.Config);
        await using var manager = new DesktopSessionManager();
        var page = new SessionViewModel(manager, factory, fixture.First)
            { ModelOptions = [new ModelChoice("offline", "offline", Efforts : model.ReasoningEfforts)] };
        await page.RefreshDefaultModelAsync();
        Assert.That(page.CurrentModelLabel, Is.EqualTo("offline"));
        Assert.That(page.ModelPickerLabel, Is.EqualTo("offline · high"));
        Assert.That(page.ModelGroups, Has.Count.EqualTo(1));
        Assert.That(page.ModelGroups[0].Options[0].IsSelected, Is.True);
        Assert.That(page.EffortOptions.Single(option => option.Label == "high").IsSelected, Is.True);
        Assert.That(page.SelectedModel, Is.Null);
        Assert.That(page.Id, Is.Null);
        Assert.That(Directory.Exists(fixture.Store), Is.False);
    }

    [Test]
    public async Task DraftDefaultIgnoresWorkspaceProjectConfigAndKeepsExplicitSelection()
    {
        var fixture = await Fixture.CreateAsync();
        // 工作目录中的 tinyharness.json 不得接管应用配置；草稿默认项只来自全局默认 profile。
        await File.WriteAllTextAsync(Path.Combine(fixture.Second, "tinyharness.json"),
                                     """
                                     {"endpoint":"https://project.invalid/v1","model":"project-model","apiKeyCredentialTarget":"fixture"}
                                     """);
        await using var manager = new DesktopSessionManager();
        var page = new SessionViewModel(manager, new DesktopSessionFactory(userConfigPath : fixture.Config),
                                        fixture.First)
        {
            ModelOptions = [new ModelChoice("offline", "offline"), new ModelChoice("project-provider", "project-model")]
        };
        await page.RefreshDefaultModelAsync();
        Assert.That(page.ModelPickerLabel, Is.EqualTo("offline · 默认"));
        page.WorkspaceRoot = fixture.Second;
        await page.RefreshDefaultModelAsync();
        Assert.That(page.CurrentModelLabel, Is.EqualTo("offline"));
        Assert.That(page.ModelGroups[0].Options[0].IsSelected, Is.True);
        await page.SelectModelAsync(new ModelChoice("offline", "offline"), null);
        await page.RefreshDefaultModelAsync();
        Assert.That(page.CurrentModelLabel, Is.EqualTo("offline"));
        Assert.That(page.Id, Is.Null);
        Assert.That(Directory.Exists(fixture.Store), Is.False);
    }

    [Test]
    public async Task FirstSendBindingMatchesDraftDefaultDespiteWorkspaceProjectConfig()
    {
        var fixture = await Fixture.CreateAsync();
        // 首次发送的绑定与草稿显示一致：工作目录配置即使声明了不同模型与 Endpoint 也不参与。
        await File.WriteAllTextAsync(Path.Combine(fixture.First, "tinyharness.json"),
                                     """
                                     {"endpoint":"https://project.invalid/v1","model":"project-model","apiKeyCredentialTarget":"fixture"}
                                     """);
        TinyHarnessConfig? bound  = null;
        var                finish = Signal();
        var factory = new DesktopSessionFactory((config, _) =>
        {
            bound = config;
            return new Model(Signal(), finish, "answer");
        }, new Credentials(), fixture.Config);
        await using (var session = await factory.OpenAsync(new DesktopSessionRequest(fixture.First),
                                                           _ => ValueTask.CompletedTask,
                                                           _ => ValueTask.CompletedTask, CancellationToken.None))
        {
            Assert.That(bound, Is.Not.Null);
            Assert.That(bound!.Model, Is.EqualTo("offline"));
            Assert.That(bound.Endpoint, Is.EqualTo("https://offline.invalid/v1"));
            Assert.That(session.Host.Session.Model.ModelId, Is.EqualTo("offline"));
            Assert.That(session.Host.Session.Model.Endpoint, Is.EqualTo("https://offline.invalid/v1"));
        }
    }

    [Test]
    public async Task DraftSelectionDoesNotResolveCredentialsOrCreateSession()
    {
        var             fixture = await Fixture.CreateAsync();
        await using var manager = new DesktopSessionManager();
        var factory =
            new DesktopSessionFactory((_, _) => throw new AssertionException("Draft must not create a client."),
                                      new Credentials(), fixture.Config);
        var page = Page(manager, factory, fixture.First);
        var choice = new ModelChoice("offline", "offline",
                                     Efforts : [ModelReasoningEffort.Low, ModelReasoningEffort.High]);
        page.Draft = "keep this";
        await page.SelectModelAsync(choice, ModelReasoningEffort.High);
        Assert.That(page.Id, Is.Null);
        Assert.That(page.CurrentEffortText, Is.EqualTo("high"));
        Assert.That(page.Draft, Is.EqualTo("keep this"));
        Assert.That(Directory.Exists(fixture.Store), Is.False);
    }

    [Test]
    public async Task SwitchingAndEffortUseRegisteredHostAndKeepHistoricalLabelsAfterResume()
    {
        var fixture = await Fixture.CreateAsync();
        var user    = await UserConfigStore.LoadAsync(fixture.Config, CancellationToken.None);
        var a = user.Profiles[0].Models[0] with
        {
            ReasoningEfforts = [ModelReasoningEffort.Low, ModelReasoningEffort.High]
        };
        user = user with
        {
            Profiles =
            [
                user.Profiles[0] with
                {
                    Models = [a, new UserProfileModel { Id = "flash", ContextWindowTokens = 64000 }]
                }
            ]
        };
        await UserConfigStore.SaveAsync(fixture.Config, user, CancellationToken.None);
        var requests = new List<(string Model, ModelReasoningEffort? Effort)>();
        var finish   = Signal();
        finish.SetResult();
        var clients = 0;
        var factory = new DesktopSessionFactory((config, _) =>
        {
            clients++;
            return new Model(Signal(), finish, config.Model,
                             observed : request => requests.Add((request.Model, request.ReasoningEffort)));
        }, new Credentials(), fixture.Config);
        string   id;
        string[] labels;
        await using (var manager = new DesktopSessionManager())
        {
            var page = Page(manager, factory, fixture.First);
            page.Draft = "first";
            await page.SendAsync();
            await page.Completion;
            id = page.Id!;
            await page.SelectModelAsync(new ModelChoice("offline", "offline", Efforts : a.ReasoningEfforts),
                                        ModelReasoningEffort.High);
            Assert.That(clients, Is.EqualTo(1), "Effort-only selection reuses the client.");
            page.Draft = "second";
            await page.SendAsync();
            await page.Completion;
            await page.SelectModelAsync(new ModelChoice("offline", "flash"), null);
            page.Draft = "third";
            await page.SendAsync();
            await page.Completion;
            Assert.That(page.Id, Is.EqualTo(id));
            Assert.That(requests,
                        Is.EqualTo(new (string, ModelReasoningEffort?)[]
                                       { ("offline", null), ("offline", ModelReasoningEffort.High), ("flash", null) }));
            labels = page.Items.OfType<ProcessGroupItemViewModel>().Select(group => group.ModelLabel).ToArray();
            Assert.That(labels, Has.Length.EqualTo(3));
            Assert.That(labels[0], Does.Contain("offline · 默认"));
            Assert.That(labels[1], Does.Contain("offline · high"));
            Assert.That(labels[2], Does.Contain("flash · 默认"));
        }

        await using var resumedManager = new DesktopSessionManager();
        var resumed = new SessionViewModel(resumedManager, factory, fixture.First, id, fixture.Store,
                                           dispatch : action =>
                                           {
                                               action();
                                               return ValueTask.CompletedTask;
                                           });
        await resumed.LoadHistoryAsync();
        Assert.That(resumed.CurrentModelNameText, Is.EqualTo("flash"));
        Assert.That(resumed.CurrentModelLabel, Is.EqualTo("flash"));
        Assert.That(resumed.ModelPickerLabel, Is.EqualTo("flash · 默认"));
        Assert.That(resumed.Items.OfType<ProcessGroupItemViewModel>().Select(group => group.ModelLabel).Take(3),
                    Is.EqualTo(labels), "恢复后的历史运行标签必须保持原模型与强度归属。");
        resumed.Draft = "fourth";
        await resumed.SendAsync();
        await resumed.Completion;
        Assert.That(requests.Last().Model, Is.EqualTo("flash"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task LegacySessionCanSwitchBeforeInputAndRestoreSelectionWithoutModelRequests(bool keepOldProvider)
    {
        var fixture = await Fixture.CreateAsync();
        var id      = await CreateLegacySessionAsync(fixture);
        var before  = await SessionStoreReader.ReadAsync(fixture.Store, id, CancellationToken.None);
        var path    = Path.Combine(fixture.Store, id + SessionStoreWriter.LogFileSuffix);
        var prefix  = await File.ReadAllBytesAsync(path);
        Assert.That(before!.ModelSelection, Is.Null);
        var user = await UserConfigStore.LoadAsync(fixture.Config, CancellationToken.None);
        var target = new UserProfile
        {
            Name         = "deepseek", Endpoint = "https://deepseek.invalid/v1", ApiKeyCredentialTarget = "fixture",
            DefaultModel = "deepseek-chat",
            Models       = [new UserProfileModel { Id = "deepseek-chat", ContextWindowTokens = 64000 }]
        };
        await UserConfigStore.SaveAsync(fixture.Config, user with
        {
            Profiles = keepOldProvider ? [user.Profiles[0], target] : [target]
        }, CancellationToken.None);
        var requests = 0;
        var clients  = new List<string>();
        var factory = new DesktopSessionFactory((config, _) =>
        {
            clients.Add(config.Model);
            return new Model(Signal(), Signal(), "unused", observed : _ => requests++);
        }, new Credentials(), fixture.Config);
        await using (var manager = new DesktopSessionManager())
        {
            var page = new SessionViewModel(manager, factory, fixture.First, id, fixture.Store,
                                            dispatch : action =>
                                            {
                                                action();
                                                return ValueTask.CompletedTask;
                                            });
            await page.SelectModelAsync(new ModelChoice("deepseek", "deepseek-chat"), null);
            Assert.That(page.Id, Is.EqualTo(id));
            Assert.That(page.Draft, Is.Empty);
            Assert.That(page.CurrentModelNameText, Is.EqualTo("deepseek-chat"));
            Assert.That(clients, Is.EqualTo(new[] { "deepseek-chat" }));
        }

        var saved = await SessionStoreReader.ReadAsync(fixture.Store, id, CancellationToken.None);
        Assert.That(saved!.Model, Is.EqualTo(before.Model));
        Assert.That(saved.ModelSelection!.Model.ModelId, Is.EqualTo("deepseek-chat"));
        Assert.That(saved.ModelSelection.Context!.ContextWindowTokens, Is.EqualTo(64000));
        Assert.That(saved.Turns, Has.Count.EqualTo(1));
        Assert.That(saved.Turns[0].Runs[0].ModelSelectionRevision, Is.Null);
        Assert.That(saved.Conversation!.Messages.Select(message => message.Content),
                    Is.EqualTo(before.Conversation!.Messages.Select(message => message.Content)));
        var after = await File.ReadAllBytesAsync(path);
        Assert.That(after.Take(prefix.Length), Is.EqualTo(prefix));
        await using var reopened = await factory.OpenAsync(new DesktopSessionRequest(fixture.First, SessionId : id),
                                                           _ => ValueTask.CompletedTask,
                                                           _ => ValueTask.CompletedTask, CancellationToken.None);
        Assert.That(reopened.Host.Session.ModelSelection!.Model.ModelId, Is.EqualTo("deepseek-chat"));
        Assert.That(reopened.Host.Session.Turns, Has.Count.EqualTo(1));
        Assert.That(requests, Is.Zero);
    }

    [Test]
    public async Task LegacySessionResumesInitialModelWithoutInheritingCurrentDefaultEffort()
    {
        var fixture = await Fixture.CreateAsync();
        var id      = await CreateLegacySessionAsync(fixture);
        var user    = await UserConfigStore.LoadAsync(fixture.Config, CancellationToken.None);
        await UserConfigStore.SaveAsync(fixture.Config, user with
        {
            Profiles =
            [
                user.Profiles[0] with
                {
                    Models =
                    [
                        user.Profiles[0].Models[0] with
                        {
                            ReasoningEfforts = [ModelReasoningEffort.High],
                            DefaultReasoningEffort = ModelReasoningEffort.High
                        }
                    ]
                }
            ]
        }, CancellationToken.None);
        var finish = Signal();
        finish.SetResult();
        var requests = new List<ChatCompletionRequest>();
        var factory = new DesktopSessionFactory((_, _) => new Model(Signal(), finish, "continued",
                                                                    observed : requests.Add),
                                                new Credentials(), fixture.Config);
        await using var session = await factory.OpenAsync(new DesktopSessionRequest(fixture.First, SessionId : id),
                                                          _ => ValueTask.CompletedTask,
                                                          _ => ValueTask.CompletedTask, CancellationToken.None);
        Assert.That(session.Host.HasModelBinding, Is.True);
        Assert.That(session.Host.Session.ModelSelection!.ReasoningEffort, Is.Null);
        Assert.That(requests, Is.Empty);
        await session.SubmitAsync("continue");
        Assert.That(requests, Has.Count.EqualTo(1));
        Assert.That(requests[0].Model, Is.EqualTo("offline"));
        Assert.That(requests[0].ReasoningEffort, Is.Null);
        Assert.That(requests[0].Messages.Select(message => message.Content),
                    Does.Contain("legacy question").And.Contain("legacy answer"));
        var saved = await SessionStoreReader.ReadAsync(fixture.Store, id, CancellationToken.None);
        Assert.That(saved!.Turns, Has.Count.EqualTo(2));
        Assert.That(saved.Turns[0].Runs[0].ModelSelectionRevision, Is.Null);
    }

    [Test]
    public async Task RemovedTargetCanBeExplicitlyReplacedWithoutReadingItsCredentials()
    {
        var fixture = await Fixture.CreateAsync();
        var finish  = Signal();
        finish.SetResult();
        string id;
        await using (var manager = new DesktopSessionManager())
        {
            var page = Page(manager,
                            new DesktopSessionFactory((_, _) => new Model(Signal(), finish, "old"), new Credentials(),
                                                      fixture.Config), fixture.First);
            page.Draft = "first";
            await page.SendAsync();
            await page.Completion;
            id = page.Id!;
        }

        var user = await UserConfigStore.LoadAsync(fixture.Config, CancellationToken.None);
        await UserConfigStore.SaveAsync(fixture.Config, user with
        {
            DefaultProfile = "offline", Profiles =
            [
                new UserProfile
                {
                    Name         = "new", Endpoint = "https://new.invalid/v1", ApiKeyCredentialTarget = "fixture",
                    DefaultModel = "new",
                    Models       = [new UserProfileModel { Id = "new", ContextWindowTokens = 128000 }]
                }
            ]
        }, CancellationToken.None);
        var factory =
            new DesktopSessionFactory((config, _) =>
                                          config.Model == "new"
                                              ? new Model(Signal(), finish, "new answer")
                                              : throw new AssertionException("Old target must not be constructed."),
                                      new Credentials(), fixture.Config);
        await using var resumed = new DesktopSessionManager();
        var history = new SessionViewModel(resumed, factory, fixture.First, id, fixture.Store, dispatch : action =>
        {
            action();
            return ValueTask.CompletedTask;
        });
        history.Draft = "preserved";
        await history.SelectModelAsync(new ModelChoice("new", "new"), null);
        Assert.That(history.Draft, Is.EqualTo("preserved"));
        var stored = await SessionStoreReader.ReadAsync(fixture.Store, id, CancellationToken.None);
        Assert.That(stored!.Turns, Has.Count.EqualTo(1));
        Assert.That(stored.ModelSelection!.Model.ModelId, Is.EqualTo("new"));
        await history.SendAsync();
        await history.Completion;
        Assert.That(Text(history), Does.Contain("new answer"));
    }

    [TestCase("endpoint", false)]
    [TestCase("endpoint", true)]
    [TestCase("protocol", false)]
    [TestCase("protocol", true)]
    [TestCase("credential", false)]
    [TestCase("credential", true)]
    [TestCase("window", false)]
    [TestCase("window", true)]
    [TestCase("capabilities", false)]
    [TestCase("capabilities", true)]
    [TestCase("effort", false)]
    [TestCase("effort", true)]
    [TestCase("default", false)]
    [TestCase("default", true)]
    public async Task ExistingCatalogTargetRefreshesBeforeSendWithoutReselection(string change, bool reopen)
    {
        var fixture = await Fixture.CreateAsync();
        var user    = await UserConfigStore.LoadAsync(fixture.Config, CancellationToken.None);
        user = user with
        {
            Profiles =
            [
                user.Profiles[0] with
                {
                    Models =
                    [
                        user.Profiles[0].Models[0] with
                        {
                            ReasoningEfforts = [ModelReasoningEffort.Low, ModelReasoningEffort.High],
                            DefaultReasoningEffort = ModelReasoningEffort.High
                        }
                    ]
                }
            ]
        };
        await UserConfigStore.SaveAsync(fixture.Config, user, CancellationToken.None);
        var finish = Signal();
        finish.SetResult();
        var requests = new List<(TinyHarnessConfig Config, string Key, ChatCompletionRequest Request)>();
        var clients  = 0;
        var disposed = 0;
        var factory = new DesktopSessionFactory((config, key) =>
                                                {
                                                    clients++;
                                                    return new Model(Signal(), finish, "answer", "old reasoning",
                                                                     request => requests.Add((config, key, request)),
                                                                     () => disposed++);
                                                },
                                                new Credentials(target => target == "replacement"
                                                                    ? "replacement-key"
                                                                    : "fixture-secret-key"),
                                                fixture.Config);
        await using var manager        = new DesktopSessionManager();
        await using var resumedManager = new DesktopSessionManager();
        var             page           = Page(manager, factory, fixture.First);
        await page.SelectModelAsync(new ModelChoice("offline", "offline"), ModelReasoningEffort.High);
        page.Draft = "first";
        await page.SendAsync();
        await page.Completion;
        var id     = page.Id!;
        var before = await SessionStoreReader.ReadAsync(fixture.Store, id, CancellationToken.None);
        var model  = user.Profiles[0].Models[0];
        var updated = change switch
        {
            "endpoint"   => user.Profiles[0] with { Endpoint = "https://changed.invalid/v1" },
            "protocol"   => user.Profiles[0] with { ChatApi = ChatApiKind.Responses },
            "credential" => user.Profiles[0] with { ApiKeyCredentialTarget = "replacement" },
            "window"     => user.Profiles[0] with { Models = [model with { ContextWindowTokens = 64000 }] },
            "capabilities" => user.Profiles[0] with
            {
                Models = [model with { ReasoningEfforts = [ModelReasoningEffort.High] }]
            },
            "effort" => user.Profiles[0] with
            {
                Models =
                [
                    model with
                    {
                        ReasoningEfforts = [ModelReasoningEffort.Low],
                        DefaultReasoningEffort = ModelReasoningEffort.Low
                    }
                ]
            },
            _ => user.Profiles[0] with { Models = [model with { DefaultReasoningEffort = ModelReasoningEffort.Low }] }
        };
        await UserConfigStore.SaveAsync(fixture.Config, user with { Profiles = [updated] }, CancellationToken.None);
        if (reopen)
        {
            await manager.DisposeAsync();
            page = new SessionViewModel(resumedManager, factory, fixture.First, id, fixture.Store,
                                        dispatch : action =>
                                        {
                                            action();
                                            return ValueTask.CompletedTask;
                                        });
        }

        page.Draft = "second";
        await page.SendAsync();
        await page.Completion;
        Assert.That(page.Draft, Is.Empty);
        Assert.That(page.Id, Is.EqualTo(id));
        Assert.That(page.CurrentEffortText, Is.EqualTo(change == "effort" ? "low" : "high"));
        Assert.That(requests, Has.Count.EqualTo(2));
        Assert.That(requests[1].Config.Endpoint, Is.EqualTo(updated.Endpoint));
        Assert.That(requests[1].Config.ChatApi, Is.EqualTo(updated.ChatApi));
        Assert.That(requests[1].Key, Is.EqualTo(change == "credential" ? "replacement-key" : "fixture-secret-key"));
        Assert.That(requests[1].Request.ReasoningEffort,
                    Is.EqualTo(change == "effort" ? ModelReasoningEffort.Low : ModelReasoningEffort.High));
        Assert.That(requests[1].Request.Messages.Select(message => message.Content),
                    Does.Contain("first").And.Contain("answer"));
        var saved = await SessionStoreReader.ReadAsync(fixture.Store, id, CancellationToken.None);
        Assert.That(saved!.Turns, Has.Count.EqualTo(2));
        Assert.That(saved.ModelSelection!.Context!.ContextWindowTokens,
                    Is.EqualTo(updated.Models[0].ContextWindowTokens));
        if (change == "default")
            Assert.That(saved.ModelSelection.Revision, Is.EqualTo(before!.ModelSelection!.Revision));
        var isolatesReasoning = change is "endpoint" or "protocol" or "credential";
        Assert.That(saved.ModelSelection.ContinuationEpoch,
                    Is.EqualTo(before!.ModelSelection!.ContinuationEpoch + (isolatesReasoning ? 1 : 0)));
        if (isolatesReasoning)
            Assert.That(requests[1].Request.Messages.Where(message => message.Role == ChatRole.Assistant)
                                   .All(message => message.Reasoning is null or { Count: 0 }), Is.True);
        Assert.That(saved.Turns[0].Runs[0].ModelSelectionRevision,
                    Is.EqualTo(before.Turns[0].Runs[0].ModelSelectionRevision));
        var revision    = saved.ModelSelection.Revision;
        var clientCount = clients;
        Assert.That(disposed, Is.EqualTo(clients - 1));
        page.Draft = "third";
        await page.SendAsync();
        await page.Completion;
        var unchanged = await SessionStoreReader.ReadAsync(fixture.Store, id, CancellationToken.None);
        Assert.That(unchanged!.ModelSelection!.Revision, Is.EqualTo(revision));
        Assert.That(clients, Is.EqualTo(clientCount));
    }

    [TestCase(true, false)]
    [TestCase(true, true)]
    [TestCase(false, false)]
    [TestCase(false, true)]
    public async Task DeletedCatalogTargetPreservesDraftWithoutUsingAnotherProvider(bool removeProvider, bool reopen)
    {
        var fixture = await Fixture.CreateAsync();
        var finish  = Signal();
        finish.SetResult();
        var requests = 0;
        var factory =
            new DesktopSessionFactory((_, _) => new Model(Signal(), finish, "answer", observed : _ => requests++),
                                      new Credentials(), fixture.Config);
        await using var manager        = new DesktopSessionManager();
        await using var resumedManager = new DesktopSessionManager();
        var             page           = Page(manager, factory, fixture.First);
        page.Draft = "first";
        await page.SendAsync();
        await page.Completion;
        var user        = await UserConfigStore.LoadAsync(fixture.Config, CancellationToken.None);
        var alternative = user.Profiles[0] with { Name = "alternative" };
        await UserConfigStore.SaveAsync(fixture.Config, user with
        {
            DefaultProfile = "alternative",
            Profiles = removeProvider
                ? [alternative]
                : [user.Profiles[0] with { Models = [], DefaultModel = "" }, alternative]
        }, CancellationToken.None);
        if (reopen)
        {
            var id = page.Id;
            await manager.DisposeAsync();
            page = new SessionViewModel(resumedManager, factory, fixture.First, id, fixture.Store,
                                        dispatch : action =>
                                        {
                                            action();
                                            return ValueTask.CompletedTask;
                                        });
        }

        page.Draft = "must stay";
        await page.SendAsync();
        await page.Completion;
        Assert.That(page.Draft, Is.EqualTo("must stay"));
        Assert.That(page.Notice, Does.Contain("重新选择"));
        Assert.That(requests, Is.EqualTo(1));
        var saved = await SessionStoreReader.ReadAsync(fixture.Store, page.Id!, CancellationToken.None);
        Assert.That(saved!.Turns, Has.Count.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CredentialRefreshFailureKeepsDraftAndCanRetryWithoutReselecting(bool reopen)
    {
        var fixture = await Fixture.CreateAsync();
        var finish  = Signal();
        finish.SetResult();
        var requests    = new List<string>();
        var unavailable = true;
        var factory =
            new DesktopSessionFactory((_, key) => new Model(Signal(), finish, "answer",
                                                            observed : _ => requests.Add(key)),
                                      new Credentials(target => target == "replacement"
                                                          ? unavailable ? null : "replacement-key"
                                                          : "fixture-secret-key"), fixture.Config);
        await using var manager        = new DesktopSessionManager();
        await using var resumedManager = new DesktopSessionManager();
        var             page           = Page(manager, factory, fixture.First);
        page.Draft = "first";
        await page.SendAsync();
        await page.Completion;
        var user = await UserConfigStore.LoadAsync(fixture.Config, CancellationToken.None);
        await UserConfigStore.SaveAsync(fixture.Config, user with
        {
            Profiles = [user.Profiles[0] with { ApiKeyCredentialTarget = "replacement" }]
        }, CancellationToken.None);
        if (reopen)
        {
            var id = page.Id;
            await manager.DisposeAsync();
            page = new SessionViewModel(resumedManager, factory, fixture.First, id, fixture.Store,
                                        dispatch : action =>
                                        {
                                            action();
                                            return ValueTask.CompletedTask;
                                        });
        }

        page.Draft = "retry me";
        await page.SendAsync();
        await page.Completion;
        Assert.That(page.Draft, Is.EqualTo("retry me"));
        Assert.That(requests, Has.Count.EqualTo(1));
        unavailable = false;
        await page.SendAsync();
        await page.Completion;
        Assert.That(page.Draft, Is.Empty);
        Assert.That(requests, Is.EqualTo(new[] { "fixture-secret-key", "replacement-key" }));
    }

    [Test]
    public async Task SettingsConflictPreservesDraftAndConcurrentCatalogChanges()
    {
        var fixture  = await Fixture.CreateAsync();
        var settings = new CatalogSettingsViewModel(fixture.Config, () => Task.CompletedTask, new Credentials());
        await settings.LoadAsync();
        var row = settings.Providers.Single(item => item.Name == "offline");
        row.EditCommand.Execute(null);
        var editor = settings.Editor;
        Assert.That(editor, Is.Not.Null);
        Assert.That(editor!.IsCreate, Is.False);
        var entry = editor.ModelEntries.Single(item => item.Id == "offline");
        entry.DisplayName = "unsaved draft";
        Assert.That(editor.IsDirty, Is.True);
        var baseline = await UserConfigStore.LoadAsync(fixture.Config, CancellationToken.None);
        var concurrent = ModelCatalogEditor.PutModel(baseline, "offline",
                                                     new UserProfileModel
                                                         { Id = "concurrent", ContextWindowTokens = 32000 }, false);
        await UserConfigStore.SaveAsync(fixture.Config, concurrent, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => editor.SaveAsync());
        Assert.That(entry.DisplayName, Is.EqualTo("unsaved draft"));
        var saved = await UserConfigStore.LoadAsync(fixture.Config, CancellationToken.None);
        Assert.That(saved.Profiles[0].Models.Select(item => item.Id), Is.EqualTo(new[] { "offline", "concurrent" }));
    }

    [Test]
    public async Task SettingsSaveAndRefreshKeepTheirEditorUntilTheOperationCompletes()
    {
        var fixture        = await Fixture.CreateAsync();
        var refreshEntered = Signal();
        var finishRefresh  = Signal();
        var settings = new CatalogSettingsViewModel(fixture.Config, async () =>
        {
            refreshEntered.SetResult();
            await finishRefresh.Task;
        }, new Credentials());
        await settings.LoadAsync();
        var row = settings.Providers.Single();
        row.EditCommand.Execute(null);
        var editor = settings.Editor!;
        editor.ModelEntries.Single().DisplayName = "saved name";
        Task saving;
        using (var saveLock = new FileStream(fixture.Config + UserConfigStore.SaveLockSuffix, FileMode.OpenOrCreate,
                                             FileAccess.ReadWrite, FileShare.None))
        {
            saving = editor.SaveAsync();
            Assert.That(editor.IsSaving, Is.True);
            Assert.That(settings.OpenAddCommand.CanExecute(null), Is.False);
            Assert.That(row.EditCommand.CanExecute(null), Is.False);
            settings.OpenAddCommand.Execute(null);
            row.EditCommand.Execute(null);
            await settings.LoadAsync();
            Assert.That(settings.Editor, Is.SameAs(editor));
        }

        await saving;
        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.That(settings.Editor, Is.Null);
            Assert.That(settings.OpenAddCommand.CanExecute(null), Is.False);
            settings.OpenAddCommand.Execute(null);
            Assert.That(settings.Editor, Is.Null);
        }
        finally
        {
            finishRefresh.TrySetResult();
        }

        var ready = Signal();
        settings.OpenAddCommand.CanExecuteChanged += (_, _) =>
        {
            if (settings.OpenAddCommand.CanExecute(null)) ready.TrySetResult();
        };
        if (settings.OpenAddCommand.CanExecute(null)) ready.TrySetResult();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        settings.Providers.Single().EditCommand.Execute(null);
        Assert.That(settings.Editor!.ModelEntries.Single().DisplayName, Is.EqualTo("saved name"));
    }

    private static async Task<string> CreateLegacySessionAsync(Fixture fixture)
    {
        var finish = Signal();
        finish.SetResult();
        var factory = new DesktopSessionFactory((_, _) => new Model(Signal(), finish, "legacy answer"),
                                                new Credentials(), fixture.Config);
        string seedId;
        await using (var seed = await factory.OpenAsync(new DesktopSessionRequest(fixture.First),
                                                        _ => ValueTask.CompletedTask,
                                                        _ => ValueTask.CompletedTask, CancellationToken.None))
        {
            await seed.SubmitAsync("legacy question");
            seedId = seed.Id;
        }

        var snapshot = (await SessionStoreReader.ReadAsync(fixture.Store, seedId, CancellationToken.None))!;
        // 独立写出没有选择记录及 run 修订引用的旧日志夹具，不改写种子日志。
        var legacy = snapshot with
        {
            Id = Guid.NewGuid().ToString("N"), ModelSelections = [],
            Turns = snapshot.Turns.Select(turn => turn with
            {
                Runs = turn.Runs.Select(run => run with { ModelSelectionRevision = null }).ToArray()
            }).ToArray(),
            Conversation = snapshot.Conversation! with { CheckpointModelSelectionRevision = null }
        };
        var       restored = SessionResume.Restore(legacy, fixture.First, legacy.Conversation.Messages[0].Content);
        using var writer   = new SessionStoreWriter(fixture.Store, legacy.Id);
        await writer.CommitAsync(restored.Session, restored.Context, CancellationToken.None);
        return legacy.Id;
    }

    private static SessionViewModel Page(DesktopSessionManager manager, DesktopSessionFactory factory, string root) =>
        new(manager, factory, root, dispatch : action =>
        {
            action();
            return ValueTask.CompletedTask;
        });

    private static string Text(SessionViewModel page) =>
        string.Join('\n', page.Items.OfType<MessageItemViewModel>().Select(message => message.Content));

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record Fixture(string First, string Second, string Store, string Config)
    {
        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "tinyharness-desktop-test-" + Guid.NewGuid().ToString("N"));
            var result = new Fixture(Path.Combine(root, "a"), Path.Combine(root, "b"), Path.Combine(root, "sessions"),
                                     Path.Combine(root, "config.json"));
            Directory.CreateDirectory(result.First);
            Directory.CreateDirectory(result.Second);
            await UserConfigStore.SaveAsync(result.Config, new UserConfig
            {
                DefaultProfile = "offline", Profiles =
                [
                    new UserProfile
                    {
                        Name = "offline", Endpoint = "https://offline.invalid/v1", ApiKeyCredentialTarget = "fixture",
                        DefaultModel = "offline",
                        Models = [new UserProfileModel { Id = "offline", ContextWindowTokens = 128_000 }]
                    }
                ],
                Settings = new UserConfigSettings { SessionStoreDirectory = result.Store }
            }, CancellationToken.None);
            return result;
        }
    }

    private sealed class Credentials(Func<string, string?>? read = null) : ICredentialStore
    {
        public bool    IsSupported => true;
        public string? Read(string   targetName) => read is null ? "fixture-secret-key" : read(targetName);
        public void    Save(string   targetName, string secret) => throw new NotSupportedException();
        public bool    Delete(string targetName) => throw new NotSupportedException();
    }

    private sealed class MultiStepModel(
        int                  toolRounds,
        TaskCompletionSource finalEntered,
        TaskCompletionSource finish) : IChatCompletionClient
    {
        private int _requests;

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
                                                                     [EnumeratorCancellation]
                                                                     CancellationToken cancellationToken)
        {
            var step = _requests++ % (toolRounds + 1);
            yield return new ChatStreamEvent
                { Kind = ChatStreamEventKind.ReasoningDelta, ReasoningDelta = $"thinking {step}" };
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = $"step {step}" };
            if (step < toolRounds)
            {
                yield return new ChatStreamEvent
                {
                    Kind = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 0, ToolCallId = $"call-{_requests}",
                    ToolCallFunctionName = "work_items", ToolCallArgumentsDelta = "{\"action\":\"list\"}"
                };
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "tool_calls" };
            }
            else
            {
                finalEntered.TrySetResult();
                await finish.Task.WaitAsync(cancellationToken);
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" };
            }
        }
    }

    private sealed class PhaseModel(
        bool                 phases,
        bool                 tools,
        TaskCompletionSource entered,
        TaskCompletionSource finish) : IChatCompletionClient
    {
        private int _requests;

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (_requests++ > 0)
            {
                yield return new ChatStreamEvent
                    { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "after tool" };
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" };
                yield break;
            }

            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ReasoningDelta, ReasoningDelta = "thinking" };
            var textItems = new[]
            {
                new AssistantTextItem("checking", "commentary", phases ? "commentary" : null),
                new AssistantTextItem("future", "unknown", phases ? "future_phase" : null),
                new AssistantTextItem("first answer", "partial", phases ? "partial_answer" : null),
                new AssistantTextItem("last answer", "final", phases ? "final_answer" : null)
            };
            foreach (var item in textItems)
            {
                yield return new ChatStreamEvent
                {
                    Kind          = ChatStreamEventKind.TextMessageStarted,
                    TextMessageId = item.MessageId,
                    TextPhase     = item.Phase
                };
                yield return new ChatStreamEvent
                {
                    Kind          = ChatStreamEventKind.ContentDelta,
                    TextMessageId = item.MessageId,
                    ContentDelta  = item.Text
                };
                yield return new ChatStreamEvent
                {
                    Kind          = ChatStreamEventKind.TextMessageCompleted,
                    TextMessageId = item.MessageId,
                    ContentDelta  = item.Text,
                    TextPhase     = item.Phase
                };
            }

            entered.TrySetResult();
            await finish.Task.WaitAsync(cancellationToken);
            if (tools)
                yield return new ChatStreamEvent
                {
                    Kind                   = ChatStreamEventKind.ToolCallDelta,
                    ToolCallIndex          = 0,
                    ToolCallId             = "call-phase",
                    ToolCallFunctionName   = "work_items",
                    ToolCallArgumentsDelta = "{\"action\":\"list\"}"
                };
            yield return new ChatStreamEvent
                { Kind = ChatStreamEventKind.End, FinishReason = tools ? "tool_calls" : "stop" };
        }
    }

    private sealed class InterruptedModel(TaskCompletionSource entered, TaskCompletionSource finish)
        : IChatCompletionClient
    {
        private int _requests;

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (_requests++ > 0)
            {
                yield return new ChatStreamEvent
                    { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "recovered" };
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" };
                yield break;
            }

            yield return new ChatStreamEvent
                { Kind = ChatStreamEventKind.ReasoningDelta, ReasoningDelta = "partial thinking" };
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "partial text" };
            entered.TrySetResult();
            await finish.Task.WaitAsync(cancellationToken);
            throw new InvalidOperationException("offline failure");
        }
    }

    private sealed class Model(
        TaskCompletionSource           entered,
        TaskCompletionSource           finish,
        string                         response,
        string?                        reasoning = null,
        Action<ChatCompletionRequest>? observed  = null,
        Action?                        disposed  = null) : IChatCompletionClient, IDisposable
    {
        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
                                                                     [EnumeratorCancellation]
                                                                     CancellationToken cancellationToken)
        {
            observed?.Invoke(request);
            entered.TrySetResult();
            await finish.Task.WaitAsync(cancellationToken);
            if (reasoning is not null)
                foreach (var part in reasoning.Chunk(4))
                    yield return new ChatStreamEvent
                        { Kind = ChatStreamEventKind.ReasoningDelta, ReasoningDelta = new string(part) };
            foreach (var part in response.Chunk(3))
                yield return new ChatStreamEvent
                    { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = new string(part) };
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" };
        }

        public void Dispose() => disposed?.Invoke();
    }
}

using NUnit.Framework;
using System.Runtime.CompilerServices;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
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
            "answer fixture-secret-key done", "thinking fixture-secret-key done", observed: _ =>
            {
                if (++requests > 1) throw new InvalidOperationException("failed fixture-secret-key");
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

    [Test]
    public async Task ReasoningIsShownByDefaultAndPreservesOriginalTextAcrossHistoryReload()
    {
        var fixture = await Fixture.CreateAsync();
        var finish  = Signal();
        finish.SetResult();
        var factory = new DesktopSessionFactory((_, _) => new Model(Signal(), finish, "offline answer",
                                                                    "thinking about fixture-secret-key before answering"),
                                                new Credentials(), fixture.Config);
        await using var manager           = new DesktopSessionManager();
        var             page              = Page(manager, factory, fixture.First);
        string?         streamedReasoning = null;
        page.Items.CollectionChanged += (_, args) =>
        {
            if (args.NewItems is null) return;
            foreach (var message in args.NewItems.OfType<MessageItemViewModel>())
                message.PropertyChanged += (_, change) =>
                {
                    if (message.IsStreaming && change.PropertyName == nameof(MessageItemViewModel.Reasoning))
                        streamedReasoning = message.Reasoning;
                };
        };
        page.Draft = "request";
        await page.SendAsync();
        await page.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var settled = page.Items.OfType<MessageItemViewModel>().Last(message => message.IsAssistantMessage);
        Assert.That(settled.HasReasoning, Is.True);
        Assert.That(settled.Reasoning, Is.EqualTo("thinking about fixture-secret-key before answering"));
        Assert.That(streamedReasoning, Is.EqualTo(settled.Reasoning));
        Assert.That(settled.ReasoningSummary, Is.Not.Null);
        Assert.That(settled.IsReasoningExpanded, Is.False);
        settled.ToggleReasoningCommand.Execute(null);
        Assert.That(settled.IsReasoningExpanded, Is.True);
        settled.ToggleReasoningCommand.Execute(null);
        Assert.That(settled.IsReasoningExpanded, Is.False);
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
        Assert.That(reloaded.Reasoning, Is.EqualTo(settled.Reasoning));
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
        var staleModel = page.ModelGroups[1].Options[0];
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
        page.ModelGroups[1].Options[0].SelectCommand.Execute(null);
        Assert.That(page.SelectedModel, Is.EqualTo(choice));
        Assert.That(page.ModelPickerLabel, Is.EqualTo("model · 默认"));
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
        string id;
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
            var labels = page.Items.OfType<MessageItemViewModel>().Where(item => item.IsAssistantMessage)
                             .Select(item => item.ModelLabel).ToArray();
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

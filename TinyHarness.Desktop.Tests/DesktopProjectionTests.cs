using NUnit.Framework;
using System.Runtime.CompilerServices;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
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
        var fixture = await Fixture.CreateAsync();
        var enteredA = Signal();
        var enteredB = Signal();
        var finishA = Signal();
        var finishB = Signal();
        var factory = new DesktopSessionFactory((config, _) => config.WorkspaceRoot == fixture.First
            ? new Model(enteredA, finishA, "response-a") : new Model(enteredB, finishB, "response-b"),
            new Credentials(), fixture.Config);
        await using var manager = new DesktopSessionManager();
        var first = Page(manager, factory, fixture.First);
        var second = Page(manager, factory, fixture.Second);
        first.Draft = "request-a";
        await first.SendAsync();
        await enteredA.Task.WaitAsync(TimeSpan.FromSeconds(10));
        second.Draft = "request-b";
        await second.SendAsync();
        await enteredB.Task.WaitAsync(TimeSpan.FromSeconds(10));
        first.Draft = "next-a";
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
    public async Task ActiveSendUsesDurableAmendmentAndDoesNotCreateAnOverlappingTurn()
    {
        var fixture = await Fixture.CreateAsync();
        var entered = Signal();
        var finish = Signal();
        var factory = new DesktopSessionFactory((_, _) => new Model(entered, finish, "offline answer"),
            new Credentials(), fixture.Config);
        await using var manager = new DesktopSessionManager();
        var page = Page(manager, factory, fixture.First);
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
    public async Task EnabledReadableThinkingPreservesOriginalTextAcrossHistoryReload()
    {
        var fixture = await Fixture.CreateAsync();
        var finish  = Signal();
        finish.SetResult();
        var factory = new DesktopSessionFactory((_, _) => new Model(Signal(), finish, "offline answer",
                                                                    "thinking about fixture-secret-key before answering"),
                                                new Credentials(), fixture.Config);
        await using var manager = new DesktopSessionManager();
        var             page    = Page(manager, factory, fixture.First);
        page.ShowReasoning = true;
        page.Draft         = "request";
        await page.SendAsync();
        await page.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var settled = page.Items.OfType<MessageItemViewModel>().Last(message => message.IsAssistantMessage);
        Assert.That(settled.HasReasoning, Is.True);
        Assert.That(settled.Reasoning, Is.EqualTo("thinking about fixture-secret-key before answering"));
        Assert.That(settled.ReasoningSummary, Is.Not.Null);
        var browse = new SessionViewModel(manager, factory, fixture.First, page.Id, fixture.Store,
                                          dispatch : action =>
                                          {
                                              action();
                                              return ValueTask.CompletedTask;
                                          });
        browse.ShowReasoning = true;
        await browse.LoadHistoryAsync();
        var reloaded = browse.Items.OfType<MessageItemViewModel>().Last(message => message.IsAssistantMessage);
        Assert.That(reloaded.HasReasoning, Is.True);
        Assert.That(reloaded.Reasoning, Is.EqualTo(settled.Reasoning));
    }

    [Test]
    public async Task DisabledReadableThinkingKeepsReasoningRowHidden()
    {
        var fixture = await Fixture.CreateAsync();
        var finish = Signal();
        finish.SetResult();
        var factory = new DesktopSessionFactory((_, _) => new Model(Signal(), finish, "offline answer",
            "hidden thinking"),
            new Credentials(), fixture.Config);
        await using var manager = new DesktopSessionManager();
        var page = Page(manager, factory, fixture.First);
        page.Draft = "request";
        await page.SendAsync();
        await page.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var settled = page.Items.OfType<MessageItemViewModel>().Last(message => message.IsAssistantMessage);
        Assert.That(settled.HasReasoning, Is.False);
        var browse = new SessionViewModel(manager, factory, fixture.First, page.Id, fixture.Store,
            dispatch: action => { action(); return ValueTask.CompletedTask; });
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
        var finish = Signal(); finish.SetResult();
        var factory = new DesktopSessionFactory((_, _) => new Model(Signal(), finish, "saved response"),
            new Credentials(), fixture.Config);
        await using var manager = new DesktopSessionManager();
        var original = Page(manager, factory, fixture.First);
        original.Draft = "stored request";
        await original.SendAsync();
        await original.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var noOpen = new DesktopSessionFactory((_, _) => throw new AssertionException("Browsing must not create a client."),
            new Credentials(), fixture.Config);
        var browse = new SessionViewModel(manager, noOpen, fixture.First, original.Id, fixture.Store,
            dispatch: action => { action(); return ValueTask.CompletedTask; });
        await browse.LoadHistoryAsync();
        Assert.That(Text(browse), Does.Contain("stored request").And.Contain("saved response"));
    }

    [Test]
    public async Task WorkspaceCanChangeOnlyInDraft_StoredSessionRejectsChangesBeforeOpening()
    {
        await using var manager = new DesktopSessionManager();
        var factory = new DesktopSessionFactory();
        var first = Path.Combine(Path.GetTempPath(), "desktop-draft-a");
        var second = Path.Combine(Path.GetTempPath(), "desktop-draft-b");
        var draft = Page(manager, factory, first);
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
        var finish = Signal();
        var factory = new DesktopSessionFactory((_, _) => new Model(entered, finish, "offline answer"),
            new Credentials(), fixture.Config);
        await using var manager = new DesktopSessionManager();
        var page = new SessionViewModel(manager, factory, fixture.First,
            dispatch: action => { action(); return ValueTask.CompletedTask; })
        { ModelOptions = [new ModelChoice("offline", "offline")] };
        page.IsModelMenuOpen = true;
        var staleModel = page.ModelGroups[1].Options[0];
        page.IsPermissionMenuOpen = true;
        var stalePermission = page.PermissionOptions[1];
        page.Draft = "start";
        await page.SendAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(page.IsModelMenuOpen || page.IsPermissionMenuOpen, Is.False);
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
        var choice = new ModelChoice("provider", "model");
        var page = new SessionViewModel(manager, new DesktopSessionFactory(), Path.GetTempPath())
        { ModelOptions = [choice] };
        page.IsModelMenuOpen = true;
        page.ModelGroups[1].Options[0].SelectCommand.Execute(null);
        Assert.That(page.SelectedModel, Is.EqualTo(choice));
        Assert.That(page.ModelPickerLabel, Is.EqualTo("model"));
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

    private static SessionViewModel Page(DesktopSessionManager manager, DesktopSessionFactory factory, string root) =>
        new(manager, factory, root, dispatch: action => { action(); return ValueTask.CompletedTask; });
    private static string Text(SessionViewModel page) => string.Join('\n', page.Items.OfType<MessageItemViewModel>().Select(message => message.Content));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record Fixture(string First, string Second, string Store, string Config)
    {
        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "tinyharness-desktop-test-" + Guid.NewGuid().ToString("N"));
            var result = new Fixture(Path.Combine(root, "a"), Path.Combine(root, "b"), Path.Combine(root, "sessions"), Path.Combine(root, "config.json"));
            Directory.CreateDirectory(result.First);
            Directory.CreateDirectory(result.Second);
            await UserConfigStore.SaveAsync(result.Config, new UserConfig
            {
                DefaultProfile = "offline", Profiles = [new UserProfile
                {
                    Name = "offline", Endpoint = "https://offline.invalid/v1", ApiKeyCredentialTarget = "fixture",
                    DefaultModel = "offline", Models = [new UserProfileModel { Id = "offline", ContextWindowTokens = 128_000 }]
                }],
                Settings = new UserConfigSettings { SessionStoreDirectory = result.Store }
            }, CancellationToken.None);
            return result;
        }
    }

    private sealed class Credentials : ICredentialStore
    {
        public bool IsSupported => true;
        public string? Read(string targetName) => "fixture-secret-key";
        public void Save(string targetName, string secret) => throw new NotSupportedException();
        public bool Delete(string targetName) => throw new NotSupportedException();
    }

    private sealed class Model(TaskCompletionSource entered, TaskCompletionSource finish, string response,
                               string? reasoning = null) : IChatCompletionClient
    {
        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await finish.Task.WaitAsync(cancellationToken);
            if (reasoning is not null)
                foreach (var part in reasoning.Chunk(4))
                    yield return new ChatStreamEvent
                        { Kind = ChatStreamEventKind.ReasoningDelta, ReasoningDelta = new string(part) };
            foreach (var part in response.Chunk(3))
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = new string(part) };
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" };
        }
    }
}

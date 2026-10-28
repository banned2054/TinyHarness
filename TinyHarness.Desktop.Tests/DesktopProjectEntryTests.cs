using NUnit.Framework;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Project;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Desktop.Runtime;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class DesktopProjectEntryTests
{
    [Test]
    public async Task InitializationBeforeRefreshContinuesPastTheFirstBatch()
    {
        var fixture = await Fixture.CreateAsync();
        for (var index = 0; index < 101; index++)
        {
            var       session = new ChatSession(index.ToString("x32"), fixture.First, Fixture.Identity);
            using var writer  = new SessionStoreWriter(fixture.Store, session.Id);
            await writer.CommitAsync(session, new ConversationContext(new ContextOptions()), CancellationToken.None);
        }

        await using var model = fixture.Window();
        await model.InitializeProjectsAsync();
        var first = await SessionStoreReader.ListAsync(fixture.Store, CancellationToken.None);
        Assert.That(first.Count(entry => entry.Session!.ProjectId is not null), Is.EqualTo(100));
        Assert.That(model.Notice, Does.Contain("仍有日志"));
        await model.InitializeProjectsAsync();
        var second = await SessionStoreReader.ListAsync(fixture.Store, CancellationToken.None);
        Assert.That(second.All(entry => entry.Session!.ProjectId is not null), Is.True);
        Assert.That(model.Notice, Does.Contain("1 已绑定"));
        Assert.That(fixture.Models, Is.Zero);
    }

    [Test]
    public async Task SwitchingAndRenamingProjectsLeaveTheOriginalRunAndDraftOnItsPage()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var model = fixture.Window();
        await model.OpenProjectAsync(fixture.First);
        var original = model.SelectedSession!;
        var group    = model.Projects.Single(item => item.Key == original.ProjectId);
        original.Draft = "running request";
        await original.SendAsync();
        await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        original.Draft   = "next input";
        group.EditedName = "renamed during run";
        await model.RenameProjectAsync(group);
        group.IsExpanded = false;
        await model.OpenProjectAsync(fixture.Second);
        var second = model.SelectedSession!;
        second.Draft = "second project input";
        Assert.That(original.IsRunning, Is.True);
        Assert.That(original.WorkspaceRoot, Is.EqualTo(fixture.First));
        Assert.That(second, Is.Not.SameAs(original));
        fixture.Release.SetResult();
        await original.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(original.Draft, Is.EqualTo("next input"));
        Assert.That(second.Draft, Is.EqualTo("second project input"));
        Assert.That(second.Id, Is.Null);
    }

    [Test]
    public async Task OpeningProjectsPreservesDraftAndCreatesNoHostsOrLogs()
    {
        var             fixture = await Fixture.CreateAsync();
        await using var model   = fixture.Window();
        model.SelectedSession!.Draft = "keep this input";
        await model.OpenProjectAsync(fixture.First);
        var firstId = model.SelectedSession.ProjectId;
        await model.OpenProjectAsync(fixture.Second);
        Assert.That(model.SelectedSession.Draft, Is.EqualTo("keep this input"));
        Assert.That(model.SelectedSession.ProjectId, Is.Not.EqualTo(firstId));
        Assert.That(model.Projects.Count(group => group.CanEdit), Is.EqualTo(2));
        Assert.That(fixture.Credentials.Reads, Is.Zero);
        Assert.That(fixture.Models, Is.Zero);
        Assert.That(Directory.GetFiles(fixture.Store, "*.session.jsonl"), Is.Empty);
        await model.OpenProjectAsync(fixture.First);
        Assert.That(model.SelectedSession.ProjectId, Is.EqualTo(firstId));
    }

    [Test]
    public async Task FirstSendBindsTheSavedProjectAndRestartKeepsIdentityAndRoot()
    {
        var     fixture = await Fixture.CreateAsync();
        string  id;
        string? projectId;
        await using (var model = fixture.Window())
        {
            await model.OpenProjectAsync(fixture.First);
            var page = model.SelectedSession!;
            projectId = page.ProjectId;
            page.Draft = "first request";
            await page.SendAsync();
            await page.Completion;
            id = page.Id!;
            var stored = await SessionStoreReader.ReadAsync(fixture.Store, id, CancellationToken.None);
            Assert.That(stored!.ProjectId, Is.EqualTo(projectId));
            Assert.That(stored.WorkspaceRoot, Is.EqualTo(fixture.First));
        }

        await using var restarted = fixture.Window();
        await restarted.InitializeAsync();
        var history = restarted.Sessions.Single(page => page.Id == id);
        Assert.That(history.ProjectId, Is.EqualTo(projectId));
        Assert.That(history.WorkspaceRoot, Is.EqualTo(fixture.First));
        Assert.That(restarted.Projects.Single(group => group.Key == projectId).Sessions, Does.Contain(history));
        Assert.Throws<InvalidOperationException>(() => history.WorkspaceRoot = fixture.Second);
    }

    [Test]
    public async Task NewProjectDraftAndRenameLeaveOtherPageAndExecutionRootUntouched()
    {
        var             fixture = await Fixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.OpenProjectAsync(fixture.First);
        var original = model.SelectedSession!;
        original.Draft = "original input";
        var group = model.Projects.Single(item => item.Key == original.ProjectId);
        await model.NewProjectSessionAsync(group);
        Assert.That(model.SelectedSession, Is.Not.SameAs(original));
        Assert.That(original.Draft, Is.EqualTo("original input"));
        var activity = original.LastActivityUtc;
        group.EditedName = "renamed";
        await model.RenameProjectAsync(group);
        Assert.That(group.Name, Is.EqualTo("renamed"));
        Assert.That(original.WorkspaceRoot, Is.EqualTo(fixture.First));
        Assert.That(original.LastActivityUtc, Is.EqualTo(activity));
        Assert.That(fixture.Credentials.Reads, Is.Zero);
        var saved = await ProjectStore.LoadAsync(ProjectStore.FilePath(fixture.Store), CancellationToken.None);
        Assert.That(saved.Projects.Single().Name, Is.EqualTo("renamed"));
        await ProjectStore.RenameAsync(ProjectStore.FilePath(fixture.Store), group.Project!.Id, "external",
                                       group.Project.Revision);
        group.EditedName = "stale";
        await Assert.ThrowsAsync<ProjectStoreException>(() => model.RenameProjectAsync(group));
    }

    [Test]
    public async Task BrowseIsReadOnlyAndExplicitInitializationRefreshesTheExistingPage()
    {
        var fixture = await Fixture.CreateAsync();
        var session = new ChatSession(Guid.NewGuid().ToString("N"), fixture.First, Fixture.Identity);
        var context = new ConversationContext(new ContextOptions());
        context.Append(ChatMessage.System("history"));
        using (var writer = new SessionStoreWriter(fixture.Store, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        var             path     = Path.Combine(fixture.Store, session.Id + SessionStoreWriter.LogFileSuffix);
        var             original = await File.ReadAllBytesAsync(path);
        await using var model    = fixture.Window();
        await model.InitializeAsync();
        var page = model.Sessions.Single(item => item.Id == session.Id);
        model.SelectedSession = page;
        await page.EnsureHistoryLoadedAsync();
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(original));
        Assert.That(File.Exists(ProjectStore.FilePath(fixture.Store)), Is.False);
        Assert.That(page.ProjectId, Is.Null);
        await model.InitializeProjectsAsync();
        Assert.That(page.ProjectId, Is.Not.Null);
        Assert.That(model.Sessions.Single(item => item.Id == session.Id), Is.SameAs(page));
        Assert.That((await File.ReadAllBytesAsync(path)).Take(original.Length), Is.EqualTo(original));
        Assert.That(fixture.Credentials.Reads, Is.Zero);
        Assert.That(fixture.Models, Is.Zero);
        var bound = await File.ReadAllBytesAsync(path);
        await model.InitializeProjectsAsync();
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(bound));
    }

    [Test]
    public async Task DamagedCatalogPreservesDraftAndMissingProjectHistoryRemainsReadable()
    {
        var fixture = await Fixture.CreateAsync();
        await using (var model = fixture.Window())
        {
            await model.OpenProjectAsync(fixture.First);
            var page = model.SelectedSession!;
            page.Draft = "saved history";
            await page.SendAsync();
            await page.Completion;
        }

        await File.WriteAllTextAsync(ProjectStore.FilePath(fixture.Store), "damaged");
        await using var restarted = fixture.Window();
        await restarted.InitializeAsync();
        var historical = restarted.Sessions.Single(page => page.Id is not null);
        await historical.EnsureHistoryLoadedAsync();
        Assert.That(historical.Items, Is.Not.Empty);
        Assert.That(restarted.Projects.Any(group => group.Name == "项目信息缺失"), Is.True);
        restarted.SelectedSession!.Draft = "keep";
        await Assert.ThrowsAsync<ProjectStoreException>(() => restarted.OpenProjectAsync(fixture.Second));
        Assert.That(restarted.SelectedSession.Draft, Is.EqualTo("keep"));
        Assert.That(await File.ReadAllTextAsync(ProjectStore.FilePath(fixture.Store)), Is.EqualTo("damaged"));
    }

    [Test]
    public async Task FactoryRejectsChangedStoreAndProjectWithoutSendingModelRequests()
    {
        var fixture = await Fixture.CreateAsync();
        var project = await SessionProjectBinding.GetProjectAsync(fixture.Store, fixture.First, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Factory.OpenAsync(
                                                             new DesktopSessionRequest(fixture.Second)
                                                                 { ProjectId = project.Id },
                                                             _ => ValueTask.CompletedTask,
                                                             _ => ValueTask.CompletedTask, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Factory.OpenAsync(
                                                             new DesktopSessionRequest(fixture.First)
                                                                 { StoreDirectory = fixture.Second },
                                                             _ => ValueTask.CompletedTask,
                                                             _ => ValueTask.CompletedTask, CancellationToken.None));
        Assert.That(Directory.GetFiles(fixture.Store, "*.session.jsonl"), Is.Empty);
    }

    [Test]
    public async Task RefreshAfterExternalBindingUpdatesTheExistingRowInPlace()
    {
        var             fixture   = await Fixture.CreateAsync();
        var             sessionId = await CommitLegacySessionWithHistoryAsync(fixture.Store, fixture.First);
        await using var model     = fixture.Window();
        await model.InitializeAsync();
        var page     = model.Sessions.Single(item => item.Id == sessionId);
        var activity = page.LastActivityUtc;
        model.SelectedSession = page;
        await page.EnsureHistoryLoadedAsync();
        var history = page.Items.Count;
        page.Draft = "draft survives refresh";
        Assert.That(history, Is.GreaterThan(0));
        Assert.That(page.ProjectId, Is.Null);
        Assert.That(model.Projects.Any(group => group.Key == PendingKey(fixture.Store, fixture.First)),
                    Is.True);

        var initialized = await SessionProjectInitializer.InitializeAsync(fixture.Store, maxSessions : 100,
                                                                          cancellationToken : CancellationToken.None);
        var outcome = initialized.Outcomes.Single(item => item.SessionId == sessionId);
        Assert.That(outcome.Status, Is.EqualTo(SessionProjectBindingStatus.Bound));
        await model.InitializeAsync();

        Assert.That(model.Sessions.Single(item => item.Id == sessionId), Is.SameAs(page));
        Assert.That(page.ProjectId, Is.EqualTo(outcome.ProjectId));
        Assert.That(page.Draft, Is.EqualTo("draft survives refresh"));
        Assert.That(page.Items.Count, Is.EqualTo(history));
        Assert.That(page.LastActivityUtc, Is.EqualTo(activity));
        Assert.That(model.Projects.Single(group => group.Key == page.ProjectId).Sessions, Does.Contain(page));
        Assert.That(model.Projects.All(group => group.Key != PendingKey(fixture.Store, fixture.First)),
                    Is.True);
        Assert.That(model.Notice, Is.Empty);
        Assert.That(fixture.Credentials.Reads, Is.Zero);
        Assert.That(fixture.Models, Is.Zero);
    }

    [Test]
    public async Task NewDraftAfterStoreSwitchSendsInCurrentStoreAndKeepsOldPageBinding()
    {
        var             fixture     = await Fixture.CreateAsync();
        var             secondStore = Path.Combine(Path.GetDirectoryName(fixture.Store)!, "sessions-2");
        await using var model       = fixture.Window();
        await model.OpenProjectAsync(fixture.First);
        var oldPage      = model.SelectedSession!;
        var oldProjectId = oldPage.ProjectId;
        oldPage.Draft = "old page keeps its binding";

        var user = await UserConfigStore.LoadAsync(fixture.Config, CancellationToken.None);
        await UserConfigStore.SaveAsync(fixture.Config, user with
        {
            Settings = (user.Settings ?? new UserConfigSettings()) with { SessionStoreDirectory = secondStore }
        }, CancellationToken.None);
        await model.InitializeAsync();

        model.NewSessionCommand.Execute(null);
        var draft = model.SelectedSession!;
        Assert.That(draft, Is.Not.SameAs(oldPage));
        for (var attempts = 0; draft.StoreDirectory is null && attempts < 500; attempts++)
            await Task.Delay(10);
        Assert.That(draft.StoreDirectory, Is.EqualTo(secondStore));

        draft.Draft = "current store request";
        await draft.SendAsync();
        await draft.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(draft.Id, Is.Not.Null);
        Assert.That(Directory.GetFiles(secondStore, "*.session.jsonl").Length, Is.EqualTo(1));
        Assert.That(Directory.GetFiles(fixture.Store, "*.session.jsonl"), Is.Empty);
        var stored = await SessionStoreReader.ReadAsync(secondStore, draft.Id!, CancellationToken.None);
        Assert.That(stored!.ProjectId, Is.EqualTo(draft.ProjectId));
        Assert.That(stored.WorkspaceRoot, Is.EqualTo(fixture.First));
        Assert.That(oldPage.ProjectId, Is.EqualTo(oldProjectId));
        Assert.That(oldPage.StoreDirectory, Is.EqualTo(fixture.Store));
        Assert.That(oldPage.Draft, Is.EqualTo("old page keeps its binding"));
        Assert.That(fixture.Models, Is.GreaterThan(0));
    }

    /// <summary>
    ///     <para>未归属旧日志的侧栏分组键：长度前缀分隔 store 与执行目录，与 MainWindowViewModel 的构造一致。</para>
    ///     <para>Group key for unbound legacy rows: a length prefix separates store and root, matching MainWindowViewModel.</para>
    /// </summary>
    private static string PendingKey(string store, string root)
    {
        var normalizedStore = ProjectRoots.Normalize(store);
        return "pending:" + normalizedStore.Length + ":" + normalizedStore + ProjectRoots.Normalize(root);
    }

    /// <summary>
    ///     <para>写入一份未绑定旧日志并追加可读历史，用于外部初始化与刷新的回归验证。</para>
    ///     <para>Commits one unbound legacy log with readable history for external-initialization refresh regressions.</para>
    /// </summary>
    private static async Task<string> CommitLegacySessionWithHistoryAsync(string store, string root)
    {
        var session = new ChatSession(Guid.NewGuid().ToString("N"), root, Fixture.Identity);
        var context = new ConversationContext(new ContextOptions());
        context.Append(ChatMessage.System("system"));
        context.Append(ChatMessage.User("question"));
        context.Append(ChatMessage.Assistant("answer"));
        using (var writer = new SessionStoreWriter(store, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
            await writer.AppendRecordsAsync(
            [
                new SessionLogRecord(string.Empty, "history",
                                     JsonSerializer.SerializeToElement(new SessionHistoryItem
                                     {
                                         Id        = string.Empty,
                                         Sequence  = 0,
                                         SessionId = session.Id,
                                         TurnId    = session.Id,
                                         Kind      = SessionHistoryKind.User,
                                         Text      = "question"
                                     }, SessionHistoryJsonContext.Default.SessionHistoryItem)),
                new SessionLogRecord(string.Empty, "history",
                                     JsonSerializer.SerializeToElement(new SessionHistoryItem
                                     {
                                         Id        = string.Empty,
                                         Sequence  = 0,
                                         SessionId = session.Id,
                                         TurnId    = session.Id,
                                         Kind      = SessionHistoryKind.Final,
                                         Text      = "answer"
                                     }, SessionHistoryJsonContext.Default.SessionHistoryItem))
            ], CancellationToken.None);
        }

        return session.Id;
    }

    private sealed class Fixture
    {
        public static readonly SessionModelIdentity Identity =
            new("offline", "https://offline.invalid/v1", ChatApiKind.ChatCompletions);

        public required string                First       { get; init; }
        public required string                Second      { get; init; }
        public required string                Store       { get; init; }
        public required string                Config      { get; init; }
        public          Credentials           Credentials { get; } = new();
        public          int                   Models      { get; private set; }
        public          TaskCompletionSource? Entered     { get; set; }
        public          TaskCompletionSource? Release     { get; set; }

        public DesktopSessionFactory Factory => new((_, _) =>
        {
            Models++;
            return new Model(Entered, Release);
        }, Credentials, Config);

        public MainWindowViewModel Window() => new(Factory, Config, action =>
        {
            action();
            return ValueTask.CompletedTask;
        });

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "tinyharness-project-entry-" + Guid.NewGuid().ToString("N"));
            var fixture = new Fixture
            {
                First  = Path.Combine(root, "a", "same"), Second = Path.Combine(root, "b", "same"),
                Store  = Path.Combine(root, "sessions"), Config = Path.Combine(root, "user.json")
            };
            Directory.CreateDirectory(fixture.First);
            Directory.CreateDirectory(fixture.Second);
            await UserConfigStore.SaveAsync(fixture.Config, new UserConfig
            {
                DefaultProfile = "offline", Profiles =
                [
                    new UserProfile
                    {
                        Name         = "offline", Endpoint = Identity.Endpoint, ApiKeyCredentialTarget = "fixture",
                        DefaultModel = "offline",
                        Models       = [new UserProfileModel { Id = "offline", ContextWindowTokens = 128_000 }]
                    }
                ],
                Settings = new UserConfigSettings { SessionStoreDirectory = fixture.Store }
            }, CancellationToken.None);
            return fixture;
        }
    }

    private sealed class Credentials : ICredentialStore
    {
        public int  Reads       { get; private set; }
        public bool IsSupported => true;

        public string? Read(string targetName)
        {
            Reads++;
            return "offline-fixture-key";
        }

        public void Save(string   targetName, string secret) => throw new NotSupportedException();
        public bool Delete(string targetName) => throw new NotSupportedException();
    }

    private sealed class Model(TaskCompletionSource? entered, TaskCompletionSource? release) : IChatCompletionClient
    {
        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
                                                                     [EnumeratorCancellation]
                                                                     CancellationToken cancellationToken)
        {
            entered?.TrySetResult();
            if (release is not null) await release.Task.WaitAsync(cancellationToken);
            yield return new ChatStreamEvent
                { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "offline answer" };
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" };
        }
    }
}

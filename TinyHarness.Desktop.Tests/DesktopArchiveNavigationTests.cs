using NUnit.Framework;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;
using TinyHarness.Desktop.Runtime;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

/// <summary>
///     <para>验证归档结果只在仍显示目标会话时切换到可复用的新对话页。</para>
///     <para>Verifies archive completion navigates to the reusable new-conversation page only while its target remains selected.</para>
/// </summary>
[TestFixture]
public sealed class DesktopArchiveNavigationTests
{
    [Test]
    public async Task ArchivingSelectedSession_NavigatesToNewConversation()
    {
        var fixture = await Fixture.CreateAsync();
        var id = await fixture.WriteSessionAsync(fixture.Root);
        await using var model = fixture.CreateWindow();
        await model.InitializeAsync();
        var row = model.Sessions.Single(session => session.Id == id);
        model.SelectSessionCommand.Execute(row);

        model.ToggleSessionArchiveCommand.Execute(row);
        await WaitUntilAsync(() => row.IsArchived);

        Assert.That(model.SelectedSession, Is.Not.SameAs(row));
        Assert.That(model.IsNewConversationSelected, Is.True);
        Assert.That(model.SelectedSession!.Id, Is.Null);
    }

    [Test]
    public async Task ArchivingNonSelectedSession_LeavesCurrentPageSelected()
    {
        var fixture = await Fixture.CreateAsync();
        var archivedId = await fixture.WriteSessionAsync(fixture.Root);
        var currentId = await fixture.WriteSessionAsync(fixture.SecondRoot);
        await using var model = fixture.CreateWindow();
        await model.InitializeAsync();
        var archived = model.Sessions.Single(session => session.Id == archivedId);
        var current = model.Sessions.Single(session => session.Id == currentId);
        model.SelectSessionCommand.Execute(current);

        model.ToggleSessionArchiveCommand.Execute(archived);
        await WaitUntilAsync(() => archived.IsArchived);

        Assert.That(model.SelectedSession, Is.SameAs(current));
    }

    [Test]
    public async Task ArchiveSaveFailure_DoesNotNavigate()
    {
        var fixture = await Fixture.CreateAsync();
        var id = await fixture.WriteSessionAsync(fixture.Root);
        await using var model = fixture.CreateWindow();
        await model.InitializeAsync();
        var row = model.Sessions.Single(session => session.Id == id);
        model.SelectSessionCommand.Execute(row);
        model.ResolveDirectoryOverride = (_, _) => throw new IOException("offline archive fixture failure");

        model.ToggleSessionArchiveCommand.Execute(row);
        await WaitUntilAsync(() => model.Notice.Contains("会话归档状态保存失败", StringComparison.Ordinal));

        Assert.That(row.IsArchived, Is.False);
        Assert.That(model.SelectedSession, Is.SameAs(row));
    }

    [Test]
    public async Task UserPageChangeDuringArchiveSave_IsNotOverridden()
    {
        var fixture = await Fixture.CreateAsync();
        var id = await fixture.WriteSessionAsync(fixture.Root);
        var otherId = await fixture.WriteSessionAsync(fixture.SecondRoot);
        await using var model = fixture.CreateWindow();
        await model.InitializeAsync();
        var row = model.Sessions.Single(session => session.Id == id);
        var other = model.Sessions.Single(session => session.Id == otherId);
        model.SelectSessionCommand.Execute(row);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.ResolveDirectoryOverride = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return fixture.Store;
        };

        model.ToggleSessionArchiveCommand.Execute(row);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        model.SelectSessionCommand.Execute(other);
        release.SetResult();
        await WaitUntilAsync(() => row.IsArchived);

        Assert.That(model.SelectedSession, Is.SameAs(other));
    }

    [Test]
    public async Task UserNavigatesAwayAndBackDuringArchiveSave_DoesNotNavigateAgain()
    {
        var fixture = await Fixture.CreateAsync();
        var id = await fixture.WriteSessionAsync(fixture.Root);
        var otherId = await fixture.WriteSessionAsync(fixture.SecondRoot);
        await using var model = fixture.CreateWindow();
        await model.InitializeAsync();
        var row = model.Sessions.Single(session => session.Id == id);
        var other = model.Sessions.Single(session => session.Id == otherId);
        model.SelectSessionCommand.Execute(row);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.ResolveDirectoryOverride = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return fixture.Store;
        };

        model.ToggleSessionArchiveCommand.Execute(row);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        model.SelectSessionCommand.Execute(other);
        model.SelectSessionCommand.Execute(row);
        release.SetResult();
        await WaitUntilAsync(() => row.IsArchived);

        Assert.That(model.SelectedSession, Is.SameAs(row));
        Assert.That(model.IsNewConversationSelected, Is.False);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!predicate())
        {
            if (DateTime.UtcNow >= timeout) Assert.Fail("Timed out waiting for the archive operation.");
            await Task.Delay(10);
        }
    }

    private sealed class Fixture
    {
        private static readonly SessionModelIdentity Identity =
            new("offline", "https://offline.invalid/v1", ChatApiKind.ChatCompletions);

        private Fixture(string root, string store, string config, string secondRoot)
        {
            Root = root;
            Store = store;
            Config = config;
            SecondRoot = secondRoot;
        }

        public string Root { get; }
        public string Store { get; }
        public string Config { get; }
        public string SecondRoot { get; }

        public MainWindowViewModel CreateWindow() => new(
            new DesktopSessionFactory((_, _) => new OfflineClient(), new Credentials(), Config), Config, action =>
            {
                action();
                return ValueTask.CompletedTask;
            });

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "tinyharness-archive-navigation-" + Guid.NewGuid().ToString("N"));
            var store = Path.Combine(root, "sessions");
            var firstRoot = Path.Combine(root, "work-a");
            var secondRoot = Path.Combine(root, "work-b");
            Directory.CreateDirectory(store);
            Directory.CreateDirectory(firstRoot);
            Directory.CreateDirectory(secondRoot);
            var config = Path.Combine(root, "user.json");
            await UserConfigStore.SaveAsync(config, new UserConfig
            {
                DefaultProfile = "offline",
                Profiles = [new UserProfile
                {
                    Name = "offline", Endpoint = Identity.Endpoint, ApiKeyCredentialTarget = "fixture",
                    DefaultModel = "offline", Models = [new UserProfileModel { Id = "offline", ContextWindowTokens = 128_000 }]
                }],
                Settings = new UserConfigSettings { SessionStoreDirectory = store }
            }, CancellationToken.None);
            return new Fixture(firstRoot, store, config, secondRoot);
        }

        public async Task<string> WriteSessionAsync(string workspaceRoot)
        {
            var session = new ChatSession(Guid.NewGuid().ToString("N"), workspaceRoot, Identity);
            var client = new OfflineClient();
            client.Enqueue(new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "offline" },
                           new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" });
            using var writer = new SessionStoreWriter(Store, session.Id);
            using var host = new ChatSessionHost(session, new ConversationContext(new ContextOptions()),
                new AgentOptions { Model = "offline", MaxAgentSteps = 10, DefaultToolTimeoutSeconds = 30 },
                client, new ToolRegistry([]), "archive navigation tests", sessionWriter: writer);
            await host.SubmitTurnAsync("seed session", CancellationToken.None);
            return session.Id;
        }
    }

    private sealed class Credentials : ICredentialStore
    {
        public bool IsSupported => true;
        public string? Read(string targetName) => "offline-fixture-key";
        public void Save(string targetName, string secret) => throw new NotSupportedException();
        public bool Delete(string targetName) => throw new NotSupportedException();
    }

    private sealed class OfflineClient : IChatCompletionClient
    {
        private readonly Queue<IReadOnlyList<ChatStreamEvent>> _responses = new();

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (_responses.Count == 0) throw new InvalidOperationException("No scripted response.");
            foreach (var item in _responses.Dequeue())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }

        public void Enqueue(params ChatStreamEvent[] events) => _responses.Enqueue(events);
    }
}

using System.Runtime.CompilerServices;
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

[TestFixture]
public sealed class DesktopArchivedSessionDeletionTests
{
    [Test]
    public async Task ConfirmDeletion_RemovesLogAndProjections_AndRefreshDoesNotRestoreRow()
    {
        using var fixture = Fixture.Create();
        var id = await fixture.CreateArchivedSessionAsync();
        await using var model = fixture.Window();
        await model.InitializeAsync();
        model.ToggleArchiveViewCommand.Execute(null);
        var row = model.Sessions.Single(session => session.Id == id);
        model.SelectSessionCommand.Execute(row);
        await row.EnsureHistoryLoadedAsync();
        row.Draft = "open runtime before deletion";
        await row.SendAsync();
        await row.Completion;
        Assert.That(row.HasOpenRuntime, Is.True, "前置：目标会话由本地 runtime 打开。 ");

        model.OpenSessionDeletionCommand.Execute(row);
        Assert.That(model.IsSessionDeletionOpen, Is.True, "删除前必须显示确认。 ");
        model.ConfirmSessionDeletionCommand.Execute(null);
        await WaitUntilAsync(() => !model.Sessions.Contains(row));

        Assert.That(File.Exists(Path.Combine(fixture.Store, id + SessionStoreWriter.LogFileSuffix)), Is.False);
        Assert.That(row.HasOpenRuntime, Is.False, "删除前应释放本地 runtime 与 writer。 ");
        Assert.That(model.SelectedSession, Is.Not.SameAs(row), "删除仍在当前页时切换到新建会话。 ");
        Assert.That((await SessionStoreArchiveStore.LoadAsync(fixture.Store, CancellationToken.None))
                        .ArchivedSessionIds,
                    Does.Not.Contain(id));
        Assert.That((await SessionStorePinStore.LoadAsync(fixture.Store, CancellationToken.None)).PinnedSessionIds,
                    Does.Not.Contain(id));
        Assert.That(await new SessionIndexStore(fixture.Store).GetRowAsync(id, CancellationToken.None), Is.Null);

        await model.InitializeAsync();
        Assert.That(model.Sessions.Any(session => session.Id == id), Is.False,
                    "刷新不得从旧索引投影恢复已删除会话。 ");
    }

    [Test]
    public async Task CancelDeletion_LeavesArchivedSessionAndLogIntact()
    {
        using var fixture = Fixture.Create();
        var id = await fixture.CreateArchivedSessionAsync();
        await using var model = fixture.Window();
        await model.InitializeAsync();
        model.ToggleArchiveViewCommand.Execute(null);
        var row = model.Sessions.Single(session => session.Id == id);

        model.OpenSessionDeletionCommand.Execute(row);
        model.CancelSessionDeletionCommand.Execute(null);

        Assert.That(model.IsSessionDeletionOpen, Is.False);
        Assert.That(model.Sessions, Does.Contain(row));
        Assert.That(File.Exists(Path.Combine(fixture.Store, id + SessionStoreWriter.LogFileSuffix)), Is.True);
        Assert.That(row.IsArchived, Is.True, "取消删除应保留恢复归档入口。 ");
    }

    [Test]
    public async Task ExternalWriterOccupancy_FailsWithoutRemovingSession()
    {
        using var fixture = Fixture.Create();
        var id = await fixture.CreateArchivedSessionAsync();
        await using var model = fixture.Window();
        await model.InitializeAsync();
        model.ToggleArchiveViewCommand.Execute(null);
        var row = model.Sessions.Single(session => session.Id == id);
        using var writer = await SessionStoreWriter.OpenExistingAsync(fixture.Store, id, null,
                                                                       CancellationToken.None);

        model.OpenSessionDeletionCommand.Execute(row);
        model.ConfirmSessionDeletionCommand.Execute(null);
        await WaitUntilAsync(() => model.SessionDeletionErrorText is not null);

        Assert.That(model.Sessions, Does.Contain(row));
        Assert.That(File.Exists(Path.Combine(fixture.Store, id + SessionStoreWriter.LogFileSuffix)), Is.True);
        Assert.That(await SessionStoreReader.ReadAsync(fixture.Store, id, CancellationToken.None), Is.Not.Null);
        Assert.That(row.HasOpenRuntime, Is.False);
    }

    [Test]
    public async Task DeleteFailureAfterRuntimeRelease_LeavesSessionReopenable()
    {
        using var fixture = Fixture.Create();
        var id = await fixture.CreateArchivedSessionAsync();
        await using var model = fixture.Window();
        await model.InitializeAsync();
        model.ToggleArchiveViewCommand.Execute(null);
        var row = model.Sessions.Single(session => session.Id == id);
        model.SelectSessionCommand.Execute(row);
        await row.EnsureHistoryLoadedAsync();
        row.Draft = "open runtime before injected failure";
        await row.SendAsync();
        await row.Completion;
        Assert.That(row.HasOpenRuntime, Is.True);

        model.DeleteSessionLogOverride = async (store, _, sessionId, token) =>
        {
            using var reopened = await SessionStoreWriter.OpenExistingAsync(store, sessionId, null, token);
            throw new IOException("Injected deletion failure after runtime release.");
        };
        model.OpenSessionDeletionCommand.Execute(row);
        model.ConfirmSessionDeletionCommand.Execute(null);
        await WaitUntilAsync(() => model.SessionDeletionErrorText is not null);

        Assert.That(model.Sessions, Does.Contain(row));
        Assert.That(model.SelectedSession, Is.SameAs(row), "删除失败应保持原页面。 ");
        Assert.That(row.HasOpenRuntime, Is.False, "失败路径不得保留已释放 runtime 的引用。 ");
        Assert.That(File.Exists(Path.Combine(fixture.Store, id + SessionStoreWriter.LogFileSuffix)), Is.True);

        model.DeleteSessionLogOverride = null;
        row.Draft = "reopen after deletion failure";
        await row.SendAsync();
        await row.Completion;
        Assert.That(row.HasOpenRuntime, Is.True, "失败后会话应重新打开 runtime 并可续聊。 ");
    }

    [Test]
    public async Task DeletionWaitsForRefreshSnapshotBeforeRemovingRow()
    {
        using var fixture = Fixture.Create();
        var id = await fixture.CreateArchivedSessionAsync();
        await using var model = fixture.Window();
        await model.InitializeAsync();
        model.ToggleArchiveViewCommand.Execute(null);
        var row = model.Sessions.Single(session => session.Id == id);
        var refreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolveCount = 0;
        model.ResolveDirectoryOverride = async (_, token) =>
        {
            if (Interlocked.Increment(ref resolveCount) == 1)
            {
                refreshEntered.TrySetResult();
                await allowRefresh.Task.WaitAsync(token);
            }

            return fixture.Store;
        };

        var refresh = model.InitializeAsync();
        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        model.OpenSessionDeletionCommand.Execute(row);
        model.ConfirmSessionDeletionCommand.Execute(null);
        await WaitUntilAsync(() => model.IsSessionDeletionRunning);
        allowRefresh.TrySetResult();
        await refresh;
        await WaitUntilAsync(() => !model.Sessions.Contains(row));

        await model.InitializeAsync();
        Assert.That(model.Sessions.Any(session => session.Id == id), Is.False);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++) await Task.Delay(10);
        Assert.That(condition(), Is.True, "等待归档会话删除异步结果超时。");
    }

    private sealed class Fixture : IDisposable
    {
        public static readonly SessionModelIdentity Identity =
            new("offline", "https://offline.invalid/v1", ChatApiKind.ChatCompletions);

        private readonly string _root = Path.Combine(Path.GetTempPath(),
                                                     "tinyharness-session-delete-" + Guid.NewGuid().ToString("N"));

        private Fixture()
        {
            Workspace = Path.Combine(_root, "workspace");
            Store = Path.Combine(_root, "sessions");
            Config = Path.Combine(_root, "user.json");
            Directory.CreateDirectory(Workspace);
            Directory.CreateDirectory(Store);
        }

        public string Workspace { get; }
        public string Store     { get; }
        public string Config    { get; }

        public static Fixture Create() => new();

        public MainWindowViewModel Window() => new(
            new DesktopSessionFactory((_, _) => new OfflineClient(), new Credentials(), Config), Config, action =>
            {
                action();
                return ValueTask.CompletedTask;
            });

        public async Task<string> CreateArchivedSessionAsync()
        {
            var session = new ChatSession(Guid.NewGuid().ToString("N"), Workspace, Identity, null, null,
                                          SessionProjectAssignmentState.NoProject);
            using (var writer = new SessionStoreWriter(Store, session.Id))
            using (var host = new ChatSessionHost(session, new ConversationContext(new ContextOptions()),
                                                 new AgentOptions
                                                 {
                                                     Model = "offline", MaxAgentSteps = 10, DefaultToolTimeoutSeconds = 30
                                                 }, new OfflineClient(),
                                                 new ToolRegistry([]), "session deletion tests", sessionWriter: writer))
                await host.SubmitTurnAsync("seed archived session", CancellationToken.None);
            await new SessionIndexReconciler(new SessionIndexStore(Store)).ReconcileAsync(CancellationToken.None);
            await SessionStoreArchiveStore.SetArchivedAsync(Store, session.Id, true, CancellationToken.None);
            await SessionStorePinStore.SetSessionPinnedAsync(Store, session.Id, true, CancellationToken.None);
            await UserConfigStore.SaveAsync(Config, new UserConfig
            {
                DefaultProfile = "offline",
                Profiles =
                [
                    new UserProfile
                    {
                        Name = "offline", Endpoint = Identity.Endpoint, ApiKeyCredentialTarget = "fixture",
                        DefaultModel = "offline",
                        Models = [new UserProfileModel { Id = "offline", ContextWindowTokens = 128_000 }]
                    }
                ],
                Settings = new UserConfigSettings { SessionStoreDirectory = Store }
            }, CancellationToken.None);
            return session.Id;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 临时测试数据清理失败不掩盖测试结果。
            }
        }
    }

    private sealed class Credentials : ICredentialStore
    {
        public bool IsSupported => true;
        public string? Read(string targetName) => "offline-test-key";
        public void Save(string targetName, string secret) => throw new NotSupportedException();
        public bool Delete(string targetName) => throw new NotSupportedException();
    }

    private sealed class OfflineClient : IChatCompletionClient
    {
        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatStreamEvent
                { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "offline answer" };
            await Task.Yield();
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" };
        }
    }
}

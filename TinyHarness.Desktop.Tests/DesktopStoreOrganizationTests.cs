using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
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
using TinyHarness.Desktop.Services;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

/// <summary>
///     <para>
///         S4 桌面组织状态测试：迁移后的 store 从业务库读取折叠/顺序，折叠切换只写业务库且桌面文件不再保存
///         该 store 的组织字段，未迁移 store 仍以桌面文件为权威。
///     </para>
///     <para>
///         S4 desktop organization tests: a migrated store reads collapse/order state from the business database,
///         collapse toggles write only the business database while the desktop file stops storing that store's
///         organization fields, and an unmigrated store keeps the desktop file authoritative.
///     </para>
/// </summary>
[TestFixture]
public sealed class DesktopStoreOrganizationTests
{
    private static readonly SessionModelIdentity Identity =
        new("offline", "https://offline.invalid/v1", ChatApiKind.ChatCompletions);

    private static string GroupKey(string projectId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectId)));

    private static async Task<Fixture> CreateAsync(bool migrated)
    {
        var root    = Path.Combine(Path.GetTempPath(), "tinyharness-org-" + Guid.NewGuid().ToString("N"));
        var fixture = new Fixture
        {
            Root    = root,
            First   = Path.Combine(root, "work"),
            Store   = Path.Combine(root, "sessions"),
            Config  = Path.Combine(root, "user.json"),
            Sidebar = Path.Combine(root, "desktop-sidebar.json")
        };
        Directory.CreateDirectory(fixture.First);
        Directory.CreateDirectory(fixture.Store);
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

        // 旧文件来源：项目目录册与两个归属会话日志（迁移前以旧文件为准）。
        var project = await ProjectStore.GetOrCreateAsync(ProjectStore.FilePath(fixture.Store), fixture.First, null,
                                                          CancellationToken.None);
        fixture.ProjectId = project.Id;
        for (var index = 0; index < 2; index++)
        {
            var session = new ChatSession(Guid.NewGuid().ToString("N"), fixture.First, Identity, null, project.Id,
                                          SessionProjectAssignmentState.Project);
            using var writer = new SessionStoreWriter(fixture.Store, session.Id);
            await writer.CommitAsync(session,
                                     new ConversationContext(new ContextOptions()),
                                     CancellationToken.None);
            fixture.SessionIds.Add(session.Id);
        }

        var collapsedKey = GroupKey(project.Id);
        await File.WriteAllTextAsync(fixture.Sidebar, JsonSerializer.Serialize(new
        {
            schema                       = "tinyharness.desktop-sidebar/1",
            isProjectMode                = true,
            sortProjectsByActivity       = false,
            showProjectDeletionPrompt    = true,
            showArchivedSessionDeletionPrompt = false,
            stores = new[]
            {
                new
                {
                    storeDirectory      = fixture.Store,
                    manualProjectOrder  = new[] { project.Id },
                    manualSessionOrder  = fixture.SessionIds.ToArray(),
                    collapsedGroupKeys  = new[] { collapsedKey }
                }
            }
        }), CancellationToken.None);

        if (migrated)
            await SessionStoreMigration.MigrateAsync(new SessionStoreMigrationOptions(fixture.Store)
            {
                SidebarPreferencesPath = fixture.Sidebar
            }, CancellationToken.None);
        return fixture;
    }

    [Test]
    public async Task MigratedStore_ReadsCollapsedStateFromBusinessDatabase()
    {
        var fixture = await CreateAsync(migrated : true);
        try
        {
            await using var model = fixture.Window();
            await model.InitializeAsync();
            var group = model.Projects.Single(item => item.Project?.Id == fixture.ProjectId);
            Assert.That(group.IsExpanded, Is.False, "已迁移 store 的折叠状态应来自业务库导入结果。");

            // 桌面文件中的应用级偏好保持不变，组织字段已由迁移消费。
            var saved = await SidebarPreferencesStore.LoadAsync(fixture.Sidebar, CancellationToken.None);
            Assert.That(saved.ShowArchivedSessionDeletionPrompt, Is.False,
                        "应用级删除提示偏好不得被组织状态迁移清空。");
            var storePreferences = saved.Stores.Single(item => ProjectRoots.AreEqual(item.StoreDirectory, fixture.Store));
            Assert.That(storePreferences.CollapsedGroupKeys, Is.Empty);
            Assert.That(storePreferences.ManualProjectOrder, Is.Empty);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task MigratedStore_ToggleCollapseWritesBusinessDatabaseNotDesktopFile()
    {
        var fixture = await CreateAsync(migrated : true);
        try
        {
            await using var model = fixture.Window();
            await model.InitializeAsync();
            var group = model.Projects.Single(item => item.Project?.Id == fixture.ProjectId);
            Assert.That(group.IsExpanded, Is.False);

            model.ToggleProjectCommand.Execute(group);
            var key = GroupKey(fixture.ProjectId);
            await WaitUntilAsync(async () =>
            {
                var state = await SessionStoreOrganizationStore.LoadAsync(fixture.Store, CancellationToken.None);
                return !state.CollapsedGroupKeys.Contains(key, StringComparer.Ordinal);
            }, "折叠状态未写入业务库。");

            // 桌面文件中的组织字段只为已迁移 store 投影为空。
            var saved = await SidebarPreferencesStore.LoadAsync(fixture.Sidebar, CancellationToken.None);
            var storePreferences = saved.Stores.Single(item => ProjectRoots.AreEqual(item.StoreDirectory, fixture.Store));
            Assert.That(storePreferences.CollapsedGroupKeys, Is.Empty);
            Assert.That(storePreferences.ManualProjectOrder, Is.Empty,
                        "已迁移 store 的组织字段不得再写回桌面文件。");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task LegacyStore_KeepsDesktopFileAuthoritative()
    {
        var fixture = await CreateAsync(migrated : false);
        try
        {
            await using var model = fixture.Window();
            await model.InitializeAsync();
            var group = model.Projects.Single(item => item.Project?.Id == fixture.ProjectId);
            Assert.That(group.IsExpanded, Is.False);

            model.ToggleProjectCommand.Execute(group);
            var key = GroupKey(fixture.ProjectId);
            await WaitUntilAsync(async () =>
            {
                var saved = await SidebarPreferencesStore.LoadAsync(fixture.Sidebar, CancellationToken.None);
                var storePreferences =
                    saved.Stores.Single(item => ProjectRoots.AreEqual(item.StoreDirectory, fixture.Store));
                return !storePreferences.CollapsedGroupKeys.Contains(key, StringComparer.Ordinal);
            }, "未迁移 store 的折叠状态未写回桌面文件。");

            Assert.That(File.Exists(SessionStoreDatabase.PathFor(fixture.Store)), Is.False,
                        "未迁移 store 不得创建业务库。");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }

        Assert.Fail(message);
    }

    private sealed class Fixture : IDisposable
    {
        public required string Root    { get; init; }
        public required string First   { get; init; }
        public required string Store   { get; init; }
        public required string Config  { get; init; }
        public required string Sidebar { get; init; }
        public          string ProjectId { get; set; } = string.Empty;
        public          List<string> SessionIds { get; } = [];

        public MainWindowViewModel Window() => new(new DesktopSessionFactory((_, _) =>
                                                                                throw new InvalidOperationException(
                                                                                    "Tests must not invoke the model."),
                                                                            new Credentials(), Config), Config,
                                                   action =>
                                                   {
                                                       action();
                                                       return ValueTask.CompletedTask;
                                                   });

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 尽力清理测试临时目录。
            }
        }
    }

    private sealed class Credentials : ICredentialStore
    {
        public bool IsSupported => true;

        public string? Read(string targetName) => "offline-fixture-key";

        public void Save(string targetName, string secret) => throw new NotSupportedException();
        public bool Delete(string targetName) => throw new NotSupportedException();
    }
}

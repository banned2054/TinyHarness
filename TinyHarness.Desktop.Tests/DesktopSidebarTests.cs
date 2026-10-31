using NUnit.Framework;
using System.Runtime.CompilerServices;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Project;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Desktop.Runtime;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class DesktopSidebarTests
{
    [TestCase(0)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(10)]
    [TestCase(12)]
    public async Task ProjectSessionsExpandFiveAtATimeUntilAllAreVisible(int count)
    {
        var             fixture = await SidebarFixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.OpenProjectAsync(fixture.First);
        for (var index = 0; index < count; index++)
            await PromoteConversationAsync(model, fixture.First, "request " + index);
        var group = model.Projects.Single();

        for (var limit = 5;; limit += 5)
        {
            var expected = new List<object> { group };
            expected.AddRange(group.Sessions.Take(limit));
            if (count > limit) expected.Add(group.MoreSessionsRow);
            Assert.That(model.SidebarRows, Is.EqualTo(expected), "每次只展示当前批次，展开入口位于项目会话底部。");
            Assert.That(group.Sessions.Count, Is.EqualTo(count), "分页不删除项目成员。");
            if (count <= limit) break;
            model.ShowMoreSessionsCommand.Execute(group);
        }

        model.ShowMoreSessionsCommand.Execute(group);
        Assert.That(model.SidebarRows, Is.EqualTo(group.Sessions.Cast<object>().Prepend(group)),
                    "全部显示后重复执行不增加行或展开入口。");
    }

    [Test]
    public async Task ShowMoreIsIndependentPerProjectAndSurvivesCollapseSelectionAndRefresh()
    {
        var             fixture = await SidebarFixture.CreateAsync();
        await using var model   = fixture.Window();
        for (var index = 0; index < 12; index++)
            await PromoteConversationAsync(model, fixture.First, "first " + index);
        for (var index = 0; index < 6; index++)
            await PromoteConversationAsync(model, fixture.Second, "second " + index);
        var first  = model.Projects.Single(group => ProjectRoots.AreEqual(group.Root, fixture.First));
        var second = model.Projects.Single(group => ProjectRoots.AreEqual(group.Root, fixture.Second));
        var more   = first.MoreSessionsRow;
        model.ShowMoreSessionsCommand.Execute(first);
        Assert.That(model.SidebarRows.OfType<SessionViewModel>(),
                    Is.EquivalentTo(first.Sessions.Take(10).Concat(second.Sessions.Take(5))));

        model.ToggleProjectCommand.Execute(first);
        Assert.That(model.SidebarRows, Does.Not.Contain(more));
        Assert.That(model.SidebarRows.OfType<SessionViewModel>(), Is.EqualTo(second.Sessions.Take(5)));
        model.ToggleProjectCommand.Execute(first);
        model.SelectSessionCommand.Execute(first.Sessions[9]);
        await model.InitializeAsync();

        Assert.That(model.Projects.Single(group => group.Key == first.Key), Is.SameAs(first));
        Assert.That(first.MoreSessionsRow, Is.SameAs(more));
        Assert.That(model.SidebarRows.OfType<SessionViewModel>(),
                    Is.EquivalentTo(first.Sessions.Take(10).Concat(second.Sessions.Take(5))));
        Assert.That(model.SelectedSession, Is.SameAs(first.Sessions[9]));

        model.ShowMoreSessionsCommand.Execute(second);
        Assert.That(model.SidebarRows, Does.Not.Contain(second.MoreSessionsRow).And.Contain(more));
        Assert.That(model.SidebarRows.OfType<SessionViewModel>(),
                    Is.EquivalentTo(first.Sessions.Take(10).Concat(second.Sessions)));
    }

    [Test]
    public async Task SidebarRowsFlattenGroupsWithMembersAndCollapseByExpansion()
    {
        var             fixture     = await SidebarFixture.CreateAsync();
        await using var model       = fixture.Window();
        var             first       = await PromoteConversationAsync(model, fixture.First, "first request");
        var             second      = await PromoteConversationAsync(model, fixture.Second, "second request");
        var             firstGroup  = model.Projects.Single(item => item.Key == first.ProjectId);
        var             secondGroup = model.Projects.Single(item => item.Key == second.ProjectId);

        Assert.That(model.SidebarRows,
                    Is.EqualTo(model.Projects.SelectMany(group => group.IsExpanded
                                                             ? group.Sessions.Cast<object>().Prepend(group)
                                                             : [group]).ToList()),
                    "侧栏行必须按项目顺序铺平，成员会话内联在展开的组头之后。");
        Assert.That(model.SidebarRows.OfType<ProjectViewModel>().Select(group => group.Key),
                    Is.EquivalentTo(new[] { firstGroup.Key, secondGroup.Key }));

        model.ToggleProjectCommand.Execute(secondGroup);
        Assert.That(model.SidebarRows, Does.Contain(secondGroup).And.Not.Contain(second));
        Assert.That(model.SidebarRows, Does.Contain(first));
        Assert.That(secondGroup.Sessions, Does.Contain(second));
        model.ToggleProjectCommand.Execute(secondGroup);
        Assert.That(model.SidebarRows, Does.Contain(second));
    }

    [Test]
    public async Task SelectionHighlightsOnlyTheGroupContainingTheSelectedSession()
    {
        var             fixture = await SidebarFixture.CreateAsync();
        await using var model   = fixture.Window();
        var             first   = await PromoteConversationAsync(model, fixture.First, "first request");
        await model.OpenProjectAsync(fixture.Second);
        var firstGroup  = model.Projects.Single(group => group.CanEdit             && group.Key == first.ProjectId);
        var secondGroup = model.Projects.Single(group => group.Project is not null && group.Key != first.ProjectId);

        model.SelectSessionCommand.Execute(first);
        Assert.That(first.IsSelected, Is.True);
        Assert.That(firstGroup.IsCurrent, Is.True);
        Assert.That(secondGroup.IsCurrent, Is.False);
        model.NewSessionCommand.Execute(null);
        Assert.That(first.IsSelected, Is.False);
        Assert.That(firstGroup.IsCurrent, Is.False, "空白新对话页不属于任何分组。");
        Assert.That(secondGroup.IsCurrent, Is.False);
    }

    /// <summary>
    ///     <para>发送一条消息把空白页提升为正式会话行并返回该页。</para>
    ///     <para>Sends one message to promote the blank page into a formal session row and returns it.</para>
    /// </summary>
    private static async Task<SessionViewModel> PromoteConversationAsync(MainWindowViewModel model, string root,
                                                                         string              text)
    {
        await model.OpenProjectAsync(root);
        var page = model.SelectedSession!;
        page.Draft = text;
        await page.SendAsync();
        await page.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(page.Id, Is.Not.Null);
        return page;
    }

    [Test]
    public async Task ProjectRenamePopupValidatesThenPersistsAndReportsConflictsInPlace()
    {
        var             fixture = await SidebarFixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.OpenProjectAsync(fixture.First);
        var group = model.Projects.Single(item => item.CanEdit);

        model.OpenProjectRenameCommand.Execute(group);
        Assert.That(model.IsProjectRenameOpen, Is.True);
        Assert.That(model.ProjectRenameDraftText, Is.EqualTo(group.Name));
        Assert.That(model.CanConfirmProjectRename, Is.False);
        model.ProjectRenameDraftText = "   ";
        Assert.That(model.CanConfirmProjectRename, Is.False);
        model.ProjectRenameDraftText = "sidebar renamed";
        Assert.That(model.CanConfirmProjectRename, Is.True);

        model.ConfirmProjectRenameCommand.Execute(null);
        await WaitUntilAsync(() => !model.IsProjectRenameOpen);
        Assert.That(model.IsProjectRenameOpen, Is.False);
        Assert.That(group.Name, Is.EqualTo("sidebar renamed"));
        var saved = await ProjectStore.LoadAsync(ProjectStore.FilePath(fixture.Store), CancellationToken.None);
        Assert.That(saved.Projects.Single(item => item.Id == group.Project!.Id).Name, Is.EqualTo("sidebar renamed"));

        await ProjectStore.RenameAsync(ProjectStore.FilePath(fixture.Store), group.Project!.Id, "external",
                                       group.Project.Revision);
        model.OpenProjectRenameCommand.Execute(group);
        Assert.That(model.ProjectRenameDraftText, Is.EqualTo("sidebar renamed"));
        model.ProjectRenameDraftText = "stale confirm";
        model.ConfirmProjectRenameCommand.Execute(null);
        await WaitUntilAsync(() => model.HasProjectRenameError);
        Assert.That(model.IsProjectRenameOpen, Is.True);
        Assert.That(model.ProjectRenameErrorText, Is.Not.Empty);
        model.CancelProjectRenameCommand.Execute(null);
        Assert.That(model.IsProjectRenameOpen, Is.False);
        Assert.That(model.HasProjectRenameError, Is.False);
    }

    [Test]
    public async Task ProjectRenameInFlightSuccessDoesNotCloseOrClearAnotherProjectsPopup()
    {
        var             fixture = await SidebarFixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.OpenProjectAsync(fixture.First);
        await model.OpenProjectAsync(fixture.Second);
        var firstGroup = model.Projects.Single(group => group.Project is not null &&
                                                        ProjectRoots.AreEqual(group.Root, fixture.First));
        var secondGroup = model.Projects.Single(group => group.Project is not null &&
                                                         ProjectRoots.AreEqual(group.Root, fixture.Second));
        var catalogPath = ProjectStore.FilePath(fixture.Store);

        // 测试独占持有目录册保存锁：A 的确认保存在锁上有界等待，取消并打开 B 的弹窗后再放行。
        await using (var gate = new FileStream(catalogPath + ProjectStore.SaveLockSuffix, FileMode.OpenOrCreate,
                                               FileAccess.ReadWrite, FileShare.None))
        {
            model.OpenProjectRenameCommand.Execute(firstGroup);
            model.ProjectRenameDraftText = "renamed a";
            Assert.That(model.CanConfirmProjectRename, Is.True);
            model.ConfirmProjectRenameCommand.Execute(null);
            Assert.That(model.CanConfirmProjectRename, Is.False, "确认后在途期间确认应被禁用。");

            model.CancelProjectRenameCommand.Execute(null);
            Assert.That(model.IsProjectRenameOpen, Is.False);
            model.OpenProjectRenameCommand.Execute(secondGroup);
            model.ProjectRenameDraftText = "keep b draft";
            Assert.That(model.IsProjectRenameOpen, Is.True);
        }

        await WaitUntilAsync(() => firstGroup.Name == "renamed a");
        Assert.That(model.IsProjectRenameOpen, Is.True, "A 保存成功不得关闭 B 的重命名弹窗。");
        Assert.That(model.ProjectRenameDraftText, Is.EqualTo("keep b draft"), "A 保存成功不得清空 B 的草稿。");
        Assert.That(model.HasProjectRenameError, Is.False, "A 保存成功不得向 B 弹窗写入错误。");
        Assert.That(model.CanConfirmProjectRename, Is.True, "在途结束后 B 应可正常确认。");

        model.ConfirmProjectRenameCommand.Execute(null);
        await WaitUntilAsync(() => !model.IsProjectRenameOpen);
        var saved = await ProjectStore.LoadAsync(catalogPath, CancellationToken.None);
        Assert.That(saved.Projects.Single(project => project.Id == firstGroup.Project!.Id).Name,
                    Is.EqualTo("renamed a"));
        Assert.That(saved.Projects.Single(project => project.Id == secondGroup.Project!.Id).Name,
                    Is.EqualTo("keep b draft"));
    }

    [Test]
    public async Task ProjectRenameInFlightFailureDoesNotWriteErrorIntoAnotherProjectsPopup()
    {
        var             fixture = await SidebarFixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.OpenProjectAsync(fixture.First);
        await model.OpenProjectAsync(fixture.Second);
        var firstGroup = model.Projects.Single(group => group.Project is not null &&
                                                        ProjectRoots.AreEqual(group.Root, fixture.First));
        var secondGroup = model.Projects.Single(group => group.Project is not null &&
                                                         ProjectRoots.AreEqual(group.Root, fixture.Second));
        var catalogPath = ProjectStore.FilePath(fixture.Store);

        // 先直接在盘上推进 A 的修订号且不刷新视图：A 的确认保存将以过期基线确定性失败。
        await ProjectStore.RenameAsync(catalogPath, firstGroup.Project!.Id, "external bump",
                                       firstGroup.Project.Revision, cancellationToken : CancellationToken.None);

        await using (var gate = new FileStream(catalogPath + ProjectStore.SaveLockSuffix, FileMode.OpenOrCreate,
                                               FileAccess.ReadWrite, FileShare.None))
        {
            model.OpenProjectRenameCommand.Execute(firstGroup);
            model.ProjectRenameDraftText = "doomed a";
            Assert.That(model.CanConfirmProjectRename, Is.True);
            model.ConfirmProjectRenameCommand.Execute(null);
            Assert.That(model.CanConfirmProjectRename, Is.False, "确认后在途期间确认应被禁用。");

            model.CancelProjectRenameCommand.Execute(null);
            Assert.That(model.IsProjectRenameOpen, Is.False);
            model.OpenProjectRenameCommand.Execute(secondGroup);
            model.ProjectRenameDraftText = "still b";
            Assert.That(model.IsProjectRenameOpen, Is.True);
        }

        await WaitUntilAsync(() => model.Notice.Length > 0);
        Assert.That(model.IsProjectRenameOpen, Is.True, "A 保存失败不得关闭 B 的重命名弹窗。");
        Assert.That(model.ProjectRenameDraftText, Is.EqualTo("still b"), "A 保存失败不得清空 B 的草稿。");
        Assert.That(model.HasProjectRenameError, Is.False, "A 的失败错误不得写入 B 的弹窗。");
        Assert.That(model.CanConfirmProjectRename, Is.True, "在途结束后 B 应可正常确认。");

        model.ConfirmProjectRenameCommand.Execute(null);
        await WaitUntilAsync(() => !model.IsProjectRenameOpen);
        var saved = await ProjectStore.LoadAsync(catalogPath, CancellationToken.None);
        Assert.That(saved.Projects.Single(project => project.Id == secondGroup.Project!.Id).Name,
                    Is.EqualTo("still b"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempts = 0; attempts < 500 && !condition(); attempts++) await Task.Delay(10);
        Assert.That(condition(), Is.True, "等待侧栏弹窗异步结果超时。");
    }

    private sealed class SidebarFixture
    {
        public static readonly SessionModelIdentity Identity =
            new("offline", "https://offline.invalid/v1", ChatApiKind.ChatCompletions);

        public required string      First       { get; init; }
        public required string      Second      { get; init; }
        public required string      Store       { get; init; }
        public required string      Config      { get; init; }
        public          Credentials Credentials { get; } = new();
        public          int         Models      { get; private set; }

        public DesktopSessionFactory Factory => new((_, _) =>
        {
            Models++;
            return new Model();
        }, Credentials, Config);

        public MainWindowViewModel Window() => new(Factory, Config, action =>
        {
            action();
            return ValueTask.CompletedTask;
        });

        public static async Task<SidebarFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "tinyharness-sidebar-" + Guid.NewGuid().ToString("N"));
            var fixture = new SidebarFixture
            {
                First = Path.Combine(root, "a", "same"), Second = Path.Combine(root, "b", "same"),
                Store = Path.Combine(root, "sessions"), Config  = Path.Combine(root, "user.json")
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
        public bool IsSupported => true;

        public string? Read(string targetName) => "offline-fixture-key";

        public void Save(string   targetName, string secret) => throw new NotSupportedException();
        public bool Delete(string targetName) => throw new NotSupportedException();
    }

    private sealed class Model : IChatCompletionClient
    {
        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
                                                                     [EnumeratorCancellation]
                                                                     CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new ChatStreamEvent
                { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "offline answer" };
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" };
        }
    }
}

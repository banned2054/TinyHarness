using NUnit.Framework;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
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

/// <summary>
///     <para>空白新对话页契约：单一可复用草稿、首发提升一次、失败保持与恢复、在途隔离与 store 重解析。</para>
///     <para>
///         Blank new-conversation page contract: one reusable draft, one-shot first-send promotion, failure retention
///         and recovery, in-flight isolation and store re-resolution.
///     </para>
/// </summary>
[TestFixture]
public sealed class DesktopBlankConversationTests
{
    [Test]
    public async Task RepeatedNewAndProjectButtonsReuseOneBlankPageWithoutRowsOrLogs()
    {
        var             fixture = await Fixture.CreateAsync();
        await using var model   = fixture.Window();
        var             blank   = model.SelectedSession!;
        Assert.That(blank.Id, Is.Null);
        model.NewSessionCommand.Execute(null);
        model.NewSessionCommand.Execute(null);
        Assert.That(model.SelectedSession, Is.SameAs(blank), "反复「新对话」必须进入同一份空白页。");
        Assert.That(model.Sessions, Is.Empty);
        Assert.That(model.SidebarRows, Is.Empty);

        await model.OpenProjectAsync(fixture.First);
        var firstGroup = model.Projects.Single(group => group.CanEdit);
        await model.NewProjectSessionAsync(firstGroup);
        Assert.That(model.SelectedSession, Is.SameAs(blank), "项目组「+」切换同一份空白页，不新建草稿页。");
        Assert.That(blank.ProjectId, Is.EqualTo(firstGroup.Project!.Id));

        await model.OpenProjectAsync(fixture.Second);
        var secondGroup = model.Projects.Single(group => group.Project!.Id != firstGroup.Project.Id);
        await model.NewProjectSessionAsync(secondGroup);
        Assert.That(model.SelectedSession, Is.SameAs(blank));
        Assert.That(blank.ProjectId, Is.EqualTo(secondGroup.Project!.Id));

        Assert.That(model.Sessions, Is.Empty, "未发送不登记正式会话行。");
        Assert.That(model.SidebarRows, Is.EquivalentTo(new object[] { firstGroup, secondGroup }),
                    "侧栏只呈现项目组头，不出现草稿行。");
        Assert.That(Directory.GetFiles(fixture.Store, "*" + SessionStoreWriter.LogFileSuffix), Is.Empty);
        Assert.That(fixture.Credentials.Reads, Is.Zero);
        Assert.That(fixture.ClientOpens, Is.Zero);
    }

    [Test]
    public async Task BlankPageKeepsTextAndPreselectionsAcrossHistoryAndProjectNavigation()
    {
        var             fixture = await Fixture.CreateAsync();
        var             id      = await CommitLegacySessionAsync(fixture.Store, fixture.First);
        await using var model   = fixture.Window();
        await model.InitializeAsync();
        var blank = model.SelectedSession!;
        Assert.That(blank.Id, Is.Null, "启动后选中的是空白新对话页。");
        blank.Draft = "keep my input";
        await blank.SelectModelAsync(new ModelChoice("offline", "offline",
                                                     Efforts : [ModelReasoningEffort.Low, ModelReasoningEffort.High]),
                                     ModelReasoningEffort.High);
        blank.AllowChanges = true;

        var history = model.Sessions.Single(session => session.Id == id);
        model.SelectSessionCommand.Execute(history);
        Assert.That(model.SelectedSession, Is.SameAs(history));
        model.NewSessionCommand.Execute(null);
        Assert.That(model.SelectedSession, Is.SameAs(blank), "返回新对话必须回到同一份空白页。");
        Assert.That(blank.Draft, Is.EqualTo("keep my input"));
        Assert.That(blank.CurrentEffortText, Is.EqualTo("high"));
        Assert.That(blank.AllowChanges, Is.True);

        // 项目「+」更改空白页目标并保留文字；新空白页的 root 不继承刚浏览的历史目录。
        await model.OpenProjectAsync(fixture.Second);
        var secondGroup = model.Projects.Single(group => ProjectRoots.AreEqual(group.Root, fixture.Second));
        await model.NewProjectSessionAsync(secondGroup);
        Assert.That(model.SelectedSession, Is.SameAs(blank));
        Assert.That(blank.Draft, Is.EqualTo("keep my input"));
        Assert.That(blank.WorkspaceRoot, Is.EqualTo(fixture.Second));
        Assert.That(blank.SelectedModel?.ModelId, Is.EqualTo("offline"), "显式模型预选不被项目切换覆盖。");
    }

    [Test]
    public async Task FirstSendPromotesTheBlankPageIntoExactlyOneFormalRow()
    {
        var             fixture = await Fixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.OpenProjectAsync(fixture.First);
        var blank = model.SelectedSession!;
        blank.Draft = "first message";
        await blank.SendAsync();
        await blank.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(blank.Id, Is.Not.Null);
        Assert.That(model.Sessions, Is.EqualTo(new[] { blank }));
        Assert.That(model.Projects.Single(group => group.Project!.Id == blank.ProjectId).Sessions, Does.Contain(blank));
        Assert.That(model.SidebarRows, Does.Contain(blank));
        Assert.That(model.IsNewConversationSelected, Is.False);

        blank.Draft = "second message";
        await blank.SendAsync();
        await blank.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(model.Sessions.Count, Is.EqualTo(1), "再次发送不得重复登记正式行。");
    }

    [Test]
    public async Task FailedRunAfterAcceptanceKeepsTheRowAndRetryReusesTheSession()
    {
        var fixture = await Fixture.CreateAsync();
        var runs    = 0;
        fixture.ClientFactory = (_, _) =>
        {
            runs++;
            return new Model(null, null, failRun : runs == 1);
        };
        await using var model = fixture.Window();
        await model.OpenProjectAsync(fixture.First);
        var blank = model.SelectedSession!;
        blank.Draft = "will fail once";
        await blank.SendAsync();
        await blank.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(blank.Id, Is.Not.Null, "发送被接受即建立真实会话，失败不伪装成草稿。");
        Assert.That(model.Sessions.Single(), Is.SameAs(blank));
        Assert.That(blank.IsRunning, Is.False);
        Assert.That(model.IsNewConversationSelected, Is.False);

        blank.Draft = "retry in the same session";
        await blank.SendAsync();
        await blank.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(model.Sessions.Count, Is.EqualTo(1), "重试复用既有会话，不重复创建。");
        Assert.That(Directory.GetFiles(fixture.Store, "*" + SessionStoreWriter.LogFileSuffix).Length, Is.EqualTo(1));
    }

    [Test]
    public async Task OpenFailureWithoutIdKeepsTheBlankPageAndRetrySucceeds()
    {
        var fixture  = await Fixture.CreateAsync();
        var attempts = 0;
        fixture.ClientFactory = (_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("offline open failure");
            return new Model(null, null);
        };
        await using var model = fixture.Window();
        await model.OpenProjectAsync(fixture.First);
        var blank = model.SelectedSession!;
        blank.Draft = "retry me";
        await Assert.ThrowsAsync<InvalidOperationException>(() => blank.SendAsync());

        Assert.That(blank.Id, Is.Null);
        Assert.That(blank.IsRunning, Is.False);
        Assert.That(blank.Draft, Is.EqualTo("retry me"), "打开失败不清空草稿文字。");
        Assert.That(blank.SelectedModel?.ModelId ?? "offline", Is.EqualTo("offline"));
        Assert.That(model.Sessions, Is.Empty);
        Assert.That(Directory.GetFiles(fixture.Store, "*" + SessionStoreWriter.LogFileSuffix), Is.Empty,
                    "客户端构造失败不落任何会话日志。");
        model.NewSessionCommand.Execute(null);
        Assert.That(model.SelectedSession, Is.SameAs(blank), "无真实 ID 的失败仍是唯一空白页。");

        await blank.SendAsync();
        await blank.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(blank.Id, Is.Not.Null);
        Assert.That(model.Sessions.Single(), Is.SameAs(blank));
        Assert.That(Directory.GetFiles(fixture.Store, "*" + SessionStoreWriter.LogFileSuffix).Length, Is.EqualTo(1));
    }

    [Test]
    public async Task LateFirstSendRegistrationDoesNotStealSelectionOrNewDraftTargets()
    {
        var fixture     = await Fixture.CreateAsync();
        var openEntered = Signal();
        var openGate    = Signal();
        fixture.ClientFactory = (_, _) =>
        {
            openEntered.TrySetResult();
            openGate.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            return new Model(null, null);
        };
        await using var model         = fixture.Window();
        using var       releaseOnExit = new GateRelease(openGate);
        await model.OpenProjectAsync(fixture.First);
        await model.OpenProjectAsync(fixture.Second);
        var secondGroup = model.Projects.Single(group => ProjectRoots.AreEqual(group.Root, fixture.Second));
        await model.OpenProjectAsync(fixture.First);
        var blank = model.SelectedSession!;
        blank.Draft = "in flight";
        var sending = blank.SendAsync();
        await openEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // 首发在途：得到独立的下一份空白页；新页改目标不影响在途目标。
        model.NewSessionCommand.Execute(null);
        var next = model.SelectedSession!;
        Assert.That(next, Is.Not.SameAs(blank));
        Assert.That(next.WorkspaceRoot, Is.EqualTo(fixture.First), "新空白页继承上一份空白页的目标。");
        next.Draft = "typed on next";
        await model.NewProjectSessionAsync(secondGroup);
        Assert.That(model.SelectedSession, Is.SameAs(next));
        Assert.That(next.WorkspaceRoot, Is.EqualTo(fixture.Second));
        Assert.That(next.Draft, Is.EqualTo("typed on next"));
        Assert.That(blank.WorkspaceRoot, Is.EqualTo(fixture.First), "在途创建目标不得被更改。");

        openGate.SetResult();
        await sending.WaitAsync(TimeSpan.FromSeconds(10));
        await blank.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(model.Sessions.Single(), Is.SameAs(blank), "迟到登记把真实会话行入册。");
        Assert.That(model.SelectedSession, Is.SameAs(next), "迟到登记不抢选中。");
        Assert.That(next.Draft, Is.EqualTo("typed on next"), "迟到登记不清其他页输入。");
        Assert.That(model.IsNewConversationSelected, Is.True);
    }

    [Test]
    public async Task PristineNextDraftIsReclaimedByAFailedFlightWithoutStealingSelection()
    {
        var fixture     = await Fixture.CreateAsync();
        var historyId   = await CommitLegacySessionAsync(fixture.Store, fixture.First);
        var openEntered = Signal();
        var openGate    = Signal();
        fixture.ClientFactory = (_, _) =>
        {
            openEntered.TrySetResult();
            openGate.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            throw new InvalidOperationException("offline open failure");
        };
        await using var model         = fixture.Window();
        using var       releaseOnExit = new GateRelease(openGate);
        await model.InitializeAsync();
        var history = model.Sessions.Single(session => session.Id == historyId);
        await model.OpenProjectAsync(fixture.First);
        var blank = model.SelectedSession!;
        Assert.That(blank.Id, Is.Null);
        blank.Draft = "flight fails";
        var sending = blank.SendAsync();
        await openEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        model.NewSessionCommand.Execute(null);
        var next = model.SelectedSession!;
        Assert.That(next, Is.Not.SameAs(blank));
        model.SelectSessionCommand.Execute(history);
        openGate.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await sending);

        Assert.That(model.SelectedSession, Is.SameAs(history), "失败不抢用户正在看的历史页。");
        Assert.That(model.HasFailedDraft, Is.False, "下一份仍空白：失败页回收为空白槽，无需恢复入口。");
        model.NewSessionCommand.Execute(null);
        Assert.That(model.SelectedSession, Is.SameAs(blank), "回收后「新对话」回到失败页并保留文字。");
        Assert.That(blank.Draft, Is.EqualTo("flight fails"));
    }

    [Test]
    public async Task FailedFlightWithTypedNextDraftExposesExplicitLosslessRecovery()
    {
        var fixture     = await Fixture.CreateAsync();
        var attempts    = 0;
        var openEntered = Signal();
        var openGate    = Signal();
        fixture.ClientFactory = (_, _) =>
        {
            openEntered.TrySetResult();
            if (Interlocked.Increment(ref attempts) == 1)
            {
                openGate.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                throw new InvalidOperationException("offline open failure");
            }

            return new Model(null, null);
        };
        await using var model         = fixture.Window();
        using var       releaseOnExit = new GateRelease(openGate);
        await model.OpenProjectAsync(fixture.First);
        var blank = model.SelectedSession!;
        blank.Draft = "flight fails";
        var sending = blank.SendAsync();
        await openEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        model.NewSessionCommand.Execute(null);
        var next = model.SelectedSession!;
        next.Draft = "typed on next";
        openGate.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await sending);

        Assert.That(model.HasFailedDraft, Is.True, "下一份已有内容：失败页需要显式恢复入口。");
        Assert.That(model.SelectedSession, Is.SameAs(next), "失败不抢导航、不覆盖新页。");
        Assert.That(next.Draft, Is.EqualTo("typed on next"));
        Assert.That(model.SidebarRows.All(row => row is not SessionViewModel rowPage || rowPage.Id is not null),
                    "失败草稿不伪装成正式侧栏行。");
        model.NewSessionCommand.Execute(null);
        Assert.That(model.SelectedSession, Is.SameAs(next), "「新对话」仍进入当前空白页，不自动跳转失败页。");

        model.ReturnToFailedDraftCommand.Execute(null);
        Assert.That(model.SelectedSession, Is.SameAs(blank));
        Assert.That(model.HasFailedDraft, Is.False, "失败页已可见：恢复入口隐藏。");
        Assert.That(blank.Draft, Is.EqualTo("flight fails"));

        await blank.SendAsync();
        await blank.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(blank.Id, Is.Not.Null);
        Assert.That(model.Sessions, Does.Contain(blank));
        Assert.That(model.HasFailedDraft, Is.False);
    }

    [Test]
    public async Task ExplicitlyRetargetedNextDraftIsNotSilentlyReclaimedByAFailure()
    {
        var fixture     = await Fixture.CreateAsync();
        var openEntered = Signal();
        var openGate    = Signal();
        fixture.ClientFactory = (_, _) =>
        {
            openEntered.TrySetResult();
            openGate.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            throw new InvalidOperationException("offline open failure");
        };
        await using var model         = fixture.Window();
        using var       releaseOnExit = new GateRelease(openGate);
        await model.OpenProjectAsync(fixture.First);
        await model.OpenProjectAsync(fixture.Second);
        var secondGroup = model.Projects.Single(group => ProjectRoots.AreEqual(group.Root, fixture.Second));
        await model.OpenProjectAsync(fixture.First);
        var blank = model.SelectedSession!;
        blank.Draft = "flight fails";
        var sending = blank.SendAsync();
        await openEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        model.NewSessionCommand.Execute(null);
        var next = model.SelectedSession!;
        // 文字为空但用户显式改过目标：失败页不得静默覆盖这份预选。
        await model.NewProjectSessionAsync(secondGroup);
        Assert.That(next.WorkspaceRoot, Is.EqualTo(fixture.Second));
        Assert.That(next.Draft, Is.Empty);
        openGate.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await sending);

        Assert.That(model.HasFailedDraft, Is.True, "显式预选过的下一份不被回收，改用恢复入口。");
        Assert.That(model.SelectedSession, Is.SameAs(next), "失败不抢导航。");
        Assert.That(next.WorkspaceRoot, Is.EqualTo(fixture.Second));
        model.ReturnToFailedDraftCommand.Execute(null);
        Assert.That(model.SelectedSession, Is.SameAs(blank));
        Assert.That(blank.Draft, Is.EqualTo("flight fails"));
    }

    [Test]
    public async Task FailureDoesNotReclaimTheCurrentlySelectedPristineNextDraft()
    {
        var fixture     = await Fixture.CreateAsync();
        var historyId   = await CommitLegacySessionAsync(fixture.Store, fixture.First);
        var openEntered = Signal();
        var openGate    = Signal();
        fixture.ClientFactory = (_, _) =>
        {
            openEntered.TrySetResult();
            openGate.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            throw new InvalidOperationException("offline open failure");
        };
        await using var model         = fixture.Window();
        using var       releaseOnExit = new GateRelease(openGate);
        await model.InitializeAsync();
        var history = model.Sessions.Single(session => session.Id == historyId);
        await model.OpenProjectAsync(fixture.First);
        var blank = model.SelectedSession!;
        blank.Draft = "flight fails";
        var sending = blank.SendAsync();
        try
        {
            await openEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // 失败到达时下一份 N 仍被选中且 pristine：N 必须留在空白槽，F 改走恢复入口。
            model.NewSessionCommand.Execute(null);
            var next = model.SelectedSession!;
            openGate.TrySetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                                                                    await sending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.That(model.SelectedSession, Is.SameAs(next), "失败不抢导航。");
            Assert.That(model.HasFailedDraft, Is.True, "被选中的 pristine 下一份不被回收，F 需要恢复入口。");

            // 之后在 N 打字、切历史再返回：输入不得静默丢失，F 也仍可恢复。
            next.Draft = "typed after failure";
            model.SelectSessionCommand.Execute(history);
            model.NewSessionCommand.Execute(null);
            Assert.That(model.SelectedSession, Is.SameAs(next), "返回新对话仍进入 N，不被失败页顶替。");
            Assert.That(next.Draft, Is.EqualTo("typed after failure"));
            model.ReturnToFailedDraftCommand.Execute(null);
            Assert.That(model.SelectedSession, Is.SameAs(blank));
            Assert.That(blank.Draft, Is.EqualTo("flight fails"));
            model.NewSessionCommand.Execute(null);
            Assert.That(model.SelectedSession, Is.SameAs(next));
        }
        finally
        {
            openGate.TrySetResult();
        }
    }

    [Test]
    public async Task LateMenuResolutionDoesNotOverwriteANewerProjectPlusSelection()
    {
        var             fixture       = await Fixture.CreateAsync();
        var             aEntered      = Signal();
        var             aRelease      = Signal();
        await using var model         = fixture.Window();
        using var       releaseOnExit = new GateRelease(aRelease);
        await model.OpenProjectAsync(fixture.First);
        await model.OpenProjectAsync(fixture.Second);
        var blank       = model.SelectedSession!;
        var groupFirst  = model.Projects.Single(group => ProjectRoots.AreEqual(group.Root, fixture.First));
        var groupSecond = model.Projects.Single(group => ProjectRoots.AreEqual(group.Root, fixture.Second));
        model.ResolveDirectoryOverride = async (root, token) =>
        {
            if (!ProjectRoots.AreEqual(root, fixture.First)) return fixture.Store;
            aEntered.TrySetResult();
            await aRelease.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            return fixture.Store;
        };

        // 菜单选 A（解析挂起）→ 组头「+」选 B（立即应用）→ A 迟到完成：不得覆盖 B。
        var selectA = model.SelectConversationProjectAsync(blank, groupFirst);
        await aEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await model.NewProjectSessionAsync(groupSecond);
        Assert.That(blank.WorkspaceRoot, Is.EqualTo(fixture.Second));
        aRelease.SetResult();
        await selectA;
        Assert.That(blank.WorkspaceRoot, Is.EqualTo(fixture.Second), "迟到的菜单解析不得覆盖更新选择。");
        Assert.That(blank.ProjectId, Is.EqualTo(groupSecond.Project!.Id));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LateOpenDirectoryResolutionDoesNotOverwriteANewerOpenSelection(bool fromHistory)
    {
        var             fixture       = await Fixture.CreateAsync();
        var             aEntered      = Signal();
        var             aRelease      = Signal();
        await using var model         = fixture.Window();
        using var       releaseOnExit = new GateRelease(aRelease);
        if (fromHistory)
        {
            var id = await CommitLegacySessionAsync(fixture.Store, fixture.First);
            await model.InitializeAsync();
            model.SelectedSession = model.Sessions.Single(page => page.Id == id);
        }

        model.ResolveDirectoryOverride = async (root, token) =>
        {
            if (!ProjectRoots.AreEqual(root, fixture.First)) return fixture.Store;
            aEntered.TrySetResult();
            await aRelease.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            return fixture.Store;
        };

        var openA = model.OpenProjectAsync(fixture.First);
        await aEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await model.OpenProjectAsync(fixture.Second).WaitAsync(TimeSpan.FromSeconds(10));
        var blank = model.SelectedSession!;
        Assert.That(blank.WorkspaceRoot, Is.EqualTo(fixture.Second));
        aRelease.TrySetResult();
        await openA.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(blank.WorkspaceRoot, Is.EqualTo(fixture.Second), "迟到的打开目录不得覆盖更新的目标。");
        Assert.That(model.SelectedSession, Is.SameAs(blank));
        Assert.That(blank.ProjectId, Is.Not.Null);
    }

    [Test]
    public async Task LateOpenDirectoryCannotRetargetAnotherPageAfterItsOriginalPageIsEstablished()
    {
        var             fixture       = await Fixture.CreateAsync();
        var             entered       = Signal();
        var             release       = Signal();
        await using var model         = fixture.Window();
        using var       releaseOnExit = new GateRelease(release);
        await model.OpenProjectAsync(fixture.First);
        var original = model.SelectedSession!;
        model.ResolveDirectoryOverride = async (root, token) =>
        {
            if (!ProjectRoots.AreEqual(root, fixture.Second)) return fixture.Store;
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            return fixture.Store;
        };

        var opening = model.OpenProjectAsync(fixture.Second);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        original.Draft = "establish original target";
        await original.SendAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await original.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        model.NewSessionCommand.Execute(null);
        var next = model.SelectedSession!;
        next.Draft = "keep next input";
        release.TrySetResult();
        await opening.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(model.SelectedSession, Is.SameAs(next));
        Assert.That(next.WorkspaceRoot, Is.EqualTo(fixture.First), "迟到操作不能改绑另一页。");
        Assert.That(next.Draft, Is.EqualTo("keep next input"));
        Assert.That(original.WorkspaceRoot, Is.EqualTo(fixture.First));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task OldStoreProjectChoiceIsResolvedAgainstTheCurrentStore(bool projectPlus)
    {
        var             fixture = await Fixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.OpenProjectAsync(fixture.First);
        var oldGroup = model.Projects.Single(group => group.CanEdit);
        var newStore = Path.Combine(Path.GetDirectoryName(fixture.Store)!, "sessions-2");
        var currentProject = await SessionProjectBinding.GetProjectAsync(newStore, fixture.First,
                                                                         CancellationToken.None);
        await fixture.UseStoreAsync(newStore);
        await model.InitializeAsync();
        var blank = model.SelectedSession!;

        if (projectPlus) await model.NewProjectSessionAsync(oldGroup);
        else await model.SelectConversationProjectAsync(blank, oldGroup);

        Assert.That(blank.StoreDirectory, Is.EqualTo(newStore));
        Assert.That(blank.ProjectId, Is.EqualTo(currentProject.Id), "旧 store 的项目 ID 不能跨 store 套用。");
        Assert.That(blank.ProjectId, Is.Not.EqualTo(oldGroup.Project!.Id));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AutomaticStoreRebindingDoesNotOutrankAUserIntent(bool userStartedBeforeRefresh)
    {
        var             fixture = await Fixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.OpenProjectAsync(fixture.Second);
        var second = model.Projects.Single(group => group.CanEdit);
        await model.OpenProjectAsync(fixture.First);
        var blank    = model.SelectedSession!;
        var newStore = Path.Combine(Path.GetDirectoryName(fixture.Store)!, "sessions-2");
        var newSecond = await SessionProjectBinding.GetProjectAsync(newStore, fixture.Second,
                                                                    CancellationToken.None);
        await fixture.UseStoreAsync(newStore);
        var       entered       = Signal();
        var       release       = Signal();
        using var releaseOnExit = new GateRelease(release);
        model.ResolveDirectoryOverride = async (root, token) =>
        {
            if (ProjectRoots.AreEqual(root, fixture.Second))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            }

            return newStore;
        };
        Task? selecting = null;
        if (userStartedBeforeRefresh)
            selecting = model.SelectConversationProjectAsync(blank, second);
        else
            model.Models.CollectionChanged += (_, _) =>
            {
                // 配置读取已开始但自动重绑尚未应用：随后发出的用户意图必须有更高优先级。
                selecting ??= model.SelectConversationProjectAsync(blank, second);
            };

        var refreshing = model.InitializeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await refreshing.WaitAsync(TimeSpan.FromSeconds(10));
        release.TrySetResult();
        await selecting!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(blank.WorkspaceRoot, Is.EqualTo(fixture.Second), "自动重绑不得在等待后抢占用户意图。");
        Assert.That(blank.ProjectId, Is.EqualTo(newSecond.Id));
        Assert.That(blank.StoreDirectory, Is.EqualTo(newStore));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PendingOpenProjectCompletesWhenRefreshAdoptsItsResolvedStore(bool cachedStore)
    {
        var fixture  = await Fixture.CreateAsync();
        var newStore = Path.Combine(Path.GetDirectoryName(fixture.Store)!, "sessions-2");
        if (!cachedStore) await fixture.UseStoreAsync(newStore);
        await using var model         = fixture.Window();
        var             entered       = Signal();
        var             release       = Signal();
        using var       releaseOnExit = new GateRelease(release);
        if (cachedStore)
        {
            await model.OpenProjectAsync(fixture.First);
            await fixture.UseStoreAsync(newStore);
        }

        var blank = model.SelectedSession!;
        model.ResolveDirectoryOverride = async (root, token) =>
        {
            if (ProjectRoots.AreEqual(root, fixture.Second))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            }

            return newStore;
        };

        var opening = model.OpenProjectAsync(fixture.Second);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await model.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(10));
        release.TrySetResult();
        await opening.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(model.SelectedSession, Is.SameAs(blank));
        Assert.That(blank.WorkspaceRoot, Is.EqualTo(fixture.Second), "刷新采用相同 store 不得丢弃显式打开意图。");
        Assert.That(blank.StoreDirectory, Is.EqualTo(newStore));
        var catalog = await ProjectStore.LoadAsync(ProjectStore.FilePath(newStore), CancellationToken.None);
        Assert.That(blank.ProjectId, Is.EqualTo(catalog.Projects.Single().Id));
    }

    [Test]
    public async Task ExplicitReadOnlyMenuSelectionProtectsAnOtherwisePristineNextDraft()
    {
        var fixture   = await Fixture.CreateAsync();
        var historyId = await CommitLegacySessionAsync(fixture.Store, fixture.First);
        var entered   = Signal();
        var release   = Signal();
        fixture.ClientFactory = (_, _) =>
        {
            entered.TrySetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            throw new InvalidOperationException("offline open failure");
        };
        await using var model         = fixture.Window();
        using var       releaseOnExit = new GateRelease(release);
        await model.InitializeAsync();
        var history = model.Sessions.Single(page => page.Id == historyId);
        await model.OpenProjectAsync(fixture.First);
        var failed = model.SelectedSession!;
        failed.AllowChanges = true;
        failed.Draft        = "flight fails with write permission";
        var sending = failed.SendAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        model.NewSessionCommand.Execute(null);
        var next = model.SelectedSession!;
        Assert.That(next.AllowChanges, Is.False);
        next.TogglePermissionMenuCommand.Execute(null);
        next.PermissionOptions.Single(option => option.Label == "只读").SelectCommand.Execute(null);
        model.SelectedSession = history;
        release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                                                                await sending.WaitAsync(TimeSpan.FromSeconds(10)));

        model.NewSessionCommand.Execute(null);
        Assert.That(model.SelectedSession, Is.SameAs(next), "显式重选默认只读也必须保留下一份页面。");
        Assert.That(next.AllowChanges, Is.False);
        Assert.That(next.HasUserPreselection, Is.True);
        Assert.That(model.HasFailedDraft, Is.True);
        model.ReturnToFailedDraftCommand.Execute(null);
        Assert.That(model.SelectedSession, Is.SameAs(failed));
        Assert.That(failed.AllowChanges, Is.True);
        Assert.That(failed.Draft, Is.EqualTo("flight fails with write permission"));
    }

    [Test]
    public async Task SameNamedProjectMenuChoicesKeepDistinctPathTipsAndTargets()
    {
        var             fixture = await Fixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.OpenProjectAsync(fixture.First);
        await model.OpenProjectAsync(fixture.Second);
        foreach (var group in model.Projects)
        {
            group.EditedName = "same name";
            await model.RenameProjectAsync(group);
        }

        var blank = model.SelectedSession!;
        blank.ToggleWorkspaceMenuCommand.Execute(null);
        var options = blank.ProjectMenuOptions.ToArray();
        Assert.That(options.Select(option => option.Label), Is.All.EqualTo("same name"));
        Assert.That(options.Select(option => option.ToolTip), Is.EquivalentTo(new[] { fixture.First, fixture.Second }),
                    "同名项目必须提供可区分的完整路径提示。");

        var changed = Signal();
        blank.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SessionViewModel.ProjectId)) changed.TrySetResult();
        };
        options.Single(option => option.ToolTip == fixture.First).SelectCommand.Execute(null);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var firstGroup = model.Projects.Single(group => ProjectRoots.AreEqual(group.Root, fixture.First));
        Assert.That(blank.WorkspaceRoot, Is.EqualTo(fixture.First));
        Assert.That(blank.ProjectId, Is.EqualTo(firstGroup.Project!.Id));
        changed = Signal();
        options.Single(option => option.ToolTip == fixture.Second).SelectCommand.Execute(null);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondGroup = model.Projects.Single(group => ProjectRoots.AreEqual(group.Root, fixture.Second));
        Assert.That(blank.WorkspaceRoot, Is.EqualTo(fixture.Second));
        Assert.That(blank.ProjectId, Is.EqualTo(secondGroup.Project!.Id));
    }

    [Test]
    public async Task MultipleFailedDraftsStayRecoverableInRotation()
    {
        var fixture     = await Fixture.CreateAsync();
        var historyId   = await CommitLegacySessionAsync(fixture.Store, fixture.First);
        var gateEntered = Signal();
        var gateRelease = Signal();
        var calls       = 0;
        fixture.ClientFactory = (_, _) =>
        {
            // 首次调用挂起后失败；后续调用立即失败：两个首发在管理器打开串行化下先后失败。
            if (Interlocked.Increment(ref calls) == 1)
            {
                gateEntered.TrySetResult();
                gateRelease.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }

            throw new InvalidOperationException("offline open failure");
        };
        await using var model         = fixture.Window();
        using var       releaseOnExit = new GateRelease(gateRelease);
        await model.InitializeAsync();
        var history = model.Sessions.Single(session => session.Id == historyId);
        await model.OpenProjectAsync(fixture.First);

        var first = model.SelectedSession!;
        first.Draft = "first flight fails";
        var firstSending = first.SendAsync();
        await gateEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // F2 在管理器打开串行化下排队等待；用户继续进入下一份并切到历史页。
        model.NewSessionCommand.Execute(null);
        var second = model.SelectedSession!;
        second.Draft = "second flight fails";
        var secondSending = second.SendAsync();
        Assert.That(second.IsRunning, Is.True);
        model.NewSessionCommand.Execute(null);
        var next = model.SelectedSession!;
        next.Draft = "typed on next";
        model.SelectSessionCommand.Execute(history);

        gateRelease.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await firstSending);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await secondSending);
        Assert.That(model.HasFailedDraft, Is.True);

        // 逐份恢复：两份失败草稿都可访问，来回切换互不覆盖。
        model.ReturnToFailedDraftCommand.Execute(null);
        var visited1 = model.SelectedSession!;
        visited1.Draft        += " edited";
        visited1.AllowChanges =  true;
        model.ReturnToFailedDraftCommand.Execute(null);
        var visited2 = model.SelectedSession!;
        Assert.That(new[] { visited1, visited2 }, Is.EquivalentTo(new[] { first, second }),
                    "两次进入恢复入口必须覆盖两份不同的失败草稿。");
        Assert.That(visited2.Draft, Does.Contain("flight fails"));
        model.ReturnToFailedDraftCommand.Execute(null);
        Assert.That(model.SelectedSession, Is.SameAs(visited1), "恢复后的草稿继续轮转可达。");
        Assert.That(visited1.Draft, Does.EndWith(" edited"));
        Assert.That(visited1.AllowChanges, Is.True, "恢复后修改的预选不被其他草稿覆盖。");
        model.NewSessionCommand.Execute(null);
        Assert.That(model.SelectedSession, Is.SameAs(next));
        Assert.That(next.Draft, Is.EqualTo("typed on next"), "恢复入口不得覆盖当前空白页输入。");
        model.SelectSessionCommand.Execute(history);
        model.ReturnToFailedDraftCommand.Execute(null);
        Assert.That(model.SelectedSession, Is.AnyOf(new[] { first, second }));
        Assert.That(model.SidebarRows.OfType<SessionViewModel>().All(page => page.Id is not null), Is.True);
    }

    [Test]
    public async Task UnboundPagesKeepTheirOwnWorkspaceLabelWhenNullProjectGroupsExist()
    {
        var fixture = await Fixture.CreateAsync();
        await CommitLegacySessionAsync(fixture.Store, fixture.First);
        await using var model = fixture.Window();
        await model.InitializeAsync();
        var legacy = model.Sessions.Single(session => session.Id is not null);
        Assert.That(legacy.ProjectId, Is.Null, "旧日志未归属时侧栏出现无项目的展示组。");

        // 空白页未绑定项目（ProjectId 为 null）时，不得误配到第一个无项目的展示组。
        var blank = model.SelectedSession!;
        Assert.That(blank.Id, Is.Null);
        blank.WorkspaceRoot = fixture.Second;
        Assert.That(blank.WorkspaceTargetLabel, Is.EqualTo("work-b"),
                    "未绑定页面必须显示自身目录标签，不得套用 null 项目的展示组名。");

        // 绑定项目后仍按项目名显示，防止修复破坏正常解析。
        await model.OpenProjectAsync(fixture.Second);
        var secondGroup = model.Projects.Single(group => ProjectRoots.AreEqual(group.Root, fixture.Second));
        secondGroup.EditedName = "second-project";
        await model.RenameProjectAsync(secondGroup);
        Assert.That(blank.WorkspaceTargetLabel, Is.EqualTo("second-project"), "已绑定项目时按项目名显示。");
    }

    [Test]
    public async Task DirectFirstSendWithoutOpenProjectBackfillsTheNewProjectGroup()
    {
        var             fixture = await Fixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.InitializeAsync();
        var blank = model.SelectedSession!;
        Assert.That(blank.ProjectId, Is.Null);
        Assert.That(model.Projects, Is.Empty, "空 store 启动后没有已知项目分组。");

        blank.WorkspaceRoot = fixture.First;
        blank.Draft         = "first message";
        // 补载完成的可观察信号：目录册分组投影重新加入带真实项目的组；不轮询不睡眠。
        var backfilled = Signal();
        model.Projects.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Add &&
                args.NewItems?.OfType<ProjectViewModel>().Any(group => group.Project is not null) == true)
                backfilled.TrySetResult();
        };
        await blank.SendAsync();
        await blank.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(blank.Id, Is.Not.Null);
        Assert.That(blank.ProjectId, Is.Not.Null);
        await backfilled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var group = model.Projects.Single(item => item.Project?.Id == blank.ProjectId);
        Assert.That(group.Sessions, Does.Contain(blank), "首发建立的会话行必须进入补载后的项目分组。");
        Assert.That(model.Projects.All(item => item.Name != "项目信息缺失"), Is.True,
                    "项目已由首发可靠建立，不得按项目信息缺失展示。");
        var catalog = await ProjectStore.LoadAsync(ProjectStore.FilePath(fixture.Store), CancellationToken.None);
        Assert.That(catalog.Projects.Select(project => project.Id), Is.EqualTo(new[] { blank.ProjectId! }),
                    "补载按目录册只读，不创建第二个项目。");
        Assert.That(Directory.GetFiles(fixture.Store, "*" + SessionStoreWriter.LogFileSuffix).Length, Is.EqualTo(1));
    }

    [Test]
    public async Task RenameAndCatalogRefreshNotifyWorkspaceTargetLabelOnFormalBlankAndFailedDraftPages()
    {
        var fixture = await Fixture.CreateAsync();
        var opens   = 0;
        var entered = Signal();
        var release = Signal();
        fixture.ClientFactory = (_, _) =>
        {
            // 第一次打开供正式页整轮完成；第二次打开挂起后失败，制造一份已绑定的失败草稿。
            if (Interlocked.Increment(ref opens) == 2)
            {
                entered.TrySetResult();
                release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                throw new InvalidOperationException("offline open failure");
            }

            return new Model(null, null);
        };
        await using var model         = fixture.Window();
        using var       releaseOnExit = new GateRelease(release);
        await model.OpenProjectAsync(fixture.First);
        var formal = model.SelectedSession!;
        formal.Draft = "formal message";
        await formal.SendAsync();
        await formal.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(formal.ProjectId, Is.Not.Null);

        model.NewSessionCommand.Execute(null);
        var failed = model.SelectedSession!;
        await model.OpenProjectAsync(fixture.First);
        failed.Draft = "flight fails";
        var sending = failed.SendAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        model.NewSessionCommand.Execute(null);
        model.SelectedSession!.Draft = "typed on next";
        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await sending);
        Assert.That(model.HasFailedDraft, Is.True, "失败页已进入恢复列表。");

        // 唯一空白页显式绑定同一项目，三类页面的目标标签都应跟随项目名。
        var blank = model.SelectedSession!;
        await model.OpenProjectAsync(fixture.First);
        Assert.That(blank.ProjectId, Is.EqualTo(formal.ProjectId));
        var formalEvents = 0;
        var failedEvents = 0;
        var blankEvents  = 0;
        formal.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SessionViewModel.WorkspaceTargetLabel)) formalEvents++;
        };
        failed.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SessionViewModel.WorkspaceTargetLabel)) failedEvents++;
        };
        blank.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SessionViewModel.WorkspaceTargetLabel)) blankEvents++;
        };

        var group = model.Projects.Single(item => item.Project?.Id == formal.ProjectId);
        group.EditedName = "renamed";
        await model.RenameProjectAsync(group);
        Assert.That(formalEvents, Is.GreaterThan(0), "改名后正式页必须收到目标标签通知。");
        Assert.That(failedEvents, Is.GreaterThan(0), "改名后失败草稿必须收到目标标签通知。");
        Assert.That(blankEvents, Is.GreaterThan(0), "改名后唯一空白页必须收到目标标签通知。");
        Assert.That(formal.WorkspaceTargetLabel, Is.EqualTo("renamed"));
        Assert.That(failed.WorkspaceTargetLabel, Is.EqualTo("renamed"));
        Assert.That(blank.WorkspaceTargetLabel, Is.EqualTo("renamed"));

        // 目录册被外部改名后经统一刷新投影：三类页面同样收到通知。
        var formalBeforeRefresh = formalEvents;
        var failedBeforeRefresh = failedEvents;
        var blankBeforeRefresh  = blankEvents;
        await ProjectStore.RenameAsync(ProjectStore.FilePath(fixture.Store), group.Project!.Id, "external",
                                       group.Project.Revision);
        await model.InitializeAsync();
        Assert.That(formalEvents, Is.GreaterThan(formalBeforeRefresh), "目录册投影更新后正式页必须收到通知。");
        Assert.That(failedEvents, Is.GreaterThan(failedBeforeRefresh), "目录册投影更新后失败草稿必须收到通知。");
        Assert.That(blankEvents, Is.GreaterThan(blankBeforeRefresh), "目录册投影更新后唯一空白页必须收到通知。");
        Assert.That(formal.WorkspaceTargetLabel, Is.EqualTo("external"));
        Assert.That(failed.WorkspaceTargetLabel, Is.EqualTo("external"));
        Assert.That(blank.WorkspaceTargetLabel, Is.EqualTo("external"));
    }

    [Test]
    public async Task UnchangedProjectionRefreshRaisesNoWorkspaceTargetLabelNotifications()
    {
        var             fixture = await Fixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.OpenProjectAsync(fixture.First);
        var page = model.SelectedSession!;
        page.Draft = "first";
        await page.SendAsync();
        await page.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        // 组名不变的活动时间重建不得触发目标标签刷新。
        var labelEvents = 0;
        page.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SessionViewModel.WorkspaceTargetLabel)) labelEvents++;
        };
        page.Draft = "second";
        await page.SendAsync();
        await page.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(labelEvents, Is.Zero, "分组名未变化的重建不得触发目标标签通知。");
        Assert.That(page.WorkspaceTargetLabel,
                    Is.EqualTo(model.Projects.Single(group => group.Project?.Id == page.ProjectId).Name));
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<string> CommitLegacySessionAsync(string store, string root)
    {
        var       session = new ChatSession(Guid.NewGuid().ToString("N"), root, Fixture.Identity);
        using var writer  = new SessionStoreWriter(store, session.Id);
        await writer.CommitAsync(session, new ConversationContext(new ContextOptions()),
                                 CancellationToken.None);
        return session.Id;
    }

    private sealed class GateRelease(TaskCompletionSource gate) : IDisposable
    {
        public void Dispose() => gate.TrySetResult();
    }

    private sealed class Fixture
    {
        public static readonly SessionModelIdentity Identity =
            new("offline", "https://offline.invalid/v1", ChatApiKind.ChatCompletions);

        public required string First  { get; init; }
        public required string Second { get; init; }
        public required string Store  { get; init; }
        public required string Config { get; init; }

        public Credentials Credentials { get; } = new();

        public int ClientOpens { get; private set; }

        public Func<TinyHarnessConfig, string, IChatCompletionClient>? ClientFactory { get; set; }

        public DesktopSessionFactory Factory => new(ClientFactory ?? DefaultClient, Credentials, Config);

        public MainWindowViewModel Window() => new(Factory, Config, action =>
        {
            action();
            return ValueTask.CompletedTask;
        });

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(),
                                    "tinyharness-blank-conversation-" + Guid.NewGuid().ToString("N"));
            var fixture = new Fixture
            {
                First = Path.Combine(root, "work-a"), Second   = Path.Combine(root, "work-b"),
                Store = Path.Combine(root, "sessions"), Config = Path.Combine(root, "user.json")
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

        public async Task UseStoreAsync(string store)
        {
            var user = await UserConfigStore.LoadAsync(Config, CancellationToken.None);
            await UserConfigStore.SaveAsync(Config, user with
            {
                Settings = (user.Settings ?? new UserConfigSettings()) with { SessionStoreDirectory = store }
            }, CancellationToken.None);
        }

        private IChatCompletionClient DefaultClient(TinyHarnessConfig config, string key)
        {
            ClientOpens++;
            return new Model(null, null);
        }
    }

    private sealed class Credentials : ICredentialStore
    {
        public int Reads { get; private set; }

        public bool IsSupported => true;

        public string? Read(string targetName)
        {
            Reads++;
            return "offline-fixture-key";
        }

        public void Save(string   targetName, string secret) => throw new NotSupportedException();
        public bool Delete(string targetName) => throw new NotSupportedException();
    }

    private sealed class Model(TaskCompletionSource? entered, TaskCompletionSource? release, bool failRun = false)
        : IChatCompletionClient
    {
        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
                                                                     [EnumeratorCancellation]
                                                                     CancellationToken cancellationToken)
        {
            if (entered is not null)
            {
                entered.TrySetResult();
                if (release is not null) await release.Task.WaitAsync(cancellationToken);
            }

            if (failRun) throw new InvalidOperationException("offline run failure");
            yield return new ChatStreamEvent
                { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "offline answer" };
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" };
        }
    }
}

using NUnit.Framework;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Principal;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Project;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Models.Session;
using TinyHarness.Desktop.Runtime;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class DesktopStartupOrganizeTests
{
    [Test]
    public async Task OneStartupOrganizesLegacyLogsAcrossBatchesAndRestartsStayIdempotent()
    {
        var fixture = await Fixture.CreateAsync();
        for (var index = 0; index < 101; index++)
            await CommitLegacySessionAsync(fixture.Store, fixture.First);
        await using var model   = fixture.Window();
        var             logPath = Directory.GetFiles(fixture.Store, "*" + SessionStoreWriter.LogFileSuffix)[0];
        var             prefix  = await File.ReadAllBytesAsync(logPath);

        await model.StartAsync();

        var entries = await SessionStoreReader.ListAsync(fixture.Store, CancellationToken.None);
        Assert.That(entries.Count(entry => entry.Session!.ProjectId is not null), Is.EqualTo(101));
        Assert.That(entries.Select(entry => entry.Session!.ProjectId).Distinct().Count(), Is.EqualTo(1),
                    "同目录旧日志必须复用同一个项目。");
        Assert.That((await File.ReadAllBytesAsync(logPath)).Take(prefix.Length), Is.EqualTo(prefix));
        Assert.That(model.OrganizeNotice, Is.Empty);
        Assert.That(fixture.Models, Is.Zero);
        Assert.That(fixture.Credentials.Reads, Is.Zero);

        var catalog = await ProjectStore.LoadAsync(ProjectStore.FilePath(fixture.Store), CancellationToken.None);
        Assert.That(catalog.Projects, Has.Count.EqualTo(1));
        var bound = await File.ReadAllBytesAsync(logPath);
        await model.StartAsync();
        Assert.That(await File.ReadAllBytesAsync(logPath), Is.EqualTo(bound), "第二次启动不得重复追加归属。");
        Assert.That(model.OrganizeNotice, Is.Empty);
    }

    [Test]
    public async Task StartupOnMissingStoreCreatesNoCatalogOrLogsAndKeepsDraftsGroup()
    {
        var fixture = await Fixture.CreateAsync();
        await using var model = fixture.Window();
        await model.StartAsync();
        Assert.That(File.Exists(ProjectStore.FilePath(fixture.Store)), Is.False, "空 store 不得生成项目目录册。");
        if (Directory.Exists(fixture.Store))
            Assert.That(Directory.GetFiles(fixture.Store, "*" + SessionStoreWriter.LogFileSuffix), Is.Empty);
        Assert.That(model.Projects.Single(group => group.Key == "drafts").Sessions,
                    Has.Count.EqualTo(1));
        Assert.That(model.OrganizeNotice, Is.Empty);
    }

    [Test]
    public async Task DraftTypingStaysAvailableWhileTheRoundRuns()
    {
        var fixture = await Fixture.CreateAsync();
        for (var index = 0; index < 101; index++)
            await CommitLegacySessionAsync(fixture.Store, fixture.First);
        await using var model   = fixture.Window();
        var             startup = model.StartAsync();
        // 目录册出现即证明扫描已在后台建立项目；此时输入必须可用。
        await WaitUntilAsync(() => File.Exists(ProjectStore.FilePath(fixture.Store)), "整理未开始写入目录册。");
        model.SelectedSession!.Draft = "typed during scan";
        await startup;
        Assert.That(model.SelectedSession.Draft, Is.EqualTo("typed during scan"));
        Assert.That(model.SelectedSession.Id, Is.Null);
        var entries = await SessionStoreReader.ListAsync(fixture.Store, CancellationToken.None);
        Assert.That(entries.All(entry => entry.Session!.ProjectId is not null), Is.True);
    }

    [Test]
    public async Task SendingDuringTheRoundBindsTheNewSessionWithoutDisturbingTheScan()
    {
        var fixture = await Fixture.CreateAsync();
        for (var index = 0; index < 101; index++)
            await CommitLegacySessionAsync(fixture.Store, fixture.First);
        await using var model   = fixture.Window();
        var             startup = model.StartAsync();
        await WaitUntilAsync(() => File.Exists(ProjectStore.FilePath(fixture.Store)), "整理未开始写入目录册。");
        model.SelectedSession!.Draft = "send during scan";
        await model.SelectedSession.SendAsync();
        await model.SelectedSession.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(model.SelectedSession.Id, Is.Not.Null);
        await startup;
        Assert.That(model.SelectedSession.ProjectId, Is.Not.Null);
        Assert.That(fixture.Models, Is.GreaterThan(0));
    }

    [Test]
    public async Task OccupiedWriterIsSkippedMarkedUnfinishedAndRetriedOnNextStartup()
    {
        var fixture = await Fixture.CreateAsync();
        var ids     = new List<string>();
        for (var index = 0; index < 3; index++)
            ids.Add(await CommitLegacySessionAsync(fixture.Store, fixture.First));
        await using (var gate = new FileStream(Path.Combine(fixture.Store, ids[0] + ".lock"), FileMode.Open,
                                               FileAccess.ReadWrite, FileShare.None))
        {
            await using var model = fixture.Window();
            await model.StartAsync();
            var entries = await SessionStoreReader.ListAsync(fixture.Store, CancellationToken.None);
            Assert.That(entries.Single(entry => entry.Id == ids[0]).Session!.ProjectId, Is.Null);
            Assert.That(entries.Where(entry => entry.Id != ids[0])
                              .All(entry => entry.Session!.ProjectId is not null), Is.True,
                        "占用项跳过时其他会话必须继续。");
            Assert.That(model.OrganizeNotice, Does.Contain("1 项待处理"));
            var group = model.Projects.Single(item => item.Key == PendingKey(fixture.Store, fixture.First));
            Assert.That(group.Name, Does.StartWith("整理未完成"));
            Assert.That(group.Detail, Does.Contain("待处理："));
        }

        await using var retried = fixture.Window();
        await retried.StartAsync();
        var bound = await SessionStoreReader.ListAsync(fixture.Store, CancellationToken.None);
        Assert.That(bound.All(entry => entry.Session!.ProjectId is not null), Is.True, "释放占用后下次启动必须重试。");
        Assert.That(retried.Projects.Any(group => group.Key.StartsWith("pending:")), Is.False);
        Assert.That(retried.OrganizeNotice, Is.Empty);
    }

    [Test]
    public async Task DamagedLogIsReportedAndNeverRepaired()
    {
        var fixture    = await Fixture.CreateAsync();
        var damagedId  = Guid.NewGuid().ToString("N");
        var damagedLog = Path.Combine(fixture.Store, damagedId + SessionStoreWriter.LogFileSuffix);
        Directory.CreateDirectory(fixture.Store);
        await File.WriteAllTextAsync(damagedLog, "{broken");
        var bytes = await File.ReadAllBytesAsync(damagedLog);
        await using var model = fixture.Window();
        await model.StartAsync();
        Assert.That(await File.ReadAllBytesAsync(damagedLog), Is.EqualTo(bytes), "损坏日志不得被改写。");
        Assert.That(model.OrganizeNotice, Does.Contain("1 项失败"));
        // 严格读取对损坏日志抛结构化异常（只有文件缺失才返回 null），不修复也不伪装成可读会话。
        await Assert.ThrowsAsync<SessionStoreException>(
            () => SessionStoreReader.ReadAsync(fixture.Store, damagedId, CancellationToken.None));
    }

    [Test]
    public async Task DamagedCatalogIsReportedPerItemAndNeverOverwritten()
    {
        var fixture = await Fixture.CreateAsync();
        await CommitLegacySessionAsync(fixture.Store, fixture.First);
        var catalogPath = ProjectStore.FilePath(fixture.Store);
        await File.WriteAllTextAsync(catalogPath, "damaged");
        await using var model = fixture.Window();
        await model.StartAsync();
        Assert.That(await File.ReadAllTextAsync(catalogPath), Is.EqualTo("damaged"), "损坏目录册不得按空目录册覆盖。");
        Assert.That(model.OrganizeNotice, Does.Contain("失败"));
        var group = model.Projects.Single(item => item.Key == PendingKey(fixture.Store, fixture.First));
        Assert.That(group.Name, Does.StartWith("整理未完成"));
    }

    [Test]
    public async Task CancelStopsTheRoundEvenWhenMoreLogsRemainAndRestartCompletes()
    {
        var fixture = await Fixture.CreateAsync();
        for (var index = 0; index < 250; index++)
            await CommitLegacySessionAsync(fixture.Store, fixture.First);
        await using var model   = fixture.Window();
        var             startup = model.StartAsync();
        await WaitUntilAsync(() => File.Exists(ProjectStore.FilePath(fixture.Store)), "第一批未完成。");
        model.CancelInitializationCommand.Execute(null);
        await startup;
        Assert.That(model.LoadingHistory, Is.False);
        var partial = await SessionStoreReader.ListAsync(fixture.Store, CancellationToken.None);
        Assert.That(partial.Count(entry => entry.Session!.ProjectId is not null), Is.LessThan(250),
                    "取消后不得继续扫描剩余日志。");

        await model.StartAsync();
        var complete = await SessionStoreReader.ListAsync(fixture.Store, CancellationToken.None);
        Assert.That(complete.All(entry => entry.Session!.ProjectId is not null), Is.True, "下次启动必须从头重试。");
    }

    [Test]
    public async Task RepeatedTriggersDuringOneRoundShareTheSameRound()
    {
        var fixture = await Fixture.CreateAsync();
        for (var index = 0; index < 101; index++)
            await CommitLegacySessionAsync(fixture.Store, fixture.First);
        await using var model   = fixture.Window();
        var             startup = model.StartAsync();
        await WaitUntilAsync(() => File.Exists(ProjectStore.FilePath(fixture.Store)), "第一批未完成。");
        await model.RefreshAsync();
        await model.RefreshAsync();
        await startup;
        var entries = await SessionStoreReader.ListAsync(fixture.Store, CancellationToken.None);
        Assert.That(entries.All(entry => entry.Session!.ProjectId is not null), Is.True);
        Assert.That(entries.Select(entry => entry.Session!.ProjectId).Distinct().Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task StoreSwitchCancelsTheOldRoundOrganizesTheNewStoreAndKeepsOldPageBindings()
    {
        var fixture     = await Fixture.CreateAsync();
        var secondStore = Path.Combine(Path.GetDirectoryName(fixture.Store)!, "sessions-2");
        for (var index = 0; index < 101; index++)
            await CommitLegacySessionAsync(fixture.Store, fixture.First);
        await using var model   = fixture.Window();
        var             startup = model.StartAsync();
        await WaitUntilAsync(() => File.Exists(ProjectStore.FilePath(fixture.Store)), "第一批未完成。");
        await WaitUntilAsync(() => model.SelectedSession!.StoreDirectory is not null, "草稿未完成首次绑定。");
        var draftStore = model.SelectedSession!.StoreDirectory;
        Assert.That(draftStore, Is.EqualTo(fixture.Store));

        var switchedId = await CommitLegacySessionAsync(secondStore, fixture.Second);
        var user = await UserConfigStore.LoadAsync(fixture.Config, CancellationToken.None);
        await UserConfigStore.SaveAsync(fixture.Config, user with
        {
            Settings = (user.Settings ?? new UserConfigSettings()) with { SessionStoreDirectory = secondStore }
        }, CancellationToken.None);
        await model.RefreshAsync();

        await WaitUntilAsync(async () =>
                                 (await SessionStoreReader.ReadAsync(secondStore, switchedId,
                                                                     CancellationToken.None))!.ProjectId is not null,
                             "新 store 的日志未被新一轮整理绑定。");
        await startup;
        Assert.That(model.SelectedSession!.StoreDirectory, Is.EqualTo(fixture.Store),
                    "旧页面必须保留原 store 绑定。");
    }

    [Test]
    public async Task ClosingDuringARoundAwaitsItAndLeavesNoLateWrites()
    {
        var fixture = await Fixture.CreateAsync();
        for (var index = 0; index < 101; index++)
            await CommitLegacySessionAsync(fixture.Store, fixture.First);
        await using var model   = fixture.Window();
        var             startup = model.StartAsync();
        await WaitUntilAsync(() => File.Exists(ProjectStore.FilePath(fixture.Store)), "第一批未完成。");
        await model.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await startup;
        var lengths = Directory.GetFiles(fixture.Store, "*" + SessionStoreWriter.LogFileSuffix)
                               .Select(File.ReadAllBytes).Select(bytes => bytes.Length).ToArray();
        await Task.Delay(300);
        Assert.That(Directory.GetFiles(fixture.Store, "*" + SessionStoreWriter.LogFileSuffix)
                              .Select(File.ReadAllBytes).Select(bytes => bytes.Length).ToArray(),
                    Is.EqualTo(lengths), "关闭后不得再有后台写入。");
    }

    [Test]
    public async Task UnboundDraftsGetTheirOwnGroupSeparatedFromLegacyLogsAndReadsStayReadOnly()
    {
        var fixture = await Fixture.CreateAsync();
        var id      = await CommitLegacySessionAsync(fixture.Store, fixture.First);
        await using var model  = fixture.Window();
        var             log    = Path.Combine(fixture.Store, id + SessionStoreWriter.LogFileSuffix);
        var             prefix = await File.ReadAllBytesAsync(log);
        await model.InitializeAsync();
        var draft = model.SelectedSession!;
        Assert.That(model.Projects.Single(group => group.Key == "drafts").Sessions, Does.Contain(draft));
        var legacy = model.Sessions.Single(session => session.Id == id);
        var group  = model.Projects.Single(item => item.Key == PendingKey(fixture.Store, fixture.First));
        Assert.That(group.Sessions, Does.Contain(legacy));
        Assert.That(group.Name, Is.EqualTo(legacy.WorkspaceLabel), "旧日志未整理前只按目录展示，不叫待初始化。");
        Assert.That(group.CanEdit, Is.False);
        Assert.That(model.Projects.Any(item => item.Name is not null && item.Name.Contains("待初始化")), Is.False);

        model.SelectedSession = legacy;
        await legacy.EnsureHistoryLoadedAsync();
        Assert.That(await File.ReadAllBytesAsync(log), Is.EqualTo(prefix), "只读刷新、历史读取与会话选择不得触发迁移。");
        Assert.That(legacy.ProjectId, Is.Null);
        Assert.That(model.OrganizeNotice, Is.Empty);
    }

    [Test]
    public async Task OrganizeNoticeTogglesRaiseHasOrganizeNoticeChanges()
    {
        var fixture = await Fixture.CreateAsync();
        var ids     = new List<string>();
        for (var index = 0; index < 3; index++)
            ids.Add(await CommitLegacySessionAsync(fixture.Store, fixture.First));
        await using var model = fixture.Window();
        var raised = new List<bool>();
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.HasOrganizeNotice))
                raised.Add(model.HasOrganizeNotice);
        };

        await using (var gate = new FileStream(Path.Combine(fixture.Store, ids[0] + ".lock"), FileMode.Open,
                                               FileAccess.ReadWrite, FileShare.None))
        {
            await model.StartAsync();
            Assert.That(model.OrganizeNotice, Does.Contain("1 项待处理"));
            Assert.That(model.HasOrganizeNotice, Is.True);
        }

        await model.StartAsync();
        Assert.That(model.OrganizeNotice, Is.Empty);
        Assert.That(model.HasOrganizeNotice, Is.False);
        Assert.That(raised, Is.EqualTo(new[] { true, false }), "空与非空互切时都必须通知 HasOrganizeNotice。");
    }

    [Test]
    public async Task CancelledStartupRoundStillLoadsUnifiedHistoryWithoutStallNotice()
    {
        var fixture = await Fixture.CreateAsync();
        for (var index = 0; index < 250; index++)
            await CommitLegacySessionAsync(fixture.Store, fixture.First);
        await using var model   = fixture.Window();
        var             startup = model.StartAsync();
        // 确定性接缝：等到首个日志已追加归属再取消——此时本批已有事实且远未结束，
        // 取消必然落在批次中段而不是项目阶段或批间空档。
        var lengths = Directory.GetFiles(fixture.Store, "*" + SessionStoreWriter.LogFileSuffix)
                               .ToDictionary(path => path, path => new FileInfo(path).Length);
        await WaitUntilAsync(() => Directory.GetFiles(fixture.Store, "*" + SessionStoreWriter.LogFileSuffix)
                                            .Any(path => new FileInfo(path).Length > lengths[path]),
                             "第一批未开始追加归属。");
        model.CancelInitializationCommand.Execute(null);
        await startup;
        Assert.That(model.OrganizeNotice, Does.Not.Contain("扫描游标未推进"), "用户取消不是停滞，不得弹停滞诊断。");
        Assert.That(model.Sessions.Any(session => session.Id is not null), Is.True,
                    "取消只停止写入，整理后的统一只读加载仍必须执行。");
        var partial = await SessionStoreReader.ListAsync(fixture.Store, CancellationToken.None);
        Assert.That(partial.Count(entry => entry.Session!.ProjectId is not null), Is.LessThan(250),
                    "取消后不得继续扫描剩余日志。");
    }

    [Test]
    public async Task ExceptionRoundClearsStaleIssuesAndMarksUnboundGroupsWithSummary()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("目录 ACL 触发的整轮异常仅在 Windows 验证。");
            return;
        }

        var fixture = await Fixture.CreateAsync();
        var ids     = new List<string>();
        for (var index = 0; index < 3; index++)
            ids.Add(await CommitLegacySessionAsync(fixture.Store, fixture.First));
        await using var model = fixture.Window();
        await using (var gate = new FileStream(Path.Combine(fixture.Store, ids[0] + ".lock"), FileMode.Open,
                                               FileAccess.ReadWrite, FileShare.None))
        {
            await model.StartAsync();
            var stale = model.Projects.Single(item => item.Key == PendingKey(fixture.Store, fixture.First));
            Assert.That(stale.Detail, Does.Contain("待处理："), "前置：上一轮已产生按目录明细。");
        }

        // 拒绝列目录使 Core 的目录枚举整体失败：整轮以异常结束，而不是逐项失败事实。
        var storeInfo = new DirectoryInfo(fixture.Store);
        var deny      = new FileSystemAccessRule(new NTAccount(Environment.UserDomainName, Environment.UserName),
                                                 FileSystemRights.ListDirectory, AccessControlType.Deny);
        try
        {
            var acl = storeInfo.GetAccessControl();
            acl.AddAccessRule(deny);
            storeInfo.SetAccessControl(acl);
            await model.RefreshAsync();
            await WaitUntilAsync(() => model.OrganizeNotice.Contains("Cannot enumerate"), "整轮异常摘要未出现。");
            await WaitUntilAsync(() => model.Projects.Single(item => item.Key == PendingKey(fixture.Store, fixture.First))
                                           .Detail?.Contains("Cannot enumerate") == true,
                                 "异常轮未为未绑定组补标统一原因。");
            Assert.That(model.OrganizeNotice, Does.Not.Contain("待处理"), "上一轮的摘要明细不得残留。");
            var group = model.Projects.Single(item => item.Key == PendingKey(fixture.Store, fixture.First));
            Assert.That(group.Name, Does.StartWith("整理未完成"));
            Assert.That(group.Detail, Does.Not.Contain("待处理："), "上一轮的按目录明细不得残留。");
        }
        finally
        {
            var acl = storeInfo.GetAccessControl();
            acl.RemoveAccessRule(deny);
            storeInfo.SetAccessControl(acl);
        }
    }

    [Test]
    public async Task ExceptionRoundAfterStoreSwitchDoesNotMarkOldStoreUnboundGroups()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("目录 ACL 触发的整轮异常仅在 Windows 验证。");
            return;
        }

        var fixture     = await Fixture.CreateAsync();
        var secondStore = Path.Combine(Path.GetDirectoryName(fixture.Store)!, "sessions-2");
        var oldId       = await CommitLegacySessionAsync(fixture.Store, fixture.First);
        var newId       = await CommitLegacySessionAsync(secondStore, fixture.Second);
        await using var model = fixture.Window();
        await using (var gate = new FileStream(Path.Combine(fixture.Store, oldId + ".lock"), FileMode.Open,
                                               FileAccess.ReadWrite, FileShare.None))
        {
            await model.StartAsync();
            // 前置：旧 store 的未绑定旧日志行留在 Sessions，且保留原 store 绑定。
            Assert.That(model.Projects.Single(item => item.Key == PendingKey(fixture.Store, fixture.First))
                          .Sessions.Single().StoreDirectory, Is.EqualTo(fixture.Store));
        }

        await using (var gate = new FileStream(Path.Combine(secondStore, newId + ".lock"), FileMode.Open,
                                               FileAccess.ReadWrite, FileShare.None))
        {
            var user = await UserConfigStore.LoadAsync(fixture.Config, CancellationToken.None);
            await UserConfigStore.SaveAsync(fixture.Config, user with
            {
                Settings = (user.Settings ?? new UserConfigSettings()) with { SessionStoreDirectory = secondStore }
            }, CancellationToken.None);
            await model.StartAsync();
            Assert.That(model.Projects.Single(item => item.Key == PendingKey(secondStore, fixture.Second))
                          .Sessions.Single().StoreDirectory, Is.EqualTo(secondStore), "前置：新 store 的未绑定行已加载。");
        }

        // 拒绝列目录使新 store 的整轮以异常结束：异常摘要只应标到新 store 的未绑定组。
        var storeInfo = new DirectoryInfo(secondStore);
        var deny      = new FileSystemAccessRule(new NTAccount(Environment.UserDomainName, Environment.UserName),
                                                 FileSystemRights.ListDirectory, AccessControlType.Deny);
        try
        {
            var acl = storeInfo.GetAccessControl();
            acl.AddAccessRule(deny);
            storeInfo.SetAccessControl(acl);
            await model.RefreshAsync();
            await WaitUntilAsync(() => model.OrganizeNotice.Contains("Cannot enumerate"), "整轮异常摘要未出现。");
            await WaitUntilAsync(() => model.Projects.Single(item => item.Key == PendingKey(secondStore, fixture.Second))
                                           .Detail?.Contains("Cannot enumerate") == true,
                                 "新 store 自身的未绑定组应获得统一原因。");
            var marked = model.Projects.Single(item => item.Key == PendingKey(secondStore, fixture.Second));
            Assert.That(marked.Detail, Does.Contain("Cannot enumerate"));
            var stale = model.Projects.Single(item => item.Key == PendingKey(fixture.Store, fixture.First));
            Assert.That(stale.Detail, Is.Null, "旧 store 的未绑定组不得标上新 store 整轮的异常摘要。");
            Assert.That(stale.Name, Does.Not.StartWith("整理未完成"));
        }
        finally
        {
            var acl = storeInfo.GetAccessControl();
            acl.RemoveAccessRule(deny);
            storeInfo.SetAccessControl(acl);
        }
    }

    [Test]
    public async Task SameProjectKeepsAllMembersOrderDraftsAndSidebarRowsAfterOrganize()
    {
        var fixture = await Fixture.CreateAsync();
        // 交错提交制造可区分的活动时间：A1(t1) < B1(t2) < B2(t3) < A2(t4)，正确的组序为 First 在 Second 前；
        // 丢失成员的错误实现只剩各组最旧成员（t1、t2），组序会翻转为 Second 在前。
        var a1 = await CommitLegacySessionAsync(fixture.Store, fixture.First);
        await Task.Delay(20);
        var b1 = await CommitLegacySessionAsync(fixture.Store, fixture.Second);
        await Task.Delay(20);
        var b2 = await CommitLegacySessionAsync(fixture.Store, fixture.Second);
        await Task.Delay(20);
        var a2 = await CommitLegacySessionAsync(fixture.Store, fixture.First);
        await using var model = fixture.Window();
        await model.StartAsync();

        SessionViewModel ById(string id) => model.Sessions.Single(session => session.Id == id);
        var first  = model.Projects.Single(group => ProjectRoots.AreEqual(group.Root, fixture.First));
        var second = model.Projects.Single(group => ProjectRoots.AreEqual(group.Root, fixture.Second));
        Assert.That(first.Sessions.Select(session => session.Id), Is.EqualTo(new[] { a2, a1 }),
                    "同项目多个会话必须全部保留并按活动时间降序。");
        Assert.That(second.Sessions.Select(session => session.Id), Is.EqualTo(new[] { b2, b1 }));
        // 启动草稿最新，草稿组排最前；First 的最近活动 t4 晚于 Second 的 t3。
        var drafts = model.Projects.Single(group => group.Key == "drafts");
        Assert.That(model.Projects, Is.EqualTo(new object[] { drafts, first, second }),
                    "项目组必须按成员最近对话活动排序。");

        await model.NewProjectSessionAsync(first);
        var draft = first.Sessions.Single(session => session.Id is null);
        Assert.That(draft.ProjectId, Is.EqualTo(first.Project!.Id));
        Assert.That(first.Sessions.Select(session => session.Id), Is.EqualTo(new string?[] { null, a2, a1 }),
                    "项目内草稿必须与全部成员留在所属项目分组。");
        Assert.That(drafts.Sessions.Select(session => session.Id), Is.EqualTo(new string?[] { null }),
                    "未发送草稿仍独立成组。");
        Assert.That(model.SidebarRows,
                    Is.EqualTo(new object[]
                    {
                        first, draft, ById(a2), ById(a1),
                        drafts, drafts.Sessions.Single(),
                        second, ById(b2), ById(b1)
                    }), "铺平侧栏行必须与分组投影一致：组头在前、成员随后且顺序稳定。");
    }

    [Test]
    public async Task ProjectGroupsKeepInstanceAndExpansionAcrossReload()
    {
        var fixture = await Fixture.CreateAsync();
        var id1     = await CommitLegacySessionAsync(fixture.Store, fixture.First);
        await Task.Delay(20);
        var id2 = await CommitLegacySessionAsync(fixture.Store, fixture.First);
        await using var model = fixture.Window();
        await model.StartAsync();

        var group  = model.Projects.Single(item => item.Key != "drafts");
        var drafts = model.Projects.Single(item => item.Key == "drafts");
        model.ToggleProjectCommand.Execute(group);
        Assert.That(group.IsExpanded, Is.False);

        await model.InitializeAsync();

        Assert.That(model.Projects.Single(item => item.Key == group.Key), Is.SameAs(group),
                    "重建必须复用原分组对象。");
        Assert.That(model.Projects.Single(item => item.Key == "drafts"), Is.SameAs(drafts));
        Assert.That(group.IsExpanded, Is.False, "重建不得重置展开状态。");
        Assert.That(group.Sessions.Select(session => session.Id).OrderBy(id => id, StringComparer.Ordinal),
                    Is.EqualTo(new[] { id1, id2 }.OrderBy(id => id, StringComparer.Ordinal)),
                    "重建后同项目成员必须完整。");
    }

    [Test]
    public async Task StartupRoundRefusesToWriteWhenStoreSitsInsideSandboxAdditionalWriteRoot()
    {
        var fixture = await Fixture.CreateAsync();
        // 沙箱启用且整个 fixture 根（含 store）是额外写根：store 不在任何日志的 WorkspaceRoot 内，
        // Core 的逐日志校验发现不了，必须由 Desktop 在写入前拒绝。
        var root = Path.GetDirectoryName(fixture.Store)!;
        var user = await UserConfigStore.LoadAsync(fixture.Config, CancellationToken.None);
        await UserConfigStore.SaveAsync(fixture.Config, user with
        {
            Settings = (user.Settings ?? new UserConfigSettings()) with
            {
                WindowsSandbox = new WindowsSandboxSettings { Enabled = true, AdditionalWriteRoots = [root] }
            }
        }, CancellationToken.None);
        var id     = await CommitLegacySessionAsync(fixture.Store, fixture.First);
        var before = SnapshotStore(fixture.Store);

        await using var model = fixture.Window();
        await model.StartAsync();

        Assert.That(SnapshotStore(fixture.Store), Is.EqualTo(before),
                    "保护拒绝前后目录内文件集与字节必须完全不变：不得创建目录册、锁或追加日志。");
        Assert.That(model.OrganizeNotice, Does.Contain("lies inside"), "拒绝原因必须进入整理摘要。");
        var entries = await SessionStoreReader.ListAsync(fixture.Store, CancellationToken.None);
        Assert.That(entries.Single().Session!.ProjectId, Is.Null, "整理不得追加归属。");
        var page = model.Sessions.Single(session => session.Id == id);
        Assert.That(page.ProjectId, Is.Null);
        var group = model.Projects.Single(item => item.Key == PendingKey(fixture.Store, fixture.First));
        Assert.That(group.Name, Does.StartWith("整理未完成"));
        Assert.That(group.Detail, Does.Contain("lies inside"), "只读历史可见且未完成组带拒绝原因。");
    }

    [Test]
    public async Task SameRootAcrossTwoStoresKeepsPendingGroupsAndIssuesIsolated()
    {
        var fixture     = await Fixture.CreateAsync();
        var secondStore = Path.Combine(Path.GetDirectoryName(fixture.Store)!, "sessions-2");
        var oldId       = await CommitLegacySessionAsync(fixture.Store, fixture.First);
        var newId       = await CommitLegacySessionAsync(secondStore, fixture.First);
        await using var model = fixture.Window();
        await using (var gate = new FileStream(Path.Combine(fixture.Store, oldId + ".lock"), FileMode.Open,
                                               FileAccess.ReadWrite, FileShare.None))
        {
            await model.StartAsync();
            // 前置：旧 store 的未绑定行已按目录成组并带本 store 的待处理原因。
            var oldGroup = model.Projects.Single(item => item.Key == PendingKey(fixture.Store, fixture.First));
            Assert.That(oldGroup.Sessions.Single().StoreDirectory, Is.EqualTo(fixture.Store));
            Assert.That(oldGroup.Detail, Does.Contain("待处理"));
        }

        await using (var gate = new FileStream(Path.Combine(secondStore, newId + ".lock"), FileMode.Open,
                                               FileAccess.ReadWrite, FileShare.None))
        {
            var user = await UserConfigStore.LoadAsync(fixture.Config, CancellationToken.None);
            await UserConfigStore.SaveAsync(fixture.Config, user with
            {
                Settings = (user.Settings ?? new UserConfigSettings()) with { SessionStoreDirectory = secondStore }
            }, CancellationToken.None);
            await model.RefreshAsync();
            await WaitUntilAsync(() => model.Projects.Any(item =>
                                        item.Key == PendingKey(secondStore, fixture.First) &&
                                        item.Detail?.Contains("待处理") == true),
                                 "新 store 的未完成组未带原因。");
        }

        var current = model.Projects.Single(item => item.Key == PendingKey(secondStore, fixture.First));
        Assert.That(current.Sessions.Select(session => session.StoreDirectory), Is.EqualTo(new[] { secondStore }),
                    "新 store 组只含新 store 的行。");
        var stale = model.Projects.Single(item => item.Key == PendingKey(fixture.Store, fixture.First));
        Assert.That(stale.Sessions.Select(session => session.StoreDirectory), Is.EqualTo(new[] { fixture.Store }),
                    "旧 store 组只含旧 store 的行。");
        Assert.That(stale.Detail, Is.Null, "旧 store 的组不得串用新 store 的整理原因。");
        Assert.That(stale.Name, Is.Not.StartsWith("整理未完成"));
    }

    [Test]
    public async Task RoundSkipsWritesWithObservableNoticeWhenResolvedStoreDriftsFromCapturedStore()
    {
        var fixture = await Fixture.CreateAsync();
        await CommitLegacySessionAsync(fixture.Store, fixture.First);
        // 首导空格使轻量解析视为相对路径并拼上 cwd，而受保护解析会 trim：两者不一致必须跳过写入并给出可见原因。
        var user = await UserConfigStore.LoadAsync(fixture.Config, CancellationToken.None);
        await UserConfigStore.SaveAsync(fixture.Config, user with
        {
            Settings = (user.Settings ?? new UserConfigSettings()) with
                { SessionStoreDirectory = " " + fixture.Store }
        }, CancellationToken.None);
        var before = SnapshotStore(fixture.Store);

        await using var model = fixture.Window();
        await model.StartAsync();

        Assert.That(SnapshotStore(fixture.Store), Is.EqualTo(before), "解析不一致时不得写入捕获的 store。");
        Assert.That(model.OrganizeNotice, Does.Contain("不一致"), "跳过必须给出可观察的未完成原因，而非静默成功。");
        var entries = await SessionStoreReader.ListAsync(fixture.Store, CancellationToken.None);
        Assert.That(entries.Single().Session!.ProjectId, Is.Null, "未写入即未绑定。");
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
    ///     <para>记录目录内全部文件的相对文件名与字节，用于断言拒绝整理前后完全无写入。</para>
    ///     <para>Records every file's name and bytes under the store to assert zero writes across a refused round.</para>
    /// </summary>
    private static IReadOnlyList<KeyValuePair<string, byte[]>> SnapshotStore(string store) =>
        Directory.GetFiles(store, "*", SearchOption.AllDirectories)
                 .OrderBy(path => path, StringComparer.Ordinal)
                 .Select(path => new KeyValuePair<string, byte[]>(Path.GetFileName(path), File.ReadAllBytes(path)))
                 .ToArray();

    private static async Task<string> CommitLegacySessionAsync(string store, string root)
    {
        var session = new ChatSession(Guid.NewGuid().ToString("N"), root, Fixture.Identity);
        using var writer = new SessionStoreWriter(store, session.Id);
        await writer.CommitAsync(session, new ConversationContext(new ContextOptions()), CancellationToken.None);
        return session.Id;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        for (var attempts = 0; attempts < 1000 && !condition(); attempts++) await Task.Delay(10);
        Assert.That(condition(), Is.True, message);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string message)
    {
        for (var attempts = 0; attempts < 1000; attempts++)
            if (await condition())
                return;
        Assert.That(await condition(), Is.True, message);
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

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "tinyharness-startup-organize-" + Guid.NewGuid().ToString("N"));
            var fixture = new Fixture
            {
                First  = Path.Combine(root, "work-a"),
                Second = Path.Combine(root, "work-b"),
                Store  = Path.Combine(root, "sessions"),
                Config = Path.Combine(root, "user.json")
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

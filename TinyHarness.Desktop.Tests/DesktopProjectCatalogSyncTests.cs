using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Project;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Desktop.Runtime;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

/// <summary>
///     <para>
///         验证项目目录册跨进程自动同步：外部建/改/删经一次检查收敛、未变只确认、损坏与缺失与共享冲突
///         的快照保护、恢复后失败状态复位、生命周期幂等与关闭退出、store 切换迟到结果让位与读取失败清空。
///     </para>
///     <para>
///         Verifies cross-process project-catalog auto sync: external create/rename/delete converge in one check,
///         unchanged content only confirms, damaged or missing or contended files keep snapshots, recovery resets
///         the failure state, lifecycle stays idempotent and exits on shutdown, and a switched store yields late
///         results and clears its snapshot on a failed read.
///     </para>
/// </summary>
/// <remarks>
///     <para>全部夹具位于 %TEMP%；外部写者直接调用 ProjectStore 公开 API，不经被测窗口写入。</para>
///     <para>
///         All fixtures live under %TEMP%; the external writer calls public ProjectStore APIs directly and never
///         goes through the window under test.
///     </para>
/// </remarks>
[TestFixture]
public sealed class DesktopProjectCatalogSyncTests
{
    private static readonly SessionModelIdentity Identity =
        new("offline", "https://offline.invalid/v1", ChatApiKind.ChatCompletions);

    /// <summary>
    ///     <para>外部“另一进程”新建项目后，一次检查即发现新项目并保持侧栏投影一致。</para>
    ///     <para>
    ///         After another "process" creates a project externally, one check discovers it and keeps the sidebar
    ///         projection consistent.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ExternalCreateIsPickedUpByOneCheckAndProjectedIntoSidebar()
    {
        var             fixture = await CatalogFixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.InitializeAsync();
        Assert.That(model.Projects, Is.Empty, "空 store 初始化后不得有项目组。");

        var created = await CreateExternalAsync(fixture.Store, fixture.First);
        await model.CheckProjectCatalogAsync(CancellationToken.None);

        var group = model.Projects.Single(item => item.Project is not null);
        Assert.That(group.Project!.Id, Is.EqualTo(created.Id), "外部新建的项目必须出现在项目组中。");
        Assert.That(group.Name, Is.EqualTo(created.Name), "组名必须与目录册一致。");
        Assert.That(ProjectRoots.AreEqual(group.Root, created.PrimaryRoot), Is.True, "组根必须与项目主目录一致。");
        Assert.That(model.Projects.Count(item => item.Project is not null), Is.EqualTo(1), "不得产生重复组。");
        var category = model.SidebarRows.OfType<SidebarCategoryViewModel>().Single();
        Assert.That(model.SidebarRows,
                    Is.EqualTo(new List<object> { category, group }.Concat(group.Sessions).ToList()),
                    "侧栏投影必须与分组一致：分类在前、组头随后、成员内联。");
    }

    /// <summary>
    ///     <para>外部改名后一次检查更新组名，且组实例、选中会话与展开态全部保持。</para>
    ///     <para>
    ///         One check after an external rename updates the group name while the group instance, the selected
    ///         session, and the expansion state all survive.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ExternalRenameUpdatesNameAndKeepsGroupInstanceSelectionAndExpansion()
    {
        var             fixture   = await CatalogFixture.CreateAsync();
        var             sessionId = await CommitLegacySessionAsync(fixture.Store, fixture.First);
        await using var model     = fixture.Window();
        await model.StartAsync();

        var group = model.Projects.Single(item => item.CanEdit);
        var row   = group.Sessions.Single(item => item.Id == sessionId);
        model.SelectSessionCommand.Execute(row);
        Assert.That(model.SelectedSession, Is.SameAs(row), "前置：会话行已选中。");
        model.ToggleProjectCommand.Execute(group);
        Assert.That(group.IsExpanded, Is.False, "前置：组已折叠。");

        var renamed = await RenameExternalAsync(fixture.Store, group.Project!.Id, "外部改名项目");
        await model.CheckProjectCatalogAsync(CancellationToken.None);

        Assert.That(model.Projects.Single(item => item.Key == group.Key), Is.SameAs(group),
                    "改名后必须复用原分组实例。");
        Assert.That(group.Name, Is.EqualTo(renamed.Name), "组名必须跟随目录册更新。");
        Assert.That(group.IsExpanded, Is.False, "重建不得重置展开状态。");
        Assert.That(model.SelectedSession, Is.SameAs(row), "改名重建不得改变选中会话。");
        Assert.That(row.ProjectId, Is.EqualTo(renamed.Id), "选中会话的归属不得改变。");
        Assert.That(model.Projects, Has.Count.EqualTo(1), "不得产生重复组。");
    }

    /// <summary>
    ///     <para>外部改名后检查按标签值差集通知：目标标签变化的页面恰好收到一次通知。</para>
    ///     <para>
    ///         One check after an external rename notifies by the label value diff: pages whose target label
    ///         changed receive exactly one notification.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ExternalRenameNotifiesChangedWorkspaceTargetLabelsOnce()
    {
        var             fixture = await CatalogFixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.InitializeAsync();
        await model.OpenProjectAsync(fixture.First);
        var group = model.Projects.Single(item => item.CanEdit);
        var page  = model.SelectedSession!;
        Assert.That(page.WorkspaceTargetLabel, Is.EqualTo(group.Name), "前置：空白页目标标签为项目名。");

        var raised = 0;
        page.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SessionViewModel.WorkspaceTargetLabel)) raised++;
        };

        var renamed = await RenameExternalAsync(fixture.Store, group.Project!.Id, "改名后的标签");
        await model.CheckProjectCatalogAsync(CancellationToken.None);

        Assert.That(group.Name, Is.EqualTo(renamed.Name), "前置：组名已更新。");
        Assert.That(page.WorkspaceTargetLabel, Is.EqualTo(renamed.Name), "目标标签必须跟随目录册改名。");
        Assert.That(raised, Is.EqualTo(1), "标签值差集通知：值变化的页面恰好通知一次。");
    }

    /// <summary>
    ///     <para>外部删除标记呈现删除中状态，完成后一次检查移除项目组。</para>
    ///     <para>
    ///         An external deletion marker renders the deleting state, and one check after completion removes the
    ///         project group.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ExternalDeletionShowsMarkerStateThenRemovalDropsTheGroup()
    {
        var             fixture = await CatalogFixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.InitializeAsync();
        var created = await CreateExternalAsync(fixture.Store, fixture.First);
        await model.CheckProjectCatalogAsync(CancellationToken.None);
        var group = model.Projects.Single(item => item.CanEdit);

        var deleting = await ProjectStore.BeginDeletionAsync(ProjectStore.FilePath(fixture.Store), created.Id,
                                                             created.Revision, null, CancellationToken.None);
        await model.CheckProjectCatalogAsync(CancellationToken.None);
        Assert.That(model.Projects.Single(item => item.Key == group.Key), Is.SameAs(group),
                    "删除标记阶段必须复用原分组实例。");
        Assert.That(group.IsDeleting, Is.True, "外部删除标记必须呈现为删除中。");
        Assert.That(group.DeleteActionText, Is.EqualTo("继续删除项目"), "删除中组必须给出继续删除入口。");

        await ProjectStore.CompleteDeletionAsync(ProjectStore.FilePath(fixture.Store), created.Id, deleting.Revision,
                                                 CancellationToken.None);
        await model.CheckProjectCatalogAsync(CancellationToken.None);
        Assert.That(model.Projects.Any(item => item.Project is not null), Is.False, "完成后项目组必须消失。");
        Assert.That(model.SidebarRows, Does.Not.Contain(group), "侧栏投影不得残留已删除组。");
    }

    /// <summary>
    ///     <para>同一目录册内容回写（修改时间推进）只确认基线，不触发任何集合重建。</para>
    ///     <para>
    ///         Rewriting the identical catalog content (with an advanced write time) only confirms the baseline and
    ///         triggers no collection rebuild.
    ///     </para>
    /// </summary>
    [Test]
    public async Task UnchangedRewriteOnlyConfirmsBaselineWithoutRebuilding()
    {
        var             fixture = await CatalogFixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.InitializeAsync();
        await CreateExternalAsync(fixture.Store, fixture.First);
        await model.CheckProjectCatalogAsync(CancellationToken.None);
        var groups = model.Projects.ToArray();

        var changes = new List<NotifyCollectionChangedAction>();
        model.Projects.CollectionChanged += (_, args) => changes.Add(args.Action);

        var path  = ProjectStore.FilePath(fixture.Store);
        var bytes = await File.ReadAllBytesAsync(path, CancellationToken.None);
        await Task.Delay(20);
        await File.WriteAllBytesAsync(path, bytes, CancellationToken.None);
        await model.CheckProjectCatalogAsync(CancellationToken.None);

        Assert.That(changes, Is.Empty, "内容未变时不得触发任何集合变更（无 Reset、Add 或 Remove）。");
        Assert.That(model.Projects.ToArray(), Is.EquivalentTo(groups), "内容未变时组实例必须全部保持原引用。");
    }

    /// <summary>
    ///     <para>目录册损坏保留最后成功快照、失败提示去重一次，修复为合法新内容后自动应用且提示不增长。</para>
    ///     <para>
    ///         A damaged catalog keeps the last successful snapshot with the failure notice deduplicated once; after
    ///         repair with new legal content the check applies it and the notice never grows.
    ///     </para>
    /// </summary>
    [Test]
    public async Task DamagedCatalogKeepsSnapshotNoticesOnceAndRecoversAfterRepair()
    {
        var             fixture = await CatalogFixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.InitializeAsync();
        await CreateExternalAsync(fixture.Store, fixture.First);
        await model.CheckProjectCatalogAsync(CancellationToken.None);
        var group = model.Projects.Single(item => item.CanEdit);
        Assert.That(model.Notice, Is.Empty, "前置：初始化后无失败提示。");

        var notices = new List<string>();
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.Notice)) notices.Add(model.Notice);
        };

        var path    = ProjectStore.FilePath(fixture.Store);
        var healthy = await ProjectStore.LoadAsync(path, CancellationToken.None);
        await File.WriteAllTextAsync(path, "{broken", CancellationToken.None);
        await model.CheckProjectCatalogAsync(CancellationToken.None);
        await model.CheckProjectCatalogAsync(CancellationToken.None);

        Assert.That(model.Projects.Single(item => item.Key == group.Key), Is.SameAs(group),
                    "损坏期间必须保留最后成功快照。");
        Assert.That(model.Notice, Does.Contain("项目目录自动同步失败"), "持续损坏必须出现脱敏的同步失败提示。");
        Assert.That(notices.Count(item => item.Contains("项目目录自动同步失败")), Is.EqualTo(1),
                    "同一持续故障必须去重：失败提示只写一次。");

        var repaired = healthy with
        {
            Revision = healthy.Revision + 1,
            Projects = [healthy.Projects[0] with { Name = "修复后的项目", Revision = healthy.Projects[0].Revision + 1 }]
        };
        await File.WriteAllTextAsync(path,
                                     JsonSerializer.Serialize(repaired, ProjectJsonContext.Default.ProjectCatalog),
                                     CancellationToken.None);
        await model.CheckProjectCatalogAsync(CancellationToken.None);

        Assert.That(group.Name, Is.EqualTo("修复后的项目"), "恢复后的新内容必须自动应用。");
        Assert.That(notices.Count(item => item.Contains("项目目录自动同步失败")), Is.EqualTo(1),
                    "恢复后失败提示不得再次增长。");
    }

    /// <summary>
    ///     <para>
    ///         检查失败后文件恢复原值：Unchanged 结果复位失败状态，再次失败能重新提示，且共享的
    ///         Notice 文本不被清空。
    ///     </para>
    ///     <para>
    ///         After a failed check the file returns to its confirmed value: the Unchanged result resets the
    ///         failure state so a renewed failure notifies again, and the shared Notice text stays untouched.
    ///     </para>
    /// </summary>
    [Test]
    public async Task UnchangedAfterRecoveryResetsFailureStateAndKeepsTheNotice()
    {
        var             fixture = await CatalogFixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.InitializeAsync();
        // 先对空目录册做一次检查确认“缺失”基线：删除损坏文件后 stat 恰好回到基线，才能走到 Unchanged 恢复路径。
        await model.CheckProjectCatalogAsync(CancellationToken.None);
        Assert.That(model.Projects, Is.Empty, "前置：空 store 无项目组。");

        var path    = ProjectStore.FilePath(fixture.Store);
        var created = await CreateExternalAsync(fixture.Store, fixture.First);
        var healthy = await File.ReadAllBytesAsync(path, CancellationToken.None);

        await File.WriteAllTextAsync(path, "{broken", CancellationToken.None);
        await model.CheckProjectCatalogAsync(CancellationToken.None);
        Assert.That(model.Notice, Does.Contain("项目目录自动同步失败"), "前置：损坏已置位失败提示。");

        // 删除后 stat 回到已确认的缺失基线：本轮 Unchanged 只复位失败状态，不动共享的 Notice 文本。
        File.Delete(path);
        await model.CheckProjectCatalogAsync(CancellationToken.None);
        Assert.That(model.Notice, Does.Contain("项目目录自动同步失败"),
                    "恢复为 Unchanged 不得清空共享的 Notice 文本。");

        // Notice 是共享单字符串：模拟其他流程写入后，再次失败必须重新提示，间接证明失败标志已复位。
        model.Notice = string.Empty;
        await File.WriteAllTextAsync(path, "{broken", CancellationToken.None);
        await model.CheckProjectCatalogAsync(CancellationToken.None);
        Assert.That(model.Notice, Does.Contain("项目目录自动同步失败"), "复位后再次失败必须重新提示。");

        await File.WriteAllBytesAsync(path, healthy, CancellationToken.None);
        await model.CheckProjectCatalogAsync(CancellationToken.None);
        Assert.That(model.Projects.Count(item => item.Project is not null), Is.EqualTo(1),
                    "恢复后的真实变化仍必须正常应用。");
        Assert.That(model.Projects.Single(item => item.Project is not null).Project!.Id, Is.EqualTo(created.Id),
                    "应用内容必须与目录册一致。");
    }

    /// <summary>
    ///     <para>目录册缺失按空目录册应用并出现“项目信息缺失”组，外部重建后恢复项目组。</para>
    ///     <para>
    ///         A missing catalog applies the empty catalog and shows the missing-info group; recreating it
    ///         externally restores the project group.
    ///     </para>
    /// </summary>
    [Test]
    public async Task MissingCatalogAppliesEmptyStateThenRecreateRestoresTheGroup()
    {
        var             fixture   = await CatalogFixture.CreateAsync();
        var             sessionId = await CommitLegacySessionAsync(fixture.Store, fixture.First);
        await using var model     = fixture.Window();
        await model.StartAsync();
        var row = model.Sessions.Single(item => item.Id == sessionId);
        Assert.That(model.Projects.Count(item => item.CanEdit), Is.EqualTo(1), "前置：整理已建立项目组。");

        File.Delete(ProjectStore.FilePath(fixture.Store));
        await model.CheckProjectCatalogAsync(CancellationToken.None);

        Assert.That(model.Projects.Any(item => item.Project is not null), Is.False,
                    "目录册缺失必须按 ProjectStore 契约应用空目录册。");
        var missing = model.Projects.Single(item => item.Name == "项目信息缺失");
        Assert.That(missing.Sessions, Does.Contain(row), "仍持有归属的会话必须进入“项目信息缺失”组。");

        await ProjectStore.GetOrCreateAsync(ProjectStore.FilePath(fixture.Store), fixture.First, null,
                                            CancellationToken.None);
        await model.CheckProjectCatalogAsync(CancellationToken.None);

        Assert.That(model.Projects.Any(item => item.Project is not null &&
                                               ProjectRoots.AreEqual(item.Root, fixture.First)),
                    Is.True, "重建目录册后项目组必须恢复。");
    }

    /// <summary>
    ///     <para>Windows 共享冲突期间检查不抛异常并保留快照，释放句柄后一次检查应用新内容。</para>
    ///     <para>
    ///         Under a Windows sharing conflict the check throws nothing and keeps the snapshot; after the handle is
    ///         released one check applies the new content.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SharedConflictKeepsSnapshotThenAppliesAfterRelease()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("目录册共享冲突仅在 Windows 验证。");
            return;
        }

        var             fixture = await CatalogFixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.InitializeAsync();
        var created = await CreateExternalAsync(fixture.Store, fixture.First);
        await model.CheckProjectCatalogAsync(CancellationToken.None);
        var group = model.Projects.Single(item => item.CanEdit);

        var renamed = await RenameExternalAsync(fixture.Store, created.Id, "外部等待期间改名");
        await using (var blocked = new FileStream(ProjectStore.FilePath(fixture.Store), FileMode.Open,
                                                  FileAccess.Read, FileShare.None))
        {
            // 不共享句柄使读取在 2 秒共享冲突重试窗口内失败：检查必须按 Failed 收场而不抛异常。
            await model.CheckProjectCatalogAsync(CancellationToken.None);
            Assert.That(model.Notice, Does.Contain("项目目录自动同步失败"), "共享冲突必须以失败提示呈现。");
            Assert.That(group.Name, Is.EqualTo(created.Name), "冲突期间必须保留最后成功快照。");
            Assert.That(model.Projects, Has.Count.EqualTo(1), "冲突期间不得破坏分组结构。");
        }

        await model.CheckProjectCatalogAsync(CancellationToken.None);
        Assert.That(group.Name, Is.EqualTo(renamed.Name), "句柄释放后必须应用新内容。");
    }

    /// <summary>
    ///     <para>加载失败路径保留最后成功快照：持锁期间重跑 InitializeAsync 不得清空侧栏。</para>
    ///     <para>
    ///         The failed-load path keeps the last successful snapshot: rerunning InitializeAsync under a held lock
    ///         must not clear the sidebar.
    ///     </para>
    /// </summary>
    [Test]
    public async Task InitializeAsyncFailureKeepsLastSnapshotInsteadOfClearing()
    {
        var             fixture = await CatalogFixture.CreateAsync();
        await using var model   = fixture.Window();
        var             created = await CreateExternalAsync(fixture.Store, fixture.First);
        await model.InitializeAsync();
        var group = model.Projects.Single(item => item.CanEdit);

        await using (var blocked = new FileStream(ProjectStore.FilePath(fixture.Store), FileMode.Open,
                                                  FileAccess.Read, FileShare.None))
        {
            await model.InitializeAsync();
            Assert.That(model.Notice, Does.Contain("项目信息无法读取"), "加载失败必须给出可读提示。");
            Assert.That(model.Projects.Single(item => item.Key == group.Key), Is.SameAs(group),
                        "加载失败必须保留最后成功快照，不得清空侧栏。");
            Assert.That(group.Name, Is.EqualTo(created.Name), "快照内容必须完整保留。");
        }

        await model.InitializeAsync();
        Assert.That(model.Projects.Any(item => item.Project is not null), Is.True, "锁释放后重新加载必须恢复项目组。");
    }

    /// <summary>
    ///     <para>
    ///         初始化读取失败给出的具体原因不被同步检查的泛化提示覆盖；恢复后再次故障才换回同步失败提示。
    ///     </para>
    ///     <para>
    ///         The specific reason reported by a failed initialization load survives the sync check's generic
    ///         notice; only a fresh failure after recovery switches back to the sync-failure notice.
    ///     </para>
    /// </summary>
    [Test]
    public async Task PrepareFailureNoticeSurvivesSyncCheckUntilRecovery()
    {
        var             fixture = await CatalogFixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.InitializeAsync();
        var created = await CreateExternalAsync(fixture.Store, fixture.First);

        await using (var blocked = new FileStream(ProjectStore.FilePath(fixture.Store), FileMode.Open,
                                                  FileAccess.Read, FileShare.None))
        {
            await model.InitializeAsync();
            Assert.That(model.Notice, Does.Contain("项目信息无法读取"), "前置：初始化已给出具体读取失败原因。");

            // 同一持续故障已由初始化提示：同步检查失败只累计退避，不得把具体原因改写成泛化提示。
            await model.CheckProjectCatalogAsync(CancellationToken.None);
            Assert.That(model.Notice, Does.Contain("项目信息无法读取"),
                        "同步检查失败不得覆盖初始化给出的具体原因。");
            Assert.That(model.Notice, Does.Not.Contain("自动同步失败"), "同一持续故障不得叠加泛化提示。");
        }

        await model.CheckProjectCatalogAsync(CancellationToken.None);
        Assert.That(model.Projects.Single(item => item.CanEdit).Project!.Id, Is.EqualTo(created.Id),
                    "前置：锁释放后检查恢复正常并应用内容。");

        await File.WriteAllTextAsync(ProjectStore.FilePath(fixture.Store), "{broken", CancellationToken.None);
        await model.CheckProjectCatalogAsync(CancellationToken.None);
        Assert.That(model.Notice, Does.Contain("项目目录自动同步失败"),
                    "恢复后的新故障必须重新以同步失败提示呈现。");
    }

    /// <summary>
    ///     <para>两个视图模型共用同一 store：一侧建立的项目被另一侧检查发现，且互不影响、无重复组。</para>
    ///     <para>
    ///         Two view models share one store: the project built by one is discovered by the other's check, with
    ///         no interference and no duplicate groups.
    ///     </para>
    /// </summary>
    [Test]
    public async Task TwoViewModelsOnOneStoreConvergeIndependently()
    {
        var             fixture = await CatalogFixture.CreateAsync();
        await using var modelA  = fixture.Window();
        await using var modelB  = fixture.Window();
        await modelA.InitializeAsync();
        await modelB.InitializeAsync();

        await modelA.OpenProjectAsync(fixture.First);
        await modelB.CheckProjectCatalogAsync(CancellationToken.None);

        var aId = modelA.Projects.Single(item => item.CanEdit).Project!.Id;
        Assert.That(modelB.Projects.Single(item => item.CanEdit).Project!.Id, Is.EqualTo(aId),
                    "B 必须经检查发现 A 建立的项目。");
        Assert.That(modelA.Projects, Has.Count.EqualTo(1), "A 不受 B 的检查影响。");

        await ProjectStore.GetOrCreateAsync(ProjectStore.FilePath(fixture.Store), fixture.Second, null,
                                            CancellationToken.None);
        await modelA.CheckProjectCatalogAsync(CancellationToken.None);
        await modelB.CheckProjectCatalogAsync(CancellationToken.None);

        Assert.That(modelA.Projects, Has.Count.EqualTo(2), "A 必须收敛到两个项目组。");
        Assert.That(modelB.Projects, Has.Count.EqualTo(2), "B 必须收敛到两个项目组。");
        Assert.That(modelB.Projects.Select(item => item.Project!.Id),
                    Is.EquivalentTo(modelA.Projects.Select(item => item.Project!.Id)), "两侧项目集合必须一致。");
    }

    /// <summary>
    ///     <para>同步循环幂等启动：重复调用不替换完成缝，关闭后循环以单任务完成退出。</para>
    ///     <para>
    ///         The sync loop starts idempotently: repeated calls never replace the completion seam, and the loop
    ///         exits once on shutdown.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SyncLoopStartsAreIdempotentAndExitOnceOnShutdown()
    {
        var             fixture = await CatalogFixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.InitializeAsync();
        var started = model.ProjectSyncLoopCompletion;
        Assert.That(started, Is.Not.Null, "InitializeAsync 必须建立同步循环。");

        model.StartProjectCatalogSyncLoop();
        model.StartProjectCatalogSyncLoop();
        Assert.That(model.ProjectSyncLoopCompletion, Is.SameAs(started), "重复启动不得替换完成缝（单常驻循环）。");

        await model.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(started, Is.Not.Null.And.Property(nameof(Task.IsCompleted)).True,
                    "关闭必须等待同步循环退出。");
        Assert.That(model.ProjectSyncLoopCompletion, Is.Null, "循环退出后完成缝必须清空。");
    }

    /// <summary>
    ///     <para>DisposeAsync 等待同步循环退出：完成缝在关闭返回前已完成，无泄漏。</para>
    ///     <para>
    ///         DisposeAsync awaits the sync loop's exit: the completion seam has finished before shutdown returns,
    ///         leaving no leak.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ShutdownAwaitsTheSyncLoopExitAndClearsTheSeam()
    {
        var             fixture = await CatalogFixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.InitializeAsync();
        var completion = model.ProjectSyncLoopCompletion;
        Assert.That(completion, Is.Not.Null, "前置：同步循环已启动。");

        await model.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(completion!.Status, Is.EqualTo(TaskStatus.RanToCompletion), "循环必须随关闭安静退出。");
        Assert.That(model.ProjectSyncLoopCompletion, Is.Null, "退出后不得残留循环句柄。");
    }

    /// <summary>
    ///     <para>store 切换后旧 store 的迟到检查结果被丢弃：展示只收敛到新 store 内容。</para>
    ///     <para>
    ///         A late check result from the previous store is discarded after a store switch: the display converges
    ///         on the new store's content only.
    ///     </para>
    /// </summary>
    [Test]
    public async Task StoreSwitchDiscardsLateResultsFromThePreviousStore()
    {
        var fixture     = await CatalogFixture.CreateAsync();
        var secondStore = Path.Combine(Path.GetDirectoryName(fixture.Store)!, "sessions-2");
        Directory.CreateDirectory(secondStore);
        await using var model = fixture.Window();
        await model.InitializeAsync();
        var created = await CreateExternalAsync(fixture.Store, fixture.First);
        await model.CheckProjectCatalogAsync(CancellationToken.None);

        // 旧 store 留下未应用变化，并让一次检查在切换前后处于在途：结果不得落到新 store 展示上。
        await RenameExternalAsync(fixture.Store, created.Id, "迟到结果项目");
        var late = model.CheckProjectCatalogAsync(CancellationToken.None);

        var user = await UserConfigStore.LoadAsync(fixture.Config, CancellationToken.None);
        await UserConfigStore.SaveAsync(fixture.Config, user with
        {
            Settings = (user.Settings ?? new UserConfigSettings()) with { SessionStoreDirectory = secondStore }
        }, CancellationToken.None);
        await CreateExternalAsync(secondStore, fixture.Second);
        await model.InitializeAsync();
        await late;

        Assert.That(model.Projects.Where(item => item.Project is not null).Select(item => item.Project!.Name),
                    Is.EqualTo(new[] { ProjectRoots.DefaultDisplayName(fixture.Second) }),
                    "切换后展示必须只含新 store 的项目组。");
        Assert.That(model.Projects.Any(item => item.Name == "迟到结果项目"), Is.False,
                    "旧 store 的迟到结果不得应用到新 store 展示。");
    }

    /// <summary>
    ///     <para>
    ///         store 切换后读取新 store 目录册失败：不保留上一 store 的快照，侧栏清空并提示；故障解除后
    ///         新 store 的项目正常加载。
    ///     </para>
    ///     <para>
    ///         A failed read of the new store's catalog after a switch keeps no previous-store snapshot: the
    ///         sidebar clears with a notice, and the new store's projects load normally once the fault clears.
    ///     </para>
    /// </summary>
    [Test]
    public async Task StoreSwitchFailureClearsSnapshotInsteadOfKeepingThePreviousStore()
    {
        var fixture     = await CatalogFixture.CreateAsync();
        var secondStore = Path.Combine(Path.GetDirectoryName(fixture.Store)!, "sessions-2");
        Directory.CreateDirectory(secondStore);
        await using var model = fixture.Window();
        await model.InitializeAsync();
        await CreateExternalAsync(fixture.Store, fixture.First);
        await model.CheckProjectCatalogAsync(CancellationToken.None);
        Assert.That(model.Projects.Count(item => item.Project is not null), Is.EqualTo(1),
                    "前置：store-A 项目组已展示。");

        // 新 store 目录册损坏：切换后的读取失败属于跨 store 场景，不得保留 store-A 快照。
        await File.WriteAllTextAsync(ProjectStore.FilePath(secondStore), "{broken", CancellationToken.None);
        var user = await UserConfigStore.LoadAsync(fixture.Config, CancellationToken.None);
        await UserConfigStore.SaveAsync(fixture.Config, user with
        {
            Settings = (user.Settings ?? new UserConfigSettings()) with { SessionStoreDirectory = secondStore }
        }, CancellationToken.None);
        await model.InitializeAsync();

        Assert.That(model.Projects.Any(item => item.Project is not null), Is.False,
                    "切换后读取失败必须清空上一 store 的快照。");
        Assert.That(model.Notice, Does.Contain("项目信息无法读取"), "切换后读取失败必须给出可读提示。");

        File.Delete(ProjectStore.FilePath(secondStore));
        await CreateExternalAsync(secondStore, fixture.Second);
        await model.InitializeAsync();
        Assert.That(model.Projects.Where(item => item.Project is not null).Select(item => item.Project!.Name),
                    Is.EqualTo(new[] { ProjectRoots.DefaultDisplayName(fixture.Second) }),
                    "故障解除后必须正常加载 store-B 的项目组。");
    }

    /// <summary>
    ///     <para>连续三次外部改名后一次检查收敛到最终状态：无重复组且实例复用。</para>
    ///     <para>
    ///         After three consecutive external renames one check converges on the final state with no duplicate
    ///         groups and a reused instance.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ConsecutiveRenamesConvergeInOneCheckWithReusedInstance()
    {
        var             fixture = await CatalogFixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.InitializeAsync();
        var created = await CreateExternalAsync(fixture.Store, fixture.First);
        await model.CheckProjectCatalogAsync(CancellationToken.None);
        var group = model.Projects.Single(item => item.CanEdit);

        for (var index = 0; index < 3; index++)
        {
            await RenameExternalAsync(fixture.Store, created.Id, "rename-" + index);
            await Task.Delay(20);
        }

        await model.CheckProjectCatalogAsync(CancellationToken.None);

        Assert.That(model.Projects, Has.Count.EqualTo(1), "连续变化不得产生重复组。");
        Assert.That(model.Projects.Single(), Is.SameAs(group), "多次变化仍必须复用原分组实例。");
        Assert.That(group.Name, Is.EqualTo("rename-2"), "单次检查必须收敛到最终名称。");
    }

    /// <summary>
    ///     <para>新建后立即改名（检查前完成两步），一次检查直接收敛到最终名称。</para>
    ///     <para>
    ///         Creating then immediately renaming (both before any check) converges on the final name in one check.
    ///     </para>
    /// </summary>
    [Test]
    public async Task CreateThenImmediateRenameConvergesInOneCheck()
    {
        var             fixture = await CatalogFixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.InitializeAsync();

        var created = await CreateExternalAsync(fixture.Store, fixture.First);
        await RenameExternalAsync(fixture.Store, created.Id, "立即改名项目");
        await model.CheckProjectCatalogAsync(CancellationToken.None);

        Assert.That(model.Projects, Has.Count.EqualTo(1), "两步外部操作只收敛出一个项目组。");
        Assert.That(model.Projects.Single(item => item.CanEdit).Name, Is.EqualTo("立即改名项目"),
                    "检查必须跳过中间状态直接收敛到最终名称。");
    }

    /// <summary>
    ///     <para>新建后立即完成删除（检查前完成两步），一次检查后不出现任何项目组。</para>
    ///     <para>
    ///         Creating then immediately completing a deletion (both before any check) leaves no project group
    ///         after one check.
    ///     </para>
    /// </summary>
    [Test]
    public async Task CreateThenImmediateDeletionConvergesToNoGroupsInOneCheck()
    {
        var             fixture = await CatalogFixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.InitializeAsync();

        var path    = ProjectStore.FilePath(fixture.Store);
        var created = await CreateExternalAsync(fixture.Store, fixture.First);
        var deleting = await ProjectStore.BeginDeletionAsync(path, created.Id, created.Revision, null,
                                                             CancellationToken.None);
        await ProjectStore.CompleteDeletionAsync(path, created.Id, deleting.Revision, CancellationToken.None);
        await model.CheckProjectCatalogAsync(CancellationToken.None);

        Assert.That(model.Projects.Any(item => item.Project is not null), Is.False,
                    "建后即删的中间状态不得在检查后残留项目组。");
    }

    /// <summary>
    ///     <para>相同字节长度、不同名称与修订号的重写经修改时间线索被一次检查发现。</para>
    ///     <para>
    ///         A same-byte-length rewrite with a different name and revision is detected through the write-time cue
    ///         by one check.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SameLengthRewriteIsDetectedThroughWriteTime()
    {
        var             fixture = await CatalogFixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.InitializeAsync();
        var created = await CreateExternalAsync(fixture.Store, fixture.First);
        await model.CheckProjectCatalogAsync(CancellationToken.None);

        var path     = ProjectStore.FilePath(fixture.Store);
        var original = await File.ReadAllTextAsync(path, CancellationToken.None);
        var newName  = "x" + created.Name[1..];
        var rewritten = original.Replace($"\"name\": \"{created.Name}\"", $"\"name\": \"{newName}\"")
                                .Replace("\"revision\": 1", "\"revision\": 2");
        Assert.That(rewritten, Is.Not.EqualTo(original), "前提：重写内容确实变化。");
        Assert.That(Encoding.UTF8.GetByteCount(rewritten), Is.EqualTo(Encoding.UTF8.GetByteCount(original)),
                    "前提：重写后的字节长度与原文件相同，差异只能来自修改时间。");
        await Task.Delay(20);
        await File.WriteAllTextAsync(path, rewritten, CancellationToken.None);

        await model.CheckProjectCatalogAsync(CancellationToken.None);

        Assert.That(model.Projects.Single(item => item.CanEdit).Name, Is.EqualTo(newName),
                    "同长度重写必须被检查发现并应用。");
    }

    /// <summary>
    ///     <para>手动刷新与同步检查交错执行：最终状态一致、无异常且无重复组。</para>
    ///     <para>
    ///         A manual refresh interleaved with a sync check converges on the same final state without exceptions
    ///         or duplicate groups.
    ///     </para>
    /// </summary>
    [Test]
    public async Task RefreshAndSyncCheckInterleaveToTheSameConvergedState()
    {
        var             fixture   = await CatalogFixture.CreateAsync();
        var             sessionId = await CommitLegacySessionAsync(fixture.Store, fixture.First);
        await using var model     = fixture.Window();
        await model.StartAsync();
        var group = model.Projects.Single(item => item.CanEdit);

        await RenameExternalAsync(fixture.Store, group.Project!.Id, "交错刷新改名");
        var refresh = model.RefreshAsync();
        await model.CheckProjectCatalogAsync(CancellationToken.None);
        await refresh;

        var groups = model.Projects.Where(item => item.Project is not null).ToArray();
        Assert.That(groups, Has.Length.EqualTo(1), "交错后不得出现重复项目组。");
        Assert.That(groups[0].Name, Is.EqualTo("交错刷新改名"), "最终状态必须收敛到目录册内容。");
        Assert.That(groups[0].Sessions.Select(item => item.Id), Does.Contain(sessionId), "会话成员必须保留。");
    }

    /// <summary>
    ///     <para>项目删除在途期间的检查让位且不破坏状态：无异常，删除完成后正常收敛。</para>
    ///     <para>
    ///         A check during a running project deletion yields without breaking state: no exception, and it
    ///         converges normally after deletion completes.
    ///     </para>
    /// </summary>
    [Test]
    public async Task CheckDuringRunningDeletionYieldsAndConverges()
    {
        var             fixture = await CatalogFixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.InitializeAsync();
        var created = await CreateExternalAsync(fixture.Store, fixture.First);
        await model.CheckProjectCatalogAsync(CancellationToken.None);
        var group = model.Projects.Single(item => item.CanEdit);
        model.Settings.General.ShowProjectDeletionPrompt = false;

        var marked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        group.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ProjectViewModel.IsDeleting)) marked.TrySetResult();
        };

        model.OpenProjectDeletionCommand.Execute(group);
        await marked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(model.IsProjectDeletionRunning, Is.True, "前置：删除仍在进行。");
        await model.CheckProjectCatalogAsync(CancellationToken.None);

        await WaitUntilAsync(() => !model.IsProjectDeletionRunning, "删除未在期限内完成。");
        await WaitUntilAsync(() => model.Projects.All(item => item.Project is null), "删除完成后项目组必须消失。");
        Assert.That(model.Projects.Select(item => item.Key), Is.Unique, "让位期间不得产生重复分组。");
    }

    /// <summary>
    ///     <para>经 ProjectStore 公开 API 以外部进程身份创建或复用项目。</para>
    ///     <para>Creates or reuses a project as an external process via the public ProjectStore API.</para>
    /// </summary>
    private static async Task<Project> CreateExternalAsync(string store, string root) =>
        await ProjectStore.GetOrCreateAsync(ProjectStore.FilePath(store), root, null, CancellationToken.None);

    /// <summary>
    ///     <para>以外部写者身份重读目录册并按当前修订号改名。</para>
    ///     <para>Renames as an external writer, reloading the catalog for the current revision first.</para>
    /// </summary>
    private static async Task<Project> RenameExternalAsync(string store, string projectId, string newName)
    {
        var path    = ProjectStore.FilePath(store);
        var catalog = await ProjectStore.LoadAsync(path, CancellationToken.None);
        var project = catalog.Projects.Single(item => item.Id == projectId);
        return await ProjectStore.RenameAsync(path, projectId, newName, project.Revision, null, CancellationToken.None);
    }

    /// <summary>
    ///     <para>向 store 提交一份仅有会话身份批次的合法旧日志，等待归属整理绑定。</para>
    ///     <para>
    ///         Commits one valid legacy log holding only the session-identity batch, for the organize round to
    ///         bind.
    ///     </para>
    /// </summary>
    private static async Task<string> CommitLegacySessionAsync(string store, string root)
    {
        var       session = new ChatSession(Guid.NewGuid().ToString("N"), root, Identity);
        using var writer  = new SessionStoreWriter(store, session.Id);
        await writer.CommitAsync(session, new ConversationContext(new ContextOptions()), CancellationToken.None);
        return session.Id;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        for (var attempts = 0; attempts < 1000 && !condition(); attempts++) await Task.Delay(10);
        Assert.That(condition(), Is.True, message);
    }

    private sealed class CatalogFixture
    {
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

        public static async Task<CatalogFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "tinyharness-catalog-sync-" + Guid.NewGuid().ToString("N"));
            var fixture = new CatalogFixture
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

using System.ComponentModel;
using NUnit.Framework;
using System.Runtime.CompilerServices;
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
///     <para>
///         Sidebar 刷新差量优化的不变量回归：刷新只做差量更新的前提下，行实例、选中、草稿、
///         展开与分页、运行态、活动时间单调性、归档权威与选中高亮语义必须保持不变。
///     </para>
///     <para>
///         Regression tests for the sidebar differential-refresh invariants: while refreshes become incremental,
///         row instances, selection, drafts, expansion and paging, running state, activity monotonicity, archive
///         authority, and selection highlight semantics must stay unchanged.
///     </para>
/// </summary>
[TestFixture]
public sealed class DesktopSidebarRefreshInvariantsTests
{
    /// <summary>
    ///     <para>刷新不得替换任何会话行实例，选中、草稿、折叠与 ShowMore 分页跨刷新保持。</para>
    ///     <para>
    ///         A refresh never replaces session-row instances; selection, drafts, collapse state and ShowMore
    ///         paging survive it.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Refresh_PreservesRowInstancesSelectionDraftsAndExpansion()
    {
        var fixture = await Fixture.CreateAsync();
        // 项目 A 6 个成员支撑 ShowMore 扩层，B 3 个、C 1 个，另有 1 个无项目行与 1 个待初始化旧日志。
        for (var index = 0; index < 6; index++)
            await WriteHostedSessionAsync(fixture.Store, fixture.First, fixture.FirstProjectId, index,
                                          CancellationToken.None);
        for (var index = 0; index < 3; index++)
            await WriteHostedSessionAsync(fixture.Store, fixture.Second, fixture.SecondProjectId, index,
                                          CancellationToken.None);
        await WriteHostedSessionAsync(fixture.Store, fixture.Third, fixture.ThirdProjectId, 0, CancellationToken.None);
        await WriteHostedSessionAsync(fixture.Store, fixture.Scratch, null, 0, CancellationToken.None);
        await CommitLegacySessionAsync(fixture.Store, fixture.First, CancellationToken.None);
        await new SessionIndexReconciler(new SessionIndexStore(fixture.Store)).ReconcileAsync(CancellationToken.None);

        await using var model = fixture.Window();
        await model.InitializeAsync();

        Assert.That(model.Sessions, Has.Count.EqualTo(12), "前置：10 个绑定行、1 个无项目行与 1 个待初始化行全部加载。");
        var groupA = model.Projects.Single(item => item.Project is { } project && project.Id == fixture.FirstProjectId);
        var groupB =
            model.Projects.Single(item => item.Project is { } project && project.Id == fixture.SecondProjectId);
        Assert.That(groupA.Sessions, Has.Count.EqualTo(6), "前置：项目 A 有 6 个成员以支撑分页扩层。");
        Assert.That(groupB.Sessions, Has.Count.EqualTo(3));

        model.SelectSessionCommand.Execute(groupA.Sessions[0]);
        var selected = groupA.Sessions[0];
        var draftRow = groupB.Sessions[0];
        draftRow.Draft = "draft kept across refresh";
        model.ToggleProjectCommand.Execute(groupB);
        Assert.That(groupB.IsExpanded, Is.False, "前置：项目 B 已折叠。");
        model.ShowMoreSessionsCommand.Execute(groupA);
        Assert.That(groupA.VisibleSessionCount, Is.EqualTo(10), "前置：项目 A 已扩一层分页。");

        var sessionsBefore = model.Sessions.ToArray();
        var sidebarBefore  = model.SidebarRows.ToArray();
        Assert.That(sidebarBefore.Distinct().Count(), Is.EqualTo(sidebarBefore.Length), "前置：侧栏行无重复。");

        await model.InitializeAsync();

        Assert.That(model.Sessions.ToArray(), Is.EqualTo(sessionsBefore), "刷新不得替换任何会话行实例。");
        Assert.That(model.SelectedSession, Is.SameAs(selected), "选中行引用必须跨刷新保持。");
        Assert.That(selected.IsSelected, Is.True);
        Assert.That(draftRow.Draft, Is.EqualTo("draft kept across refresh"), "正式行草稿必须跨刷新保持。");
        Assert.That(model.Projects.Single(item => item.Key == groupA.Key), Is.SameAs(groupA), "分组实例必须跨刷新复用。");
        Assert.That(model.Projects.Single(item => item.Key == groupB.Key), Is.SameAs(groupB));
        Assert.That(groupB.IsExpanded, Is.False, "折叠状态必须跨刷新保持。");
        Assert.That(groupA.VisibleSessionCount, Is.EqualTo(10), "ShowMore 扩层结果必须跨刷新保持。");

        var sidebarAfter = model.SidebarRows.ToArray();
        Assert.That(sidebarAfter.Length, Is.EqualTo(sidebarBefore.Length), "刷新后侧栏行数必须一致。");
        for (var index = 0; index < sidebarAfter.Length; index++)
            Assert.That(sidebarAfter[index], Is.SameAs(sidebarBefore[index]), $"侧栏行 {index} 不得被替换实例。");
        Assert.That(sidebarAfter.Distinct().Count(), Is.EqualTo(sidebarAfter.Length), "刷新后侧栏行不得出现重复。");
    }

    /// <summary>
    ///     <para>刷新只推进被更新摘要的行；未变行的活动时间与归属属性保持原值且不发出属性通知。</para>
    ///     <para>
    ///         A refresh only moves the row whose summary advanced; unchanged rows keep their activity and ownership
    ///         values and raise no property notifications.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Refresh_PreservesMetadataGuards_ActivityOnlyMovesForwardAndUnchangedRowsStayQuiet()
    {
        var fixture = await Fixture.CreateAsync();
        var firstId = await WriteHostedSessionAsync(fixture.Store, fixture.First, fixture.FirstProjectId, 0,
                                                    CancellationToken.None);
        var secondId = await WriteHostedSessionAsync(fixture.Store, fixture.Second, fixture.SecondProjectId, 1,
                                                     CancellationToken.None);
        await new SessionIndexReconciler(new SessionIndexStore(fixture.Store)).ReconcileAsync(CancellationToken.None);

        await using var model = fixture.Window();
        await model.InitializeAsync();

        var advancedRow    = model.Sessions.Single(session => session.Id == firstId);
        var quietRow       = model.Sessions.Single(session => session.Id == secondId);
        var quietActivity  = quietRow.LastActivityUtc;
        var advancedBefore = advancedRow.LastActivityUtc;
        using var guard = new PropertyGuardCounter(quietRow,
        [
            nameof(SessionViewModel.ProjectId),
            nameof(SessionViewModel.ProjectAssignmentState),
            nameof(SessionViewModel.LastActivityUtc)
        ]);

        // 只推进另一行的摘要活动时间；来源标记保持不变，权威日志不动。
        var index  = new SessionIndexStore(fixture.Store);
        var source = await index.GetRowAsync(firstId, CancellationToken.None);
        Assert.That(source!.Summary, Is.Not.Null, "前置：被推进行必须携带健康摘要。");
        await index.WriteSummaryAsync(source.Summary! with
                                      {
                                          LastActivityUtc = source.Summary.LastActivityUtc.AddHours(1)
                                      },
                                      source.SourceLength, source.SourceModifiedUtc, CancellationToken.None);

        await model.InitializeAsync();

        Assert.That(advancedRow.LastActivityUtc, Is.GreaterThan(advancedBefore), "被推进行的活动时间必须前进。");
        Assert.That(quietRow.LastActivityUtc, Is.EqualTo(quietActivity), "未变行的活动时间必须完全不变。");
        Assert.That(guard.Counts, Is.Empty, "未变行不得发出 ProjectId/ProjectAssignmentState/LastActivityUtc 通知。");
        Assert.That(quietRow.ProjectId, Is.EqualTo(fixture.SecondProjectId));
        Assert.That(quietRow.ProjectAssignmentState, Is.EqualTo(SessionProjectAssignmentState.Project));
    }

    /// <summary>
    ///     <para>本地已推进的活动时间是行的下界；更早的目录快照经刷新应用后不得使其回退。</para>
    ///     <para>
    ///         A locally advanced activity time bounds the row from below; applying an older catalog snapshot in a
    ///         refresh never rewinds it.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Refresh_ActivitySnapshotCannotRewindExistingRow()
    {
        var fixture = await Fixture.CreateAsync();
        var id = await WriteHostedSessionAsync(fixture.Store, fixture.First, fixture.FirstProjectId, 0,
                                               CancellationToken.None);
        await new SessionIndexReconciler(new SessionIndexStore(fixture.Store)).ReconcileAsync(CancellationToken.None);

        await using var model = fixture.Window();
        await model.InitializeAsync();
        var row      = model.Sessions.Single(session => session.Id == id);
        var baseline = row.LastActivityUtc;

        // 本地把行活动时间推到快照之后，再写入更早的摘要快照。
        row.ApplyStoredMetadata(row.ProjectId, row.ProjectAssignmentState, baseline.AddHours(2));
        Assert.That(row.LastActivityUtc, Is.EqualTo(baseline.AddHours(2)), "前置：本地活动时间已推进。");
        var index  = new SessionIndexStore(fixture.Store);
        var source = await index.GetRowAsync(id, CancellationToken.None);
        Assert.That(source!.Summary, Is.Not.Null, "前置：目标行必须携带健康摘要。");
        await index.WriteSummaryAsync(source.Summary! with
                                      {
                                          LastActivityUtc = baseline.AddHours(-1)
                                      },
                                      source.SourceLength, source.SourceModifiedUtc, CancellationToken.None);

        await model.InitializeAsync();

        Assert.That(model.Sessions.Single(session => session.Id == id), Is.SameAs(row), "刷新不得替换行实例。");
        Assert.That(row.LastActivityUtc, Is.EqualTo(baseline.AddHours(2)), "更早的快照不得回退已有行的活动时间。");
    }

    /// <summary>
    ///     <para>运行中的会话行跨刷新保持实例与运行状态，会话集合与侧栏投影都不出现重复。</para>
    ///     <para>
    ///         A running session row keeps its instance and running state across a refresh; neither the session
    ///         collection nor the sidebar projection duplicates it.
    ///     </para>
    /// </summary>
    [Test]
    public async Task RunningSession_SurvivesRefreshWithoutReplacementOrDuplicate()
    {
        var fixture = await Fixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DesktopSessionFactory((_, _) => new GatedModel(entered, finish), new Credentials(),
                                                fixture.Config);
        await using var model = new MainWindowViewModel(factory, fixture.Config, action =>
        {
            action();
            return ValueTask.CompletedTask;
        });
        await model.InitializeAsync();
        await model.OpenProjectAsync(fixture.First);
        var page = model.SelectedSession!;
        page.Draft = "still running";
        await page.SendAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(page.Id, Is.Not.Null, "前置：运行页已取得会话 ID。");
        Assert.That(page.IsRunning, Is.True, "前置：会话已进入运行中。");
        Assert.That(model.Sessions, Does.Contain(page), "前置：运行页已登记为正式行。");

        await model.InitializeAsync();

        Assert.That(model.Sessions.Count(session => session.Id == page.Id), Is.EqualTo(1),
                    "运行中会话不得出现重复行。");
        Assert.That(model.Sessions.Single(session => session.Id == page.Id), Is.SameAs(page),
                    "刷新不得替换运行中的行实例。");
        Assert.That(page.IsRunning, Is.True, "刷新不得打断运行状态。");
        Assert.That(model.SidebarRows.Count(row => ReferenceEquals(row, page)), Is.EqualTo(1),
                    "侧栏投影中运行行必须恰好出现一次。");
        Assert.That(model.SidebarRows.GroupBy(row => row).Any(group => group.Count() > 1), Is.False,
                    "侧栏投影不得出现重复行。");

        // 释放模型门并等待运行收尾，避免后台流延续到测试结束。
        finish.SetResult();
        await page.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(page.IsRunning, Is.False, "收尾：门放行后运行应正常结束。");
    }

    /// <summary>
    ///     <para>归档权威来自状态文件的内存快照：归档行离开默认视图并进入归档视图，索引列与摘要不得推翻它。</para>
    ///     <para>
    ///         Archive authority lives in the in-memory snapshot of the state file: archived rows leave the default
    ///         view for the archive view, and neither the index column nor summaries can overturn that.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Refresh_KeepsArchiveAuthoritativeFromSnapshot()
    {
        var fixture = await Fixture.CreateAsync();
        var firstId = await WriteHostedSessionAsync(fixture.Store, fixture.First, fixture.FirstProjectId, 0,
                                                    CancellationToken.None);
        await WriteHostedSessionAsync(fixture.Store, fixture.Second, fixture.SecondProjectId, 1,
                                      CancellationToken.None);
        await new SessionIndexReconciler(new SessionIndexStore(fixture.Store)).ReconcileAsync(CancellationToken.None);

        await using var model = fixture.Window();
        await model.InitializeAsync();

        var archivedRow = model.Sessions.Single(session => session.Id == firstId);
        var group = model.Projects.Single(item => item.Project is { } project && project.Id == fixture.FirstProjectId);
        Assert.That(group.Sessions, Does.Contain(archivedRow), "前置：归档前会话属于项目分组。");

        // 直接写归档状态文件（权威来源），经 InitializeAsync 的归档状态装载进入内存快照。
        await SessionStoreArchiveStore.SetArchivedAsync(fixture.Store, firstId, true, CancellationToken.None);
        await model.InitializeAsync();

        Assert.That(archivedRow.IsArchived, Is.True, "归档状态文件的内存快照必须让行进入归档态。");
        Assert.That(group.Sessions, Does.Not.Contain(archivedRow), "归档行必须离开默认项目分组。");
        Assert.That(model.SidebarRows, Does.Not.Contain(archivedRow), "默认视图不得展示归档行。");

        model.ToggleArchiveViewCommand.Execute(null);
        Assert.That(model.IsArchiveView, Is.True);
        Assert.That(model.SidebarRows.Count(row => ReferenceEquals(row, archivedRow)), Is.EqualTo(1),
                    "归档视图必须恰好展示归档行一次。");
        model.ToggleArchiveViewCommand.Execute(null);

        // 把索引归档投影置 0 并重写摘要：SQLite 投影不是归档权威，行必须保持归档态。
        var index = new SessionIndexStore(fixture.Store);
        await index.SyncArchiveProjectionAsync(new HashSet<string>(StringComparer.Ordinal), CancellationToken.None);
        var source = await index.GetRowAsync(firstId, CancellationToken.None);
        Assert.That(source!.Archived, Is.False, "前置：索引归档投影已置 0。");
        await index.WriteSummaryAsync(source.Summary!, source.SourceLength, source.SourceModifiedUtc,
                                      CancellationToken.None);

        await model.InitializeAsync();

        Assert.That(model.Sessions.Single(session => session.Id == firstId), Is.SameAs(archivedRow));
        Assert.That(archivedRow.IsArchived, Is.True, "索引摘要与归档投影列不得覆盖内存快照的归档权威。");
        Assert.That(group.Sessions, Does.Not.Contain(archivedRow), "归档行在刷新后仍必须离开默认项目分组。");
    }

    /// <summary>
    ///     <para>选中变化不产生重复行；只有包含选中未置顶会话的分组保持 IsCurrent 高亮。</para>
    ///     <para>
    ///         Selection changes never duplicate rows; only the group holding the selected unpinned session keeps its
    ///         IsCurrent highlight.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SelectionChange_DoesNotDuplicateRowsAndKeepsHighlightSemantics()
    {
        var fixture = await Fixture.CreateAsync();
        await WriteHostedSessionAsync(fixture.Store, fixture.First, fixture.FirstProjectId, 0, CancellationToken.None);
        await WriteHostedSessionAsync(fixture.Store, fixture.First, fixture.FirstProjectId, 1, CancellationToken.None);
        await WriteHostedSessionAsync(fixture.Store, fixture.Second, fixture.SecondProjectId, 2,
                                      CancellationToken.None);
        await WriteHostedSessionAsync(fixture.Store, fixture.Second, fixture.SecondProjectId, 3,
                                      CancellationToken.None);
        await WriteHostedSessionAsync(fixture.Store, fixture.Scratch, null, 4, CancellationToken.None);
        await new SessionIndexReconciler(new SessionIndexStore(fixture.Store)).ReconcileAsync(CancellationToken.None);

        await using var model = fixture.Window();
        await model.InitializeAsync();

        var groupA = model.Projects.Single(item => item.Project is { } project && project.Id == fixture.FirstProjectId);
        var groupB =
            model.Projects.Single(item => item.Project is { } project && project.Id == fixture.SecondProjectId);
        var noProjectGroup = model.Projects.Single(item => item.IsNoProjectGroup);
        Assert.That(noProjectGroup.Sessions, Has.Count.EqualTo(1), "前置：无项目行已进入最近分组。");
        Assert.That(groupA.Sessions, Has.Count.EqualTo(2), "前置：项目 A 成员就绪。");
        Assert.That(groupB.Sessions, Has.Count.EqualTo(2), "前置：项目 B 成员就绪。");

        // 依次选中项目内、跨项目与无项目行；每次都核对重复行与分组高亮口径。
        foreach (var (row, group) in new[]
                 {
                     (groupA.Sessions[0], groupA), (groupB.Sessions[0], groupB),
                     (noProjectGroup.Sessions[0], noProjectGroup)
                 })
        {
            model.SelectSessionCommand.Execute(row);
            Assert.That(row.IsSelected, Is.True, "选中行必须标记 IsSelected。");
            Assert.That(model.SidebarRows.Count(inner => ReferenceEquals(inner, row)), Is.EqualTo(1),
                        "选中行在侧栏投影中必须恰好一次。");
            Assert.That(model.SidebarRows.GroupBy(inner => inner).Any(items => items.Count() > 1), Is.False,
                        "选中变化不得产生重复行。");
            Assert.That(model.Projects.Where(item => item.IsCurrent).ToArray(), Is.EqualTo(new[] { group }),
                        "只有包含选中未置顶会话的分组保持 IsCurrent。");
        }
    }

    /// <summary>
    ///     <para>经宿主提交一份最小合法会话日志；显式绑定或显式无项目。</para>
    ///     <para>Commits one minimal valid session log through the host; bound or explicitly no-project.</para>
    /// </summary>
    private static async Task<string> WriteHostedSessionAsync(
        string store, string workspaceRoot, string? projectId, int index, CancellationToken cancellationToken)
    {
        var session = new ChatSession(Guid.NewGuid().ToString("N"), workspaceRoot, Fixture.Identity, null, projectId,
                                      projectId is null
                                          ? SessionProjectAssignmentState.NoProject
                                          : SessionProjectAssignmentState.Project);
        var client = new ScriptedClient();
        client.Enqueue(new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "offline answer" },
                       new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason          = "stop" });
        using var writer = new SessionStoreWriter(store, session.Id);
        using var host = new ChatSessionHost(session, new ConversationContext(new ContextOptions()),
                                             new AgentOptions
                                             {
                                                 Model                     = "offline", MaxAgentSteps = 10,
                                                 DefaultToolTimeoutSeconds = 30
                                             },
                                             client, new ToolRegistry([]), "sidebar invariants harness",
                                             sessionWriter : writer);
        await host.SubmitTurnAsync($"sidebar invariant session {index}", cancellationToken);
        return session.Id;
    }

    /// <summary>直写一份未归属的旧格式日志；无回合正文，保持待初始化状态。</summary>
    private static async Task<string> CommitLegacySessionAsync(string            store, string root,
                                                               CancellationToken cancellationToken)
    {
        var       session = new ChatSession(Guid.NewGuid().ToString("N"), root, Fixture.Identity);
        using var writer  = new SessionStoreWriter(store, session.Id);
        await writer.CommitAsync(session, new ConversationContext(new ContextOptions()), cancellationToken);
        return session.Id;
    }

    /// <summary>
    ///     <para>统计目标行上被监视属性名的通知次数；只记录命中项，未命中保持缺席。</para>
    ///     <para>
    ///         Counts notifications of watched property names on one row; only hits are recorded, misses stay absent.
    ///     </para>
    /// </summary>
    private sealed class PropertyGuardCounter : IDisposable
    {
        private readonly SessionViewModel _row;
        private readonly HashSet<string>  _watched;

        public PropertyGuardCounter(SessionViewModel row, IReadOnlyList<string> watchedProperties)
        {
            _row                =  row;
            _watched            =  [.. watchedProperties];
            row.PropertyChanged += OnRowPropertyChanged;
        }

        public Dictionary<string, int> Counts { get; } = [];

        public void Dispose() => _row.PropertyChanged -= OnRowPropertyChanged;

        private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is not { } name || !_watched.Contains(name)) return;
            Counts[name] = Counts.GetValueOrDefault(name) + 1;
        }
    }

    private sealed class Fixture
    {
        public static readonly SessionModelIdentity Identity =
            new("offline", "https://offline.invalid/v1", ChatApiKind.ChatCompletions);

        public required string First           { get; init; }
        public required string Second          { get; init; }
        public required string Third           { get; init; }
        public required string Scratch         { get; init; }
        public required string Store           { get; init; }
        public required string Config          { get; init; }
        public required string FirstProjectId  { get; init; }
        public required string SecondProjectId { get; init; }
        public required string ThirdProjectId  { get; init; }

        public DesktopSessionFactory Factory => new((_, _) => new ScriptedClient(), new Credentials(), Config);

        public MainWindowViewModel Window() => new(Factory, Config, action =>
        {
            action();
            return ValueTask.CompletedTask;
        });

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(),
                                    "tinyharness-sidebar-invariants-" + Guid.NewGuid().ToString("N"));
            var store = Path.Combine(root, "sessions");
            Directory.CreateDirectory(store);
            var first   = Path.Combine(root, "work-a");
            var second  = Path.Combine(root, "work-b");
            var third   = Path.Combine(root, "work-c");
            var scratch = Path.Combine(root, "scratch");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            Directory.CreateDirectory(third);
            Directory.CreateDirectory(scratch);
            var firstProject = await ProjectStore.GetOrCreateAsync(ProjectStore.FilePath(store), first, null,
                                                                   CancellationToken.None);
            var secondProject = await ProjectStore.GetOrCreateAsync(ProjectStore.FilePath(store), second, null,
                                                                    CancellationToken.None);
            var thirdProject = await ProjectStore.GetOrCreateAsync(ProjectStore.FilePath(store), third, null,
                                                                   CancellationToken.None);
            var fixture = new Fixture
            {
                First           = first,
                Second          = second,
                Third           = third,
                Scratch         = scratch,
                Store           = store,
                Config          = Path.Combine(root, "user.json"),
                FirstProjectId  = firstProject.Id,
                SecondProjectId = secondProject.Id,
                ThirdProjectId  = thirdProject.Id
            };
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

    /// <summary>脚本化假模型客户端：按队列逐次应答，驱动宿主的提交回合。</summary>
    private sealed class ScriptedClient : IChatCompletionClient
    {
        private readonly Queue<IReadOnlyList<ChatStreamEvent>> _responses = new();

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (_responses.Count == 0) throw new InvalidOperationException("No scripted response for the request.");
            foreach (var item in _responses.Dequeue())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }

        public void Enqueue(params ChatStreamEvent[] events) => _responses.Enqueue(events);
    }

    /// <summary>门控假模型：进入流式后停在门上，放行后给出最终回答。</summary>
    private sealed class GatedModel(
        TaskCompletionSource entered,
        TaskCompletionSource finish) : IChatCompletionClient
    {
        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await finish.Task.WaitAsync(cancellationToken);
            yield return new ChatStreamEvent
                { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "offline answer" };
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" };
        }
    }
}

using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using NUnit.Framework;
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
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;
using TinyHarness.Desktop.Runtime;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

/// <summary>
///     <para>
///         侧栏差量更新契约：无变化刷新保持安静；索引循环轻量路径应用会话行但不重读目录册；
///         单行元数据变化只触及目标行。
///     </para>
///     <para>
///         Sidebar delta-update contracts: an unchanged refresh stays quiet; the index loop's lightweight path applies
///         session rows without reloading the catalog; a single-row metadata change only touches the target row.
///     </para>
/// </summary>
[TestFixture]
public sealed class DesktopSidebarDeltaUpdateTests
{
    /// <summary>
    ///     <para>
    ///         无变化刷新零扰动：SidebarRows 与项目菜单集合零事件，抽样行的权限属性零通知，
    ///         行数、行实例与选中保持不变。
    ///     </para>
    ///     <para>
    ///         An unchanged refresh is fully quiet: zero events on SidebarRows and the project menu, zero permission
    ///         notifications on sampled rows, with row count, row instances and selection preserved.
    ///     </para>
    /// </summary>
    [Test]
    public async Task UnchangedRefresh_StaysQuietOnSidebarRowsPermissionAndProjectMenu()
    {
        var fixture = await DeltaFixture.CreateAsync();
        var firstId = await DeltaFixture.WriteHostedSessionAsync(fixture.Store, fixture.First, fixture.FirstProjectId,
                                                                 0,
                                                                 CancellationToken.None);
        await DeltaFixture.WriteHostedSessionAsync(fixture.Store, fixture.Second, fixture.SecondProjectId, 1,
                                                   CancellationToken.None);
        await DeltaFixture.WriteHostedSessionAsync(fixture.Store, fixture.Third, fixture.ThirdProjectId, 2,
                                                   CancellationToken.None);
        await DeltaFixture.CommitLegacySessionAsync(fixture.Store, fixture.Scratch, CancellationToken.None);
        await fixture.ReconcileAsync();

        await using var model = fixture.Window();
        await model.InitializeAsync();

        Assert.That(model.Sessions, Has.Count.EqualTo(4), "前置：3 个绑定行与 1 个待初始化旧日志行全部加载。");
        var rowsBefore     = model.Sessions.ToArray();
        var sidebarBefore  = model.SidebarRows.ToArray();
        var selectedBefore = model.SelectedSession;
        Assert.That(sidebarBefore.Distinct().Count(), Is.EqualTo(sidebarBefore.Length), "前置：侧栏行无重复。");

        var sidebarCounter = new CollectionEventCounter(model.SidebarRows);
        var menuCounter    = new CollectionEventCounter(rowsBefore[0].ProjectMenuOptions);
        using var watches = new RowNotificationWatchCollection(rowsBefore.Take(3),
        [
            nameof(SessionViewModel.PermissionMode),
            nameof(SessionViewModel.PermissionPickerLabel)
        ]);

        await model.InitializeAsync();

        Assert.That(sidebarCounter.TotalEvents, Is.EqualTo(0), "无变化刷新不得扰动 SidebarRows 投影。");
        Assert.That(menuCounter.TotalEvents, Is.EqualTo(0),
                    "projectOptions 复用必须让项目菜单集合零事件。");
        foreach (var (row, counts) in watches.CountsByRow)
            Assert.That(counts, Is.Empty, $"行 {row.Id} 的权限属性不得发出通知。");
        Assert.That(model.Sessions.Count, Is.EqualTo(rowsBefore.Length), "行数必须不变：安静不得来自未加载。");
        Assert.That(model.Sessions.ToArray(), Is.EqualTo(rowsBefore), "刷新不得替换任何行实例。");
        Assert.That(model.SidebarRows.ToArray(), Is.EqualTo(sidebarBefore), "侧栏投影序列必须逐位不变。");
        Assert.That(model.SelectedSession, Is.SameAs(selectedBefore), "选中页必须跨刷新保持。");
    }

    /// <summary>
    ///     <para>
    ///         索引循环轻量路径应用会话行变化但不重读目录册：新增行进入正确分组且无重复，
    ///         目录册改名暂不应用；随后的目录册检查把组名收敛并复用分组实例。
    ///     </para>
    ///     <para>
    ///         The index loop's lightweight path applies session-row changes without reloading the catalog: the new row
    ///         joins the right group without duplicates while the rename stays unapplied; a following catalog check
    ///         converges the name and reuses the group instance.
    ///     </para>
    /// </summary>
    [Test]
    public async Task IndexLoopLightRefresh_AppliesSessionRowsWithoutCatalogReload()
    {
        var fixture = await DeltaFixture.CreateAsync();
        await DeltaFixture.WriteHostedSessionAsync(fixture.Store, fixture.First, fixture.FirstProjectId, 0,
                                                   CancellationToken.None);
        await fixture.ReconcileAsync();

        await using var model = fixture.Window();
        await model.InitializeAsync();

        var group = model.Projects.Single(item => item.Project is { } project && project.Id == fixture.FirstProjectId);
        var oldName = group.Name;

        // 索引 bump：新增一份绑定日志并对账，只重建新增条目。
        var newId = await DeltaFixture.WriteHostedSessionAsync(fixture.Store, fixture.First, fixture.FirstProjectId, 1,
                                                               CancellationToken.None);
        var round = await new SessionIndexReconciler(new SessionIndexStore(fixture.Store))
           .ReconcileAsync(CancellationToken.None);
        Assert.That(round.Rebuilt, Is.EqualTo(1), "前置：对账只重建新增条目。");

        // 目录册改名只落文件：不经 monitor 应用，轻路径也不重读目录册。
        var renamed = await RenameProjectAsync(fixture.Store, fixture.FirstProjectId, "差量改名项目",
                                               CancellationToken.None);

        await model.RefreshSessionListAsync(fixture.Store);

        Assert.That(model.Sessions.Count, Is.EqualTo(2), "轻路径必须应用新增会话行。");
        var newRow = model.Sessions.Single(session => session.Id == newId);
        Assert.That(model.Sessions.Count(session => session.Id   == newId), Is.EqualTo(1), "新增行不得重复。");
        var groupAfterLight =
            model.Projects.Single(item => item.Project is { } project && project.Id == fixture.FirstProjectId);
        Assert.That(groupAfterLight, Is.SameAs(group), "轻路径必须复用分组实例。");
        Assert.That(groupAfterLight.Sessions, Does.Contain(newRow), "新增会话行必须进入所属项目分组。");
        Assert.That(model.SidebarRows.Count(row => ReferenceEquals(row, newRow)), Is.EqualTo(1),
                    "侧栏投影中新增行必须恰好出现一次。");
        Assert.That(groupAfterLight.Name, Is.EqualTo(oldName),
                    "轻路径不重读目录册：改名不被本轮应用是有意契约，由目录册检查收敛。");

        await model.CheckProjectCatalogAsync(CancellationToken.None);

        Assert.That(model.Projects.Single(item => item.Project is { } project && project.Id == fixture.FirstProjectId),
                    Is.SameAs(group), "目录册收敛必须复用原分组实例。");
        Assert.That(group.Name, Is.EqualTo(renamed.Name), "目录册检查必须把组名收敛到改名结果。");
    }

    /// <summary>
    ///     <para>
    ///         单行摘要变化只触及目标行：目标行活动时间前进且实例保持，其余行零归属与活动通知；
    ///         SidebarRows 事件数仅记录不硬断言（活动变化不一定引发行移动，手动顺序主导）。
    ///     </para>
    ///     <para>
    ///         A single-summary change only touches the target row: its activity advances with the instance kept while
    ///         other rows raise no ownership or activity notifications; SidebarRows events are recorded, not asserted,
    ///         since activity changes do not necessarily move rows under manual ordering.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SingleRowMetadataChange_OnlyTouchesTargetRow()
    {
        var fixture = await DeltaFixture.CreateAsync();
        var targetId = await DeltaFixture.WriteHostedSessionAsync(fixture.Store, fixture.First, fixture.FirstProjectId,
                                                                  0,
                                                                  CancellationToken.None);
        await DeltaFixture.WriteHostedSessionAsync(fixture.Store, fixture.Second, fixture.SecondProjectId, 1,
                                                   CancellationToken.None);
        await DeltaFixture.WriteHostedSessionAsync(fixture.Store, fixture.Third, fixture.ThirdProjectId, 2,
                                                   CancellationToken.None);
        await fixture.ReconcileAsync();

        await using var model = fixture.Window();
        await model.InitializeAsync();

        var targetRow      = model.Sessions.Single(session => session.Id == targetId);
        var quietRows      = model.Sessions.Where(session => session.Id  != targetId).ToArray();
        var baseline       = targetRow.LastActivityUtc;
        var sidebarCounter = new CollectionEventCounter(model.SidebarRows);
        using var watches = new RowNotificationWatchCollection(quietRows,
        [
            nameof(SessionViewModel.LastActivityUtc),
            nameof(SessionViewModel.ProjectId)
        ]);

        // 只推进目标行的索引摘要活动时间；来源标记不变，权威日志不动。
        var index  = new SessionIndexStore(fixture.Store);
        var source = await index.GetRowAsync(targetId, CancellationToken.None);
        Assert.That(source!.Summary, Is.Not.Null, "前置：目标行必须携带健康摘要。");
        await index.WriteSummaryAsync(source.Summary! with
                                      {
                                          LastActivityUtc = source.Summary.LastActivityUtc.AddHours(1)
                                      },
                                      source.SourceLength, source.SourceModifiedUtc, CancellationToken.None);

        await model.InitializeAsync();

        Assert.That(model.Sessions.Single(session => session.Id == targetId), Is.SameAs(targetRow),
                    "刷新不得替换目标行实例。");
        Assert.That(targetRow.LastActivityUtc, Is.GreaterThan(baseline), "目标行活动时间必须前进。");
        foreach (var (row, counts) in watches.CountsByRow)
            Assert.That(counts, Is.Empty, $"行 {row.Id} 不得发出 LastActivityUtc/ProjectId 通知。");
        TestContext.Progress.WriteLine($"sidebar_rows_events_after_single_row_change={sidebarCounter.TotalEvents}");
    }

    /// <summary>以外部写者身份重读目录册并按当前修订号改名。</summary>
    private static async Task<Project> RenameProjectAsync(
        string store, string projectId, string newName, CancellationToken cancellationToken)
    {
        var path    = ProjectStore.FilePath(store);
        var catalog = await ProjectStore.LoadAsync(path, cancellationToken);
        var project = catalog.Projects.Single(item => item.Id == projectId);
        return await ProjectStore.RenameAsync(path, projectId, newName, project.Revision, null, cancellationToken);
    }

    /// <summary>
    ///     <para>统计单个集合的全部变更事件次数。</para>
    ///     <para>Counts every collection-changed event raised by one collection.</para>
    /// </summary>
    private sealed class CollectionEventCounter : IDisposable
    {
        private readonly INotifyCollectionChanged _collection;

        public CollectionEventCounter(INotifyCollectionChanged collection)
        {
            _collection                  =  collection;
            collection.CollectionChanged += OnCollectionChanged;
        }

        public int TotalEvents { get; private set; }

        public void Dispose() => _collection.CollectionChanged -= OnCollectionChanged;

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => TotalEvents++;
    }

    /// <summary>
    ///     <para>批量挂接行通知计数；按行分桶统计被监视属性名的命中次数，未命中保持缺席。</para>
    ///     <para>
    ///         Batched row-notification counting; watched property hits are bucketed per row and misses stay absent.
    ///     </para>
    /// </summary>
    private sealed class RowNotificationWatchCollection : IDisposable
    {
        private readonly Dictionary<SessionViewModel, Dictionary<string, int>> _countsByRow = [];
        private readonly List<SessionViewModel>                                _rows        = [];
        private readonly HashSet<string>                                       _watched;

        public RowNotificationWatchCollection(IEnumerable<SessionViewModel> rows,
                                              IReadOnlyList<string>         watchedProperties)
        {
            _watched = [.. watchedProperties];
            foreach (var row in rows)
            {
                _rows.Add(row);
                _countsByRow[row]   =  [];
                row.PropertyChanged += OnRowPropertyChanged;
            }
        }

        public IReadOnlyDictionary<SessionViewModel, Dictionary<string, int>> CountsByRow => _countsByRow;

        public void Dispose()
        {
            foreach (var row in _rows) row.PropertyChanged -= OnRowPropertyChanged;
        }

        private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is not { } name  ||
                sender is not SessionViewModel row ||
                !_watched.Contains(name)           ||
                !_countsByRow.TryGetValue(row, out var counts))
                return;
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }
    }

    /// <summary>
    ///     <para>差量契约夹具：隔离目录、离线配置、三个项目工作区与已建索引的会话存储。</para>
    ///     <para>
    ///         The delta-contract fixture: an isolated root, offline config, three project workspaces, and a session
    ///         store with a built index.
    ///     </para>
    /// </summary>
    private sealed class DeltaFixture
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

        /// <summary>对当前 store 建立或追平会话索引。</summary>
        public async Task ReconcileAsync()
        {
            await new SessionIndexReconciler(new SessionIndexStore(Store)).ReconcileAsync(CancellationToken.None);
        }

        /// <summary>
        ///     <para>经宿主提交一份最小合法会话日志；显式绑定或显式无项目。</para>
        ///     <para>Commits one minimal valid session log through the host; bound or explicitly no-project.</para>
        /// </summary>
        public static async Task<string> WriteHostedSessionAsync(
            string store, string workspaceRoot, string? projectId, int index, CancellationToken cancellationToken)
        {
            var session = new ChatSession(Guid.NewGuid().ToString("N"), workspaceRoot, Identity, null, projectId,
                                          projectId is null
                                              ? SessionProjectAssignmentState.NoProject
                                              : SessionProjectAssignmentState.Project);
            var client = new ScriptedClient();
            client.Enqueue(new ChatStreamEvent
                               { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "offline answer" },
                           new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" });
            using var writer = new SessionStoreWriter(store, session.Id);
            using var host = new ChatSessionHost(session, new ConversationContext(new ContextOptions()),
                                                 new AgentOptions
                                                 {
                                                     Model                     = "offline", MaxAgentSteps = 10,
                                                     DefaultToolTimeoutSeconds = 30
                                                 },
                                                 client, new ToolRegistry([]), "sidebar delta harness",
                                                 sessionWriter : writer);
            await host.SubmitTurnAsync($"sidebar delta session {index}", cancellationToken);
            return session.Id;
        }

        /// <summary>直写一份未归属的旧格式日志；无回合正文，保持待初始化状态。</summary>
        public static async Task<string> CommitLegacySessionAsync(
            string store, string root, CancellationToken cancellationToken)
        {
            var       session = new ChatSession(Guid.NewGuid().ToString("N"), root, Identity);
            using var writer  = new SessionStoreWriter(store, session.Id);
            await writer.CommitAsync(session, new ConversationContext(new ContextOptions()), cancellationToken);
            return session.Id;
        }

        public static async Task<DeltaFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(),
                                    "tinyharness-sidebar-delta-" + Guid.NewGuid().ToString("N"));
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
            var fixture = new DeltaFixture
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
}

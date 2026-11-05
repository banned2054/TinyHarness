using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
///         侧栏投影链路的隔离基准：3030 份合成会话日志加 100 个项目目录册，测量 MainWindowViewModel
///         在冷全量、无变化重刷、单会话变化、新增会话、项目改名、联合变化与索引循环轻刷新七个场景下的
///         墙钟、分配与集合事件数量，作为第三阶段优化的基线记录。
///     </para>
///     <para>
///         Isolated benchmark for the sidebar projection chain: 3030 synthetic session logs plus a 100-project
///         catalog measure wall time, allocations, and collection events of MainWindowViewModel across seven
///         scenarios — cold full load, no-change reload, single-summary change, new session, project rename, the
///         combined change, and the index loop's lightweight refresh — as the phase-three optimization baseline.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         标记 Explicit 且带 benchmark 类别，常规测试运行不执行。负载写入全新隔离的
///         %TEMP%\tinyharness-sidebar-bench-&lt;guid&gt; 目录并在测试结束删除，结果行写入
///         %TEMP%\tinyharness-sidebar-bench\baseline-sidebar-report.json。数值只记录不断言；
///         正确性断言仅限行数、归属与改名收敛。VisibleSessionCount 每组 5 行的分页是既有 UI 行为，按现状记录。
///     </para>
///     <para>
///         Marked Explicit with the benchmark category so normal test runs skip it. The load lives in a fresh
///         isolated %TEMP%\tinyharness-sidebar-bench-&lt;guid&gt; directory deleted when the test ends, and result
///         rows go to %TEMP%\tinyharness-sidebar-bench\baseline-sidebar-report.json. Numbers are recorded, never
///         asserted; correctness assertions cover row counts, ownership, and rename convergence only. The
///         VisibleSessionCount page of 5 rows per group is existing UI behavior, recorded as-is.
///     </para>
/// </remarks>
[TestFixture]
[NUnit.Framework.Category("benchmark")]
public sealed class DesktopSidebarBenchmarkTests
{
    private const int ShortCount     = 3000;
    private const int LongCount      = 20;
    private const int ToolHeavyCount = 10;
    private const int ProjectCount   = 100;

    /// <summary>前 ShortCount - NoProjectCount 条短会话按项目轮转绑定，其余显式无项目。</summary>
    private const int NoProjectCount = 100;

    /// <summary>场景间等待集合事件静默的窗口长度；连续静默 8 次视为无在途后台工作。</summary>
    private const int QuiescePollMs = 100;

    private const int QuiesceQuietPolls = 8;

    private static readonly SessionModelIdentity Identity =
        new("offline", "https://offline.invalid/v1", ChatApiKind.ChatCompletions);

    private static string ReportDirectory => Path.Combine(Path.GetTempPath(), "tinyharness-sidebar-bench");

    /// <summary>
    ///     <para>顺序执行 S1–S7 七个场景：每个场景独立计时、统计分配与集合事件，结果逐行追加到 JSON 报告。</para>
    ///     <para>
    ///         Runs scenarios S1–S7 sequentially: each is timed, allocation-sampled, and event-counted independently,
    ///         with rows appended to the JSON report.
    ///     </para>
    /// </summary>
    [Test]
    [Explicit]
    public async Task Measure_SidebarProjectionScenarios_Baseline()
    {
        var cancellationToken = CancellationToken.None;
        var fixture           = await SidebarFixture.CreateAsync(cancellationToken);
        try
        {
            await using var model    = fixture.Window();
            var             counters = new ScenarioCounters(model);
            var             results  = new List<Dictionary<string, object?>>();

            async Task<Dictionary<string, object?>> MeasureAsync(string              scenario, Func<Task> action,
                                                                 RowPropertySampler? sampler)
            {
                var allocatedBefore = GC.GetTotalAllocatedBytes(true);
                var watch           = Stopwatch.StartNew();
                await action();
                watch.Stop();
                var row = counters.SnapshotRow(scenario, model, watch.Elapsed.TotalMilliseconds,
                                               GC.GetTotalAllocatedBytes(true) - allocatedBefore, sampler);
                results.Add(row);
                await WriteReportAsync(results, cancellationToken);
                TestContext.Progress.WriteLine($"[{scenario}] wall_ms={row["wall_ms"]} " +
                                               $"alloc_bytes={row["alloc_bytes"]} "      +
                                               $"group_events={row["group_sessions_events_total"]}");
                return row;
            }

            async Task SettleAsync(RowPropertySampler? sampler)
            {
                // 先吸收上一轮（如 S1 启动轮）的尾部重载等在途后台工作，再等待一次完整启动轮收尾：
                // StartAsync 会等待对账＋空整理＋尾部加载全部结束，并把索引刷新循环的 15 秒计时复位，
                // 使随后被测窗口内不会出现后台重载污染事件计数。
                await counters.QuiesceAsync();
                await model.StartAsync();
                counters.Reset();
                sampler?.ResetCounts();
            }

            // S1 冷全量：新 VM 首次 InitializeAsync；索引已 Available，归属整理轮随首次 store 变化在后台启动。
            counters.Reset();
            RowPropertySampler? sampler = null;
            await MeasureAsync("S1_cold_full_load", () => model.InitializeAsync(), sampler);
            Assert.That(model.Sessions.Count, Is.EqualTo(SidebarFixture.TotalSessions),
                        "冷全量必须呈现全部会话行。");
            Assert.That(model.Projects.Count, Is.EqualTo(ProjectCount + 1),
                        "100 个项目组加 1 个无项目组。");
            // 抽样 3 个长会话行挂 PropertyChanged 计数器；行实例跨重载保留，后续场景复用同一批行。
            sampler = new RowPropertySampler(model, fixture.LongIds.Take(3).ToList());

            // S2 无变化重刷：同一 VM 再次 InitializeAsync，全部行只做元数据刷新。
            await SettleAsync(sampler);
            await MeasureAsync("S2_reload_no_change", () => model.InitializeAsync(), sampler);

            // S3 单会话变化：索引摘要活动时间 +1 小时（来源标记不变）后重载，目标行取自未抽样的短会话。
            await SettleAsync(sampler);
            var targetId       = fixture.ShortIds[0];
            var targetActivity = model.Sessions.Single(session => session.Id == targetId).LastActivityUtc;
            await ChangeSummaryActivityAsync(fixture.Store, targetId, TimeSpan.FromHours(1), cancellationToken);
            await MeasureAsync("S3_single_summary_change", () => model.InitializeAsync(), sampler);
            Assert.That(model.Sessions.Single(session => session.Id == targetId).LastActivityUtc,
                        Is.GreaterThan(targetActivity), "摘要活动时间必须推进到目标行。");

            // S4 新增会话：写 1 份绑定日志并对账，重载只新增 1 行。
            await SettleAsync(sampler);
            var newId = await SidebarFixture.WriteHostedSessionAsync(fixture.Store, fixture.ProjectRoots[0],
                                                                     fixture.ProjectIds[0], 1, false,
                                                                     9999, cancellationToken);
            var round = await new SessionIndexReconciler(new SessionIndexStore(fixture.Store))
               .ReconcileAsync(SessionIndexReconcileOptions.Default, cancellationToken);
            Assert.That(round.Rebuilt, Is.EqualTo(1), "对账只重建新增条目。");
            Assert.That(round.Unchanged, Is.EqualTo(SidebarFixture.TotalSessions), "既有条目零正文扫描。");
            await MeasureAsync("S4_new_session_load", () => model.InitializeAsync(), sampler);
            Assert.That(model.Sessions.Count, Is.EqualTo(SidebarFixture.TotalSessions + 1), "新增会话必须成行。");
            Assert.That(model.Sessions.Any(session => session.Id == newId), Is.True, "新增会话行必须存在。");

            // S5 项目改名：目录册改名后经内部同步缝一次检查收敛；S5 只关心 Projects/SidebarRows/组集合事件。
            await SettleAsync(sampler);
            var renameId = fixture.ProjectIds[5];

            ProjectViewModel GroupById(string id) =>
                model.Projects.Single(item => item.Project is { } project && project.Id == id);

            var groupBefore = GroupById(renameId);
            var renamed     = await RenameProjectAsync(fixture.Store, renameId, "基准改名项目-05", cancellationToken);
            await MeasureAsync("S5_project_rename_sync",
                               () => model.CheckProjectCatalogAsync(cancellationToken), sampler);
            var groupAfter = GroupById(renameId);
            Assert.That(groupAfter, Is.SameAs(groupBefore), "改名必须复用原分组实例。");
            Assert.That(groupAfter.Name, Is.EqualTo(renamed.Name), "组名必须收敛到目录册改名结果。");

            // S6 联合变化：另一份摘要 +1 小时与另一个项目改名同时发生，一次重载加一次目录册检查。
            await SettleAsync(sampler);
            var renameId6 = fixture.ProjectIds[6];
            var group6    = GroupById(renameId6);
            await ChangeSummaryActivityAsync(fixture.Store, fixture.ShortIds[1], TimeSpan.FromHours(1),
                                             cancellationToken);
            var renamed6 = await RenameProjectAsync(fixture.Store, renameId6, "基准改名项目-06", cancellationToken);
            await MeasureAsync("S6_combined_change", async () =>
            {
                await model.InitializeAsync();
                await model.CheckProjectCatalogAsync(cancellationToken);
            }, sampler);
            Assert.That(GroupById(renameId6).Name, Is.EqualTo(renamed6.Name), "联合变化后组名必须收敛。");
            Assert.That(GroupById(renameId6), Is.SameAs(group6), "联合变化仍必须复用原分组实例。");
            Assert.That(model.Sessions.Count, Is.EqualTo(SidebarFixture.TotalSessions + 1), "联合变化不改行数。");

            // S7 索引循环轻刷新：沿用 S3 的单行摘要变化准备，改走 15 秒循环实际路径 RefreshSessionListAsync。
            await SettleAsync(sampler);
            var targetId7       = fixture.ShortIds[2];
            var targetActivity7 = model.Sessions.Single(session => session.Id == targetId7).LastActivityUtc;
            await ChangeSummaryActivityAsync(fixture.Store, targetId7, TimeSpan.FromHours(1), cancellationToken);
            await MeasureAsync("S7_index_loop_light_refresh", () => model.RefreshSessionListAsync(fixture.Store),
                               sampler);
            Assert.That(model.Sessions.Single(session => session.Id == targetId7).LastActivityUtc,
                        Is.GreaterThan(targetActivity7), "轻刷新必须把摘要活动时间推进到目标行。");
            Assert.That(model.Sessions.Count, Is.EqualTo(SidebarFixture.TotalSessions + 1), "轻刷新不改行数。");
        }
        finally
        {
            fixture.Delete();
        }
    }

    /// <summary>
    ///     <para>把索引摘要的目标行活动时间前移指定时长；来源标记保持不变，不触碰权威日志。</para>
    ///     <para>
    ///         Advances one index summary's activity time by the given span; source markers stay unchanged and the
    ///         authoritative log is never touched.
    ///     </para>
    /// </summary>
    private static async Task ChangeSummaryActivityAsync(
        string store, string sessionId, TimeSpan advance, CancellationToken cancellationToken)
    {
        var indexStore = new SessionIndexStore(store);
        var row        = await indexStore.GetRowAsync(sessionId, cancellationToken);
        Assert.That(row, Is.Not.Null, "前置：目标行必须在索引中。");
        Assert.That(row!.Summary, Is.Not.Null, "前置：目标行必须携带健康摘要。");
        await indexStore.WriteSummaryAsync(
                                           row.Summary! with
                                           {
                                               LastActivityUtc = row.Summary.LastActivityUtc.Add(advance)
                                           },
                                           row.SourceLength, row.SourceModifiedUtc, cancellationToken);
    }

    /// <summary>
    ///     <para>以外部写者身份重读目录册并按当前修订号改名。</para>
    ///     <para>Renames as an external writer, reloading the catalog for the current revision first.</para>
    /// </summary>
    private static async Task<Project> RenameProjectAsync(
        string store, string projectId, string newName, CancellationToken cancellationToken)
    {
        var path    = ProjectStore.FilePath(store);
        var catalog = await ProjectStore.LoadAsync(path, cancellationToken);
        var project = catalog.Projects.Single(item => item.Id == projectId);
        return await ProjectStore.RenameAsync(path, projectId, newName, project.Revision, null, cancellationToken);
    }

    /// <summary>
    ///     <para>把当前全部场景结果行整体写入固定报告路径；每场景落盘一次，中途失败也保留已完成行。</para>
    ///     <para>
    ///         Rewrites all scenario rows to the fixed report path after each scenario, so a mid-run failure keeps
    ///         the finished rows.
    ///     </para>
    /// </summary>
    private static async Task WriteReportAsync(
        IReadOnlyList<Dictionary<string, object?>> results, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ReportDirectory);
        var path = Path.Combine(ReportDirectory, "baseline-sidebar-report.json");
        await File.WriteAllTextAsync(path,
                                     JsonSerializer.Serialize(results,
                                                              new JsonSerializerOptions { WriteIndented = true }),
                                     cancellationToken);
        TestContext.Progress.WriteLine($"report={path}");
    }

    /// <summary>
    ///     <para>单场景集合事件计数：挂接 Sessions、Projects、SidebarRows 与全部项目组的 Sessions 集合。</para>
    ///     <para>
    ///         Per-scenario collection event counting over Sessions, Projects, SidebarRows, and every project
    ///         group's Sessions collection.
    ///     </para>
    /// </summary>
    private sealed class ScenarioCounters
    {
        private readonly GroupEventHub       _groups;
        private readonly MainWindowViewModel _model;
        private readonly CollectionCounter   _projects    = new();
        private readonly CollectionCounter   _sessions    = new();
        private readonly CollectionCounter   _sidebarRows = new();

        public ScenarioCounters(MainWindowViewModel model)
        {
            _model  = model;
            _groups = new GroupEventHub();
            Attach();
        }

        public int TotalEventCount =>
            _sessions.TotalEvents + _projects.TotalEvents + _sidebarRows.TotalEvents + _groups.TotalEvents;

        /// <summary>
        ///     <para>等待集合事件连续静默；吸收启动轮尾部重载等在途后台工作，避免污染下一窗口。</para>
        ///     <para>
        ///         Waits until collection events stay quiet, absorbing in-flight background work such as a startup
        ///         round's tail reload so the next window stays clean.
        ///     </para>
        /// </summary>
        public async Task QuiesceAsync()
        {
            for (var quiet = 0; quiet < QuiesceQuietPolls;)
            {
                var before = TotalEventCount;
                await Task.Delay(QuiescePollMs);
                if (TotalEventCount == before) quiet++;
                else quiet = 0;
            }
        }

        public void Reset()
        {
            // 组实例跨重载复用：重新按当前 Projects 成员挂接，丢弃已消失的组计数并清零全部计数。
            _sessions.ResetCounts();
            _projects.ResetCounts();
            _sidebarRows.ResetCounts();
            _groups.Reset(_model.Projects);
        }

        public Dictionary<string, object?> SnapshotRow(
            string              scenario, MainWindowViewModel model, double wallMs, long allocBytes,
            RowPropertySampler? sampler) =>
            new()
            {
                ["scenario"]                     = scenario,
                ["sessions"]                     = model.Sessions.Count,
                ["projects"]                     = model.Projects.Count,
                ["wall_ms"]                      = Math.Round(wallMs, 1),
                ["alloc_bytes"]                  = allocBytes,
                ["sessions_events"]              = _sessions.Snapshot(),
                ["projects_events"]              = _projects.Snapshot(),
                ["sidebarrows_events"]           = _sidebarRows.Snapshot(),
                ["group_sessions_events_total"]  = _groups.TotalEvents,
                ["sampled_row_property_changes"] = sampler?.Snapshot() ?? RowPropertySampler.ZeroSnapshot()
            };

        private void Attach()
        {
            _sessions.Attach(_model.Sessions);
            _projects.Attach(_model.Projects);
            _sidebarRows.Attach(_model.SidebarRows);
            _groups.Reset(_model.Projects);
        }
    }

    /// <summary>
    ///     <para>项目组集合事件汇总：跟随 Projects 成员变化挂接与摘除各组 Sessions 的计数器。</para>
    ///     <para>
    ///         Aggregated group-collection events: attaches and detaches per-group Sessions counters as the
    ///         Projects membership changes.
    ///     </para>
    /// </summary>
    private sealed class GroupEventHub
    {
        private readonly List<CollectionCounter>                 _counters = [];
        private          ObservableCollection<ProjectViewModel>? _projects;
        private          NotifyCollectionChangedEventHandler?    _projectsHandler;

        public int TotalEvents => _counters.Sum(counter => counter.TotalEvents);

        /// <summary>
        ///     <para>按当前 Projects 成员重新挂接；重建期间新进入的组由 Projects 的 Add 事件即时补挂。</para>
        ///     <para>
        ///         Reattaches to the current Projects membership; groups added during a rebuild are picked up by the
        ///         Projects Add handler immediately.
        ///     </para>
        /// </summary>
        public void Reset(ObservableCollection<ProjectViewModel> projects)
        {
            DetachAll();
            _projects = projects;
            _projectsHandler = (_, args) =>
            {
                if (args.Action != NotifyCollectionChangedAction.Add || args.NewItems is null) return;
                foreach (ProjectViewModel group in args.NewItems) AttachGroup(group);
            };
            projects.CollectionChanged += _projectsHandler;
            foreach (var group in projects) AttachGroup(group);
        }

        private void AttachGroup(ProjectViewModel group)
        {
            if (_counters.Any(counter => counter.IsAttachedTo(group.Sessions))) return;
            var counter = new CollectionCounter();
            counter.Attach(group.Sessions);
            _counters.Add(counter);
        }

        private void DetachAll()
        {
            if (_projects is not null && _projectsHandler is not null)
                _projects.CollectionChanged -= _projectsHandler;
            _projects        = null;
            _projectsHandler = null;
            foreach (var counter in _counters) counter.Detach();
            _counters.Clear();
        }
    }

    /// <summary>
    ///     <para>单个集合的事件计数；ObservableCollection 的 Insert 折叠为 Add，replace 单独记录。</para>
    ///     <para>
    ///         One collection's event counts; ObservableCollection folds Insert into Add, and Replace is counted
    ///         separately.
    ///     </para>
    /// </summary>
    private sealed class CollectionCounter
    {
        private int                       _add;
        private INotifyCollectionChanged? _attached;
        private int                       _move;
        private int                       _remove;
        private int                       _replace;
        private int                       _reset;

        public int TotalEvents => _reset + _add + _remove + _move + _replace;

        public void Attach(INotifyCollectionChanged collection)
        {
            if (ReferenceEquals(_attached, collection)) return;
            Detach();
            _attached                    =  collection;
            collection.CollectionChanged += OnCollectionChanged;
        }

        public bool IsAttachedTo(INotifyCollectionChanged collection) => ReferenceEquals(_attached, collection);

        public void Detach()
        {
            if (_attached is null) return;
            _attached.CollectionChanged -= OnCollectionChanged;
            _attached                   =  null;
        }

        public void ResetCounts() => _reset = _add = _remove = _move = _replace = 0;

        public Dictionary<string, int> Snapshot() => new()
        {
            // insert 键按报告字段约定保留：Insert 调用与 Add 同为一个 Add 事件，恒为 0，实际插入计入 add。
            ["insert"]  = 0,
            ["reset"]   = _reset,
            ["add"]     = _add,
            ["remove"]  = _remove,
            ["move"]    = _move,
            ["replace"] = _replace
        };

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            switch (args.Action)
            {
                case NotifyCollectionChangedAction.Reset :   _reset++; break;
                case NotifyCollectionChangedAction.Add :     _add++; break;
                case NotifyCollectionChangedAction.Remove :  _remove++; break;
                case NotifyCollectionChangedAction.Move :    _move++; break;
                case NotifyCollectionChangedAction.Replace : _replace++; break;
            }
        }
    }

    /// <summary>
    ///     <para>抽样会话行的属性通知计数，只统计权限模式、权限标签、活动时间与项目归属四项。</para>
    ///     <para>
    ///         Sampled session-row property-change counts over permission mode, permission label, activity time, and
    ///         project ownership only.
    ///     </para>
    /// </summary>
    private sealed class RowPropertySampler
    {
        private static readonly string[] WatchedProperties =
        [
            nameof(SessionViewModel.PermissionMode),
            nameof(SessionViewModel.PermissionPickerLabel),
            nameof(SessionViewModel.LastActivityUtc),
            nameof(SessionViewModel.ProjectId)
        ];

        private readonly Dictionary<string, int> _counts = [];

        private readonly List<SessionViewModel> _rows = [];

        public RowPropertySampler(MainWindowViewModel model, IReadOnlyList<string> sessionIds)
        {
            foreach (var id in sessionIds)
            {
                var row = model.Sessions.Single(session => session.Id == id);
                _rows.Add(row);
                row.PropertyChanged += OnRowPropertyChanged;
            }
        }

        public static Dictionary<string, int> ZeroSnapshot() =>
            WatchedProperties.ToDictionary(name => name, _ => 0);

        public void ResetCounts() => _counts.Clear();

        public Dictionary<string, int> Snapshot() =>
            WatchedProperties.ToDictionary(name => name, name => _counts.GetValueOrDefault(name));

        private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (Array.IndexOf(WatchedProperties, args.PropertyName) < 0) return;
            _counts[args.PropertyName!] = _counts.GetValueOrDefault(args.PropertyName!) + 1;
        }
    }

    /// <summary>
    ///     <para>合成负载夹具：3030 份会话日志、100 个项目目录册与已建好的索引，位于全新隔离目录。</para>
    ///     <para>
    ///         The synthetic load fixture: 3030 session logs, a 100-project catalog, and a prebuilt index inside one
    ///         fresh isolated directory.
    ///     </para>
    /// </summary>
    private sealed class SidebarFixture
    {
        public const int TotalSessions = ShortCount + LongCount + ToolHeavyCount;

        public required string       Root         { get; init; }
        public required string       Store        { get; init; }
        public required string       Config       { get; init; }
        public required List<string> ProjectIds   { get; init; }
        public required List<string> ProjectRoots { get; init; }
        public required List<string> ShortIds     { get; init; }
        public required List<string> LongIds      { get; init; }

        private DesktopSessionFactory Factory => new((_, _) => new ScriptedClient(), new FixtureCredentials(),
                                                     Config);

        public MainWindowViewModel Window() => new(Factory, Config, action =>
        {
            action();
            return ValueTask.CompletedTask;
        });

        /// <summary>
        ///     <para>生成全部负载：3000 短会话（2900 绑定＋100 显式无项目）、20 长会话、10 工具密集会话，随后建索引。</para>
        ///     <para>
        ///         Builds the whole load: 3000 short sessions (2900 bound plus 100 explicitly no-project), 20 long
        ///         sessions, and 10 tool-heavy sessions, then builds the index.
        ///     </para>
        /// </summary>
        public static async Task<SidebarFixture> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(),
                                    "tinyharness-sidebar-bench-" + Guid.NewGuid().ToString("N"));
            var store      = Path.Combine(root, "sessions");
            var workspaces = Path.Combine(root, "workspaces");
            var config     = Path.Combine(root, "user.json");
            Directory.CreateDirectory(store);
            Directory.CreateDirectory(workspaces);

            var projectIds   = new List<string>(ProjectCount);
            var projectRoots = new List<string>(ProjectCount);
            for (var index = 0; index < ProjectCount; index++)
            {
                var projectRoot = Path.Combine(workspaces, $"proj-{index:D3}");
                Directory.CreateDirectory(projectRoot);
                var project = await ProjectStore.GetOrCreateAsync(ProjectStore.FilePath(store), projectRoot, null,
                                                                  cancellationToken);
                projectIds.Add(project.Id);
                projectRoots.Add(projectRoot);
            }

            await UserConfigStore.SaveAsync(config, new UserConfig
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
                Settings = new UserConfigSettings { SessionStoreDirectory = store }
            }, cancellationToken);

            var shortIds      = new List<string>(ShortCount);
            var noProjectRoot = Path.Combine(workspaces, "scratch");
            Directory.CreateDirectory(noProjectRoot);
            for (var index = 0; index < ShortCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 前 2900 条按项目轮转绑定，最后 100 条显式无项目；正文均为单回合的最小合法日志。
                var noProject = index >= ShortCount - NoProjectCount;
                shortIds.Add(await WriteHostedSessionAsync(store,
                                                           noProject
                                                               ? noProjectRoot
                                                               : projectRoots[index % ProjectCount],
                                                           noProject ? null : projectIds[index % ProjectCount], 1,
                                                           false, index, cancellationToken));
            }

            var longIds = new List<string>(LongCount);
            for (var index = 0; index < LongCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                longIds.Add(await WriteHostedSessionAsync(store, projectRoots[index], projectIds[index], 60,
                                                          false, index, cancellationToken));
            }

            for (var index = 0; index < ToolHeavyCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WriteHostedSessionAsync(store, projectRoots[LongCount + index],
                                              projectIds[LongCount          + index], 30, true, index,
                                              cancellationToken);
            }

            var build = await new SessionIndexReconciler(new SessionIndexStore(store))
               .ReconcileAsync(cancellationToken);
            Assert.That(build.Rebuilt, Is.EqualTo(TotalSessions), "前置：索引首建必须重建全部会话条目。");
            Assert.That(Directory.GetFiles(store, "*" + SessionStoreWriter.LogFileSuffix).Length,
                        Is.EqualTo(TotalSessions), "前置：store 必须含全部会话日志。");

            return new SidebarFixture
            {
                Root         = root,
                Store        = store,
                Config       = config,
                ProjectIds   = projectIds,
                ProjectRoots = projectRoots,
                ShortIds     = shortIds,
                LongIds      = longIds
            };
        }

        /// <summary>删除整个隔离负载目录；只删除本夹具的带 guid 前缀目录。</summary>
        public void Delete()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }

        /// <summary>
        ///     <para>经宿主提交一份会话日志；绑定或显式无项目，正文最小化，回合可含工具调用。</para>
        ///     <para>
        ///         Commits one session log through the host; bound or explicitly no-project, bodies stay minimal, and
        ///         turns may carry tool calls.
        ///     </para>
        /// </summary>
        public static async Task<string> WriteHostedSessionAsync(
            string            store, string workspaceRoot, string? projectId, int turnCount, bool toolCalls, int index,
            CancellationToken cancellationToken)
        {
            var session = new ChatSession(Guid.NewGuid().ToString("N"), workspaceRoot, Identity, null,
                                          projectId,
                                          projectId is null
                                              ? SessionProjectAssignmentState.NoProject
                                              : SessionProjectAssignmentState.Project);
            var       client = new ScriptedClient();
            using var writer = new SessionStoreWriter(store, session.Id);
            using var host = new ChatSessionHost(session, new ConversationContext(new ContextOptions()),
                                                 new AgentOptions
                                                 {
                                                     Model                     = "offline", MaxAgentSteps = 10,
                                                     DefaultToolTimeoutSeconds = 30
                                                 },
                                                 client, new ToolRegistry([]), "benchmark harness",
                                                 sessionWriter : writer);
            for (var turn = 0; turn < turnCount; turn++)
            {
                // 无审批方时工具调用走准备失败路径，仍产生工具事实与历史条目的字节量。
                if (toolCalls)
                    client.Enqueue(ToolCall("read_thing", $"{{\"path\":\"src/file_{index}_{turn}.cs\"}}"));
                client.Enqueue(Text("offline answer"));
                await host.SubmitTurnAsync($"session {index} turn {turn}: inspect the sources",
                                           cancellationToken);
            }

            return session.Id;
        }

        private static ChatStreamEvent[] Text(string content) =>
        [
            new() { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = content }
        ];

        private static ChatStreamEvent[] ToolCall(string name, string argumentsJson) =>
        [
            new()
            {
                Kind                   = ChatStreamEventKind.ToolCallDelta,
                ToolCallIndex          = 0,
                ToolCallId             = "call_1",
                ToolCallFunctionName   = name,
                ToolCallArgumentsDelta = argumentsJson
            }
        ];
    }

    /// <summary>脚本化假模型客户端：按队列逐次应答，驱动宿主的 Agent 主循环。</summary>
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

    private sealed class FixtureCredentials : ICredentialStore
    {
        public bool IsSupported => true;

        public string? Read(string targetName) => "offline-fixture-key";

        public void Save(string   targetName, string secret) => throw new NotSupportedException();
        public bool Delete(string targetName) => throw new NotSupportedException();
    }
}

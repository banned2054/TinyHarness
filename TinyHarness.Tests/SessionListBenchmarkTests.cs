using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         会话列表链路的隔离基准（Session 列表加载与 SQLite 索引计划阶段 A）：
///         用真实 writer 与宿主生成合成 store，测量现有 ListAsync、逐日志严格读取与归属整理轮的基线。
///     </para>
///     <para>
///         Isolated benchmark for the session-list chain (phase A of the session-list/SQLite-index plan): builds a
///         synthetic store with real writers and hosts, and measures the current ListAsync, per-log strict reads, and
///         organize rounds as the baseline.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         标记 Explicit 且带 benchmark 类别，常规测试运行不执行；负载写入 %TEMP% 隔离目录，
///         不触碰真实用户日志，不调用真实模型。结果同时输出到测试进度与负载目录下的 bench-report.json。
///     </para>
///     <para>
///         Marked Explicit with the benchmark category so normal test runs skip it; the load lives in an isolated
///         %TEMP% directory, never touching real user logs or calling a real model. Results go to the test progress
///         output and to bench-report.json inside the load directory.
///     </para>
/// </remarks>
[TestFixture]
[Category("benchmark")]
public class SessionListBenchmarkTests
{
    private const string ShortAnswer = "The quick brown fox jumps over the lazy dog near the riverbank.";

    private const string LongTurnSeed =
        "Analysis paragraph with moderately long content used to grow the log bytes for realistic paging costs. ";

    private static readonly SessionModelIdentity Identity =
        new("offline", "https://offline.invalid/v1", ChatApiKind.ChatCompletions);

    private static string FixtureRoot => Path.Combine(Path.GetTempPath(), "tinyharness-session-list-bench");

    /// <summary>
    ///     <para>生成或复用隔离合成 store：3000 短会话、20 长会话（60 回合）、10 工具密集会话（30 回合）。</para>
    ///     <para>
    ///         Builds or reuses the isolated synthetic store: 3000 short sessions, 20 long sessions (60 turns), and
    ///         10 tool-heavy sessions (30 turns).
    ///     </para>
    /// </summary>
    [Test]
    [Explicit]
    public async Task Fixture_BuildSyntheticStore_ReportsVolumes()
    {
        var (store, counts) = await EnsureFixtureAsync(CancellationToken.None);
        TestContext.Progress.WriteLine($"fixture store={store} " +
                                       $"short={counts.Short} long={counts.Long} toolHeavy={counts.ToolHeavy}");
        Assert.That(Directory.GetFiles(store, "*" + SessionStoreWriter.LogFileSuffix).Length,
                    Is.EqualTo(counts.Short       + counts.Long + counts.ToolHeavy));
    }

    /// <summary>
    ///     <para>测量当前基线：来源枚举、逐日志严格读取分布、ListAsync 总时长与分配、归属整理整轮。</para>
    ///     <para>
    ///         Measures the current baseline: source enumeration, per-log strict-read distribution, ListAsync wall
    ///         time and allocations, and one full organize round.
    ///     </para>
    /// </summary>
    [Test]
    [Explicit]
    public async Task Measure_CurrentListAndOrganize_Baseline()
    {
        // 强制重建：上一轮测量的整理会改写日志，冷基线必须从统一未绑定状态开始。
        var (store, _) = await RebuildFixtureAsync(CancellationToken.None);
        var report = new Dictionary<string, object>();

        // 来源枚举：只取文件名与长度，对应轻量准备之后的第一步。
        var enumerate = Stopwatch.StartNew();
        var logs = Directory.GetFiles(store, "*" + SessionStoreWriter.LogFileSuffix)
                            .Select(path => (Id : Path.GetFileName(path)[..^SessionStoreWriter.LogFileSuffix.Length],
                                             Bytes : new FileInfo(path).Length))
                            .ToArray();
        enumerate.Stop();
        report["enumerate_ms"] = Math.Round(enumerate.Elapsed.TotalMilliseconds, 1);
        report["log_count"]    = logs.Length;
        report["total_bytes"]  = logs.Sum(log => log.Bytes);

        // 逐日志严格读取：现有列表链路的主体成本。
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var perLog          = new List<double>(logs.Length);
        var readAll         = Stopwatch.StartNew();
        foreach (var log in logs)
        {
            var stopwatch = Stopwatch.StartNew();
            await SessionStoreReader.ReadAsync(store, log.Id, CancellationToken.None);
            stopwatch.Stop();
            perLog.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        readAll.Stop();
        perLog.Sort();
        report["readall_ms"]           = Math.Round(readAll.Elapsed.TotalMilliseconds, 1);
        report["readall_alloc_mb"]     = Math.Round((GC.GetTotalAllocatedBytes(true) - allocatedBefore) / 1048576.0, 1);
        report["read_p50_ms"]          = Percentile(perLog, 0.50);
        report["read_p95_ms"]          = Percentile(perLog, 0.95);
        report["read_max_ms"]          = Math.Round(perLog[^1], 3);
        // 现链路全部日志读取完成后才产出首行，首条可见时间即 ListAsync 总时长。
        report["first_row_visible_ms"] = Math.Round(readAll.Elapsed.TotalMilliseconds, 1);

        // ListAsync：Desktop 与 CLI 实际调用的入口。
        allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var listAsync = Stopwatch.StartNew();
        var entries   = await SessionStoreReader.ListAsync(store, CancellationToken.None);
        listAsync.Stop();
        report["listasync_ms"]       = Math.Round(listAsync.Elapsed.TotalMilliseconds, 1);
        report["listasync_alloc_mb"] = Math.Round((GC.GetTotalAllocatedBytes(true) - allocatedBefore) / 1048576.0, 1);
        report["listasync_entries"]  = entries.Count;

        // 归属整理整轮：100 一批直到没有剩余；夹具全部未绑定，是最差路径。
        var     organize   = Stopwatch.StartNew();
        var     rounds     = 0;
        var     boundTotal = 0;
        string? cursor     = null;
        while (true)
        {
            var result = await SessionProjectInitializer.InitializeAsync(store, cursor, 100,
                                                                         cancellationToken :
                                                                         CancellationToken.None);
            rounds++;
            boundTotal += result.Outcomes.Count(outcome => outcome.Status == SessionProjectBindingStatus.Bound);
            if (!result.HasMore) break;
            cursor = result.NextCursor;
        }

        organize.Stop();
        report["organize_ms"]     = Math.Round(organize.Elapsed.TotalMilliseconds, 1);
        report["organize_rounds"] = rounds;
        report["organize_bound"]  = boundTotal;

        var path = Path.Combine(FixtureRoot, "bench-report.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report), CancellationToken.None);
        foreach (var (key, value) in report)
            TestContext.Progress.WriteLine($"{key}={value}");
        TestContext.Progress.WriteLine($"report={path}");
    }

    /// <summary>
    ///     <para>
    ///         测量索引路径首次建库（计划阶段 E）：降级摘要扫描与建库对账各读一遍正文的成本分段计时单列，
    ///         首条渐进可见时间、分配与权威日志字节不变性一并记录。
    ///     </para>
    ///     <para>
    ///         Measures the index path's first build (plan phase E): the degraded summary scan and the build
    ///         reconciliation each read the bodies once, so their costs are timed separately along with first-row
    ///         progressive visibility, allocations, and authoritative-byte invariance.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         建库后紧跟同进程的冷（建库后首次查询）与热（重复查询）索引查询；真实 GUI 上屏另按
    ///         计划口径单独报告，不从本基准推定。
    ///     </para>
    ///     <para>
    ///         Immediately after the build, the same process performs a cold (first query after the build) and a
    ///         warm (repeated) index query; real GUI rendering is reported separately per the plan and never
    ///         inferred from this benchmark.
    ///     </para>
    /// </remarks>
    [Test]
    [Explicit]
    public async Task Measure_IndexFirstBuild_ColdStore_Progressive()
    {
        var (store, counts) = await RebuildFixtureAsync(CancellationToken.None);
        var report = new Dictionary<string, object>
        {
            ["log_count"]   = counts.Short + counts.Long + counts.ToolHeavy,
            ["total_bytes"] = LogBytes(store)
        };

        // 降级扫描（不落库）：首建时 Desktop 列表的先行呈现路径，与建库对账构成计划内双扫描。
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var scan            = Stopwatch.StartNew();
        var scanEntries     = await new SessionListProjection(store).ScanSummariesAsync(CancellationToken.None);
        scan.Stop();
        report["scan_ms"]       = Math.Round(scan.Elapsed.TotalMilliseconds, 1);
        report["scan_alloc_mb"] = Math.Round((GC.GetTotalAllocatedBytes(true) - allocatedBefore) / 1048576.0, 1);
        report["scan_healthy"]  = scanEntries.Count(entry => entry.Summary is not null);
        report["scan_failed"]   = scanEntries.Count(entry => entry.Summary is null);

        // 建库对账（含首条渐进回调）：首条可见时间即首个回调时刻。
        var projection     = new SessionListProjection(store);
        var firstCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCallback  = Stopwatch.StartNew();
        allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var reconcile = Stopwatch.StartNew();
        var build = await projection.ReconcileAsync(new SessionIndexReconcileOptions
        {
            SummaryCommitted = (_, _) =>
            {
                firstCallback.Stop();
                firstCommitted.TrySetResult();
                return Task.CompletedTask;
            }
        }, CancellationToken.None);
        reconcile.Stop();
        await firstCommitted.Task.WaitAsync(TimeSpan.FromMinutes(10), CancellationToken.None);
        report["first_commit_ms"] = Math.Round(firstCallback.Elapsed.TotalMilliseconds, 1);
        report["build_ms"]        = Math.Round(reconcile.Elapsed.TotalMilliseconds, 1);
        report["build_alloc_mb"]  = Math.Round((GC.GetTotalAllocatedBytes(true) - allocatedBefore) / 1048576.0, 1);
        report["build_rebuilt"]   = build.Rebuilt;
        report["build_failed"]    = build.MarkedFailed;
        report["build_notes"]     = build.Notes.Count;
        Assert.That(build.Rebuilt, Is.EqualTo(report["log_count"]), "首次建库必须重建全部条目。");
        Assert.That(LogBytes(store), Is.EqualTo(report["total_bytes"]), "建索引不得改动权威日志字节。");

        // 索引查询：冷为建库后首次查询，热为紧随的重复查询。
        allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var coldQuery     = Stopwatch.StartNew();
        var coldRows      = await projection.ListRowsAsync(CancellationToken.None);
        var coldSummaries = await projection.ListSummariesAsync(new SessionSummaryQuery(), CancellationToken.None);
        coldQuery.Stop();
        report["cold_query_ms"]       = Math.Round(coldQuery.Elapsed.TotalMilliseconds, 1);
        report["cold_query_alloc_mb"] = Math.Round((GC.GetTotalAllocatedBytes(true) - allocatedBefore) / 1048576.0, 1);
        report["cold_query_rows"]     = coldRows.Count;
        Assert.That(coldSummaries, Has.Count.EqualTo(report["log_count"]));

        allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var warmQuery = Stopwatch.StartNew();
        await projection.ListRowsAsync(CancellationToken.None);
        await projection.ListSummariesAsync(new SessionSummaryQuery(), CancellationToken.None);
        warmQuery.Stop();
        report["warm_query_ms"]       = Math.Round(warmQuery.Elapsed.TotalMilliseconds, 1);
        report["warm_query_alloc_mb"] = Math.Round((GC.GetTotalAllocatedBytes(true) - allocatedBefore) / 1048576.0, 1);
        report["peak_working_set_mb"] = Math.Round(Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0, 1);

        await WriteReportAsync("index-first-build-report.json", report, CancellationToken.None);
    }

    /// <summary>
    ///     <para>
    ///         测量有效索引下的启动与后台对账：来源未变时零正文扫描，索引查询与对账计时、分配一并记录。
    ///     </para>
    ///     <para>
    ///         Measures startup and background reconciliation against a valid index: zero body scans when sources
    ///         are unchanged, with index-query and reconciliation timings and allocations recorded.
    ///     </para>
    /// </summary>
    [Test]
    [Explicit]
    public async Task Measure_IndexWarm_UnchangedSources()
    {
        var (store, counts) = await EnsureFixtureAsync(CancellationToken.None);
        var logCount   = counts.Short + counts.Long + counts.ToolHeavy;
        var projection = new SessionListProjection(store);
        await EnsureIndexReadyAsync(projection, logCount, CancellationToken.None);
        var report = new Dictionary<string, object> { ["log_count"] = logCount, ["total_bytes"] = LogBytes(store) };

        var availability = await projection.GetAvailabilityAsync(CancellationToken.None);
        report["availability"] = availability.Status.ToString();

        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var firstQuery      = Stopwatch.StartNew();
        await projection.ListRowsAsync(CancellationToken.None);
        await projection.ListSummariesAsync(new SessionSummaryQuery(), CancellationToken.None);
        firstQuery.Stop();
        report["cold_query_ms"] = Math.Round(firstQuery.Elapsed.TotalMilliseconds, 1);

        allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var warmQuery = Stopwatch.StartNew();
        await projection.ListRowsAsync(CancellationToken.None);
        var summaries = await projection.ListSummariesAsync(new SessionSummaryQuery(), CancellationToken.None);
        warmQuery.Stop();
        report["warm_query_ms"]       = Math.Round(warmQuery.Elapsed.TotalMilliseconds, 1);
        report["warm_query_alloc_mb"] = Math.Round((GC.GetTotalAllocatedBytes(true) - allocatedBefore) / 1048576.0, 1);
        Assert.That(summaries, Has.Count.EqualTo(logCount));

        allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var reconcile = Stopwatch.StartNew();
        var report2   = await projection.ReconcileAsync(SessionIndexReconcileOptions.Default, CancellationToken.None);
        reconcile.Stop();
        report["background_reconcile_ms"] = Math.Round(reconcile.Elapsed.TotalMilliseconds, 1);
        report["background_reconcile_alloc_mb"] =
            Math.Round((GC.GetTotalAllocatedBytes(true) - allocatedBefore) / 1048576.0, 1);
        report["background_unchanged"]     = report2.Unchanged;
        report["background_rebuilt"]       = report2.Rebuilt;
        report["background_marked_failed"] = report2.MarkedFailed;
        Assert.That(report2.Rebuilt, Is.EqualTo(0), "有效索引下未变来源不得重扫。");
        Assert.That(report2.Unchanged, Is.EqualTo(logCount));

        await WriteReportAsync("index-warm-report.json", report, CancellationToken.None);
    }

    /// <summary>
    ///     <para>
    ///         测量少量变更：新增会话与归档切换后，对账只重建变化条目，默认列表成本不随归档增长。
    ///     </para>
    ///     <para>
    ///         Measures small changes: after new sessions and archive flips, reconciliation rebuilds only the
    ///         changed entries and the default view's cost never grows with archived history.
    ///     </para>
    /// </summary>
    [Test]
    [Explicit]
    public async Task Measure_IndexIncremental_NewSessionsAndArchive()
    {
        var (store, counts) = await EnsureFixtureAsync(CancellationToken.None);
        var logCount   = counts.Short + counts.Long + counts.ToolHeavy;
        var projection = new SessionListProjection(store);
        await EnsureIndexReadyAsync(projection, logCount, CancellationToken.None);

        var newIds = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var client = new FakeChatClient();
            client.Enqueue(FakeChatClient.Text(ShortAnswer));
            using var host = NewHost(WorkspaceRoot(), store, client);
            await host.SubmitTurnAsync($"incremental question {i}", CancellationToken.None);
            newIds.Add(host.Session.Id);
        }

        var rows = await projection.ListRowsAsync(CancellationToken.None);
        foreach (var row in rows.Take(2))
            await SessionStoreArchiveStore.SetArchivedAsync(store, row.Id, true, CancellationToken.None);

        var report = new Dictionary<string, object>();
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var reconcile = Stopwatch.StartNew();
        var result = await projection.ReconcileAsync(SessionIndexReconcileOptions.Default, CancellationToken.None);
        reconcile.Stop();
        report["incremental_reconcile_ms"] = Math.Round(reconcile.Elapsed.TotalMilliseconds, 1);
        report["incremental_alloc_mb"] = Math.Round((GC.GetTotalAllocatedBytes(true) - allocatedBefore) / 1048576.0, 1);
        report["incremental_rebuilt"] = result.Rebuilt;
        report["incremental_unchanged"] = result.Unchanged;
        Assert.That(result.Rebuilt, Is.EqualTo(3), "仅新增会话重建。");
        Assert.That(result.Unchanged, Is.EqualTo(logCount), "既有条目零正文扫描。");

        var allocated = GC.GetTotalAllocatedBytes(true);
        var query     = Stopwatch.StartNew();
        var defaultView = await projection.ListSummariesAsync(new SessionSummaryQuery { Archived = false },
                                                              CancellationToken.None);
        query.Stop();
        report["default_query_ms"]        = Math.Round(query.Elapsed.TotalMilliseconds, 1);
        report["default_query_alloc_mb"]  = Math.Round((GC.GetTotalAllocatedBytes(true) - allocated) / 1048576.0, 1);
        report["default_query_summaries"] = defaultView.Count;
        Assert.That(defaultView.Count, Is.EqualTo(logCount + 1), "3 份新增进入默认视图，2 份归档移出。");
        Assert.That(defaultView.Select(summary => summary.Id).Intersect(newIds), Is.EquivalentTo(newIds));

        await WriteReportAsync("index-incremental-report.json", report, CancellationToken.None);
    }

    /// <summary>
    ///     <para>确保索引可用；缺失时先完整建库，供各测量复用。</para>
    ///     <para>Ensures the index is available, building it wholly when missing, for reuse by each measure.</para>
    /// </summary>
    private static async Task EnsureIndexReadyAsync(
        SessionListProjection projection, int logCount, CancellationToken cancellationToken)
    {
        if ((await projection.GetAvailabilityAsync(cancellationToken)).Status
         == SessionIndexAvailabilityStatus.Available)
            return;
        var build = await projection.ReconcileAsync(SessionIndexReconcileOptions.Default, cancellationToken);
        Assert.That(build.Rebuilt, Is.EqualTo(logCount), "索引缺失时必须完整建库。");
    }

    /// <summary>
    ///     <para>把测量报告写入夹具目录并输出到测试进度。</para>
    ///     <para>Writes the measure report into the fixture directory and echoes it to the test progress.</para>
    /// </summary>
    private static async Task WriteReportAsync(
        string fileName, IReadOnlyDictionary<string, object> report, CancellationToken cancellationToken)
    {
        var path = Path.Combine(FixtureRoot, fileName);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report), cancellationToken);
        foreach (var (key, value) in report)
            TestContext.Progress.WriteLine($"{key}={value}");
        TestContext.Progress.WriteLine($"report={path}");
    }

    /// <summary>
    ///     <para>统计 store 内全部会话日志字节数，用于记录扫描量与权威字节不变性。</para>
    ///     <para>
    ///         Sums every session-log byte count in the store for scanned-volume recording and authoritative-byte
    ///         invariance.
    ///     </para>
    /// </summary>
    private static long LogBytes(string store) =>
        Directory.EnumerateFiles(store, "*" + SessionStoreWriter.LogFileSuffix)
                 .Where(File.Exists)
                 .Sum(path => new FileInfo(path).Length);

    private static double Percentile(IReadOnlyList<double> ordered, double fraction)
    {
        if (ordered.Count == 0) return 0;
        var index = Math.Clamp((int)Math.Ceiling(fraction * ordered.Count) - 1, 0, ordered.Count - 1);
        return Math.Round(ordered[index], 3);
    }

    /// <summary>
    ///     <para>确保合成 store 存在且完整；已有完整夹具时直接复用，不追加。</para>
    ///     <para>
    ///         Ensures the synthetic store exists and is complete; an existing complete fixture is reused without
    ///         appending.
    ///     </para>
    /// </summary>
    private static async Task<(string Store, (int Short, int Long, int ToolHeavy) Counts)> EnsureFixtureAsync(
        CancellationToken cancellationToken)
    {
        const int shortCount = 3000;
        const int longCount  = 20;
        const int toolCount  = 10;
        var       root       = FixtureRoot;
        Directory.CreateDirectory(root);
        // 工作区根必须是 store 之外的独立目录，否则受保护边界校验会把归属整理判为失败。
        var store      = Path.Combine(root, "store");
        var workspaces = Path.Combine(root, "workspaces");
        Directory.CreateDirectory(workspaces);
        var markerPath = Path.Combine(root, "fixture-complete.json");
        if (File.Exists(markerPath) && Directory.Exists(store))
        {
            var counts = JsonSerializer.Deserialize<int[]>(await File.ReadAllTextAsync(markerPath, cancellationToken))!;
            return (store, (counts[0], counts[1], counts[2]));
        }

        if (Directory.Exists(store)) Directory.Delete(store, true);
        Directory.CreateDirectory(store);

        for (var i = 0; i < shortCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CommitSingleTurnAsync(store, WorkspaceRoot(),
                                        $"short question {i} about the workspace", ShortAnswer,
                                        cancellationToken);
        }

        // 预生成约 2KB 的长回答，避免每个回合重复构造字符串。
        var builder = new StringBuilder(2048);
        while (builder.Length < 2048) builder.Append(LongTurnSeed);
        var longAnswer = builder.ToString();

        for (var i = 0; i < longCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var       client = new FakeChatClient();
            using var host   = NewHost(WorkspaceRoot(), store, client);
            for (var turn = 0; turn < 60; turn++)
            {
                client.Enqueue(FakeChatClient.Text(longAnswer));
                await host.SubmitTurnAsync($"long session {i} turn {turn}: {LongTurnSeed}", cancellationToken);
            }
        }

        for (var i = 0; i < toolCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var       client = new FakeChatClient();
            using var host   = NewHost(WorkspaceRoot(), store, client);
            for (var turn = 0; turn < 30; turn++)
            {
                // 工具调用在无审批方时走准备失败路径，仍产生工具事实与历史条目的字节量。
                client.Enqueue(FakeChatClient.ToolCall("read_thing", $"{{\"path\":\"src/file_{i}_{turn}.cs\"}}"));
                client.Enqueue(FakeChatClient.Text(ShortAnswer));
                await host.SubmitTurnAsync($"tool session {i} turn {turn}: inspect the sources", cancellationToken);
            }
        }

        await File.WriteAllTextAsync(markerPath,
                                     JsonSerializer.Serialize(new[] { shortCount, longCount, toolCount }),
                                     cancellationToken);
        return (store, (shortCount, longCount, toolCount));
    }

    /// <summary>
    ///     <para>删除夹具标记与 store 后重建，保证每次测量从统一未绑定状态开始。</para>
    ///     <para>
    ///         Deletes the fixture marker and store before rebuilding so every measurement starts from one uniform
    ///         unbound state.
    ///     </para>
    /// </summary>
    private static async Task<(string Store, (int Short, int Long, int ToolHeavy) Counts)> RebuildFixtureAsync(
        CancellationToken cancellationToken)
    {
        var markerPath = Path.Combine(FixtureRoot, "fixture-complete.json");
        if (File.Exists(markerPath)) File.Delete(markerPath);
        var store = Path.Combine(FixtureRoot, "store");
        if (Directory.Exists(store)) Directory.Delete(store, true);
        return await EnsureFixtureAsync(cancellationToken);
    }

    /// <summary>
    ///     <para>夹具共用的执行目录；位于 store 之外，满足受保护边界。</para>
    ///     <para>The fixture's shared workspace root; outside the store, satisfying the protected boundary.</para>
    /// </summary>
    private static string WorkspaceRoot() => Path.Combine(FixtureRoot, "workspaces");

    /// <summary>
    ///     <para>提交一个单回合会话；对应真实常见日志的规模。</para>
    ///     <para>Commits a single-turn session; matching the size of typical real logs.</para>
    /// </summary>
    private static async Task CommitSingleTurnAsync(
        string store, string workspaceRoot, string input, string answer, CancellationToken cancellationToken)
    {
        var       client = new FakeChatClient();
        using var host   = NewHost(workspaceRoot, store, client);
        client.Enqueue(FakeChatClient.Text(answer));
        await host.SubmitTurnAsync(input, cancellationToken);
    }

    private static ChatSessionHost NewHost(string workspaceRoot, string store, FakeChatClient client) =>
        new(workspaceRoot,
            Identity,
            new AgentOptions { Model = "offline", MaxAgentSteps = 10, DefaultToolTimeoutSeconds = 30 },
            client,
            new ToolRegistry([]),
            "You are a benchmark harness.",
            sessionWriterFactory : id => new SessionStoreWriter(store, id));
}

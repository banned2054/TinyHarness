using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Configuration;
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
        TestContext.Progress.WriteLine(
                                       $"fixture store={store} short={counts.Short} long={counts.Long} toolHeavy={counts.ToolHeavy}");
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
        // Force a rebuild: the previous round's organize mutates logs, so a cold baseline must start from one
        // uniform unbound state.
        var (store, _) = await RebuildFixtureAsync(CancellationToken.None);
        var report = new Dictionary<string, object>();

        // 来源枚举：只取文件名与长度，对应轻量准备之后的第一步。
        // Source enumeration: file names and lengths only, the first step after light preparation.
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
        // Per-log strict reads: the dominant cost of the current list chain.
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
        // The current chain emits the first row only after every log is read, so first-row visibility equals the
        // ListAsync total.
        report["first_row_visible_ms"] = Math.Round(readAll.Elapsed.TotalMilliseconds, 1);

        // ListAsync：Desktop 与 CLI 实际调用的入口。
        // ListAsync: the entry point Desktop and CLI actually call.
        allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var listAsync = Stopwatch.StartNew();
        var entries   = await SessionStoreReader.ListAsync(store, CancellationToken.None);
        listAsync.Stop();
        report["listasync_ms"]       = Math.Round(listAsync.Elapsed.TotalMilliseconds, 1);
        report["listasync_alloc_mb"] = Math.Round((GC.GetTotalAllocatedBytes(true) - allocatedBefore) / 1048576.0, 1);
        report["listasync_entries"]  = entries.Count;

        // 归属整理整轮：100 一批直到没有剩余；夹具全部未绑定，是最差路径。
        // A full organize round: batches of 100 until nothing remains; the fixture starts fully unbound, the worst
        // path.
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
        // The workspace root must be a separate directory outside the store, or the protected-boundary check fails
        // every organize outcome.
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
        // Prebuild a ~2KB long answer so each turn does not rebuild the string.
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
                // Tool calls without an approver take the preparation-failure path yet still emit tool facts and
                // history-item bytes.
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
    private static async Task CommitSingleTurnAsync(string store, string workspaceRoot, string input, string answer,
                                                    CancellationToken cancellationToken)
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

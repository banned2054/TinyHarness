using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.IndexProbe;

/// <summary>
///     <para>
///         会话索引探针：以独立进程操作单个 store 的列表索引，供跨进程竞争测试与仓库外原生产物
///         smoke 使用。全部模式只触碰调用方给定的隔离目录，不读取或改写真实用户数据。
///     </para>
///     <para>
///         The session index probe: a separate process operating one store's list index for cross-process
///         contention tests and out-of-repo native-artifact smokes. Every mode touches only the isolated
///         directory supplied by the caller and never reads or rewrites real user data.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         模式契约（输出均为单行，进程退出码 0 表示成功）：
///         <c>create-log &lt;store&gt; &lt;count&gt;</c> 写入 count 份仅含会话身份批次的最小合法日志；
///         <c>append-log &lt;store&gt; &lt;sessionId&gt;</c> 向尚未归属的既有日志追加一条无项目归属记录；
///         <c>reconcile &lt;store&gt;</c> 执行一轮对账并打印计数；
///         <c>list &lt;store&gt;</c> 打印可用性、行数与默认视图摘要数；
///         <c>write-summary &lt;store&gt; &lt;sessionId&gt; &lt;sequence&gt; &lt;bytes&gt; &lt;title&gt;</c>
///         条件提交一条更新并打印是否应用；
///         <c>hold-lock &lt;store&gt; &lt;ms&gt;</c> 独占锁住索引数据库指定时长；
///         <c>corrupt-index &lt;store&gt;</c> 把索引数据库文件覆写为垃圾字节。
///     </para>
///     <para>
///         Mode contract (single-line output, exit code 0 on success):
///         <c>create-log &lt;store&gt; &lt;count&gt;</c> writes count minimal valid logs holding only the
///         session-identity batch; <c>append-log &lt;store&gt; &lt;sessionId&gt;</c> appends one no-project
///         assignment record to an unassigned existing log; <c>reconcile &lt;store&gt;</c> runs one reconciliation round
///         and prints counts;
///         <c>list &lt;store&gt;</c> prints availability, row count, and default-view summary count;
///         <c>write-summary &lt;store&gt; &lt;sessionId&gt; &lt;sequence&gt; &lt;bytes&gt; &lt;title&gt;</c>
///         conditionally commits one update and prints whether it applied;
///         <c>hold-lock &lt;store&gt; &lt;ms&gt;</c> holds an exclusive lock on the index database for the given
///         duration; <c>corrupt-index &lt;store&gt;</c> overwrites the index database file with garbage bytes.
///     </para>
/// </remarks>
internal static class Program
{
    /// <summary>
    ///     <para>按模式分发执行；未知模式与非法参数以非零退出码失败并写 stderr。</para>
    ///     <para>
    ///         Dispatches by mode; unknown modes and invalid arguments fail with a non-zero exit code and a
    ///         stderr message.
    ///     </para>
    /// </summary>
    /// <param name="args">
    ///     <para>模式名及其参数；首个参数为模式名。</para>
    ///     <para>The mode name followed by its arguments; the first argument selects the mode.</para>
    /// </param>
    /// <returns>
    ///     <para>成功返回 0；任何失败返回 1。</para>
    ///     <para>Zero on success; one on any failure.</para>
    /// </returns>
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new ArgumentException("A probe mode is required.");
            // 各模式参数个数固定；缺失时报用法错误而非 IndexOutOfRangeException。未知模式仍由下方分发报错。
            var requiredArgCount = args[0] switch
            {
                "create-log" or "append-log" or "hold-lock" => 3,
                "write-summary"                             => 6,
                "reconcile" or "list" or "corrupt-index"    => 2,
                _                                           => 1
            };
            if (args.Length < requiredArgCount)
                throw new ArgumentException($"Mode '{args[0]}' requires {requiredArgCount} arguments.");
            return args[0] switch
            {
                "create-log" => await CreateLogAsync(args[1], ParsePositive(args, 2, "count")),
                "append-log" => await AppendLogAsync(args[1], args[2]),
                "reconcile"  => await ReconcileAsync(args[1]),
                "list"       => await ListAsync(args[1]),
                "write-summary" => await WriteSummaryAsync(args[1], args[2], args[3], args[4],
                                                           string.Join(' ', args.Skip(5))),
                "hold-lock"     => await HoldLockAsync(args[1], ParsePositive(args, 2, "milliseconds")),
                "corrupt-index" => CorruptIndex(args[1]),
                _               => throw new ArgumentException($"Unknown probe mode '{args[0]}'.")
            };
        }
        catch (Exception ex)
        {
            // 只写完整异常一次，供测试断言输出诊断；不再附加单行摘要。
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static int ParsePositive(string[] args, int index, string name)
    {
        if (args.Length <= index || !int.TryParse(args[index], out var value) || value <= 0)
            throw new ArgumentException($"A positive integer {name} is required at position {index}.");
        return value;
    }

    /// <summary>
    ///     <para>写入 count 份仅含会话身份批次的最小合法日志，不含对话与运行事实，不调用任何模型。</para>
    ///     <para>
    ///         Writes count minimal valid logs holding only the session-identity batch — no conversation or run
    ///         facts, and no model invocation.
    ///     </para>
    /// </summary>
    private static async Task<int> CreateLogAsync(string store, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var sessionId = RandomSessionId();
            var session = new ChatSession(sessionId, "probe-workspace",
                                          new SessionModelIdentity("offline", "https://offline.invalid/v1",
                                                                   ChatApiKind.ChatCompletions));
            using var writer = new SessionStoreWriter(store, sessionId);
            await writer.CommitAsync(session, new ConversationContext(new ContextOptions()), CancellationToken.None)
                        .ConfigureAwait(false);
        }

        Console.Out.WriteLine($"created={count}");
        return 0;
    }

    /// <summary>
    ///     <para>
    ///         向尚未归属的既有日志追加一条无项目归属记录，使来源长度与修改时间变化，触发索引重建判断。
    ///     </para>
    ///     <para>
    ///         Appends one no-project assignment record to an unassigned existing log, changing the source length
    ///         and modification time to trigger the index rebuild decision.
    ///     </para>
    /// </summary>
    private static async Task<int> AppendLogAsync(string store, string sessionId)
    {
        using var writer =
            await SessionStoreWriter.OpenExistingAsync(store, sessionId, null, CancellationToken.None)
                                    .ConfigureAwait(false);
        await writer.AppendRecordsAsync([AssignmentRecord()], CancellationToken.None).ConfigureAwait(false);
        Console.Out.WriteLine("appended=1");
        return 0;
    }

    /// <summary>
    ///     <para>执行一轮对账并打印可用性与计数报告。</para>
    ///     <para>Runs one reconciliation round and prints availability plus the count report.</para>
    /// </summary>
    private static async Task<int> ReconcileAsync(string store)
    {
        var projection = new SessionListProjection(store);
        var report = await projection.ReconcileAsync(SessionIndexReconcileOptions.Default,
                                                     CancellationToken.None).ConfigureAwait(false);
        Console.Out.WriteLine($"availability={report.Availability.Status} sources={report.SourceCount} " +
                              $"rebuilt={report.Rebuilt} removed={report.Removed} " +
                              $"markedFailed={report.MarkedFailed} archiveSynced={report.ArchiveSynced}");
        return 0;
    }

    /// <summary>
    ///     <para>打印索引可用性、总行数与默认视图健康摘要数。</para>
    ///     <para>Prints index availability, total row count, and default-view healthy summary count.</para>
    /// </summary>
    private static async Task<int> ListAsync(string store)
    {
        var projection       = new SessionListProjection(store);
        var availability     = await projection.GetAvailabilityAsync(CancellationToken.None).ConfigureAwait(false);
        var rows             = 0;
        var defaultSummaries = 0;
        if (availability.Status == SessionIndexAvailabilityStatus.Available)
        {
            rows = (await projection.ListRowsAsync(CancellationToken.None).ConfigureAwait(false)).Count;
            defaultSummaries = (await projection
                .ListSummariesAsync(new SessionSummaryQuery { Archived = false }, CancellationToken.None)
                .ConfigureAwait(false)).Count;
        }

        Console.Out.WriteLine($"availability={availability.Status} "                         +
                              (availability.Detail is { } detail ? $"detail={detail} " : "") +
                              $"rows={rows} defaultSummaries={defaultSummaries}");
        return 0;
    }

    /// <summary>
    ///     <para>按给定序号与字节条件提交一条更新；健康行只接受更新快照，被拒时不报错。</para>
    ///     <para>
    ///         Conditionally commits one update with the given sequence and bytes; healthy rows accept only fresher
    ///         snapshots, and rejection is not an error.
    ///     </para>
    /// </summary>
    private static async Task<int> WriteSummaryAsync(
        string store, string sessionId, string sequenceText, string bytesText, string title)
    {
        if (!long.TryParse(sequenceText, out var sequence) || sequence < 1)
            throw new ArgumentException("A positive commit sequence is required.");
        if (!long.TryParse(bytesText, out var bytes) || bytes < 0)
            throw new ArgumentException("A non-negative byte count is required.");

        var logPath    = Path.Combine(store, sessionId.ToLowerInvariant() + SessionStoreWriter.LogFileSuffix);
        var sourceInfo = new FileInfo(logPath);
        var summary = new SessionSummary
        {
            Id                     = sessionId.ToLowerInvariant(),
            WorkspaceRoot          = "probe-workspace",
            ProjectAssignmentState = SessionProjectAssignmentState.NoProject,
            Title                  = title,
            CreatedUtc             = TruncateToMilliseconds(DateTimeOffset.UtcNow),
            UpdatedUtc             = TruncateToMilliseconds(DateTimeOffset.UtcNow),
            LastActivityUtc        = TruncateToMilliseconds(DateTimeOffset.UtcNow),
            TurnCount              = 1,
            CommitSequence         = sequence,
            IndexedBytes           = bytes
        };
        var index = new SessionIndexStore(store);
        var source = sourceInfo.Exists
            ? (sourceInfo.Length, (DateTimeOffset?)new DateTimeOffset(sourceInfo.LastWriteTimeUtc))
            : (0L, null);
        var applied = await index.TryAdvanceFromCommitAsync(summary, source.Item1, source.Item2,
                                                            CancellationToken.None)
                                 .ConfigureAwait(false);
        Console.Out.WriteLine($"applied={(applied ? 1 : 0)}");
        return 0;
    }

    /// <summary>
    ///     <para>对索引数据库取得独占锁并持有指定毫秒，用于验证等待有界与业务提交不被阻塞。</para>
    ///     <para>
    ///         Takes an exclusive lock on the index database for the given duration, proving bounded waits and
    ///         unblocked business commits.
    ///     </para>
    /// </summary>
    private static async Task<int> HoldLockAsync(string store, int milliseconds)
    {
        var databasePath = SessionIndexStore.PathFor(store);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode       = SqliteOpenMode.ReadWriteCreate,
            Pooling    = false
        }.ToString());
        await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
        // 不使用 DbTransaction 对象：显式 BEGIN EXCLUSIVE 由命令直接执行，持锁至连接关闭。
        var begin = connection.CreateCommand();
        begin.CommandText = "PRAGMA busy_timeout=500; BEGIN EXCLUSIVE";
        await begin.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        Console.Out.WriteLine("locked");
        Console.Out.Flush();
        await Task.Delay(milliseconds).ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
        Console.Out.WriteLine("released");
        return 0;
    }

    /// <summary>
    ///     <para>把索引数据库覆写为垃圾字节，制造损坏索引供降级与恢复路径验证。</para>
    ///     <para>
    ///         Overwrites the index database with garbage bytes, producing a damaged index for degraded-path and
    ///         recovery verification.
    ///     </para>
    /// </summary>
    private static int CorruptIndex(string store)
    {
        var databasePath = SessionIndexStore.PathFor(store);
        File.WriteAllText(databasePath, "this is not a sqlite database");
        Console.Out.WriteLine("corrupted=1");
        return 0;
    }

    private static SessionLogRecord AssignmentRecord() =>
        new(string.Empty, "project-assignment",
            JsonSerializer.SerializeToElement(new SessionLogProjectAssignment(
                SessionLogProjectAssignment.SchemaName, SessionLogProjectAssignment.NoProject),
                SessionLogJsonContext.Default.SessionLogProjectAssignment));

    private static string RandomSessionId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value) =>
        new(value.UtcDateTime.Ticks - value.UtcDateTime.Ticks % TimeSpan.TicksPerMillisecond,
            TimeSpan.Zero);
}

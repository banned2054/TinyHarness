using System.Diagnostics;
using System.Text.Json;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         以真实子进程（TinyHarness.IndexProbe）验证索引跨进程一致性：子进程新提交不被父进程旧快照
///         回退、提交经 data_version 跨进程可见、独占锁下写等待有界而读者与业务提交保持可用。
///     </para>
///     <para>
///         Verifies index cross-process consistency with a real child process (TinyHarness.IndexProbe): child
///         commits never regress to parent stale snapshots, commits surface through data_version, and under an
///         exclusive lock writes wait bounded while readers and business commits stay available.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         探针可执行文件先复制到隔离测试目录再启动，避免直接运行仓库内构建产物；
///         夹具仅位于 %TEMP%，不触碰真实用户数据。
///     </para>
///     <para>
///         The probe executable is copied into an isolated test directory before launch rather than run from the
///         repository build output; fixtures live only under %TEMP% and never touch real user data.
///     </para>
/// </remarks>
public class SessionIndexCrossProcessTests
{
    private const string ProbeExeName = "TinyHarness.IndexProbe.exe";

    private static readonly SessionModelIdentity Identity =
        new("offline", "https://offline.invalid/v1", ChatApiKind.ChatCompletions);

    /// <summary>
    ///     <para>子进程以更大序号条件提交后，父进程旧快照的推进与重建写入均被拒绝，且经 data_version 可见。</para>
    ///     <para>
    ///         After the child conditionally commits a larger sequence, both the parent's stale advance and
    ///         rebuild writes are rejected, and the commit surfaces through data_version.
    ///     </para>
    /// </summary>
    [Test]
    public async Task CrossProcess_ChildFresherCommit_IsNotOverwrittenByStaleParentWrites()
    {
        using var dir   = new TestTempDir();
        var       store = Path.Combine(dir.Root, "store");
        Directory.CreateDirectory(store);
        var sessionId = await SeedPendingSessionAsync(store, Path.Combine(dir.Root, "workspace"));
        var index     = new SessionIndexStore(store);
        await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        var committed = await index.GetRowAsync(sessionId, CancellationToken.None);
        Assert.That(committed!.State, Is.EqualTo(SessionIndexEntryState.Current));
        Assert.That(committed.Summary, Is.Not.Null);

        await using var watcher = await index.OpenVersionWatcherAsync(CancellationToken.None);
        var             before  = await watcher.ReadAsync(CancellationToken.None);

        var (exit, output) = await RunProbeAsync(dir.Root, "write-summary", store, sessionId,
                                                 (committed.Summary!.CommitSequence + 5).ToString(),
                                                 (committed.Summary.IndexedBytes    + 64).ToString(),
                                                 "child fresher commit");
        Assert.That(exit, Is.EqualTo(0), output);
        Assert.That(output, Does.Contain("applied=1"));

        Assert.That(await watcher.ReadAsync(CancellationToken.None), Is.Not.EqualTo(before),
                    "跨进程提交必须通过 data_version 可见。");

        // 父进程以子进程写入前的旧快照重放提交推进与重建写入，均不得回退新快照。
        Assert.That(await index.TryAdvanceFromCommitAsync(committed.Summary, committed.SourceLength,
                                                          committed.SourceModifiedUtc, CancellationToken.None),
                    Is.False, "旧序号的提交推进必须被拒绝。");
        await index.WriteSummaryAsync(committed.Summary, committed.SourceLength, committed.SourceModifiedUtc,
                                      CancellationToken.None);
        var row = await index.GetRowAsync(sessionId, CancellationToken.None);
        Assert.That(row!.Summary!.Title, Is.EqualTo("child fresher commit"));
        Assert.That(row.Summary.CommitSequence, Is.EqualTo(committed.Summary.CommitSequence + 5));
        Assert.That(row.State, Is.EqualTo(SessionIndexEntryState.Current));
    }

    /// <summary>
    ///     <para>
    ///         子进程独占锁住索引期间：索引写等待有界并以异常收场，writer 业务提交不受阻塞且失败留痕，
    ///         子进程退出后对账追平。
    ///     </para>
    ///     <para>
    ///         While the child holds an exclusive index lock: index writes wait bounded and end in an exception,
    ///         writer business commits stay unblocked with the failure recorded, and reconciliation catches up
    ///         after the child exits.
    ///     </para>
    /// </summary>
    [Test]
    public async Task CrossProcess_ExclusiveChildLock_BoundsIndexWrites_WhileBusinessCommitStaysHealthy()
    {
        using var dir   = new TestTempDir();
        var       store = Path.Combine(dir.Root, "store");
        Directory.CreateDirectory(store);
        var sessionId = await SeedPendingSessionAsync(store, Path.Combine(dir.Root, "workspace"));
        var index     = new SessionIndexStore(store);
        await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        var row         = await index.GetRowAsync(sessionId, CancellationToken.None);
        var bytesBefore = new FileInfo(Path.Combine(store, sessionId + SessionStoreWriter.LogFileSuffix)).Length;

        Assert.That(row, Is.Not.Null);
        Assert.That(row!.Summary, Is.Not.Null);
        var stale = row.Summary!;

        var             updater = new SessionIndexUpdater(index);
        await using var probe   = await StartProbeAsync(dir.Root, "hold-lock", store, "15000");
        try
        {
            await probe.Locked.WaitAsync(TimeSpan.FromSeconds(10));

            var commitWatch = Stopwatch.StartNew();
            using (var writer =
                   await SessionStoreWriter.OpenExistingAsync(store, sessionId, null, CancellationToken.None,
                                                              updater.HandleBatchCommittedAsync))
            {
                await writer.AppendRecordsAsync([NoProjectAssignmentRecord()], CancellationToken.None);
            }

            commitWatch.Stop();
            Assert.That(new FileInfo(Path.Combine(store, sessionId + SessionStoreWriter.LogFileSuffix)).Length,
                        Is.GreaterThan(bytesBefore), "业务提交不得因索引锁失败或长期阻塞。");
            Assert.That(updater.LastFailure, Is.Not.Null, "持锁窗口内的索引推进失败必须留痕。");
            Assert.That(commitWatch.Elapsed.TotalSeconds, Is.LessThan(30), "业务提交的索引等待必须有界。");

            var writeWatch = Stopwatch.StartNew();
            Assert.That(async () => await index.WriteSummaryAsync(stale, row.SourceLength, row.SourceModifiedUtc,
                                                                  CancellationToken.None),
                        Throws.TypeOf<SessionIndexException>());
            writeWatch.Stop();
            Assert.That(writeWatch.Elapsed.TotalSeconds, Is.LessThan(20), "索引写锁等待必须有界。");
        }
        finally
        {
            await probe.DisposeAsync();
        }

        var report = await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        Assert.That(report.Rebuilt, Is.EqualTo(1), "锁释放后对账追平持锁期间的变化。");
    }

    /// <summary>
    ///     <para>子进程独占写锁期间索引读者保持可用：可用性报告正常、行与健康摘要查询有界返回。</para>
    ///     <para>
    ///         While the child holds an exclusive write lock, index readers stay available: availability reports
    ///         healthy and row and healthy-summary queries return bounded.
    ///     </para>
    /// </summary>
    [Test]
    public async Task CrossProcess_ExclusiveChildLock_KeepsReaderAvailableAndQueriesServing()
    {
        using var dir   = new TestTempDir();
        var       store = Path.Combine(dir.Root, "store");
        Directory.CreateDirectory(store);
        await SeedPendingSessionAsync(store, Path.Combine(dir.Root, "workspace"));
        await SeedPendingSessionAsync(store, Path.Combine(dir.Root, "workspace"));
        var index = new SessionIndexStore(store);
        await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);

        await using var probe = await StartProbeAsync(dir.Root, "hold-lock", store, "8000");
        try
        {
            await probe.Locked.WaitAsync(TimeSpan.FromSeconds(10));
            var readWatch    = Stopwatch.StartNew();
            var availability = await index.GetAvailabilityAsync(CancellationToken.None);
            Assert.That(availability.Status, Is.EqualTo(SessionIndexAvailabilityStatus.Available),
                        "WAL 读取不得因独占写锁受阻。");
            var rows      = await index.ListRowsAsync(CancellationToken.None);
            var summaries = await index.ListSummariesAsync(new SessionSummaryQuery(), CancellationToken.None);
            readWatch.Stop();
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(summaries, Has.Count.EqualTo(2));
            Assert.That(readWatch.Elapsed.TotalSeconds, Is.LessThan(15), "持锁期间的读者等待必须有界。");
        }
        finally
        {
            await probe.DisposeAsync();
        }
    }

    /// <summary>
    ///     <para>向 store 提交一份仅有会话身份批次的合法日志；归属保持待初始化。</para>
    ///     <para>
    ///         Commits one valid log holding only the session-identity batch into the store; ownership stays
    ///         pending initialization.
    ///     </para>
    /// </summary>
    private static async Task<string> SeedPendingSessionAsync(string store, string workspaceRoot)
    {
        var       sessionId = Guid.NewGuid().ToString("N");
        var       session   = new ChatSession(sessionId, workspaceRoot, Identity);
        using var writer    = new SessionStoreWriter(store, sessionId);
        await writer.CommitAsync(session, new ConversationContext(new ContextOptions()), CancellationToken.None);
        return sessionId;
    }

    /// <summary>
    ///     <para>构造一条显式无项目归属记录，模拟归属整理对待初始化会话的业务写入。</para>
    ///     <para>
    ///         Builds one explicit no-project assignment record, mimicking organize's business write to a
    ///         pending-initialization session.
    ///     </para>
    /// </summary>
    private static SessionLogRecord NoProjectAssignmentRecord() =>
        new(string.Empty, "project-assignment",
            JsonSerializer.SerializeToElement(new SessionLogProjectAssignment(
                SessionLogProjectAssignment.SchemaName, SessionLogProjectAssignment.NoProject),
                SessionLogJsonContext.Default.SessionLogProjectAssignment));

    /// <summary>
    ///     <para>启动探针并等待其锁定就绪信号；释放时终止仍在运行的进程。</para>
    ///     <para>
    ///         Starts the probe and waits for its lock-ready signal; disposal terminates a still-running process.
    ///     </para>
    /// </summary>
    private static async Task<ProbeProcess> StartProbeAsync(string root, params string[] arguments)
    {
        var process = StartProbeProcess(root, arguments);
        var locked  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = PumpLinesAsync(process.StandardOutput, locked);
        return new ProbeProcess(process, locked);
    }

    /// <summary>
    ///     <para>启动探针并等待退出，返回退出码与标准输出全文。</para>
    ///     <para>Starts the probe and waits for exit, returning the exit code and full standard output.</para>
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunProbeAsync(string root, params string[] arguments)
    {
        using var process    = StartProbeProcess(root, arguments);
        var       outputTask = process.StandardOutput.ReadToEndAsync();
        var       errorTask  = process.StandardError.ReadToEndAsync();
        using var timeout    = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw;
        }

        return (process.ExitCode, await outputTask.ConfigureAwait(false) +
                                  await errorTask.ConfigureAwait(false));
    }

    private static Process StartProbeProcess(string root, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName               = CopyProbeExecutable(root),
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = root
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Probe process failed to start.");
    }

    private static async Task PumpLinesAsync(StreamReader standardOutput, TaskCompletionSource locked)
    {
        while (await standardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            if (line.Trim() == "locked")
                locked.TrySetResult();
    }

    /// <summary>
    ///     <para>把探针产物整体复制到隔离目录并返回可执行文件路径；缺失时以断言失败报告。</para>
    ///     <para>
    ///         Copies the whole probe output into the isolated directory and returns the executable path; a
    ///         missing build fails the assertion.
    ///     </para>
    /// </summary>
    private static string CopyProbeExecutable(string root)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "TinyHarness.slnx")))
            current = current.Parent;
        Assert.That(current, Is.Not.Null, "未找到仓库根 TinyHarness.slnx。");
        var probeBin = Path.Combine(current!.FullName, "TinyHarness.IndexProbe", "bin");
        Assert.That(Directory.Exists(probeBin), Is.True, "TinyHarness.IndexProbe 尚未构建（缺少 bin 输出）。");
        var probeDirectory = ResolveProbeOutputDirectory(probeBin);

        var target = Path.Combine(root, "probe");
        Directory.CreateDirectory(target);
        // 整树复制：原生 e_sqlite3 位于 runtimes/ 子树，平铺复制会丢掉原生加载路径。
        foreach (var file in Directory.GetFiles(probeDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(probeDirectory, file);
            if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || relative.Contains("browser-wasm"))
                continue;
            var targetPath = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, true);
        }

        return Path.Combine(target, ProbeExeName);
    }

    /// <summary>
    ///     <para>优先选取与测试进程相同配置的探针输出目录；未命中时回退为最新修改时间的产物目录。</para>
    ///     <para>
    ///         Prefers the probe output directory matching the test process's own configuration, falling back to
    ///         the most recently modified one when that misses.
    ///     </para>
    /// </summary>
    private static string ResolveProbeOutputDirectory(string probeBin)
    {
        // 测试自身输出形如 ...\bin\{配置}\{目标框架}\；同配置探针产物优先，Debug/Release 并存时不选错。
        var segments = AppContext.BaseDirectory.Split(Path.DirectorySeparatorChar,
                                                      Path.AltDirectorySeparatorChar);
        var binIndex = Array.LastIndexOf(segments, "bin");
        if (binIndex >= 0 && binIndex + 2 < segments.Length)
        {
            var sameConfiguration = Path.Combine(probeBin, segments[binIndex + 1], segments[binIndex + 2],
                                                 ProbeExeName);
            if (File.Exists(sameConfiguration))
                return Path.GetDirectoryName(sameConfiguration)!;
        }

        var newest = Directory.EnumerateFiles(probeBin, ProbeExeName, SearchOption.AllDirectories)
                              .Where(path =>
                                  !path.Contains($"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}"))
                              .OrderByDescending(File.GetLastWriteTimeUtc)
                              .FirstOrDefault();
        Assert.That(newest, Is.Not.Null, "TinyHarness.IndexProbe bin 中没有探针可执行文件。");
        return Path.GetDirectoryName(newest)!;
    }

    /// <summary>
    ///     <para>探针运行句柄：锁定就绪信号与退出等待；释放时终止仍在运行的进程。</para>
    ///     <para>
    ///         A running-probe handle: the lock-ready signal plus exit waiting; disposal terminates a still-running
    ///         process.
    ///     </para>
    /// </summary>
    private sealed class ProbeProcess : IAsyncDisposable
    {
        private readonly Process _process;

        public ProbeProcess(Process process, TaskCompletionSource locked)
        {
            _process = process;
            Locked   = locked.Task;
        }

        /// <summary>
        ///     <para>探针输出 locked 单行后完成的任务。</para>
        ///     <para>The task completing once the probe prints its locked line.</para>
        /// </summary>
        public Task Locked { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(true);
                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException)
            {
                // 进程尚未启动或已退出：无需终止。
            }
            finally
            {
                _process.Dispose();
            }
        }
    }
}

using System.Diagnostics;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         仅 Debug 构建生效的沙箱诊断文件日志：每进程一个文件，由后台任务异步追加，
///         任何写入失败静默停用；Release 构建中调用点整体消失，不产生日志或副作用。
///     </para>
///     <para>
///         A sandbox diagnostic file log active only in Debug builds: one file per
///         process, appended asynchronously by a background drain, silently disabled on any
///         write failure; Release builds erase the call sites entirely, producing no logs or
///         side effects.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         只记录白名单字段（路径、目录身份、SID、访问掩码、标志、退出码、耗时与本地构造的异常类型/消息），
///         绝不记录 payload、环境变量、命令正文、聊天内容或凭据。默认位置是每用户
///         <c>%LOCALAPPDATA%\tinyharness\logs</c>；只有 <see cref="Initialize" /> 之后的事件会落盘，
///         库级单元测试因此不会写入真实用户目录。
///     </para>
///     <para>
///         Only whitelisted fields are recorded (paths, directory identities, SIDs, access
///         masks, flags, exit codes, durations, and locally constructed exception types and
///         messages) — never payloads, environment variables, command text, chat content, or
///         credentials. The default location is the per-user
///         <c>%LOCALAPPDATA%\tinyharness\logs</c>; only events after <see cref="Initialize" />
///         reach disk, so library-level unit tests never write into the real user directory.
///     </para>
/// </remarks>
public static class SandboxDiagnosticLog
{
    /// <summary>
    ///     <para>写进程与构建头并固定日志文件；未调用前所有事件被丢弃，避免库测试污染真实用户目录。</para>
    ///     <para>
    ///         Writes the process/build header and pins the log file; before it runs all events are dropped so
    ///         library tests never touch the real user directory.
    ///     </para>
    /// </summary>
    [Conditional("DEBUG")]
    public static void Initialize()
    {
#if DEBUG
        string logFilePath;
        lock (Gate)
        {
            if (_logFilePath is not null || _disabled) return;
            try
            {
                var directory = LogDirectoryOverrideForTests ?? DefaultLogDirectory();
                if (string.IsNullOrWhiteSpace(directory))
                {
                    _disabled = true;
                    return;
                }

                Directory.CreateDirectory(directory);
                logFilePath = Path.Combine(directory,
                                           $"sandbox-diagnostic-{DateTime.UtcNow:yyyyMMdd-HHmmss}-p{Environment.ProcessId}.log");
                _logFilePath = logFilePath;
            }
            catch
            {
                _disabled = true;
                return;
            }
        }

        Event("startup",
              $"pid={Environment.ProcessId} process='{Environment.ProcessPath}' build=Debug runtime={Environment.Version} " +
              $"os={Environment.OSVersion}");
        Event("startup", $"log-file='{logFilePath}'");
        Debug.WriteLine($"TinyHarness sandbox diagnostic log: {logFilePath}");
#endif
    }

    /// <summary>
    ///     <para>追加一条单行诊断事件；消息会折叠换行并截断，任何失败静默丢弃，不阻塞执行路径。</para>
    ///     <para>
    ///         Appends a single-line diagnostic event; messages are flattened and truncated, and any failure is
    ///         silently dropped without ever blocking the execution path.
    ///     </para>
    /// </summary>
    [Conditional("DEBUG")]
    public static void Event(string category, string message)
    {
#if DEBUG
        lock (Gate)
        {
            if (_disabled || _logFilePath is null) return;
            try
            {
                var run = RunId.Value;
                var line =
                    $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}|{Environment.ProcessId}|{_sequence++}|{category}" +
                    (run is null ? string.Empty : $"|run={run}") + $"|{Sanitize(message)}";
                _pendingLines ??= new Queue<string>(64);
                if (_pendingLines.Count >= MaximumQueuedLines) return;
                _pendingLines.Enqueue(line);
                if (_drainTask is null || _drainTask.IsCompleted) _drainTask = DrainPendingLinesAsync(_logFilePath);
            }
            catch
            {
                _disabled = true;
            }
        }
#endif
    }
#if DEBUG
    /// <summary>排队上限；超出直接丢弃，仅约束内存中的无界积压；单行长度由 <see cref="Sanitize" /> 限制。</summary>
    private const int MaximumQueuedLines = 4_096;

    private const int MaximumLineLength = 8_192;

    private static readonly object Gate = new();

    private static readonly AsyncLocal<string?> RunId = new();

    private static Queue<string>? _pendingLines;
    private static Task?          _drainTask;
    private static string?        _logFilePath;
    private static bool           _disabled;
    private static long           _sequence;

    /// <summary>
    ///     <para>
    ///         为当前异步流设置诊断 run id，使后续事件自动携带该 id；用 null 清除。
    ///         只影响日志行格式，不改变任何被诊断路径的执行语义。
    ///     </para>
    ///     <para>
    ///         Sets the diagnostic run id for the current async flow so later events carry it automatically;
    ///         pass null to clear. It changes only the log line format, never the semantics of the diagnosed path.
    ///     </para>
    /// </summary>
    internal static void BeginRun(string? runId) => RunId.Value = runId;

    /// <summary>
    ///     <para>测试注入的日志目录；为 null 时使用用户 LocalApplicationData 下的固定目录。</para>
    ///     <para>Log-directory injection for tests; null selects the fixed per-user LocalApplicationData directory.</para>
    /// </summary>
    internal static string? LogDirectoryOverrideForTests { get; set; }

    /// <summary>
    ///     <para>每用户固定日志目录，与 sessions、windows-sandbox-home 共用 tinyharness 命名空间。</para>
    ///     <para>The fixed per-user log directory, sharing the tinyharness namespace with sessions and windows-sandbox-home.</para>
    /// </summary>
    internal static string DefaultLogDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tinyharness", "logs");

    /// <summary>
    ///     <para>折叠换行并截断超长消息，保证一行一个事件、单行长度有界。</para>
    ///     <para>Flattens newlines and truncates overlong messages so one event stays one length-bounded line.</para>
    /// </summary>
    internal static string Sanitize(string message)
    {
        var flat = message.Replace("\r", string.Empty, StringComparison.Ordinal)
                          .Replace("\n", "\\n", StringComparison.Ordinal);
        return flat.Length <= MaximumLineLength ? flat : flat[..MaximumLineLength] + "...<truncated>";
    }

    /// <summary>
    ///     <para>等待当前排空任务结束；仅测试使用，不保证此后新事件的落盘。</para>
    ///     <para>Waits for the current drain task to finish; tests only, with no guarantee for later events.</para>
    /// </summary>
    internal static async Task FlushForTestsAsync()
    {
        while (true)
        {
            Task? drain;
            lock (Gate)
            {
                drain = _drainTask;
                if (drain is null) return;
            }

            await drain.ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     <para>清空全部状态供下一个测试使用；旧排空任务可能仍写旧文件，测试须各自使用独立目录。</para>
    ///     <para>
    ///         Resets all state for the next test; an old drain task may still write its old file, so tests must
    ///         use separate directories.
    ///     </para>
    /// </summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _pendingLines = null;
            _drainTask    = null;
            _logFilePath  = null;
            _disabled     = false;
            _sequence     = 0;
        }
    }

    private static async Task DrainPendingLinesAsync(string logFilePath)
    {
        try
        {
            while (true)
            {
                string line;
                lock (Gate)
                {
                    if (_pendingLines is not { Count: > 0 })
                    {
                        _drainTask = null;
                        return;
                    }

                    line = _pendingLines.Dequeue();
                }

                await File.AppendAllTextAsync(logFilePath, line + Environment.NewLine, CancellationToken.None)
                          .ConfigureAwait(false);
            }
        }
        catch
        {
            // 诊断失败只停用自身，绝不向上传播，也不重试或递归记录。
            lock (Gate)
            {
                _disabled  = true;
                _drainTask = null;
                _pendingLines?.Clear();
            }
        }
    }
#endif
}

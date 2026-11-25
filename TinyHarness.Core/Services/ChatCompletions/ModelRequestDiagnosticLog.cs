using System.Diagnostics;

namespace TinyHarness.Core.Services.ChatCompletions;

/// <summary>
///     <para>
///         仅 Debug 构建生效的模型请求与 SDK 重试诊断日志：每进程一个文件，后台异步追加，
///         任何写入失败静默停用；Release 构建中调用点整体消失，不产生日志或副作用。
///     </para>
///     <para>
///         A model-request and SDK-retry diagnostic file log active only in Debug builds: one file per
///         process, appended asynchronously by a background drain, silently disabled on any write
///         failure; Release builds erase all call sites with no logs or side effects.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         只记录白名单元数据（请求序号、尝试次数、HTTP 方法与路径、状态码、耗时、流阶段、token 计数与异常类型），
///         绝不记录 API 密钥、请求头、查询参数、聊天正文或推理文本。默认位置是每用户
///         <c>%LOCALAPPDATA%\tinyharness\logs</c>；只有 <see cref="Initialize" /> 之后的事件会落盘。
///     </para>
///     <para>
///         Records only whitelisted metadata (request number, attempt count, HTTP method and path,
///         status code, elapsed duration, stream phase, token counts, and exception type) — never API
///         keys, headers, query parameters, chat text, or reasoning content. Defaults to per-user
///         <c>%LOCALAPPDATA%\tinyharness\logs</c>; only events after <see cref="Initialize" /> reach disk.
///     </para>
/// </remarks>
public static class ModelRequestDiagnosticLog
{
    /// <summary>
    ///     <para>写进程与构建头并固定日志文件；未调用前所有事件被丢弃，避免库测试污染真实用户目录。</para>
    ///     <para>
    ///         Writes the process/build header and pins the log file; before it runs all events are dropped
    ///         so library tests never touch the real user directory.
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
                                           $"model-request-diagnostic-{DateTime.UtcNow:yyyyMMdd-HHmmss}-p{Environment.ProcessId}.log");
                _logFilePath = logFilePath;
            }
            catch
            {
                _disabled = true;
                return;
            }
        }

        Event("startup",
              $"pid={Environment.ProcessId} process='{Environment.ProcessPath}' build=Debug runtime={Environment.Version}");
        Event("startup", $"log-file='{logFilePath}'");
        Debug.WriteLine($"TinyHarness model request diagnostic log: {logFilePath}");
#endif
    }

    /// <summary>
    ///     <para>追加一条单行诊断事件；消息会折叠换行并截断，任何失败静默丢弃，不阻塞请求路径。</para>
    ///     <para>
    ///         Appends a single-line diagnostic event; messages are flattened and truncated, and any failure
    ///         is silently dropped without blocking the request path.
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
                var line =
                    $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}|{Environment.ProcessId}|{_sequence++}|{category}|{Sanitize(message)}";
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
    private const int MaximumQueuedLines = 4_096;
    private const int MaximumLineLength  = 4_096;

    private static readonly object Gate = new();

    private static Queue<string>? _pendingLines;
    private static Task?          _drainTask;
    private static string?        _logFilePath;
    private static bool           _disabled;
    private static long           _sequence;

    /// <summary>
    ///     <para>测试注入的日志目录；为 null 时使用用户 LocalApplicationData 下的固定目录。</para>
    ///     <para>Log-directory injection for tests; null selects the fixed per-user LocalApplicationData directory.</para>
    /// </summary>
    internal static string? LogDirectoryOverrideForTests { get; set; }

    internal static string DefaultLogDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tinyharness", "logs");

    internal static string Sanitize(string message)
    {
        var flat = message.Replace("\r", string.Empty, StringComparison.Ordinal)
                          .Replace("\n", "\\n", StringComparison.Ordinal);
        return flat.Length <= MaximumLineLength ? flat : flat[..MaximumLineLength] + "...<truncated>";
    }

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
        while (true)
        {
            List<string> batch;
            lock (Gate)
            {
                if (_disabled || _pendingLines is null || _pendingLines.Count == 0)
                {
                    _drainTask = null;
                    return;
                }

                batch = new List<string>(_pendingLines.Count);
                while (_pendingLines.Count > 0) batch.Add(_pendingLines.Dequeue());
            }

            try
            {
                await File.AppendAllLinesAsync(logFilePath, batch).ConfigureAwait(false);
            }
            catch
            {
                lock (Gate)
                {
                    _disabled = true;
                    _pendingLines?.Clear();
                    _drainTask = null;
                    return;
                }
            }
        }
    }
#endif
}

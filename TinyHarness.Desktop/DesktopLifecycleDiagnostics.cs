using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TinyHarness.Desktop;

/// <summary>
///     <para>只在 Debug 桌面入口安装生命周期取证；不处理异常，不记录消息、正文或配置。</para>
///     <para>
///         Installs lifecycle evidence only at the Debug desktop entry, without handling exceptions or recording
///         messages, content or configuration.
///     </para>
/// </summary>
internal static class DesktopLifecycleDiagnostics
{
    private static readonly object  Gate = new();
    private static          string? _path;

    [Conditional("DEBUG")]
    internal static void Initialize()
    {
        lock (Gate)
        {
            if (_path is not null) return;
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                             "tinyharness", "logs");
                Directory.CreateDirectory(directory);
                _path = Path.Combine(directory,
                                     $"desktop-lifecycle-{DateTime.UtcNow:yyyyMMdd-HHmmss}-p{Environment.ProcessId}.log");
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"Desktop lifecycle diagnostics unavailable: {exception.GetType().Name}");
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Record("unhandled", args.ExceptionObject as Exception, args.IsTerminating);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Record("process-exit");
        }

        Record("startup");
    }

    /// <summary>
    ///     <para>同步保留固定阶段和脱敏异常栈，避免进程终止时异步队列丢失；诊断失败不改变应用行为。</para>
    ///     <para>
    ///         Synchronously preserves fixed phases and sanitized exception stacks so termination cannot lose an async
    ///         queue; diagnostic failures never change application behavior.
    ///     </para>
    /// </summary>
    [Conditional("DEBUG")]
    internal static void Record(string phase, Exception? exception = null, bool terminating = false)
    {
        lock (Gate)
        {
            if (_path is null) return;
            try
            {
                // 终止路径不能依赖后台任务；仅写固定阶段、类型、错误码和不含源码路径的栈。
                File.AppendAllText(_path, Format(phase, exception, terminating), Encoding.UTF8);
            }
            catch (Exception failure)
            {
                Trace.WriteLine($"Desktop lifecycle diagnostics write failed: {failure.GetType().Name}");
            }
        }
    }

    internal static string Format(string phase, Exception? exception, bool terminating)
    {
        var text = new StringBuilder();
        text.Append(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            .Append("|pid=").Append(Environment.ProcessId)
            .Append("|phase=").Append(phase.Replace('\r', ' ').Replace('\n', ' '))
            .Append("|terminating=").Append(terminating)
            .Append("|exitCode=").Append(Environment.ExitCode).AppendLine();
        if (phase == "window-close-requested") text.AppendLine(new StackTrace(false).ToString());
        var pending = new Queue<Exception>();
        if (exception is not null) pending.Enqueue(exception);
        for (var index = 0; pending.Count > 0 && index < 8; index++)
        {
            var current = pending.Dequeue();
            text.Append("exception[").Append(index).Append("]=").Append(current.GetType().FullName)
                .Append("|hresult=0x").Append(current.HResult.ToString("X8", CultureInfo.InvariantCulture))
                .AppendLine();
            var stack = new StackTrace(current, false).ToString();
            text.Append(stack.AsSpan(0, Math.Min(stack.Length, 8192))).AppendLine();
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions.Take(8))
                    pending.Enqueue(inner);
            else if (current.InnerException is { } inner) pending.Enqueue(inner);
        }

        return text.ToString();
    }
}

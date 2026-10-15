using System.ComponentModel;
using System.Diagnostics;
using TinyHarness.Core.Models.Runtime;

namespace TinyHarness.Core.Services.Runtime;

/// <summary>
///     <para>在宿主上执行已准备的进程计划，捕获输出并在超时或取消时回收进程树。</para>
///     <para>
///         Executes a prepared process plan on the host, captures output,
///         and reclaims the process tree on timeout or cancellation.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         取消后丢弃输出；清理使用独立宽限。
///         无法确认进程树回收时抛出 <see cref="ToolExecutionUncertainException" />，不返回已确认结果。
///     </para>
///     <para>
///         Output is discarded on cancellation; cleanup uses an independent grace period.
///         Unconfirmed process-tree reclamation throws <see cref="ToolExecutionUncertainException" />
///         instead of returning a confirmed result.
///     </para>
/// </remarks>
public sealed class HostProcessBackend : IProcessExecutionBackend
{
    private readonly IReadOnlyDictionary<string, string> _knownSecrets;

    /// <summary>
    ///     <para>构造宿主后端，并从子进程环境移除已知秘密对应的环境变量。</para>
    ///     <para>
    ///         Constructs the host backend and removes environment variables
    ///         associated with known secrets from the child environment.
    ///     </para>
    /// </summary>
    public HostProcessBackend(IReadOnlyDictionary<string, string>? knownSecrets = null) =>
        _knownSecrets = knownSecrets ?? new Dictionary<string, string>(StringComparer.Ordinal);

    public async Task<ProcessExecutionResult> ExecuteAsync(PreparedProcessExecution execution,
                                                           IProcessOutputCapture    stdoutCapture,
                                                           IProcessOutputCapture    stderrCapture,
                                                           CancellationToken        cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(stdoutCapture);
        ArgumentNullException.ThrowIfNull(stderrCapture);

        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(execution.TimeoutSeconds));
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                 timeoutSource.Token);
        using var        captureSource = new CancellationTokenSource();
        ProcessExecution processExecution;
        try
        {
            processExecution = ProcessExecution.Start(BuildStartInfo(execution), execution.RawCmdCommand);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new ProcessExecutionStartException(ex.Message, ex);
        }

        using (processExecution)
        {
            var processTask = processExecution.Process.WaitForExitAsync(CancellationToken.None);
            var stdoutTask  = PumpAsync(processExecution.StandardOutput, stdoutCapture, captureSource.Token);
            var stderrTask  = PumpAsync(processExecution.StandardError, stderrCapture, captureSource.Token);
            var allTasks    = new[] { processTask, stdoutTask, stderrTask };
            var timedOut    = false;
            try
            {
                await WaitForExecutionAsync(allTasks, linkedSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
            {
                await TerminateAndDrainAsync(processExecution, allTasks, captureSource,
                                             cancellationToken.IsCancellationRequested
                                                 ? "cancellation"
                                                 : $"the {execution.TimeoutSeconds}s timeout").ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    stdoutCapture.Discard();
                    stderrCapture.Discard();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                timedOut = true;
            }
            catch (Exception executionError) when (executionError is not OperationCanceledException)
            {
                try
                {
                    await TerminateAndDrainAsync(processExecution, allTasks, captureSource,
                                                 "an execution or output-capture failure",
                                                 executionError).ConfigureAwait(false);
                }
                catch (Exception cleanupError)
                {
                    throw new
                        ToolExecutionUncertainException($"Process execution failed ({executionError.Message}) and process-tree cleanup also failed " +
                                                        $"({cleanupError.Message}).",
                                                        new AggregateException(executionError, cleanupError));
                }

                stdoutCapture.Discard();
                stderrCapture.Discard();
                throw new
                    IOException($"Failed while executing or capturing output from '{execution.Executable}': {executionError.Message}",
                                executionError);
            }

            int? exitCode = processExecution.Process.HasExited ? processExecution.Process.ExitCode : null;
            return new ProcessExecutionResult(exitCode, timedOut, null);
        }
    }

    private ProcessStartInfo BuildStartInfo(PreparedProcessExecution execution)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName               = execution.Executable,
            WorkingDirectory       = execution.WorkingDirectory,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };
        foreach (var argument in execution.Arguments) startInfo.ArgumentList.Add(argument);

        foreach (var secret in _knownSecrets) startInfo.Environment.Remove(secret.Key);

        return startInfo;
    }

    /// <summary>
    ///     <para>分块捕获一条重定向进程流，并在流结束时冲刷捕获器尾部。</para>
    ///     <para>
    ///         Captures a redirected process stream in chunks
    ///         and flushes the capture's tail at end-of-stream.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>块大小固定为 4096 字符，以满足跨块秘密脱敏的约束；空块触发尾部冲刷。</para>
    ///     <para>
    ///         The fixed 4096-character chunk size satisfies cross-chunk secret-redaction constraints;
    ///         an empty chunk triggers the final flush.
    ///     </para>
    /// </remarks>
    private static async Task PumpAsync(
        StreamReader reader, IProcessOutputCapture capture, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;

            await capture.AppendAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await capture.AppendAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WaitForExecutionAsync(IEnumerable<Task> tasks, CancellationToken cancellationToken)
    {
        var pending = tasks.ToList();
        while (pending.Count != 0)
        {
            var completed = await Task.WhenAny(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
            pending.Remove(completed);
            await completed.ConfigureAwait(false);
        }
    }

    private static async Task TerminateAndDrainAsync(ProcessExecution          execution,
                                                     IReadOnlyCollection<Task> tasks,
                                                     CancellationTokenSource   captureSource,
                                                     string                    reason,
                                                     Exception?                knownFailure = null)
    {
        Exception? terminationError = null;
        try
        {
            execution.Terminate();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            terminationError = ex;
        }

        var completion = Task.WhenAll(tasks);
        try
        {
            await completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!completion.IsCompleted)
        {
            captureSource.Cancel();
            execution.CloseOutput();
            try
            {
                await completion.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 以随后抛出的上下文清理错误为准；Task.WhenAll 已观察所有子任务。
            }

            throw new
                ToolExecutionUncertainException($"Timed out draining process output after {reason}; the process tree was termination-requested" +
                                                (terminationError is null
                                                    ? "."
                                                    : $", but termination failed: {terminationError.Message}"),
                                                terminationError);
        }
        catch (Exception drainError)
        {
            var drainErrors = completion.Exception?.Flatten().InnerExceptions ?? [drainError];
            IReadOnlyList<Exception> unexpectedErrors = knownFailure is null
                ? drainErrors
                : drainErrors.Where(error => !ReferenceEquals(error, knownFailure)).ToArray();
            if (unexpectedErrors.Count != 0 || terminationError is not null)
            {
                var cleanupErrors = unexpectedErrors.ToList();
                if (terminationError is not null) cleanupErrors.Insert(0, terminationError);

                throw new
                    ToolExecutionUncertainException($"Process-tree cleanup encountered an additional failure after {reason}: " +
                                                    string.Join("; ", cleanupErrors.Select(error => error.Message)),
                                                    new AggregateException(cleanupErrors));
            }
        }

        if (terminationError is not null)
            throw new
                ToolExecutionUncertainException($"Failed to terminate the process tree after {reason}: {terminationError.Message}",
                                                terminationError);
    }

    private sealed class ProcessExecution : IDisposable
    {
        private readonly WindowsJobProcess.RunningProcess? _windowsProcess;
        private          bool                              _outputClosed;

        private ProcessExecution(Process process, StreamReader standardOutput, StreamReader standardError,
                                 WindowsJobProcess.RunningProcess? windowsProcess)
        {
            Process         = process;
            StandardOutput  = standardOutput;
            StandardError   = standardError;
            _windowsProcess = windowsProcess;
        }

        public Process      Process        { get; }
        public StreamReader StandardOutput { get; }
        public StreamReader StandardError  { get; }

        public void Dispose()
        {
            CloseOutput();
            if (_windowsProcess is not null)
                _windowsProcess.Dispose();
            else
                Process.Dispose();
        }

        public static ProcessExecution Start(ProcessStartInfo startInfo, string? rawCmdCommand)
        {
            if (OperatingSystem.IsWindows())
            {
                var process = WindowsJobProcess.Start(startInfo, rawCmdCommand);
                return new ProcessExecution(process.Process, process.StandardOutput, process.StandardError,
                                            process);
            }

            if (rawCmdCommand is not null)
                throw new PlatformNotSupportedException("Raw cmd execution is available only on Windows.");

            var managedProcess = new Process { StartInfo = startInfo };
            if (managedProcess.Start())
                return new ProcessExecution(managedProcess, managedProcess.StandardOutput,
                                            managedProcess.StandardError, null);
            managedProcess.Dispose();
            throw new InvalidOperationException($"Failed to start executable '{startInfo.FileName}'.");
        }

        public void Terminate()
        {
            if (_windowsProcess is not null)
            {
                _windowsProcess.Terminate();
                return;
            }

            if (Process.HasExited)
                throw new
                    InvalidOperationException("Cannot terminate descendants after the root process has exited on this platform.");

            Process.Kill(true);
        }

        public void CloseOutput()
        {
            if (_outputClosed) return;

            _outputClosed = true;
            StandardOutput.Dispose();
            StandardError.Dispose();
        }
    }
}

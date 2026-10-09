using System.ComponentModel;
using System.Diagnostics;
using TinyHarness.Core.Models.Runtime;

namespace TinyHarness.Core.Services.Runtime;

/// <summary>
///     宿主侧进程执行后端：直接在宿主上启动受约束的子进程，把两条重定向流按块推入捕获器，
///     并按计划的时限终止进程树。启动、等待、终止、排空与宽限语义自 ShellTool 原样迁入，
///     可观察行为不变。
///     Host-side process-execution backend: starts the constrained child process
///     directly on the host machine, pumps both redirected streams into captures as
///     chunks, and terminates the process tree at the plan's timeout. The start,
///     wait, terminate, drain, and grace semantics were moved verbatim from
///     ShellTool; observable behavior is unchanged.
/// </summary>
public sealed class HostProcessBackend : IProcessExecutionBackend
{
    private readonly IReadOnlyDictionary<string, string> _knownSecrets;

    /// <summary>
    ///     用已知 secret 集合构造宿主后端；secret 的环境变量名会从子进程环境中移除。
    ///     Constructs the host backend with the known-secret set; each secret's
    ///     environment variable name is removed from the child environment.
    /// </summary>
    public HostProcessBackend(IReadOnlyDictionary<string, string>? knownSecrets = null)
    {
        _knownSecrets = knownSecrets ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

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
                        InvalidOperationException($"Process execution failed ({executionError.Message}) and process-tree cleanup also failed " +
                                                  $"({cleanupError.Message}).",
                                                  new AggregateException(executionError, cleanupError));
                }

                stdoutCapture.Discard();
                stderrCapture.Discard();
                throw new IOException(
                                      $"Failed while executing or capturing output from '{execution.Executable}': {executionError.Message}",
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
    ///     把一条重定向进程流按 4096 字符块推入捕获器；跨块 secret 脱敏依赖该块大小。
    ///     流读到结尾后追加一个空块，触发捕获器的尾部冲刷。
    ///     Pumps one redirected process stream into its capture in 4096-character
    ///     chunks; cross-chunk secret redaction depends on that chunk size. After
    ///     end-of-stream an empty chunk is appended to flush the capture's tail.
    /// </summary>
    private static async Task PumpAsync(StreamReader      reader, IProcessOutputCapture capture,
                                        CancellationToken cancellationToken)
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
                // The contextual cleanup error below is authoritative. All
                // component tasks are observed by Task.WhenAll.
            }

            throw new
                TimeoutException($"Timed out draining process output after {reason}; the process tree was termination-requested" +
                                 (terminationError is null
                                     ? "."
                                     : $", but termination failed: {terminationError.Message}"), terminationError);
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
                    InvalidOperationException($"Process-tree cleanup encountered an additional failure after {reason}: " +
                                              string.Join("; ", cleanupErrors.Select(error => error.Message)),
                                              new AggregateException(cleanupErrors));
            }
        }

        if (terminationError is not null)
            throw new
                InvalidOperationException($"Failed to terminate the process tree after {reason}: {terminationError.Message}",
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
            if (!managedProcess.Start())
            {
                managedProcess.Dispose();
                throw new InvalidOperationException($"Failed to start executable '{startInfo.FileName}'.");
            }

            return new ProcessExecution(managedProcess, managedProcess.StandardOutput,
                                        managedProcess.StandardError, null);
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

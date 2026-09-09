using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Runtime;

namespace TinyHarness.Core.Tools;

/// <summary>
/// 进程执行工具。direct 模式固定 executable 与逐项 arguments；显式 shell 模式把完整 command
/// 映射成所选解释器的参数。两者都冻结工作目录与 timeout，并在超时或取消时终止进程树。
///
/// Process-execution tool. Direct mode fixes an executable and individual
/// arguments; explicit shell mode maps complete command text to the selected
/// interpreter. Both freeze cwd/timeout; direct invocations retain per-argument
/// quoting, while Windows cmd receives its complete command through the raw
/// /S /C command tail. Timeout and cancellation terminate the process tree.
/// </summary>
public sealed class ShellTool : ITool
{
    private const int DefaultTimeoutSeconds = 120;
    private const int MaximumTimeoutSeconds = 3600;
    private const int MaximumArguments      = 256;
    private const int MaximumArgumentLength = 32 * 1024;
    private const int MaximumCommandLength  = 32 * 1024;
    private const int DefaultOutputCharactersPerStream = 16 * 1024;

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["mode"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("direct", "shell"),
                ["description"] = "direct launches executable/arguments; shell interprets command using the selected shell.",
            },
            ["executable"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Executable name or path. It is launched directly; shell syntax is not interpreted.",
            },
            ["arguments"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = MaximumArgumentLength },
                ["maxItems"] = MaximumArguments,
                ["description"] = "Arguments passed individually to the executable.",
            },
            ["shell"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("powershell", "cmd", "bash", "zsh", "sh"),
                ["description"] = "Shell flavor for mode=shell. Defaults to PowerShell on Windows and SHELL/sh elsewhere.",
            },
            ["command"] = new JsonObject
            {
                ["type"] = "string",
                ["maxLength"] = MaximumCommandLength,
                ["description"] = "Complete command text interpreted only when mode=shell.",
            },
            ["workingDirectory"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Workspace-relative working directory. Defaults to the workspace root.",
            },
            ["timeoutSeconds"] = new JsonObject
            {
                ["type"] = "integer",
                ["minimum"] = 1,
                ["maximum"] = MaximumTimeoutSeconds,
                ["description"] = "Optional execution timeout in seconds.",
            },
        },
        ["oneOf"] = new JsonArray
        {
            (JsonNode)new JsonObject
            {
                ["properties"] = new JsonObject { ["mode"] = new JsonObject { ["const"] = "direct" } },
                ["required"]   = new JsonArray("mode", "executable"),
            },
            (JsonNode)new JsonObject
            {
                ["properties"] = new JsonObject { ["mode"] = new JsonObject { ["const"] = "shell" } },
                ["required"]   = new JsonArray("mode", "command"),
            },
        },
    };

    private readonly Workspace _workspace;
    private readonly int _defaultTimeoutSeconds;
    private readonly int _outputCharacterLimit;
    private readonly string _artifactRoot;
    private readonly IReadOnlyDictionary<string, string> _knownSecrets;
    private readonly Func<string, int, IReadOnlyList<string>, IProcessOutputCapture> _captureFactory;

    public ShellTool(Workspace workspace, int? defaultTimeoutSeconds = null,
                     IReadOnlyDictionary<string, string>? knownSecrets = null,
                     string? artifactRoot = null,
                     int outputCharacterLimit = DefaultOutputCharactersPerStream)
    {
        _workspace = workspace;
        _defaultTimeoutSeconds = defaultTimeoutSeconds ?? DefaultTimeoutSeconds;
        ValidateTimeout(_defaultTimeoutSeconds, "default tool timeout");
        if (outputCharacterLimit < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(outputCharacterLimit), "Output limit must be at least 2 characters.");
        }

        _outputCharacterLimit = outputCharacterLimit;
        _artifactRoot = artifactRoot ?? Path.Combine(Path.GetTempPath(), "TinyHarness", "process-output");
        _knownSecrets = knownSecrets ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _captureFactory = static (path, limit, secrets) => new ProcessOutputCapture(path, limit, secrets);
    }

    internal ShellTool(Workspace workspace,
                       Func<string, int, IReadOnlyList<string>, IProcessOutputCapture> captureFactory,
                       int? defaultTimeoutSeconds = null, string? artifactRoot = null,
                       int outputCharacterLimit = DefaultOutputCharactersPerStream)
        : this(workspace, defaultTimeoutSeconds, artifactRoot : artifactRoot,
               outputCharacterLimit : outputCharacterLimit)
    {
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));
    }

    public ToolDefinition Definition { get; } = new()
    {
        Name = "shell",
        Description = "Runs either a direct executable/arguments invocation or an explicit shell command inside the workspace. " +
                      "Returns exit code, timeout state, stdout, and stderr.",
        Parameters = Schema,
    };

    public ToolPreparation Prepare(ChatToolCall call)
    {
        var args                = ToolArgs.ParseObject(call);
        var mode                = JsonArgs.Optional(args, "mode", "direct").ToLowerInvariant();
        var rawWorkingDirectory = JsonArgs.Optional(args, "workingDirectory", ".");
        var workingDirectory    = _workspace.ResolveInside(rawWorkingDirectory, "workingDirectory");
        var timeoutSeconds      = JsonArgs.OptionalInt(args, "timeoutSeconds") ?? _defaultTimeoutSeconds;
        ValidateTimeout(timeoutSeconds, "timeoutSeconds");

        var command = mode switch
        {
            "direct" => PrepareDirect(args, workingDirectory),
            "shell"  => PrepareShell(args),
            _ => throw new InvalidDataException("Tool argument 'mode' must be 'direct' or 'shell'."),
        };

        var normalizedArguments = new JsonArray();
        foreach (var argument in command.Arguments)
        {
            normalizedArguments.Add((JsonNode?)JsonValue.Create(argument));
        }

        args["mode"]             = mode;
        args["executable"]       = command.Executable;
        args["arguments"]        = normalizedArguments;
        args["workingDirectory"] = workingDirectory;
        args["timeoutSeconds"]   = timeoutSeconds;
        var workspaceExecutable =
            IsPathLike(command.Executable) && Workspace.IsInside(_workspace.Root, command.Executable);
        args["_workspaceExecutable"] = workspaceExecutable;

        var commandIdentity = new JsonObject
        {
            ["mode"]       = mode,
            ["executable"] = command.Executable,
            ["arguments"]  = normalizedArguments.DeepClone(),
            ["shell"]      = command.Shell,
            ["command"]    = command.CommandText,
            ["workingDirectory"] = mode == "shell" ? workingDirectory : null,
        }.ToJsonString();
        var displayCommand = mode == "direct"
            ? FormatCommand(command.Executable, command.Arguments)
            : $"{command.Shell}: {command.CommandText}";
        return new ToolPreparation
        {
            ToolName          = Definition.Name,
            CallId            = call.Id,
            Arguments         = args,
            Capability        = "process.execute",
            Summary           = $"shell: {displayCommand} (cwd: {_workspace.ToDisplay(workingDirectory)}, timeout: {timeoutSeconds}s)",
            RiskLevel         = mode == "shell" ? ToolRiskLevel.Elevated : ToolRiskLevel.Standard,
            SessionConstraint = commandIdentity,
            TargetPaths       = [workingDirectory],
            ExecutionPlan     = new ShellExecutionPlan(
                command.Executable, Array.AsReadOnly(command.Arguments.ToArray()), workingDirectory,
                timeoutSeconds, workspaceExecutable,
                mode == "shell" && command.Shell == "cmd" ? command.CommandText : null),
        };
    }

    public async Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
    {
        if (preparation.ExecutionPlan is not ShellExecutionPlan plan)
        {
            throw new InvalidDataException("Shell execution requires the immutable plan produced by Prepare.");
        }

        if (!Directory.Exists(plan.WorkingDirectory))
        {
            return Failed($"Working directory not found: {_workspace.ToDisplay(plan.WorkingDirectory)}");
        }

        _workspace.EnsureFinalTargetInside(plan.WorkingDirectory, isDirectory : true, "Working directory");
        if (plan.WorkspaceExecutable)
        {
            if (!File.Exists(plan.Executable))
            {
                return Failed($"Executable not found: {_workspace.ToDisplay(plan.Executable)}");
            }

            _workspace.EnsureFinalTargetInside(plan.Executable, isDirectory : false, "Executable");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_artifactRoot);
        var runId      = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}";
        var stdoutPath = Path.Combine(_artifactRoot, runId + ".stdout.txt");
        var stderrPath = Path.Combine(_artifactRoot, runId + ".stderr.txt");
        var secrets = _knownSecrets.Values.Where(value => !string.IsNullOrEmpty(value)).ToArray();
        var stdoutCapture = _captureFactory(stdoutPath, _outputCharacterLimit, secrets);
        var stderrCapture = _captureFactory(stderrPath, _outputCharacterLimit, secrets);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(plan.TimeoutSeconds));
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                                                                                 timeoutSource.Token);
        using var captureSource = new CancellationTokenSource();
        ProcessExecution execution;
        try
        {
            execution = ProcessExecution.Start(BuildStartInfo(plan), plan.RawCmdCommand);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return Failed($"Failed to start executable '{plan.Executable}': {ex.Message}");
        }

        using (execution)
        {
            var processTask = execution.Process.WaitForExitAsync(CancellationToken.None);
            var stdoutTask = stdoutCapture.ReadAsync(execution.StandardOutput, captureSource.Token);
            var stderrTask = stderrCapture.ReadAsync(execution.StandardError, captureSource.Token);
            var allTasks = new[] { processTask, stdoutTask, stderrTask };
            var timedOut = false;
            try
            {
                await WaitForExecutionAsync(allTasks, linkedSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
            {
                await TerminateAndDrainAsync(execution, allTasks, captureSource,
                                             cancellationToken.IsCancellationRequested
                                                 ? "cancellation"
                                                 : $"the {plan.TimeoutSeconds}s timeout").ConfigureAwait(false);
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
                    await TerminateAndDrainAsync(execution, allTasks, captureSource,
                                                 "an execution or output-capture failure",
                                                 executionError).ConfigureAwait(false);
                }
                catch (Exception cleanupError)
                {
                    throw new InvalidOperationException(
                        $"Process execution failed ({executionError.Message}) and process-tree cleanup also failed " +
                        $"({cleanupError.Message}).",
                        new AggregateException(executionError, cleanupError));
                }

                stdoutCapture.Discard();
                stderrCapture.Discard();
                throw new IOException(
                    $"Failed while executing or capturing output from '{plan.Executable}': {executionError.Message}",
                                      executionError);
            }

            var stdout = stdoutCapture.Complete();
            var stderr = stderrCapture.Complete();
            int? exitCode = execution.Process.HasExited ? execution.Process.ExitCode : null;
            var content = FormatResult(exitCode, timedOut, plan.TimeoutSeconds, stdout, stderr);
            return new ToolResult
            {
                Succeeded          = !timedOut && exitCode == 0,
                Content            = content,
                ExitCode           = exitCode,
                TimedOut           = timedOut,
                OutputTruncated    = stdout.Truncated || stderr.Truncated,
                Stdout             = stdout.Content,
                Stderr             = stderr.Content,
                StdoutArtifactPath = stdout.ArtifactPath,
                StderrArtifactPath = stderr.ArtifactPath,
            };
        }
    }

    private ProcessStartInfo BuildStartInfo(ShellExecutionPlan plan)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName               = plan.Executable,
            WorkingDirectory       = plan.WorkingDirectory,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        foreach (var argument in plan.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var secret in _knownSecrets)
        {
            startInfo.Environment.Remove(secret.Key);
        }

        return startInfo;
    }

    /// <summary>
    /// 准备不经过 shell 解释的直接进程调用。
    /// Prepares a direct process invocation whose arguments are never interpreted as shell syntax.
    /// </summary>
    private PreparedCommand PrepareDirect(JsonObject args, string workingDirectory)
    {
        if (args["command"] is not null || args["shell"] is not null)
        {
            throw new InvalidDataException("Direct mode does not accept 'command' or 'shell'.");
        }

        var rawExecutable = JsonArgs.Required(args, "executable").Trim();
        ValidateText(rawExecutable, "executable");
        var arguments = JsonArgs.OptionalStringArray(args, "arguments", MaximumArguments);
        ValidateArguments(arguments);
        return new PreparedCommand(NormalizeExecutable(rawExecutable, workingDirectory), arguments,
                                   Shell : null, CommandText : null);
    }

    /// <summary>
    /// 将显式 shell flavor 与完整命令转换为一个冻结的直接进程计划；不会使用 Start-Process。
    /// Converts an explicit shell flavor and complete command into a frozen direct process plan; never uses Start-Process.
    /// </summary>
    private static PreparedCommand PrepareShell(JsonObject args)
    {
        if (args["executable"] is not null || args["arguments"] is not null)
        {
            throw new InvalidDataException("Shell mode does not accept 'executable' or 'arguments'.");
        }

        var command = JsonArgs.Required(args, "command");
        if (command.Length > MaximumCommandLength)
        {
            throw new InvalidDataException($"Tool argument 'command' exceeds the {MaximumCommandLength} character limit.");
        }

        ValidateCommandText(command);
        var shell = JsonArgs.Optional(args, "shell", DefaultShell()).ToLowerInvariant();
        var invocation = shell switch
        {
            "powershell" => new PreparedCommand(
                OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command], shell, command),
            "cmd" when OperatingSystem.IsWindows() =>
                new PreparedCommand(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                                    ["/d", "/s", "/c", command], shell, command),
            "cmd" => throw new InvalidDataException("The cmd shell is available only on Windows."),
            "bash" or "zsh" => new PreparedCommand(shell, ["-c", command], shell, command),
            "sh" => new PreparedCommand(OperatingSystem.IsWindows() ? "sh" : "/bin/sh",
                                        ["-c", command], shell, command),
            _ => throw new InvalidDataException(
                "Tool argument 'shell' must be 'powershell', 'cmd', 'bash', 'zsh', or 'sh'."),
        };

        args["shell"]   = shell;
        args["command"] = command;
        return invocation;
    }

    private static string DefaultShell()
    {
        if (OperatingSystem.IsWindows())
        {
            return "powershell";
        }

        var configured = Path.GetFileName(Environment.GetEnvironmentVariable("SHELL"));
        return configured is "bash" or "zsh" or "sh" ? configured : "sh";
    }

    private static void ValidateArguments(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            ValidateText(arguments[i], $"arguments[{i}]");
            if (arguments[i].Length > MaximumArgumentLength)
            {
                throw new InvalidDataException(
                    $"Tool argument 'arguments[{i}]' exceeds the {MaximumArgumentLength} character limit.");
            }
        }
    }

    private string NormalizeExecutable(string executable, string workingDirectory)
    {
        if (!IsPathLike(executable))
        {
            return executable;
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(executable)
                                        ? executable
                                        : Path.Combine(workingDirectory, executable));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"Tool argument 'executable' is not a valid path: '{executable}'.", ex);
        }

        if (!Path.IsPathRooted(executable) && !Workspace.IsInside(_workspace.Root, full))
        {
            throw new InvalidDataException("A relative executable path escapes the workspace root.");
        }

        return full;
    }

    private static bool IsPathLike(string executable)
        => Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar) ||
           executable.Contains(Path.AltDirectorySeparatorChar);

    private static void ValidateText(string value, string argumentName)
    {
        if (value.IndexOf('\0') >= 0 || value.Any(character => char.IsControl(character) && character is not '\t'))
        {
            throw new InvalidDataException($"Tool argument '{argumentName}' contains a control character.");
        }
    }

    private static void ValidateCommandText(string command)
    {
        if (command.IndexOf('\0') >= 0 ||
            command.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
        {
            throw new InvalidDataException("Tool argument 'command' contains an unsupported control character.");
        }
    }

    private static void ValidateTimeout(int timeoutSeconds, string argumentName)
    {
        if (timeoutSeconds is < 1 or > MaximumTimeoutSeconds)
        {
            throw new ArgumentOutOfRangeException(argumentName,
                $"Timeout must be between 1 and {MaximumTimeoutSeconds} seconds.");
        }
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

    private static async Task TerminateAndDrainAsync(ProcessExecution       execution,
                                                     IReadOnlyCollection<Task> tasks,
                                                     CancellationTokenSource captureSource,
                                                     string                  reason,
                                                     Exception?              knownFailure = null)
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

            throw new TimeoutException(
                $"Timed out draining process output after {reason}; the process tree was termination-requested" +
                (terminationError is null ? "." : $", but termination failed: {terminationError.Message}"),
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
                var cleanupErrors = unexpectedErrors.Cast<Exception>().ToList();
                if (terminationError is not null)
                {
                    cleanupErrors.Insert(0, terminationError);
                }

                throw new InvalidOperationException(
                    $"Process-tree cleanup encountered an additional failure after {reason}: " +
                    string.Join("; ", cleanupErrors.Select(error => error.Message)),
                    new AggregateException(cleanupErrors));
            }
        }

        if (terminationError is not null)
        {
            throw new InvalidOperationException(
                $"Failed to terminate the process tree after {reason}: {terminationError.Message}",
                                                terminationError);
        }
    }

    private static string FormatCommand(string executable, IReadOnlyList<string> arguments)
        => string.Join(' ', new[] { executable }.Concat(arguments).Select(QuoteForDisplay));

    private static string QuoteForDisplay(string value)
        => value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"'
            : value;

    private static string FormatResult(int? exitCode, bool timedOut, int timeoutSeconds,
                                       CapturedProcessOutput stdout, CapturedProcessOutput stderr)
    {
        var output = new StringBuilder();
        output.Append("Exit code: ").AppendLine(exitCode?.ToString() ?? "unavailable");
        output.Append("Timed out: ").AppendLine(timedOut ? $"true ({timeoutSeconds}s)" : "false");
        output.AppendLine("stdout:").AppendLine(stdout.Content);
        output.AppendLine("stderr:").Append(stderr.Content);
        return output.ToString();
    }

    private static ToolResult Failed(string message) => new() { Succeeded = false, Content = message };

    private sealed record PreparedCommand(string Executable, IReadOnlyList<string> Arguments,
                                           string? Shell, string? CommandText);

    private sealed record ShellExecutionPlan(
        string                Executable,
        IReadOnlyList<string> Arguments,
        string                WorkingDirectory,
        int                   TimeoutSeconds,
        bool                  WorkspaceExecutable,
        string?               RawCmdCommand);

    private sealed class ProcessExecution : IDisposable
    {
        private readonly WindowsJobProcess.RunningProcess? _windowsProcess;
        private bool _outputClosed;

        private ProcessExecution(Process process, StreamReader standardOutput, StreamReader standardError,
                                 WindowsJobProcess.RunningProcess? windowsProcess)
        {
            Process        = process;
            StandardOutput = standardOutput;
            StandardError  = standardError;
            _windowsProcess = windowsProcess;
        }

        public Process Process { get; }
        public StreamReader StandardOutput { get; }
        public StreamReader StandardError { get; }

        public static ProcessExecution Start(ProcessStartInfo startInfo, string? rawCmdCommand)
        {
            if (OperatingSystem.IsWindows())
            {
                var process = WindowsJobProcess.Start(startInfo, rawCmdCommand);
                return new ProcessExecution(process.Process, process.StandardOutput, process.StandardError,
                                            process);
            }

            if (rawCmdCommand is not null)
            {
                throw new PlatformNotSupportedException("Raw cmd execution is available only on Windows.");
            }

            var managedProcess = new Process { StartInfo = startInfo };
            if (!managedProcess.Start())
            {
                managedProcess.Dispose();
                throw new InvalidOperationException($"Failed to start executable '{startInfo.FileName}'.");
            }

            return new ProcessExecution(managedProcess, managedProcess.StandardOutput,
                                        managedProcess.StandardError, windowsProcess : null);
        }

        public void Terminate()
        {
            if (_windowsProcess is not null)
            {
                _windowsProcess.Terminate();
                return;
            }

            if (Process.HasExited)
            {
                throw new InvalidOperationException(
                    "Cannot terminate descendants after the root process has exited on this platform.");
            }

            Process.Kill(entireProcessTree : true);
        }

        public void CloseOutput()
        {
            if (_outputClosed)
            {
                return;
            }

            _outputClosed = true;
            StandardOutput.Dispose();
            StandardError.Dispose();
        }

        public void Dispose()
        {
            CloseOutput();
            if (_windowsProcess is not null)
            {
                _windowsProcess.Dispose();
            }
            else
            {
                Process.Dispose();
            }
        }
    }
}

using System.Text;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Core.Services.Tools;

/// <summary>
///     进程执行工具。direct 模式固定 executable 与逐项 arguments；显式 shell 模式把完整 command
///     映射成所选解释器的参数。两者都冻结工作目录与 timeout，并在超时或取消时终止进程树。
///     Process-execution tool. Direct mode fixes an executable and individual
///     arguments; explicit shell mode maps complete command text to the selected
///     interpreter. Both freeze cwd/timeout; direct invocations retain per-argument
///     quoting, while Windows cmd receives its complete command through the raw
///     /S /C command tail. Timeout and cancellation terminate the process tree.
/// </summary>
public sealed class ShellTool : ITool
{
    private const int DefaultTimeoutSeconds            = 120;
    private const int MaximumTimeoutSeconds            = 3600;
    private const int MaximumArguments                 = 256;
    private const int MaximumArgumentLength            = 32 * 1024;
    private const int MaximumCommandLength             = 32 * 1024;
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
                ["description"] =
                    "direct launches executable/arguments; shell interprets command using the selected shell."
            },
            ["executable"] = new JsonObject
            {
                ["type"]        = "string",
                ["description"] = "Executable name or path. It is launched directly; shell syntax is not interpreted."
            },
            ["arguments"] = new JsonObject
            {
                ["type"]        = "array",
                ["items"]       = new JsonObject { ["type"] = "string", ["maxLength"] = MaximumArgumentLength },
                ["maxItems"]    = MaximumArguments,
                ["description"] = "Arguments passed individually to the executable."
            },
            ["shell"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("powershell", "cmd", "bash", "zsh", "sh"),
                ["description"] =
                    "Shell flavor for mode=shell. Defaults to PowerShell on Windows and SHELL/sh elsewhere."
            },
            ["command"] = new JsonObject
            {
                ["type"]        = "string",
                ["maxLength"]   = MaximumCommandLength,
                ["description"] = "Complete command text interpreted only when mode=shell."
            },
            ["workingDirectory"] = new JsonObject
            {
                ["type"]        = "string",
                ["description"] = "Workspace-relative working directory. Defaults to the workspace root."
            },
            ["timeoutSeconds"] = new JsonObject
            {
                ["type"]        = "integer",
                ["minimum"]     = 1,
                ["maximum"]     = MaximumTimeoutSeconds,
                ["description"] = "Optional execution timeout in seconds."
            }
        },
        ["oneOf"] = new JsonArray
        {
            (JsonNode)new JsonObject
            {
                ["properties"] = new JsonObject { ["mode"] = new JsonObject { ["const"] = "direct" } },
                ["required"]   = new JsonArray("mode", "executable")
            },
            (JsonNode)new JsonObject
            {
                ["properties"] = new JsonObject { ["mode"] = new JsonObject { ["const"] = "shell" } },
                ["required"]   = new JsonArray("mode", "command")
            }
        }
    };

    private readonly string                                                          _artifactRoot;
    private readonly Func<string, int, IReadOnlyList<string>, IProcessOutputCapture> _captureFactory;
    private readonly int                                                             _defaultTimeoutSeconds;
    private readonly ProcessExecutionPolicy?                                         _executionPolicy;
    private readonly IReadOnlyDictionary<string, string>                             _knownSecrets;
    private readonly int                                                             _outputCharacterLimit;
    private readonly IProcessExecutionBackend                                        _processBackend;

    private readonly Workspace _workspace;

    public ShellTool(Workspace                            workspace, int? defaultTimeoutSeconds = null,
                     IReadOnlyDictionary<string, string>? knownSecrets         = null,
                     string?                              artifactRoot         = null,
                     int                                  outputCharacterLimit = DefaultOutputCharactersPerStream,
                     IProcessExecutionBackend?            processBackend       = null,
                     ProcessExecutionPolicy?              executionPolicy      = null)
    {
        _workspace             = workspace;
        _defaultTimeoutSeconds = defaultTimeoutSeconds ?? DefaultTimeoutSeconds;
        ValidateTimeout(_defaultTimeoutSeconds, "default tool timeout");
        if (outputCharacterLimit < 2)
            throw new ArgumentOutOfRangeException(nameof(outputCharacterLimit),
                                                  "Output limit must be at least 2 characters.");

        _outputCharacterLimit = outputCharacterLimit;
        _artifactRoot         = artifactRoot ?? Path.Combine(Path.GetTempPath(), "TinyHarness", "process-output");
        _knownSecrets         = knownSecrets ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _captureFactory       = static (path, limit, secrets) => new ProcessOutputCapture(path, limit, secrets);
        _processBackend       = processBackend ?? new HostProcessBackend(_knownSecrets);
        _executionPolicy      = executionPolicy;
    }

    internal ShellTool(Workspace workspace,
                       Func<string, int, IReadOnlyList<string>, IProcessOutputCapture> captureFactory,
                       int? defaultTimeoutSeconds = null, string? artifactRoot = null,
                       int outputCharacterLimit = DefaultOutputCharactersPerStream,
                       IProcessExecutionBackend? processBackend = null,
                       ProcessExecutionPolicy? executionPolicy = null)
        : this(workspace, defaultTimeoutSeconds, null, artifactRoot,
               outputCharacterLimit, processBackend,
               executionPolicy)
    {
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));
    }

    public ToolDefinition Definition { get; } = new()
    {
        Name = "shell",
        Description =
            "Runs either a direct executable/arguments invocation or an explicit shell command inside the workspace. " +
            "Returns exit code, timeout state, stdout, and stderr.",
        Parameters = Schema
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
            _        => throw new InvalidDataException("Tool argument 'mode' must be 'direct' or 'shell'.")
        };

        var normalizedArguments = new JsonArray();
        foreach (var argument in command.Arguments) normalizedArguments.Add((JsonNode?)JsonValue.Create(argument));

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
            ["mode"]             = mode,
            ["executable"]       = command.Executable,
            ["arguments"]        = normalizedArguments.DeepClone(),
            ["shell"]            = command.Shell,
            ["command"]          = command.CommandText,
            ["workingDirectory"] = mode == "shell" ? workingDirectory : null
        }.ToJsonString();
        // Grants bind the command identity plus the execution policy identity,
        // so an approval can never be reused to switch hosts or widen the
        // sandbox policy; denies keep the bare command identity and survive
        // backend changes (see ToolPreparation.SessionGrantConstraint).
        var grantConstraint = _executionPolicy is { } policy
            ? commandIdentity + "\nexecution-policy: " + policy.PolicyIdentity
            : null;
        var displayCommand = mode == "direct"
            ? FormatCommand(command.Executable, command.Arguments)
            : $"{command.Shell}: {command.CommandText}";
        var summary =
            $"shell: {displayCommand} (cwd: {_workspace.ToDisplay(workingDirectory)}, timeout: {timeoutSeconds}s)";
        if (_executionPolicy is { } executionPolicy) summary += $" [{executionPolicy.DisplayName}]";

        return new ToolPreparation
        {
            ToolName               = Definition.Name,
            CallId                 = call.Id,
            Arguments              = args,
            Capability             = "process.execute",
            Summary                = summary,
            RiskLevel              = mode == "shell" ? ToolRiskLevel.Elevated : ToolRiskLevel.Standard,
            SessionConstraint      = commandIdentity,
            SessionGrantConstraint = grantConstraint,
            ExecutionPolicy        = _executionPolicy?.PolicyIdentity,
            TargetPaths            = [workingDirectory],
            ExecutionPlan = new PreparedProcessExecution(command.Executable,
                                                         Array.AsReadOnly(command.Arguments.ToArray()),
                                                         workingDirectory, timeoutSeconds, workspaceExecutable,
                                                         mode == "shell" && command.Shell == "cmd"
                                                             ? command.CommandText
                                                             : null)
        };
    }

    public async Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
    {
        if (preparation.ExecutionPlan is not PreparedProcessExecution plan)
            throw new InvalidDataException("Shell execution requires the immutable plan produced by Prepare.");

        if (!Directory.Exists(plan.WorkingDirectory))
            return Failed($"Working directory not found: {_workspace.ToDisplay(plan.WorkingDirectory)}");

        _workspace.EnsureFinalTargetInside(plan.WorkingDirectory, true, "Working directory");
        if (plan.WorkspaceExecutable)
        {
            if (!File.Exists(plan.Executable))
                return Failed($"Executable not found: {_workspace.ToDisplay(plan.Executable)}");

            _workspace.EnsureFinalTargetInside(plan.Executable, false, "Executable");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_artifactRoot);
        var                    runId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}";
        var                    stdoutPath = Path.Combine(_artifactRoot, runId + ".stdout.txt");
        var                    stderrPath = Path.Combine(_artifactRoot, runId + ".stderr.txt");
        var                    secrets = _knownSecrets.Values.Where(value => !string.IsNullOrEmpty(value)).ToArray();
        var                    stdoutCapture = _captureFactory(stdoutPath, _outputCharacterLimit, secrets);
        var                    stderrCapture = _captureFactory(stderrPath, _outputCharacterLimit, secrets);
        ProcessExecutionResult execution;
        try
        {
            execution = await _processBackend.ExecuteAsync(plan, stdoutCapture, stderrCapture, cancellationToken)
                                             .ConfigureAwait(false);
        }
        catch (ProcessExecutionStartException ex)
        {
            stdoutCapture.Discard();
            stderrCapture.Discard();
            return Failed($"Failed to start executable '{plan.Executable}': {ex.Message}");
        }
        catch (Exception)
        {
            stdoutCapture.Discard();
            stderrCapture.Discard();
            throw;
        }

        var stdout   = stdoutCapture.Complete();
        var stderr   = stderrCapture.Complete();
        var exitCode = execution.ExitCode;
        var content  = FormatResult(exitCode, execution.TimedOut, plan.TimeoutSeconds, stdout, stderr);
        if (execution.Failure is { } failure)
            content += $"{Environment.NewLine}Failure: {failure.Message}{Environment.NewLine}";

        return new ToolResult
        {
            Succeeded          = !execution.TimedOut && exitCode == 0 && execution.Failure is null,
            Content            = content,
            ExitCode           = exitCode,
            TimedOut           = execution.TimedOut,
            OutputTruncated    = stdout.Truncated || stderr.Truncated,
            Stdout             = stdout.Content,
            Stderr             = stderr.Content,
            StdoutArtifactPath = stdout.ArtifactPath,
            StderrArtifactPath = stderr.ArtifactPath
        };
    }

    /// <summary>
    ///     准备不经过 shell 解释的直接进程调用。
    ///     Prepares a direct process invocation whose arguments are never interpreted as shell syntax.
    /// </summary>
    private PreparedCommand PrepareDirect(JsonObject args, string workingDirectory)
    {
        if (args["command"] is not null || args["shell"] is not null)
            throw new InvalidDataException("Direct mode does not accept 'command' or 'shell'.");

        var rawExecutable = JsonArgs.Required(args, "executable").Trim();
        ValidateText(rawExecutable, "executable");
        var arguments = JsonArgs.OptionalStringArray(args, "arguments", MaximumArguments);
        ValidateArguments(arguments);
        return new PreparedCommand(NormalizeExecutable(rawExecutable, workingDirectory), arguments,
                                   null, null);
    }

    /// <summary>
    ///     将显式 shell flavor 与完整命令转换为一个冻结的直接进程计划；不会使用 Start-Process。
    ///     Converts an explicit shell flavor and complete command into a frozen direct process plan; never uses Start-Process.
    /// </summary>
    private static PreparedCommand PrepareShell(JsonObject args)
    {
        if (args["executable"] is not null || args["arguments"] is not null)
            throw new InvalidDataException("Shell mode does not accept 'executable' or 'arguments'.");

        var command = JsonArgs.Required(args, "command");
        if (command.Length > MaximumCommandLength)
            throw new
                InvalidDataException($"Tool argument 'command' exceeds the {MaximumCommandLength} character limit.");

        ValidateCommandText(command);
        var shell = JsonArgs.Optional(args, "shell", DefaultShell()).ToLowerInvariant();
        var invocation = shell switch
        {
            "powershell" => new PreparedCommand(OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
                                                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command],
                                                shell, command),
            "cmd" when OperatingSystem.IsWindows() =>
                new PreparedCommand(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                                    ["/d", "/s", "/c", command], shell, command),
            "cmd"           => throw new InvalidDataException("The cmd shell is available only on Windows."),
            "bash" or "zsh" => new PreparedCommand(shell, ["-c", command], shell, command),
            "sh" => new PreparedCommand(OperatingSystem.IsWindows() ? "sh" : "/bin/sh",
                                        ["-c", command], shell, command),
            _ => throw new
                InvalidDataException("Tool argument 'shell' must be 'powershell', 'cmd', 'bash', 'zsh', or 'sh'.")
        };

        args["shell"]   = shell;
        args["command"] = command;
        return invocation;
    }

    private static string DefaultShell()
    {
        if (OperatingSystem.IsWindows()) return "powershell";

        var configured = Path.GetFileName(Environment.GetEnvironmentVariable("SHELL"));
        return configured is "bash" or "zsh" or "sh" ? configured : "sh";
    }

    private static void ValidateArguments(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            ValidateText(arguments[i], $"arguments[{i}]");
            if (arguments[i].Length > MaximumArgumentLength)
                throw new
                    InvalidDataException($"Tool argument 'arguments[{i}]' exceeds the {MaximumArgumentLength} character limit.");
        }
    }

    private string NormalizeExecutable(string executable, string workingDirectory)
    {
        if (!IsPathLike(executable)) return executable;

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
            throw new InvalidDataException("A relative executable path escapes the workspace root.");

        return full;
    }

    private static bool IsPathLike(string executable)
    {
        return Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar) ||
               executable.Contains(Path.AltDirectorySeparatorChar);
    }

    private static void ValidateText(string value, string argumentName)
    {
        if (value.IndexOf('\0') >= 0 || value.Any(character => char.IsControl(character) && character is not '\t'))
            throw new InvalidDataException($"Tool argument '{argumentName}' contains a control character.");
    }

    private static void ValidateCommandText(string command)
    {
        if (command.IndexOf('\0') >= 0 ||
            command.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
            throw new InvalidDataException("Tool argument 'command' contains an unsupported control character.");
    }

    private static void ValidateTimeout(int timeoutSeconds, string argumentName)
    {
        if (timeoutSeconds is < 1 or > MaximumTimeoutSeconds)
            throw new ArgumentOutOfRangeException(argumentName,
                                                  $"Timeout must be between 1 and {MaximumTimeoutSeconds} seconds.");
    }

    private static string FormatCommand(string executable, IReadOnlyList<string> arguments)
    {
        return string.Join(' ', new[] { executable }.Concat(arguments).Select(QuoteForDisplay));
    }

    private static string QuoteForDisplay(string value)
    {
        return value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"'
            : value;
    }

    private static string FormatResult(int?                  exitCode, bool timedOut, int timeoutSeconds,
                                       CapturedProcessOutput stdout,   CapturedProcessOutput stderr)
    {
        var output = new StringBuilder();
        output.Append("Exit code: ").AppendLine(exitCode?.ToString() ?? "unavailable");
        output.Append("Timed out: ").AppendLine(timedOut ? $"true ({timeoutSeconds}s)" : "false");
        output.AppendLine("stdout:").AppendLine(stdout.Content);
        output.AppendLine("stderr:").Append(stderr.Content);
        return output.ToString();
    }

    private static ToolResult Failed(string message)
    {
        return new ToolResult { Succeeded = false, Content = message };
    }

    private sealed record PreparedCommand(
        string                Executable,
        IReadOnlyList<string> Arguments,
        string?               Shell,
        string?               CommandText);
}

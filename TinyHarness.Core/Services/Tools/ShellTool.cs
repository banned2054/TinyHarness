using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.ProjectInstructions;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

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
    private const int MaximumWriteRoots                = 8;
    private const int DefaultOutputCharactersPerStream = 16 * 1024;

    /// <summary>
    ///     <para>网络申请在摘要前添加的准确标记；宿主路径用它剔除无意义的标记。</para>
    ///     <para>
    ///         The accurate summary marker prepended for network requests; the host path strips it
    ///         because the parameter carries no meaning there.
    ///     </para>
    /// </summary>
    internal const string NetworkSummaryPrefix = "[联网] ";

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
            },
            ["networkAccess"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("default", "enabled"),
                ["description"] =
                    "Requests network access for this invocation inside the configured sandbox " +
                    "(full network, including host-local proxies). Defaults to 'default', "      +
                    "which keeps the sandbox network restricted and requires per-call approval."
            },
            ["writeRoots"] = new JsonObject
            {
                ["type"]     = "array",
                ["items"]    = new JsonObject { ["type"] = "string" },
                ["maxItems"] = MaximumWriteRoots,
                ["description"] =
                    "Optional absolute directory roots outside the workspace this single invocation needs "   +
                    "to write (at most 8). Each request requires per-call approval at level 2 and the roots " +
                    "are frozen into the approved grant; omit the parameter to stay inside the workspace "    +
                    "baseline. Paths containing spaces or quotes are refused."
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

    private readonly IActiveCapabilitySource? _activeCapabilities;

    private readonly string                                                          _artifactRoot;
    private readonly Func<string, int, IReadOnlyList<string>, IProcessOutputCapture> _captureFactory;
    private readonly int                                                             _defaultTimeoutSeconds;
    private readonly ProcessExecutionPolicy?                                         _executionPolicy;
    private readonly SandboxGrantContext?                                            _grantContext;
    private readonly IReadOnlyDictionary<string, string>                             _knownSecrets;
    private readonly int                                                             _outputCharacterLimit;
    private readonly IProcessExecutionBackend                                        _processBackend;

    private readonly Workspace _workspace;

    public ShellTool(Workspace                            workspace, int? defaultTimeoutSeconds = null,
                     IReadOnlyDictionary<string, string>? knownSecrets         = null,
                     string?                              artifactRoot         = null,
                     int                                  outputCharacterLimit = DefaultOutputCharactersPerStream,
                     IProcessExecutionBackend?            processBackend       = null,
                     ProcessExecutionPolicy?              executionPolicy      = null,
                     IActiveCapabilitySource?             activeCapabilities   = null,
                     SandboxGrantContext?                 grantContext         = null)
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
        _activeCapabilities   = activeCapabilities;
        _grantContext         = grantContext;
    }

    internal ShellTool(Workspace workspace,
                       Func<string, int, IReadOnlyList<string>, IProcessOutputCapture> captureFactory,
                       int? defaultTimeoutSeconds = null, string? artifactRoot = null,
                       int outputCharacterLimit = DefaultOutputCharactersPerStream,
                       IProcessExecutionBackend? processBackend = null,
                       ProcessExecutionPolicy? executionPolicy = null,
                       IActiveCapabilitySource? activeCapabilities = null,
                       SandboxGrantContext? grantContext = null)
        : this(workspace, defaultTimeoutSeconds, null, artifactRoot, outputCharacterLimit, processBackend,
               executionPolicy, activeCapabilities, grantContext) =>
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));

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
        var networkAccess = ResolveNetworkAccess(args).ToLowerInvariant();
        if (networkAccess is not ("default" or "enabled"))
            throw new InvalidDataException("Tool argument 'networkAccess' must be 'default' or 'enabled'.");
        var requiresNetwork     = networkAccess == "enabled";
        var requestedWriteRoots = ResolveWriteRoots(args);
        if (requestedWriteRoots.Count > 0 &&
            !(_executionPolicy is { } writeRootsPolicy                               &&
              writeRootsPolicy.Backend == ProcessExecutionBackendKind.WindowsSandbox &&
              _activeCapabilities is not null                                        &&
              _grantContext is not null))
            throw new
                InvalidDataException("Tool argument 'writeRoots' requests sandbox write roots, but this shell "  +
                                     "does not run inside the Windows sandbox; omit the request or compose the " +
                                     "shell with the sandbox execution stack.");

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
        if (requestedWriteRoots.Count > 0)
        {
            // 写回校验后的规范化根：拒绝记忆指纹与审计读到的是 Prepare 认可的形态。
            var normalizedRoots = new JsonArray();
            foreach (var root in requestedWriteRoots) normalizedRoots.Add((JsonNode?)JsonValue.Create(root));
            args["writeRoots"] = normalizedRoots;
        }

        var identity = new JsonObject
        {
            ["mode"]             = mode,
            ["executable"]       = command.Executable,
            ["arguments"]        = normalizedArguments.DeepClone(),
            ["shell"]            = command.Shell,
            ["command"]          = command.CommandText,
            ["workingDirectory"] = mode == "shell" ? workingDirectory : null
        };
        // 写根申请进入命令身份：同一命令不同写根互不共享约束；缺省时不写入，身份串与历史逐字节一致。
        if (requestedWriteRoots.Count > 0)
            identity["writeRoots"] = new JsonArray(requestedWriteRoots
                                                  .Select(root => (JsonNode?)JsonValue.Create(root)).ToArray());
        var commandIdentity = identity.ToJsonString();
        // Grants bind the command identity plus the execution policy identity,
        // so an approval can never be reused to switch hosts or widen the
        // sandbox policy; denies keep the bare command identity and survive
        // backend changes (see ToolPreparation.SessionGrantConstraint).
        // 网络申请追加进授权约束：网络状态不同的同一命令不能复用既有批准。
        // R-identity：活动 grant 快照在 Prepare 冻结——解析为 grant 范围并追加进策略身份，
        // 执行期（backend）只消费计划内冻结值，不再查询任何实时源。
        var grantScope      = SandboxGrantScope.Empty;
        var policyIdentity  = _executionPolicy?.PolicyIdentity;
        if (_activeCapabilities is { } source)
        {
            if (_grantContext is not { } context)
                throw new
                    InvalidOperationException("An active capability source requires the sandbox grant context; compose them together.");

            if (_executionPolicy is not { } frozenPolicy ||
                frozenPolicy.Backend != ProcessExecutionBackendKind.WindowsSandbox)
                throw new
                    InvalidOperationException("An active capability source requires a Windows sandbox execution policy.");

            var snapshot         = source.Snapshot();
            // 本次调用显式申请的写根并入 grant 范围：校验（凭据/组件/home/C1 实例重叠与盘根拒绝）
            // 与活动授权同一入口，批准后与既有快照机制共用一条生效通道。
            var mergedWriteRoots = snapshot.AdditionalWriteRoots.Concat(requestedWriteRoots).ToArray();
            grantScope = WindowsSandboxComposer.ResolveGrantScope(context.Policy, context.Components,
                                                                  snapshot.AdditionalReadRoots,
                                                                  mergedWriteRoots,
                                                                  snapshot.MetadataWritePaths,
                                                                  context.PermissionSystemPaths);
            if (!grantScope.IsEmpty && policyIdentity is not null)
                policyIdentity = WindowsSandboxComposer.AppendGrantSection(policyIdentity, grantScope);
            // C2 获批读授权通道：与基线 deny-read 集冲突的获批根分流为 authorized 集，进入策略身份
            // 使授权执行身份与普通执行必不相同；C1 保护路径双向重叠在构造处恒拒，错误可见。
            if (grantScope.ReadRoots.Count > 0)
            {
                var authorizedReads = SandboxAuthorizedReads.Split(context.Policy.DenyReadPaths,
                                                                   grantScope.ReadRoots);
                WindowsSandboxComposer.RefusePermissionSystemOverlappingAuthorizedReads(
                    authorizedReads.AuthorizedReadRoots, context.PermissionSystemPaths);
                if (authorizedReads.HasAuthorizedReads && policyIdentity is not null)
                    policyIdentity = WindowsSandboxComposer.AppendAuthorizedReadSection(policyIdentity,
                                                                                        authorizedReads
                                                                                           .AuthorizedReadRoots);
            }
        }

        var grantConstraint = _executionPolicy is { } policy
            ? commandIdentity + "\nexecution-policy: " + policyIdentity +
              (requiresNetwork ? "\nnetwork-access: enabled" : string.Empty)
            : null;
        var displayCommand = mode == "direct"
            ? FormatCommand(command.Executable, command.Arguments)
            : $"{command.Shell}: {command.CommandText}";
        var summary =
            $"shell: {displayCommand} (cwd: {_workspace.ToDisplay(workingDirectory)}, timeout: {timeoutSeconds}s)";
        if (_executionPolicy is { } executionPolicy) summary += $" [{executionPolicy.DisplayName}]";
        // 网络申请在前缀准确标记，模型与审批者都能看到这不是默认网络状态。
        if (requiresNetwork) summary                         =  NetworkSummaryPrefix + summary;
        // 写根申请在摘要如实列出，审批者不经展开参数即可看到申请的写范围。
        if (requestedWriteRoots.Count > 0)
            summary += $" (write roots: {string.Join("; ", requestedWriteRoots)})";

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
            ExecutionPolicy        = policyIdentity,
            RequiresNetworkAccess  = requiresNetwork,
            RequestedWriteRoots    = requestedWriteRoots,
            TargetPaths = requestedWriteRoots.Count > 0
                ? requestedWriteRoots
                : [workingDirectory],
            InstructionScopes = workspaceExecutable
                ?
                [
                    new InstructionScope(workingDirectory, InstructionScopeKind.Directory),
                    new InstructionScope(command.Executable, InstructionScopeKind.File)
                ]
                : [new InstructionScope(workingDirectory, InstructionScopeKind.Directory)],
            ExecutionPlan = new PreparedProcessExecution(command.Executable,
                                                         Array.AsReadOnly(command.Arguments.ToArray()),
                                                         workingDirectory, timeoutSeconds, workspaceExecutable,
                                                         mode == "shell" && command.Shell == "cmd"
                                                             ? command.CommandText
                                                             : null)
            {
                // 经对象初始化器赋值：兼容位置参数默认值与 init 属性两种契约形态。
                NetworkAccessEnabled = requiresNetwork,
                // R-identity：接入快照源的计划恒存冻结范围（Empty=冻结时无活动 grant）；
                // 未接入快照源的计划不携带该字段，派发复核对二者区别对待。
                GrantScope           = _activeCapabilities is null ? null : grantScope
            }
        };
    }

    public async Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
    {
        if (preparation.ExecutionPlan is not PreparedProcessExecution plan)
            throw new InvalidDataException("Shell execution requires the immutable plan produced by Prepare.");

        if (!Directory.Exists(plan.WorkingDirectory))
            return Failed($"Working directory not found: {_workspace.ToDisplay(plan.WorkingDirectory)}");

        _workspace.EnsureFinalTargetInside(plan.WorkingDirectory, true, "Working directory");
        preparation.Coverage?.EnsureCoveredDirectory(plan.WorkingDirectory);
        if (plan.WorkspaceExecutable)
        {
            if (!File.Exists(plan.Executable))
                return Failed($"Executable not found: {_workspace.ToDisplay(plan.Executable)}");

            _workspace.EnsureFinalTargetInside(plan.Executable, false, "Executable");
            preparation.Coverage?.EnsureCoveredDirectory(Path.GetDirectoryName(plan.Executable)!);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_artifactRoot);
        var                    runId         = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}";
        var                    stdoutPath    = Path.Combine(_artifactRoot, runId + ".stdout.txt");
        var                    stderrPath    = Path.Combine(_artifactRoot, runId + ".stderr.txt");
        // 本地工具结果也是后续模型上下文，保留原文；凭据环境隔离仍由执行后端负责。
        var                    stdoutCapture = _captureFactory(stdoutPath, _outputCharacterLimit, []);
        var                    stderrCapture = _captureFactory(stderrPath, _outputCharacterLimit, []);
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
    ///     <para>
    ///         严格读取 networkAccess：字符串值原样返回（交由调用方校验取值）；缺失或显式 null 视为
    ///         未提供并回退 'default'；其他类型（布尔、数字、对象、数组）抛 InvalidDataException，
    ///         文案列出合法取值。
    ///     </para>
    ///     <para>
    ///         Reads networkAccess strictly: string values return verbatim (the caller
    ///         validates the allowed values); a missing or explicit null node counts as
    ///         not provided and falls back to 'default'; any other type (Boolean,
    ///         number, object, array) throws InvalidDataException with a message
    ///         listing the legal values.
    ///     </para>
    /// </summary>
    private static string ResolveNetworkAccess(JsonObject args)
    {
        var node = args["networkAccess"];
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var raw)) return raw;
            // 显式 null 与缺失同义：视为未提供。
            if (value.GetValueKind() == JsonValueKind.Null) return "default";
        }
        else if (node is null)
        {
            return "default";
        }

        throw
            new InvalidDataException("Tool argument 'networkAccess' must be a string, either 'default' or 'enabled'.");
    }

    /// <summary>
    ///     <para>
    ///         严格校验并规范化 writeRoots：绝对路径、控制字符拒绝、含空格或引号的路径按 CmdSafePath
    ///         纪律明确拒绝（B3e 实测：经 argv 序列化后会产生宿主同形的路径畸变，不静默放行），
    ///         文件系统根（盘根）拒绝，数量上限 <see cref="MaximumWriteRoots" />，大小写不敏感去重保序。
    ///     </para>
    ///     <para>
    ///         Strictly validates and normalizes writeRoots: absolute paths, control characters refused,
    ///         paths containing spaces or quotes rejected explicitly under the CmdSafePath discipline
    ///         (B3e measured: argv serialization distorts them the same way on the host, never silently
    ///         accepted), file-system roots (drive roots) refused, at most <see cref="MaximumWriteRoots" />
    ///         entries, deduplicated case-insensitively while preserving order.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<string> ResolveWriteRoots(JsonObject args)
    {
        var raw = JsonArgs.OptionalStringArray(args, "writeRoots", MaximumWriteRoots);
        if (raw.Count == 0) return [];

        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();
        for (var i = 0; i < raw.Count; i++)
        {
            var candidate = raw[i];
            ValidateText(candidate, $"writeRoots[{i}]");
            // CmdSafePath 纪律：含空格或引号的路径经 argv 引号转义会产生宿主同形畸变，明确拒绝。
            if (candidate.Any(char.IsWhiteSpace) || candidate.Contains('"'))
                throw new
                    InvalidDataException($"Tool argument 'writeRoots[{i}]' must not contain spaces or quotes; " +
                                         "such paths distort through command-line quoting: "                    +
                                         $"'{candidate}'.");
            if (!Path.IsPathFullyQualified(candidate))
                throw new
                    InvalidDataException($"Tool argument 'writeRoots[{i}]' must be a fully qualified absolute path: '{candidate}'.");

            string full;
            try
            {
                full = Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new InvalidDataException($"Tool argument 'writeRoots[{i}]' is not a valid path: '{candidate}'.",
                                               ex);
            }

            // 盘根/文件系统根不做写根：根级写范围等于全盘授权，一律拒绝。
            if (string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase))
                throw new
                    InvalidDataException($"Tool argument 'writeRoots[{i}]' must not be a file-system root: '{full}'.");

            if (seen.Add(full)) results.Add(full);
        }

        return results;
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

    private static bool IsPathLike(string executable) =>
        Path.IsPathRooted(executable)                    ||
        executable.Contains(Path.DirectorySeparatorChar) ||
        executable.Contains(Path.AltDirectorySeparatorChar);

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

    private static string FormatCommand(string executable, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { executable }.Concat(arguments).Select(QuoteForDisplay));

    private static string QuoteForDisplay(string value) => value.Any(char.IsWhiteSpace) || value.Contains('"')
        ? '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"'
        : value;

    private static string FormatResult(
        int? exitCode, bool timedOut, int timeoutSeconds, CapturedProcessOutput stdout, CapturedProcessOutput stderr)
    {
        var output = new StringBuilder();
        output.Append("Exit code: ").AppendLine(exitCode?.ToString() ?? "unavailable");
        output.Append("Timed out: ").AppendLine(timedOut ? $"true ({timeoutSeconds}s)" : "false");
        output.AppendLine("stdout:").AppendLine(stdout.Content);
        output.AppendLine("stderr:").Append(stderr.Content);
        return output.ToString();
    }

    private static ToolResult Failed(string message) => new() { Succeeded = false, Content = message };

    private sealed record PreparedCommand(
        string                Executable,
        IReadOnlyList<string> Arguments,
        string?               Shell,
        string?               CommandText);
}

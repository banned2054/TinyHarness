using TinyHarness.Core.Agent;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Configuration;
using TinyHarness.Core.Context;
using TinyHarness.Core.Permissions;
using TinyHarness.Core.Persistence;
using TinyHarness.Core.Runtime;
using TinyHarness.Core.Tools;

namespace TinyHarness.Cli;

/// <summary>
/// TinyHarness 命令行应用的组合根与入口调度器。
/// Composition root and entry-point dispatcher for the TinyHarness command-line application.
/// </summary>
internal static class Program
{
    /// <summary>
    /// CLI 入口。解析 smoke、run 或无动词调用方式，并把退出码交还操作系统。
    /// CLI entry point that dispatches smoke, run, or verbless invocation forms and returns an OS exit code.
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await DispatchAsync(args).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Run cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            // Convert boundary failures (configuration, fixture setup, persistence
            // construction, and similar startup errors) into normal CLI output so
            // Windows never presents a CLR application-error dialog.
            await Console.Error.WriteLineAsync($"TinyHarness failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> DispatchAsync(string[] args)
    {
        if (args.Length == 0)
        {
            await Console.Error.WriteLineAsync("Usage:");
            await Console.Error.WriteLineAsync("  tinyharness smoke [--config <path>]");
            await Console.Error.WriteLineAsync("  tinyharness run [--config <path>] \"your prompt\"");
            await Console.Error.WriteLineAsync("  tinyharness \"your prompt\"");
            return 2;
        }

        var first = args[0];
        if (string.Equals(first, "process-smoke-child", StringComparison.Ordinal))
        {
            await Console.Out.WriteLineAsync("process stdout");
            await Console.Error.WriteLineAsync("process stderr");
            return 0;
        }

        if (string.Equals(first, "smoke", StringComparison.Ordinal))
        {
            var (_, configPath) = ParseArgs(args);
            return await RunSmokeAsync(configPath);
        }

        var verblessPrompt = string.Equals(first, "run", StringComparison.Ordinal);
        var (promptParts, configPath2) = ParseArgs(verblessPrompt ? args[1..] : args);
        var prompt = string.Join(' ', promptParts).Trim();
        if (prompt.Length == 0)
        {
            await Console.Error.WriteLineAsync("A prompt is required.");
            return 2;
        }

        return await RunAsync(prompt, configPath2);
    }

    /// <summary>
    /// 从命令行参数中提取可选配置路径，其余片段保留为用户提示词。
    /// Extracts the optional configuration path and keeps all remaining arguments as prompt fragments.
    /// </summary>
    private static (List<string> PromptArgs, string? ConfigPath) ParseArgs(string[] args)
    {
        var     promptArgs = new List<string>();
        string? configPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--config", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                configPath = args[++i];
            }
            else
            {
                promptArgs.Add(args[i]);
            }
        }

        return (promptArgs, configPath);
    }

    /// <summary>
    /// 加载真实运行配置、组装模型与工具权限依赖，运行一次 Agent 任务并输出摘要。
    /// Loads live configuration, composes model/tool/permission dependencies, runs one agent task, and prints its summary.
    /// </summary>
    private static async Task<int> RunAsync(string prompt, string? configPath)
    {
        using var cts                = new CancellationTokenSource();
        using var cancelRegistration = new ConsoleCancellation(cts);
        var config = await ConfigurationLoader.LoadAsync(configPath ?? "tinyharness.json", cts.Token)
                                              .ConfigureAwait(false);

        await Console.Out.WriteLineAsync($"TinyHarness run");
        await Console.Out.WriteLineAsync($"  model     : {config.Model}");
        await Console.Out.WriteLineAsync($"  endpoint  : {config.Endpoint}");
        await Console.Out.WriteLineAsync($"  workspace : {config.WorkspaceRoot}");
        await Console.Out.WriteLineAsync($"  tools     : list_files, search_text, read_file, apply_patch, shell");

        if (string.IsNullOrWhiteSpace(config.Model))
        {
            await Console.Error.WriteLineAsync("A model must be configured (model= in tinyharness.json).");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(config.Endpoint))
        {
            await Console.Error.WriteLineAsync("An endpoint must be configured (endpoint= in tinyharness.json).");
            return 1;
        }

        var apiKey = ReadApiKey(config.ApiKeyEnvironmentVariable);
        if (apiKey is null)
        {
            await Console.Error
                         .WriteLineAsync($"No API key found. Set the environment variable '{config.ApiKeyEnvironmentVariable}'" +
                                         $" (configured as apiKeyEnvironmentVariable) before running.");
            return 1;
        }

        // Assemble the real model transport, workspace tools and interactive
        // permission flow used by normal CLI runs.
        IChatCompletionClient model = new OpenAiChatCompletionClient(config.Model, config.Endpoint, apiKey);

        var               workspace   = new Workspace(config.WorkspaceRoot);
        var               tools       = BuildTools(workspace, config, apiKey);
        var               permissions = new PermissionEngine(config.WorkspaceRoot, config.CommandRules);
        IApprovalProvider approver    = new ConsoleApprovalProvider();
        var options = new AgentOptions
        {
            Model                     = config.Model,
            MaxAgentSteps             = config.MaxAgentSteps,
            DefaultToolTimeoutSeconds = config.DefaultToolTimeoutSeconds,
            Context = new ContextOptions
            {
                ContextWindowTokens       = config.ContextWindowTokens,
                ReservedOutputTokens      = config.ReservedOutputTokens,
                CompactionThresholdTokens = config.CompactionThreshold,
            },
        };

        var recorder = new FileRunRecorder(config.SessionDirectory, knownSecrets : KnownSecrets(config, apiKey));
        var loop     = new AgentLoop(model, tools, options, permissions, approver, recorder);
        loop.ContextCompacted += change =>
            Console.Out.WriteLine($"Context: {change.BeforeTokens:N0} -> {change.AfterTokens:N0} tokens after compaction");
        const string systemPrompt =
            "You are TinyHarness, a local coding harness that inspects and modifies a workspace. " +
            "Use list_files, search_text and read_file to inspect, apply_patch to modify files, and shell to run commands. " +
            "For ordinary commands use shell mode 'direct' with executable and an arguments array. Use mode 'shell' " +
            "with a shell flavor and command string only when pipelines, redirection, or other shell syntax is required. " +
            "Tool paths are relative to the workspace root. " +
            "apply_patch takes a unified diff: '--- a/path' and '+++ b/path' headers, then '@@ -s[,c] +s[,c] @@' " +
            "hunks with ' ' (context), '-' (remove) and '+' (add) line prefixes. New files use '--- /dev/null'. " +
            "Writes and commands may require the user's approval before they run; a denial is returned to you so you can adjust.";

        var result = await loop.RunAsync(systemPrompt, prompt, cts.Token).ConfigureAwait(false);

        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync($"status     : {result.Status}");
        await Console.Out.WriteLineAsync($"steps      : {result.Steps}");
        await Console.Out.WriteLineAsync($"toolExecs  : {result.ToolExecutions}");
        await Console.Out.WriteLineAsync($"final      : {result.FinalMessage}");
        await WritePersistencePathsAsync(recorder).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            await Console.Out.WriteLineAsync($"error      : {result.Error}");
        }

        return result.Status switch
        {
            AgentStatus.Completed => 0,
            AgentStatus.Cancelled => 130,
            _                     => 1,
        };
    }

    /// <summary>
    /// 把完整的只读与写入工具集合绑定到同一个工作区。
    ///
    /// Binds the complete read/write tool set to one workspace.
    /// </summary>
    private static ToolRegistry BuildTools(Workspace workspace, TinyHarnessConfig config, string? apiKey = null) =>
        new([
            new ListFilesTool(workspace), new SearchTextTool(workspace), new ReadFileTool(workspace),
            new ApplyPatchTool(workspace), new ShellTool(workspace, config.DefaultToolTimeoutSeconds,
                                                         KnownSecrets(config, apiKey)),
        ]);

    /// <summary>
    /// 为进程环境清理与输出脱敏提供已知 secret；不记录 secret 值。
    /// Supplies known secrets for child-environment removal and output redaction without logging their values.
    /// </summary>
    private static IReadOnlyDictionary<string, string> KnownSecrets(TinyHarnessConfig config, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKeyEnvironmentVariable) || string.IsNullOrEmpty(apiKey))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [config.ApiKeyEnvironmentVariable] = apiKey,
        };
    }

    /// <summary>
    /// 只从配置指定的环境变量读取 API key；绝不记录或嵌入密钥值。
    ///
    /// Reads the API key from the configured environment variable. Never logs or
    /// embeds the value; only its presence is reported.
    /// </summary>
    private static string? ReadApiKey(string environmentVariable)
    {
        if (string.IsNullOrWhiteSpace(environmentVariable))
        {
            return null;
        }

        var value = Environment.GetEnvironmentVariable(environmentVariable);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// 使用脚本模型和临时工作区运行离线冒烟流程，检查读写工具闭环及审批流程。
    ///
    /// Runs an offline smoke flow with a scripted model and disposable workspace,
    /// exercising the read/write tool loop and approval flow without network access.
    /// </summary>
    private static async Task<int> RunSmokeAsync(string? configPath)
    {
        using var cts                = new CancellationTokenSource();
        using var cancelRegistration = new ConsoleCancellation(cts);
        var config = await ConfigurationLoader.LoadAsync(configPath ?? "tinyharness.json", cts.Token)
                                              .ConfigureAwait(false);

        await Console.Out.WriteLineAsync("TinyHarness smoke");
        await Console.Out.WriteLineAsync($"  model        : {config.Model}");

        if (string.IsNullOrWhiteSpace(config.Model))
        {
            await Console.Error.WriteLineAsync("A model must be configured (model= in tinyharness.json).");
            return 1;
        }

        using var fixture = await SmokeWorkspace.CreateAsync(cts.Token).ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"  fixtureWs    : {fixture.Root}");

        var plan      = SmokeScript.Build(ProcessProbeCommand(), fixture.Root);
        var script    = new SmokeScriptClient(plan.Steps);
        var workspace = new Workspace(fixture.Root);
        var tools     = BuildTools(workspace, config);
        // Smoke intentionally uses the default policy so its side-effect
        // approvals remain deterministic regardless of the user's live rules.
        var permissions = new PermissionEngine(fixture.Root);
        var approver    = new SmokeApprover();
        var options = new AgentOptions
        {
            Model                     = config.Model,
            MaxAgentSteps             = config.MaxAgentSteps,
            DefaultToolTimeoutSeconds = config.DefaultToolTimeoutSeconds,
            Context = new ContextOptions
            {
                ContextWindowTokens       = config.ContextWindowTokens,
                ReservedOutputTokens      = config.ReservedOutputTokens,
                CompactionThresholdTokens = config.CompactionThreshold,
            },
        };

        var recorder = new FileRunRecorder(Path.Combine(Path.GetTempPath(), "tinyharness-smoke-runs"));
        var loop = new AgentLoop(script, tools, options, permissions, approver, recorder);
        const string systemPrompt =
            "You are TinyHarness, a local coding harness that inspects and modifies a workspace. " +
            "Use list_files, search_text and read_file to inspect, apply_patch to modify files, and shell to run commands. " +
            "Use shell mode 'direct' for executable/arguments and mode 'shell' only for shell syntax. " +
            "Tool paths are relative to the workspace root. " +
            "Writes may require the user's approval before they are applied.";
        var result = await loop
                          .RunAsync(systemPrompt, "smoke: inspect the fixture workspace and apply one patch.",
                                    cts.Token).ConfigureAwait(false);

        await Console.Out.WriteLineAsync($"  status       : {result.Status}");
        await Console.Out.WriteLineAsync($"  steps        : {result.Steps}");
        await Console.Out.WriteLineAsync($"  toolExecs    : {result.ToolExecutions}");
        await Console.Out.WriteLineAsync($"  finalMessage : {result.FinalMessage}");
        await WritePersistencePathsAsync(recorder, "  ").ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            await Console.Out.WriteLineAsync($"  error        : {result.Error}");
        }

        if (result.Status != AgentStatus.Completed)
        {
            return 1;
        }

        // The scripted client throws on any closure or content mismatch, which the
        // Agent loop surfaces as Failed above; a Completed run with the expected
        // tool count and expected approval prompts (apply_patch, direct process,
        // and explicit shell) mean every
        // tool really executed inside the native artifact through the permission
        // flow.
        return result.ToolExecutions == plan.ExpectedToolExecutions &&
               approver.Prompts      == plan.ExpectedApprovalPrompts
            ? 0
            : 1;
    }

    /// <summary>
    /// 只报告实际存在的持久化产物，避免保存失败时把预期路径误报为已成功生成。
    /// Reports only persistence artifacts that actually exist, so an expected path is never presented as saved.
    /// </summary>
    private static async Task WritePersistencePathsAsync(FileRunRecorder recorder, string prefix = "")
    {
        if (File.Exists(recorder.SessionPath))
        {
            await Console.Out.WriteLineAsync($"{prefix}session    : {recorder.SessionPath}");
        }

        if (File.Exists(recorder.AuditPath))
        {
            await Console.Out.WriteLineAsync($"{prefix}audit      : {recorder.AuditPath}");
        }
    }

    /// <summary>
    /// 构造调用当前 CLI 隐藏 probe 动词的结构化命令；兼容 framework-dependent 与原生发布入口。
    /// Builds a structured invocation of this CLI's hidden probe verb for both framework-dependent and native hosts.
    /// </summary>
    private static ProcessProbe ProcessProbeCommand()
    {
        var executable = Environment.ProcessPath
                      ?? throw new InvalidOperationException("Cannot determine the current process executable.");
        var entryAssembly = Environment.GetCommandLineArgs()[0];
        var isDotnetHost = string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet",
                                         StringComparison.OrdinalIgnoreCase);
        return isDotnetHost
            ? new ProcessProbe(executable, [entryAssembly, "process-smoke-child"])
            : new ProcessProbe(executable, ["process-smoke-child"]);
    }

    /// <summary>
    /// 将 Ctrl+C 转换为任务取消，并在运行结束时可靠移除全局事件处理器。
    /// Converts Ctrl+C into task cancellation and reliably detaches the global handler afterward.
    /// </summary>
    private sealed class ConsoleCancellation : IDisposable
    {
        private readonly CancellationTokenSource   _source;
        private readonly ConsoleCancelEventHandler _handler;

        public ConsoleCancellation(CancellationTokenSource source)
        {
            _source                =  source;
            _handler               =  OnCancel;
            Console.CancelKeyPress += _handler;
        }

        public void Dispose() => Console.CancelKeyPress -= _handler;

        private void OnCancel(object? sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            _source.Cancel();
        }
    }
}

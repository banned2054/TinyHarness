using TinyHarness.Core.Agent;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Configuration;
using TinyHarness.Core.Permissions;
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
        if (args.Length == 0)
        {
            await Console.Error.WriteLineAsync("Usage:");
            await Console.Error.WriteLineAsync("  tinyharness smoke [--config <path>]");
            await Console.Error.WriteLineAsync("  tinyharness run [--config <path>] \"your prompt\"");
            await Console.Error.WriteLineAsync("  tinyharness \"your prompt\"");
            return 2;
        }

        var first = args[0];
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
        using var cts = new CancellationTokenSource();
        var config = await ConfigurationLoader.LoadAsync(configPath ?? "tinyharness.json", cts.Token)
                                              .ConfigureAwait(false);

        await Console.Out.WriteLineAsync($"TinyHarness run");
        await Console.Out.WriteLineAsync($"  model     : {config.Model}");
        await Console.Out.WriteLineAsync($"  endpoint  : {config.Endpoint}");
        await Console.Out.WriteLineAsync($"  workspace : {config.WorkspaceRoot}");
        await Console.Out.WriteLineAsync($"  tools     : list_files, search_text, read_file, apply_patch");

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
        var               tools       = BuildTools(workspace);
        var               permissions = new PermissionEngine(config.WorkspaceRoot);
        IApprovalProvider approver    = new ConsoleApprovalProvider();
        var options = new AgentOptions
        {
            Model                     = config.Model,
            MaxAgentSteps             = config.MaxAgentSteps,
            DefaultToolTimeoutSeconds = config.DefaultToolTimeoutSeconds,
        };

        var loop = new AgentLoop(model, tools, options, permissions, approver);
        const string systemPrompt =
            "You are TinyHarness, a local coding harness that inspects and modifies a workspace. "                 +
            "Use list_files, search_text and read_file to inspect, and apply_patch to modify files. "              +
            "Tool paths are relative to the workspace root. "                                                      +
            "apply_patch takes a unified diff: '--- a/path' and '+++ b/path' headers, then '@@ -s[,c] +s[,c] @@' " +
            "hunks with ' ' (context), '-' (remove) and '+' (add) line prefixes. New files use '--- /dev/null'. "  +
            "Writes may require the user's approval before they are applied; a denial is returned to you so you can adjust.";

        var result = await loop.RunAsync(systemPrompt, prompt, cts.Token).ConfigureAwait(false);

        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync($"status     : {result.Status}");
        await Console.Out.WriteLineAsync($"steps      : {result.Steps}");
        await Console.Out.WriteLineAsync($"toolExecs  : {result.ToolExecutions}");
        await Console.Out.WriteLineAsync($"final      : {result.FinalMessage}");

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
    private static ToolRegistry BuildTools(Workspace workspace) => new([
        new ListFilesTool(workspace), new SearchTextTool(workspace), new ReadFileTool(workspace),
        new ApplyPatchTool(workspace),
    ]);

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
        using var cts = new CancellationTokenSource();
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

        var plan        = SmokeScript.Build();
        var script      = new SmokeScriptClient(plan.Steps);
        var workspace   = new Workspace(fixture.Root);
        var tools       = BuildTools(workspace);
        var permissions = new PermissionEngine(fixture.Root);
        var approver    = new SmokeApprover();
        var options = new AgentOptions
        {
            Model                     = config.Model,
            MaxAgentSteps             = config.MaxAgentSteps,
            DefaultToolTimeoutSeconds = config.DefaultToolTimeoutSeconds,
        };

        var loop = new AgentLoop(script, tools, options, permissions, approver);
        const string systemPrompt =
            "You are TinyHarness, a local coding harness that inspects and modifies a workspace. "    +
            "Use list_files, search_text and read_file to inspect, and apply_patch to modify files. " +
            "Tool paths are relative to the workspace root. "                                         +
            "Writes may require the user's approval before they are applied.";
        var result = await loop
                          .RunAsync(systemPrompt, "smoke: inspect the fixture workspace and apply one patch.",
                                    cts.Token).ConfigureAwait(false);

        await Console.Out.WriteLineAsync($"  status       : {result.Status}");
        await Console.Out.WriteLineAsync($"  steps        : {result.Steps}");
        await Console.Out.WriteLineAsync($"  toolExecs    : {result.ToolExecutions}");
        await Console.Out.WriteLineAsync($"  finalMessage : {result.FinalMessage}");
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
        // tool count and exactly one approval prompt (the apply_patch) means every
        // tool really executed inside the native artifact through the permission
        // flow.
        return result.ToolExecutions == plan.ExpectedToolExecutions && approver.Prompts == 1 ? 0 : 1;
    }
}

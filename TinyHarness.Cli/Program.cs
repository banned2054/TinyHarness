using TinyHarness.Core.Agent;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Configuration;
using TinyHarness.Core.Runtime;
using TinyHarness.Core.Tools;

namespace TinyHarness.Cli;

internal static class Program
{
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

    private static async Task<int> RunAsync(string prompt, string? configPath)
    {
        using var cts = new CancellationTokenSource();
        var config = await ConfigurationLoader.LoadAsync(configPath ?? "tinyharness.json", cts.Token)
                                              .ConfigureAwait(false);

        await Console.Out.WriteLineAsync($"TinyHarness run");
        await Console.Out.WriteLineAsync($"  model     : {config.Model}");
        await Console.Out.WriteLineAsync($"  endpoint  : {config.Endpoint}");
        await Console.Out.WriteLineAsync($"  workspace : {config.WorkspaceRoot}");
        await Console.Out.WriteLineAsync($"  tools     : list_files, search_text, read_file");

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

        // M3: read-only tool set over the real transport. Write tooling and the
        // Permission Engine land in M4.
        IChatCompletionClient model = new OpenAiChatCompletionClient(config.Model, config.Endpoint, apiKey);

        var workspace = new Workspace(config.WorkspaceRoot);
        var tools     = BuildReadOnlyTools(workspace);
        var options = new AgentOptions
        {
            Model                     = config.Model,
            MaxAgentSteps             = config.MaxAgentSteps,
            DefaultToolTimeoutSeconds = config.DefaultToolTimeoutSeconds,
        };

        var loop = new AgentLoop(model, tools, options);
        const string systemPrompt =
            "You are TinyHarness, a local coding harness inspecting a read-only workspace. " +
            "Use the tools (list_files, search_text, read_file) to inspect the repository. " +
            "Tool paths are relative to the workspace root. You cannot modify files in this milestone.";

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
    /// M3 composition: the read-only tool set bound to a workspace. The
    /// Permission Engine (M4) later authorizes between Prepare and Execute.
    /// </summary>
    private static ToolRegistry BuildReadOnlyTools(Workspace workspace)
        => new ToolRegistry([
            new ListFilesTool(workspace), new SearchTextTool(workspace), new ReadFileTool(workspace),
        ]);

    /// <summary>
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
    /// Offline smoke path (M3): a scripted client drives the real read-only tools
    /// through the Agent loop, so the published native artifact actually executes
    /// list_files / search_text / read_file (toolExecs &gt; 0) with no network or
    /// key. Fixture files live in a disposable temp workspace, never in the repo.
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

        var plan      = SmokeScript.Build();
        var script    = new SmokeScriptClient(plan.Steps);
        var workspace = new Workspace(fixture.Root);
        var tools     = BuildReadOnlyTools(workspace);
        var options = new AgentOptions
        {
            Model                     = config.Model,
            MaxAgentSteps             = config.MaxAgentSteps,
            DefaultToolTimeoutSeconds = config.DefaultToolTimeoutSeconds,
        };

        var loop = new AgentLoop(script, tools, options);
        const string systemPrompt =
            "You are TinyHarness, a local coding harness inspecting a read-only workspace. " +
            "Use the tools (list_files, search_text, read_file) to inspect the repository. " +
            "Tool paths are relative to the workspace root. You cannot modify files in this milestone.";
        var result = await loop
                          .RunAsync(systemPrompt, "smoke: inspect the fixture workspace with the read-only tools.",
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
        // tool count means every tool really executed inside the native artifact.
        return result.ToolExecutions == plan.ExpectedToolExecutions ? 0 : 1;
    }
}

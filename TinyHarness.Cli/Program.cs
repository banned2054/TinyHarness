using TinyHarness.Core.Agent;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Configuration;
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
        var    promptArgs = new List<string>();
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
            await Console.Error.WriteLineAsync(
                $"No API key found. Set the environment variable '{config.ApiKeyEnvironmentVariable}'" +
                $" (configured as apiKeyEnvironmentVariable) before running.");
            return 1;
        }

        // M2: real transport backed by the official OpenAI SDK. No tools yet;
        // the read-only tool loop arrives in milestone M3.
        IChatCompletionClient model =
            new OpenAiChatCompletionClient(config.Model, config.Endpoint, apiKey);
        var tools   = new ToolRegistry([]);
        var options = new AgentOptions
        {
            Model                     = config.Model,
            MaxAgentSteps             = config.MaxAgentSteps,
            DefaultToolTimeoutSeconds = config.DefaultToolTimeoutSeconds,
        };

        var loop         = new AgentLoop(model, tools, options);
        const string systemPrompt =
            "You are TinyHarness, a local coding harness. Help inspect and fix the workspace.";

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

    private static async Task<int> RunSmokeAsync(string? configPath)
    {
        using var cts = new CancellationTokenSource();
        var config = await ConfigurationLoader.LoadAsync(configPath ?? "tinyharness.json", cts.Token)
                                              .ConfigureAwait(false);

        await Console.Out.WriteLineAsync($"TinyHarness smoke");
        await Console.Out.WriteLineAsync($"  model        : {config.Model}");
        await Console.Out.WriteLineAsync($"  workspace    : {config.WorkspaceRoot}");
        await Console.Out.WriteLineAsync($"  contextWindow: {config.ContextWindowTokens}");

        if (string.IsNullOrWhiteSpace(config.Model))
        {
            await Console.Error.WriteLineAsync("A model must be configured (model= in tinyharness.json).");
            return 1;
        }

        // Offline smoke path: a fake client that always replies with plain text,
        // kept so the smoke verb needs no network or key. The real transport is
        // OpenAiChatCompletionClient (see RunAsync).
        var model = new OfflineTextClient();
        var tools = new ToolRegistry([]);
        var options = new AgentOptions
        {
            Model                     = config.Model,
            MaxAgentSteps             = config.MaxAgentSteps,
            DefaultToolTimeoutSeconds = config.DefaultToolTimeoutSeconds,
        };

        var loop         = new AgentLoop(model, tools, options);
        var systemPrompt = "You are TinyHarness, a local coding harness. Help inspect and fix the workspace.";
        var userPrompt   = "smoke: reply with a short confirmation.";

        var result = await loop.RunAsync(systemPrompt, userPrompt, cts.Token).ConfigureAwait(false);

        await Console.Out.WriteLineAsync($"  status       : {result.Status}");
        await Console.Out.WriteLineAsync($"  steps        : {result.Steps}");
        await Console.Out.WriteLineAsync($"  toolExecs    : {result.ToolExecutions}");
        await Console.Out.WriteLineAsync($"  finalMessage : {result.FinalMessage}");

        return result.Status switch
        {
            AgentStatus.Completed => 0,
            AgentStatus.Cancelled => 130,
            _                     => 1,
        };
    }

    /// <summary>
    /// Offline fake model client for the smoke verb: returns a fixed plain-text
    /// completion without network. The real transport is
    /// <see cref="OpenAiChatCompletionClient"/>.
    /// </summary>
    private sealed class OfflineTextClient : IChatCompletionClient
    {
        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
                                                                     [System.Runtime.CompilerServices.
                                                                         EnumeratorCancellation]
                                                                     CancellationToken cancellationToken)
        {
            const string reply = "Offline smoke ok: the Agent loop is running.";
            foreach (var chunk in reply)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatStreamEvent
                    { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = chunk.ToString() };
                await Task.Yield();
            }

            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End };
        }
    }
}

using TinyHarness.Core.Agent;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Configuration;
using TinyHarness.Core.Tools;

namespace TinyHarness.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // M1 CLI supports a single offline smoke verb that exercises the full
        // dependency construction and Agent loop with a fake model client.
        if (args.Length == 0)
        {
            await Console.Error.WriteLineAsync("Usage:");
            await Console.Error.WriteLineAsync("  tinyharness smoke [--config <path>]");
            return 2;
        }

        var (verb, configPath) = ParseArgs(args);
        if (!string.Equals(verb, "smoke", StringComparison.Ordinal))
        {
            await Console.Error.WriteLineAsync($"Unknown verb '{verb}'. Supported: smoke");
            return 2;
        }

        return await RunSmokeAsync(configPath);
    }

    private static (string TrimmedVerb, string? ConfigPath) ParseArgs(string[] args)
    {
        var     verb       = args[0];
        string? configPath = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--config", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                configPath = args[++i];
            }
        }

        return (verb, configPath);
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

        // M1 offline path: a fake client that always replies with plain text.
        // Real transport lands in milestone M2.
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
    /// M1 fake model client: returns a fixed plain-text completion. Replaced by the
    /// real transport in M2. Kept here so the offline smoke path needs no network.
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

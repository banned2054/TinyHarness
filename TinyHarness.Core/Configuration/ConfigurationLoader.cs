using System.Text.Json.Nodes;

namespace TinyHarness.Core.Configuration;

/// <summary>
/// Builds a config document from a JSON file. Uses <see cref="JsonObject"/> for
/// manual, reflection-free binding so the config path stays NativeAOT-safe
/// without a source-generated context at this layer. Values not present in the
/// file fall back to the documented defaults in <see cref="TinyHarnessConfig"/>.
/// </summary>
public static class ConfigurationLoader
{
    public static async Task<TinyHarnessConfig> LoadAsync(string configJsonPath, CancellationToken cancellationToken)
    {
        var config = new TinyHarnessConfig();
        if (!string.IsNullOrWhiteSpace(configJsonPath) && File.Exists(configJsonPath))
        {
            config = await ReadFromFileAsync(config, configJsonPath, cancellationToken).ConfigureAwait(false);
        }

        return Resolve(config);
    }

    private static async Task<TinyHarnessConfig> ReadFromFileAsync(TinyHarnessConfig config, string configJsonPath,
                                                                   CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(configJsonPath, cancellationToken).ConfigureAwait(false);
        var root = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidDataException($"Config file '{configJsonPath}' must contain a JSON object.");

        config = config with
        {
            Endpoint = ReadString(root, "endpoint") ?? config.Endpoint,
            ApiKeyEnvironmentVariable =
            ReadString(root, "apiKeyEnvironmentVariable") ?? config.ApiKeyEnvironmentVariable,
            Model = ReadString(root, "model")                                      ?? config.Model,
            ContextWindowTokens = ReadInt(root, "contextWindowTokens")             ?? config.ContextWindowTokens,
            ReservedOutputTokens = ReadInt(root, "reservedOutputTokens")           ?? config.ReservedOutputTokens,
            CompactionThreshold = ReadInt(root, "compactionThreshold")             ?? config.CompactionThreshold,
            MaxAgentSteps = ReadInt(root, "maxAgentSteps")                         ?? config.MaxAgentSteps,
            DefaultToolTimeoutSeconds = ReadInt(root, "defaultToolTimeoutSeconds") ?? config.DefaultToolTimeoutSeconds,
            WorkspaceRoot = ReadString(root, "workspaceRoot")                      ?? config.WorkspaceRoot,
        };

        return config;
    }

    /// <summary>
    /// Resolves the workspace root to a normalized absolute path.
    ///
    /// The API key is deliberately not resolved or validated here: M1 does not
    /// require a key. When the real transport lands in M2, the composition root
    /// will read the key from the configured environment variable
    /// (<see cref="TinyHarnessConfig.ApiKeyEnvironmentVariable"/>).
    /// </summary>
    private static TinyHarnessConfig Resolve(TinyHarnessConfig config)
    {
        var workspace = string.IsNullOrWhiteSpace(config.WorkspaceRoot)
            ? Environment.CurrentDirectory
            : Path.GetFullPath(config.WorkspaceRoot);

        return config with { WorkspaceRoot = workspace };
    }

    private static string? ReadString(JsonObject root, string property)
    {
        var node = root[property];
        return node?.GetValue<string>();
    }

    private static int? ReadInt(JsonObject root, string property)
    {
        var node = root[property];
        if (node is null)
        {
            return null;
        }

        return node.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? int.Parse(node.GetValue<string>())
            : node.GetValue<int>();
    }
}

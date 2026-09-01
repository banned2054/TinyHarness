namespace TinyHarness.Core.Configuration;

/// <summary>
/// Strongly typed configuration document (PLAN §14). No endpoint, model, context
/// window or API key is hardcoded; all values come from config plus environment.
/// </summary>
public sealed record TinyHarnessConfig
{
    public string Endpoint { get; init; } = string.Empty;

    public string ApiKeyEnvironmentVariable { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public int ContextWindowTokens { get; init; } = 128_000;

    public int ReservedOutputTokens { get; init; } = 8_000;

    public int CompactionThreshold { get; init; } = 0;

    public int MaxAgentSteps { get; init; } = 40;

    public int? DefaultToolTimeoutSeconds { get; init; }

    public string WorkspaceRoot { get; init; } = string.Empty;
}

namespace TinyHarness.Core.Agent;

/// <summary>
/// Configuration that drives the Agent Loop runtime (not the transport layer).
/// Values are supplied by the composition root; nothing is hardcoded here.
/// </summary>
public sealed record AgentOptions
{
    public required string Model { get; init; }

    public required int MaxAgentSteps { get; init; }

    public required int? DefaultToolTimeoutSeconds { get; init; }
}

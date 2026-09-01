namespace TinyHarness.Core.Agent;

/// <summary>
/// The outcome of an entire Agent run.
/// </summary>
public sealed record AgentResult
{
    public required AgentStatus Status { get; init; }

    /// <summary>The final assistant text, if the run produced one.</summary>
    public string FinalMessage { get; init; } = string.Empty;

    /// <summary>Total number of model request steps executed.</summary>
    public int Steps { get; init; }

    /// <summary>Total number of tool invocations executed.</summary>
    public int ToolExecutions { get; init; }

    /// <summary>Set when the run failed or was cancelled.</summary>
    public string? Error { get; init; }
}

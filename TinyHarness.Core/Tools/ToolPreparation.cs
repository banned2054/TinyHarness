using System.Text.Json.Nodes;

namespace TinyHarness.Core.Tools;

/// <summary>
/// Outcome of a tool invocation returned to the model as a tool message.
/// </summary>
public sealed record ToolResult
{
    /// <summary>Whether execution completed without throwing.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Text to return to the model (truncated to the result budget).</summary>
    public string Content { get; init; } = string.Empty;
}

/// <summary>
/// The immutable, fully validated plan produced by <see cref="ITool.Prepare"/>.
/// Permissions and execution both operate on this, never on raw model arguments.
/// </summary>
public sealed record ToolPreparation
{
    public required string ToolName { get; init; }

    public required string CallId { get; init; }

    /// <summary>
    /// Validated arguments after Prepare-side normalization (e.g. paths resolved
    /// to absolute, inside-workspace form). Execute consumes this object only;
    /// raw model arguments are never re-interpreted at execution time.
    /// </summary>
    public required JsonObject Arguments { get; init; }

    /// <summary>Normalized capability required (e.g. filesystem.list).</summary>
    public required string Capability { get; init; }

    /// <summary>Human-readable one-line summary shown during approval/audit.</summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Normalized absolute resource paths this invocation will access. The
    /// Permission Engine (M4) matches its rules against these scopes; the Agent
    /// Loop and CLI use them for display.
    /// </summary>
    public IReadOnlyList<string> TargetPaths { get; init; } = [];
}

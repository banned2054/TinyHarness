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

    public required JsonObject Arguments { get; init; }

    /// <summary>Normalized capability required (e.g. filesystem.list).</summary>
    public required string Capability { get; init; }

    public required string Summary { get; init; }
}

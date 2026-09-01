using TinyHarness.Core.Tools;

namespace TinyHarness.Core.ChatCompletions;

/// <summary>
/// A request to the Chat Completions endpoint. Only the fields required by the
/// baseline protocol are modelled; unsupported optional features are omitted
/// rather than guessed.
/// </summary>
public sealed record ChatCompletionRequest
{
    public required string Model { get; init; }

    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    public IReadOnlyList<ToolDefinition>? Tools { get; init; }
}

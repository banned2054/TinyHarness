namespace TinyHarness.Core.ChatCompletions;

/// <summary>
/// A single structured element produced from the response stream. A streaming
/// response can yield text deltas, tool-call delta fragments, or a terminal
/// stream event. Tool-call fragments must be accumulated per choice/tool index
/// before the arguments can be parsed as JSON.
/// </summary>
public enum ChatStreamEventKind
{
    /// <summary>A text fragment to append to the assistant message content.</summary>
    ContentDelta,

    /// <summary>A partial tool-call fragment; arguments arrive across deltas.</summary>
    ToolCallDelta,

    /// <summary>The stream ended normally and the accumulated message is complete.</summary>
    End,
}

public sealed record ChatStreamEvent
{
    public ChatStreamEventKind Kind { get; init; }

    /// <summary>Text appended when <see cref="Kind"/> is ContentDelta.</summary>
    public string ContentDelta { get; init; } = string.Empty;

    /// <summary>Tool-call index within the choice; stable across deltas.</summary>
    public int? ToolCallIndex { get; init; }

    /// <summary>
    /// Optional call id carried on the first delta of a tool call. Some providers
    /// only emit it on the first fragment of the function arguments.
    /// </summary>
    public string? ToolCallId { get; init; }

    public string? ToolCallFunctionName { get; init; }

    /// <summary>Arguments fragment appended to the caller's accumulation.</summary>
    public string ToolCallArgumentsDelta { get; init; } = string.Empty;
}

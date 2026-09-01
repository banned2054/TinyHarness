namespace TinyHarness.Core.ChatCompletions;

/// <summary>
/// A single message in a chat conversation. Content and tool calls are mutually
/// relevant: an assistant message may carry text and/or tool calls, while a tool
/// message carries the result for a specific tool call id.
/// </summary>
public sealed record ChatMessage
{
    public ChatRole Role { get; init; }

    public string Content { get; init; } = string.Empty;

    /// <summary>Set on assistant messages that request tool execution.</summary>
    public IReadOnlyList<ChatToolCall>? ToolCalls { get; init; }

    /// <summary>Set on tool messages; links the result to a prior tool call.</summary>
    public string? ToolCallId { get; init; }

    public string? Name { get; init; }

    public static ChatMessage System(string content) => new()
    {
        Role    = ChatRole.System,
        Content = content,
    };

    public static ChatMessage User(string content) => new()
    {
        Role    = ChatRole.User,
        Content = content,
    };

    public static ChatMessage Assistant(string content, IReadOnlyList<ChatToolCall>? toolCalls = null) => new()
    {
        Role      = ChatRole.Assistant,
        Content   = content,
        ToolCalls = toolCalls,
    };

    public static ChatMessage Tool(string name, string callId, string content) => new()
    {
        Role       = ChatRole.Tool,
        Name       = name,
        ToolCallId = callId,
        Content    = content,
    };
}

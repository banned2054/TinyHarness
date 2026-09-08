namespace TinyHarness.Core.ChatCompletions;

/// <summary>
/// 对话中的单条消息。assistant 消息可同时携带文本和工具调用，tool 消息通过调用 ID
/// 回传某次工具执行结果。
///
/// A single message in a chat conversation. Content and tool calls are mutually
/// relevant: an assistant message may carry text and/or tool calls, while a tool
/// message carries the result for a specific tool call id.
/// </summary>
public sealed record ChatMessage
{
    public ChatRole Role { get; init; }

    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// assistant 请求执行工具时设置。
    /// Set on assistant messages that request tool execution.
    /// </summary>
    public IReadOnlyList<ChatToolCall>? ToolCalls { get; init; }

    /// <summary>
    /// tool 消息设置此字段，用于关联之前的工具调用。
    /// Set on tool messages to link the result to a prior tool call.
    /// </summary>
    public string? ToolCallId { get; init; }

    public string? Name { get; init; }

    /// <summary>
    /// 创建系统指令消息。
    /// Creates a system-instruction message.
    /// </summary>
    public static ChatMessage System(string content) => new()
    {
        Role    = ChatRole.System,
        Content = content,
    };

    /// <summary>
    /// 创建用户输入消息。
    /// Creates a user-input message.
    /// </summary>
    public static ChatMessage User(string content) => new()
    {
        Role    = ChatRole.User,
        Content = content,
    };

    /// <summary>
    /// 创建包含文本和可选工具调用的 assistant 消息。
    /// Creates an assistant message with text and optional tool calls.
    /// </summary>
    public static ChatMessage Assistant(string content, IReadOnlyList<ChatToolCall>? toolCalls = null) => new()
    {
        Role      = ChatRole.Assistant,
        Content   = content,
        ToolCalls = toolCalls,
    };

    /// <summary>
    /// 创建绑定到指定调用 ID 的工具结果消息。
    /// Creates a tool-result message linked to the specified call id.
    /// </summary>
    public static ChatMessage Tool(string name, string callId, string content) => new()
    {
        Role       = ChatRole.Tool,
        Name       = name,
        ToolCallId = callId,
        Content    = content,
    };
}

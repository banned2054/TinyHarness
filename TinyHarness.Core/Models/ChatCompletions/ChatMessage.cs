using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.ChatCompletions;

/// <summary>
///     对话中的单条消息。assistant 消息可同时携带文本和工具调用，tool 消息通过调用 ID
///     回传某次工具执行结果。
///     A single message in a chat conversation. Content and tool calls are mutually
///     relevant: an assistant message may carry text and/or tool calls, while a tool
///     message carries the result for a specific tool call id.
/// </summary>
public sealed record ChatMessage
{
    public ChatRole Role { get; init; }

    public string Content { get; init; } = string.Empty;

    /// <summary>
    ///     <para>按到达顺序保存的助手文本消息；旧协议为空，拼接正文必须与 Content 一致。</para>
    ///     <para>Assistant text messages in arrival order; null for legacy protocols, with concatenated text matching Content.</para>
    /// </summary>
    public IReadOnlyList<AssistantTextItem>? TextItems { get; init; }

    /// <summary>
    ///     <para>展示与历史投影共用的文本条目；旧消息没有 TextItems 时按 Content 合成单条兜底，不写回 canonical。</para>
    ///     <para>
    ///         Text items shared by display and history projection; legacy messages without TextItems synthesize one
    ///         from Content as a fallback, never written back to canonical data.
    ///     </para>
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<AssistantTextItem> DisplayTextItems =>
        TextItems is { Count: > 0 } ? TextItems : [new AssistantTextItem(Content)];

    /// <summary>
    ///     <para>按阶段筛选本次响应的回答正文；缺少阶段时沿用工具调用判断。</para>
    ///     <para>Selects answer text by phase, falling back to tool-call inference when phase is absent.</para>
    /// </summary>
    [JsonIgnore]
    public string AnswerText =>
        string.Concat(DisplayTextItems.Where(item => item.IsAnswer(ToolCalls is { Count: > 0 }))
                                      .Select(item => item.Text));

    /// <summary>
    ///     assistant 请求执行工具时设置。
    ///     Set on assistant messages that request tool execution.
    /// </summary>
    public IReadOnlyList<ChatToolCall>? ToolCalls { get; init; }

    /// <summary>
    ///     tool 消息设置此字段，用于关联之前的工具调用。
    ///     Set on tool messages to link the result to a prior tool call.
    /// </summary>
    public string? ToolCallId { get; init; }

    public string? Name { get; init; }

    /// <summary>
    ///     assistant 消息携带的推理内容；回传时可原样续接服务的推理上下文。
    ///     Reasoning content carried on assistant messages; replaying it verbatim
    ///     continues the service-side reasoning context.
    /// </summary>
    public IReadOnlyList<ReasoningContent>? Reasoning { get; init; }

    /// <summary>
    ///     创建系统指令消息。
    ///     Creates a system-instruction message.
    /// </summary>
    public static ChatMessage System(string content) => new()
    {
        Role    = ChatRole.System,
        Content = content
    };

    /// <summary>
    ///     创建用户输入消息。
    ///     Creates a user-input message.
    /// </summary>
    public static ChatMessage User(string content) => new()
    {
        Role    = ChatRole.User,
        Content = content
    };

    /// <summary>
    ///     创建包含文本和可选工具调用的 assistant 消息。
    ///     Creates an assistant message with text and optional tool calls.
    /// </summary>
    public static ChatMessage Assistant(string content, IReadOnlyList<ChatToolCall>? toolCalls = null) => new()
    {
        Role      = ChatRole.Assistant,
        Content   = content,
        ToolCalls = toolCalls
    };

    /// <summary>
    ///     创建同时携带推理内容、文本和可选工具调用的 assistant 消息。
    ///     Creates an assistant message carrying reasoning, text, and optional tool calls.
    /// </summary>
    public static ChatMessage Assistant(
        string content, IReadOnlyList<ChatToolCall>? toolCalls, IReadOnlyList<ReasoningContent>? reasoning) => new()
    {
        Role      = ChatRole.Assistant,
        Content   = content,
        ToolCalls = toolCalls,
        Reasoning = reasoning
    };

    /// <summary>
    ///     创建绑定到指定调用 ID 的工具结果消息。
    ///     Creates a tool-result message linked to the specified call id.
    /// </summary>
    public static ChatMessage Tool(string name, string callId, string content) => new()
    {
        Role       = ChatRole.Tool,
        Name       = name,
        ToolCallId = callId,
        Content    = content
    };
}

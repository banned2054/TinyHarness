namespace TinyHarness.Core.Models.ChatCompletions;

/// <summary>
/// 响应流产生的单个结构化元素类型：文本增量、工具调用增量、正常结束，以及为
/// Responses 协议预留的推理增量、推理条目边界和 usage 事件。
/// 工具参数必须按 choice 与工具索引完整拼装后才能解析为 JSON。
///
/// A single structured element produced from the response stream. A streaming
/// response can yield text deltas, tool-call delta fragments, a terminal
/// stream event, plus reasoning deltas, reasoning item boundaries and usage
/// events reserved for the Responses protocol. Tool-call fragments must be
/// accumulated per choice/tool index before the arguments can be parsed as JSON.
/// </summary>
public enum ChatStreamEventKind
{
    /// <summary>
    /// 追加到 assistant 消息正文的文本片段。
    /// A text fragment appended to the assistant message content.
    /// </summary>
    ContentDelta,

    /// <summary>
    /// 工具调用的部分片段；参数可能跨多个增量到达。
    /// A partial tool-call fragment whose arguments may span deltas.
    /// </summary>
    ToolCallDelta,

    /// <summary>
    /// 流正常结束，累积消息已经完整。
    /// The stream ended normally and the accumulated message is complete.
    /// </summary>
    End,

    /// <summary>
    /// 追加到当前推理条目的摘要文本片段。
    /// A reasoning-summary text fragment appended to the current reasoning entry.
    /// </summary>
    ReasoningDelta,

    /// <summary>
    /// 服务端关闭一个推理条目并开启新条目；携带加密载荷与 item id 等不透明标识。
    ///
    /// The server closed one reasoning item and started a new one; carries the
    /// encrypted payload and item id as opaque identifiers.
    /// </summary>
    ReasoningItem,

    /// <summary>
    /// 服务端单独下发的 usage 数据；部分协议不把它附在结束事件上。
    ///
    /// Usage data delivered as its own event; some protocols do not attach it
    /// to the terminal event.
    /// </summary>
    Usage,
}

/// <summary>
/// 模型响应流中的一个结构化事件，按 <see cref="Kind"/> 携带文本或工具调用字段。
/// One structured model-stream event carrying text or tool-call fields according to <see cref="Kind"/>.
/// </summary>
public sealed record ChatStreamEvent
{
    public ChatStreamEventKind Kind { get; init; }

    /// <summary>
    /// 当 <see cref="Kind"/> 为 ContentDelta 时追加的文本。
    /// Text appended when <see cref="Kind"/> is ContentDelta.
    /// </summary>
    public string ContentDelta { get; init; } = string.Empty;

    /// <summary>
    /// 同一 choice 内稳定不变的工具调用索引。
    /// Tool-call index within the choice, stable across deltas.
    /// </summary>
    public int? ToolCallIndex { get; init; }

    /// <summary>
    /// 工具调用首个增量可能携带的 ID；部分服务后续片段不会重复发送。
    ///
    /// Optional call id carried on the first delta of a tool call. Some providers
    /// only emit it on the first fragment of the function arguments.
    /// </summary>
    public string? ToolCallId { get; init; }

    /// <summary>
    /// 工具调用函数名，通常只出现在该调用的首个增量中。
    /// Tool function name, commonly present only on the call's first delta.
    /// </summary>
    public string? ToolCallFunctionName { get; init; }

    /// <summary>
    /// 追加到对应工具调用缓冲区的参数片段。
    /// Argument fragment appended to the corresponding tool-call buffer.
    /// </summary>
    public string ToolCallArgumentsDelta { get; init; } = string.Empty;

    /// <summary>
    /// 当 <see cref="Kind"/> 为 ReasoningDelta 时追加的推理摘要文本片段。
    /// Reasoning-summary text fragment appended when <see cref="Kind"/> is ReasoningDelta.
    /// </summary>
    public string? ReasoningDelta { get; init; }

    /// <summary>
    /// 服务端加密的不透明推理载荷（Responses encrypted_content），随 ReasoningItem 事件到达。
    ///
    /// Server-encrypted opaque reasoning payload (Responses encrypted_content),
    /// delivered with ReasoningItem events.
    /// </summary>
    public string? ReasoningProtectedData { get; init; }

    /// <summary>
    /// 服务端分配的 reasoning item id，需原样回传以续接推理上下文。
    ///
    /// Server-assigned reasoning item id; must be replayed verbatim to continue
    /// the reasoning context.
    /// </summary>
    public string? ReasoningItemId { get; init; }

    /// <summary>
    /// 本次请求的输入 token 数；由 Usage 或 End 事件携带。
    /// Input token count for the request; carried by Usage or End events.
    /// </summary>
    public int? InputTokens { get; init; }

    /// <summary>
    /// 本次请求的输出 token 数；由 Usage 或 End 事件携带。
    /// Output token count for the request; carried by Usage or End events.
    /// </summary>
    public int? OutputTokens { get; init; }

    /// <summary>
    /// 服务端报告的结束原因（如 stop、tool_calls）；由 Usage 或 End 事件携带。
    ///
    /// The server-reported finish reason (e.g. stop, tool_calls); carried by
    /// Usage or End events.
    /// </summary>
    public string? FinishReason { get; init; }
}

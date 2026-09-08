namespace TinyHarness.Core.ChatCompletions;

/// <summary>
/// 响应流产生的单个结构化元素类型：文本增量、工具调用增量或正常结束。
/// 工具参数必须按 choice 与工具索引完整拼装后才能解析为 JSON。
///
/// A single structured element produced from the response stream. A streaming
/// response can yield text deltas, tool-call delta fragments, or a terminal
/// stream event. Tool-call fragments must be accumulated per choice/tool index
/// before the arguments can be parsed as JSON.
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
}

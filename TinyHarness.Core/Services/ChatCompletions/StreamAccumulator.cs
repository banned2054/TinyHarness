using TinyHarness.Core.Models.ChatCompletions;

namespace TinyHarness.Core.Services.ChatCompletions;

/// <summary>
/// 把流式事件累积为完整 assistant 消息，并按工具索引拼接被拆分的参数；仅在流结束后校验 JSON。
/// 同时累积推理增量与条目边界，以及 usage 和结束原因等流元数据。
///
/// Accumulates streaming events into a complete assistant message, joining
/// tool-call arguments that are split across delta fragments. Arguments are
/// parsed only after the stream ends. Reasoning deltas, reasoning item
/// boundaries, usage, and the finish reason are accumulated alongside.
/// </summary>
public sealed class StreamAccumulator
{
    private readonly System.Text.StringBuilder _content = new();

    private readonly List<ChatToolCall> _toolCalls = [];

    // Per tool-call index accumulation buffers during streaming.
    private readonly List<ToolCallBuffer> _buffers = [];

    // Reasoning accumulation: completed entries plus the open entry. The open
    // entry carries the accumulated text and, once seen, its item id; a delta
    // bearing a different item id or an explicit item event closes it.
    private readonly List<ReasoningContent> _reasoning = [];

    private readonly System.Text.StringBuilder _reasoningText = new();

    private string? _openReasoningItemId;

    private sealed record ToolCallBuffer
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public readonly System.Text.StringBuilder Arguments = new();
    }

    public string Content => _content.ToString();

    public IReadOnlyList<ChatToolCall> ToolCalls => _toolCalls;

    /// <summary>
    /// 已关闭的推理条目；尚未收到结束或条目边界事件的尾部条目在 <see cref="Finish"/> 时落盘。
    /// Reasoning entries closed so far; the open trailing entry is flushed by <see cref="Finish"/>.
    /// </summary>
    public IReadOnlyList<ReasoningContent>? Reasoning => _reasoning.Count == 0 ? null : _reasoning;

    /// <summary>
    /// 服务端报告的输入 token 数；缺少可靠 usage 时保持为 null。
    /// Server-reported input token count; stays null when no reliable usage arrived.
    /// </summary>
    public int? InputTokens { get; private set; }

    /// <summary>
    /// 服务端报告的输出 token 数；缺少可靠 usage 时保持为 null。
    /// Server-reported output token count; stays null when no reliable usage arrived.
    /// </summary>
    public int? OutputTokens { get; private set; }

    /// <summary>
    /// 服务端报告的结束原因；未报告时保持为 null。
    /// Server-reported finish reason; stays null when none was reported.
    /// </summary>
    public string? FinishReason { get; private set; }

    /// <summary>
    /// 追加一个文本、工具调用或推理增量，并记录 usage/结束元数据。
    /// Appends one text, tool-call, or reasoning delta and records usage/finish metadata.
    /// </summary>
    public void Append(ChatStreamEvent @event)
    {
        switch (@event.Kind)
        {
            case ChatStreamEventKind.ContentDelta :
                _content.Append(@event.ContentDelta);
                break;

            case ChatStreamEventKind.ToolCallDelta :
                AppendToolCallDelta(@event);
                break;

            case ChatStreamEventKind.ReasoningDelta :
                AppendReasoningDelta(@event);
                break;

            case ChatStreamEventKind.ReasoningItem :
                AppendReasoningItem(@event);
                break;

            case ChatStreamEventKind.Usage :
            case ChatStreamEventKind.End :
                AppendStreamMetadata(@event);
                break;
        }
    }

    /// <summary>
    /// 按工具调用索引合并 ID、函数名和参数片段，兼容元数据只在首个增量出现的服务。
    /// Merges id, function name, and argument fragments by tool index, including first-fragment-only metadata.
    /// </summary>
    private void AppendToolCallDelta(ChatStreamEvent @event)
    {
        var index = @event.ToolCallIndex ?? 0;
        while (_buffers.Count <= index)
        {
            _buffers.Add(new ToolCallBuffer());
        }

        var buffer = _buffers[index];
        if (@event.ToolCallId is not null)
        {
            buffer.Id = @event.ToolCallId;
        }

        if (@event.ToolCallFunctionName is not null)
        {
            buffer.Name = @event.ToolCallFunctionName;
        }

        if (!string.IsNullOrEmpty(@event.ToolCallArgumentsDelta))
        {
            buffer.Arguments.Append(@event.ToolCallArgumentsDelta);
        }
    }

    /// <summary>
    /// 追加推理增量。携带 item id 且与开放条目不同的增量即隐式条目边界：先关闭当前开放条目
    /// （仅文本与 id，无加密载荷）再开新条目；不带 id 的增量视为归属当前条目。
    ///
    /// Appends one reasoning delta. A delta whose item id differs from the open
    /// entry's id is an implicit item boundary: the open entry (text and id
    /// only, no encrypted payload) is closed first and a new one opened; an
    /// id-less delta belongs to the current entry.
    /// </summary>
    private void AppendReasoningDelta(ChatStreamEvent @event)
    {
        if (@event.ReasoningItemId is { } itemId
         && !string.Equals(_openReasoningItemId, itemId, StringComparison.Ordinal))
        {
            FlushReasoningEntry();
            _openReasoningItemId = itemId;
        }

        _reasoningText.Append(@event.ReasoningDelta ?? string.Empty);
    }

    /// <summary>
    /// 处理推理条目事件：以事件文本为准关闭当前条目（事件文本为空则用已累积文本），并把事件
    /// 携带的加密载荷与 item id 写入该条目，随后开启新的空开放条目。事件文本与已流出增量重复时，
    /// 以条目为单位只保留事件文本这一份完整文本。
    ///
    /// Handles a reasoning item event: closes the current entry with the event's
    /// text as authoritative (falling back to the accumulated text when the
    /// event text is empty), writes the event's encrypted payload and item id
    /// into that entry, then opens a fresh empty entry. When the event text
    /// duplicates already streamed deltas, exactly one copy — the event's
    /// complete text — is kept per entry.
    /// </summary>
    private void AppendReasoningItem(ChatStreamEvent @event)
    {
        var text = !string.IsNullOrEmpty(@event.ReasoningDelta)
            ? @event.ReasoningDelta
            : _reasoningText.Length == 0 ? null : _reasoningText.ToString();
        CloseReasoningEntry(text, @event.ReasoningProtectedData,
                            @event.ReasoningItemId ?? _openReasoningItemId);
    }

    private void FlushReasoningEntry()
        => CloseReasoningEntry(_reasoningText.Length == 0 ? null : _reasoningText.ToString(),
                               protectedData : null, itemId : _openReasoningItemId);

    /// <summary>
    /// 关闭开放条目；既无文本也无任何不透明标识的条目不产出。最终 Reasoning 列表按到达顺序。
    ///
    /// Closes the open entry; an entry with neither text nor any opaque
    /// identifier is not produced. The final reasoning list keeps arrival order.
    /// </summary>
    private void CloseReasoningEntry(string? text, string? protectedData, string? itemId)
    {
        if (!string.IsNullOrEmpty(text) || protectedData is not null || itemId is not null)
        {
            _reasoning.Add(new ReasoningContent(string.IsNullOrEmpty(text) ? null : text, protectedData, itemId));
        }

        _reasoningText.Clear();
        _openReasoningItemId = null;
    }

    /// <summary>
    /// 记录 usage 与结束原因；字段以先到达者为准，后续事件只在仍为空时补充。
    ///
    /// Records usage and the finish reason; the first value wins and later
    /// events only fill fields that are still empty.
    /// </summary>
    private void AppendStreamMetadata(ChatStreamEvent @event)
    {
        if (@event.InputTokens is { } inputTokens)
        {
            InputTokens ??= inputTokens;
        }

        if (@event.OutputTokens is { } outputTokens)
        {
            OutputTokens ??= outputTokens;
        }

        if (@event.FinishReason is { } finishReason)
        {
            FinishReason ??= finishReason;
        }
    }

    /// <summary>
    /// 结束累积，落盘尾部推理条目，校验每个工具调用的名称及完整参数 JSON，并生成不可分割的调用对象。
    ///
    /// Finalizes the accumulation, flushing the trailing reasoning entry and
    /// parsing and validating the assembled tool calls. Must only be called
    /// once, when the stream signals End or terminates.
    /// </summary>
    public void Finish()
    {
        FlushReasoningEntry();

        foreach (var buffer in _buffers)
        {
            if (string.IsNullOrEmpty(buffer.Name))
            {
                throw new InvalidOperationException("A tool call was streamed without a function name.");
            }

            // Throws if the assembled arguments are not valid JSON, surfacing a
            // malformed-stream error instead of silently corrupting the call.
            var arguments = buffer.Arguments.ToString();
            if (!string.IsNullOrEmpty(arguments))
            {
                _ = System.Text.Json.Nodes.JsonNode.Parse(arguments)
                 ?? throw new InvalidDataException("Tool call arguments were not valid JSON.");
            }

            _toolCalls.Add(new ChatToolCall(Id : buffer.Id ?? string.Empty, FunctionName : buffer.Name,
                                            ArgumentsJson : arguments));
        }
    }
}

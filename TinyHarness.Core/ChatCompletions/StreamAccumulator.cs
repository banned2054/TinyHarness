namespace TinyHarness.Core.ChatCompletions;

/// <summary>
/// Accumulates streaming events into a complete assistant message, joining
/// tool-call arguments that are split across delta fragments. Arguments are
/// parsed only after the stream ends.
/// </summary>
public sealed class StreamAccumulator
{
    private readonly System.Text.StringBuilder _content = new();

    private readonly List<ChatToolCall> _toolCalls = [];

    // Per tool-call index accumulation buffers during streaming.
    private readonly List<ToolCallBuffer> _buffers = [];

    private sealed record ToolCallBuffer
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public readonly System.Text.StringBuilder Arguments = new();
    }

    public string Content => _content.ToString();

    public IReadOnlyList<ChatToolCall> ToolCalls => _toolCalls;

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
        }
    }

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
    /// Finalizes the accumulation, parsing and validating the assembled tool calls.
    /// Must only be called once, when the stream signals End or terminates.
    /// </summary>
    public void Finish()
    {
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

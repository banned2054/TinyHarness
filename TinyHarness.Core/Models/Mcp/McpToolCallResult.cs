namespace TinyHarness.Core.Models.Mcp;

/// <summary>
///     MCP tools/call 的返回形状。本服务以一个 JSON 文本块返回有界 WorkerResult，isError 标记
///     worker 本身失败或取消；Incomplete 是有效的部分结果，由文本中的 status 明确表达。
///     MCP tools/call result. This server returns the bounded WorkerResult in one JSON text block;
///     isError marks worker failure or cancellation, while Incomplete remains a valid partial result
///     whose status is explicit in the text.
/// </summary>
public sealed record McpToolCallResult
{
    public required IReadOnlyList<McpTextContent> Content { get; init; }

    public bool IsError { get; init; }
}

/// <summary>A plain-text MCP content block. / MCP 纯文本内容块。</summary>
public sealed record McpTextContent
{
    public string Type { get; init; } = "text";

    public required string Text { get; init; }
}

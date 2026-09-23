namespace TinyHarness.Core.Models.Mcp;

/// <summary>
///     tools/list 的结果。工具数量固定为一个，因此不声明 nextCursor 分页游标。
///     The tools/list result. The tool set is fixed at one, so no nextCursor pagination is offered.
/// </summary>
public sealed record McpToolsListResult
{
    public required IReadOnlyList<McpToolDefinition> Tools { get; init; }
}

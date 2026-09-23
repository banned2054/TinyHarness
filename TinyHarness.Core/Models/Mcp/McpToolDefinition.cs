using System.Text.Json;

namespace TinyHarness.Core.Models.Mcp;

/// <summary>
///     tools/list 返回的单个工具定义。inputSchema 是预先构建的 JSON Schema 元素，
///     由工具目录以静态字面量提供，避免为任意 schema 建立可反射的类型树。
///     One tool definition returned by tools/list. inputSchema is a prebuilt JSON Schema element
///     supplied as a static literal by the tool catalog, avoiding a reflection-prone type tree for
///     arbitrary schemas.
/// </summary>
public sealed record McpToolDefinition
{
    public required string Name { get; init; }

    public string? Title { get; init; }

    public required string Description { get; init; }

    public required JsonElement InputSchema { get; init; }
}

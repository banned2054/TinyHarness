namespace TinyHarness.Core.Models.Mcp;

/// <summary>
///     tools 能力对象。工具列表固定不变，不声明 listChanged 通知能力。
///     The tools capability object. The tool list is fixed, so no listChanged notification support
///     is declared.
/// </summary>
public sealed record McpToolsCapability
{
    public bool ListChanged => false;
}

namespace TinyHarness.Core.Models.Mcp;

/// <summary>
///     服务端能力声明。本服务只提供工具，不声明 logging、prompts、resources 等能力。
///     The server capability advertisement. This server only offers tools; logging, prompts,
///     resources and similar capabilities are not declared.
/// </summary>
public sealed record McpServerCapabilities
{
    public required McpToolsCapability Tools { get; init; }
}

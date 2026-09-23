namespace TinyHarness.Core.Models.Mcp;

/// <summary>
///     initialize 结果中的服务端实现信息。
///     Server implementation information in the initialize result.
/// </summary>
public sealed record McpServerInfo
{
    public required string Name { get; init; }

    public required string Version { get; init; }
}

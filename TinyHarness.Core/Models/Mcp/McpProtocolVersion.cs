namespace TinyHarness.Core.Models.Mcp;

/// <summary>
///     本服务支持的 MCP 协议版本。第一版只声明 2025-06-18；客户端请求其他版本时，
///     按生命周期规范回落到该版本，由客户端决定是否继续会话。
///     MCP protocol versions supported by this server. The first version declares 2025-06-18 only;
///     for any other client request the server falls back to this version per the lifecycle spec,
///     and the client decides whether to continue the session.
/// </summary>
public static class McpProtocolVersion
{
    /// <summary>本服务实现的唯一协议版本，也是版本协商失败时的回落值。</summary>
    public const string Latest = "2025-06-18";

    /// <summary>
    ///     判断客户端请求的协议版本是否受支持。
    ///     Whether the requested protocol version is supported.
    /// </summary>
    public static bool IsSupported(string? requestedVersion)
    {
        return string.Equals(requestedVersion, Latest, StringComparison.Ordinal);
    }
}

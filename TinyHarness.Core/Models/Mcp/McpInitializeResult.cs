using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Mcp;

/// <summary>
///     initialize 请求的结果：协商后的协议版本、服务端能力、服务端信息与可选使用说明。
///     The initialize result: the negotiated protocol version, server capabilities, server info,
///     and optional usage instructions.
/// </summary>
public sealed record McpInitializeResult
{
    public required string ProtocolVersion { get; init; }

    public required McpServerCapabilities Capabilities { get; init; }

    public required McpServerInfo ServerInfo { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; init; }
}

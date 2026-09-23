using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Mcp;

/// <summary>
///     出站 JSON-RPC 2.0 响应：result 与 error 互斥，各自为 null 时省略；id 为 JSON null 时
///     必须显式输出 "id": null（解析错误与无法提取 id 的无效请求要求如此）。
///     Outbound JSON-RPC 2.0 response: result and error are mutually exclusive and omitted when
///     null; a JSON null id must be emitted explicitly as "id": null, as required for parse errors
///     and invalid requests whose id cannot be recovered.
/// </summary>
public sealed record McpJsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    public JsonElement? Id { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public McpJsonRpcError? Error { get; init; }
}

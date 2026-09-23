using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Mcp;

/// <summary>
///     入站 JSON-RPC 2.0 消息的宽松 DTO：字段合法性（jsonrpc 版本、method 非空、id 类型）由
///     分发器校验并映射为 -32600，缺失字段不得在反序列化阶段抛出异常，否则会与 -32700 解析错误混淆。
///     Lenient DTO for inbound JSON-RPC 2.0 messages. Field validity (jsonrpc version, non-empty
///     method, id type) is validated by the dispatcher and mapped to -32600; missing fields must not
///     throw during deserialization or they would be confused with a -32700 parse error.
/// </summary>
public sealed record McpJsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string? JsonRpc { get; init; }

    /// <summary>
    ///     null 表示通知（无 id 字段）；JSON null 的 id 是合法的携带 null 标识的请求，两者必须区分。
    ///     null means a notification (no id field); a JSON null id is a valid request carrying a null
    ///     identifier — the two cases must stay distinguishable.
    /// </summary>
    public JsonElement? Id { get; init; }

    public string? Method { get; init; }

    public JsonElement? Params { get; init; }
}

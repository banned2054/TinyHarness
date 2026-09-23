using System.Text.Json.Serialization;
using TinyHarness.Core.Models.Mcp;

namespace TinyHarness.Core.Services.Mcp;

/// <summary>
/// MCP stdio 协议消息的 source-generated JSON 契约。序列化与反序列化只走静态 metadata，
/// 不使用反射，保持 NativeAOT 裁剪安全（PLAN §6）。
///
/// Source-generated JSON contract for the MCP stdio protocol messages. Serialization and
/// deserialization go through static metadata only — no reflection — so the path stays
/// trimming-safe under NativeAOT (PLAN §6).
/// </summary>
[JsonSerializable(typeof(McpJsonRpcRequest))]
[JsonSerializable(typeof(McpJsonRpcResponse))]
[JsonSerializable(typeof(McpInitializeResult))]
[JsonSerializable(typeof(McpToolsListResult))]
[JsonSerializable(typeof(McpToolCallResult))]
[JsonSerializable(typeof(McpEmptyResult))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             GenerationMode = JsonSourceGenerationMode.Metadata |
                                              JsonSourceGenerationMode.Serialization)]
public sealed partial class McpJsonContext : JsonSerializerContext;

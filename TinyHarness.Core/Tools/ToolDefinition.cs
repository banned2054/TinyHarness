namespace TinyHarness.Core.Tools;

/// <summary>
/// 随模型请求发送的工具定义，包括名称、描述和参数 JSON Schema。
///
/// The JSON schema form of a tool, as sent to the model in a request. The schema
/// is a plain JSON fragment (serialized) so the model can validate arguments.
/// </summary>
public sealed record ToolDefinition
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>
    /// 描述工具参数的 JSON Schema 对象；使用独立 JsonNode 文档以保持可裁剪和 NativeAOT 安全。
    ///
    /// JSON Schema object containing "type"/"properties" describing the arguments
    /// accepted by the tool. Stored as a JsonObject (a JsonNode) so it stays a
    /// self-contained, trimming-safe document that can be serialized by a
    /// source-generated context.
    /// </summary>
    public required System.Text.Json.Nodes.JsonObject Parameters { get; init; }
}

using System.Text.Json.Nodes;
using TinyHarness.Core.ChatCompletions;

namespace TinyHarness.Core.Tools;

/// <summary>
/// 文件工具共用的参数辅助方法，负责解析模型 JSON，并从准备计划读取已规范化的绝对路径。
///
/// Shared helpers for the file tools: parsing the model's arguments JSON and
/// reading the normalized absolute path that Prepare wrote back into the plan.
/// </summary>
internal static class ToolArgs
{
    /// <summary>
    /// 将工具调用参数解析为 JSON 对象；空参数视为空对象，非对象 JSON 会明确报错。
    /// Parses tool arguments as a JSON object, treating empty input as an empty object and rejecting other JSON kinds.
    /// </summary>
    public static JsonObject ParseObject(ChatToolCall call)
    {
        var args = string.IsNullOrEmpty(call.ArgumentsJson)
            ? new JsonObject()
            : JsonNode.Parse(call.ArgumentsJson) as JsonObject
           ?? throw new InvalidDataException($"Tool '{call.FunctionName}' arguments were not a JSON object.");
        return args;
    }

    /// <summary>
    /// 读取 Prepare 已经解析并写回计划的绝对路径；Execute 不会重新解析原始输入。
    ///
    /// Reads the absolute path that Prepare resolved and wrote into the plan.
    /// Execute never re-resolves raw model input.
    /// </summary>
    public static string ReadAbsolute(ToolPreparation preparation, string key)
        => JsonArgs.Required(preparation.Arguments, key);
}

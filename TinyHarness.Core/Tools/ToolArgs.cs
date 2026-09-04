using System.Text.Json.Nodes;
using TinyHarness.Core.ChatCompletions;

namespace TinyHarness.Core.Tools;

/// <summary>
/// Shared helpers for the file tools: parsing the model's arguments JSON and
/// reading the normalized absolute path that Prepare wrote back into the plan.
/// </summary>
internal static class ToolArgs
{
    public static JsonObject ParseObject(ChatToolCall call)
    {
        var args = string.IsNullOrEmpty(call.ArgumentsJson)
            ? new JsonObject()
            : JsonNode.Parse(call.ArgumentsJson) as JsonObject
           ?? throw new InvalidDataException($"Tool '{call.FunctionName}' arguments were not a JSON object.");
        return args;
    }

    /// <summary>
    /// Reads the absolute path that Prepare resolved and wrote into the plan.
    /// Execute never re-resolves raw model input.
    /// </summary>
    public static string ReadAbsolute(ToolPreparation preparation, string key)
        => JsonArgs.Required(preparation.Arguments, key);
}

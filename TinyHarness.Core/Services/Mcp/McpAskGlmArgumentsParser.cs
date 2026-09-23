using System.Text.Json;
using TinyHarness.Core.Models.Worker;

namespace TinyHarness.Core.Services.Mcp;

/// <summary>
///     严格解析 MCP tools/call 的方法参数与 ask_glm 任务包；拒绝重复、未知或类型错误的字段，
///     不允许 JSON 请求带入模型、工作区、权限或预算。
///     Strictly parses MCP tools/call parameters and the ask_glm task package. Duplicate, unknown or
///     mistyped fields are rejected; model, workspace, permission and budget settings are not accepted.
/// </summary>
public static class McpAskGlmArgumentsParser
{
    public static bool TryParse(JsonElement? parameters, out string toolName, out WorkerRequest? request)
    {
        toolName = string.Empty;
        request  = null;
        if (parameters is not { ValueKind: JsonValueKind.Object } value) return false;

        string?      name      = null;
        JsonElement? arguments = null;
        var          seen      = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name)) return false;
            switch (property.Name)
            {
                case "name" when property.Value.ValueKind == JsonValueKind.String :
                    name = property.Value.GetString();
                    break;
                case "arguments" when property.Value.ValueKind == JsonValueKind.Object :
                    arguments = property.Value;
                    break;
                case "_meta" when property.Value.ValueKind == JsonValueKind.Object :
                    // MCP request metadata is transport-level data, not worker task input.
                    break;
                default :
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(name) || arguments is not { } argumentObject) return false;
        toolName = name;

        string?               task           = null;
        IReadOnlyList<string> knownFacts     = [];
        IReadOnlyList<string> focusPaths     = [];
        var                   expectedOutput = string.Empty;
        seen.Clear();
        foreach (var property in argumentObject.EnumerateObject())
        {
            if (!seen.Add(property.Name)) return false;
            switch (property.Name)
            {
                case "task" when property.Value.ValueKind == JsonValueKind.String :
                    task = property.Value.GetString();
                    break;
                case "knownFacts" when TryReadStringArray(property.Value, out var facts) :
                    knownFacts = facts;
                    break;
                case "focusPaths" when TryReadStringArray(property.Value, out var paths) :
                    focusPaths = paths;
                    break;
                case "expectedOutput" when property.Value.ValueKind == JsonValueKind.String :
                    expectedOutput = property.Value.GetString() ?? string.Empty;
                    break;
                default :
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(task)) return false;
        request = new WorkerRequest
        {
            TaskPrompt     = task,
            KnownFacts     = knownFacts,
            FocusPaths     = focusPaths,
            ExpectedOutput = expectedOutput,
        };
        return true;
    }

    private static bool TryReadStringArray(JsonElement value, out IReadOnlyList<string> values)
    {
        values = [];
        if (value.ValueKind != JsonValueKind.Array) return false;

        var items = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String) return false;
            items.Add(element.GetString() ?? string.Empty);
        }

        values = items;
        return true;
    }
}

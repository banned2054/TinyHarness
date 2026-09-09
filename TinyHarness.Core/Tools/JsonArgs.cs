using System.Text.Json.Nodes;

namespace TinyHarness.Core.Tools;

/// <summary>
/// 严格且无反射的工具参数读取器。缺失或类型错误会抛出包含参数名的异常，供 Agent Loop
/// 回传模型并让其修正调用。
///
/// Strict, reflection-free readers for tool argument objects. Missing or
/// mistyped arguments raise <see cref="InvalidDataException"/> naming the
/// argument, so the Agent Loop can report the failure back to the model, which
/// then adjusts its call.
/// </summary>
internal static class JsonArgs
{
    /// <summary>
    /// 读取必填的非空字符串。
    /// Reads a required, non-empty string value.
    /// </summary>
    public static string Required(JsonObject args, string key)
    {
        if (!TryString(args, key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw Error(args, key, "a non-empty string", "is required");
        }

        return value;
    }

    /// <summary>
    /// 读取可选字符串，缺失或空白时使用回退值。
    /// Reads an optional string and uses the fallback when it is absent or blank.
    /// </summary>
    public static string Optional(JsonObject args, string key, string fallback)
    {
        if (!TryString(args, key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value;
    }

    /// <summary>
    /// 读取可选布尔值；缺失时使用回退值，类型错误时拒绝参数。
    /// Reads an optional Boolean, using the fallback when absent and rejecting type mismatches.
    /// </summary>
    public static bool OptionalBool(JsonObject args, string key, bool fallback)
    {
        var node = args[key];
        if (node is null)
        {
            return fallback;
        }

        if (node is JsonValue v && v.TryGetValue<bool>(out var value))
        {
            return value;
        }

        throw Error(args, key, "a boolean", "is invalid");
    }

    /// <summary>
    /// 读取可选整数；缺失时返回 <see langword="null"/>，类型错误时拒绝参数。
    /// Reads an optional integer, returning <see langword="null"/> when absent and rejecting type mismatches.
    /// </summary>
    public static int? OptionalInt(JsonObject args, string key)
    {
        var node = args[key];
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue v && v.TryGetValue<int>(out var value))
        {
            return value;
        }

        throw Error(args, key, "an integer", "is invalid");
    }

    /// <summary>
    /// 读取可选字符串数组；缺失时返回空数组，元素类型错误或数量超限时拒绝参数。
    /// Reads an optional string array, returning an empty array when absent and rejecting invalid elements or size.
    /// </summary>
    public static IReadOnlyList<string> OptionalStringArray(JsonObject args, string key, int maximumCount)
    {
        var node = args[key];
        if (node is null)
        {
            return [];
        }

        if (node is not JsonArray array)
        {
            throw Error(args, key, "an array of strings", "is invalid");
        }

        if (array.Count > maximumCount)
        {
            throw new
                InvalidDataException($"Tool argument '{key}' has {array.Count} entries; the limit is {maximumCount}.");
        }

        var values = new List<string>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonValue value || !value.TryGetValue<string>(out var item) || item is null)
            {
                throw new InvalidDataException($"Tool argument '{key}[{i}]' must be a string.");
            }

            values.Add(item);
        }

        return values;
    }

    /// <summary>
    /// 尝试取得字符串节点，并将缺失、null 或类型不符统一视为失败。
    /// Attempts to read a string node, treating absence, null, and type mismatch uniformly as failure.
    /// </summary>
    private static bool TryString(JsonObject args, string key, out string value)
    {
        value = string.Empty;
        var node = args[key];
        if (node is not JsonValue v || !v.TryGetValue<string>(out var raw) || raw is null)
        {
            return false;
        }

        value = raw;
        return true;
    }

    /// <summary>
    /// 创建包含参数名、期望类型和实际 JSON 类型的诊断异常。
    /// Creates a diagnostic exception containing the argument name, expected type, and actual JSON kind.
    /// </summary>
    private static InvalidDataException Error(JsonObject args, string key, string expected, string reason)
    {
        var node = args[key];
        var kind = node is null ? "absent" : node.GetValueKind().ToString();
        return new InvalidDataException($"Tool argument '{key}' {reason}: expected {expected}, got {kind}.");
    }
}

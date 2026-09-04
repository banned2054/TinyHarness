using System.Text.Json.Nodes;

namespace TinyHarness.Core.Tools;

/// <summary>
/// Strict, reflection-free readers for tool argument objects. Missing or
/// mistyped arguments raise <see cref="InvalidDataException"/> naming the
/// argument, so the Agent Loop can report the failure back to the model, which
/// then adjusts its call.
/// </summary>
internal static class JsonArgs
{
    public static string Required(JsonObject args, string key)
    {
        if (!TryString(args, key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw Error(args, key, "a non-empty string", "is required");
        }

        return value;
    }

    public static string Optional(JsonObject args, string key, string fallback)
    {
        if (!TryString(args, key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value;
    }

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

    private static InvalidDataException Error(JsonObject args, string key, string expected, string reason)
    {
        var node = args[key];
        var kind = node is null ? "absent" : node.GetValueKind().ToString();
        return new InvalidDataException($"Tool argument '{key}' {reason}: expected {expected}, got {kind}.");
    }
}

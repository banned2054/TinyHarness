using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Configuration;

namespace TinyHarness.Core.Services.Configuration;

/// <summary>
/// 命令允许规则的 JSON 绑定与写出。配置加载与用户配置读写共用同一份无反射实现，保证两侧校验一致。
///
/// JSON binding and writing for command allow rules. The project config loader and the user config store share
/// this reflection-free implementation so both sides validate identically.
/// </summary>
internal static class CommandRuleJson
{
    /// <summary>
    /// 手工绑定命令允许规则数组；字段缺失时返回 <see langword="null"/>，错误诊断包含来源与字段路径。
    ///
    /// Manually binds a command allow rule array; returns <see langword="null"/> when the property is absent and
    /// diagnoses wrong types with the source and field path.
    /// </summary>
    public static IReadOnlyList<CommandRule>? Read(JsonObject root, string property, string source)
    {
        if (root[property] is null)
        {
            return null;
        }

        if (root[property] is not JsonArray array)
        {
            throw new InvalidDataException($"Config field '{property}' in {source} must be an array.");
        }

        var rules = new List<CommandRule>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject item)
            {
                throw new InvalidDataException($"Config field '{property}[{i}]' in {source} must be an object.");
            }

            var mode = (ReadString(item, "mode") ?? "direct").ToLowerInvariant();
            if (mode is not "direct" and not "shell")
            {
                throw new
                    InvalidDataException($"Config field '{property}[{i}].mode' in {source} must be 'direct' or 'shell'.");
            }

            var executable = ReadString(item, "executable") ?? string.Empty;
            var shell      = (ReadString(item, "shell") ?? string.Empty).ToLowerInvariant();
            var command    = ReadString(item, "command") ?? string.Empty;
            if (mode == "direct" && string.IsNullOrWhiteSpace(executable))
            {
                throw new InvalidDataException(
                                               $"Config field '{property}[{i}].executable' in {source} is required for direct mode.");
            }

            if (mode == "direct" && (!string.IsNullOrEmpty(shell) || !string.IsNullOrEmpty(command)))
            {
                throw new InvalidDataException(
                                               $"Config fields '{property}[{i}].shell' and '.command' in {source} are valid only for shell mode.");
            }

            if (mode == "shell" && (string.IsNullOrWhiteSpace(shell) || string.IsNullOrWhiteSpace(command)))
            {
                throw new InvalidDataException(
                                               $"Config fields '{property}[{i}].shell' and '{property}[{i}].command' in {source} are required for shell mode.");
            }

            if (mode == "shell" && (!string.IsNullOrEmpty(executable) || item["arguments"] is not null))
            {
                throw new InvalidDataException(
                                               $"Config fields '{property}[{i}].executable' and '.arguments' in {source} are valid only for direct mode.");
            }

            var argumentsNode = item["arguments"];
            var arguments     = new List<string>();
            if (argumentsNode is not null)
            {
                if (argumentsNode is not JsonArray argumentsArray)
                {
                    throw new
                        InvalidDataException($"Config field '{property}[{i}].arguments' in {source} must be an array.");
                }

                for (var argumentIndex = 0; argumentIndex < argumentsArray.Count; argumentIndex++)
                {
                    if (argumentsArray[argumentIndex] is not JsonValue value ||
                        !value.TryGetValue<string>(out var argument)         || argument is null)
                    {
                        throw new InvalidDataException(
                                                       $"Config field '{property}[{i}].arguments[{argumentIndex}]' in {source} must be a string.");
                    }

                    arguments.Add(argument);
                }
            }

            rules.Add(new CommandRule
            {
                Mode             = mode,
                Executable       = executable,
                Arguments        = arguments,
                Shell            = shell,
                Command          = command,
                WorkingDirectory = ReadString(item, "workingDirectory") ?? ".",
            });
        }

        return rules;
    }

    /// <summary>
    /// 把命令允许规则写成 JSON 数组，与绑定格式互逆。
    /// Writes command allow rules as a JSON array, the inverse of the binding format.
    /// </summary>
    public static JsonArray Write(IReadOnlyList<CommandRule> rules)
    {
        var array = new JsonArray();
        foreach (var rule in rules)
        {
            var item = new JsonObject();
            if (!string.IsNullOrEmpty(rule.Mode))
            {
                item["mode"] = rule.Mode;
            }

            if (!string.IsNullOrEmpty(rule.Executable))
            {
                item["executable"] = rule.Executable;
            }

            if (rule.Arguments is { Count: > 0 })
            {
                var arguments = new JsonArray();
                foreach (var argument in rule.Arguments)
                {
                    // Cast to JsonNode? so the non-generic JsonArray.Add binds; the
                    // generic Add<T> overload is not trimming/AOT-safe.
                    arguments.Add((JsonNode?)JsonValue.Create(argument));
                }

                item["arguments"] = arguments;
            }

            if (!string.IsNullOrEmpty(rule.Shell))
            {
                item["shell"] = rule.Shell;
            }

            if (!string.IsNullOrEmpty(rule.Command))
            {
                item["command"] = rule.Command;
            }

            if (!string.IsNullOrEmpty(rule.WorkingDirectory))
            {
                item["workingDirectory"] = rule.WorkingDirectory;
            }

            array.Add((JsonNode?)item);
        }

        return array;
    }

    /// <summary>
    /// 读取可选字符串字段，字段缺失时返回 <see langword="null"/>。
    /// Reads an optional string property and returns <see langword="null"/> when absent.
    /// </summary>
    private static string? ReadString(JsonObject node, string property)
    {
        var nodeValue = node[property];
        return nodeValue?.GetValue<string>();
    }
}

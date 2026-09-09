using System.Text.Json.Nodes;

namespace TinyHarness.Core.Configuration;

/// <summary>
/// 从 JSON 文件加载配置，并使用手工、无反射的绑定保持 NativeAOT 安全；缺失项沿用
/// <see cref="TinyHarnessConfig"/> 的默认值。
///
/// Builds a config document from a JSON file. Uses <see cref="JsonObject"/> for
/// manual, reflection-free binding so the config path stays NativeAOT-safe
/// without a source-generated context at this layer. Values not present in the
/// file fall back to the documented defaults in <see cref="TinyHarnessConfig"/>.
/// </summary>
public static class ConfigurationLoader
{
    /// <summary>
    /// 读取可选配置文件、合并默认值，并规范化需要在运行时使用的路径。
    /// Reads an optional configuration file, merges defaults, and normalizes runtime paths.
    /// </summary>
    public static async Task<TinyHarnessConfig> LoadAsync(string configJsonPath, CancellationToken cancellationToken)
    {
        var config = new TinyHarnessConfig();
        if (!string.IsNullOrWhiteSpace(configJsonPath) && File.Exists(configJsonPath))
        {
            config = await ReadFromFileAsync(config, configJsonPath, cancellationToken).ConfigureAwait(false);
        }

        return Resolve(config);
    }

    /// <summary>
    /// 将 JSON 对象中的已知字段覆盖到现有配置上；格式或类型错误直接报告给调用方。
    /// Overlays known JSON fields onto an existing configuration and surfaces malformed values.
    /// </summary>
    private static async Task<TinyHarnessConfig> ReadFromFileAsync(TinyHarnessConfig config, string configJsonPath,
                                                                   CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(configJsonPath, cancellationToken).ConfigureAwait(false);
        var root = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidDataException($"Config file '{configJsonPath}' must contain a JSON object.");

        config = config with
        {
            Endpoint = ReadString(root, "endpoint") ?? config.Endpoint,
            ApiKeyEnvironmentVariable =
            ReadString(root, "apiKeyEnvironmentVariable") ?? config.ApiKeyEnvironmentVariable,
            Model = ReadString(root, "model")                                      ?? config.Model,
            ContextWindowTokens = ReadInt(root, "contextWindowTokens")             ?? config.ContextWindowTokens,
            ReservedOutputTokens = ReadInt(root, "reservedOutputTokens")           ?? config.ReservedOutputTokens,
            CompactionThreshold = ReadInt(root, "compactionThreshold")             ?? config.CompactionThreshold,
            MaxAgentSteps = ReadInt(root, "maxAgentSteps")                         ?? config.MaxAgentSteps,
            DefaultToolTimeoutSeconds = ReadInt(root, "defaultToolTimeoutSeconds") ?? config.DefaultToolTimeoutSeconds,
            WorkspaceRoot = ReadString(root, "workspaceRoot")                      ?? config.WorkspaceRoot,
            CommandRules = ReadCommandRules(root, "commandRules")                  ?? config.CommandRules,
        };

        return config;
    }

    /// <summary>
    /// 将工作区根目录解析为规范化绝对路径；API key 由调用方根据配置的环境变量读取。
    ///
    /// Resolves the workspace root to a normalized absolute path. The caller reads
    /// the API key from <see cref="TinyHarnessConfig.ApiKeyEnvironmentVariable"/>.
    /// </summary>
    private static TinyHarnessConfig Resolve(TinyHarnessConfig config)
    {
        var workspace = string.IsNullOrWhiteSpace(config.WorkspaceRoot)
            ? Environment.CurrentDirectory
            : Path.GetFullPath(config.WorkspaceRoot);

        return config with { WorkspaceRoot = workspace };
    }

    /// <summary>
    /// 读取可选字符串字段，字段缺失时返回 <see langword="null"/>。
    /// Reads an optional string property and returns <see langword="null"/> when absent.
    /// </summary>
    private static string? ReadString(JsonObject root, string property)
    {
        var node = root[property];
        return node?.GetValue<string>();
    }

    /// <summary>
    /// 读取可选整数字段，同时接受 JSON 数字和整数字符串。
    /// Reads an optional integer property from either a JSON number or an integer string.
    /// </summary>
    private static int? ReadInt(JsonObject root, string property)
    {
        var node = root[property];
        if (node is null)
        {
            return null;
        }

        return node.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? int.Parse(node.GetValue<string>())
            : node.GetValue<int>();
    }

    /// <summary>
    /// 手工绑定命令允许规则，保持配置路径无反射且对错误类型给出明确诊断。
    /// Manually binds command allow rules, keeping configuration reflection-free and diagnostics explicit.
    /// </summary>
    private static IReadOnlyList<CommandRule>? ReadCommandRules(JsonObject root, string property)
    {
        var node = root[property];
        if (node is null)
        {
            return null;
        }

        if (node is not JsonArray array)
        {
            throw new InvalidDataException($"Config field '{property}' must be an array.");
        }

        var rules = new List<CommandRule>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject item)
            {
                throw new InvalidDataException($"Config field '{property}[{i}]' must be an object.");
            }

            var mode = (ReadString(item, "mode") ?? "direct").ToLowerInvariant();
            if (mode is not "direct" and not "shell")
            {
                throw new InvalidDataException($"Config field '{property}[{i}].mode' must be 'direct' or 'shell'.");
            }

            var executable = ReadString(item, "executable") ?? string.Empty;
            var shell      = (ReadString(item, "shell")     ?? string.Empty).ToLowerInvariant();
            var command    = ReadString(item, "command")    ?? string.Empty;
            if (mode == "direct" && string.IsNullOrWhiteSpace(executable))
            {
                throw new InvalidDataException(
                    $"Config field '{property}[{i}].executable' is required for direct mode.");
            }

            if (mode == "direct" && (!string.IsNullOrEmpty(shell) || !string.IsNullOrEmpty(command)))
            {
                throw new InvalidDataException(
                    $"Config fields '{property}[{i}].shell' and '.command' are valid only for shell mode.");
            }

            if (mode == "shell" && (string.IsNullOrWhiteSpace(shell) || string.IsNullOrWhiteSpace(command)))
            {
                throw new InvalidDataException(
                    $"Config fields '{property}[{i}].shell' and '{property}[{i}].command' are required for shell mode.");
            }

            if (mode == "shell" && (!string.IsNullOrEmpty(executable) || item["arguments"] is not null))
            {
                throw new InvalidDataException(
                    $"Config fields '{property}[{i}].executable' and '.arguments' are valid only for direct mode.");
            }

            var argumentsNode = item["arguments"];
            var arguments     = new List<string>();
            if (argumentsNode is not null)
            {
                if (argumentsNode is not JsonArray argumentsArray)
                {
                    throw new InvalidDataException($"Config field '{property}[{i}].arguments' must be an array.");
                }

                for (var argumentIndex = 0; argumentIndex < argumentsArray.Count; argumentIndex++)
                {
                    if (argumentsArray[argumentIndex] is not JsonValue value ||
                        !value.TryGetValue<string>(out var argument)         || argument is null)
                    {
                        throw new InvalidDataException(
                                                       $"Config field '{property}[{i}].arguments[{argumentIndex}]' must be a string.");
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
}

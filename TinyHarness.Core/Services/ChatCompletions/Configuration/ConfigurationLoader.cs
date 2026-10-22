using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Configuration;

namespace TinyHarness.Core.Services.ChatCompletions.Configuration;

/// <summary>
///     <para>
///         从 JSON 文件加载配置，并使用手工、无反射的绑定保持 NativeAOT 安全；缺失项沿用
///         <see cref="TinyHarnessConfig" /> 的默认值。
///     </para>
///     <para>
///         Builds a config document from a JSON file. Uses <see cref="JsonObject" /> for manual,
///         reflection-free binding so the config path stays NativeAOT-safe without a source-generated context
///         at this layer. Values not present in the file fall back to the documented defaults in
///         <see cref="TinyHarnessConfig" />.
///     </para>
/// </summary>
public static class ConfigurationLoader
{
    /// <summary>
    ///     <para>读取可选配置文件、合并默认值，并规范化需要在运行时使用的路径。</para>
    ///     <para>Reads an optional configuration file, merges defaults, and normalizes runtime paths.</para>
    /// </summary>
    public static async Task<TinyHarnessConfig> LoadAsync(string configJsonPath, CancellationToken cancellationToken)
    {
        var config = new TinyHarnessConfig();
        if (!string.IsNullOrWhiteSpace(configJsonPath) && File.Exists(configJsonPath))
            config = await ReadFromFileAsync(config, configJsonPath, cancellationToken).ConfigureAwait(false);

        return NormalizePaths(config);
    }

    /// <summary>
    ///     <para>将 JSON 对象中的已知字段覆盖到现有配置上；格式或类型错误直接报告给调用方。</para>
    ///     <para>Overlays known JSON fields onto an existing configuration and surfaces malformed values.</para>
    /// </summary>
    private static async Task<TinyHarnessConfig> ReadFromFileAsync(
        TinyHarnessConfig config, string configJsonPath, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(configJsonPath, cancellationToken).ConfigureAwait(false);
        var root = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidDataException($"Config file '{configJsonPath}' must contain a JSON object.");

        config = config with
        {
            Endpoint = ReadString(root, "endpoint") ?? config.Endpoint,
            ApiKeyEnvironmentVariable =
            ReadString(root, "apiKeyEnvironmentVariable") ?? config.ApiKeyEnvironmentVariable,
            ApiKeyCredentialTarget =
            ReadString(root, "apiKeyCredentialTarget") ?? config.ApiKeyCredentialTarget,
            ChatApi = ReadChatApi(root, "chatApi", configJsonPath) ?? config.ChatApi,
            Model = ReadString(root, "model") ?? config.Model,
            ContextWindowTokens = ReadInt(root, "contextWindowTokens") ?? config.ContextWindowTokens,
            ReservedOutputTokens = ReadInt(root, "reservedOutputTokens") ?? config.ReservedOutputTokens,
            CompactionThreshold = ReadInt(root, "compactionThreshold") ?? config.CompactionThreshold,
            MaxAgentSteps = ReadInt(root, "maxAgentSteps") ?? config.MaxAgentSteps,
            DefaultToolTimeoutSeconds = ReadInt(root, "defaultToolTimeoutSeconds") ?? config.DefaultToolTimeoutSeconds,
            WorkspaceRoot = ReadString(root, "workspaceRoot") ?? config.WorkspaceRoot,
            SessionDirectory = ReadString(root, "sessionDirectory") ?? config.SessionDirectory,
            CommandRules = CommandRuleJson.Read(root, "commandRules", $"'{configJsonPath}'") ?? config.CommandRules
        };

        return config;
    }

    /// <summary>
    ///     <para>将工作区根目录与 session 目录解析为规范化绝对路径；API key 由调用方根据配置的凭据引用读取。</para>
    ///     <para>
    ///         Resolves the workspace root and session directory to normalized absolute paths. The caller reads the
    ///         API key from the configured credential reference.
    ///     </para>
    /// </summary>
    internal static TinyHarnessConfig NormalizePaths(TinyHarnessConfig config)
    {
        if (config.MaxAgentSteps < 0)
            throw new
                InvalidDataException("Config field 'maxAgentSteps' must be a non-negative integer; 0 means unlimited.");

        var workspace = string.IsNullOrWhiteSpace(config.WorkspaceRoot)
            ? Environment.CurrentDirectory
            : Path.GetFullPath(config.WorkspaceRoot);

        var sessionDirectory = Path.IsPathRooted(config.SessionDirectory)
            ? config.SessionDirectory
            : Path.GetFullPath(config.SessionDirectory);
        return config with { WorkspaceRoot = workspace, SessionDirectory = sessionDirectory };
    }

    /// <summary>
    ///     <para>读取可选字符串字段，字段缺失时返回 <see langword="null" />。</para>
    ///     <para>Reads an optional string property and returns <see langword="null" /> when absent.</para>
    /// </summary>
    private static string? ReadString(JsonObject root, string property)
    {
        var node = root[property];
        return node?.GetValue<string>();
    }

    /// <summary>
    ///     <para>读取可选整数字段，同时接受 JSON 数字和整数字符串。</para>
    ///     <para>Reads an optional integer property from either a JSON number or an integer string.</para>
    /// </summary>
    private static int? ReadInt(JsonObject root, string property)
    {
        var node = root[property];
        if (node is null) return null;

        return node.GetValueKind() == JsonValueKind.String
            ? int.Parse(node.GetValue<string>())
            : node.GetValue<int>();
    }

    /// <summary>
    ///     <para>
    ///         通过共享映射读取可选 chatApi 字段；字段缺失时返回 <see langword="null" /> 沿用现值，
    ///         非法值抛出包含键名与原值的错误。
    ///     </para>
    ///     <para>
    ///         Reads the optional chatApi property through the shared mapping; an absent property returns
    ///         <see langword="null" /> to keep the current value, an invalid one fails with the key and raw value.
    ///     </para>
    /// </summary>
    private static ChatApiKind? ReadChatApi(JsonObject root, string property, string source)
    {
        var node = root[property];
        if (node is null) return null;

        // JSON 值可能是任意类型（如 "chatApi": 5）；显式校验类型并给出带键名的错误，
        // 而不是让 GetValue<string>() 抛出无上下文的 InvalidOperationException。
        if (node.GetValueKind() is not JsonValueKind.String)
            throw new InvalidDataException($"Config file '{source}' field '{property}' must be a string.");

        var text = node.GetValue<string>();
        return ChatApiKindParser.TryParse(text, out var api)
            ? api
            : throw new InvalidDataException($"Config file '{source}' field '{property}' " +
                                             $"has an invalid value '{text}'; "            +
                                             $"expected '{ChatApiKindParser.ChatCompletionsValue}' or '{ChatApiKindParser.ResponsesValue}'.");
    }
}

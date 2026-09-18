using System.Text.Json;
using System.Text.Json.Nodes;

namespace TinyHarness.Core.Configuration;

/// <summary>
/// 用户配置中一个 provider profile 声明的模型及其显式声明的上下文窗口；窗口大小不从模型名称推断。
///
/// A model declared by a provider profile in the user config, with its explicitly declared context window;
/// the window size is never inferred from the model name.
/// </summary>
public sealed record UserProfileModel
{
    public string Id { get; init; } = string.Empty;

    public int ContextWindowTokens { get; init; }
}

/// <summary>
/// 命名 provider profile：endpoint、API key 引用（环境变量名或系统凭据存储条目）与模型列表。
/// 普通 JSON 只保存引用，不保存密钥值。
///
/// A named provider profile: endpoint plus API-key references (an environment variable name or a system
/// credential-store entry). Plain JSON stores references only, never the secret value.
/// </summary>
public sealed record UserProfile
{
    public string Name { get; init; } = string.Empty;

    public string Endpoint { get; init; } = string.Empty;

    public string ApiKeyEnvironmentVariable { get; init; } = string.Empty;

    public string ApiKeyCredentialTarget { get; init; } = string.Empty;

    public string DefaultModel { get; init; } = string.Empty;

    public IReadOnlyList<UserProfileModel> Models { get; init; } = [];
}

/// <summary>
/// 用户配置中的全局默认设置；字段缺省时沿用 <see cref="TinyHarnessConfig"/> 的内置默认值。
/// 不包含 workspaceRoot：工作区属于项目语义，由项目配置或当前目录决定。
///
/// Global defaults in the user config; absent fields fall back to the built-in defaults of
/// <see cref="TinyHarnessConfig"/>. Deliberately has no workspaceRoot: the workspace is project
/// semantics, decided by the project config or the current directory.
/// </summary>
public sealed record UserConfigSettings
{
    public int? MaxAgentSteps { get; init; }

    public int? ReservedOutputTokens { get; init; }

    public int? CompactionThreshold { get; init; }

    public int? DefaultToolTimeoutSeconds { get; init; }

    public string? SessionDirectory { get; init; }

    public IReadOnlyList<CommandRule>? CommandRules { get; init; }
}

/// <summary>
/// TinyHarness 用户配置文档：默认 provider profile、profile 列表与全局默认设置。
///
/// The TinyHarness user config document: default provider profile, profile list, and global defaults.
/// </summary>
public sealed record UserConfig
{
    public string? DefaultProfile { get; init; }

    public IReadOnlyList<UserProfile> Profiles { get; init; } = [];

    public UserConfigSettings? Settings { get; init; }
}

/// <summary>
/// 用户配置文件路径解析与读写。文件由 TinyHarness 拥有：保存时会整体重写为当前 schema。
///
/// User config file path resolution and load/save. TinyHarness owns this file: saving rewrites it
/// to the current schema.
/// </summary>
public static class UserConfigStore
{
    /// <summary>
    /// 覆盖用户配置目录的环境变量；用于便携部署与测试，普通用户无需设置。
    ///
    /// Environment variable that overrides the user config directory; used for portable setups and tests.
    /// </summary>
    public const string DirectoryOverrideVariable = "TINYHARNESS_USER_CONFIG_DIR";

    /// <summary>
    /// 用户配置文件名。
    /// The user config file name.
    /// </summary>
    public const string FileName = "user-config.json";

    /// <summary>
    /// 返回固定的用户配置文件绝对路径：优先 <see cref="DirectoryOverrideVariable"/>；Windows 使用
    /// %APPDATA%\tinyharness；其他平台使用 XDG_CONFIG_HOME 或 ~/.config；目录 API 不可用时退回当前目录。
    ///
    /// Returns the fixed absolute user config path: <see cref="DirectoryOverrideVariable"/> first; on Windows
    /// %APPDATA%\tinyharness; elsewhere XDG_CONFIG_HOME or ~/.config; falls back to the current directory when
    /// the folder APIs are unavailable.
    /// </summary>
    public static string DefaultFilePath()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable(DirectoryOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            return Path.GetFullPath(Path.Combine(overrideDirectory.Trim(), FileName));
        }

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData))
            {
                return Path.Combine(appData, "tinyharness", FileName);
            }
        }
        else
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configHome))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(home))
                {
                    configHome = Path.Combine(home, ".config");
                }
            }

            if (!string.IsNullOrWhiteSpace(configHome))
            {
                return Path.Combine(configHome, "tinyharness", FileName);
            }
        }

        return Path.GetFullPath(Path.Combine("tinyharness", FileName));
    }

    /// <summary>
    /// 读取用户配置；文件不存在时返回空配置（由调用方决定是否视为错误）。格式错误抛出带路径的异常。
    ///
    /// Loads the user config; a missing file yields an empty config (the caller decides whether that is an
    /// error). Malformed content throws with the file path included.
    /// </summary>
    public static async Task<UserConfig> LoadAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return new UserConfig();
        }

        var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        var root = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidDataException($"User config '{filePath}' must contain a JSON object.");
        return Bind(root, filePath);
    }

    /// <summary>
    /// 保存用户配置，自动创建父目录；内容整体重写为当前 schema。
    ///
    /// Saves the user config, creating parent directories; the content is rewritten to the current schema.
    /// </summary>
    public static async Task SaveAsync(string filePath, UserConfig config, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(config);

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var root = new JsonObject();
        if (!string.IsNullOrWhiteSpace(config.DefaultProfile))
        {
            root["defaultProfile"] = config.DefaultProfile;
        }

        if (config.Profiles.Count > 0)
        {
            var profiles = new JsonArray();
            foreach (var profile in config.Profiles)
            {
                var models = new JsonArray();
                foreach (var model in profile.Models)
                {
                    // Cast to JsonNode? so the non-generic JsonArray.Add binds; the
                    // generic Add<T> overload is not trimming/AOT-safe.
                    var modelNode = new JsonObject
                    {
                        ["id"]                  = model.Id,
                        ["contextWindowTokens"] = model.ContextWindowTokens,
                    };
                    models.Add((JsonNode?)modelNode);
                }

                var profileNode = new JsonObject
                {
                    ["name"]     = profile.Name,
                    ["endpoint"] = profile.Endpoint,
                    ["apiKeyEnvironmentVariable"] =
                        string.IsNullOrWhiteSpace(profile.ApiKeyEnvironmentVariable)
                            ? null
                            : profile.ApiKeyEnvironmentVariable,
                    ["apiKeyCredentialTarget"] =
                        string.IsNullOrWhiteSpace(profile.ApiKeyCredentialTarget)
                            ? null
                            : profile.ApiKeyCredentialTarget,
                    ["defaultModel"] =
                        string.IsNullOrWhiteSpace(profile.DefaultModel) ? null : profile.DefaultModel,
                    ["models"] = models,
                };
                profiles.Add((JsonNode?)profileNode);
            }

            root["profiles"] = profiles;
        }

        var settings = config.Settings;
        if (settings is not null)
        {
            var settingsNode = new JsonObject();
            if (settings.MaxAgentSteps is { } maxAgentSteps)
            {
                settingsNode["maxAgentSteps"] = maxAgentSteps;
            }

            if (settings.ReservedOutputTokens is { } reservedOutputTokens)
            {
                settingsNode["reservedOutputTokens"] = reservedOutputTokens;
            }

            if (settings.CompactionThreshold is { } compactionThreshold)
            {
                settingsNode["compactionThreshold"] = compactionThreshold;
            }

            if (settings.DefaultToolTimeoutSeconds is { } defaultToolTimeoutSeconds)
            {
                settingsNode["defaultToolTimeoutSeconds"] = defaultToolTimeoutSeconds;
            }

            if (!string.IsNullOrWhiteSpace(settings.SessionDirectory))
            {
                settingsNode["sessionDirectory"] = settings.SessionDirectory;
            }

            if (settings.CommandRules is { Count: > 0 } commandRules)
            {
                settingsNode["commandRules"] = CommandRuleJson.Write(commandRules);
            }

            if (settingsNode.Count > 0)
            {
                root["settings"] = settingsNode;
            }
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(filePath, root.ToJsonString(options) + Environment.NewLine, cancellationToken)
                  .ConfigureAwait(false);
    }

    /// <summary>
    /// 手工、无反射地把 JSON 绑定为 <see cref="UserConfig"/>，错误消息包含字段路径与文件位置。
    ///
    /// Binds JSON to <see cref="UserConfig"/> manually without reflection; errors include field paths and the file location.
    /// </summary>
    private static UserConfig Bind(JsonObject root, string filePath)
    {
        var defaultProfile = ReadString(root, "defaultProfile", filePath);

        UserProfile[]? profiles = null;
        if (root["profiles"] is not null)
        {
            if (root["profiles"] is not JsonArray profileArray)
            {
                throw new InvalidDataException($"User config field 'profiles' in '{filePath}' must be an array.");
            }

            profiles = new UserProfile[profileArray.Count];
            var seenProfiles = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < profileArray.Count; i++)
            {
                if (profileArray[i] is not JsonObject profileNode)
                {
                    throw new
                        InvalidDataException($"User config field 'profiles[{i}]' in '{filePath}' must be an object.");
                }

                var name = ReadString(profileNode, "name", filePath)
                        ?? throw new InvalidDataException(
                                                          $"User config field 'profiles[{i}].name' in '{filePath}' is required and must be a non-empty string.");
                if (!seenProfiles.Add(name))
                {
                    throw new InvalidDataException(
                                                   $"User config field 'profiles[{i}].name' in '{filePath}' duplicates profile name '{name}'.");
                }

                UserProfileModel[]? models = null;
                if (profileNode["models"] is not null)
                {
                    if (profileNode["models"] is not JsonArray modelArray)
                    {
                        throw new InvalidDataException(
                                                       $"User config field 'profiles[{i}].models' in '{filePath}' must be an array.");
                    }

                    models = new UserProfileModel[modelArray.Count];
                    var seenModels = new HashSet<string>(StringComparer.Ordinal);
                    for (var m = 0; m < modelArray.Count; m++)
                    {
                        if (modelArray[m] is not JsonObject modelNode)
                        {
                            throw new InvalidDataException(
                                                           $"User config field 'profiles[{i}].models[{m}]' in '{filePath}' must be an object.");
                        }

                        var id = ReadString(modelNode, "id", filePath)
                              ?? throw new InvalidDataException(
                                                                $"User config field 'profiles[{i}].models[{m}].id' in '{filePath}' is required and must be a non-empty string.");
                        if (!seenModels.Add(id))
                        {
                            throw new InvalidDataException(
                                                           $"User config field 'profiles[{i}].models[{m}].id' in '{filePath}' duplicates model id '{id}'.");
                        }

                        var contextWindow = ReadPositiveInt(modelNode, "contextWindowTokens", filePath,
                                                            $"profiles[{i}].models[{m}].contextWindowTokens")
                                         ?? throw new InvalidDataException(
                                                                           $"User config field 'profiles[{i}].models[{m}].contextWindowTokens' in '{filePath}' is required and must be a positive integer.");
                        models[m] = new UserProfileModel { Id = id, ContextWindowTokens = contextWindow };
                    }
                }

                var endpoint = ReadString(profileNode, "endpoint", filePath) ?? string.Empty;
                var apiKeyEnvironmentVariable =
                    ReadString(profileNode, "apiKeyEnvironmentVariable", filePath) ?? string.Empty;
                var apiKeyCredentialTarget =
                    ReadString(profileNode, "apiKeyCredentialTarget", filePath) ?? string.Empty;

                profiles[i] = new UserProfile
                {
                    Name                      = name,
                    Endpoint                  = endpoint,
                    ApiKeyEnvironmentVariable = apiKeyEnvironmentVariable,
                    ApiKeyCredentialTarget    = apiKeyCredentialTarget,
                    DefaultModel              = ReadString(profileNode, "defaultModel", filePath) ?? string.Empty,
                    Models                    = models                                            ?? [],
                };
            }
        }

        UserConfigSettings? settings = null;
        if (root["settings"] is JsonObject settingsNode)
        {
            settings = new UserConfigSettings
            {
                MaxAgentSteps = ReadPositiveInt(settingsNode, "maxAgentSteps", filePath, "settings.maxAgentSteps"),
                ReservedOutputTokens = ReadNonNegativeInt(settingsNode, "reservedOutputTokens", filePath,
                                                          "settings.reservedOutputTokens"),
                CompactionThreshold = ReadNonNegativeInt(settingsNode, "compactionThreshold", filePath,
                                                         "settings.compactionThreshold"),
                DefaultToolTimeoutSeconds =
                    ReadNonNegativeInt(settingsNode, "defaultToolTimeoutSeconds", filePath,
                                       "settings.defaultToolTimeoutSeconds"),
                SessionDirectory = ReadString(settingsNode, "sessionDirectory", filePath),
                CommandRules = root["settings"] is JsonObject s && s["commandRules"] is not null
                    ? CommandRuleJson.Read(settingsNode, "commandRules", $"user config '{filePath}'")
                    : null,
            };
        }

        return new UserConfig
        {
            DefaultProfile = defaultProfile,
            Profiles       = profiles ?? [],
            Settings       = settings,
        };
    }

    /// <summary>
    /// 读取可选字符串字段。
    /// Reads an optional string property.
    /// </summary>
    private static string? ReadString(JsonObject node, string property, string filePath)
    {
        var value = node[property];
        if (value is null)
        {
            return null;
        }

        if (value.GetValueKind() is not JsonValueKind.String)
        {
            throw new InvalidDataException($"User config field '{property}' in '{filePath}' must be a string.");
        }

        var text = value.GetValue<string>();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// 读取可选正整数字段，接受 JSON 数字或整数字符串。
    /// Reads an optional positive integer property from a JSON number or an integer string.
    /// </summary>
    private static int? ReadPositiveInt(JsonObject node, string property, string filePath, string fieldPath)
    {
        var value = ReadIntValue(node, property, filePath, fieldPath);
        if (value is null || value > 0)
        {
            return value;
        }

        throw new InvalidDataException($"User config field '{fieldPath}' in '{filePath}' must be a positive integer.");
    }

    /// <summary>
    /// 读取可选非负整数字段，接受 JSON 数字或整数字符串。
    /// Reads an optional non-negative integer property from a JSON number or an integer string.
    /// </summary>
    private static int? ReadNonNegativeInt(JsonObject node, string property, string filePath, string fieldPath)
    {
        var value = ReadIntValue(node, property, filePath, fieldPath);
        if (value is null || value >= 0)
        {
            return value;
        }

        throw new
            InvalidDataException($"User config field '{fieldPath}' in '{filePath}' must be a non-negative integer.");
    }

    /// <summary>
    /// 读取整数字段原始值，类型非法时给出明确诊断。
    /// Reads the raw integer value and reports clear diagnostics for wrong types.
    /// </summary>
    private static int? ReadIntValue(JsonObject node, string property, string filePath, string fieldPath)
    {
        var value = node[property];
        if (value is null)
        {
            return null;
        }

        try
        {
            return value.GetValueKind() == JsonValueKind.String
                ? int.Parse(value.GetValue<string>())
                : value.GetValue<int>();
        }
        catch (FormatException)
        {
            throw new InvalidDataException($"User config field '{fieldPath}' in '{filePath}' must be an integer.");
        }
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Configuration;

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
                    ["chatApi"]      = ChatApiKindParser.ToValueString(profile.ChatApi),
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

            if (settings.Worker is { } worker)
            {
                var workerNode = new JsonObject();
                if (!string.IsNullOrWhiteSpace(worker.WorkspaceRoot))
                    workerNode["workspaceRoot"] = worker.WorkspaceRoot;
                if (worker.RunTimeoutSeconds is { } runTimeoutSeconds)
                    workerNode["runTimeoutSeconds"] = runTimeoutSeconds;
                if (worker.MaxAgentSteps is { } workerMaxAgentSteps)
                    workerNode["maxAgentSteps"] = workerMaxAgentSteps;
                if (worker.DefaultToolTimeoutSeconds is { } workerToolTimeoutSeconds)
                    workerNode["defaultToolTimeoutSeconds"] = workerToolTimeoutSeconds;
                if (worker.MaxTaskPackageCharacters is { } maxTaskPackageCharacters)
                    workerNode["maxTaskPackageCharacters"] = maxTaskPackageCharacters;
                if (worker.MaxToolCalls is { } maxToolCalls)
                    workerNode["maxToolCalls"] = maxToolCalls;
                if (worker.MaxToolOutputCharacters is { } maxToolOutputCharacters)
                    workerNode["maxToolOutputCharacters"] = maxToolOutputCharacters;
                if (worker.MaxContextTokensPerRequest is { } maxContextTokensPerRequest)
                    workerNode["maxContextTokensPerRequest"] = maxContextTokensPerRequest;
                if (worker.MaxCumulativeContextTokens is { } maxCumulativeContextTokens)
                    workerNode["maxCumulativeContextTokens"] = maxCumulativeContextTokens;
                if (worker.MaxModelResponseCharacters is { } maxModelResponseCharacters)
                    workerNode["maxModelResponseCharacters"] = maxModelResponseCharacters;

                if (workerNode.Count > 0)
                    settingsNode["worker"] = workerNode;
            }

            if (settings.WindowsSandbox is { } sandbox)
            {
                var sandboxNode = new JsonObject();
                if (sandbox.Enabled)
                    sandboxNode["enabled"] = true;
                if (!string.IsNullOrWhiteSpace(sandbox.SetupExecutablePath))
                    sandboxNode["setupExecutablePath"] = sandbox.SetupExecutablePath;
                if (!string.IsNullOrWhiteSpace(sandbox.RunnerExecutablePath))
                    sandboxNode["runnerExecutablePath"] = sandbox.RunnerExecutablePath;
                if (!string.IsNullOrWhiteSpace(sandbox.SandboxHome))
                    sandboxNode["sandboxHome"] = sandbox.SandboxHome;
                if (sandbox.Policy != SandboxPolicyKind.WorkspaceWrite)
                    sandboxNode["policy"] = sandbox.Policy == SandboxPolicyKind.ReadOnly ? "read-only" : "workspace-write";
                if (!string.IsNullOrWhiteSpace(sandbox.SandboxTempRoot))
                    sandboxNode["sandboxTempRoot"] = sandbox.SandboxTempRoot;
                if (sandbox.AdditionalWriteRoots is { Count: > 0 } writeRoots)
                {
                    var rootsArray = new JsonArray();
                    foreach (var writeRoot in writeRoots)
                        rootsArray.Add((JsonNode?)JsonValue.Create(writeRoot));
                    sandboxNode["additionalWriteRoots"] = rootsArray;
                }

                if (sandbox.ExtraEnvironment is { Count: > 0 } environment)
                {
                    var environmentNode = new JsonObject();
                    foreach (var (name, value) in environment)
                        environmentNode[name] = value;
                    sandboxNode["extraEnvironment"] = environmentNode;
                }

                if (sandboxNode.Count > 0)
                    settingsNode["windowsSandbox"] = sandboxNode;
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
                        ?? throw new
                               InvalidDataException($"User config field 'profiles[{i}].name' in '{filePath}' is required and must be a non-empty string.");
                if (!seenProfiles.Add(name))
                {
                    throw new
                        InvalidDataException($"User config field 'profiles[{i}].name' in '{filePath}' duplicates profile name '{name}'.");
                }

                UserProfileModel[]? models = null;
                if (profileNode["models"] is not null)
                {
                    if (profileNode["models"] is not JsonArray modelArray)
                    {
                        throw new
                            InvalidDataException($"User config field 'profiles[{i}].models' in '{filePath}' must be an array.");
                    }

                    models = new UserProfileModel[modelArray.Count];
                    var seenModels = new HashSet<string>(StringComparer.Ordinal);
                    for (var m = 0; m < modelArray.Count; m++)
                    {
                        if (modelArray[m] is not JsonObject modelNode)
                        {
                            throw new
                                InvalidDataException($"User config field 'profiles[{i}].models[{m}]' in '{filePath}' must be an object.");
                        }

                        var id = ReadString(modelNode, "id", filePath)
                              ?? throw new
                                     InvalidDataException($"User config field 'profiles[{i}].models[{m}].id' in '{filePath}' is required and must be a non-empty string.");
                        if (!seenModels.Add(id))
                        {
                            throw new
                                InvalidDataException($"User config field 'profiles[{i}].models[{m}].id' in '{filePath}' duplicates model id '{id}'.");
                        }

                        var contextWindow = ReadPositiveInt(modelNode, "contextWindowTokens", filePath,
                                                            $"profiles[{i}].models[{m}].contextWindowTokens")
                                         ?? throw new
                                                InvalidDataException($"User config field 'profiles[{i}].models[{m}].contextWindowTokens' in '{filePath}' is required and must be a positive integer.");
                        models[m] = new UserProfileModel { Id = id, ContextWindowTokens = contextWindow };
                    }
                }

                var endpoint = ReadString(profileNode, "endpoint", filePath) ?? string.Empty;
                var apiKeyEnvironmentVariable =
                    ReadString(profileNode, "apiKeyEnvironmentVariable", filePath) ?? string.Empty;
                var apiKeyCredentialTarget =
                    ReadString(profileNode, "apiKeyCredentialTarget", filePath) ?? string.Empty;
                var chatApi = ReadChatApi(profileNode, "chatApi", filePath, $"profiles[{i}].chatApi");

                profiles[i] = new UserProfile
                {
                    Name                      = name,
                    Endpoint                  = endpoint,
                    ApiKeyEnvironmentVariable = apiKeyEnvironmentVariable,
                    ApiKeyCredentialTarget    = apiKeyCredentialTarget,
                    ChatApi                   = chatApi,
                    DefaultModel              = ReadString(profileNode, "defaultModel", filePath) ?? string.Empty,
                    Models                    = models                                            ?? [],
                };
            }
        }

        UserConfigSettings? settings = null;
        if (root["settings"] is JsonObject settingsNode)
        {
            UserWorkerSettings? workerSettings = null;
            if (settingsNode["worker"] is not null)
            {
                if (settingsNode["worker"] is not JsonObject workerNode)
                    throw new
                        InvalidDataException($"User config field 'settings.worker' in '{filePath}' must be an object.");

                workerSettings = new UserWorkerSettings
                {
                    WorkspaceRoot = ReadString(workerNode, "workspaceRoot", filePath),
                    RunTimeoutSeconds = ReadPositiveInt(workerNode, "runTimeoutSeconds", filePath,
                                                        "settings.worker.runTimeoutSeconds"),
                    MaxAgentSteps =
                        ReadPositiveInt(workerNode, "maxAgentSteps", filePath, "settings.worker.maxAgentSteps"),
                    DefaultToolTimeoutSeconds = ReadPositiveInt(workerNode, "defaultToolTimeoutSeconds", filePath,
                                                                "settings.worker.defaultToolTimeoutSeconds"),
                    MaxTaskPackageCharacters = ReadPositiveInt(workerNode, "maxTaskPackageCharacters", filePath,
                                                               "settings.worker.maxTaskPackageCharacters"),
                    MaxToolCalls =
                        ReadPositiveInt(workerNode, "maxToolCalls", filePath, "settings.worker.maxToolCalls"),
                    MaxToolOutputCharacters = ReadPositiveInt(workerNode, "maxToolOutputCharacters", filePath,
                                                              "settings.worker.maxToolOutputCharacters"),
                    MaxContextTokensPerRequest = ReadPositiveInt(workerNode, "maxContextTokensPerRequest", filePath,
                                                                 "settings.worker.maxContextTokensPerRequest"),
                    MaxCumulativeContextTokens = ReadPositiveInt(workerNode, "maxCumulativeContextTokens", filePath,
                                                                 "settings.worker.maxCumulativeContextTokens"),
                    MaxModelResponseCharacters = ReadPositiveInt(workerNode, "maxModelResponseCharacters", filePath,
                                                                 "settings.worker.maxModelResponseCharacters"),
                };
            }

            settings = new UserConfigSettings
            {
                MaxAgentSteps = ReadPositiveInt(settingsNode, "maxAgentSteps", filePath, "settings.maxAgentSteps"),
                ReservedOutputTokens =
                    ReadNonNegativeInt(settingsNode, "reservedOutputTokens", filePath, "settings.reservedOutputTokens"),
                CompactionThreshold =
                    ReadNonNegativeInt(settingsNode, "compactionThreshold", filePath, "settings.compactionThreshold"),
                DefaultToolTimeoutSeconds = ReadNonNegativeInt(settingsNode, "defaultToolTimeoutSeconds", filePath,
                                                               "settings.defaultToolTimeoutSeconds"),
                SessionDirectory = ReadString(settingsNode, "sessionDirectory", filePath),
                CommandRules = root["settings"] is JsonObject s && s["commandRules"] is not null
                    ? CommandRuleJson.Read(settingsNode, "commandRules", $"user config '{filePath}'")
                    : null,
                Worker = workerSettings,
                WindowsSandbox = BindWindowsSandbox(settingsNode, filePath),
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
    /// 绑定可信的 settings.windowsSandbox；字段缺失沿用保守默认（enabled=false、workspace-write）。
    /// 绑定只做结构校验（类型与枚举值），路径与组件存在性由组合器 fail-closed 校验。
    ///
    /// Binds the trusted settings.windowsSandbox; missing fields keep the
    /// conservative defaults (enabled=false, workspace-write). Binding checks
    /// structure only (types and enum values); paths and component existence are
    /// validated fail-closed by the composer.
    /// </summary>
    private static WindowsSandboxSettings? BindWindowsSandbox(JsonObject settingsNode, string filePath)
    {
        if (settingsNode["windowsSandbox"] is not JsonObject sandboxNode)
        {
            return null;
        }

        IReadOnlyList<string> additionalRoots = [];
        if (sandboxNode["additionalWriteRoots"] is not null)
        {
            if (sandboxNode["additionalWriteRoots"] is not JsonArray rootsArray)
            {
                throw new
                    InvalidDataException($"User config field 'settings.windowsSandbox.additionalWriteRoots' in '{filePath}' must be an array.");
            }

            var roots = new List<string>();
            foreach (var node in rootsArray)
            {
                if (node is not JsonValue { } value || value.GetValueKind() is not JsonValueKind.String)
                {
                    throw new
                        InvalidDataException($"User config field 'settings.windowsSandbox.additionalWriteRoots' in '{filePath}' must contain strings only.");
                }

                roots.Add(value.GetValue<string>());
            }

            additionalRoots = roots;
        }

        IReadOnlyDictionary<string, string>? extraEnvironment = null;
        if (sandboxNode["extraEnvironment"] is not null)
        {
            if (sandboxNode["extraEnvironment"] is not JsonObject environmentNode)
            {
                throw new
                    InvalidDataException($"User config field 'settings.windowsSandbox.extraEnvironment' in '{filePath}' must be an object.");
            }

            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in environmentNode)
            {
                if (string.IsNullOrWhiteSpace(name) || value is not JsonValue { } envValue ||
                    envValue.GetValueKind() is not JsonValueKind.String)
                {
                    throw new
                        InvalidDataException($"User config field 'settings.windowsSandbox.extraEnvironment' in '{filePath}' must map non-empty names to string values.");
                }

                environment[name] = envValue.GetValue<string>();
            }

            extraEnvironment = environment;
        }

        return new WindowsSandboxSettings
        {
            Enabled = ReadBool(sandboxNode, "enabled", filePath, "settings.windowsSandbox.enabled") ?? false,
            SetupExecutablePath  = ReadString(sandboxNode, "setupExecutablePath", filePath) ?? string.Empty,
            RunnerExecutablePath = ReadString(sandboxNode, "runnerExecutablePath", filePath) ?? string.Empty,
            SandboxHome          = ReadString(sandboxNode, "sandboxHome", filePath) ?? string.Empty,
            Policy               = ReadSandboxPolicyKind(sandboxNode, filePath),
            SandboxTempRoot      = ReadString(sandboxNode, "sandboxTempRoot", filePath),
            AdditionalWriteRoots = additionalRoots,
            ExtraEnvironment     = extraEnvironment,
        };
    }

    /// <summary>
    /// 读取可选布尔字段；类型非法时报出字段路径。
    /// Reads an optional boolean property, reporting the field path on a type mismatch.
    /// </summary>
    private static bool? ReadBool(JsonObject node, string property, string filePath, string fieldPath)
    {
        var value = node[property];
        if (value is null)
        {
            return null;
        }

        if (value.GetValueKind() is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidDataException($"User config field '{fieldPath}' in '{filePath}' must be a boolean.");
        }

        return value.GetValue<bool>();
    }

    /// <summary>
    /// 读取可选的沙箱策略种类；缺失沿用 workspace-write，非法值报出可接受的取值。
    /// Reads the optional sandbox policy kind; absent keeps workspace-write, an
    /// invalid value lists the accepted choices.
    /// </summary>
    private static SandboxPolicyKind ReadSandboxPolicyKind(JsonObject sandboxNode, string filePath)
    {
        var value = ReadString(sandboxNode, "policy", filePath);
        return value switch
        {
            null                    => SandboxPolicyKind.WorkspaceWrite,
            "workspace-write"       => SandboxPolicyKind.WorkspaceWrite,
            "read-only"             => SandboxPolicyKind.ReadOnly,
            _ => throw new InvalidDataException(
                                         $"User config field 'settings.windowsSandbox.policy' in '{filePath}' has an invalid value '{value}'; " +
                                         "expected 'workspace-write' or 'read-only'."),
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
    /// 通过共享映射读取可选 chatApi 字段；字段缺失时沿用 Chat Completions 默认值，
    /// 非法值抛出包含字段路径与原值的错误。
    ///
    /// Reads the optional chatApi property through the shared mapping; an absent property keeps the
    /// Chat Completions default, an invalid one fails with the field path and raw value.
    /// </summary>
    private static ChatApiKind ReadChatApi(JsonObject node, string property, string filePath, string fieldPath)
    {
        var value = ReadString(node, property, filePath);
        if (value is null)
        {
            return ChatApiKind.ChatCompletions;
        }

        return ChatApiKindParser.TryParse(value, out var api)
            ? api
            : throw new InvalidDataException(
                                             $"User config field '{fieldPath}' in '{filePath}' has an invalid value '{value}'; " +
                                             $"expected '{ChatApiKindParser.ChatCompletionsValue}' or '{ChatApiKindParser.ResponsesValue}'.");
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

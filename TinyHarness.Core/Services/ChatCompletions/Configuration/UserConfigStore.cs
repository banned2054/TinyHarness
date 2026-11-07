using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Services.ChatCompletions;

namespace TinyHarness.Core.Services.ChatCompletions.Configuration;

/// <summary>
///     <para>用户配置文件路径解析与读写。文件由 TinyHarness 拥有：保存时会整体重写为当前 schema。</para>
///     <para>
///         User config file path resolution and load/save. TinyHarness owns this file: saving rewrites it to
///         the current schema.
///     </para>
/// </summary>
public static class UserConfigStore
{
    /// <summary>
    ///     <para>覆盖用户配置目录的环境变量；用于便携部署与测试，普通用户无需设置。</para>
    ///     <para>Environment variable that overrides the user config directory; used for portable setups and tests.</para>
    /// </summary>
    public const string DirectoryOverrideVariable = "TINYHARNESS_USER_CONFIG_DIR";

    /// <summary>
    ///     <para>用户配置文件名。</para>
    ///     <para>The user config file name.</para>
    /// </summary>
    public const string FileName = "user-config.json";

    /// <summary>
    ///     <para>保存锁文件名后缀；锁以独占句柄为准，进程崩溃即由操作系统释放，残留文件不阻塞后续保存。</para>
    ///     <para>
    ///         Save-lock file name suffix; the lock is the exclusive handle, released by the OS on process death, and a
    ///         leftover file never blocks later saves.
    ///     </para>
    /// </summary>
    public const string SaveLockSuffix = ".lock";

    private const int SaveLockTimeoutMs    = 5000;
    private const int SaveLockRetryDelayMs = 25;

    /// <summary>
    ///     <para>保存与基线比较共用的写出设置，保证两侧产生同一规范形式。</para>
    ///     <para>Serialization settings shared by saving and baseline comparison so both produce one canonical form.</para>
    /// </summary>
    private static readonly JsonSerializerOptions SerializedJsonOptions = new() { WriteIndented = true };

    /// <summary>
    ///     <para>
    ///         严格绑定用户权限设置并识别形状：旧 defaultMode/composerModes 进入迁移待确认态，
    ///         新旧字段混用一律拒绝；未知枚举不得回退成更宽松的授权。
    ///     </para>
    ///     <para>
    ///         Strictly binds user permission settings and detects the shape: legacy defaultMode/composerModes
    ///         become pending-migration, mixing both shapes is always rejected, and unknown enums never fall back
    ///         to a looser authorization.
    ///     </para>
    /// </summary>
    private static PermissionSettings? BindPermissions(JsonObject settings)
    {
        if (!settings.TryGetPropertyValue("permissions", out var node)) return null;
        if (node is not JsonObject value) throw new InvalidDataException("settings.permissions must be an object.");

        var hasLegacy = value.ContainsKey("defaultMode") || value.ContainsKey("composerModes");
        if (hasLegacy && value.ContainsKey("defaultApprovalMode"))
            throw new InvalidDataException("settings.permissions cannot mix legacy (defaultMode/composerModes) " +
                                           "and current (defaultApprovalMode) fields.");

        return hasLegacy ? BindLegacyPermissions(value) : BindCurrentPermissions(value);
    }

    /// <summary>
    ///     <para>
    ///         按旧形状绑定权限设置：严格解析旧枚举并映射为审批模式；整体置迁移待确认态，其余配置不受影响。
    ///     </para>
    ///     <para>
    ///         Binds permission settings in the legacy shape: the legacy enums parse strictly and map to an
    ///         approval mode; the whole block becomes pending-migration while every other config field stays usable.
    ///     </para>
    /// </summary>
    private static PermissionSettings BindLegacyPermissions(JsonObject value)
    {
        // 旧形状仅按已知旧模式名做最小解析（迁移判定与过渡展示），不做授权解释。
        static string Parse(JsonNode? item)
        {
            var text = item?.GetValue<string>();
            if (text is null || !LegacyPermissionSelection.KnownLegacyModeNames.Contains(text, StringComparer.Ordinal))
                throw new InvalidDataException("Unknown permission mode.");
            return text;
        }

        var modes = value["composerModes"] is null
            ? LegacyPermissionSelection.KnownLegacyModeNames
            : value["composerModes"] is JsonArray array
                ? array.Select(Parse).ToArray()
                : throw new InvalidDataException("permissions.composerModes must be an array.");
        var legacyDefault = value["defaultMode"] is null
            ? "ReadOnly"
            : Parse(value["defaultMode"]);
        var result = new PermissionSettings
        {
            LegacyDefaultMode   = legacyDefault,
            LegacyComposerModes = modes,
            DefaultApprovalMode = LegacyPermissionSelectionMapper.FromLegacyName(legacyDefault) ??
                                  throw new InvalidDataException("Unknown legacy permission mode."),
            UseCurrentModel  = value["useCurrentModel"]?.GetValue<bool>() ?? true,
            ReviewerProfile  = value["reviewerProfile"]?.GetValue<string>(),
            ReviewerModel    = value["reviewerModel"]?.GetValue<string>(),
            PendingMigration = true
        };
        result.Validate();
        return result;
    }

    /// <summary>
    ///     <para>
    ///         按当前形状绑定权限设置：defaultApprovalMode 严格解析（缺省人工审批），旧字段一律缺席。
    ///     </para>
    ///     <para>
    ///         Binds permission settings in the current shape: defaultApprovalMode parses strictly (defaulting to
    ///         human approval) and no legacy field is present.
    ///     </para>
    /// </summary>
    private static PermissionSettings BindCurrentPermissions(JsonObject value)
    {
        var result = new PermissionSettings
        {
            DefaultApprovalMode = value["defaultApprovalMode"] is null
                ? SessionApprovalMode.HumanApproval
                : ParseApprovalMode(value["defaultApprovalMode"]),
            UseCurrentModel = value["useCurrentModel"]?.GetValue<bool>() ?? true,
            ReviewerProfile = value["reviewerProfile"]?.GetValue<string>(),
            ReviewerModel   = value["reviewerModel"]?.GetValue<string>()
        };
        result.Validate();
        return result;
    }

    /// <summary>
    ///     <para>严格解析审批模式：未知取值或大小写变体一律拒绝，不回落到更宽松的授权。</para>
    ///     <para>
    ///         Parses an approval mode strictly: unknown values and case variants are rejected and never fall
    ///         back to a looser authorization.
    ///     </para>
    /// </summary>
    private static SessionApprovalMode ParseApprovalMode(JsonNode? item)
    {
        var text = item?.GetValue<string>();
        if (text is null          || !Enum.TryParse<SessionApprovalMode>(text, out var mode) ||
            !Enum.IsDefined(mode) || mode.ToString() != text)
            throw new InvalidDataException("Unknown permission approval mode.");
        return mode;
    }

    /// <summary>
    ///     <para>
    ///         返回固定的用户配置文件绝对路径：优先 <see cref="DirectoryOverrideVariable" />；Windows 使用
    ///         %APPDATA%\tinyharness；其他平台使用 XDG_CONFIG_HOME 或 ~/.config；目录 API 不可用时退回当前目录。
    ///     </para>
    ///     <para>
    ///         Returns the fixed absolute user config path: <see cref="DirectoryOverrideVariable" /> first; on
    ///         Windows %APPDATA%\tinyharness; elsewhere XDG_CONFIG_HOME or ~/.config; falls back to the current
    ///         directory when the folder APIs are unavailable.
    ///     </para>
    /// </summary>
    public static string DefaultFilePath()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable(DirectoryOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            return Path.GetFullPath(Path.Combine(overrideDirectory.Trim(), FileName));

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData)) return Path.Combine(appData, "tinyharness", FileName);
        }
        else
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configHome))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(home)) configHome = Path.Combine(home, ".config");
            }

            if (!string.IsNullOrWhiteSpace(configHome)) return Path.Combine(configHome, "tinyharness", FileName);
        }

        return Path.GetFullPath(Path.Combine("tinyharness", FileName));
    }

    /// <summary>
    ///     <para>
    ///         读取用户配置；文件不存在时返回空配置（由调用方决定是否视为错误）。格式错误抛出带路径的异常。
    ///     </para>
    ///     <para>
    ///         Loads the user config; a missing file yields an empty config (the caller decides whether that is an
    ///         error). Malformed content throws with the file path included.
    ///     </para>
    /// </summary>
    public static async Task<UserConfig> LoadAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath)) return new UserConfig();

        var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        var root = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidDataException($"User config '{filePath}' must contain a JSON object.");
        return Bind(root, filePath);
    }

    /// <summary>
    ///     <para>保存用户配置：获取同目录保存锁，锁内校验候选、临时写入并安全替换，自动创建父目录。</para>
    ///     <para>
    ///         Saves the user config: acquires the sibling save lock, then validates the candidate, writes a temp file, and
    ///         safely replaces the target inside it, creating parent directories.
    ///     </para>
    /// </summary>
    public static Task SaveAsync(string filePath, UserConfig config, CancellationToken cancellationToken)
        => SaveAsync(filePath, config, null, cancellationToken);

    /// <summary>
    ///     <para>
    ///         保存用户配置：先校验候选，再获取同目录保存锁，锁内完成基线复查、临时写入与安全替换；
    ///         自动创建父目录。基线被其他入口改动则明确失败，由调用方重载后重试，不用旧快照覆盖新数据。
    ///     </para>
    ///     <para>
    ///         Saves the user config: validates the candidate first, acquires the sibling save lock, then re-checks
    ///         the baseline, writes a temp file and safely replaces the target inside one critical section, creating
    ///         parent directories. A baseline changed by another entry point fails explicitly; the caller reloads and
    ///         retries instead of overwriting newer data.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         基线复查与最终替换之间没有锁外的窗口；并发保存按锁串行，后到者会看到前者的写入并按基线失败。
    ///         锁跨进程生效，等待期间响应取消，超时抛出带锁路径的 IOException。
    ///     </para>
    ///     <para>
    ///         No window is left between the baseline recheck and the final replacement; concurrent saves serialize
    ///         on the lock, and the later one observes the earlier write and fails its baseline. The lock spans
    ///         processes, waiting honors cancellation, and a timeout throws an IOException naming the lock path.
    ///     </para>
    /// </remarks>
    public static async Task SaveAsync(string            filePath, UserConfig config, UserConfig? expectedBaseline,
                                       CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(config);

        // 先校验候选：非法输入不获取锁、不触碰文件系统。
        UserConfigValidator.Validate(config);

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        await using var saveLock = await OpenSaveLockAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (expectedBaseline is not null)
        {
            var current = await LoadAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (!ConfigEquals(current, expectedBaseline))
                throw new
                    InvalidDataException($"User config '{filePath}' changed while it was being edited; reload it and retry.");
        }

        var content  = WriteRoot(config).ToJsonString(SerializedJsonOptions) + Environment.NewLine;
        var tempPath = filePath                                              + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
            if (File.Exists(filePath))
                File.Replace(tempPath, filePath, null);
            else
                File.Move(tempPath, filePath);
        }
        catch
        {
            // 替换失败时尽力清理同目录临时文件；清理失败不得掩盖原始错误。
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }

    /// <summary>
    ///     <para>获取配置文件的跨进程保存锁；被占用时短暂等待，超时抛出带锁路径的 IOException。</para>
    ///     <para>
    ///         Acquires the cross-process save lock for a config file; waits briefly while held and throws an IOException
    ///         naming the lock path on timeout.
    ///     </para>
    /// </summary>
    private static async Task<FileStream> OpenSaveLockAsync(string filePath, CancellationToken cancellationToken)
    {
        var lockPath = filePath                + SaveLockSuffix;
        var deadline = Environment.TickCount64 + SaveLockTimeoutMs;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                await Task.Delay(SaveLockRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new
                    IOException($"Could not acquire the user-config save lock '{lockPath}' within " +
                                $"{SaveLockTimeoutMs} ms; another writer may be saving the same file. Retry the save.",
                                ex);
            }
        }
    }

    /// <summary>
    ///     <para>按同一写出规范比较两份配置的结构内容，忽略文件级空白差异。</para>
    ///     <para>Compares two configs structurally through the same canonical writer, ignoring file-level whitespace.</para>
    /// </summary>
    private static bool ConfigEquals(UserConfig left, UserConfig right) =>
        string.Equals(WriteRoot(left).ToJsonString(SerializedJsonOptions),
                      WriteRoot(right).ToJsonString(SerializedJsonOptions), StringComparison.Ordinal);

    private static JsonObject WriteRoot(UserConfig config)
    {
        var root                                                                      = new JsonObject();
        if (!string.IsNullOrWhiteSpace(config.DefaultProfile)) root["defaultProfile"] = config.DefaultProfile;

        if (config.Profiles.Count > 0)
        {
            var profiles = new JsonArray();
            foreach (var profile in config.Profiles)
            {
                var models = new JsonArray();
                foreach (var model in profile.Models)
                {
                    // 强转为 JsonNode? 以绑定非泛型 JsonArray.Add；泛型 Add<T> 重载不满足 trimming/AOT 约束。
                    var modelNode = new JsonObject
                    {
                        ["id"]                  = model.Id,
                        ["contextWindowTokens"] = model.ContextWindowTokens,
                        ["supportsImageInput"]  = model.SupportsImageInput,
                        ["supportsVideoInput"]  = model.SupportsVideoInput,
                        ["supportsPdfInput"]    = model.SupportsPdfInput
                    };
                    if (!string.IsNullOrWhiteSpace(model.DisplayName))
                        modelNode["displayName"] = model.DisplayName;
                    if (model.ReasoningEfforts is { Count: > 0 } efforts)
                    {
                        var effortArray = new JsonArray();
                        foreach (var effort in efforts)
                            effortArray.Add((JsonNode?)
                                            JsonValue.Create(ModelReasoningEffortParser.ToValueString(effort)));
                        modelNode["reasoningEfforts"] = effortArray;
                    }

                    if (model.DefaultReasoningEffort is { } defaultEffort)
                        modelNode["defaultReasoningEffort"] =
                            ModelReasoningEffortParser.ToValueString(defaultEffort);
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
                    ["chatApi"] = ChatApiKindParser.ToValueString(profile.ChatApi),
                    ["defaultModel"] =
                        string.IsNullOrWhiteSpace(profile.DefaultModel) ? null : profile.DefaultModel,
                    ["models"] = models
                };
                if (profile.Network is { } networkRoute)
                    profileNode["network"] = WriteNetworkRoute(networkRoute);
                profiles.Add((JsonNode?)profileNode);
            }

            root["profiles"] = profiles;
        }

        var settings = config.Settings;
        if (settings is not null)
        {
            var settingsNode = new JsonObject();
            if (settings.Permissions is { } permissions)
            {
                permissions.Validate();
                // 形状随实例走：携带旧形状字段时原样保留（迁移未被确认不静默改写）；
                // 确认迁移后的新形状只写出当前字段，旧字段从此消失。
                settingsNode["permissions"] = permissions.LegacyDefaultMode is { } legacyDefault
                    ? new JsonObject
                    {
                        ["defaultMode"] = legacyDefault,
                        ["composerModes"] = new JsonArray(permissions.LegacyComposerModes!
                                                                     .Select(mode =>
                                                                                 (JsonNode?)JsonValue.Create(mode))
                                                                     .ToArray()),
                        ["useCurrentModel"] = permissions.UseCurrentModel,
                        ["reviewerProfile"] = permissions.ReviewerProfile,
                        ["reviewerModel"]   = permissions.ReviewerModel
                    }
                    : new JsonObject
                    {
                        ["defaultApprovalMode"] = permissions.DefaultApprovalMode.ToString(),
                        ["useCurrentModel"]     = permissions.UseCurrentModel,
                        ["reviewerProfile"]     = permissions.ReviewerProfile,
                        ["reviewerModel"]       = permissions.ReviewerModel
                    };
            }

            if (settings.RunBudget is { } budget)
            {
                budget.Validate();
                settingsNode["runBudget"] =
                    JsonSerializer.SerializeToNode(budget, RunBudgetJsonContext.Default.RunBudgetLimits);
            }

            if (settings.ModelTimeouts is { } modelTimeouts)
            {
                modelTimeouts.Validate();
                settingsNode["modelTimeouts"] =
                    JsonSerializer.SerializeToNode(modelTimeouts, ModelTimeoutsJsonContext.Default.ModelTimeouts);
            }

            if (settings.MaxAgentSteps is { } maxAgentSteps) settingsNode["maxAgentSteps"] = maxAgentSteps;

            if (settings.ReservedOutputTokens is { } reservedOutputTokens)
                settingsNode["reservedOutputTokens"] = reservedOutputTokens;

            if (settings.CompactionThreshold is { } compactionThreshold)
                settingsNode["compactionThreshold"] = compactionThreshold;

            if (settings.DefaultToolTimeoutSeconds is { } defaultToolTimeoutSeconds)
                settingsNode["defaultToolTimeoutSeconds"] = defaultToolTimeoutSeconds;

            if (!string.IsNullOrWhiteSpace(settings.SessionDirectory))
                settingsNode["sessionDirectory"] = settings.SessionDirectory;

            if (!string.IsNullOrWhiteSpace(settings.SessionStoreDirectory))
                settingsNode["sessionStoreDirectory"] = settings.SessionStoreDirectory;

            if (settings.CommandRules is { Count: > 0 } commandRules)
                settingsNode["commandRules"] = CommandRuleJson.Write(commandRules);

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
                if (sandbox.AllowTcpLoopback)
                    sandboxNode["allowTcpLoopback"] = true;
                if (sandbox.Enabled)
                    sandboxNode["enabled"] = true;
                if (!string.IsNullOrWhiteSpace(sandbox.SetupExecutablePath))
                    sandboxNode["setupExecutablePath"] = sandbox.SetupExecutablePath;
                if (!string.IsNullOrWhiteSpace(sandbox.RunnerExecutablePath))
                    sandboxNode["runnerExecutablePath"] = sandbox.RunnerExecutablePath;
                if (!string.IsNullOrWhiteSpace(sandbox.SandboxHome))
                    sandboxNode["sandboxHome"] = sandbox.SandboxHome;
                if (sandbox.Policy != SandboxPolicyKind.WorkspaceWrite)
                    sandboxNode["policy"] =
                        sandbox.Policy == SandboxPolicyKind.ReadOnly ? "read-only" : "workspace-write";
                if (sandbox.NetworkPolicy != SandboxNetworkPolicy.Restricted)
                    sandboxNode["networkPolicy"] = WriteSandboxNetworkPolicy(sandbox.NetworkPolicy);
                if (sandbox.Proxy is { } proxy && proxy.Mode != SandboxProxyMode.FollowAppDefault)
                    sandboxNode["proxy"] = WriteSandboxProxyPolicy(proxy);
                if (!string.IsNullOrWhiteSpace(sandbox.SandboxTempRoot))
                    sandboxNode["sandboxTempRoot"] = sandbox.SandboxTempRoot;
                if (sandbox.AdditionalWriteRoots is { Count: > 0 } writeRoots)
                {
                    var rootsArray = new JsonArray();
                    foreach (var writeRoot in writeRoots)
                        rootsArray.Add((JsonNode?)JsonValue.Create(writeRoot));
                    sandboxNode["additionalWriteRoots"] = rootsArray;
                }

                if (sandbox.AdditionalReadRoots is { Count: > 0 } readRoots)
                {
                    var readRootsArray = new JsonArray();
                    foreach (var readRoot in readRoots)
                        readRootsArray.Add((JsonNode?)JsonValue.Create(readRoot));
                    sandboxNode["additionalReadRoots"] = readRootsArray;
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

            if (settings.Network is { } network)
            {
                var networkNode = new JsonObject
                {
                    ["defaultRoute"] = WriteNetworkRoute(network.DefaultRoute)
                };
                var proxies = new JsonArray();
                foreach (var proxy in network.Proxies)
                {
                    var proxyNode = new JsonObject
                    {
                        ["id"]       = proxy.Id,
                        ["name"]     = proxy.Name,
                        ["protocol"] = ProxyProtocolParser.ToValueString(proxy.Protocol),
                        ["host"]     = proxy.Host,
                        ["port"]     = proxy.Port
                    };
                    if (!string.IsNullOrWhiteSpace(proxy.CredentialTarget))
                        proxyNode["credentialTarget"] = proxy.CredentialTarget;
                    proxies.Add((JsonNode?)proxyNode);
                }

                networkNode["proxies"]  = proxies;
                settingsNode["network"] = networkNode;
            }

            if (settingsNode.Count > 0) root["settings"] = settingsNode;
        }

        return root;
    }

    /// <summary>
    ///     <para>手工、无反射地把 JSON 绑定为 <see cref="UserConfig" />，错误消息包含字段路径与文件位置。</para>
    ///     <para>
    ///         Binds JSON to <see cref="UserConfig" /> manually without reflection; errors include field paths and
    ///         the file location.
    ///     </para>
    /// </summary>
    private static UserConfig Bind(JsonObject root, string filePath)
    {
        var defaultProfile = ReadString(root, "defaultProfile", filePath);

        UserProfile[]? profiles = null;
        if (root["profiles"] is not null)
        {
            if (root["profiles"] is not JsonArray profileArray)
                throw new InvalidDataException($"User config field 'profiles' in '{filePath}' must be an array.");

            profiles = new UserProfile[profileArray.Count];
            var seenProfiles = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < profileArray.Count; i++)
            {
                if (profileArray[i] is not JsonObject profileNode)
                    throw new
                        InvalidDataException($"User config field 'profiles[{i}]' in '{filePath}' must be an object.");

                var name = ReadString(profileNode, "name", filePath)
                        ?? throw new
                               InvalidDataException($"User config field 'profiles[{i}].name' in '{filePath}' is required and must be a non-empty string.");
                if (!seenProfiles.Add(name))
                    throw new
                        InvalidDataException($"User config field 'profiles[{i}].name' in '{filePath}' duplicates profile name '{name}'.");

                UserProfileModel[]? models = null;
                if (profileNode["models"] is not null)
                {
                    if (profileNode["models"] is not JsonArray modelArray)
                        throw new
                            InvalidDataException($"User config field 'profiles[{i}].models' in '{filePath}' must be an array.");

                    models = new UserProfileModel[modelArray.Count];
                    var seenModels = new HashSet<string>(StringComparer.Ordinal);
                    for (var m = 0; m < modelArray.Count; m++)
                    {
                        if (modelArray[m] is not JsonObject modelNode)
                            throw new
                                InvalidDataException($"User config field 'profiles[{i}].models[{m}]' in '{filePath}' must be an object.");

                        var id = ReadString(modelNode, "id", filePath)
                              ?? throw new
                                     InvalidDataException($"User config field 'profiles[{i}].models[{m}].id' in '{filePath}' is required and must be a non-empty string.");
                        if (!seenModels.Add(id))
                            throw new
                                InvalidDataException($"User config field 'profiles[{i}].models[{m}].id' in '{filePath}' duplicates model id '{id}'.");

                        var contextWindow = ReadPositiveInt(modelNode, "contextWindowTokens", filePath,
                                                            $"profiles[{i}].models[{m}].contextWindowTokens")
                                         ?? throw new
                                                InvalidDataException($"User config field 'profiles[{i}].models[{m}].contextWindowTokens' in '{filePath}' is required and must be a positive integer.");
                        var displayName = ReadString(modelNode, "displayName", filePath);

                        IReadOnlyList<ModelReasoningEffort>? reasoningEfforts = null;
                        if (modelNode["reasoningEfforts"] is not null)
                        {
                            if (modelNode["reasoningEfforts"] is not JsonArray effortArray)
                                throw new
                                    InvalidDataException($"User config field 'profiles[{i}].models[{m}].reasoningEfforts' in '{filePath}' must be an array.");

                            var parsed      = new List<ModelReasoningEffort>();
                            var seenEfforts = new HashSet<ModelReasoningEffort>();
                            for (var e = 0; e < effortArray.Count; e++)
                            {
                                if (effortArray[e] is not JsonValue { } effortValue        ||
                                    effortValue.GetValueKind() is not JsonValueKind.String ||
                                    !ModelReasoningEffortParser.TryParse(effortValue.GetValue<string>(),
                                                                         out var effort))
                                    throw new
                                        InvalidDataException($"User config field 'profiles[{i}].models[{m}].reasoningEfforts[{e}]' in '{filePath}' has an invalid effort value; expected one of: {ModelReasoningEffortParser.AcceptedValuesText}.");
                                if (!seenEfforts.Add(effort))
                                    throw new
                                        InvalidDataException($"User config field 'profiles[{i}].models[{m}].reasoningEfforts[{e}]' in '{filePath}' duplicates effort '{ModelReasoningEffortParser.ToValueString(effort)}'.");

                                parsed.Add(effort);
                            }

                            // 空数组与缺省同义：未声明强度能力。
                            if (parsed.Count > 0) reasoningEfforts = parsed;
                        }

                        var defaultEffort = ReadReasoningEffort(modelNode, "defaultReasoningEffort", filePath,
                                                                $"profiles[{i}].models[{m}].defaultReasoningEffort");
                        if (defaultEffort is { } declaredDefault &&
                            (reasoningEfforts is null || !reasoningEfforts.Contains(declaredDefault)))
                            throw new
                                InvalidDataException($"User config field 'profiles[{i}].models[{m}].defaultReasoningEffort' in '{filePath}' must be one of the model's declared reasoningEfforts.");
                        models[m] = new UserProfileModel
                        {
                            Id                  = id,
                            DisplayName         = displayName,
                            ContextWindowTokens = contextWindow,
                            SupportsImageInput = ReadBool(modelNode, "supportsImageInput", filePath,
                                                          $"profiles[{i}].models[{m}].supportsImageInput") ?? false,
                            SupportsVideoInput = ReadBool(modelNode, "supportsVideoInput", filePath,
                                                          $"profiles[{i}].models[{m}].supportsVideoInput") ?? false,
                            SupportsPdfInput = ReadBool(modelNode, "supportsPdfInput", filePath,
                                                        $"profiles[{i}].models[{m}].supportsPdfInput") ?? false,
                            ReasoningEfforts       = reasoningEfforts,
                            DefaultReasoningEffort = defaultEffort
                        };
                    }
                }

                var endpoint = ReadString(profileNode, "endpoint", filePath) ?? string.Empty;
                if (UserConfigValidator.GetEndpointError(endpoint) is { } endpointError)
                    throw new
                        InvalidDataException($"User config field 'profiles[{i}].endpoint' in '{filePath}' {endpointError}.");
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
                    Network = BindNetworkRoute(profileNode, "network", filePath,
                                               $"profiles[{i}].network")
                };
            }
        }

        UserConfigSettings? settings = null;
        if (root["settings"] is not JsonObject settingsNode)
        {
            var config = new UserConfig
            {
                DefaultProfile = defaultProfile,
                Profiles       = profiles ?? [],
                Settings       = null
            };
            UserConfigValidator.Validate(config);
            return config;
        }

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
                                                             "settings.worker.maxModelResponseCharacters")
            };
        }

        settings = new UserConfigSettings
        {
            Permissions   = BindPermissions(settingsNode),
            RunBudget     = BindRunBudget(settingsNode, filePath),
            ModelTimeouts = BindModelTimeouts(settingsNode, filePath),
            MaxAgentSteps = ReadNonNegativeInt(settingsNode, "maxAgentSteps", filePath, "settings.maxAgentSteps"),
            ReservedOutputTokens =
                ReadNonNegativeInt(settingsNode, "reservedOutputTokens", filePath, "settings.reservedOutputTokens"),
            CompactionThreshold =
                ReadNonNegativeInt(settingsNode, "compactionThreshold", filePath, "settings.compactionThreshold"),
            DefaultToolTimeoutSeconds = ReadNonNegativeInt(settingsNode, "defaultToolTimeoutSeconds", filePath,
                                                           "settings.defaultToolTimeoutSeconds"),
            SessionDirectory      = ReadString(settingsNode, "sessionDirectory", filePath),
            SessionStoreDirectory = ReadString(settingsNode, "sessionStoreDirectory", filePath),
            CommandRules = root["settings"] is JsonObject s && s["commandRules"] is not null
                ? CommandRuleJson.Read(settingsNode, "commandRules", $"user config '{filePath}'")
                : null,
            Worker         = workerSettings,
            WindowsSandbox = BindWindowsSandbox(settingsNode, filePath),
            Network        = BindNetworkSettings(settingsNode, filePath)
        };

        var result = new UserConfig
        {
            DefaultProfile = defaultProfile,
            Profiles       = profiles ?? [],
            Settings       = settings
        };
        UserConfigValidator.Validate(result);
        return result;
    }

    private static JsonObject WriteNetworkRoute(NetworkRoute route) => new()
    {
        ["mode"]    = NetworkModeParser.ToValueString(route.Mode),
        ["proxyId"] = route.ProxyId
    };

    private static NetworkRoute? BindNetworkRoute(JsonObject parent, string property, string filePath,
                                                  string     fieldPath)
    {
        if (parent[property] is null) return null;
        if (parent[property] is not JsonObject routeNode)
            throw new InvalidDataException($"User config field '{fieldPath}' in '{filePath}' must be an object.");

        var modeValue = ReadString(routeNode, "mode", filePath)
                     ?? throw new
                            InvalidDataException($"User config field '{fieldPath}.mode' in '{filePath}' is required.");
        if (!NetworkModeParser.TryParse(modeValue, out var mode))
            throw new
                InvalidDataException($"User config field '{fieldPath}.mode' in '{filePath}' has an invalid value '{modeValue}'; expected 'system', 'direct', or 'proxy'.");

        return new NetworkRoute
        {
            Mode    = mode,
            ProxyId = ReadString(routeNode, "proxyId", filePath)
        };
    }

    private static NetworkSettings? BindNetworkSettings(JsonObject settingsNode, string filePath)
    {
        if (settingsNode["network"] is null) return null;
        if (settingsNode["network"] is not JsonObject networkNode)
            throw new InvalidDataException($"User config field 'settings.network' in '{filePath}' must be an object.");

        var defaultRoute = networkNode["defaultRoute"] is null
            ? new NetworkRoute()
            : BindNetworkRoute(networkNode, "defaultRoute", filePath, "settings.network.defaultRoute")!;
        ProxyDefinition[] proxies = [];
        if (networkNode["proxies"] is not null)
        {
            if (networkNode["proxies"] is not JsonArray proxyArray)
                throw new
                    InvalidDataException($"User config field 'settings.network.proxies' in '{filePath}' must be an array.");

            proxies = new ProxyDefinition[proxyArray.Count];
            for (var i = 0; i < proxyArray.Count; i++)
            {
                if (proxyArray[i] is not JsonObject proxyNode)
                    throw new
                        InvalidDataException($"User config field 'settings.network.proxies[{i}]' in '{filePath}' must be an object.");
                var protocolText = ReadString(proxyNode, "protocol", filePath)
                                ?? throw new
                                       InvalidDataException($"User config field 'settings.network.proxies[{i}].protocol' in '{filePath}' is required.");
                if (!ProxyProtocolParser.TryParse(protocolText, out var protocol))
                    throw new
                        InvalidDataException($"User config field 'settings.network.proxies[{i}].protocol' in '{filePath}' has an invalid value '{protocolText}'; expected 'http', 'https', or 'socks5'.");
                var port = ReadPositiveInt(proxyNode, "port", filePath, $"settings.network.proxies[{i}].port")
                        ?? throw new
                               InvalidDataException($"User config field 'settings.network.proxies[{i}].port' in '{filePath}' is required.");
                proxies[i] = new ProxyDefinition
                {
                    Id               = ReadString(proxyNode, "id", filePath)   ?? string.Empty,
                    Name             = ReadString(proxyNode, "name", filePath) ?? string.Empty,
                    Protocol         = protocol,
                    Host             = ReadString(proxyNode, "host", filePath) ?? string.Empty,
                    Port             = port,
                    CredentialTarget = ReadString(proxyNode, "credentialTarget", filePath)
                };
            }
        }

        return new NetworkSettings { DefaultRoute = defaultRoute, Proxies = proxies };
    }

    /// <summary>
    ///     <para>严格绑定可信累计预算；拒绝未知字段、错误类型和非法上限。</para>
    ///     <para>Strictly binds trusted cumulative limits, rejecting unknown fields, wrong types and invalid limits.</para>
    /// </summary>
    private static RunBudgetLimits? BindRunBudget(JsonObject settings, string filePath)
    {
        if (!settings.TryGetPropertyValue("runBudget", out var node)) return null;
        try
        {
            if (node is not JsonObject supplied) throw new JsonException("runBudget must be an object");

            // source generation 会将缺省 init 属性赋为类型零值，先补齐宿主默认值。
            var complete =
                JsonSerializer.SerializeToNode(new RunBudgetLimits(), RunBudgetJsonContext.Default.RunBudgetLimits)!
                              .AsObject();
            foreach (var (name, value) in supplied) complete[name] = value?.DeepClone();

            var budget = complete.Deserialize(RunBudgetJsonContext.Default.RunBudgetLimits)
                      ?? throw new JsonException("runBudget must be an object");
            budget.Validate();
            return budget;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException)
        {
            throw new
                InvalidDataException($"User config field 'settings.runBudget' in '{filePath}' is invalid: {ex.Message}",
                                     ex);
        }
    }

    /// <summary>
    ///     <para>
    ///         严格绑定可信模型请求超时策略；拒绝未知字段、错误类型和非法期限，
    ///         节缺失返回 null 沿用内置默认值。
    ///     </para>
    ///     <para>
    ///         Strictly binds the trusted model-request timeout policy, rejecting unknown fields, wrong types and
    ///         invalid deadlines; a missing section returns null and keeps the built-in defaults.
    ///     </para>
    /// </summary>
    private static ModelTimeouts? BindModelTimeouts(JsonObject settings, string filePath)
    {
        if (!settings.TryGetPropertyValue("modelTimeouts", out var node)) return null;
        try
        {
            if (node is not JsonObject supplied) throw new JsonException("modelTimeouts must be an object");

            // source generation 会将缺省 init 属性赋为类型零值，先补齐宿主默认值。
            var complete =
                JsonSerializer.SerializeToNode(ModelTimeouts.Default, ModelTimeoutsJsonContext.Default.ModelTimeouts)!
                              .AsObject();
            foreach (var (name, value) in supplied) complete[name] = value?.DeepClone();

            var timeouts = complete.Deserialize(ModelTimeoutsJsonContext.Default.ModelTimeouts)
                          ?? throw new JsonException("modelTimeouts must be an object");
            timeouts.Validate();
            return timeouts;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException)
        {
            throw new
                InvalidDataException($"User config field 'settings.modelTimeouts' in '{filePath}' is invalid: {ex.Message}",
                                     ex);
        }
    }

    /// <summary>
    ///     <para>
    ///         绑定可信的 settings.windowsSandbox；字段缺失沿用保守默认（enabled=false、workspace-write、
    ///         网络受限、跟随软件默认代理）。绑定只做结构校验（类型与枚举值），
    ///         路径与组件存在性由组合器 fail-closed 校验。
    ///     </para>
    ///     <para>
    ///         Binds the trusted settings.windowsSandbox; missing fields keep the conservative defaults
    ///         (enabled=false, workspace-write, restricted network, app-default proxy). Binding checks
    ///         structure only (types and enum values); paths and component existence are validated
    ///         fail-closed by the composer.
    ///     </para>
    /// </summary>
    private static WindowsSandboxSettings? BindWindowsSandbox(JsonObject settingsNode, string filePath)
    {
        if (settingsNode["windowsSandbox"] is not JsonObject sandboxNode) return null;

        IReadOnlyList<string> additionalRoots = [];
        if (sandboxNode["additionalWriteRoots"] is not null)
        {
            if (sandboxNode["additionalWriteRoots"] is not JsonArray rootsArray)
                throw new
                    InvalidDataException($"User config field 'settings.windowsSandbox.additionalWriteRoots' in '{filePath}' must be an array.");

            var roots = new List<string>();
            foreach (var node in rootsArray)
            {
                if (node is not JsonValue value || value.GetValueKind() is not JsonValueKind.String)
                    throw new
                        InvalidDataException($"User config field 'settings.windowsSandbox.additionalWriteRoots' in '{filePath}' must contain strings only.");

                roots.Add(value.GetValue<string>());
            }

            additionalRoots = roots;
        }

        var additionalReadRoots = BindAdditionalReadRoots(sandboxNode, filePath);

        IReadOnlyDictionary<string, string>? extraEnvironment = null;
        if (sandboxNode["extraEnvironment"] is null)
            return new WindowsSandboxSettings
            {
                Enabled = ReadBool(sandboxNode, "enabled", filePath, "settings.windowsSandbox.enabled") ?? false,
                AllowTcpLoopback = ReadBool(sandboxNode, "allowTcpLoopback", filePath,
                                            "settings.windowsSandbox.allowTcpLoopback") ?? false,
                SetupExecutablePath  = ReadString(sandboxNode, "setupExecutablePath", filePath)  ?? string.Empty,
                RunnerExecutablePath = ReadString(sandboxNode, "runnerExecutablePath", filePath) ?? string.Empty,
                SandboxHome          = ReadString(sandboxNode, "sandboxHome", filePath)          ?? string.Empty,
                Policy               = ReadSandboxPolicyKind(sandboxNode, filePath),
                NetworkPolicy        = ReadSandboxNetworkPolicy(sandboxNode, filePath),
                Proxy                = ReadSandboxProxyPolicy(sandboxNode, filePath),
                SandboxTempRoot      = ReadString(sandboxNode, "sandboxTempRoot", filePath),
                AdditionalWriteRoots = additionalRoots,
                AdditionalReadRoots  = additionalReadRoots,
                ExtraEnvironment     = extraEnvironment
            };

        if (sandboxNode["extraEnvironment"] is not JsonObject environmentNode)
            throw new
                InvalidDataException($"User config field 'settings.windowsSandbox.extraEnvironment' in '{filePath}' must be an object.");

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in environmentNode)
        {
            if (string.IsNullOrWhiteSpace(name)     ||
                value is not JsonValue { } envValue ||
                envValue.GetValueKind() is not JsonValueKind.String)
                throw new
                    InvalidDataException($"User config field 'settings.windowsSandbox.extraEnvironment' in '{filePath}' must map non-empty names to string values.");

            environment[name] = envValue.GetValue<string>();
        }

        extraEnvironment = environment;


        return new WindowsSandboxSettings
        {
            Enabled = ReadBool(sandboxNode, "enabled", filePath, "settings.windowsSandbox.enabled") ?? false,
            AllowTcpLoopback =
                ReadBool(sandboxNode, "allowTcpLoopback", filePath, "settings.windowsSandbox.allowTcpLoopback") ??
                false,
            SetupExecutablePath  = ReadString(sandboxNode, "setupExecutablePath", filePath)  ?? string.Empty,
            RunnerExecutablePath = ReadString(sandboxNode, "runnerExecutablePath", filePath) ?? string.Empty,
            SandboxHome          = ReadString(sandboxNode, "sandboxHome", filePath)          ?? string.Empty,
            Policy               = ReadSandboxPolicyKind(sandboxNode, filePath),
            NetworkPolicy        = ReadSandboxNetworkPolicy(sandboxNode, filePath),
            Proxy                = ReadSandboxProxyPolicy(sandboxNode, filePath),
            SandboxTempRoot      = ReadString(sandboxNode, "sandboxTempRoot", filePath),
            AdditionalWriteRoots = additionalRoots,
            AdditionalReadRoots  = additionalReadRoots,
            ExtraEnvironment     = extraEnvironment
        };
    }

    /// <summary>
    ///     <para>绑定可选的 additionalReadRoots；缺省为空列表，字符串之外的元素报出字段路径。</para>
    ///     <para>
    ///         Binds the optional additionalReadRoots; absent keeps an empty list, and non-string
    ///         elements fail with the field path.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<string> BindAdditionalReadRoots(JsonObject sandboxNode, string filePath)
    {
        if (sandboxNode["additionalReadRoots"] is null) return [];
        if (sandboxNode["additionalReadRoots"] is not JsonArray rootsArray)
            throw new
                InvalidDataException($"User config field 'settings.windowsSandbox.additionalReadRoots' in '{filePath}' must be an array.");

        var roots = new List<string>();
        foreach (var node in rootsArray)
        {
            if (node is not JsonValue value || value.GetValueKind() is not JsonValueKind.String)
                throw new
                    InvalidDataException($"User config field 'settings.windowsSandbox.additionalReadRoots' in '{filePath}' must contain strings only.");

            roots.Add(value.GetValue<string>());
        }

        return roots;
    }

    /// <summary>
    ///     <para>读取可选的沙箱网络策略；缺失沿用 Restricted（默认逐次审批），非法值列出可接受取值。</para>
    ///     <para>
    ///         Reads the optional sandbox network policy; absent keeps Restricted (per-call approval),
    ///         an invalid value lists the accepted choices.
    ///     </para>
    /// </summary>
    private static SandboxNetworkPolicy ReadSandboxNetworkPolicy(JsonObject sandboxNode, string filePath)
    {
        var value = ReadString(sandboxNode, "networkPolicy", filePath);
        return value switch
        {
            null         => SandboxNetworkPolicy.Restricted,
            "restricted" => SandboxNetworkPolicy.Restricted,
            "enabled"    => SandboxNetworkPolicy.Enabled,
            _ => throw new
                InvalidDataException($"User config field 'settings.windowsSandbox.networkPolicy' in '{filePath}' has an invalid value '{value}'; " +
                                     "expected 'restricted' or 'enabled'.")
        };
    }

    /// <summary>
    ///     <para>
    ///         读取可选的沙箱代理策略；缺失或 mode 缺失沿用 FollowAppDefault，named 必须携带 proxyName。
    ///     </para>
    ///     <para>
    ///         Reads the optional sandbox proxy policy; absent node or mode keeps FollowAppDefault,
    ///         and named requires a proxyName.
    ///     </para>
    /// </summary>
    private static SandboxProxyPolicy ReadSandboxProxyPolicy(JsonObject sandboxNode, string filePath)
    {
        if (sandboxNode["proxy"] is null) return new SandboxProxyPolicy();
        if (sandboxNode["proxy"] is not JsonObject proxyNode)
            throw new
                InvalidDataException($"User config field 'settings.windowsSandbox.proxy' in '{filePath}' must be an object.");

        var modeValue = ReadString(proxyNode, "mode", filePath);
        var mode = modeValue switch
        {
            null                 => SandboxProxyMode.FollowAppDefault,
            "follow-app-default" => SandboxProxyMode.FollowAppDefault,
            "system"             => SandboxProxyMode.System,
            "direct"             => SandboxProxyMode.Direct,
            "named"              => SandboxProxyMode.Named,
            _ => throw new
                InvalidDataException($"User config field 'settings.windowsSandbox.proxy.mode' in '{filePath}' has an invalid value '{modeValue}'; " +
                                     "expected 'follow-app-default', 'system', 'direct', or 'named'.")
        };
        var proxyName = ReadString(proxyNode, "proxyName", filePath);
        if (mode == SandboxProxyMode.Named && string.IsNullOrWhiteSpace(proxyName))
            throw new
                InvalidDataException($"User config field 'settings.windowsSandbox.proxy.proxyName' in '{filePath}' is required when mode is 'named'.");

        return new SandboxProxyPolicy { Mode = mode, ProxyName = proxyName };
    }

    /// <summary>把沙箱网络策略写成规范字符串。Writes the sandbox network policy as its canonical string.</summary>
    private static string WriteSandboxNetworkPolicy(SandboxNetworkPolicy policy) => policy switch
    {
        SandboxNetworkPolicy.Enabled => "enabled",
        _                            => "restricted"
    };

    /// <summary>
    ///     把沙箱代理策略写成规范 JSON；凭据引用不写入，只保留名称。Writes the sandbox proxy policy as canonical JSON without credential
    ///     references, keeping only the name.
    /// </summary>
    private static JsonObject WriteSandboxProxyPolicy(SandboxProxyPolicy proxy)
    {
        var node = new JsonObject
        {
            ["mode"] = proxy.Mode switch
            {
                SandboxProxyMode.System => "system",
                SandboxProxyMode.Direct => "direct",
                SandboxProxyMode.Named  => "named",
                _                       => "follow-app-default"
            }
        };
        if (!string.IsNullOrWhiteSpace(proxy.ProxyName)) node["proxyName"] = proxy.ProxyName;
        return node;
    }

    /// <summary>
    ///     <para>读取可选布尔字段；类型非法时报出字段路径。</para>
    ///     <para>Reads an optional boolean property, reporting the field path on a type mismatch.</para>
    /// </summary>
    private static bool? ReadBool(JsonObject node, string property, string filePath, string fieldPath)
    {
        var value = node[property];
        if (value is null) return null;

        return value.GetValueKind() is not JsonValueKind.True and not JsonValueKind.False
            ? throw new InvalidDataException($"User config field '{fieldPath}' in '{filePath}' must be a boolean.")
            : value.GetValue<bool>();
    }

    /// <summary>
    ///     <para>读取可选的沙箱策略种类；缺失沿用 workspace-write，非法值报出可接受的取值。</para>
    ///     <para>
    ///         Reads the optional sandbox policy kind; absent keeps workspace-write, an invalid value lists the
    ///         accepted choices.
    ///     </para>
    /// </summary>
    private static SandboxPolicyKind ReadSandboxPolicyKind(JsonObject sandboxNode, string filePath)
    {
        var value = ReadString(sandboxNode, "policy", filePath);
        return value switch
        {
            null              => SandboxPolicyKind.WorkspaceWrite,
            "workspace-write" => SandboxPolicyKind.WorkspaceWrite,
            "read-only"       => SandboxPolicyKind.ReadOnly,
            _ => throw new
                InvalidDataException($"User config field 'settings.windowsSandbox.policy' in '{filePath}' has an invalid value '{value}'; " +
                                     "expected 'workspace-write' or 'read-only'.")
        };
    }

    /// <summary>
    ///     <para>读取可选字符串字段。</para>
    ///     <para>Reads an optional string property.</para>
    /// </summary>
    private static string? ReadString(JsonObject node, string property, string filePath)
    {
        var value = node[property];
        if (value is null) return null;

        if (value.GetValueKind() is not JsonValueKind.String)
            throw new InvalidDataException($"User config field '{property}' in '{filePath}' must be a string.");

        var text = value.GetValue<string>();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    ///     <para>读取可选正整数字段，接受 JSON 数字或整数字符串。</para>
    ///     <para>Reads an optional positive integer property from a JSON number or an integer string.</para>
    /// </summary>
    private static int? ReadPositiveInt(JsonObject node, string property, string filePath, string fieldPath)
    {
        var value = ReadIntValue(node, property, filePath, fieldPath);
        return value is null or > 0
            ? value
            : throw new
                InvalidDataException($"User config field '{fieldPath}' in '{filePath}' must be a positive integer.");
    }

    /// <summary>
    ///     <para>读取可选非负整数字段，接受 JSON 数字或整数字符串。</para>
    ///     <para>Reads an optional non-negative integer property from a JSON number or an integer string.</para>
    /// </summary>
    private static int? ReadNonNegativeInt(JsonObject node, string property, string filePath, string fieldPath)
    {
        var value = ReadIntValue(node, property, filePath, fieldPath);
        if (value is null or >= 0) return value;

        throw new
            InvalidDataException($"User config field '{fieldPath}' in '{filePath}' must be a non-negative integer.");
    }

    /// <summary>
    ///     <para>
    ///         通过共享映射读取可选 chatApi 字段；字段缺失时沿用 Chat Completions 默认值，
    ///         非法值抛出包含字段路径与原值的错误。
    ///     </para>
    ///     <para>
    ///         Reads the optional chatApi property through the shared mapping; an absent property keeps the Chat
    ///         Completions default, an invalid one fails with the field path and raw value.
    ///     </para>
    /// </summary>
    private static ChatApiKind ReadChatApi(JsonObject node, string property, string filePath, string fieldPath)
    {
        var value = ReadString(node, property, filePath);
        if (value is null) return ChatApiKind.ChatCompletions;

        return ChatApiKindParser.TryParse(value, out var api)
            ? api
            : throw new
                InvalidDataException($"User config field '{fieldPath}' in '{filePath}' has an invalid value '{value}'; " +
                                     $"expected '{ChatApiKindParser.ChatCompletionsValue}' or '{ChatApiKindParser.ResponsesValue}'.");
    }

    /// <summary>
    ///     <para>
    ///         通过共享映射读取可选强度档位字段；非法值抛出包含字段路径、原值与可接受值列表的错误。
    ///     </para>
    ///     <para>
    ///         Reads the optional reasoning effort property through the shared mapping; an invalid value fails with
    ///         the field path, raw value, and the accepted values.
    ///     </para>
    /// </summary>
    private static ModelReasoningEffort? ReadReasoningEffort(JsonObject node, string property, string filePath,
                                                             string     fieldPath)
    {
        var value = ReadString(node, property, filePath);
        if (value is null) return null;

        return ModelReasoningEffortParser.TryParse(value, out var effort)
            ? effort
            : throw new
                InvalidDataException($"User config field '{fieldPath}' in '{filePath}' has an invalid value '{value}'; " +
                                     $"expected one of: {ModelReasoningEffortParser.AcceptedValuesText}.");
    }

    /// <summary>
    ///     <para>读取整数字段原始值，类型非法时给出明确诊断。</para>
    ///     <para>Reads the raw integer value and reports clear diagnostics for wrong types.</para>
    /// </summary>
    private static int? ReadIntValue(JsonObject node, string property, string filePath, string fieldPath)
    {
        var value = node[property];
        if (value is null) return null;

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

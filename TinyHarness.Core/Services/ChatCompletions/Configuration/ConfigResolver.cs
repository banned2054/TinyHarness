using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Configuration;

namespace TinyHarness.Core.Services.ChatCompletions.Configuration;

/// <summary>
///     <para>
///         按固定顺序解析生效配置：显式 `--config` → 用户配置 → 内置默认值。不隐式合并多份文件，
///         也绝不自动读取工作目录中的 tinyharness.json；显式指定的配置文件才参与解析。
///     </para>
///     <para>
///         Resolves the effective configuration in a fixed order: explicit `--config` → user config → built-in
///         defaults. Multiple files are never merged implicitly, and a tinyharness.json in the working directory is
///         never read automatically; only an explicitly specified config file participates.
///     </para>
/// </summary>
public static class ConfigResolver
{
    /// <summary>
    ///     <para>
    ///         解析生效配置与来源信息。显式指定但不存在的配置文件抛出使用错误，不发模型请求。
    ///         用户配置路径可注入，供测试与嵌入式调用使用；省略时使用默认用户配置路径。
    ///     </para>
    ///     <para>
    ///         Resolves the effective configuration and its source. An explicitly specified but missing file throws
    ///         a usage error before any model request is made. The user config path can be injected for tests and
    ///         embedded callers; an omitted value means the default user config path.
    ///     </para>
    /// </summary>
    public static async Task<ConfigResolution> ResolveAsync(
        string? explicitConfigPath, CancellationToken cancellationToken, string? userConfigPath = null)
    {
        var userConfigPathResolved = string.IsNullOrWhiteSpace(userConfigPath)
            ? UserConfigStore.DefaultFilePath()
            : Path.GetFullPath(userConfigPath);
        var userExists = File.Exists(userConfigPathResolved);

        if (!string.IsNullOrWhiteSpace(explicitConfigPath))
        {
            var explicitPath = Path.GetFullPath(explicitConfigPath);
            if (!File.Exists(explicitPath))
                throw new ConfigException($"Configuration file not found: {explicitPath}" + Environment.NewLine +
                                          "Pass an existing file via --config, or run 'tinyharness config show' to see the lookup paths.",
                                          true);

            var explicitConfig = await ConfigurationLoader.LoadAsync(explicitPath, cancellationToken)
                                                          .ConfigureAwait(false);
            return new ConfigResolution
            {
                Config           = explicitConfig,
                Source           = ConfigSourceKind.ExplicitFile,
                SourcePath       = explicitPath,
                UserConfigPath   = userConfigPathResolved,
                UserConfigExists = userExists
            };
        }

        if (!userExists)
            return new ConfigResolution
            {
                Config           = ConfigurationLoader.NormalizePaths(new TinyHarnessConfig()),
                Source           = ConfigSourceKind.Defaults,
                UserConfigPath   = userConfigPathResolved,
                UserConfigExists = false
            };
        var userConfig = await UserConfigStore.LoadAsync(userConfigPathResolved, cancellationToken)
                                              .ConfigureAwait(false);
        var config = FromUserConfig(userConfig, userConfigPathResolved);
        return new ConfigResolution
        {
            Config           = ConfigurationLoader.NormalizePaths(config),
            Source           = ConfigSourceKind.UserConfig,
            SourcePath       = userConfigPathResolved,
            ProfileName      = string.IsNullOrWhiteSpace(userConfig.DefaultProfile) ? null : userConfig.DefaultProfile,
            ModelName        = string.IsNullOrWhiteSpace(config.Model) ? null : config.Model,
            UserConfigPath   = userConfigPathResolved,
            UserConfigExists = true
        };
    }

    /// <summary>
    ///     <para>
    ///         把用户配置映射为运行配置：endpoint 与凭据引用来自默认 profile，上下文窗口来自所选模型，
    ///         其余字段来自用户设置；缺省部分沿用内置默认值。
    ///     </para>
    ///     <para>
    ///         Maps the user config onto the runtime config: endpoint and credential references come from the
    ///         default profile, the context window from the selected model, other fields from user settings; absent
    ///         parts keep built-in defaults.
    ///     </para>
    /// </summary>
    private static TinyHarnessConfig FromUserConfig(UserConfig userConfig, string userConfigPath)
    {
        var profile = string.IsNullOrWhiteSpace(userConfig.DefaultProfile)
            ? null
            : userConfig.Profiles.FirstOrDefault(p => string.Equals(p.Name, userConfig.DefaultProfile,
                                                                    StringComparison.Ordinal));
        if (profile is null && !string.IsNullOrWhiteSpace(userConfig.DefaultProfile))
            throw new
                ConfigException($"User config '{userConfigPath}' declares default profile '{userConfig.DefaultProfile}', " +
                                "but no profile with that name exists. Run 'tinyharness provider list' and " +
                                "'tinyharness provider use <name>' to fix it.");

        var endpoint            = string.Empty;
        var envVar              = string.Empty;
        var credTarget          = string.Empty;
        var model               = string.Empty;
        var defaults            = new TinyHarnessConfig();
        var contextWindowTokens = defaults.ContextWindowTokens;
        var chatApi             = defaults.ChatApi;

        if (profile is not null)
        {
            endpoint   = profile.Endpoint;
            envVar     = profile.ApiKeyEnvironmentVariable;
            credTarget = profile.ApiKeyCredentialTarget;
            chatApi    = profile.ChatApi;

            UserProfileModel? selectedModel = null;
            if (!string.IsNullOrWhiteSpace(profile.DefaultModel))
            {
                selectedModel = profile.Models.FirstOrDefault(m => string.Equals(m.Id, profile.DefaultModel,
                                                                       StringComparison.Ordinal));
                if (selectedModel is null)
                    throw new
                        ConfigException($"Profile '{profile.Name}' in '{userConfigPath}' declares default model '{profile.DefaultModel}', " +
                                        "but no model with that id exists. Run 'tinyharness model add " +
                                        $"{profile.DefaultModel} --context-window <tokens>' or 'tinyharness model use <model-id>'.");
            }

            if (selectedModel is not null)
            {
                model               = selectedModel.Id;
                contextWindowTokens = selectedModel.ContextWindowTokens;
            }
        }

        var settings = userConfig.Settings;
        return new TinyHarnessConfig
        {
            Endpoint                  = endpoint,
            ApiKeyEnvironmentVariable = envVar,
            ApiKeyCredentialTarget    = credTarget,
            ChatApi                   = chatApi,
            Model                     = model,
            ContextWindowTokens       = contextWindowTokens,
            ReservedOutputTokens      = settings?.ReservedOutputTokens      ?? defaults.ReservedOutputTokens,
            CompactionThreshold       = settings?.CompactionThreshold       ?? defaults.CompactionThreshold,
            MaxAgentSteps             = settings?.MaxAgentSteps             ?? defaults.MaxAgentSteps,
            DefaultToolTimeoutSeconds = settings?.DefaultToolTimeoutSeconds ?? defaults.DefaultToolTimeoutSeconds,
            WorkspaceRoot             = string.Empty,
            SessionDirectory = string.IsNullOrWhiteSpace(settings?.SessionDirectory)
                ? defaults.SessionDirectory
                : settings.SessionDirectory,
            CommandRules = settings?.CommandRules ?? []
        };
    }
}

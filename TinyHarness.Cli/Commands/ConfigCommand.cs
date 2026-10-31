using TinyHarness.Cli.Models;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions.Configuration;

namespace TinyHarness.Cli.Commands;

/// <summary>
///     <para>`tinyharness config`：显示生效配置的值、来源与查找路径（密钥只显示可用性），以及更新用户配置中的非敏感默认值。</para>
///     <para>
///         `tinyharness config`: shows the effective values, their source, and lookup paths (API key availability
///         only, never the value), and updates non-sensitive defaults in the user config.
///     </para>
/// </summary>
internal static class ConfigCommand
{
    /// <summary>
    ///     <para>config set 支持的键。</para>
    ///     <para>Keys supported by config set.</para>
    /// </summary>
    internal static readonly string[] SettableKeys =
    [
        "maxAgentSteps", "reservedOutputTokens", "compactionThreshold", "defaultToolTimeoutSeconds",
        "sessionDirectory", "sessionStoreDirectory"
    ];

    /// <summary>
    ///     <para>执行 config 子命令。</para>
    ///     <para>Executes a config subcommand.</para>
    /// </summary>
    public static async Task<int> ExecuteAsync(
        CommandContext context, CliOptions options, CancellationToken cancellationToken) => options.Subcommand switch
    {
        "show" => await ShowAsync(context, cancellationToken).ConfigureAwait(false),
        "set" => await SetAsync(context, options.Primary!, options.Secondary!, cancellationToken)
           .ConfigureAwait(false),
        _ => throw new InvalidOperationException($"Unknown config subcommand '{options.Subcommand}'.")
    };


    /// <summary>
    ///     <para>显示生效值、来源、绝对路径与密钥可用性。</para>
    ///     <para>Shows effective values, sources, absolute paths, and API key availability.</para>
    /// </summary>
    private static async Task<int> ShowAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var io = context.Io;
        var resolution = await ConfigResolver
                              .ResolveAsync(null, cancellationToken,
                                            context.ResolveUserConfigPath()).ConfigureAwait(false);
        var config = resolution.Config;

        await io.WriteLineAsync("TinyHarness configuration", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"  source          : {DescribeSource(resolution)}", cancellationToken)
                .ConfigureAwait(false);
        if (resolution.ProfileName is not null)
            await io.WriteLineAsync($"  profile         : {resolution.ProfileName}", cancellationToken)
                    .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(config.Model))
            await io
                 .WriteLineAsync($"  model           : {config.Model} ({config.ContextWindowTokens:N0} tokens context window)",
                                 cancellationToken).ConfigureAwait(false);
        else
            await io.WriteLineAsync("  model           : (not set)", cancellationToken).ConfigureAwait(false);

        await io.WriteLineAsync(config.Endpoint.Length > 0
                                    ? $"  endpoint        : {config.Endpoint}"
                                    : "  endpoint        : (not set)",
                                cancellationToken).ConfigureAwait(false);

        await io.WriteLineAsync($"  chat api        : {ChatApiKindParser.ToValueString(config.ChatApi)}",
                                cancellationToken).ConfigureAwait(false);

        var key = ApiKeyReader.Describe(config.ApiKeyEnvironmentVariable, config.ApiKeyCredentialTarget,
                                        context.Credentials);
        await io.WriteLineAsync($"  api key         : {DescribeKey(key)}", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"  workspace       : {config.WorkspaceRoot}", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"  session dir     : {config.SessionDirectory}", cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync($"  budgets         : contextWindow={config.ContextWindowTokens:N0}" +
                                $" reservedOutput={config.ReservedOutputTokens:N0}"                  +
                                $" compactionThreshold={config.CompactionThreshold:N0}"              +
                                $" maxAgentSteps={config.MaxAgentSteps}"                             +
                                $" toolTimeout={config.DefaultToolTimeoutSeconds}s",
                                cancellationToken).ConfigureAwait(false);

        await io.WriteLineAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        await io
             .WriteLineAsync("Lookup paths (no implicit merging; a tinyharness.json in the working directory is never read automatically):",
                             cancellationToken).ConfigureAwait(false);
        await io
             .WriteLineAsync($"  user config     : {resolution.UserConfigPath}{(resolution.UserConfigExists ? " (exists)" : string.Empty)}",
                             cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync("Use --config <path> to run tasks against a specific file; see 'tinyharness help run'.",
                                cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    ///     <para>更新用户配置中的非敏感默认值；只写入用户配置，不改写其他配置文件。</para>
    ///     <para>
    ///         Updates a non-sensitive default in the user config; only the user config is written, never any other
    ///         config file.
    ///     </para>
    /// </summary>
    private static async Task<int> SetAsync(
        CommandContext context, string key, string value, CancellationToken cancellationToken)
    {
        var io = context.Io;
        if (!SettableKeys.Contains(key, StringComparer.Ordinal))
            throw new
                InvalidOperationException($"Unknown config key '{key}'. Settable keys: {string.Join(", ", SettableKeys)}. " +
                                          "Endpoint, profiles, models, and API keys are managed by init/provider/model/auth.");

        var (config, _) = await UserConfigAccess.LoadAsync(context, cancellationToken).ConfigureAwait(false);
        var    baseline = config;
        var    settings = config.Settings ?? new UserConfigSettings();
        string oldValue;
        switch (key)
        {
            case "maxAgentSteps" :
                settings = settings with { MaxAgentSteps = ParseNonNegativeInt(key, value) };
                oldValue = DescribeOptionalInt(config.Settings?.MaxAgentSteps);
                break;
            case "reservedOutputTokens" :
                settings = settings with { ReservedOutputTokens = ParseNonNegativeInt(key, value) };
                oldValue = DescribeOptionalInt(config.Settings?.ReservedOutputTokens);
                break;
            case "compactionThreshold" :
                settings = settings with { CompactionThreshold = ParseNonNegativeInt(key, value) };
                oldValue = DescribeOptionalInt(config.Settings?.CompactionThreshold);
                break;
            case "defaultToolTimeoutSeconds" :
                settings = settings with { DefaultToolTimeoutSeconds = ParsePositiveInt(key, value) };
                oldValue = DescribeOptionalInt(config.Settings?.DefaultToolTimeoutSeconds);
                break;
            case "sessionStoreDirectory" :
                if (string.IsNullOrWhiteSpace(value))
                    throw new InvalidOperationException("'sessionStoreDirectory' cannot be empty.");

                if (!Path.IsPathFullyQualified(value.Trim()))
                    throw new
                        InvalidOperationException("'sessionStoreDirectory' must be an absolute path; a relative location depends on the launch directory and is not accepted for resumable session storage.");

                settings = settings with { SessionStoreDirectory = value.Trim() };
                oldValue = config.Settings?.SessionStoreDirectory ?? "(built-in default)";
                break;
            default : // 默认分支：sessionDirectory
                if (string.IsNullOrWhiteSpace(value))
                    throw new InvalidOperationException("'sessionDirectory' cannot be empty.");

                settings = settings with { SessionDirectory = value };
                oldValue = config.Settings?.SessionDirectory ?? "(built-in default)";
                break;
        }

        config = config with { Settings = settings };
        await UserConfigStore.SaveAsync(context.ResolveUserConfigPath(), config, baseline, cancellationToken)
                             .ConfigureAwait(false);

        await io.WriteLineAsync($"{key}: {oldValue} -> {value}", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"Saved to user config: {context.ResolveUserConfigPath()}", cancellationToken)
                .ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    ///     <para>解析正整数配置值；非法输入给出包含合法范围的错误。</para>
    ///     <para>Parses a positive-integer config value; invalid input errors with the accepted range.</para>
    /// </summary>
    private static int ParsePositiveInt(string key, string value)
    {
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
            throw new
                InvalidOperationException($"'{value}' is not a valid value for '{key}'; expected a positive integer.");

        return parsed;
    }

    /// <summary>
    ///     <para>解析非负整数配置值；0 是合法值（例如 compactionThreshold=0 表示使用窗口大小）。</para>
    ///     <para>
    ///         Parses a non-negative-integer config value; 0 is legal (e.g. compactionThreshold=0 means "use the window
    ///         size").
    ///     </para>
    /// </summary>
    private static int ParseNonNegativeInt(string key, string value)
    {
        if (!int.TryParse(value, out var parsed) || parsed < 0)
            throw new
                InvalidOperationException($"'{value}' is not a valid value for '{key}'; expected a non-negative integer.");

        return parsed;
    }

    private static string DescribeOptionalInt(int? value) => value is { } v ? v.ToString() : "(built-in default)";

    /// <summary>
    ///     <para>描述配置来源；总是带上绝对路径。</para>
    ///     <para>Describes the config source; always includes the absolute path.</para>
    /// </summary>
    private static string DescribeSource(ConfigResolution resolution) => resolution.Source switch
    {
        ConfigSourceKind.ExplicitFile => $"explicit file {resolution.SourcePath}",
        ConfigSourceKind.UserConfig   => $"user config {resolution.SourcePath}",
        _                             => "built-in defaults (no config file found)"
    };

    /// <summary>
    ///     <para>描述密钥可用性；绝不包含密钥值。</para>
    ///     <para>Describes key availability; never includes the secret value.</para>
    /// </summary>
    private static string DescribeKey(ApiKeyStatus key) => key.Kind switch
    {
        ApiKeySourceKind.CredentialStore => key.Available
            ? $"available (credential store '{key.SourceName}')"
            : $"NOT available (credential store '{key.SourceName}' has no entry)",
        ApiKeySourceKind.EnvironmentVariable => key.Available
            ? $"available (environment variable '{key.SourceName}')"
            : $"NOT set (environment variable '{key.SourceName}')",
        _ => "not configured (see 'tinyharness help auth')"
    };
}

using TinyHarness.Cli.Models;
using TinyHarness.Cli.Services;

namespace TinyHarness.Cli.Commands;

/// <summary>
/// `tinyharness provider`：列出、添加和选择命名 provider profile。
///
/// `tinyharness provider`: list, add, and select named provider profiles.
/// </summary>
internal static class ProviderCommand
{
    /// <summary>
    /// 执行 provider 子命令。
    /// Executes a provider subcommand.
    /// </summary>
    public static async Task<int> ExecuteAsync(CommandContext    context, CliOptions options,
                                               CancellationToken cancellationToken)
    {
        switch (options.Subcommand)
        {
            case "list" :
                return await ListAsync(context, cancellationToken).ConfigureAwait(false);
            case "add" :
                return await AddAsync(context, options.Primary!, cancellationToken).ConfigureAwait(false);
            case "use" :
                return await UseAsync(context, options.Primary!, cancellationToken).ConfigureAwait(false);
            default :
                throw new InvalidOperationException($"Unknown provider subcommand '{options.Subcommand}'.");
        }
    }

    /// <summary>
    /// 列出全部 profile；用户配置不存在时给出 init 入口提示。
    /// Lists all profiles; a missing user config points at the init entry.
    /// </summary>
    private static async Task<int> ListAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var io = context.Io;
        var (config, exists) = await UserConfigAccess.LoadAsync(context, cancellationToken).ConfigureAwait(false);
        var userPath = context.ResolveUserConfigPath();
        await io.WriteLineAsync($"TinyHarness providers (user config: {userPath})", cancellationToken)
                .ConfigureAwait(false);
        if (!exists || config.Profiles.Count == 0)
        {
            await io.WriteLineAsync("  (no profiles yet - run 'tinyharness init' to create one)", cancellationToken)
                    .ConfigureAwait(false);
            return 0;
        }

        foreach (var profile in config.Profiles)
        {
            var marker   = string.Equals(profile.Name, config.DefaultProfile, StringComparison.Ordinal) ? "*" : " ";
            var endpoint = profile.Endpoint.Length > 0 ? profile.Endpoint : "(endpoint not set)";
            await io.WriteLineAsync($"{marker} {profile.Name,-20} {endpoint}", cancellationToken)
                    .ConfigureAwait(false);
            await io.WriteLineAsync($"  key          : {UserConfigAccess.DescribeKeySource(profile)}",
                                    cancellationToken).ConfigureAwait(false);
            await io.WriteLineAsync(
                                    $"  models       : {profile.Models.Count}{(profile.DefaultModel.Length > 0 ? $", default: {profile.DefaultModel}" : string.Empty)}",
                                    cancellationToken).ConfigureAwait(false);
        }

        await io.WriteLineAsync(config.DefaultProfile is { Length: > 0 }
                                    ? $"default profile: {config.DefaultProfile}"
                                    : "no default profile selected - run 'tinyharness provider use <name>'",
                                cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// 交互式添加或覆盖一个 profile；可选择设为默认。
    /// Interactively adds or overwrites one profile; optionally selects it as the default.
    /// </summary>
    private static async Task<int> AddAsync(CommandContext    context, string profileName,
                                            CancellationToken cancellationToken)
    {
        var io = context.Io;
        var (config, exists) = await UserConfigAccess.LoadAsync(context, cancellationToken).ConfigureAwait(false);
        await io
             .WriteLineAsync($"TinyHarness provider add '{profileName}' (user config: {context.ResolveUserConfigPath()})",
                             cancellationToken).ConfigureAwait(false);

        var existing = UserConfigAccess.FindProfile(config, profileName);
        if (existing is not null &&
            !await CliPrompt.ConfirmAsync(io, $"Profile '{profileName}' already exists. Overwrite it?",
                                          defaultYes : false,
                                          cancellationToken).ConfigureAwait(false))
        {
            await io.WriteLineAsync("Cancelled. Nothing was changed.", cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var profile = await ProfileEditor.CollectAsync(context, profileName, existing, cancellationToken)
                                         .ConfigureAwait(false);
        var makeDefault = await CliPrompt
                               .ConfirmAsync(io, $"Make '{profileName}' the default profile?",
                                             defaultYes : config.DefaultProfile is null, cancellationToken)
                               .ConfigureAwait(false);

        config = UserConfigAccess.UpsertProfile(config, profile);
        if (makeDefault)
        {
            config = config with { DefaultProfile = profileName };
        }

        await UserConfigAccess.SaveAsync(context, config, cancellationToken).ConfigureAwait(false);

        await io.WriteLineAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"Saved profile '{profileName}'.", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(profile.ApiKeyCredentialTarget) &&
            string.IsNullOrEmpty(profile.ApiKeyEnvironmentVariable))
        {
            await io.WriteLineAsync($"No API key source configured yet. Run 'tinyharness auth set {profileName}'.",
                                    cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>
    /// 选择默认 profile；不存在时列出可用名称。
    /// Selects the default profile; unknown names list the available ones.
    /// </summary>
    private static async Task<int> UseAsync(CommandContext    context, string profileName,
                                            CancellationToken cancellationToken)
    {
        var io = context.Io;
        var (config, exists) = await UserConfigAccess.LoadAsync(context, cancellationToken).ConfigureAwait(false);
        var profile = UserConfigAccess.FindProfile(config, profileName);
        if (profile is null)
        {
            var known = exists && config.Profiles.Count > 0
                ? string.Join(", ", config.Profiles.Select(p => p.Name))
                : "(none - run 'tinyharness init' or 'tinyharness provider add <name>')";
            throw new InvalidOperationException($"Profile '{profileName}' was not found. Known profiles: {known}.");
        }

        config = config with { DefaultProfile = profileName };
        await UserConfigAccess.SaveAsync(context, config, cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"Default profile is now '{profileName}' ({profile.Endpoint}).", cancellationToken)
                .ConfigureAwait(false);
        if (profile.DefaultModel.Length > 0)
        {
            await io.WriteLineAsync($"  default model: {profile.DefaultModel}", cancellationToken)
                    .ConfigureAwait(false);
        }
        else
        {
            await io
                 .WriteLineAsync("  no default model selected - run 'tinyharness model add <model-id> --context-window <tokens>'",
                                 cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }
}

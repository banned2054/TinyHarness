using TinyHarness.Cli.Models;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions.Configuration;

namespace TinyHarness.Cli.Commands;

/// <summary>
///     `tinyharness provider`：列出、添加和选择命名 provider profile。
///     `tinyharness provider`: list, add, and select named provider profiles.
/// </summary>
internal static class ProviderCommand
{
    /// <summary>
    ///     执行 provider 子命令。
    ///     Executes a provider subcommand.
    /// </summary>
    public static async Task<int> ExecuteAsync(
        CommandContext context, CliOptions options, CancellationToken cancellationToken) => options.Subcommand switch
    {
        "list" => await ListAsync(context, cancellationToken).ConfigureAwait(false),
        "add"  => await SaveProviderAsync(context, options.Primary!, false, cancellationToken).ConfigureAwait(false),
        "edit" => await SaveProviderAsync(context, options.Primary!, true, cancellationToken).ConfigureAwait(false),
        "use"  => await UseAsync(context, options.Primary!, cancellationToken).ConfigureAwait(false),
        _      => throw new InvalidOperationException($"Unknown provider subcommand '{options.Subcommand}'.")
    };

    /// <summary>
    ///     列出全部 profile；用户配置不存在时给出 init 入口提示。
    ///     Lists all profiles; a missing user config points at the init entry.
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
            await io.WriteLineAsync($"  chat api     : {ChatApiKindParser.ToValueString(profile.ChatApi)}",
                                    cancellationToken).ConfigureAwait(false);
            await io
                 .WriteLineAsync($"  models       : {profile.Models.Count}{(profile.DefaultModel.Length > 0 ? $", default: {profile.DefaultModel}" : string.Empty)}",
                                 cancellationToken).ConfigureAwait(false);
        }

        await io.WriteLineAsync(config.DefaultProfile is { Length: > 0 }
                                    ? $"default profile: {config.DefaultProfile}"
                                    : "no default profile selected - run 'tinyharness provider use <name>'",
                                cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    ///     <para>通过共享表单添加或编辑供应商，不隐式设置默认项。</para>
    ///     <para>Adds or edits a provider using shared forms without implicitly setting defaults.</para>
    /// </summary>
    private static async Task<int> SaveProviderAsync(CommandContext    context, string name, bool edit,
                                                     CancellationToken token)
    {
        try
        {
            await CatalogWizard.ProviderAsync(context, name, edit, token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            await context.Io.WriteLineAsync("Cancelled. Nothing was saved.", token);
        }

        return 0;
    }

    /// <summary>
    ///     选择默认 profile；不存在时列出可用名称。
    ///     Selects the default profile; unknown names list the available ones.
    /// </summary>
    private static async Task<int> UseAsync(
        CommandContext context, string profileName, CancellationToken cancellationToken)
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

        var baseline = config;
        config = config with { DefaultProfile = profileName };
        await ModelCatalogEditor.SaveAsync(context.ResolveUserConfigPath(), baseline, config, cancellationToken)
                                .ConfigureAwait(false);
        await io.WriteLineAsync($"Default profile is now '{profileName}' ({profile.Endpoint}).", cancellationToken)
                .ConfigureAwait(false);
        if (profile.DefaultModel.Length > 0)
            await io.WriteLineAsync($"  default model: {profile.DefaultModel}", cancellationToken)
                    .ConfigureAwait(false);
        else
            await io
                 .WriteLineAsync("  no default model selected - run 'tinyharness model add <model-id> --context-window <tokens>'",
                                 cancellationToken).ConfigureAwait(false);

        return 0;
    }
}

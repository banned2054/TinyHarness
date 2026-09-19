using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.Configuration;

namespace TinyHarness.Cli.Commands;

/// <summary>
/// `tinyharness init`：交互式创建用户配置（profile、API key 来源、默认模型与上下文窗口），
/// 让用户第一次使用时不必手工打开 JSON 文件。
///
/// `tinyharness init`: interactively creates the user config (profile, API key source, default model, and
/// context window) so first-time users never need to hand-edit a JSON file.
/// </summary>
internal static class InitCommand
{
    /// <summary>
    /// 运行 init 引导；用户取消覆盖确认时不做任何修改。
    /// Runs the init guide; declining the overwrite confirmation changes nothing.
    /// </summary>
    public static async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var io       = context.Io;
        var userPath = context.ResolveUserConfigPath();
        await io.WriteLineAsync("TinyHarness init", cancellationToken).ConfigureAwait(false);
        await io
             .WriteLineAsync($"  user config : {userPath}{(File.Exists(userPath) ? string.Empty : " (will be created)")}",
                             cancellationToken).ConfigureAwait(false);
        await io
             .WriteLineAsync("  lookup order: .\\tinyharness.json in the current directory -> user config -> built-in defaults",
                             cancellationToken).ConfigureAwait(false);

        var config = await UserConfigStore.LoadAsync(userPath, cancellationToken).ConfigureAwait(false);

        var profileName = await PromptProfileNameAsync(io, config, cancellationToken).ConfigureAwait(false);
        var existing    = UserConfigAccess.FindProfile(config, profileName);
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
        config = UserConfigAccess.UpsertProfile(config, profile) with { DefaultProfile = profileName };
        await UserConfigStore.SaveAsync(userPath, config, cancellationToken).ConfigureAwait(false);

        await io.WriteLineAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"Saved user config: {userPath}", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"Profile '{profileName}' is now the default.", cancellationToken)
                .ConfigureAwait(false);
        if (string.IsNullOrEmpty(profile.ApiKeyCredentialTarget) &&
            string.IsNullOrEmpty(profile.ApiKeyEnvironmentVariable))
        {
            await io.WriteLineAsync($"No API key source configured yet. Run 'tinyharness auth set {profileName}'.",
                                    cancellationToken).ConfigureAwait(false);
        }

        await io
             .WriteLineAsync("Next: 'tinyharness doctor' checks the setup offline; 'tinyharness model add <model-id> --context-window <tokens>' extends the model list.",
                             cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// 提示并校验 profile 名称；默认名称为 default，与已有名称冲突时只作为覆盖提醒。
    ///
    /// Prompts for and validates the profile name; the default is 'default', and a colliding existing name only serves as the overwrite notice.
    /// </summary>
    private static async Task<string> PromptProfileNameAsync(ICliConsole       io, UserConfig config,
                                                             CancellationToken cancellationToken)
    {
        while (true)
        {
            var name = await CliPrompt.LineWithDefaultAsync(io, "Provider profile name", "default", cancellationToken)
                                      .ConfigureAwait(false);
            if (CommandLine.IsValidProfileName(name))
            {
                return name;
            }

            await io
                 .WriteLineAsync("  Profile names use 1-64 characters: letters, digits, '.', '_', '-', not starting with '-' or '.'.",
                                 cancellationToken).ConfigureAwait(false);
        }
    }
}

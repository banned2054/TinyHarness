using TinyHarness.Cli.Models;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Configuration;

namespace TinyHarness.Cli.Commands;

/// <summary>
/// `tinyharness model`：管理某个 profile 的模型列表与默认模型。上下文窗口始终显式声明，
/// 不从模型名称推断。
///
/// `tinyharness model`: manages a profile's model list and default model. The context window is always
/// explicit and never inferred from the model name.
/// </summary>
internal static class ModelCommand
{
    /// <summary>
    /// 执行 model 子命令。
    /// Executes a model subcommand.
    /// </summary>
    public static async Task<int> ExecuteAsync(CommandContext    context, CliOptions options,
                                               CancellationToken cancellationToken)
    {
        switch (options.Subcommand)
        {
            case "list" :
                return await ListAsync(context, options.ProviderName, cancellationToken).ConfigureAwait(false);
            case "add" :
                return await AddAsync(context, options.Primary!, options.ContextWindowTokens!.Value,
                                      options.ProviderName, cancellationToken).ConfigureAwait(false);
            case "use" :
                return await UseAsync(context, options.Primary!, options.ProviderName, cancellationToken)
                   .ConfigureAwait(false);
            default :
                throw new InvalidOperationException($"Unknown model subcommand '{options.Subcommand}'.");
        }
    }

    /// <summary>
    /// 列出 profile 中配置的模型并标记默认模型。
    /// Lists the models configured in a profile and marks the default one.
    /// </summary>
    private static async Task<int> ListAsync(CommandContext    context, string? providerName,
                                             CancellationToken cancellationToken)
    {
        var io = context.Io;
        var (_, profile) =
            await UserConfigAccess.RequireProfileAsync(context, providerName, cancellationToken).ConfigureAwait(false);

        await io
             .WriteLineAsync($"TinyHarness models in profile '{profile.Name}' (user config: {context.ResolveUserConfigPath()})",
                             cancellationToken).ConfigureAwait(false);
        if (profile.Models.Count == 0)
        {
            await io
                 .WriteLineAsync("  (no models yet - run 'tinyharness model add <model-id> --context-window <tokens>')",
                                 cancellationToken).ConfigureAwait(false);
            return 0;
        }

        foreach (var model in profile.Models)
        {
            var marker = string.Equals(model.Id, profile.DefaultModel, StringComparison.Ordinal) ? "*" : " ";
            await io.WriteLineAsync($"{marker} {model.Id,-40} {model.ContextWindowTokens:N0} tokens",
                                    cancellationToken).ConfigureAwait(false);
        }

        await io.WriteLineAsync(
                                profile.DefaultModel.Length > 0
                                    ? $"default model: {profile.DefaultModel}"
                                    : "no default model selected - run 'tinyharness model use <model-id>'",
                                cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// 向 profile 添加模型；重复 id 报错；profile 没有默认模型时自动设为默认。
    ///
    /// Adds a model to a profile; duplicate ids fail; the model becomes the default when the profile has none.
    /// </summary>
    private static async Task<int> AddAsync(CommandContext context,      string modelId, int contextWindowTokens,
                                            string?        providerName, CancellationToken cancellationToken)
    {
        var io = context.Io;
        var (config, profile) =
            await UserConfigAccess.RequireProfileAsync(context, providerName, cancellationToken).ConfigureAwait(false);

        if (profile.Models.Any(m => string.Equals(m.Id, modelId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                                                $"Model '{modelId}' already exists in profile '{profile.Name}'. Run 'tinyharness model use {modelId}' to select it.");
        }

        var makeDefault = profile.DefaultModel.Length == 0;
        var updated = profile with
        {
            Models =
            [
                .. profile.Models, new UserProfileModel { Id = modelId, ContextWindowTokens = contextWindowTokens }
            ],
            DefaultModel = makeDefault ? modelId : profile.DefaultModel,
        };
        config = UserConfigAccess.UpsertProfile(config, updated);
        await UserConfigAccess.SaveAsync(context, config, cancellationToken).ConfigureAwait(false);

        await io.WriteLineAsync($"Added '{modelId}' ({contextWindowTokens:N0} tokens) to profile '{profile.Name}'.",
                                cancellationToken).ConfigureAwait(false);
        if (makeDefault)
        {
            await io.WriteLineAsync($"  it is now the default model of the profile.", cancellationToken)
                    .ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>
    /// 选择默认模型；id 必须已在 profile 中声明。
    /// Selects the default model; the id must already be declared in the profile.
    /// </summary>
    private static async Task<int> UseAsync(CommandContext    context, string modelId, string? providerName,
                                            CancellationToken cancellationToken)
    {
        var io = context.Io;
        var (config, profile) =
            await UserConfigAccess.RequireProfileAsync(context, providerName, cancellationToken).ConfigureAwait(false);

        var model = profile.Models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.Ordinal));
        if (model is null)
        {
            var known = profile.Models.Count > 0
                ? string.Join(", ", profile.Models.Select(m => m.Id))
                : "(none - run 'tinyharness model add <model-id> --context-window <tokens>')";
            throw new InvalidOperationException(
                                                $"Model '{modelId}' is not configured in profile '{profile.Name}'. Known models: {known}.");
        }

        var updated = profile with { DefaultModel = modelId };
        config = UserConfigAccess.UpsertProfile(config, updated);
        await UserConfigAccess.SaveAsync(context, config, cancellationToken).ConfigureAwait(false);

        await io
             .WriteLineAsync($"Default model for profile '{profile.Name}' is now '{modelId}' ({model.ContextWindowTokens:N0} tokens).",
                             cancellationToken).ConfigureAwait(false);
        return 0;
    }
}

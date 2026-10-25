using TinyHarness.Cli.Models;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Services.ChatCompletions.Configuration;

namespace TinyHarness.Cli.Commands;

/// <summary>
///     <para>显式更新凭据来源，密钥只在完整输入后写入新目标，原凭据不覆盖或删除。</para>
///     <para>Explicitly updates credential sources, writing complete key input to fresh targets without overwriting or deleting old credentials.</para>
/// </summary>
internal static class AuthCommand
{
    /// <summary>
    ///     <para>收集来源后经共享保存服务提交引用；冲突保留原配置。</para>
    ///     <para>Collects a source and commits references through shared saving, preserving the old config on conflicts.</para>
    /// </summary>
    public static async Task<int> ExecuteAsync(CommandContext    context, CliOptions options,
                                               CancellationToken cancellationToken)
    {
        var (baseline, profile) = await UserConfigAccess
                                       .RequireProfileAsync(context, options.Primary, cancellationToken)
                                       .ConfigureAwait(false);
        var variable = options.EnvironmentVariable;
        var useStore = options.UseCredentialStore;
        if (variable is null && !useStore)
        {
            var choice = await CliPrompt
                              .RequiredLineAsync(context.Io,
                                                 "API key source: 1) environment variable  2) system credential store",
                                                 cancellationToken).ConfigureAwait(false);
            if (choice is "1" or "")
                variable = await CliPrompt
                                .LineWithDefaultAsync(context.Io, "Environment variable name",
                                                      ProfileEditor.DefaultApiKeyVariable, cancellationToken)
                                .ConfigureAwait(false);
            else if (choice == "2") useStore = true;
            else throw new InvalidOperationException("Choose 1 or 2.");
        }

        string? secret = null;
        if (useStore)
        {
            if (!context.Credentials.IsSupported)
                throw new InvalidOperationException("System credential storage is unavailable; use --env.");
            secret = await ProfileEditor.ReadSecretAsync(context.Io, cancellationToken).ConfigureAwait(false);
        }
        else if (variable is null || !ProfileEditor.IsValidEnvironmentVariableName(variable))
            throw new InvalidOperationException("Invalid environment variable name.");
        var updated = profile with
        {
            ApiKeyEnvironmentVariable = variable ?? string.Empty, ApiKeyCredentialTarget = string.Empty
        };
        var candidate = ModelCatalogEditor.PutProvider(baseline, updated, true);
        await ModelCatalogEditor.SaveAsync(context.ResolveUserConfigPath(), baseline, candidate, cancellationToken,
                                           context.Credentials, secret, profile.Name).ConfigureAwait(false);
        await context.Io
                     .WriteLineAsync($"API key source for profile '{profile.Name}': " +
                                     (useStore ? "new system credential reference" : $"env '{variable}'"),
                                     cancellationToken).ConfigureAwait(false);
        await context.Io
                     .WriteLineAsync("The key value is not stored in the user config, logs, or sessions. Old credential entries were retained.",
                                     cancellationToken).ConfigureAwait(false);
        return 0;
    }
}

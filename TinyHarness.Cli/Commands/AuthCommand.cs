using TinyHarness.Cli.Models;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Configuration;

namespace TinyHarness.Cli.Commands;

/// <summary>
/// `tinyharness auth set`：为 profile 配置 API key 来源——环境变量引用或系统凭据存储。
/// 密钥值只进入所选来源，绝不写入 JSON、日志或输出。
///
/// `tinyharness auth set`: configures where a profile's API key comes from - an environment variable
/// reference or the system credential store. The secret value only enters the chosen source, never JSON,
/// logs, or output.
/// </summary>
internal static class AuthCommand
{
    /// <summary>
    /// 执行 auth set。
    /// Executes auth set.
    /// </summary>
    public static async Task<int> ExecuteAsync(CommandContext    context, CliOptions options,
                                               CancellationToken cancellationToken)
    {
        var io = context.Io;
        var (config, profile) =
            await UserConfigAccess.RequireProfileAsync(context, options.Primary, cancellationToken)
                                  .ConfigureAwait(false);
        var profileName = profile.Name;

        var (envVariable, credentialTarget) = options.EnvironmentVariable is not null
            ? await ApplyEnvironmentVariableAsync(context, profile, options.EnvironmentVariable, cancellationToken)
               .ConfigureAwait(false)
            : options.UseCredentialStore
                ? await ApplyCredentialStoreAsync(context, profile, cancellationToken).ConfigureAwait(false)
                : await PromptSourceAsync(context, profile, cancellationToken).ConfigureAwait(false);

        var updated = profile with
        {
            ApiKeyEnvironmentVariable = envVariable,
            ApiKeyCredentialTarget = credentialTarget,
        };
        config = UserConfigAccess.UpsertProfile(config, updated);
        await UserConfigAccess.SaveAsync(context, config, cancellationToken).ConfigureAwait(false);

        await io.WriteLineAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"API key source for profile '{profileName}': " +
                                UserConfigAccess.DescribeKeySource(updated), cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync("The key value is not stored in the user config, logs, or sessions.",
                                cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// 无标志时交互选择来源：环境变量或凭据存储（支持时）。
    /// With no flags, asks interactively for the source: environment variable or credential store (when supported).
    /// </summary>
    private static async Task<(string EnvVariable, string CredentialTarget)> PromptSourceAsync(
        CommandContext context, UserProfile profile, CancellationToken cancellationToken)
    {
        var io = context.Io;
        await io.WriteLineAsync($"Choose the API key source for profile '{profile.Name}':", cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync("  1) environment variable (the name is stored; the key stays outside the config)",
                                cancellationToken).ConfigureAwait(false);
        if (context.Credentials.IsSupported)
        {
            await io
                 .WriteLineAsync($"  2) Windows credential store (the key is saved under '{ProfileEditor.CredentialTargetPrefix}{profile.Name}')",
                                 cancellationToken).ConfigureAwait(false);
        }

        while (true)
        {
            var choice = await CliPrompt
                              .RequiredLineAsync(io, $"Choose [(1){(context.Credentials.IsSupported ? "/2" : "")}]",
                                                 cancellationToken).ConfigureAwait(false);
            if (choice.Length == 0 || choice == "1")
            {
                return await ApplyEnvironmentVariableAsync(context, profile, null, cancellationToken)
                   .ConfigureAwait(false);
            }

            if (context.Credentials.IsSupported && choice == "2")
            {
                return await ApplyCredentialStoreAsync(context, profile, cancellationToken).ConfigureAwait(false);
            }

            await io.WriteLineAsync("  Answer with the option number.", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 配置环境变量引用；清除旧凭据存储目标并删除遗留条目。
    ///
    /// Configures an environment-variable reference; clears the old credential-store target and deletes the leftover entry.
    /// </summary>
    private static async Task<(string EnvVariable, string CredentialTarget)> ApplyEnvironmentVariableAsync(
        CommandContext context, UserProfile profile, string? requestedVariable, CancellationToken cancellationToken)
    {
        var io = context.Io;

        string variable;
        while (true)
        {
            variable = requestedVariable ??
                       await CliPrompt.LineWithDefaultAsync(io, "Environment variable name",
                                                            ProfileEditor.DefaultApiKeyVariable, cancellationToken)
                                      .ConfigureAwait(false);
            requestedVariable = null;
            if (ProfileEditor.IsValidEnvironmentVariableName(variable))
            {
                break;
            }

            await io
                 .WriteLineAsync($"  '{variable}' is not a valid environment variable name (letters, digits, underscore).",
                                 cancellationToken).ConfigureAwait(false);
        }

        await RemoveStoredCredentialAsync(context, profile, cancellationToken).ConfigureAwait(false);
        return (variable, string.Empty);
    }

    /// <summary>
    /// 通过隐藏输入读取密钥并保存到系统凭据存储；JSON 只保存目标名称。
    ///
    /// Reads the secret through hidden input and saves it to the system credential store; JSON keeps only the target name.
    /// </summary>
    private static async Task<(string EnvVariable, string CredentialTarget)> ApplyCredentialStoreAsync(
        CommandContext context, UserProfile profile, CancellationToken cancellationToken)
    {
        if (!context.Credentials.IsSupported)
        {
            throw new InvalidOperationException("The system credential store is not supported on this platform. " +
                                                $"Use an environment variable instead: tinyharness auth set {profile.Name} --env <VARIABLE_NAME>.");
        }

        var io = context.Io;
        await io
             .WriteLineAsync($"The key is stored in the Windows Credential Manager as '{ProfileEditor.CredentialTargetPrefix}{profile.Name}'.",
                             cancellationToken).ConfigureAwait(false);
        var secret = await ProfileEditor.ReadSecretAsync(io, cancellationToken).ConfigureAwait(false);
        var target = ProfileEditor.CredentialTargetPrefix + profile.Name;
        context.Credentials.Save(target, secret);
        return (string.Empty, target);
    }

    /// <summary>
    /// 当 profile 原先指向凭据存储时，尽力删除遗留条目；失败不阻塞配置切换。
    ///
    /// Best-effort deletes a leftover credential entry when the profile previously pointed at the store; failures do not block the switch.
    /// </summary>
    private static async Task RemoveStoredCredentialAsync(CommandContext    context, UserProfile profile,
                                                          CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.ApiKeyCredentialTarget) || !context.Credentials.IsSupported)
        {
            return;
        }

        try
        {
            context.Credentials.Delete(profile.ApiKeyCredentialTarget);
            await context.Io
                         .WriteLineAsync($"  Removed the leftover credential entry '{profile.ApiKeyCredentialTarget}'.",
                                         cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            await context.Io
                         .WriteLineAsync($"  Could not remove the credential entry '{profile.ApiKeyCredentialTarget}': {ex.Message}",
                                         cancellationToken).ConfigureAwait(false);
        }
    }
}

using TinyHarness.Cli.Commands;
using TinyHarness.Core.Models.Configuration;

namespace TinyHarness.Cli.Services;

/// <summary>
/// 交互式收集一个 provider profile 的字段：endpoint、API key 来源、默认模型与上下文窗口。
/// init 与 provider add 共用同一套提示，保证两条入口产生一致的配置。
///
/// Interactively collects one provider profile: endpoint, API key source, default model, and context window.
/// init and provider add share the same prompts so both entries produce consistent configs.
/// </summary>
internal static class ProfileEditor
{
    /// <summary>
    /// 凭据存储中 TinyHarness API key 的目标名称前缀。
    /// Target-name prefix for TinyHarness API keys inside the credential store.
    /// </summary>
    public const string CredentialTargetPrefix = "TinyHarness:";

    /// <summary>
    /// 默认 API key 环境变量名。
    /// The default API key environment variable name.
    /// </summary>
    public const string DefaultApiKeyVariable = "TINYHARNESS_API_KEY";

    /// <summary>
    /// 依次提示并收集 profile 字段；选择凭据存储时立即保存密钥条目。
    ///
    /// Prompts for the profile fields in order; the credential entry is saved immediately when the store is chosen.
    /// </summary>
    public static async Task<UserProfile> CollectAsync(CommandContext context,  string            profileName,
                                                       UserProfile?   existing, CancellationToken cancellationToken)
    {
        var io = context.Io;
        await io.WriteLineAsync(string.Empty, cancellationToken).ConfigureAwait(false);

        var endpoint = await PromptEndpointAsync(io, existing?.Endpoint, cancellationToken).ConfigureAwait(false);

        var (envVariable, credentialTarget) =
            await PromptApiKeySourceAsync(context, profileName, existing, cancellationToken).ConfigureAwait(false);

        var modelId = await PromptModelIdAsync(io, existing?.DefaultModel, cancellationToken).ConfigureAwait(false);
        var contextWindow =
            await CliPrompt.PositiveIntAsync(io, $"Context window tokens for '{modelId}'", 128_000, cancellationToken)
                           .ConfigureAwait(false);

        return new UserProfile
        {
            Name                      = profileName,
            Endpoint                  = endpoint,
            ApiKeyEnvironmentVariable = envVariable,
            ApiKeyCredentialTarget    = credentialTarget,
            DefaultModel              = modelId,
            Models =
            [
                new UserProfileModel { Id = modelId, ContextWindowTokens = contextWindow },
            ],
        };
    }

    /// <summary>
    /// 提示 endpoint 并校验为绝对 http(s) URL；非法输入重复提示。
    /// Prompts for the endpoint and validates an absolute http(s) URL; re-prompts on invalid input.
    /// </summary>
    private static async Task<string> PromptEndpointAsync(ICliConsole       io, string? existing,
                                                          CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = existing is { Length: > 0 }
                ? await CliPrompt
                       .LineWithDefaultAsync(io,
                                             "Endpoint URL (OpenAI-compatible base, e.g. https://api.example.com/v1)",
                                             existing, cancellationToken).ConfigureAwait(false)
                : await CliPrompt
                       .RequiredLineAsync(io, "Endpoint URL (OpenAI-compatible base, e.g. https://api.example.com/v1)",
                                          cancellationToken).ConfigureAwait(false);

            if (IsValidEndpoint(line, out var normalized))
            {
                return normalized;
            }

            await io.WriteLineAsync("  The endpoint must be an absolute http(s) URL, typically ending in '/v1'.",
                                    cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 提示 API key 来源；三种方式：环境变量引用、系统凭据存储（支持时）、暂时跳过。
    ///
    /// Prompts for the API key source: an environment variable reference, the system credential store (when supported), or skip.
    /// </summary>
    private static async Task<(string EnvVariable, string CredentialTarget)> PromptApiKeySourceAsync(
        CommandContext context, string profileName, UserProfile? existing, CancellationToken cancellationToken)
    {
        var io = context.Io;
        await io.WriteLineAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync("API key source:", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync("  1) environment variable (the name is stored; the key stays outside the config)",
                                cancellationToken).ConfigureAwait(false);
        if (context.Credentials.IsSupported)
        {
            await io
                 .WriteLineAsync($"  2) Windows credential store (the key is saved under '{CredentialTargetPrefix}{profileName}'; JSON keeps the reference only)",
                                 cancellationToken).ConfigureAwait(false);
        }

        await io.WriteLineAsync("  3) skip (configure later with 'tinyharness auth set')", cancellationToken)
                .ConfigureAwait(false);

        while (true)
        {
            var hasStore = context.Credentials.IsSupported;
            var choice = await CliPrompt.RequiredLineAsync(io, $"Choose [(1){(hasStore ? "/2" : "")}/3]",
                                                           cancellationToken).ConfigureAwait(false);
            if (choice.Length == 0 || choice == "1")
            {
                var variable = await CliPrompt
                                    .LineWithDefaultAsync(io, "Environment variable name", DefaultApiKeyVariable,
                                                          cancellationToken).ConfigureAwait(false);
                if (IsValidEnvironmentVariableName(variable))
                {
                    return (variable, string.Empty);
                }

                await io.WriteLineAsync("  Use letters, digits and underscores, starting with a letter or underscore.",
                                        cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (hasStore && choice == "2")
            {
                var secret = await ReadSecretAsync(io, cancellationToken).ConfigureAwait(false);
                var target = CredentialTargetPrefix + profileName;
                context.Credentials.Save(target, secret);
                await io
                     .WriteLineAsync($"  Saved the API key to the credential store entry '{target}'. The value was not written to any file.",
                                     cancellationToken).ConfigureAwait(false);
                return (string.Empty, target);
            }

            if (choice == "3")
            {
                return (string.Empty, string.Empty);
            }

            await io.WriteLineAsync("  Answer with the option number.", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 读取默认模型 id 并校验；非法输入重复提示。
    /// Prompts for the default model id and validates it; re-prompts on invalid input.
    /// </summary>
    private static async Task<string> PromptModelIdAsync(ICliConsole       io, string? existing,
                                                         CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = existing is { Length: > 0 }
                ? await CliPrompt.LineWithDefaultAsync(io, "Default model id", existing, cancellationToken)
                                 .ConfigureAwait(false)
                : await CliPrompt
                       .RequiredLineAsync(io,
                                          "Default model id (must support streaming Chat Completions and tool calling)",
                                          cancellationToken).ConfigureAwait(false);

            if (CommandLine.IsValidModelId(line))
            {
                return line;
            }

            await io.WriteLineAsync("  A model id has no whitespace and 1-200 characters.", cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 通过隐藏输入读取密钥；空输入重复提示。
    /// Reads a secret through hidden input; re-prompts on empty input.
    /// </summary>
    internal static async Task<string> ReadSecretAsync(ICliConsole io, CancellationToken cancellationToken)
    {
        while (true)
        {
            await io.WriteAsync("API key (input hidden): ", cancellationToken).ConfigureAwait(false);
            var secret = await io.ReadHiddenLineAsync(cancellationToken).ConfigureAwait(false);
            if (secret is null)
            {
                throw new
                    InvalidOperationException("Input ended before the API key was entered. Nothing was saved; rerun the command.");
            }

            if (secret.Trim().Length > 0)
            {
                return secret.Trim();
            }

            await io.WriteLineAsync("  The API key cannot be empty.", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 校验 endpoint 为绝对 http(s) URL，并去掉结尾多余斜杠。
    /// Validates the endpoint as an absolute http(s) URL and trims trailing slashes.
    /// </summary>
    public static bool IsValidEndpoint(string value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        normalized = value.Trim().TrimEnd('/');
        return normalized.Length > 0;
    }

    /// <summary>
    /// 校验环境变量名：字母或下划线开头，后接字母、数字或下划线。
    /// Validates an environment variable name: starts with a letter or underscore, then letters, digits, or underscores.
    /// </summary>
    public static bool IsValidEnvironmentVariableName(string value) =>
        value.Length > 0 && (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}

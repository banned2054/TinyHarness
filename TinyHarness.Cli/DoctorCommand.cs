using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Configuration;

namespace TinyHarness.Cli;

/// <summary>
/// `tinyharness doctor`：默认离线检查配置字段、路径与凭据可用性；只有显式 `--connect`
/// 才发送一次最小模型请求，并在联网前明确提示可能产生费用。
///
/// `tinyharness doctor`: checks config fields, paths, and credential availability offline by default; only an
/// explicit `--connect` sends one minimal model request, with a clear notice beforehand that it may incur charges.
/// </summary>
internal static class DoctorCommand
{
    /// <summary>
    /// 执行 doctor 检查；存在失败项时返回 1，警告不影响退出码。
    ///
    /// Runs the doctor checks; returns 1 when any check fails, warnings do not affect the exit code.
    /// </summary>
    public static async Task<int> ExecuteAsync(CommandContext    context, CliOptions options,
                                               CancellationToken cancellationToken)
    {
        var io = context.Io;
        var resolution = await ConfigResolver.ResolveAsync(null, cancellationToken,
                                                           workingDirectory : context.ResolveWorkingDirectory(),
                                                           userConfigPath : context.ResolveUserConfigPath())
                                             .ConfigureAwait(false);
        var config   = resolution.Config;
        var failures = 0;

        await io.WriteLineAsync("TinyHarness doctor (offline checks)", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"  config source : {resolution.Source}" +
                                (resolution.SourcePath is null ? string.Empty : $" ({resolution.SourcePath})"),
                                cancellationToken).ConfigureAwait(false);

        if (resolution.Source is ConfigSourceKind.Defaults)
        {
            await io.WriteLineAsync("  [warn] no config file found; run 'tinyharness init' to create one",
                                    cancellationToken)
                    .ConfigureAwait(false);
        }

        // Endpoint: must be set and a syntactically valid absolute http(s) URL for live runs.
        var endpointValid = ProfileEditor.IsValidEndpoint(config.Endpoint, out var normalizedEndpoint);
        if (config.Endpoint.Length == 0)
        {
            await io.WriteLineAsync("  [fail] endpoint is not configured", cancellationToken).ConfigureAwait(false);
            failures++;
        }
        else if (!endpointValid)
        {
            await io.WriteLineAsync($"  [fail] endpoint '{config.Endpoint}' is not an absolute http(s) URL",
                                    cancellationToken)
                    .ConfigureAwait(false);
            failures++;
        }
        else
        {
            await io.WriteLineAsync($"  [ok]   endpoint {normalizedEndpoint}", cancellationToken).ConfigureAwait(false);
        }

        // Model: required for live runs and the offline smoke path.
        if (config.Model.Length == 0)
        {
            await io.WriteLineAsync("  [fail] model is not configured", cancellationToken).ConfigureAwait(false);
            failures++;
        }
        else
        {
            await io
                 .WriteLineAsync($"  [ok]   model {config.Model} ({config.ContextWindowTokens:N0} tokens context window)",
                                 cancellationToken).ConfigureAwait(false);
        }

        // Budget sanity.
        if (config.ReservedOutputTokens >= config.ContextWindowTokens)
        {
            await io.WriteLineAsync($"  [warn] reservedOutputTokens ({config.ReservedOutputTokens:N0}) leaves no room" +
                                    " inside the context window; live runs will stop before sending",
                                    cancellationToken).ConfigureAwait(false);
        }

        // Workspace.
        if (!Directory.Exists(config.WorkspaceRoot))
        {
            await io.WriteLineAsync($"  [fail] workspace directory does not exist: {config.WorkspaceRoot}",
                                    cancellationToken)
                    .ConfigureAwait(false);
            failures++;
        }
        else
        {
            await io.WriteLineAsync($"  [ok]   workspace {config.WorkspaceRoot}", cancellationToken)
                    .ConfigureAwait(false);
        }

        // Session directory: must be creatable/writable; creation is the same step the run recorder performs.
        try
        {
            Directory.CreateDirectory(config.SessionDirectory);
            await io.WriteLineAsync($"  [ok]   session dir {config.SessionDirectory}", cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await io
                 .WriteLineAsync($"  [fail] session directory cannot be created: {config.SessionDirectory} ({ex.Message})",
                                 cancellationToken).ConfigureAwait(false);
            failures++;
        }

        // API key availability (offline): presence only, never the value.
        var key = ApiKeyReader.Describe(config.ApiKeyEnvironmentVariable, config.ApiKeyCredentialTarget,
                                        context.Credentials);
        switch (key.Kind)
        {
            case ApiKeySourceKind.None :
                await io
                     .WriteLineAsync("  [warn] no API key source configured; live runs need one ('tinyharness help auth')",
                                     cancellationToken).ConfigureAwait(false);
                break;
            case ApiKeySourceKind.EnvironmentVariable :
                await io.WriteLineAsync(key.Available
                                            ? $"  [ok]   api key available via environment variable '{key.SourceName}'"
                                            : $"  [warn] environment variable '{key.SourceName}' is not set; live runs need it",
                                        cancellationToken).ConfigureAwait(false);
                break;
            default :
                await io.WriteLineAsync(key.Available
                                            ? $"  [ok]   api key available via credential store '{key.SourceName}'"
                                            : $"  [warn] credential store entry '{key.SourceName}' is empty; run 'tinyharness auth set'",
                                        cancellationToken).ConfigureAwait(false);
                break;
        }

        if (options.Connect)
        {
            var connectOk = await ConnectAsync(context, config, endpointValid, key, cancellationToken)
               .ConfigureAwait(false);
            if (!connectOk)
            {
                failures++;
            }
        }
        else
        {
            await io.WriteLineAsync("  (offline mode; pass --connect to also send one minimal model request)",
                                    cancellationToken)
                    .ConfigureAwait(false);
        }

        await io.WriteLineAsync(failures == 0 ? "All critical checks passed." : $"{failures} check(s) failed.",
                                cancellationToken).ConfigureAwait(false);
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 发送一次最小模型请求；联网与计费提示先行，工具定义不随请求发送。
    ///
    /// Sends one minimal model request; the network/charge notice comes first and no tool definitions are sent.
    /// </summary>
    private static async Task<bool> ConnectAsync(CommandContext context, TinyHarnessConfig config, bool endpointValid,
                                                 ApiKeyStatus   key,     CancellationToken cancellationToken)
    {
        var io = context.Io;
        await io.WriteLineAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        if (!endpointValid || config.Model.Length == 0 || !key.Available)
        {
            await io.WriteLineAsync("  [skip] --connect needs a valid endpoint, a model, and an available API key",
                                    cancellationToken).ConfigureAwait(false);
            return false;
        }

        await io
             .WriteLineAsync($"  --connect will send one real model request to {config.Endpoint} using model '{config.Model}'.",
                             cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync("  This uses the network and may incur charges. No tools are sent with the request.",
                                cancellationToken).ConfigureAwait(false);
        if (!await CliPrompt.ConfirmAsync(io, "Continue?", defaultYes : false, cancellationToken).ConfigureAwait(false))
        {
            await io.WriteLineAsync("  Skipped by user choice.", cancellationToken).ConfigureAwait(false);
            return true;
        }

        string? apiKey;
        try
        {
            apiKey = ApiKeyReader.Read(config.ApiKeyEnvironmentVariable, config.ApiKeyCredentialTarget,
                                       context.Credentials);
        }
        catch (ConfigException ex)
        {
            await io.WriteLineAsync($"  [fail] cannot read the API key: {ex.Message}", cancellationToken)
                    .ConfigureAwait(false);
            return false;
        }

        if (apiKey is null)
        {
            await io.WriteLineAsync("  [fail] no API key is available", cancellationToken).ConfigureAwait(false);
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            IChatCompletionClient client = new OpenAiChatCompletionClient(config.Model, config.Endpoint, apiKey);
            var request = new ChatCompletionRequest
            {
                Model    = config.Model,
                Messages = [ChatMessage.User("Reply with exactly: ok")],
            };

            await io.WriteAsync("  waiting for reply", cancellationToken).ConfigureAwait(false);
            await foreach (var streamEvent in client.CompleteAsync(request, timeout.Token).ConfigureAwait(false))
            {
                if (streamEvent.Kind == ChatStreamEventKind.End)
                {
                    break;
                }

                if (streamEvent.Kind == ChatStreamEventKind.ContentDelta && streamEvent.ContentDelta is { Length: > 0 })
                {
                    await io.WriteAsync(".", cancellationToken).ConfigureAwait(false);
                }
            }

            await io.WriteLineAsync(string.Empty, cancellationToken).ConfigureAwait(false);
            await io.WriteLineAsync("  [ok]   the endpoint accepted a streaming Chat Completions request",
                                    cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException ||
                                   timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await io.WriteLineAsync(string.Empty, cancellationToken).ConfigureAwait(false);
            await io.WriteLineAsync($"  [fail] model request failed: {ex.Message}", cancellationToken)
                    .ConfigureAwait(false);
            return false;
        }
    }
}

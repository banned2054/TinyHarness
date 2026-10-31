using TinyHarness.Cli.Models;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;

namespace TinyHarness.Cli.Commands;

/// <summary>
///     <para>`tinyharness doctor`：默认离线检查配置字段、路径与凭据可用性；只有显式 `--connect` 才发送一次最小模型请求，并在联网前明确提示可能产生费用。</para>
///     <para>
///         `tinyharness doctor`: checks config fields, paths, and credential availability offline by default; only an
///         explicit `--connect` sends one minimal model request, with a clear notice beforehand that it may incur charges.
///     </para>
/// </summary>
internal static class DoctorCommand
{
    /// <summary>
    ///     <para>执行 doctor 检查；存在失败项时返回 1，警告不影响退出码。</para>
    ///     <para>Runs the doctor checks; returns 1 when any check fails, warnings do not affect the exit code.</para>
    /// </summary>
    public static async Task<int> ExecuteAsync(
        CommandContext context, CliOptions options, CancellationToken cancellationToken)
    {
        var io = context.Io;
        var resolution = await ConfigResolver.ResolveAsync(null, cancellationToken,
                                                           context.ResolveUserConfigPath())
                                             .ConfigureAwait(false);
        var config   = resolution.Config;
        var failures = 0;

        await io.WriteLineAsync("TinyHarness doctor (offline checks)", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"  config source : {resolution.Source}" +
                                (resolution.SourcePath is null ? string.Empty : $" ({resolution.SourcePath})"),
                                cancellationToken).ConfigureAwait(false);

        if (resolution.Source is ConfigSourceKind.Defaults)
            await io.WriteLineAsync("  [warn] no config file found; run 'tinyharness init' to create one",
                                    cancellationToken)
                    .ConfigureAwait(false);

        // Endpoint：live 运行必须已设置，且是语法有效的绝对 http(s) URL。
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

        // Chat API 协议：来自显式配置选择，绝不从模型名猜测。
        await io.WriteLineAsync($"  [ok]   chat api {ChatApiKindParser.ToValueString(config.ChatApi)}",
                                cancellationToken).ConfigureAwait(false);

        // Model：live 运行与离线 smoke 路径都必需。
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

        // 预算合理性：保留输出预算不得吃满整个上下文窗口。
        if (config.ReservedOutputTokens >= config.ContextWindowTokens)
            await io.WriteLineAsync($"  [warn] reservedOutputTokens ({config.ReservedOutputTokens:N0}) leaves no room" +
                                    " inside the context window; live runs will stop before sending",
                                    cancellationToken).ConfigureAwait(false);

        // 工作区：目录必须存在。
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

        // Session directory：只报告存在性；doctor 是诊断命令，不创建、不写入任何目录。
        // 会话持久化实际使用工作区外的受保护会话存储，该目录不再是运行必需。
        if (Directory.Exists(config.SessionDirectory))
            await io.WriteLineAsync($"  [ok]   session dir {config.SessionDirectory}", cancellationToken)
                    .ConfigureAwait(false);
        else
            await io
                 .WriteLineAsync($"  [info] session dir {config.SessionDirectory} is not present; doctor creates nothing",
                                 cancellationToken).ConfigureAwait(false);

        // API key 可用性（离线）：只检查是否存在，绝不输出密钥值。
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
            if (!connectOk) failures++;
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
    ///     <para>发送一次最小模型请求；联网与计费提示先行，工具定义不随请求发送。</para>
    ///     <para>Sends one minimal model request; the network/charge notice comes first and no tool definitions are sent.</para>
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
        if (!await CliPrompt.ConfirmAsync(io, "Continue?", false, cancellationToken).ConfigureAwait(false))
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
            var client = ModelClientFactory.Create(config.Model, config.Endpoint, apiKey,
                                                   config.ChatApi);
            var request = new ChatCompletionRequest
            {
                Model    = config.Model,
                Messages = [ChatMessage.User("Reply with exactly: ok")]
            };

            await io.WriteAsync("  waiting for reply", cancellationToken).ConfigureAwait(false);
            await foreach (var streamEvent in client.CompleteAsync(request, timeout.Token).ConfigureAwait(false))
            {
                if (streamEvent.Kind == ChatStreamEventKind.End) break;

                if (streamEvent is { Kind: ChatStreamEventKind.ContentDelta, ContentDelta.Length: > 0 })
                    await io.WriteAsync(".", cancellationToken).ConfigureAwait(false);
            }

            await io.WriteLineAsync(string.Empty, cancellationToken).ConfigureAwait(false);
            await io.WriteLineAsync(
                                    $"  [ok]   the endpoint accepted a streaming " +
                                    $"{ChatApiKindParser.ToValueString(config.ChatApi)} request",
                                    cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException ||
                                   (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested))
        {
            await io.WriteLineAsync(string.Empty, cancellationToken).ConfigureAwait(false);
            await io.WriteLineAsync($"  [fail] model request failed: {ex.Message}", cancellationToken)
                    .ConfigureAwait(false);
            return false;
        }
    }
}

using TinyHarness.Cli.Commands;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions.Configuration;

namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>管理命令和会话菜单共用的目录表单；每次完成后保存，取消不写入。</para>
///     <para>Catalog forms shared by management commands and session menus; saves only completed forms and never writes on cancellation.</para>
/// </summary>
internal static class CatalogWizard
{
    private static async Task<string> FieldAsync(ICliConsole io, string label, string initial, CancellationToken token)
    {
        var text = await CliPrompt.LineWithDefaultAsync(io, label + " (/cancel cancels, - clears)", initial, token);
        if (text == "/cancel") throw new OperationCanceledException("Catalog editing cancelled.");
        return text == "-" ? string.Empty : text;
    }

    private static async Task<bool> ConfirmAsync(ICliConsole       io, string label, bool defaultValue,
                                                 CancellationToken token)
    {
        var value = await FieldAsync(io, label + " (y/n)", defaultValue ? "y" : "n", token);
        return value.ToLowerInvariant() switch
        {
            "y" or "yes" => true, "n" or "no" => false,
            _            => throw new InvalidOperationException("Answer y or n; nothing was saved.")
        };
    }

    /// <summary>
    ///     <para>编辑供应商字段并保留全部模型；密钥仅在完整表单提交后写入新目标。</para>
    ///     <para>Edits provider fields while retaining all models; keys are written to fresh targets only after submitting a complete form.</para>
    /// </summary>
    public static async Task ProviderAsync(CommandContext context, string name, bool edit, CancellationToken token,
                                           bool           allowSecret = true)
    {
        var baseline = await UserConfigStore.LoadAsync(context.ResolveUserConfigPath(), token);
        var existing = baseline.Profiles.SingleOrDefault(item => item.Name == name);
        if (edit != (existing is not null)) throw new InvalidOperationException(edit
                                                    ? "Provider not found."
                                                    : "Provider already exists; use provider edit.");
        var io       = context.Io;
        var endpoint = await FieldAsync(io, "Endpoint base URL", existing?.Endpoint ?? string.Empty, token);
        var protocol = await FieldAsync(io, "Protocol (chat-completions/responses)",
                                        ChatApiKindParser.ToValueString(existing?.ChatApi ??
                                                                        ChatApiKind.ChatCompletions), token);
        if (!ChatApiKindParser.TryParse(protocol, out var chatApi)) throw new InvalidDataException("Unknown protocol.");
        await io.WriteLineAsync("Request route: " + endpoint.TrimEnd('/') +
                                (chatApi == ChatApiKind.Responses ? "/responses" : "/chat/completions"), token);
        var env    = existing?.ApiKeyEnvironmentVariable ?? string.Empty;
        var target = existing?.ApiKeyCredentialTarget    ?? string.Empty;
        var source = await FieldAsync(io, allowSecret
                                          ? "Credentials: keep / env / reference / none / new-key"
                                          : "Credentials: keep / env / reference / none (use auth set for hidden key input)",
                                      "keep", token);
        string? secret = null;
        switch (source)
        {
            case "keep" : break;
            case "env" :
                env    = await FieldAsync(io, "Environment variable name", env, token);
                target = string.Empty;
                break;
            case "reference" :
                target = await FieldAsync(io, "Existing system credential target (reference only)", target, token);
                env    = string.Empty;
                break;
            case "none" : env = target = string.Empty; break;
            case "new-key" when allowSecret :
                secret = await ProfileEditor.ReadSecretAsync(io, token);
                if (secret == "/cancel") throw new OperationCanceledException("Catalog editing cancelled.");
                break;
            default : throw new InvalidOperationException("Unknown credential source.");
        }

        var provider = new UserProfile
        {
            Name                      = name, Endpoint              = endpoint.TrimEnd('/'), ChatApi = chatApi,
            ApiKeyEnvironmentVariable = env, ApiKeyCredentialTarget = target
        };
        var candidate = ModelCatalogEditor.PutProvider(baseline, provider, edit);
        if (!await ConfirmAsync(io, "Save provider?", false, token)) return;
        await ModelCatalogEditor.SaveAsync(context.ResolveUserConfigPath(), baseline, candidate, token,
                                           context.Credentials, secret, name);
        await io.WriteLineAsync("Provider saved; open sessions keep their confirmed selections.", token);
    }

    /// <summary>
    ///     <para>添加或编辑固定 ID 的模型，显式收集窗口、能力和默认强度。</para>
    ///     <para>Adds or edits a fixed-ID model, explicitly collecting its window, capabilities and default effort.</para>
    /// </summary>
    public static async Task ModelAsync(CommandContext    context, string providerName, string id, bool edit,
                                        CancellationToken token)
    {
        var baseline = await UserConfigStore.LoadAsync(context.ResolveUserConfigPath(), token);
        var provider = baseline.Profiles.SingleOrDefault(item => item.Name == providerName) ??
                       throw new InvalidOperationException("Provider not found.");
        var existing = provider.Models.SingleOrDefault(item => item.Id == id);
        if (edit != (existing is not null)) throw new InvalidOperationException(edit ? "Model not found." : "Model already exists; use model edit.");
        var io      = context.Io;
        var display = await FieldAsync(io, "Display name", existing?.DisplayName ?? string.Empty, token);
        var windowText = await FieldAsync(io, "Context window tokens (required)",
                                          existing?.ContextWindowTokens.ToString() ?? string.Empty, token);
        if (!int.TryParse(windowText, out var window))
            throw new InvalidDataException("A positive context window is required.");
        var efforts = await FieldAsync(io, "Supported efforts (comma separated, empty = undeclared)",
                                       string.Join(',',
                                                   (existing?.ReasoningEfforts ?? []).Select(ModelReasoningEffortParser
                                                      .ToValueString)), token);
        var effort = await FieldAsync(io, "Default effort",
                                      existing?.DefaultReasoningEffort is { } level
                                          ? ModelReasoningEffortParser.ToValueString(level)
                                          : "default", token);
        var model       = ModelCatalogEditor.Model(id, display, window, efforts, effort);
        var makeDefault = await ConfirmAsync(io, "Set as this provider's default model?", false, token);
        var candidate   = ModelCatalogEditor.PutModel(baseline, providerName, model, edit, makeDefault);
        if (!await ConfirmAsync(io, "Save model?", false, token)) return;
        await ModelCatalogEditor.SaveAsync(context.ResolveUserConfigPath(), baseline, candidate, token);
        await io.WriteLineAsync("Model saved; select it explicitly to apply it to this session.", token);
    }

    /// <summary>
    ///     <para>通过 chat 唯一读取器管理目录；不收集明文密钥，不自动应用模型。</para>
    ///     <para>Manages the catalog through chat's single reader; never collects plaintext keys or automatically applies models.</para>
    /// </summary>
    public static async Task MenuAsync(CommandContext context, CancellationToken token)
    {
        try
        {
            var catalog = await UserConfigStore.LoadAsync(context.ResolveUserConfigPath(), token);
            await context.Io.WriteLineAsync(catalog.Profiles.Count == 0
                                                ? "No providers yet; create a provider first."
                                                : "Providers: " +
                                                  string.Join(", ", catalog.Profiles.Select(item => item.Name)), token);
            var operation = await FieldAsync(context.Io, "add-provider / edit-provider / add-model / edit-model",
                                             catalog.Profiles.Count == 0 ? "add-provider" : "add-model", token);
            var provider = await FieldAsync(context.Io, "Provider name", string.Empty, token);
            if (operation is "add-provider" or "edit-provider")
                await ProviderAsync(context, provider, operation == "edit-provider", token, false);
            else if (operation is "add-model" or "edit-model")
            {
                var id = await FieldAsync(context.Io, "Model API ID", string.Empty, token);
                await ModelAsync(context, provider, id, operation == "edit-model", token);
            }
            else throw new InvalidOperationException("Unknown catalog action.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            await context.Io.WriteLineAsync("Cancelled. Nothing was saved.", token);
        }
    }
}

/// <summary>
///     <para>目录菜单使用 chat 已有的读取器，禁止另开隐藏输入通道。</para>
///     <para>Catalog menus use chat's existing reader and disallow a second hidden-input channel.</para>
/// </summary>
internal sealed class ChatCatalogConsole(TextReader input, TextWriter output) : ICliConsole
{
    public bool IsInteractive => true;

    public Task WriteAsync(string text, CancellationToken cancellationToken) =>
        output.WriteAsync(text.AsMemory(), cancellationToken);

    public Task WriteLineAsync(string text, CancellationToken cancellationToken) =>
        output.WriteLineAsync(text.AsMemory(), cancellationToken);

    public Task WriteErrorLineAsync(string text, CancellationToken cancellationToken) =>
        WriteLineAsync(text, cancellationToken);

    public Task<string?> ReadLineAsync(CancellationToken cancellationToken) =>
        input.ReadLineAsync(cancellationToken).AsTask();

    public Task<string?> ReadHiddenLineAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Use tinyharness auth set for hidden key input.");
}

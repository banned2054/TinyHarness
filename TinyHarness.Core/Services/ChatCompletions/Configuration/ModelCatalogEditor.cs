using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Core.Services.ChatCompletions.Configuration;

/// <summary>
///     <para>目录表单共用的候选构造与保存；不改变已打开会话。</para>
///     <para>Shared catalog candidate construction and saving; never changes open sessions.</para>
/// </summary>
public static class ModelCatalogEditor
{
    /// <summary>
    ///     <para>添加或明确编辑供应商，保留其模型和默认项；重复添加与缺失编辑均拒绝。</para>
    ///     <para>Adds or explicitly edits a provider, preserving its models and defaults; rejects duplicate adds and missing edits.</para>
    /// </summary>
    public static UserConfig PutProvider(UserConfig baseline, UserProfile provider, bool edit)
    {
        var existing = baseline.Profiles.SingleOrDefault(item => item.Name == provider.Name);
        if (edit != (existing is not null))
            throw new InvalidOperationException(edit
                                                    ? "Provider no longer exists; reload the catalog."
                                                    : "Provider already exists; use edit.");
        UserConfigValidator.ValidateProfileEntry(provider.Name, provider.Endpoint, provider.ChatApi, "provider");
        if (provider.Name.Length > 64 || provider.Name[0] is '-' or '.' ||
            provider.Name.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '.' and not '_' and not '-'))
            throw new
                InvalidDataException("Provider names use 1-64 letters, digits, '.', '_' or '-', without a leading '-' or '.'.");
        if (provider.ApiKeyEnvironmentVariable.Length > 0 &&
            (!char.IsAsciiLetter(provider.ApiKeyEnvironmentVariable[0]) &&
              provider.ApiKeyEnvironmentVariable[0] != '_' ||
             provider.ApiKeyEnvironmentVariable.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_')))
            throw new InvalidDataException("Invalid environment variable name.");
        var candidate = existing is null
            ? provider
            : provider with { Models = existing.Models, DefaultModel = existing.DefaultModel };
        return baseline with
        {
            Profiles = existing is null
                ? [.. baseline.Profiles, candidate]
                : baseline.Profiles.Select(item => item.Name == provider.Name ? candidate : item).ToArray()
        };
    }

    /// <summary>
    ///     <para>添加或编辑一个模型；ID 是固定键，默认项仅按明确选择更改。</para>
    ///     <para>Adds or edits one model with an immutable ID key, changing the default only when explicitly selected.</para>
    /// </summary>
    public static UserConfig PutModel(UserConfig baseline, string providerName, UserProfileModel model, bool edit,
                                      bool       makeDefault = false)
    {
        var provider = baseline.Profiles.SingleOrDefault(item => item.Name == providerName)
                    ?? throw new InvalidOperationException("Provider no longer exists; reload the catalog.");
        var exists = provider.Models.Any(item => item.Id == model.Id);
        if (edit != exists)
            throw new InvalidOperationException(edit
                                                    ? "Model no longer exists; reload the catalog."
                                                    : "Model already exists; use edit.");
        if (model.Id.Length is < 1 or > 200 || model.Id.Any(char.IsWhiteSpace))
            throw new InvalidDataException("Model ID must contain 1-200 characters without whitespace.");
        UserConfigValidator.ValidateModelEntry(model.Id, model.ContextWindowTokens, model.ReasoningEfforts,
                                               model.DefaultReasoningEffort, "model");
        var updated = provider with
        {
            Models = edit
                ? provider.Models.Select(item => item.Id == model.Id ? model : item).ToArray()
                : [.. provider.Models, model],
            DefaultModel = makeDefault ? model.Id : provider.DefaultModel
        };
        return baseline with
        {
            Profiles = baseline.Profiles.Select(item => item.Name == providerName ? updated : item).ToArray()
        };
    }

    /// <summary>
    ///     <para>解析表单能力列表；空值不声明能力，default 表示不传强度。</para>
    ///     <para>Parses form capabilities; empty declares no capability and default omits effort.</para>
    /// </summary>
    public static UserProfileModel Model(string id, string? displayName, int window, string efforts,
                                         string defaultEffort)
    {
        ModelReasoningEffort Parse(string value) => ModelReasoningEffortParser.TryParse(value, out var parsed)
            ? parsed
            : throw new
                InvalidDataException($"Unsupported effort '{value}'. Accepted: {ModelReasoningEffortParser.AcceptedValuesText}.");

        var model = new UserProfileModel
        {
            Id = id.Trim(), DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            ContextWindowTokens = window,
            ReasoningEfforts = string.IsNullOrWhiteSpace(efforts)
                ? null
                : efforts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                         .Select(Parse).ToArray(),
            DefaultReasoningEffort = string.IsNullOrWhiteSpace(defaultEffort) || defaultEffort == "default"
                ? null
                : Parse(defaultEffort)
        };
        UserConfigValidator.ValidateModelEntry(model.Id, window, model.ReasoningEfforts, model.DefaultReasoningEffort,
                                               "model");
        return model;
    }

    /// <summary>
    ///     <para>完成表单后保存；新密钥写入独立目标，配置失败保留原引用并报告残留目标，不删除旧凭据。</para>
    ///     <para>Saves a completed form; new keys use fresh targets, and config failure preserves old references and reports orphan targets without deleting old credentials.</para>
    /// </summary>
    public static async Task SaveAsync(string            path, UserConfig baseline, UserConfig candidate,
                                       CancellationToken cancellationToken,
                                       ICredentialStore? credentials    = null, string? secret = null,
                                       string?           secretProvider = null)
    {
        UserConfigValidator.Validate(candidate);
        string? target = null;
        if (!string.IsNullOrEmpty(secret))
        {
            if (credentials is not { IsSupported: true })
                throw new InvalidOperationException("System credential storage is unavailable.");
            var provider = candidate.Profiles.Single(item => item.Name == secretProvider);
            target = "TinyHarness:" + provider.Name + ":" + Guid.NewGuid().ToString("N");
            candidate = candidate with
            {
                Profiles = candidate.Profiles.Select(item => item.Name == provider.Name
                                                         ? item with
                                                         {
                                                             ApiKeyCredentialTarget = target,
                                                             ApiKeyEnvironmentVariable = string.Empty
                                                         }
                                                         : item).ToArray()
            };
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                credentials.Save(target, secret);
            }
            catch (Exception ex)
            {
                throw new
                    IOException($"Credential storage failed ({ex.GetType().Name}); configuration was not saved. New entry '{target}' may remain.");
            }
        }

        try
        {
            await UserConfigStore.SaveAsync(path, candidate, baseline, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (target is not null)
        {
            // 不带底层异常正文，防止凭据后端错误包含密钥。
            throw new
                IOException($"Configuration was not saved ({ex.GetType().Name}); the previous reference remains valid. New credential entry '{target}' may remain. Reload before retrying.");
        }
    }
}

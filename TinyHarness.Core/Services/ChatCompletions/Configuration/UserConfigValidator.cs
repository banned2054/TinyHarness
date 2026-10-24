using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;

namespace TinyHarness.Core.Services.ChatCompletions.Configuration;

/// <summary>
///     <para>
///         用户配置目录的共享字段校验：供应商名称、HTTP(S) Endpoint、已实现的协议枚举、合法 API ID、
///         正数窗口、适配可用的强度集合与默认档位。CLI 与 Desktop 的添加／编辑入口和配置保存共用同一份规则。
///     </para>
///     <para>
///         Shared field validation for the user config catalog: provider names, HTTP(S) endpoints, implemented
///         protocol kinds, valid API ids, positive windows, and adapter-available effort sets with defaults. CLI and
///         Desktop add/edit entry points and config saves all use the same rules.
///     </para>
/// </summary>
/// <remarks>
///     <para>只校验结构与取值，不解析凭据、不探测服务、不读写磁盘；非法字段抛出带字段路径的异常。</para>
///     <para>Validates structure and values only — no credential resolution, service probing, or disk access; invalid fields throw with field paths.</para>
/// </remarks>
public static class UserConfigValidator
{
    /// <summary>
    ///     <para>校验完整候选配置；规则与加载端一致，保证已保存文件始终可以再次保存。</para>
    ///     <para>Validates a complete candidate config; the rules mirror the loading side so a saved file can always be saved again.</para>
    /// </summary>
    public static void Validate(UserConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var seenProfiles = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < config.Profiles.Count; i++)
        {
            var profile   = config.Profiles[i];
            var fieldPath = $"profiles[{i}]";
            if (string.IsNullOrWhiteSpace(profile.Name))
                throw new InvalidDataException($"User config field '{fieldPath}.name' must be a non-empty string.");
            if (!seenProfiles.Add(profile.Name))
                throw new
                    InvalidDataException($"User config field '{fieldPath}.name' duplicates profile name '{profile.Name}'.");

            ValidateEndpoint(profile.Endpoint, $"{fieldPath}.endpoint");
            if (!Enum.IsDefined(profile.ChatApi))
                throw new
                    InvalidDataException($"User config field '{fieldPath}.chatApi' has an unknown value '{profile.ChatApi}'.");

            var seenModels = new HashSet<string>(StringComparer.Ordinal);
            for (var m = 0; m < profile.Models.Count; m++)
            {
                var model     = profile.Models[m];
                var modelPath = $"{fieldPath}.models[{m}]";
                if (string.IsNullOrWhiteSpace(model.Id))
                    throw new InvalidDataException($"User config field '{modelPath}.id' must be a non-empty string.");
                if (!seenModels.Add(model.Id))
                    throw new
                        InvalidDataException($"User config field '{modelPath}.id' duplicates model id '{model.Id}'.");
                if (model.ContextWindowTokens <= 0)
                    throw new
                        InvalidDataException($"User config field '{modelPath}.contextWindowTokens' must be a positive integer.");

                ValidateReasoningCapabilities(model, modelPath);
            }
        }
    }

    /// <summary>
    ///     <para>校验候选供应商字段：名称非空、Endpoint 为空（待配置）或绝对 http(s) URL、协议为已实现枚举。</para>
    ///     <para>Validates a candidate provider: non-empty name, an empty (pending) or absolute http(s) endpoint, and an implemented protocol kind.</para>
    /// </summary>
    public static void ValidateProfileEntry(string name, string endpoint, ChatApiKind chatApi, string fieldPath)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException($"User config field '{fieldPath}.name' must be a non-empty string.");
        ValidateEndpoint(endpoint, $"{fieldPath}.endpoint");
        if (!Enum.IsDefined(chatApi))
            throw
                new InvalidDataException($"User config field '{fieldPath}.chatApi' has an unknown value '{chatApi}'.");
    }

    /// <summary>
    ///     <para>
    ///         校验候选模型条目：API ID 非空、窗口为正、强度档位互异且默认档位属于声明集合；未声明强度能力时默认档位必须为空。
    ///     </para>
    ///     <para>
    ///         Validates a candidate model entry: non-empty API id, positive window, distinct effort levels with the
    ///         default inside the declared set; without a declared capability the default must stay empty.
    ///     </para>
    /// </summary>
    public static void ValidateModelEntry(
        string id, int contextWindowTokens, IReadOnlyList<ModelReasoningEffort>? reasoningEfforts,
        ModelReasoningEffort? defaultReasoningEffort, string fieldPath)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidDataException($"User config field '{fieldPath}.id' must be a non-empty string.");
        if (contextWindowTokens <= 0)
            throw new
                InvalidDataException($"User config field '{fieldPath}.contextWindowTokens' must be a positive integer.");

        var model = new UserProfileModel
        {
            Id                     = id,
            ContextWindowTokens    = contextWindowTokens,
            ReasoningEfforts       = reasoningEfforts,
            DefaultReasoningEffort = defaultReasoningEffort
        };
        ValidateReasoningCapabilities(model, fieldPath);
    }

    /// <summary>
    ///     <para>Endpoint 为空表示待配置状态；非空时必须是绝对 http(s) URL。加载与保存共用同一规则。</para>
    ///     <para>An empty endpoint marks a pending profile; otherwise it must be an absolute http(s) URL. Loading and saving share this rule.</para>
    /// </summary>
    public static void ValidateEndpoint(string endpoint, string fieldPath)
    {
        if (GetEndpointError(endpoint) is { } error)
            throw new InvalidDataException($"User config field '{fieldPath}' {error}.");
    }

    /// <summary>
    ///     <para>返回 Endpoint 的校验错误片段，空或绝对 http(s) URL 时为 null；规则只有一个来源，加载端据此拼入文件路径。</para>
    ///     <para>Returns the endpoint validation error fragment, or null when it is empty or an absolute http(s) URL; the rule has a single source and the loading side appends the file path.</para>
    /// </summary>
    public static string? GetEndpointError(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;

        return Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is ("http" or "https")
            ? null
            : "must be an absolute http(s) URL, or empty for a pending profile";
    }

    /// <summary>
    ///     <para>
    ///         校验强度能力：档位属于已实现映射枚举且互异；默认档位只能为空（不发送参数）或属于声明集合，
    ///         未声明能力时默认档位必须为空。
    ///     </para>
    ///     <para>
    ///         Validates reasoning capabilities: levels must come from the implemented mapping enum and stay distinct;
    ///         the default must be empty (no parameter) or inside the declared set, and empty when no capability is declared.
    ///     </para>
    /// </summary>
    private static void ValidateReasoningCapabilities(UserProfileModel model, string fieldPath)
    {
        var seenEfforts = new HashSet<ModelReasoningEffort>();
        if (model.ReasoningEfforts is { } efforts)
            foreach (var effort in efforts)
            {
                if (!Enum.IsDefined(effort))
                    throw new
                        InvalidDataException($"User config field '{fieldPath}.reasoningEfforts' has an unknown effort '{effort}'.");
                if (!seenEfforts.Add(effort))
                    throw new
                        InvalidDataException($"User config field '{fieldPath}.reasoningEfforts' duplicates effort '{ModelReasoningEffortParser.ToValueString(effort)}'.");
            }

        if (model.DefaultReasoningEffort is { } defaultEffort)
        {
            if (model.ReasoningEfforts is not { Count: > 0 })
                throw new
                    InvalidDataException($"User config field '{fieldPath}.defaultReasoningEffort' requires the model to declare reasoningEfforts.");
            if (!seenEfforts.Contains(defaultEffort))
                throw new
                    InvalidDataException($"User config field '{fieldPath}.defaultReasoningEffort' must be one of the model's declared reasoningEfforts.");
        }
    }
}

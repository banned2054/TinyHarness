using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;

namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>
///         恢复会话时解析可执行目标：默认按日志最后保存的模型选择解析可信配置并构造客户端；
///         目标缺失、歧义、Endpoint／协议变化或已保存强度不再受支持时，返回“需要重新选择模型”，
///         不悄悄改用全局默认，也不解析失效目标的凭据或调用其客户端工厂。
///     </para>
///     <para>
///         Resolves the executable target when resuming a session: by default the log's last saved model selection
///         is resolved against trusted configuration to build the client; a missing or ambiguous target, an
///         endpoint/protocol change, or a saved effort no longer supported returns "re-selection required" instead
///         of silently adopting the global default or resolving the stale target's credentials and client factory.
///     </para>
/// </summary>
internal static class ChatResumeBinding
{
    /// <summary>
    ///     <para>
    ///         成功解析的恢复目标：模型身份、宿主参数、客户端、凭据路由、profile 提示与最后保存的选择；
    ///         目标模型当前窗口与保存选择不一致时携带 <see cref="RefreshedContext" />，使用前须生成新选择快照。
    ///     </para>
    ///     <para>
    ///         The resolved resume target: model identity, host options, client, credential route, profile hint, and
    ///         the last saved selection; when the target model's current window differs from the saved selection,
    ///         <see cref="RefreshedContext" /> is set and a new selection snapshot must be applied before use.
    ///     </para>
    /// </summary>
    internal sealed record ResolvedTarget(
        SessionModelIdentity Identity, AgentOptions Options, IChatCompletionClient Client,
        string CredentialRoute, string? ProfileHint, SessionModelSelection? SavedSelection,
        ContextOptions? RefreshedContext = null, IReadOnlyList<ModelReasoningEffort>? CurrentCapabilities = null);

    /// <summary>
    ///     <para>
    ///         目标解析结果：成功时携带完整绑定；失败时只携带需要重选的说明，历史仍可浏览，发送前须显式选择。
    ///     </para>
    ///     <para>
    ///         The resolution outcome: success carries the full binding; failure carries only the re-selection
    ///         reason — history stays browsable and an explicit selection must precede sending.
    ///     </para>
    /// </summary>
    internal sealed record ResolutionOutcome(ResolvedTarget? Target, string? ReselectionReason)
    {
        public bool NeedsReselection => Target is null;

        public static ResolutionOutcome Bound(ResolvedTarget target) => new(target, null);

        public static ResolutionOutcome Reselect(string reason) => new(null, reason);
    }

    /// <summary>
    ///     <para>
    ///         按快照最后保存的选择（旧日志按初始身份）解析恢复目标；模型身份与已记录凭据路由都与启动配置一致时
    ///         复用启动客户端，否则从可信目录解析目标 profile 并构造候选客户端。
    ///     </para>
    ///     <para>
    ///         Resolves the resume target from the snapshot's last saved selection (the initial identity for legacy
    ///         logs); the startup client is reused only when both the model identity and the recorded credential
    ///         route match the startup configuration, otherwise the target profile is resolved from the trusted
    ///         catalog to build the candidate client.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         宿主参数取最后保存选择记录的模型、上下文与强度，满足恢复契约要求的选择与参数一致；
    ///         同三元组但凭据路由不同的选择不得复用启动客户端，否则恢复会错用启动账户并追加非用户选择的路由修订。
    ///         有已记录路由时必须精确匹配——路由来源已不存在即需要重选，唯一同三元组候选不得替代另一账户；
    ///         未跟踪路由（null）沿用启动客户端或唯一候选兜底。已保存强度不再属于目标模型当前声明的能力时需要重选，
    ///         不删除参数后继续请求；复用启动客户端的路径同样按目录核对当前能力与窗口漂移，仅客户端复用不跳过核对。
    ///         目标 profile 仍在但凭据不可解析（组合工厂抛出 ConfigException）同样按需要重选返回，不中断恢复，
    ///         历史与写入端保留。目标解析失败时不调用客户端工厂，也不解析任何历史目标凭据；
    ///         目标模型当前窗口与保存选择不同时返回刷新上下文，由调用方在使用前追加新选择快照。
    ///     </para>
    ///     <para>
    ///         Host options take the model, context, and effort recorded in the last saved selection so the resume
    ///         contract's selection-versus-options consistency holds; a selection sharing the model triple but
    ///         carrying a different credential route must not reuse the startup client, or resume would run under the
    ///         startup account and append a route revision the user never chose. A recorded credential route must
    ///         match exactly — an unresolvable route requires re-selection instead of substituting the sole
    ///         same-triple candidate, and only untracked (null) records fall back to a unique candidate. A saved
    ///         effort that no longer belongs to the target model's currently declared capabilities requires
    ///         re-selection rather than sending without the parameter; the startup-client reuse path verifies the
    ///         same capability and window drift against the catalog — reusing the client never skips the check. A
    ///         target profile whose credentials no longer resolve (the composition factory throws
    ///         ConfigException) also returns re-selection-required instead of failing the resume, keeping history
    ///         and the writer intact. The client factory is never invoked for a failed resolution and no historical
    ///         target's credentials are resolved; when the target model's current window differs from the saved
    ///         selection a refreshed context is returned for the caller to materialize as a new selection snapshot
    ///         before use.
    ///     </para>
    ///     <para>
    ///         传入 null 启动客户端表示降级组合（启动默认模型凭据缺失）：没有可复用的客户端与路由，
    ///         目标一律从可信目录按日志记录解析，解析失败或凭据不可用同样返回需要重选——失效的启动默认
    ///         不阻止恢复入口先接管历史。
    ///     </para>
    ///     <para>
    ///         A null startup client denotes a degraded composition (the startup default's credentials are missing):
    ///         with nothing to reuse, the target always resolves from the trusted catalog by the log's records, and
    ///         resolution failures or unusable credentials likewise return re-selection-required — a stale startup
    ///         default never blocks the resume entry from taking over history first.
    ///     </para>
    /// </remarks>
    public static async Task<ResolutionOutcome> ResolveAsync(
        PersistedSession snapshot, SessionModelIdentity configuredIdentity, AgentOptions configuredOptions,
        IChatCompletionClient? configuredClient, string? configuredCredentialRoute,
        Func<CancellationToken, Task<UserConfig>> catalogLoader,
        Func<UserProfile, string, CancellationToken, Task<IChatCompletionClient>> clientFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(configuredIdentity);
        ArgumentNullException.ThrowIfNull(configuredOptions);
        ArgumentNullException.ThrowIfNull(catalogLoader);
        ArgumentNullException.ThrowIfNull(clientFactory);
        if (configuredClient is null)
        {
            // 降级组合（启动默认凭据缺失）：没有可复用的启动客户端与路由，目标只能从可信目录解析。
            if (configuredCredentialRoute is not null)
                throw new ArgumentException("A degraded resume composition has no startup credential route.",
                                            nameof(configuredCredentialRoute));
        }
        else if (string.IsNullOrWhiteSpace(configuredCredentialRoute))
            throw new ArgumentException("The startup credential route is required with a startup client.",
                                        nameof(configuredCredentialRoute));
        cancellationToken.ThrowIfCancellationRequested();

        // 旧日志没有任何选择记录：以初始身份为隐式修订 0 的目标；与启动配置一致时直接复用启动组合。
        var saved = snapshot.ModelSelection;
        if (saved is null)
        {
            if (snapshot.Model is null)
                return ResolutionOutcome.Reselect(
                    $"Stored session '{snapshot.Id}' carries no usable model identity; start a new session instead.");
            if (configuredClient is not null && snapshot.Model.Equals(configuredIdentity))
                return ResolutionOutcome.Bound(new ResolvedTarget(configuredIdentity, configuredOptions,
                                                                  configuredClient, configuredCredentialRoute!,
                                                                  ProfileHint   : null,
                                                                  SavedSelection: null));
            return await ResolveFromCatalogAsync(snapshot, snapshot.Model, null, configuredOptions, catalogLoader,
                                                 clientFactory, cancellationToken).ConfigureAwait(false);
        }

        IChatCompletionClient client       = configuredClient!;
        var                   route        = configuredCredentialRoute!;
        ResolvedTarget?       fromCatalog  = null;
        UserProfileModel?     startupMatch = null;

        // 未跟踪路由（null）的历史选择无从解析：沿用启动客户端，由恢复入口保守采纳当前路由。
        // 身份不同、已记录路由指向其他凭据（含同三元组换 profile／换账户），或没有启动组合时，必须按记录解析目标。
        if (configuredClient is null ||
            saved.Model != configuredIdentity ||
            (saved.CredentialRoute is not null && saved.CredentialRoute != configuredCredentialRoute))
        {
            var resolved = await ResolveFromCatalogAsync(snapshot, saved.Model, saved.CredentialRoute,
                                                         configuredOptions, catalogLoader, clientFactory,
                                                         cancellationToken).ConfigureAwait(false);
            if (resolved.NeedsReselection) return resolved;
            fromCatalog = resolved.Target!;
            client      = fromCatalog.Client;
            route       = fromCatalog.CredentialRoute;
        }
        else
        {
            // 启动复用客户端不等于跳过当前配置核对：按目录唯一定位当前模型，
            // 已保存强度不再受当前声明支持即需要重选，窗口漂移生成刷新上下文由调用方追加新快照；
            // 无法唯一归属（如未跟踪路由遇上同三元组多 profile）时无从核对，不据此阻断恢复。
            var matched = await TryMatchCurrentModelAsync(saved.Model, route, catalogLoader, cancellationToken)
                                .ConfigureAwait(false);
            if (matched is { } current)
            {
                if (saved.ReasoningEffort is { } effort &&
                    !(current.ReasoningEfforts?.Contains(effort) ?? false))
                    return ResolutionOutcome.Reselect(EffortUnsupportedReason(snapshot, current));
                startupMatch = current;
            }
        }

        // 参数取最后保存的选择本身，保证宿主接管校验所需的三项一致，不改用全局默认。
        var options = configuredOptions with
        {
            Model           = saved.Model.ModelId,
            Context         = saved.Context,
            ReasoningEffort = saved.ReasoningEffort
        };
        return ResolutionOutcome.Bound(new ResolvedTarget(saved.Model, options, client, route,
                                                          fromCatalog?.ProfileHint ?? saved.ProfileHint, saved,
                                                          fromCatalog?.RefreshedContext ??
                                                          (startupMatch is { } match
                                                              ? RefreshedContextFor(saved.Context, match)
                                                              : null),
                                                          fromCatalog?.CurrentCapabilities ??
                                                          startupMatch?.ReasoningEfforts));
    }

    /// <summary>
    ///     <para>
    ///         按日志身份三元组从可信目录解析目标 profile：有已记录路由时精确匹配，未跟踪路由允许唯一候选兜底；
    ///         解析成功且已保存强度仍受支持时构造客户端，否则返回需要重选。
    ///     </para>
    ///     <para>
    ///         Resolves the target profile from the trusted catalog by the log's identity triple: a recorded route
    ///         must match exactly while an untracked one allows the unique-candidate fallback; success builds the
    ///         client when any saved effort is still supported, otherwise re-selection is required.
    ///     </para>
    /// </summary>
    private static async Task<ResolutionOutcome> ResolveFromCatalogAsync(
        PersistedSession snapshot, SessionModelIdentity targetIdentity, string? recordedRoute,
        AgentOptions configuredOptions, Func<CancellationToken, Task<UserConfig>> catalogLoader,
        Func<UserProfile, string, CancellationToken, Task<IChatCompletionClient>> clientFactory,
        CancellationToken cancellationToken)
    {
        var catalog = await catalogLoader(cancellationToken).ConfigureAwait(false);
        var candidates =
            ChatModelSelection.ResolveTargets(catalog, targetIdentity.ModelId, null,
                                              profile => string.Equals(profile.Endpoint, targetIdentity.Endpoint,
                                                                       StringComparison.Ordinal) &&
                                                         targetIdentity.ChatApi == profile.ChatApi);
        // 有已记录路由时必须精确匹配：路由来源已不存在即需要重选，唯一同三元组候选不得替代另一账户。
        // 路由为 null 的历史记录无从判定来源，才允许唯一候选兜底。
        var target = ResolveRoutedTarget(candidates, recordedRoute);
        if (target is null && string.IsNullOrEmpty(recordedRoute) && candidates.Count == 1)
            target = candidates[0];
        if (target is not { } resolved)
        {
            if (candidates.Count == 0)
                return ResolutionOutcome.Reselect(
                    $"Stored session '{snapshot.Id}' was last saved with model "          +
                    $"'{targetIdentity.ModelId}' at '{targetIdentity.Endpoint}', which is no " +
                    "longer present in the local configuration; restore the provider or start a new session.");
            if (recordedRoute is not null)
                return ResolutionOutcome.Reselect(
                    $"Stored session '{snapshot.Id}' was last saved with model "        +
                    $"'{targetIdentity.ModelId}' at '{targetIdentity.Endpoint}' under " +
                    $"credential route '{recordedRoute}', which is no longer configured (currently configured " +
                    $"providers: {string.Join(", ", candidates.Select(candidate => candidate.Profile.Name))}); " +
                    "restore the provider or start a new session.");
            return ResolutionOutcome.Reselect(
                $"Stored session '{snapshot.Id}' was last saved with model "           +
                $"'{targetIdentity.ModelId}' at '{targetIdentity.Endpoint}', which is now configured under " +
                "multiple providers "                                                    +
                $"({string.Join(", ", candidates.Select(candidate => candidate.Profile.Name))}); "          +
                "remove the duplicate providers or restore the original credential route before resuming.");
        }

        // 已保存强度属于记录事实，不因当前能力变化判为损坏；但发送前要求仍受当前声明支持。
        if (snapshot.ModelSelection?.ReasoningEffort is { } savedEffort &&
            !(resolved.Model.ReasoningEfforts?.Contains(savedEffort) ?? false))
            return ResolutionOutcome.Reselect(EffortUnsupportedReason(snapshot, resolved.Model));

        IChatCompletionClient client;
        try
        {
            client = await clientFactory(resolved.Profile, targetIdentity.ModelId, cancellationToken)
                          .ConfigureAwait(false);
        }
        catch (ConfigException ex)
        {
            // 目标 profile 仍在但凭据已不可解析：目标不可用属于需要重选，而不是恢复失败；
            // 历史与写入端保留，用户可在未绑定会话中改选其他模型。
            return ResolutionOutcome.Reselect(
                $"Stored session '{snapshot.Id}' was last saved with model '{targetIdentity.ModelId}' under " +
                $"provider '{resolved.Profile.Name}', whose credentials could not be resolved ({ex.Message}); " +
                "restore the provider's API key or select another model with '/models use' before sending.");
        }

        var route = CredentialRoute.Describe(resolved.Profile.Name, resolved.Profile.ApiKeyEnvironmentVariable,
                                              resolved.Profile.ApiKeyCredentialTarget);
        var context = configuredOptions.Context is { } existing
            ? existing with { ContextWindowTokens = resolved.Model.ContextWindowTokens }
            : new ContextOptions { ContextWindowTokens = resolved.Model.ContextWindowTokens };
        var options = configuredOptions with
        {
            Model           = targetIdentity.ModelId,
            Context         = context,
            ReasoningEffort = null
        };
        return ResolutionOutcome.Bound(new ResolvedTarget(targetIdentity, options, client, route,
                                                          ProfileHint   : resolved.Profile.Name,
                                                          SavedSelection: snapshot.ModelSelection,
                                                          // 有保存选择时窗口漂移交由调用方追加新快照；旧日志直接采用当前窗口。
                                                          RefreshedContext:
                                                          snapshot.ModelSelection is { } savedSelection
                                                              ? RefreshedContextFor(savedSelection.Context,
                                                                                    resolved.Model)
                                                              : null,
                                                          CurrentCapabilities: resolved.Model.ReasoningEfforts));
    }

    private static string EffortUnsupportedReason(PersistedSession snapshot, UserProfileModel model) =>
        $"Stored session '{snapshot.Id}' was last saved with reasoning effort "                            +
        $"'{ModelReasoningEffortParser.ToValueString(snapshot.ModelSelection!.ReasoningEffort!.Value)}', " +
        $"which model '{model.Id}' no longer declares; select an effort again before sending.";

    /// <summary>
    ///     <para>按身份三元组与实际路由在当前目录中唯一定位目标模型；歧义或缺失返回 null，不据此阻断恢复。</para>
    ///     <para>Locates the target model in the current catalog by identity triple and actual route; ambiguity or absence returns null without blocking resume.</para>
    /// </summary>
    private static async Task<UserProfileModel?> TryMatchCurrentModelAsync(
        SessionModelIdentity identity, string route,
        Func<CancellationToken, Task<UserConfig>> catalogLoader, CancellationToken cancellationToken)
    {
        var catalog = await catalogLoader(cancellationToken).ConfigureAwait(false);
        var candidates =
            ChatModelSelection.ResolveTargets(catalog, identity.ModelId, null,
                                              profile => string.Equals(profile.Endpoint, identity.Endpoint,
                                                                       StringComparison.Ordinal) &&
                                                         identity.ChatApi == profile.ChatApi);
        return ResolveRoutedTarget(candidates, route)?.Model;
    }

    /// <summary>
    ///     <para>
    ///         目标模型当前窗口与保存选择不同时生成刷新上下文；窗口一致、保存上下文未知（null）或无从判断返回 null，
    ///         不为未记录窗口的选择补造当前值。
    ///     </para>
    ///     <para>
    ///         Builds a refreshed context when the target model's current window differs from the saved selection;
    ///         equal windows, an unknown (null) saved context, or an undeterminable target return null — selections
    ///         that never recorded a window are never back-filled with the current one.
    ///     </para>
    /// </summary>
    private static ContextOptions? RefreshedContextFor(ContextOptions? saved, UserProfileModel? model)
    {
        if (saved is null || model?.ContextWindowTokens is not { } tokens ||
            saved.ContextWindowTokens == tokens) return null;
        return saved with { ContextWindowTokens = tokens };
    }

    /// <summary>
    ///     <para>在身份匹配的候选中优先取凭据路由完全一致的 profile，区分同三元组的不同供应商。</para>
    ///     <para>Prefers the candidate whose credential route matches exactly, distinguishing providers that share one model triple.</para>
    /// </summary>
    private static (UserProfile Profile, UserProfileModel Model)? ResolveRoutedTarget(
        List<(UserProfile Profile, UserProfileModel Model)> candidates, string? credentialRoute)
    {
        if (string.IsNullOrEmpty(credentialRoute)) return null;

        foreach (var candidate in candidates)
            if (CredentialRoute.Describe(candidate.Profile.Name,
                                         candidate.Profile.ApiKeyEnvironmentVariable,
                                         candidate.Profile.ApiKeyCredentialTarget) == credentialRoute)
                return candidate;

        return null;
    }
}

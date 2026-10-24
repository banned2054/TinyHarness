using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;

namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>
///         恢复会话前从日志最后保存的模型选择推导请求绑定：身份、参数与客户端保持一致，
///         不沿用启动组合的默认选择；选择无法从当前可信配置解析时按恢复失败拒绝，不悄悄改用全局默认。
///     </para>
///     <para>
///         Derives the resume request binding from the session log's last saved model selection,
///         keeping identity, options, and client consistent instead of reusing the startup defaults;
///         a selection unresolvable from trusted configuration rejects the resume rather than silently
///         adopting the global default.
///     </para>
/// </summary>
internal static class ChatResumeBinding
{
    /// <summary>
    ///     <para>恢复用的请求绑定：模型身份、宿主参数、实际使用的客户端与其凭据路由，及最后保存的选择。</para>
    ///     <para>The resume request binding: model identity, host options, the client actually in use with its credential route, and the last saved selection.</para>
    /// </summary>
    internal sealed record RequestBinding(
        SessionModelIdentity Identity, AgentOptions Options, IChatCompletionClient Client,
        string CredentialRoute, SessionModelSelection? SavedSelection);

    /// <summary>
    ///     <para>
    ///         按快照最后保存的选择准备恢复绑定；模型身份与已记录凭据路由都与启动配置一致时复用启动客户端，
    ///         否则从可信目录解析目标 profile 并构造候选客户端，凭据路由描述实际使用的客户端。
    ///     </para>
    ///     <para>
    ///         Prepares the resume binding from the snapshot's last saved selection; the startup client is
    ///         reused only when both the model identity and the recorded credential route match the startup
    ///         configuration, otherwise the target profile is resolved from the trusted catalog to build
    ///         the candidate client, and the credential route describes the client in use.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         宿主参数取最后保存选择记录的模型、上下文与强度，满足恢复契约要求的选择与参数一致；
    ///         同三元组但凭据路由不同的选择不得复用启动客户端，否则恢复会错用启动账户并追加非用户选择的路由修订。
    ///         未跟踪路由（null）沿用启动客户端，由恢复入口按选择事务保守采纳；有已记录路由时必须精确匹配，
    ///         路由来源已不存在即拒绝恢复，唯一同三元组候选不得替代另一账户，只有未跟踪记录允许唯一候选兜底；
    ///         目标已从目录删除或匹配歧义时抛出 <see cref="SessionStoreException" />，不猜默认 profile。
    ///     </para>
    ///     <para>
    ///         Host options take the model, context, and effort recorded in the last saved selection so the
    ///         resume contract's selection-versus-options consistency holds; a selection sharing the model triple
    ///         but carrying a different credential route must not reuse the startup client, or resume would run
    ///         under the startup account and append a route revision the user never chose. Untracked routes
    ///         (null) keep the startup client with conservative adoption at the resume entry; a recorded
    ///         credential route must match exactly — an unresolvable route rejects the resume instead of
    ///         substituting the sole same-triple candidate, and only untracked (null) records fall back to a
    ///         unique candidate. A deleted model or an ambiguous triple throws
    ///         <see cref="SessionStoreException" /> instead of guessing a default profile.
    ///         The candidate client comes from the factory, whose key-resolution failures keep their own
    ///         semantics; constructed clients follow the process-lifetime convention.
    ///     </para>
    /// </remarks>
    public static async Task<RequestBinding> PrepareAsync(
        PersistedSession snapshot, SessionModelIdentity configuredIdentity, AgentOptions configuredOptions,
        IChatCompletionClient configuredClient, string configuredCredentialRoute,
        Func<CancellationToken, Task<UserConfig>> catalogLoader,
        Func<UserProfile, string, CancellationToken, Task<IChatCompletionClient>> clientFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(configuredIdentity);
        ArgumentNullException.ThrowIfNull(configuredOptions);
        ArgumentNullException.ThrowIfNull(configuredClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredCredentialRoute);
        ArgumentNullException.ThrowIfNull(catalogLoader);
        ArgumentNullException.ThrowIfNull(clientFactory);
        cancellationToken.ThrowIfCancellationRequested();

        var saved = snapshot.ModelSelection;
        if (saved is null)
            return new RequestBinding(configuredIdentity, configuredOptions, configuredClient,
                                      configuredCredentialRoute, null);

        IChatCompletionClient client = configuredClient;
        var                   route  = configuredCredentialRoute;

        // 未跟踪路由（null）的历史选择无从解析：沿用启动客户端，由恢复入口保守采纳当前路由。
        // 身份不同，或已记录路由指向其他凭据（含同三元组换 profile／换账户）时，必须按记录解析目标。
        if (saved.Model != configuredIdentity ||
            (saved.CredentialRoute is not null && saved.CredentialRoute != configuredCredentialRoute))
        {
            var catalog = await catalogLoader(cancellationToken).ConfigureAwait(false);
            var candidates =
                ChatModelSelection.ResolveTargets(catalog, saved.Model.ModelId, null,
                                                  profile => string.Equals(profile.Endpoint, saved.Model.Endpoint,
                                                                           StringComparison.Ordinal) &&
                                                             saved.Model.ChatApi == profile.ChatApi);
            // 有已记录路由时必须精确匹配：路由来源已不存在即拒绝恢复，唯一同三元组候选不得替代另一账户。
            // 路由为 null 的历史记录无从判定来源，才允许唯一候选兜底。
            var target = ResolveRoutedTarget(candidates, saved.CredentialRoute);
            if (target is null && string.IsNullOrEmpty(saved.CredentialRoute) && candidates.Count == 1)
                target = candidates[0];
            if (target is not { } resolved)
            {
                if (candidates.Count == 0)
                    throw new
                        SessionStoreException($"Stored session '{snapshot.Id}' was last saved with model "         +
                                              $"'{saved.Model.ModelId}' at '{saved.Model.Endpoint}', which is no " +
                                              "longer present in the local configuration; restore the provider or start a new session.");
                if (saved.CredentialRoute is not null)
                    throw new
                        SessionStoreException($"Stored session '{snapshot.Id}' was last saved with model " +
                                              $"'{saved.Model.ModelId}' at '{saved.Model.Endpoint}' under " +
                                              $"credential route '{saved.CredentialRoute}', which is no longer " +
                                              "configured (currently configured providers: " +
                                              $"{string.Join(", ", candidates.Select(candidate => candidate.Profile.Name))}); " +
                                              "restore the provider or start a new session.");
                throw new SessionStoreException($"Stored session '{snapshot.Id}' was last saved with model " +
                                                $"'{saved.Model.ModelId}' at '{saved.Model.Endpoint}', which is now " +
                                                "configured under multiple providers " +
                                                $"({string.Join(", ", candidates.Select(candidate => candidate.Profile.Name))}); " +
                                                "remove the duplicate providers or restore the original credential route before resuming.");
            }

            client = await clientFactory(resolved.Profile, saved.Model.ModelId, cancellationToken)
                          .ConfigureAwait(false);
            route = CredentialRoute.Describe(resolved.Profile.Name, resolved.Profile.ApiKeyEnvironmentVariable,
                                             resolved.Profile.ApiKeyCredentialTarget);
        }

        // 参数取最后保存的选择本身，保证宿主接管校验所需的三项一致，不改用全局默认。
        var options = configuredOptions with
        {
            Model           = saved.Model.ModelId,
            Context         = saved.Context,
            ReasoningEffort = saved.ReasoningEffort
        };
        return new RequestBinding(saved.Model, options, client, route, saved);
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

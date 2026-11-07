using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Session;

namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>为 CLI 会话组合 AI 审核路由，并从会话事实构建带来源标记的审批上下文。</para>
///     <para>Composes AI approval routing for CLI sessions and builds source-marked approval context from session facts.</para>
/// </summary>
internal static class CliApprovalRouting
{
    /// <summary>
    ///     <para>创建按当前会话审批模式工作的提供者；审核客户端由调用方按需提供。</para>
    ///     <para>
    ///         Creates a provider routed by the current session approval mode; the caller supplies reviewer clients on
    ///         demand.
    ///     </para>
    /// </summary>
    internal static IApprovalProvider Create(
        IApprovalProvider human, Func<ChatSessionHost> host, PermissionEngine permissions,
        Func<CancellationToken, Task<(IChatCompletionClient Client, string Model)>> createReviewer) =>
        new AiRoutedApprovalProvider(human, new AiReviewer(createReviewer), () => permissions.ApprovalMode,
                                     () => BuildContext(host(), permissions));

    /// <summary>
    ///     <para>仅把原始用户回合、已生效补充和授权历史送入审核上下文，来源与记录标识由程序赋予。</para>
    ///     <para>
    ///         Includes only original user turns, applied amendments and grant history; sources and record ids are assigned
    ///         by the program.
    ///     </para>
    /// </summary>
    internal static ApprovalContext BuildContext(ChatSessionHost host, PermissionEngine permissions)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(permissions);

        var entries = new List<ApprovalContextEntry>();
        var turns   = host.Session.Turns;
        for (var i = 0; i < turns.Count; i++)
            entries.Add(new ApprovalContextEntry(turns[i].CreatedUtc, ApprovalInputSource.UserUtterance,
                                                 $"turn-{i + 1}", turns[i].UserInput));

        foreach (var amendment in host.Session.Amendments.Where(amendment =>
                                                                    amendment.CanonicalMessageIndex is not null))
            entries.Add(new ApprovalContextEntry(amendment.CreatedUtc, ApprovalInputSource.EffectiveAmendment,
                                                 $"amendment-{amendment.Id}", amendment.Text));

        foreach (var grant in permissions.Grants.SnapshotGrants())
            entries.Add(new ApprovalContextEntry(grant.CreatedUtc, ApprovalInputSource.GrantHistory,
                                                 grant.GrantId,
                                                 $"grant {grant.Scope} {grant.Capability} state={grant.State}"));

        return ApprovalContextBuilder.Build(entries);
    }
}

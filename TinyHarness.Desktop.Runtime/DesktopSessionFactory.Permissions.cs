using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Session;

namespace TinyHarness.Desktop.Runtime;

public sealed partial class DesktopSessionFactory
{
    /// <summary>
    ///     <para>从可信配置创建独立审批客户端；跟随当前会话或使用明确指定的供应商与模型。</para>
    ///     <para>
    ///         Creates an independent reviewer from trusted configuration, following the current session or an explicit
    ///         provider/model.
    ///     </para>
    /// </summary>
    private async Task<(IChatCompletionClient Client, string Model)> CreateReviewerAsync(
        ChatSessionHost host, CancellationToken cancellationToken)
    {
        var user = await UserConfigStore
                        .LoadAsync(_userConfigPath ?? UserConfigStore.DefaultFilePath(), cancellationToken)
                        .ConfigureAwait(false);
        var settings = user.Settings?.Permissions ?? new PermissionSettings();
        settings.Validate();
        var target = settings.UseCurrentModel
            ? FindTarget(user, host.Session.ModelSelection, host.Session.Model)
            : user.Profiles.Where(profile => profile.Name == settings.ReviewerProfile)
                  .SelectMany(profile => profile.Models.Where(model => model.Id == settings.ReviewerModel)
                                                .Select(model => ((UserProfile Profile, UserProfileModel Model)?)(
                                                            profile, model)))
                  .SingleOrDefault();
        if (target is not { } selected)
            throw new InvalidOperationException("审批供应商或模型已不存在，请在权限设置中重新选择。");
        var config = ConfigFor(new TinyHarnessConfig { WorkspaceRoot = host.Session.WorkspaceRoot },
                               selected.Profile, selected.Model);
        return (CreateClient(config, selected.Profile, user.Settings?.Network), selected.Model.Id);
    }

    /// <summary>
    ///     <para>
    ///         构建结构化审批输入：用户原话与已生效补充按时间进入窗口（经 <see cref="ApprovalContextBuilder" />
    ///         封顶并附加系统窗口说明），活动授权作为 GrantHistory 条目。工具输出、助手文本与待审操作
    ///         不作为授权来源。
    ///     </para>
    ///     <para>
    ///         Builds the structured approval input: user wording and applied amendments enter the time-ordered
    ///         window (bounded by <see cref="ApprovalContextBuilder" /> with a system window notice appended), and
    ///         active grants enter as GrantHistory entries. Tool output, assistant text and the pending operation
    ///         never grant authority.
    ///     </para>
    /// </summary>
    private static ApprovalContext ReviewContext(ChatSessionHost host, PermissionEngine? permissions)
    {
        var entries = new List<ApprovalContextEntry>();
        var turns = host.Session.Turns.ToArray();
        for (var i = 0; i < turns.Length; i++)
            entries.Add(new ApprovalContextEntry(turns[i].CreatedUtc, ApprovalInputSource.UserUtterance,
                                                 $"turn-{i + 1}", turns[i].UserInput));
        foreach (var amendment in host.Session.Amendments.Where(amendment =>
                                                                   amendment.CanonicalMessageIndex is not null))
            entries.Add(new ApprovalContextEntry(amendment.CreatedUtc, ApprovalInputSource.EffectiveAmendment,
                                                 $"amendment-{amendment.Id}", amendment.Text));
        foreach (var grant in permissions?.Grants.SnapshotGrants() ?? new List<Grant>())
            entries.Add(new ApprovalContextEntry(grant.CreatedUtc, ApprovalInputSource.GrantHistory,
                                                 grant.GrantId,
                                                 $"grant {grant.Scope} {grant.Capability} state={grant.State}"));
        return ApprovalContextBuilder.Build(entries);
    }
}

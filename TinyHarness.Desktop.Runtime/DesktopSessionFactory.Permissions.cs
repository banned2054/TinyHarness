using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
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
    ///     <para>按时间提供近期用户原话与已生效补充；工具输出和助手文本不作为授权来源。</para>
    ///     <para>
    ///         Provides recent user wording and applied amendments chronologically; tool output and assistant text never
    ///         grant authority.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<string> ReviewInstructions(ChatSessionHost host) =>
        host.Session.Turns.Select(turn => (Time : turn.CreatedUtc, Text : turn.UserInput))
            .Concat(host.Session.Amendments.Where(amendment => amendment.CanonicalMessageIndex is not null)
                        .Select(amendment => (Time : amendment.CreatedUtc, amendment.Text)))
            .OrderBy(item => item.Time).TakeLast(16).Select(item => item.Text).ToArray();
}

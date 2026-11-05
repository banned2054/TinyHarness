using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Session;

namespace TinyHarness.Desktop.Runtime;

public sealed partial class DesktopSessionFactory
{
    private const int MaxReviewInstructionEntries    = 32;
    private const int MaxReviewInstructionCharacters = 30_000;

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
    ///         按时间提供近期用户原话与已生效补充；超出条数或字数上限时丢弃更早记录并附加窗口说明；
    ///         工具输出和助手文本不作为授权来源。
    ///     </para>
    ///     <para>
    ///         Provides recent user wording and applied amendments chronologically; records beyond the entry
    ///         or character caps are dropped and a window notice is appended, and tool output or assistant
    ///         text never grants authority.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<string> ReviewInstructions(ChatSessionHost host)
    {
        var items = host.Session.Turns.Select(turn => (Time : turn.CreatedUtc, Text : turn.UserInput))
                        .Concat(host.Session.Amendments.Where(amendment => amendment.CanonicalMessageIndex is not null)
                                    .Select(amendment => (Time : amendment.CreatedUtc, amendment.Text)))
                        .OrderBy(item => item.Time).ToArray();
        return SelectReviewInstructions(items);
    }

    /// <summary>
    ///     <para>
    ///         从按时间升序的发言中自最新向前保留至多 32 条、累计 30,000 字符；丢弃更早记录时在末尾追加
    ///         以〔窗口说明〕开头的合成说明，无丢弃时不追加。
    ///     </para>
    ///     <para>
    ///         Keeps at most 32 entries totalling 30,000 characters from the newest end of a time-ascending
    ///         list; when older records are dropped a synthesized notice starting with 〔窗口说明〕 is appended,
    ///         and none is appended without a drop.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         首条候选在尚未保留任何条目时即使超预算也保留，避免空窗口；窗口说明不计入字符预算；
    ///         输出条目保持输入的时间升序，窗口说明位于最后。
    ///     </para>
    ///     <para>
    ///         The first candidate is kept even when over budget while nothing is kept yet, avoiding an empty
    ///         window; the notice does not count against the character budget, kept entries stay in the
    ///         input's ascending order, and the notice comes last.
    ///     </para>
    /// </remarks>
    internal static IReadOnlyList<string> SelectReviewInstructions(
        IReadOnlyList<(DateTimeOffset Time, string Text)> items)
    {
        var kept       = new List<string>(items.Count);
        var characters = 0;
        for (var i = items.Count - 1; i >= 0 && kept.Count < MaxReviewInstructionEntries; i--)
        {
            // 尚未保留任何条目时不做预算检查，保证窗口至少包含最新一条授权相关发言。
            if (kept.Count > 0 && characters + items[i].Text.Length > MaxReviewInstructionCharacters) break;
            kept.Add(items[i].Text);
            characters += items[i].Text.Length;
        }

        kept.Reverse();
        if (kept.Count < items.Count)
            kept.Add($"〔窗口说明〕以上为最近保留的 {kept.Count} 条用户发言与已生效补充；另有 {items.Count - kept.Count} 条更早记录未包含在内，未被包含的更早发言既不授予也不否定本次授权。");
        return kept;
    }
}

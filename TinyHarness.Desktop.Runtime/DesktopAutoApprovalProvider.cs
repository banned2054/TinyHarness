using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Permissions;

namespace TinyHarness.Desktop.Runtime;

/// <summary>
///     <para>
///         桌面的 AI 审核适配器：把 Core 的 <see cref="AiRoutedApprovalProvider" /> 与本地状态回调组合。
///         AI 允许按单次放行；拒绝保持未执行且绝不转人工；仅升级决定转人工；技术失败返回 Unavailable。
///     </para>
///     <para>
///         The desktop's AI-review adapter: it composes Core's <see cref="AiRoutedApprovalProvider" /> with local
///         status callbacks. AI allow releases the single operation; denial stays unexecuted and never reaches a
///         human; only an escalation does; technical failures return Unavailable.
///     </para>
/// </summary>
public sealed class DesktopAutoApprovalProvider(
    DesktopApprovalProvider                                                     human,
    Func<SessionApprovalMode>                                                   mode,
    Func<CancellationToken, Task<(IChatCompletionClient Client, string Model)>> createReviewer,
    Func<ApprovalContext>                                                       context,
    Func<string, ValueTask>                                                     status) : IApprovalProvider
{
    private readonly AiRoutedApprovalProvider routed =
        new(human, new DesktopAiReviewer(createReviewer, status), mode, context);

    /// <inheritdoc />
    public Task<ApprovalResolution> PromptAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        routed.PromptAsync(request, cancellationToken);

    /// <summary>
    ///     <para>把 Core 审核器包装为 <see cref="IAiReviewer" />，前后发布桌面状态回调。</para>
    ///     <para>Wraps the Core reviewer as <see cref="IAiReviewer" /> with desktop status callbacks around it.</para>
    /// </summary>
    private sealed class DesktopAiReviewer(
        Func<CancellationToken, Task<(IChatCompletionClient Client, string Model)>> createReviewer,
        Func<string, ValueTask>                                                     status) : IAiReviewer
    {
        private readonly AiReviewer _inner = new(createReviewer);

        public async Task<AiReviewOutcome> ReviewAsync(ApprovalRequest      request,
                                                       ApprovalContext      context,
                                                       CancellationToken    cancellationToken)
        {
            await status("AI 正在审核操作").ConfigureAwait(false);
            var outcome = await _inner.ReviewAsync(request, context, cancellationToken).ConfigureAwait(false);
            if (outcome.Result?.Decision == AiReviewDecision.Allow)
                await status("AI 已批准本次操作").ConfigureAwait(false);
            else
                await status("等待用户审批").ConfigureAwait(false);
            return outcome;
        }
    }
}

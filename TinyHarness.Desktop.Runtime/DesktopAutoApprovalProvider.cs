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
/// <param name="approvalTimeoutProvider">
///     <para>
///         审批期限来源：每次审核开始时解析一次并冻结于该次审核，不再是构造期冻结值；
///         null 时由审核员沿用内置默认值。
///     </para>
///     <para>
///         The approval deadline source: resolved once when each review starts and frozen for that review
///         instead of a construction-time value; null keeps the reviewer's built-in default.
///     </para>
/// </param>
/// <param name="timeProvider">
///     <para>时间源；主要供测试注入可控时钟，null 使用系统时间。</para>
///     <para>The time source, mainly for tests to inject a controlled clock; null uses the system time.</para>
/// </param>
public sealed class DesktopAutoApprovalProvider(
    DesktopApprovalProvider                                                     human,
    Func<SessionApprovalMode>                                                   mode,
    Func<CancellationToken, Task<(IChatCompletionClient Client, string Model)>> createReviewer,
    Func<ApprovalContext>                                                       context,
    Func<string, ValueTask>                                                     status,
    Func<TimeSpan>?                                                             approvalTimeoutProvider = null,
    TimeProvider?                                                               timeProvider            = null) : IApprovalProvider
{
    private readonly AiRoutedApprovalProvider routed =
        new(human,
            new DesktopAiReviewer(createReviewer, status, approvalTimeoutProvider, timeProvider),
            mode, context);

    /// <inheritdoc />
    public Task<ApprovalResolution> PromptAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        routed.PromptAsync(request, cancellationToken);

    /// <summary>
    ///     <para>把 Core 审核器包装为 <see cref="IAiReviewer" />，前后发布桌面状态回调。</para>
    ///     <para>Wraps the Core reviewer as <see cref="IAiReviewer" /> with desktop status callbacks around it.</para>
    /// </summary>
    private sealed class DesktopAiReviewer(
        Func<CancellationToken, Task<(IChatCompletionClient Client, string Model)>> createReviewer,
        Func<string, ValueTask>                                                     status,
        Func<TimeSpan>?                                                             approvalTimeoutProvider,
        TimeProvider?                                                               timeProvider) : IAiReviewer
    {
        private readonly AiReviewer _inner = new(createReviewer, approvalTimeoutProvider, timeProvider);

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

using TinyHarness.Core.Models.Permissions;

namespace TinyHarness.Core.Services.Permissions;

/// <summary>
///     <para>
///         按审批模式路由的审批提供者：人工审批直达人工通道；AI 审核先交独立审核员——
///         允许按单次放行，拒绝保持未执行且绝不转人工，仅升级决定转交人工；技术失败
///         返回 Unavailable，不执行、不转人工，允许明确重试审核。
///     </para>
///     <para>
///         The mode-routed approval provider: human approval goes straight to the human channel; AI review goes to
///         the independent reviewer first — allow releases the single operation, denial stays unexecuted and never
///         reaches a human, only an escalation does, and technical failures return Unavailable without execution
///         or human fallback, awaiting an explicit review retry.
///     </para>
/// </summary>
public sealed class AiRoutedApprovalProvider(
    IApprovalProvider         human,
    IAiReviewer               reviewer,
    Func<SessionApprovalMode> mode,
    Func<ApprovalContext>     context) : IApprovalProvider
{
    /// <inheritdoc />
    public async Task<ApprovalResolution> PromptAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (mode() != SessionApprovalMode.AIAutoReview) return await human.PromptAsync(request, cancellationToken)
                                                                     .ConfigureAwait(false);

        var outcome = await reviewer.ReviewAsync(request, context(), cancellationToken).ConfigureAwait(false);
        if (outcome.Failure is { } failure)
            return ApprovalResolution.Unavailable(
                $"AI review {failure.Kind.ToString().ToLowerInvariant()}: {failure.Detail}; the operation was not executed.");

        var result = outcome.Result!;
        return result.Decision switch
        {
            AiReviewDecision.Allow => ApprovalResolution.Granted(GrantScope.SingleOperation),
            AiReviewDecision.Deny  => ApprovalResolution.Denied($"AI 审核拒绝：{result.Reason}"),
            AiReviewDecision.EscalateToUser => await EscalateAsync(request with
            {
                ReviewerNote = result.Reason,
                Review       = result
            }, cancellationToken).ConfigureAwait(false),
            _ => ApprovalResolution.Unavailable("Unknown AI review decision; the operation was not executed.")
        };
    }

    /// <summary>
    ///     <para>把同一冻结计划连同审核理由交人工裁决；人批只覆盖原计划。</para>
    ///     <para>Hands the same frozen plan plus the review reason to the human; a human approval covers the original plan only.</para>
    /// </summary>
    private async Task<ApprovalResolution> EscalateAsync(ApprovalRequest escalated, CancellationToken cancellationToken)
    {
        try
        {
            return await human.PromptAsync(escalated, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ApprovalResolution.Unavailable(
                $"The human approval channel is unreachable: {exception.Message}; the operation was not executed.");
        }
    }
}

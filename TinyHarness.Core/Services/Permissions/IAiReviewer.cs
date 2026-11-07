using TinyHarness.Core.Models.Permissions;

namespace TinyHarness.Core.Services.Permissions;

/// <summary>
///     <para>
///         独立、无工具的 AI 审核员接口：对一次审批请求给出三值决定，或以四类技术失败单独表示失败。
///         失败不是拒绝：失败不写入拒绝记忆、不转交人工，操作保持未执行。
///     </para>
///     <para>
///         The independent tool-free AI reviewer: it returns a three-valued decision for one approval request,
///         or a technical failure in one of four kinds. A failure is not a denial — it records no denial memory,
///         escalates to no one, and leaves the operation unexecuted.
///     </para>
/// </summary>
public interface IAiReviewer
{
    /// <summary>
    ///     <para>
    ///         以结构化审批输入审核当前冻结计划。实现不得缓存审核结果；取消令牌取消本次审核。
    ///     </para>
    ///     <para>
    ///         Reviews the frozen plan against the structured approval input. Implementations must not cache
    ///         verdicts; the cancellation token cancels the review.
    ///     </para>
    /// </summary>
    Task<AiReviewOutcome> ReviewAsync(ApprovalRequest      request,
                                      ApprovalContext      context,
                                      CancellationToken    cancellationToken);
}

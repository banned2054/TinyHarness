using TinyHarness.Core.Models.Permissions;

namespace TinyHarness.Core.Services.Permissions;

/// <summary>
///     <para>
///         向审批人展示已经准备好的副作用调用并获取审批结论。仅当权限引擎返回 Ask 时由
///         Agent Loop 调用；拒绝与不可用都保持操作未执行，两者是不同的结论。
///     </para>
///     <para>
///         Asks an approver to resolve a prepared, side-effect-bearing invocation. The Agent Loop calls this only
///         when the permission engine decides Ask; denied and unavailable both leave the operation unexecuted and
///         are distinct resolutions.
///     </para>
/// </summary>
public interface IApprovalProvider
{
    /// <summary>
    ///     <para>
    ///         异步请求审批结论（按允许范围批准、拒绝或报告通道不可用），并响应取消。
    ///         人审通道不可达时返回 Unavailable，明确表示"未执行"，不得伪装成拒绝。
    ///     </para>
    ///     <para>
    ///         Resolves asynchronously — grant within the allowed scopes, deny, or report the channel
    ///         unavailable — while honoring cancellation. An unreachable human channel returns Unavailable,
    ///         stating plainly that nothing executed; it must never masquerade as a denial.
    ///     </para>
    /// </summary>
    Task<ApprovalResolution> PromptAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

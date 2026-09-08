using TinyHarness.Core.Tools;

namespace TinyHarness.Core.Permissions;

/// <summary>
/// 向用户展示已经准备好的副作用调用并获取审批选择。仅当权限引擎返回
/// <see cref="PermissionDecision.Ask"/> 时由 Agent Loop 调用。
///
/// Asks a human to approve a prepared, side-effect-bearing tool invocation. The
/// Agent Loop calls this only when the <see cref="PermissionEngine"/> decides
/// <see cref="PermissionDecision.Ask"/>. The CLI implements it as an interactive
/// prompt; tests and the offline smoke use scripted implementations.
/// </summary>
public interface IApprovalProvider
{
    /// <summary>
    /// 异步请求用户选择单次允许、会话允许或拒绝，并响应取消。
    /// Asks asynchronously for allow-once, allow-session, or deny, while honoring cancellation.
    /// </summary>
    Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken);
}

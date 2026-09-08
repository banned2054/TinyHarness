namespace TinyHarness.Core.Permissions;

/// <summary>
/// 用户审批副作用调用时可选择的动作：单次允许、会话允许或拒绝。
///
/// The user's choice when asked to approve a side effect: allow once, allow for
/// the session, or deny.
/// </summary>
public enum ApprovalAction
{
    /// <summary>
    /// 仅允许与该指纹完全相同的这一次调用。
    /// Approves exactly this invocation, bound to its fingerprint.
    /// </summary>
    AllowOnce,

    /// <summary>
    /// 在本次会话余下时间允许该能力与资源范围。
    /// Approves this capability and resource scope for the rest of the session.
    /// </summary>
    AllowSession,

    /// <summary>
    /// 拒绝当前调用，后续调用仍按正常规则重新判断。
    /// Refuses this invocation; later invocations are evaluated normally.
    /// </summary>
    Deny,
}

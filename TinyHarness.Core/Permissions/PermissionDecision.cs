namespace TinyHarness.Core.Permissions;

/// <summary>
/// 已准备工具调用经过当前策略与会话状态计算后的权限结论。
///
/// The outcome of evaluating a prepared tool invocation against the current
/// policy and session state.
/// </summary>
public enum PermissionDecision
{
    /// <summary>
    /// 无需询问即可执行。
    /// Execute without asking.
    /// </summary>
    Allow,

    /// <summary>
    /// 执行前必须询问用户。
    /// Ask the user before executing.
    /// </summary>
    Ask,

    /// <summary>
    /// 拒绝执行。
    /// Refuse to execute.
    /// </summary>
    Deny,
}

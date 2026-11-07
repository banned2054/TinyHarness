namespace TinyHarness.Core.Models.Permissions;

/// <summary>
///     <para>会话级审批模式：只区分审批人，不扩大或收窄任何资源授权。</para>
///     <para>
///         The session-level approval mode: it only selects the approver and never widens or narrows
///         resource authorization.
///     </para>
/// </summary>
public enum SessionApprovalMode
{
    /// <summary>
    ///     <para>所有需审批的操作交由用户裁决。</para>
    ///     <para>Every approval-required operation is decided by the user.</para>
    /// </summary>
    HumanApproval,

    /// <summary>
    ///     <para>需审批的操作先交独立 AI 审核员；仅其升级决定转交用户。</para>
    ///     <para>
    ///         Approval-required operations go to the independent AI reviewer first; only its escalation
    ///         decision reaches the user.
    ///     </para>
    /// </summary>
    AIAutoReview
}

/// <summary>
///     <para>审批模式的稳定用户界面名称与说明。</para>
///     <para>Stable user-facing names and descriptions for approval modes.</para>
/// </summary>
public static class SessionApprovalModes
{
    /// <summary>全部审批模式；设置菜单与离线冒烟使用。All approval modes; used by settings and offline smoke.</summary>
    public static IReadOnlyList<SessionApprovalMode> All { get; } =
        Array.AsReadOnly(Enum.GetValues<SessionApprovalMode>());


    /// <summary>
    ///     <para>返回稳定的用户界面名称。</para>
    ///     <para>Returns the stable user-facing name.</para>
    /// </summary>
    public static string Label(this SessionApprovalMode mode) => mode switch
    {
        SessionApprovalMode.HumanApproval => "人工审批",
        SessionApprovalMode.AIAutoReview  => "AI 审核",
        _                                 => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    /// <summary>
    ///     <para>返回该模式自动范围与审批路由的说明。</para>
    ///     <para>Describes the mode's automatic scope and approval routing.</para>
    /// </summary>
    public static string Description(this SessionApprovalMode mode) => mode switch
    {
        SessionApprovalMode.HumanApproval =>
            "沙箱基线操作自动执行；其余操作由用户审批。",
        SessionApprovalMode.AIAutoReview =>
            "沙箱基线操作自动执行；其余操作先由独立 AI 审核员审核，仅其升级决定转交用户。",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}

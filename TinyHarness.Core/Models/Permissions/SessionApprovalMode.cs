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
        SessionApprovalMode.HumanApproval => "请求批准",
        SessionApprovalMode.AIAutoReview  => "帮我批准",
        _                                 => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    /// <summary>
    ///     <para>返回菜单行使用的单行说明；比 <see cref="Description" /> 短，只讲何时需要询问。</para>
    ///     <para>
    ///         Returns the one-line menu caption; shorter than <see cref="Description" /> and limited to when
    ///         approval is requested.
    ///     </para>
    /// </summary>
    public static string Summary(this SessionApprovalMode mode) => mode switch
    {
        SessionApprovalMode.HumanApproval => "编辑工作区外文件、联网和宿主执行时始终询问",
        SessionApprovalMode.AIAutoReview  => "仅对 AI 判断有风险的操作请求你批准",
        _                                 => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    /// <summary>
    ///     <para>返回该模式自动范围与审批路由的完整说明。</para>
    ///     <para>Describes the mode's automatic scope and approval routing in full.</para>
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

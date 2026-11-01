namespace TinyHarness.Core.Models.Permissions;

/// <summary>
///     <para>用户选择的会话权限预设；不包含临时批准。</para>
///     <para>A user-selected session permission preset, excluding temporary approvals.</para>
/// </summary>
public enum SessionPermissionMode
{
    ConfirmEveryAction,
    ReadOnly,
    ProjectWrite,
    FullAccess,
    ReviewEveryAction,
    ReadOnlyAutoReview,
    ProjectWriteAutoReview
}

/// <summary>
///     <para>七种权限预设共用的名称与审批语义。</para>
///     <para>Shared names and approval semantics for the seven permission presets.</para>
/// </summary>
public static class SessionPermissionModes
{
    public static IReadOnlyList<SessionPermissionMode> All { get; } =
        Array.AsReadOnly(Enum.GetValues<SessionPermissionMode>());

    /// <summary>
    ///     <para>返回稳定的用户界面名称。</para>
    ///     <para>Returns the stable user-facing name.</para>
    /// </summary>
    public static string Label(this SessionPermissionMode mode) => mode switch
    {
        SessionPermissionMode.ConfirmEveryAction     => "逐次确认",
        SessionPermissionMode.ReadOnly               => "只读，可申请",
        SessionPermissionMode.ProjectWrite           => "项目内读写",
        SessionPermissionMode.FullAccess             => "完全开放",
        SessionPermissionMode.ReviewEveryAction      => "逐项 AI 审核",
        SessionPermissionMode.ReadOnlyAutoReview     => "只读，AI 审批",
        SessionPermissionMode.ProjectWriteAutoReview => "项目内读写，AI 审批",
        _                                            => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    /// <summary>
    ///     <para>返回自动范围、审批范围和审批人的说明。</para>
    ///     <para>Describes automatic scope, approval scope, and the reviewer.</para>
    /// </summary>
    public static string Description(this SessionPermissionMode mode) => mode switch
    {
        SessionPermissionMode.ConfirmEveryAction     => "读取、写入与 Shell 均由用户逐次批准。",
        SessionPermissionMode.ReadOnly               => "项目内读取自动；写入、项目外访问与 Shell 由用户审批。",
        SessionPermissionMode.ProjectWrite           => "项目内读写自动；项目外访问与 Shell 由用户审批。",
        SessionPermissionMode.FullAccess             => "文件与 Shell 自动执行，使用宿主权限，不逐次审批。",
        SessionPermissionMode.ReviewEveryAction      => "读取、写入与 Shell 均先由 AI 审核。",
        SessionPermissionMode.ReadOnlyAutoReview     => "项目内读取自动；写入、项目外访问与 Shell 由 AI 审批。",
        SessionPermissionMode.ProjectWriteAutoReview => "项目内读写自动；项目外访问与 Shell 由 AI 审批。",
        _                                            => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    /// <summary>
    ///     <para>判断该预设是否把待审批操作交给 AI。</para>
    ///     <para>Determines whether pending approvals are routed to AI.</para>
    /// </summary>
    public static bool UsesAutoReview(this SessionPermissionMode mode) =>
        mode is SessionPermissionMode.ReviewEveryAction or SessionPermissionMode.ReadOnlyAutoReview or
                SessionPermissionMode.ProjectWriteAutoReview;
}

/// <summary>
///     <para>可靠保存的模式选择；修订连续，不保存可复用的执行批准。</para>
///     <para>A durable mode selection with contiguous revisions and no reusable execution grants.</para>
/// </summary>
public sealed record SessionPermissionSelection
{
    public string                Schema   { get; init; } = "tinyharness.permission-selection/1";
    public long                  Revision { get; init; }
    public SessionPermissionMode Mode     { get; init; }

    /// <summary>
    ///     <para>拒绝未知模式、协议或无效修订。</para>
    ///     <para>Rejects unknown modes, schemas, and invalid revisions.</para>
    /// </summary>
    public void Validate()
    {
        if (Schema != "tinyharness.permission-selection/1" || Revision < 1 || !Enum.IsDefined(Mode))
            throw new InvalidDataException("Invalid session permission selection.");
    }
}

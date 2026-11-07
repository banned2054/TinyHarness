using TinyHarness.Core.Models.Tools;

namespace TinyHarness.Core.Models.Permissions;

/// <summary>
///     <para>
///         交给审批人的完整请求：冻结计划、执行分类、允许的授权范围、审核员备注与 AI 审核结果。
///         除冻结计划与分类外，其余字段仅供展示，不能授予任何权限。
///     </para>
///     <para>
///         The complete request handed to an approver: the frozen plan, execution level, allowed grant scopes,
///         reviewer note and AI-review result. Except for the frozen plan and level, all fields are display-only
///         and never grant authority.
///     </para>
/// </summary>
public sealed record ApprovalRequest(
    ToolPreparation          Preparation,
    PermissionLevel          Level,
    IReadOnlyList<GrantScope> AllowedScopes,
    string?                  ReviewerNote,
    AiReviewResult?          Review);

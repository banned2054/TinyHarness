using TinyHarness.Core.Models.Permissions;

namespace TinyHarness.Core.Models.Persistence;

/// <summary>
///     <para>
///         可靠保存的审批模式选择（协议 /2）：修订连续，只记录审批人路由，不保存可复用的执行授权。
///         校验拒绝 /1 与未知协议；/1 记录由迁移流程处理，不直接进入运行时。
///     </para>
///     <para>
///         The durably saved approval-mode selection (schema /2): contiguous revisions recording only the approver
///         routing, never reusable execution grants. Validation rejects /1 and unknown schemas; /1 records go
///         through the migration flow and never reach the runtime directly.
///     </para>
/// </summary>
public sealed record SessionPermissionSelectionV2
{
    public const string CurrentSchema = "tinyharness.permission-selection/2";

    public string             Schema       { get; init; } = CurrentSchema;
    public long               Revision     { get; init; }
    public SessionApprovalMode ApprovalMode { get; init; }

    /// <summary>
    ///     <para>拒绝未知协议、无效修订与未知审批模式。</para>
    ///     <para>Rejects unknown schemas, invalid revisions and unknown approval modes.</para>
    /// </summary>
    public void Validate()
    {
        if (Schema != CurrentSchema || Revision < 1 || !Enum.IsDefined(ApprovalMode))
            throw new InvalidDataException("Invalid session approval-mode selection.");
    }
}

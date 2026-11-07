using TinyHarness.Core.Models.Permissions;

namespace TinyHarness.Core.Models.Persistence;

/// <summary>
///     <para>
///         旧权限选择迁移完成的必需记录（协议 /1）：记录迁移时间、旧模式名与新审批模式。
///         不携带任何可执行授权；旧程序读到该记录种类会拒绝读取日志（既有机制）。
///     </para>
///     <para>
///         The required record of a completed legacy permission-selection migration (schema /1): it records the
///         completion time, the legacy mode name and the new approval mode. It carries no executable authority;
///         older programs reject the log on reading this record kind (existing mechanism).
///     </para>
/// </summary>
public sealed record PermissionMigrationRecord
{
    public const string CurrentSchema = "tinyharness.permission-migration/1";

    public string             Schema       { get; init; } = CurrentSchema;
    public long               Revision     { get; init; }
    public DateTimeOffset     CompletedUtc { get; init; }
    public string?            PreviousMode { get; init; }
    public SessionApprovalMode ApprovalMode { get; init; }
    public string             PolicyVersion { get; init; } = CurrentPolicyVersion;

    /// <summary>本记录所对应权限策略的版本号；策略语义变化时递增。The permission-policy version this record maps to; bumped on policy changes.</summary>
    public const string CurrentPolicyVersion = "1";

    /// <summary>
    ///     <para>拒绝未知协议、无效修订、未知审批模式与未知时间。</para>
    ///     <para>Rejects unknown schemas, invalid revisions, unknown approval modes and default timestamps.</para>
    /// </summary>
    public void Validate()
    {
        if (Schema != CurrentSchema || Revision < 1 || !Enum.IsDefined(ApprovalMode) ||
            CompletedUtc == default)
            throw new InvalidDataException("Invalid permission migration record.");
        if (PreviousMode is not null && LegacyPermissionSelectionMapper.FromLegacyName(PreviousMode) is null)
            throw new InvalidDataException("Invalid previous mode name in the permission migration record.");
    }
}

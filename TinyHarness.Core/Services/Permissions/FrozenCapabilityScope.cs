namespace TinyHarness.Core.Services.Permissions;

/// <summary>
///     <para>
///         冻结计划携带的能力范围：计划被授权时锁定的附加读根、附加写根与受保护元数据写路径。
///         执行侧从其私有执行计划（PreparedProcessExecution.GrantScope）读取；复核时
///         current 快照的每一项都必须仍在该范围内。
///     </para>
///     <para>
///         The capability scope carried by a frozen plan: the additional read roots, additional write roots and
///         protected-metadata write paths locked in when the plan was authorized. The execution side reads it
///         from its private execution plan (PreparedProcessExecution.GrantScope); every item of the current
///         snapshot must still be contained in it at revalidation.
///     </para>
/// </summary>
public sealed record FrozenCapabilityScope(
    IReadOnlyList<string> AdditionalReadRoots,
    IReadOnlyList<string> AdditionalWriteRoots,
    IReadOnlyList<string> MetadataWritePaths);

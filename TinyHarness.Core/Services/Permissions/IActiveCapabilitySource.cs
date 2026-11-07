namespace TinyHarness.Core.Services.Permissions;

/// <summary>
///     <para>
///         执行侧注入的活动 Grant 状态查询：向授权侧与审批展示暴露当前生效的附加读根、
///         附加写根与受保护元数据写路径。它只是状态事实，不产生授权，也不是引擎求值输入。
///     </para>
///     <para>
///         The active-grant state query injected by the execution side: it exposes the additional read roots,
///         additional write roots and metadata-write paths currently in effect for authorization-side and
///         approval display. It states facts only, grants nothing, and is not an engine evaluation input.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         实现必须廉价、纯查询、不得抛出异常或执行 I/O；快照按调用时状态返回。
///         MetadataWritePaths 是活动 grant 允许收缩 DenyWritePaths 的受保护元数据路径。
///     </para>
///     <para>
///         Implementations must be cheap, pure lookups that never throw or perform I/O; the snapshot reflects
///         the state at call time. MetadataWritePaths lists protected-metadata paths that active grants allow
///         narrowing out of DenyWritePaths.
///     </para>
/// </remarks>
public interface IActiveCapabilitySource
{
    /// <summary>
    ///     <para>返回活动 Grant 状态快照；每次调用重新求值，调用方不得长期缓存。</para>
    ///     <para>Returns the active-grant snapshot, re-evaluated per call; callers must not cache it long-term.</para>
    /// </summary>
    ActiveCapabilitySnapshot Snapshot();
}

/// <summary>
///     <para>活动 Grant 状态快照：附加读根、附加写根与受保护元数据写路径。</para>
///     <para>The active-grant snapshot: additional read roots, additional write roots and metadata-write paths.</para>
/// </summary>
public sealed record ActiveCapabilitySnapshot(
    IReadOnlyList<string> AdditionalReadRoots,
    IReadOnlyList<string> AdditionalWriteRoots,
    IReadOnlyList<string> MetadataWritePaths);

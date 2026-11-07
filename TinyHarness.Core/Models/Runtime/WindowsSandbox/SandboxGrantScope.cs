namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         grant 驱动的沙箱能力扩展范围：活动授权的附加只读根、附加写根与受保护元数据写路径。
///         由执行侧从活动 Grant 归并而来，经组合器 fail-closed 校验后，供每命令 setup payload
///         与策略身份构造消费；空范围表示本次执行不携带任何 grant 能力扩展。
///     </para>
///     <para>
///         The grant-driven sandbox capability extension: the active grants' additional read
///         roots, write roots, and protected-metadata write paths. The execution side merges
///         them from active grants; after the composer's fail-closed validation they feed the
///         per-command setup payload and the policy identity. An empty scope means the
///         execution carries no grant extension at all.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         路径必须是绝对路径；读根与写根可重叠（写蕴含读）。元数据写路径只允许指向
///         <see cref="SandboxIsolationPolicy.ProtectedMetadataSubpaths" /> 下的目录，使 deny 收缩
///         不会波及任何其他保护路径。
///     </para>
///     <para>
///         Paths must be absolute; read and write roots may overlap (write implies read).
///         Metadata write paths may only address directories under
///         <see cref="SandboxIsolationPolicy.ProtectedMetadataSubpaths" />, so the deny
///         reduction can never touch any other protected path.
///     </para>
/// </remarks>
public sealed record SandboxGrantScope
{
    /// <summary>不携带任何 grant 能力扩展的空范围。 The empty scope carrying no grant extension.</summary>
    public static SandboxGrantScope Empty { get; } = new()
    {
        ReadRoots = [], WriteRoots = [], MetadataWritePaths = []
    };

    /// <summary>活动授权附加的只读根（绝对路径，已去重保持顺序）。 Additional read roots from active grants (absolute, deduplicated, order-preserving).</summary>
    public required IReadOnlyList<string> ReadRoots { get; init; }

    /// <summary>活动授权附加的写根（绝对路径，已去重保持顺序）。 Additional write roots from active grants (absolute, deduplicated, order-preserving).</summary>
    public required IReadOnlyList<string> WriteRoots { get; init; }

    /// <summary>
    ///     活动授权允许写入的受保护元数据目录（绝对路径）；这些目录将从 deny-write 集合移除。
    ///     Protected-metadata directories active grants may write (absolute); they are removed from the deny-write set.
    /// </summary>
    public required IReadOnlyList<string> MetadataWritePaths { get; init; }

    /// <summary>
    ///     判断范围是否为空：三个列表都为空时与 <see cref="Empty" /> 等价，payload 与身份串保持无 grant 形态。
    ///     Whether the scope is empty: with all three lists empty this equals <see cref="Empty" /> and both the
    ///     payload and the identity keep their grant-free shape.
    /// </summary>
    public bool IsEmpty => ReadRoots.Count == 0 && WriteRoots.Count == 0 && MetadataWritePaths.Count == 0;
}

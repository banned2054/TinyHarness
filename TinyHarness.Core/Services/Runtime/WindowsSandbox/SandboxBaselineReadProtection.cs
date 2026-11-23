namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         沙箱基线广泛读保护计划：随组合冻结的 OS 读根（用户 profile 根）与 deny-read 保护集
///         （C1 宿主密钥路径 + C2 已知敏感目录），经 setup payload 的 read_roots 与
///         deny_read_paths 通道下发。
///     </para>
///     <para>
///         The sandbox baseline broad-read protection plan: the OS read root (the user profile
///         root) frozen with composition plus the deny-read protection set (C1 host secret paths
///         and C2 known sensitive directories), delivered through the setup payload's
///         read_roots and deny_read_paths channels.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         读根走既有组 RX ACL 通道，非 grant、非可信设置；deny 条目必须与读根覆盖的保护路径
///         配对，缺配对即组合 fail-closed。本类型不改变应用层资源分类，只为 OS 侧读边界提供
///         组合期事实。
///     </para>
///     <para>
///         The read root uses the existing group-RX ACL channel and is neither a grant nor a
///         trusted setting; every deny entry must pair with a protected path covered by the read
///         root, and a missing pair fails composition closed. This type never changes the
///         application-layer resource classification; it only states composition-time facts for
///         the OS read boundary.
///     </para>
///     <para>
///         相等语义只覆盖读根与拒读集：两个计划当且仅当 <see cref="ReadRoots" /> 与
///         <see cref="DenyReadPaths" /> 逐位置相等（元素按大小写不敏感的序数路径语义比较，
///         顺序敏感）时相等；其他观测属性与未来新增的观测属性一律不参与 Equals、GetHashCode
///         与 ==/!=，比较计划的策略语义时不得依赖它们。
///     </para>
///     <para>
///         The equality contract covers only the read roots and the deny set: two plans are equal
///         if and only if <see cref="ReadRoots" /> and <see cref="DenyReadPaths" /> are positionally
///         equal (elements compared with case-insensitive ordinal path semantics, order-sensitive);
///         any other observational properties, present or future, never participate in Equals,
///         GetHashCode, or ==/!=, and comparisons of a plan's policy semantics must not depend
///         on them.
///     </para>
/// </remarks>
public sealed record SandboxBaselineReadProtection(
    IReadOnlyList<string> ReadRoots,
    IReadOnlyList<string> DenyReadPaths)
{
    /// <summary>
    ///     <para>
    ///         按读根与拒读集的逐位置序列相等判定（顺序敏感、元素大小写不敏感）；其他观测
    ///         属性与未来新增的观测属性一律不参与。
    ///     </para>
    ///     <para>
    ///         Judges equality by the positional sequence equality of the read roots and the deny
    ///         set (order-sensitive, case-insensitive elements); any other observational
    ///         properties, present or future, never participate.
    ///     </para>
    /// </summary>
    public bool Equals(SandboxBaselineReadProtection? other) =>
        other is not null &&
        ReadRoots.SequenceEqual(other.ReadRoots, PathComparer) &&
        DenyReadPaths.SequenceEqual(other.DenyReadPaths, PathComparer);

    // record 语义：用户声明 Equals(R?) 后编译器自动合成 Equals(object?)（转发到本方法）与
    // ==/!=，二者与本方法绑定一致；显式再声明 Equals(object?) 反而与合成成员冲突（CS0111）。
    public override int GetHashCode() =>
        HashCode.Combine(SequenceHashCode(ReadRoots), SequenceHashCode(DenyReadPaths));

    // 计划成员是规范化的绝对路径；与其他路径边界比较一致地忽略大小写（Windows 语义）。
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private static int SequenceHashCode(IReadOnlyList<string> paths)
    {
        var hash = new HashCode();
        foreach (var path in paths) hash.Add(path, PathComparer);

        return hash.ToHashCode();
    }
}

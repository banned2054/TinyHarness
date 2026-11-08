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
/// </remarks>
public sealed record SandboxBaselineReadProtection(
    IReadOnlyList<string> ReadRoots,
    IReadOnlyList<string> DenyReadPaths);

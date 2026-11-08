using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         grant 只读根按基线 deny-read 集的分流结果：<see cref="AuthorizedReadRoots" /> 是被 deny 集
///         覆盖的获批路径（等于或落入某 deny 条目，组 RX 通道必被 DENY 压制，只能走授权通道），
///         <see cref="GroupReadRoots" /> 维持既有 read_roots 组通道不变；仅覆盖 deny 条目的父根在组
///         通道本可读，保持组通道，deny 子树仍受保护。
///     </para>
///     <para>
///         The split of grant read roots against the baseline deny-read set:
///         <see cref="AuthorizedReadRoots" /> holds approved paths covered by the deny set (equal to or
///         inside a deny entry — the group-RX channel would be suppressed by DENY, so only the
///         authorized channel can carry them), while <see cref="GroupReadRoots" /> stays on the
///         existing read_roots group channel unchanged. A parent root that merely covers deny entries
///         reads fine on the group channel and stays there; denied subtrees remain protected.
///     </para>
/// </summary>
public sealed record SandboxAuthorizedReadSplit(
    IReadOnlyList<string> AuthorizedReadRoots,
    IReadOnlyList<string> GroupReadRoots)
{
    /// <summary>是否存在必须走授权通道的获批读路径。Whether any approved read root must use the authorized channel.</summary>
    public bool HasAuthorizedReads => AuthorizedReadRoots.Count > 0;
}

/// <summary>
///     <para>
///         authorized_read_paths 的分流纯函数：deny_read_paths 与 grant 只读根之间的确定性判定，
///         供 payload 构造与策略身份构造共用，保证两侧看到同一分流。
///     </para>
///     <para>
///         The pure splitter behind authorized_read_paths: a deterministic verdict between the
///         deny_read_paths and the grant read roots, shared by payload and policy-identity
///         construction so both sides always see the same split.
///     </para>
/// </summary>
public static class SandboxAuthorizedReads
{
    /// <summary>
    ///     <para>
    ///         按序分流 grant 只读根：等于或落入任一 deny 条目（即该根自身的组 RX 访问必被 DENY
    ///         压制）的根进入授权集，其余保持组通道顺序。仅覆盖 deny 条目的父根不被压制，维持组
    ///         通道；deny 集保持组合期冻结不动。两侧输出都做词法规范化（Path.GetFullPath），保证
    ///         payload 与身份构造看到确定的路径形态。
    ///     </para>
    ///     <para>
    ///         Splits the grant read roots in order: a root equal to or inside any deny entry (its own
    ///         group-RX access would be suppressed by DENY) joins the authorized set while the rest
    ///         keep the group channel in order; a parent root that only covers deny entries is not
    ///         suppressed and stays on the group channel. The deny set stays frozen from composition.
    ///         Both outputs are lexically normalized (Path.GetFullPath) so payload and identity
    ///         construction always see a deterministic path form.
    ///     </para>
    /// </summary>
    public static SandboxAuthorizedReadSplit Split(
        IReadOnlyList<string> denyReadPaths, IReadOnlyList<string> grantReadRoots)
    {
        ArgumentNullException.ThrowIfNull(denyReadPaths);
        ArgumentNullException.ThrowIfNull(grantReadRoots);
        if (grantReadRoots.Count == 0) return new SandboxAuthorizedReadSplit([], []);

        var authorized = new List<string>();
        var group      = new List<string>();
        foreach (var root in grantReadRoots)
        {
            var full = Path.GetFullPath(root);
            if (denyReadPaths.Any(deny => Workspace.IsInside(deny, full))) authorized.Add(full);
            else group.Add(full);
        }

        return new SandboxAuthorizedReadSplit(authorized, group);
    }
}

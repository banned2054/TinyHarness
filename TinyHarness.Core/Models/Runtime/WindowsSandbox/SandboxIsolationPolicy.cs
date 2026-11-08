namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     <para>已解析的有效隔离策略：工作区根、spawn profile、有效写入根（含临时写根）与 deny-write 元数据保护。</para>
///     <para>
///         The resolved effective isolation policy: the workspace root, the spawn profile, the effective
///         write roots (including temp roots), and the deny-write metadata protection.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         同一实例供权限审批与执行消费；由它派生 setup refresh payload，保证 ACL 与 profile 一致。
///         record 相等性对 IReadOnlyList 成员退化为引用相等，比较实例必须逐字段进行，不可依赖 Equals。
///     </para>
///     <para>
///         Approval and execution consume the same frozen instance; setup refresh payloads derive from it
///         so ACLs stay consistent with the profile. Record equality degrades to reference equality for
///         IReadOnlyList members, so comparisons must go field by field instead of relying on Equals.
///     </para>
/// </remarks>
public sealed record SandboxIsolationPolicy
{
    /// <summary>
    ///     <para>约定受保护的元数据子目录；WorkspaceWrite 下保持只读。</para>
    ///     <para>Conventionally protected metadata subdirectories; read-only under WorkspaceWrite.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> ProtectedMetadataSubpaths = [".git", ".agents", ".codex", ".aws"];

    public required SandboxPolicyKind Kind { get; init; }

    public bool AllowTcpLoopback { get; init; }

    public required string WorkspaceRoot { get; init; }

    public required SandboxPermissionProfile SpawnProfile { get; init; }

    /// <summary>
    ///     <para>常设网络策略为 enabled；spawn profile 的 network 与之一致，逐次审批由后端按执行计划覆盖。</para>
    ///     <para>
    ///         The standing network policy is enabled; the spawn profile's network matches it, while
    ///         per-command grants are applied by the backend from the execution plan.
    ///     </para>
    /// </summary>
    public bool NetworkEnabled { get; init; }

    /// <summary>
    ///     <para>
    ///         有效只读根（绝对路径，已规范化）：可信设置声明的额外只读根，进入 setup 的 read_roots，
    ///         由沙箱账户组以 OS 级 RX ACL 访问。
    ///     </para>
    ///     <para>
    ///         The effective read roots (absolute, normalized): additional read-only
    ///         roots declared by trusted settings, fed into setup read_roots and
    ///         exposed to the sandbox account group through OS-level RX ACLs.
    ///     </para>
    /// </summary>
    public required IReadOnlyList<string> EffectiveReadRoots { get; init; }

    /// <summary>
    ///     <para>
    ///         基线广泛读根（绝对路径）：沙箱启用组合时随基线下发的 OS 读根（用户 profile 根），
    ///         走既有 read_roots 组 RX 通道，非 grant、非可信设置；凭据等保护路径由
    ///         <see cref="DenyReadPaths" /> 配对排除。
    ///     </para>
    ///     <para>
    ///         The baseline broad-read root (absolute): the OS read root shipped with the baseline
    ///         when the sandbox composes (the user profile root), through the existing read_roots
    ///         group-RX channel — neither a grant nor a trusted setting; protected paths such as
    ///         credentials are excluded by the paired <see cref="DenyReadPaths" />.
    ///     </para>
    /// </summary>
    public IReadOnlyList<string> BaselineReadRoots { get; init; } = [];

    /// <summary>
    ///     <para>
    ///         基线 deny-read 保护集（绝对路径）：C1 宿主密钥路径与 C2 已知敏感目录，经 setup
    ///         payload 的 deny_read_paths 在每命令 refresh 前同步施加，集合收缩时由 deny-read
    ///         簿记回收（与 deny-write 的不回收语义不同）。
    ///     </para>
    ///     <para>
    ///         The baseline deny-read protection set (absolute): C1 host secret paths and C2 known
    ///         sensitive directories, applied synchronously before every command refresh through the
    ///         setup payload's deny_read_paths and reclaimed by the deny-read bookkeeping when the
    ///         set shrinks (unlike deny-write, which is never reclaimed).
    ///     </para>
    /// </summary>
    public IReadOnlyList<string> DenyReadPaths { get; init; } = [];

    /// <summary>
    ///     <para>
    ///         有效写入根（绝对路径，含临时写根）；ReadOnly 策略为空。用于 capability SID 选择与
    ///         setup 的 write_roots。
    ///     </para>
    ///     <para>
    ///         The effective write roots (absolute, including temp roots); empty under the ReadOnly policy.
    ///         Feeds capability-SID selection and setup write_roots.
    ///     </para>
    /// </summary>
    public required IReadOnlyList<string> EffectiveWriteRoots { get; init; }

    /// <summary>
    ///     <para>保持只读的受保护路径（元数据目录）；setup 以 deny_write_paths 施加。</para>
    ///     <para>Protected read-only paths (metadata directories); applied by setup as deny_write_paths.</para>
    /// </summary>
    public required IReadOnlyList<string> DenyWritePaths { get; init; }

    /// <summary>
    ///     <para>临时写根（来自清理后的目标环境 TEMP/TMP，绝不直接使用宿主 TEMP）。</para>
    ///     <para>
    ///         The temp write roots (from the cleaned target environment's TEMP/TMP, never the host TEMP directly).
    ///     </para>
    /// </summary>
    public required IReadOnlyList<string> TempWriteRoots { get; init; }

    public bool UsesWriteCapabilities => EffectiveWriteRoots.Count > 0;

    /// <summary>
    ///     <para>
    ///         构造每命令 refresh payload：read_roots 覆盖工作区、命令 cwd、临时根、有效只读根与
    ///         基线广泛读根；write_roots 为有效写入根；deny-write 保护元数据目录；deny-read 按
    ///         保护集下发（缺失路径由 helper 物化为目录，组合侧已保证 C1 存在）；proxy 端口留空。
    ///     </para>
    ///     <para>
    ///         Builds the per-command refresh payload: read_roots cover the workspace, the command cwd,
    ///         the temp roots, the effective read roots, and the baseline broad-read root;
    ///         write_roots are the effective write roots; deny-write protects metadata directories;
    ///         deny-read ships the protection set as-is (the helper materializes missing paths as
    ///         directories, and composition already ensured the C1 entries exist); proxy ports stay
    ///         empty.
    ///     </para>
    /// </summary>
    public SandboxSetupPayload CreateSetupPayload(
        WindowsSandboxComponents components, string commandWorkingDirectory, string realUser, bool refreshOnly) =>
        CreateSetupPayload(components, commandWorkingDirectory, realUser, refreshOnly, null);

    /// <summary>
    ///     <para>
    ///         构造每命令 refresh payload，并把已通过校验的 grant 范围并入：grant 只读根按 deny-read
    ///         集分流——与 deny 集冲突（等于或落入 deny 条目）的获批路径进入 authorized_read_paths
    ///         （payload 版本随之升为 v7，走专用授权主体通道），其余与写根分别追加进
    ///         read_roots/write_roots；grant 元数据写路径从 deny-write 集合移除。deny_read_paths
    ///         集合保持冻结不动。空范围与无 grant 的 payload 完全一致。合并前假设调用方已通过组合器
    ///         的 ResolveGrantScope 完成 fail-closed 校验；本方法只做确定性合并。
    ///     </para>
    ///     <para>
    ///         Builds the per-command refresh payload and merges a validated grant scope: grant read
    ///         roots split against the deny-read set — approved paths conflicting with the deny set
    ///         (equal to or inside a deny entry) go to authorized_read_paths (the payload version moves
    ///         to v7 through the dedicated authorization subject), while the rest and the write roots
    ///         are appended to read_roots/write_roots; grant metadata write paths are removed from the
    ///         deny-write set. The deny_read_paths set stays frozen. An empty scope yields exactly the
    ///         grant-free payload. The caller is expected to have run the composer's fail-closed
    ///         ResolveGrantScope validation; this method only performs the deterministic merge.
    ///     </para>
    /// </summary>
    public SandboxSetupPayload CreateSetupPayload(
        WindowsSandboxComponents components, string commandWorkingDirectory, string realUser, bool refreshOnly,
        SandboxGrantScope?       grantScope)
    {
        if (components.AllowTcpLoopback != AllowTcpLoopback)
            throw new InvalidOperationException("Sandbox component and policy TCP-loopback modes must match.");
        var readRoots = new List<string> { WorkspaceRoot, commandWorkingDirectory };
        readRoots.AddRange(TempWriteRoots);
        readRoots.AddRange(EffectiveReadRoots);
        readRoots.AddRange(BaselineReadRoots);
        var writeRoots = new List<string>(EffectiveWriteRoots);
        IReadOnlyList<string>? authorizedReadPaths = null;
        if (grantScope is { } grant)
        {
            var split = SandboxAuthorizedReads.Split(DenyReadPaths, grant.ReadRoots);
            readRoots.AddRange(split.GroupReadRoots);
            writeRoots.AddRange(grant.WriteRoots);
            if (split.HasAuthorizedReads) authorizedReadPaths = Deduplicate(split.AuthorizedReadRoots);
        }

        // deny 收缩只针对 grant 明确允许的元数据目录；其余 deny 项不受 grant 影响。
        var denyWritePaths = Deduplicate(DenyWritePaths.Where(path => File.Exists(path) || Directory.Exists(path)));
        if (grantScope is { } scope && scope.MetadataWritePaths.Count > 0)
            denyWritePaths = denyWritePaths
                            .Where(path => !scope.MetadataWritePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                            .ToList();

        return new SandboxSetupPayload
        {
            AllowTcpLoopback        = AllowTcpLoopback,
            OfflineUsername         = components.OfflineUsername,
            OnlineUsername          = components.OnlineUsername,
            SandboxHome             = components.SandboxHome,
            CommandWorkingDirectory = commandWorkingDirectory,
            ReadRoots               = Deduplicate(readRoots),
            WriteRoots              = Deduplicate(writeRoots),
            // deny-read 保护集原样下发（缺失路径由 helper 物化）；deny-write 元数据保护
            // 遵守 skip-if-missing，避免 helper 创建占位目录。
            DenyReadPaths           = Deduplicate(DenyReadPaths),
            DenyWritePaths          = denyWritePaths,
            AuthorizedReadPaths     = authorizedReadPaths,
            RealUser                = realUser,
            RefreshOnly             = refreshOnly
        };
    }

    /// <summary>
    ///     <para>路径去重（大小写不敏感、保持顺序）；策略解析与 refresh payload 构造共用一份实现。</para>
    ///     <para>
    ///         Path deduplication (case-insensitive, order-preserving); a single implementation
    ///         shared by policy resolution and refresh payload construction.
    ///     </para>
    /// </summary>
    internal static IReadOnlyList<string> Deduplicate(IEnumerable<string> paths)
    {
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var path in paths)
            if (seen.Add(path))
                result.Add(path);

        return result;
    }
}

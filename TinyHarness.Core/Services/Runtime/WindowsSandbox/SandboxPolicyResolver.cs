using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         把固定策略种类解析为有效隔离策略：绑定工作区根，从清理后的目标环境推导临时写根
///         （绝不直接用宿主 TEMP），并派生与 Codex 内置 workspace_write 语义一致的 spawn profile
///         （root 读、project_roots 写、tmpdir 写、元数据目录只读保护）。
///     </para>
///     <para>
///         Resolves a fixed policy kind into the effective isolation policy: binds
///         the workspace root, derives temp write roots from the cleaned target
///         environment (never the host TEMP directly), and produces a spawn profile
///         matching the built-in Codex workspace_write semantics (root read,
///         project_roots write, tmpdir write, read-only metadata protection).
///     </para>
/// </summary>
public static class SandboxPolicyResolver
{
    /// <summary>
    ///     <para>
    ///         解析策略。workspaceRoot 必须是完全限定的绝对路径（否则抛 ArgumentException）；
    ///         env 是将冻结给目标命令的环境（从中取 TEMP/TMP）；additionalWriteRoots 是可信设置声明的
    ///         额外写根（绝对路径；read-only 策略下这是声明必要临时写根的唯一机制）；network 是常设
    ///         网络策略；additionalReadRoots 是可信只读根；sandboxHome 用于写根、只读根与临时写根
    ///         对凭据目录的重叠校验——生产路径始终提供，缺省时跳过该校验。baselineReadRoots 与
    ///         baselineDenyReadPaths 是基线广泛读根与 deny-read 保护集（GAP-1）：不做对凭据目录的
    ///         附加根校验（凭据目录由 provisioning 永久 DENY 且 deny 集配对保护），形态校验由组合器
    ///         fail-closed 把守。
    ///     </para>
    ///     <para>
    ///         Resolves a policy. workspaceRoot must be a fully qualified absolute
    ///         path (otherwise ArgumentException is thrown); env is the environment to
    ///         be frozen for the target command (TEMP/TMP are read from it);
    ///         additionalWriteRoots are extra write roots declared by trusted settings
    ///         (absolute paths; under the read-only policy this is the only mechanism
    ///         to declare necessary write roots); network is the standing network
    ///         policy; additionalReadRoots are trusted read-only roots; sandboxHome
    ///         feeds the write-, read-, and temp-root overlap validation against the
    ///         credentials directory (the production path always supplies it; when
    ///         absent the overlap check is skipped). baselineReadRoots and
    ///         baselineDenyReadPaths are the baseline broad-read root and the deny-read
    ///         protection set (GAP-1): they skip the additional-root credentials-overlap
    ///         validation (the credentials directory is permanently denied by provisioning
    ///         and paired through the deny set), while their shape stays guarded fail-closed
    ///         by the composer.
    ///     </para>
    /// </summary>
    public static SandboxIsolationPolicy Resolve(SandboxPolicyKind                   kind,
                                                 string                              workspaceRoot,
                                                 IReadOnlyDictionary<string, string> targetEnvironment,
                                                 IReadOnlyList<string>?              additionalWriteRoots = null,
                                                 bool                                allowTcpLoopback     = false,
                                                 SandboxNetworkPolicy network =
                                                     SandboxNetworkPolicy.Restricted,
                                                 IReadOnlyList<string>? additionalReadRoots   = null,
                                                 string?                sandboxHome           = null,
                                                 IReadOnlyList<string>? baselineReadRoots     = null,
                                                 IReadOnlyList<string>? baselineDenyReadPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(targetEnvironment);

        if (!Path.IsPathFullyQualified(workspaceRoot))
            throw new
                ArgumentException($"The workspace root must be a fully qualified absolute path: '{workspaceRoot}'.",
                                  nameof(workspaceRoot));

        var extraRoots     = NormalizeAdditionalWriteRoots(additionalWriteRoots, workspaceRoot, sandboxHome);
        var readRoots      = NormalizeAdditionalReadRoots(additionalReadRoots, workspaceRoot, sandboxHome);
        var baselineReads  = NormalizeBaselineRoots(baselineReadRoots);
        var baselineDenies = NormalizeBaselineRoots(baselineDenyReadPaths);
        var tempRoots      = ResolveTempRoots(targetEnvironment);
        RequireTempRootsOutsideCredentials(tempRoots, sandboxHome, nameof(targetEnvironment));
        var profileValue = SandboxNetworkPolicyParser.ToProfileValue(network);
        return kind switch
        {
            SandboxPolicyKind.ReadOnly => new SandboxIsolationPolicy
            {
                AllowTcpLoopback = allowTcpLoopback,
                Kind             = kind,
                WorkspaceRoot    = workspaceRoot,
                SpawnProfile = new SandboxPermissionProfile
                {
                    FileSystem = new SandboxFileSystemRestriction
                    {
                        // 显式声明的写根是 read-only 策略唯一携带的写能力。
                        Entries =
                        [
                            SandboxFileSystemEntry.Read(SandboxPolicyPath.Root()),
                            .. extraRoots.Select(root =>
                                                     SandboxFileSystemEntry.Write(SandboxPolicyPath.Directory(root))),
                            .. readRoots.Select(root =>
                                                    SandboxFileSystemEntry.Read(SandboxPolicyPath.Directory(root))),
                            .. baselineReads.Select(root =>
                                                        SandboxFileSystemEntry.Read(SandboxPolicyPath.Directory(root)))
                        ]
                    },
                    Network = profileValue
                },
                NetworkEnabled      = network == SandboxNetworkPolicy.Enabled,
                EffectiveWriteRoots = extraRoots,
                EffectiveReadRoots  = readRoots,
                BaselineReadRoots   = baselineReads,
                DenyReadPaths       = baselineDenies,
                DenyWritePaths      = [],
                TempWriteRoots      = tempRoots
            },
            SandboxPolicyKind.WorkspaceWrite => new SandboxIsolationPolicy
            {
                AllowTcpLoopback = allowTcpLoopback,
                Kind             = kind,
                WorkspaceRoot    = workspaceRoot,
                SpawnProfile = new SandboxPermissionProfile
                {
                    FileSystem = new SandboxFileSystemRestriction
                    {
                        Entries =
                        [
                            .. BuildWorkspaceWriteEntries(),
                            .. extraRoots.Select(root =>
                                                     SandboxFileSystemEntry.Write(SandboxPolicyPath.Directory(root))),
                            .. readRoots.Select(root =>
                                                    SandboxFileSystemEntry.Read(SandboxPolicyPath.Directory(root))),
                            .. baselineReads.Select(root =>
                                                        SandboxFileSystemEntry.Read(SandboxPolicyPath.Directory(root)))
                        ]
                    },
                    Network = profileValue
                },
                NetworkEnabled      = network == SandboxNetworkPolicy.Enabled,
                EffectiveWriteRoots = SandboxIsolationPolicy.Deduplicate([workspaceRoot, .. tempRoots, .. extraRoots]),
                EffectiveReadRoots  = readRoots,
                BaselineReadRoots   = baselineReads,
                DenyReadPaths       = baselineDenies,
                DenyWritePaths = SandboxIsolationPolicy.ProtectedMetadataSubpaths
                                                       .Select(subpath => Path.Combine(workspaceRoot, subpath))
                                                       .ToArray(),
                TempWriteRoots = tempRoots
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown sandbox policy kind.")
        };
    }

    /// <summary>
    ///     <para>
    ///         校验并规范化额外写根：必须全限定绝对路径；与工作区根规范相同的条目被丢弃
    ///         （workspace-write 已由 project_roots 覆盖）；去重保持顺序。提供 sandboxHome 时，
    ///         任一写根覆盖凭据目录（home 的 .sandbox-secrets）或落入其中都抛 ArgumentException——
    ///         写根授予真实写入，会把 DPAPI 凭据暴露给沙箱账户组，必须 fail closed。组合器
    ///         （WindowsSandboxComposer）复用此规范化结果，对可信路径做可写根覆盖校验。
    ///     </para>
    ///     <para>
    ///         Validates and normalizes additional write roots: each must be a fully
    ///         qualified absolute path; entries identical to the workspace root are
    ///         dropped (workspace-write already covers them via project_roots);
    ///         deduplication preserves order. When sandboxHome is supplied, a write
    ///         root that covers the home's .sandbox-secrets credentials directory or
    ///         lies inside it throws ArgumentException — write roots grant real
    ///         filesystem writes and would expose the DPAPI-protected credentials to
    ///         the sandbox account group, so this fails closed. The composer
    ///         (WindowsSandboxComposer) reuses this normalization to validate trusted
    ///         paths against the write roots.
    ///     </para>
    /// </summary>
    internal static IReadOnlyList<string> NormalizeAdditionalWriteRoots(
        IReadOnlyList<string>? additionalWriteRoots, string workspaceRoot, string? sandboxHome = null)
    {
        if (additionalWriteRoots is not { Count: > 0 }) return [];

        var workspaceKey = SandboxCapabilitySidStore.CanonicalRootKey(workspaceRoot);
        var roots        = new List<string>();
        foreach (var root in additionalWriteRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                throw new
                    ArgumentException($"An additional sandbox write root must be a fully qualified absolute path: '{root}'.",
                                      nameof(additionalWriteRoots));

            var full = Path.GetFullPath(root);
            if (!string.IsNullOrWhiteSpace(sandboxHome) && TouchesSandboxCredentials(full, sandboxHome))
                throw new
                    ArgumentException($"An additional sandbox write root '{full}' covers or lies inside the sandbox credentials directory " +
                                      $"of '{sandboxHome}'; it would expose the DPAPI-protected sandbox credentials.",
                                      nameof(additionalWriteRoots));

            if (SandboxCapabilitySidStore.CanonicalRootKey(full)
                                         .Equals(workspaceKey, StringComparison.Ordinal)) continue;

            if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
        }

        return roots;
    }

    /// <summary>
    ///     <para>
    ///         校验并规范化额外只读根：必须全限定绝对路径；与工作区根相同的条目被丢弃（root 读
    ///         已覆盖）；去重保持顺序。提供 sandboxHome 时，任一只读根覆盖凭据目录（home 的
    ///         .sandbox-secrets）或落入其中都抛 ArgumentException——它会把 DPAPI 凭据暴露给
    ///         沙箱账户组，必须 fail closed；生产路径（策略规划器）始终提供 sandboxHome。
    ///     </para>
    ///     <para>
    ///         Validates and normalizes additional read roots: each must be a fully
    ///         qualified absolute path; entries identical to the workspace root are
    ///         dropped (the root read already covers them); deduplication preserves
    ///         order. When sandboxHome is supplied, a read root that covers the home's
    ///         .sandbox-secrets credentials directory or lies inside it throws
    ///         ArgumentException — it would expose the DPAPI-protected credentials to
    ///         the sandbox account group, so this fails closed; the production path
    ///         (the policy planner) always supplies the sandbox home.
    ///     </para>
    /// </summary>
    internal static IReadOnlyList<string> NormalizeAdditionalReadRoots(
        IReadOnlyList<string>? additionalReadRoots, string workspaceRoot, string? sandboxHome)
    {
        if (additionalReadRoots is not { Count: > 0 }) return [];

        var workspaceKey = SandboxCapabilitySidStore.CanonicalRootKey(workspaceRoot);
        var roots        = new List<string>();
        foreach (var root in additionalReadRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                throw new
                    ArgumentException($"An additional sandbox read root must be a fully qualified absolute path: '{root}'.",
                                      nameof(additionalReadRoots));

            var full = Path.GetFullPath(root);
            if (!string.IsNullOrWhiteSpace(sandboxHome) && TouchesSandboxCredentials(full, sandboxHome))
                throw new
                    ArgumentException($"An additional sandbox read root '{full}' covers or lies inside the sandbox credentials directory " +
                                      $"of '{sandboxHome}'; it would expose the DPAPI-protected sandbox credentials.",
                                      nameof(additionalReadRoots));

            if (SandboxCapabilitySidStore.CanonicalRootKey(full)
                                         .Equals(workspaceKey, StringComparison.Ordinal)) continue;

            if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
        }

        return roots;
    }

    /// <summary>
    ///     <para>
    ///         规范化基线广泛读根/deny-read 保护集（GAP-1）：绝对化、去空白、去重（大小写不敏感）、
    ///         保持顺序。形态校验（绝对、非盘根、配对）由组合器 fail-closed 把守，这里只做规范化；
    ///         与用户附加根不同，本集不做对凭据目录的重叠校验。
    ///     </para>
    ///     <para>
    ///         Normalizes the baseline broad-read root / deny-read protection set (GAP-1):
    ///         absolutized, trimmed, deduplicated case-insensitively, order-preserving. Shape
    ///         validation (absolute, no filesystem roots, pairing) stays guarded fail-closed by the
    ///         composer; this only normalizes. Unlike user-declared additional roots, this set skips
    ///         the credentials-overlap validation.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<string> NormalizeBaselineRoots(IReadOnlyList<string>? roots)
    {
        if (roots is null || roots.Count == 0) return [];

        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;

            var full = Path.GetFullPath(root);
            if (seen.Add(full)) results.Add(full);
        }

        return results;
    }

    /// <summary>
    ///     <para>
    ///         判断可信根（写根或只读根）与凭据目录（home\.sandbox-secrets）是否重叠：任一方向
    ///         包含或相等都算重叠，覆盖 home 本身的根必然覆盖凭据目录。词法比较之外，Windows 上
    ///         还把两侧解析为文件系统最终落点（展开 8.3 短名与 junction/symlink）再次比对——
    ///         任一形态重叠都拒绝；路径不存在或解析失败时回落词法路径继续校验，绝不跳过。
    ///         可信附加根解析与 grant 根校验（<see cref="WindowsSandboxComposer.ResolveGrantScope" />）共用。
    ///     </para>
    ///     <para>
    ///         Tests whether a trusted root (write or read) overlaps the credentials
    ///         directory (home\.sandbox-secrets): containment or equality in either
    ///         direction overlaps, and a root covering the home itself necessarily
    ///         covers the credentials directory. Beyond the lexical comparison,
    ///         Windows also resolves both sides to their final filesystem locations
    ///         (expanding 8.3 short names and junctions/symlinks) and compares again —
    ///         overlap in any form is refused; a missing path or failed resolution
    ///         falls back to the lexical path and keeps validating, never skipping.
    ///     </para>
    /// </summary>
    internal static bool TouchesSandboxCredentials(string trustedRoot, string sandboxHome)
    {
        var credentialsDirectory = Path.Combine(sandboxHome, ".sandbox-secrets");
        // 词法比对：声明形态之间的包含或相等即重叠。
        if (Overlaps(trustedRoot, credentialsDirectory)) return true;

        if (!OperatingSystem.IsWindows()) return false;

        // Windows 上词法前缀比较可被 8.3 短名与 junction/symlink 绕过；把两侧解析为最终落点后，
        // 词法与最终路径共四种形态两两比对，任一重叠都拒绝。解析失败回落词法路径继续校验。
        var rootFinal        = SandboxFinalPathResolver.TryGetFinalPath(trustedRoot);
        var credentialsFinal = SandboxFinalPathResolver.TryGetFinalPath(credentialsDirectory);
        return (rootFinal, credentialsFinal) switch
        {
            (null, null)            => false,
            ({ } root, null)        => Overlaps(root, credentialsDirectory),
            (null, { } credentials) => Overlaps(trustedRoot, credentials),
            ({ } root, { } credentials) => Overlaps(root, credentials)          ||
                                           Overlaps(root, credentialsDirectory) ||
                                           Overlaps(trustedRoot, credentials)
        };
    }

    /// <summary>
    ///     <para>两路径任一方向包含或相等都算重叠。</para>
    ///     <para>Two paths overlap on containment or equality in either direction.</para>
    /// </summary>
    private static bool Overlaps(string left, string right) =>
        Workspace.IsInside(left, right) || Workspace.IsInside(right, left);

    /// <summary>
    ///     <para>
    ///         与内置 workspace_write 一致的 entries：root 读、project_roots/slash_tmp/tmpdir 写、
    ///         四个元数据子目录只读且缺失跳过。
    ///     </para>
    ///     <para>
    ///         Entries matching the built-in workspace_write: root read;
    ///         project_roots/slash_tmp/tmpdir write; the four metadata subdirectories
    ///         read-only with skip-if-missing.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<SandboxFileSystemEntry> BuildWorkspaceWriteEntries()
    {
        var entries = new List<SandboxFileSystemEntry>
        {
            SandboxFileSystemEntry.Read(SandboxPolicyPath.Root()),
            SandboxFileSystemEntry.Write(SandboxPolicyPath.ProjectRoots()),
            SandboxFileSystemEntry.Write(SandboxPolicyPath.SlashTmp()),
            SandboxFileSystemEntry.Write(SandboxPolicyPath.TmpDir())
        };
        entries.AddRange(SandboxIsolationPolicy.ProtectedMetadataSubpaths.Select(subpath =>
                                      SandboxFileSystemEntry.Read(SandboxPolicyPath.ProjectRootsSubpath(subpath),
                                                                  true)));
        return entries;
    }

    /// <summary>
    ///     <para>
    ///         从目标环境取绝对 TEMP/TMP（大小写不敏感、保持环境块顺序）；缺失、盘符相对
    ///         （如 C:temp）或相对的值被忽略，避免解析到宿主当前目录。
    ///     </para>
    ///     <para>
    ///         Reads absolute TEMP/TMP values from the target environment
    ///         (case-insensitive, preserving environment order); missing,
    ///         drive-relative (e.g. C:temp), or relative values are ignored so they
    ///         never resolve against the host's current directory.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<string> ResolveTempRoots(IReadOnlyDictionary<string, string> targetEnvironment)
    {
        var roots = new List<string>();
        foreach (var (name, value) in targetEnvironment)
        {
            if (!name.Equals("TEMP", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("TMP", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!Path.IsPathFullyQualified(value)) continue;

            var full = Path.GetFullPath(value);
            if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
        }

        return roots;
    }

    /// <summary>
    ///     <para>
    ///         临时写根对凭据目录的重叠校验（单一拦截点）：提供 sandboxHome 时，任一 temp 根覆盖
    ///         凭据目录（home 的 .sandbox-secrets）或落入其中都抛 ArgumentException——temp 根经
    ///         EffectiveWriteRoots 授予真实写入，凭据目录绝不可作为沙箱可写根，否则会把 DPAPI 凭据
    ///         暴露给沙箱账户组。可信 tempRoot 设置与可信 extraEnvironment 注入的 TEMP/TMP 都在此拦截。
    ///     </para>
    ///     <para>
    ///         The temp write root overlap validation against the credentials directory
    ///         (the single interception point): with a sandboxHome supplied, any temp
    ///         root covering the home's .sandbox-secrets or lying inside it throws
    ///         ArgumentException — temp roots grant real writes through
    ///         EffectiveWriteRoots and the credentials directory must never be a
    ///         sandbox writable root, or the DPAPI credentials would be exposed to the
    ///         sandbox account group. Both the trusted tempRoot setting and TEMP/TMP
    ///         injected through trusted extraEnvironment are intercepted here.
    ///     </para>
    /// </summary>
    private static void RequireTempRootsOutsideCredentials(
        IReadOnlyList<string> tempRoots, string? sandboxHome, string targetEnvironmentParameterName)
    {
        if (string.IsNullOrWhiteSpace(sandboxHome)) return;

        foreach (var root in tempRoots)
            if (TouchesSandboxCredentials(root, sandboxHome))
                throw new
                    ArgumentException($"A sandbox temp write root '{root}' covers or lies inside the sandbox credentials directory " +
                                      $"of '{sandboxHome}'; the credentials directory must never be a sandbox writable root: " +
                                      "it would expose the DPAPI-protected sandbox credentials.",
                                      targetEnvironmentParameterName);
    }
}

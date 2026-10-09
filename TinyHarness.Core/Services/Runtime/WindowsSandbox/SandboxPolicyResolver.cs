using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     把固定策略种类解析为有效隔离策略：绑定工作区根，从清理后的目标环境推导临时写根
///     （绝不直接用宿主 TEMP），并派生与 Codex 内置 workspace_write 语义一致的 spawn profile
///     （root 读、project_roots 写、tmpdir 写、元数据目录只读保护）。
///     Resolves a fixed policy kind into the effective isolation policy: binds
///     the workspace root, derives temp write roots from the cleaned target
///     environment (never the host TEMP directly), and produces a spawn profile
///     matching the built-in Codex workspace_write semantics (root read,
///     project_roots write, tmpdir write, read-only metadata protection).
/// </summary>
public static class SandboxPolicyResolver
{
    /// <summary>
    ///     解析策略。workspaceRoot 必须是完全限定的绝对路径（否则抛 ArgumentException）；
    ///     env 是将冻结给目标命令的环境（从中取 TEMP/TMP）；additionalWriteRoots 是可信设置声明的
    ///     额外写根（绝对路径；read-only 策略下这是声明必要临时写根的唯一机制）。
    ///     Resolves a policy. workspaceRoot must be a fully qualified absolute
    ///     path (otherwise ArgumentException is thrown); env is the environment to
    ///     be frozen for the target command (TEMP/TMP are read from it);
    ///     additionalWriteRoots are extra write roots declared by trusted settings
    ///     (absolute paths; under the read-only policy this is the only mechanism
    ///     to declare necessary write roots).
    /// </summary>
    public static SandboxIsolationPolicy Resolve(SandboxPolicyKind                   kind,
                                                 string                              workspaceRoot,
                                                 IReadOnlyDictionary<string, string> targetEnvironment,
                                                 IReadOnlyList<string>?              additionalWriteRoots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(targetEnvironment);

        if (!Path.IsPathFullyQualified(workspaceRoot))
            throw new ArgumentException(
                                        $"The workspace root must be a fully qualified absolute path: '{workspaceRoot}'.",
                                        nameof(workspaceRoot));

        var extraRoots = NormalizeAdditionalWriteRoots(additionalWriteRoots, workspaceRoot);
        var tempRoots  = ResolveTempRoots(targetEnvironment);
        return kind switch
        {
            SandboxPolicyKind.ReadOnly => new SandboxIsolationPolicy
            {
                Kind          = kind,
                WorkspaceRoot = workspaceRoot,
                SpawnProfile = new SandboxPermissionProfile
                {
                    FileSystem = new SandboxFileSystemRestriction
                    {
                        // Explicitly declared write roots are the only write
                        // capability a read-only policy ever carries.
                        Entries =
                        [
                            SandboxFileSystemEntry.Read(SandboxPolicyPath.Root()),
                            .. extraRoots.Select(root =>
                                                     SandboxFileSystemEntry.Write(SandboxPolicyPath.Directory(root)))
                        ]
                    },
                    Network = "restricted"
                },
                EffectiveWriteRoots = extraRoots,
                DenyWritePaths      = [],
                TempWriteRoots      = tempRoots
            },
            SandboxPolicyKind.WorkspaceWrite => new SandboxIsolationPolicy
            {
                Kind          = kind,
                WorkspaceRoot = workspaceRoot,
                SpawnProfile = new SandboxPermissionProfile
                {
                    FileSystem = new SandboxFileSystemRestriction
                    {
                        Entries =
                        [
                            .. BuildWorkspaceWriteEntries(),
                            .. extraRoots.Select(root =>
                                                     SandboxFileSystemEntry.Write(SandboxPolicyPath.Directory(root)))
                        ]
                    },
                    Network = "restricted"
                },
                EffectiveWriteRoots = SandboxIsolationPolicy.Deduplicate([workspaceRoot, .. tempRoots, .. extraRoots]),
                DenyWritePaths = SandboxIsolationPolicy.ProtectedMetadataSubpaths
                                                       .Select(subpath => Path.Combine(workspaceRoot, subpath))
                                                       .ToArray(),
                TempWriteRoots = tempRoots
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown sandbox policy kind.")
        };
    }

    /// <summary>
    ///     校验并规范化额外写根：必须全限定绝对路径；与工作区根规范相同的条目被丢弃
    ///     （workspace-write 已由 project_roots 覆盖）；去重保持顺序。组合器
    ///     （WindowsSandboxComposer）复用此规范化结果，对可信路径做可写根覆盖校验。
    ///     Validates and normalizes additional write roots: each must be a fully
    ///     qualified absolute path; entries identical to the workspace root are
    ///     dropped (workspace-write already covers them via project_roots);
    ///     deduplication preserves order. The composer (WindowsSandboxComposer)
    ///     reuses this normalization to validate trusted paths against the write
    ///     roots.
    /// </summary>
    internal static IReadOnlyList<string> NormalizeAdditionalWriteRoots(
        IReadOnlyList<string>? additionalWriteRoots, string workspaceRoot)
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
            if (SandboxCapabilitySidStore.CanonicalRootKey(full)
                                         .Equals(workspaceKey, StringComparison.Ordinal)) continue;

            if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
        }

        return roots;
    }

    /// <summary>
    ///     与内置 workspace_write 一致的 entries：root 读、project_roots/slash_tmp/tmpdir 写、
    ///     四个元数据子目录只读且缺失跳过。
    ///     Entries matching the built-in workspace_write: root read;
    ///     project_roots/slash_tmp/tmpdir write; the four metadata subdirectories
    ///     read-only with skip-if-missing.
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
    ///     从目标环境取绝对 TEMP/TMP（大小写不敏感、保持环境块顺序）；缺失、盘符相对
    ///     （如 C:temp）或相对的值被忽略，避免解析到宿主当前目录。
    ///     Reads absolute TEMP/TMP values from the target environment
    ///     (case-insensitive, preserving environment order); missing,
    ///     drive-relative (e.g. C:temp), or relative values are ignored so they
    ///     never resolve against the host's current directory.
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
}

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
/// 已解析的有效隔离策略：工作区根、spawn profile、有效写入根（含临时写根）与
/// deny-write 元数据保护。同一实例供权限审批与执行消费；由它派生 setup refresh payload，
/// 保证 ACL 与 profile 一致。注意：record 相等性对 IReadOnlyList 成员退化为引用相等，
/// 当前无消费方；未来做桌面/授权缓存时不可直接用 Equals（对照设计文档 Step 4a 第 4 条）。
///
/// The resolved effective isolation policy: the workspace root, the spawn
/// profile, the effective write roots (including temp roots), and the
/// deny-write metadata protection. Approval and execution consume the same
/// frozen instance; setup refresh payloads derive from it so ACLs stay
/// consistent with the profile. Note: record equality degrades to reference
/// equality for IReadOnlyList members and nothing consumes it today; future
/// desktop/authorization caching must not rely on Equals directly (see
/// design document Step 4a, item 4).
/// </summary>
public sealed record SandboxIsolationPolicy
{
    /// <summary>约定受保护的元数据子目录；WorkspaceWrite 下保持只读。
    /// Conventionally protected metadata subdirectories; read-only under WorkspaceWrite.</summary>
    public static readonly IReadOnlyList<string> ProtectedMetadataSubpaths = [".git", ".agents", ".codex", ".aws"];

    public required SandboxPolicyKind Kind { get; init; }

    public required string WorkspaceRoot { get; init; }

    public required SandboxPermissionProfile SpawnProfile { get; init; }

    /// <summary>
    /// 有效写入根（绝对路径，含临时写根）；ReadOnly 策略为空。用于 capability SID 选择与
    /// setup 的 write_roots。
    /// The effective write roots (absolute, including temp roots); empty under
    /// the ReadOnly policy. Feeds capability-SID selection and setup write_roots.
    /// </summary>
    public required IReadOnlyList<string> EffectiveWriteRoots { get; init; }

    /// <summary>
    /// 保持只读的受保护路径（元数据目录）；setup 以 deny_write_paths 施加。
    /// Protected read-only paths (metadata directories); applied by setup as deny_write_paths.
    /// </summary>
    public required IReadOnlyList<string> DenyWritePaths { get; init; }

    /// <summary>
    /// 临时写根（来自清理后的目标环境 TEMP/TMP，绝不直接使用宿主 TEMP）。
    /// The temp write roots (from the cleaned target environment's TEMP/TMP,
    /// never the host TEMP directly).
    /// </summary>
    public required IReadOnlyList<string> TempWriteRoots { get; init; }

    public bool UsesWriteCapabilities => EffectiveWriteRoots.Count > 0;

    /// <summary>
    /// 构造每命令 refresh payload：read_roots 覆盖工作区与命令 cwd；write_roots 为有效
    /// 写入根；deny-write 保护元数据目录；proxy 端口留空（固定断网）。
    ///
    /// Builds the per-command refresh payload: read_roots cover the workspace
    /// and the command cwd; write_roots are the effective write roots;
    /// deny-write protects metadata directories; proxy ports stay empty (fixed
    /// offline policy).
    /// </summary>
    public SandboxSetupPayload CreateSetupPayload(WindowsSandboxComponents components,
                                                  string                  commandWorkingDirectory,
                                                  string                  realUser,
                                                  bool                    refreshOnly)
    {
        var readRoots = new List<string> { WorkspaceRoot, commandWorkingDirectory };
        readRoots.AddRange(TempWriteRoots);
        return new SandboxSetupPayload
        {
            OfflineUsername          = components.OfflineUsername,
            OnlineUsername           = components.OnlineUsername,
            SandboxHome              = components.SandboxHome,
            CommandWorkingDirectory  = commandWorkingDirectory,
            ReadRoots                = Deduplicate(readRoots),
            WriteRoots               = Deduplicate(EffectiveWriteRoots),
            DenyWritePaths           = Deduplicate(DenyWritePaths),
            RealUser                 = realUser,
            RefreshOnly              = refreshOnly,
        };
    }

    /// <summary>
    /// 路径去重（大小写不敏感、保持顺序）；策略解析与 refresh payload 构造共用一份实现。
    ///
    /// Path deduplication (case-insensitive, order-preserving); a single
    /// implementation shared by policy resolution and refresh payload
    /// construction.
    /// </summary>
    internal static IReadOnlyList<string> Deduplicate(IEnumerable<string> paths)
    {
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var path in paths)
        {
            if (seen.Add(path))
            {
                result.Add(path);
            }
        }

        return result;
    }
}

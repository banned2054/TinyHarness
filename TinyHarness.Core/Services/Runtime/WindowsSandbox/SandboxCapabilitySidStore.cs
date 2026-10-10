using System.Security.Cryptography;
using System.Text.Json;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     capability SID 表（&lt;home&gt;\cap_sid）的 get-or-create 语义：只读命令取 readonly SID；
///     可写命令按 Rust 契约 <c>workspace_write_cap_sid_for_root</c>（cap.rs）逐根取 SID——写根等于
///     命令 cwd 时取 <c>workspace_by_cwd</c> 表（正是 setup 刷到该目录 ACL 上的 SID），其余写根取
///     <c>writable_root_by_path</c> 表；缺失则生成 S-1-5-21-{4 个随机 u32} 并整体写回。setup 的
///     ACL refresh 与 spawn token 用同一份推导，两侧 SID 必须一致，否则受限 token 缺目录 ACE 对应
///     的 SID，写入会被拒。
///     Get-or-create semantics for the capability-SID table (&lt;home&gt;\cap_sid):
///     read-only commands take the readonly SID; writable commands follow the
///     Rust contract <c>workspace_write_cap_sid_for_root</c> (cap.rs) per root —
///     a write root equal to the command cwd takes the <c>workspace_by_cwd</c>
///     table (exactly the SID setup grants on that directory's ACL), every
///     other root takes <c>writable_root_by_path</c>; missing entries generate
///     S-1-5-21-{four random u32} and rewrite the file. Setup's ACL refresh and
///     the spawn token share this derivation — both sides must agree, or the
///     restricted token lacks the SID of the directory's ACE and writes are
///     denied.
/// </summary>
public sealed class SandboxCapabilitySidStore(WindowsSandboxComponents components)
{
    public WindowsSandboxComponents Components { get; } = components;

    /// <summary>
    ///     按策略解析 capability SID 列表；有写入根时逐根 get-or-create（选表规则见类注释）。
    ///     返回列表的首个 SID 同时用于 NUL 设备 allow ACE。commandWorkingDirectory 必须与
    ///     refresh payload 的 command_cwd 一致。
    ///     Resolves the capability-SID list for a policy, get-or-creating per
    ///     write root (table choice per the class comment). The first returned
    ///     SID is also used for the NUL device allow ACE. commandWorkingDirectory
    ///     must equal the refresh payload's command_cwd.
    /// </summary>
    public async Task<IReadOnlyList<string>> ResolveForPolicy(
        SandboxIsolationPolicy policy, string commandWorkingDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandWorkingDirectory);

        var capabilitySids = await LoadOrCreate(cancellationToken).ConfigureAwait(false);
        if (!policy.UsesWriteCapabilities) return [capabilitySids.ReadOnly];

        if (policy.EffectiveWriteRoots.Count == 0)
            throw new
                InvalidOperationException("The workspace-write sandbox policy has no writable root capability SIDs.");

        var commandCwdKey = CanonicalRootKey(commandWorkingDirectory);
        var resolved      = new List<string>();
        foreach (var writeRoot in policy.EffectiveWriteRoots)
        {
            var key  = CanonicalRootKey(writeRoot);
            // cap.rs workspace_write_cap_sid_for_root: root == cwd → the
            // per-cwd workspace SID (the ACE setup grants on that directory);
            // any other root → the per-root writable table.
            var table = key == commandCwdKey ? capabilitySids.WorkspaceByCwd : capabilitySids.WritableRootByPath;
            if (!table.TryGetValue(key, out var sid))
            {
                sid          = GenerateCapabilitySid();
                table[key]   = sid;
                await SaveAsync(capabilitySids, cancellationToken).ConfigureAwait(false);
            }

            resolved.Add(sid);
        }

        return resolved;
    }

    /// <summary>
    ///     读取表；文件缺失时创建仅含新 workspace/readonly SID 的表并写回。旧格式（纯 SID 文本）
    ///     与损坏内容一律拒绝——版本管理属于 provisioning，不在执行路径迁移。
    ///     Loads the table, creating and persisting one with fresh
    ///     workspace/readonly SIDs when the file is missing. Legacy content (plain
    ///     SID text) and corrupt JSON are rejected — version handling belongs to
    ///     provisioning, not the execution path.
    /// </summary>
    public async Task<SandboxCapabilitySids> LoadOrCreate(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Components.CapabilitySidPath))
        {
            var created = new SandboxCapabilitySids
            {
                Workspace          = GenerateCapabilitySid(),
                ReadOnly           = GenerateCapabilitySid(),
                WorkspaceByCwd     = [],
                WritableRootByPath = []
            };
            await SaveAsync(created, cancellationToken).ConfigureAwait(false);
            return created;
        }

        var content = await File.ReadAllTextAsync(Components.CapabilitySidPath, cancellationToken)
                                .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException($"The capability SID table is empty: {Components.CapabilitySidPath}");

        try
        {
            var capabilitySids =
                JsonSerializer.Deserialize(content, WindowsSandboxJsonContext.Default.SandboxCapabilitySids) ??
                throw new InvalidOperationException("The capability SID table is empty.");
            ValidateEntries(capabilitySids);
            return capabilitySids;
        }
        catch (JsonException ex)
        {
            throw new
                InvalidOperationException($"The capability SID table at {Components.CapabilitySidPath} is not the expected JSON format: {ex.Message}",
                                          ex);
        }
    }

    /// <summary>
    ///     写回 capability SID 表；与 setup helper 的非原子写一致，仅由编排方串行调用。
    ///     Persists the capability-SID table; like the setup helper's own write
    ///     it is non-atomic and only called serially by the orchestrator.
    /// </summary>
    public async Task SaveAsync(SandboxCapabilitySids capabilitySids, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Components.SandboxHome);
        await File.WriteAllTextAsync(Components.CapabilitySidPath,
                                     JsonSerializer.Serialize(capabilitySids,
                                                              WindowsSandboxJsonContext.Default.SandboxCapabilitySids),
                                     cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     与 Rust setup helper 的 canonical_path_key 对齐：GetFullPath（消除尾部分隔符）后统一
    ///     正斜杠并小写，键形如 c:/users/dev/repo。已知残差：GetFullPath 不解析符号链接，而
    ///     dunce::canonicalize 会解析——含符号链接的写根两侧仍可能得到不一致的键。
    ///     Aligns with the Rust setup helper's canonical_path_key: GetFullPath
    ///     (which drops any trailing separator) followed by forward slashes and
    ///     lowercasing yields keys like c:/users/dev/repo. Known residual:
    ///     GetFullPath does not resolve symbolic links while dunce::canonicalize
    ///     does — write roots behind symlinks can still produce mismatched keys.
    /// </summary>
    internal static string CanonicalRootKey(string root)
    {
        return Path.GetFullPath(root).Replace('\\', '/').ToLowerInvariant();
    }

    /// <summary>
    ///     required 属性仍会被 JSON 显式 null 置空，后续字典查找将抛裸 NRE；此处带文件路径
    ///     提前失败。
    ///     Required members can still be assigned explicit JSON nulls, turning
    ///     later dictionary lookups into bare NREs; fail early with the file path
    ///     instead.
    /// </summary>
    private void ValidateEntries(SandboxCapabilitySids capabilitySids)
    {
        if (string.IsNullOrEmpty(capabilitySids.Workspace) ||
            string.IsNullOrEmpty(capabilitySids.ReadOnly)  ||
            capabilitySids.WorkspaceByCwd is null          ||
            capabilitySids.WritableRootByPath is null)
            throw new
                InvalidOperationException($"The capability SID table at {Components.CapabilitySidPath} is missing required entries; explicit JSON nulls are not accepted.");
    }

    internal static string GenerateCapabilitySid()
    {
        // Four random u32 sub-authorities, matching the setup helper's scheme;
        // uint formatting emits digits only, so no culture handling is needed.
        Span<byte> random = stackalloc byte[16];
        RandomNumberGenerator.Fill(random);
        return $"S-1-5-21-{BitConverter.ToUInt32(random)}-{BitConverter.ToUInt32(random[4..])}-" +
               $"{BitConverter.ToUInt32(random[8..])}-{BitConverter.ToUInt32(random[12..])}";
    }
}

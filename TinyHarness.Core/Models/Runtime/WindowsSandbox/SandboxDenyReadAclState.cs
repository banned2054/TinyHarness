using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         helper 拒读 ACL 簿记状态（<c>&lt;home&gt;\.sandbox\deny_read_acl_state.json</c>，与 fork
///         windows-sandbox-rs deny_read_state.rs 的 <c>PersistentDenyReadAclState</c> 同源 schema）：
///         主体键为 SID 字符串，条目为该主体已下发拒读的路径清单。Desktop 组合顶层读基线时读取，
///         把全部主体条目并入自己的 deny 集，防止 refresh 撤销共享主体与 home 的旧 CLI 正在依赖
///         的扫描命中 deny。
///     </para>
///     <para>
///         The helper deny-read ACL bookkeeping state
///         (<c>&lt;home&gt;\.sandbox\deny_read_acl_state.json</c>, the same schema as fork
///         windows-sandbox-rs deny_read_state.rs's <c>PersistentDenyReadAclState</c>): principal keys
///         are SID strings, and entries are the paths already denied for that principal. The Desktop
///         top-level baseline composition reads it and folds every principal's entries into its own
///         deny set, so a refresh never revokes scan-hit denies that the legacy CLI sharing the
///         principal and home still relies on.
///     </para>
/// </summary>
public sealed record SandboxDenyReadAclState
{
    /// <summary>
    ///     <para>各安全主体（SID）已下发的拒读路径清单；缺失或 null 由反序列化直接拒绝。</para>
    ///     <para>Deny paths already applied per security principal (SID); a missing or null value is rejected outright by deserialization.</para>
    /// </summary>
    [JsonPropertyName("principals")]
    [JsonRequired]
    public Dictionary<string, string[]> Principals { get; init; } = [];
}

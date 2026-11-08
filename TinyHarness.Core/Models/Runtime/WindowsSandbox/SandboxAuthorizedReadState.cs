using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         授权执行禁用状态（&lt;home&gt;\.sandbox-secrets\authorized_read_state.json）：恢复 refresh
///         失败或进程回收结果未知时持久化，后续授权执行在入口阻断；仅经后续成功的恢复刷新清除。
///         应用以真实用户写入，目录对沙箱组永久 DENY，沙箱内命令不可篡改。
///     </para>
///     <para>
///         The authorized-execution disable state
///         (&lt;home&gt;\.sandbox-secrets\authorized_read_state.json): persisted when a restore refresh
///         fails or process reclamation is unknown, blocking later authorized executions at the entry;
///         cleared only by a later successful restore refresh. The app writes it as the real user in a
///         directory permanently denied to the sandbox group, so sandboxed commands cannot tamper with it.
///     </para>
/// </summary>
public sealed record SandboxAuthorizedReadState
{
    [JsonPropertyName("disabled")]
    public bool Disabled { get; init; }

    /// <summary>禁用原因（回收未知或恢复失败的具体说明）。The disable reason (unknown reclamation or the concrete restore failure).</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    /// <summary>禁用发生时间（UTC，ISO 8601）。When the disable happened (UTC, ISO 8601).</summary>
    [JsonPropertyName("disabled_at_utc")]
    public string? DisabledAtUtc { get; init; }
}

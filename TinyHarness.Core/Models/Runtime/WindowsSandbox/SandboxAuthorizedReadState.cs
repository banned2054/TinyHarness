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
    /// <summary>授权窗口打开前预写守卫类别（宿主崩溃时遗留，持锁后按退出未知保守阻断）。</summary>
    public const string WindowOpenKind = "window_open";

    /// <summary>ACL 恢复刷新失败类别（旧授权进程已确认退出，可通过后续成功恢复刷新清除）。</summary>
    public const string RestoreFailedKind = "restore_failed";

    /// <summary>进程或整树回收未知类别（旧授权进程可能仍存活，不可仅凭 ACL 刷新解禁）。</summary>
    public const string ReclamationUncertainKind = "reclamation_uncertain";

    [JsonPropertyName("disabled")]
    [JsonRequired]
    public bool Disabled { get; init; }

    /// <summary>禁用原因（回收未知或恢复失败的具体说明）。The disable reason (unknown reclamation or the concrete restore failure).</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    /// <summary>禁用发生时间（UTC，ISO 8601）。When the disable happened (UTC, ISO 8601).</summary>
    [JsonPropertyName("disabled_at_utc")]
    public string? DisabledAtUtc { get; init; }

    /// <summary>
    ///     <para>禁用类别：<see cref="RestoreFailedKind" /> 或 <see cref="ReclamationUncertainKind" />；缺省或未知按保守不可自动解禁处理。</para>
    ///     <para>
    ///         The disable kind: <see cref="RestoreFailedKind" /> or <see cref="ReclamationUncertainKind" />;
    ///         missing or unknown values are treated conservatively as non-auto-recoverable.
    ///     </para>
    /// </summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>
    ///     <para>记录禁用时的系统开机周期标识（Windows BootId），用于判定重启后旧授权进程是否已不可能存活。</para>
    ///     <para>
    ///         The system boot identifier (Windows BootId) captured when disabled, used to determine whether a
    ///         reboot guarantees no prior authorized process can still be alive.
    ///     </para>
    /// </summary>
    [JsonPropertyName("boot_id")]
    public int? BootId { get; init; }

    /// <summary>
    ///     <para>
    ///         产生该禁用记录的规范化 <c>sandboxHome</c> 路径：固定 helper 仅按 <c>payload.codex_home</c> 读取
    ///         拒读簿记并撤销授权组上的旧授权，因此只有与 <see cref="SourceSandboxHome" /> 相同的 home 才允许
    ///         执行自动恢复刷新并清除禁用状态。
    ///     </para>
    ///     <para>
    ///         The normalized <c>sandboxHome</c> path that produced this disable record: the pinned helper reads
    ///         bookkeeping and revokes prior authorization grants only under <c>payload.codex_home</c>, so only
    ///         the matching home may auto-recover and clear this disable state.
    ///     </para>
    /// </summary>
    [JsonPropertyName("source_sandbox_home")]
    public string? SourceSandboxHome { get; init; }

    /// <summary>
    ///     <para>产生该禁用记录时原 <c>sandboxHome</c> 目录的 64 位卷序列号（Windows <c>FileIdInfo</c>）。</para>
    ///     <para>
    ///         The 64-bit volume serial number of the originating <c>sandboxHome</c> directory captured when this
    ///         disable record was written (Windows <c>FileIdInfo</c>).
    ///     </para>
    /// </summary>
    [JsonPropertyName("source_home_volume_serial")]
    public ulong? SourceHomeVolumeSerial { get; init; }

    /// <summary>
    ///     <para>产生该禁用记录时原 <c>sandboxHome</c> 目录的 128 位文件 ID（32 位十六进制字符串）。</para>
    ///     <para>
    ///         The 128-bit file identifier of the originating <c>sandboxHome</c> directory captured when this
    ///         disable record was written (32-character hexadecimal string).
    ///     </para>
    /// </summary>
    [JsonPropertyName("source_home_file_id")]
    public string? SourceHomeFileId { get; init; }
}

using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     管理入口 provisioning 的轻量审计行，追加到 &lt;home&gt;\.sandbox\tinyharness-provision.jsonl。
///     只记录结果元数据：UTC 时间戳、outcome、退出码（可空）、mode、refreshOnly、payload 的 SHA-256
///     和截断后的错误摘要。绝不记录完整 payload、账户密码或任何凭据——payload 摘要足以关联
///     root/环境语义而不泄露路径清单之外的信息。
///     One lightweight audit line for a management-entry provisioning attempt,
///     appended to &lt;home&gt;\.sandbox\tinyharness-provision.jsonl. It records
///     result metadata only: the UTC timestamp, outcome, exit code (nullable),
///     mode, refreshOnly, the payload's SHA-256, and a truncated error summary.
///     The full payload, account passwords, and credentials are never written —
///     the payload digest correlates root/environment semantics without
///     disclosing anything beyond that.
/// </summary>
public sealed record SandboxProvisionAuditRecord
{
    [JsonPropertyName("timestampUtc")]
    public required string TimestampUtc { get; init; }

    /// <summary>completed / failed / declined / not-finished。</summary>
    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; init; }

    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    [JsonPropertyName("refreshOnly")]
    public bool RefreshOnly { get; init; }

    /// <summary>payload JSON 字节的 SHA-256（大写十六进制）。</summary>
    [JsonPropertyName("payloadSha256")]
    public required string PayloadSha256 { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

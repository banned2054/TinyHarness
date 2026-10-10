using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     一次 Windows 沙箱隔离验收的完整报告：时间戳、环境（机器、OS、离线账户名——仅用户名，
///     绝不含凭据）、实际执行的策略种类、验收工作区、组件证据、逐用例结果与 limitations 清单。
///     序列化走 source generation（禁反射 JSON）；报告文件落盘在
///     &lt;sandbox home&gt;\.sandbox\tinyharness-verify-&lt;timestamp&gt;.json。
///     The complete report of one Windows sandbox isolation acceptance run:
///     timestamp, environment (machine, OS, offline account name — username
///     only, never credentials), the policy kinds actually exercised, the
///     acceptance workspace, component evidence, per-case results, and the
///     limitations list. Serialization is source generated (reflection-free
///     JSON); the report file lands in
///     &lt;sandbox home&gt;\.sandbox\tinyharness-verify-&lt;timestamp&gt;.json.
/// </summary>
public sealed record WindowsSandboxAcceptanceReport
{
    [JsonPropertyName("timestampUtc")]
    public required string TimestampUtc { get; init; }

    [JsonPropertyName("machineName")]
    public required string MachineName { get; init; }

    [JsonPropertyName("osVersion")]
    public required string OsVersion { get; init; }

    /// <summary>所选 offline 沙箱账户的用户名；密码与任何凭据绝不进入报告。</summary>
    [JsonPropertyName("offlineAccountName")]
    public required string OfflineAccountName { get; init; }

    [JsonPropertyName("policyKinds")]
    public required IReadOnlyList<string> PolicyKinds { get; init; }

    [JsonPropertyName("workspaceRoot")]
    public required string WorkspaceRoot { get; init; }

    [JsonPropertyName("components")]
    public required SandboxAcceptanceComponentSnapshot Components { get; init; }

    [JsonPropertyName("cases")]
    public required IReadOnlyList<SandboxAcceptanceCaseResult> Cases { get; init; }

    /// <summary>观测方法局限清单；报告读者据此约束结论范围。</summary>
    [JsonPropertyName("limitations")]
    public required IReadOnlyList<string> Limitations { get; init; }

    /// <summary>
    ///     报告文件的实际落盘路径（不序列化进 JSON 自身）。写出失败时为 null。
    ///     The path the report file was actually written to (not serialized into
    ///     the JSON itself). Null when writing failed.
    /// </summary>
    [JsonIgnore]
    public string? ReportPath { get; init; }
}

using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     单个验收用例的结果：稳定名称、命令摘要、期望、实际观测（退出码/超时/失败/输出摘要）、
///     判定与耗时。名称稳定供 --case 过滤与跨报告比对。
///     The result of one acceptance case: its stable name, a command summary,
///     the expectation, the actual observation (exit code / timeout / failure /
///     output summary), the verdict, and the duration. Names stay stable for
///     --case filtering and cross-report comparison.
/// </summary>
public sealed record SandboxAcceptanceCaseResult
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("command")]
    public required string CommandSummary { get; init; }

    [JsonPropertyName("expectation")]
    public required string Expectation { get; init; }

    [JsonPropertyName("actual")]
    public string? ActualSummary { get; init; }

    [JsonPropertyName("verdict")]
    public required SandboxAcceptanceVerdict Verdict { get; init; }

    /// <summary>耗时文本（"hh:mm:ss.fff"）；仅作观测记录，不是超时判据。</summary>
    [JsonPropertyName("duration")]
    public required string Duration { get; init; }

    /// <summary>补充说明（观测值、fixture 说明、失败原因细节）。</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

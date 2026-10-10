using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     单个验收用例的判定：PASS（门控期望全部满足）、FAIL（隔离失效、语义不符或 harness 损坏）、
///     INDETERMINATE（环境/组件缺失导致无法判定，不构成隔离失效证明）、SKIPPED（总期限到期未执行）。
///     The verdict of one acceptance case: PASS (every gated expectation met),
///     FAIL (isolation breach, semantic mismatch, or broken harness),
///     INDETERMINATE (a missing component or environment gap that proves
///     nothing about isolation), and SKIPPED (never ran before the overall
///     deadline).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SandboxAcceptanceVerdict>))]
public enum SandboxAcceptanceVerdict
{
    Pass,
    Fail,
    Indeterminate,
    Skipped
}

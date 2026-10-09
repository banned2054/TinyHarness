using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     runner→父进程的 exit payload。exit_code 与 timed_out 是两个独立事实：192 不代表超时，
///     只有 timed_out=true 才是 runner 判定超时；自然退出 192 时 timed_out 为 false。两属性均
///     required：缺字段的 exit 帧反序列化即失败并走协议失败路径，绝不静默为 0/false 假冒成功。
///     The runner→parent exit payload. exit_code and timed_out are independent
///     facts: 192 alone does not mean a timeout — only timed_out=true is the
///     runner's timeout verdict; a natural 192 exit keeps timed_out false. Both
///     properties are required: an exit frame missing either fails
///     deserialization and takes the protocol failure path instead of silently
///     becoming 0/false.
/// </summary>
public sealed record RunnerExitPayload
{
    [JsonPropertyName("exit_code")]
    public required int ExitCode { get; init; }

    [JsonPropertyName("timed_out")]
    public required bool TimedOut { get; init; }
}

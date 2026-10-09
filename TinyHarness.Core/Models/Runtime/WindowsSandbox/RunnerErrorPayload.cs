using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
/// runner→父进程的 error payload：runner 在读取 spawn 请求、启动子进程或写 spawn_ready
/// 阶段失败。error 之后不会有 exit 帧；命令结果不可得。
///
/// The runner→parent error payload: the runner failed while reading the spawn
/// request, spawning the child, or writing spawn_ready. No exit frame follows
/// an error; no command result is available.
/// </summary>
public sealed record RunnerErrorPayload
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>
    /// "read_spawn_request"、"spawn_child" 或 "write_spawn_ready"。
    /// Either "read_spawn_request", "spawn_child", or "write_spawn_ready".
    /// </summary>
    [JsonPropertyName("stage")]
    public required string Stage { get; init; }

    [JsonPropertyName("windows_error_code")]
    public int? WindowsErrorCode { get; init; }
}

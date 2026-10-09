using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
/// runner→父进程的 spawn_ready payload：目标命令进程已启动。
///
/// The runner→parent spawn_ready payload: the target command process started.
/// </summary>
public sealed record RunnerSpawnReady
{
    [JsonPropertyName("process_id")]
    public uint ProcessId { get; init; }
}

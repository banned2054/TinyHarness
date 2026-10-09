using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     &lt;home&gt;\cap_sid：明文 JSON 的 capability SID 表。readonly 给只读命令；
///     writable_root_by_path 按规范化写入根取 SID，缺失时由编排方生成并写回。
///     &lt;home&gt;\cap_sid: the capability-SID table stored as plain JSON. The
///     readonly SID serves read-only commands; writable_root_by_path maps each
///     normalized write root to a SID, with the orchestrator generating and
///     persisting missing entries.
/// </summary>
public sealed record SandboxCapabilitySids
{
    [JsonPropertyName("workspace")]
    public required string Workspace { get; init; }

    [JsonPropertyName("readonly")]
    public required string ReadOnly { get; init; }

    [JsonPropertyName("workspace_by_cwd")]
    public Dictionary<string, string> WorkspaceByCwd { get; init; } = [];

    [JsonPropertyName("writable_root_by_path")]
    public Dictionary<string, string> WritableRootByPath { get; init; } = [];
}

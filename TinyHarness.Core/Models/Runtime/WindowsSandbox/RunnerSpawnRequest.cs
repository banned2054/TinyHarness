using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
/// spawn_request 的 payload（IPC v6，字段 snake_case 与 runner serde 一致）。command 为
/// 完整 argv；env 是目标命令的完整环境块；codex_home 传 sandbox 子目录，real_codex_home
/// 传 home 本身；timeout_ms 为 null 时 runner 无限等待。
///
/// The spawn_request payload (IPC v6, snake_case fields matching the runner
/// serde). command is the full argv; env is the complete child environment;
/// codex_home carries the .sandbox subdirectory while real_codex_home carries
/// the home itself; a null timeout_ms makes the runner wait indefinitely.
/// </summary>
public sealed record RunnerSpawnRequest
{
    [JsonPropertyName("command")]
    public required IReadOnlyList<string> Command { get; init; }

    [JsonPropertyName("cwd")]
    public required string WorkingDirectory { get; init; }

    [JsonPropertyName("env")]
    public required Dictionary<string, string> Environment { get; init; }

    [JsonPropertyName("permission_profile")]
    public required SandboxPermissionProfile PermissionProfile { get; init; }

    [JsonPropertyName("workspace_roots")]
    public required IReadOnlyList<string> WorkspaceRoots { get; init; }

    [JsonPropertyName("codex_home")]
    public required string SandboxDirectory { get; init; }

    [JsonPropertyName("real_codex_home")]
    public required string RealSandboxHome { get; init; }

    [JsonPropertyName("cap_sids")]
    public required IReadOnlyList<string> CapabilitySids { get; init; }

    [JsonPropertyName("network_proxy_restricting_sid")]
    public string? NetworkProxyRestrictingSid { get; init; }

    /// <summary>
    /// 有限超时必须约束在 0..4294967294 毫秒；null 表示无限等待。序列化时始终出现。
    /// A finite timeout must stay within 0..4294967294 ms; null waits forever.
    /// Always present on the wire.
    /// </summary>
    [JsonPropertyName("timeout_ms")]
    public ulong? TimeoutMilliseconds { get; init; }

    [JsonPropertyName("tty")]
    public bool Tty { get; init; }

    [JsonPropertyName("stdin_open")]
    public bool StdinOpen { get; init; }

    [JsonPropertyName("private_desktop_name")]
    public string? PrivateDesktopName { get; init; }
}

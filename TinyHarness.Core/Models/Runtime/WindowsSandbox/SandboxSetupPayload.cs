using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         传给 setup helper 的 snake_case payload：严格模式版本 5，TCP 环回模式版本 6。
///         日常执行只以 refresh_only=true 在普通权限下调用；首次 provisioning 属于独立管理入口。
///     </para>
///     <para>
///         The snake_case setup-helper payload: version 5 for strict mode and 6 for TCP loopback.
///         Routine execution uses refresh_only=true at normal privilege; initial provisioning
///         belongs to the separate management entry point.
///     </para>
/// </summary>
public sealed record SandboxSetupPayload
{
    public const uint RequiredVersion = 5;

    [JsonPropertyName("version")]
    public uint Version => AllowTcpLoopback ? WindowsSandboxComponents.TcpLoopbackSetupVersion : RequiredVersion;

    [JsonPropertyName("allow_tcp_loopback")]
    public bool AllowTcpLoopback { get; init; }

    [JsonPropertyName("offline_username")]
    public required string OfflineUsername { get; init; }

    [JsonPropertyName("online_username")]
    public required string OnlineUsername { get; init; }

    [JsonPropertyName("codex_home")]
    public required string SandboxHome { get; init; }

    [JsonPropertyName("command_cwd")]
    public required string CommandWorkingDirectory { get; init; }

    [JsonPropertyName("read_roots")]
    public required IReadOnlyList<string> ReadRoots { get; init; }

    [JsonPropertyName("write_roots")]
    public required IReadOnlyList<string> WriteRoots { get; init; }

    [JsonPropertyName("deny_read_paths")]
    public IReadOnlyList<string> DenyReadPaths { get; init; } = [];

    [JsonPropertyName("deny_write_paths")]
    public IReadOnlyList<string> DenyWritePaths { get; init; } = [];

    [JsonPropertyName("proxy_ports")]
    public IReadOnlyList<ushort> ProxyPorts { get; init; } = [];

    [JsonPropertyName("allow_local_binding")]
    public bool AllowLocalBinding { get; init; }

    [JsonPropertyName("real_user")]
    public required string RealUser { get; init; }

    /// <summary>
    ///     <para>"full"（默认）、"interactive-provision"、"provision-only" 或 "read-acls-only"，kebab-case。</para>
    ///     <para>
    ///         Either "full" (default), "interactive-provision", "provision-only", or
    ///         "read-acls-only", in kebab-case.
    ///     </para>
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "full";

    [JsonPropertyName("refresh_only")]
    public bool RefreshOnly { get; init; }
}

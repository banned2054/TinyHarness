using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     传给 codex-windows-sandbox-setup.exe 的 payload（serde snake_case，version 必须 == 5）。
///     日常执行只以 refresh_only=true 调用（普通权限，不提权）；首次 provisioning 是独立
///     管理入口的职责，不在本模型的使用路径中。
///     The payload handed to codex-windows-sandbox-setup.exe (serde snake_case;
///     version must equal 5). Routine execution only calls it with
///     refresh_only=true at normal privilege; first-time provisioning belongs to
///     the separate management entry point.
/// </summary>
public sealed record SandboxSetupPayload
{
    public const uint RequiredVersion = 5;

    [JsonPropertyName("version")]
    public uint Version => RequiredVersion;

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
    ///     "full"（默认）、"interactive-provision"、"provision-only" 或 "read-acls-only"，kebab-case。
    ///     Either "full" (default), "interactive-provision", "provision-only", or
    ///     "read-acls-only", in kebab-case.
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "full";

    [JsonPropertyName("refresh_only")]
    public bool RefreshOnly { get; init; }
}

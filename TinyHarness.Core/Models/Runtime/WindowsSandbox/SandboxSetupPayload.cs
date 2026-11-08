using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         传给 setup helper 的 snake_case payload：严格模式版本 5，TCP 环回模式版本 6；
///         携带 <see cref="AuthorizedReadPaths" />（非 null）的授权执行使用版本 7。
///         日常执行只以 refresh_only=true 在普通权限下调用；首次 provisioning 属于独立管理入口。
///     </para>
///     <para>
///         The snake_case setup-helper payload: version 5 for strict mode and 6 for TCP loopback;
///         an authorized execution carrying a non-null <see cref="AuthorizedReadPaths" /> uses
///         version 7. Routine execution uses refresh_only=true at normal privilege; initial
///         provisioning belongs to the separate management entry point.
///     </para>
/// </summary>
public sealed record SandboxSetupPayload
{
    public const uint RequiredVersion = 5;

    /// <summary>
    ///     <para>
    ///         payload 协议版本：普通执行维持既有版本（旧 helper 兼容），授权执行升级为 v7。
    ///     </para>
    ///     <para>
    ///         The payload protocol version: normal executions keep the existing version (old-helper
    ///         compatible) while authorized executions move to v7.
    ///     </para>
    /// </summary>
    [JsonPropertyName("version")]
    public uint Version => AuthorizedReadPaths is not null
        ? WindowsSandboxComponents.AuthorizedSetupVersion
        : AllowTcpLoopback ? WindowsSandboxComponents.TcpLoopbackSetupVersion : RequiredVersion;

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

    /// <summary>
    ///     <para>
    ///         本次执行经授权豁免 deny 的获批读路径（规范绝对路径）。null 表示普通执行：字段不下发，
    ///         helper 不触碰授权组；空数组表示 v7 恢复 refresh：对授权组重同步全量 deny。非空时
    ///         helper 对授权组施加"全量 deny − 获批路径"。
    ///     </para>
    ///     <para>
    ///         The approved read paths exempted from the deny set for this execution through the
    ///         authorization subject (absolute, canonical). null means a normal execution: the field is
    ///         omitted and the helper never touches the authorization group; an empty array means a v7
    ///         restore refresh resyncing the authorization group's full deny. When non-empty the helper
    ///         applies "full deny minus the approved paths" to the authorization group.
    ///     </para>
    /// </summary>
    [JsonPropertyName("authorized_read_paths")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AuthorizedReadPaths { get; init; }

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

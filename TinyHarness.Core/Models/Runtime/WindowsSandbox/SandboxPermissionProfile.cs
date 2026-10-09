using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
/// spawn_request 携带的 managed 权限 profile：restricted 文件系统 entries 加网络枚举。
/// 固定断网策略下 network 恒为 "restricted"。
///
/// The managed permission profile carried by spawn_request: restricted
/// filesystem entries plus the network enum. Under the fixed offline policy
/// network is always "restricted".
/// </summary>
public sealed record SandboxPermissionProfile
{
    [JsonPropertyName("type")]
    public string Type => "managed";

    [JsonPropertyName("file_system")]
    public required SandboxFileSystemRestriction FileSystem { get; init; }

    /// <summary>
    /// "restricted" 或 "enabled"；字符串枚举，不是对象。
    /// Either "restricted" or "enabled"; a string enum, not an object.
    /// </summary>
    [JsonPropertyName("network")]
    public required string Network { get; init; }
}

/// <summary>
/// restricted 文件系统权限：entries 必需，缺失不会自动变成空数组。
///
/// Restricted filesystem permissions: entries are required and never default
/// to an empty array when missing.
/// </summary>
public sealed record SandboxFileSystemRestriction
{
    [JsonPropertyName("type")]
    public string Type => "restricted";

    [JsonPropertyName("entries")]
    public required IReadOnlyList<SandboxFileSystemEntry> Entries { get; init; }
}

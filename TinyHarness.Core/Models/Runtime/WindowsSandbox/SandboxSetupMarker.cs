using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     &lt;home&gt;\.sandbox\setup_marker.json：setup 完成标记。空文件表示未完成；version 必须
///     与 SETUP_VERSION 一致。
///     &lt;home&gt;\.sandbox\setup_marker.json: the setup completion marker. An empty
///     file means provisioning did not finish; version must match SETUP_VERSION.
/// </summary>
public sealed record SandboxSetupMarker
{
    [JsonPropertyName("version")]
    public uint Version { get; init; }

    [JsonPropertyName("offline_username")]
    public string? OfflineUsername { get; init; }

    [JsonPropertyName("online_username")]
    public string? OnlineUsername { get; init; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; init; }
}

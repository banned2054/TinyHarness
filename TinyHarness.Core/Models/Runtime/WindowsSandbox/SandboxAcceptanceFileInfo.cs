using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     验收报告中的组件文件证据：绝对路径、文件大小与 mtime。PE 版本号在报告中留空——版本权威
///     是 marker/payload/帧 JSON 里的数值（见 SandboxAcceptanceComponentSnapshot）。
///     Component-file evidence for the acceptance report: the absolute path,
///     size, and mtime. The PE version is intentionally absent — the version
///     authority is the numeric values in the marker/payload/frame JSON (see
///     SandboxAcceptanceComponentSnapshot).
/// </summary>
public sealed record SandboxAcceptanceFileInfo
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("sizeBytes")]
    public long? SizeBytes { get; init; }

    [JsonPropertyName("lastWriteTimeUtc")]
    public string? LastWriteTimeUtc { get; init; }
}

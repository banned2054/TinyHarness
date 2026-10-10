using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     验收时的组件与版本证据：setup/runner/deny-probe 文件信息、sandbox home、实际 setup marker
///     版本，以及本 build 钉死的协议硬版本（SetupVersion=5、IpcVersion=6）。PE 文件版本不是权威，
///     报告以这些数值为准。
///     Component and version evidence at acceptance time: file info for the
///     setup/runner/deny-probe executables, the sandbox home, the actual setup
///     marker version, and the hard protocol versions pinned by this build
///     (SetupVersion=5, IpcVersion=6). The PE file version is not authoritative;
///     the report relies on these numeric values instead.
/// </summary>
public sealed record SandboxAcceptanceComponentSnapshot
{
    [JsonPropertyName("setupExecutable")]
    public required SandboxAcceptanceFileInfo SetupExecutable { get; init; }

    [JsonPropertyName("runnerExecutable")]
    public required SandboxAcceptanceFileInfo RunnerExecutable { get; init; }

    /// <summary>验收实际使用的 deny-probe 副本（位于验收工作区内）；未用到时为 null。</summary>
    [JsonPropertyName("denyProbeExecutable")]
    public SandboxAcceptanceFileInfo? DenyProbeExecutable { get; init; }

    [JsonPropertyName("sandboxHome")]
    public required string SandboxHome { get; init; }

    /// <summary>&lt;home&gt;\.sandbox\setup_marker.json 中读到的 version；无法解析为 null。</summary>
    [JsonPropertyName("markerVersion")]
    public uint? MarkerVersion { get; init; }

    [JsonPropertyName("setupVersion")]
    public required int SetupVersion { get; init; }

    [JsonPropertyName("ipcVersion")]
    public required int IpcVersion { get; init; }
}

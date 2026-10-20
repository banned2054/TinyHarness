using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     <para>一次有界网络探针的原始事实；状态不直接代表隔离判定。</para>
///     <para>Raw facts from one bounded network probe; its status is not an isolation verdict.</para>
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SandboxNetworkObservation(
    [property : JsonRequired] string Transport,
    [property : JsonRequired] string Address,
    [property : JsonRequired] int    Port,
    [property : JsonRequired] string Nonce,
    [property : JsonRequired] string Status,
    [property : JsonRequired] bool   Connected   = false,
    [property : JsonRequired] bool   Sent        = false,
    string?                          SocketError = null,
    int?                             NativeError = null,
    string?                          Detail      = null);

/// <summary>
///     <para>同一受控目标的宿主前后正对照、沙箱观测及最终判定。</para>
///     <para>Host controls before and after the sandbox observation, and the verdict for one controlled target.</para>
/// </summary>
public sealed record SandboxNetworkCheck
{
    public required string                     Target          { get; init; }
    public required bool                       ExpectedAllowed { get; init; }
    public          SandboxNetworkObservation? HostBefore      { get; init; }
    public          SandboxNetworkObservation? Sandbox         { get; init; }
    public          SandboxNetworkObservation? HostAfter       { get; init; }
    public          int?                       ExitCode        { get; init; }
    public          bool                       OuterTimedOut   { get; init; }
    public required SandboxAcceptanceVerdict   Verdict         { get; init; }
    public required string                     Reason          { get; init; }
}

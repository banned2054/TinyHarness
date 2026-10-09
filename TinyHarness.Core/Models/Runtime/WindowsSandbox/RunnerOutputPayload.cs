using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     runner→父进程的 output payload：base64 原始字节加流标识，跨帧保持字节流语义。
///     The runner→parent output payload: base64 raw bytes plus the stream id;
///     frames concatenated form one byte stream.
/// </summary>
public sealed record RunnerOutputPayload
{
    [JsonPropertyName("data_b64")]
    public required string DataBase64 { get; init; }

    /// <summary>
    ///     "stdout" 或 "stderr"。
    ///     Either "stdout" or "stderr".
    /// </summary>
    [JsonPropertyName("stream")]
    public required string Stream { get; init; }
}

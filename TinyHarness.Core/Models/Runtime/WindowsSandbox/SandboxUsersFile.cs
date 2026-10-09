using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
/// &lt;home&gt;\.sandbox-secrets\sandbox_users.json：沙箱账户凭据。password 是
/// base64(DPAPI LocalMachine blob)；明文仅在启动 runner 时解出，绝不进入参数摘要、
/// 审计、异常或模型上下文。
///
/// &lt;home&gt;\.sandbox-secrets\sandbox_users.json: sandbox account credentials.
/// password is base64(DPAPI LocalMachine blob); the plaintext is decrypted
/// only to launch the runner and never reaches argument summaries, audit
/// logs, exceptions, or model context.
/// </summary>
public sealed record SandboxUsersFile
{
    public const uint RequiredVersion = 5;

    [JsonPropertyName("version")]
    public uint Version { get; init; }

    [JsonPropertyName("offline")]
    public required SandboxUserCredential Offline { get; init; }

    [JsonPropertyName("online")]
    public required SandboxUserCredential Online { get; init; }
}

/// <summary>
/// 单个沙箱账户条目：用户名加 DPAPI 保护的密码 blob（base64）。
///
/// One sandbox account entry: the username plus the base64-encoded
/// DPAPI-protected password blob.
/// </summary>
public sealed record SandboxUserCredential
{
    [JsonPropertyName("username")]
    public required string Username { get; init; }

    [JsonPropertyName("password")]
    public required string ProtectedPassword { get; init; }
}

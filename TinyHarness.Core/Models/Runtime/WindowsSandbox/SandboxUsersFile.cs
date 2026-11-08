using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     &lt;home&gt;\.sandbox-secrets\sandbox_users.json：沙箱账户凭据。password 是
///     base64(DPAPI LocalMachine blob)；明文仅在启动 runner 时解出，绝不进入参数摘要、
///     审计、异常或模型上下文。
///     &lt;home&gt;\.sandbox-secrets\sandbox_users.json: sandbox account credentials.
///     password is base64(DPAPI LocalMachine blob); the plaintext is decrypted
///     only to launch the runner and never reaches argument summaries, audit
///     logs, exceptions, or model context.
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

    /// <summary>
    ///     <para>
    ///         授权执行专用账户对（helper 协议 v7 写入；旧代凭据文件缺省 null）。两个账户都不加入
    ///         沙箱组，加入授权组（deny−获批面是其唯一生效 ACL 身份）；授权执行以它们启动 runner，
    ///         普通执行绝不使用。null 或缺失条目使授权执行在凭据读取处明确失败（fail-closed），
    ///         绝不回退普通沙箱账户。
    ///     </para>
    ///     <para>
    ///         The dedicated authorization account pair (written by helper protocol v7; null by
    ///         default in legacy-generation credential files). Neither account joins the sandbox
    ///         group; both join the authorization group (the deny-minus-approved face is their only
    ///         effective ACL identity). Authorized executions launch the runner under them and
    ///         ordinary executions never use them. A null pair or missing entry makes an authorized
    ///         execution fail explicitly at credential loading (fail-closed), never falling back to
    ///         the ordinary sandbox accounts.
    ///     </para>
    /// </summary>
    [JsonPropertyName("authorized")]
    public SandboxAuthorizedUserPair? Authorized { get; init; }

    /// <summary>
    ///     <para>
    ///         凭据文件 version 记录 provisioning 时的 helper SETUP_VERSION（Full provisioning 写入）。
    ///         当前部署代接受旧代（<see cref="RequiredVersion" />，普通执行不受影响）与授权代
    ///         （<see cref="WindowsSandboxComponents.AuthorizedSetupVersion" />，具备授权账户对）；
    ///         其余版本一律拒绝。
    ///     </para>
    ///     <para>
    ///         The credentials file's version records the provisioning-time helper SETUP_VERSION
    ///         (written by Full provisioning). The accepted deployment generations are the legacy one
    ///         (<see cref="RequiredVersion" />; normal execution unaffected) and the authorized one
    ///         (<see cref="WindowsSandboxComponents.AuthorizedSetupVersion" />, which created the
    ///         authorization account pair); every other version is rejected.
    ///     </para>
    /// </summary>
    public static bool IsAcceptableDeploymentVersion(uint version) =>
        version is RequiredVersion or WindowsSandboxComponents.AuthorizedSetupVersion;
}

/// <summary>
///     授权执行专用账户对：离线与在线两条目，语义与普通账户对的离线/在线对称。
///     The dedicated authorization account pair: offline and online entries mirroring the
///     ordinary pair's offline/online semantics.
/// </summary>
public sealed record SandboxAuthorizedUserPair
{
    [JsonPropertyName("offline")]
    public required SandboxUserCredential Offline { get; init; }

    [JsonPropertyName("online")]
    public required SandboxUserCredential Online { get; init; }
}

/// <summary>
///     单个沙箱账户条目：用户名加 DPAPI 保护的密码 blob（base64）。
///     One sandbox account entry: the username plus the base64-encoded
///     DPAPI-protected password blob.
/// </summary>
public sealed record SandboxUserCredential
{
    [JsonPropertyName("username")]
    public required string Username { get; init; }

    [JsonPropertyName("password")]
    public required string ProtectedPassword { get; init; }
}

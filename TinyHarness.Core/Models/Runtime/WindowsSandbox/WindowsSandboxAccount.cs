using System.Security.Principal;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     解出的沙箱账户身份：用户名、仅在启动 runner 时使用的明文密码，以及本机账户 SID。
///     凭据卫生要求：ToString 永远把密码脱敏为占位符，明文绝不经字符串化进入日志或摘要。
///     The resolved sandbox account identity: username, the plaintext password
///     used only to launch the runner, and the machine account SID. Credential
///     hygiene: ToString always redacts the password to a placeholder so the
///     plaintext never reaches logs or summaries through string formatting.
/// </summary>
public sealed record WindowsSandboxAccount(
    string             Username,
    string             Password,
    SecurityIdentifier AccountSid)
{
    /// <summary>
    ///     覆盖 record 默认字符串化，防止明文密码经 ToString/日志扩散。
    ///     Overrides the record's default stringification to keep the plaintext
    ///     password from leaking through ToString/logging.
    /// </summary>
    public override string ToString() => $"{nameof(WindowsSandboxAccount)} {{ {nameof(Username)} = {Username}, " +
                                         $"{nameof(Password)} = <redacted>, {nameof(AccountSid)} = {AccountSid} }}";
}

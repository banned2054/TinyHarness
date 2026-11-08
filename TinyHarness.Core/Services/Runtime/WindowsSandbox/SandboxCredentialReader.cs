using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     读取并解出沙箱账户凭据：sandbox_users.json → DPAPI(LocalMachine) 解密 → 本机账户 SID。
///     明文密码只存在于返回值中，仅供启动 runner；任何失败抛出带上下文的异常。
///     普通账户接受旧代（v5）与授权代（v7）凭据文件；授权账户额外要求 v7 代与专用授权条目，
///     任何缺失都以明确异常拒绝，绝不回退普通沙箱账户。
///     Loads and decrypts the sandbox account credentials: sandbox_users.json →
///     DPAPI (LocalMachine) decryption → the machine account SID. The plaintext
///     password exists only inside the returned record for launching the runner;
///     every failure throws with context. Ordinary accounts accept both the legacy
///     (v5) and the authorized (v7) credential generations; the authorization
///     accounts additionally require the v7 generation and the dedicated entries,
///     refusing with explicit exceptions and never falling back to the ordinary
///     sandbox accounts.
/// </summary>
public interface ISandboxCredentialSource
{
    Task<WindowsSandboxAccount> LoadOfflineAccount(CancellationToken cancellationToken = default);

    /// <summary>
    ///     <para>解出在线沙箱账户凭据；校验、DPAPI 与 SID 语义与离线账户对称。</para>
    ///     <para>
    ///         Resolves the online sandbox account credentials; validation, DPAPI, and
    ///         SID semantics mirror the offline account.
    ///     </para>
    /// </summary>
    Task<WindowsSandboxAccount> LoadOnlineAccount(CancellationToken cancellationToken = default);

    /// <summary>
    ///     <para>
    ///         解出授权执行专用离线账户凭据；要求凭据文件为授权代（v7）且专用条目存在，
    ///         否则以明确异常拒绝。
    ///     </para>
    ///     <para>
    ///         Resolves the dedicated authorization offline account credentials; requires the
    ///         v7 credential generation and the dedicated entry, refusing with an explicit
    ///         exception otherwise.
    ///     </para>
    /// </summary>
    Task<WindowsSandboxAccount> LoadAuthorizedOfflineAccount(CancellationToken cancellationToken = default);

    /// <summary>
    ///     <para>解出授权执行专用在线账户凭据；校验语义与授权离线账户对称。</para>
    ///     <para>
    ///         Resolves the dedicated authorization online account credentials; the validation
    ///         semantics mirror the authorization offline account.
    ///     </para>
    /// </summary>
    Task<WindowsSandboxAccount> LoadAuthorizedOnlineAccount(CancellationToken cancellationToken = default);
}

/// <summary>
///     <see cref="ISandboxCredentialSource" /> 的文件 + DPAPI 实现，仅限 Windows。
///     File + DPAPI implementation of <see cref="ISandboxCredentialSource" />,
///     available on Windows only.
/// </summary>
public sealed class SandboxCredentialReader(WindowsSandboxComponents components) : ISandboxCredentialSource
{
    public Task<WindowsSandboxAccount> LoadOfflineAccount(CancellationToken cancellationToken = default) =>
        LoadAccountAsync(users => users.Offline, components.OfflineUsername, "offline",
                         true, cancellationToken);

    public Task<WindowsSandboxAccount> LoadOnlineAccount(CancellationToken cancellationToken = default) =>
        LoadAccountAsync(users => users.Online, components.OnlineUsername, "online",
                         true, cancellationToken);

    public Task<WindowsSandboxAccount> LoadAuthorizedOfflineAccount(CancellationToken cancellationToken = default) =>
        LoadAccountAsync(users => users.Authorized?.Offline, components.AuthorizedOfflineUsername,
                         "authorized offline", false, cancellationToken);

    public Task<WindowsSandboxAccount> LoadAuthorizedOnlineAccount(CancellationToken cancellationToken = default) =>
        LoadAccountAsync(users => users.Authorized?.Online, components.AuthorizedOnlineUsername,
                         "authorized online", false, cancellationToken);

    /// <summary>
    ///     <para>
    ///         读取凭据文件并解出指定条目：版本、条目完整性、外来账户名拒绝与 DPAPI 解密共用一套实现。
    ///         普通账户接受旧代与授权代凭据文件；授权账户只接受授权代，且条目缺失或代际不符都在
    ///         DPAPI 之前明确失败。
    ///     </para>
    ///     <para>
    ///         Reads the credentials file and resolves one entry: version, entry
    ///         completeness, foreign-account-name refusal, and DPAPI decryption share
    ///         one implementation. Ordinary accounts accept both credential generations;
    ///         authorization accounts accept only the authorized generation, and a missing
    ///         entry or a wrong generation fails explicitly before DPAPI.
    ///     </para>
    /// </summary>
    private async Task<WindowsSandboxAccount> LoadAccountAsync(
        Func<SandboxUsersFile, SandboxUserCredential?> entrySelector, string expectedUsername, string accountKind,
        bool                                           acceptLegacyGeneration, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new
                PlatformNotSupportedException("The Windows sandbox credential reader is available on Windows only.");

        if (!File.Exists(components.UsersFilePath))
            throw new InvalidOperationException($"Sandbox account credentials not found: {components.UsersFilePath}");

        SandboxUsersFile users;
        try
        {
            var content = await File.ReadAllTextAsync(components.UsersFilePath, cancellationToken)
                                    .ConfigureAwait(false);
            users = JsonSerializer.Deserialize(content, WindowsSandboxJsonContext.Default.SandboxUsersFile) ??
                    throw new InvalidOperationException("Sandbox account credentials file is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Sandbox account credentials are not valid JSON: {ex.Message}", ex);
        }

        // 授权账户的代际门在条目选择之前：旧代（v5）凭据文件没有专用账户对，继续解密只会
        // 得到 null；以明确错误要求重新 provisioning，绝不回退普通沙箱账户。
        // The authorization generation gate sits ahead of entry selection: a legacy (v5)
        // credential file has no dedicated pair, so decryption could only yield null; an explicit
        // error demands re-provisioning instead of falling back to the ordinary sandbox accounts.
        if (acceptLegacyGeneration)
        {
            if (!SandboxUsersFile.IsAcceptableDeploymentVersion(users.Version))
                throw new
                    InvalidOperationException($"Sandbox account credentials version {users.Version} does not match the required {SandboxUsersFile.RequiredVersion} (or the authorized generation {WindowsSandboxComponents.AuthorizedSetupVersion}).");
        }
        else
        {
            if (users.Version != WindowsSandboxComponents.AuthorizedSetupVersion)
                throw new
                    InvalidOperationException($"Sandbox account credentials version {users.Version} does not carry the authorized generation {WindowsSandboxComponents.AuthorizedSetupVersion}; the {accountKind} account is unavailable until provisioning runs with the v7-capable helper. The approved read cannot fall back to the ordinary sandbox accounts.");
        }

        var entry = entrySelector(users) ??
                    throw new
                        InvalidOperationException($"Sandbox account credentials at {components.UsersFilePath} contain a null {accountKind} account entry; re-provision with a v7-capable helper to create the dedicated authorization accounts.");
        if (string.IsNullOrEmpty(entry.Username) || string.IsNullOrEmpty(entry.ProtectedPassword))
            throw new
                InvalidOperationException($"Sandbox account credentials are missing the {accountKind} account entry.");

        // Refuse foreign-namespace credentials before touching DPAPI: a legacy
        // Codex home must be re-provisioned, never reused across installs.
        if (!string.Equals(entry.Username, expectedUsername, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(WindowsSandboxComponents.IsLegacyCodexAccountName(entry.Username)
                                                    ? $"Sandbox account credentials at {components.UsersFilePath} name the legacy Codex account '{entry.Username}'; refusing to reuse Codex-release credentials. Provision an independent TinyHarness sandbox home with this fork's setup executable."
                                                    : $"Sandbox account credentials at {components.UsersFilePath} name account '{entry.Username}', which does not match this build's {accountKind} account '{expectedUsername}'.");

        string password;
        try
        {
            var blob      = Convert.FromBase64String(entry.ProtectedPassword);
            var decrypted = ProtectedData.Unprotect(blob, null, DataProtectionScope.LocalMachine);
            password = Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            throw new
                InvalidOperationException($"Failed to decrypt the {accountKind} sandbox account password: {ex.Message}",
                                          ex);
        }

        var accountSid = TranslateAccountSid(entry.Username);
        return new WindowsSandboxAccount(entry.Username, password, accountSid);
    }

    private static SecurityIdentifier TranslateAccountSid(string username)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Sandbox account SID lookup is available on Windows only.");

        try
        {
            var account = new NTAccount(Environment.MachineName, username);
            return (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            throw new
                InvalidOperationException($"Could not resolve the SID of sandbox account '{username}'; the account may not exist yet: {ex.Message}",
                                          ex);
        }
    }
}

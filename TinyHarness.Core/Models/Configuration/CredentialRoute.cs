namespace TinyHarness.Core.Models.Configuration;

/// <summary>
///     <para>
///         从 profile 名与密钥引用来源组合凭据路由标识，供会话选择记录与恢复核对使用；
///         只含引用，不含密钥值或指纹。
///     </para>
///     <para>
///         Composes a credential-route key from a profile name and key-reference source for session selection
///         records and resume reconciliation; references only, never secret values or fingerprints.
///     </para>
/// </summary>
public static class CredentialRoute
{
    /// <summary>
    ///     <para>组合凭据路由标识：profile 名与密钥引用来源，任一变化都改变标识。</para>
    ///     <para>Composes the credential route from the profile name and key-reference source; a change to either changes the route.</para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         同一 profile 名下更换密钥引用（环境变量或凭据库目标）同样视为路由变化；
    ///         未配置任何引用时使用固定的 unconfigured 来源，保持标识稳定。
    ///     </para>
    ///     <para>
    ///         Changing the key reference (environment variable or credential-store target) under one profile
    ///         name also counts as a route change; with no reference configured, a fixed "unconfigured" source
    ///         keeps the route stable.
    ///     </para>
    /// </remarks>
    public static string Describe(string? profileName, string? apiKeyEnvironmentVariable,
                                  string? apiKeyCredentialTarget)
    {
        var source = !string.IsNullOrWhiteSpace(apiKeyCredentialTarget)
            ? "credential:" + apiKeyCredentialTarget
            : !string.IsNullOrWhiteSpace(apiKeyEnvironmentVariable)
                ? "env:" + apiKeyEnvironmentVariable
                : "unconfigured";
        return $"{profileName ?? string.Empty}|{source}";
    }
}

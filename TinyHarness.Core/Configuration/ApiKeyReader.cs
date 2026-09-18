using TinyHarness.Core.Runtime;

namespace TinyHarness.Core.Configuration;

/// <summary>
/// API key 的来源类型。凭据存储条目优先于环境变量；两者都未配置时为 None。
///
/// Where an API key comes from. A credential-store entry takes precedence over the environment variable;
/// None when neither is configured.
/// </summary>
public enum ApiKeySourceKind
{
    /// <summary>未配置任何 API key 来源。No API key source is configured.</summary>
    None,

    /// <summary>来自配置的环境变量。From the configured environment variable.</summary>
    EnvironmentVariable,

    /// <summary>来自系统凭据存储条目。From a system credential-store entry.</summary>
    CredentialStore,
}

/// <summary>
/// API key 来源的可用性描述；只包含来源类型、名称与是否存在，绝不包含密钥值。
///
/// Availability description of an API key source; carries only the kind, name, and presence — never the secret value.
/// </summary>
public sealed record ApiKeyStatus(ApiKeySourceKind Kind, string SourceName, bool Available);

/// <summary>
/// 按固定优先级解析 API key：凭据存储条目 → 环境变量。只报告存在性，不在日志或消息中输出密钥值。
///
/// Resolves the API key in a fixed precedence: credential-store entry, then environment variable. Only
/// presence is reported; the secret value never appears in logs or messages.
/// </summary>
public static class ApiKeyReader
{
    /// <summary>
    /// 返回生效的 API key 来源及其可用性，不读取密钥值本身。
    /// Describes the effective API key source and whether it can produce a key, without reading the value.
    /// </summary>
    public static ApiKeyStatus Describe(string environmentVariable, string credentialTarget, ICredentialStore store)
    {
        if (!string.IsNullOrWhiteSpace(credentialTarget))
        {
            return new ApiKeyStatus(ApiKeySourceKind.CredentialStore, credentialTarget,
                                    store.IsSupported && ReadCredential(store, credentialTarget) is not null);
        }

        if (!string.IsNullOrWhiteSpace(environmentVariable))
        {
            return new ApiKeyStatus(ApiKeySourceKind.EnvironmentVariable, environmentVariable,
                                    !string.IsNullOrWhiteSpace(Environment
                                                                  .GetEnvironmentVariable(environmentVariable)));
        }

        return new ApiKeyStatus(ApiKeySourceKind.None, string.Empty, false);
    }

    /// <summary>
    /// 读取生效的 API key 值；没有可用来源时返回 <see langword="null"/>。
    /// 凭据存储已配置但无法提供密钥时抛出带修复指引的 <see cref="ConfigException"/>，不静默回退到环境变量。
    ///
    /// Reads the effective API key value; returns <see langword="null"/> when no source is available. A configured
    /// but unusable credential-store entry throws a <see cref="ConfigException"/> with fix hints instead of
    /// silently falling back to the environment variable.
    /// </summary>
    public static string? Read(string  environmentVariable, string credentialTarget, ICredentialStore store,
                               string? profileName = null)
    {
        if (!string.IsNullOrWhiteSpace(credentialTarget))
        {
            if (!store.IsSupported)
            {
                throw new ConfigException(
                                          $"The configured credential store entry '{credentialTarget}' cannot be read on this platform. " +
                                          "Set the API key through an environment variable instead, e.g. " +
                                          $"'tinyharness auth set {profileName ?? "<profile>"} --env <VARIABLE_NAME>'.");
            }

            var credential = ReadCredential(store, credentialTarget);
            if (string.IsNullOrWhiteSpace(credential))
            {
                throw new ConfigException(
                                          $"No API key found in the credential store entry '{credentialTarget}'. "  +
                                          $"Re-run 'tinyharness auth set {profileName ?? "<profile>"}' or set the " +
                                          "API key through an environment variable.");
            }

            return credential;
        }

        if (!string.IsNullOrWhiteSpace(environmentVariable))
        {
            var value = Environment.GetEnvironmentVariable(environmentVariable);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    /// <summary>
    /// 从凭据存储读取条目，读取失败视为不存在；不记录密钥值。
    /// Reads a credential-store entry, treating read failures as absence; never logs the secret value.
    /// </summary>
    private static string? ReadCredential(ICredentialStore store, string targetName)
    {
        try
        {
            return store.Read(targetName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}

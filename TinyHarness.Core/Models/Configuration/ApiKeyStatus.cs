namespace TinyHarness.Core.Models.Configuration;

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

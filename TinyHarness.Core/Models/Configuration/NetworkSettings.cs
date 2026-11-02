namespace TinyHarness.Core.Models.Configuration;

/// <summary>
///     <para>模型 HTTP 请求的实际网络出口模式；Provider 继承关系由可空覆盖字段表达。</para>
///     <para>
///         The actual network route mode for model HTTP requests; provider inheritance is represented by a nullable
///         override.
///     </para>
/// </summary>
public enum NetworkMode
{
    System,
    Direct,
    Proxy
}

/// <summary>
///     <para>代理服务使用的协议。</para>
///     <para>The protocol spoken by a proxy server.</para>
/// </summary>
public enum ProxyProtocol
{
    Http,
    Https,
    Socks5
}

/// <summary>
///     <para>指定系统代理、直连或一个已保存代理的网络路由。</para>
///     <para>Selects the system proxy, a direct connection, or one saved proxy.</para>
/// </summary>
public sealed record NetworkRoute
{
    /// <summary>实际传输模式。The actual transport mode.</summary>
    public NetworkMode Mode { get; init; } = NetworkMode.System;

    /// <summary>Mode 为 Proxy 时引用的稳定代理 ID。Stable proxy ID required when Mode is Proxy.</summary>
    public string? ProxyId { get; init; }
}

/// <summary>
///     <para>可复用代理定义；认证凭据只通过系统凭据存储引用，不进入普通配置 JSON。</para>
///     <para>
///         A reusable proxy definition; authentication secrets are referenced from the system credential store and never
///         written to ordinary config JSON.
///     </para>
/// </summary>
public sealed record ProxyDefinition
{
    /// <summary>跨编辑保持不变的代理标识。The stable proxy identifier retained across edits.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>用户可见名称。The user-visible name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>代理协议。The proxy protocol.</summary>
    public ProxyProtocol Protocol { get; init; } = ProxyProtocol.Http;

    /// <summary>代理主机名或 IP 地址，不含 scheme 和端口。Proxy host name or IP address, without scheme or port.</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>代理 TCP 端口。The proxy TCP port.</summary>
    public int Port { get; init; }

    /// <summary>可选的系统凭据存储目标；为空表示不使用代理认证。Optional credential-store target; empty means no proxy authentication.</summary>
    public string? CredentialTarget { get; init; }
}

/// <summary>
///     <para>用户级全局默认路由和可复用代理列表。</para>
///     <para>The user-level default route and reusable proxy list.</para>
/// </summary>
public sealed record NetworkSettings
{
    /// <summary>缺省使用 .NET 当前环境的系统代理。Defaults to the system proxy configured for the current .NET environment.</summary>
    public NetworkRoute DefaultRoute { get; init; } = new();

    /// <summary>所有全局和 Provider 路由可引用的代理定义。Proxy definitions available to global and provider routes.</summary>
    public IReadOnlyList<ProxyDefinition> Proxies { get; init; } = [];
}

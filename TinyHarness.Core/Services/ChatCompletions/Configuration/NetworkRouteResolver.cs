using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TinyHarness.Core.Models.Configuration;

namespace TinyHarness.Core.Services.ChatCompletions.Configuration;

/// <summary>
///     <para>把 Provider 可空覆盖与全局默认解析为一个实际传输路由。</para>
///     <para>Resolves a nullable provider override and the global default into one effective transport route.</para>
/// </summary>
public static class NetworkRouteResolver
{
    /// <summary>
    ///     <para>解析有效路由；无 Provider 覆盖时采用全局默认，缺省配置为系统代理。</para>
    ///     <para>
    ///         Resolves the effective route; providers without an override use the global default, which defaults to the
    ///         system proxy.
    ///     </para>
    /// </summary>
    public static ResolvedNetworkRoute Resolve(NetworkSettings? settings, NetworkRoute? providerOverride)
    {
        var global = settings ?? new NetworkSettings();
        NetworkSettingsValidator.Validate(global);
        NetworkSettingsValidator.ValidateProviderRoute(providerOverride, "profile.network", global);
        var route = providerOverride ?? global.DefaultRoute;
        var proxy = route.Mode == NetworkMode.Proxy
            ? global.Proxies.Single(item => item.Id == route.ProxyId)
            : null;
        return new ResolvedNetworkRoute(route.Mode, proxy);
    }

    /// <summary>
    ///     <para>生成不含地址或凭据的网络方式说明。</para>
    ///     <para>Creates a network-mode label without proxy addresses or credentials.</para>
    /// </summary>
    public static string Describe(ResolvedNetworkRoute route) => route.Mode switch
    {
        NetworkMode.System => "系统代理",
        NetworkMode.Direct => "直连",
        NetworkMode.Proxy  => $"代理 {route.Proxy?.Name ?? "?"}",
        _                  => throw new ArgumentOutOfRangeException(nameof(route), route.Mode, "Unknown network mode.")
    };

    /// <summary>
    ///     <para>生成不含密钥值的客户端绑定身份；代理地址或凭据引用变化会产生新身份。</para>
    ///     <para>
    ///         Creates a client-binding identity without secret values; proxy endpoint or credential-reference changes
    ///         produce a new identity.
    ///     </para>
    /// </summary>
    public static string BindingIdentity(ResolvedNetworkRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.Mode == NetworkMode.System) return "system";
        if (route.Mode == NetworkMode.Direct) return "direct";
        if (route.Mode != NetworkMode.Proxy || route.Proxy is not { } proxy)
            throw new InvalidDataException("The resolved proxy route is incomplete.");

        var fields = new[]
        {
            proxy.Id,
            ProxyProtocolParser.ToValueString(proxy.Protocol),
            proxy.Host,
            proxy.Port.ToString(CultureInfo.InvariantCulture),
            proxy.CredentialTarget ?? string.Empty
        };
        var payload = string.Concat(fields.Select(field => field.Length.ToString(
                                                       CultureInfo.InvariantCulture) + ":" + field));
        return "proxy:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}

/// <summary>
///     <para>不含密钥的有效传输路由快照，用于安全边界比较和界面展示。</para>
///     <para>An effective transport-route snapshot without secrets, used for safe-boundary comparisons and display.</para>
/// </summary>
public sealed record ResolvedNetworkRoute(NetworkMode Mode, ProxyDefinition? Proxy);

/// <summary>
///     <para>将网络模式映射到稳定的小写配置值。</para>
///     <para>Maps network modes to stable lowercase config values.</para>
/// </summary>
public static class NetworkModeParser
{
    public static string ToValueString(NetworkMode mode) => mode switch
    {
        NetworkMode.System => "system",
        NetworkMode.Direct => "direct",
        NetworkMode.Proxy  => "proxy",
        _                  => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown network mode.")
    };

    public static bool TryParse(string value, out NetworkMode mode)
    {
        mode = value switch
        {
            "system" => NetworkMode.System,
            "direct" => NetworkMode.Direct,
            "proxy"  => NetworkMode.Proxy,
            _        => (NetworkMode)(-1)
        };
        return Enum.IsDefined(mode);
    }
}

/// <summary>
///     <para>将代理协议映射到稳定的小写配置值。</para>
///     <para>Maps proxy protocols to stable lowercase config values.</para>
/// </summary>
public static class ProxyProtocolParser
{
    public static string ToValueString(ProxyProtocol protocol) => protocol switch
    {
        ProxyProtocol.Http => "http",
        ProxyProtocol.Https => "https",
        ProxyProtocol.Socks5 => "socks5",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "Unknown proxy protocol.")
    };

    public static bool TryParse(string value, out ProxyProtocol protocol)
    {
        protocol = value switch
        {
            "http"                => ProxyProtocol.Http,
            "https"               => ProxyProtocol.Https,
            "socks5" or "socks5h" => ProxyProtocol.Socks5,
            _                     => (ProxyProtocol)(-1)
        };
        return Enum.IsDefined(protocol);
    }
}

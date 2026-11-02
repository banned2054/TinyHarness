using System.Net;
using TinyHarness.Core.Models.Configuration;

namespace TinyHarness.Core.Services.ChatCompletions.Configuration;

/// <summary>
///     <para>校验可信网络配置的路由引用、代理地址与端口。</para>
///     <para>Validates trusted network routes, proxy references, addresses, and ports.</para>
/// </summary>
public static class NetworkSettingsValidator
{
    /// <summary>
    ///     <para>校验全局路由及代理列表；异常包含配置字段路径。</para>
    ///     <para>Validates the global route and proxy list; failures include the config field path.</para>
    /// </summary>
    public static void Validate(NetworkSettings settings, string fieldPath = "settings.network")
    {
        ArgumentNullException.ThrowIfNull(settings);

        var ids   = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < settings.Proxies.Count; i++)
        {
            var proxy     = settings.Proxies[i];
            var proxyPath = $"{fieldPath}.proxies[{i}]";
            if (string.IsNullOrWhiteSpace(proxy.Id) || proxy.Id.Length > 128 ||
                proxy.Id.Any(char.IsWhiteSpace)     || proxy.Id.Any(char.IsControl))
                throw new
                    InvalidDataException($"User config field '{proxyPath}.id' must be a stable non-empty identifier without whitespace.");
            if (!ids.Add(proxy.Id))
                throw new InvalidDataException($"User config field '{proxyPath}.id' duplicates proxy id '{proxy.Id}'.");
            if (string.IsNullOrWhiteSpace(proxy.Name) || proxy.Name.Any(char.IsControl))
                throw new
                    InvalidDataException($"User config field '{proxyPath}.name' must be a non-empty display name.");
            if (!names.Add(proxy.Name.Trim()))
                throw new
                    InvalidDataException($"User config field '{proxyPath}.name' duplicates proxy name '{proxy.Name}'.");
            if (!Enum.IsDefined(proxy.Protocol))
                throw new
                    InvalidDataException($"User config field '{proxyPath}.protocol' has an unknown protocol '{proxy.Protocol}'.");
            if (!IsValidHost(proxy.Host))
                throw new
                    InvalidDataException($"User config field '{proxyPath}.host' must be a host name or IP address.");
            if (proxy.Port is < 1 or > 65535)
                throw new InvalidDataException($"User config field '{proxyPath}.port' must be between 1 and 65535.");
            if (proxy.CredentialTarget is { Length: > 0 } target && string.IsNullOrWhiteSpace(target))
                throw new
                    InvalidDataException($"User config field '{proxyPath}.credentialTarget' must be non-empty when supplied.");
        }

        ValidateRoute(settings.DefaultRoute, $"{fieldPath}.defaultRoute", ids);
    }

    /// <summary>
    ///     <para>校验 Provider 覆盖路由；null 代表继承，Proxy 必须引用现存代理。</para>
    ///     <para>Validates a provider override; null means inherit, and Proxy must reference an existing proxy.</para>
    /// </summary>
    public static void ValidateProviderRoute(NetworkRoute? route, string fieldPath, NetworkSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);
        if (route is null) return;

        ValidateRoute(route, fieldPath, settings.Proxies.Select(proxy => proxy.Id).ToHashSet(StringComparer.Ordinal));
    }

    private static void ValidateRoute(NetworkRoute route, string fieldPath, HashSet<string> proxyIds)
    {
        if (!Enum.IsDefined(route.Mode))
            throw new
                InvalidDataException($"User config field '{fieldPath}.mode' has an unknown network mode '{route.Mode}'.");

        if (route.Mode == NetworkMode.Proxy)
        {
            if (string.IsNullOrWhiteSpace(route.ProxyId))
                throw new
                    InvalidDataException($"User config field '{fieldPath}.proxyId' is required when mode is 'proxy'.");
            if (!proxyIds.Contains(route.ProxyId))
                throw new
                    InvalidDataException($"User config field '{fieldPath}.proxyId' references missing proxy '{route.ProxyId}'.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(route.ProxyId))
            throw new
                InvalidDataException($"User config field '{fieldPath}.proxyId' is only valid when mode is 'proxy'.");
    }

    private static bool IsValidHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || !string.Equals(host, host.Trim(), StringComparison.Ordinal) ||
            host.Any(char.IsWhiteSpace)     || host.Any(char.IsControl)                                    ||
            host.IndexOfAny(['/', '\\', '@', '?', '#']) >= 0)
            return false;

        return IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) == UriHostNameType.Dns;
    }
}

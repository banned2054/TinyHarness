using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions.Configuration;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>下拉列表中的稳定代理引用和展示文本。</para>
///     <para>A stable proxy reference and its display text for selectors.</para>
/// </summary>
public sealed record NetworkProxyChoice(string Id, string Label);

/// <summary>
///     <para>Provider 网络选项；follow 只表达配置继承，不作为传输模式。</para>
///     <para>A provider network option; follow expresses configuration inheritance and is not a transport mode.</para>
/// </summary>
public sealed record ProviderNetworkChoice(string Value, string Label)
{
    public NetworkRoute? ToRoute(string? proxyId) => Value switch
    {
        "follow" => null,
        "system" => new NetworkRoute { Mode = NetworkMode.System },
        "direct" => new NetworkRoute { Mode = NetworkMode.Direct },
        "proxy" when !string.IsNullOrWhiteSpace(proxyId) =>
            new NetworkRoute { Mode = NetworkMode.Proxy, ProxyId = proxyId },
        "proxy" => null,
        _       => throw new InvalidOperationException("Unknown provider network choice.")
    };

    public override string ToString() => Label;
}

/// <summary>
///     <para>全局默认网络选项。</para>
///     <para>A global default network choice.</para>
/// </summary>
public sealed record DefaultNetworkChoice(string Value, string Label)
{
    public NetworkMode? Mode => Value switch
    {
        "system" => NetworkMode.System,
        "direct" => NetworkMode.Direct,
        "proxy"  => NetworkMode.Proxy,
        _        => null
    };

    public override string ToString() => Label;
}

internal static class NetworkProxyDisplay
{
    internal static NetworkProxyChoice ToChoice(ProxyDefinition proxy) =>
        new(proxy.Id,
            $"{proxy.Name} · {ProxyProtocolParser.ToValueString(proxy.Protocol).ToUpperInvariant()} " +
            $"{FormatHost(proxy.Host)}:{proxy.Port}");

    private static string FormatHost(string host) => host.Contains(':') ? $"[{host}]" : host;
}

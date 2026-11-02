using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Core.Services.ChatCompletions;

/// <summary>
///     <para>检测代理 TCP 端口，并测量经代理收到 HTTPS 目标响应头的耗时。</para>
///     <para>
///         Checks the proxy TCP endpoint and measures the time to receive HTTPS target response headers through the
///         proxy.
///     </para>
/// </summary>
public static class ProxyConnectivityTester
{
    /// <summary>
    ///     <para>检测指定代理，并请求用户提供的 HTTPS 目标；不会携带 Provider 凭据或自动切换出口。</para>
    ///     <para>Tests the selected proxy and a user-supplied HTTPS target without provider credentials or route fallback.</para>
    /// </summary>
    public static async Task<ProxyConnectivityTestResult> TestAsync(
        ProxyDefinition   proxy, string httpsTarget, ICredentialStore? credentials,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        if (!Uri.TryCreate(httpsTarget, UriKind.Absolute, out var target) ||
            target.Scheme != Uri.UriSchemeHttps                           || !string.IsNullOrEmpty(target.UserInfo))
            throw new
                InvalidDataException("The connectivity-test target must be an HTTPS URL without embedded credentials.");

        using var proxyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        proxyTimeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(proxy.Host, proxy.Port, proxyTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return new ProxyConnectivityTestResult(false, false, "代理 TCP 端口不可连接。");
        }

        var network = new NetworkSettings
        {
            DefaultRoute = new NetworkRoute { Mode = NetworkMode.Proxy, ProxyId = proxy.Id },
            Proxies      = [proxy]
        };
        using var client         = ProviderTransportFactory.CreateHttpClient(network, null, credentials);
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var requestStarted = Stopwatch.GetTimestamp();
            using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead,
                                                       requestTimeout.Token).ConfigureAwait(false);
            var targetResponseTime = Stopwatch.GetElapsedTime(requestStarted);
            if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                return new ProxyConnectivityTestResult(true, false, "代理认证未通过。");

            return new ProxyConnectivityTestResult(true, true, null)
            {
                TargetResponseTime = targetResponseTime
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when
            (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            return new ProxyConnectivityTestResult(true, false, "目标 HTTPS 请求未完成。");
        }
    }
}

/// <summary>
///     <para>代理端口连接状态及目标 HTTPS 响应耗时。</para>
///     <para>Proxy connection status and target HTTPS response time.</para>
/// </summary>
public sealed record ProxyConnectivityTestResult(bool ProxyTcpConnected, bool HttpsResponseReceived, string? Error)
{
    /// <summary>
    ///     <para>经代理收到 HTTPS 目标响应头所用的时间；未收到目标响应时为 null。</para>
    ///     <para>The time to receive HTTPS target response headers through the proxy; null if no target response was received.</para>
    /// </summary>
    public TimeSpan? TargetResponseTime { get; init; }
}

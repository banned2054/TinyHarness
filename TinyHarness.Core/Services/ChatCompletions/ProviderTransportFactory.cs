using System.ClientModel.Primitives;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Core.Services.ChatCompletions;

/// <summary>
///     <para>按已解析网络路由建立隔离的 HTTP 连接池；每个模型客户端持有自己的 Transport 生命周期。</para>
///     <para>Builds an isolated HTTP connection pool for a resolved route; each model client owns its transport lifetime.</para>
/// </summary>
public static class ProviderTransportFactory
{
    /// <summary>
    ///     <para>创建带独立连接池的 SDK Transport；调用方负责释放返回对象。</para>
    ///     <para>Creates an SDK transport with a dedicated connection pool; the caller owns and disposes the result.</para>
    /// </summary>
    public static ProviderHttpTransport Create(NetworkSettings?  settings, NetworkRoute? providerOverride,
                                               ICredentialStore? credentials = null)
    {
        var route  = NetworkRouteResolver.Resolve(settings, providerOverride);
        var client = CreateHttpClient(route, credentials);
        try
        {
            return new ProviderHttpTransport(route, client, new HttpClientPipelineTransport(client));
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     <para>为在线模型发现或连接检查创建遵循相同路由的 HTTP 客户端。</para>
    ///     <para>Creates an HTTP client for model discovery or connectivity checks using the same route rules.</para>
    /// </summary>
    public static HttpClient CreateHttpClient(NetworkSettings?  settings, NetworkRoute? providerOverride,
                                              ICredentialStore? credentials = null) =>
        CreateHttpClient(NetworkRouteResolver.Resolve(settings, providerOverride), credentials);

    private static HttpClient CreateHttpClient(ResolvedNetworkRoute route, ICredentialStore? credentials)
    {
        var handler = new SocketsHttpHandler();
        try
        {
            switch (route.Mode)
            {
                case NetworkMode.System :
                    // 保留 SocketsHttpHandler 默认的系统代理与环境变量解析。
                    break;
                case NetworkMode.Direct :
                    handler.UseProxy = false;
                    break;
                case NetworkMode.Proxy :
                    ConfigureProxy(handler, route.Proxy!, credentials);
                    break;
                default :
                    throw new ArgumentOutOfRangeException(nameof(route), route.Mode, "Unknown network mode.");
            }

            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }

    private static void ConfigureProxy(SocketsHttpHandler handler, ProxyDefinition proxy,
                                       ICredentialStore?  credentials)
    {
        var authentication = ReadCredentials(proxy, credentials);
        if (proxy.Protocol == ProxyProtocol.Socks5)
        {
            handler.UseProxy        = false;
            handler.ConnectCallback = (context, token) => ConnectSocks5Async(context, proxy, authentication, token);
            return;
        }

        var scheme   = proxy.Protocol == ProxyProtocol.Https ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        var address  = new UriBuilder(scheme, proxy.Host, proxy.Port).Uri;
        var webProxy = new WebProxy(address, false);
        if (authentication is { } credentialsPair)
            webProxy.Credentials = new NetworkCredential(credentialsPair.Username, credentialsPair.Password);

        handler.UseProxy = true;
        handler.Proxy    = webProxy;
    }

    private static (string Username, string Password)? ReadCredentials(ProxyDefinition   proxy,
                                                                       ICredentialStore? credentials)
    {
        if (string.IsNullOrWhiteSpace(proxy.CredentialTarget)) return null;
        if (credentials is null || !credentials.IsSupported)
            throw new InvalidOperationException("The selected proxy requires a supported system credential store.");

        var stored = credentials.Read(proxy.CredentialTarget);
        if (string.IsNullOrEmpty(stored))
            throw new
                InvalidOperationException("Credentials for the selected proxy are missing from the system credential store.");
        try
        {
            return ProxyCredentialCodec.Decode(stored);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidOperationException("Credentials for the selected proxy are invalid.", exception);
        }
    }

    private static async ValueTask<Stream> ConnectSocks5Async(
        SocketsHttpConnectionContext        context,     ProxyDefinition   proxy,
        (string Username, string Password)? credentials, CancellationToken cancellationToken)
    {
        var            socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        NetworkStream? stream = null;
        try
        {
            await socket.ConnectAsync(proxy.Host, proxy.Port, cancellationToken).ConfigureAwait(false);
            stream = new NetworkStream(socket, true);
            await NegotiateSocks5Async(stream, context.DnsEndPoint, credentials, cancellationToken)
               .ConfigureAwait(false);
            return stream;
        }
        catch (OperationCanceledException)
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            else socket.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            else socket.Dispose();
            throw new IOException("The configured SOCKS5 proxy connection or negotiation failed.", exception);
        }
    }

    private static async Task NegotiateSocks5Async(NetworkStream                       stream, DnsEndPoint target,
                                                   (string Username, string Password)? credentials,
                                                   CancellationToken                   cancellationToken)
    {
        var methods  = credentials is null ? new byte[] { 0x00 } : [0x00, 0x02];
        var greeting = new byte[2 + methods.Length];
        greeting[0] = 0x05;
        greeting[1] = checked((byte)methods.Length);
        methods.CopyTo(greeting, 2);
        await stream.WriteAsync(greeting, cancellationToken).ConfigureAwait(false);

        var selection = new byte[2];
        await stream.ReadExactlyAsync(selection, cancellationToken).ConfigureAwait(false);
        if (selection[0] != 0x05 || selection[1] == 0xFF)
            throw new IOException("The SOCKS5 proxy rejected the offered authentication methods.");
        if (selection[1] == 0x02)
        {
            if (credentials is not { } selectedCredentials)
                throw new IOException("The SOCKS5 proxy requires authentication but no credentials are configured.");
            await AuthenticateSocks5Async(stream, selectedCredentials, cancellationToken).ConfigureAwait(false);
        }
        else if (selection[1] != 0x00)
        {
            throw new IOException("The SOCKS5 proxy selected an unsupported authentication method.");
        }

        var request = CreateSocks5ConnectRequest(target);
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        var response = new byte[4];
        await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
        if (response[0] != 0x05 || response[1] != 0x00)
            throw new IOException($"The SOCKS5 proxy rejected the destination connection (reply {response[1]}).");

        var addressLength = response[3] switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 => await ReadSocks5DomainLengthAsync(stream, cancellationToken).ConfigureAwait(false),
            _    => throw new IOException("The SOCKS5 proxy returned an invalid address type.")
        };
        var boundAddressAndPort = new byte[addressLength + 2];
        await stream.ReadExactlyAsync(boundAddressAndPort, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AuthenticateSocks5Async(NetworkStream                      stream,
                                                      (string Username, string Password) credentials,
                                                      CancellationToken                  cancellationToken)
    {
        var username = Encoding.UTF8.GetBytes(credentials.Username);
        var password = Encoding.UTF8.GetBytes(credentials.Password);
        if (username.Length is 0 or > byte.MaxValue || password.Length is 0 or > byte.MaxValue)
            throw new IOException("SOCKS5 username or password exceeds the protocol limit.");

        var request = new byte[3 + username.Length + password.Length];
        request[0] = 0x01;
        request[1] = (byte)username.Length;
        username.CopyTo(request, 2);
        request[2 + username.Length] = (byte)password.Length;
        password.CopyTo(request, 3 + username.Length);
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);

        var response = new byte[2];
        await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
        if (response[0] != 0x01 || response[1] != 0x00)
            throw new IOException("The SOCKS5 proxy rejected the configured credentials.");
    }

    private static byte[] CreateSocks5ConnectRequest(DnsEndPoint target)
    {
        byte   addressType;
        byte[] address;
        if (IPAddress.TryParse(target.Host, out var ipAddress))
        {
            addressType = ipAddress.AddressFamily == AddressFamily.InterNetwork ? (byte)0x01 : (byte)0x04;
            address     = ipAddress.GetAddressBytes();
        }
        else
        {
            var asciiHost = new IdnMapping().GetAscii(target.Host);
            address = Encoding.ASCII.GetBytes(asciiHost);
            if (address.Length is 0 or > byte.MaxValue)
                throw new IOException("The destination host is outside the SOCKS5 domain-name limit.");
            addressType = 0x03;
        }

        var request = new byte[4 + (addressType == 0x03 ? 1 : 0) + address.Length + 2];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        request[3] = addressType;
        var offset                                 = 4;
        if (addressType == 0x03) request[offset++] = checked((byte)address.Length);
        address.CopyTo(request, offset);
        offset              += address.Length;
        request[offset]     =  (byte)(target.Port >> 8);
        request[offset + 1] =  (byte)target.Port;
        return request;
    }

    private static async Task<int> ReadSocks5DomainLengthAsync(NetworkStream     stream,
                                                               CancellationToken cancellationToken)
    {
        var size = new byte[1];
        await stream.ReadExactlyAsync(size, cancellationToken).ConfigureAwait(false);
        return size[0];
    }
}

/// <summary>
///     <para>拥有一个 Provider 专属 HttpClient 与 SDK Transport。</para>
///     <para>Owns one provider-specific HttpClient and SDK transport.</para>
/// </summary>
public sealed class ProviderHttpTransport : IDisposable
{
    private readonly HttpClient _client;
    private          bool       _disposed;

    internal ProviderHttpTransport(ResolvedNetworkRoute        route, HttpClient client,
                                   HttpClientPipelineTransport transport)
    {
        Route     = route;
        _client   = client;
        Transport = transport;
    }

    public ResolvedNetworkRoute Route { get; }

    public HttpClientPipelineTransport Transport { get; }

    /// <summary>
    ///     <para>释放 SDK Transport 和其独立连接池。</para>
    ///     <para>Disposes the SDK transport and its dedicated connection pool.</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Transport.Dispose();
        }
        finally
        {
            _client.Dispose();
        }
    }
}

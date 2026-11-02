using System.Net;
using System.Net.Sockets;
using System.Text;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Tests;

public sealed class NetworkConfigurationTests
{
    [Test]
    public void Resolve_DefaultsToSystemAndProviderOverridesGlobalRoute()
    {
        var defaults = new NetworkSettings();
        Assert.That(defaults.DefaultRoute.Mode, Is.EqualTo(NetworkMode.System));
        Assert.That(NetworkRouteResolver.Resolve(null, null).Mode, Is.EqualTo(NetworkMode.System));

        var direct = defaults with { DefaultRoute = new NetworkRoute { Mode = NetworkMode.Direct } };
        Assert.That(NetworkRouteResolver.Resolve(direct, null).Mode, Is.EqualTo(NetworkMode.Direct));
        Assert.That(NetworkRouteResolver.Resolve(direct, new NetworkRoute { Mode = NetworkMode.System }).Mode,
                    Is.EqualTo(NetworkMode.System));
    }

    [Test]
    public void Resolve_InheritedProviderTracksGlobalChangesAndExplicitRouteDoesNot()
    {
        var proxy         = new ProxyDefinition { Id      = "proxy-a", Name = "A", Host = "127.0.0.1", Port = 7890 };
        var system        = new NetworkSettings { Proxies = [proxy] };
        var direct        = system with { DefaultRoute = new NetworkRoute { Mode = NetworkMode.Direct } };
        var explicitProxy = new NetworkRoute { Mode = NetworkMode.Proxy, ProxyId = "proxy-a" };
        var withProxy     = system with { DefaultRoute = explicitProxy };

        Assert.That(NetworkRouteResolver.Resolve(system, null).Mode, Is.EqualTo(NetworkMode.System));
        Assert.That(NetworkRouteResolver.Resolve(direct, null).Mode, Is.EqualTo(NetworkMode.Direct));
        Assert.That(NetworkRouteResolver.Resolve(direct, explicitProxy).Proxy?.Id, Is.EqualTo("proxy-a"));
        Assert.That(NetworkRouteResolver.Resolve(withProxy, null).Proxy?.Id, Is.EqualTo("proxy-a"));
        Assert.That(NetworkRouteResolver.Resolve(direct, new NetworkRoute { Mode = NetworkMode.Direct }).Mode,
                    Is.EqualTo(NetworkMode.Direct));
    }

    [Test]
    public void Resolve_AllowsProvidersToUseDifferentSavedProxies()
    {
        var settings = new NetworkSettings
        {
            Proxies =
            [
                new ProxyDefinition { Id = "one", Name = "one", Host = "127.0.0.1", Port = 8001 },
                new ProxyDefinition { Id = "two", Name = "two", Host = "127.0.0.1", Port = 8002 }
            ]
        };

        Assert.That(NetworkRouteResolver
                   .Resolve(settings, new NetworkRoute { Mode = NetworkMode.Proxy, ProxyId = "one" })
                   .Proxy?.Port, Is.EqualTo(8001));
        Assert.That(NetworkRouteResolver
                   .Resolve(settings, new NetworkRoute { Mode = NetworkMode.Proxy, ProxyId = "two" })
                   .Proxy?.Port, Is.EqualTo(8002));
    }

    [TestCase(null, "proxyId")]
    [TestCase("missing", "missing proxy")]
    public void Validate_RejectsMissingProxyReference(string? proxyId, string expected)
    {
        var settings = new NetworkSettings
        {
            DefaultRoute = new NetworkRoute { Mode = NetworkMode.Proxy, ProxyId = proxyId }
        };

        var error = Assert.Throws<InvalidDataException>(() => NetworkSettingsValidator.Validate(settings));
        Assert.That(error!.Message, Does.Contain(expected));
    }

    [TestCase((ProxyProtocol)123, "proxy.invalid", 8080, "protocol")]
    [TestCase(ProxyProtocol.Http, "http://proxy.invalid", 8080, "host")]
    [TestCase(ProxyProtocol.Http, "proxy.invalid", 65536, "port")]
    public void Validate_RejectsInvalidProxyFields(ProxyProtocol protocol, string host, int port, string expected)
    {
        var invalid = new ProxyDefinition
        {
            Id = "bad", Name = "Bad", Protocol = protocol, Host = host, Port = port
        };

        var error = Assert.Throws<InvalidDataException>(() => NetworkSettingsValidator.Validate(new NetworkSettings
        {
            Proxies = [invalid]
        }));

        Assert.That(error!.Message, Does.Contain(expected));
    }

    [Test]
    public void NetworkModeParser_MapsSocks5hToTheSupportedSocks5Transport()
    {
        Assert.That(ProxyProtocolParser.TryParse("socks5h", out var protocol), Is.True);
        Assert.That(protocol, Is.EqualTo(ProxyProtocol.Socks5));
        Assert.That(ProxyProtocolParser.ToValueString(protocol), Is.EqualTo("socks5"));
    }

    [Test]
    public async Task ConfigRoundTrip_PreservesLegacyProviderFieldsAndNeverWritesProxySecrets()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tinyharness-network-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "user-config.json");
        var profile = new UserProfile
        {
            Name         = "work", Endpoint = "https://api.example.invalid/v1", ApiKeyCredentialTarget = "existing-key",
            DefaultModel = "model-a", Network = new NetworkRoute { Mode = NetworkMode.Proxy, ProxyId = "shared" },
            Models =
            [
                new UserProfileModel { Id = "model-a", ContextWindowTokens = 1000 },
                new UserProfileModel { Id = "model-b", ContextWindowTokens = 2000 }
            ]
        };
        var config = new UserConfig
        {
            DefaultProfile = "work", Profiles = [profile],
            Settings = new UserConfigSettings
            {
                Network = new NetworkSettings
                {
                    DefaultRoute = new NetworkRoute { Mode = NetworkMode.System },
                    Proxies =
                    [
                        new ProxyDefinition
                        {
                            Id               = "shared", Name                = "Office", Protocol = ProxyProtocol.Http,
                            Host             = "proxy.example.invalid", Port = 8080,
                            CredentialTarget = "TinyHarness:NetworkProxy:shared:entry"
                        }
                    ]
                }
            }
        };
        try
        {
            await UserConfigStore.SaveAsync(path, config, CancellationToken.None);
            var json   = await File.ReadAllTextAsync(path);
            var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);

            Assert.That(json, Does.Not.Contain("proxy-password"));
            Assert.That(loaded.Profiles[0].ApiKeyCredentialTarget, Is.EqualTo("existing-key"));
            Assert.That(loaded.Profiles[0].Models, Is.EqualTo(profile.Models));
            Assert.That(loaded.Profiles[0].Network, Is.EqualTo(profile.Network));
            Assert.That(loaded.Settings?.Network?.Proxies[0].CredentialTarget,
                        Is.EqualTo("TinyHarness:NetworkProxy:shared:entry"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task LegacyConfigWithoutNetworkFieldsLoadsSystemDefaultAndInheritedProvider()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tinyharness-network-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "user-config.json");
        try
        {
            await File.WriteAllTextAsync(path,
                                         """{"defaultProfile":"work","profiles":[{"name":"work","endpoint":"https://api.example.invalid/v1","defaultModel":"model-a","models":[{"id":"model-a","contextWindowTokens":1000}]}]}""");

            var loaded  = await UserConfigStore.LoadAsync(path, CancellationToken.None);
            var profile = TestAssert.Single(loaded.Profiles);

            Assert.That(loaded.Settings?.Network, Is.Null);
            Assert.That(profile.Network, Is.Null);
            Assert.That(NetworkRouteResolver.Resolve(loaded.Settings?.Network, profile.Network).Mode,
                        Is.EqualTo(NetworkMode.System));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task ProviderHttpClient_RoutesThroughEachSelectedProxyPool()
    {
        await using var firstProxy = new LoopbackHttpEndpoint(200, "first");
        await using var secondProxy = new LoopbackHttpEndpoint(200, "second");
        var first = new ProxyDefinition { Id = "first", Name = "first", Host = "127.0.0.1", Port = firstProxy.Port };
        var second = new ProxyDefinition
            { Id = "second", Name = "second", Host = "127.0.0.1", Port = secondProxy.Port };
        var settings = new NetworkSettings { Proxies = [first, second] };

        using (var client = ProviderTransportFactory.CreateHttpClient(settings,
                                                                      new NetworkRoute
                                                                      {
                                                                          Mode = NetworkMode.Proxy, ProxyId = first.Id
                                                                      }))
        {
            Assert.That(await client.GetStringAsync("http://api.invalid/one").WaitAsync(TimeSpan.FromSeconds(5)),
                        Is.EqualTo("first"));
        }

        using (var client = ProviderTransportFactory.CreateHttpClient(settings,
                                                                      new NetworkRoute
                                                                      {
                                                                          Mode = NetworkMode.Proxy, ProxyId = second.Id
                                                                      }))
        {
            Assert.That(await client.GetStringAsync("http://api.invalid/two").WaitAsync(TimeSpan.FromSeconds(5)),
                        Is.EqualTo("second"));
        }

        Assert.That(await firstProxy.RequestLine.WaitAsync(TimeSpan.FromSeconds(5)),
                    Does.StartWith("GET http://api.invalid/one "));
        Assert.That(await secondProxy.RequestLine.WaitAsync(TimeSpan.FromSeconds(5)),
                    Does.StartWith("GET http://api.invalid/two "));
    }

    [Test]
    public async Task ExplicitProxyFailureDoesNotFallBackToReachableOrigin()
    {
        await using var origin          = new LoopbackHttpEndpoint(200, "must-not-be-reached");
        var             unavailablePort = ReserveUnusedPort();
        var proxy = new ProxyDefinition
        {
            Id = "unavailable", Name = "Unavailable", Host = "127.0.0.1", Port = unavailablePort
        };
        var settings = new NetworkSettings { Proxies = [proxy] };
        using var client = ProviderTransportFactory.CreateHttpClient(settings,
                                                                     new NetworkRoute
                                                                     {
                                                                         Mode = NetworkMode.Proxy, ProxyId = proxy.Id
                                                                     });

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
                                                           await client
                                                                .GetAsync($"http://127.0.0.1:{origin.Port}/must-not-fallback")
                                                                .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.That(origin.RequestLine.IsCompleted, Is.False);
    }

    [Test]
    public async Task DirectRouteReachesOriginServer()
    {
        await using var origin = new LoopbackHttpEndpoint(200, "direct");
        using var client = ProviderTransportFactory.CreateHttpClient(
                                                                     new NetworkSettings
                                                                     {
                                                                         DefaultRoute = new NetworkRoute
                                                                             { Mode = NetworkMode.Direct }
                                                                     }, null);

        Assert.That(await client.GetStringAsync($"http://127.0.0.1:{origin.Port}/direct")
                                .WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo("direct"));
        Assert.That(await origin.RequestLine.WaitAsync(TimeSpan.FromSeconds(5)), Does.StartWith("GET /direct "));
    }

    [TestCase(ChatApiKind.ChatCompletions, "chat/completions")]
    [TestCase(ChatApiKind.Responses, "responses")]
    public async Task ModelClientFactory_UsesSelectedProxyForBothOpenAiProtocols(ChatApiKind api, string path)
    {
        await using var proxyServer = new LoopbackHttpEndpoint(400, string.Empty);
        var proxy = new ProxyDefinition
        {
            Id = "api", Name = "API", Host = "127.0.0.1", Port = proxyServer.Port
        };
        var network = new NetworkSettings { Proxies = [proxy] };
        using var model = (IDisposable)ModelClientFactory.Create(
                                                                 "model-x", "http://api.invalid/v1", "test-key", api,
                                                                 network,
                                                                 new NetworkRoute
                                                                     { Mode = NetworkMode.Proxy, ProxyId = proxy.Id });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        try
        {
            await ConsumeAsync((IChatCompletionClient)model, timeout.Token)
               .WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (Exception) when (!timeout.IsCancellationRequested)
        {
            // Fake proxy deliberately returns HTTP 400 after capturing the outgoing request.
        }

        Assert.That(await proxyServer.RequestLine.WaitAsync(TimeSpan.FromSeconds(5)),
                    Does.StartWith($"POST http://api.invalid/v1/{path} "));
    }

    [Test]
    public async Task Socks5UsesProxySideDnsAndSecureStoredAuthentication()
    {
        var credentials = new FakeCredentialStore();
        credentials.Save("proxy-auth", ProxyCredentialCodec.Encode("proxy-user", "proxy-password"));
        await using var socks = new LoopbackSocks5Proxy("proxy-user", "proxy-password");
        var proxy = new ProxyDefinition
        {
            Id   = "socks", Name     = "SOCKS", Protocol            = ProxyProtocol.Socks5,
            Host = "127.0.0.1", Port = socks.Port, CredentialTarget = "proxy-auth"
        };
        using var client = ProviderTransportFactory.CreateHttpClient(
                                                                     new NetworkSettings { Proxies = [proxy] },
                                                                     new NetworkRoute
                                                                     {
                                                                         Mode = NetworkMode.Proxy, ProxyId = proxy.Id
                                                                     }, credentials);

        Assert.That(await client.GetStringAsync("http://dns-only.invalid/socks")
                                .WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo("socks"));
        var observed = await socks.Observation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(observed.TargetHost, Is.EqualTo("dns-only.invalid"));
        Assert.That(observed.AuthenticationMatched, Is.True);
    }

    [TestCase("http://user:password@target.invalid/path")]
    [TestCase("http://target.invalid/path")]
    public async Task ConnectivityTest_RejectsNonHttpsTargetWithoutCredentialLeak(string target)
    {
        var proxy = new ProxyDefinition { Id = "p", Name = "p", Host = "127.0.0.1", Port = 1 };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                                                                       ProxyConnectivityTester.TestAsync(proxy, target,
                                                                           null, CancellationToken.None));
        Assert.That(error!.Message, Does.Not.Contain("password"));
    }

    private static async Task ConsumeAsync(IChatCompletionClient client, CancellationToken cancellationToken)
    {
        var request = new ChatCompletionRequest { Model = "model-x", Messages = [ChatMessage.User("probe")] };
        await foreach (var _ in client.CompleteAsync(request, cancellationToken).ConfigureAwait(false))
        {
        }
    }

    private static int ReserveUnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes   = new List<byte>();
        var current = new byte[1];
        while (bytes.Count < 64 * 1024)
        {
            await stream.ReadExactlyAsync(current, cancellationToken).ConfigureAwait(false);
            bytes.Add(current[0]);
            var count = bytes.Count;
            if (count >= 4 && bytes[count - 4] == 13 && bytes[count - 3] == 10 &&
                bytes[count               - 2] == 13 && bytes[count - 1] == 10)
                return Encoding.ASCII.GetString(bytes.ToArray());
        }

        throw new IOException("The test server received oversized HTTP headers.");
    }

    private static int ReadContentLength(string headers)
    {
        var line = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                          .FirstOrDefault(value => value.StartsWith("Content-Length:",
                                                                    StringComparison.OrdinalIgnoreCase));
        if (line is null) return 0;
        if (!int.TryParse(line.AsSpan("Content-Length:".Length).Trim(), out var length) ||
            length is < 0 or > 4 * 1024 * 1024)
            throw new IOException("The test server received an invalid content length.");
        return length;
    }

    private static async Task<byte[]> ReadFixedAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }

    private static async Task<string> ReadDomainAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = await ReadFixedAsync(stream, 1, cancellationToken).ConfigureAwait(false);
        var name   = await ReadFixedAsync(stream, length[0], cancellationToken).ConfigureAwait(false);
        return Encoding.ASCII.GetString(name);
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);
        public           bool IsSupported => true;
        public           void Save(string targetName, string secret) => _entries[targetName] = secret;
        public           string? Read(string targetName) => _entries.GetValueOrDefault(targetName);
        public           bool Delete(string targetName) => _entries.Remove(targetName);
    }

    private sealed class LoopbackHttpEndpoint : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TcpListener             _listener     = new(IPAddress.Loopback, 0);

        private readonly TaskCompletionSource<string> _requestLine =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly byte[] _response;
        private readonly Task   _serveTask;

        public LoopbackHttpEndpoint(int statusCode, string body)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            var bytes  = Encoding.UTF8.GetBytes(body);
            var phrase = statusCode == 200 ? "OK" : "Bad Request";
            _response = Encoding.ASCII.GetBytes(
                                                $"HTTP/1.1 {statusCode} {phrase}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n")
                                .Concat(bytes).ToArray();
            _serveTask = ServeAsync();
        }

        public int Port { get; }

        public Task<string> RequestLine => _requestLine.Task;

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            try
            {
                await _serveTask.ConfigureAwait(false);
            }
            catch (Exception) when (_cancellation.IsCancellationRequested)
            {
            }

            _cancellation.Dispose();
        }

        private async Task ServeAsync()
        {
            try
            {
                using var client    = await _listener.AcceptTcpClientAsync(_cancellation.Token).ConfigureAwait(false);
                using var stream    = client.GetStream();
                var       headers   = await ReadHeadersAsync(stream, _cancellation.Token).ConfigureAwait(false);
                var       firstLine = headers.Split("\r\n")[0];
                _requestLine.TrySetResult(firstLine);
                var contentLength = ReadContentLength(headers);
                if (contentLength > 0)
                    await stream.ReadExactlyAsync(new byte[contentLength], _cancellation.Token).ConfigureAwait(false);
                await stream.WriteAsync(_response, _cancellation.Token).ConfigureAwait(false);
                await stream.FlushAsync(_cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (_cancellation.IsCancellationRequested &&
                                              exception is OperationCanceledException or SocketException
                                               or ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                _requestLine.TrySetException(exception);
            }
        }
    }

    private sealed class LoopbackSocks5Proxy : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly string                  _expectedPassword;
        private readonly string                  _expectedUsername;
        private readonly TcpListener             _listener = new(IPAddress.Loopback, 0);

        private readonly TaskCompletionSource<(string TargetHost, bool AuthenticationMatched)> _observation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Task _serveTask;

        public LoopbackSocks5Proxy(string expectedUsername, string expectedPassword)
        {
            _expectedUsername = expectedUsername;
            _expectedPassword = expectedPassword;
            _listener.Start();
            Port       = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serveTask = ServeAsync();
        }

        public int Port { get; }

        public Task<(string TargetHost, bool AuthenticationMatched)> Observation => _observation.Task;

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            try
            {
                await _serveTask.ConfigureAwait(false);
            }
            catch (Exception) when (_cancellation.IsCancellationRequested)
            {
            }

            _cancellation.Dispose();
        }

        private async Task ServeAsync()
        {
            try
            {
                using var client   = await _listener.AcceptTcpClientAsync(_cancellation.Token).ConfigureAwait(false);
                using var stream   = client.GetStream();
                var       greeting = new byte[2];
                await stream.ReadExactlyAsync(greeting, _cancellation.Token).ConfigureAwait(false);
                var methods = new byte[greeting[1]];
                await stream.ReadExactlyAsync(methods, _cancellation.Token).ConfigureAwait(false);
                await stream.WriteAsync(new byte[] { 0x05, 0x02 }, _cancellation.Token).ConfigureAwait(false);

                var authHeader = new byte[2];
                await stream.ReadExactlyAsync(authHeader, _cancellation.Token).ConfigureAwait(false);
                var username = new byte[authHeader[1]];
                await stream.ReadExactlyAsync(username, _cancellation.Token).ConfigureAwait(false);
                var passwordLength = new byte[1];
                await stream.ReadExactlyAsync(passwordLength, _cancellation.Token).ConfigureAwait(false);
                var password = new byte[passwordLength[0]];
                await stream.ReadExactlyAsync(password, _cancellation.Token).ConfigureAwait(false);
                var authMatched = authHeader[0]                     == 1                 &&
                                  Encoding.UTF8.GetString(username) == _expectedUsername &&
                                  Encoding.UTF8.GetString(password) == _expectedPassword;
                await stream.WriteAsync(new byte[] { 0x01, authMatched ? (byte)0x00 : (byte)0x01 },
                                        _cancellation.Token).ConfigureAwait(false);

                var requestHeader = new byte[4];
                await stream.ReadExactlyAsync(requestHeader, _cancellation.Token).ConfigureAwait(false);
                var host = requestHeader[3] switch
                {
                    0x01 => new IPAddress(await ReadFixedAsync(stream, 4, _cancellation.Token).ConfigureAwait(false))
                       .ToString(),
                    0x04 => new IPAddress(await ReadFixedAsync(stream, 16, _cancellation.Token).ConfigureAwait(false))
                       .ToString(),
                    0x03 => await ReadDomainAsync(stream, _cancellation.Token).ConfigureAwait(false),
                    _    => throw new IOException("Invalid SOCKS5 destination address type.")
                };
                await ReadFixedAsync(stream, 2, _cancellation.Token).ConfigureAwait(false);
                _observation.TrySetResult((host, authMatched));
                await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 },
                                        _cancellation.Token).ConfigureAwait(false);
                await ReadHeadersAsync(stream, _cancellation.Token).ConfigureAwait(false);
                var body = Encoding.UTF8.GetBytes("socks");
                var response = Encoding.ASCII.GetBytes(
                                                       $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n")
                                       .Concat(body).ToArray();
                await stream.WriteAsync(response, _cancellation.Token).ConfigureAwait(false);
                await stream.FlushAsync(_cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (_cancellation.IsCancellationRequested &&
                                              exception is OperationCanceledException or SocketException
                                               or ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                _observation.TrySetException(exception);
            }
        }
    }
}

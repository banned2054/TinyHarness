using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>验收专用的有界 TCP/UDP 请求回应探针；仅连接显式 IP，不调用系统名称解析器。</para>
///     <para>Bounded TCP/UDP request-response probe for acceptance; connects to explicit IPs without name resolution.</para>
/// </summary>
public static class SandboxNetworkProbe
{
    internal static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(3);

    /// <summary>
    ///     <para>执行隐藏子命令并输出 source-generated JSON；参数无效时退出 2，探针内部错误退出 3。</para>
    ///     <para>Runs the hidden command and writes source-generated JSON; invalid arguments exit 2, probe errors exit 3.</para>
    /// </summary>
    public static async Task<int> RunCommandAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        if (args is not [("tcp" or "udp"), _, _, _]       ||
            !IPAddress.TryParse(args[1], out var address) ||
            address.Equals(IPAddress.Any)                 ||
            address.Equals(IPAddress.IPv6Any)             ||
            !int.TryParse(args[2], out var port)          ||
            port is < 1 or > 65535                        ||
            args[3].Length != 32                          ||
            !args[3].All(Uri.IsHexDigit))
            return 2;

        var observation = await ExchangeAsync(args[0], address, port, args[3], cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(JsonSerializer.Serialize(observation,
                                                             WindowsSandboxAcceptanceJsonContext.Default
                                                                .SandboxNetworkObservation)).ConfigureAwait(false);
        return observation.Status is "error" or "mismatch" ? 3 : 0;
    }

    internal static async Task<SandboxNetworkObservation> ExchangeAsync(
        string transport, IPAddress address, int port, string nonce, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(WaitLimit);
        var connected = false;
        var sent      = false;
        try
        {
            var    request  = Encoding.ASCII.GetBytes($"THNET1:{nonce}\n");
            var    expected = Encoding.ASCII.GetBytes($"ACK:{nonce}\n");
            byte[] response;
            if (transport == "tcp")
            {
                using var client = new TcpClient(address.AddressFamily);
                await client.ConnectAsync(address, port, deadline.Token).ConfigureAwait(false);
                connected = true;
                var stream = client.GetStream();
                await stream.WriteAsync(request, deadline.Token).ConfigureAwait(false);
                sent     = true;
                response = new byte[expected.Length];
                await stream.ReadExactlyAsync(response, deadline.Token).ConfigureAwait(false);
            }
            else
            {
                using var client = new UdpClient(address.AddressFamily);
                client.Connect(address, port);
                await client.SendAsync(request, deadline.Token).ConfigureAwait(false);
                sent     = true;
                response = (await client.ReceiveAsync(deadline.Token).ConfigureAwait(false)).Buffer;
            }

            return new SandboxNetworkObservation(transport, address.ToString(), port, nonce,
                                                 response.AsSpan().SequenceEqual(expected) ? "matched" : "mismatch",
                                                 connected, sent);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SandboxNetworkObservation(transport, address.ToString(), port, nonce, "wait-expired", connected,
                                                 sent,
                                                 Detail : "探针内部 3 秒等待到期；不是 Harness 命令超时。");
        }
        catch (SocketException ex)
        {
            var status = ex.SocketErrorCode is SocketError.AccessDenied or SocketError.ConnectionRefused or
                                               SocketError.TimedOut or SocketError.HostUnreachable
                                            or SocketError.NetworkUnreachable or
                                               SocketError.ConnectionReset
                ? "socket-denied"
                : "error";
            return new SandboxNetworkObservation(transport, address.ToString(), port, nonce, status, connected, sent,
                                                 ex.SocketErrorCode.ToString(), ex.NativeErrorCode, ex.Message);
        }
        catch (IOException ex)
        {
            return new SandboxNetworkObservation(transport, address.ToString(), port, nonce, "error", connected, sent,
                                                 Detail : ex.Message);
        }
    }
}

/// <summary>
///     <para>仅绑定一个明确地址的受控回应器；独占端口，不接管既有服务。</para>
///     <para>Controlled responder bound to one explicit address; uses an exclusive port without taking over services.</para>
/// </summary>
internal interface ISandboxNetworkResponder : IAsyncDisposable
{
    int     Port    { get; }
    string? Failure { get; }
}

internal sealed class SandboxNetworkResponder : ISandboxNetworkResponder
{
    private readonly Task                    _loop;
    private readonly Socket                  _socket;
    private readonly CancellationTokenSource _stop = new();

    internal SandboxNetworkResponder(string transport, IPAddress address, int port, string nonce)
    {
        _socket = new Socket(address.AddressFamily, transport == "tcp" ? SocketType.Stream : SocketType.Dgram,
                             transport                        == "tcp" ? ProtocolType.Tcp : ProtocolType.Udp);
        try
        {
            _socket.ExclusiveAddressUse = true;
            if (address.AddressFamily == AddressFamily.InterNetworkV6) _socket.DualMode = false;
            _socket.Bind(new IPEndPoint(address, port));
            if (transport == "tcp") _socket.Listen(8);
            _loop = ServeAsync(transport, nonce);
        }
        catch
        {
            _socket.Dispose();
            _stop.Dispose();
            throw;
        }
    }

    public int     Port    => ((IPEndPoint)_socket.LocalEndPoint!).Port;
    public string? Failure { get; private set; }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _socket.Dispose();
        _stop.Dispose();
    }

    private async Task ServeAsync(string transport, string nonce)
    {
        var expected = Encoding.ASCII.GetBytes($"THNET1:{nonce}\n");
        var ack      = Encoding.ASCII.GetBytes($"ACK:{nonce}\n");
        try
        {
            while (!_stop.IsCancellationRequested)
                if (transport == "tcp")
                {
                    using var socket   = await _socket.AcceptAsync(_stop.Token).ConfigureAwait(false);
                    using var stream   = new NetworkStream(socket, false);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    deadline.CancelAfter(SandboxNetworkProbe.WaitLimit);
                    try
                    {
                        var request = new byte[expected.Length];
                        await stream.ReadExactlyAsync(request, deadline.Token).ConfigureAwait(false);
                        if (request.AsSpan().SequenceEqual(expected))
                            await stream.WriteAsync(ack, deadline.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!_stop.IsCancellationRequested &&
                                               ex is IOException or SocketException or OperationCanceledException)
                    {
                        // 单个客户端不能拖住监听；后续宿主正对照仍须成功。
                    }
                }
                else
                {
                    var buffer = new byte[128];
                    EndPoint remote = new IPEndPoint(_socket.AddressFamily == AddressFamily.InterNetwork
                                                         ? IPAddress.Any
                                                         : IPAddress.IPv6Any, 0);
                    var packet = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, _stop.Token)
                                              .ConfigureAwait(false);
                    if (buffer.AsSpan(0, packet.ReceivedBytes).SequenceEqual(expected))
                        await _socket.SendToAsync(ack, SocketFlags.None, packet.RemoteEndPoint, _stop.Token)
                                     .ConfigureAwait(false);
                }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Failure = $"{ex.GetType().Name}: {ex.Message}";
        }
    }
}

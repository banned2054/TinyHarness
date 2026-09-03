using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TinyHarness.Tests;

/// <summary>
/// A scripted local HTTP server that mimics an OpenAI-compatible SSE endpoint
/// (PLAN M2 contract tests). Each HTTP request is answered from a queue of
/// handlers, letting tests drive the real transport without network or key.
/// Requests are recorded so tests can assert what the client actually sent.
/// </summary>
internal sealed class MockSseServer : IDisposable
{
    private readonly CancellationTokenSource                    _cts      = new();
    private readonly object                                     _lock     = new();
    private readonly Queue<Func<ReceivedRequest, HttpResponse>> _handlers = new();
    private          HttpListener                               _listener = new();

    public sealed record ReceivedRequest(string Method, string Path, string Body);

    public sealed record HttpResponse(int Status, string ContentType, string Body);

    public string BaseUrl { get; private set; } = string.Empty;

    public List<ReceivedRequest> Requests { get; } = [];

    public void Start()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        BaseUrl = $"http://127.0.0.1:{port}/v1";
        _       = Task.Run(ListenerLoopAsync);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (ObjectDisposedException)
        {
            // Already stopped.
        }

        _listener.Close();
        _cts.Dispose();
    }

    /// <summary>Enqueues the response for the next request.</summary>
    public void Enqueue(Func<ReceivedRequest, HttpResponse> handler)
    {
        lock (_lock)
        {
            _handlers.Enqueue(handler);
        }
    }

    public void EnqueueError(int status)
        => Enqueue(_ => new HttpResponse(status, "application/json",
                                         JsonSerializer.Serialize(new
                                         {
                                             error = new
                                             {
                                                 message = $"mock {status}", type = "mock_error", code = status,
                                             },
                                         })));

    public void EnqueueRaw(string sseBody)
        => Enqueue(_ => new HttpResponse(200, "text/event-stream", sseBody));

    private async Task ListenerLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        string body;
        using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        var request = new ReceivedRequest(ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath, body);
        lock (_lock)
        {
            Requests.Add(request);
        }

        HttpResponse response;
        lock (_lock)
        {
            response = _handlers.Count == 0
                ? new HttpResponse(500, "text/plain", "no scripted response")
                : _handlers.Dequeue()(request);
        }

        ctx.Response.StatusCode      = response.Status;
        ctx.Response.ContentType     = response.ContentType;
        ctx.Response.ContentLength64 = Encoding.UTF8.GetByteCount(response.Body);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(response.Body);
            await ctx.Response.OutputStream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected.
        }

        try
        {
            ctx.Response.Close();
        }
        catch (ObjectDisposedException)
        {
            // Response already closed.
        }
    }
}

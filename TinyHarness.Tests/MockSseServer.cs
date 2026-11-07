using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TinyHarness.Tests;

/// <summary>
///     A scripted local HTTP server that mimics an OpenAI-compatible SSE endpoint
///     (PLAN M2 contract tests). Each HTTP request is answered from a queue of
///     handlers, letting tests drive the real transport without network or key.
///     Requests are recorded so tests can assert what the client actually sent.
/// </summary>
internal sealed class MockSseServer : IDisposable
{
    private readonly CancellationTokenSource                    _cts      = new();
    private readonly Queue<Func<ReceivedRequest, HttpResponse>> _handlers = new();
    private readonly Queue<StreamingResponse> _chunkedHandlers = new();
    private readonly Lock                                       _lock     = new();

    private HttpListener _listener = new();

    public string BaseUrl { get; private set; } = string.Empty;

    public List<ReceivedRequest> Requests { get; } = [];

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

    /// <summary>Enqueues the response for the next request.</summary>
    public void Enqueue(Func<ReceivedRequest, HttpResponse> handler)
    {
        lock (_lock)
        {
            _handlers.Enqueue(handler);
        }
    }

    public void EnqueueError(int status)
    {
        Enqueue(_ => new HttpResponse(status, "application/json",
                                      JsonSerializer.Serialize(new
                                      {
                                          error = new
                                          {
                                              message = $"mock {status}", type = "mock_error", code = status
                                          }
                                      })));
    }

    public void EnqueueRaw(string sseBody)
    {
        Enqueue(_ => new HttpResponse(200, "text/event-stream", sseBody));
    }

    /// <summary>
    ///     <para>把下一个请求改为分块响应：各块之间等待指定间隔，模拟迟交流式 SSE 传输。</para>
    ///     <para>
    ///         Answers the next request with a chunked response that waits the given gap between
    ///         chunks, mimicking a dribbling SSE transport.
    ///     </para>
    /// </summary>
    public void EnqueueChunked(IReadOnlyList<string> chunks, TimeSpan gap)
    {
        lock (_lock)
        {
            _chunkedHandlers.Enqueue(new StreamingResponse(chunks, gap));
        }
    }

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

        // 分块与常规处理器互斥出队：分块路径渐进写出后直接返回。
        // The chunked and regular handlers dequeue mutually exclusively; the chunked path writes
        // progressively and returns.
        StreamingResponse? streaming;
        lock (_lock)
        {
            streaming = _chunkedHandlers.Count > 0 ? _chunkedHandlers.Dequeue() : null;
        }

        if (streaming is not null)
        {
            await WriteChunkedAsync(ctx, streaming).ConfigureAwait(false);
            return;
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

    /// <summary>
    ///     <para>以 chunked 传输逐块写出响应体；每块写出后立即刷新，并按间隔等待以模拟迟交流。</para>
    ///     <para>
    ///         Writes the response body chunk by chunk over chunked transfer encoding, flushing
    ///         after each chunk and waiting the gap in between to mimic dribbled delivery.
    ///     </para>
    /// </summary>
    private async Task WriteChunkedAsync(HttpListenerContext ctx, StreamingResponse response)
    {
        ctx.Response.StatusCode  = 200;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.SendChunked = true;
        foreach (var chunk in response.Chunks)
        {
            var bytes = Encoding.UTF8.GetBytes(chunk);
            await ctx.Response.OutputStream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
            await ctx.Response.OutputStream.FlushAsync(_cts.Token).ConfigureAwait(false);
            if (response.Gap > TimeSpan.Zero) await Task.Delay(response.Gap, _cts.Token).ConfigureAwait(false);
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

    public sealed record ReceivedRequest(string Method, string Path, string Body);

    public sealed record HttpResponse(int Status, string ContentType, string Body);

    public sealed record StreamingResponse(IReadOnlyList<string> Chunks, TimeSpan Gap);
}

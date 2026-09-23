using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using TinyHarness.Core.Models.Mcp;
using TinyHarness.Core.Models.Worker;
using TinyHarness.Core.Services.Worker;

namespace TinyHarness.Core.Services.Mcp;

/// <summary>
///     本机 stdio MCP 服务端：按行读取 JSON-RPC 2.0 消息、串行处理请求，stdout 仅写协议响应。
///     输入行有固定上限；读取期间由独立 pump 识别 notifications/cancelled，并在断开时取消
///     正在运行的 worker。生产入口必须注入可信配置构造的 ask_glm delegate。
///     Local stdio MCP server: reads newline-delimited JSON-RPC 2.0 messages, processes requests
///     serially and writes only protocol responses to stdout. Input lines are bounded; a separate
///     reader pump handles notifications/cancelled and cancels the active worker on disconnect. The
///     production entry point must inject ask_glm composed from trusted host configuration.
/// </summary>
public sealed class McpStdioServer(
    TextReader                                                  input,
    TextWriter                                                  output,
    Func<WorkerRequest, CancellationToken, Task<WorkerResult>>? askGlm = null)
{
    private const string JsonRpcVersion            = "2.0";
    private const int    ErrorParse                = -32700;
    private const int    ErrorInvalidRequest       = -32600;
    private const int    ErrorMethodNotFound       = -32601;
    private const int    ErrorInvalidParams        = -32602;
    private const int    ErrorServerNotInitialized = -32002;
    private const int    MaxMessageCharacters      = 65_536;
    private const int    MaxQueuedMessages         = 16;

    private readonly TextReader _input      = input  ?? throw new ArgumentNullException(nameof(input));
    private readonly TextWriter _output     = output ?? throw new ArgumentNullException(nameof(output));
    private readonly Lock       _activeGate = new();

    private JsonElement?             _activeRequestId;
    private CancellationTokenSource? _activeWorker;

    private bool _initializeReceived;

    /// <summary>运行到输入 EOF 或宿主取消。Runs until input EOF or host cancellation.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(MaxQueuedMessages)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode     = BoundedChannelFullMode.Wait,
        });
        var readerTask = PumpInputAsync(channel.Writer, cancellationToken);

        try
        {
            await foreach (var line in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var response = await HandleMessageAsync(line, cancellationToken).ConfigureAwait(false);
                if (response is null) continue;

                await _output.WriteAsync(response.AsMemory(), cancellationToken).ConfigureAwait(false);
                await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            CancelActiveWorker();
            channel.Writer.TryComplete();
            try
            {
                await readerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected during host shutdown.
            }
        }
    }

    private async Task PumpInputAsync(ChannelWriter<string> writer, CancellationToken cancellationToken)
    {
        var reader = new BoundedLineReader(_input, MaxMessageCharacters);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;

                if (line.Value.IsTooLong)
                {
                    if (!writer.TryWrite(TooLargeMessageSentinel))
                    {
                        CancelActiveWorker();
                        break;
                    }

                    continue;
                }

                if (TryGetCancellationTarget(line.Value.Text!, out var target))
                {
                    CancelActiveWorker(target);
                    continue;
                }

                if (!writer.TryWrite(line.Value.Text!))
                {
                    // Backpressure overflow closes the input side and cancels the active run instead
                    // of allocating an unbounded queue of local requests.
                    CancelActiveWorker();
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host cancellation is propagated to the active worker below.
        }
        finally
        {
            CancelActiveWorker();
            writer.TryComplete();
        }
    }

    private static readonly string TooLargeMessageSentinel = "\0tinyharness-mcp-message-too-large\0";

    private async Task<string?> HandleMessageAsync(string line, CancellationToken cancellationToken)
    {
        if (string.Equals(line, TooLargeMessageSentinel, StringComparison.Ordinal))
            return WriteError(id : null, ErrorInvalidRequest, "Message exceeds the maximum size");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return WriteError(id : null, ErrorParse, "Parse error");
        }

        using (document)
        {
            McpJsonRpcRequest? request;
            try
            {
                request = document.RootElement.Deserialize(McpJsonContext.Default.McpJsonRpcRequest);
            }
            catch (JsonException)
            {
                return WriteError(id : null, ErrorInvalidRequest, "Invalid Request");
            }

            if (request is null || !IsWellFormed(request))
                return WriteError(IdOf(request), ErrorInvalidRequest, "Invalid Request");

            return await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsWellFormed(McpJsonRpcRequest request) =>
        string.Equals(request.JsonRpc, JsonRpcVersion, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(request.Method)                               &&
        (!request.Id.HasValue ||
         request.Id.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null);

    private static JsonElement? IdOf(McpJsonRpcRequest? request)
    {
        if (request?.Id is not { ValueKind: (JsonValueKind.String or JsonValueKind.Number) } id)
            return null;
        return id;
    }

    private async Task<string?> DispatchAsync(McpJsonRpcRequest request, CancellationToken cancellationToken)
    {
        if (request.Id is not { } id) return null;

        if (!_initializeReceived                                                   &&
            !string.Equals(request.Method, "initialize", StringComparison.Ordinal) &&
            !string.Equals(request.Method, "ping", StringComparison.Ordinal))
            return WriteError(id, ErrorServerNotInitialized, "Server not initialized");

        switch (request.Method)
        {
            case "initialize" :
                _initializeReceived = true;
                return WriteResult(id, BuildInitializeResult(request.Params),
                                   McpJsonContext.Default.McpInitializeResult);
            case "tools/list" :
                return WriteResult(id, new McpToolsListResult { Tools = McpToolCatalog.ListTools() },
                                   McpJsonContext.Default.McpToolsListResult);
            case "tools/call" :
                return await CallToolAsync(id, request.Params, cancellationToken).ConfigureAwait(false);
            case "ping" :
                return WriteResult(id, new McpEmptyResult(), McpJsonContext.Default.McpEmptyResult);
            default :
                return WriteError(id, ErrorMethodNotFound, $"Method not found: {request.Method}");
        }
    }

    private async Task<string> CallToolAsync(JsonElement       id, JsonElement? parameters,
                                             CancellationToken serverCancellationToken)
    {
        if (!McpAskGlmArgumentsParser.TryParse(parameters, out var name, out var taskRequest))
            return WriteError(id, ErrorInvalidParams, "Invalid tools/call parameters");

        if (!string.Equals(name, "ask_glm", StringComparison.Ordinal))
        {
            var unknown = FailureResult("Unknown tool name.");
            return WriteResult(id, ToCallResult(unknown, isError : true), McpJsonContext.Default.McpToolCallResult);
        }

        if (askGlm is null)
        {
            var unavailable = FailureResult("The worker is not configured.");
            return WriteResult(id, ToCallResult(unavailable, isError : true), McpJsonContext.Default.McpToolCallResult);
        }

        var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
        lock (_activeGate)
        {
            // Dispatch is serial, so a non-null active request indicates a broken server invariant.
            if (_activeWorker is not null)
            {
                workerCancellation.Dispose();
                return WriteError(id, ErrorInvalidRequest, "Another worker request is active");
            }

            _activeRequestId = id.Clone();
            _activeWorker    = workerCancellation;
        }

        WorkerResult result;
        try
        {
            var requestValidation = WorkerRequestValidator.Validate(taskRequest);
            if (!requestValidation.IsValid)
            {
                result = FailureResult("The ask_glm task does not satisfy the worker input limits.");
            }
            else
            {
                result = await askGlm(taskRequest!, workerCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (workerCancellation.IsCancellationRequested)
        {
            result = new WorkerResult
            {
                Status       = WorkerResultStatus.Cancelled,
                StatusDetail = "Cancelled by the MCP client or host before completion.",
            };
        }
        catch (Exception)
        {
            // A client/transport exception may contain credentials, endpoint details or response data.
            result = FailureResult("The worker failed; error details are withheld.");
        }
        finally
        {
            lock (_activeGate)
            {
                if (ReferenceEquals(_activeWorker, workerCancellation))
                {
                    _activeRequestId = null;
                    _activeWorker    = null;
                }
            }

            workerCancellation.Dispose();
        }

        if (!WorkerResultValidator.Validate(result).IsValid)
            result = FailureResult("The worker returned an invalid result; details are withheld.");

        var isError = result.Status is WorkerResultStatus.Failed or WorkerResultStatus.Cancelled;
        return WriteResult(id, ToCallResult(result, isError), McpJsonContext.Default.McpToolCallResult);
    }

    private static McpToolCallResult ToCallResult(WorkerResult result, bool isError)
    {
        var json = JsonSerializer.Serialize(result, WorkerResultJsonContext.Default.WorkerResult);
        return new McpToolCallResult
        {
            Content = [new McpTextContent { Text = json }],
            IsError = isError,
        };
    }

    private static WorkerResult FailureResult(string detail) => new()
    {
        Status       = WorkerResultStatus.Failed,
        StatusDetail = detail,
    };

    private void CancelActiveWorker(JsonElement? cancellationTarget = null)
    {
        lock (_activeGate)
        {
            if (_activeWorker is null) return;
            if (cancellationTarget is { } target &&
                (_activeRequestId is not { } activeId || !IdsEqual(activeId, target))) return;
            try
            {
                _activeWorker.Cancel();
            }
            catch (AggregateException)
            {
                // Cancellation callbacks belong to the client/transport. They must not corrupt MCP framing.
            }
        }
    }

    private static bool TryGetCancellationTarget(string line, out JsonElement target)
    {
        target = default;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object           ||
                !root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String ||
                version.GetString()                                                   != JsonRpcVersion ||
                root.TryGetProperty("id", out _) ||
                !root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String ||
                method.GetString() != "notifications/cancelled" ||
                !root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
                !parameters.TryGetProperty("requestId", out var requestId) ||
                requestId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
                return false;

            target = requestId.Clone();
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IdsEqual(JsonElement left, JsonElement right) =>
        left.ValueKind == right.ValueKind &&
        string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);

    private static McpInitializeResult BuildInitializeResult(JsonElement? parameters)
    {
        var requested = parameters is { ValueKind: JsonValueKind.Object }                           &&
                        parameters.Value.TryGetProperty("protocolVersion", out var versionProperty) &&
                        versionProperty.ValueKind == JsonValueKind.String
            ? versionProperty.GetString()
            : null;

        return new McpInitializeResult
        {
            ProtocolVersion = McpProtocolVersion.IsSupported(requested) ? requested! : McpProtocolVersion.Latest,
            Capabilities = new McpServerCapabilities { Tools = new McpToolsCapability() },
            ServerInfo = new McpServerInfo { Name = McpToolCatalog.ServerName, Version = McpToolCatalog.ServerVersion },
            Instructions =
                "ask_glm runs a one-shot, read-only investigation over the workspace fixed at server startup. "   +
                "Provide a concrete task; the worker returns a bounded result with status, evidence and limits. " +
                "Files permitted by the worker read policy are sent to the explicitly selected model endpoint. "  +
                "The worker cannot modify files or run commands.",
        };
    }

    private static string WriteResult<T>(JsonElement? id, T result, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Serialize(new McpJsonRpcResponse
        {
            Id     = id,
            Result = JsonSerializer.SerializeToElement(result, typeInfo),
        }, McpJsonContext.Default.McpJsonRpcResponse) + "\n";

    private static string WriteError(JsonElement? id, int code, string message) =>
        JsonSerializer.Serialize(new McpJsonRpcResponse
        {
            Id    = id,
            Error = new McpJsonRpcError { Code = code, Message = message },
        }, McpJsonContext.Default.McpJsonRpcResponse) + "\n";

    private sealed class BoundedLineReader(TextReader input, int maximumCharacters)
    {
        private readonly char[] _buffer = new char[4096];
        private          int    _start;
        private          int    _count;

        public async ValueTask<BoundedLine?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var builder = new StringBuilder(Math.Min(maximumCharacters, 4096));
            var tooLong = false;

            while (true)
            {
                if (_start >= _count)
                {
                    _count = await input.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    _start = 0;
                    if (_count == 0)
                    {
                        if (builder.Length == 0 && !tooLong) return null;
                        return MakeLine(builder, tooLong);
                    }
                }

                var newline = Array.IndexOf(_buffer, '\n', _start, _count - _start);
                var end     = newline >= 0 ? newline : _count;
                var length  = end - _start;
                if (!tooLong)
                {
                    if (builder.Length + length > maximumCharacters)
                    {
                        tooLong = true;
                        builder.Clear();
                    }
                    else
                    {
                        builder.Append(_buffer, _start, length);
                    }
                }

                _start = newline >= 0 ? newline + 1 : _count;
                if (newline >= 0) return MakeLine(builder, tooLong);
            }
        }

        private static BoundedLine MakeLine(StringBuilder builder, bool tooLong)
        {
            if (!tooLong && builder.Length > 0 && builder[^1] == '\r') builder.Length--;
            return new BoundedLine(tooLong ? null : builder.ToString(), tooLong);
        }
    }

    private readonly record struct BoundedLine(string? Text, bool IsTooLong);
}

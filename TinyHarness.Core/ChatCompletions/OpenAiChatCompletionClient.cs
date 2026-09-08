using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Runtime.CompilerServices;

namespace TinyHarness.Core.ChatCompletions;

/// <summary>
/// 基于 OpenAI SDK 的 Chat Completions 流式客户端。它把项目内部请求映射为 SDK 请求，
/// 再将文本和工具调用增量转换回内部流事件。
///
/// Chat Completions streaming client backed by the OpenAI SDK. It maps internal
/// requests to SDK requests and converts text and tool-call deltas back to internal events.
/// </summary>
public sealed class OpenAiChatCompletionClient : IChatCompletionClient
{
    private readonly ChatClient _client;

    /// <summary>
    /// 验证模型、端点与密钥，并构造使用自定义基地址的 SDK 客户端。
    /// Validates model, endpoint, and key, then creates the SDK client with the custom base URI.
    /// </summary>
    public OpenAiChatCompletionClient(string model, string endpoint, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("A model is required.", nameof(model));
        }

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("An endpoint is required.", nameof(endpoint));
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("An API key is required.", nameof(apiKey));
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
         || (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"Endpoint must be an absolute http(s) URL, got '{endpoint}'.",
                                        nameof(endpoint));
        }

        // Ensure the SDK appends "chat/completions" to the base URL instead of
        // gluing it onto the last path segment.
        var baseUri = new Uri(endpoint.TrimEnd('/') + "/");

        var sdkOptions = new OpenAIClientOptions { Endpoint = baseUri };
        _client = new ChatClient(model, new ApiKeyCredential(apiKey), sdkOptions);
    }

    /// <summary>
    /// 将内部请求转换为 SDK 请求，并把 SDK 的文本与工具调用增量映射为项目自己的流事件。
    /// Converts the internal request to SDK input and maps SDK text/tool deltas back to project stream events.
    /// </summary>
    public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
                                                                 [EnumeratorCancellation]
                                                                 CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var sdkMessages = ToSdkMessages(request);
        var sdkOptions  = new ChatCompletionOptions();

        if (request.Tools is { Count: > 0 } tools)
        {
            foreach (var tool in tools)
            {
                sdkOptions.Tools.Add(ChatTool.CreateFunctionTool(tool.Name, tool.Description,
                                                                 BinaryData.FromString(tool.Parameters
                                                                    .ToJsonString())));
            }
        }

        // SDK failures (HTTP errors, malformed or truncated streams) surface as
        // ClientModelException/ClientResultException whose message already carries
        // the HTTP status. They intentionally propagate to the Agent Loop boundary,
        // which converts them into a Failed result with that context; nothing is
        // swallowed here. (A C# iterator cannot yield inside a try-with-catch.)
        await foreach (var update in _client
                                    .CompleteChatStreamingAsync(sdkMessages, sdkOptions, cancellationToken)
                                    .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var part in update.ContentUpdate)
            {
                // Only plain text parts map onto ContentDelta; audio, image
                // and refusal parts are out of the MVP protocol.
                if (!string.IsNullOrEmpty(part.Text))
                {
                    yield return new ChatStreamEvent
                    {
                        Kind         = ChatStreamEventKind.ContentDelta,
                        ContentDelta = part.Text,
                    };
                }
            }

            foreach (var delta in update.ToolCallUpdates)
            {
                // id/name may only be present on the first fragment of a
                // tool call; the accumulator merges per tool-call index.
                yield return new ChatStreamEvent
                {
                    Kind                   = ChatStreamEventKind.ToolCallDelta,
                    ToolCallIndex          = delta.Index,
                    ToolCallId             = string.IsNullOrEmpty(delta.ToolCallId) ? null : delta.ToolCallId,
                    ToolCallFunctionName   = string.IsNullOrEmpty(delta.FunctionName) ? null : delta.FunctionName,
                    ToolCallArgumentsDelta = delta.FunctionArgumentsUpdate?.ToString() ?? string.Empty,
                };
            }
        }

        yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End };
    }

    /// <summary>
    /// 把项目消息转换为 SDK 消息；含工具调用的 assistant 消息按 API 约束不携带文本内容。
    ///
    /// Translates protocol messages into SDK message types. Note: the SDK cannot
    /// express an assistant message that carries both text content and tool
    /// calls (the official API leaves content empty when tool_calls are set), so
    /// tool-call messages are sent without their (normally empty) content.
    /// </summary>
    private static IReadOnlyList<OpenAI.Chat.ChatMessage> ToSdkMessages(ChatCompletionRequest request)
    {
        var result = new List<OpenAI.Chat.ChatMessage>(request.Messages.Count);

        foreach (var message in request.Messages)
        {
            switch (message.Role)
            {
                case ChatRole.System :
                    result.Add(new SystemChatMessage(message.Content));
                    break;

                case ChatRole.User :
                    result.Add(new UserChatMessage(message.Content));
                    break;

                case ChatRole.Assistant when message.ToolCalls is { Count: > 0 } :
                    result.Add(new AssistantChatMessage(ToSdkToolCalls(message.ToolCalls)));
                    break;

                case ChatRole.Assistant :
                    result.Add(new AssistantChatMessage(message.Content));
                    break;

                case ChatRole.Tool :
                    if (string.IsNullOrEmpty(message.ToolCallId))
                    {
                        throw new InvalidDataException("A tool message must reference a tool call id.");
                    }

                    result.Add(new ToolChatMessage(message.ToolCallId, message.Content));
                    break;

                default :
                    throw new InvalidOperationException($"Unsupported chat role '{message.Role}'.");
            }
        }

        return result;
    }

    /// <summary>
    /// 将内部工具调用序列转换为 SDK 的函数调用对象。
    /// Converts internal tool calls into SDK function-call objects.
    /// </summary>
    private static IEnumerable<OpenAI.Chat.ChatToolCall> ToSdkToolCalls(IReadOnlyList<ChatToolCall> calls) =>
        calls.Select(call => OpenAI.Chat.ChatToolCall.CreateFunctionToolCall(call.Id, call.FunctionName,
                                                                             BinaryData
                                                                                .FromString(call.ArgumentsJson)));
}

using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;
using System.ClientModel;
using TinyHarness.Core.Models.Configuration;

namespace TinyHarness.Core.Services.ChatCompletions;

/// <summary>
///     按配置创建模型客户端的组合入口：CLI run、MCP host 与 doctor --connect 共用同一创建逻辑。
///     The single composition entry for model clients, shared by CLI run, the MCP
///     host, and doctor --connect.
/// </summary>
public static class ModelClientFactory
{
    /// <summary>
    ///     验证模型、端点与密钥，并按所选协议构造 Chat Completions 客户端。
    ///     返回的 <see cref="MicrosoftAiChatClient" /> 持有底层 M.E.AI IChatClient 并实现
    ///     IDisposable；<see cref="IChatCompletionClient" /> 接口本身无释放语义，CLI 组合根的
    ///     客户端与进程同生命周期（与旧 SDK 客户端一致），不需要显式释放。
    ///     Validates model, endpoint, and key, then creates the client for the
    ///     selected protocol. The returned <see cref="MicrosoftAiChatClient" /> owns
    ///     the underlying M.E.AI IChatClient and implements IDisposable; the
    ///     <see cref="IChatCompletionClient" /> interface itself carries no dispose
    ///     semantics, and the CLI composition roots keep their client for the whole
    ///     process lifetime (as with the previous SDK client), so they never
    ///     dispose it explicitly.
    /// </summary>
    public static IChatCompletionClient Create(
        string model, string endpoint, string apiKey, ChatApiKind api = ChatApiKind.ChatCompletions)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("A model is required.", nameof(model));

        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("An endpoint is required.", nameof(endpoint));

        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("An API key is required.", nameof(apiKey));

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"Endpoint must be an absolute http(s) URL, got '{endpoint}'.",
                                        nameof(endpoint));

        // Ensure the SDK appends "chat/completions" to the base URL instead of
        // gluing it onto the last path segment.
        var baseUri = new Uri(endpoint.TrimEnd('/') + "/");

        switch (api)
        {
            case ChatApiKind.ChatCompletions :
                var chatClient = new ChatClient(model, new ApiKeyCredential(apiKey),
                                                new OpenAIClientOptions { Endpoint = baseUri });
                return new MicrosoftAiChatClient(chatClient.AsIChatClient());

            case ChatApiKind.Responses :
                // OPENAI001（Responses 属实验 API）在此最小范围关闭：PLAN §21 已离线验证该
                // 传输路径，并经 win-x64 NativeAOT 发布确认零 trim/AOT 警告；探针见
                // artifacts/protocol-compat/ProviderProbe.cs。
                // OPENAI001 (experimental Responses API) is suppressed for this
                // minimal scope only: PLAN §21 verified this transport offline and
                // confirmed zero trim/AOT warnings in a win-x64 NativeAOT publish;
                // see the probe in artifacts/protocol-compat/ProviderProbe.cs.
#pragma warning disable OPENAI001
                var responsesClient = new ResponsesClient(new ApiKeyCredential(apiKey),
                                                          new ResponsesClientOptions { Endpoint = baseUri });
                return new MicrosoftAiChatClient(responsesClient.AsIChatClient(model), api);
#pragma warning restore OPENAI001

            default :
                throw new ArgumentOutOfRangeException(nameof(api), api, "Unknown chat API kind.");
        }
    }
}

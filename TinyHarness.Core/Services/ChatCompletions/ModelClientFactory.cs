using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;
using System.ClientModel;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Core.Services.ChatCompletions;

/// <summary>
///     按配置创建模型客户端的组合入口：CLI run、MCP host 与 doctor --connect 共用同一创建逻辑。
///     The single composition entry for model clients, shared by CLI run, the MCP
///     host, and doctor --connect.
/// </summary>
public static class ModelClientFactory
{
    /// <summary>
    ///     <para>普通对话和 AI 审批共用的单次模型请求总时限。</para>
    ///     <para>The shared end-to-end timeout for one chat or AI approval model request.</para>
    /// </summary>
    internal static TimeSpan ModelRequestTimeout { get; } = TimeSpan.FromMinutes(5);

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
        string            model, string endpoint, string apiKey, ChatApiKind api = ChatApiKind.ChatCompletions,
        NetworkSettings?  networkSettings = null, NetworkRoute? providerNetwork = null,
        ICredentialStore? credentials     = null)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("A model is required.", nameof(model));

        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("An endpoint is required.", nameof(endpoint));

        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("An API key is required.", nameof(apiKey));

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Endpoint must be an absolute http(s) URL.", nameof(endpoint));

        // Ensure the SDK appends "chat/completions" to the base URL instead of
        // gluing it onto the last path segment.
        var baseUri       = new Uri(endpoint.TrimEnd('/') + "/");
        var transport     = ProviderTransportFactory.Create(networkSettings, providerNetwork, credentials);
        var routeIdentity = NetworkRouteResolver.BindingIdentity(transport.Route);
        try
        {
            var options = new OpenAIClientOptions
            {
                Endpoint       = baseUri,
                Transport      = transport.Transport,
                NetworkTimeout = ModelRequestTimeout
            };
            switch (api)
            {
                case ChatApiKind.ChatCompletions :
                    var chatClient = new ChatClient(model, new ApiKeyCredential(apiKey), options);
                    return new MicrosoftAiChatClient(chatClient.AsIChatClient(), ownedResource : transport,
                                                     networkRouteIdentity : routeIdentity);

                case ChatApiKind.Responses :
                    // OPENAI001（Responses 属实验 API）在此最小范围关闭：传输仍使用相同的显式网络路由。
#pragma warning disable OPENAI001
                    var responsesOptions = new ResponsesClientOptions
                    {
                        Endpoint       = baseUri,
                        Transport      = transport.Transport,
                        NetworkTimeout = ModelRequestTimeout
                    };
                    var responsesClient = new ResponsesClient(new ApiKeyCredential(apiKey), responsesOptions);
                    return new MicrosoftAiChatClient(responsesClient.AsIChatClient(model), api, transport,
                                                     routeIdentity);
#pragma warning restore OPENAI001
                default :
                    throw new ArgumentOutOfRangeException(nameof(api), api, "Unknown chat API kind.");
            }
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }
}

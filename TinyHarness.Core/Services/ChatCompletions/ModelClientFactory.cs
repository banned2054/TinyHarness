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
    /// <remarks>
    ///     <para>
    ///         <see cref="OpenAIClientOptions.NetworkTimeout" /> 承载每次网络操作保护（请求发出到
    ///         响应头，以及流式期间单次读取的停滞上限），不是响应总期限；配置把空闲保护设为 0
    ///         （关闭）时，仍保留 300 秒的每操作默认保护。
    ///     </para>
    ///     <para>
    ///         <see cref="OpenAIClientOptions.NetworkTimeout" /> carries per-network-operation
    ///         protection (request send through response headers, plus a per-read stall limit while
    ///         streaming) rather than an end-to-end response deadline; when the idle guard is
    ///         disabled (zero), the 300-second per-operation protection is kept.
    ///     </para>
    /// </remarks>
    /// <param name="timeouts">
    ///     <para>模型超时期限；null 使用 <see cref="ModelTimeouts.Default" />。</para>
    ///     <para>Model timeout limits; null selects <see cref="ModelTimeouts.Default" />.</para>
    /// </param>
    public static IChatCompletionClient Create(
        string            model, string endpoint, string apiKey, ChatApiKind api = ChatApiKind.ChatCompletions,
        NetworkSettings?  networkSettings = null, NetworkRoute? providerNetwork = null,
        ICredentialStore? credentials     = null, ModelTimeouts?  timeouts = null)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("A model is required.", nameof(model));

        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("An endpoint is required.", nameof(endpoint));

        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("An API key is required.", nameof(apiKey));

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Endpoint must be an absolute http(s) URL.", nameof(endpoint));

        var effectiveTimeouts = timeouts ?? ModelTimeouts.Default;

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
                NetworkTimeout = PerOperationTimeout(effectiveTimeouts)
            };
            switch (api)
            {
                case ChatApiKind.ChatCompletions :
                    var chatClient = new ChatClient(model, new ApiKeyCredential(apiKey), options);
                    return new MicrosoftAiChatClient(chatClient.AsIChatClient(), api, transport, routeIdentity,
                                                     effectiveTimeouts, null);

                case ChatApiKind.Responses :
                    // OPENAI001（Responses 属实验 API）在此最小范围关闭：传输仍使用相同的显式网络路由。
#pragma warning disable OPENAI001
                    var responsesOptions = new ResponsesClientOptions
                    {
                        Endpoint       = baseUri,
                        Transport      = transport.Transport,
                        NetworkTimeout = PerOperationTimeout(effectiveTimeouts)
                    };
                    var responsesClient = new ResponsesClient(new ApiKeyCredential(apiKey), responsesOptions);
                    return new MicrosoftAiChatClient(responsesClient.AsIChatClient(model), api, transport,
                                                     routeIdentity, effectiveTimeouts, null);
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

    /// <summary>
    ///     <para>把空闲保护换算成每次网络操作上限；空闲保护关闭时保留 300 秒默认保护。</para>
    ///     <para>
    ///         Converts the idle guard into the per-operation limit; a disabled idle guard keeps the
    ///         300-second default protection.
    ///     </para>
    /// </summary>
    private static TimeSpan PerOperationTimeout(ModelTimeouts timeouts) =>
        TimeSpan.FromSeconds(timeouts.StreamIdleTimeoutSeconds > 0 ? timeouts.StreamIdleTimeoutSeconds : 300);
}

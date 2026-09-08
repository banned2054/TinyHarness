namespace TinyHarness.Core.ChatCompletions;

/// <summary>
/// Agent Loop 调用模型的最小协议边界，使主循环不依赖具体供应商 SDK。
///
/// The narrow protocol through which the Agent Loop talks to a model. The loop
/// depends only on this interface, never on a concrete vendor SDK type.
/// </summary>
public interface IChatCompletionClient
{
    /// <summary>
    /// 异步产生一次 Chat Completions 请求的结构化流事件，并响应取消请求。
    /// Asynchronously yields structured stream events for one Chat Completions request and honors cancellation.
    /// </summary>
    IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request, CancellationToken cancellationToken);
}

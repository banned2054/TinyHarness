namespace TinyHarness.Core.ChatCompletions;

/// <summary>
/// The narrow protocol through which the Agent Loop talks to a model. The loop
/// depends only on this interface, never on a concrete vendor SDK type.
/// </summary>
public interface IChatCompletionClient
{
    IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request, CancellationToken cancellationToken);
}

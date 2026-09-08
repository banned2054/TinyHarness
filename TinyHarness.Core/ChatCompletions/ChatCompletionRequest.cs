using TinyHarness.Core.Tools;

namespace TinyHarness.Core.ChatCompletions;

/// <summary>
/// 发送到 Chat Completions 端点的内部请求，只建模基线协议确实需要的字段。
///
/// A request to the Chat Completions endpoint. Only the fields required by the
/// baseline protocol are modelled; unsupported optional features are omitted
/// rather than guessed.
/// </summary>
public sealed record ChatCompletionRequest
{
    public required string Model { get; init; }

    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    public IReadOnlyList<ToolDefinition>? Tools { get; init; }
}

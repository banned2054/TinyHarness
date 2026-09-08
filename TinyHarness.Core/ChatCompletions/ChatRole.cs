namespace TinyHarness.Core.ChatCompletions;

/// <summary>
/// 遵循 OpenAI-compatible Chat Completions 语义的消息角色。
///
/// The role of a message in a chat completion conversation,
/// following the OpenAI-compatible Chat Completions semantics.
/// </summary>
public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool,
}

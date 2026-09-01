namespace TinyHarness.Core.ChatCompletions;

/// <summary>
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

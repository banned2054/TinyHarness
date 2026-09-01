namespace TinyHarness.Core.ChatCompletions;

/// <summary>
/// A single tool call requested by the model in an assistant message.
/// A round may contain multiple tool calls, which are executed sequentially.
/// </summary>
public sealed record ChatToolCall(string Id, string FunctionName, string ArgumentsJson);

namespace TinyHarness.Core.ChatCompletions;

/// <summary>
/// assistant 消息请求的单个工具调用；同一轮可包含多个调用，并按顺序执行。
///
/// A single tool call requested by the model in an assistant message.
/// A round may contain multiple tool calls, which are executed sequentially.
/// </summary>
public sealed record ChatToolCall(string Id, string FunctionName, string ArgumentsJson);

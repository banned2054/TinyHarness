using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Tools;

namespace TinyHarness.Core.Context;

/// <summary>
/// 保守、确定性的 token 估算器（不依赖任何 tokenizer 库）。宽字符（CJK 等）按一字一
/// token、其余文本按每 4 字符一 token 向上取整估算，并对每条消息和工具调用附加固定开销，
/// 使估算值偏向高估而不是低估，避免压缩触发过晚。
///
/// Conservative, deterministic token estimator with no tokenizer dependency. Wide
/// characters (CJK and friends) count as one token each and remaining text is
/// estimated at one token per four characters, rounded up; every message and tool
/// call also carries a fixed overhead, biasing the estimate upward so compaction
/// never triggers too late.
/// </summary>
public static class TokenEstimator
{
    private const int MessageOverheadTokens        = 3;
    private const int ToolCallOverheadTokens       = 3;
    private const int ToolDefinitionOverheadTokens = 8;

    /// <summary>
    /// 估算纯文本的 token 数；空文本返回 0。
    /// Estimates the token count of plain text; empty text costs zero.
    /// </summary>
    public static int EstimateText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var wideTokens  = 0;
        var narrowChars = 0;
        foreach (var character in text)
        {
            if (IsWideCharacter(character))
            {
                wideTokens++;
            }
            else
            {
                narrowChars++;
            }
        }

        return wideTokens + (narrowChars + 3) / 4;
    }

    /// <summary>
    /// 估算单条协议消息（含 role、内容、tool-call 参数与 ID 的开销）。
    /// Estimates one protocol message including role, content, and tool-call overhead.
    /// </summary>
    public static int EstimateMessage(ChatMessage message)
    {
        var tokens = MessageOverheadTokens + EstimateText(message.Content);
        if (message.Role == ChatRole.Tool)
        {
            tokens += EstimateText(message.Name       ?? string.Empty);
            tokens += EstimateText(message.ToolCallId ?? string.Empty);
        }

        if (message.ToolCalls is { Count: > 0 } calls)
        {
            foreach (var call in calls)
            {
                tokens += ToolCallOverheadTokens + EstimateText(call.FunctionName) +
                          EstimateText(call.ArgumentsJson);
            }
        }

        return tokens;
    }

    /// <summary>
    /// 估算一组消息的总 token 数。
    /// Estimates the total token count of a message list.
    /// </summary>
    public static int EstimateMessages(IEnumerable<ChatMessage> messages)
    {
        var tokens = 0;
        foreach (var message in messages)
        {
            tokens += EstimateMessage(message);
        }

        return tokens;
    }

    /// <summary>
    /// 估算随请求发送的工具定义的 token 数，作为请求固定开销参与预算计算。
    /// Estimates the token cost of the tool definitions attached to a request.
    /// </summary>
    public static int EstimateToolDefinitions(IReadOnlyList<ToolDefinition> tools)
    {
        var tokens = 0;
        foreach (var tool in tools)
        {
            tokens += ToolDefinitionOverheadTokens + EstimateText(tool.Name) + EstimateText(tool.Description) +
                      EstimateText(tool.Parameters.ToJsonString());
        }

        return tokens;
    }

    /// <summary>
    /// 宽字符（每字符约一 token）的粗略判断：CJK 统一表意文字、兼容表意文字、假名/谚文、
    /// 全角与 CJK 标点区间。
    ///
    /// Coarse wide-character test (roughly one token per character): CJK unified
    /// and compatibility ideographs, kana/hangul, full-width forms, and CJK punctuation.
    /// </summary>
    private static bool IsWideCharacter(char value) =>
        value is (>= '\u2E80' and <= '\u9FFF')
              or (>= '\uAC00' and <= '\uD7AF')
              or (>= '\uF900' and <= '\uFAFF')
              or (>= '\uFF00' and <= '\uFFEF')
              or (>= '\u3000' and <= '\u303F');
}

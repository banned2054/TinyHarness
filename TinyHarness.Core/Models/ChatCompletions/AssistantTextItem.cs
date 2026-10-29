namespace TinyHarness.Core.Models.ChatCompletions;

/// <summary>
///     <para>保留助手文本的消息边界与协议阶段；缺少阶段时才按工具调用推断用途。</para>
///     <para>Preserves assistant text boundaries and protocol phases; tool calls infer purpose only when phase is absent.</para>
/// </summary>
public sealed record AssistantTextItem(string Text, string? MessageId = null, string? Phase = null)
{
    /// <summary>
    ///     <para>显式回答阶段属于结果；未知阶段保守归入过程，不按工具调用覆盖。</para>
    ///     <para>Explicit answer phases belong to the result; unknown phases remain process text without tool-call inference.</para>
    /// </summary>
    public bool IsAnswer(bool hasToolCalls) => Phase switch
    {
        null                               => !hasToolCalls,
        "partial_answer" or "final_answer" => true,
        _                                  => false
    };
}

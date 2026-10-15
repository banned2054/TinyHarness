using TinyHarness.Core.Models.ChatCompletions;

namespace TinyHarness.Core.Services.Agent;

/// <summary>
///     <para>
///         提供执行前与会话恢复共享的对话协议不变量，避免校验规则漂移。
///     </para>
///     <para>
///         Provides shared conversation protocol invariants for execution and session restoration
///         to prevent validation drift.
///     </para>
/// </summary>
internal static class ChatHistoryInvariants
{
    /// <summary>
    ///     <para>
    ///         要求历史中每个 assistant 工具调用都有对应结果，拒绝不完整工具原子组。
    ///     </para>
    ///     <para>
    ///         Requires a matching result for every historical assistant tool call, rejecting incomplete atomic groups.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         须在追加新输入前校验；取消或失败留下的缺失结果不能补造或继续发给模型。
    ///     </para>
    ///     <para>
    ///         Validate before appending new input; results missing after cancellation or failure
    ///         must neither be fabricated nor sent on as invalid history.
    ///     </para>
    /// </remarks>
    public static void EnsureCompleteToolGroups(IReadOnlyList<ChatMessage> messages)
    {
        HashSet<string>? pending = null;
        foreach (var message in messages)
            switch (message.Role)
            {
                case ChatRole.Assistant :
                    if (pending is { Count: > 0 }) throw IncompleteTail(pending);

                    pending = message.ToolCalls is { Count: > 0 }
                        ? new HashSet<string>(message.ToolCalls.Select(call => call.Id), StringComparer.Ordinal)
                        : null;
                    break;

                case ChatRole.Tool :
                    if (pending is not null && message.ToolCallId is { } id) pending.Remove(id);
                    break;

                case ChatRole.User or ChatRole.System :
                    if (pending is { Count: > 0 }) throw IncompleteTail(pending);
                    break;
            }

        if (pending is { Count: > 0 }) throw IncompleteTail(pending);
        return;

        static InvalidOperationException IncompleteTail(IReadOnlyCollection<string> pending) =>
            new("The conversation contains an incomplete tool round: assistant tool call(s) "                +
                string.Join(", ", pending)                                                                   +
                " have no tool result. The previous run was cancelled or failed mid-round, so the executed " +
                "work may have an unknown state; continuing would send malformed history to the model. "     +
                "Inspect the workspace before starting new work.");
    }

    /// <summary>
    ///     <para>
    ///         计算不拆分工具原子组的最近完整协议边界。
    ///     </para>
    ///     <para>
    ///         Finds the latest complete protocol boundary without splitting atomic tool groups.
    ///     </para>
    /// </summary>
    /// <returns>
    ///     <para>
    ///         完整历史返回消息总数；仅尾组缺结果时返回开启该组的 assistant 下标，
    ///         供调用方整体回退到该组之前；非尾部出现不完整组时返回 -1，须按损坏处理。
    ///     </para>
    ///     <para>
    ///         Returns the message count for complete history, or the opening assistant index
    ///         when only the trailing group lacks results, so callers rewind before the whole group.
    ///         Returns -1 for a non-trailing incomplete group, which must be treated as corruption.
    ///     </para>
    /// </returns>
    public static int LastCompleteBoundary(IReadOnlyList<ChatMessage> messages)
    {
        var              pendingStart = -1;
        HashSet<string>? pending      = null;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            switch (message.Role)
            {
                case ChatRole.Assistant :
                    if (pending is { Count: > 0 }) return -1;

                    if (message.ToolCalls is { Count: > 0 })
                    {
                        pending = new HashSet<string>(message.ToolCalls.Select(call => call.Id),
                                                      StringComparer.Ordinal);
                        pendingStart = index;
                    }
                    else
                    {
                        pending      = null;
                        pendingStart = -1;
                    }

                    break;

                case ChatRole.Tool :
                    if (pending is not null && message.ToolCallId is { } id) pending.Remove(id);
                    break;

                case ChatRole.User or ChatRole.System :
                    if (pending is { Count: > 0 }) return -1;
                    break;
            }
        }

        return pending is { Count: > 0 } ? pendingStart : messages.Count;
    }
}

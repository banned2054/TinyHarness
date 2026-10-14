using TinyHarness.Core.Models.ChatCompletions;

namespace TinyHarness.Core.Services.Agent;

/// <summary>
///     对话历史必须满足的协议不变量，由执行前校验（<see cref="AgentLoop" />）与跨进程恢复
///     （会话续聊）共用同一实现，避免两处规则漂移。
///     Protocol invariants a conversation history must satisfy, shared by the
///     pre-execution check (<see cref="AgentLoop" />) and the cross-process
///     restoration (session resume) so the two rules can never drift apart.
/// </summary>
internal static class ChatHistoryInvariants
{
    /// <summary>
    ///     校验历史中不存在不完整的工具原子组：每个 assistant tool_calls 的每个调用都必须
    ///     有对应的 tool 结果消息。取消或失败可能留下已请求但未回传结果的调用；把这样的
    ///     历史继续发给模型是无效请求，必须在新输入追加前拒绝。
    ///     Verifies the history carries no incomplete tool group: every assistant
    ///     tool call must have a matching tool-result message. A cancel or failure
    ///     can leave calls whose results never arrived; continuing would send
    ///     invalid history, so this is refused before any new input is appended.
    /// </summary>
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
}

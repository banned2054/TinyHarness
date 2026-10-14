using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.Context;

namespace TinyHarness.Core.Services.Session;

/// <summary>
///     一次成功恢复的结果：重建的会话（身份、全部历史回合与运行终态）与回填了完整历史、
///     压缩状态和折叠边界的对话上下文。两者交给 <see cref="ChatSessionHost" /> 的恢复构造
///     路径继续服务。
///     One successful restoration: the rebuilt session (identity, every past turn
///     with its run terminals) and the conversation context rehydrated with the
///     full history, compaction state and fold boundaries. Both are handed to
///     <see cref="ChatSessionHost" />'s resume construction to keep serving.
/// </summary>
public sealed record SessionRestoration(ChatSession Session, ConversationContext Context);

/// <summary>
///     会话续聊的恢复端（M13.2，PLAN §7.2/§7.3）：把受保护存储中的版本化快照重建为可继续的
///     会话与上下文。恢复是纯函数——不解析凭据、不调用模型、不启动工具、不写任何文件；所有
///     检查失败都转换为有上下文的 <see cref="SessionStoreException" />，绝不猜测解释或降级放行。
///     检查覆盖：opaque 完整性哈希、工作区与模型/协议身份、系统提示一致、完整工具原子组、
///     压缩边界与状态可解析、回合终态可识别。仅 v3 的保真协议内容用于续聊；旧 v1/v2 可能
///     已被脱敏，无法反推出原值，明确拒绝恢复。旧 <see cref="PersistedConversation.OpaqueRedacted" />
///     标记仍拒绝续传被改变的 opaque 值；检查视图可经 `session show` 离线查看并手工交接。
///     以不完整工具组结尾的会话（最后一次运行取消或失败于执行中途）同样拒绝：恢复到最近
///     完整协议边界与中断检查属于 M13.3，本切片不重放状态不明的执行。
///     The resume side of session continuation (M13.2, PLAN §7.2/§7.3): rebuilds
///     a versioned snapshot from the protected store into a continuable session
///     and context. Restoration is a pure function — it resolves no credentials,
///     calls no model, starts no tool and writes no file; every failed check
///     becomes a contextual <see cref="SessionStoreException" /> instead of a
///     guess or a silent downgrade. The checks cover the opaque integrity
///     hashes, the workspace and model/protocol identity, the system prompt,
///     complete tool groups, parseable compaction boundaries and state, and
///     recognizable run terminals. Only v3's verbatim protocol content can
///     resume; older v1/v2 snapshots may have been redacted and cannot recover
///     their original values. The legacy
///     <see cref="PersistedConversation.OpaqueRedacted" /> marker still refuses
///     replay of changed opaque values. Inspection stays available offline via
///     `session show` for an explicit manual handover. A
///     session ending in an incomplete tool group (its last run was cancelled or
///     failed mid-execution) is refused as well: recovery to the nearest
///     complete protocol boundary with interruption inspection belongs to M13.3,
///     and this slice never replays execution of unknown state.
/// </summary>
public static class SessionResume
{
    /// <summary>
    ///     校验快照并重建会话与上下文。当前工作区与模型身份必须与快照一致（会话固定工作区与
    ///     模型协议身份，PLAN §5）；系统提示必须与快照首条 system 消息完全一致。快照来自
    ///     <see cref="Persistence.SessionStoreWriter.OpenExistingAsync" /> 时已通过严格读取与审计
    ///     对账，本方法不再读盘。
    ///     Validates the snapshot and rebuilds the session and context. The
    ///     current workspace and model identity must match the snapshot (a session
    ///     is fixed to one workspace and one model/protocol identity, PLAN §5);
    ///     the system prompt must equal the snapshot's first system message
    ///     verbatim. Snapshots coming from
    ///     <see cref="Persistence.SessionStoreWriter.OpenExistingAsync" /> already
    ///     passed the strict read with audit reconciliation; this method reads no
    ///     files itself.
    /// </summary>
    public static SessionRestoration Restore(
        PersistedSession snapshot, string workspaceRoot, SessionModelIdentity model, string systemPrompt,
        ContextOptions?  contextOptions = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);

        var conversation = snapshot.Conversation
                        ?? throw new
                               SessionStoreException($"Stored session '{snapshot.Id}' (format version {snapshot.FormatVersion}) carries " +
                                                     "no resumable conversation; it was stored without message content and stays " +
                                                     "inspection-only. Start a new session instead.");

        if (conversation.OpaqueRedacted)
            throw new
                SessionStoreException($"Stored session '{snapshot.Id}' cannot be resumed losslessly: known-secret " +
                                      "redaction touched its opaque reasoning payloads, and replaying a changed value " +
                                      "would forge it (PLAN §7.2). Inspect it with 'session show' and hand the needed " +
                                      "context over explicitly in a new session.");

        if (snapshot.FormatVersion < PersistedSession.CurrentFormatVersion)
            throw new
                SessionStoreException($"Stored session '{snapshot.Id}' (format version {snapshot.FormatVersion}) stays " +
                                      "inspection-only: its conversation may have been redacted before saving, so " +
                                      "verbatim protocol replay cannot be guaranteed. Inspect it with 'session show' " +
                                      "and start a new session instead.");

        foreach (var message in conversation.Messages)
        {
            if (message.Reasoning is null) continue;

            foreach (var reasoning in message.Reasoning)
                if (!string.Equals(PersistedReasoning.ComputeOpaqueHash(reasoning.ProtectedData, reasoning.ItemId),
                                   reasoning.OpaqueHash, StringComparison.Ordinal))
                    throw new
                        SessionStoreException($"Stored session '{snapshot.Id}' fails its opaque-data integrity check: a " +
                                              "reasoning payload does not match its recorded hash, so the snapshot is " +
                                              "damaged or was tampered with and cannot be replayed to the model.");
        }

        if (!SamePath(snapshot.WorkspaceRoot, workspaceRoot))
            throw new
                SessionStoreException($"Stored session '{snapshot.Id}' belongs to workspace '{snapshot.WorkspaceRoot}' but " +
                                      $"the current workspace is '{Path.GetFullPath(workspaceRoot)}'. A session is fixed " +
                                      "to one workspace (PLAN §5); reopen it from there or start a new session.");

        if (snapshot.Model is null || !snapshot.Model.Equals(model))
            throw new
                SessionStoreException($"Stored session '{snapshot.Id}' belongs to model "                         +
                                      DescribeModel(snapshot.Model)                                               +
                                      " but the current configuration uses "                                      +
                                      DescribeModel(model)                                                        +
                                      ". Resume requires the same endpoint, model and chat API; opaque protocol " +
                                      "content is never silently replayed under a different identity (PLAN §5). " +
                                      "Restore the configuration or start a new session.");

        var messages = new List<ChatMessage>(conversation.Messages.Count);
        foreach (var persisted in conversation.Messages)
        {
            if (!Enum.TryParse<ChatRole>(persisted.Role, out var role))
                throw new
                    SessionStoreException($"Stored session '{snapshot.Id}' is damaged: message role '{persisted.Role}' " +
                                          "is not a known conversation role.");

            messages.Add(new ChatMessage
            {
                Role       = role,
                Content    = persisted.Content ?? string.Empty,
                ToolCallId = persisted.ToolCallId,
                Name       = persisted.Name,
                ToolCalls = persisted.ToolCalls?
                                     .Select(call => new ChatToolCall(call.Id, call.Name, call.Arguments))
                                     .ToArray(),
                Reasoning = persisted.Reasoning?
                                     .Select(entry => new ReasoningContent(entry.Text, entry.ProtectedData,
                                                                           entry.ItemId))
                                     .ToArray()
            });
        }

        if (messages.Count == 0)
            throw new
                SessionStoreException($"Stored session '{snapshot.Id}' has an empty conversation; there is nothing to resume.");

        if (messages[0].Role != ChatRole.System ||
            !string.Equals(messages[0].Content, systemPrompt, StringComparison.Ordinal))
            throw new
                SessionStoreException($"Stored session '{snapshot.Id}' was recorded under different session instructions; " +
                                      "continuing a session requires the same system prompt. Start a new session instead.");

        try
        {
            ChatHistoryInvariants.EnsureCompleteToolGroups(messages);
        }
        catch (InvalidOperationException ex)
        {
            throw new
                SessionStoreException($"Stored session '{snapshot.Id}' ends with an incomplete tool round: its last run " +
                                      "was cancelled or failed mid-execution, and resuming would send malformed history " +
                                      $"to the model. {ex.Message}", ex);
        }

        var state = StructuredState.Empty;
        if (conversation.HasState &&
            (string.IsNullOrWhiteSpace(conversation.StateJson) ||
             !StructuredState.TryParse(conversation.StateJson, out state)))
            throw new
                SessionStoreException($"Stored session '{snapshot.Id}' is damaged: its compacted state cannot be parsed " +
                                      "back, so the fold boundaries cannot be restored.");

        var context = new ConversationContext(contextOptions);
        try
        {
            context.Restore(messages, state, conversation.HasState, conversation.FoldStartMessageIndex,
                            conversation.FoldEndMessageIndex);
        }
        catch (ArgumentException ex)
        {
            throw new
                SessionStoreException($"Stored session '{snapshot.Id}' is damaged: its fold position does not agree with " +
                                      $"the stored message history. {ex.Message}", ex);
        }

        return new SessionRestoration(RebuildSession(snapshot), context);
    }

    /// <summary>
    ///     从检查视图重建会话与全部历史回合/运行：终态枚举不可识别的快照按损坏拒绝；
    ///     未结束的运行按原样恢复（不补造终态）。
    ///     Rebuilds the session with every past turn and run from the inspection
    ///     view: an unrecognizable terminal status rejects the snapshot as damaged;
    ///     unfinished runs are restored as-is (terminals are never fabricated).
    /// </summary>
    private static ChatSession RebuildSession(PersistedSession snapshot)
    {
        var session = new ChatSession(snapshot.Id, snapshot.WorkspaceRoot, snapshot.Model!, snapshot.CreatedUtc);
        foreach (var turn in snapshot.Turns)
        {
            var restoredTurn = new SessionTurn(turn.Id, turn.UserInput, turn.CreatedUtc);
            foreach (var run in turn.Runs)
            {
                var restoredRun = new SessionRun(run.Id, snapshot.Id, turn.Id, run.StartedUtc);
                if (run.Status is not null)
                {
                    if (!Enum.TryParse<AgentStatus>(run.Status, out var status))
                        throw new
                            SessionStoreException($"Stored session '{snapshot.Id}' is damaged: run '{run.Id}' carries " +
                                                  $"unknown terminal status '{run.Status}'.");

                    restoredRun.Finish(new AgentResult
                    {
                        Status         = status,
                        FinalMessage   = run.FinalMessage ?? string.Empty,
                        Error          = run.Error,
                        Steps          = run.Steps,
                        ToolExecutions = run.ToolExecutions,
                        InputTokens    = run.InputTokens,
                        OutputTokens   = run.OutputTokens
                    }, run.FinishedUtc ?? run.StartedUtc);
                }

                restoredTurn.AddRun(restoredRun);
            }

            session.AddTurn(restoredTurn);
        }

        return session;
    }

    private static bool SamePath(string left, string right)
    {
        var fullLeft  = Path.GetFullPath(left);
        var fullRight = Path.GetFullPath(right);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(fullLeft, fullRight, comparison) ||
               string.Equals(fullLeft.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                             fullRight.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                             comparison);
    }

    private static string DescribeModel(SessionModelIdentity? model) =>
        model is null
            ? "(unknown)"
            : $"'{model.ModelId}' @ '{model.Endpoint}' ({model.ChatApi})";
}

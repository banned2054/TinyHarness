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
///     <para>已恢复的会话与上下文，供 <see cref="ChatSessionHost" /> 接管续聊。</para>
///     <para>A restored session and context for <see cref="ChatSessionHost" /> to take over and continue.</para>
/// </summary>
/// <remarks>
///     <para>
///         <see cref="NeedsInspection" /> 非空表示仍须用户检查确认，由宿主建立非只读阻断；
///         <see cref="RewoundTail" /> 表示不完整尾部工具组已整体移出模型视图。
///     </para>
///     <para>
///         A non-null <see cref="NeedsInspection" /> requires user inspection and acknowledgement,
///         prompting the host to block non-read-only capabilities. <see cref="RewoundTail" /> indicates
///         an incomplete trailing tool group was removed whole from the model view.
///     </para>
/// </remarks>
public sealed record SessionRestoration(
    ChatSession Session, ConversationContext Context, string? NeedsInspection = null, bool RewoundTail = false);

/// <summary>
///     <para>校验会话日志投影并重建可续聊的会话与上下文，不解析凭据、调用模型、启动工具或读写文件。</para>
///     <para>
///         Validates session-log projections and rebuilds resumable sessions and contexts without resolving
///         credentials, calling models, starting tools or reading or writing files.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         仅接受保真协议内容；opaque 哈希不符时拒绝续聊，
///         不伪造原值。模型视图使用校验后的 canonical 历史，不以脱敏检查投影替代。
///     </para>
///     <para>
///         Only verbatim protocol content can resume.
///         Opaque hash mismatches refuse resume without forging original values.
///         The model view uses validated canonical history, never a redacted inspection projection.
///     </para>
///     <para>
///         不完整尾部工具组整体回退到最近完整边界；历史中段不完整则按损坏拒绝。
///         未知副作用或中断尾部缺少执行记录时保守阻断，不补造结果或退出码。
///     </para>
///     <para>
///         Incomplete trailing tool groups rewind whole to the last complete boundary;
///         incomplete groups within history are rejected as damage. Unknown side effects or interrupted tails
///         without records in session-log projections block conservatively, without fabricated results or exit codes.
///     </para>
/// </remarks>
public static class SessionResume
{
    /// <summary>
    ///     <para>校验快照身份、协议与压缩状态，重建会话及安全边界内的上下文。</para>
    ///     <para>
    ///         Validates snapshot identity, protocol and compaction state, rebuilding the session
    ///         and context within a safe boundary.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         当前工作区、模型与协议身份须匹配快照，系统提示须与首条 system 消息完全一致。
    ///         工具组、opaque 哈希、折叠边界、交接摘要来源与运行记录均须有效。
    ///     </para>
    ///     <para>
    ///         The current workspace, model and protocol identity must match the snapshot;
    ///         the system prompt must exactly match its first system message. Tool groups, opaque hashes,
    ///         fold boundaries, handoff sources and run records must be valid.
    ///     </para>
    /// </remarks>
    /// <param name="snapshot">
    ///     <para>
    ///         应由 <see cref="Persistence.SessionStoreWriter.OpenExistingAsync" /> 严格读取并完成日志校验；
    ///         本方法不读盘或复核日志。
    ///     </para>
    ///     <para>
    ///         Should be strictly read and log-validated by
    ///         <see cref="Persistence.SessionStoreWriter.OpenExistingAsync" />;
    ///         this method neither reads files nor rechecks the log.
    ///     </para>
    /// </param>
    /// <exception cref="SessionStoreException">
    ///     <para>快照不可续聊、身份不一致或恢复内容校验失败；不降级放行。</para>
    ///     <para>
    ///         The snapshot is not resumable, identity differs, or restored content fails validation;
    ///         no fallback is allowed.
    ///     </para>
    /// </exception>
    public static SessionRestoration Restore(PersistedSession     snapshot, string workspaceRoot,
                                             SessionModelIdentity model,    string systemPrompt,
                                             ContextOptions?      contextOptions = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);

        var conversation = snapshot.Conversation
                        ?? throw new
                               SessionStoreException($"Stored session '{snapshot.Id}' carries " +
                                                     "no resumable conversation; it was stored without message content and stays " +
                                                     "inspection-only. Start a new session instead.");

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

        var selectedModel = snapshot.ModelSelection?.Model ?? snapshot.Model;
        if (selectedModel is null || !selectedModel.Equals(model))
            throw new
                SessionStoreException($"Stored session '{snapshot.Id}' belongs to model "                         +
                                      DescribeModel(selectedModel)                                                +
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
                ToolCalls = persisted.ToolCalls?.Select(call => new ChatToolCall(call.Id, call.Name, call.Arguments))
                                     .ToArray(),
                Reasoning = persisted.Reasoning?.Select(entry =>
                                                            new ReasoningContent(entry.Text, entry.ProtectedData,
                                                                entry.ItemId)).ToArray()
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

        var boundary = ChatHistoryInvariants.LastCompleteBoundary(messages);
        if (boundary < 0)
            throw new
                SessionStoreException($"Stored session '{snapshot.Id}' is damaged: it contains an incomplete tool " +
                                      "group that is not the trailing round, so no complete protocol boundary "     +
                                      "exists to resume from.");
        var rewoundTail = boundary < messages.Count;
        if (rewoundTail) messages.RemoveRange(boundary, messages.Count - boundary);

        var needsInspection = PendingInspectionReason(snapshot, rewoundTail);

        var state = conversation.State ?? CompactionState.Empty;

        var checkpointSelection =
            snapshot.ModelSelections.FirstOrDefault(selection =>
                                                       selection.Revision == conversation.CheckpointModelSelectionRevision);
        var checkpointOptions = checkpointSelection?.Context ??
                                snapshot.ModelSelections.FirstOrDefault()?.Context ?? contextOptions;
        var context = new ConversationContext(checkpointOptions);
        try
        {
            context.Restore(messages, state, conversation.HasState, conversation.FoldStartMessageIndex,
                            conversation.FoldEndMessageIndex, conversation.CheckpointModelSelectionRevision);
            context.RestoreProjectInstructions(conversation.ProjectInstructionRecords);
            if (snapshot.ModelSelection is { } selection) context.BindModel(selection);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            throw new
                SessionStoreException($"Stored session '{snapshot.Id}' is damaged: its conversation, fold boundaries, or " +
                                      $"project-instruction source records are inconsistent. {ex.Message}", ex);
        }

        var session = RebuildSession(snapshot);
        session.PendingInspectionReason = needsInspection;
        if (needsInspection is not null) session.InspectionAcknowledgedUtc = null;
        return new SessionRestoration(session, context, needsInspection, rewoundTail);
    }

    /// <summary>
    ///     <para>从持久阻断原因、用户确认与执行事实推导 NeedsInspection，不依据模型输出。</para>
    ///     <para>
    ///         Derives NeedsInspection from the persisted blocking reason, user acknowledgement
    ///         and execution facts, never model output.
    ///     </para>
    /// </summary>
    /// <returns>
    ///     <para>优先保留持久阻断原因；否则已确认时为 null，未知副作用或回退尾部缺少记录时返回阻断原因。</para>
    ///     <para>
    ///         The persisted blocking reason takes precedence; otherwise acknowledgement returns null,
    ///         and unknown side effects or a rewound tail without records return a blocking reason.
    ///     </para>
    /// </returns>
    private static string? PendingInspectionReason(PersistedSession snapshot, bool rewoundTail)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.PendingInspectionReason)) return snapshot.PendingInspectionReason;
        if (snapshot.InspectionAcknowledgedUtc is not null) return null;

        var unknown               = new List<string>();
        var trailingRunHasRecords = snapshot.Turns.LastOrDefault()?.Runs.LastOrDefault()?.ToolCalls is { Count: > 0 };
        foreach (var call in snapshot.Turns.SelectMany(turn => turn.Runs).SelectMany(run => run.ToolCalls ?? []))
        {
            if (!string.Equals(call.State, nameof(ToolCallExecutionState.UnknownResult), StringComparison.Ordinal))
                continue;

            if (call.SideEffects) unknown.Add($"{call.ToolName} ({call.Summary})");
        }

        if (unknown.Count > 0)
            return "an interrupted run left side-effect tools with unknown results — " +
                   string.Join("; ", unknown)                                          +
                   ". The workspace may carry their effects; inspect it before allowing more side effects.";
        if (rewoundTail && !trailingRunHasRecords)
            return "the stored conversation ends in an interrupted tool round whose execution was not recorded " +
                   "and its side effects cannot be proven absent. Inspect the workspace "                        +
                   "before allowing more side effects.";
        return null;
    }

    /// <summary>
    ///     <para>从检查视图重建会话、历史回合、运行记录与检查确认状态。</para>
    ///     <para>
    ///         Rebuilds the session, past turns, run records and inspection acknowledgement
    ///         from the inspection view.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>不补造未结束运行的终态；原样重建执行记录，不可识别的终态或执行状态按损坏拒绝。</para>
    ///     <para>
    ///         Unfinished runs receive no fabricated terminal result; execution records are rebuilt as stored,
    ///         and unrecognized terminal or execution states are rejected as damage.
    ///     </para>
    /// </remarks>
    private static ChatSession RebuildSession(PersistedSession snapshot)
    {
        var session = new ChatSession(snapshot.Id, snapshot.WorkspaceRoot, snapshot.Model!, snapshot.CreatedUtc)
        {
            InspectionAcknowledgedUtc = snapshot.InspectionAcknowledgedUtc,
            PendingInspectionReason   = snapshot.PendingInspectionReason
        };
        foreach (var selection in snapshot.ModelSelections) session.RecordModelSelection(selection);
        foreach (var turn in snapshot.Turns)
        {
            var restoredTurn = new SessionTurn(turn.Id, turn.UserInput, turn.CreatedUtc);
            foreach (var run in turn.Runs)
            {
                run.Budget?.Validate(run.ToolExecutions);
                var restoredRun = new SessionRun(run.Id, snapshot.Id, turn.Id, run.StartedUtc,
                                                 snapshot.ModelSelections.FirstOrDefault(selection =>
                                                     selection.Revision == run.ModelSelectionRevision));
                var records = run.ToolCalls is { Count: > 0 } calls
                    ? calls.Select(call => RebuildToolCallRecord(snapshot.Id, call)).ToArray()
                    : null;
                if (records is not null) restoredRun.RecordExecutionIntent(records);
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
                        OutputTokens   = run.OutputTokens,
                        Budget         = run.Budget,
                        ToolCalls      = records
                    }, run.FinishedUtc ?? run.StartedUtc);
                }

                restoredTurn.AddRun(restoredRun);
            }

            session.AddTurn(restoredTurn);
        }

        foreach (var amendment in snapshot.Amendments) session.RecordAmendment(amendment);
        foreach (var item in snapshot.WorkItems) session.RecordWorkItem(item);
        return session;
    }

    /// <summary>
    ///     <para>重建工具执行记录；不可识别的执行状态按损坏拒绝。</para>
    ///     <para>Rebuilds a tool-execution record; unrecognized execution states are rejected as damage.</para>
    /// </summary>
    private static ToolCallRecord RebuildToolCallRecord(string sessionId, PersistedToolCallRecord call)
    {
        if (!Enum.TryParse<ToolCallExecutionState>(call.State, out var state) || !Enum.IsDefined(state))
            throw new
                SessionStoreException($"Stored session '{sessionId}' is damaged: tool call '{call.CallId}' carries " +
                                      $"unknown execution state '{call.State}'.");

        return new ToolCallRecord
        {
            CallId           = call.CallId,
            InvocationId     = call.InvocationId,
            ToolName         = call.ToolName,
            Capability       = call.Capability,
            SideEffects      = call.SideEffects,
            Summary          = call.Summary,
            PreparationError = call.PreparationError,
            NotStartedReason = call.NotStartedReason,
            Approval         = call.Approval,
            State            = state
        };
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

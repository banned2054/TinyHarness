using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.ProjectInstructions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;

namespace TinyHarness.Core.Services.Context;

/// <summary>
///     <para>
///         保留完整本地历史，构建受预算约束的模型视图并提交有界交接摘要。
///     </para>
///     <para>
///         Retains complete local history, builds a budgeted model view and commits bounded handoff summaries.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         assistant tool_calls 与相邻 tool 结果是不可拆分的原子组；最新进行中组与最新完整组不折叠。
///         视图及摘要输入可裁剪工具结果，但不改写本地原始消息；当前项目规则独立保留，旧版本按来源过滤。
///         未启用预算时仍应用规则投影，未管理规则时直通完整历史。
///         摘要输入按独立预算从最旧完整组开始分批；提交须有界、使视图严格缩小且计划仍有效。
///     </para>
///     <para>
///         Assistant tool_calls and adjacent tool results form indivisible atomic groups;
///         the latest in-progress and latest complete groups are never folded.
///         View and summary input may cap tool results without rewriting local messages;
///         current project rules remain present and source bindings filter superseded versions.
///         Without budgeting the rules projection still applies; unmanaged history passes through unchanged.
///         Summary input is batched oldest-first within its own budget; commits require bounded summaries,
///         a strictly smaller view and a valid fold plan.
///     </para>
/// </remarks>
public sealed class ConversationContext
{
    private const int NewStateTokenAssumption = 240;

    private const string SummaryPrefix =
        "Handoff summary of earlier conversation (historical context, not new authorization):\n";

    private const string CompactionInstruction =
        "You are performing a CONTEXT CHECKPOINT COMPACTION, not answering the earlier user requests. "     +
        "Create a concise handoff summary for another model that will continue this conversation. "         +
        "Include the current goal, progress, key decisions, important constraints and user preferences, "   +
        "remaining work and next steps, and necessary file/data references. Distinguish confirmed tool "    +
        "results from proposals, denied commands, failed attempts and unverified claims. Preserve current " +
        "scope restrictions, including withdrawn authorization. Do not execute tasks or request tools. "    +
        "Return only the handoff text. Consolidate outdated details; do not copy an ever-growing ledger.";

    private readonly HashSet<int>                   _instructionMessageIndices = [];
    private readonly List<ProjectInstructionRecord> _instructionRecords        = [];

    private readonly List<ChatMessage> _messages = [];

    private ContextOptions? _options;
    private ContextOptions? _checkpointOptions;
    private int             _reasoningStartMessageIndex;
    private long            _modelSelectionRevision;
    private int             _foldEndMessageIndex;

    // 折叠区只从模型视图隐藏，完整原始消息仍保留在本地。
    private int  _foldStartMessageIndex = -1;
    private bool _hasState;

    // 失败尝试锁定到当前消息数，避免原地重试；成功不锁定，以便继续分批压缩。
    private int _messagesAtLastAttempt = -1;

    private FoldSelection?            _pendingFold;
    private ProjectInstructionRecord? _pendingInstructions;

    // 用最近一次请求输入比值校准视图估算，并钳位到 [0.5, 4.0] 防止异常 usage 主导预算。
    // 校准仍是估算，Reset 保留它，因为偏差属于估算器而非单次任务。
    private double _usageCalibration = 1.0;

    /// <summary>
    ///     <para>
    ///         创建上下文管理器。<paramref name="options" /> 为 <see langword="null" /> 时不启用预算
    ///         与压缩；未管理规则时视图等于完整历史，已管理的规则仍投影当前版本。
    ///     </para>
    ///     <para>
    ///         Creates the context manager. A <see langword="null" /> <paramref name="options" />
    ///         disables budgeting and compaction; unmanaged history passes through unchanged,
    ///         while managed project instructions still project the current revision.
    ///     </para>
    /// </summary>
    public ConversationContext(ContextOptions? options = null) => _options = options?.Validate();

    internal ContextOptions? CheckpointOptions                => _checkpointOptions ?? _options;
    internal long?           CheckpointModelSelectionRevision { get; private set; }

    /// <summary>
    ///     <para>在空闲边界重绑请求策略；完整历史与已提交检查点不变，旧区间的推理仅从请求视图隔离。</para>
    ///     <para>Rebinds request policy while idle, retaining canonical history and checkpoints and isolating old reasoning in views.</para>
    /// </summary>
    internal void BindModel(SessionModelSelection selection)
    {
        selection.Validate();
        _checkpointOptions ??= _options;
        _options                    = selection.Context;
        _reasoningStartMessageIndex = selection.ReasoningStartMessageIndex;
        _modelSelectionRevision     = selection.Revision;
        _usageCalibration           = 1.0;
        _pendingFold                = null;
        _messagesAtLastAttempt      = -1;
    }

    /// <summary>
    ///     <para>已确认提交的当前项目规则；null 表示尚未纳入宿主管理。</para>
    ///     <para>The current reliably committed project rules; null means unmanaged.</para>
    /// </summary>
    public ProjectInstructionSnapshot? ProjectInstructions { get; private set; }

    /// <summary>
    ///     <para>结构化来源事实，含待提交候选；仅由权威写入端增量保存。</para>
    ///     <para>Structured source facts, including a pending candidate, saved incrementally by the authoritative writer.</para>
    /// </summary>
    public IReadOnlyList<ProjectInstructionRecord> ProjectInstructionRecords => _instructionRecords.AsReadOnly();

    /// <summary>
    ///     <para>
    ///         是否启用预算与压缩。
    ///     </para>
    ///     <para>
    ///         Whether budgeting and compaction are enabled.
    ///     </para>
    /// </summary>
    public bool Enabled => _options is not null;

    /// <summary>
    ///     <para>
    ///         完整本地历史（压缩不会移除其中的任何消息）。
    ///     </para>
    ///     <para>
    ///         The complete local history; compaction never removes messages from it.
    ///     </para>
    /// </summary>
    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>
    ///     <para>
    ///         当前压缩位置（是否已有状态与折叠边界）；持久化写入端据此保存会话的压缩形态。
    ///     </para>
    ///     <para>
    ///         The current compaction position (whether a state exists and the fold
    ///         boundaries); the persistence writer saves the session's compacted shape from it.
    ///     </para>
    /// </summary>
    public CompactionPosition Position => new(_hasState, _foldStartMessageIndex, _foldEndMessageIndex);

    /// <summary>
    ///     <para>已提交的交接文本与有界原话来源。</para>
    ///     <para>The committed handoff text and bounded original user sources.</para>
    /// </summary>
    public CompactionState State { get; private set; } = CompactionState.Empty;

    /// <summary>
    ///     <para>最近一次压缩拒绝原因；不包含模型正文。</para>
    ///     <para>The latest compaction rejection reason, without model text.</para>
    /// </summary>
    public CompactionFailureReason LastCompactionFailure { get; private set; }

    private int SummaryBudget =>
        Math.Min(_options?.SummaryTokens ?? 2000,
                 Math.Max(1, ((_options?.ContextWindowTokens  ?? 128_000) -
                              (_options?.ReservedOutputTokens ?? 8000)) / 4));

    private int RetainedUserBudget =>
        Math.Min(_options?.RetainedUserMessageTokens ?? 2000,
                 Math.Max(0, ((_options?.ContextWindowTokens  ?? 128_000) -
                              (_options?.ReservedOutputTokens ?? 8000)) / 8));

    private int ThresholdTokens => _options!.CompactionThresholdTokens > 0
        ? _options.CompactionThresholdTokens
        : Math.Max(1, (int)(_options.ContextWindowTokens * 0.9));

    /// <summary>
    ///     <para>暂存完整规则及来源绑定，等待同批日志确认后生效。</para>
    ///     <para>Stages complete rules and their source binding until the same journal batch is confirmed.</para>
    /// </summary>
    public ProjectInstructionRecord StageProjectInstructions(
        ProjectInstructionSnapshot snapshot, string reason, string turnId, string runId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ProjectInstructionRenderer.Validate(snapshot);
        if (_pendingInstructions is not null)
            throw new InvalidOperationException("A project instruction update is already pending.");
        if (_messages.Count                                       == 0               ||
            _messages[0].Role                                     != ChatRole.System ||
            ChatHistoryInvariants.LastCompleteBoundary(_messages) != _messages.Count)
            throw new InvalidOperationException("Project instructions require a complete conversation boundary.");
        var rulesChanged = ProjectInstructions?.Revision != snapshot.Revision;
        var record = new ProjectInstructionRecord
        {
            Action                = rulesChanged ? "rules-applied" : "scopes-tracked",
            Revision              = snapshot.Revision,
            PreviousRevision      = ProjectInstructions?.Revision,
            TurnId                = turnId,
            RunId                 = runId,
            Reason                = reason,
            Snapshot              = snapshot,
            CanonicalMessageIndex = rulesChanged ? _messages.Count : null
        };
        if (record.CanonicalMessageIndex is { } index)
        {
            Append(ChatMessage.User(ProjectInstructionRenderer.Render(snapshot)));
            _instructionMessageIndices.Add(index);
        }

        _instructionRecords.Add(record);
        _pendingInstructions = record;
        _pendingFold         = null;
        return record;
    }

    /// <summary>
    ///     <para>同批日志可靠提交后发布暂存规则。</para>
    ///     <para>Publishes staged rules after their journal batch has committed reliably.</para>
    /// </summary>
    public void ConfirmProjectInstructions()
    {
        if (_pendingInstructions is null) return;
        ProjectInstructions    = _pendingInstructions.Snapshot;
        _pendingInstructions   = null;
        _messagesAtLastAttempt = -1;
    }

    /// <summary>
    ///     <para>提交失败时丢弃暂存投影，不修改磁盘或已生效规则。</para>
    ///     <para>Discards a failed candidate without changing disk or the effective rules.</para>
    /// </summary>
    public void RejectProjectInstructions()
    {
        if (_pendingInstructions is not { } pending) return;
        if (pending.CanonicalMessageIndex is { } index)
        {
            _messages.RemoveAt(index);
            _instructionMessageIndices.Remove(index);
        }

        _instructionRecords.RemoveAt(_instructionRecords.Count - 1);
        _pendingInstructions   = null;
        _pendingFold           = null;
        _messagesAtLastAttempt = -1;
    }

    /// <summary>
    ///     <para>从已校验的统一日志恢复来源链；正文标记不能生成宿主身份。</para>
    ///     <para>Restores source bindings from the validated journal; content markers never establish host identity.</para>
    /// </summary>
    public void RestoreProjectInstructions(IReadOnlyList<ProjectInstructionRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var                         restoredRecords      = records.ToArray();
        var                         indices              = new HashSet<int>();
        var                         previousMessageIndex = -1;
        ProjectInstructionSnapshot? current              = null;
        foreach (var record in restoredRecords)
        {
            ProjectInstructionRenderer.Validate(record.Snapshot);
            if (record.Schema           != "tinyharness.project-instructions/1" ||
                record.Revision         != record.Snapshot.Revision             ||
                record.PreviousRevision != current?.Revision                    ||
                string.IsNullOrWhiteSpace(record.TurnId)                        ||
                string.IsNullOrWhiteSpace(record.RunId)                         ||
                string.IsNullOrWhiteSpace(record.Reason))
                throw new InvalidDataException("Invalid project instruction revision chain.");
            if (record.Action == "rules-applied")
            {
                if (current?.Revision == record.Revision ||
                    record.CanonicalMessageIndex is not { } index ||
                    index < 1 ||
                    index >= _messages.Count ||
                    !indices.Add(index) ||
                    index <= previousMessageIndex ||
                    _messages[index].Role != ChatRole.User ||
                    _messages[index].Content != ProjectInstructionRenderer.Render(record.Snapshot) ||
                    ChatHistoryInvariants.LastCompleteBoundary(_messages.Take(index).ToArray()) != index)
                    throw new InvalidDataException("Invalid project instruction canonical message binding.");
                previousMessageIndex = index;
            }
            else if (record.Action != "scopes-tracked"   ||
                     current is null                     ||
                     record.Revision != current.Revision ||
                     record.CanonicalMessageIndex is not null)
            {
                throw new InvalidDataException("Invalid project instruction discovery record.");
            }

            current = record.Snapshot;
        }

        _instructionRecords.Clear();
        _instructionRecords.AddRange(restoredRecords);
        _instructionMessageIndices.Clear();
        _instructionMessageIndices.UnionWith(indices);
        ProjectInstructions  = current;
        _pendingInstructions = null;
        ValidateStateSources();
    }

    /// <summary>
    ///     <para>
    ///         追加一条消息；任何追加都会解除上次压缩尝试的重试锁。
    ///     </para>
    ///     <para>
    ///         Appends one message; every append releases the previous compaction-attempt latch.
    ///     </para>
    /// </summary>
    public void Append(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _messages.Add(message);
        _messagesAtLastAttempt = -1;
    }

    /// <summary>
    ///     <para>
    ///         清空历史并回到未压缩状态，用于开始一次新任务。
    ///     </para>
    ///     <para>
    ///         Clears the history and compaction state, ready for a fresh task.
    ///     </para>
    /// </summary>
    public void Reset()
    {
        _messages.Clear();
        _instructionRecords.Clear();
        _instructionMessageIndices.Clear();
        ProjectInstructions    = null;
        _pendingInstructions   = null;
        LastCompactionFailure  = CompactionFailureReason.None;
        State                  = CompactionState.Empty;
        _hasState              = false;
        _foldStartMessageIndex = -1;
        _foldEndMessageIndex   = 0;
        _pendingFold           = null;
        _messagesAtLastAttempt = -1;
    }

    /// <summary>
    ///     <para>
    ///         从持久化快照恢复完整历史、交接摘要和折叠位置。
    ///     </para>
    ///     <para>
    ///         Restores complete history, handoff state and fold position from a persisted snapshot.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         仅用于会话恢复。起点 -1 表示未折叠，不得携带状态或非零终点；
    ///         已折叠时必须有状态，且 1 ≤ 起点 &lt; 终点 ≤ 消息数。
    ///     </para>
    ///     <para>
    ///         Use only for session restoration. Start -1 means unfolded and permits neither state nor a nonzero end;
    ///         a folded position requires state and 1 ≤ start &lt; end ≤ message count.
    ///     </para>
    /// </remarks>
    public void Restore(IReadOnlyList<ChatMessage> messages,              CompactionState state, bool hasState,
                        int                        foldStartMessageIndex, int             foldEndMessageIndex,
                        long?                      checkpointModelSelectionRevision = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(state);
        if (foldStartMessageIndex < 0)
        {
            if (hasState || foldEndMessageIndex != 0)
                throw new
                    ArgumentException("An unfolded conversation cannot carry a compaction state or a non-zero fold end.");
        }
        else if (!hasState || foldStartMessageIndex >= foldEndMessageIndex || foldEndMessageIndex > messages.Count)
        {
            throw new ArgumentException("The fold position does not agree with the restored message history.");
        }

        if (state.Schema != CompactionState.CurrentSchema           ||
            state.Summary is null                                   ||
            state.RetainedUserMessages is null                      ||
            (hasState  && string.IsNullOrWhiteSpace(state.Summary)) ||
            (!hasState && (!string.IsNullOrEmpty(state.Summary) || state.RetainedUserMessages.Count != 0)))
            throw new ArgumentException("Invalid compaction checkpoint schema or summary.");
        if (hasState &&
            (foldStartMessageIndex < 1 ||
             ChatHistoryInvariants.LastCompleteBoundary(messages.Take(foldStartMessageIndex).ToArray()) !=
             foldStartMessageIndex ||
             ChatHistoryInvariants.LastCompleteBoundary(messages.Take(foldEndMessageIndex).ToArray()) !=
             foldEndMessageIndex))
            throw new ArgumentException("The fold position splits a tool group or removes the system message.");

        Reset();
        _messages.AddRange(messages);
        State                            = state;
        _hasState                        = hasState;
        _foldStartMessageIndex           = foldStartMessageIndex;
        _foldEndMessageIndex             = foldEndMessageIndex;
        _checkpointOptions               = _options;
        CheckpointModelSelectionRevision = checkpointModelSelectionRevision;
        ValidateStateSources();
        _pendingFold           = null;
        _messagesAtLastAttempt = -1;
    }

    /// <summary>
    ///     <para>
    ///         在中断结算时把历史回退到指定完整协议边界，不改动折叠区。
    ///     </para>
    ///     <para>
    ///         Rewinds history to a specified complete protocol boundary during interruption settlement,
    ///         leaving the folded region unchanged.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         宿主先保留执行意图与确认记录，再整体丢弃不完整尾部，不拆分工具组或补造结果。
    ///     </para>
    ///     <para>
    ///         The host preserves execution intent and confirmation records first, then discards the incomplete tail
    ///         as a whole, without splitting tool groups or fabricating results.
    ///     </para>
    /// </remarks>
    /// <param name="messageCount">
    ///     <para>
    ///         须保留非空真前缀，且边界不早于折叠区终点；完整性由调用方校验。
    ///     </para>
    ///     <para>
    ///         Must retain a nonempty proper prefix and not precede the fold end;
    ///         the caller validates protocol completeness.
    ///     </para>
    /// </param>
    public void TruncateToMessageCount(int messageCount)
    {
        if (messageCount <= 0 || messageCount >= _messages.Count)
            throw new
                ArgumentOutOfRangeException(nameof(messageCount),
                                            "The truncation boundary must keep a non-empty proper prefix of the history.");
        if (messageCount < _foldEndMessageIndex)
            throw new
                ArgumentException("The truncation boundary would cut into the folded region; a complete-boundary tail never overlaps compaction.",
                                  nameof(messageCount));

        _messages.RemoveRange(messageCount, _messages.Count - messageCount);
        var retained = _instructionRecords.TakeWhile(record => record.CanonicalMessageIndex is not { } index ||
                                                               index < messageCount).ToArray();
        RestoreProjectInstructions(retained);
        _pendingFold           = null;
        _messagesAtLastAttempt = -1;
    }

    /// <summary>
    ///     <para>
    ///         判断下一次模型请求前是否需要压缩：总估算（视图 + 工具定义 + 保留输出）超过阈值，
    ///         且存在尚未折叠、可压缩的旧完整回合。只记录失败尝试的重试锁在此生效。
    ///     </para>
    ///     <para>
    ///         Reports whether the next model request requires compaction: the total estimate
    ///         (view + tool definitions + reserved output) exceeds the threshold and
    ///         there are foldable old complete turns not yet compacted. The retry latch recorded for
    ///         failed attempts is honored here.
    ///     </para>
    /// </summary>
    public bool RequiresCompaction(IReadOnlyList<ToolDefinition> tools)
    {
        if (!Enabled || _messagesAtLastAttempt == _messages.Count || PlanFold(tools) is null) return false;

        var total = EstimateViewTokens() + TokenEstimator.EstimateToolDefinitions(tools);
        return total + _options!.ReservedOutputTokens > ThresholdTokens;
    }

    /// <summary>
    ///     <para>
    ///         返回含状态与工具结果裁剪的校准后视图 token 估算，而非实测。
    ///     </para>
    ///     <para>
    ///         Returns a calibrated view-token estimate including state and tool-result caps, not a measurement.
    ///     </para>
    /// </summary>
    public int EstimateViewTokens() =>
        (int)Math.Ceiling(TokenEstimator.EstimateMessages(BuildModelView()) * _usageCalibration);

    /// <summary>
    ///     <para>
    ///         用最近一次请求前输入估算与服务端实测输入的比值校准后续视图估算。
    ///     </para>
    ///     <para>
    ///         Calibrates subsequent view estimates using the latest ratio of server-reported input
    ///         to pre-request input estimates.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         忽略 null 或非正实测，以及非正估算；比值钳位到 0.5–4.0。
    ///         观测口径包含工具 schema，应用口径仅为消息视图；大 schema 可能使估算偏保守、压缩提前。
    ///     </para>
    ///     <para>
    ///         Ignores null or nonpositive measurements and nonpositive estimates; clamps the ratio to 0.5–4.0.
    ///         Observations include tool schemas while application covers only the message view;
    ///         large schemas can make estimates conservative and trigger compaction early.
    ///     </para>
    /// </remarks>
    public void RecordModelUsage(int estimatedPromptTokens, int? actualInputTokens)
    {
        if (actualInputTokens is not (> 0 and var actual) || estimatedPromptTokens <= 0) return;

        _usageCalibration = Math.Clamp(actual / (double)estimatedPromptTokens, 0.5, 4.0);
    }

    /// <summary>
    ///     <para>构建当前规则、有界原话、交接摘要和完整近期工具组组成的模型视图。</para>
    ///     <para>Builds the model view from current rules, bounded user originals, a handoff, and complete recent tool groups.</para>
    /// </summary>
    public IReadOnlyList<ChatMessage> BuildModelView() =>
        BuildProjectedView(_hasState ? State : null, _hasState ? _foldStartMessageIndex : -1, _foldEndMessageIndex);

    /// <summary>
    ///     <para>按已保存的来源重建视图，不从摘要正文猜测宿主规则。</para>
    ///     <para>Reconstructs the view from saved sources without inferring host rules from summary text.</para>
    /// </summary>
    private IReadOnlyList<ChatMessage> BuildProjectedView(CompactionState? state, int foldStart, int foldEnd)
    {
        var view = new List<ChatMessage>();
        for (var i = 0; i < _messages.Count; i++)
        {
            if (state is not null && i == foldStart)
            {
                foreach (var retained in state.RetainedUserMessages) view.Add(RetainedMessage(retained));
                view.Add(ChatMessage.User(SummaryPrefix + state.Summary));
            }

            if ((i >= foldStart && i < foldEnd) || _instructionMessageIndices.Contains(i)) continue;
            view.Add(ViewMessage(i));
            if (i == 0 && ProjectInstructions is { } snapshot)
                view.Add(ChatMessage.User(ProjectInstructionRenderer.Render(snapshot)));
        }

        return view;
    }

    /// <summary>
    ///     <para>选择旧完整组，并在历史末尾追加禁用工具的交接请求；空列表表示没有可折叠内容。</para>
    ///     <para>
    ///         Selects old complete groups and appends a tools-disabled handoff request last; empty means nothing is
    ///         foldable.
    ///     </para>
    /// </summary>
    public IReadOnlyList<ChatMessage> BuildCompactionMessages(IReadOnlyList<ToolDefinition> tools)
    {
        var selection = PlanFold(tools);
        if (selection is null) return [];
        _pendingFold          = selection;
        LastCompactionFailure = CompactionFailureReason.None;
        var result = SummaryHead();
        foreach (var span in selection.Spans)
            for (var i = span.Start; i < span.Start + span.Count; i++)
                if (!_instructionMessageIndices.Contains(i))
                    result.Add(ViewMessage(i));
        result.Add(SummaryTask());
        return result;
    }

    /// <summary>
    ///     <para>提交非空、有界且有压缩收益的交接文本；拒绝不改变已提交状态和历史。</para>
    ///     <para>Commits nonempty bounded handoff text with a budget benefit; rejection preserves committed state and history.</para>
    /// </summary>
    public bool TryApplyCompaction(string summary)
    {
        var pending = _pendingFold;
        _pendingFold = null;
        if (pending is null                               ||
            _messages.Count != pending.MessageCountAtPlan ||
            (_foldStartMessageIndex >= 0 && pending.StartMessageIndex != _foldEndMessageIndex))
            return RejectCompaction(CompactionFailureReason.InvalidPlan);
        if (string.IsNullOrWhiteSpace(summary)) return RejectCompaction(CompactionFailureReason.EmptySummary);
        summary = summary.Trim();
        if (TokenEstimator.EstimateText(summary) > SummaryBudget)
            return RejectCompaction(CompactionFailureReason.SummaryTooLarge);
        var start = _foldStartMessageIndex < 0 ? pending.StartMessageIndex : _foldStartMessageIndex;
        var end   = pending.StartMessageIndex + pending.FoldMessageCount;
        var candidate = new CompactionState
        {
            Summary              = summary,
            RetainedUserMessages = SelectUserMessages(start, end)
        };
        if (EstimateCandidateViewTokens(candidate, start, end) >= EstimateViewTokens())
            return RejectCompaction(CompactionFailureReason.NoBudgetBenefit);
        _foldStartMessageIndex           = start;
        _foldEndMessageIndex             = end;
        State                            = candidate;
        _checkpointOptions               = _options;
        CheckpointModelSelectionRevision = _modelSelectionRevision == 0 ? null : _modelSelectionRevision;
        _hasState                        = true;
        LastCompactionFailure            = CompactionFailureReason.None;
        return true;
    }

    /// <summary>
    ///     <para>锁定失败尝试直到历史增长，不丢弃已提交摘要。</para>
    ///     <para>Latches a failed attempt until history grows without discarding the committed handoff.</para>
    /// </summary>
    public void MarkCompactionAttempted(CompactionFailureReason reason = CompactionFailureReason.ModelRequestFailed)
    {
        _pendingFold          = null;
        LastCompactionFailure = reason;
        LatchAttempt();
    }

    /// <summary>
    ///     <para>记录拒绝原因并保留原视图。</para>
    ///     <para>Records the rejection reason and preserves the original view.</para>
    /// </summary>
    private bool RejectCompaction(CompactionFailureReason reason)
    {
        MarkCompactionAttempted(reason);
        return false;
    }

    /// <summary>
    ///     <para>
    ///         单条 tool 结果消息在模型视图中的裁剪；启用预算时只对超长结果生效。
    ///     </para>
    ///     <para>
    ///         Applies the per-tool-result character cap to a view message when budgeting is on.
    ///     </para>
    /// </summary>
    private ChatMessage CapViewMessage(ChatMessage message)
    {
        if (message.Role != ChatRole.Tool || message.Content.Length <= _options!.ToolResultViewCharacters)
            return message;

        var cap  = _options.ToolResultViewCharacters;
        var head = cap / 2;
        var tail = cap - head;
        var content = message.Content[..head]                                                     +
                      $"\n... [model view truncated to {cap} of {message.Content.Length} chars; " +
                      "the full result stays in the local history] ...\n"                         +
                      message.Content[^tail..];
        return ChatMessage.Tool(message.Name ?? string.Empty, message.ToolCallId ?? string.Empty, content);
    }

    private ChatMessage ViewMessage(int index)
    {
        var message = _messages[index];
        if (index < _reasoningStartMessageIndex && message.Reasoning is not null)
            message = message with { Reasoning = null };
        return Enabled ? CapViewMessage(message) : message;
    }

    /// <summary>
    ///     <para>从最旧完整组分批选择折叠区，保留最新完整组与进行中尾部，并独立计入摘要输入预算。</para>
    ///     <para>
    ///         Selects oldest complete groups in batches, retaining the latest complete group and active tail within an
    ///         independent summary budget.
    ///     </para>
    /// </summary>
    private FoldSelection? PlanFold(IReadOnlyList<ToolDefinition> tools)
    {
        if (!Enabled) return null;
        var spans = AssistantSpans();
        if (spans.Count < 2) return null;
        var complete = spans.Take(spans.Count - 1).Where(span => span.Start >= _foldEndMessageIndex).ToArray();
        if (complete.Length < 2) return null;
        var headTokens = TokenEstimator.EstimateMessage(_messages[0]);
        if (ProjectInstructions is { } rules)
            headTokens += TokenEstimator.EstimateMessage(ChatMessage.User(ProjectInstructionRenderer.Render(rules)));
        var remaining = ThresholdTokens - headTokens
                      - Math.Min(NewStateTokenAssumption, SummaryBudget) - RetainedUserBudget
                      - SpanTokens(spans[^1]) - TokenEstimator.EstimateToolDefinitions(tools)
                      - _options!.ReservedOutputTokens;
        var kept = 0;
        for (var i = complete.Length - 1; i >= 0; i--)
        {
            var cost = SpanTokens(complete[i]);
            if (kept > 0 && cost > remaining) break;
            kept++;
            remaining -= cost;
        }

        var summaryRemaining = _options.ContextWindowTokens - _options.ReservedOutputTokens
                             - TokenEstimator.EstimateMessages(SummaryHead())
                             - TokenEstimator.EstimateMessage(SummaryTask());
        var selected = new List<MessageSpan>();
        for (var i = 0; i < complete.Length - kept; i++)
        {
            var cost = SpanTokens(complete[i]);
            if (cost > summaryRemaining) break;
            selected.Add(complete[i]);
            summaryRemaining -= cost;
        }

        return selected.Count == 0 ? null : new FoldSelection(selected) { MessageCountAtPlan = _messages.Count };
    }

    /// <summary>
    ///     <para>构建摘要输入的当前宿主上下文和旧交接，不复制完整旧审计。</para>
    ///     <para>Builds current host context and the prior handoff without copying the complete old audit.</para>
    /// </summary>
    private List<ChatMessage> SummaryHead()
    {
        var result = new List<ChatMessage> { _messages[0] };
        if (ProjectInstructions is { } rules) result.Add(ChatMessage.User(ProjectInstructionRenderer.Render(rules)));
        if (_hasState)
        {
            foreach (var retained in State.RetainedUserMessages) result.Add(RetainedMessage(retained));
            result.Add(ChatMessage.User(SummaryPrefix + State.Summary));
        }

        return result;
    }

    /// <summary>
    ///     <para>将当前摘要任务置于请求末尾，并给出宿主实际允许的估算输出预算。</para>
    ///     <para>Places the current compaction task last and supplies the host's estimated output budget.</para>
    /// </summary>
    private ChatMessage SummaryTask() =>
        ChatMessage.User(CompactionInstruction +
                         $" Keep the handoff within approximately {SummaryBudget} tokens; prioritize current restrictions and next steps.");

    /// <summary>
    ///     <para>按独立预算从近到远保留折叠区真实原话，必要时只保留明确标记的尾部。</para>
    ///     <para>
    ///         Retains recent original user text from the fold within its own budget, using an explicitly marked suffix when
    ///         needed.
    ///     </para>
    /// </summary>
    private IReadOnlyList<RetainedUserMessage> SelectUserMessages(int start, int end)
    {
        var retained  = new List<RetainedUserMessage>();
        var remaining = RetainedUserBudget;
        for (var i = end - 1; i >= start && remaining > 0; i--)
        {
            var message = _messages[i];
            if (message.Role != ChatRole.User          ||
                _instructionMessageIndices.Contains(i) ||
                string.IsNullOrEmpty(message.Content))
                continue;
            var entry = new RetainedUserMessage(i, message.Content);
            var cost  = TokenEstimator.EstimateMessage(RetainedMessage(entry));
            if (cost > remaining)
            {
                var low  = 0;
                var high = message.Content.Length;
                while (low < high)
                {
                    var length                                 = low + (high - low + 1) / 2;
                    var suffix                                 = message.Content[^length..];
                    if (char.IsLowSurrogate(suffix[0])) suffix = suffix[1..];
                    var candidate                              = new RetainedUserMessage(i, suffix);
                    if (TokenEstimator.EstimateMessage(RetainedMessage(candidate)) <= remaining) low = length;
                    else high                                                                        = length - 1;
                }

                if (low == 0) break;
                var text                               = message.Content[^low..];
                if (char.IsLowSurrogate(text[0])) text = text[1..];
                if (text.Length == 0) break;
                entry = new RetainedUserMessage(i, text);
                cost  = TokenEstimator.EstimateMessage(RetainedMessage(entry));
            }

            retained.Add(entry);
            remaining -= cost;
        }

        retained.Reverse();
        return retained.ToArray();
    }

    /// <summary>
    ///     <para>显示原话裁剪标记，标记不写入 canonical 内容。</para>
    ///     <para>Marks clipped originals without writing that marker into canonical content.</para>
    /// </summary>
    private ChatMessage RetainedMessage(RetainedUserMessage entry) =>
        ChatMessage.User(entry.Content.Length == _messages[entry.MessageIndex].Content.Length
                             ? entry.Content
                             : "Earlier user input (only its retained suffix follows):\n" + entry.Content);

    /// <summary>
    ///     <para>验证摘要版本、来源顺序、原文绑定及宿主规则隔离；恢复不静默重选原话。</para>
    ///     <para>
    ///         Validates summary version, ordered original sources and host-rule separation without silently reselecting
    ///         text on restore.
    ///     </para>
    /// </summary>
    private void ValidateStateSources()
    {
        if (!_hasState) return;
        var previous = -1;
        var tokens   = 0;
        foreach (var entry in State.RetainedUserMessages)
        {
            if (entry is null                                           ||
                entry.MessageIndex <= previous                          ||
                entry.MessageIndex < _foldStartMessageIndex             ||
                entry.MessageIndex >= _foldEndMessageIndex              ||
                entry.Content is null                                   ||
                entry.Content.Length               == 0                 ||
                _messages[entry.MessageIndex].Role != ChatRole.User     ||
                _instructionMessageIndices.Contains(entry.MessageIndex) ||
                !_messages[entry.MessageIndex].Content.EndsWith(entry.Content, StringComparison.Ordinal))
                throw new InvalidDataException("Invalid retained user-message source in compaction checkpoint.");
            previous =  entry.MessageIndex;
            tokens   += TokenEstimator.EstimateMessage(RetainedMessage(entry));
        }

        var generationOptions = _checkpointOptions ?? _options;
        var available = (generationOptions?.ContextWindowTokens  ?? 128_000) -
                        (generationOptions?.ReservedOutputTokens ?? 8000);
        var retainedBudget = Math.Min(generationOptions?.RetainedUserMessageTokens ?? 2000, Math.Max(0, available / 8));
        var summaryBudget  = Math.Min(generationOptions?.SummaryTokens             ?? 2000, Math.Max(1, available / 4));
        if (generationOptions is not null &&
            (tokens > retainedBudget || TokenEstimator.EstimateText(State.Summary) > summaryBudget))
            throw new InvalidDataException("The saved compaction exceeds the current summary or retained-user budget.");
    }

    /// <summary>
    ///     <para>
    ///         按与当前视图相同的校准口径估算候选视图，供严格缩小校验使用。
    ///     </para>
    ///     <para>
    ///         Estimates a candidate view on the same calibrated scale as the current view
    ///         for the strictly-smaller check.
    ///     </para>
    /// </summary>
    private int EstimateCandidateViewTokens(CompactionState state, int foldStart, int foldEnd)
    {
        var tokens = TokenEstimator.EstimateMessages(BuildProjectedView(state, foldStart, foldEnd));
        return (int)Math.Ceiling(tokens * _usageCalibration);
    }

    private void LatchAttempt()
    {
        _messagesAtLastAttempt = _messages.Count;
    }

    /// <summary>
    ///     <para>
    ///         定位每个 assistant 回合：一条 assistant 消息及其后紧邻的连续 tool 消息（原子组）。
    ///     </para>
    ///     <para>
    ///         Locates every assistant turn: an assistant message plus its contiguous tool messages (one atomic group).
    ///     </para>
    /// </summary>
    private List<MessageSpan> AssistantSpans()
    {
        var spans  = new List<MessageSpan>();
        var starts = new List<int>();
        for (var i = 0; i < _messages.Count; i++)
            if (_messages[i].Role == ChatRole.Assistant)
                starts.Add(i);
        for (var i = 0; i < starts.Count; i++)
        {
            // 相邻 assistant 之间的真实输入与宿主消息也属于连续折叠区。
            var end   = i + 1 < starts.Count ? starts[i + 1] : _messages.Count;
            var start = i     == 0 ? 1 : starts[i];
            spans.Add(new MessageSpan(start, end - start));
        }

        return spans;
    }

    private int SpanTokens(MessageSpan span)
    {
        var tokens = 0;
        for (var i = span.Start; i < span.Start + span.Count; i++)
            if (!_instructionMessageIndices.Contains(i))
                tokens += TokenEstimator.EstimateMessage(ViewMessage(i));

        return tokens;
    }

    /// <summary>
    ///     <para>
    ///         会话恢复时的压缩位置快照。
    ///     </para>
    ///     <para>
    ///         The compaction-position snapshot used when restoring a session.
    ///     </para>
    /// </summary>
    public readonly record struct CompactionPosition(bool HasState, int FoldStartMessageIndex, int FoldEndMessageIndex);

    /// <summary>
    ///     <para>
    ///         保留在原始历史中的消息区间。
    ///     </para>
    ///     <para>
    ///         A message range within the retained raw history.
    ///     </para>
    /// </summary>
    private readonly record struct MessageSpan(int Start, int Count);

    /// <summary>
    ///     <para>
    ///         一次压缩选择的折叠区间（必然是连续前缀）。
    ///     </para>
    ///     <para>
    ///         The fold ranges chosen by one compaction (always a contiguous prefix).
    ///     </para>
    /// </summary>
    private sealed record FoldSelection(IReadOnlyList<MessageSpan> Spans)
    {
        /// <summary>
        ///     <para>
        ///         构建计划时的消息总数；提交时若消息已追加则计划失效。
        ///     </para>
        ///     <para>
        ///         Total message count when the plan was built; appends invalidate the plan.
        ///     </para>
        /// </summary>
        public int MessageCountAtPlan { get; init; }

        public int StartMessageIndex => Spans[0].Start;
        public int FoldMessageCount  => Spans.Sum(span => span.Count);
    }
}

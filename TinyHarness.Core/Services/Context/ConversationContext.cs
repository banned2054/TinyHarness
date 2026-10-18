using System.Text;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.ProjectInstructions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;

namespace TinyHarness.Core.Services.Context;

/// <summary>
///     <para>
///         保留完整本地历史，构建受预算约束的模型视图并提交结构化压缩状态。
///     </para>
///     <para>
///         Retains complete local history, builds a budgeted model view and commits structured compaction state.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         assistant tool_calls 与相邻 tool 结果是不可拆分的原子组；最新进行中组与最新完整组不折叠。
///         视图及摘要输入可裁剪工具结果，但不改写本地原始消息；当前项目规则独立保留，旧版本按来源过滤。
///         未启用预算时仍应用规则投影，未管理规则时直通完整历史。
///         摘要输入按独立预算从最旧完整组开始分批；提交须保留事实、使视图严格缩小且计划仍有效。
///     </para>
///     <para>
///         Assistant tool_calls and adjacent tool results form indivisible atomic groups;
///         the latest in-progress and latest complete groups are never folded.
///         View and summary input may cap tool results without rewriting local messages;
///         current project rules remain present and source bindings filter superseded versions.
///         Without budgeting the rules projection still applies; unmanaged history passes through unchanged.
///         Summary input is batched oldest-first within its own budget; commits require preserved facts,
///         a strictly smaller view and a valid fold plan.
///     </para>
/// </remarks>
public sealed class ConversationContext
{
    private const int NewStateTokenAssumption = 240;

    private const string CompactionInstruction =
        "You maintain the working context of a coding-agent run. Below you receive the "                  +
        "current compacted state (if any) followed by earlier complete turns that must be "               +
        "folded into it. Reply with ONLY one JSON object, no markdown and no commentary, "                +
        "matching exactly this schema: {\"goal\":string,\"constraints\":string[],"                        +
        "\"decisions\":string[],\"filesInspected\":string[],\"filesModified\":string[],"                  +
        "\"commandsAndResults\":string[],\"pendingWork\":string[]}. Preserve filesInspected, "            +
        "filesInspected, filesModified, and commandsAndResults entries verbatim; preserve prior "         +
        "decisions; constraints and pendingWork describe current state and may be replaced only "         +
        "with an explicit non-empty update. Never silently omit prior decisions or pending work; "        +
        "to close pending work, include a clear completion entry such as 'completed: ...'. Never invent " +
        "completed work, and prefer brevity over detail. The "                                            +
        "user messages above the turns carry the current compacted state (or 'No previous "               +
        "summary.') and, on the first fold, the run's original task and constraints; use them "           +
        "to fill goal, constraints and pendingWork accurately.";

    private readonly HashSet<int>                   _instructionMessageIndices = [];
    private readonly List<ProjectInstructionRecord> _instructionRecords        = [];

    private readonly List<ChatMessage> _messages = [];

    private readonly ContextOptions? _options;
    private          int             _foldEndMessageIndex;

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

    public StructuredState State { get; private set; } = StructuredState.Empty;

    private int ThresholdTokens => _options!.CompactionThresholdTokens > 0
        ? _options.CompactionThresholdTokens
        : _options.ContextWindowTokens;

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
        if (_messages.Count                                       == 0 || _messages[0].Role != ChatRole.System ||
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
            if (record.Schema   != "tinyharness.project-instructions/1" ||
                record.Revision != record.Snapshot.Revision || record.PreviousRevision != current?.Revision ||
                string.IsNullOrWhiteSpace(record.TurnId) || string.IsNullOrWhiteSpace(record.RunId) ||
                string.IsNullOrWhiteSpace(record.Reason))
                throw new InvalidDataException("Invalid project instruction revision chain.");
            if (record.Action == "rules-applied")
            {
                if (current?.Revision == record.Revision || record.CanonicalMessageIndex is not { } index ||
                    index < 1 || index >= _messages.Count || !indices.Add(index) ||
                    index <= previousMessageIndex ||
                    _messages[index].Role != ChatRole.User ||
                    _messages[index].Content != ProjectInstructionRenderer.Render(record.Snapshot) ||
                    ChatHistoryInvariants.LastCompleteBoundary(_messages.Take(index).ToArray()) != index)
                    throw new InvalidDataException("Invalid project instruction canonical message binding.");
                previousMessageIndex = index;
            }
            else if (record.Action   != "scopes-tracked" || current is null ||
                     record.Revision != current.Revision || record.CanonicalMessageIndex is not null)
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
        State                  = StructuredState.Empty;
        _hasState              = false;
        _foldStartMessageIndex = -1;
        _foldEndMessageIndex   = 0;
        _pendingFold           = null;
        _messagesAtLastAttempt = -1;
    }

    /// <summary>
    ///     <para>
    ///         从持久化快照恢复完整历史、结构化状态和折叠位置。
    ///     </para>
    ///     <para>
    ///         Restores complete history, structured state and fold position from a persisted snapshot.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         仅用于会话恢复。起点 -1 表示未折叠，不得携带状态或非零终点；
    ///         已折叠时必须有状态，且 0 ≤ 起点 &lt; 终点 ≤ 消息数。
    ///     </para>
    ///     <para>
    ///         Use only for session restoration. Start -1 means unfolded and permits neither state nor a nonzero end;
    ///         a folded position requires state and 0 ≤ start &lt; end ≤ message count.
    ///     </para>
    /// </remarks>
    public void Restore(IReadOnlyList<ChatMessage> messages,              StructuredState state, bool hasState,
                        int                        foldStartMessageIndex, int             foldEndMessageIndex)
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

        Reset();
        _messages.AddRange(messages);
        State                  = state;
        _hasState              = hasState;
        _foldStartMessageIndex = foldStartMessageIndex;
        _foldEndMessageIndex   = foldEndMessageIndex;
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
        var retained = _instructionRecords.TakeWhile(record =>
                                                         record.CanonicalMessageIndex is not { } index ||
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
    ///     <para>
    ///         构建本次请求发送给模型的视图：头部 + （如有）结构化状态消息 + 未折叠回合；
    ///         未启用预算时仍过滤过期宿主规则，未管理规则时直通完整历史。
    ///     </para>
    ///     <para>
    ///         Builds the view sent to the model on the next request: retained head, the
    ///         structured-state message when present, then all not-yet-folded turns. In
    ///         pass-through mode source bindings still filter superseded rules; unmanaged history stays unchanged.
    ///     </para>
    /// </summary>
    public IReadOnlyList<ChatMessage> BuildModelView() =>
        BuildProjectedView(_hasState ? State : null,
                           Enabled ? _foldStartMessageIndex : -1,
                           Enabled ? _foldEndMessageIndex : 0);

    /// <summary>
    ///     <para>以同一来源过滤和规则插入逻辑构建实际视图及预算候选。</para>
    ///     <para>Builds actual and budget candidate views with identical source filtering and rule insertion.</para>
    /// </summary>
    private IReadOnlyList<ChatMessage> BuildProjectedView(StructuredState? state, int foldStart, int foldEnd)
    {
        var view = new List<ChatMessage>(_messages.Count + 2);
        for (var i = 0; i < _messages.Count; i++)
        {
            if (state is not null && i == foldStart)
                view.Add(ChatMessage.User(state.ToJson()));
            if ((i >= foldStart && i < foldEnd) || _instructionMessageIndices.Contains(i)) continue;
            view.Add(Enabled ? CapViewMessage(_messages[i]) : _messages[i]);
            if (i == 0 && ProjectInstructions is { } snapshot)
                view.Add(ChatMessage.User(ProjectInstructionRenderer.Render(snapshot)));
        }

        return view;
    }

    /// <summary>
    ///     <para>
    ///         构造受独立预算约束的摘要请求，记录待提交的旧完整组折叠计划。
    ///     </para>
    ///     <para>
    ///         Builds an independently budgeted summary request and records the pending fold plan for old complete groups.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         请求包含指令、旧状态、首次折叠的原始任务与约束，以及已按视图上限裁剪的折叠输入。
    ///         到 TryApplyCompaction 之间不得追加消息；追加会使计划失效。
    ///     </para>
    ///     <para>
    ///         The request includes instructions, prior state, the original task and constraints on the first fold,
    ///         and fold input capped like the model view. Do not append messages before TryApplyCompaction;
    ///         appends invalidate the plan.
    ///     </para>
    /// </remarks>
    /// <returns>
    ///     <para>
    ///         没有可折叠内容时返回空列表。
    ///     </para>
    ///     <para>
    ///         Returns an empty list when nothing is foldable.
    ///     </para>
    /// </returns>
    public IReadOnlyList<ChatMessage> BuildCompactionMessages(IReadOnlyList<ToolDefinition> tools)
    {
        var selection = PlanFold(tools);
        if (selection is null) return [];

        _pendingFold = selection;
        var result = new List<ChatMessage>(selection.Spans.Count * 2 + 3)
        {
            ChatMessage.System(CompactionInstruction),
            ChatMessage.User(_hasState ? State.ToJson() : "No previous summary.")
        };

        // 首次压缩没有旧状态可推导目标，需提供头部任务与约束，不能仅凭工具记录猜测。
        if (!_hasState && BuildOriginalTaskMessage() is { } originalTask) result.Add(originalTask);
        if (ProjectInstructions is { } instructions)
            result.Add(ChatMessage.User(ProjectInstructionRenderer.Render(instructions)));

        foreach (var span in selection.Spans)
            for (var i = span.Start; i < span.Start + span.Count; i++)
                if (!_instructionMessageIndices.Contains(i))
                    result.Add(CapViewMessage(_messages[i]));

        return result;
    }

    /// <summary>
    ///     <para>
    ///         验证摘要的结构、事实保留与预算收益后提交状态并推进折叠边界。
    ///     </para>
    ///     <para>
    ///         Commits state and advances fold boundaries after validating summary structure, fact retention and budget
    ///         benefit.
    ///     </para>
    /// </summary>
    /// <returns>
    ///     <para>
    ///         无挂起计划、计划失效、解析失败、实质状态被清空、旧事实丢失或视图未缩小时返回 false。
    ///         失败不改动历史、已提交状态或折叠边界；挂起计划被消费，并在拒绝时锁定重试。
    ///     </para>
    ///     <para>
    ///         Returns false for an absent or invalid plan, parse failure, erased substantive state, lost prior facts
    ///         or a view that does not shrink. Failures leave history, committed state and fold boundaries intact;
    ///         the pending plan is consumed and rejected attempts latch retries.
    ///     </para>
    /// </returns>
    public bool TryApplyCompaction(string summaryJson)
    {
        if (_pendingFold is null) return false;

        var pending = _pendingFold;
        _pendingFold = null;

        // 计划绑定原始消息索引，追加消息后拒绝提交，避免边界与对话失配。
        if (_messages.Count != pending.MessageCountAtPlan)
        {
            LatchAttempt();
            return false;
        }

        if (!StructuredState.TryParse(summaryJson, out var parsed))
        {
            LatchAttempt();
            return false;
        }

        // 折叠必须连续；起点不衔接说明计划已失效，不能安全应用摘要。
        if (_foldStartMessageIndex >= 0 && pending.StartMessageIndex != _foldEndMessageIndex)
        {
            LatchAttempt();
            return false;
        }

        var newFoldStart = _foldStartMessageIndex < 0 ? pending.StartMessageIndex : _foldStartMessageIndex;
        var newFoldEnd   = pending.StartMessageIndex + pending.FoldMessageCount;

        // 摘要不能从模型视图抹去真实工作或已有事实。
        if (!KeepsPriorFacts(parsed, pending))
        {
            LatchAttempt();
            return false;
        }

        // 候选视图必须严格缩小，否则保留原视图。
        var beforeTokens = EstimateViewTokens();
        var afterTokens  = EstimateCandidateViewTokens(parsed, newFoldStart, newFoldEnd);
        if (afterTokens >= beforeTokens)
        {
            LatchAttempt();
            return false;
        }

        _foldStartMessageIndex = newFoldStart;
        _foldEndMessageIndex   = newFoldEnd;
        State                  = parsed;
        _hasState              = true;
        return true;
    }

    /// <summary>
    ///     <para>
    ///         记录一次失败压缩尝试（例如摘要模型调用抛错），锁定到当前消息数，避免立即重试。
    ///     </para>
    ///     <para>
    ///         Records one failed compaction attempt (for example a failed summarizer call) and
    ///         latches it to the current message count so it is not retried immediately.
    ///     </para>
    /// </summary>
    public void MarkCompactionAttempted()
    {
        LatchAttempt();
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

    /// <summary>
    ///     <para>
    ///         选择最旧一批可折叠完整组，同时保留近端上下文并满足摘要预算。
    ///     </para>
    ///     <para>
    ///         Selects the oldest batch of foldable complete groups while retaining recent context
    ///         and satisfying the summary budget.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         总是保留最新完整组，并按预算从新到旧保留更多；进行中尾组不折叠。
    ///         摘要放不下的旧组留待后继批次。
    ///     </para>
    ///     <para>
    ///         Always retains the latest complete group and keeps more newest-first while budget allows;
    ///         the in-progress tail group is not folded. Older groups exceeding the summary budget wait for later batches.
    ///     </para>
    /// </remarks>
    private FoldSelection? PlanFold(IReadOnlyList<ToolDefinition> tools)
    {
        if (!Enabled) return null;

        var spans = AssistantSpans();
        if (spans.Count < 2) return null; // 唯一回合仍在进行，不能折叠。

        var tailSpan = spans[^1];
        var complete = new List<MessageSpan>(spans.Count - 1);
        for (var i = 0; i < spans.Count - 1; i++)
            if (spans[i].Start >= _foldEndMessageIndex)
                complete.Add(spans[i]);

        if (complete.Count == 0) return null;

        var headTokens = TokenEstimator.EstimateMessages(HeadMessages());
        var stateTokens = _hasState
            ? TokenEstimator.EstimateMessage(ChatMessage.User(State.ToJson()))
            : NewStateTokenAssumption;
        var tailTokens = SpanTokens(tailSpan);
        var fixedCosts = TokenEstimator.EstimateToolDefinitions(tools) + _options!.ReservedOutputTokens;
        var remaining  = ThresholdTokens - fixedCosts - headTokens - stateTokens - tailTokens;

        var keptCount = 0;
        for (var i = complete.Count - 1; i >= 0; i--)
        {
            var cost = SpanTokens(complete[i]);
            if (keptCount > 0 && cost > remaining) break; // 此回合及更早完整回合进入折叠集合。

            keptCount++;
            remaining -= cost;
        }

        var foldable = complete.Count - keptCount;
        if (foldable <= 0) return null; // 保留最新完整回合后，无需再折叠。

        // 摘要请求也须为输出留预算；最旧原子组放不下时不强塞，交由普通请求窗口检查报错。
        var toFold           = new List<MessageSpan>();
        var summaryRemaining = SummaryInputBudgetTokens();
        for (var i = 0; i < foldable; i++)
        {
            var cost = SpanTokens(complete[i]);
            if (cost > summaryRemaining) break;

            toFold.Add(complete[i]);
            summaryRemaining -= cost;
        }

        return toFold.Count == 0 ? null : new FoldSelection(toFold) { MessageCountAtPlan = _messages.Count };
    }

    /// <summary>
    ///     <para>
    ///         摘要请求自身可用的估算预算：模型窗口扣除保留输出、指令、既有状态与原始任务后，
    ///         剩余空间用于装入被折叠回合。折叠输入与普通视图一样逐条裁剪。
    ///     </para>
    ///     <para>
    ///         Estimated budget available for the summarization request itself: the model
    ///         window minus reserved output, the instruction, the existing state and the
    ///         original task; the remainder carries the capped fold input.
    ///     </para>
    /// </summary>
    private int SummaryInputBudgetTokens()
    {
        var budget = _options!.ContextWindowTokens - _options.ReservedOutputTokens;
        budget -= TokenEstimator.EstimateMessage(ChatMessage.System(CompactionInstruction));
        budget -=
            TokenEstimator.EstimateMessage(ChatMessage.User(_hasState ? State.ToJson() : "No previous summary."));
        if (!_hasState && BuildOriginalTaskMessage() is { } originalTask)
            budget -= TokenEstimator.EstimateMessage(originalTask);
        if (ProjectInstructions is { } instructions)
            budget -= TokenEstimator.EstimateMessage(ChatMessage.User(ProjectInstructionRenderer.Render(instructions)));

        return budget;
    }

    /// <summary>
    ///     <para>
    ///         从保留头部构造首次摘要所需的原始任务与约束消息。
    ///     </para>
    ///     <para>
    ///         Builds the original task and constraints message for the first summary from the retained head.
    ///     </para>
    /// </summary>
    /// <returns>
    ///     <para>
    ///         头部无内容时返回 null；否则帮助摘要填写 goal、constraints 与 pendingWork，而非猜测任务意图。
    ///     </para>
    ///     <para>
    ///         Returns null when the head has no content; otherwise informs goal, constraints and pendingWork
    ///         without guessing task intent.
    ///     </para>
    /// </returns>
    private ChatMessage? BuildOriginalTaskMessage()
    {
        var text =
            new
                StringBuilder("Original task and standing constraints of this run. Fold them into goal, constraints and pendingWork:");
        var appended = false;
        for (var i = 0; i < HeadEndIndex(); i++)
        {
            if (_instructionMessageIndices.Contains(i)) continue;
            var message = _messages[i];
            if (string.IsNullOrWhiteSpace(message.Content)) continue;

            text.Append("\n[")
                .Append(message.Role)
                .Append("] ")
                .Append(message.Content);
            appended = true;
        }

        return appended ? ChatMessage.User(text.ToString()) : null;
    }

    /// <summary>
    ///     <para>
    ///         头部消息：折叠区起点之前、始终原样保留的指令与输入。
    ///     </para>
    ///     <para>
    ///         Head messages: instructions and the user input retained verbatim before the fold region.
    ///     </para>
    /// </summary>
    private IReadOnlyList<ChatMessage> HeadMessages()
    {
        var end  = HeadEndIndex();
        var head = new List<ChatMessage>();
        for (var i = 0; i < end; i++)
        {
            if (!_instructionMessageIndices.Contains(i)) head.Add(_messages[i]);
            if (i == 0 && ProjectInstructions is { } snapshot)
                head.Add(ChatMessage.User(ProjectInstructionRenderer.Render(snapshot)));
        }

        return head;
    }

    /// <summary>
    ///     <para>
    ///         头部消息的结束下标。折叠已经发生时是折叠区起点；首次折叠尚未发生时用最早完整回合
    ///         的起点——折叠一旦发生，这条边界之前的所有消息都会继续留在模型视图中，所以规划预算
    ///         时不能漏算它们。
    ///     </para>
    ///     <para>
    ///         End index of the head region. Once a fold exists this is the fold-region start;
    ///         before the first fold it is the start of the oldest complete turn, because those
    ///         messages stay in the model view after the fold and must be charged when planning.
    ///     </para>
    /// </summary>
    private int HeadEndIndex()
    {
        if (_foldStartMessageIndex >= 0) return _foldStartMessageIndex;

        var spans = AssistantSpans();
        return spans.Count == 0 ? 0 : spans[0].Start;
    }

    /// <summary>
    ///     <para>
    ///         检验摘要没有清空实质状态或丢失既有事实与待办。
    ///     </para>
    ///     <para>
    ///         Checks that the summary neither erases substantive state nor loses prior facts or pending work.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         已有 Goal 不得变空；文件、命令与决策须保留。约束可更新，已有待办须保留
    ///         或以 <c>completed: &lt;item&gt;</c> 明确关闭，其中 item 为原待办文本。
    ///         这些检查只防止关键状态静默丢失，不保证消除所有语义失真。
    ///     </para>
    ///     <para>
    ///         A prior nonempty Goal must remain nonempty; files, commands and decisions must remain.
    ///         Constraints may change; each prior pending item must remain or close explicitly with
    ///         <c>completed: &lt;item&gt;</c>, where item is the original pending text.
    ///         These checks prevent silent loss of key state, not every semantic distortion.
    ///     </para>
    /// </remarks>
    private bool KeepsPriorFacts(StructuredState parsed, FoldSelection pending)
    {
        var foldCarriesSubstance = false;
        foreach (var span in pending.Spans)
            for (var i = span.Start; i < span.Start + span.Count && !foldCarriesSubstance; i++)
            {
                var message = _messages[i];
                foldCarriesSubstance = message.Role == ChatRole.Tool               ||
                                       !string.IsNullOrWhiteSpace(message.Content) ||
                                       message.ToolCalls is { Count: > 0 };
            }

        if (parsed.IsEmpty && (!State.IsEmpty || foldCarriesSubstance)) return false;

        if (!_hasState) return true;

        if (!string.IsNullOrWhiteSpace(State.Goal) && string.IsNullOrWhiteSpace(parsed.Goal)) return false;

        return ContainsEvery(parsed.FilesInspected, State.FilesInspected)
            && ContainsEvery(parsed.FilesModified, State.FilesModified)
            && ContainsEvery(parsed.CommandsAndResults, State.CommandsAndResults)
            && ContainsEvery(parsed.Decisions, State.Decisions)
            && KeepsPendingWork(parsed.PendingWork);
    }

    private bool KeepsPendingWork(IReadOnlyList<string> pendingWork)
    {
        if (State.PendingWork.Count == 0) return true;

        if (pendingWork.Count == 0) return false;

        foreach (var priorItem in State.PendingWork)
        {
            if (pendingWork.Contains(priorItem, StringComparer.Ordinal)) continue;

            var completion = "completed: " + priorItem;
            if (!pendingWork.Contains(completion, StringComparer.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    private static bool ContainsEvery(IReadOnlyList<string> container, IReadOnlyList<string> required)
    {
        foreach (var item in required)
            if (!container.Contains(item, StringComparer.Ordinal))
                return false;

        return true;
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
    private int EstimateCandidateViewTokens(StructuredState state, int foldStart, int foldEnd)
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
            var end = i + 1 < starts.Count ? starts[i + 1] : _messages.Count;
            spans.Add(new MessageSpan(starts[i], end - starts[i]));
        }

        return spans;
    }

    private int SpanTokens(MessageSpan span)
    {
        var tokens = 0;
        for (var i = span.Start; i < span.Start + span.Count; i++)
            if (!_instructionMessageIndices.Contains(i))
                tokens += TokenEstimator.EstimateMessage(CapViewMessage(_messages[i]));

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

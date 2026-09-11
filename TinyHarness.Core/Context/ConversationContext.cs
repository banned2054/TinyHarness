using System.Text;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Tools;

namespace TinyHarness.Core.Context;

/// <summary>
/// Context Manager：在本地保留完整历史（PLAN §13），并据此构建“发送给模型的视图”。
/// 启用预算后，视图会对每条 tool 结果消息做字符上限裁剪；当总估算超过阈值且存在可压缩
/// 的旧完整回合时，先由外部模型（禁用 tools）把最旧的完整回合汇总成 <see cref="StructuredState"/>，
/// 再提交压缩。assistant tool_calls 与其 tool 消息始终作为不可拆分的原子组整体保留或整体折叠，
/// 进行中的回合（最新 assistant + 尚未续写的 tool 结果）永远不会被折叠。
///
/// 压缩请求本身也受预算约束：折叠输入按视图上限逐条裁剪，并按独立预算从最旧回合开始分批，
/// 放不下的旧回合留到同一预请求阶段的后继压缩批次继续折叠。提交摘要前会验证其保留此前已
/// 折叠状态中的事实、确实使视图变小，且计划构建后历史未被追加；任一不满足都回滚并保持原视图。
///
/// The context manager: retains the full history locally (PLAN §13) and derives the
/// model-facing view from it. When budgeting is enabled the view caps every tool-result
/// message; once the estimated total exceeds the threshold and foldable old complete
/// turns exist, an external model (with tools disabled) summarizes the oldest complete
/// turns into a <see cref="StructuredState"/> which is then committed. An assistant's
/// tool_calls and their tool messages are treated as one atomic group that is either
/// kept whole or folded whole; the in-progress turn (newest assistant plus its tool
/// results that have not been continued yet) is never folded.
///
/// The summarization request is budgeted on its own: every folded message is capped
/// like the model view, and the fold set is batched oldest-first against an independent
/// budget, leaving any overflow for later compaction passes in the same pre-request
/// phase. Before a summary is committed it must preserve the facts of the previously
/// folded state, make the view strictly smaller, and the history must not have grown
/// since the plan was built; any failure rolls back and keeps the original view.
/// </summary>
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

    private readonly ContextOptions?   _options;
    private readonly List<ChatMessage> _messages = [];
    private          StructuredState   _state    = StructuredState.Empty;
    private          bool              _hasState;

    // Raw-message window of the turns already folded into _state. Nothing between
    // _foldStartMessageIndex and _foldEndMessageIndex appears in the model view,
    // while the full messages stay retained below.
    private int _foldStartMessageIndex = -1;
    private int _foldEndMessageIndex;

    private FoldSelection? _pendingFold;

    // Failed compaction attempts are latched to the message count they ran against:
    // until new messages arrive the manager will not retry a failed or rejected
    // compaction, so it can never spin in a loop. Successful compactions do not
    // latch, because the caller may need more passes to fold everything.
    private int _messagesAtLastAttempt = -1;

    /// <summary>
    /// 创建上下文管理器。<paramref name="options"/> 为 <see langword="null"/> 时不启用预算
    /// 与压缩，视图等于完整历史（向后兼容的直通模式）。
    ///
    /// Creates the context manager. A <see langword="null"/> <paramref name="options"/>
    /// disables budgeting and compaction: the view equals the full history (pass-through
    /// mode kept for backward compatibility).
    /// </summary>
    public ConversationContext(ContextOptions? options = null)
    {
        _options = options?.Validate();
    }

    /// <summary>
    /// 是否启用预算与压缩。
    /// Whether budgeting and compaction are enabled.
    /// </summary>
    public bool Enabled => _options is not null;

    /// <summary>
    /// 完整本地历史（压缩不会移除其中的任何消息）。
    /// The complete local history; compaction never removes messages from it.
    /// </summary>
    public IReadOnlyList<ChatMessage> Messages => _messages;

    public StructuredState State => _state;

    /// <summary>
    /// 追加一条消息；任何追加都会解除上次压缩尝试的重试锁。
    /// Appends one message; every append releases the previous compaction-attempt latch.
    /// </summary>
    public void Append(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _messages.Add(message);
        _messagesAtLastAttempt = -1;
    }

    /// <summary>
    /// 清空历史并回到未压缩状态，用于开始一次新任务。
    /// Clears the history and compaction state, ready for a fresh task.
    /// </summary>
    public void Reset()
    {
        _messages.Clear();
        _state                 = StructuredState.Empty;
        _hasState              = false;
        _foldStartMessageIndex = -1;
        _foldEndMessageIndex   = 0;
        _pendingFold           = null;
        _messagesAtLastAttempt = -1;
    }

    /// <summary>
    /// 判断下一次模型请求前是否需要压缩：总估算（视图 + 工具定义 + 保留输出）超过阈值，
    /// 且存在尚未折叠、可压缩的旧完整回合。只记录失败尝试的重试锁在此生效。
    ///
    /// Reports whether the next model request requires compaction: the total estimate
    /// (view + tool definitions + reserved output) exceeds the threshold and there are
    /// foldable old complete turns not yet compacted. The retry latch recorded for
    /// failed attempts is honored here.
    /// </summary>
    public bool RequiresCompaction(IReadOnlyList<ToolDefinition> tools)
    {
        if (!Enabled || _messagesAtLastAttempt == _messages.Count || PlanFold(tools) is null)
        {
            return false;
        }

        var total = EstimateViewTokens() + TokenEstimator.EstimateToolDefinitions(tools);
        return total + _options!.ReservedOutputTokens > ThresholdTokens;
    }

    /// <summary>
    /// 当前模型视图的估算 token 数（含状态消息与 tool 结果裁剪）。
    /// Estimated tokens of the current model view, including the state message and caps.
    /// </summary>
    public int EstimateViewTokens() => TokenEstimator.EstimateMessages(BuildModelView());

    /// <summary>
    /// 构建本次请求发送给模型的视图：头部 + （如有）结构化状态消息 + 未折叠回合；
    /// 在直通模式下等于完整历史。
    ///
    /// Builds the view sent to the model on the next request: retained head, the
    /// structured-state message when present, then all not-yet-folded turns. In
    /// pass-through mode this equals the full history.
    /// </summary>
    public IReadOnlyList<ChatMessage> BuildModelView()
    {
        if (!Enabled)
        {
            return _messages.ToList();
        }

        var view      = new List<ChatMessage>(_messages.Count + 1);
        var prefixEnd = _foldStartMessageIndex < 0 ? 0 : _foldStartMessageIndex;
        for (var i = 0; i < prefixEnd; i++)
        {
            view.Add(CapViewMessage(_messages[i]));
        }

        if (_hasState)
        {
            view.Add(ChatMessage.User(_state.ToJson()));
        }

        for (var i = _foldEndMessageIndex; i < _messages.Count; i++)
        {
            view.Add(CapViewMessage(_messages[i]));
        }

        return view;
    }

    /// <summary>
    /// 构造本次压缩的摘要请求消息：指令 + 已有状态 + （首次压缩时）原始任务与约束 +
    /// 将要折叠的旧完整回合（每条已按视图上限裁剪）。当没有可折叠内容时返回空列表。
    /// 调用成功后、<see cref="TryApplyCompaction"/> 之前不应追加新消息；若追加，
    /// 应用摘要会因消息数不匹配而失败。
    ///
    /// Builds the summarization request messages: instructions, the current state
    /// (when present), the original task and constraints (on the first fold only),
    /// then the old complete turns to fold, each capped like the model view. Returns
    /// an empty list when there is nothing to fold. Do not append messages between
    /// this call and <see cref="TryApplyCompaction"/>; an append makes the commit
    /// fail on the message-count mismatch.
    /// </summary>
    public IReadOnlyList<ChatMessage> BuildCompactionMessages(IReadOnlyList<ToolDefinition> tools)
    {
        var selection = PlanFold(tools);
        if (selection is null)
        {
            return [];
        }

        _pendingFold = selection;
        var result = new List<ChatMessage>(selection.Spans.Count * 2 + 3)
        {
            ChatMessage.System(CompactionInstruction),
            ChatMessage.User(_hasState ? _state.ToJson() : "No previous summary."),
        };

        // First fold only: the summarizer has no compacted state to derive the goal
        // from, so hand it the original task and standing constraints that remain in
        // the retained head instead of letting it guess from bare tool records.
        if (!_hasState && BuildOriginalTaskMessage() is { } originalTask)
        {
            result.Add(originalTask);
        }

        foreach (var span in selection.Spans)
        {
            for (var i = span.Start; i < span.Start + span.Count; i++)
            {
                result.Add(CapViewMessage(_messages[i]));
            }
        }

        return result;
    }

    /// <summary>
    /// 提交摘要结果：解析、校验结构、验证状态保留与压缩收益后推进折叠边界。解析失败、
    /// 摘要为空或丢弃了此前已记录的事实、候选视图没有变小、没有挂起的折叠计划、或消息
    /// 在计划后被追加，都会返回 <see langword="false"/>，且不修改任何上下文状态。
    ///
    /// Commits a summarizer result: parses and validates the structured state, verifies
    /// that it preserves previously recorded facts and that the candidate view is
    /// strictly smaller, then advances the fold boundary. Returns
    /// <see langword="false"/> — without touching any context state — when parsing
    /// fails, the summary is empty or drops previously recorded facts, the candidate
    /// view does not shrink, no fold plan is pending, or messages were appended after
    /// the plan was built.
    /// </summary>
    public bool TryApplyCompaction(string summaryJson)
    {
        if (_pendingFold is null)
        {
            return false;
        }

        var pending = _pendingFold;
        _pendingFold = null;

        // The plan addresses raw message indexes; an append after planning would move
        // the fold boundary out of sync with the conversation, so the commit is refused.
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

        // Folds are always contiguous: the next foldable span starts where the
        // previous folded region ended. A mismatch means the conversation changed
        // underneath the plan, so the summary cannot be applied safely.
        if (_foldStartMessageIndex >= 0 && pending.StartMessageIndex != _foldEndMessageIndex)
        {
            LatchAttempt();
            return false;
        }

        var newFoldStart = _foldStartMessageIndex < 0 ? pending.StartMessageIndex : _foldStartMessageIndex;
        var newFoldEnd   = pending.StartMessageIndex + pending.FoldMessageCount;

        // Never commit a summary that would wipe real work or previously recorded
        // facts from the model's view (decisions, modified files, pending work, ...).
        if (!KeepsPriorFacts(parsed, pending))
        {
            LatchAttempt();
            return false;
        }

        // Only commit when the summary actually buys budget: the candidate view must
        // be strictly smaller than the current one, otherwise the fold is pointless
        // and the original view is kept.
        var beforeTokens = EstimateViewTokens();
        var afterTokens  = EstimateCandidateViewTokens(parsed, newFoldStart, newFoldEnd);
        if (afterTokens >= beforeTokens)
        {
            LatchAttempt();
            return false;
        }

        _foldStartMessageIndex = newFoldStart;
        _foldEndMessageIndex   = newFoldEnd;
        _state                 = parsed;
        _hasState              = true;
        return true;
    }

    /// <summary>
    /// 记录一次失败压缩尝试（例如摘要模型调用抛错），锁定到当前消息数，避免立即重试。
    /// Records one failed compaction attempt (for example a failed summarizer call) and
    /// latches it to the current message count so it is not retried immediately.
    /// </summary>
    public void MarkCompactionAttempted() => LatchAttempt();

    /// <summary>
    /// 单条 tool 结果消息在模型视图中的裁剪；启用预算时只对超长结果生效。
    /// Applies the per-tool-result character cap to a view message when budgeting is on.
    /// </summary>
    private ChatMessage CapViewMessage(ChatMessage message)
    {
        if (message.Role != ChatRole.Tool || message.Content.Length <= _options!.ToolResultViewCharacters)
        {
            return message;
        }

        var cap  = _options.ToolResultViewCharacters;
        var head = cap / 2;
        var tail = cap - head;
        var content = message.Content[..head]                                                     +
                      $"\n... [model view truncated to {cap} of {message.Content.Length} chars; " +
                      "the full result stays in the local history] ...\n"                         +
                      message.Content[^tail..];
        return ChatMessage.Tool(message.Name ?? string.Empty, message.ToolCallId ?? string.Empty, content);
    }

    private int ThresholdTokens => _options!.CompactionThresholdTokens > 0
        ? _options.CompactionThresholdTokens
        : _options.ContextWindowTokens;

    /// <summary>
    /// 计划本次要折叠的旧完整回合：总是保留最新完整回合作为近端上下文，再按预算从新到旧
    /// 继续保留，其余较旧的完整回合进入折叠集合。折叠集合还要满足摘要请求自己的预算——
    /// 放不下的部分留到后继批次，因此一次计划可能只折叠最旧的一批完整回合。
    ///
    /// Plans the old complete turns to fold: the newest complete turn is always kept as
    /// nearby context, further turns are kept newest-first while the budget allows, and
    /// the remaining older complete turns form the fold set. The fold set must also fit
    /// the summarization request's own budget, so a single plan may fold only the
    /// oldest batch and leave the rest for the next pass.
    /// </summary>
    private FoldSelection? PlanFold(IReadOnlyList<ToolDefinition> tools)
    {
        if (!Enabled)
        {
            return null;
        }

        var spans = AssistantSpans();
        if (spans.Count < 2)
        {
            return null; // The only turn is still in progress; nothing is complete yet.
        }

        var tailSpan = spans[^1];
        var complete = new List<MessageSpan>(spans.Count - 1);
        for (var i = 0; i < spans.Count - 1; i++)
        {
            if (spans[i].Start >= _foldEndMessageIndex)
            {
                complete.Add(spans[i]);
            }
        }

        if (complete.Count == 0)
        {
            return null;
        }

        var headTokens = TokenEstimator.EstimateMessages(HeadMessages());
        var stateTokens = _hasState
            ? TokenEstimator.EstimateMessage(ChatMessage.User(_state.ToJson()))
            : NewStateTokenAssumption;
        var tailTokens = SpanTokens(tailSpan);
        var fixedCosts = TokenEstimator.EstimateToolDefinitions(tools) + _options!.ReservedOutputTokens;
        var remaining  = ThresholdTokens - fixedCosts - headTokens - stateTokens - tailTokens;

        var keptCount = 0;
        for (var i = complete.Count - 1; i >= 0; i--)
        {
            var cost = SpanTokens(complete[i]);
            if (keptCount > 0 && cost > remaining)
            {
                break; // Fold this and every older complete turn.
            }

            keptCount++;
            remaining -= cost;
        }

        var foldable = complete.Count - keptCount;
        if (foldable <= 0)
        {
            return null; // Every complete turn fits; there is nothing to fold.
        }

        // The summarizer input is itself a model request: fold the oldest complete
        // turns that fit the summarizer budget, oldest first. A compaction request
        // must always leave room for the reserved output, so an indivisible oldest
        // turn that does not fit is left in place for the final normal-request guard
        // to report instead of being forced into an over-budget summary.
        var toFold           = new List<MessageSpan>();
        var summaryRemaining = SummaryInputBudgetTokens();
        for (var i = 0; i < foldable; i++)
        {
            var cost = SpanTokens(complete[i]);
            if (cost > summaryRemaining)
            {
                break;
            }

            toFold.Add(complete[i]);
            summaryRemaining -= cost;
        }

        return toFold.Count == 0 ? null : new FoldSelection(toFold) { MessageCountAtPlan = _messages.Count };
    }

    /// <summary>
    /// 摘要请求自身可用的估算预算：模型窗口扣除保留输出、指令、既有状态与原始任务后，
    /// 剩余空间用于装入被折叠回合。折叠输入与普通视图一样逐条裁剪。
    ///
    /// Estimated budget available for the summarization request itself: the model
    /// window minus reserved output, the instruction, the existing state and the
    /// original task; the remainder carries the capped fold input.
    /// </summary>
    private int SummaryInputBudgetTokens()
    {
        var budget = _options!.ContextWindowTokens - _options.ReservedOutputTokens;
        budget -= TokenEstimator.EstimateMessage(ChatMessage.System(CompactionInstruction));
        budget -=
            TokenEstimator.EstimateMessage(ChatMessage.User(_hasState ? _state.ToJson() : "No previous summary."));
        if (!_hasState && BuildOriginalTaskMessage() is { } originalTask)
        {
            budget -= TokenEstimator.EstimateMessage(originalTask);
        }

        return budget;
    }

    /// <summary>
    /// 首次压缩时构造“原始任务与约束”消息：把保留在头部（折叠区之前）的 system/user
    /// 内容整理成一条 user 消息，使摘要模型能准确填写 goal、constraints 与 pendingWork，
    /// 而不是仅凭被折叠回合里的工具记录猜测任务意图。头部为空时返回 <see langword="null"/>。
    ///
    /// Builds the original-task message used on the first fold: the system/user content
    /// retained in the head (before the fold region) is gathered into one user message
    /// so the summarizer can fill goal, constraints and pendingWork accurately instead
    /// of guessing the task from bare tool records. Returns <see langword="null"/> when
    /// the head carries no content.
    /// </summary>
    private ChatMessage? BuildOriginalTaskMessage()
    {
        var text =
            new
                StringBuilder("Original task and standing constraints of this run. Fold them into goal, constraints and pendingWork:");
        var appended = false;
        for (var i = 0; i < HeadEndIndex(); i++)
        {
            var message = _messages[i];
            if (string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            text.Append("\n[")
                .Append(message.Role)
                .Append("] ")
                .Append(message.Content);
            appended = true;
        }

        return appended ? ChatMessage.User(text.ToString()) : null;
    }

    /// <summary>
    /// 头部消息：折叠区起点之前、始终原样保留的指令与输入。
    /// Head messages: instructions and the user input retained verbatim before the fold region.
    /// </summary>
    private IReadOnlyList<ChatMessage> HeadMessages()
    {
        var end = HeadEndIndex();
        return end == 0 ? [] : _messages.GetRange(0, end);
    }

    /// <summary>
    /// 头部消息的结束下标。折叠已经发生时是折叠区起点；首次折叠尚未发生时用最早完整回合
    /// 的起点——折叠一旦发生，这条边界之前的所有消息都会继续留在模型视图中，所以规划预算
    /// 时不能漏算它们。
    ///
    /// End index of the head region. Once a fold exists this is the fold-region start;
    /// before the first fold it is the start of the oldest complete turn, because those
    /// messages stay in the model view after the fold and must be charged when planning.
    /// </summary>
    private int HeadEndIndex()
    {
        if (_foldStartMessageIndex >= 0)
        {
            return _foldStartMessageIndex;
        }

        var spans = AssistantSpans();
        return spans.Count == 0 ? 0 : spans[0].Start;
    }

    /// <summary>
    /// 摘要是否保留了此前已记录的事实，且没有把承载真实工作的回合压缩成空状态。
    /// 规则：空摘要（Goal 与所有列表都为空）在旧状态有内容或折叠含实质内容时被拒绝；
    /// 旧 Goal 非空时新 Goal 不能为空；文件、命令和已有决策是历史事实，必须保留。
    /// 约束与待办是当前状态，允许用非空列表更新；每个已有待办必须保留或用明确的
    /// completed: <item> 条目关闭。这样不试图解决所有语义失真，但能阻止
    /// 摘要模型无理由吞掉关键状态。
    ///
    /// Whether the summary preserves previously recorded facts and does not compress
    /// turns that carried real work into an empty state. An all-empty summary is
    /// rejected when the previous state had content or the fold carries substance;
    /// a non-empty previous goal must stay non-empty; files, command records, and prior
    /// decisions are historical facts that cannot be dropped. Constraints and pending
    /// work may be updated, but each prior pending item must remain or be explicitly
    /// closed with a matching `completed: <item>` entry. This does not
    /// attempt to solve every semantic distortion, only silent loss of key state.
    /// </summary>
    private bool KeepsPriorFacts(StructuredState parsed, FoldSelection pending)
    {
        var foldCarriesSubstance = false;
        foreach (var span in pending.Spans)
        {
            for (var i = span.Start; i < span.Start + span.Count && !foldCarriesSubstance; i++)
            {
                var message = _messages[i];
                foldCarriesSubstance = message.Role == ChatRole.Tool
                                    || !string.IsNullOrWhiteSpace(message.Content)
                                    || message.ToolCalls is { Count: > 0 };
            }
        }

        if (parsed.IsEmpty && (!_state.IsEmpty || foldCarriesSubstance))
        {
            return false;
        }

        if (!_hasState)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(_state.Goal) && string.IsNullOrWhiteSpace(parsed.Goal))
        {
            return false;
        }

        return ContainsEvery(parsed.FilesInspected, _state.FilesInspected)
            && ContainsEvery(parsed.FilesModified, _state.FilesModified)
            && ContainsEvery(parsed.CommandsAndResults, _state.CommandsAndResults)
            && ContainsEvery(parsed.Decisions, _state.Decisions)
            && KeepsPendingWork(parsed.PendingWork);
    }

    private bool KeepsPendingWork(IReadOnlyList<string> pendingWork)
    {
        if (_state.PendingWork.Count == 0)
        {
            return true;
        }

        if (pendingWork.Count == 0)
        {
            return false;
        }

        foreach (var priorItem in _state.PendingWork)
        {
            if (pendingWork.Contains(priorItem, StringComparer.Ordinal))
            {
                continue;
            }

            var completion = "completed: " + priorItem;
            if (!pendingWork.Contains(completion, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsEvery(IReadOnlyList<string> container, IReadOnlyList<string> required)
    {
        foreach (var item in required)
        {
            if (!container.Contains(item, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 估算提交候选状态并推进折叠边界后的视图 token 数，用于提交前的收益校验。
    /// Estimates the tokens of the view after committing the candidate state and
    /// advancing the fold boundary, used to verify the compaction benefit.
    /// </summary>
    private int EstimateCandidateViewTokens(StructuredState state, int foldStart, int foldEnd)
    {
        var tokens = 0;
        for (var i = 0; i < foldStart; i++)
        {
            tokens += TokenEstimator.EstimateMessage(CapViewMessage(_messages[i]));
        }

        tokens += TokenEstimator.EstimateMessage(ChatMessage.User(state.ToJson()));
        for (var i = foldEnd; i < _messages.Count; i++)
        {
            tokens += TokenEstimator.EstimateMessage(CapViewMessage(_messages[i]));
        }

        return tokens;
    }

    private void LatchAttempt() => _messagesAtLastAttempt = _messages.Count;

    /// <summary>
    /// 定位每个 assistant 回合：一条 assistant 消息及其后紧邻的连续 tool 消息（原子组）。
    /// Locates every assistant turn: an assistant message plus its contiguous tool messages (one atomic group).
    /// </summary>
    private List<MessageSpan> AssistantSpans()
    {
        var spans = new List<MessageSpan>();
        for (var i = 0; i < _messages.Count; i++)
        {
            if (_messages[i].Role != ChatRole.Assistant)
            {
                continue;
            }

            var start = i;
            while (i + 1 < _messages.Count && _messages[i + 1].Role == ChatRole.Tool)
            {
                i++;
            }

            spans.Add(new MessageSpan(start, i - start + 1));
        }

        return spans;
    }

    private int SpanTokens(MessageSpan span)
    {
        var tokens = 0;
        for (var i = span.Start; i < span.Start + span.Count; i++)
        {
            tokens += TokenEstimator.EstimateMessage(CapViewMessage(_messages[i]));
        }

        return tokens;
    }

    /// <summary>
    /// 保留在原始历史中的消息区间。
    /// A message range within the retained raw history.
    /// </summary>
    private readonly record struct MessageSpan(int Start, int Count);

    /// <summary>
    /// 一次压缩选择的折叠区间（必然是连续前缀）。
    /// The fold ranges chosen by one compaction (always a contiguous prefix).
    /// </summary>
    private sealed record FoldSelection(IReadOnlyList<MessageSpan> Spans)
    {
        /// <summary>
        /// 构建计划时的消息总数；提交时若消息已追加则计划失效。
        /// Total message count when the plan was built; appends invalidate the plan.
        /// </summary>
        public int MessageCountAtPlan { get; init; }

        public int StartMessageIndex => Spans[0].Start;

        public int FoldMessageCount
        {
            get
            {
                var count = 0;
                foreach (var span in Spans)
                {
                    count += span.Count;
                }

                return count;
            }
        }
    }
}

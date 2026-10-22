using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Tools;

namespace TinyHarness.Core.Models.Session;

/// <summary>
///     <para>
///         宿主事件的种类：运行生命周期、assistant 文本增量、工具生命周期（准备/开始/终态）、
///         审批请求与结论、usage、压缩、错误与运行终态。文本增量按模型流原样发布
///         （显示前脱敏由宿主负责）；reasoning 与 opaque 内容不发布、不默认展示。
///     </para>
///     <para>
///         Host event kinds: run lifecycle, assistant text deltas, tool lifecycle
///         (prepared/started/finished), approval requests and resolutions, usage, compaction,
///         errors and the run terminal state. Text deltas are published as streamed
///         (display-side redaction is the host's job); reasoning and opaque content is never
///         published or displayed by default.
///     </para>
/// </summary>
public enum HostEventKind
{
    /// <summary>
    ///     <para>一次运行开始处理一个回合；无负载。</para>
    ///     <para>A run started processing a turn; no payload.</para>
    /// </summary>
    RunStarted = 1,

    /// <summary>
    ///     <para>一次成功的上下文压缩；负载为 <see cref="ContextChange" />。</para>
    ///     <para>One successful context compaction; payload is a <see cref="ContextChange" />.</para>
    /// </summary>
    CompactionApplied = 2,

    /// <summary>
    ///     <para>
    ///         一次运行到达终态；负载为 <see cref="AgentResult" />，包含最终状态、步数与错误
    ///         说明。终态事件不因取消而丢弃。
    ///     </para>
    ///     <para>
    ///         A run reached its terminal state; payload is the <see cref="AgentResult" /> with
    ///         the final status, step counts and error detail. The terminal event is never
    ///         dropped because of cancellation.
    ///     </para>
    /// </summary>
    RunFinished = 3,

    /// <summary>
    ///     <para>
    ///         assistant 正文的一个文本增量；负载为增量字符串（<see cref="string" />）。增量按模型
    ///         流顺序发布且可能包含已知密钥——宿主必须在显示前做跨片段脱敏。压缩摘要请求的
    ///         流不发布该事件，避免内部摘要文本混入回答展示。
    ///     </para>
    ///     <para>
    ///         One assistant-content text delta; payload is the delta string
    ///         (<see cref="string" />). Deltas are published in stream order and can carry
    ///         known secrets — the host must redact across fragments before display. The
    ///         compaction summary request's stream does not publish this event, so internal
    ///         summary text never mixes into the answer display.
    ///     </para>
    /// </summary>
    AssistantTextDelta = 4,

    /// <summary>
    ///     <para>
    ///         一次调用已完成准备并通过校验；负载为 <see cref="ToolPreparation" />。准备无副作用，
    ///         该事件不代表已授权或已执行。
    ///     </para>
    ///     <para>
    ///         One call finished preparation and validation; payload is
    ///         <see cref="ToolPreparation" />. Preparation is side-effect free, so this event
    ///         implies neither authorization nor execution.
    ///     </para>
    /// </summary>
    ToolPrepared = 5,

    /// <summary>
    ///     <para>
    ///         权限引擎要求人工审批；负载为 <see cref="ToolPreparation" />。交互式审批界面由
    ///         审批器绘制，该事件供非终端宿主呈现同一等待状态。
    ///     </para>
    ///     <para>
    ///         The permission engine asked for a human approval; payload is
    ///         <see cref="ToolPreparation" />. The interactive approval UI is drawn by the
    ///         approval provider; this event lets non-terminal hosts show the same wait.
    ///     </para>
    /// </summary>
    ApprovalRequested = 6,

    /// <summary>
    ///     <para>
    ///         一次权限结论已确定；负载为 <see cref="ApprovalResolution" />。规则允许/拒绝与交互
    ///         审批结果都以同一事件发布。
    ///     </para>
    ///     <para>
    ///         One permission decision is final; payload is <see cref="ApprovalResolution" />.
    ///         Rule-based allows/denies and interactive approval outcomes publish alike.
    ///     </para>
    /// </summary>
    ApprovalResolved = 7,

    /// <summary>
    ///     <para>一次已授权调用开始执行；负载为 <see cref="ToolPreparation" />。</para>
    ///     <para>One authorized call started executing; payload is <see cref="ToolPreparation" />.</para>
    /// </summary>
    ToolStarted = 8,

    /// <summary>
    ///     <para>
    ///         一次已开始执行的调用到达终态；负载为 <see cref="ToolExecutionOutcome" />。被拒绝或
    ///         未准备的调用不产生该事件，其结论由 <see cref="ApprovalResolved" /> 与回传模型的
    ///         工具结果承载。
    ///     </para>
    ///     <para>
    ///         One started call reached its terminal state; payload is
    ///         <see cref="ToolExecutionOutcome" />. Denied or unprepared calls never produce
    ///         this event; their outcomes ride on <see cref="ApprovalResolved" /> and the tool
    ///         result returned to the model.
    ///     </para>
    /// </summary>
    ToolFinished = 9,

    /// <summary>
    ///     <para>
    ///         一次模型请求的 usage 元数据；负载为 <see cref="ModelUsage" />。普通请求与压缩摘要
    ///         请求都发布（<see cref="ModelUsage.SummaryRequest" /> 区分）；usage 与 finish reason
    ///         都缺失时不发布——仅有 finish reason 时事件仍发布，两个 token 字段为 null。
    ///     </para>
    ///     <para>
    ///         Usage metadata for one model request; payload is <see cref="ModelUsage" />. Both
    ///         regular and compaction summary requests publish (<see cref="ModelUsage.SummaryRequest" />
    ///         tells them apart); nothing is published when both the usage and the finish reason
    ///         are absent — a finish reason alone still publishes with null token counts.
    ///     </para>
    /// </summary>
    ModelUsage = 10,

    /// <summary>
    ///     <para>
    ///         一次运行级错误的非终态提示；负载为错误说明字符串（<see cref="string" />）。仅在运行
    ///         以失败终态结束时发布，先于 <see cref="RunFinished" />；取消不发布该事件。
    ///     </para>
    ///     <para>
    ///         A non-terminal notice of a run-level error; payload is the error detail string
    ///         (<see cref="string" />). Published only when the run ends in a failed terminal
    ///         state, right before <see cref="RunFinished" />; cancellation does not publish it.
    ///     </para>
    /// </summary>
    RunError = 11,

    /// <summary>
    ///     <para>会话历史条目已可靠追加；负载为与日志逐字段一致的 <see cref="SessionHistoryItem" />。</para>
    ///     <para>
    ///         A session history item was durably appended; payload is the field-identical <see cref="SessionHistoryItem" />
    ///         .
    ///     </para>
    /// </summary>
    HistoryItemCompleted = 12,

    /// <summary>
    ///     <para>
    ///         一组项目指令已在统一日志可靠提交并生效；负载为
    ///         <see cref="ProjectInstructionsChanged" /> 的元数据摘要，不包含规则正文。
    ///     </para>
    ///     <para>
    ///         A project-instruction set was reliably committed and became effective; the payload is the
    ///         metadata-only <see cref="ProjectInstructionsChanged" /> summary and never includes rule text.
    ///     </para>
    /// </summary>
    ProjectInstructionsChanged = 13,

    /// <summary>
    ///     <para>压缩未提交；负载为不含模型正文的 CompactionFailureReason。</para>
    ///     <para>Compaction was not committed; payload is CompactionFailureReason without model text.</para>
    /// </summary>
    CompactionFailed = 14,

    /// <summary>
    ///     <para>已在安全边界暂停、继续或应用补充；负载为状态字符串，不含用户原话。</para>
    ///     <para>Paused, resumed or applied an amendment at a safe boundary; payload is a state string without user text.</para>
    /// </summary>
    RunControlChanged = 15,

    /// <summary>
    ///     <para>模型请求开始；负载为 ModelRequestProgress，不含请求正文。</para>
    ///     <para>A model request started; payload is ModelRequestProgress without request text.</para>
    /// </summary>
    ModelRequestStarted = 16,

    /// <summary>
    ///     <para>模型请求仍在等待流事件；负载为 ModelRequestProgress，不展示推理内容。</para>
    ///     <para>A model request is still awaiting stream events; payload is ModelRequestProgress without reasoning.</para>
    /// </summary>
    ModelRequestWaiting = 17,

    /// <summary>
    ///     <para>模型请求已结束或失败；负载为 ModelRequestProgress，取消也须交付。</para>
    ///     <para>A model request finished or failed; payload is ModelRequestProgress, delivered even on cancellation.</para>
    /// </summary>
    ModelRequestFinished = 18,

    /// <summary>
    ///     <para>首次观察到一种请求活动；负载仅为 ModelRequestProgress，不包含模型原文。</para>
    ///     <para>A request activity was observed for the first time; payload is metadata-only ModelRequestProgress.</para>
    /// </summary>
    ModelRequestActivity = 19
}

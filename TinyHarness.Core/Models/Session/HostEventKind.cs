using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Tools;

namespace TinyHarness.Core.Models.Session;

/// <summary>
///     宿主事件的种类（PLAN §6）。M12.2 贯通完整集合：运行生命周期、assistant 文本增量、
///     工具生命周期（准备/开始/终态）、审批请求与结论、usage、压缩、错误与运行终态。
///     文本增量按模型流原样发布（显示前脱敏由宿主负责）；reasoning 与 opaque 内容不发布、
///     不默认展示（PLAN §6）。
///     Host event kinds (PLAN §6). M12.2 wires the complete set: run lifecycle, assistant
///     text deltas, tool lifecycle (prepared/started/finished), approval requests and
///     resolutions, usage, compaction, errors and the run terminal state. Text deltas are
///     published as streamed (display-side redaction is the host's job); reasoning and
///     opaque content is never published or displayed by default (PLAN §6).
/// </summary>
public enum HostEventKind
{
    /// <summary>一次运行开始处理一个回合；无负载。A run started processing a turn; no payload.</summary>
    RunStarted = 1,

    /// <summary>一次成功的上下文压缩；负载为 <see cref="ContextChange" />。One successful context compaction; payload is a <see cref="ContextChange" />.</summary>
    CompactionApplied = 2,

    /// <summary>
    ///     一次运行到达终态；负载为 <see cref="AgentResult" />，包含最终状态、步数与错误
    ///     说明。终态事件不因取消而丢弃。
    ///     A run reached its terminal state; payload is the <see cref="AgentResult" /> with
    ///     the final status, step counts and error detail. The terminal event is never
    ///     dropped because of cancellation.
    /// </summary>
    RunFinished = 3,

    /// <summary>
    ///     assistant 正文的一个文本增量；负载为增量字符串（<see cref="string" />）。增量按模型
    ///     流顺序发布且可能包含已知密钥——宿主必须在显示前做跨片段脱敏。压缩摘要请求的
    ///     流不发布该事件，避免内部摘要文本混入回答展示。
    ///     One assistant-content text delta; payload is the delta string
    ///     (<see cref="string" />). Deltas are published in stream order and can carry
    ///     known secrets — the host must redact across fragments before display. The
    ///     compaction summary request's stream does not publish this event, so internal
    ///     summary text never mixes into the answer display.
    /// </summary>
    AssistantTextDelta = 4,

    /// <summary>
    ///     一次调用已完成准备并通过校验；负载为 <see cref="ToolPreparation" />。准备无副作用，
    ///     该事件不代表已授权或已执行。
    ///     One call finished preparation and validation; payload is
    ///     <see cref="ToolPreparation" />. Preparation is side-effect free, so this event
    ///     implies neither authorization nor execution.
    /// </summary>
    ToolPrepared = 5,

    /// <summary>
    ///     权限引擎要求人工审批；负载为 <see cref="ToolPreparation" />。交互式审批界面由
    ///     审批器绘制，该事件供非终端宿主呈现同一等待状态。
    ///     The permission engine asked for a human approval; payload is
    ///     <see cref="ToolPreparation" />. The interactive approval UI is drawn by the
    ///     approval provider; this event lets non-terminal hosts show the same wait.
    /// </summary>
    ApprovalRequested = 6,

    /// <summary>
    ///     一次权限结论已确定；负载为 <see cref="ApprovalResolution" />。规则允许/拒绝与交互
    ///     审批结果都以同一事件发布。
    ///     One permission decision is final; payload is <see cref="ApprovalResolution" />.
    ///     Rule-based allows/denies and interactive approval outcomes publish alike.
    /// </summary>
    ApprovalResolved = 7,

    /// <summary>
    ///     一次已授权调用开始执行；负载为 <see cref="ToolPreparation" />。
    ///     One authorized call started executing; payload is <see cref="ToolPreparation" />.
    /// </summary>
    ToolStarted = 8,

    /// <summary>
    ///     一次已开始执行的调用到达终态；负载为 <see cref="ToolExecutionOutcome" />。被拒绝或
    ///     未准备的调用不产生该事件，其结论由 <see cref="ApprovalResolved" /> 与回传模型的
    ///     工具结果承载。
    ///     One started call reached its terminal state; payload is
    ///     <see cref="ToolExecutionOutcome" />. Denied or unprepared calls never produce
    ///     this event; their outcomes ride on <see cref="ApprovalResolved" /> and the tool
    ///     result returned to the model.
    /// </summary>
    ToolFinished = 9,

    /// <summary>
    ///     一次模型请求的 usage 元数据；负载为 <see cref="ModelUsage" />。普通请求与压缩摘要
    ///     请求都发布（<see cref="ModelUsage.SummaryRequest" /> 区分）；usage 与 finish reason
    ///     都缺失时不发布——仅有 finish reason 时事件仍发布，两个 token 字段为 null。
    ///     Usage metadata for one model request; payload is <see cref="ModelUsage" />. Both
    ///     regular and compaction summary requests publish (<see cref="ModelUsage.SummaryRequest" />
    ///     tells them apart); nothing is published when both the usage and the finish reason
    ///     are absent — a finish reason alone still publishes with null token counts.
    /// </summary>
    ModelUsage = 10,

    /// <summary>
    ///     一次运行级错误的非终态提示；负载为错误说明字符串（<see cref="string" />）。仅在运行
    ///     以失败终态结束时发布，先于 <see cref="RunFinished" />；取消不发布该事件。
    ///     A non-terminal notice of a run-level error; payload is the error detail string
    ///     (<see cref="string" />). Published only when the run ends in a failed terminal
    ///     state, right before <see cref="RunFinished" />; cancellation does not publish it.
    /// </summary>
    RunError = 11
}

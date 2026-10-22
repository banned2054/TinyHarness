using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Tools;

namespace TinyHarness.Core.Models.Session;

/// <summary>
///     一次权限结论的事件负载（<see cref="HostEventKind.ApprovalResolved" />）：
///     引擎结论、交互审批动作（仅 Ask 路径）与写入审计的结果说明。
///     The event payload of one permission decision
///     (<see cref="HostEventKind.ApprovalResolved" />): the engine decision, the
///     interactive approval action (Ask paths only) and the audit-recorded outcome.
/// </summary>
public sealed record ApprovalResolution(
    PermissionDecision Decision,
    ApprovalAction?    Action,
    string             Outcome);

/// <summary>
///     一次已执行调用的终态事件负载（<see cref="HostEventKind.ToolFinished" />）：完整的
///     不可变计划与回传模型的工具结果。计划与结果对象由执行栈原样给出，宿主只读。
///     The terminal event payload of one executed call
///     (<see cref="HostEventKind.ToolFinished" />): the complete immutable preparation and
///     the tool result returned to the model. Both objects come verbatim from the
///     execution stack; hosts treat them as read-only.
/// </summary>
public sealed record ToolExecutionOutcome(ToolPreparation Preparation, ToolResult Result);

/// <summary>
///     一次模型请求的 usage 元数据（<see cref="HostEventKind.ModelUsage" />）。usage 两个
///     token 字段可能同时缺失（此时事件仅承载 finish reason）；三者都缺失时不发布该事件。
///     Usage metadata for one model request (<see cref="HostEventKind.ModelUsage" />). The
///     two token fields can be absent together (the event then carries only the finish
///     reason); nothing is published when all three are absent.
/// </summary>
public sealed record ModelUsage(
    int?    InputTokens,
    int?    OutputTokens,
    string? FinishReason,
    bool    SummaryRequest);

/// <summary>
///     <para>单次模型请求的展示状态；序号包含压缩请求，耗时不是供应商网络超时。</para>
///     <para>Display state of one model request; numbering includes compaction and elapsed time is not a network timeout.</para>
/// </summary>
public sealed record ModelRequestProgress(
    int     RequestNumber,
    bool    SummaryRequest,
    long    ElapsedMilliseconds,
    string? Outcome = null,
    bool    PartialText = false);

/// <summary>
///     <para>
///         项目指令生效事件的负载（<see cref="HostEventKind.ProjectInstructionsChanged" />）：
///         只携带版本、来源数量和探测摘要，不携带规则正文或授权语义。
///     </para>
///     <para>
///         Payload for <see cref="HostEventKind.ProjectInstructionsChanged" />: revision, source count
///         and discovery metadata only, never rule text or authorization semantics.
///     </para>
/// </summary>
public sealed record ProjectInstructionsChanged(
    string                 Action,
    string                 Revision,
    string?                PreviousRevision,
    string                 Reason,
    int                    DocumentCount,
    int                    ProbedDirectoryCount,
    string                 DiscoveryFingerprint,
    string                 RenderedHash,
    string                 RendererVersion,
    IReadOnlyList<string>? Sources = null);

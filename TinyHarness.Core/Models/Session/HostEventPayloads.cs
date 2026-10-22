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
    bool    SummaryRequest)
{
    public int? CachedInputTokens { get; init; }
    public int? ReasoningTokens   { get; init; }
}

/// <summary>
///     <para>最近收到的 SDK 流活动种类，不推断服务端排队、推理进程或网络状态。</para>
///     <para>The latest observed SDK stream activity, without inferring server queues, computation or network state.</para>
/// </summary>
public enum ModelRequestPhase
{
    WaitingForResponse,
    ReceivingResponse,
    ReceivingReasoning,
    ReceivingText,
    ReceivingTools
}

/// <summary>
///     <para>单次模型请求的展示状态；序号包含压缩请求，耗时不是供应商网络超时。</para>
///     <para>Display state of one model request; numbering includes compaction and elapsed time is not a network timeout.</para>
/// </summary>
public sealed record ModelRequestProgress(
    int     RequestNumber,
    bool    SummaryRequest,
    long    ElapsedMilliseconds,
    string? Outcome     = null,
    bool    PartialText = false)
{
    public ModelRequestPhase Phase                { get; init; }
    public int               EstimatedInputTokens { get; init; }
    public int               InputMessageCount    { get; init; }

    /// <summary>
    ///     <para>从请求开始到首次 SDK 流事件的耗时；未收到为 null，不是 HTTP 首字节时间。</para>
    ///     <para>Time to the first SDK stream event; null until observed, and not HTTP time to first byte.</para>
    /// </summary>
    public long? FirstStreamEventMilliseconds { get; init; }

    public long? FirstReasoningMilliseconds  { get; init; }
    public long? FirstTextMilliseconds       { get; init; }
    public long? LastStreamEventMilliseconds { get; init; }
    public long  ReasoningCharacters         { get; init; }
    public long  TextCharacters              { get; init; }
    public int?  InputTokens                 { get; init; }
    public int?  OutputTokens                { get; init; }
    public int?  CachedInputTokens           { get; init; }
    public int?  ReasoningTokens             { get; init; }
}

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

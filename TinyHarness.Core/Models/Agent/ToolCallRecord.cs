using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Agent;

/// <summary>
///     <para>工具调用的执行意图与确认事实；用于检查，不是可重放的计划或授权。</para>
///     <para>
///         Execution intent and confirmation facts for a tool call; for inspection, not a replayable plan
///         or authorization.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         主循环随准备、授权与执行更新 <see cref="State" /> 和 <see cref="Approval" />，
///         运行结束后不再变化；持久化时写入脱敏检查视图，不据此恢复执行或授权。
///     </para>
///     <para>
///         The loop updates <see cref="State" /> and <see cref="Approval" /> during preparation,
///         authorization and execution; records stop changing when the run finishes.
///         Persistence uses a redacted inspection view, never restoring execution or authorization from it.
///     </para>
/// </remarks>
public sealed class ToolCallRecord
{
    public required string CallId { get; init; }

    /// <summary>
    ///     <para>本次运行内按 assistant 工具调用出现顺序分配的稳定执行标识。</para>
    ///     <para>A stable execution identity assigned by assistant tool-call appearance order within this run.</para>
    /// </summary>
    public string? InvocationId { get; init; }

    public required string ToolName { get; init; }

    /// <summary>
    ///     <para>调用所需的规范化能力名；准备失败、没有计划时为 null。</para>
    ///     <para>The normalized capability required by the call; null when preparation failed with no plan.</para>
    /// </summary>
    public string? Capability { get; init; }

    /// <summary>
    ///     <para>能力是否可能产生副作用；准备失败的调用从未执行，按无副作用记录。</para>
    ///     <para>
    ///         Whether the capability can produce side effects; calls that failed preparation never executed
    ///         and are recorded as side-effect-free.
    ///     </para>
    /// </summary>
    public bool SideEffects { get; init; }

    /// <summary>
    ///     <para>审批与审计使用的一行摘要；进程内保留原文，写入检查视图前脱敏。</para>
    ///     <para>
    ///         The one-line approval and audit summary; kept verbatim in memory and redacted before
    ///         entering the inspection view.
    ///     </para>
    /// </summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    ///     <para>准备失败的原因；准备成功时为 null。</para>
    ///     <para>The preparation error; null when preparation succeeded.</para>
    /// </summary>
    public string? PreparationError { get; init; }

    /// <summary>
    ///     <para>授权结论或拒绝原因；尚未授权时为 null。“allow session”仅表示本次运行期授权。</para>
    ///     <para>
    ///         The authorization outcome or denial reason; null before authorization.
    ///         "allow session" grants authorization only for the current run.
    ///     </para>
    /// </summary>
    [JsonInclude]
    public string? Approval { get; internal set; }

    [JsonInclude]
    public ToolCallExecutionState State { get; internal set; }

    internal ToolCallRecord Snapshot() => new()
    {
        CallId           = CallId,
        InvocationId     = InvocationId,
        ToolName         = ToolName,
        Capability       = Capability,
        SideEffects      = SideEffects,
        Summary          = Summary,
        PreparationError = PreparationError,
        Approval         = Approval,
        State            = State
    };
}

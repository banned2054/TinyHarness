namespace TinyHarness.Core.Models.Agent;

/// <summary>
///     <para>工具调用的执行事实：已知未开始、结果已确认或可能执行但结果未知；不由模型陈述决定。</para>
///     <para>
///         Execution facts for a tool call: known not started, confirmed result, or possibly executed with
///         an unknown result; never determined by model statements.
///     </para>
/// </summary>
public enum ToolCallExecutionState
{
    /// <summary>
    ///     <para>已准备或授权，但从未启动执行；不存在未知副作用。</para>
    ///     <para>Prepared or authorized, but execution never started; no unknown side effect exists.</para>
    /// </summary>
    KnownNotStarted,

    /// <summary>
    ///     <para>执行已启动且结果已回传历史；副作用状态已知。</para>
    ///     <para>Execution started and its result was appended to history; the side-effect state is known.</para>
    /// </summary>
    ConfirmedResult,

    /// <summary>
    ///     <para>已进入执行窗口，但结果未回传；副作用可能已发生，不补造成功、失败或退出码。</para>
    ///     <para>
    ///         The execution window was entered, but no result was appended; side effects may have occurred,
    ///         and no success, failure or exit code is fabricated.
    ///     </para>
    /// </summary>
    UnknownResult
}

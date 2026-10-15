namespace TinyHarness.Core.Services.Runtime;

/// <summary>
///     <para>执行可能已开始，但无法确认结果或进程树回收。</para>
///     <para>
///         Execution may have started, but its outcome or process-tree reclamation
///         could not be confirmed.
///     </para>
/// </summary>
/// <remarks>
///     <para>Agent 必须停止，不补造工具结果，并保留未知执行记录供检查。</para>
///     <para>
///         The agent must stop without fabricating a tool result
///         and retain the unknown execution record for inspection.
///     </para>
/// </remarks>
public sealed class ToolExecutionUncertainException(string message, Exception? innerException = null)
    : Exception(message, innerException);

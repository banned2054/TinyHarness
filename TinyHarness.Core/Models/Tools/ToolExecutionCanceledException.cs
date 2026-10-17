namespace TinyHarness.Core.Models.Tools;

/// <summary>
///     <para>表示写入已开始后取消；<see cref="Result" /> 保留已确认与未知文件事实。</para>
///     <para>Represents cancellation after writes started; <see cref="Result" /> keeps confirmed and unknown file facts.</para>
/// </summary>
public sealed class ToolExecutionCanceledException : OperationCanceledException
{
    /// <summary>
    ///     <para>创建携带部分执行结果和原始取消令牌的异常。</para>
    ///     <para>Creates an exception carrying the partial execution result and original cancellation token.</para>
    /// </summary>
    public ToolExecutionCanceledException(
        ToolResult result, CancellationToken cancellationToken, Exception? innerException = null) :
        base((result ?? throw new ArgumentNullException(nameof(result))).Content, innerException, cancellationToken) =>
        Result = result with { Succeeded = false, ExecutionIncomplete = true };

    /// <summary>
    ///     <para>执行开始后的部分结果；始终标记整体执行未完成且成功状态为 false。</para>
    ///     <para>The partial result after execution started, always marked incomplete and unsuccessful.</para>
    /// </summary>
    public ToolResult Result { get; }
}

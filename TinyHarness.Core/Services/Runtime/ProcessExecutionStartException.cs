namespace TinyHarness.Core.Services.Runtime;

/// <summary>
///     进程启动失败的信号：包装原始启动异常，让工具层把它转换为失败的 tool result
///     而不是执行阶段异常。
///     Signal that launching the process failed: wraps the original launch
///     exception so the tool layer can convert it into a failed tool result instead
///     of an execution-phase error.
/// </summary>
internal sealed class ProcessExecutionStartException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);

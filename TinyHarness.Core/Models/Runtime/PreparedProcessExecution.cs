namespace TinyHarness.Core.Models.Runtime;

/// <summary>
///     Prepare 阶段冻结的进程执行计划：直接启动的 executable、逐项 arguments、工作目录、
///     超时秒数，以及 Windows cmd 专用的原始命令尾。权限审批与执行后端消费同一实例，
///     执行阶段不再重新解释原始模型参数。
///     The frozen process-execution plan produced by Prepare: a directly launched
///     executable, per-item arguments, working directory, timeout in seconds, and
///     the raw command tail used only for Windows cmd. Approval and the execution
///     backend consume the same instance; raw model arguments are never
///     re-interpreted at execution time.
/// </summary>
public sealed record PreparedProcessExecution(
    string                Executable,
    IReadOnlyList<string> Arguments,
    string                WorkingDirectory,
    int                   TimeoutSeconds,
    bool                  WorkspaceExecutable,
    string?               RawCmdCommand);

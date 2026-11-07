namespace TinyHarness.Core.Models.Runtime;

using TinyHarness.Core.Models.Runtime.WindowsSandbox;

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
/// <remarks>
///     <para>
///         NetworkAccessEnabled 表达该命令获得的逐次在线审批；常设网络策略由执行后端自带，
///         两者任一为真即按在线执行。GrantScope 是 Prepare 期冻结的沙箱能力扩展（活动 grant
///         根的快照解析结果），仅供 WindowsSandbox 后端消费；host 后端忽略它，撤销与存活性
///         由授权侧在执行前复核。
///     </para>
///     <para>
///         NetworkAccessEnabled carries the per-command online grant; the standing
///         network policy lives with the execution backend, and either one being true
///         selects online execution. GrantScope is the sandbox capability extension
///         frozen at Prepare (the snapshot-resolved active grant roots), consumed only
///         by the WindowsSandbox backend; the host backend ignores it, and revocation
///         and liveness are re-checked by the authorization side before execution.
///     </para>
/// </remarks>
public sealed record PreparedProcessExecution(
    string                Executable,
    IReadOnlyList<string> Arguments,
    string                WorkingDirectory,
    int                   TimeoutSeconds,
    bool                  WorkspaceExecutable,
    string?               RawCmdCommand,
    bool                  NetworkAccessEnabled = false,
    SandboxGrantScope?    GrantScope           = null);

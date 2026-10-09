namespace TinyHarness.Core.Models.Runtime;

/// <summary>
/// 进程执行的终态。ExitCode 为 null 表示没有可用的命令退出码，例如进程树被终止后
/// 退出码不可读，或后端在没有启动命令的情况下失败；不为此补造退出码。
///
/// Terminal state of a process execution. ExitCode is null when no command
/// exit code is available, for example when it is unreadable after
/// process-tree termination or the backend failed before the command ran; no
/// exit code is invented in that case.
/// </summary>
public sealed record ProcessExecutionResult(
    int?                     ExitCode,
    bool                     TimedOut,
    ProcessExecutionFailure? Failure);

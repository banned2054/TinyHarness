namespace TinyHarness.Core.Models.Runtime;

/// <summary>
///     没有命令退出码的执行失败描述，例如后端启动或协议失败。
///     Description of an execution failure that produced no command exit code,
///     for example a backend launch or protocol failure.
/// </summary>
public sealed record ProcessExecutionFailure(string Message);

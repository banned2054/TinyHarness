using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
/// 已组合的 Windows 沙箱执行要素：组件、有效策略、冻结目标环境、执行策略描述和后端实例。
/// 全部在组合期一次构建；Prepare/Authorize/Execute 共享同一份冻结策略与环境。
///
/// The composed Windows sandbox execution pieces: components, the effective
/// policy, the frozen target environment, the execution policy description,
/// and the backend instance. Everything is built once at composition;
/// Prepare/Authorize/Execute share the same frozen policy and environment.
/// </summary>
public sealed record WindowsSandboxExecution(
    WindowsSandboxComponents            Components,
    SandboxIsolationPolicy              Policy,
    IReadOnlyDictionary<string, string> TargetEnvironment,
    ProcessExecutionPolicy              ExecutionPolicy,
    IProcessExecutionBackend            Backend);

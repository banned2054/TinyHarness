namespace TinyHarness.Core.Agent;

/// <summary>
/// Agent Loop 的运行中或终止状态。
///
/// Terminal or running state of an Agent loop.
/// </summary>
public enum AgentStatus
{
    Ready,
    Running,
    Completed,
    Failed,
    Cancelled,
    StepLimitReached,
}

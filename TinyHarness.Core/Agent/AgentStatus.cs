namespace TinyHarness.Core.Agent;

/// <summary>
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

using System.Text.Json.Serialization;

namespace TinyHarness.Core.Agent;

/// <summary>
/// Agent Loop 的运行中或终止状态。
///
/// Terminal or running state of an Agent loop.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentStatus>))]
public enum AgentStatus
{
    Ready,
    Running,
    Completed,
    Failed,
    Cancelled,
    StepLimitReached,
}

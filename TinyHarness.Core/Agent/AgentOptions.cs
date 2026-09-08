namespace TinyHarness.Core.Agent;

/// <summary>
/// 驱动 Agent Loop 运行行为的配置，不包含传输层选项；由组合根显式提供。
///
/// Configuration that drives the Agent Loop runtime (not the transport layer).
/// Values are supplied by the composition root; nothing is hardcoded here.
/// </summary>
public sealed record AgentOptions
{
    public required string Model { get; init; }

    public required int MaxAgentSteps { get; init; }

    public required int? DefaultToolTimeoutSeconds { get; init; }
}

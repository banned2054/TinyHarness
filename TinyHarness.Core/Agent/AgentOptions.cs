using TinyHarness.Core.Context;

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

    /// <summary>
    /// 启用 Context Manager 的预算与压缩选项；为 <see langword="null"/> 时主循环按完整历史
    /// 直通发送（测试与简单只读流程使用）。
    ///
    /// Context-manager budgeting and compaction options; when <see langword="null"/> the
    /// loop sends the full history verbatim (used by tests and simple read-only flows).
    /// </summary>
    public ContextOptions? Context { get; init; }
}

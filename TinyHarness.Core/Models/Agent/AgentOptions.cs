using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.ChatCompletions;

namespace TinyHarness.Core.Models.Agent;

/// <summary>
///     驱动 Agent Loop 运行行为的配置，不包含传输层选项；由组合根显式提供。
///     Configuration that drives the Agent Loop runtime (not the transport layer).
///     Values are supplied by the composition root; nothing is hardcoded here.
/// </summary>
public sealed record AgentOptions
{
    public required string Model { get; init; }

    /// <summary>
    ///     <para>请求推理强度；null 表示不发送强度参数，沿用服务默认值。</para>
    ///     <para>Requested reasoning effort; null omits the parameter and preserves the service default.</para>
    /// </summary>
    public ModelReasoningEffort? ReasoningEffort { get; init; }

    /// <summary>
    ///     <para>每次运行的普通模型步数上限；0 表示不限，负数无效。</para>
    ///     <para>Ordinary model-step limit per run; zero means unlimited and negative values are invalid.</para>
    /// </summary>
    public required int MaxAgentSteps { get; init; }

    public required int? DefaultToolTimeoutSeconds { get; init; }

    /// <summary>
    ///     <para>可信宿主提供的运行累计预算；不从模型或项目指令解析。</para>
    ///     <para>Cumulative run limits supplied by the trusted host, never parsed from model or project instructions.</para>
    /// </summary>
    public RunBudgetLimits Budget { get; init; } = new();

    /// <summary>
    ///     <para>由组合根显式启用会话工作项工具；低层只读循环无需注册。</para>
    ///     <para>Explicitly enables the session work-item tool at composition; low-level read-only loops need not register it.</para>
    /// </summary>
    public bool EnableWorkItems { get; init; }

    /// <summary>
    ///     启用 Context Manager 的预算与压缩选项；为 <see langword="null" /> 时主循环按完整历史
    ///     直通发送（测试与简单只读流程使用）。
    ///     Context-manager budgeting and compaction options; when <see langword="null" /> the
    ///     loop sends the full history verbatim (used by tests and simple read-only flows).
    /// </summary>
    public ContextOptions? Context { get; init; }
}

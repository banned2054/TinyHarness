namespace TinyHarness.Core.Context;

/// <summary>
/// 驱动 Context Manager 预算与压缩策略的配置。字段来自 TinyHarness 配置的 context window、
/// 保留输出与压缩阈值；估算器只做保守估算，窗口大小始终由配置显式声明（PLAN §13/§14）。
///
/// Configuration that drives the context-manager budget and compaction policy.
/// Values come from the TinyHarness config (declared context window, reserved
/// output, compaction threshold); estimates are conservative and the window is
/// never guessed from a model name (PLAN §13/§14).
/// </summary>
public sealed record ContextOptions
{
    /// <summary>
    /// 模型声明的上下文窗口（token）。
    /// The model's declared context window in tokens.
    /// </summary>
    public int ContextWindowTokens { get; init; } = 128_000;

    /// <summary>
    /// 为模型输出预留的 token 数，不参与历史发送。
    /// Tokens reserved for model output and excluded from the history budget.
    /// </summary>
    public int ReservedOutputTokens { get; init; } = 8_000;

    /// <summary>
    /// 触发压缩的总估算阈值。0 表示自动取 <see cref="ContextWindowTokens"/>。
    /// Total estimated-token level that triggers compaction. Zero selects the
    /// automatic default equal to <see cref="ContextWindowTokens"/>.
    /// </summary>
    public int CompactionThresholdTokens { get; init; }

    /// <summary>
    /// 模型视图中单条 tool 结果消息的字符上限；完整结果始终保留在本地历史里（PLAN §13
    /// “完整历史保留在本地，压缩只影响发送给模型的视图”）。
    ///
    /// Per-tool-result character cap in the model view. The full result always
    /// stays in the locally retained history (PLAN §13: full history stays local;
    /// compaction only affects the view sent to the model).
    /// </summary>
    public int ToolResultViewCharacters { get; init; } = 12_000;

    /// <summary>
    /// 校验并返回自洽的选项；非法值抛出带上下文的异常。
    /// Validates and returns a consistent options instance; invalid values throw contextual errors.
    /// </summary>
    public ContextOptions Validate()
    {
        if (ContextWindowTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ContextWindowTokens),
                                                  "The context window must be positive.");
        }

        if (ReservedOutputTokens < 0 || ReservedOutputTokens >= ContextWindowTokens)
        {
            throw new ArgumentOutOfRangeException(nameof(ReservedOutputTokens),
                                                  "Reserved output tokens must be within the context window.");
        }

        if (CompactionThresholdTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CompactionThresholdTokens),
                                                  "The compaction threshold cannot be negative.");
        }

        if (CompactionThresholdTokens > ContextWindowTokens)
        {
            throw new ArgumentOutOfRangeException(nameof(CompactionThresholdTokens),
                                                  "The compaction threshold cannot exceed the context window.");
        }

        if (ToolResultViewCharacters < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(ToolResultViewCharacters),
                                                  "The tool-result view cap must be at least 16 characters.");
        }

        return this;
    }
}

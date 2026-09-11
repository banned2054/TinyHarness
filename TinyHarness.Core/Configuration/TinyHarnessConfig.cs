namespace TinyHarness.Core.Configuration;

/// <summary>
/// TinyHarness 的强类型运行配置。端点、模型、上下文窗口和 API key 均来自配置或环境，
/// 不在代码中硬编码。
///
/// Strongly typed TinyHarness runtime configuration. No endpoint, model, context
/// window or API key is hardcoded; all values come from config plus environment.
/// </summary>
public sealed record TinyHarnessConfig
{
    public string Endpoint { get; init; } = string.Empty;

    public string ApiKeyEnvironmentVariable { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public int ContextWindowTokens { get; init; } = 128_000;

    public int ReservedOutputTokens { get; init; } = 8_000;

    public int CompactionThreshold { get; init; } = 0;

    public int MaxAgentSteps { get; init; } = 40;

    public int? DefaultToolTimeoutSeconds { get; init; } = 120;

    public string WorkspaceRoot { get; init; } = string.Empty;

    public string SessionDirectory { get; init; } = "artifacts/runs";

    public IReadOnlyList<CommandRule> CommandRules { get; init; } = [];
}

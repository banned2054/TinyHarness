namespace TinyHarness.Core.Models.Worker;

/// <summary>
///     Worker 配置的默认值与不可提高的硬上限。可信用户配置只能在默认值和硬上限之间收紧或调整。
///     Defaults and non-increasable hard ceilings for worker configuration. Trusted user settings can
///     adjust values up to the ceilings, while each MCP request has no access to these settings.
/// </summary>
public static class WorkerExecutionLimits
{
    public const int DefaultRunTimeoutSeconds          = 120;
    public const int MaxRunTimeoutSeconds              = 600;
    public const int DefaultMaxAgentSteps              = 6;
    public const int MaxAgentSteps                     = 24;
    public const int DefaultToolTimeoutSeconds         = 20;
    public const int MaxToolTimeoutSeconds             = 120;
    public const int DefaultMaxTaskPackageCharacters   = 12_000;
    public const int MaxTaskPackageCharacters          = 32_000;
    public const int DefaultMaxToolCalls               = 12;
    public const int MaxToolCalls                      = 40;
    public const int DefaultMaxToolOutputCharacters    = 24_000;
    public const int MaxToolOutputCharacters           = 128_000;
    public const int DefaultMaxCumulativeContextTokens = 32_000;
    public const int MaxCumulativeContextTokens        = 256_000;
    public const int DefaultMaxModelResponseCharacters = 12_000;
    public const int MaxModelResponseCharacters        = 64_000;
    public const int DefaultReservedOutputTokens       = 4_096;
}

namespace TinyHarness.Core.Models.Persistence;

public sealed record AuditRecord
{
    public string                 RunId        { get; init; } = string.Empty;
    public DateTimeOffset         TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string                 Kind         { get; init; } = string.Empty;
    public string?                SystemPrompt { get; init; }
    public string?                UserInput    { get; init; }
    public string?                ToolName     { get; init; }
    public string?                CallId       { get; init; }
    public string?                Capability   { get; init; }
    public string?                Summary      { get; init; }
    public IReadOnlyList<string>? TargetPaths  { get; init; }
    public string?                Decision     { get; init; }
    public string?                Outcome      { get; init; }

    /// <summary>
    ///     工具执行使用的策略身份（backend=host 或完整沙箱身份串）；非工具记录为空。
    ///     The execution policy identity used by a tool invocation (backend=host or
    ///     the full sandbox identity); empty for non-tool records.
    /// </summary>
    public string? ExecutionPolicy { get; init; }

    public string? Status          { get; init; }
    public bool?   Succeeded       { get; init; }
    public int?    ExitCode        { get; init; }
    public bool?   TimedOut        { get; init; }
    public bool?   OutputTruncated { get; init; }
    public int?    BeforeTokens    { get; init; }
    public int?    AfterTokens     { get; init; }
    public int?    InputTokens     { get; init; }
    public int?    OutputTokens    { get; init; }
    public string? FinishReason    { get; init; }
}

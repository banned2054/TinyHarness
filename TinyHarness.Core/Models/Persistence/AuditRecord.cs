namespace TinyHarness.Core.Models.Persistence;

public sealed record AuditRecord
{
    public string RunId { get; init; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Kind { get; init; } = string.Empty;
    public string? SystemPrompt { get; init; }
    public string? UserInput { get; init; }
    public string? ToolName { get; init; }
    public string? CallId { get; init; }
    public string? Capability { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<string>? TargetPaths { get; init; }
    public string? Decision { get; init; }
    public string? Outcome { get; init; }
    public string? Status { get; init; }
    public bool? Succeeded { get; init; }
    public int? ExitCode { get; init; }
    public bool? TimedOut { get; init; }
    public bool? OutputTruncated { get; init; }
    public int? BeforeTokens { get; init; }
    public int? AfterTokens { get; init; }
}

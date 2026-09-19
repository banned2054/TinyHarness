using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Context;

namespace TinyHarness.Core.Models.Persistence;

public sealed record SessionSnapshot
{
    public string RunId { get; init; } = string.Empty;
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; init; }
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];
    public StructuredState State { get; init; } = StructuredState.Empty;
    public AgentResult Result { get; init; } = new() { Status = AgentStatus.Failed };
}

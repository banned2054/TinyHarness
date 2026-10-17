using System.Text.Json;
using TinyHarness.Core.Models.Session;

namespace TinyHarness.Core.Models.Persistence;

/// <summary>
///     <para>单次 JSONL 追加提交，格式、会话、序号和批次身份均显式保存。</para>
///     <para>A single JSONL append commit with explicit format, session, sequence, and batch identity.</para>
/// </summary>
public sealed record SessionLogBatch
{
    public string                          Format     { get; init; } = string.Empty;
    public string                          SessionId  { get; init; } = string.Empty;
    public long                            Sequence   { get; init; }
    public string                          BatchId    { get; init; } = string.Empty;
    public DateTimeOffset                  WrittenUtc { get; init; }
    public IReadOnlyList<SessionLogRecord> Records    { get; init; } = [];
}

/// <summary>
///     <para>批次内的显式类型记录；ID 由 writer 按会话和序号分配。</para>
///     <para>An explicitly typed record within a batch; the writer assigns its session-scoped sequence ID.</para>
/// </summary>
public sealed record SessionLogRecord(string Id, string Kind, JsonElement Payload);

public sealed record SessionLogIdentity(
    string               Id,
    string               WorkspaceRoot,
    SessionModelIdentity Model,
    DateTimeOffset       CreatedUtc);

public sealed record SessionLogTurn(string Id, string UserInput, DateTimeOffset CreatedUtc);

public sealed record SessionLogRun(string TurnId, PersistedRun Run);

public sealed record SessionLogMessage(PersistedMessage Message);

public sealed record SessionLogCheckpoint(
    string? StateJson,
    bool    HasState,
    int     FoldStartMessageIndex,
    int     FoldEndMessageIndex);

public sealed record SessionLogSessionState(
    DateTimeOffset? InspectionAcknowledgedUtc,
    string?         PendingInspectionReason,
    bool            ExecutionIntent = false);

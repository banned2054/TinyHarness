namespace TinyHarness.Core.Models.Persistence;

/// <summary>
///     <para>绑定固定日志上界与最后一条历史位置的稳定分页游标。</para>
///     <para>A stable paging cursor bound to a fixed log ceiling and the last history position.</para>
/// </summary>
public sealed record SessionHistoryCursor(
    string  SessionId,
    long    UpperSequence,
    long    LastSequence,
    int     LastRecordIndex,
    string? Warning = null);

/// <summary>
///     <para>有界历史页及其可靠前缀、读取量和后续游标。</para>
///     <para>A bounded history page with its reliable prefix, read volume, and continuation cursor.</para>
/// </summary>
public sealed record SessionHistoryPage(
    IReadOnlyList<SessionHistoryItem> Items,
    SessionHistoryCursor?             NextCursor,
    long                              UpperSequence,
    long                              ReliablePrefixSequence,
    long                              BytesRead,
    string?                           Warning);

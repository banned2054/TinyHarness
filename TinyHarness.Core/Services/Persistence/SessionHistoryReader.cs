using System.Buffers;
using System.Text.Json;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Persistence;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>从追加式会话日志读取固定上界、内存和 UTF-8 预算内的历史页。</para>
///     <para>Reads history pages from an append-only session log within fixed sequence, memory, and UTF-8 budgets.</para>
/// </summary>
public sealed class SessionHistoryReader
{
    public const int MaxPageItems = 200;
    public const int MaxPageBytes = 8 * 1024 * 1024;

    /// <summary>
    ///     <para>读取稳定历史页；首屏捕获上界，续页只扫描到游标绑定的序号。</para>
    ///     <para>
    ///         Reads a stable history page; the first page captures its ceiling and later pages scan only through the
    ///         cursor-bound sequence.
    ///     </para>
    /// </summary>
    /// <param name="directory">
    ///     <para>日志目录。</para>
    ///     <para>The log directory.</para>
    /// </param>
    /// <param name="sessionId">
    ///     <para>32 位十六进制会话 ID。</para>
    ///     <para>The 32-character hexadecimal session ID.</para>
    /// </param>
    /// <param name="limit">
    ///     <para>页面条目数，范围为 1 至 200。</para>
    ///     <para>The page item count, from 1 through 200.</para>
    /// </param>
    /// <param name="cursor">
    ///     <para>上一页游标；为空时开始新读取范围。</para>
    ///     <para>The previous-page cursor, or null to begin a new read range.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消扫描。</para>
    ///     <para>Cancels scanning.</para>
    /// </param>
    /// <returns>
    ///     <para>条目、稳定游标、可靠前缀与读取量。</para>
    ///     <para>The items, stable cursor, reliable prefix, and read volume.</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <para>页面上限不在范围内时抛出。</para>
    ///     <para>Thrown when the page limit is outside its allowed range.</para>
    /// </exception>
    /// <exception cref="SessionStoreException">
    ///     <para>会话、日志或游标边界无效时抛出。</para>
    ///     <para>Thrown when the session, log, or cursor boundary is invalid.</para>
    /// </exception>
    public async Task<SessionHistoryPage> ReadPageAsync(string                directory, string sessionId, int limit,
                                                        SessionHistoryCursor? cursor,
                                                        CancellationToken     cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (limit is < 1 or > MaxPageItems)
            throw new ArgumentOutOfRangeException(nameof(limit), $"Page limit must be between 1 and {MaxPageItems}.");
        if (!SessionStorePaths.IsValidSessionId(sessionId))
            throw new SessionStoreException($"'{sessionId}' is not a valid session id.");
        sessionId = sessionId.ToLowerInvariant();

        var path = Path.Combine(directory, sessionId + SessionStoreWriter.LogFileSuffix);
        SessionStorePaths.EnsureStoreLeaf(directory, path);
        if (!File.Exists(path)) throw new SessionStoreException($"Session log '{path}' does not exist.");

        if (cursor is not null) ValidateCursor(cursor, sessionId);
        var upperSequence = cursor?.UpperSequence;
        var warning       = cursor?.Warning;

        var items                   = new List<SessionHistoryItem>(limit);
        var serializedItem          = new ArrayBufferWriter<byte>();
        var pageBytes               = 0;
        var pageSealed              = false;
        var exceededBudget          = false;
        var cursorPositionValidated = cursor is null;
        var lastSequence            = cursor?.LastSequence    ?? 0;
        var lastRecordIndex         = cursor?.LastRecordIndex ?? -1;

        var pageScan = await SessionLogScanner.ScanAsync(path, sessionId, null, upperSequence, cursor is null,
                                                         (batch, token) =>
                                                         {
                                                             token.ThrowIfCancellationRequested();
                                                             if (pageSealed) return ValueTask.CompletedTask;
                                                             for (var index = 0; index < batch.Records.Count; index++)
                                                             {
                                                                 var record = batch.Records[index];
                                                                 if (cursor is not null                    &&
                                                                     batch.Sequence == cursor.LastSequence &&
                                                                     index          == cursor.LastRecordIndex)
                                                                 {
                                                                     if (record.Kind != "history")
                                                                         throw new
                                                                             SessionStoreException("The history cursor position does not identify a history record.");
                                                                     cursorPositionValidated = true;
                                                                 }

                                                                 if (record.Kind != "history") continue;
                                                                 if (cursor is not null &&
                                                                     Compare(batch.Sequence, index, cursor.LastSequence,
                                                                             cursor.LastRecordIndex) <= 0)
                                                                     continue;
                                                                 if (items.Count == limit)
                                                                 {
                                                                     pageSealed = true;
                                                                     return ValueTask.CompletedTask;
                                                                 }

                                                                 var item =
                                                                     record.Payload
                                                                           .Deserialize(SessionHistoryJsonContext
                                                                                        .Default.SessionHistoryItem)
                                                                  ?? throw new
                                                                         SessionStoreException($"Session '{sessionId}' has an empty history item at sequence {batch.Sequence}.");
                                                                 serializedItem.Clear();
                                                                 using (var jsonWriter =
                                                                        new Utf8JsonWriter(serializedItem))
                                                                 {
                                                                     JsonSerializer.Serialize(jsonWriter, item,
                                                                              SessionHistoryJsonContext.Default
                                                                                 .SessionHistoryItem);
                                                                 }

                                                                 var size = serializedItem.WrittenCount;
                                                                 if (size > MaxPageBytes - pageBytes)
                                                                 {
                                                                     if (items.Count == 0)
                                                                         throw new
                                                                             SessionStoreException($"A single history item exceeds the {MaxPageBytes:N0}-byte page budget; it cannot be returned without omission.");
                                                                     pageSealed     = true;
                                                                     exceededBudget = true;
                                                                     return ValueTask.CompletedTask;
                                                                 }

                                                                 items.Add(item);
                                                                 pageBytes       += size;
                                                                 lastSequence    =  batch.Sequence;
                                                                 lastRecordIndex =  index;
                                                             }

                                                             return ValueTask.CompletedTask;
                                                         }, cancellationToken).ConfigureAwait(false);

        if (upperSequence is not null && pageScan.LastSequence != upperSequence)
            throw new
                SessionStoreException($"Session log '{path}' did not reach the cursor's fixed upper sequence {upperSequence}.");
        if (!cursorPositionValidated)
            throw new SessionStoreException("The history cursor position does not exist in its bound batch.");
        upperSequence =   pageScan.LastSequence;
        warning       ??= pageScan.Warning;

        var nextCursor = pageSealed && items.Count > 0
            ? new SessionHistoryCursor(sessionId, upperSequence.Value, lastSequence, lastRecordIndex, warning)
            : null;
        if (exceededBudget && warning is null)
            warning = $"Page stopped at the {MaxPageBytes:N0}-byte UTF-8 item budget; continue with NextCursor.";
        return new SessionHistoryPage(items, nextCursor, upperSequence.Value, pageScan.LastSequence, pageScan.BytesRead,
                                      warning);
    }

    /// <summary>
    ///     <para>拒绝绑定到其他会话、越界位置或无效序号的游标。</para>
    ///     <para>Rejects cursors bound to another session, out-of-range positions, or invalid sequences.</para>
    /// </summary>
    /// <param name="cursor">
    ///     <para>待验证游标。</para>
    ///     <para>The cursor to validate.</para>
    /// </param>
    /// <param name="sessionId">
    ///     <para>请求会话 ID。</para>
    ///     <para>The requested session ID.</para>
    /// </param>
    /// <exception cref="SessionStoreException">
    ///     <para>游标与请求范围不匹配时抛出。</para>
    ///     <para>Thrown when the cursor does not match the requested range.</para>
    /// </exception>
    private static void ValidateCursor(SessionHistoryCursor cursor, string sessionId)
    {
        if (!string.Equals(cursor.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) ||
            cursor.UpperSequence   < 1 || cursor.LastSequence < 1 || cursor.LastSequence > cursor.UpperSequence ||
            cursor.LastRecordIndex < 0)
            throw new SessionStoreException("The history cursor is invalid or belongs to a different session.");
    }

    /// <summary>
    ///     <para>比较批次序号与批内记录位置。</para>
    ///     <para>Compares a batch sequence and record index position.</para>
    /// </summary>
    /// <param name="leftSequence">
    ///     <para>左侧批次序号。</para>
    ///     <para>The left batch sequence.</para>
    /// </param>
    /// <param name="leftIndex">
    ///     <para>左侧批内位置。</para>
    ///     <para>The left record index.</para>
    /// </param>
    /// <param name="rightSequence">
    ///     <para>右侧批次序号。</para>
    ///     <para>The right batch sequence.</para>
    /// </param>
    /// <param name="rightIndex">
    ///     <para>右侧批内位置。</para>
    ///     <para>The right record index.</para>
    /// </param>
    /// <returns>
    ///     <para>字典序位置比较结果。</para>
    ///     <para>The lexicographic position comparison.</para>
    /// </returns>
    private static int Compare(long leftSequence, int leftIndex, long rightSequence, int rightIndex)
    {
        var sequenceComparison = leftSequence.CompareTo(rightSequence);
        return sequenceComparison != 0 ? sequenceComparison : leftIndex.CompareTo(rightIndex);
    }
}

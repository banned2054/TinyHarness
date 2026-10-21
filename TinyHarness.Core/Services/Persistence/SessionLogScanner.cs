using System.Buffers;
using System.Text.Json;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>按固定字节范围顺序扫描并校验 session-log JSONL，不保留全日志投影。</para>
///     <para>
///         Scans and validates session-log JSONL sequentially within a fixed byte range without retaining a full
///         projection.
///     </para>
/// </summary>
internal static class SessionLogScanner
{
    internal const int MaxBatchBytes = SessionStoreWriter.MaxSnapshotBytes;
    internal const int MaxJsonDepth  = SessionStoreReader.MaxJsonDepth;
    internal const int BufferSize    = 64 * 1024;

    private static readonly string[] HistoryStringProperties =
    [
        "runId", "text", "callId", "toolName", "argumentsJson", "capability", "summary", "executionPolicy", "approval",
        "preparationError", "notStartedReason", "state", "stdout", "stderr", "outputWarning"
    ];

    private static readonly string[] HistoryBooleanProperties = ["timedOut", "outputTruncated", "outputIncomplete"];

    /// <summary>
    ///     <para>扫描指定上界；仅显式允许时把末尾半行作为可靠前缀警告返回。</para>
    ///     <para>
    ///         Scans to the supplied ceiling; a trailing partial line is returned as a reliable-prefix warning only when
    ///         explicitly allowed.
    ///     </para>
    /// </summary>
    /// <param name="path">
    ///     <para>日志文件路径。</para>
    ///     <para>The log file path.</para>
    /// </param>
    /// <param name="sessionId">
    ///     <para>预期的会话 ID。</para>
    ///     <para>The expected session ID.</para>
    /// </param>
    /// <param name="byteCeiling">
    ///     <para>固定的最大读取字节数；为空时在打开时捕获文件长度。</para>
    ///     <para>The fixed maximum byte count; null captures file length when opened.</para>
    /// </param>
    /// <param name="upperSequence">
    ///     <para>可选的最后批次序号；到达后立即停止，不解析后续追加内容。</para>
    ///     <para>The optional final batch sequence; scanning stops there without parsing later appends.</para>
    /// </param>
    /// <param name="allowIncompleteTail">
    ///     <para>是否将尾部半行报告为有效前缀警告。</para>
    ///     <para>Whether to report a trailing partial line as a valid-prefix warning.</para>
    /// </param>
    /// <param name="onBatch">
    ///     <para>每个已校验批次的异步接收器；批次与 payload 仅在回调完成前有效。</para>
    ///     <para>
    ///         The asynchronous receiver for each validated batch; batches and payloads are valid only until the callback
    ///         completes.
    ///     </para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消扫描。</para>
    ///     <para>Cancels the scan.</para>
    /// </param>
    /// <returns>
    ///     <para>扫描边界、有效序号、读取字节数及可能的尾部警告。</para>
    ///     <para>The scan ceiling, valid sequence, bytes read, and optional tail warning.</para>
    /// </returns>
    /// <exception cref="SessionStoreException">
    ///     <para>日志格式、身份、顺序、记录或资源边界无效时抛出。</para>
    ///     <para>Thrown when the log format, identity, ordering, record, or resource boundary is invalid.</para>
    /// </exception>
    internal static async Task<SessionLogScanResult> ScanAsync(
        string                                              path,
        string                                              sessionId,
        long?                                               byteCeiling,
        long?                                               upperSequence,
        bool                                                allowIncompleteTail,
        Func<SessionLogBatch, CancellationToken, ValueTask> onBatch,
        CancellationToken                                   cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(onBatch);
        if (!SessionStorePaths.IsValidSessionId(sessionId))
            throw new SessionStoreException($"'{sessionId}' is not a valid session id.");
        if (byteCeiling is < 0 or > SessionStoreWriter.MaxLogBytes)
            throw new
                SessionStoreException($"Session log ceiling must be between 0 and {SessionStoreWriter.MaxLogBytes:N0} bytes.");
        if (upperSequence is < 1)
            throw new SessionStoreException("A session log upper sequence must be positive.");

        sessionId = sessionId.ToLowerInvariant();
        var fullPath  = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        SessionStorePaths.EnsureStoreLeaf(directory, fullPath);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        SessionStorePaths.EnsureStoreLeaf(directory, fullPath);
        var capturedLength = byteCeiling ?? stream.Length;
        if (stream.Length < capturedLength)
            throw new SessionStoreException($"Session log '{path}' was shortened below its captured read ceiling.");
        if (capturedLength > SessionStoreWriter.MaxLogBytes)
            throw new
                SessionStoreException($"Session log '{path}' exceeds the {SessionStoreWriter.MaxLogBytes:N0}-byte limit.");

        var  expectedSequence = 1L;
        long bytesFetched     = 0;
        long bytesScanned     = 0;
        var  line             = ArrayPool<byte>.Shared.Rent(4 * 1024);
        var  lineLength       = 0;
        var  buffer           = new byte[BufferSize];
        try
        {
            while (bytesScanned < capturedLength && (upperSequence is null || expectedSequence <= upperSequence))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = (int)Math.Min(buffer.Length, capturedLength - bytesScanned);
                var count = await stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken)
                                        .ConfigureAwait(false);
                if (count == 0)
                    throw new
                        SessionStoreException($"Session log '{path}' was shortened while its captured read range was being scanned.");
                bytesFetched += count;

                for (var i = 0; i < count; i++)
                {
                    bytesScanned++;
                    if (buffer[i] != (byte)'\n')
                    {
                        if (lineLength >= MaxBatchBytes)
                            throw new
                                SessionStoreException($"Session log '{path}' contains a line exceeding the batch limit.");
                        EnsureLineCapacity(ref line, lineLength + 1);
                        line[lineLength++] = buffer[i];
                        continue;
                    }

                    if (lineLength == 0)
                        throw new SessionStoreException($"Session log '{path}' contains an empty line.");
                    if (lineLength + 1 > MaxBatchBytes)
                        throw new
                            SessionStoreException($"Session log '{path}' contains a line exceeding the batch limit.");
                    var jsonLength = lineLength;
                    if (line[jsonLength - 1] == (byte)'\r') jsonLength--;

                    try
                    {
                        using var document = JsonDocument.Parse(line.AsMemory(0, jsonLength),
                                                                new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
                        var batch = ReadBatch(document.RootElement);
                        ValidateBatch(batch, sessionId, expectedSequence, path);
                        lineLength = 0;
                        await onBatch(batch, cancellationToken).ConfigureAwait(false);
                        expectedSequence++;
                        if (upperSequence == batch.Sequence)
                            return new SessionLogScanResult(capturedLength, bytesFetched, batch.Sequence, null);
                    }
                    catch (JsonException ex)
                    {
                        throw new
                            SessionStoreException($"Session log '{path}' has a damaged batch at sequence {expectedSequence}: {ex.Message}",
                                                  ex);
                    }
                }
            }

            if (expectedSequence <= upperSequence)
                throw new
                    SessionStoreException($"Session log '{path}' ends before requested sequence {upperSequence}; valid prefix ends at sequence {expectedSequence - 1}.");
            if (expectedSequence == 1)
                throw new
                    SessionStoreException($"Session log '{path}' contains no committed history batch; reliable prefix ends at sequence 0.");
            if (lineLength == 0)
                return new SessionLogScanResult(capturedLength, bytesFetched, expectedSequence - 1, null);
            var warning =
                $"Session log ends with an incomplete line; reliable prefix ends at sequence {expectedSequence - 1}.";
            if (!allowIncompleteTail)
                throw new
                    SessionStoreException($"Session log '{path}' ends with an incomplete line; valid prefix ends at sequence {expectedSequence - 1}.");
            return new SessionLogScanResult(capturedLength, bytesFetched, expectedSequence - 1, warning);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(line);
        }
    }

    private static SessionLogBatch ReadBatch(JsonElement root)
    {
        RequireObject(root, "batch");
        var format          = RequiredString(root, "format");
        var sessionId       = RequiredString(root, "sessionId");
        var sequenceElement = Required(root, "sequence");
        if (!sequenceElement.TryGetInt64(out var sequence))
            throw new JsonException("batch sequence must be an integer");
        var batchId        = RequiredString(root, "batchId");
        var writtenUtc     = Required(root, "writtenUtc").GetDateTimeOffset();
        var recordsElement = Required(root, "records");
        if (recordsElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("batch records must be an array");

        var records = new List<SessionLogRecord>(recordsElement.GetArrayLength());
        foreach (var recordElement in recordsElement.EnumerateArray())
        {
            RequireObject(recordElement, "record");
            records.Add(new SessionLogRecord(RequiredString(recordElement, "id"),
                                             RequiredString(recordElement, "kind"),
                                             Required(recordElement, "payload")));
        }

        return new SessionLogBatch
        {
            Format     = format,
            SessionId  = sessionId,
            Sequence   = sequence,
            BatchId    = batchId,
            WrittenUtc = writtenUtc,
            Records    = records
        };
    }

    private static void EnsureLineCapacity(ref byte[] line, int required)
    {
        if (required <= line.Length) return;
        var nextSize    = Math.Min(MaxBatchBytes, Math.Max(required, line.Length * 2));
        var replacement = ArrayPool<byte>.Shared.Rent(nextSize);
        line.AsSpan(0, Math.Min(line.Length, required)).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(line);
        line = replacement;
    }

    /// <summary>
    ///     <para>校验批次身份、严格连续序号、确定性 ID 与所有已知必需记录载荷。</para>
    ///     <para>Validates batch identity, contiguous sequence, deterministic IDs, and every known required record payload.</para>
    /// </summary>
    /// <param name="batch">
    ///     <para>待校验批次。</para>
    ///     <para>The batch to validate.</para>
    /// </param>
    /// <param name="sessionId">
    ///     <para>预期会话 ID。</para>
    ///     <para>The expected session ID.</para>
    /// </param>
    /// <param name="sequence">
    ///     <para>预期批次序号。</para>
    ///     <para>The expected batch sequence.</para>
    /// </param>
    /// <param name="path">
    ///     <para>日志文件路径。</para>
    ///     <para>The log file path.</para>
    /// </param>
    /// <exception cref="SessionStoreException">
    ///     <para>发现格式、ID、kind 或载荷错误时抛出。</para>
    ///     <para>Thrown when format, IDs, kind, or payload is invalid.</para>
    /// </exception>
    private static void ValidateBatch(SessionLogBatch batch, string sessionId, long sequence, string path)
    {
        if (batch.Format != SessionLogFormat.FormatName)
            throw new SessionStoreException($"Session log '{path}' uses unknown format '{batch.Format}'.");
        if (batch.SessionId != sessionId ||
            batch.Sequence != sequence ||
            batch.BatchId != $"{sessionId}:{sequence:D20}")
            throw new
                SessionStoreException($"Session log '{path}' has a duplicate, mismatched, or out-of-order batch at sequence {sequence}.");
        if (batch.Records is null || batch.Records.Count == 0)
            throw new
                SessionStoreException($"Session log '{path}' contains an empty record batch at sequence {sequence}.");
        switch (sequence)
        {
            case 1 when (batch.Records[0]?.Kind != "identity") :
                throw new SessionStoreException($"Session log '{path}' must begin with an identity record.");
            case > 1 when batch.Records.Any(record => record?.Kind == "identity") :
                throw new SessionStoreException($"Session log '{path}' repeats its identity record.");
        }

        for (var i = 0; i < batch.Records.Count; i++)
        {
            var record = batch.Records[i];
            if (record is null ||
                record.Id != $"{sessionId}:{sequence:D20}:{i:D4}" ||
                string.IsNullOrWhiteSpace(record.Kind) ||
                record.Payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new
                    SessionStoreException($"Session log '{path}' has a duplicate or invalid record id in sequence {sequence}.");
            ValidatePayload(record, batch, path, i);
        }
    }

    /// <summary>
    ///     <para>使用 source-generated metadata 校验一种已知记录，且不投影或改写 canonical 消息。</para>
    ///     <para>Validates one known record with source-generated metadata without projecting or rewriting canonical messages.</para>
    /// </summary>
    /// <param name="record">
    ///     <para>待校验记录。</para>
    ///     <para>The record to validate.</para>
    /// </param>
    /// <param name="batch">
    ///     <para>所属批次。</para>
    ///     <para>The containing batch.</para>
    /// </param>
    /// <param name="path">
    ///     <para>日志路径。</para>
    ///     <para>The log path.</para>
    /// </param>
    /// <param name="index">
    ///     <para>批内位置。</para>
    ///     <para>The index within the batch.</para>
    /// </param>
    /// <exception cref="SessionStoreException">
    ///     <para>必需 kind 未知或 payload 类型无效时抛出。</para>
    ///     <para>Thrown when a required kind is unknown or its payload type is invalid.</para>
    /// </exception>
    private static void ValidatePayload(SessionLogRecord record, SessionLogBatch batch, string path, int index)
    {
        try
        {
            switch (record.Kind)
            {
                case "identity" :
                    RequireObject(record.Payload, "identity payload");
                    var model = Required(record.Payload, "model");
                    RequireObject(model, "model identity");
                    RequireStringProperty(model, "modelId");
                    RequireStringProperty(model, "endpoint");
                    RequireStringProperty(model, "chatApi");
                    Required(record.Payload, "createdUtc").GetDateTimeOffset();
                    if (RequiredString(record.Payload, "id") != batch.SessionId)
                        throw new SessionStoreException($"Session log '{path}' has a mismatched identity record.");
                    RequireStringProperty(record.Payload, "workspaceRoot");
                    break;
                case "turn" :
                    RequireObject(record.Payload, "turn payload");
                    RequireStringProperty(record.Payload, "id");
                    RequireStringProperty(record.Payload, "userInput");
                    Required(record.Payload, "createdUtc").GetDateTimeOffset();
                    break;
                case "run" :
                    RequireObject(record.Payload, "run payload");
                    RequireStringProperty(record.Payload, "turnId");
                    RequireObject(Required(record.Payload, "run"), "run");
                    var persistedRun = record.Payload.Deserialize(SessionLogJsonContext.Default.SessionLogRun);
                    persistedRun?.Run.Budget?.Validate(persistedRun.Run.ToolExecutions);
                    break;
                case "message" :
                    RequireObject(record.Payload, "message payload");
                    _ = Required(record.Payload, "message");
                    break;
                case "project-instructions" :
                    RequireObject(record.Payload, "project-instructions payload");
                    var instruction = record.Payload.Deserialize(SessionLogJsonContext.Default.ProjectInstructionRecord)
                                   ?? throw new
                                          SessionStoreException("A project-instructions record must contain a typed payload.");
                    if (instruction.Schema != "tinyharness.project-instructions/1" ||
                        instruction.Action is not ("rules-applied" or "scopes-tracked") ||
                        string.IsNullOrWhiteSpace(instruction.Revision) ||
                        string.IsNullOrWhiteSpace(instruction.TurnId) ||
                        string.IsNullOrWhiteSpace(instruction.RunId) ||
                        string.IsNullOrWhiteSpace(instruction.Reason) ||
                        instruction.Snapshot is null ||
                        instruction.CanonicalMessageIndex is < 0)
                        throw new
                            SessionStoreException($"Session log '{path}' contains an invalid project-instructions record.");
                    break;
                case "run-amendment" :
                case "work-item" :
                    SessionStoreWriter.ValidateControlRecord(record with { Id = string.Empty });
                    break;
                case "checkpoint" :
                    throw new
                        SessionStoreException("Legacy structured-state compaction checkpoints are unsupported; original data was not changed.");
                case "compaction-checkpoint" :
                    RequireObject(record.Payload, "checkpoint payload");
                    RequireBoolean(record.Payload, "hasState");
                    RequireInteger(record.Payload, "foldStartMessageIndex");
                    RequireInteger(record.Payload, "foldEndMessageIndex");
                    var checkpoint = record.Payload.Deserialize(SessionLogJsonContext.Default.SessionLogCheckpoint)
                                  ?? throw new JsonException("missing compaction checkpoint");
                    if (checkpoint.HasState &&
                        (checkpoint.State is null ||
                         RequiredString(Required(record.Payload, "state"), "schema") !=
                         CompactionState.CurrentSchema ||
                         string.IsNullOrWhiteSpace(checkpoint.State.Summary) ||
                         checkpoint.State.RetainedUserMessages is null))
                        throw new SessionStoreException("Unsupported or invalid compaction checkpoint schema.");
                    break;
                case "session-state" :
                    RequireObject(record.Payload, "session-state payload");
                    RequireNullableDateTimeOffset(record.Payload, "inspectionAcknowledgedUtc");
                    RequireNullableString(record.Payload, "pendingInspectionReason");
                    RequireOptionalNonNullableBoolean(record.Payload, "executionIntent");
                    break;
                case "audit" :
                    RequireObject(record.Payload, "audit payload");
                    RequireStringProperty(record.Payload, "runId");
                    Required(record.Payload, "timestampUtc").GetDateTimeOffset();
                    RequireStringProperty(record.Payload, "kind");
                    break;
                case "history" :
                    ValidateHistoryPayload(record.Payload);
                    if (RequiredString(record.Payload, "sessionId") != batch.SessionId ||
                        RequiredString(record.Payload, "id") != record.Id ||
                        RequiredInt64(record.Payload, "sequence") != batch.Sequence ||
                        string.IsNullOrWhiteSpace(RequiredString(record.Payload, "kind")))
                        throw new
                            SessionStoreException($"Session log '{path}' contains an unbound history item at sequence {batch.Sequence}, record {index}.");
                    break;
                default :
                    throw new
                        SessionStoreException($"Session log '{path}' contains unknown required record kind '{record.Kind}'.");
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new
                SessionStoreException($"Session log '{path}' contains an invalid '{record.Kind}' payload at sequence {batch.Sequence}, record {index}: {ex.Message}",
                                      ex);
        }
    }

    private static void ValidateHistoryPayload(JsonElement payload)
    {
        RequireObject(payload, "history payload");
        RequireStringProperty(payload, "id");
        RequireStringProperty(payload, "sessionId");
        RequireStringProperty(payload, "turnId");
        RequireStringProperty(payload, "kind");
        RequireInt64(payload, "sequence");

        foreach (var name in HistoryStringProperties)
            RequireNullableString(payload, name);

        RequireOptionalNullableBoolean(payload, "succeeded");
        foreach (var name in HistoryBooleanProperties)
            RequireOptionalNonNullableBoolean(payload, name);
        RequireOptionalInteger(payload, "exitCode");
        ValidateStringArray(payload, "targetPaths");
        ValidatePatchChanges(payload);
    }

    private static void ValidatePatchChanges(JsonElement payload)
    {
        if (!payload.TryGetProperty("patchChanges", out var changes) || changes.ValueKind == JsonValueKind.Null) return;
        if (changes.ValueKind != JsonValueKind.Array) throw new JsonException("history patchChanges must be an array");
        foreach (var change in changes.EnumerateArray())
        {
            RequireObject(change, "patch change");
            RequireStringProperty(change, "path");
            RequireStringProperty(change, "operation");
            RequireStringProperty(change, "diff");
            RequireStringProperty(change, "state");
        }
    }

    private static void ValidateStringArray(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var array) || array.ValueKind == JsonValueKind.Null) return;
        if (array.ValueKind != JsonValueKind.Array) throw new JsonException($"'{name}' must be an array");
        foreach (var item in array.EnumerateArray())
            if (item.ValueKind != JsonValueKind.String)
                throw new JsonException($"'{name}' values must be strings");
    }

    private static JsonElement Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value : throw new JsonException($"missing '{name}'");

    private static string RequiredString(JsonElement element, string name)
    {
        var value = Required(element, name);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? throw new JsonException($"'{name}' must not be null")
            : throw new JsonException($"'{name}' must be a string");
    }

    private static void RequireObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException($"'{name}' must be an object");
    }

    private static void RequireStringProperty(JsonElement element, string name) => _ = RequiredString(element, name);
    private static long RequiredInt64(JsonElement         element, string name) => RequireInt64(element, name);

    private static long RequireInt64(JsonElement element, string name)
    {
        var value = Required(element, name);
        return value.TryGetInt64(out var parsed) ? parsed : throw new JsonException($"'{name}' must be an integer");
    }

    private static void RequireInteger(JsonElement element, string name) => _ = RequireInt64(element, name);

    private static void RequireBoolean(JsonElement element, string name)
    {
        if (Required(element, name).ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new JsonException($"'{name}' must be a boolean");
    }

    private static void RequireOptionalNonNullableBoolean(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) &&
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new JsonException($"'{name}' must be a boolean");
    }

    private static void RequireOptionalNullableBoolean(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) &&
            value.ValueKind is not (JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False))
            throw new JsonException($"'{name}' must be a nullable boolean");
    }

    private static void RequireOptionalInteger(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) &&
            value.ValueKind != JsonValueKind.Null &&
            !value.TryGetInt32(out _))
            throw new JsonException($"'{name}' must be a nullable integer");
    }

    private static void RequireNullableString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) &&
            value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            throw new JsonException($"'{name}' must be a nullable string");
    }

    private static void RequireNullableDateTimeOffset(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.String || !value.TryGetDateTimeOffset(out _))
            throw new JsonException($"'{name}' must be a nullable date-time");
    }
}

/// <summary>
///     <para>单次扫描的固定范围与可靠前缀元数据。</para>
///     <para>Fixed-range and reliable-prefix metadata for one scan.</para>
/// </summary>
/// <param name="CapturedLength">
///     <para>扫描开始时固定的文件字节上界。</para>
///     <para>The file byte ceiling fixed at scan start.</para>
/// </param>
/// <param name="BytesRead">
///     <para>本次扫描实际消费的字节数。</para>
///     <para>The number of bytes consumed by this scan.</para>
/// </param>
/// <param name="LastSequence">
///     <para>最后一个完整有效批次序号。</para>
///     <para>The last complete valid batch sequence.</para>
/// </param>
/// <param name="Warning">
///     <para>尾部半行警告；完整严格扫描时为空。</para>
///     <para>The trailing partial-line warning, or null for a complete strict scan.</para>
/// </param>
internal sealed record SessionLogScanResult(long CapturedLength, long BytesRead, long LastSequence, string? Warning);

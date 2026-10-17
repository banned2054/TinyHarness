using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Context;

namespace TinyHarness.Core.Services.Persistence;

public static class SessionLogFormat
{
    public const string FormatName = "tinyharness.session-log/1";
}

internal static class SessionLogWriter
{
    public const string FormatName = SessionLogFormat.FormatName;
}

/// <summary>
///     <para>将会话提交为带排他锁的追加式 JSONL 批次，并在不确定写入后停止追加。</para>
///     <para>Commits sessions as append-only JSONL batches under an exclusive lock and stops after uncertain writes.</para>
/// </summary>
public sealed class SessionStoreWriter : ISessionStoreWriter
{
    public const int  MaxSnapshotBytes = 8    * 1024 * 1024;
    public const long MaxLogBytes      = 256L * 1024 * 1024;

    public const     string                               LogFileSuffix = ".session.jsonl";
    private readonly SemaphoreSlim                        _gate         = new(1, 1);
    private readonly IReadOnlyDictionary<string, string>? _knownSecrets;

    private readonly FileStream     _lockStream;
    private readonly FileStream     _logStream;
    private readonly SecretRedactor _redactor;

    private string?                 _checkpointFingerprint;
    private string?                 _lastRunId;
    private string?                 _lastRunJson;
    private SessionLogSessionState? _lastSessionState;
    private long                    _persistedMessageCount;
    private int                     _persistedTurnCount;
    private bool                    _poisoned;
    private string?                 _trackedTurnId;
    private int                     _trackedTurnRunCount;

    public SessionStoreWriter(
        string directory, string sessionId, IReadOnlyDictionary<string, string>? knownSecrets = null)
        : this(directory, sessionId, true, knownSecrets)
    {
    }

    private SessionStoreWriter(
        string directory, string sessionId, bool requireNew, IReadOnlyDictionary<string, string>? knownSecrets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!SessionStorePaths.IsValidSessionId(sessionId))
            throw new ArgumentException("A session id must contain exactly 32 hexadecimal characters.",
                                        nameof(sessionId));

        Directory.CreateDirectory(directory);
        SessionId     = sessionId.ToLowerInvariant();
        SnapshotPath  = Path.Combine(directory, SessionId + LogFileSuffix);
        _knownSecrets = knownSecrets?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        _redactor     = new SecretRedactor(_knownSecrets);

        var lockPath = Path.Combine(directory, $"{SessionId}.lock");
        SessionStorePaths.EnsureStoreLeaf(directory, lockPath);
        SessionStorePaths.EnsureStoreLeaf(directory, SnapshotPath);
        try
        {
            _lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            SessionStorePaths.EnsureStoreLeaf(directory, lockPath);
        }
        catch (IOException ex)
        {
            throw new SessionStoreException($"Session '{SessionId}' is already open by another writer.", ex);
        }

        if (requireNew && File.Exists(SnapshotPath))
        {
            _lockStream.Dispose();
            throw new
                SessionStoreException($"Session '{SessionId}' already has stored data; new sessions require an unused id.");
        }

        _lockStream.SetLength(0);
        _lockStream.Write(Encoding.UTF8.GetBytes($"pid={Environment.ProcessId} openedUtc={DateTimeOffset.UtcNow:O}"));
        _lockStream.Flush(true);
        FileStream? openedLog = null;
        try
        {
            openedLog = new FileStream(SnapshotPath, requireNew ? FileMode.CreateNew : FileMode.Open,
                                       FileAccess.ReadWrite, FileShare.ReadWrite, 64 * 1024,
                                       FileOptions.Asynchronous | FileOptions.WriteThrough);
            SessionStorePaths.EnsureStoreLeaf(directory, SnapshotPath);
            if (openedLog.Length > MaxLogBytes)
                throw new SessionStoreException($"Session log exceeds the {MaxLogBytes:N0}-byte limit.");
            openedLog.Position = openedLog.Length;
            _logStream         = openedLog;
        }
        catch
        {
            openedLog?.Dispose();
            _lockStream.Dispose();
            throw;
        }
    }

    public string            SessionId      { get; }
    public PersistedSession? LoadedSnapshot { get; private set; }

    internal Action<SessionLogBatch, string>? InjectWriteFault { get; init; }

    /// <summary>
    ///     <para>权威 JSONL 会话日志路径。</para>
    ///     <para>The authoritative JSONL session-log path.</para>
    /// </summary>
    public string SnapshotPath { get; }

    public long    CommitSequence { get; private set; }
    public bool    IsHealthy      => !_poisoned;
    public string? WriteFailure   { get; private set; }

    public IRunRecorder CreateRunRecorder(string                        sessionId, string turnId, string runId,
                                          Func<CancellationToken, Task> commitInitialContext,
                                          Func<SessionHistoryItem, CancellationToken, ValueTask>?
                                              historyItemCommitted) =>
        new SessionLogRunRecorder(this, sessionId, turnId, runId, commitInitialContext, _knownSecrets,
                                  historyItemCommitted);

    public Task CommitAsync(ChatSession session, ConversationContext context, CancellationToken cancellationToken) =>
        CommitCoreAsync(session, context, false, cancellationToken);

    public Task CommitExecutionIntentAsync(
        ChatSession session, ConversationContext context, CancellationToken cancellationToken) =>
        CommitCoreAsync(session, context, true, cancellationToken);

    public async Task<IReadOnlyList<SessionLogRecord>> AppendRecordsAsync(
        IReadOnlyList<SessionLogRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
            throw new ArgumentException("At least one journal record is required.", nameof(records));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_poisoned) throw PoisonedException();
            if (CommitSequence == 0)
                throw new
                    SessionStoreException("The session identity batch must be committed before appending run records.");

            var sequence   = CommitSequence + 1;
            var normalized = new SessionLogRecord[records.Count];
            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i] ?? throw new SessionStoreException("The record batch contains a null record.");
                if (!string.IsNullOrEmpty(record.Id))
                    throw new
                        SessionStoreException("Callers must leave journal record IDs empty; the writer assigns them.");
                if (string.IsNullOrWhiteSpace(record.Kind) ||
                    record.Payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    throw new SessionStoreException("A journal record must have a kind and a non-null typed payload.");
                var recordId = RecordId(SessionId, sequence, i);
                if (record.Kind is "audit" or "history")
                    try
                    {
                        if (record.Kind == "history")
                        {
                            var item = record.Payload.Deserialize(SessionHistoryJsonContext.Default.SessionHistoryItem)
                                    ?? throw new
                                           SessionStoreException("A history record must contain a typed history item.");
                            if (!string.Equals(item.SessionId, SessionId, StringComparison.Ordinal))
                                throw new
                                    SessionStoreException($"History item session '{item.SessionId}' does not match '{SessionId}'.");
                            var boundItem = item with { Id = recordId, Sequence = sequence };
                            normalized[i] = record with
                            {
                                Id = recordId,
                                Payload = JsonSerializer.SerializeToElement(boundItem,
                                                                            SessionHistoryJsonContext.Default
                                                                               .SessionHistoryItem)
                            };
                        }
                        else
                        {
                            var audit = record.Payload.Deserialize(PersistenceJsonContext.Default.AuditRecord)
                                     ?? throw new
                                            SessionStoreException("An audit record must contain a typed payload.");
                            if (string.IsNullOrWhiteSpace(audit.Kind) || string.IsNullOrWhiteSpace(audit.RunId))
                                throw new SessionStoreException("An audit record must identify its run and fact kind.");
                            normalized[i] = record with { Id = recordId };
                        }
                    }
                    catch (JsonException ex)
                    {
                        throw new
                            SessionStoreException($"Journal record '{record.Kind}' has an invalid typed payload: {ex.Message}",
                                                  ex);
                    }
                else
                    throw new SessionStoreException($"Unknown required journal record kind '{record.Kind}'.");
            }

            var batch = new SessionLogBatch
            {
                Format     = SessionLogWriter.FormatName,
                SessionId  = SessionId,
                Sequence   = sequence,
                BatchId    = BatchId(SessionId, sequence),
                WrittenUtc = DateTimeOffset.UtcNow,
                Records    = normalized
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(batch, SessionLogJsonContext.Default.SessionLogBatch);
            if (bytes.Length + 1 > MaxSnapshotBytes)
                throw new
                    SessionStoreException($"Session batch exceeds {MaxSnapshotBytes:N0} bytes; no data was appended.");
            if (_logStream.Length + bytes.Length + 1 > MaxLogBytes)
                throw new
                    SessionStoreException($"Session log would exceed {MaxLogBytes:N0} bytes; no data was appended.");

            await WriteBatchAsync(batch, bytes, cancellationToken).ConfigureAwait(false);

            CommitSequence = sequence;
            return normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _logStream.Dispose();
        _lockStream.Dispose();
        _gate.Dispose();
    }

    private async Task CommitCoreAsync(
        ChatSession session, ConversationContext context, bool executionIntent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(session.Id, SessionId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The writer and session ids differ.", nameof(session));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_poisoned) throw PoisonedException();
            if (context.Messages.Count < _persistedMessageCount)
                throw new
                    SessionStoreException("The in-memory conversation rewound behind the committed journal prefix.");

            var nextSequence = CommitSequence + 1;
            var records      = new List<SessionLogRecord>();

            void Add<T>(string kind, T payload) =>
                records.Add(new SessionLogRecord(RecordId(SessionId, nextSequence, records.Count), kind,
                                                 JsonSerializer.SerializeToElement(payload,
                                                          (JsonTypeInfo<T>)SessionLogJsonContext.Default
                                                             .GetTypeInfo(typeof(T))!)));

            var persistedTurnCount    = _persistedTurnCount;
            var trackedTurnId         = _trackedTurnId;
            var trackedTurnRunCount   = _trackedTurnRunCount;
            var lastRunId             = _lastRunId;
            var lastRunJson           = _lastRunJson;
            var checkpointFingerprint = _checkpointFingerprint;
            var lastSessionState      = _lastSessionState;

            if (CommitSequence == 0)
                Add("identity",
                    new SessionLogIdentity(SessionId, session.WorkspaceRoot, session.Model, session.CreatedUtc));

            var turns = session.Turns;
            for (var i = persistedTurnCount; i < turns.Count; i++)
            {
                var turn = turns[i];
                Add("turn", new SessionLogTurn(turn.Id, _redactor.Redact(turn.UserInput), turn.CreatedUtc));
                foreach (var run in turn.Runs)
                {
                    var persistedRun = PersistRun(run);
                    Add("run", new SessionLogRun(turn.Id, persistedRun));
                    lastRunId   = run.Id;
                    lastRunJson = JsonSerializer.Serialize(persistedRun, SessionLogJsonContext.Default.PersistedRun);
                }
            }

            if (turns.Count > persistedTurnCount)
            {
                persistedTurnCount  = turns.Count;
                trackedTurnId       = turns[^1].Id;
                trackedTurnRunCount = turns[^1].Runs.Count;
            }

            if (turns.Count > 0)
            {
                var lastTurn = turns[^1];
                if (trackedTurnId == lastTurn.Id)
                {
                    for (var i = trackedTurnRunCount; i < lastTurn.Runs.Count; i++)
                    {
                        var newRun       = lastTurn.Runs[i];
                        var persistedRun = PersistRun(newRun);
                        Add("run", new SessionLogRun(lastTurn.Id, persistedRun));
                        lastRunId = newRun.Id;
                        lastRunJson =
                            JsonSerializer.Serialize(persistedRun, SessionLogJsonContext.Default.PersistedRun);
                    }

                    trackedTurnRunCount = lastTurn.Runs.Count;

                    if (lastTurn.Runs.LastOrDefault() is { } activeRun && activeRun.Id == lastRunId)
                    {
                        var persistedRun = PersistRun(activeRun);
                        var runJson =
                            JsonSerializer.Serialize(persistedRun, SessionLogJsonContext.Default.PersistedRun);
                        if (!string.Equals(runJson, lastRunJson, StringComparison.Ordinal))
                        {
                            Add("run", new SessionLogRun(lastTurn.Id, persistedRun));
                            lastRunJson = runJson;
                        }
                    }
                }
                else
                {
                    throw new
                        SessionStoreException("The session turn sequence changed behind the committed journal cursor.");
                }
            }

            for (var i = (int)_persistedMessageCount; i < context.Messages.Count; i++)
                Add("message", new SessionLogMessage(PersistMessage(context.Messages[i])));

            var position = context.Position;
            var nextCheckpointFingerprint =
                $"{position.HasState}:{position.FoldStartMessageIndex}:{position.FoldEndMessageIndex}:" +
                (position.HasState ? context.State.ToJson() : string.Empty);
            if (!string.Equals(nextCheckpointFingerprint, checkpointFingerprint, StringComparison.Ordinal))
            {
                if (position.HasState || checkpointFingerprint is not null)
                    Add("checkpoint", new SessionLogCheckpoint(position.HasState ? context.State.ToJson() : null,
                                                               position.HasState, position.FoldStartMessageIndex,
                                                               position.FoldEndMessageIndex));
                checkpointFingerprint = nextCheckpointFingerprint;
            }

            var state = new SessionLogSessionState(session.InspectionAcknowledgedUtc,
                                                   _redactor.RedactNullable(session.PendingInspectionReason),
                                                   executionIntent);
            if (lastSessionState != state)
            {
                Add("session-state", state);
                lastSessionState = state;
            }

            if (records.Count == 0) return;

            var batch = new SessionLogBatch
            {
                Format     = SessionLogWriter.FormatName,
                SessionId  = SessionId,
                Sequence   = nextSequence,
                BatchId    = BatchId(SessionId, nextSequence),
                WrittenUtc = DateTimeOffset.UtcNow,
                Records    = records
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(batch, SessionLogJsonContext.Default.SessionLogBatch);
            if (bytes.Length + 1 > MaxSnapshotBytes)
                throw new
                    SessionStoreException($"Session batch exceeds {MaxSnapshotBytes:N0} bytes; no data was appended.");
            if (_logStream.Length + bytes.Length + 1 > MaxLogBytes)
                throw new
                    SessionStoreException($"Session log would exceed {MaxLogBytes:N0} bytes; no data was appended.");

            await WriteBatchAsync(batch, bytes, cancellationToken).ConfigureAwait(false);

            CommitSequence         = nextSequence;
            _persistedMessageCount = context.Messages.Count;
            _persistedTurnCount    = persistedTurnCount;
            _trackedTurnId         = trackedTurnId;
            _trackedTurnRunCount   = trackedTurnRunCount;
            _lastRunId             = lastRunId;
            _lastRunJson           = lastRunJson;
            _checkpointFingerprint = checkpointFingerprint;
            _lastSessionState      = lastSessionState;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static async Task<SessionStoreWriter> OpenExistingAsync(string directory, string sessionId,
                                                                   IReadOnlyDictionary<string, string>? knownSecrets,
                                                                   CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!SessionStorePaths.IsValidSessionId(sessionId))
            throw new ArgumentException("A session id must contain exactly 32 hexadecimal characters.",
                                        nameof(sessionId));
        sessionId = sessionId.ToLowerInvariant();
        var path = Path.Combine(directory, sessionId + LogFileSuffix);
        if (!File.Exists(path))
            throw new SessionStoreException($"No JSONL session log '{path}' exists.");

        var writer = new SessionStoreWriter(directory, sessionId, false, knownSecrets);
        try
        {
            var snapshot = await SessionStoreReader.ReadAsync(directory, sessionId, cancellationToken)
                                                   .ConfigureAwait(false)
                        ?? throw new SessionStoreException($"Session log '{path}' is empty.");
            writer.CommitSequence         = snapshot.CommitSequence;
            writer.LoadedSnapshot         = snapshot;
            writer._persistedTurnCount    = snapshot.Turns.Count;
            writer._trackedTurnId         = snapshot.Turns.LastOrDefault()?.Id;
            writer._trackedTurnRunCount   = snapshot.Turns.LastOrDefault()?.Runs.Count ?? 0;
            writer._persistedMessageCount = snapshot.Conversation?.Messages.Count      ?? 0;
            writer._lastSessionState = new SessionLogSessionState(snapshot.InspectionAcknowledgedUtc,
                                                                  snapshot.PendingInspectionReason);
            if (snapshot.Conversation is { } conversation)
                writer._checkpointFingerprint = $"{conversation.HasState}:{conversation.FoldStartMessageIndex}:" +
                                                $"{conversation.FoldEndMessageIndex}:" + (conversation.HasState
                                                    ? conversation.StateJson
                                                    : string.Empty);
            if (snapshot.Turns.LastOrDefault()?.Runs.LastOrDefault() is { } lastRun)
            {
                writer._lastRunId   = lastRun.Id;
                writer._lastRunJson = JsonSerializer.Serialize(lastRun, SessionLogJsonContext.Default.PersistedRun);
            }

            return writer;
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    private PersistedRun PersistRun(SessionRun run)
    {
        var toolCalls = run.Result?.ToolCalls ?? run.ExecutionRecords;
        return new PersistedRun
        {
            Id              = run.Id,
            StartedUtc      = run.StartedUtc,
            FinishedUtc     = run.FinishedUtc,
            Status          = run.Result?.Status.ToString(),
            FinalMessage    = run.Result is null ? null : _redactor.Redact(run.Result.FinalMessage),
            Error           = run.Result is null ? null : _redactor.RedactNullable(run.Result.Error),
            Steps           = run.Result?.Steps          ?? 0,
            ToolExecutions  = run.Result?.ToolExecutions ?? 0,
            InputTokens     = run.Result?.InputTokens,
            OutputTokens    = run.Result?.OutputTokens,
            NeedsInspection = toolCalls?.Any(c => c.SideEffects && c.State == ToolCallExecutionState.UnknownResult),
            ToolCalls = toolCalls?.Select(c => new PersistedToolCallRecord
            {
                CallId = _redactor.Redact(c.CallId), InvocationId = c.InvocationId,
                ToolName = _redactor.Redact(c.ToolName),
                Capability = _redactor.RedactNullable(c.Capability), SideEffects = c.SideEffects,
                Summary = _redactor.Redact(c.Summary), PreparationError = _redactor.RedactNullable(c.PreparationError),
                Approval = _redactor.RedactNullable(c.Approval), State = c.State.ToString()
            }).ToArray()
        };
    }

    private async Task WriteBatchAsync(SessionLogBatch batch, byte[] bytes, CancellationToken cancellationToken)
    {
        try
        {
            InjectWriteFault?.Invoke(batch, "before-write");
            await _logStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _logStream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            InjectWriteFault?.Invoke(batch, "after-line");
            await _logStream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            _logStream.Flush(true);
            InjectWriteFault?.Invoke(batch, "after-flush");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _poisoned    = true;
            WriteFailure = ex.Message;
            throw new
                SessionStoreException($"Session '{SessionId}' journal write failed; the writer is poisoned and will not retry.",
                                      ex);
        }
    }

    private static PersistedMessage PersistMessage(ChatMessage message) => new()
    {
        Role = message.Role.ToString(), Content = message.Content, ToolCallId = message.ToolCallId, Name = message.Name,
        ToolCalls = message.ToolCalls?.Select(c => new PersistedToolCall(c.Id, c.FunctionName, c.ArgumentsJson))
                           .ToArray(),
        Reasoning = message.Reasoning?.Select(r => new PersistedReasoning
        {
            Text       = r.Text, ProtectedData = r.ProtectedData, ItemId = r.ItemId,
            OpaqueHash = PersistedReasoning.ComputeOpaqueHash(r.ProtectedData, r.ItemId)
        }).ToArray()
    };

    internal static string BatchId(string sessionId, long sequence) => $"{sessionId}:{sequence:D20}";

    internal static string RecordId(string sessionId, long sequence, int index) =>
        $"{sessionId}:{sequence:D20}:{index:D4}";

    private SessionStoreException PoisonedException() =>
        new($"Session '{SessionId}' journal writer is poisoned after a failed append: {WriteFailure ?? "unknown write failure"}.");
}

/// <summary>
///     <para>严格扫描 JSONL 会话日志并恢复内存投影。</para>
///     <para>Strictly scans JSONL session logs into an in-memory projection.</para>
/// </summary>
public static class SessionStoreReader
{
    public const int MaxJsonDepth = 32;

    public static async Task<PersistedSession?> ReadAsync(
        string directory, string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!SessionStorePaths.IsValidSessionId(sessionId))
            throw new SessionStoreException($"'{sessionId}' is not a valid session id.");
        sessionId = sessionId.ToLowerInvariant();
        var path = Path.Combine(directory, sessionId + SessionStoreWriter.LogFileSuffix);
        SessionStorePaths.EnsureStoreLeaf(directory, path);
        if (!File.Exists(path)) return null;
        var length = new FileInfo(path).Length;
        if (length > SessionStoreWriter.MaxLogBytes)
            throw new
                SessionStoreException($"Session log '{path}' exceeds the {SessionStoreWriter.MaxLogBytes:N0}-byte limit.");

        var builder = new ProjectionBuilder(sessionId);
        SessionStorePaths.EnsureStoreLeaf(directory, path);
        var scan = await SessionLogScanner.ScanAsync(path, sessionId, length, null, false,
                                                     (batch, _) =>
                                                     {
                                                         builder.Apply(batch);
                                                         return ValueTask.CompletedTask;
                                                     }, cancellationToken).ConfigureAwait(false);
        return builder.Build(scan.LastSequence);
    }

    public static async Task<IReadOnlyList<SessionStoreEntry>> ListAsync(
        string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return [];
        var entries = new List<SessionStoreEntry>();
        foreach (var path in Directory.EnumerateFiles(directory, "*" + SessionStoreWriter.LogFileSuffix))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(path)[..^SessionStoreWriter.LogFileSuffix.Length];
            try
            {
                var session = await ReadAsync(directory, id, cancellationToken).ConfigureAwait(false);
                entries.Add(new SessionStoreEntry(id, session, null));
            }
            catch (SessionStoreException ex)
            {
                entries.Add(new SessionStoreEntry(id, null, ex.Message));
            }
        }

        return entries.OrderByDescending(e => e.Session?.UpdatedUtc ?? DateTimeOffset.MinValue).ToArray();
    }

    private sealed class ProjectionBuilder(string sessionId)
    {
        private readonly Dictionary<string, PersistedToolCallRecord> _completedToolFacts = new(StringComparer.Ordinal);
        private readonly List<PersistedMessage> _messages = [];
        private readonly List<PersistedTurn> _turns = [];
        private readonly HashSet<string> _unknownSideEffectCalls = new(StringComparer.Ordinal);
        private          long _acknowledgedThroughSequence;
        private          SessionLogCheckpoint? _checkpoint;
        private          SessionLogIdentity? _identity;
        private          SessionLogSessionState? _state;
        private          DateTimeOffset _updatedUtc;

        public void Apply(SessionLogBatch batch)
        {
            _updatedUtc = batch.WrittenUtc;
            foreach (var record in batch.Records)
                switch (record.Kind)
                {
                    case "history" :
                        var history = Read<SessionHistoryItem>(record.Payload,
                                                               SessionHistoryJsonContext.Default.SessionHistoryItem);
                        if (history.SessionId != sessionId || history.Id != record.Id ||
                            history.Sequence  != batch.Sequence)
                            throw new
                                SessionStoreException($"Session '{sessionId}' contains an unbound history item at sequence {batch.Sequence}.");
                        ApplyHistory(history);
                        break;
                    case "audit" :
                        var audit = record.Payload.Deserialize(PersistenceJsonContext.Default.AuditRecord)
                                 ?? throw new
                                        SessionStoreException($"Session '{sessionId}' contains an empty audit record.");
                        if (string.IsNullOrWhiteSpace(audit.Kind) || string.IsNullOrWhiteSpace(audit.RunId))
                            throw new SessionStoreException($"Session '{sessionId}' contains an invalid audit record.");
                        break;
                    case "identity" :
                        if (_identity is not null || batch.Sequence != 1)
                            throw new
                                SessionStoreException($"Session '{sessionId}' has a duplicate or misplaced identity record.");
                        _identity = Read<SessionLogIdentity>(record.Payload,
                                                             SessionLogJsonContext.Default.SessionLogIdentity);
                        if (_identity.Id != sessionId || _identity.Model is null)
                            throw new
                                SessionStoreException($"Session log identity '{_identity.Id}' does not match file session '{sessionId}'.");
                        break;
                    case "turn" :
                        var turn = Read<SessionLogTurn>(record.Payload, SessionLogJsonContext.Default.SessionLogTurn);
                        if (_turns.Any(t => t.Id == turn.Id))
                            throw new SessionStoreException($"Session '{sessionId}' repeats turn id '{turn.Id}'.");
                        _turns.Add(new PersistedTurn
                                       { Id = turn.Id, UserInput = turn.UserInput, CreatedUtc = turn.CreatedUtc });
                        break;
                    case "run" :
                        var run   = Read<SessionLogRun>(record.Payload, SessionLogJsonContext.Default.SessionLogRun);
                        var index = _turns.FindIndex(t => t.Id == run.TurnId);
                        if (index < 0)
                            throw new
                                SessionStoreException($"Session '{sessionId}' references an unknown turn '{run.TurnId}'.");
                        var oldTurn  = _turns[index];
                        var runs     = oldTurn.Runs.ToList();
                        var runIndex = runs.FindIndex(r => r.Id == run.Run.Id);
                        ReconcileRun(run.TurnId, run.Run, runIndex < 0 ? null : runs[runIndex]);
                        if (runIndex < 0) runs.Add(run.Run);
                        else runs[runIndex] = run.Run;
                        _turns[index] = oldTurn with { Runs = runs };
                        if (run.Run.ToolCalls is not null)
                            foreach (var call in run.Run.ToolCalls)
                            {
                                var key = UnknownCallKey(run.TurnId, run.Run.Id, call.InvocationId, call.CallId);
                                if (call is
                                    {
                                        SideEffects: true, State: nameof(ToolCallExecutionState.UnknownResult)
                                    } &&
                                    batch.Sequence > _acknowledgedThroughSequence)
                                    _unknownSideEffectCalls.Add(key);
                                else if (call.State is nameof(ToolCallExecutionState.ConfirmedResult) or
                                                       nameof(ToolCallExecutionState.KnownNotStarted))
                                    _unknownSideEffectCalls.Remove(key);
                            }

                        break;
                    case "message" :
                        _messages.Add(Read<SessionLogMessage>(record.Payload,
                                                              SessionLogJsonContext.Default.SessionLogMessage).Message);
                        break;
                    case "checkpoint" :
                        _checkpoint =
                            Read<SessionLogCheckpoint>(record.Payload,
                                                       SessionLogJsonContext.Default.SessionLogCheckpoint);
                        break;
                    case "session-state" :
                        _state = Read<SessionLogSessionState>(record.Payload,
                                                              SessionLogJsonContext.Default.SessionLogSessionState);
                        if (_state.InspectionAcknowledgedUtc is not null)
                        {
                            _unknownSideEffectCalls.Clear();
                            _acknowledgedThroughSequence = batch.Sequence;
                        }

                        break;
                    default :
                        throw new
                            SessionStoreException($"Session '{sessionId}' contains unknown required record kind '{record.Kind}'.");
                }
        }

        public PersistedSession Build(long sequence)
        {
            if (_identity?.Model is null)
                throw new SessionStoreException($"Session '{sessionId}' has no valid identity record.");
            var checkpoint            = _checkpoint ?? new SessionLogCheckpoint(null, false, -1, 0);
            var hasUnknownSideEffects = _unknownSideEffectCalls.Count > 0;
            var acknowledged          = _state?.InspectionAcknowledgedUtc;
            var pendingReason = hasUnknownSideEffects
                ? _state?.PendingInspectionReason ??
                  "A side-effect tool has an unknown result; inspect before continuing."
                : _state?.ExecutionIntent == true
                    ? null
                    : acknowledged is not null
                        ? null
                        : _state?.PendingInspectionReason;
            return new PersistedSession
            {
                Id = _identity.Id, WorkspaceRoot = _identity.WorkspaceRoot, Model = _identity.Model,
                CreatedUtc = _identity.CreatedUtc, UpdatedUtc = _updatedUtc, CommitSequence = sequence,
                PendingInspectionReason = pendingReason,
                InspectionAcknowledgedUtc =
                    hasUnknownSideEffects || _state?.ExecutionIntent == true ? null : acknowledged,
                Turns = _turns,
                Conversation = new PersistedConversation
                {
                    Messages              = _messages, StateJson = checkpoint.StateJson, HasState = checkpoint.HasState,
                    FoldStartMessageIndex = checkpoint.FoldStartMessageIndex,
                    FoldEndMessageIndex   = checkpoint.FoldEndMessageIndex
                }
            };
        }

        private void ApplyHistory(SessionHistoryItem item)
        {
            if (item.Kind is not (SessionHistoryKind.User or SessionHistoryKind.Process or
                                  SessionHistoryKind.Final or SessionHistoryKind.Tool))
                throw new SessionStoreException($"Session '{sessionId}' contains unknown history kind '{item.Kind}'.");
            if (item.Kind != SessionHistoryKind.Tool) return;
            if (string.IsNullOrWhiteSpace(item.RunId) || string.IsNullOrWhiteSpace(item.CallId) ||
                string.IsNullOrWhiteSpace(item.State))
                throw new
                    SessionStoreException($"Session '{sessionId}' contains an incomplete terminal tool history item.");

            var callState = item.State switch
            {
                nameof(ToolCallExecutionState.UnknownResult)   => nameof(ToolCallExecutionState.UnknownResult),
                nameof(ToolCallExecutionState.ConfirmedResult) => nameof(ToolCallExecutionState.ConfirmedResult),
                nameof(ToolCallExecutionState.KnownNotStarted) => nameof(ToolCallExecutionState.KnownNotStarted),
                _ => throw new
                    SessionStoreException($"Session '{sessionId}' contains an invalid terminal tool state '{item.State}'.")
            };
            var turnIndex = _turns.FindIndex(turn => turn.Id == item.TurnId);
            if (turnIndex < 0)
                throw new
                    SessionStoreException($"Session '{sessionId}' history references unknown turn '{item.TurnId}'.");
            var turn     = _turns[turnIndex];
            var runs     = turn.Runs.ToList();
            var runIndex = runs.FindIndex(run => run.Id == item.RunId);
            if (runIndex < 0)
                throw
                    new SessionStoreException($"Session '{sessionId}' history references unknown run '{item.RunId}'.");
            var run     = runs[runIndex];
            var factKey = UnknownCallKey(item.TurnId, item.RunId, item.InvocationId, item.CallId);
            if (_completedToolFacts.ContainsKey(factKey))
                throw new
                    SessionStoreException($"Session '{sessionId}' contains duplicate terminal history for run '{item.RunId}', call '{item.CallId}'.");
            var calls = run.ToolCalls?.ToList() ?? [];
            var callIndex = calls.FindIndex(call =>
                                                call.InvocationId is not null && item.InvocationId is not null
                                                    ? call.InvocationId == item.InvocationId
                                                    : call.CallId       == item.CallId);
            var requiresIntent = callState != nameof(ToolCallExecutionState.KnownNotStarted) &&
                                 (item.Capability is null || !ToolCapabilities.IsReadOnly(item.Capability));
            if (callIndex < 0)
            {
                if (requiresIntent)
                    throw new
                        SessionStoreException($"Session '{sessionId}', run '{item.RunId}', invocation '{item.InvocationId ?? item.CallId}' has an executed side-effect history fact without an execution-intent record.");
                // 拒绝、准备失败及只读调用可能先写终态事实，随后才提交运行的完整记录。
                calls.Add(new PersistedToolCallRecord
                {
                    CallId           = item.CallId,
                    InvocationId     = item.InvocationId,
                    ToolName         = item.ToolName ?? string.Empty,
                    Capability       = item.Capability,
                    SideEffects      = item.Capability is not null && !ToolCapabilities.IsReadOnly(item.Capability),
                    Summary          = item.Summary ?? string.Empty,
                    PreparationError = item.PreparationError,
                    Approval         = item.Approval,
                    State            = callState
                });
                callIndex = calls.Count - 1;
            }
            else if (item.InvocationId is not null && calls[callIndex].InvocationId is not null)
            {
                var call = calls[callIndex];
                if (call.CallId != item.CallId || (item.ToolName is not null && call.ToolName   != item.ToolName) ||
                    (item.Capability is not null                             && call.Capability != item.Capability))
                    throw new
                        SessionStoreException($"Session '{sessionId}', run '{item.RunId}', invocation '{item.InvocationId}' has conflicting tool identity in terminal history.");
                if (requiresIntent && (!call.SideEffects || call.State != nameof(ToolCallExecutionState.UnknownResult)))
                    throw new
                        SessionStoreException($"Session '{sessionId}', run '{item.RunId}', invocation '{item.InvocationId}' has no pending side-effect execution intent.");
            }

            calls[callIndex] = calls[callIndex] with { State = callState };
            _completedToolFacts.Add(factKey, calls[callIndex]);
            var key = factKey;
            if (callState     == nameof(ToolCallExecutionState.UnknownResult) && calls[callIndex].SideEffects &&
                item.Sequence > _acknowledgedThroughSequence)
                _unknownSideEffectCalls.Add(key);
            else if (callState is nameof(ToolCallExecutionState.ConfirmedResult) or
                                  nameof(ToolCallExecutionState.KnownNotStarted))
                _unknownSideEffectCalls.Remove(key);
            runs[runIndex] = run with
            {
                ToolCalls = calls,
                NeedsInspection = calls.Any(call => call.SideEffects &&
                                                    call.State == nameof(ToolCallExecutionState.UnknownResult))
            };
            _turns[turnIndex] = turn with { Runs = runs };
        }

        /// <summary>
        ///     <para>将后续运行记录与权威终态事实对账，拒绝降级、改名或遗漏已完成调用。</para>
        ///     <para>Reconciles later run records with authoritative terminal facts, rejecting downgrade, renaming, or omission.</para>
        /// </summary>
        private void ReconcileRun(string turnId, PersistedRun incoming, PersistedRun? previous)
        {
            var invocationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var call in incoming.ToolCalls ?? [])
            {
                if (call.InvocationId is null) continue;
                if (string.IsNullOrWhiteSpace(call.InvocationId) || !invocationIds.Add(call.InvocationId))
                    throw new
                        SessionStoreException($"Session '{sessionId}', run '{incoming.Id}' repeats or omits an invocation identity.");
                var key = UnknownCallKey(turnId, incoming.Id, call.InvocationId, call.CallId);
                if (!_completedToolFacts.TryGetValue(key, out var terminal)) continue;
                if (call.State      != terminal.State      || call.CallId != terminal.CallId ||
                    call.ToolName   != terminal.ToolName   ||
                    call.Capability != terminal.Capability || call.SideEffects != terminal.SideEffects)
                    throw new
                        SessionStoreException($"Session '{sessionId}', run '{incoming.Id}', invocation '{call.InvocationId}' contradicts its terminal history fact.");
            }

            foreach (var call in previous?.ToolCalls ?? [])
                if (call.InvocationId is not null && !invocationIds.Contains(call.InvocationId) &&
                    _completedToolFacts.ContainsKey(UnknownCallKey(turnId, incoming.Id, call.InvocationId,
                                                                   call.CallId)))
                    throw new
                        SessionStoreException($"Session '{sessionId}', run '{incoming.Id}' omits completed invocation '{call.InvocationId}'.");
        }

        private T Read<T>(JsonElement payload, JsonTypeInfo<T> typeInfo) =>
            payload.Deserialize(typeInfo) ??
            throw new SessionStoreException($"Session '{sessionId}' has an empty {typeof(T).Name} payload.");

        private static string UnknownCallKey(string turnId, string runId, string? invocationId, string callId) =>
            $"{turnId}\u001f{runId}\u001f{(invocationId is null ? "call:" + callId : "invocation:" + invocationId)}";
    }
}

[JsonSerializable(typeof(SessionLogBatch))]
[JsonSerializable(typeof(SessionLogRecord))]
[JsonSerializable(typeof(SessionLogIdentity))]
[JsonSerializable(typeof(SessionLogTurn))]
[JsonSerializable(typeof(SessionLogRun))]
[JsonSerializable(typeof(SessionLogMessage))]
[JsonSerializable(typeof(SessionLogCheckpoint))]
[JsonSerializable(typeof(SessionLogSessionState))]
[JsonSerializable(typeof(PersistedRun))]
[JsonSerializable(typeof(PersistedTurn))]
[JsonSerializable(typeof(PersistedMessage))]
[JsonSerializable(typeof(PersistedToolCall))]
[JsonSerializable(typeof(PersistedToolCallRecord))]
[JsonSerializable(typeof(PersistedReasoning))]
[JsonSerializable(typeof(SessionModelIdentity))]
[JsonSerializable(typeof(ChatApiKind))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             UseStringEnumConverter = true,
                             GenerationMode = JsonSourceGenerationMode.Metadata |
                                              JsonSourceGenerationMode.Serialization)]
public sealed partial class SessionLogJsonContext : JsonSerializerContext;

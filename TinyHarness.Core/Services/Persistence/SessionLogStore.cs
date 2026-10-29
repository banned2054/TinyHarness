using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.ProjectInstructions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
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
///     <para>强类型打开既有 writer 的状态：成功、确定的锁占用，或其余失败。</para>
///     <para>Strongly typed statuses for opening an existing writer: opened, a definite lock hold, or any other failure.</para>
/// </summary>
internal enum SessionWriterOpenStatus
{
    Opened,
    LockHeld,
    Failed
}

/// <summary>
///     <para>
///         <see cref="SessionStoreWriter.TryOpenExistingAsync" /> 的结果；<see cref="Status" /> 为 Opened 时调用方负责释放
///         <see cref="Writer" />，否则 <see cref="Error" /> 携带不含凭据的结构化失败原因。
///     </para>
///     <para>
///         The result of <see cref="SessionStoreWriter.TryOpenExistingAsync" />; when opened, the caller owns
///         and disposes <see cref="Writer" />. Otherwise <see cref="Error" /> carries the credential-free
///         structural failure reason.
///     </para>
/// </summary>
internal sealed record SessionWriterOpenResult(
    SessionWriterOpenStatus Status,
    SessionStoreWriter?     Writer,
    SessionStoreException?  Error)
{
    internal static SessionWriterOpenResult FromOpened(SessionStoreWriter writer) =>
        new(SessionWriterOpenStatus.Opened, writer, null);

    internal static SessionWriterOpenResult FromLockHeld(string sessionId) =>
        new(SessionWriterOpenStatus.LockHeld, null,
            new SessionStoreException($"Session '{sessionId}' is already open by another writer."));

    internal static SessionWriterOpenResult FromFailure(SessionStoreException error) =>
        new(SessionWriterOpenStatus.Failed, null, error);
}

/// <summary>
///     <para>将会话提交为带排他锁的追加式 JSONL 批次，并在不确定写入后停止追加。</para>
///     <para>Commits sessions as append-only JSONL batches under an exclusive lock and stops after uncertain writes.</para>
/// </summary>
public sealed class SessionStoreWriter : ISessionStoreWriter
{
    public const int  MaxSnapshotBytes = 8    * 1024 * 1024;
    public const long MaxLogBytes      = 256L * 1024 * 1024;

    public const string LogFileSuffix = ".session.jsonl";

    // Win32 ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION 的 HRESULT，仅 Windows 的共享检查会返回。
    private const    int                                  SharingViolationHResult = unchecked((int)0x80070020);
    private const    int                                  LockViolationHResult    = unchecked((int)0x80070021);
    private readonly SemaphoreSlim                        _gate                   = new(1, 1);
    private readonly IReadOnlyDictionary<string, string>? _knownSecrets;

    private readonly FileStream     _lockStream;
    private readonly FileStream     _logStream;
    private readonly SecretRedactor _redactor;

    private string? _checkpointFingerprint;

    private Func<SessionHistoryItem, CancellationToken, ValueTask>? _historyItemCommitted;

    private Action<Exception>?      _historyPublishError;
    private string?                 _lastRunId;
    private string?                 _lastRunJson;
    private SessionLogSessionState? _lastSessionState;

    private long    _persistedMessageCount;
    private int     _persistedProjectInstructionRecordCount;
    private int     _persistedTurnCount;
    private bool    _poisoned;
    private string? _projectId;
    private string? _trackedTurnId;
    private int     _trackedTurnRunCount;

    public SessionStoreWriter(
        string directory, string sessionId, IReadOnlyDictionary<string, string>? knownSecrets = null)
        : this(directory, sessionId, true, knownSecrets)
    {
    }

    private SessionStoreWriter(
        string      directory, string sessionId, bool requireNew, IReadOnlyDictionary<string, string>? knownSecrets,
        FileStream? acquiredLockStream = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!SessionStorePaths.IsValidSessionId(sessionId))
            throw new ArgumentException("A session id must contain exactly 32 hexadecimal characters.",
                                        nameof(sessionId));

        Directory.CreateDirectory(directory);
        SessionId     = sessionId.ToLowerInvariant();
        SnapshotPath  = Path.Combine(directory, SessionId + LogFileSuffix);
        _knownSecrets = knownSecrets;
        _redactor     = new SecretRedactor(_knownSecrets);

        var lockPath = Path.Combine(directory, $"{SessionId}.lock");
        SessionStorePaths.EnsureStoreLeaf(directory, lockPath);
        SessionStorePaths.EnsureStoreLeaf(directory, SnapshotPath);
        try
        {
            // acquiredLockStream 由调用方在锁分类后移交；此处不再重复打开。
            _lockStream = acquiredLockStream ??
                          new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
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
    ///     <para>已可靠提交的模型选择数量；恢复时从日志快照续种，供调用方判断当前选择是否已持久化。</para>
    ///     <para>
    ///         The number of reliably committed model selections; resumed sessions seed it from the log snapshot so callers
    ///         can tell whether the current selection is durable.
    ///     </para>
    /// </summary>
    public int PersistedModelSelectionCount { get; private set; }

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
        CreateBoundRunRecorder(sessionId, turnId, runId, commitInitialContext, historyItemCommitted);

    public Task CommitAsync(ChatSession session, ConversationContext context, CancellationToken cancellationToken) =>
        CommitCoreAsync(session, context, false, cancellationToken);

    public Task CommitExecutionIntentAsync(
        ChatSession session, ConversationContext context, CancellationToken cancellationToken) =>
        CommitCoreAsync(session, context, true, cancellationToken);

    public Task CommitControlAsync(ChatSession                     session,
                                   ConversationContext             context,
                                   IReadOnlyList<SessionLogRecord> records,
                                   CancellationToken               cancellationToken) =>
        CommitCoreAsync(session, context, false, cancellationToken, records);

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

            var     sequence         = CommitSequence + 1;
            var     normalized       = new SessionLogRecord[records.Count];
            string? bindingProjectId = null;
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
                {
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
                            if (item.Kind is not (SessionHistoryKind.User or SessionHistoryKind.Process or
                                                  SessionHistoryKind.Final or SessionHistoryKind.Tool))
                                throw new
                                    SessionStoreException($"History kind '{item.Kind}' cannot be appended through the generic journal API.");
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
                }
                else if (record.Kind is "run-amendment" or "work-item")
                {
                    ValidateControlRecord(record);
                    normalized[i] = record with { Id = recordId };
                }
                else if (record.Kind == "project-binding")
                {
                    if (_projectId is not null || bindingProjectId is not null)
                        throw new
                            SessionStoreException($"Session '{SessionId}' already carries a project binding; ownership is bound once and never appended again or transferred.");
                    try
                    {
                        bindingProjectId = SessionLogProjectBinding.Parse(record.Payload);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or JsonException)
                    {
                        throw new
                            SessionStoreException($"Journal record 'project-binding' has an invalid typed payload: {ex.Message}",
                                                  ex);
                    }

                    normalized[i] = record with { Id = recordId };
                }
                else
                {
                    throw new SessionStoreException($"Unknown required journal record kind '{record.Kind}'.");
                }
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
            // 校验只暂存待绑定值，成功落盘后才推进 writer 归属缓存。
            if (bindingProjectId is not null) _projectId = bindingProjectId;
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

    private IRunRecorder CreateBoundRunRecorder(
        string sessionId, string turnId, string runId, Func<CancellationToken, Task> commitInitialContext,
        Func<SessionHistoryItem, CancellationToken, ValueTask>? historyItemCommitted)
    {
        _historyItemCommitted = historyItemCommitted;
        var recorder = new SessionLogRunRecorder(this, sessionId, turnId, runId, commitInitialContext, _knownSecrets,
                                                 historyItemCommitted);
        _historyPublishError = recorder.ReportHistoryPublishError;
        return recorder;
    }

    private async Task CommitCoreAsync(ChatSession session, ConversationContext context, bool executionIntent,
                                       CancellationToken cancellationToken,
                                       IReadOnlyList<SessionLogRecord>? controlRecords = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(session.Id, SessionId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The writer and session ids differ.", nameof(session));
        List<SessionHistoryItem>? committedHistoryItems = null;
        var                       historyCallback       = _historyItemCommitted;
        var                       historyPublishError   = _historyPublishError;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_poisoned) throw PoisonedException();
            if (CommitSequence > 0 && !string.Equals(session.ProjectId, _projectId, StringComparison.Ordinal))
                throw new
                    SessionStoreException($"Session '{SessionId}' carries project '{session.ProjectId ?? "(unbound)"}' while the committed journal carries '{_projectId ?? "(unbound)"}'; project ownership is bound once and never transferred or dropped.");
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
            {
                Add("identity",
                    new SessionLogIdentity(SessionId, session.WorkspaceRoot, session.InitialModel, session.CreatedUtc));
                // 初始归属随身份批次一次提交，不先请求模型再补写。
                if (session.ProjectId is { } initialProjectId)
                    Add("project-binding",
                        new SessionLogProjectBinding(SessionLogProjectBinding.SchemaName, initialProjectId));
            }

            var modelSelectionCount = PersistedModelSelectionCount;
            for (var i = modelSelectionCount; i < session.ModelSelections.Count; i++)
            {
                Add("model-selection", session.ModelSelections[i]);
                modelSelectionCount++;
            }

            var turns = session.Turns;
            for (var i = persistedTurnCount; i < turns.Count; i++)
            {
                var turn = turns[i];
                Add("turn", new SessionLogTurn(turn.Id, turn.UserInput, turn.CreatedUtc));
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
                if (trackedTurnId != lastTurn.Id)
                    throw new
                        SessionStoreException("The session turn sequence changed behind the committed journal cursor.");

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

            for (var i = (int)_persistedMessageCount; i < context.Messages.Count; i++)
                Add("message", new SessionLogMessage(PersistMessage(context.Messages[i])));

            var projectInstructionRecords = context.ProjectInstructionRecords;
            if (projectInstructionRecords.Count < _persistedProjectInstructionRecordCount)
                throw new
                    SessionStoreException("The in-memory project-instruction record list rewound behind the committed journal prefix.");
            for (var i = _persistedProjectInstructionRecordCount; i < projectInstructionRecords.Count; i++)
            {
                var instructionRecord = projectInstructionRecords[i];
                var persistedInstructionRecord = instructionRecord.Action == "scopes-tracked"
                    ? instructionRecord with
                    {
                        Snapshot = instructionRecord.Snapshot with { Documents = [] }
                    }
                    : instructionRecord;
                Add("project-instructions", persistedInstructionRecord);
                if (instructionRecord.Action != "rules-applied") continue;
                var item = new SessionHistoryItem
                {
                    Id        = RecordId(SessionId, nextSequence, records.Count),
                    Sequence  = nextSequence,
                    SessionId = SessionId,
                    TurnId    = instructionRecord.TurnId,
                    RunId     = instructionRecord.RunId,
                    Kind      = SessionHistoryKind.ProjectInstructions,
                    Text      = ProjectInstructionHistorySummary(instructionRecord)
                };
                records.Add(new SessionLogRecord(item.Id, "history",
                                                 JsonSerializer.SerializeToElement(item,
                                                          SessionHistoryJsonContext.Default.SessionHistoryItem)));
                (committedHistoryItems ??= []).Add(item);
            }

            var position = context.Position;
            var nextCheckpointFingerprint =
                $"{position.HasState}:{position.FoldStartMessageIndex}:{position.FoldEndMessageIndex}:" +
                (position.HasState ? context.State.ToJson() : string.Empty)                             + ":" +
                context.CheckpointModelSelectionRevision;
            if (!string.Equals(nextCheckpointFingerprint, checkpointFingerprint, StringComparison.Ordinal))
            {
                if (position.HasState || checkpointFingerprint is not null)
                    Add("compaction-checkpoint", new SessionLogCheckpoint(position.HasState ? context.State : null,
                                                                          position.HasState,
                                                                          position.FoldStartMessageIndex,
                                                                          position.FoldEndMessageIndex,
                                                                          context.CheckpointModelSelectionRevision));
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

            foreach (var record in controlRecords ?? [])
            {
                ValidateControlRecord(record);
                if (record.Kind == "model-selection") modelSelectionCount++;
                records.Add(record with { Id = RecordId(SessionId, nextSequence, records.Count) });
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

            CommitSequence                          = nextSequence;
            PersistedModelSelectionCount            = modelSelectionCount;
            _persistedMessageCount                  = context.Messages.Count;
            _persistedProjectInstructionRecordCount = projectInstructionRecords.Count;
            _persistedTurnCount                     = persistedTurnCount;
            _trackedTurnId                          = trackedTurnId;
            _trackedTurnRunCount                    = trackedTurnRunCount;
            _lastRunId                              = lastRunId;
            _lastRunJson                            = lastRunJson;
            _checkpointFingerprint                  = checkpointFingerprint;
            _lastSessionState                       = lastSessionState;
            _projectId                              = session.ProjectId;
        }
        finally
        {
            _gate.Release();
        }

        if (committedHistoryItems is not null)
            await PublishCommittedHistoryItemsAsync(historyCallback, historyPublishError, committedHistoryItems,
                                                    cancellationToken).ConfigureAwait(false);
    }

    private string ProjectInstructionHistorySummary(ProjectInstructionRecord instructionRecord)
    {
        var sources = instructionRecord.Snapshot.Documents.Count == 0
            ? "no source documents (empty rules)"
            : string.Join("; ", instructionRecord.Snapshot.Documents.Select(document =>
                                                                                $"source: {_redactor.Redact(document.SourcePath)} (scope: {_redactor.Redact(document.ScopeDirectory)})"));
        return
            _redactor.Redact($"Project instructions {instructionRecord.Action}: revision {instructionRecord.Revision}; " +
                             $"{instructionRecord.Snapshot.Documents.Count} document(s); {sources}; reason {instructionRecord.Reason}.");
    }

    private async Task PublishCommittedHistoryItemsAsync(
        Func<SessionHistoryItem, CancellationToken, ValueTask>? callback,
        Action<Exception>?                                      historyPublishError,
        IReadOnlyList<SessionHistoryItem>                       items, CancellationToken cancellationToken)
    {
        if (callback is null) return;
        foreach (var item in items)
            try
            {
                await callback(item, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try
                {
                    if (historyPublishError is { } reportError)
                    {
                        reportError(ex);
                    }
                    else
                    {
                        var safeMessage                           = _redactor.Redact(ex.Message);
                        if (safeMessage.Length > 512) safeMessage = safeMessage[..512];
                        Trace.TraceError("Committed session history live publication failed: {0}", safeMessage);
                    }
                }
                catch (Exception reportException)
                {
                    Trace.TraceError("Committed session history error reporting failed: {0}",
                                     _redactor.Redact(reportException.Message));
                }
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
            writer.SeedFromSnapshot(snapshot);
            return writer;
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     <para>
    ///         以强类型结果打开既有会话 writer：仅确定的锁共享/占用返回 <see cref="SessionWriterOpenStatus.LockHeld" />，
    ///         路径、链接、权限及其他 IO 失败返回 <see cref="SessionWriterOpenStatus.Failed" />，不按异常文本区分。
    ///     </para>
    ///     <para>
    ///         Opens an existing session writer as a strongly typed result: only a definite lock sharing or hold
    ///         returns <see cref="SessionWriterOpenStatus.LockHeld" />, while path, link, permission, and other IO
    ///         failures return <see cref="SessionWriterOpenStatus.Failed" /> — never classified by message text.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         锁冲突仅按 Windows 共享/锁冲突 HRESULT 确定区分；其他平台无法确定归属时按失败报告。
    ///         <see cref="SessionWriterOpenStatus.Opened" /> 时返回的 writer 由调用方释放；
    ///         取消在读取阶段抛出并释放已持有的句柄。
    ///     </para>
    ///     <para>
    ///         Lock contention is decided only by the Windows sharing/lock-violation HRESULTs; platforms where it
    ///         cannot be decided report failure. The caller owns and disposes a writer returned as
    ///         <see cref="SessionWriterOpenStatus.Opened" />; cancellation during the strict read throws after
    ///         releasing the held handles.
    ///     </para>
    /// </remarks>
    internal static async Task<SessionWriterOpenResult> TryOpenExistingAsync(
        string            directory, string sessionId, IReadOnlyDictionary<string, string>? knownSecrets,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!SessionStorePaths.IsValidSessionId(sessionId))
            throw new ArgumentException("A session id must contain exactly 32 hexadecimal characters.",
                                        nameof(sessionId));
        sessionId = sessionId.ToLowerInvariant();
        var path = Path.Combine(directory, sessionId + LogFileSuffix);
        if (!File.Exists(path))
            return SessionWriterOpenResult.FromFailure(
                                                       new
                                                           SessionStoreException($"No JSONL session log '{path}' exists."));

        var                 lockPath   = Path.Combine(directory, $"{sessionId}.lock");
        FileStream?         lockStream = null;
        SessionStoreWriter? writer     = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SessionStorePaths.EnsureStoreLeaf(directory, lockPath);
            try
            {
                lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (IsLockContention(ex))
            {
                return SessionWriterOpenResult.FromLockHeld(sessionId);
            }

            SessionStorePaths.EnsureStoreLeaf(directory, lockPath);
            writer     = new SessionStoreWriter(directory, sessionId, false, knownSecrets, lockStream);
            lockStream = null;
            var snapshot = await SessionStoreReader.ReadAsync(directory, sessionId, cancellationToken)
                                                   .ConfigureAwait(false)
                        ?? throw new SessionStoreException($"Session log '{path}' is empty.");
            writer.SeedFromSnapshot(snapshot);
            var opened = writer;
            writer = null;
            return SessionWriterOpenResult.FromOpened(opened);
        }
        catch (Exception ex) when (IsRecoverableOpenFailure(ex))
        {
            return SessionWriterOpenResult.FromFailure(ex is SessionStoreException storeFailure
                                                           ? storeFailure
                                                           : new SessionStoreException(
                                                                $"Cannot open session '{sessionId}' for ownership initialization: {ex.Message}",
                                                                ex));
        }
        finally
        {
            // 只有成功返回才移交所有权；取消及未分类异常同样必须释放句柄。
            writer?.Dispose();
            lockStream?.Dispose();
        }
    }

    internal static bool IsRecoverableOpenFailure(Exception exception) =>
        exception is SessionStoreException or IOException or UnauthorizedAccessException or JsonException or
                     InvalidDataException or InvalidOperationException or ArgumentException or FormatException or
                     OverflowException or NotSupportedException;

    private static bool IsLockContention(IOException ex) =>
        OperatingSystem.IsWindows() &&
        (ex.HResult == SharingViolationHResult || ex.HResult == LockViolationHResult);

    /// <summary>
    ///     <para>以严格读取的快照续种 writer 的提交游标与各持久化缓存；归属缓存一并从日志镜像。</para>
    ///     <para>
    ///         Reseeds the writer's commit cursor and persistence caches from a strictly read snapshot, mirroring the
    ///         project binding too.
    ///     </para>
    /// </summary>
    private void SeedFromSnapshot(PersistedSession snapshot)
    {
        CommitSequence               = snapshot.CommitSequence;
        LoadedSnapshot               = snapshot;
        PersistedModelSelectionCount = snapshot.ModelSelections.Count;
        _persistedTurnCount          = snapshot.Turns.Count;
        _trackedTurnId               = snapshot.Turns.LastOrDefault()?.Id;
        _trackedTurnRunCount         = snapshot.Turns.LastOrDefault()?.Runs.Count ?? 0;
        _persistedMessageCount       = snapshot.Conversation?.Messages.Count      ?? 0;
        _persistedProjectInstructionRecordCount =
            snapshot.Conversation?.ProjectInstructionRecords.Count ?? 0;
        _lastSessionState = new SessionLogSessionState(snapshot.InspectionAcknowledgedUtc,
                                                       snapshot.PendingInspectionReason);
        _projectId = snapshot.ProjectId;
        if (snapshot.Conversation is { } seededConversation)
            _checkpointFingerprint = $"{seededConversation.HasState}:{seededConversation.FoldStartMessageIndex}:" +
                                     $"{seededConversation.FoldEndMessageIndex}:" + (seededConversation.HasState
                                         ? seededConversation.State?.ToJson()
                                         : string.Empty) + ":" +
                                     seededConversation.CheckpointModelSelectionRevision;
        if (snapshot.Turns.LastOrDefault()?.Runs.LastOrDefault() is not { } lastRun) return;
        _lastRunId   = lastRun.Id;
        _lastRunJson = JsonSerializer.Serialize(lastRun, SessionLogJsonContext.Default.PersistedRun);
    }

    private PersistedRun PersistRun(SessionRun run)
    {
        var toolCalls = run.Result?.ToolCalls ?? run.ExecutionRecords;
        return new PersistedRun
        {
            ModelSelectionRevision = run.ModelSelection?.Revision,
            Id                     = run.Id,
            StartedUtc             = run.StartedUtc,
            FinishedUtc            = run.FinishedUtc,
            Status                 = run.Result?.Status.ToString(),
            FinalMessage           = run.Result?.FinalMessage,
            Error                  = run.Result is null ? null : _redactor.RedactNullable(run.Result.Error),
            Steps                  = run.Result?.Steps          ?? 0,
            ToolExecutions         = run.Result?.ToolExecutions ?? 0,
            InputTokens            = run.Result?.InputTokens,
            OutputTokens           = run.Result?.OutputTokens,
            Budget                 = run.Result?.Budget,
            NeedsInspection =
                toolCalls?.Any(c => c is { SideEffects: true, State: ToolCallExecutionState.UnknownResult }),
            ToolCalls = toolCalls?.Select(c => new PersistedToolCallRecord
            {
                CallId           = c.CallId,
                InvocationId     = c.InvocationId,
                ToolName         = c.ToolName,
                Capability       = c.Capability,
                SideEffects      = c.SideEffects,
                Summary          = c.Summary,
                PreparationError = _redactor.RedactNullable(c.PreparationError),
                NotStartedReason = _redactor.RedactNullable(c.NotStartedReason),
                Approval         = c.Approval,
                State            = c.State.ToString()
            }).ToArray()
        };
    }

    internal static void ValidateControlRecord(SessionLogRecord record)
    {
        if (!string.IsNullOrEmpty(record.Id))
            throw new SessionStoreException("Control record IDs are assigned by the writer.");
        switch (record.Kind)
        {
            case "model-selection" :
                (record.Payload.Deserialize(SessionLogJsonContext.Default.SessionModelSelection)
              ?? throw new InvalidDataException("Missing model selection payload.")).Validate();
                break;
            case "run-amendment" :
                (record.Payload.Deserialize(SessionLogJsonContext.Default.RunAmendment)
              ?? throw new InvalidDataException("Missing amendment payload.")).Validate();
                break;
            case "work-item" :
                (record.Payload.Deserialize(SessionLogJsonContext.Default.SessionWorkItem)
              ?? throw new InvalidDataException("Missing work-item payload.")).Validate();
                break;
            default : throw new SessionStoreException($"Unknown control record kind '{record.Kind}'.");
        }
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

    private static PersistedMessage PersistMessage(ChatMessage message)
    {
        var persisted = new PersistedMessage
        {
            Role       = message.Role.ToString(),
            Content    = message.Content,
            TextItems  = message.TextItems?.ToArray(),
            ToolCallId = message.ToolCallId,
            Name       = message.Name,
            ToolCalls = message.ToolCalls?.Select(c => new PersistedToolCall(c.Id, c.FunctionName, c.ArgumentsJson))
                               .ToArray(),
            Reasoning = message.Reasoning?.Select(r => new PersistedReasoning
            {
                Text          = r.Text,
                ProtectedData = r.ProtectedData,
                ItemId        = r.ItemId,
                OpaqueHash    = PersistedReasoning.ComputeOpaqueHash(r.ProtectedData, r.ItemId)
            }).ToArray()
        };
        try
        {
            persisted.ValidateTextItems();
        }
        catch (InvalidDataException ex)
        {
            throw new SessionStoreException($"Invalid canonical message text items: {ex.Message}", ex);
        }

        return persisted;
    }

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

        var builder = new ProjectionBuilder(sessionId, path);
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

    private sealed class ProjectionBuilder(string sessionId, string logPath)
    {
        private readonly Dictionary<string, RunAmendment> _amendments = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PersistedToolCallRecord> _completedToolFacts = new(StringComparer.Ordinal);
        private readonly List<PersistedMessage> _messages = [];
        private readonly List<SessionModelSelection> _modelSelections = [];
        private readonly List<ProjectInstructionRecord> _projectInstructionRecords = [];
        private readonly List<PersistedTurn> _turns = [];
        private readonly HashSet<string> _unknownSideEffectCalls = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SessionWorkItem> _workItems = new(StringComparer.Ordinal);
        private          long _acknowledgedThroughSequence;
        private          SessionLogCheckpoint? _checkpoint;

        private (string Id, long Sequence, string TurnId, string RunId, string Revision, int DocumentCount)?
            _expectedInstructionHistory;

        private SessionLogIdentity?     _identity;
        private DateTimeOffset          _lastActivityUtc;
        private string?                 _projectId;
        private SessionLogSessionState? _state;
        private DateTimeOffset          _updatedUtc;

        public void Apply(SessionLogBatch batch)
        {
            _updatedUtc = batch.WrittenUtc;
            // 纯归属维护批次不构成对话活动；含任何其他记录的批次按其提交时间推进。
            if (batch.Records.Any(record => record.Kind != "project-binding"))
                _lastActivityUtc = batch.WrittenUtc;
            for (var recordIndex = 0; recordIndex < batch.Records.Count; recordIndex++)
            {
                var record = batch.Records[recordIndex];
                if (_expectedInstructionHistory is not null && record.Kind != "history")
                    throw new
                        SessionStoreException($"Session '{sessionId}' is missing the history item bound to a project-instructions record.");
                switch (record.Kind)
                {
                    case "history" :
                        var history = Read<SessionHistoryItem>(record.Payload,
                                                               SessionHistoryJsonContext.Default.SessionHistoryItem);
                        if (history.SessionId != sessionId ||
                            history.Id        != record.Id ||
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
                    case "model-selection" :
                        var selection =
                            Read<SessionModelSelection>(record.Payload,
                                                        SessionLogJsonContext.Default.SessionModelSelection);
                        selection.Validate();
                        var previousSelection = _modelSelections.LastOrDefault();
                        // /2 记录显式携带前驱并须与链上一致；/1 旧记录无前驱字段，按修订连续性推导。
                        if (!selection.IsLegacySchema &&
                            selection.PreviousRevision != (previousSelection?.Revision ?? 0))
                            throw new SessionStoreException("Invalid model selection predecessor revision.");
                        var identityChanged = (previousSelection?.Model ?? _identity?.Model) != selection.Model;
                        var previousEpoch   = previousSelection?.ContinuationEpoch          ?? 0;
                        var previousStart   = previousSelection?.ReasoningStartMessageIndex ?? 0;
                        if (selection.Revision != (previousSelection?.Revision ?? 0) + 1 ||
                            (selection.MessageBoundary != _messages.Count &&
                             !(previousSelection is null && !identityChanged && selection.MessageBoundary == 0)) ||
                            (identityChanged && selection.ContinuationEpoch != previousEpoch + 1)                ||
                            (!identityChanged && selection.ContinuationEpoch != previousEpoch &&
                             selection.ContinuationEpoch                     != previousEpoch + 1) ||
                            selection.ReasoningStartMessageIndex !=
                            (selection.ContinuationEpoch == previousEpoch ? previousStart : _messages.Count) ||
                            (_messages.Count > 0 &&
                             ChatHistoryInvariants.LastCompleteBoundary(_messages.Select(ToChatMessage).ToArray()) !=
                             _messages.Count))
                            throw new SessionStoreException("Invalid model selection revision or protocol boundary.");
                        _modelSelections.Add(selection);
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
                        if (runIndex                             < 0
                                ? run.Run.ModelSelectionRevision != _modelSelections.LastOrDefault()?.Revision
                                : run.Run.ModelSelectionRevision != runs[runIndex].ModelSelectionRevision)
                            throw new SessionStoreException("A run has an unknown or changed model selection.");
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
                        var message = Read<SessionLogMessage>(record.Payload,
                                                              SessionLogJsonContext.Default.SessionLogMessage).Message;
                        if (message is null)
                            throw
                                new SessionStoreException($"Session '{sessionId}' contains a null canonical message.");
                        try
                        {
                            message.ValidateTextItems();
                        }
                        catch (InvalidDataException ex)
                        {
                            throw new
                                SessionStoreException($"Session '{sessionId}' contains invalid text items: {ex.Message}",
                                                      ex);
                        }

                        _messages.Add(message);
                        break;
                    case "run-amendment" :
                        var amendment = Read<RunAmendment>(record.Payload, SessionLogJsonContext.Default.RunAmendment);
                        amendment.Validate();
                        if (_turns.Where(turn => turn.Id == amendment.TurnId).SelectMany(turn => turn.Runs)
                                  .All(run => run.Id     != amendment.RunId))
                            throw new SessionStoreException("An amendment references an unknown run.");
                        if (_amendments.TryGetValue(amendment.Id, out var priorAmendment))
                        {
                            if (priorAmendment.CanonicalMessageIndex is not null ||
                                amendment.CanonicalMessageIndex is null          ||
                                priorAmendment with { CanonicalMessageIndex = amendment.CanonicalMessageIndex } !=
                                amendment)
                                throw
                                    new SessionStoreException("An amendment was rewritten or applied more than once.");
                        }
                        else if (amendment.CanonicalMessageIndex is not null)
                        {
                            throw new SessionStoreException("An applied amendment lacks its acceptance record.");
                        }

                        if (amendment.CanonicalMessageIndex is { } amendmentIndex &&
                            (amendmentIndex                    >= _messages.Count       ||
                             _messages[amendmentIndex].Role    != nameof(ChatRole.User) ||
                             _messages[amendmentIndex].Content != amendment.Text        ||
                             ChatHistoryInvariants.LastCompleteBoundary(_messages.Select(ToChatMessage).ToArray()) !=
                             _messages.Count))
                            throw new SessionStoreException("An amendment is not bound to its canonical user message.");
                        _amendments[amendment.Id] = amendment;
                        break;
                    case "work-item" :
                        var item = Read<SessionWorkItem>(record.Payload, SessionLogJsonContext.Default.SessionWorkItem);
                        item.Validate();
                        if (_turns.All(turn => turn.Id                             != item.SourceTurnId) ||
                            _turns.SelectMany(turn => turn.Runs).All(run => run.Id != item.UpdatedRunId) ||
                            item.EvidenceMessageIndexes.Any(index => index                 >= _messages.Count ||
                                                                     _messages[index].Role != nameof(ChatRole.Tool)))
                            throw new SessionStoreException("A work item has an unknown source or evidence reference.");
                        if (_workItems.TryGetValue(item.Id, out var priorItem))
                        {
                            if (priorItem.SourceTurnId != item.SourceTurnId || item.Revision != priorItem.Revision + 1)
                                throw new
                                    SessionStoreException("A work item does not continue its source and revision chain.");
                        }
                        else if (item.Revision != 1 || _workItems.Count >= 128)
                        {
                            throw new
                                SessionStoreException("Invalid initial work-item revision or too many work items.");
                        }

                        _workItems[item.Id] = item;
                        break;
                    case "project-instructions" :
                        ApplyProjectInstructions(Read<ProjectInstructionRecord>(record.Payload,
                                                                                    SessionLogJsonContext.Default
                                                                                       .ProjectInstructionRecord),
                                                 batch.Sequence, recordIndex,
                                                 batch.Records.Count);
                        break;
                    case "compaction-checkpoint" :
                        _checkpoint =
                            Read<SessionLogCheckpoint>(record.Payload,
                                                       SessionLogJsonContext.Default.SessionLogCheckpoint);
                        if (_checkpoint.ModelSelectionRevision is { } checkpointRevision &&
                            _modelSelections.All(selection => selection.Revision != checkpointRevision))
                            throw new
                                SessionStoreException("A compaction checkpoint references an unknown model selection.");
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
                    case "project-binding" :
                        string boundProjectId;
                        try
                        {
                            boundProjectId = SessionLogProjectBinding.Parse(record.Payload);
                        }
                        catch (Exception ex) when (ex is InvalidDataException or JsonException)
                        {
                            throw new
                                SessionStoreException($"Session '{sessionId}' contains an invalid project-binding record: {ex.Message}",
                                                      ex);
                        }

                        if (_projectId is not null)
                            throw new
                                SessionStoreException($"Session '{sessionId}' repeats or conflicts with its project binding '{_projectId}'; ownership is bound exactly once and never transferred.");
                        _projectId = boundProjectId;
                        break;
                    default :
                        throw new
                            SessionStoreException($"Session '{sessionId}' contains unknown required record kind '{record.Kind}'.");
                }
            }

            if (_expectedInstructionHistory is not null)
                throw new
                    SessionStoreException($"Session '{sessionId}' ends a batch without the history item bound to a project-instructions record.");
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
                Id                      = _identity.Id,
                WorkspaceRoot           = _identity.WorkspaceRoot,
                ProjectId               = _projectId,
                Model                   = _identity.Model,
                ModelSelections         = _modelSelections.ToArray(),
                CreatedUtc              = _identity.CreatedUtc,
                UpdatedUtc              = _updatedUtc,
                LastActivityUtc         = _lastActivityUtc,
                CommitSequence          = sequence,
                PendingInspectionReason = pendingReason,
                InspectionAcknowledgedUtc =
                    hasUnknownSideEffects || _state?.ExecutionIntent == true ? null : acknowledged,
                Turns      = _turns,
                Amendments = _amendments.Values.ToArray(),
                WorkItems  = _workItems.Values.ToArray(),
                Conversation = new PersistedConversation
                {
                    Messages                         = _messages,
                    State                            = checkpoint.State,
                    HasState                         = checkpoint.HasState,
                    ProjectInstructionRecords        = _projectInstructionRecords,
                    FoldStartMessageIndex            = checkpoint.FoldStartMessageIndex,
                    FoldEndMessageIndex              = checkpoint.FoldEndMessageIndex,
                    CheckpointModelSelectionRevision = checkpoint.ModelSelectionRevision
                }
            };
        }

        private void ApplyHistory(SessionHistoryItem item)
        {
            if (item.Kind is not (SessionHistoryKind.User or SessionHistoryKind.Process or
                                  SessionHistoryKind.Final or SessionHistoryKind.Tool or
                                  SessionHistoryKind.ProjectInstructions))
                throw new SessionStoreException($"Session '{sessionId}' contains unknown history kind '{item.Kind}'.");
            if (item.Kind == SessionHistoryKind.ProjectInstructions)
            {
                var expected = _expectedInstructionHistory;
                if (string.IsNullOrWhiteSpace(item.TurnId)     ||
                    string.IsNullOrWhiteSpace(item.RunId)      ||
                    string.IsNullOrWhiteSpace(item.Text)       ||
                    _turns.All(turn => turn.Id != item.TurnId) ||
                    _turns.Where(turn => turn.Id == item.TurnId).SelectMany(turn => turn.Runs)
                          .All(run => run.Id     != item.RunId)                                                  ||
                    expected is null                                                                             ||
                    item.Id       != expected.Value.Id                                                           ||
                    item.Sequence != expected.Value.Sequence                                                     ||
                    item.TurnId   != expected.Value.TurnId                                                       ||
                    item.RunId    != expected.Value.RunId                                                        ||
                    !item.Text.Contains($"revision {expected.Value.Revision}", StringComparison.Ordinal)         ||
                    !item.Text.Contains($"{expected.Value.DocumentCount} document(s)", StringComparison.Ordinal) ||
                    (expected.Value.DocumentCount > 0 &&
                     (!item.Text.Contains("source:", StringComparison.Ordinal) ||
                      !item.Text.Contains("(scope:", StringComparison.Ordinal))))
                    throw new
                        SessionStoreException($"Session '{sessionId}' contains an unbound project-instructions history item.");
                _expectedInstructionHistory = null;
                return;
            }

            if (item.Kind != SessionHistoryKind.Tool) return;
            if (string.IsNullOrWhiteSpace(item.RunId)  ||
                string.IsNullOrWhiteSpace(item.CallId) ||
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
            if (item.NotStartedReason is { } notStartedReason &&
                (callState != nameof(ToolCallExecutionState.KnownNotStarted) ||
                 string.IsNullOrWhiteSpace(notStartedReason)))
                throw new
                    SessionStoreException($"Session '{sessionId}' contains an invalid not-started reason for call '{item.CallId}'.");
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
            var callIndex = calls.FindIndex(call => call.InvocationId is not null && item.InvocationId is not null
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
                    NotStartedReason = item.NotStartedReason,
                    Approval         = item.Approval,
                    State            = callState
                });
                callIndex = calls.Count - 1;
            }
            else if (item.InvocationId is not null && calls[callIndex].InvocationId is not null)
            {
                var call = calls[callIndex];
                if (call.CallId != item.CallId                                        ||
                    (item.ToolName is not null   && call.ToolName   != item.ToolName) ||
                    (item.Capability is not null && call.Capability != item.Capability))
                    throw new
                        SessionStoreException($"Session '{sessionId}', run '{item.RunId}', invocation '{item.InvocationId}' has conflicting tool identity in terminal history.");
                if (requiresIntent && (!call.SideEffects || call.State != nameof(ToolCallExecutionState.UnknownResult)))
                    throw new
                        SessionStoreException($"Session '{sessionId}', run '{item.RunId}', invocation '{item.InvocationId}' has no pending side-effect execution intent.");
            }

            calls[callIndex] = calls[callIndex] with
            {
                State = callState,
                NotStartedReason = item.NotStartedReason
            };
            _completedToolFacts.Add(factKey, calls[callIndex]);
            var key = factKey;
            switch (callState)
            {
                case nameof(ToolCallExecutionState.UnknownResult) when calls[callIndex].SideEffects &&
                                                                       item.Sequence > _acknowledgedThroughSequence :
                    _unknownSideEffectCalls.Add(key);
                    break;
                case nameof(ToolCallExecutionState.ConfirmedResult) or
                     nameof(ToolCallExecutionState.KnownNotStarted) :
                    _unknownSideEffectCalls.Remove(key);
                    break;
            }

            runs[runIndex] = run with
            {
                ToolCalls = calls,
                NeedsInspection = calls.Any(call => call is
                {
                    SideEffects: true, State: nameof(ToolCallExecutionState.UnknownResult)
                })
            };
            _turns[turnIndex] = turn with { Runs = runs };
        }

        private void ApplyProjectInstructions(
            ProjectInstructionRecord instructionRecord, long sequence, int recordIndex, int recordCount)
        {
            if (instructionRecord is null                                             ||
                instructionRecord.Schema != "tinyharness.project-instructions/1"      ||
                instructionRecord.Action is not ("rules-applied" or "scopes-tracked") ||
                string.IsNullOrWhiteSpace(instructionRecord.Revision)                 ||
                string.IsNullOrWhiteSpace(instructionRecord.TurnId)                   ||
                string.IsNullOrWhiteSpace(instructionRecord.RunId)                    ||
                string.IsNullOrWhiteSpace(instructionRecord.Reason)                   ||
                instructionRecord.Snapshot is null)
                throw InvalidProjectInstruction("record has a missing or unsupported required field");

            ValidateProjectInstructionSnapshot(instructionRecord.Snapshot,
                                               instructionRecord.Action == "rules-applied");
            if (!string.Equals(instructionRecord.Revision, instructionRecord.Snapshot.Revision,
                               StringComparison.Ordinal))
                throw InvalidProjectInstruction("record revision does not match the snapshot revision");

            if (_turns.All(turn => turn.Id != instructionRecord.TurnId) ||
                _turns.Where(turn => turn.Id == instructionRecord.TurnId).SelectMany(turn => turn.Runs)
                      .All(run => run.Id     != instructionRecord.RunId))
                throw InvalidProjectInstruction("record references an unknown turn or run");

            try
            {
                var previousInstructionRecord = _projectInstructionRecords.LastOrDefault();
                if (instructionRecord.Action == "scopes-tracked")
                {
                    if (previousInstructionRecord is null || instructionRecord.Snapshot.Documents.Count != 0)
                        throw
                            InvalidProjectInstruction("scopes-tracked must reference an existing revision and omit document bodies");
                    instructionRecord = instructionRecord with
                    {
                        Snapshot = instructionRecord.Snapshot with
                        {
                            Documents = previousInstructionRecord.Snapshot.Documents
                        }
                    };
                }

                ValidateProjectInstructionSnapshot(instructionRecord.Snapshot, true);
                var prior = previousInstructionRecord;
                switch (instructionRecord.Action)
                {
                    case "rules-applied" :
                        if (prior is null
                                ? instructionRecord.PreviousRevision is not null
                                : !string.Equals(instructionRecord.PreviousRevision, prior.Revision,
                                                 StringComparison.Ordinal) ||
                                  string.Equals(instructionRecord.Revision, prior.Revision, StringComparison.Ordinal))
                            throw InvalidProjectInstruction("revision does not continue the previous revision chain");
                        if (instructionRecord.CanonicalMessageIndex is not { } messageIndex ||
                            messageIndex < 0                                                ||
                            messageIndex >= _messages.Count)
                            throw InvalidProjectInstruction("record has an invalid canonical message index");
                        var message  = _messages[messageIndex];
                        var rendered = ProjectInstructionRenderer.Render(instructionRecord.Snapshot);
                        var priorMessageIndex = _projectInstructionRecords
                                               .Where(existing => existing.Action == "rules-applied")
                                               .Select(existing => existing.CanonicalMessageIndex ?? -1)
                                               .DefaultIfEmpty(-1).Max();
                        if (message.Role    != nameof(ChatRole.User) ||
                            message.Content != rendered              ||
                            messageIndex    <= priorMessageIndex     ||
                            ChatHistoryInvariants.LastCompleteBoundary(_messages.Select(ToChatMessage).ToArray()) !=
                            _messages.Count)
                            throw
                                InvalidProjectInstruction("record is not bound to its exact canonical user message outside a tool group");
                        if (recordIndex + 1 >= recordCount)
                            throw
                                InvalidProjectInstruction("record cannot be bound to its history item in the same batch");
                        _expectedInstructionHistory =
                            ($"{sessionId}:{sequence:D20}:{recordIndex + 1:D4}", sequence,
                             instructionRecord.TurnId, instructionRecord.RunId, instructionRecord.Revision,
                             instructionRecord.Snapshot.Documents.Count);
                        break;
                    case "scopes-tracked" :
                        if (prior is null                                                                        ||
                            instructionRecord.CanonicalMessageIndex is not null                                  ||
                            !string.Equals(instructionRecord.Revision, prior.Revision, StringComparison.Ordinal) ||
                            !string.Equals(instructionRecord.PreviousRevision, prior.Revision,
                                           StringComparison.Ordinal)                                  ||
                            instructionRecord.Snapshot.RenderedHash    != prior.Snapshot.RenderedHash ||
                            instructionRecord.Snapshot.RendererVersion != prior.Snapshot.RendererVersion)
                            throw
                                InvalidProjectInstruction("scopes-tracked must reference the current revision without a message binding");
                        break;
                    default :
                        throw InvalidProjectInstruction($"contains unknown action '{instructionRecord.Action}'");
                }
            }
            catch (SessionStoreException)
            {
                throw;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or
                                             FormatException or OverflowException)
            {
                throw InvalidProjectInstruction($"contains invalid data: {ex.Message}", ex);
            }

            _projectInstructionRecords.Add(instructionRecord);
        }

        private void ValidateProjectInstructionSnapshot(ProjectInstructionSnapshot snapshot, bool validateRenderer)
        {
            if (snapshot is null)
                throw InvalidProjectInstruction("snapshot is null");
            if (snapshot.Documents is null || snapshot.ProbedDirectories is null)
                throw InvalidProjectInstruction("snapshot documents or probed-directories collection is null");
            if (string.IsNullOrWhiteSpace(snapshot.Revision)             ||
                string.IsNullOrWhiteSpace(snapshot.DiscoveryFingerprint) ||
                string.IsNullOrWhiteSpace(snapshot.RenderedHash)         ||
                string.IsNullOrWhiteSpace(snapshot.RendererVersion))
                throw
                    InvalidProjectInstruction("snapshot has a missing revision, fingerprint, rendered hash, or renderer version");

            foreach (var directory in snapshot.ProbedDirectories)
            {
                if (directory is null)
                    throw InvalidProjectInstruction("snapshot contains a null probed directory");
                ValidateRelativePath(directory, "probed directory", true);
            }

            foreach (var document in snapshot.Documents)
            {
                if (document is null)
                    throw InvalidProjectInstruction("snapshot contains a null document");
                if (string.IsNullOrWhiteSpace(document.SourcePath)     ||
                    string.IsNullOrWhiteSpace(document.ScopeDirectory) ||
                    document.Content is null                           ||
                    string.IsNullOrWhiteSpace(document.ContentHash)    ||
                    document.ByteCount < 0)
                    throw
                        InvalidProjectInstruction("snapshot document has a null or invalid source, scope, content, hash, or byte count");

                ValidateRelativePath(document.SourcePath, "source path");
                ValidateRelativePath(document.ScopeDirectory, "scope directory", true);
                if (!string.Equals(Path.GetFileName(document.SourcePath), "AGENTS.md",
                                   OperatingSystem.IsWindows()
                                       ? StringComparison.OrdinalIgnoreCase
                                       : StringComparison.Ordinal))
                    throw InvalidProjectInstruction($"source '{document.SourcePath}' does not name AGENTS.md");
                var sourceDirectory = Path
                                     .GetDirectoryName(document.SourcePath.Replace('/', Path.DirectorySeparatorChar))
                                    ?.Replace(Path.DirectorySeparatorChar, '/');
                if (string.IsNullOrEmpty(sourceDirectory)) sourceDirectory = ".";
                if (!string.Equals(sourceDirectory, document.ScopeDirectory, StringComparison.Ordinal))
                    throw
                        InvalidProjectInstruction($"source '{document.SourcePath}' does not match scope '{document.ScopeDirectory}'");

                if (document.ContentHash.Length != 64 ||
                    document.ContentHash.Any(character =>
                                                 character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')
                                                 and not (>= 'A' and <= 'F')))
                    throw
                        InvalidProjectInstruction($"source '{document.SourcePath}' has a malformed SHA-256 content hash");

                byte[] contentBytes;
                try
                {
                    contentBytes = new UTF8Encoding(false, true).GetBytes(document.Content);
                }
                catch (EncoderFallbackException ex)
                {
                    throw InvalidProjectInstruction($"source '{document.SourcePath}' contains invalid UTF-16 text", ex);
                }

                if (document.HasUtf8Bom)
                    contentBytes = [0xEF, 0xBB, 0xBF, .. contentBytes];
                var actualHash = Convert.ToHexString(SHA256.HashData(contentBytes));
                if (document.ByteCount != contentBytes.Length ||
                    !string.Equals(actualHash, document.ContentHash, StringComparison.OrdinalIgnoreCase))
                    throw
                        InvalidProjectInstruction($"source '{document.SourcePath}' does not match its raw-byte hash or byte count");
            }

            if (!validateRenderer) return;
            try
            {
                ProjectInstructionRenderer.Validate(snapshot);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or
                                             FormatException or OverflowException)
            {
                throw InvalidProjectInstruction($"snapshot validation failed: {ex.Message}", ex);
            }
        }

        private SessionStoreException InvalidProjectInstruction(string detail, Exception? innerException = null)
        {
            var message =
                $"Session '{sessionId}' log '{logPath}' contains invalid project-instructions data: {detail}.";
            return innerException is null
                ? new SessionStoreException(message)
                : new SessionStoreException(message, innerException);
        }

        private void ValidateRelativePath(string path, string field, bool allowRoot = false)
        {
            if (allowRoot && path == ".") return;
            if (string.IsNullOrWhiteSpace(path) ||
                path.StartsWith('/')            ||
                path.Contains('\\')             ||
                path.Contains(':')              ||
                path.Any(char.IsControl)        ||
                Path.IsPathRooted(path)         ||
                path.Split('/').Any(segment => segment is "" or "." or ".."))
                throw InvalidProjectInstruction($"{field} '{path}' is not a normalized workspace-relative path");
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
                if (call.State            != terminal.State       ||
                    call.CallId           != terminal.CallId      ||
                    call.ToolName         != terminal.ToolName    ||
                    call.Capability       != terminal.Capability  ||
                    call.SideEffects      != terminal.SideEffects ||
                    call.NotStartedReason != terminal.NotStartedReason)
                    throw new
                        SessionStoreException($"Session '{sessionId}', run '{incoming.Id}', invocation '{call.InvocationId}' contradicts its terminal history fact.");
            }

            foreach (var call in previous?.ToolCalls ?? [])
                if (call.InvocationId is not null              &&
                    !invocationIds.Contains(call.InvocationId) &&
                    _completedToolFacts.ContainsKey(UnknownCallKey(turnId, incoming.Id, call.InvocationId,
                                                                   call.CallId)))
                    throw new
                        SessionStoreException($"Session '{sessionId}', run '{incoming.Id}' omits completed invocation '{call.InvocationId}'.");
        }

        private T Read<T>(JsonElement payload, JsonTypeInfo<T> typeInfo) =>
            payload.Deserialize(typeInfo) ??
            throw new SessionStoreException($"Session '{sessionId}' has an empty {typeof(T).Name} payload.");

        private static ChatMessage ToChatMessage(PersistedMessage message) => new()
        {
            Role       = Enum.Parse<ChatRole>(message.Role),
            Content    = message.Content,
            TextItems  = message.TextItems?.ToArray(),
            ToolCallId = message.ToolCallId,
            Name       = message.Name,
            ToolCalls = message.ToolCalls?.Select(call => new ChatToolCall(call.Id, call.Name, call.Arguments))
                               .ToArray()
        };

        private static string UnknownCallKey(string turnId, string runId, string? invocationId, string callId) =>
            $"{turnId}\u001f{runId}\u001f{(invocationId is null ? "call:" + callId : "invocation:" + invocationId)}";
    }
}

[JsonSerializable(typeof(SessionLogBatch))]
[JsonSerializable(typeof(SessionLogRecord))]
[JsonSerializable(typeof(SessionLogIdentity))]
[JsonSerializable(typeof(SessionLogProjectBinding))]
[JsonSerializable(typeof(SessionLogTurn))]
[JsonSerializable(typeof(SessionLogRun))]
[JsonSerializable(typeof(SessionLogMessage))]
[JsonSerializable(typeof(SessionLogCheckpoint))]
[JsonSerializable(typeof(SessionLogSessionState))]
[JsonSerializable(typeof(RunAmendment))]
[JsonSerializable(typeof(SessionWorkItem))]
[JsonSerializable(typeof(ProjectInstructionSnapshot))]
[JsonSerializable(typeof(ProjectInstructionRecord))]
[JsonSerializable(typeof(PersistedRun))]
[JsonSerializable(typeof(PersistedTurn))]
[JsonSerializable(typeof(PersistedMessage))]
[JsonSerializable(typeof(AssistantTextItem))]
[JsonSerializable(typeof(PersistedToolCall))]
[JsonSerializable(typeof(PersistedToolCallRecord))]
[JsonSerializable(typeof(PersistedReasoning))]
[JsonSerializable(typeof(SessionModelIdentity))]
[JsonSerializable(typeof(SessionModelSelection))]
[JsonSerializable(typeof(ChatApiKind))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             UseStringEnumConverter = true,
                             GenerationMode = JsonSourceGenerationMode.Metadata |
                                              JsonSourceGenerationMode.Serialization)]
public sealed partial class SessionLogJsonContext : JsonSerializerContext;

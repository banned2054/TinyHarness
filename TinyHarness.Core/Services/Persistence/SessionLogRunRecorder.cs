using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>将脱敏运行审计和保留原文的本地展示历史追加到所属会话日志。</para>
///     <para>Appends redacted run audits and verbatim local display history to the session log.</para>
/// </summary>
public sealed class SessionLogRunRecorder : IRunRecorder
{
    private const    int MaxArtifactBytes = 2 * 1024 * 1024;
    private const    int MaxArtifactsBytes = 4 * 1024 * 1024;
    private readonly Dictionary<string, ChatToolCall> _assistantCalls = new(StringComparer.Ordinal);
    private readonly Func<CancellationToken, Task> _commitInitialContext;
    private readonly HashSet<string> _completedInvocationIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _currentInvocationIds = new(StringComparer.Ordinal);
    private readonly Func<SessionHistoryItem, CancellationToken, ValueTask>? _historyItemCommitted;
    private readonly Action<Exception>? _historyPublishError;
    private readonly Dictionary<string, PendingTool> _pendingTools = new(StringComparer.Ordinal);

    private readonly Dictionary<ToolPreparation, string> _preparationInvocationIds =
        new(ReferenceEqualityComparer.Instance);

    private readonly SecretRedactor      _redactor;
    private readonly ISessionStoreWriter _writer;

    private int _displayFailureCount;
    private int _nextInvocationSequence;

    /// <summary>
    ///     <para>创建绑定到单个会话、回合和运行的 recorder。</para>
    ///     <para>Creates a recorder bound to one session, turn, and run.</para>
    /// </summary>
    /// <param name="writer">
    ///     <para>已由宿主持有的共享会话日志 writer。</para>
    ///     <para>The shared session-log writer owned by the host.</para>
    /// </param>
    /// <param name="sessionId">
    ///     <para>writer 所属会话标识。</para>
    ///     <para>The session identity owned by the writer.</para>
    /// </param>
    /// <param name="turnId">
    ///     <para>当前用户回合标识。</para>
    ///     <para>The current user-turn identity.</para>
    /// </param>
    /// <param name="runId">
    ///     <para>当前运行标识。</para>
    ///     <para>The current run identity.</para>
    /// </param>
    /// <param name="commitInitialContext">
    ///     <para>在运行审计前提交已包含 identity 与用户输入的 canonical 上下文。</para>
    ///     <para>Commits canonical context containing identity and user input before run audit begins.</para>
    /// </param>
    /// <param name="knownSecrets">
    ///     <para>用于审计和诊断的已知密钥；协议消息和本地历史不经过此脱敏器。</para>
    ///     <para>Known secrets for audits and diagnostics; protocol messages and local history bypass this redactor.</para>
    /// </param>
    /// <param name="historyItemCommitted">
    ///     <para>日志确认追加后接收 writer 绑定的同一展示条目。</para>
    ///     <para>Receives the same writer-bound display item after the journal confirms the append.</para>
    /// </param>
    public SessionLogRunRecorder(
        ISessionStoreWriter                                     writer, string sessionId, string turnId, string runId,
        Func<CancellationToken, Task>                           commitInitialContext,
        IReadOnlyDictionary<string, string>?                    knownSecrets         = null,
        Func<SessionHistoryItem, CancellationToken, ValueTask>? historyItemCommitted = null,
        Action<Exception>?                                      historyPublishError  = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(commitInitialContext);
        _writer               = writer;
        SessionId             = sessionId;
        TurnId                = turnId;
        RunId                 = runId;
        _commitInitialContext = commitInitialContext;
        _redactor             = new SecretRedactor(knownSecrets);
        _historyItemCommitted = historyItemCommitted;
        _historyPublishError  = historyPublishError;
    }

    public string  SessionId      { get; }
    public string  TurnId         { get; }
    public string  RunId          { get; }
    public string? DisplayWarning { get; private set; }

    public async Task StartAsync(string systemPrompt, string userInput, CancellationToken cancellationToken)
    {
        await _commitInitialContext(cancellationToken).ConfigureAwait(false);
        var audit = new AuditRecord
        {
            RunId        = RunId,
            Kind         = "run.started",
            SystemPrompt = _redactor.Redact(systemPrompt),
            UserInput    = _redactor.Redact(userInput)
        };
        var userItem = NewHistoryItem(SessionHistoryKind.User, userInput);
        var appended = await AppendBatchAsync([AuditRecord(audit), HistoryRecord(userItem)], cancellationToken)
           .ConfigureAwait(false);
        await PublishHistoryItemsAsync(appended, cancellationToken).ConfigureAwait(false);
    }

    public Task RecordPreparedAsync(ToolPreparation preparation, CancellationToken cancellationToken)
    {
        var invocationId = GetCurrentInvocationId(preparation.CallId);
        EnsureNotCompleted(invocationId);
        var rawCall = _assistantCalls.GetValueOrDefault(invocationId);
        _pendingTools[invocationId]            = new PendingTool(rawCall, preparation, null, null);
        _preparationInvocationIds[preparation] = invocationId;
        return AppendAuditAsync(new AuditRecord
        {
            RunId           = RunId,
            Kind            = "tool.prepared",
            InvocationId    = invocationId,
            ToolName        = _redactor.Redact(preparation.ToolName),
            CallId          = _redactor.Redact(preparation.CallId),
            Capability      = _redactor.Redact(preparation.Capability),
            Summary         = _redactor.Redact(preparation.Summary),
            TargetPaths     = RedactPaths(preparation.TargetPaths),
            ExecutionPolicy = _redactor.RedactNullable(preparation.ExecutionPolicy)
        }, cancellationToken);
    }

    public async Task RecordPermissionAsync(ToolPreparation   preparation, PermissionDecision decision, string outcome,
                                            CancellationToken cancellationToken)
    {
        var invocationId = GetInvocationId(preparation);
        EnsureNotCompleted(invocationId);
        PendingTool? pending = null;
        if (_pendingTools.TryGetValue(invocationId, out var existingPending))
        {
            pending = existingPending with
            {
                Decision = decision,
                Outcome = outcome
            };
            _pendingTools[invocationId] = pending;
        }

        var audit = new AuditRecord
        {
            RunId           = RunId,
            Kind            = "tool.permission",
            InvocationId    = invocationId,
            ToolName        = _redactor.Redact(preparation.ToolName),
            CallId          = _redactor.Redact(preparation.CallId),
            Capability      = _redactor.Redact(preparation.Capability),
            Summary         = _redactor.Redact(preparation.Summary),
            TargetPaths     = RedactPaths(preparation.TargetPaths),
            Decision        = _redactor.Redact(decision.ToString()),
            Outcome         = _redactor.Redact(outcome),
            ExecutionPolicy = _redactor.RedactNullable(preparation.ExecutionPolicy)
        };
        var isDenied = decision == PermissionDecision.Deny                                         ||
                       outcome.StartsWith("Permission denied", StringComparison.OrdinalIgnoreCase) ||
                       outcome.StartsWith("Blocked pending inspection", StringComparison.OrdinalIgnoreCase);
        if (!isDenied || pending is null)
        {
            await AppendAuditAsync(audit, cancellationToken).ConfigureAwait(false);
            return;
        }

        var item = await CreateToolHistoryItemAsync(pending, invocationId, null,
                                                    nameof(ToolCallExecutionState.KnownNotStarted), false,
                                                    cancellationToken).ConfigureAwait(false);
        var appended = await AppendBatchAsync([AuditRecord(audit), HistoryRecord(item)], cancellationToken)
           .ConfigureAwait(false);
        _completedInvocationIds.Add(invocationId);
        _pendingTools.Remove(invocationId);
        _assistantCalls.Remove(invocationId);
        await PublishHistoryItemsAsync(appended, cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordResultAsync(
        ToolPreparation preparation, ToolResult result, CancellationToken cancellationToken)
    {
        var invocationId = GetInvocationId(preparation);
        EnsureNotCompleted(invocationId);

        var pending = _pendingTools.GetValueOrDefault(invocationId)
                   ?? new PendingTool(_assistantCalls.GetValueOrDefault(invocationId), preparation, null, null);
        var executionUnknown = result.ExecutionIncomplete ||
                               result.PatchChanges?.Any(change => change.State == PatchChange.UnknownState) == true;
        var executionState = executionUnknown
            ? nameof(ToolCallExecutionState.UnknownResult)
            : nameof(ToolCallExecutionState.ConfirmedResult);
        var audit = new AuditRecord
        {
            RunId           = RunId,
            Kind            = "tool.result",
            InvocationId    = invocationId,
            Status          = executionState,
            ToolName        = _redactor.Redact(preparation.ToolName),
            CallId          = _redactor.Redact(preparation.CallId),
            Succeeded       = result.Succeeded,
            ExitCode        = result.ExitCode,
            TimedOut        = result.TimedOut,
            OutputTruncated = result.OutputTruncated,
            Outcome         = result.Succeeded ? "tool completed" : "tool failed",
            ExecutionPolicy = _redactor.RedactNullable(preparation.ExecutionPolicy)
        };
        var item = await CreateToolHistoryItemAsync(pending, invocationId, result,
                                                    executionState, false, cancellationToken).ConfigureAwait(false);
        var appended = await AppendBatchAsync([AuditRecord(audit), HistoryRecord(item)], cancellationToken)
           .ConfigureAwait(false);
        _completedInvocationIds.Add(invocationId);
        _pendingTools.Remove(invocationId);
        _assistantCalls.Remove(invocationId);
        await PublishHistoryItemsAsync(appended, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>在进入下一轮模型请求前保存已知未启动的调用，并发布日志确认后的同一历史条目。</para>
    ///     <para>Saves a known-not-started invocation before the next model request and publishes its confirmed history item.</para>
    /// </summary>
    public async Task RecordNotStartedAsync(ToolCallRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.State != ToolCallExecutionState.KnownNotStarted)
            throw new ArgumentException("Only known-not-started invocations can be recorded through this callback.",
                                        nameof(record));
        var invocationId = record.InvocationId ?? GetCurrentInvocationId(record.CallId);
        EnsureNotCompleted(invocationId);
        var pending = _pendingTools.GetValueOrDefault(invocationId);
        if (pending is null)
        {
            if (!_assistantCalls.TryGetValue(invocationId, out var call))
                throw new InvalidOperationException("The invocation has no recorded assistant tool call.");
            pending = new PendingTool(call, null, null, record.Approval);
        }

        var item = await CreateToolHistoryItemAsync(pending, invocationId, null,
                                                    nameof(ToolCallExecutionState.KnownNotStarted), false,
                                                    cancellationToken, record).ConfigureAwait(false);
        var audit = new AuditRecord
        {
            RunId = RunId,
            Kind = "tool.not-started",
            InvocationId = invocationId,
            CallId = _redactor.Redact(record.CallId),
            ToolName = _redactor.Redact(record.ToolName),
            Capability = _redactor.RedactNullable(record.Capability),
            Summary = _redactor.Redact(record.Summary),
            TargetPaths = pending.Preparation is null ? null : RedactPaths(pending.Preparation.TargetPaths),
            ExecutionPolicy = _redactor.RedactNullable(pending.Preparation?.ExecutionPolicy),
            Status = nameof(ToolCallExecutionState.KnownNotStarted),
            Outcome = _redactor.RedactNullable(record.NotStartedReason ?? record.PreparationError ?? record.Approval)
        };
        var appended = await AppendBatchAsync([AuditRecord(audit), HistoryRecord(item)], cancellationToken)
           .ConfigureAwait(false);
        _completedInvocationIds.Add(invocationId);
        _pendingTools.Remove(invocationId);
        _assistantCalls.Remove(invocationId);
        if (_currentInvocationIds.GetValueOrDefault(record.CallId) == invocationId)
            _currentInvocationIds.Remove(record.CallId);
        await PublishHistoryItemsAsync(appended, cancellationToken).ConfigureAwait(false);
    }

    public Task RecordCompactionAsync(ContextChange change, CancellationToken cancellationToken) =>
        AppendAuditAsync(new AuditRecord
        {
            RunId        = RunId,
            Kind         = "context.compacted",
            BeforeTokens = change.BeforeTokens,
            AfterTokens  = change.AfterTokens
        }, cancellationToken);

    /// <summary>
    ///     <para>只记录压缩拒绝的固定原因，不保存内部摘要或异常正文。</para>
    ///     <para>Records only the fixed compaction failure reason, excluding internal summaries and exception text.</para>
    /// </summary>
    public Task RecordCompactionFailedAsync(CompactionFailureReason reason, CancellationToken cancellationToken) =>
        AppendAuditAsync(new AuditRecord
                             { RunId = RunId, Kind = "context.compaction-failed", Outcome = reason.ToString() },
                         cancellationToken);

    public Task RecordModelUsageAsync(
        int? inputTokens, int? outputTokens, string? finishReason, CancellationToken cancellationToken) =>
        AppendAuditAsync(new AuditRecord
        {
            RunId        = RunId,
            Kind         = "model_usage",
            InputTokens  = inputTokens,
            OutputTokens = outputTokens,
            FinishReason = _redactor.RedactNullable(finishReason)
        }, cancellationToken);

    /// <summary>
    ///     <para>将请求观测追加到同一审计日志，取消或失败也保留已观察到的阶段。</para>
    ///     <para>Appends request observations to the same audit journal, retaining observed phases on cancellation or failure.</para>
    /// </summary>
    public Task RecordModelRequestAsync(ModelRequestProgress progress, CancellationToken cancellationToken) =>
        AppendAuditAsync(new AuditRecord
        {
            RunId        = RunId,
            Kind         = "model.request",
            ModelRequest = progress
        }, cancellationToken);

    public async Task RecordAssistantAsync(ChatMessage assistantMessage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assistantMessage);
        if (assistantMessage.Role != ChatRole.Assistant)
            throw new ArgumentException("Only completed assistant messages can be recorded as history.",
                                        nameof(assistantMessage));

        if (assistantMessage.ToolCalls is { } toolCalls)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var call in toolCalls)
                if (string.IsNullOrWhiteSpace(call.Id) || !ids.Add(call.Id))
                    throw new
                        InvalidOperationException("Assistant tool call ids must be nonempty and unique within one round.");
        }

        var hasTools  = assistantMessage.ToolCalls is { Count: > 0 };
        var texts     = assistantMessage.DisplayTextItems;
        var records   = new List<SessionLogRecord>();
        var reasoning = ReasoningText(assistantMessage.Reasoning);
        foreach (var text in texts)
        {
            var kind = text.IsAnswer(hasTools) ? SessionHistoryKind.Final : SessionHistoryKind.Process;
            var item = NewHistoryItem(kind, text.Text, reasoning);
            records.Add(HistoryRecord(item));
            reasoning = null;
        }

        var appended = await AppendBatchAsync(records, cancellationToken).ConfigureAwait(false);
        _currentInvocationIds.Clear();
        if (assistantMessage.ToolCalls is { } recordedCalls)
            foreach (var call in recordedCalls)
            {
                var invocationId = $"call-{checked(++_nextInvocationSequence):D8}";
                _currentInvocationIds.Add(call.Id, invocationId);
                _assistantCalls.Add(invocationId, call);
            }

        await PublishHistoryItemsAsync(appended, cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteAsync(AgentResult       result, IReadOnlyList<ChatMessage> messages,
                                    CancellationToken cancellationToken)
    {
        var records = new List<SessionLogRecord>
        {
            AuditRecord(new AuditRecord
            {
                RunId   = RunId,
                Kind    = "run.completed",
                Status  = _redactor.Redact(result.Status.ToString()),
                Outcome = _redactor.RedactNullable(result.Error)
            })
        };
        foreach (var (invocationId, pending) in _pendingTools)
        {
            var execution = FindExecution(result, invocationId, pending.Call?.Id ?? pending.Preparation!.CallId);
            var item = await CreateToolHistoryItemAsync(pending, invocationId, null,
                                                        execution?.State.ToString() ??
                                                        nameof(ToolCallExecutionState.UnknownResult), true,
                                                        cancellationToken, execution).ConfigureAwait(false);
            records.Add(HistoryRecord(item));
        }

        foreach (var (invocationId, call) in _assistantCalls)
        {
            if (_pendingTools.ContainsKey(invocationId) || _completedInvocationIds.Contains(invocationId)) continue;
            var execution = FindExecution(result, invocationId, call.Id);
            var pending   = new PendingTool(call, null, null, execution?.Approval);
            var item = await CreateToolHistoryItemAsync(pending, invocationId, null,
                                                        execution?.State.ToString() ??
                                                        nameof(ToolCallExecutionState.KnownNotStarted), false,
                                                        cancellationToken, execution).ConfigureAwait(false);
            records.Add(HistoryRecord(item));
        }

        var appended = await AppendBatchAsync(records, cancellationToken).ConfigureAwait(false);
        foreach (var invocationId in _pendingTools.Keys.Concat(_assistantCalls.Keys))
            _completedInvocationIds.Add(invocationId);
        _pendingTools.Clear();
        _assistantCalls.Clear();
        _currentInvocationIds.Clear();
        await PublishHistoryItemsAsync(appended, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>按到达顺序拼接助手的可读推理文本；不展示 opaque 载荷。</para>
    ///     <para>Joins readable assistant reasoning in arrival order without exposing opaque payloads.</para>
    /// </summary>
    private string? ReasoningText(IReadOnlyList<ReasoningContent>? entries)
    {
        if (entries is not { Count: > 0 }) return null;

        string? joined = null;
        foreach (var entry in entries)
        {
            var text = entry.Text;
            if (string.IsNullOrEmpty(text)) continue;
            joined = joined is null ? text : $"{joined}\n{text}";
        }

        return joined;
    }

    private string GetCurrentInvocationId(string callId) =>
        _currentInvocationIds.TryGetValue(callId, out var invocationId)
            ? invocationId
            : throw new InvalidOperationException("The tool call does not belong to the current assistant round.");

    private string GetInvocationId(ToolPreparation preparation) =>
        _preparationInvocationIds.TryGetValue(preparation, out var invocationId)
            ? invocationId
            : GetCurrentInvocationId(preparation.CallId);

    private void EnsureNotCompleted(string invocationId)
    {
        if (_completedInvocationIds.Contains(invocationId))
            throw new InvalidOperationException($"Tool invocation '{invocationId}' already has a persisted result.");
    }

    private static ToolCallRecord? FindExecution(AgentResult result, string invocationId, string callId) =>
        result.ToolCalls?.FirstOrDefault(call => call.InvocationId == invocationId) ??
        result.ToolCalls?.FirstOrDefault(call => call.InvocationId is null && call.CallId == callId);

    private async Task AppendAuditAsync(AuditRecord audit, CancellationToken cancellationToken) =>
        _ = await AppendBatchAsync([AuditRecord(audit)], cancellationToken).ConfigureAwait(false);

    private Task<IReadOnlyList<SessionLogRecord>> AppendBatchAsync(
        IReadOnlyList<SessionLogRecord> records, CancellationToken cancellationToken) =>
        _writer.AppendRecordsAsync(records, cancellationToken);

    private async Task PublishHistoryItemsAsync(
        IReadOnlyList<SessionLogRecord> records, CancellationToken cancellationToken)
    {
        if (_historyItemCommitted is null) return;
        foreach (var record in records)
        {
            if (record.Kind != "history") continue;
            var item = record.Payload.Deserialize(SessionHistoryJsonContext.Default.SessionHistoryItem)
                    ?? throw new InvalidDataException("The session writer returned an empty history item.");
            try
            {
                await _historyItemCommitted(item, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ReportHistoryPublishError(ex);
            }
        }
    }

    internal void ReportHistoryPublishError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var safeMessage                           = _redactor.Redact(exception.Message);
        if (safeMessage.Length > 512) safeMessage = safeMessage[..512];
        _displayFailureCount++;
        DisplayWarning = $"Live history display failed for {_displayFailureCount} item(s): {safeMessage}";
        var safeException = new InvalidOperationException(safeMessage);
        try
        {
            if (_historyPublishError is not null) _historyPublishError(safeException);
            else Trace.TraceError("Session history display callback failed: {0}", safeMessage);
        }
        catch (Exception reportError)
        {
            Trace.TraceError("Session history display error reporter failed: {0}",
                             _redactor.Redact(reportError.Message));
        }
    }

    private async Task<SessionHistoryItem> CreateToolHistoryItemAsync(PendingTool       pending, string invocationId,
                                                                      ToolResult?       result,  string state,
                                                                      bool              outputIncomplete,
                                                                      CancellationToken cancellationToken,
                                                                      ToolCallRecord?   execution = null)
    {
        var preparation    = pending.Preparation;
        var remainingBytes = MaxArtifactsBytes;
        var stdout         = result?.Stdout;
        var stderr         = result?.Stderr;
        var outputWarnings = new List<string>();
        if (result?.ExecutionIncomplete == true)
            outputWarnings.Add("tool execution is incomplete; retained output does not confirm overall completion");
        if (result?.StdoutArtifactPath is { } stdoutPath)
        {
            var artifact = await ReadArtifactAsync(stdoutPath, remainingBytes, cancellationToken).ConfigureAwait(false);
            remainingBytes -= artifact.BytesRead;
            if (artifact.Text is not null) stdout = artifact.Text;
            if (artifact.Warning is not null) outputWarnings.Add("stdout: " + artifact.Warning);
        }

        if (result?.StderrArtifactPath is { } stderrPath)
        {
            var artifact = await ReadArtifactAsync(stderrPath, remainingBytes, cancellationToken).ConfigureAwait(false);
            if (artifact.Text is not null) stderr = artifact.Text;
            if (artifact.Warning is not null) outputWarnings.Add("stderr: " + artifact.Warning);
        }

        var outputWarning = outputWarnings.Count == 0 ? null : string.Join("; ", outputWarnings);
        return NewHistoryItem(SessionHistoryKind.Tool,
                              result?.Content,
                              callId : preparation?.CallId ?? pending.Call?.Id ?? execution?.CallId ?? string.Empty,
                              invocationId : invocationId,
                              toolName : preparation?.ToolName ?? pending.Call?.FunctionName ?? string.Empty,
                              argumentsJson : pending.Call?.ArgumentsJson,
                              capability : preparation?.Capability ?? execution?.Capability,
                              summary : preparation?.Summary       ?? execution?.Summary ?? string.Empty,
                              targetPaths : preparation?.TargetPaths,
                              executionPolicy : preparation?.ExecutionPolicy,
                              approval : pending.Outcome ?? execution?.Approval,
                              state : state,
                              preparationError : _redactor.RedactNullable(execution?.PreparationError),
                              notStartedReason : _redactor.RedactNullable(execution?.NotStartedReason),
                              succeeded : result?.Succeeded,
                              exitCode : result?.ExitCode,
                              timedOut : result?.TimedOut               ?? false,
                              outputTruncated : result?.OutputTruncated ?? false,
                              stdout : stdout,
                              stderr : stderr,
                              patchChanges : result?.PatchChanges,
                              outputIncomplete : outputIncomplete || outputWarning is not null,
                              outputWarning : outputWarning);
    }

    private static async Task<ArtifactReadResult> ReadArtifactAsync(
        string path, int remainingBytes, CancellationToken cancellationToken)
    {
        var limit = Math.Min(MaxArtifactBytes, remainingBytes);
        if (limit <= 0) return new ArtifactReadResult(null, "aggregate 4 MiB artifact budget exceeded", 0);

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                    16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var buffer = new MemoryStream(Math.Min(limit, (int)Math.Min(stream.Length, limit)));
            var       chunk  = new byte[16 * 1024];
            while (buffer.Length <= limit)
            {
                var requested = (int)Math.Min(chunk.Length, limit + 1L - buffer.Length);
                if (requested <= 0) break;
                var read = await stream.ReadAsync(chunk.AsMemory(0, requested), cancellationToken)
                                       .ConfigureAwait(false);
                if (read == 0) break;
                buffer.Write(chunk, 0, read);
            }

            if (buffer.Length > limit)
                return new ArtifactReadResult(null,
                                              stream.Length > MaxArtifactBytes
                                                  ? "artifact exceeds 2 MiB limit"
                                                  : "aggregate 4 MiB artifact budget exceeded",
                                              limit);

            return new ArtifactReadResult(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), null,
                                          (int)buffer.Length);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                         NotSupportedException)
        {
            return new ArtifactReadResult(null, ex is FileNotFoundException or DirectoryNotFoundException
                                              ? "artifact is missing"
                                              : "artifact could not be read", 0);
        }
    }

    private SessionHistoryItem NewHistoryItem(string kind, string? text = null, string? reasoning = null,
                                              string? callId = null,
                                              string? toolName = null, string? argumentsJson = null,
                                              string? capability = null, string? summary = null,
                                              IReadOnlyList<string>? targetPaths = null, string? executionPolicy = null,
                                              string? approval = null,
                                              string? state = null, bool? succeeded = null, int? exitCode = null,
                                              bool timedOut = false,
                                              bool outputTruncated = false, string? stdout = null,
                                              string? stderr = null,
                                              IReadOnlyList<PatchChange>? patchChanges = null,
                                              bool outputIncomplete = false,
                                              string? outputWarning = null, string? preparationError = null,
                                              string? invocationId = null, string? notStartedReason = null) => new()
    {
        Id               = string.Empty,
        Sequence         = 0,
        SessionId        = SessionId,
        TurnId           = TurnId,
        RunId            = RunId,
        Kind             = kind,
        Text             = text,
        Reasoning        = reasoning,
        CallId           = callId,
        InvocationId     = invocationId,
        ToolName         = toolName,
        ArgumentsJson    = argumentsJson,
        Capability       = capability,
        Summary          = summary,
        TargetPaths      = targetPaths,
        ExecutionPolicy  = executionPolicy,
        Approval         = approval,
        PreparationError = preparationError,
        NotStartedReason = notStartedReason,
        State            = state,
        Succeeded        = succeeded,
        ExitCode         = exitCode,
        TimedOut         = timedOut,
        OutputTruncated  = outputTruncated,
        Stdout           = stdout,
        Stderr           = stderr,
        PatchChanges     = patchChanges,
        OutputIncomplete = outputIncomplete,
        OutputWarning    = outputWarning
    };

    private SessionLogRecord AuditRecord(AuditRecord audit) =>
        new(string.Empty, "audit",
            JsonSerializer.SerializeToElement(audit, PersistenceJsonContext.Default.AuditRecord));

    private static SessionLogRecord HistoryRecord(SessionHistoryItem item) =>
        new(string.Empty, "history",
            JsonSerializer.SerializeToElement(item, SessionHistoryJsonContext.Default.SessionHistoryItem));

    private IReadOnlyList<string> RedactPaths(IReadOnlyList<string> paths) =>
        paths.Select(_redactor.Redact).ToArray();

    private sealed record ArtifactReadResult(string? Text, string? Warning, int BytesRead);

    private sealed record PendingTool(
        ChatToolCall?       Call,
        ToolPreparation?    Preparation,
        PermissionDecision? Decision,
        string?             Outcome);
}

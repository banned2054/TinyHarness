using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Core.Services.Session;

/// <summary>
///     <para>
///         协调固定身份与系统提示的会话生命周期，共享跨回合上下文；每回合创建独立运行与循环，
///         同一时刻仅允许一个活动运行。
///     </para>
///     <para>
///         Coordinates a session with fixed identity and system prompt, sharing context across turns;
///         each turn creates a separate run and loop, with only one active run at a time.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         终态后结算中断并提交快照；未知副作用触发 NeedsInspection，
///         仅用户检查确认可解除，模型输出不能建立或解除阻断。
///     </para>
///     <para>
///         Settles interruptions and commits snapshots after terminal results; unknown side effects
///         trigger NeedsInspection, cleared only by user inspection and acknowledgement, never model output.
///     </para>
///     <para>
///         不主动存储凭据或恢复可执行授权；v3 canonical 历史可能含秘密并原样落盘，检查视图另行脱敏。
///         恢复时沿用已校验的会话、上下文与写入端，在原身份和提交序号上继续。
///     </para>
///     <para>
///         Credentials are not proactively stored and executable authorization is not restored;
///         v3 canonical history may contain secrets and is persisted verbatim, with separately redacted inspection views.
///         Resume continues the validated session, context and writer under the existing identity and commit sequence.
///     </para>
/// </remarks>
public sealed class ChatSessionHost : IDisposable
{
    private readonly IApprovalProvider?          _approver;
    private readonly ConversationContext         _context;
    private readonly IHostEventSink?             _events;
    private readonly IChatCompletionClient       _modelClient;
    private readonly AgentOptions                _options;
    private readonly PermissionEngine?           _permissions;
    private readonly Func<string, IRunRecorder>? _recorderFactory;
    private readonly ISessionStoreWriter?        _sessionWriter;
    private readonly string                      _systemPrompt;
    private readonly ToolRegistry                _tools;

    private int _activeRuns;

    /// <summary>
    ///     <para>当前 NeedsInspection 阻断原因；null 表示未阻断，仅用户检查确认可清除。</para>
    ///     <para>
    ///         The current NeedsInspection reason; null while unblocked, cleared only by user
    ///         inspection and acknowledgement.
    ///     </para>
    /// </summary>
    private string? _inspectionNote;

    public ChatSessionHost(string                             workspaceRoot,
                           SessionModelIdentity               model,
                           AgentOptions                       options,
                           IChatCompletionClient              modelClient,
                           ToolRegistry                       tools,
                           string                             systemPrompt,
                           PermissionEngine?                  permissions          = null,
                           IApprovalProvider?                 approver             = null,
                           IHostEventSink?                    events               = null,
                           Func<string, IRunRecorder>?        recorderFactory      = null,
                           Func<string, ISessionStoreWriter>? sessionWriterFactory = null)
        : this(new ChatSession(NewId(), workspaceRoot, model), ContextFor(options), options, modelClient, tools,
               systemPrompt, permissions, approver, events, recorderFactory, sessionWriterFactory, null)
    {
    }

    /// <summary>
    ///     <para>接管 <see cref="SessionResume" /> 重建的会话、上下文及已打开的写入端。</para>
    ///     <para>Takes over the session and context rebuilt by <see cref="SessionResume" /> and an open writer.</para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         写入端须已持锁并续种提交序号；构造失败时仍由调用方释放。
    ///         未确认的检查原因在启动时阻断非只读能力，仅 <see cref="AcknowledgeInspectionAsync" /> 可解除。
    ///     </para>
    ///     <para>
    ///         The writer must hold the lock and have its commit sequence seeded; the caller disposes it
    ///         if construction fails. Unacknowledged inspection reasons block non-read-only capabilities at startup,
    ///         cleared only by <see cref="AcknowledgeInspectionAsync" />.
    ///     </para>
    /// </remarks>
    public ChatSessionHost(ChatSession                 session,
                           ConversationContext         context,
                           AgentOptions                options,
                           IChatCompletionClient       modelClient,
                           ToolRegistry                tools,
                           string                      systemPrompt,
                           PermissionEngine?           permissions     = null,
                           IApprovalProvider?          approver        = null,
                           IHostEventSink?             events          = null,
                           Func<string, IRunRecorder>? recorderFactory = null,
                           ISessionStoreWriter?        sessionWriter   = null,
                           string?                     needsInspection = null)
        : this(session, context, options, modelClient, tools, systemPrompt, permissions, approver, events,
               recorderFactory, sessionWriter is null ? null : _ => sessionWriter, needsInspection)
    {
    }

    /// <summary>
    ///     <para>校验固定模型与系统提示，建立必要的检查阻断，最后打开或接管写入端；失败即拒绝启动。</para>
    ///     <para>
    ///         Validates the fixed model and system prompt, establishes required inspection blocking,
    ///         and opens or takes over the writer last; any failure rejects startup.
    ///     </para>
    /// </summary>
    private ChatSessionHost(ChatSession                        session,
                            ConversationContext                context,
                            AgentOptions                       options,
                            IChatCompletionClient              modelClient,
                            ToolRegistry                       tools,
                            string                             systemPrompt,
                            PermissionEngine?                  permissions,
                            IApprovalProvider?                 approver,
                            IHostEventSink?                    events,
                            Func<string, IRunRecorder>?        recorderFactory,
                            Func<string, ISessionStoreWriter>? sessionWriterFactory,
                            string?                            needsInspection)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(modelClient);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        if (!string.Equals(options.Model, session.Model.ModelId, StringComparison.Ordinal))
            throw new
                ArgumentException($"AgentOptions.Model '{options.Model}' does not match the session model identity '{session.Model.ModelId}'.",
                                  nameof(options));

        if (context.Messages.Count > 0)
        {
            var head = context.Messages[0];
            if (head.Role != ChatRole.System || !string.Equals(head.Content, systemPrompt, StringComparison.Ordinal))
                throw new
                    ArgumentException("The restored conversation's system message differs from this host's system prompt; " +
                                      "a resumed session requires identical instructions.", nameof(context));
        }

        Session          = session;
        _options         = options;
        _modelClient     = modelClient;
        _tools           = tools;
        _systemPrompt    = systemPrompt;
        _permissions     = permissions;
        _approver        = approver;
        _events          = events;
        _recorderFactory = recorderFactory;
        _context         = context;
        // 新中断会清空旧确认；保留的确认时间表示该中断已由用户检查。
        var pending = session.PendingInspectionReason ?? needsInspection;
        if (pending is not null && session.InspectionAcknowledgedUtc is null)
        {
            _inspectionNote                 = pending;
            session.PendingInspectionReason = pending;
            _permissions?.SetNonReadOnlyBlock(pending);
        }

        _sessionWriter = sessionWriterFactory?.Invoke(session.Id);
    }

    /// <summary>
    ///     <para>本宿主服务的会话，包含稳定身份与全部回合。</para>
    ///     <para>The session served by this host, including stable identity and all turns.</para>
    /// </summary>
    public ChatSession Session { get; }

    /// <summary>
    ///     <para>当前是否存在活动运行；同一会话最多一个。</para>
    ///     <para>Whether a run is active; at most one per session.</para>
    /// </summary>
    public bool HasActiveRun => Volatile.Read(ref _activeRuns) == 1;

    /// <summary>
    ///     <para>是否存在待用户确认的 NeedsInspection；阻断期间拒绝所有非只读能力，即使未注入权限引擎。</para>
    ///     <para>
    ///         Whether NeedsInspection awaits user acknowledgement; all non-read-only capabilities are denied
    ///         while blocked, even without an injected permission engine.
    ///     </para>
    /// </summary>
    public bool InspectionPending => _inspectionNote is not null;

    /// <summary>
    ///     <para>阻断原因与中断事实摘要；展示时使用脱敏检查输出，未阻断时为 null。</para>
    ///     <para>
    ///         The blocking reason and interruption summary; displayed through redacted inspection output,
    ///         null while unblocked.
    ///     </para>
    /// </summary>
    public string? InspectionPendingNote => _inspectionNote;

    /// <summary>
    ///     <para>
    ///         最近一次快照提交失败的说明；成功提交时清空。非空须向用户报告“本次未可靠保存”，
    ///         上一有效快照仍保留，回合终态不证明落盘成功。
    ///     </para>
    ///     <para>
    ///         The last snapshot-commit failure, cleared by a successful commit. A non-null value must be
    ///         reported as "not reliably saved this time"; the previous valid snapshot remains,
    ///         and a turn's terminal result does not prove persistence.
    ///     </para>
    /// </summary>
    public string? PersistenceWarning { get; private set; }

    /// <summary>
    ///     <para>快照文件绝对路径；未注入写入端时为 null。</para>
    ///     <para>The absolute snapshot path; null without an injected writer.</para>
    /// </summary>
    public string? PersistencePath => _sessionWriter?.SnapshotPath;

    public void Dispose() => _sessionWriter?.Dispose();

    private static ConversationContext ContextFor(AgentOptions options) =>
        new((options ?? throw new ArgumentNullException(nameof(options))).Context);

    /// <summary>
    ///     <para>提交新用户回合，执行至终态，结算中断并尝试保存快照。</para>
    ///     <para>
    ///         Submits a new user turn, executes to a terminal result, settles interruptions
    ///         and attempts to save a snapshot.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         先记录回合与运行；执行栈异常转换为失败或取消终态。取消不阻止终态保存，
    ///         保存失败记录为 <see cref="PersistenceWarning" />，不改写终态。
    ///     </para>
    ///     <para>
    ///         The turn and run are recorded first; execution-stack exceptions become failed or cancelled
    ///         terminal results. Cancellation does not prevent terminal persistence; save failures become
    ///         <see cref="PersistenceWarning" /> without rewriting the result.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     <para>已有活动运行时，在创建任何回合记录前拒绝提交。</para>
    ///     <para>An active run rejects submission before any turn record is created.</para>
    /// </exception>
    public async Task<SessionRun> SubmitTurnAsync(string userInput, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userInput);
        if (Interlocked.CompareExchange(ref _activeRuns, 1, 0) != 0)
            throw new
                InvalidOperationException("This session already has an active run; a session processes one run at a time (PLAN §5). " +
                                          "Wait for the current turn to finish before submitting another one.");

        var turn = new SessionTurn(NewId(), userInput);
        var run  = new SessionRun(NewId(), Session.Id, turn.Id);
        Session.AddTurn(turn);
        turn.AddRun(run);
        try
        {
            var loop   = ComposeRunLoop(run);
            var result = await loop.RunTurnAsync(_systemPrompt, userInput, cancellationToken).ConfigureAwait(false);
            run.Finish(result);
            return run;
        }
        catch (OperationCanceledException)
        {
            // 循环之外的取消也必须留下唯一终态与已有执行记录。
            run.Finish(new AgentResult
            {
                Status = AgentStatus.Cancelled, Error = "Run cancelled", ToolCalls = run.ExecutionRecords
            });
            return run;
        }
        catch (Exception ex)
        {
            run.Finish(new AgentResult
            {
                Status = AgentStatus.Failed, Error = ex.Message, ToolCalls = run.ExecutionRecords
            });
            return run;
        }
        finally
        {
            try
            {
                // 先回退不完整尾部再保存，避免快照携带无法续传的工具组。
                SettleInterruption(run);
                await CommitSnapshotAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _activeRuns, 0);
            }
        }
    }

    /// <summary>
    ///     <para>在用户完成检查并确认后解除非只读阻断，立即保存确认时间；未阻断时不操作。</para>
    ///     <para>
    ///         Clears non-read-only blocking after user inspection and acknowledgement, immediately saving
    ///         the acknowledgement time; a no-op while unblocked.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         保存成功后恢复不重复阻断；保存失败仅记录警告，内存中的解除仍有效。
    ///         取消在解除前检查，确认后的保存不受取消影响。
    ///     </para>
    ///     <para>
    ///         A successful save prevents repeated blocking on resume; a failed save records a warning
    ///         while the in-memory unblock remains. Cancellation is checked before unblocking,
    ///         not during persistence of the acknowledgement.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     <para>活动运行期间不能确认检查。</para>
    ///     <para>Inspection cannot be acknowledged while a run is active.</para>
    /// </exception>
    public async Task AcknowledgeInspectionAsync(CancellationToken cancellationToken)
    {
        if (!InspectionPending) return;

        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _activeRuns, 1, 0) != 0)
            throw new InvalidOperationException("Inspection cannot be acknowledged while a run is active.");

        try
        {
            Session.InspectionAcknowledgedUtc = DateTimeOffset.UtcNow;
            Session.PendingInspectionReason   = null;
            _inspectionNote                   = null;
            _permissions?.SetNonReadOnlyBlock(null);
            await CommitSnapshotAsync().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _activeRuns, 0);
        }
    }

    private async Task CheckpointExecutionAsync(
        SessionRun run, IReadOnlyList<ToolCallRecord> records, CancellationToken cancellationToken)
    {
        if (_sessionWriter is null) return;

        // 副作用前必须可靠保存执行意图；只截取完整协议边界，不为活动运行补造终态。
        var boundary = ChatHistoryInvariants.LastCompleteBoundary(_context.Messages);
        if (boundary < 0)
            throw new
                InvalidOperationException("Execution intent cannot be saved without a complete protocol boundary.");

        var position    = _context.Position;
        var safeContext = ContextFor(_options);
        safeContext.Restore(_context.Messages.Take(boundary).ToArray(), _context.State, position.HasState,
                            position.FoldStartMessageIndex, position.FoldEndMessageIndex);
        run.RecordExecutionIntent(records);
        var previousReason          = Session.PendingInspectionReason;
        var previousAcknowledgement = Session.InspectionAcknowledgedUtc;
        try
        {
            Session.PendingInspectionReason = $"run '{run.Id}' may have started side-effect tools whose results " +
                                              "were not reliably saved; inspect the workspace before continuing.";
            Session.InspectionAcknowledgedUtc = null;
            await _sessionWriter.CommitAsync(Session, safeContext, cancellationToken).ConfigureAwait(false);
            PersistenceWarning = null;
        }
        catch (Exception ex)
        {
            PersistenceWarning = ex.Message;
            throw new InvalidOperationException("Execution intent was not reliably saved; the tool was not started. " +
                                                ex.Message, ex);
        }
        finally
        {
            Session.PendingInspectionReason   = previousReason;
            Session.InspectionAcknowledgedUtc = previousAcknowledgement;
        }
    }

    /// <summary>
    ///     <para>结算终态后的中断：整体回退不完整尾部工具组，未知副作用触发 NeedsInspection 并清空旧确认。</para>
    ///     <para>
    ///         Settles interruptions after a terminal result: rewinds an incomplete trailing tool group whole,
    ///         and unknown side effects trigger NeedsInspection and clear earlier acknowledgement.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         在快照提交前仅修改内存，保留执行记录，不拆分工具原子组或补造结果。
    ///         无法判定时阻断副作用，不抛出异常掩盖终态或阻止保存。
    ///     </para>
    ///     <para>
    ///         Changes memory only before snapshot commit, retaining execution records without splitting
    ///         atomic tool groups or fabricating results. Unclassifiable interruptions block side effects;
    ///         no exception escapes to mask the terminal result or prevent saving.
    ///     </para>
    /// </remarks>
    private void SettleInterruption(SessionRun run)
    {
        try
        {
            var boundary = ChatHistoryInvariants.LastCompleteBoundary(_context.Messages);
            if (boundary < 0)
            {
                // 历史中段损坏不能靠尾部回退修复，保守阻断副作用。
                LatchNeedsInspection("the conversation tail could not be settled to a complete protocol boundary; " +
                                     "side effects stay blocked pending inspection.");
                return;
            }

            var incompleteTail = boundary < _context.Messages.Count;
            if (incompleteTail) _context.TruncateToMessageCount(boundary);
            var status = run.Result?.Status.ToString().ToLowerInvariant() ?? "unfinished";
            var unknownSideEffects = run.Result?.ToolCalls?
                                        .Where(record => record.State == ToolCallExecutionState.UnknownResult &&
                                                         record.SideEffects)
                                        .Select(record => $"{record.ToolName} ({record.Summary})")
                                        .ToArray();
            if (unknownSideEffects is { Length: > 0 })
                LatchNeedsInspection($"run '{run.Id}' ({status}) left side-effect tools with unknown results: " +
                                     string.Join("; ", unknownSideEffects)                                      +
                                     ". The workspace may carry their effects; inspect it before allowing more side effects.");
            else if (incompleteTail && run.Result?.ToolCalls is not { Count: > 0 })
                // 缺少执行记录，无法证明副作用未发生。
                LatchNeedsInspection($"run '{run.Id}' ({status}) ended mid tool execution without execution records; " +
                                     "its side effects cannot be proven absent.");
        }
        catch (Exception)
        {
            LatchNeedsInspection("interruption settlement could not classify the run's tail; side effects stay " +
                                 "blocked pending inspection.");
        }
    }

    /// <summary>
    ///     <para>建立 NeedsInspection 并使旧确认失效；新中断须重新由用户检查确认。</para>
    ///     <para>
    ///         Latches NeedsInspection and invalidates earlier acknowledgement;
    ///         a new interruption requires renewed user inspection and acknowledgement.
    ///     </para>
    /// </summary>
    private void LatchNeedsInspection(string reason)
    {
        _inspectionNote                   = reason;
        Session.PendingInspectionReason   = reason;
        Session.InspectionAcknowledgedUtc = null;
        _permissions?.SetNonReadOnlyBlock(reason);
    }

    /// <summary>
    ///     <para>提交当前快照；失败转换为 <see cref="PersistenceWarning" />，不掩盖终态或中断内存会话。</para>
    ///     <para>
    ///         Commits the current snapshot; failures become <see cref="PersistenceWarning" /> without
    ///         masking the terminal result or interrupting the in-memory session.
    ///     </para>
    /// </summary>
    private async Task CommitSnapshotAsync()
    {
        if (_sessionWriter is null) return;

        try
        {
            await _sessionWriter.CommitAsync(Session, _context, CancellationToken.None).ConfigureAwait(false);
            PersistenceWarning = null;
        }
        catch (Exception ex)
        {
            PersistenceWarning = ex.Message;
        }
    }

    /// <summary>
    ///     <para>组合独立循环、共享上下文与身份关联事件；待检查时保证非只读阻断生效。</para>
    ///     <para>
    ///         Composes a fresh loop, shared context and identity-correlated events;
    ///         ensures non-read-only blocking while inspection is pending.
    ///     </para>
    /// </summary>
    private AgentLoop ComposeRunLoop(SessionRun run)
    {
        var recorder    = _recorderFactory?.Invoke(run.Id);
        var events      = _events is null ? null : new RunEventCorrelator(Session.Id, run.TurnId, run.Id, _events);
        var permissions = _permissions;
        if (!InspectionPending || permissions is not null)
            return new AgentLoop(_modelClient, _tools, _options, permissions, _approver, recorder, events, _context,
                                 _sessionWriter is null
                                     ? null
                                     : (records, token) => CheckpointExecutionAsync(run, records, token));
        permissions = new PermissionEngine(Session.WorkspaceRoot);
        permissions.SetNonReadOnlyBlock(_inspectionNote);

        return new AgentLoop(_modelClient, _tools, _options, permissions, _approver, recorder, events, _context,
                             _sessionWriter is null
                                 ? null
                                 : (records, token) => CheckpointExecutionAsync(run, records, token));
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>
    ///     <para>转发循环事件并补齐会话、回合与运行身份，使循环不依赖会话层。</para>
    ///     <para>
    ///         Forwards loop events with session, turn and run identities, keeping the loop independent
    ///         of the session layer.
    ///     </para>
    /// </summary>
    private sealed class RunEventCorrelator(string sessionId, string turnId, string runId, IHostEventSink inner)
        : IHostEventSink
    {
        public async ValueTask PublishAsync(HostEvent @event, CancellationToken cancellationToken)
        {
            await inner.PublishAsync(@event with
                                     {
                                         SessionId = sessionId,
                                         TurnId = turnId,
                                         RunId = runId
                                     },
                                     cancellationToken)
                       .ConfigureAwait(false);
        }
    }
}

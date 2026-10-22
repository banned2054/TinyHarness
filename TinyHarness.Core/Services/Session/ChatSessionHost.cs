using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.ProjectInstructions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.ProjectInstructions;
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
///         副作用前提交安全意图，终态后结算中断并追加会话事实；未知副作用触发 NeedsInspection，
///         仅用户检查确认可解除，模型输出不能建立或解除阻断。
///     </para>
///     <para>
///         Commits safe intent before side effects, then settles interruptions and appends session facts;
///         unknown side effects
///         trigger NeedsInspection, cleared only by user inspection and acknowledgement, never model output.
///     </para>
///     <para>
///         不主动存储凭据或恢复可执行授权；canonical 历史可能含秘密并原样追加到日志，检查视图另行脱敏。
///         恢复时沿用已校验的会话、上下文与写入端，在原身份和提交序号上继续。
///     </para>
///     <para>
///         Credentials are not proactively stored and executable authorization is not restored;
///         canonical history may contain secrets and is appended verbatim, with separately redacted inspection views.
///         Resume continues the validated session, context and writer under the existing identity and commit sequence.
///     </para>
/// </remarks>
public sealed partial class ChatSessionHost : IDisposable
{
    private readonly IApprovalProvider?          _approver;
    private readonly ConversationContext         _context;
    private readonly SecretRedactor?             _eventRedactor;
    private readonly IHostEventSink?             _events;
    private readonly ProjectInstructionSnapshot? _initialInstructions;
    private readonly ProjectInstructionLoader?   _instructionLoader;
    private readonly IChatCompletionClient       _modelClient;
    private readonly AgentOptions                _options;
    private readonly PermissionEngine?           _permissions;
    private readonly Func<string, IRunRecorder>? _recorderFactory;
    private readonly ISessionStoreWriter?        _sessionWriter;
    private readonly string                      _systemPrompt;
    private readonly ToolRegistry                _tools;
    private          IRunRecorder?               _activeRecorder;

    private int _activeRuns;

    /// <summary>
    ///     <para>当前 NeedsInspection 阻断原因；null 表示未阻断，仅用户检查确认可清除。</para>
    ///     <para>
    ///         The current NeedsInspection reason; null while unblocked, cleared only by user
    ///         inspection and acknowledgement.
    ///     </para>
    /// </summary>
    private string? _inspectionNote;

    public ChatSessionHost(string                               workspaceRoot,
                           SessionModelIdentity                 model,
                           AgentOptions                         options,
                           IChatCompletionClient                modelClient,
                           ToolRegistry                         tools,
                           string                               systemPrompt,
                           PermissionEngine?                    permissions          = null,
                           IApprovalProvider?                   approver             = null,
                           IHostEventSink?                      events               = null,
                           Func<string, IRunRecorder>?          recorderFactory      = null,
                           Func<string, ISessionStoreWriter>?   sessionWriterFactory = null,
                           ProjectInstructionLoader?            instructionLoader    = null,
                           ProjectInstructionSnapshot?          initialInstructions  = null,
                           IReadOnlyDictionary<string, string>? knownSecrets         = null)
        : this(new ChatSession(NewId(), workspaceRoot, model), ContextFor(options), options, modelClient, tools,
               systemPrompt, permissions, approver, events, recorderFactory, sessionWriterFactory, null,
               instructionLoader, initialInstructions, knownSecrets)
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
    public ChatSessionHost(ChatSession                          session,
                           ConversationContext                  context,
                           AgentOptions                         options,
                           IChatCompletionClient                modelClient,
                           ToolRegistry                         tools,
                           string                               systemPrompt,
                           PermissionEngine?                    permissions         = null,
                           IApprovalProvider?                   approver            = null,
                           IHostEventSink?                      events              = null,
                           Func<string, IRunRecorder>?          recorderFactory     = null,
                           ISessionStoreWriter?                 sessionWriter       = null,
                           string?                              needsInspection     = null,
                           ProjectInstructionLoader?            instructionLoader   = null,
                           ProjectInstructionSnapshot?          initialInstructions = null,
                           IReadOnlyDictionary<string, string>? knownSecrets        = null)
        : this(session, context, options, modelClient, tools, systemPrompt, permissions, approver, events,
               recorderFactory, sessionWriter is null ? null : _ => sessionWriter, needsInspection,
               instructionLoader, initialInstructions, knownSecrets)
    {
    }

    /// <summary>
    ///     <para>校验固定模型与系统提示，建立必要的检查阻断，最后打开或接管写入端；失败即拒绝启动。</para>
    ///     <para>
    ///         Validates the fixed model and system prompt, establishes required inspection blocking,
    ///         and opens or takes over the writer last; any failure rejects startup.
    ///     </para>
    /// </summary>
    private ChatSessionHost(ChatSession                          session,
                            ConversationContext                  context,
                            AgentOptions                         options,
                            IChatCompletionClient                modelClient,
                            ToolRegistry                         tools,
                            string                               systemPrompt,
                            PermissionEngine?                    permissions,
                            IApprovalProvider?                   approver,
                            IHostEventSink?                      events,
                            Func<string, IRunRecorder>?          recorderFactory,
                            Func<string, ISessionStoreWriter>?   sessionWriterFactory,
                            string?                              needsInspection,
                            ProjectInstructionLoader?            instructionLoader,
                            ProjectInstructionSnapshot?          initialInstructions,
                            IReadOnlyDictionary<string, string>? knownSecrets)
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

        Session              = session;
        _options             = options;
        _modelClient         = modelClient;
        _tools               = tools;
        _systemPrompt        = systemPrompt;
        _permissions         = permissions;
        _approver            = approver;
        _events              = events;
        _recorderFactory     = recorderFactory;
        _context             = context;
        _instructionLoader   = instructionLoader;
        _initialInstructions = initialInstructions;
        _eventRedactor       = knownSecrets is null ? null : new SecretRedactor(knownSecrets);
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
    ///         最近一次日志提交失败的说明；成功提交时清空。非空须向用户报告“本次未可靠保存”，
    ///         有效前缀仍可检查，不确定写入后的 writer 停止追加，回合终态不证明落盘成功。
    ///     </para>
    ///     <para>
    ///         The last journal-commit failure, cleared by a successful commit. A non-null value must be
    ///         reported as "not reliably saved this time"; the valid prefix remains inspectable and an uncertain
    ///         writer stops appending. A turn's terminal result does not prove persistence.
    ///     </para>
    /// </summary>
    public string? PersistenceWarning { get; private set; }

    /// <summary>
    ///     <para>实时历史展示故障；与日志保存警告分开，且不会改变工具执行确认。</para>
    ///     <para>Live-history display failures, separate from persistence warnings and execution confirmation.</para>
    /// </summary>
    public string? DisplayWarning { get; private set; }

    /// <summary>
    ///     <para>权威 JSONL 会话日志绝对路径；未注入写入端时为 null。</para>
    ///     <para>The absolute path of the authoritative JSONL session log; null without a writer.</para>
    /// </summary>
    public string? PersistencePath => _sessionWriter?.SnapshotPath;

    public void Dispose() => _sessionWriter?.Dispose();

    private static ConversationContext ContextFor(AgentOptions options) =>
        new((options ?? throw new ArgumentNullException(nameof(options))).Context);

    /// <summary>
    ///     <para>提交新用户回合，执行至终态，结算中断并尝试追加会话事实。</para>
    ///     <para>
    ///         Submits a new user turn, executes to a terminal result, settles interruptions
    ///         and attempts to append session facts.
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
        DisplayWarning = null;

        var turn = new SessionTurn(NewId(), userInput);
        var run  = new SessionRun(NewId(), Session.Id, turn.Id);
        Session.AddTurn(turn);
        turn.AddRun(run);
        using var control = CreateRunControl(run, cancellationToken);
        _activeControl = control;
        try
        {
            var loop   = ComposeRunLoop(run);
            var result = await loop.RunTurnAsync(_systemPrompt, userInput, control.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result.PersistenceWarning))
                PersistenceWarning = result.PersistenceWarning;
            run.Finish(result);
            return run;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 循环之外的调用方取消也须留下终态；其他取消异常交由失败分支保留原因。
            run.Finish(new AgentResult
            {
                Status    = AgentStatus.Cancelled,
                Error     = "Run cancelled",
                ToolCalls = run.ExecutionRecords
            });
            return run;
        }
        catch (Exception ex)
        {
            run.Finish(new AgentResult
            {
                Status    = AgentStatus.Failed,
                Error     = ex.Message,
                ToolCalls = run.ExecutionRecords
            });
            return run;
        }
        finally
        {
            await control.SealAsync().ConfigureAwait(false);
            try
            {
                // 先回退不完整尾部再提交，避免 canonical 历史携带无法续传的工具组。
                SettleInterruption(run);
                await CommitSnapshotAsync().ConfigureAwait(false);
            }
            finally
            {
                DisplayWarning  = _activeRecorder?.DisplayWarning;
                _activeRecorder = null;
                _activeControl  = null;
                _permissions?.ClearTemporaryAllows();
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
    ///         确认可靠保存后才解除阻断，恢复时不重复阻断；保存失败保留内存阻断并报告失败。
    ///         取消在确认前检查，确认保存不受后续取消影响。
    ///     </para>
    ///     <para>
    ///         Unblocks only after the acknowledgement is reliably saved, preventing repeated blocking on resume.
    ///         A failed save retains the in-memory block and reports failure. Cancellation is checked before
    ///         acknowledgement and does not interrupt its persistence afterward.
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
            var previousAcknowledgement = Session.InspectionAcknowledgedUtc;
            var previousReason          = Session.PendingInspectionReason;
            Session.InspectionAcknowledgedUtc = DateTimeOffset.UtcNow;
            Session.PendingInspectionReason   = null;
            if (!await CommitSnapshotAsync().ConfigureAwait(false))
            {
                Session.InspectionAcknowledgedUtc = previousAcknowledgement;
                Session.PendingInspectionReason   = previousReason;
                throw new
                    InvalidOperationException("Inspection acknowledgement was not reliably saved; non-read-only actions remain blocked.");
            }

            _inspectionNote = null;
            _permissions?.SetNonReadOnlyBlock(null);
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
        var safeContext = CompleteContextSnapshot();
        run.RecordExecutionIntent(records);
        var previousReason          = Session.PendingInspectionReason;
        var previousAcknowledgement = Session.InspectionAcknowledgedUtc;
        try
        {
            Session.PendingInspectionReason = $"run '{run.Id}' may have started side-effect tools whose results " +
                                              "were not reliably saved; inspect the workspace before continuing.";
            Session.InspectionAcknowledgedUtc = null;
            await _sessionWriter.CommitExecutionIntentAsync(Session, safeContext, cancellationToken)
                                .ConfigureAwait(false);
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
    ///         在日志提交前仅修改内存，保留执行记录，不拆分工具原子组或补造结果。
    ///         无法判定时阻断副作用，不抛出异常掩盖终态或阻止保存。
    ///     </para>
    ///     <para>
    ///         Changes memory only before journal commit, retaining execution records without splitting
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
    ///     <para>追加当前会话增量；失败转换为 <see cref="PersistenceWarning" />，不掩盖执行终态。</para>
    ///     <para>
    ///         Appends current session increments; failures become <see cref="PersistenceWarning" /> without
    ///         masking the execution outcome.
    ///     </para>
    /// </summary>
    private async Task<bool> CommitSnapshotAsync()
    {
        if (_sessionWriter is null) return true;

        if (!_sessionWriter.IsHealthy)
        {
            PersistenceWarning = _sessionWriter.WriteFailure ??
                                 "The session journal is poisoned after an earlier write failure.";
            return false;
        }

        try
        {
            await _sessionWriter.CommitAsync(Session, _context, CancellationToken.None).ConfigureAwait(false);
            PersistenceWarning = null;
            return true;
        }
        catch (Exception ex)
        {
            PersistenceWarning = ex.Message;
            return false;
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
        var events = _events is null ? null : new RunEventCorrelator(Session.Id, run.TurnId, run.Id, _events);
        var recorder = _recorderFactory?.Invoke(run.Id) ??
                       _sessionWriter?.CreateRunRecorder(Session.Id, run.TurnId, run.Id,
                                                         token => _sessionWriter.CommitAsync(Session, _context, token),
                                                         events is null ? null : events.PublishHistoryItemAsync);
        _activeRecorder = recorder;
        var permissions = _permissions;
        if (InspectionPending && permissions is null)
        {
            permissions = new PermissionEngine(Session.WorkspaceRoot);
            permissions.SetNonReadOnlyBlock(_inspectionNote);
        }

        var instructions = _instructionLoader is null
            ? null
            : new ProjectInstructionBoundary(this, run, _instructionLoader);

        var tools = _options.EnableWorkItems
            ? new ToolRegistry([
                .. _tools.Values, new SessionWorkItemTool(() => Session.WorkItems,
                                                          WorkEvidenceCandidates,
                                                          (id, title, status, evidence, token) =>
                                                              UpdateWorkItemAsync(run, id, title,
                                                                  status, evidence, token))
            ])
            : _tools;
        return new AgentLoop(_modelClient, tools, _options, permissions, _approver, recorder, events, _context,
                             _sessionWriter is null
                                 ? null
                                 : (records, token) => CheckpointExecutionAsync(run, records, token),
                             instructions, _activeControl);
    }

    private IReadOnlyList<ProjectInstructionRecord> RetainedInstructionRecords(int boundary)
    {
        var retained = new List<ProjectInstructionRecord>();
        foreach (var record in _context.ProjectInstructionRecords)
        {
            if (record.CanonicalMessageIndex is { } index && index >= boundary) break;
            retained.Add(record);
        }

        return retained;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>
    ///     <para>
    ///         把宿主的项目指令加载、可靠提交、权限失效和元数据事件适配为 Agent Loop 的窄边界。
    ///     </para>
    ///     <para>
    ///         Adapts the host's instruction loading, reliable commits, permission invalidation and metadata
    ///         events to the narrow boundary consumed by the Agent Loop.
    ///     </para>
    /// </summary>
    private sealed class ProjectInstructionBoundary(
        ChatSessionHost host, SessionRun run, ProjectInstructionLoader loader) : IAgentInstructionBoundary
    {
        private ProjectInstructionRecord?   _pendingRecord;
        private ProjectInstructionSnapshot? _toolRoundSnapshot;

        public async Task PrepareUserContextAsync(CancellationToken cancellationToken)
        {
            // 后续回合必须从当前有效快照继续；initial 只用于尚未纳入宿主管理的新会话。
            var previous = host._context.ProjectInstructions ?? host._initialInstructions;
            var snapshot = await loader.LoadAsync(previous, [], cancellationToken).ConfigureAwait(false);
            StageIfChanged(snapshot);
        }

        public async Task<ProjectInstructionsChanged?> ConfirmInitialCommitAsync(CancellationToken cancellationToken)
        {
            if (_pendingRecord is null) return null;

            // Recorder.Start 已提交初始批次；这里再走同一 writer，缺省增量时为空操作，
            // 但可覆盖自定义 recorder 未完成初始提交的低层用法。
            return await CommitPendingAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<ProjectInstructionsChanged?> RefreshBeforeModelRequestAsync(
            CancellationToken cancellationToken)
        {
            // 工具轮发现的无规则目录只改变探测集合；在同一运行内保留该候选，完整边界后再可靠提交。
            var previous = _toolRoundSnapshot ?? host._context.ProjectInstructions;
            var snapshot = await loader.LoadAsync(previous, [], cancellationToken).ConfigureAwait(false);
            _toolRoundSnapshot = snapshot;

            var current = host._context.ProjectInstructions;
            if (SnapshotUnchanged(current, snapshot)) return null;

            StageIfChanged(snapshot);
            return await CommitPendingAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<InstructionBoundaryCheck> CheckToolRoundAsync(
            IReadOnlyList<ToolPreparation> preparations, CancellationToken cancellationToken)
        {
            try
            {
                var snapshot = await LoadForScopesAsync(preparations, cancellationToken).ConfigureAwait(false);
                return CheckSnapshot(snapshot);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return new InstructionBoundaryCheck(InstructionBoundaryStatus.Failed, ex.Message, null, null);
            }
        }

        public async Task<InstructionBoundaryCheck> RecheckBeforeDispatchAsync(
            IReadOnlyList<ToolPreparation> preparations, CancellationToken cancellationToken)
        {
            try
            {
                var snapshot = await LoadForScopesAsync(preparations, cancellationToken).ConfigureAwait(false);
                return CheckSnapshot(snapshot);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return new InstructionBoundaryCheck(InstructionBoundaryStatus.Failed, ex.Message, null, null);
            }
        }

        public async Task<ProjectInstructionsChanged?> ApplyToolRoundUpdateAsync(
            IReadOnlyList<ToolCallRecord> records, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(records);
            if (_toolRoundSnapshot is not { } snapshot)
                throw
                    new InvalidOperationException("No project instruction update was discovered for this tool round.");

            StageIfChanged(snapshot);
            return await CommitPendingAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<ProjectInstructionSnapshot> LoadForScopesAsync(
            IReadOnlyList<ToolPreparation> preparations, CancellationToken cancellationToken)
        {
            var scopes = preparations.SelectMany(preparation => preparation.InstructionScopes)
                                     .Distinct()
                                     .ToArray();
            var previous = _toolRoundSnapshot ?? host._context.ProjectInstructions;
            var snapshot = await loader.LoadAsync(previous, scopes, cancellationToken)
                                       .ConfigureAwait(false);
            _toolRoundSnapshot = snapshot;
            return snapshot;
        }

        private static bool SnapshotUnchanged(
            ProjectInstructionSnapshot? previous, ProjectInstructionSnapshot snapshot) =>
            previous is not null                               &&
            previous.Revision             == snapshot.Revision &&
            previous.DiscoveryFingerprint == snapshot.DiscoveryFingerprint;

        private InstructionBoundaryCheck CheckSnapshot(ProjectInstructionSnapshot snapshot)
        {
            var current = host._context.ProjectInstructions;
            return current?.Revision == snapshot.Revision
                ? new InstructionBoundaryCheck(InstructionBoundaryStatus.Unchanged, null, null,
                                               new InstructionCoverage(host.Session.WorkspaceRoot, snapshot))
                : new InstructionBoundaryCheck(InstructionBoundaryStatus.Updated, null, snapshot.Revision, null);
        }

        private void StageIfChanged(ProjectInstructionSnapshot snapshot)
        {
            var current = host._context.ProjectInstructions;
            if (SnapshotUnchanged(current, snapshot)) return;

            var reason = current is null
                ? "initial"
                : current.Documents.Count > snapshot.Documents.Count
                    ? "removed"
                    : current.Revision == snapshot.Revision
                        ? "scope-discovered"
                        : "changed";
            _pendingRecord = host._context.StageProjectInstructions(snapshot, reason, run.TurnId, run.Id);
        }

        private async Task<ProjectInstructionsChanged?> CommitPendingAsync(CancellationToken cancellationToken)
        {
            if (_pendingRecord is not { } record) return null;

            if (host._sessionWriter is not null)
            {
                if (!host._sessionWriter.IsHealthy)
                    throw new
                        InvalidOperationException("The session journal is poisoned; changed project instructions were not activated.");

                try
                {
                    await host._sessionWriter.CommitAsync(host.Session, host._context, cancellationToken)
                              .ConfigureAwait(false);
                    host.PersistenceWarning = null;
                }
                catch (Exception ex)
                {
                    host.PersistenceWarning = ex.Message;
                    throw new
                        InvalidOperationException("Changed project instructions were not reliably saved; the request was not sent.",
                                                  ex);
                }
            }

            host._context.ConfirmProjectInstructions();
            if (record.Action == "rules-applied") host._permissions?.ClearTemporaryAllows();

            _toolRoundSnapshot = record.Snapshot;
            _pendingRecord     = null;
            // scopes-tracked 没有 effective rules 变化；只由统一 writer 发布 history item。
            return record.Action == "rules-applied" ? ChangedEvent(record) : null;
        }

        private ProjectInstructionsChanged ChangedEvent(ProjectInstructionRecord record)
        {
            var snapshot = record.Snapshot;
            var sources = snapshot.Documents
                                  .Select(document => $"{document.SourcePath} (scope: {document.ScopeDirectory})")
                                  .Select(source => host._eventRedactor?.Redact(source) ?? source)
                                  .ToArray();

            return new ProjectInstructionsChanged(record.Action, record.Revision, record.PreviousRevision,
                                                  record.Reason,
                                                  snapshot.Documents.Count, snapshot.ProbedDirectories.Count,
                                                  snapshot.DiscoveryFingerprint,
                                                  snapshot.RenderedHash, snapshot.RendererVersion, sources);
        }
    }

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
        public bool IncludeReasoningText => inner.IncludeReasoningText;

        private long _sequence;

        public async ValueTask PublishAsync(HostEvent @event, CancellationToken cancellationToken)
        {
            await inner.PublishAsync(@event with
                                     {
                                         Sequence = Interlocked.Increment(ref _sequence),
                                         SessionId = sessionId,
                                         TurnId = turnId,
                                         RunId = runId
                                     },
                                     cancellationToken)
                       .ConfigureAwait(false);
        }

        public ValueTask PublishHistoryItemAsync(SessionHistoryItem item, CancellationToken cancellationToken) =>
            inner.PublishAsync(new HostEvent
            {
                Kind      = HostEventKind.HistoryItemCompleted,
                Sequence  = Interlocked.Increment(ref _sequence),
                SessionId = sessionId,
                TurnId    = turnId,
                RunId     = runId,
                Payload   = item
            }, cancellationToken);
    }
}

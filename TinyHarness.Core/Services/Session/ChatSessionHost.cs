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
///     会话生命周期协调器（M12.1，PLAN §5）：持有一个 <see cref="ChatSession" /> 的稳定身份、
///     固定系统提示与跨回合共享的 <see cref="ConversationContext" />；每个用户回合创建独立的
///     <see cref="SessionRun" />，组合全新的 <see cref="AgentLoop" />（与 worker 每次调用组合
///     新循环的既有模式一致）并执行到终态。同一时刻只允许一个活动运行；上一回合以不完整的
///     工具原子组结束时（执行中途取消或失败），续聊被显式拒绝并记录为失败运行——不补造工具
///     结果，不把无效历史发给模型（PLAN §7.3 的进程内对应物；检查与解除阻断属于 M13/M15）。
///     会话与运行不主动存储认证凭据（凭据由调用方持有的模型客户端按引用解析），也不存储或
///     恢复可执行授权；对话历史可能包含用户粘贴、文件读取或模型返回的秘密（v3 canonical
///     保真存储原样落盘，检查视图另行脱敏）。
///     M13.1 起可选注入会话快照写入端：每次回合唱到终态后在完整回合边界提交快照（PLAN §7.1），
///     保存失败不改变回合终态，而是记录为 <see cref="PersistenceWarning" /> 交由宿主展示
///     “本次未可靠保存”。M13.2 起提供恢复构造路径：由 <see cref="SessionResume" /> 重建的会话
///     与已回填的上下文连同已打开的写入端一并接管，跨进程续聊在同一身份与提交序号上继续。
///     The session lifecycle coordinator (M12.1, PLAN §5): owns one <see cref="ChatSession" />'s
///     stable identity, the fixed system prompt and the <see cref="ConversationContext" /> shared
///     across turns. Every user turn creates its own <see cref="SessionRun" />, composes a fresh
///     <see cref="AgentLoop" /> (the same compose-per-call pattern the worker already uses) and
///     executes it to a terminal state. Only one run may be active at a time; when the previous
///     turn ended with an incomplete tool group (cancel or failure mid-execution), continuation
///     is explicitly refused and recorded as a failed run — tool results are never fabricated and
///     invalid history is never sent to the model (the in-process counterpart of PLAN §7.3; the
///     inspection and unblocking flow belongs to M13/M15). Neither the session nor a run
///     proactively stores authentication credentials (resolved by reference inside the
///     caller-supplied model client) or any executable authorization; the conversation
///     history may still contain secrets pasted by the user, read from files or returned
///     by the model (stored verbatim by the v3 canonical format, while inspection views
///     are redacted separately). Since M13.1 an optional session-snapshot writer can be
///     injected: after every turn reaches its terminal state the snapshot is committed at that
///     complete turn boundary (PLAN §7.1); a failed save never rewrites the turn's terminal
///     result but is recorded as <see cref="PersistenceWarning" /> for the host to report
///     "not reliably saved". Since M13.2 a resume construction path takes over a session rebuilt
///     by <see cref="SessionResume" /> together with its rehydrated context and an already-open
///     writer, continuing a cross-process resume on the same identity and commit sequence.
/// </summary>
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
               systemPrompt, permissions, approver, events, recorderFactory, sessionWriterFactory)
    {
    }

    /// <summary>
    ///     恢复构造路径（M13.2）：接管 <see cref="SessionResume" /> 重建的会话与上下文，并沿用
    ///     调用方已打开（锁已持有、提交序号已续种）的写入端继续在完整回合边界提交。构造失败时
    ///     写入端仍归调用方所有，必须由调用方释放。
    ///     The resume construction path (M13.2): takes over the session and context
    ///     rebuilt by <see cref="SessionResume" /> and keeps committing at complete
    ///     turn boundaries through the caller's already-open writer (lock held,
    ///     commit sequence seeded). On a construction failure the writer still
    ///     belongs to the caller, who must dispose it.
    /// </summary>
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
                           ISessionStoreWriter?        sessionWriter   = null)
        : this(session, context, options, modelClient, tools, systemPrompt, permissions, approver, events,
               recorderFactory, sessionWriter is null ? null : _ => sessionWriter)
    {
    }

    /// <summary>
    ///     两条构造路径的共享主构造：校验模型身份一致，非空上下文的首条 system 消息必须与本宿主的
    ///     系统提示一致（会话内指令固定），最后才打开/接管写入端——失败即会话启动失败（fail-closed）。
    ///     The shared master constructor of both paths: the model identity must
    ///     agree, and a non-empty context must start with the same system message as
    ///     this host's prompt (instructions are fixed per session); the writer is
    ///     opened/taken over last — any failure fails the session startup (fail-closed).
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
                            Func<string, ISessionStoreWriter>? sessionWriterFactory)
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

        // A restored context must already carry the same fixed instructions; a fresh
        // one is empty and receives the prompt on its first turn.
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
        // Opened after the session identity exists so the writer can bind the id;
        // a failure here fails the session startup (fail-closed, PLAN §7.1).
        _sessionWriter = sessionWriterFactory?.Invoke(session.Id);
    }

    /// <summary>本宿主服务的会话（稳定身份与全部回合）。The session served by this host (stable identity, all turns).</summary>
    public ChatSession Session { get; }

    /// <summary>当前是否存在活动运行；同一会话同一时刻最多一个。Whether a run is currently active; at most one per session.</summary>
    public bool HasActiveRun => Volatile.Read(ref _activeRuns) == 1;

    /// <summary>
    ///     最近一次快照提交失败的说明；提交成功时清空。非空表示“本次未可靠保存”，上一有效快照仍在，
    ///     会话可以继续但必须向用户报告（PLAN §7.1）。存储终态以本属性与实际文件为准，
    ///     回合终态不证明落盘成功。
    ///     The last snapshot-commit failure; cleared by a successful commit. A
    ///     non-empty value means "not reliably saved this time" — the previous
    ///     valid snapshot remains and the session continues, but the user must be
    ///     told (PLAN §7.1). Persistence truth lives here and in the files, never
    ///     in a run's terminal state.
    /// </summary>
    public string? PersistenceWarning { get; private set; }

    /// <summary>快照文件的绝对路径；未注入写入端时为 null。The snapshot file's absolute path; null without an injected writer.</summary>
    public string? PersistencePath => _sessionWriter?.SnapshotPath;

    public void Dispose() => _sessionWriter?.Dispose();

    private static ConversationContext ContextFor(AgentOptions options) =>
        new((options ?? throw new ArgumentNullException(nameof(options))).Context);

    /// <summary>
    ///     提交一个新的用户回合并执行到终态。回合与运行记录先写入会话；执行栈的异常在宿主
    ///     边界转换为运行终态（失败或取消），不会向上抛出。并发提交在创建任何记录之前被拒绝。
    ///     回合终态确定后，在完整回合边界提交会话快照；保存失败只记录
    ///     <see cref="PersistenceWarning" />。
    ///     Submits a new user turn and executes it to a terminal state. The turn and run
    ///     records enter the session first; execution-stack exceptions are converted at the
    ///     host boundary into the run's terminal state (failed or cancelled) and never
    ///     escape this method. Concurrent submission is rejected before any record is made.
    ///     After the terminal state is settled the session snapshot is committed at that
    ///     complete turn boundary; a failed save only records
    ///     <see cref="PersistenceWarning" />.
    /// </summary>
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
            // AgentLoop normally converts cancellation into a terminal result itself; this
            // defensive path keeps the run's lifecycle invariant (exactly one terminal).
            run.Finish(new AgentResult { Status = AgentStatus.Cancelled, Error = "Run cancelled" });
            return run;
        }
        catch (Exception ex)
        {
            // Host boundary conversion (AGENTS.md): execution-stack exceptions become a
            // failed terminal with the exception message instead of escaping with no record.
            run.Finish(new AgentResult { Status = AgentStatus.Failed, Error = ex.Message });
            return run;
        }
        finally
        {
            try
            {
                // Commit after the terminal state is recorded, at the complete turn
                // boundary. Deliberately not tied to the turn's cancellation token:
                // a cancelled turn must still persist its Cancelled terminal.
                await CommitSnapshotAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _activeRuns, 0);
            }
        }
    }

    /// <summary>
    ///     提交当前会话快照；失败转换为 <see cref="PersistenceWarning" /> 而不是异常，
    ///     保证回合终态与续聊能力不受存储故障影响。写入端契约以
    ///     <see cref="PersistenceWarning" /> 为中心，但回合终态已记录，任何形态的存储
    ///     故障都不允许从本路径逃逸或掩盖终态。
    ///     Commits the current session snapshot; failures become
    ///     <see cref="PersistenceWarning" /> instead of exceptions, keeping the turn's
    ///     terminal state and the session's usability intact through storage faults.
    ///     The writer contract centers on its store exception, but since the turn's
    ///     terminal state is already recorded, no persistence fault of any shape may
    ///     escape this path or mask it.
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
    ///     为本次运行组合执行栈：全新循环 + 跨回合共享的上下文 + 关联了运行身份的事件转发。
    ///     Composes the execution stack for one run: a fresh loop, the context shared across
    ///     turns, and event forwarding correlated with the run identity.
    /// </summary>
    private AgentLoop ComposeRunLoop(SessionRun run)
    {
        var recorder = _recorderFactory?.Invoke(run.Id);
        var events   = _events is null ? null : new RunEventCorrelator(Session.Id, run.TurnId, run.Id, _events);
        return new AgentLoop(_modelClient, _tools, _options, _permissions, _approver, recorder, events, _context);
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>
    ///     把循环发布的运行内事件与会话/回合/运行身份关联后转发：循环不知道会话层概念，
    ///     身份由宿主补齐。
    ///     Correlates loop-published run-scoped events with the session/turn/run identity
    ///     while forwarding: the loop never knows session-layer concepts, the host stamps
    ///     them in.
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

using TinyHarness.Core.Models.Agent;
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
///     会话与运行都不包含密钥或可执行授权：凭据由调用方持有的模型客户端按引用解析。
///     The session lifecycle coordinator (M12.1, PLAN §5): owns one <see cref="ChatSession" />'s
///     stable identity, the fixed system prompt and the <see cref="ConversationContext" /> shared
///     across turns. Every user turn creates its own <see cref="SessionRun" />, composes a fresh
///     <see cref="AgentLoop" /> (the same compose-per-call pattern the worker already uses) and
///     executes it to a terminal state. Only one run may be active at a time; when the previous
///     turn ended with an incomplete tool group (cancel or failure mid-execution), continuation
///     is explicitly refused and recorded as a failed run — tool results are never fabricated and
///     invalid history is never sent to the model (the in-process counterpart of PLAN §7.3; the
///     inspection and unblocking flow belongs to M13/M15). Neither the session nor a run carries
///     secrets or executable authorization: credentials are resolved by reference inside the
///     caller-supplied model client.
/// </summary>
public sealed class ChatSessionHost
{
    private readonly IApprovalProvider?          _approver;
    private readonly ConversationContext         _context;
    private readonly IHostEventSink?             _events;
    private readonly IChatCompletionClient       _modelClient;
    private readonly AgentOptions                _options;
    private readonly PermissionEngine?           _permissions;
    private readonly Func<string, IRunRecorder>? _recorderFactory;
    private readonly string                      _systemPrompt;
    private readonly ToolRegistry                _tools;

    private int _activeRuns;

    public ChatSessionHost(string                      workspaceRoot,
                           SessionModelIdentity        model,
                           AgentOptions                options,
                           IChatCompletionClient       modelClient,
                           ToolRegistry                tools,
                           string                      systemPrompt,
                           PermissionEngine?           permissions     = null,
                           IApprovalProvider?          approver        = null,
                           IHostEventSink?             events          = null,
                           Func<string, IRunRecorder>? recorderFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(modelClient);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        if (!string.Equals(options.Model, model.ModelId, StringComparison.Ordinal))
            throw new
                ArgumentException($"AgentOptions.Model '{options.Model}' does not match the session model identity '{model.ModelId}'.",
                                  nameof(options));

        Session          = new ChatSession(NewId(), workspaceRoot, model);
        _options         = options;
        _modelClient     = modelClient;
        _tools           = tools;
        _systemPrompt    = systemPrompt;
        _permissions     = permissions;
        _approver        = approver;
        _events          = events;
        _recorderFactory = recorderFactory;
        _context         = new ConversationContext(options.Context);
    }

    /// <summary>本宿主服务的会话（稳定身份与全部回合）。The session served by this host (stable identity, all turns).</summary>
    public ChatSession Session { get; }

    /// <summary>当前是否存在活动运行；同一会话同一时刻最多一个。Whether a run is currently active; at most one per session.</summary>
    public bool HasActiveRun => Volatile.Read(ref _activeRuns) == 1;

    /// <summary>
    ///     提交一个新的用户回合并执行到终态。回合与运行记录先写入会话；执行栈的异常在宿主
    ///     边界转换为运行终态（失败或取消），不会向上抛出。并发提交在创建任何记录之前被拒绝。
    ///     Submits a new user turn and executes it to a terminal state. The turn and run
    ///     records enter the session first; execution-stack exceptions are converted at the
    ///     host boundary into the run's terminal state (failed or cancelled) and never
    ///     escape this method. Concurrent submission is rejected before any record is made.
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
            Interlocked.Exchange(ref _activeRuns, 0);
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

    private static string NewId()
    {
        return Guid.NewGuid().ToString("N");
    }

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

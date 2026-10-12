using System.Text.Json;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Core.Services.Agent;

/// <summary>
///     Agent 主循环。负责请求模型、消费流、组装工具调用、整轮准备与授权、顺序执行工具，
///     并将结果交还模型；当模型返回纯文本、达到步数限制或任务取消时结束。每一步请求前，
///     主循环会检查 Context Manager：当预算超限且存在旧完整回合时，先以禁用工具的摘要调用
///     压缩旧历史，再发送模型视图。压缩只发生在两次模型请求之间，绝不会在等待审批或工具
///     执行期间进行。
///     未提供 <see cref="PermissionEngine" /> 时会跳过授权，仅用于测试或简单的只读流程。
///     循环不感知会话层概念：宿主可注入共享的 <see cref="ConversationContext" /> 实现跨回合
///     续聊（<see cref="RunTurnAsync" />），并经由窄的 <see cref="IHostEventSink" /> 契约接收
///     统一的宿主事件（PLAN §6），事件身份由宿主补齐。
///     The Agent main loop. Request the model, consume the stream, assemble tool
///     calls, prepare every call of the round, authorize and execute the approved
///     ones, hand results back, and repeat until the model produces a plain-text
///     turn, a limit is reached, or the run is cancelled. Before each request the
///     loop consults its Context Manager: when the budget is exceeded and old
///     complete turns exist it first compacts them with a tools-disabled summary
///     call, then sends the model view. Compaction only runs between two model
///     requests, never while an approval is pending or a tool is executing.
///     When no <see cref="PermissionEngine" /> is supplied (test/simple read-only
///     scenarios), authorization is skipped and every prepared invocation executes.
///     The loop knows nothing about sessions: the host can inject a shared
///     <see cref="ConversationContext" /> for cross-turn continuation
///     (<see cref="RunTurnAsync" />) and receives unified host events through the
///     narrow <see cref="IHostEventSink" /> contract (PLAN §6), with identity stamped
///     in by the host.
/// </summary>
public sealed class AgentLoop
{
    private readonly IApprovalProvider?    _approver;
    private readonly ConversationContext   _context;
    private readonly IHostEventSink?       _events;
    private readonly IChatCompletionClient _model;
    private readonly AgentOptions          _options;
    private readonly PermissionEngine?     _permissions;
    private readonly IRunRecorder?         _recorder;
    private readonly ToolRegistry          _tools;

    private long _eventSequence;

    /// <summary>
    ///     服务端报告的输入/输出 token 累计（含压缩摘要请求）；任一请求缺可靠 usage 时不计入，
    ///     全程无可靠 usage 保持 null。终态结果只反映本回合的累计。
    ///     Server-reported input/output token totals (compaction summary requests
    ///     included); requests without reliable usage contribute nothing and an all-null
    ///     run keeps null. The terminal result reflects this turn's totals only.
    /// </summary>
    private long? _reportedInputTokens;

    private long? _reportedOutputTokens;

    /// <summary>回合内首个事件接收方故障的说明；随终态结果并入 Error。The first sink-fault note of the turn; merged into Error on the terminal result.</summary>
    private string? _sinkFault;

    public AgentLoop(IChatCompletionClient model,         ToolRegistry         tools,    AgentOptions  options,
                     PermissionEngine?     permissions,   IApprovalProvider?   approver, IRunRecorder? recorder = null,
                     IHostEventSink?       events = null, ConversationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(options);
        _model       = model;
        _tools       = tools;
        _options     = options;
        _permissions = permissions;
        _approver    = approver;
        _recorder    = recorder;
        _events      = events;
        _context     = context ?? new ConversationContext(options.Context);
    }

    /// <summary>
    ///     创建不启用权限检查的主循环，仅供测试和只读流程使用；CLI 始终注入权限引擎。
    ///     Runs without authorization. Intended for tests and for read-only flows;
    ///     the CLI composition root always supplies a Permission Engine instead.
    /// </summary>
    public AgentLoop(IChatCompletionClient model, ToolRegistry tools, AgentOptions options)
        : this(model, tools, options, null, null)
    {
    }

    public IReadOnlyList<ChatMessage> History => _context.Messages;

    /// <summary>
    ///     从系统提示和用户输入开始运行一次完整 Agent 任务，并返回终止状态、最终文本及执行统计。
    ///     上下文总是被重置；跨回合续聊请使用 <see cref="RunTurnAsync" />。
    ///     Runs one complete agent task from the supplied prompts and returns its terminal state, final text,
    ///     and execution counts. The context is always reset; use <see cref="RunTurnAsync" /> to continue
    ///     a conversation across turns.
    /// </summary>
    public async Task<AgentResult> RunAsync(string systemPrompt, string userInput, CancellationToken cancellationToken)
    {
        _context.Reset();
        _context.Append(ChatMessage.System(systemPrompt));

        return await RunTurnCoreAsync(systemPrompt, userInput, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     在既有对话上下文上继续一个新回合：上下文为空时先写入系统提示；非空时要求系统提示
    ///     与首条 system 消息一致（会话内指令固定），并要求历史以完整的工具原子组结尾，然后
    ///     只追加新的用户输入。上一回合若在工具执行中途被取消或失败，续聊会被显式拒绝——
    ///     不补造工具结果，也不把无效历史发给模型（PLAN §7.3 的进程内对应物）。
    ///     Continues a new turn on the existing conversation context: an empty context gets
    ///     the system prompt first; a non-empty one must carry the same system prompt
    ///     (instructions are fixed per session) and must end with a complete tool group,
    ///     and then only the new user input is appended. When the previous turn was
    ///     cancelled or failed mid tool execution, continuation is explicitly refused —
    ///     tool results are never fabricated and invalid history is never sent to the
    ///     model (the in-process counterpart of PLAN §7.3).
    /// </summary>
    public async Task<AgentResult> RunTurnAsync(
        string systemPrompt, string userInput, CancellationToken cancellationToken)
    {
        if (_context.Messages.Count == 0)
        {
            _context.Append(ChatMessage.System(systemPrompt));
        }
        else
        {
            var head = _context.Messages[0];
            if (head.Role != ChatRole.System ||
                !string.Equals(head.Content, systemPrompt, StringComparison.Ordinal))
                throw new
                    InvalidOperationException("The supplied system prompt differs from the conversation's existing system prompt; " +
                                              "continuing a session requires the same instructions.");

            EnsureCompleteToolGroups();
        }

        return await RunTurnCoreAsync(systemPrompt, userInput, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     一次回合的共享执行核心：追加用户输入、发布运行开始事件，并驱动主循环到终态。
    ///     The shared execution core of one turn: append the user input, publish the
    ///     run-started event, and drive the main loop to a terminal state.
    /// </summary>
    private async Task<AgentResult> RunTurnCoreAsync(
        string systemPrompt, string userInput, CancellationToken cancellationToken)
    {
        // Each turn is one run for the event contract: sequence and sink-fault state
        // restart here so a reused loop still reports per-run identity.
        _eventSequence        = 0;
        _sinkFault            = null;
        _reportedInputTokens  = null;
        _reportedOutputTokens = null;

        _context.Append(ChatMessage.User(userInput));

        var steps          = 0;
        var toolExecutions = 0;
        var compactions    = 0;

        try
        {
            if (_recorder is not null)
                await _recorder.StartAsync(systemPrompt, userInput, cancellationToken).ConfigureAwait(false);

            await PublishEventAsync(HostEventKind.RunStarted, null, cancellationToken).ConfigureAwait(false);

            while (steps < _options.MaxAgentSteps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                steps++;

                // Budget check between requests only: compaction never runs while
                // an approval is pending or a tool is executing, because both only
                // happen after the model request below has returned. One compaction
                // pass may fold only part of the foldable history (the summarizer
                // input is budgeted too), so the loop keeps compacting until the
                // view fits the threshold or nothing foldable remains.
                var definitions = CurrentToolDefinitions() ?? [];
                while (await CompactIfNeededAsync(definitions, cancellationToken).ConfigureAwait(false)) compactions++;

                // Final budget guard before the request is sent: when the view still
                // cannot fit the model window (a failed summary, nothing foldable, or
                // a newest turn that alone exceeds the window), fail with an explicit
                // error instead of silently sending an over-window request.
                if (_options.Context is { } context)
                {
                    var total = _context.EstimateViewTokens()
                              + TokenEstimator.EstimateToolDefinitions(definitions)
                              + context.ReservedOutputTokens;
                    if (total > context.ContextWindowTokens)
                        return await FinishAsync(new AgentResult
                        {
                            Status         = AgentStatus.Failed,
                            Steps          = steps,
                            ToolExecutions = toolExecutions,
                            Compactions    = compactions,
                            Error = $"The context still exceeds the model window after compaction " +
                                    $"({total} > {context.ContextWindowTokens} tokens); the request was not sent."
                        }).ConfigureAwait(false);
                }

                var request = BuildRequest(definitions);

                // Calibration reference (PLAN §13), captured before the request is sent so
                // the view cannot change between estimate and send: the input-scope
                // estimate (view + tool definitions) of exactly the payload being sent,
                // on the raw estimator scale. It must stay raw — a value already scaled
                // by the current calibration factor would make the observed ratio
                // oscillate instead of converging.
                var estimatedPromptTokens = TokenEstimator.EstimateMessages(request.Messages)
                                          + TokenEstimator.EstimateToolDefinitions(definitions);
                var assistant = await RequestAssistantMessageAsync(request, cancellationToken, estimatedPromptTokens,
                                                                   true);

                _context.Append(assistant);

                if (assistant.ToolCalls is null || assistant.ToolCalls.Count == 0)
                    return await FinishAsync(new AgentResult
                    {
                        Status         = AgentStatus.Completed,
                        FinalMessage   = assistant.Content,
                        Steps          = steps,
                        ToolExecutions = toolExecutions,
                        Compactions    = compactions
                    }).ConfigureAwait(false);

                // Prepare and validate every call of the round before any of
                // them executes. A round that contains an un-preparable
                // call (unknown tool, malformed or out-of-bounds arguments) is
                // gated as a whole: nothing runs, so an invalid sibling call can
                // never leave side effects from the valid ones behind. When the
                // whole round prepares, each call is authorized and executed in
                // order.
                var round = PrepareRound(_tools, assistant.ToolCalls);
                if (_recorder is not null)
                    foreach (var prepared in round)
                        if (prepared.Preparation is not null)
                            await _recorder.RecordPreparedAsync(prepared.Preparation, cancellationToken)
                                           .ConfigureAwait(false);
                foreach (var item in round)
                    if (item.Preparation is not null)
                        await PublishEventAsync(HostEventKind.ToolPrepared, item.Preparation, cancellationToken)
                           .ConfigureAwait(false);

                var invalid = round.FirstOrDefault(item => item.Error is not null);
                if (invalid is not null)
                {
                    foreach (var item in round)
                        _context.Append(ChatMessage.Tool(item.Call.FunctionName, item.Call.Id,
                                                         item.Error ?? NotExecutedReason(invalid)));

                    continue;
                }

                // Resolve every permission decision before dispatching the first
                // call. This keeps the round phases explicit: Prepare all,
                // Authorize all, then Execute approved calls sequentially.
                var authorized = new List<AuthorizedRoundCall>(round.Count);
                foreach (var item in round)
                {
                    var authorization = _permissions is null
                        ? AuthorizationOutcome.Granted
                        : await AuthorizeAsync(item.Preparation!, cancellationToken);
                    authorized.Add(new AuthorizedRoundCall(item, authorization));
                }

                foreach (var item in authorized)
                {
                    if (!item.Authorization.Allowed)
                    {
                        _context.Append(ChatMessage.Tool(item.RoundCall.Call.FunctionName, item.RoundCall.Call.Id,
                                                         item.Authorization.Reason));
                        continue;
                    }

                    toolExecutions++;
                    await PublishEventAsync(HostEventKind.ToolStarted, item.RoundCall.Preparation!, cancellationToken)
                       .ConfigureAwait(false);
                    var result = await ExecutePreparedAsync(item.RoundCall.Tool!, item.RoundCall.Preparation!,
                                                            cancellationToken);

                    // The side effect already happened. Preserve its result in the
                    // in-memory history before attempting fallible audit I/O so a
                    // recorder failure cannot erase or cause a replay of the tool.
                    _context.Append(ChatMessage.Tool(item.RoundCall.Call.FunctionName, item.RoundCall.Call.Id,
                                                     result.Content));
                    await PublishEventAsync(HostEventKind.ToolFinished,
                                            new ToolExecutionOutcome(item.RoundCall.Preparation!, result),
                                            cancellationToken)
                       .ConfigureAwait(false);
                    if (_recorder is not null)
                        await _recorder.RecordResultAsync(item.RoundCall.Preparation!, result, cancellationToken)
                                       .ConfigureAwait(false);
                }
            }

            return await FinishAsync(new AgentResult
            {
                Status         = AgentStatus.StepLimitReached,
                FinalMessage   = string.Empty,
                Steps          = steps,
                ToolExecutions = toolExecutions,
                Compactions    = compactions
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await FinishAsync(new AgentResult
            {
                Status         = AgentStatus.Cancelled,
                Steps          = steps,
                ToolExecutions = toolExecutions,
                Compactions    = compactions,
                Error          = "Run cancelled"
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await FinishAsync(new AgentResult
            {
                Status         = AgentStatus.Failed,
                Steps          = steps,
                ToolExecutions = toolExecutions,
                Compactions    = compactions,
                Error          = ex.Message
            }).ConfigureAwait(false);
        }
    }

    private async Task<AgentResult> FinishAsync(AgentResult result)
    {
        // Stamp the turn's server-reported usage totals; a request without reliable
        // usage contributes nothing, so absence keeps meaning "not reported".
        result = result with { InputTokens = _reportedInputTokens, OutputTokens = _reportedOutputTokens };

        if (_recorder is null) return await PublishRunFinishedAsync(result).ConfigureAwait(false);
        try
        {
            await _recorder.CompleteAsync(result, _context.Messages, _context.State, CancellationToken.None)
                           .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            result = result with
            {
                Status = AgentStatus.Failed,
                Error = string.IsNullOrWhiteSpace(result.Error)
                    ? $"Run completed but persistence failed: {ex.Message}"
                    : $"{result.Error}; persistence failed: {ex.Message}"
            };
        }

        return await PublishRunFinishedAsync(result).ConfigureAwait(false);
    }

    /// <summary>
    ///     发布运行终态事件。终态事件不带令牌发布：取消也不得吞掉终态交付；事件接收方失败
    ///     不改变运行终态，只在 Error 中附加说明——显示失败不能抹去真实的执行结果。
    ///     回合内由普通事件发布记录的展示故障也在此并入 Error。失败终态在终态事件之前发布
    ///     一条 <see cref="HostEventKind.RunError" />（同样不带令牌），让宿主能以独立种类呈现
    ///     错误说明；取消与未失败终态不发布。
    ///     Publishes the run-finished event. The terminal event is published without a
    ///     token so cancellation can never swallow its delivery; a failing sink does not
    ///     change the terminal state and only appends an explanatory error — a display
    ///     failure must not erase the real outcome. Display faults recorded while
    ///     publishing ordinary events are merged into Error here as well. A failed
    ///     terminal state first publishes a <see cref="HostEventKind.RunError" /> (also
    ///     tokenless) so hosts can render the error under its own kind; cancelled and
    ///     non-failed terminals do not.
    /// </summary>
    private async Task<AgentResult> PublishRunFinishedAsync(AgentResult result)
    {
        if (_events is null) return result;

        if (_sinkFault is not null)
            result = result with
            {
                Error = string.IsNullOrWhiteSpace(result.Error)
                    ? $"Host event sink failed during the run: {_sinkFault}"
                    : $"{result.Error}; host event sink failed during the run: {_sinkFault}"
            };

        if (result.Status == AgentStatus.Failed && !string.IsNullOrEmpty(result.Error))
            try
            {
                await _events.PublishAsync(new HostEvent
                                           {
                                               Kind     = HostEventKind.RunError,
                                               Sequence = Interlocked.Increment(ref _eventSequence),
                                               Payload  = result.Error
                                           },
                                           CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _sinkFault ??= ex.Message;
            }

        try
        {
            await _events.PublishAsync(new HostEvent
                                       {
                                           Kind     = HostEventKind.RunFinished,
                                           Sequence = Interlocked.Increment(ref _eventSequence),
                                           Payload  = result
                                       },
                                       CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = result with
            {
                Error = string.IsNullOrWhiteSpace(result.Error)
                    ? $"Host event sink failed: {ex.Message}"
                    : $"{result.Error}; host event sink failed: {ex.Message}"
            };
        }

        return result;
    }

    /// <summary>
    ///     发布一条宿主事件；未配置事件接收方时为无操作。顺序号按运行递增（每个运行从 1 开始）。
    ///     与终态发布一致，展示失败不终止运行（PLAN §6：事件通道只有发布语义）：异常在此
    ///     转换为首条故障说明并随终态结果上报，而不是抹去真实的执行结果。运行自身取消导致
    ///     的接收方取消异常原样传播——不得把取消误记为展示故障。
    ///     Publishes one host event; a no-op when no sink is configured. The sequence
    ///     increases within the run (starting at 1). Matching the terminal publish, a sink
    ///     failure never aborts the run (PLAN §6: the channel is publish-only): the
    ///     exception is converted into the first fault note and surfaced on the terminal
    ///     result instead of erasing the real outcome. A sink failure driven by the run's
    ///     own cancellation propagates instead — cancellation must never latch a fault note
    ///     onto the cancelled result.
    /// </summary>
    private async ValueTask PublishEventAsync(
        HostEventKind kind, object? payload, CancellationToken cancellationToken)
    {
        if (_events is null) return;

        var @event = new HostEvent
        {
            Kind     = kind,
            Sequence = Interlocked.Increment(ref _eventSequence),
            Payload  = payload
        };
        try
        {
            await _events.PublishAsync(@event, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _sinkFault ??= ex.Message;
        }
    }

    /// <summary>
    ///     校验历史中不存在不完整的工具原子组：每个 assistant tool_calls 的每个调用都必须
    ///     有对应的 tool 结果消息。取消或失败可能留下已请求但未回传结果的调用；把这样的
    ///     历史继续发给模型是无效请求，必须在追加新输入前拒绝。
    ///     Verifies the history carries no incomplete tool group: every assistant tool
    ///     call must have a matching tool-result message. A cancel or failure can
    ///     leave calls whose results never arrived; continuing would send invalid history,
    ///     so the turn is refused before any new input is appended.
    /// </summary>
    private void EnsureCompleteToolGroups()
    {
        HashSet<string>? pending = null;
        foreach (var message in _context.Messages)
            switch (message.Role)
            {
                case ChatRole.Assistant :
                    if (pending is { Count: > 0 }) throw IncompleteTail(pending);

                    pending = message.ToolCalls is { Count: > 0 }
                        ? new HashSet<string>(message.ToolCalls.Select(call => call.Id), StringComparer.Ordinal)
                        : null;
                    break;

                case ChatRole.Tool :
                    if (pending is not null && message.ToolCallId is { } id) pending.Remove(id);
                    break;

                case ChatRole.User or ChatRole.System :
                    if (pending is { Count: > 0 }) throw IncompleteTail(pending);
                    break;
            }

        if (pending is { Count: > 0 }) throw IncompleteTail(pending);
        return;

        static InvalidOperationException IncompleteTail(IReadOnlyCollection<string> pending) =>
            new("The conversation contains an incomplete tool round: assistant tool call(s) "                +
                string.Join(", ", pending)                                                                   +
                " have no tool result. The previous run was cancelled or failed mid-round, so the executed " +
                "work may have an unknown state; continuing would send malformed history to the model. "     +
                "Inspect the workspace before starting new work.");
    }

    /// <summary>
    ///     用当前模型视图（经 Context Manager 构建）和已注册工具构造下一次模型请求。
    ///     Builds the next model request from the current model view and registered tools.
    /// </summary>
    private ChatCompletionRequest BuildRequest(IReadOnlyList<ToolDefinition> definitions) => new()
    {
        Model    = _options.Model,
        Messages = _context.BuildModelView(),
        Tools    = definitions.Count == 0 ? null : definitions
    };

    /// <summary>
    ///     返回随普通请求发送的工具定义；注册表为空时不发送 tools。
    ///     Returns the tool definitions sent with regular requests, or none when the registry is empty.
    /// </summary>
    private List<ToolDefinition>? CurrentToolDefinitions()
    {
        if (_tools.Count == 0) return null;

        var definitions = new List<ToolDefinition>(_tools.Count);
        foreach (var tool in _tools.Values) definitions.Add(tool.Definition);

        return definitions;
    }

    /// <summary>
    ///     在预算超限且存在可折叠旧完整回合时执行一次上下文压缩：以禁用工具的摘要请求把最旧一批
    ///     旧回合汇总为结构化状态。摘要失败或格式非法时回滚并锁定重试，绝不破坏本地完整历史。
    ///     返回 <see langword="true" /> 表示本次压缩已应用；调用方应在预算仍超限时继续调用，直到
    ///     返回 <see langword="false" />（视图已容纳或没有更多可折叠内容）。
    ///     Runs one compaction when the budget is exceeded and foldable complete turns exist:
    ///     a tools-disabled summary request folds the oldest batch of turns into structured
    ///     state. Failures and malformed summaries roll back and latch retries without ever
    ///     corrupting the retained full history. Returns <see langword="true" /> when one
    ///     compaction was applied; the caller keeps calling while the budget is still
    ///     exceeded, until <see langword="false" /> means the view fits or nothing foldable
    ///     is left.
    /// </summary>
    private async Task<bool> CompactIfNeededAsync(
        IReadOnlyList<ToolDefinition> definitions, CancellationToken cancellationToken)
    {
        if (!_context.Enabled) return false;

        if (!_context.RequiresCompaction(definitions)) return false;

        var before       = _context.EstimateViewTokens();
        var foldMessages = _context.BuildCompactionMessages(definitions);
        if (foldMessages.Count == 0) return false;

        ChatMessage summary;
        try
        {
            // PLAN §13: 摘要调用禁用 tools。The summary call disables tools.
            var summaryRequest = new ChatCompletionRequest
            {
                Model    = _options.Model,
                Messages = foldMessages,
                Tools    = null
            };
            // The summary request calibrates on its own request-scope input estimate
            // (its messages, no tool definitions), captured before the call like
            // every other model request. Its text stream is internal and never
            // published as answer deltas; its usage still publishes, flagged as a
            // summary request.
            var estimatedSummaryTokens = TokenEstimator.EstimateMessages(foldMessages);
            summary = await RequestAssistantMessageAsync(summaryRequest, cancellationToken, estimatedSummaryTokens,
                                                         false)
               .ConfigureAwait(false);
            if (summary.ToolCalls is { Count: > 0 })
                throw new InvalidOperationException("The compaction summary unexpectedly requested tools.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A failed summary call leaves the retained context untouched; latch
            // the attempt so the loop does not retry before the conversation grows.
            _context.MarkCompactionAttempted();
            return false;
        }

        if (!_context.TryApplyCompaction(summary.Content)) return false;

        var after  = _context.EstimateViewTokens();
        var change = new ContextChange(before, after);
        await PublishEventAsync(HostEventKind.CompactionApplied, change, cancellationToken).ConfigureAwait(false);
        if (_recorder is not null)
            await _recorder.RecordCompactionAsync(change, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    ///     消费一次模型流，将文本与分片工具调用汇总成单条完整的 assistant 消息；流结束后把
    ///     服务端报告的 usage 与结束原因（如有）写入审计，并用实测输入 token 校准预算估算。
    ///     usage 与结束原因全部缺失时不写审计行，避免 JSONL 噪音。
    ///     Consumes one model stream and assembles its text and fragmented tool calls
    ///     into one assistant message. After the stream, any server-reported usage and
    ///     finish reason land in the audit trail, and the actual input tokens calibrate
    ///     the budget estimates against the pre-request estimate. A stream reporting
    ///     neither usage nor a finish reason writes no audit line to keep the JSONL
    ///     free of noise.
    /// </summary>
    /// <param name="publishTextDeltas">
    ///     是否把正文增量以 <see cref="HostEventKind.AssistantTextDelta" /> 发布（增量原样、
    ///     跨片段脱敏由宿主负责）；压缩摘要请求传 <see langword="false" />，内部摘要文本不进入
    ///     回答展示。
    ///     Whether content deltas publish as <see cref="HostEventKind.AssistantTextDelta" />
    ///     (verbatim; cross-fragment redaction is the host's job); compaction summary
    ///     requests pass <see langword="false" /> so internal summary text never enters the
    ///     answer display.
    /// </param>
    private async Task<ChatMessage> RequestAssistantMessageAsync(ChatCompletionRequest request,
                                                                 CancellationToken     cancellationToken,
                                                                 int                   estimatedPromptTokens,
                                                                 bool                  publishTextDeltas)
    {
        var accumulator = new StreamAccumulator();

        await foreach (var @event in _model.CompleteAsync(request, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            accumulator.Append(@event);
            if (publishTextDeltas && @event.Kind == ChatStreamEventKind.ContentDelta)
                await PublishEventAsync(HostEventKind.AssistantTextDelta, @event.ContentDelta, cancellationToken)
                   .ConfigureAwait(false);
        }

        accumulator.Finish();

        // Land the server-reported usage and finish reason (when any) in the audit
        // trail. Compaction summary calls flow through here too, so their usage is
        // recorded the same way without special casing.
        if (accumulator.InputTokens is not null  ||
            accumulator.OutputTokens is not null ||
            accumulator.FinishReason is not null)
        {
            if (_recorder is not null)
                await _recorder.RecordModelUsageAsync(accumulator.InputTokens, accumulator.OutputTokens,
                                                      accumulator.FinishReason, cancellationToken)
                               .ConfigureAwait(false);

            // Every model request — summary calls included — publishes its usage and
            // joins the turn's totals; the summary flag lets hosts tell the two apart.
            var usage = new ModelUsage(accumulator.InputTokens, accumulator.OutputTokens,
                                       accumulator.FinishReason, !publishTextDeltas);
            await PublishEventAsync(HostEventKind.ModelUsage, usage, cancellationToken).ConfigureAwait(false);
            if (accumulator.InputTokens is { } inputTokens)
                _reportedInputTokens = (_reportedInputTokens ?? 0) + inputTokens;

            if (accumulator.OutputTokens is { } outputTokens)
                _reportedOutputTokens = (_reportedOutputTokens ?? 0) + outputTokens;
        }

        // PLAN §13: calibrate the estimator with the measured input of this request
        // against the estimate captured before it was sent; ignored when no
        // reliable usage arrived.
        if (accumulator.InputTokens is { } actualInputTokens)
            _context.RecordModelUsage(estimatedPromptTokens, actualInputTokens);

        return ChatMessage.Assistant(accumulator.Content, accumulator.ToolCalls, accumulator.Reasoning);
    }

    /// <summary>
    ///     在任何工具执行前准备并校验本轮全部调用，确保一个无效调用不会留下同轮的部分副作用。
    ///     Prepares every call of one model round before any of them may execute.
    ///     Preparation is pure and side-effect free, so an un-preparable call is
    ///     known before a single tool of the round runs.
    /// </summary>
    private static IReadOnlyList<RoundCall> PrepareRound(ToolRegistry tools, IReadOnlyList<ChatToolCall> calls)
    {
        var round = new List<RoundCall>(calls.Count);
        foreach (var call in calls)
        {
            if (!tools.TryGetValue(call.FunctionName, out var tool))
            {
                round.Add(RoundCall.Failed(call, $"Unknown tool: {call.FunctionName}"));
                continue;
            }

            try
            {
                var preparation = tool.Prepare(call);
                round.Add(RoundCall.Ready(call, tool, preparation));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                round.Add(RoundCall.Failed(call, $"Tool '{call.FunctionName}' failed to prepare: {ex.Message}"));
            }
        }

        return round;
    }

    /// <summary>
    ///     生成整轮被无效调用拦截时，其余调用未执行的原因。
    ///     Explains why another call in a round containing an invalid call was not executed.
    /// </summary>
    private static string NotExecutedReason(RoundCall invalid) =>
        $"Not executed: the round contains an invalid call '{invalid.Call.Id}' ({invalid.Call.FunctionName}) — " +
        $"{invalid.Error}";

    /// <summary>
    ///     执行已经准备并获批的调用；工具异常会在此转换为可回传模型的失败结果。
    ///     Executes an already prepared and approved call. Runs only after every call
    ///     of the round has been prepared and every permission decision is known.
    /// </summary>
    private static async Task<ToolResult> ExecutePreparedAsync(
        ITool tool, ToolPreparation preparation, CancellationToken cancellationToken)
    {
        try
        {
            return await tool.ExecuteAsync(preparation, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ToolResult
            {
                Succeeded = false,
                Content   = $"Tool '{preparation.ToolName}' failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    ///     计算权限决策；需要询问时调用审批器，并正确处理单次授权、会话授权和拒绝。
    ///     拒绝会作为工具结果返回模型，只有任务级取消才终止整个运行。
    ///     Decides and, when the engine asks, prompts the user before returning.
    ///     "Allow session" is recorded in the engine; interactive "allow once" and
    ///     "deny" cover only the invocation immediately following the prompt and are
    ///     not persisted. A denial is returned as a tool result so the model can
    ///     adjust its plan; only user-level cancellation terminates the run.
    /// </summary>
    private async Task<AuthorizationOutcome> AuthorizeAsync(
        ToolPreparation preparation, CancellationToken cancellationToken)
    {
        var decision = _permissions!.Decide(preparation);
        switch (decision)
        {
            case PermissionDecision.Allow :
                // A pre-authorized one-shot must be reserved during the
                // authorization phase. Waiting until execution would let two
                // identical calls in the same round reuse the same grant.
                _permissions.TryConsumeOnce(preparation);
                await RecordPermissionAsync(preparation, decision, "allow", cancellationToken).ConfigureAwait(false);
                await PublishResolutionAsync(decision, null, "allow", cancellationToken).ConfigureAwait(false);
                return AuthorizationOutcome.Granted;

            case PermissionDecision.Deny :
                var policyDenied = AuthorizationOutcome.Denied($"Permission denied: {preparation.Summary}");
                await RecordPermissionAsync(preparation, decision, policyDenied.Reason, cancellationToken)
                   .ConfigureAwait(false);
                await PublishResolutionAsync(decision, null, policyDenied.Reason, cancellationToken)
                   .ConfigureAwait(false);
                return policyDenied;

            case PermissionDecision.Ask :
                var approvalProvider = _approver ?? throw new InvalidOperationException(
                 "An approval provider is required when the permission engine returns Ask.");
                await PublishEventAsync(HostEventKind.ApprovalRequested, preparation, cancellationToken)
                   .ConfigureAwait(false);
                var action = await approvalProvider.PromptAsync(preparation, cancellationToken);
                switch (action)
                {
                    case ApprovalAction.AllowOnce :
                        // The interactive approval covers exactly the invocation
                        // that executes right after this prompt. Storing a grant
                        // in the engine would let it outlive that execution and
                        // auto-approve an identical later call, so nothing is
                        // recorded here.
                        await RecordPermissionAsync(preparation, decision, "allow once", cancellationToken)
                           .ConfigureAwait(false);
                        await PublishResolutionAsync(decision, action, "allow once", cancellationToken)
                           .ConfigureAwait(false);
                        return AuthorizationOutcome.Granted;

                    case ApprovalAction.AllowSession :
                        _permissions.GrantSession(preparation);
                        await RecordPermissionAsync(preparation, decision, "allow session", cancellationToken)
                           .ConfigureAwait(false);
                        await PublishResolutionAsync(decision, action, "allow session", cancellationToken)
                           .ConfigureAwait(false);
                        return AuthorizationOutcome.Granted;

                    default :
                        var userDenied =
                            AuthorizationOutcome.Denied($"Permission denied by user: {preparation.Summary}");
                        await RecordPermissionAsync(preparation, decision, userDenied.Reason, cancellationToken)
                           .ConfigureAwait(false);
                        await PublishResolutionAsync(decision, ApprovalAction.Deny, userDenied.Reason,
                                                     cancellationToken)
                           .ConfigureAwait(false);
                        return userDenied;
                }

            default :
                throw new InvalidOperationException($"Unknown permission decision: {decision}");
        }
    }

    /// <summary>
    ///     发布审批结论事件；与审计记录一一对应，宿主据此呈现每次权限结论（含规则自动
    ///     允许/拒绝），事件通道只读，不反向影响执行。
    ///     Publishes the approval-resolution event, one per audit-recorded decision, so
    ///     hosts can render every outcome (rule-based allows and denies included); the
    ///     event channel stays read-only and never influences execution.
    /// </summary>
    private async ValueTask PublishResolutionAsync(
        PermissionDecision decision, ApprovalAction? action, string outcome, CancellationToken cancellationToken)
    {
        await PublishEventAsync(HostEventKind.ApprovalResolved, new ApprovalResolution(decision, action, outcome),
                                cancellationToken)
           .ConfigureAwait(false);
    }

    private async Task RecordPermissionAsync(ToolPreparation   preparation, PermissionDecision decision, string outcome,
                                             CancellationToken cancellationToken)
    {
        if (_recorder is not null)
            await _recorder.RecordPermissionAsync(preparation, decision, outcome, cancellationToken)
                           .ConfigureAwait(false);
    }

    /// <summary>
    ///     表示模型一轮中已经准备完成或准备失败的单个调用。
    ///     Represents one model-round call after preparation, either ready or failed.
    /// </summary>
    private sealed record RoundCall(ChatToolCall Call, ITool? Tool, ToolPreparation? Preparation, string? Error)
    {
        /// <summary>
        ///     创建可进入授权阶段的调用记录。
        ///     Creates a call record ready for authorization.
        /// </summary>
        public static RoundCall Ready(ChatToolCall call, ITool tool, ToolPreparation preparation) =>
            new(call, tool, preparation, null);

        /// <summary>
        ///     创建携带准备错误、禁止执行的调用记录。
        ///     Creates a non-executable call record carrying its preparation error.
        /// </summary>
        public static RoundCall Failed(ChatToolCall call, string error) => new(call, null, null, error);
    }

    /// <summary>
    ///     将准备结果与对应权限结论绑定，供顺序执行阶段使用。
    ///     Couples a prepared round call with its authorization outcome for sequential dispatch.
    /// </summary>
    private sealed record AuthorizedRoundCall(RoundCall RoundCall, AuthorizationOutcome Authorization);

    /// <summary>
    ///     权限阶段的内部结果，包含是否允许以及拒绝时返回模型的原因。
    ///     Internal authorization result containing the allow flag and a model-facing denial reason.
    /// </summary>
    private sealed record AuthorizationOutcome(bool Allowed, string Reason)
    {
        public static AuthorizationOutcome Granted { get; } = new(true, string.Empty);

        /// <summary>
        ///     创建带说明的拒绝结果。
        ///     Creates a denied outcome with an explanatory reason.
        /// </summary>
        public static AuthorizationOutcome Denied(string reason) => new(false, reason);
    }
}

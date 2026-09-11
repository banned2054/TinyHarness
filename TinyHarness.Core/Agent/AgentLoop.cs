using System.Text.Json;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Context;
using TinyHarness.Core.Permissions;
using TinyHarness.Core.Persistence;
using TinyHarness.Core.Tools;

namespace TinyHarness.Core.Agent;

/// <summary>
/// Agent 主循环。负责请求模型、消费流、组装工具调用、整轮准备与授权、顺序执行工具，
/// 并将结果交还模型；当模型返回纯文本、达到步数限制或任务取消时结束。每一步请求前，
/// 主循环会检查 Context Manager：当预算超限且存在旧完整回合时，先以禁用工具的摘要调用
/// 压缩旧历史，再发送模型视图。压缩只发生在两次模型请求之间，绝不会在等待审批或工具
/// 执行期间进行。
/// 未提供 <see cref="PermissionEngine"/> 时会跳过授权，仅用于测试或简单的只读流程。
///
/// The Agent main loop. Request the model, consume the stream, assemble tool
/// calls, prepare every call of the round, authorize and execute the approved
/// ones, hand results back, and repeat until the model produces a plain-text
/// turn, a limit is reached, or the run is cancelled. Before each request the
/// loop consults its Context Manager: when the budget is exceeded and old
/// complete turns exist it first compacts them with a tools-disabled summary
/// call, then sends the model view. Compaction only runs between two model
/// requests, never while an approval is pending or a tool is executing.
///
/// When no <see cref="PermissionEngine"/> is supplied (test/simple read-only
/// scenarios), authorization is skipped and every prepared invocation executes.
/// </summary>
public sealed class AgentLoop
{
    private readonly IChatCompletionClient _model;
    private readonly ToolRegistry          _tools;
    private readonly AgentOptions          _options;
    private readonly PermissionEngine?     _permissions;
    private readonly IApprovalProvider?    _approver;
    private readonly ConversationContext   _context;
    private readonly IRunRecorder?         _recorder;

    /// <summary>
    /// 每次成功压缩后触发，携带压缩前后的视图估算 token 数（CLI 用于展示）。
    /// Raised after every successful compaction with the view estimates before and after (shown by the CLI).
    /// </summary>
    public event Action<ContextChange>? ContextCompacted;

    public AgentLoop(IChatCompletionClient model,       ToolRegistry       tools, AgentOptions options,
                     PermissionEngine?     permissions, IApprovalProvider? approver,
                     IRunRecorder?         recorder = null)
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
        _context     = new ConversationContext(options.Context);
    }

    /// <summary>
    /// 创建不启用权限检查的主循环，仅供测试和只读流程使用；CLI 始终注入权限引擎。
    ///
    /// Runs without authorization. Intended for tests and for read-only flows;
    /// the CLI composition root always supplies a Permission Engine instead.
    /// </summary>
    public AgentLoop(IChatCompletionClient model, ToolRegistry tools, AgentOptions options)
        : this(model, tools, options, permissions : null, approver : null)
    {
    }

    public IReadOnlyList<ChatMessage> History => _context.Messages;

    /// <summary>
    /// 从系统提示和用户输入开始运行一次完整 Agent 任务，并返回终止状态、最终文本及执行统计。
    /// Runs one complete agent task from the supplied prompts and returns its terminal state, final text,
    /// and execution counts.
    /// </summary>
    public async Task<AgentResult> RunAsync(string systemPrompt, string userInput, CancellationToken cancellationToken)
    {
        _context.Reset();
        _context.Append(ChatMessage.System(systemPrompt));
        _context.Append(ChatMessage.User(userInput));

        var steps          = 0;
        var toolExecutions = 0;
        var compactions    = 0;

        try
        {
            if (_recorder is not null)
            {
                await _recorder.StartAsync(systemPrompt, userInput, cancellationToken).ConfigureAwait(false);
            }

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
                while (await CompactIfNeededAsync(definitions, cancellationToken).ConfigureAwait(false))
                {
                    compactions++;
                }

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
                    {
                        return await FinishAsync(new AgentResult
                        {
                            Status         = AgentStatus.Failed,
                            Steps          = steps,
                            ToolExecutions = toolExecutions,
                            Compactions    = compactions,
                            Error = $"The context still exceeds the model window after compaction " +
                                    $"({total} > {context.ContextWindowTokens} tokens); the request was not sent.",
                        }).ConfigureAwait(false);
                    }
                }

                var request   = BuildRequest(definitions);
                var assistant = await RequestAssistantMessageAsync(request, cancellationToken);

                _context.Append(assistant);

                if (assistant.ToolCalls is null || assistant.ToolCalls.Count == 0)
                {
                    return await FinishAsync(new AgentResult
                    {
                        Status         = AgentStatus.Completed,
                        FinalMessage   = assistant.Content,
                        Steps          = steps,
                        ToolExecutions = toolExecutions,
                        Compactions    = compactions,
                    }).ConfigureAwait(false);
                }

                // Prepare and validate every call of the round before any of
                // them executes. A round that contains an un-preparable
                // call (unknown tool, malformed or out-of-bounds arguments) is
                // gated as a whole: nothing runs, so an invalid sibling call can
                // never leave side effects from the valid ones behind. When the
                // whole round prepares, each call is authorized and executed in
                // order.
                var round = PrepareRound(_tools, assistant.ToolCalls);
                if (_recorder is not null)
                {
                    foreach (var prepared in round)
                    {
                        if (prepared.Preparation is not null)
                        {
                            await _recorder.RecordPreparedAsync(prepared.Preparation, cancellationToken)
                                           .ConfigureAwait(false);
                        }
                    }
                }

                var invalid = round.FirstOrDefault(item => item.Error is not null);
                if (invalid is not null)
                {
                    foreach (var item in round)
                    {
                        _context.Append(ChatMessage.Tool(item.Call.FunctionName, item.Call.Id,
                                                         item.Error ?? NotExecutedReason(invalid)));
                    }

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
                    var result = await ExecutePreparedAsync(item.RoundCall.Tool!, item.RoundCall.Preparation!,
                                                            cancellationToken);

                    // The side effect already happened. Preserve its result in the
                    // in-memory history before attempting fallible audit I/O so a
                    // recorder failure cannot erase or cause a replay of the tool.
                    _context.Append(ChatMessage.Tool(item.RoundCall.Call.FunctionName, item.RoundCall.Call.Id,
                                                     result.Content));
                    if (_recorder is not null)
                    {
                        await _recorder.RecordResultAsync(item.RoundCall.Preparation!, result, cancellationToken)
                                       .ConfigureAwait(false);
                    }
                }
            }

            return await FinishAsync(new AgentResult
            {
                Status         = AgentStatus.StepLimitReached,
                FinalMessage   = string.Empty,
                Steps          = steps,
                ToolExecutions = toolExecutions,
                Compactions    = compactions,
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
                Error          = "Run cancelled",
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
                Error          = ex.Message,
            }).ConfigureAwait(false);
        }
    }

    private async Task<AgentResult> FinishAsync(AgentResult result)
    {
        if (_recorder is not null)
        {
            try
            {
                await _recorder.CompleteAsync(result, _context.Messages, _context.State, CancellationToken.None)
                               .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return result with
                {
                    Status = AgentStatus.Failed,
                    Error = string.IsNullOrWhiteSpace(result.Error)
                        ? $"Run completed but persistence failed: {ex.Message}"
                        : $"{result.Error}; persistence failed: {ex.Message}",
                };
            }
        }

        return result;
    }

    /// <summary>
    /// 用当前模型视图（经 Context Manager 构建）和已注册工具构造下一次模型请求。
    /// Builds the next model request from the current model view and registered tools.
    /// </summary>
    private ChatCompletionRequest BuildRequest(IReadOnlyList<ToolDefinition> definitions) => new()
    {
        Model    = _options.Model,
        Messages = _context.BuildModelView(),
        Tools    = definitions.Count == 0 ? null : definitions,
    };

    /// <summary>
    /// 返回随普通请求发送的工具定义；注册表为空时不发送 tools。
    /// Returns the tool definitions sent with regular requests, or none when the registry is empty.
    /// </summary>
    private List<ToolDefinition>? CurrentToolDefinitions()
    {
        if (_tools.Count == 0)
        {
            return null;
        }

        var definitions = new List<ToolDefinition>(_tools.Count);
        foreach (var tool in _tools.Values)
        {
            definitions.Add(tool.Definition);
        }

        return definitions;
    }

    /// <summary>
    /// 在预算超限且存在可折叠旧完整回合时执行一次上下文压缩：以禁用工具的摘要请求把最旧一批
    /// 旧回合汇总为结构化状态。摘要失败或格式非法时回滚并锁定重试，绝不破坏本地完整历史。
    /// 返回 <see langword="true"/> 表示本次压缩已应用；调用方应在预算仍超限时继续调用，直到
    /// 返回 <see langword="false"/>（视图已容纳或没有更多可折叠内容）。
    ///
    /// Runs one compaction when the budget is exceeded and foldable complete turns exist:
    /// a tools-disabled summary request folds the oldest batch of turns into structured
    /// state. Failures and malformed summaries roll back and latch retries without ever
    /// corrupting the retained full history. Returns <see langword="true"/> when one
    /// compaction was applied; the caller keeps calling while the budget is still
    /// exceeded, until <see langword="false"/> means the view fits or nothing foldable
    /// is left.
    /// </summary>
    private async Task<bool> CompactIfNeededAsync(IReadOnlyList<ToolDefinition> definitions,
                                                  CancellationToken             cancellationToken)
    {
        if (!_context.Enabled)
        {
            return false;
        }

        if (!_context.RequiresCompaction(definitions))
        {
            return false;
        }

        var before       = _context.EstimateViewTokens();
        var foldMessages = _context.BuildCompactionMessages(definitions);
        if (foldMessages.Count == 0)
        {
            return false;
        }

        ChatMessage summary;
        try
        {
            // PLAN §13: 摘要调用禁用 tools。The summary call disables tools.
            var summaryRequest = new ChatCompletionRequest
            {
                Model    = _options.Model,
                Messages = foldMessages,
                Tools    = null,
            };
            summary = await RequestAssistantMessageAsync(summaryRequest, cancellationToken).ConfigureAwait(false);
            if (summary.ToolCalls is { Count: > 0 })
            {
                throw new InvalidOperationException("The compaction summary unexpectedly requested tools.");
            }
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

        if (!_context.TryApplyCompaction(summary.Content))
        {
            return false;
        }

        var after  = _context.EstimateViewTokens();
        var change = new ContextChange(before, after);
        ContextCompacted?.Invoke(change);
        if (_recorder is not null)
        {
            await _recorder.RecordCompactionAsync(change, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// 消费一次模型流，将文本与分片工具调用汇总成单条完整的 assistant 消息。
    /// Consumes one model stream and assembles its text and fragmented tool calls into one assistant message.
    /// </summary>
    private async Task<ChatMessage> RequestAssistantMessageAsync(ChatCompletionRequest request,
                                                                 CancellationToken     cancellationToken)
    {
        var accumulator = new StreamAccumulator();

        await foreach (var @event in _model.CompleteAsync(request, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            accumulator.Append(@event);
        }

        accumulator.Finish();
        return ChatMessage.Assistant(accumulator.Content, accumulator.ToolCalls);
    }

    /// <summary>
    /// 在任何工具执行前准备并校验本轮全部调用，确保一个无效调用不会留下同轮的部分副作用。
    ///
    /// Prepares every call of one model round before any of them may execute.
    /// Preparation is pure and side-effect free, so an un-preparable call is
    /// known before a single tool of the round runs.
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
    /// 生成整轮被无效调用拦截时，其余调用未执行的原因。
    /// Explains why another call in a round containing an invalid call was not executed.
    /// </summary>
    private static string NotExecutedReason(RoundCall invalid)
        => $"Not executed: the round contains an invalid call '{invalid.Call.Id}' ({invalid.Call.FunctionName}) — " +
           $"{invalid.Error}";

    /// <summary>
    /// 执行已经准备并获批的调用；工具异常会在此转换为可回传模型的失败结果。
    ///
    /// Executes an already prepared and approved call. Runs only after every call
    /// of the round has been prepared and every permission decision is known.
    /// </summary>
    private static async Task<ToolResult> ExecutePreparedAsync(ITool             tool,
                                                               ToolPreparation   preparation,
                                                               CancellationToken cancellationToken)
    {
        try
        {
            return await tool.ExecuteAsync(preparation, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ToolResult
            {
                Succeeded = false, Content = $"Tool '{preparation.ToolName}' failed: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// 计算权限决策；需要询问时调用审批器，并正确处理单次授权、会话授权和拒绝。
    /// 拒绝会作为工具结果返回模型，只有任务级取消才终止整个运行。
    ///
    /// Decides and, when the engine asks, prompts the user before returning.
    /// "Allow session" is recorded in the engine; interactive "allow once" and
    /// "deny" cover only the invocation immediately following the prompt and are
    /// not persisted. A denial is returned as a tool result so the model can
    /// adjust its plan; only user-level cancellation terminates the run.
    /// </summary>
    private async Task<AuthorizationOutcome> AuthorizeAsync(ToolPreparation   preparation,
                                                            CancellationToken cancellationToken)
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
                return AuthorizationOutcome.Granted;

            case PermissionDecision.Deny :
                var policyDenied = AuthorizationOutcome.Denied($"Permission denied: {preparation.Summary}");
                await RecordPermissionAsync(preparation, decision, policyDenied.Reason, cancellationToken)
                   .ConfigureAwait(false);
                return policyDenied;

            case PermissionDecision.Ask :
                var approvalProvider = _approver ?? throw new InvalidOperationException(
                     "An approval provider is required when the permission engine returns Ask.");
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
                        return AuthorizationOutcome.Granted;

                    case ApprovalAction.AllowSession :
                        _permissions.GrantSession(preparation);
                        await RecordPermissionAsync(preparation, decision, "allow session", cancellationToken)
                           .ConfigureAwait(false);
                        return AuthorizationOutcome.Granted;

                    default :
                        var userDenied =
                            AuthorizationOutcome.Denied($"Permission denied by user: {preparation.Summary}");
                        await RecordPermissionAsync(preparation, decision, userDenied.Reason, cancellationToken)
                           .ConfigureAwait(false);
                        return userDenied;
                }

            default :
                throw new InvalidOperationException($"Unknown permission decision: {decision}");
        }
    }

    private async Task RecordPermissionAsync(ToolPreparation   preparation, PermissionDecision decision, string outcome,
                                             CancellationToken cancellationToken)
    {
        if (_recorder is not null)
        {
            await _recorder.RecordPermissionAsync(preparation, decision, outcome, cancellationToken)
                           .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 表示模型一轮中已经准备完成或准备失败的单个调用。
    /// Represents one model-round call after preparation, either ready or failed.
    /// </summary>
    private sealed record RoundCall(ChatToolCall Call, ITool? Tool, ToolPreparation? Preparation, string? Error)
    {
        /// <summary>
        /// 创建可进入授权阶段的调用记录。
        /// Creates a call record ready for authorization.
        /// </summary>
        public static RoundCall Ready(ChatToolCall call, ITool tool, ToolPreparation preparation)
            => new(call, tool, preparation, Error : null);

        /// <summary>
        /// 创建携带准备错误、禁止执行的调用记录。
        /// Creates a non-executable call record carrying its preparation error.
        /// </summary>
        public static RoundCall Failed(ChatToolCall call, string error)
            => new(call, Tool : null, Preparation : null, Error : error);
    }

    /// <summary>
    /// 将准备结果与对应权限结论绑定，供顺序执行阶段使用。
    /// Couples a prepared round call with its authorization outcome for sequential dispatch.
    /// </summary>
    private sealed record AuthorizedRoundCall(RoundCall RoundCall, AuthorizationOutcome Authorization);

    /// <summary>
    /// 权限阶段的内部结果，包含是否允许以及拒绝时返回模型的原因。
    /// Internal authorization result containing the allow flag and a model-facing denial reason.
    /// </summary>
    private sealed record AuthorizationOutcome(bool Allowed, string Reason)
    {
        public static AuthorizationOutcome Granted { get; } = new(true, string.Empty);

        /// <summary>
        /// 创建带说明的拒绝结果。
        /// Creates a denied outcome with an explanatory reason.
        /// </summary>
        public static AuthorizationOutcome Denied(string reason) => new(false, reason);
    }
}

using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Permissions;
using TinyHarness.Core.Tools;

namespace TinyHarness.Core.Agent;

/// <summary>
/// Agent 主循环。负责请求模型、消费流、组装工具调用、整轮准备与授权、顺序执行工具，
/// 并将结果交还模型；当模型返回纯文本、达到步数限制或任务取消时结束。
/// 未提供 <see cref="PermissionEngine"/> 时会跳过授权，仅用于测试或简单的只读流程。
///
/// The Agent main loop. Request the model, consume the stream, assemble tool
/// calls, prepare every call of the round, authorize and execute the approved
/// ones, hand results back, and repeat until the model produces a plain-text
/// turn, a limit is reached, or the run is cancelled.
///
/// When no <see cref="PermissionEngine"/> is supplied (test/simple read-only
/// scenarios), authorization is skipped and every prepared invocation executes.
/// </summary>
public sealed class AgentLoop(
    IChatCompletionClient model,
    ToolRegistry          tools,
    AgentOptions          options,
    PermissionEngine?     permissions,
    IApprovalProvider?    approver)
{
    private readonly List<ChatMessage> _history = [];

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

    public IReadOnlyList<ChatMessage> History => _history;

    /// <summary>
    /// 从系统提示和用户输入开始运行一次完整 Agent 任务，并返回终止状态、最终文本及执行统计。
    /// Runs one complete agent task from the supplied prompts and returns its terminal state, final text,
    /// and execution counts.
    /// </summary>
    public async Task<AgentResult> RunAsync(string systemPrompt, string userInput, CancellationToken cancellationToken)
    {
        _history.Clear();
        _history.Add(ChatMessage.System(systemPrompt));
        _history.Add(ChatMessage.User(userInput));

        var steps          = 0;
        var toolExecutions = 0;

        try
        {
            while (steps < options.MaxAgentSteps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                steps++;

                var request   = BuildRequest();
                var assistant = await RequestAssistantMessageAsync(request, cancellationToken);

                _history.Add(assistant);

                if (assistant.ToolCalls is null || assistant.ToolCalls.Count == 0)
                {
                    return new AgentResult
                    {
                        Status         = AgentStatus.Completed,
                        FinalMessage   = assistant.Content,
                        Steps          = steps,
                        ToolExecutions = toolExecutions,
                    };
                }

                // Prepare and validate every call of the round before any of
                // them executes. A round that contains an un-preparable
                // call (unknown tool, malformed or out-of-bounds arguments) is
                // gated as a whole: nothing runs, so an invalid sibling call can
                // never leave side effects from the valid ones behind. When the
                // whole round prepares, each call is authorized and executed in
                // order.
                var round   = PrepareRound(tools, assistant.ToolCalls);
                var invalid = round.FirstOrDefault(item => item.Error is not null);
                if (invalid is not null)
                {
                    foreach (var item in round)
                    {
                        _history.Add(ChatMessage.Tool(item.Call.FunctionName, item.Call.Id,
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
                    var authorization = permissions is null
                        ? AuthorizationOutcome.Granted
                        : await AuthorizeAsync(item.Preparation!, cancellationToken);
                    authorized.Add(new AuthorizedRoundCall(item, authorization));
                }

                foreach (var item in authorized)
                {
                    if (!item.Authorization.Allowed)
                    {
                        _history.Add(ChatMessage.Tool(item.RoundCall.Call.FunctionName, item.RoundCall.Call.Id,
                                                      item.Authorization.Reason));
                        continue;
                    }

                    toolExecutions++;
                    var result = await ExecutePreparedAsync(item.RoundCall.Tool!, item.RoundCall.Preparation!,
                                                            cancellationToken);
                    _history.Add(ChatMessage.Tool(item.RoundCall.Call.FunctionName, item.RoundCall.Call.Id,
                                                  result.Content));
                }
            }

            return new AgentResult
            {
                Status         = AgentStatus.StepLimitReached,
                FinalMessage   = string.Empty,
                Steps          = steps,
                ToolExecutions = toolExecutions,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new AgentResult
            {
                Status         = AgentStatus.Cancelled,
                Steps          = steps,
                ToolExecutions = toolExecutions,
                Error          = "Run cancelled",
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentResult
            {
                Status         = AgentStatus.Failed,
                Steps          = steps,
                ToolExecutions = toolExecutions,
                Error          = ex.Message,
            };
        }
    }

    /// <summary>
    /// 用当前完整历史和已注册工具构造下一次模型请求。
    /// Builds the next model request from the current history and registered tools.
    /// </summary>
    private ChatCompletionRequest BuildRequest()
    {
        var tools1 = tools.Count == 0 ? null : tools.Values.Select(t => t.Definition).ToList();

        return new ChatCompletionRequest
        {
            Model    = options.Model,
            Messages = _history,
            Tools    = tools1,
        };
    }

    /// <summary>
    /// 消费一次模型流，将文本与分片工具调用汇总成单条完整的 assistant 消息。
    /// Consumes one model stream and assembles its text and fragmented tool calls into one assistant message.
    /// </summary>
    private async Task<ChatMessage> RequestAssistantMessageAsync(ChatCompletionRequest request,
                                                                 CancellationToken     cancellationToken)
    {
        var accumulator = new StreamAccumulator();

        await foreach (var @event in model.CompleteAsync(request, cancellationToken))
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
                round.Add(RoundCall.Ready(call, tool, tool.Prepare(call)));
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
        var decision = permissions!.Decide(preparation);
        switch (decision)
        {
            case PermissionDecision.Allow :
                // A pre-authorized one-shot must be reserved during the
                // authorization phase. Waiting until execution would let two
                // identical calls in the same round reuse the same grant.
                permissions.TryConsumeOnce(preparation);
                return AuthorizationOutcome.Granted;

            case PermissionDecision.Deny :
                return AuthorizationOutcome.Denied($"Permission denied: {preparation.Summary}");

            case PermissionDecision.Ask :
                var approvalProvider = approver ?? throw new InvalidOperationException(
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
                        return AuthorizationOutcome.Granted;

                    case ApprovalAction.AllowSession :
                        permissions.GrantSession(preparation);
                        return AuthorizationOutcome.Granted;

                    default :
                        return AuthorizationOutcome.Denied($"Permission denied by user: {preparation.Summary}");
                }

            default :
                throw new InvalidOperationException($"Unknown permission decision: {decision}");
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

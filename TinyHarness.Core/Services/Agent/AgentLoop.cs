using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.ProjectInstructions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Core.Services.Agent;

internal enum InstructionBoundaryStatus
{
    Unchanged,
    Updated,
    ScopeTooBroad,
    Failed
}

internal readonly record struct InstructionBoundaryCheck(
    InstructionBoundaryStatus Status,
    string?                   Error,
    string?                   NewRevision,
    InstructionCoverage?      Coverage);

/// <summary>
///     <para>宿主提供给循环的项目指令边界；所有可靠提交和生效仍由宿主协调。</para>
///     <para>The project-instruction boundary supplied by the host; reliable commits and activation remain host-owned.</para>
/// </summary>
internal interface IAgentInstructionBoundary
{
    Task PrepareUserContextAsync(CancellationToken cancellationToken);

    Task<ProjectInstructionsChanged?> ConfirmInitialCommitAsync(CancellationToken cancellationToken);

    Task<ProjectInstructionsChanged?> RefreshBeforeModelRequestAsync(CancellationToken cancellationToken);

    Task<InstructionBoundaryCheck> CheckToolRoundAsync(
        IReadOnlyList<ToolPreparation> preparations, CancellationToken cancellationToken);

    Task<InstructionBoundaryCheck> RecheckBeforeDispatchAsync(
        IReadOnlyList<ToolPreparation> preparations, CancellationToken cancellationToken);

    Task<ProjectInstructionsChanged?> ApplyToolRoundUpdateAsync(
        IReadOnlyList<ToolCallRecord> records, CancellationToken cancellationToken);
}

/// <summary>
///     <para>
///         驱动单回合的模型请求、工具授权与顺序执行，维护上下文和宿主事件。
///     </para>
///     <para>
///         Drives model requests, authorization and sequential tool execution for one turn,
///         maintaining context and host events.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         压缩仅在模型请求之间进行，不在审批或工具执行期间进行。
///         共享 ConversationContext 可用于续聊；事件身份由宿主补齐。
///         未注入 PermissionEngine 时跳过授权，仅供测试或简单只读流程使用。
///     </para>
///     <para>
///         Compaction runs only between model requests, never during approval or tool execution.
///         A shared ConversationContext supports continuation; the host stamps event identity.
///         Without PermissionEngine, authorization is skipped; this mode is intended only
///         for tests or simple read-only flows.
///     </para>
/// </remarks>
public sealed partial class AgentLoop
{
    private readonly IApprovalProvider?  _approver;
    private readonly ConversationContext _context;
    private readonly IHostEventSink?     _events;

    private readonly Func<IReadOnlyList<ToolCallRecord>, CancellationToken, Task>? _executionCheckpoint;

    private readonly IAgentInstructionBoundary? _instructionBoundary;
    private readonly IChatCompletionClient      _model;
    private readonly AgentOptions               _options;
    private readonly PermissionEngine?          _permissions;
    private readonly IRunRecorder?              _recorder;

    /// <summary>
    ///     <para>
    ///         本次运行的工具执行意图与确认记录，供宿主进行中断结算。
    ///     </para>
    ///     <para>
    ///         Execution-intent and confirmation records for host interruption settlement.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         准备、授权、启动和结果回传分别更新记录；执行窗口内取消时保留结果未知，不补造终态。
    ///     </para>
    ///     <para>
    ///         Preparation, authorization, dispatch and result append update the records.
    ///         Cancellation within the execution window leaves an unknown result, never a fabricated terminal state.
    ///     </para>
    /// </remarks>
    private readonly List<ToolCallRecord> _toolCallRecords = [];

    private readonly ToolRegistry _tools;
    private          RunBudget?   _budget;

    private long _eventSequence;
    private long _nextInvocationSequence;

    /// <summary>
    ///     <para>
    ///         本回合服务端报告的累计输入 token，包含压缩摘要请求。
    ///     </para>
    ///     <para>
    ///         Cumulative server-reported input tokens for this turn, including compaction requests.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         未报告可靠 usage 的请求不计入累计；全程无报告时保持 null。
    ///     </para>
    ///     <para>
    ///         Requests without reliable usage do not contribute; the total stays null if none report it.
    ///     </para>
    /// </remarks>
    private long? _reportedInputTokens;

    private long? _reportedOutputTokens;

    /// <summary>
    ///     <para>
    ///         本回合首个事件接收方故障，随终态结果并入 Error。
    ///     </para>
    ///     <para>
    ///         The first event-sink fault of this turn, merged into Error on the terminal result.
    ///     </para>
    /// </summary>
    private string? _sinkFault;

    public AgentLoop(IChatCompletionClient model, ToolRegistry tools, AgentOptions options,
                     PermissionEngine? permissions, IApprovalProvider? approver, IRunRecorder? recorder = null,
                     IHostEventSink? events = null, ConversationContext? context = null,
                     Func<IReadOnlyList<ToolCallRecord>, CancellationToken, Task>? executionCheckpoint = null)
        : this(model, tools, options, permissions, approver, recorder, events, context, executionCheckpoint, null)
    {
    }

    internal AgentLoop(IChatCompletionClient model, ToolRegistry tools, AgentOptions options,
                       PermissionEngine? permissions, IApprovalProvider? approver, IRunRecorder? recorder,
                       IHostEventSink? events, ConversationContext? context,
                       Func<IReadOnlyList<ToolCallRecord>, CancellationToken, Task>? executionCheckpoint,
                       IAgentInstructionBoundary? instructionBoundary, SessionRunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxAgentSteps);
        _model               = model;
        _tools               = tools;
        _options             = options;
        _permissions         = permissions;
        _approver            = approver;
        _recorder            = recorder;
        _events              = events;
        _context             = context ?? new ConversationContext(options.Context);
        _executionCheckpoint = executionCheckpoint;
        _instructionBoundary = instructionBoundary;
        _control             = control;
    }

    /// <summary>
    ///     <para>
    ///         创建跳过授权的主循环，仅供测试和简单只读流程使用。
    ///     </para>
    ///     <para>
    ///         Creates a loop without authorization, intended only for tests and simple read-only flows.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         CLI 始终注入权限引擎。
    ///     </para>
    ///     <para>
    ///         The CLI always supplies a permission engine.
    ///     </para>
    /// </remarks>
    public AgentLoop(IChatCompletionClient model, ToolRegistry tools, AgentOptions options)
        : this(model, tools, options, null, null)
    {
    }

    public IReadOnlyList<ChatMessage> History => _context.Messages;

    /// <summary>
    ///     <para>
    ///         重置上下文并执行完整任务，返回终态、最终文本与执行统计。
    ///     </para>
    ///     <para>
    ///         Resets context and runs a complete task, returning terminal state, final text and execution counts.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         跨回合续聊使用 <see cref="RunTurnAsync" />。
    ///     </para>
    ///     <para>
    ///         Use <see cref="RunTurnAsync" /> for cross-turn continuation.
    ///     </para>
    /// </remarks>
    public async Task<AgentResult> RunAsync(string systemPrompt, string userInput, CancellationToken cancellationToken)
    {
        _context.Reset();
        _context.Append(ChatMessage.System(systemPrompt));

        return await RunTurnCoreAsync(systemPrompt, userInput, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>
    ///         在既有对话上执行新回合，要求系统提示一致且历史工具组完整。
    ///     </para>
    ///     <para>
    ///         Runs a new turn on the existing conversation, requiring a matching system prompt
    ///         and complete historical tool groups.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         空上下文先写入系统提示；校验后只追加新用户输入。
    ///         中断历史应由宿主先回退到最近完整边界；此处仍拒绝不完整工具组，不补造结果或发送无效历史。
    ///     </para>
    ///     <para>
    ///         An empty context receives the system prompt first; validation precedes appending new user input.
    ///         The host must rewind interrupted history to the latest complete boundary.
    ///         This check still rejects incomplete tool groups without fabricating results or sending invalid history.
    ///     </para>
    /// </remarks>
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
    ///     <para>
    ///         一次回合的共享执行核心：追加用户输入、发布运行开始事件，并驱动主循环到终态。
    ///     </para>
    ///     <para>
    ///         The shared execution core of one turn: append the user input, publish the
    ///         run-started event, and drive the main loop to a terminal state.
    ///     </para>
    /// </summary>
    private async Task<AgentResult> RunTurnCoreAsync(
        string systemPrompt, string userInput, CancellationToken cancellationToken)
    {
        // 复用主循环时，事件序号、故障和执行记录仍须隔离到本次运行。
        _eventSequence                  = 0;
        _sinkFault                      = null;
        _modelRequestPersistenceWarning = null;
        _reportedInputTokens            = null;
        _reportedOutputTokens           = null;
        _toolCallRecords.Clear();
        _nextInvocationSequence = 0;
        using var budget = new RunBudget(_options.Budget, cancellationToken);
        _budget           = budget;
        cancellationToken = budget.Token;

        var steps                              = 0;
        var toolExecutions                     = 0;
        var compactions                        = 0;
        var instructionDeferralsSinceExecution = 0;

        try
        {
            if (_instructionBoundary is not null)
                await _instructionBoundary.PrepareUserContextAsync(cancellationToken).ConfigureAwait(false);

            ProjectInstructionsChanged? initialInstructionEvent = null;
            _context.Append(ChatMessage.User(userInput));
            if (_recorder is not null)
                await _recorder.StartAsync(systemPrompt, userInput, cancellationToken).ConfigureAwait(false);

            if (_instructionBoundary is not null)
            {
                initialInstructionEvent = await _instructionBoundary.ConfirmInitialCommitAsync(cancellationToken)
                                                                    .ConfigureAwait(false);
                await PublishInstructionChangedAsync(initialInstructionEvent, cancellationToken).ConfigureAwait(false);
            }

            _control?.Ready();
            await PublishEventAsync(HostEventKind.RunStarted, null, cancellationToken).ConfigureAwait(false);

            while (_options.MaxAgentSteps == 0 || steps < _options.MaxAgentSteps)
            {
                budget.Check();
                await WaitForControlAsync(cancellationToken).ConfigureAwait(false);
                await ApplyAmendmentsAsync(cancellationToken).ConfigureAwait(false);
                steps++;

                // 摘要输入也受预算限制，可能需多批压缩；只在模型请求之间进行。
                var definitions = CurrentToolDefinitions() ?? [];
                while (true)
                {
                    await WaitForControlAsync(cancellationToken).ConfigureAwait(false);
                    await ApplyAmendmentsAsync(cancellationToken).ConfigureAwait(false);
                    if (_instructionBoundary is not null)
                    {
                        var beforeCompactionEvent = await _instructionBoundary
                                                         .RefreshBeforeModelRequestAsync(cancellationToken)
                                                         .ConfigureAwait(false);
                        await PublishInstructionChangedAsync(beforeCompactionEvent, cancellationToken)
                           .ConfigureAwait(false);
                    }

                    if (!await CompactIfNeededAsync(definitions, cancellationToken).ConfigureAwait(false)) break;
                    compactions++;
                }

                if (_instructionBoundary is not null)
                {
                    var beforeRequestEvent =
                        await _instructionBoundary.RefreshBeforeModelRequestAsync(cancellationToken)
                                                  .ConfigureAwait(false);
                    await PublishInstructionChangedAsync(beforeRequestEvent, cancellationToken).ConfigureAwait(false);
                }

                // 摘要失败或不可再折叠时仍须拒绝超窗口请求，不能依赖模型服务兜底。
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

                await WaitForControlAsync(cancellationToken).ConfigureAwait(false);
                if (await ApplyAmendmentsAsync(cancellationToken).ConfigureAwait(false)) continue;
                var request = BuildRequest(definitions);

                // 校准基准必须是发送前的原始输入估算（视图 + 工具定义）；
                // 若先乘已有校准系数，观测比值会振荡而非收敛。
                var estimatedPromptTokens = TokenEstimator.EstimateMessages(request.Messages)
                                          + TokenEstimator.EstimateToolDefinitions(definitions);
                var assistant = await RequestAssistantMessageAsync(request, cancellationToken, estimatedPromptTokens,
                                                                   true);

                ValidateCallIds(assistant.ToolCalls);
                _context.Append(assistant);
                if (_recorder is not null)
                    await _recorder.RecordAssistantAsync(assistant, cancellationToken).ConfigureAwait(false);

                if (assistant.ToolCalls is null || assistant.ToolCalls.Count == 0)
                {
                    await WaitForControlAsync(cancellationToken).ConfigureAwait(false);
                    if (_control is null || await _control.TryFinishAsync(cancellationToken).ConfigureAwait(false))
                        return await FinishAsync(new AgentResult
                        {
                            Status         = AgentStatus.Completed,
                            FinalMessage   = assistant.AnswerText,
                            Steps          = steps,
                            ToolExecutions = toolExecutions,
                            Compactions    = compactions
                        }).ConfigureAwait(false);
                    await ApplyAmendmentsAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // 整轮先做无副作用准备；任一调用无效时整轮不执行，避免留下部分副作用。
                var round = PrepareRound(_tools, assistant.ToolCalls);
                if (_instructionBoundary is not null && round.Any(item => item.Preparation is not null))
                {
                    var instructionCheck =
                        await _instructionBoundary
                             .CheckToolRoundAsync(round.Where(item => item.Preparation is not null)
                                                       .Select(item => item.Preparation!).ToArray(),
                                                  cancellationToken).ConfigureAwait(false);
                    switch (instructionCheck.Status)
                    {
                        case InstructionBoundaryStatus.ScopeTooBroad :
                        {
                            var records = RecordRound(round);
                            AppendDeferredToolResults(round,
                                                      "The tool round was not started because its project instruction scope is too broad. " +
                                                      instructionCheck.Error +
                                                      " Retry with fewer calls or a narrower path; no tool in this round ran.");
                            await RecordNotStartedRoundAsync(records, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        case InstructionBoundaryStatus.Failed :
                        {
                            var records = RecordRound(round);
                            AppendDeferredToolResults(round,
                                                      "The tool round was not started because project instructions could not be refreshed. " +
                                                      instructionCheck.Error);
                            await RecordNotStartedRoundAsync(records, cancellationToken).ConfigureAwait(false);
                            return await FinishAsync(new AgentResult
                            {
                                Status         = AgentStatus.Failed,
                                Error          = instructionCheck.Error,
                                Steps          = steps,
                                ToolExecutions = toolExecutions,
                                Compactions    = compactions
                            }).ConfigureAwait(false);
                        }
                        case InstructionBoundaryStatus.Updated :
                        {
                            var records = RecordRound(round);
                            AppendDeferredToolResults(round,
                                                      $"Not started because project instructions changed to revision {instructionCheck.NewRevision}; the tool round will be retried.");
                            if (instructionDeferralsSinceExecution >= 1)
                            {
                                await RecordNotStartedRoundAsync(records, cancellationToken).ConfigureAwait(false);
                                return await FinishAsync(new AgentResult
                                {
                                    Status = AgentStatus.Failed,
                                    Error =
                                        "Project instructions changed again after the tool round was already deferred once.",
                                    Steps          = steps,
                                    ToolExecutions = toolExecutions,
                                    Compactions    = compactions
                                }).ConfigureAwait(false);
                            }

                            await RecordNotStartedRoundAsync(records, cancellationToken).ConfigureAwait(false);
                            var changedEvent = await _instructionBoundary
                                                    .ApplyToolRoundUpdateAsync(records.Values.ToArray(),
                                                                               cancellationToken).ConfigureAwait(false);
                            await PublishInstructionChangedAsync(changedEvent, cancellationToken).ConfigureAwait(false);
                            instructionDeferralsSinceExecution++;
                            continue;
                        }
                        default :
                            round = BindInstructionCoverage(round, instructionCheck.Coverage);
                            break;
                    }
                }

                var roundRecords = RecordRound(round);
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

                    if (_recorder is not null)
                        foreach (var item in round)
                            await _recorder
                                 .RecordNotStartedAsync(roundRecords[item.Call.Id].Snapshot(), cancellationToken)
                                 .ConfigureAwait(false);

                    continue;
                }

                // 整轮完成授权后才顺序执行获批调用，审批期间不提前产生副作用。
                await WaitForControlAsync(cancellationToken).ConfigureAwait(false);
                if (_control?.HasAmendments == true)
                {
                    await DeferForAmendmentAsync(round, roundRecords, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var                       authorized      = new List<AuthorizedRoundCall>(round.Count);
                InstructionBoundaryCheck? approvalRefresh = null;
                foreach (var item in round)
                {
                    var authorization = _permissions is null
                        ? AuthorizationOutcome.Granted
                        : await AuthorizeAsync(item.Preparation!, roundRecords, cancellationToken);
                    authorized.Add(new AuthorizedRoundCall(item, authorization));
                    if (_control?.HasAmendments == true) break;
                    if (_instructionBoundary is null || _permissions is null) continue;
                    var check =
                        await _instructionBoundary
                             .RecheckBeforeDispatchAsync(round.Select(candidate => candidate.Preparation!).ToArray(),
                                                         cancellationToken).ConfigureAwait(false);
                    if (check.Status == InstructionBoundaryStatus.Unchanged) continue;
                    approvalRefresh = check;
                    break;
                }

                await WaitForControlAsync(cancellationToken).ConfigureAwait(false);
                if (_control?.HasAmendments == true)
                {
                    await DeferForAmendmentAsync(round, roundRecords, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (approvalRefresh is { } approvalChange && _instructionBoundary is { } approvalBoundary)
                {
                    var reason = approvalChange.Status == InstructionBoundaryStatus.Updated
                        ? $"Not started because project instructions changed to revision {approvalChange.NewRevision}; approval is obsolete."
                        : "Not started because project instructions could not be refreshed. " + approvalChange.Error;
                    AppendDeferredToolResults(round, reason);
                    await RecordNotStartedRoundAsync(roundRecords, cancellationToken).ConfigureAwait(false);
                    if (approvalChange.Status              == InstructionBoundaryStatus.Failed ||
                        instructionDeferralsSinceExecution >= 1)
                        return await FinishAsync(new AgentResult
                        {
                            Status = AgentStatus.Failed,
                            Error = approvalChange.Error ??
                                    "Project instructions changed again after the tool round was already deferred once.",
                            Steps          = steps,
                            ToolExecutions = toolExecutions,
                            Compactions    = compactions
                        }).ConfigureAwait(false);
                    var changedEvent = await approvalBoundary
                                            .ApplyToolRoundUpdateAsync(roundRecords.Values.ToArray(), cancellationToken)
                                            .ConfigureAwait(false);
                    await PublishInstructionChangedAsync(changedEvent, cancellationToken).ConfigureAwait(false);
                    instructionDeferralsSinceExecution++;
                    continue;
                }

                var dispatchDeferred = false;
                for (var dispatchIndex = 0; dispatchIndex < authorized.Count && !dispatchDeferred; dispatchIndex++)
                {
                    var item = authorized[dispatchIndex];
                    await WaitForControlAsync(cancellationToken).ConfigureAwait(false);
                    if (_control?.HasAmendments == true)
                    {
                        await DeferForAmendmentAsync(authorized.Skip(dispatchIndex)
                                                               .Select(candidate => candidate.RoundCall).ToArray(),
                                                     roundRecords, cancellationToken).ConfigureAwait(false);
                        dispatchDeferred = true;
                        break;
                    }

                    if (_instructionBoundary is not null)
                    {
                        var dispatchCheck =
                            await _instructionBoundary
                                 .RecheckBeforeDispatchAsync(round.Where(candidate => candidate.Preparation is not null)
                                                                  .Select(candidate => candidate.Preparation!)
                                                                  .ToArray(),
                                                             cancellationToken).ConfigureAwait(false);
                        if (dispatchCheck.Status is InstructionBoundaryStatus.Failed
                                                 or InstructionBoundaryStatus.Updated)
                        {
                            var deferred = authorized.SkipWhile(candidate => !ReferenceEquals(candidate, item))
                                                     .ToArray();
                            var deferredCalls = deferred.Select(candidate => candidate.RoundCall).ToArray();
                            var reason = dispatchCheck.Status == InstructionBoundaryStatus.Updated
                                ? $"Not started because project instructions changed to revision {dispatchCheck.NewRevision}; the tool round will be retried."
                                : "The tool was not started because project instructions could not be refreshed. " +
                                  dispatchCheck.Error;
                            AppendDeferredToolResults(deferredCalls, reason);

                            if (dispatchCheck.Status == InstructionBoundaryStatus.Updated)
                            {
                                if (instructionDeferralsSinceExecution >= 1)
                                {
                                    await RecordNotStartedCallsAsync(roundRecords, deferredCalls, cancellationToken)
                                       .ConfigureAwait(false);
                                    return await FinishAsync(new AgentResult
                                    {
                                        Status = AgentStatus.Failed,
                                        Error =
                                            "Project instructions changed again after the tool round was already deferred once.",
                                        Steps          = steps,
                                        ToolExecutions = toolExecutions,
                                        Compactions    = compactions
                                    }).ConfigureAwait(false);
                                }

                                await RecordNotStartedCallsAsync(roundRecords, deferredCalls, cancellationToken)
                                   .ConfigureAwait(false);
                                var changedEvent =
                                    await _instructionBoundary
                                         .ApplyToolRoundUpdateAsync(deferred.Select(candidate =>
                                                                        roundRecords[candidate.RoundCall.Call
                                                                           .Id]).ToArray(),
                                                                    cancellationToken).ConfigureAwait(false);
                                await PublishInstructionChangedAsync(changedEvent, cancellationToken)
                                   .ConfigureAwait(false);
                                instructionDeferralsSinceExecution++;
                                dispatchDeferred = true;
                                break;
                            }

                            await RecordNotStartedCallsAsync(roundRecords, deferredCalls, cancellationToken)
                               .ConfigureAwait(false);
                            return await FinishAsync(new AgentResult
                            {
                                Status         = AgentStatus.Failed,
                                Error          = dispatchCheck.Error,
                                Steps          = steps,
                                ToolExecutions = toolExecutions,
                                Compactions    = compactions
                            }).ConfigureAwait(false);
                        }
                    }

                    if (!item.Authorization.Allowed)
                    {
                        _context.Append(ChatMessage.Tool(item.RoundCall.Call.FunctionName, item.RoundCall.Call.Id,
                                                         item.Authorization.Reason));
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    await WaitForControlAsync(cancellationToken).ConfigureAwait(false);
                    if (_control?.HasAmendments == true)
                    {
                        await DeferForAmendmentAsync(authorized.Skip(dispatchIndex)
                                                               .Select(candidate => candidate.RoundCall).ToArray(),
                                                     roundRecords, cancellationToken).ConfigureAwait(false);
                        dispatchDeferred = true;
                        break;
                    }

                    try
                    {
                        budget.CheckTool();
                    }
                    catch (RunBudgetExceededException)
                    {
                        var deferredCalls = authorized.Skip(dispatchIndex).Select(candidate => candidate.RoundCall)
                                                      .ToArray();
                        await RecordDeferredCallsAsync(deferredCalls, roundRecords,
                                                       "Not started because the run budget was reached.",
                                                       CancellationToken.None).ConfigureAwait(false);
                        throw;
                    }

                    var record = roundRecords[item.RoundCall.Call.Id];
                    // 派发前持久化未知状态；检查点使用快照，不能共享可变执行记录。
                    record.State = ToolCallExecutionState.UnknownResult;
                    try
                    {
                        if (record.SideEffects && _executionCheckpoint is not null)
                            await _executionCheckpoint(_toolCallRecords.Select(call => call.Snapshot()).ToArray(),
                                                       cancellationToken).ConfigureAwait(false);
                        await PublishEventAsync(HostEventKind.ToolStarted, item.RoundCall.Preparation!,
                                                cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    catch (Exception)
                    {
                        record.State = ToolCallExecutionState.KnownNotStarted;
                        throw;
                    }

                    // 首个真实启动构成新的安全推进边界；后续规则变化重新获得一次延期预算。
                    instructionDeferralsSinceExecution = 0;
                    budget.ToolStarted();
                    toolExecutions++;
                    ToolResult result;
                    try
                    {
                        result = await ExecutePreparedAsync(item.RoundCall.Tool!, item.RoundCall.Preparation!,
                                                            cancellationToken);
                    }
                    catch (ToolExecutionCanceledException ex)
                    {
                        result       = ex.Result;
                        record.State = ToolCallExecutionState.UnknownResult;
                        _context.Append(ChatMessage.Tool(item.RoundCall.Call.FunctionName, item.RoundCall.Call.Id,
                                                         result.Content));
                        string? persistenceWarning = null;
                        if (_recorder is not null)
                            try
                            {
                                await _recorder.RecordResultAsync(item.RoundCall.Preparation!, result,
                                                                  CancellationToken.None).ConfigureAwait(false);
                            }
                            catch (Exception writeError)
                            {
                                persistenceWarning = writeError.Message;
                            }

                        await PublishEventAsync(HostEventKind.ToolFinished,
                                                new ToolExecutionOutcome(item.RoundCall.Preparation!, result),
                                                CancellationToken.None).ConfigureAwait(false);
                        return await FinishAsync(new AgentResult
                        {
                            Status = AgentStatus.Cancelled,
                            Error =
                                "Run cancelled after tool execution started; partial results were retained for inspection.",
                            PersistenceWarning = persistenceWarning,
                            Steps              = steps,
                            ToolExecutions     = toolExecutions,
                            Compactions        = compactions
                        }).ConfigureAwait(false);
                    }

                    // 副作用已发生，先保留结果再写审计，避免审计失败抹去结果或诱发重放。
                    _context.Append(ChatMessage.Tool(item.RoundCall.Call.FunctionName, item.RoundCall.Call.Id,
                                                     result.Content));
                    // 持久化确认失败时仍保持 UnknownResult，调用链立即结束且后续工具不会启动。
                    if (_recorder is not null)
                        try
                        {
                            await _recorder.RecordResultAsync(item.RoundCall.Preparation!, result, cancellationToken)
                                           .ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            return await FinishAsync(new AgentResult
                            {
                                Status = cancellationToken.IsCancellationRequested
                                    ? AgentStatus.Cancelled
                                    : AgentStatus.Failed,
                                Error =
                                    "The tool ran but its result was not reliably recorded; later tools were stopped.",
                                PersistenceWarning = ex.Message,
                                Steps              = steps,
                                ToolExecutions     = toolExecutions,
                                Compactions        = compactions
                            }).ConfigureAwait(false);
                        }

                    if (result.ExecutionIncomplete ||
                        result.PatchChanges?.Any(change => change.State == PatchChange.UnknownState) == true)
                    {
                        MarkRecordState(roundRecords, item.RoundCall.Call.Id, ToolCallExecutionState.UnknownResult);
                        await PublishEventAsync(HostEventKind.ToolFinished,
                                                new ToolExecutionOutcome(item.RoundCall.Preparation!, result),
                                                CancellationToken.None).ConfigureAwait(false);
                        return await FinishAsync(new AgentResult
                        {
                            Status = cancellationToken.IsCancellationRequested
                                ? AgentStatus.Cancelled
                                : AgentStatus.Failed,
                            Error =
                                "The tool execution is incomplete or left file outcomes unknown; later tools were stopped pending inspection.",
                            Steps          = steps,
                            ToolExecutions = toolExecutions,
                            Compactions    = compactions
                        }).ConfigureAwait(false);
                    }

                    MarkRecordState(roundRecords, item.RoundCall.Call.Id, ToolCallExecutionState.ConfirmedResult);
                    await PublishEventAsync(HostEventKind.ToolFinished,
                                            new ToolExecutionOutcome(item.RoundCall.Preparation!, result),
                                            cancellationToken).ConfigureAwait(false);
                }

                if (dispatchDeferred)
                    continue;
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
        catch (RunBudgetExceededException ex)
        {
            return await FinishAsync(new AgentResult
            {
                Status         = AgentStatus.BudgetLimitReached,
                Steps          = steps,
                ToolExecutions = toolExecutions,
                Compactions    = compactions,
                Error          = ex.Message
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
        catch (Exception ex)
        {
            // SDK 超时也可能抛取消异常；只有调用方令牌已取消才进入上面的取消分支。
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
        if (_control is not null)
        {
            await _control.SealAsync().ConfigureAwait(false);
            await PublishControlHistoryAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var budget = _budget?.Snapshot;
        // 缺失 usage 仍表示未报告，不能补成零。
        result = result with
        {
            Budget = budget,
            Status = budget?.LimitReason is not null ? AgentStatus.BudgetLimitReached : result.Status,
            Error = budget?.LimitReason is { } reason
                ? $"Run budget reached: {reason}. {result.Error}".TrimEnd()
                : result.Error,
            InputTokens = _reportedInputTokens,
            OutputTokens = _reportedOutputTokens,
            ToolCalls = _toolCallRecords.ToArray(),
            PersistenceWarning = _modelRequestPersistenceWarning is null
                ? result.PersistenceWarning
                : string.IsNullOrWhiteSpace(result.PersistenceWarning)
                    ? _modelRequestPersistenceWarning
                    : $"{result.PersistenceWarning}; {_modelRequestPersistenceWarning}"
        };

        if (_recorder is null) return await PublishRunFinishedAsync(result).ConfigureAwait(false);
        try
        {
            await _recorder.CompleteAsync(result, _context.Messages, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = result with
            {
                PersistenceWarning = string.IsNullOrWhiteSpace(result.PersistenceWarning)
                    ? ex.Message
                    : $"{result.PersistenceWarning}; {ex.Message}"
            };
        }

        return await PublishRunFinishedAsync(result).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>
    ///         发布运行终态，并把展示故障并入 Error 而不改写执行状态。
    ///     </para>
    ///     <para>
    ///         Publishes terminal events, merging display faults into Error without changing execution status.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         终态交付不使用取消令牌；失败终态先发布 RunError。
    ///         取消或非失败终态不发布 RunError，事件接收方失败不能抹去真实执行结果。
    ///     </para>
    ///     <para>
    ///         Terminal delivery uses no cancellation token; a failed terminal first publishes RunError.
    ///         Cancelled and non-failed terminals omit RunError. Sink failures must not erase the actual outcome.
    ///     </para>
    /// </remarks>
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
    ///     <para>
    ///         发布运行内顺序递增的宿主事件；未配置接收方时不执行操作。
    ///     </para>
    ///     <para>
    ///         Publishes host events with increasing per-run sequence numbers; does nothing without a sink.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         每次运行的序号从 1 开始。展示失败只记录首个故障并随终态上报，不终止执行；
    ///         运行自身取消导致的异常原样传播，不误记为展示故障。
    ///     </para>
    ///     <para>
    ///         Each run starts its sequence at 1. Display failures record the first fault for terminal reporting
    ///         without aborting execution; exceptions caused by run cancellation propagate,
    ///         rather than being misclassified as display faults.
    ///     </para>
    /// </remarks>
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
    ///     <para>
    ///         校验历史中不存在不完整的工具原子组；实现由 <see cref="ChatHistoryInvariants" /> 提供，
    ///         与会话续聊的恢复校验共用同一不变量。
    ///     </para>
    ///     <para>
    ///         Verifies the history carries no incomplete tool group; the implementation
    ///         lives in <see cref="ChatHistoryInvariants" /> and is shared with the
    ///         resume-side validation of session continuation.
    ///     </para>
    /// </summary>
    private void EnsureCompleteToolGroups() => ChatHistoryInvariants.EnsureCompleteToolGroups(_context.Messages);

    /// <summary>
    ///     <para>发布已可靠提交的项目指令元数据事件；null 表示本次边界没有生效变化。</para>
    ///     <para>Publishes metadata for a reliably committed instruction change; null means no effective change.</para>
    /// </summary>
    private async ValueTask PublishInstructionChangedAsync(
        ProjectInstructionsChanged? payload, CancellationToken cancellationToken)
    {
        if (payload is null) return;
        await PublishEventAsync(HostEventKind.ProjectInstructionsChanged, payload, cancellationToken)
           .ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>为整轮准备对象绑定同一指令覆盖凭据，保留工具私有执行计划。</para>
    ///     <para>Binds one instruction-coverage credential to every prepared plan while preserving tool-private state.</para>
    /// </summary>
    private static IReadOnlyList<RoundCall> BindInstructionCoverage(
        IReadOnlyList<RoundCall> round, InstructionCoverage? coverage)
    {
        if (coverage is null) return round;

        var bound = new List<RoundCall>(round.Count);
        foreach (var item in round)
            bound.Add(item.Preparation is null
                          ? item
                          : RoundCall.Ready(item.Call, item.Tool!, item.Preparation.WithInstructionCoverage(coverage)));

        return bound;
    }

    private void AppendDeferredToolResults(IReadOnlyList<RoundCall> round, string reason)
    {
        foreach (var item in round)
        {
            _toolCallRecords.Last(record => record.CallId == item.Call.Id).NotStartedReason = reason;
            _context.Append(ChatMessage.Tool(item.Call.FunctionName, item.Call.Id, reason));
        }
    }

    private async Task RecordNotStartedRoundAsync(
        Dictionary<string, ToolCallRecord> records, CancellationToken cancellationToken)
    {
        if (_recorder is null) return;
        foreach (var record in records.Values)
            await _recorder.RecordNotStartedAsync(record.Snapshot(), cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordNotStartedCallsAsync(
        Dictionary<string, ToolCallRecord> records, IReadOnlyList<RoundCall> calls, CancellationToken cancellationToken)
    {
        if (_recorder is null) return;
        foreach (var call in calls)
            await _recorder.RecordNotStartedAsync(records[call.Call.Id].Snapshot(), cancellationToken)
                           .ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>
    ///         用当前模型视图（经 Context Manager 构建）和已注册工具构造下一次模型请求。
    ///     </para>
    ///     <para>
    ///         Builds the next model request from the current model view and registered tools.
    ///     </para>
    /// </summary>
    private ChatCompletionRequest BuildRequest(IReadOnlyList<ToolDefinition> definitions) => new()
    {
        Model           = _options.Model,
        ReasoningEffort = _options.ReasoningEffort,
        Messages        = _context.BuildModelView(),
        Tools           = definitions.Count == 0 ? null : definitions
    };

    /// <summary>
    ///     <para>
    ///         返回随普通请求发送的工具定义；注册表为空时不发送 tools。
    ///     </para>
    ///     <para>
    ///         Returns the tool definitions sent with regular requests, or none when the registry is empty.
    ///     </para>
    /// </summary>
    private List<ToolDefinition>? CurrentToolDefinitions()
    {
        if (_tools.Count == 0) return null;

        var definitions = new List<ToolDefinition>(_tools.Count);
        foreach (var tool in _tools.Values) definitions.Add(tool.Definition);

        return definitions;
    }

    /// <summary>
    ///     <para>
    ///         预算超限时，用禁用工具的摘要请求压缩最旧一批完整回合。
    ///     </para>
    ///     <para>
    ///         Compacts the oldest batch of complete turns with a tools-disabled summary request
    ///         when the budget is exceeded.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         失败或非法摘要不破坏本地历史，并锁定重试直到对话增长。
    ///     </para>
    ///     <para>
    ///         Failed or invalid summaries leave local history intact and latch retries until the conversation grows.
    ///     </para>
    /// </remarks>
    /// <returns>
    ///     <para>
    ///         已应用压缩时返回 true；预算仍超限时可继续调用。
    ///         返回 false 表示未应用压缩，包括已容纳、无可折叠内容或摘要失败。
    ///     </para>
    ///     <para>
    ///         Returns true after applying compaction; call again if the budget is still exceeded.
    ///         Returns false if none was applied, including a fitting view, no foldable content or a failed summary.
    ///     </para>
    /// </returns>
    private async Task<bool> CompactIfNeededAsync(
        IReadOnlyList<ToolDefinition> definitions, CancellationToken cancellationToken)
    {
        if (!_context.Enabled) return false;

        if (!_context.RequiresCompaction(definitions)) return false;

        var foldMessages = _context.BuildCompactionMessages(definitions);
        if (foldMessages.Count == 0) return false;

        ChatMessage summary;
        try
        {
            // 摘要不得请求工具或产生副作用。
            var summaryRequest = new ChatCompletionRequest
            {
                Model           = _options.Model,
                ReasoningEffort = _options.ReasoningEffort,
                Messages        = foldMessages,
                Tools           = null
            };
            // 摘要按自身输入校准并累计 usage，但内部文本不得作为回答展示。
            var estimatedSummaryTokens = TokenEstimator.EstimateMessages(foldMessages);
            summary = await RequestAssistantMessageAsync(summaryRequest, cancellationToken, estimatedSummaryTokens,
                                                         false).ConfigureAwait(false);
            if (summary.ToolCalls is { Count: > 0 })
            {
                _context.MarkCompactionAttempted(CompactionFailureReason.ToolCallsNotAllowed);
                await PublishCompactionFailureAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or RunBudgetExceededException ||
                                   _modelRequestPersistenceWarning is not null)
        {
            throw;
        }
        catch (Exception ex)
        {
            _context.MarkCompactionAttempted(ex switch
            {
                EmptyModelResponseException => CompactionFailureReason.EmptySummary,
                InvalidDataException        => CompactionFailureReason.IncompleteResponse,
                _                           => CompactionFailureReason.ModelRequestFailed
            });
            await PublishCompactionFailureAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        // 摘要 usage 可能更新校准；前后估算使用同一口径。
        var before = _context.EstimateViewTokens();
        if (!_context.TryApplyCompaction(summary.Content))
        {
            await PublishCompactionFailureAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var after  = _context.EstimateViewTokens();
        var change = new ContextChange(before, after);
        await PublishEventAsync(HostEventKind.CompactionApplied, change, cancellationToken).ConfigureAwait(false);
        if (_recorder is not null)
            await _recorder.RecordCompactionAsync(change, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    ///     <para>向宿主和同一权威日志发布具体拒绝原因，不泄露模型内部正文。</para>
    ///     <para>Publishes the rejection reason to the host and the same authoritative log without internal model text.</para>
    /// </summary>
    private async Task PublishCompactionFailureAsync(CancellationToken cancellationToken)
    {
        var reason = _context.LastCompactionFailure;
        await PublishEventAsync(HostEventKind.CompactionFailed, reason, cancellationToken).ConfigureAwait(false);
        if (_recorder is not null)
            await _recorder.RecordCompactionFailedAsync(reason, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>
    ///         在追加 assistant 消息或执行工具前，拒绝空白或重复的工具调用 ID。
    ///     </para>
    ///     <para>
    ///         Rejects blank or duplicate tool-call IDs before appending the assistant message or executing tools.
    ///     </para>
    /// </summary>
    private static void ValidateCallIds(IReadOnlyList<ChatToolCall>? calls)
    {
        if (calls is null) return;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in calls)
        {
            if (string.IsNullOrWhiteSpace(call.Id))
                throw new
                    InvalidDataException("The model returned an empty tool call id; no tool in the round was started.");
            if (!ids.Add(call.Id))
                throw new
                    InvalidDataException($"The model returned duplicate tool call id '{call.Id}'; no tool in the round was started.");
        }
    }

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
    ///     <para>
    ///         生成整轮被无效调用拦截时，其余调用未执行的原因。
    ///     </para>
    ///     <para>
    ///         Explains why another call in a round containing an invalid call was not executed.
    ///     </para>
    /// </summary>
    private static string NotExecutedReason(RoundCall invalid) =>
        $"Not executed: the round contains an invalid call '{invalid.Call.Id}' ({invalid.Call.FunctionName}) — " +
        $"{invalid.Error}";

    /// <summary>
    ///     <para>
    ///         执行已准备且获批的调用，将普通工具异常转换为模型可见的失败结果。
    ///     </para>
    ///     <para>
    ///         Executes a prepared, approved call, converting ordinary tool exceptions into model-facing failures.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         仅在整轮准备及授权结束后调用；取消与 ToolExecutionUncertainException 原样传播，
    ///         未知执行状态不得伪装为已确认失败。
    ///     </para>
    ///     <para>
    ///         Call only after round-wide preparation and authorization. Cancellation and
    ///         ToolExecutionUncertainException propagate; unknown execution state must not appear as confirmed failure.
    ///     </para>
    /// </remarks>
    private static async Task<ToolResult> ExecutePreparedAsync(
        ITool tool, ToolPreparation preparation, CancellationToken cancellationToken)
    {
        try
        {
            return await tool.ExecuteAsync(preparation, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ToolExecutionUncertainException)
        {
            return new ToolResult
            {
                Succeeded = false,
                Content   = $"Tool '{preparation.ToolName}' failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    ///     <para>
    ///         为本轮调用建立执行记录，供运行终态的中断判定与持久化检查。
    ///     </para>
    ///     <para>
    ///         Creates round execution records for terminal interruption checks and persisted inspection.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         初始状态为已知未开始；准备失败只记录错误，不认定发生副作用。
    ///     </para>
    ///     <para>
    ///         Records start as known-not-started; preparation failures record errors without implying side effects.
    ///     </para>
    /// </remarks>
    private Dictionary<string, ToolCallRecord> RecordRound(IReadOnlyList<RoundCall> round)
    {
        var records = new Dictionary<string, ToolCallRecord>(round.Count, StringComparer.Ordinal);
        foreach (var item in round)
        {
            var record = item.Preparation is { } preparation
                ? new ToolCallRecord
                {
                    CallId       = item.Call.Id,
                    InvocationId = NextInvocationId(),
                    ToolName     = item.Call.FunctionName,
                    Capability   = preparation.Capability,
                    SideEffects  = !ToolCapabilities.IsReadOnly(preparation.Capability),
                    Summary      = preparation.Summary
                }
                : new ToolCallRecord
                {
                    CallId           = item.Call.Id,
                    InvocationId     = NextInvocationId(),
                    ToolName         = item.Call.FunctionName,
                    Summary          = $"{item.Call.FunctionName}()",
                    PreparationError = item.Error
                };
            records[item.Call.Id] = record;
            _toolCallRecords.Add(record);
        }

        return records;
    }

    /// <summary>
    ///     <para>按本次运行中的出现顺序分配调用标识，准备失败也占用序号。</para>
    ///     <para>Assigns an invocation identity in run appearance order, including failed preparations.</para>
    /// </summary>
    private string NextInvocationId() => $"call-{checked(++_nextInvocationSequence):D8}";

    /// <summary>
    ///     <para>
    ///         更新本轮某个调用的执行状态；记录实例在累积列表与本轮字典间共享，就地更新。
    ///     </para>
    ///     <para>
    ///         Updates the execution state of one call of the round; the record instance is
    ///         shared between the accumulated list and the round dictionary, updated in place.
    ///     </para>
    /// </summary>
    private static void MarkRecordState(
        Dictionary<string, ToolCallRecord> records, string callId, ToolCallExecutionState state) =>
        records[callId].State = state;

    /// <summary>
    ///     <para>
    ///         计算并记录调用的授权结论，需要时请求用户审批。
    ///     </para>
    ///     <para>
    ///         Decides and records invocation authorization, requesting user approval when needed.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         交互单次批准与拒绝只覆盖当前调用，不存为授权；
    ///         AllowSession 在引擎中记录，宿主须把其生命周期限制为本次运行。
    ///         拒绝及 NeedsInspection 原因作为工具结果回传，任务取消才终止运行。
    ///     </para>
    ///     <para>
    ///         Interactive allow-once and denial cover only the current invocation and are not stored as grants.
    ///         AllowSession is recorded in the engine; the host must limit its lifetime to the current run.
    ///         Denials, including NeedsInspection reasons, return as tool results; task cancellation ends the run.
    ///     </para>
    /// </remarks>
    private async Task<AuthorizationOutcome> AuthorizeAsync(
        ToolPreparation preparation, Dictionary<string, ToolCallRecord> records, CancellationToken cancellationToken)
    {
        var decision = _permissions!.Decide(preparation);
        switch (decision)
        {
            case PermissionDecision.Allow :
                // 授权阶段即消费预授权，避免同轮相同调用复用一次性 grant。
                _permissions.TryConsumeOnce(preparation);
                records[preparation.CallId].Approval = "allow";
                await RecordPermissionAsync(preparation, decision, "allow", cancellationToken).ConfigureAwait(false);
                await PublishResolutionAsync(decision, null, "allow", cancellationToken).ConfigureAwait(false);
                return AuthorizationOutcome.Granted;

            case PermissionDecision.Deny :
                var blockedReason = _permissions.NonReadOnlyBlock(preparation);
                var policyDenied = AuthorizationOutcome.Denied(blockedReason is not null
                                                                   ? $"Blocked pending inspection of an interrupted run ({blockedReason}); denied: {preparation.Summary}"
                                                                   : $"Permission denied: {preparation.Summary}");
                records[preparation.CallId].Approval = policyDenied.Reason;
                await RecordPermissionAsync(preparation, decision, policyDenied.Reason, cancellationToken)
                   .ConfigureAwait(false);
                await PublishResolutionAsync(decision, null, policyDenied.Reason, cancellationToken)
                   .ConfigureAwait(false);
                return policyDenied;

            case PermissionDecision.Ask :
            {
                var approvalProvider = _approver ??
                                       throw new
                                           InvalidOperationException("An approval provider is required when the permission engine returns Ask.");
                await PublishEventAsync(HostEventKind.ApprovalRequested, preparation, cancellationToken)
                   .ConfigureAwait(false);
                var amendmentToken = _control?.AmendmentToken ?? CancellationToken.None;
                using var approvalToken =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, amendmentToken);
                ApprovalAction action;
                try
                {
                    action = await approvalProvider.PromptAsync(preparation, approvalToken.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (amendmentToken.IsCancellationRequested &&
                                                         !cancellationToken.IsCancellationRequested)
                {
                    await PublishResolutionAsync(decision, null, "approval obsolete: user amendment", cancellationToken)
                       .ConfigureAwait(false);
                    return AuthorizationOutcome.Granted;
                }

                switch (action)
                {
                    case ApprovalAction.AllowOnce :
                        // 交互批准只绑定当前调用；存入引擎会误批后续相同调用。
                        records[preparation.CallId].Approval = "allow once";
                        await RecordPermissionAsync(preparation, decision, "allow once", cancellationToken)
                           .ConfigureAwait(false);
                        await PublishResolutionAsync(decision, action, "allow once", cancellationToken)
                           .ConfigureAwait(false);
                        return AuthorizationOutcome.Granted;

                    case ApprovalAction.AllowSession :
                        _permissions.GrantSession(preparation);
                        records[preparation.CallId].Approval = "allow session";
                        await RecordPermissionAsync(preparation, decision, "allow session", cancellationToken)
                           .ConfigureAwait(false);
                        await PublishResolutionAsync(decision, action, "allow session", cancellationToken)
                           .ConfigureAwait(false);
                        return AuthorizationOutcome.Granted;

                    default :
                        var userDenied =
                            AuthorizationOutcome.Denied($"Permission denied by user: {preparation.Summary}");
                        records[preparation.CallId].Approval = userDenied.Reason;
                        await RecordPermissionAsync(preparation, decision, userDenied.Reason, cancellationToken)
                           .ConfigureAwait(false);
                        await PublishResolutionAsync(decision, ApprovalAction.Deny, userDenied.Reason,
                                                     cancellationToken).ConfigureAwait(false);
                        return userDenied;
                }
            }
            default :
                throw new InvalidOperationException($"Unknown permission decision: {decision}");
        }
    }

    /// <summary>
    ///     <para>
    ///         发布审批结论事件；与审计记录一一对应，宿主据此呈现每次权限结论（含规则自动
    ///         允许/拒绝），事件通道只读，不反向影响执行。
    ///     </para>
    ///     <para>
    ///         Publishes the approval-resolution event, one per audit-recorded decision, so
    ///         hosts can render every outcome (rule-based allows and denies included); the
    ///         event channel stays read-only and never influences execution.
    ///     </para>
    /// </summary>
    private async ValueTask PublishResolutionAsync(
        PermissionDecision decision, ApprovalAction? action, string outcome, CancellationToken cancellationToken)
    {
        await PublishEventAsync(HostEventKind.ApprovalResolved, new ApprovalResolution(decision, action, outcome),
                                cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordPermissionAsync(
        ToolPreparation preparation, PermissionDecision decision, string outcome, CancellationToken cancellationToken)
    {
        if (_recorder is not null)
            await _recorder.RecordPermissionAsync(preparation, decision, outcome, cancellationToken)
                           .ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>
    ///         表示模型一轮中已经准备完成或准备失败的单个调用。
    ///     </para>
    ///     <para>
    ///         Represents one model-round call after preparation, either ready or failed.
    ///     </para>
    /// </summary>
    private sealed record RoundCall(ChatToolCall Call, ITool? Tool, ToolPreparation? Preparation, string? Error)
    {
        /// <summary>
        ///     <para>
        ///         创建可进入授权阶段的调用记录。
        ///     </para>
        ///     <para>
        ///         Creates a call record ready for authorization.
        ///     </para>
        /// </summary>
        public static RoundCall Ready(ChatToolCall call, ITool tool, ToolPreparation preparation) =>
            new(call, tool, preparation, null);

        /// <summary>
        ///     <para>
        ///         创建携带准备错误、禁止执行的调用记录。
        ///     </para>
        ///     <para>
        ///         Creates a non-executable call record carrying its preparation error.
        ///     </para>
        /// </summary>
        public static RoundCall Failed(ChatToolCall call, string error) => new(call, null, null, error);
    }

    /// <summary>
    ///     <para>
    ///         将准备结果与对应权限结论绑定，供顺序执行阶段使用。
    ///     </para>
    ///     <para>
    ///         Couples a prepared round call with its authorization outcome for sequential dispatch.
    ///     </para>
    /// </summary>
    private sealed record AuthorizedRoundCall(RoundCall RoundCall, AuthorizationOutcome Authorization);

    /// <summary>
    ///     <para>
    ///         权限阶段的内部结果，包含是否允许以及拒绝时返回模型的原因。
    ///     </para>
    ///     <para>
    ///         Internal authorization result containing the allow flag and a model-facing denial reason.
    ///     </para>
    /// </summary>
    private sealed record AuthorizationOutcome(bool Allowed, string Reason)
    {
        public static AuthorizationOutcome Granted { get; } = new(true, string.Empty);

        /// <summary>
        ///     <para>
        ///         创建带说明的拒绝结果。
        ///     </para>
        ///     <para>
        ///         Creates a denied outcome with an explanatory reason.
        ///     </para>
        /// </summary>
        public static AuthorizationOutcome Denied(string reason) => new(false, reason);
    }
}

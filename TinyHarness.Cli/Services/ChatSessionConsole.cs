using System.Text;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Session;

namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>协调 chat 会话的单一输入循环、回合摘要与中断检查确认。</para>
///     <para>Coordinates the single chat input loop, turn summaries, and interruption inspection confirmation.</para>
/// </summary>
/// <remarks>
///     <para>
///         每行非空输入是当前回合的最新目标。回合、审批与检查确认共享读取器；
///         回答、工具、审批与 usage 由宿主事件展示，本循环不重复打印
///         <see cref="AgentResult.FinalMessage" />。输入输出、宿主与取消协调器由调用方管理。
///     </para>
///     <para>
///         Each nonblank input line is the current turn's latest goal. Turns, approvals,
///         and inspection confirmation share a reader. Host events display answers, tools,
///         approvals, and usage; this loop never reprints <see cref="AgentResult.FinalMessage" />.
///         The caller owns input, output, the host, and the cancellation coordinator.
///     </para>
///     <para>
///         活动回合取消、提交被拒绝或回合失败均不结束会话；EOF 或空闲 Ctrl+C 结束会话。
///         宿主负责终态回合保存，本循环报告不可靠保存。
///         未知副作用在检查确认前保持阻断，只读分析仍可继续；每次中断仅提示一次。
///     </para>
///     <para>
///         Active-turn cancellation, rejected submissions, and failed turns leave the session usable;
///         EOF or idle Ctrl+C ends it. The host saves terminal turns, and this loop reports unreliable saves.
///         Unknown side effects remain blocked until inspection is confirmed, while read-only analysis stays available;
///         each interruption prompts once.
///     </para>
/// </remarks>
internal sealed class ChatSessionConsole
{
    private readonly ChatCancellation      _cancellation;
    private readonly ChatSessionHost       _host;
    private readonly TextReader            _input;
    private readonly TextWriter            _output;
    private readonly DisplaySecretRedactor _redactor;

    /// <summary>
    ///     <para>已提示中断的说明；与运行 ID 共同识别中断，避免只读续聊重复提示。</para>
    ///     <para>
    ///         The note of the prompted interruption; together with its run ID,
    ///         identifies the interruption to avoid repeat prompts during read-only follow-ups.
    ///     </para>
    /// </summary>
    private string? _promptedInspectionNote;

    private string? _promptedInspectionRunId;

    public ChatSessionConsole(ChatSessionHost host, TextReader input, TextWriter output, ChatCancellation cancellation,
                              IReadOnlyDictionary<string, string>? knownSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(cancellation);
        _host         = host;
        _input        = input;
        _output       = output;
        _cancellation = cancellation;
        _redactor     = new DisplaySecretRedactor(knownSecrets);
    }

    /// <summary>
    ///     <para>本控制台服务的会话。</para>
    ///     <para>The session served by this console.</para>
    /// </summary>
    public ChatSession Session => _host.Session;

    /// <summary>
    ///     <para>运行输入循环，直到 EOF、空闲 Ctrl+C 或会话取消。</para>
    ///     <para>Runs the input loop until EOF, idle Ctrl+C, or session cancellation.</para>
    /// </summary>
    /// <remarks>
    ///     <para>回合异常由宿主转换为终态；提交被拒绝时报告原因并继续等待输入。</para>
    ///     <para>
    ///         The host converts turn exceptions into terminal results; rejected submissions are reported and input
    ///         continues.
    ///     </para>
    /// </remarks>
    /// <param name="cancellationToken">
    ///     <para>传入回合与循环边界操作的外部取消令牌；阻塞输入使用会话取消令牌。</para>
    ///     <para>
    ///         External cancellation for turns and loop-boundary operations;
    ///         blocking input uses the session cancellation token.
    ///     </para>
    /// </param>
    /// <returns>
    ///     <para>干净结束返回 0；会话经取消协调器结束返回 130。</para>
    ///     <para>0 for a clean end; 130 when the cancellation coordinator ends the session.</para>
    /// </returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        await _output.WriteLineAsync("Type one request per line; each line starts a new turn in this session.");
        await _output.WriteLineAsync("Ctrl+C cancels the active turn; Ctrl+C at the prompt or EOF ends the session.");
        await PromptInspectionIfNeededAsync(cancellationToken);

        while (!_cancellation.IsSessionCancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _output.WriteAsync("you> ");
            await _output.FlushAsync(cancellationToken);

            string? line;
            try
            {
                line = await _input.ReadLineAsync(_cancellation.SessionToken);
            }
            catch (OperationCanceledException)
            {
                // 空闲 Ctrl+C 或会话释放取消当前读取，并结束会话。
                break;
            }

            if (line is null) break; // EOF 正常结束会话。

            var userInput = line.Trim();
            if (userInput.Length == 0) continue;

            await SubmitTurnAsync(userInput, cancellationToken);
        }

        await _output.WriteLineAsync("Session ended.");
        return _cancellation.IsSessionCancelled ? 130 : 0;
    }

    /// <summary>
    ///     <para>等待回合终态后输出摘要、保存警告与必要的检查确认。</para>
    ///     <para>Waits for a terminal turn, then prints its summary, save warning, and any required inspection prompt.</para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         参数或活动运行冲突导致的提交拒绝会报告后返回；无论提交结果如何，均结束活动回合的取消作用域。
    ///         最终回答已由事件流展示，不在摘要中重印。
    ///     </para>
    ///     <para>
    ///         Submissions rejected for argument errors or active-run conflicts are reported and return;
    ///         the active-turn cancellation scope ends regardless of submission outcome.
    ///         The summary never reprints the final answer already shown by the event stream.
    ///     </para>
    /// </remarks>
    private async Task SubmitTurnAsync(string userInput, CancellationToken cancellationToken)
    {
        var        turnNumber = _host.Session.Turns.Count + 1;
        var        turnToken  = _cancellation.BeginTurn(cancellationToken);
        SessionRun run;
        try
        {
            try
            {
                run = await _host.SubmitTurnAsync(userInput, turnToken);
            }
            catch (ArgumentException ex)
            {
                await _output.WriteLineAsync(TerminalDisplay.Format($"cannot start a turn: {ex.Message}", _redactor));
                return;
            }
            catch (InvalidOperationException ex)
            {
                // 宿主在创建记录前拒绝并发回合；报告后保持会话可用。
                await _output.WriteLineAsync(TerminalDisplay.Format($"cannot start a turn: {ex.Message}", _redactor));
                return;
            }
        }
        finally
        {
            _cancellation.EndTurn();
        }

        await WriteTurnFooterAsync(turnNumber, run.Result!);
        // 保存不可靠必须在所属回合立即告知用户，不能以运行终态代替保存证据。
        if (_host.PersistenceWarning is { Length: > 0 } warning)
            await _output.WriteLineAsync(TerminalDisplay.Format($"warning: this turn was not reliably saved: {warning}",
                                                                _redactor));

        // 新中断仅提示一次；确认前副作用已被阻断，只读分析不受影响。
        await PromptInspectionIfNeededAsync(cancellationToken);

        await _output.WriteLineAsync();
    }

    private async Task PromptInspectionIfNeededAsync(CancellationToken cancellationToken)
    {
        if (!_host.InspectionPending)
        {
            _promptedInspectionNote  = null;
            _promptedInspectionRunId = null;
            return;
        }

        // 跟踪最近的未知副作用运行而非最近回合：只读续聊不重复提示，新中断即使仍在阻断也须再提示。
        var runId = Session.Turns.SelectMany(turn => turn.Runs)
                           .LastOrDefault(run => run.Result?.ToolCalls?.Any(call => call is
                            {
                                SideEffects: true,
                                State: ToolCallExecutionState
                                   .UnknownResult
                            }) == true)?.Id;
        var note = _host.InspectionPendingNote;
        if (note == _promptedInspectionNote && runId == _promptedInspectionRunId) return;

        _promptedInspectionNote  = note;
        _promptedInspectionRunId = runId;
        if (await InspectionPrompt.ConfirmAsync(_host, _input, _output, _redactor, _cancellation.SessionToken))
        {
            await _host.AcknowledgeInspectionAsync(cancellationToken);
            _promptedInspectionNote  = null;
            _promptedInspectionRunId = null;
        }
    }

    /// <summary>
    ///     <para>打印回合状态、计数、已报告的 usage 及必要的终态说明。</para>
    ///     <para>Prints turn status, counts, reported usage, and any required terminal-state detail.</para>
    /// </summary>
    /// <remarks>
    ///     <para>输出先做已知密钥脱敏，再转义控制字符。</para>
    ///     <para>Output is redacted for known secrets before control characters are escaped.</para>
    /// </remarks>
    private async Task WriteTurnFooterAsync(int turnNumber, AgentResult result)
    {
        var summary = new StringBuilder("turn ").Append(turnNumber).Append(" finished: ")
                                                .Append(StatusText(result.Status))
                                                .Append(" (steps: ").Append(result.Steps)
                                                .Append(", tools: ").Append(result.ToolExecutions);
        if (result.InputTokens is not null || result.OutputTokens is not null)
            summary.Append(", ").Append((result.InputTokens      ?? 0).ToString("N0"))
                   .Append(" in / ").Append((result.OutputTokens ?? 0).ToString("N0"))
                   .Append(" out tokens");
        summary.Append(')');

        switch (result.Status)
        {
            case AgentStatus.Failed when !string.IsNullOrWhiteSpace(result.Error) :
                summary.Append(" — ").Append(result.Error);
                break;
            case AgentStatus.Cancelled :
                summary.Append(" — the session continues; start a new turn when ready.");
                break;
            case AgentStatus.StepLimitReached :
                summary.Append(" — the step limit was reached.");
                break;
        }

        await _output.WriteLineAsync(TerminalDisplay.Format(summary.ToString(), _redactor));
    }

    private static string StatusText(AgentStatus status) => status switch
    {
        AgentStatus.Completed        => "completed",
        AgentStatus.Cancelled        => "cancelled",
        AgentStatus.Failed           => "failed",
        AgentStatus.StepLimitReached => "step limit reached",
        _                            => status.ToString().ToLowerInvariant()
    };
}

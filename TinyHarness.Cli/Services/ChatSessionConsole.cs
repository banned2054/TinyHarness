using System.Text;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Session;

namespace TinyHarness.Cli.Services;

/// <summary>
///     chat 交互会话的输入循环（M12.3，PLAN §10）：把 M12.1 的会话生命周期与 M12.2 的事件展示
///     接入连续用户回合。每行输入是当前回合的最新目标（不把第一条输入当作所有后续回合的
///     目标）；回合内回答文本与工具、审批、usage 由共享的宿主事件展示绘制，本循环只在回合
///     结束后打印一行终态摘要，绝不重复打印 <see cref="AgentResult.FinalMessage" />。
///     输入经与审批提示共享的单一读取出口（PLAN §6）：Ctrl+C 由 <see cref="ChatCancellation" />
///     分发——活动回合被取消后循环继续；EOF 或空闲 Ctrl+C 结束会话。重复的回合提交（宿主
///     单活动运行约束）、空白行与失败回合都报告后继续，不终止会话。M13.1 起每个完成的回合
///     都提交到受保护会话存储供离线查看；不可靠的保存以警告行报告，不伪称成功。
///     The input loop of the interactive chat session (M12.3, PLAN §10): it wires the M12.1
///     session lifecycle and the M12.2 event display into continuous user turns. Every input
///     line is the current turn's latest goal (the first input never becomes the goal of all
///     later turns); answer text, tools, approvals and usage inside a turn are drawn by the
///     shared host-event display, and this loop prints only a one-line terminal summary after
///     the turn — it never re-prints <see cref="AgentResult.FinalMessage" />. Input flows
///     through the single read outlet shared with approval prompts (PLAN §6); Ctrl+C is
///     dispatched by <see cref="ChatCancellation" /> — a cancelled active turn returns to the
///     loop, while EOF or an idle Ctrl+C ends the session. Duplicate turn submissions (the
///     host's single-active-run rule), blank lines and failed turns are reported and the
///     session continues. Since M13.1 every finished turn is committed to the protected
///     session store for offline inspection; an unreliable save is reported as a warning
///     line instead of being presented as success.
/// </summary>
internal sealed class ChatSessionConsole
{
    private readonly ChatCancellation      _cancellation;
    private readonly ChatSessionHost       _host;
    private readonly TextReader            _input;
    private readonly TextWriter            _output;
    private readonly DisplaySecretRedactor _redactor;

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

    /// <summary>本控制台服务的会话。The session served by this console.</summary>
    public ChatSession Session => _host.Session;

    /// <summary>
    ///     运行输入循环直到 EOF、空闲 Ctrl+C 或会话取消。退出码：干净结束为 0；经 Ctrl+C
    ///     请求结束为 130。回合执行的异常已在宿主边界转换为终态结果；回合提交被拒绝（会话
    ///     已有活动运行）时报告原因并继续等待输入。
    ///     Runs the input loop until EOF, an idle Ctrl+C, or session cancellation. Exit code:
    ///     0 for a clean end; 130 when ended via Ctrl+C. Turn execution exceptions are already
    ///     converted into terminal results at the host boundary; a rejected turn submission
    ///     (the session already has an active run) is reported and the loop keeps waiting.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        await _output.WriteLineAsync("Type one request per line; each line starts a new turn in this session.");
        await _output.WriteLineAsync("Ctrl+C cancels the active turn; Ctrl+C at the prompt or EOF ends the session.");

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
                // Idle Ctrl+C (or session teardown): the pending read ends the session.
                break;
            }

            if (line is null) break; // EOF: end the session cleanly.

            var userInput = line.Trim();
            if (userInput.Length == 0) continue;

            await SubmitTurnAsync(userInput, cancellationToken);
        }

        await _output.WriteLineAsync("Session ended.");
        return _cancellation.IsSessionCancelled ? 130 : 0;
    }

    /// <summary>
    ///     提交并等待一个回合到终态，然后打印单行摘要。重复提交与其他提交期错误报告后返回，
    ///     会话继续。摘要绝不包含最终回答文本——它已由事件展示流式打印过一次。
    ///     Submits one turn, waits for its terminal state, and prints the one-line summary.
    ///     Duplicate submissions and other submission-time errors are reported and the session
    ///     continues. The summary never contains the final answer text — the event display
    ///     already streamed it exactly once.
    /// </summary>
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
                // The host rejects a second concurrent submission before creating any record
                // (PLAN §5); report it and keep the session usable.
                await _output.WriteLineAsync(TerminalDisplay.Format($"cannot start a turn: {ex.Message}", _redactor));
                return;
            }
        }
        finally
        {
            _cancellation.EndTurn();
        }

        await WriteTurnFooterAsync(turnNumber, run.Result!);
        // A snapshot commit that did not verify must be told to the user right at the
        // turn it belongs to; the run's terminal state is never proof of a save.
        if (_host.PersistenceWarning is { Length: > 0 } warning)
            await _output.WriteLineAsync(TerminalDisplay.Format($"warning: this turn was not reliably saved: {warning}",
                                                                _redactor));
        await _output.WriteLineAsync();
    }

    /// <summary>
    ///     打印回合终态摘要：状态、步数、工具数与（如已报告）usage；失败终态附上错误说明，
    ///     取消终态说明会话可继续。文本先做已知密钥脱敏再转义控制字符，与其他展示面一致。
    ///     Prints the turn's terminal summary: status, steps, tool count and (when reported)
    ///     usage; a failed terminal appends the error detail and a cancelled one notes that
    ///     the session continues. Text is redacted for known secrets and control-escaped,
    ///     matching every other display surface.
    /// </summary>
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

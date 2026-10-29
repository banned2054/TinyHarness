using System.Text;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Persistence;
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
///         <see cref="AgentResult.FinalMessage" />。输入输出、直接传入的宿主与取消协调器由调用方管理。
///     </para>
///     <para>
///         Each nonblank input line is the current turn's latest goal. Turns, approvals,
///         and inspection confirmation share a reader. Host events display answers, tools,
///         approvals, and usage; this loop never reprints <see cref="AgentResult.FinalMessage" />.
///         The caller owns input, output, the directly supplied host, and the cancellation coordinator.
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
internal sealed class ChatSessionConsole : IDisposable
{
    private readonly ChatCancellation                                        _cancellation;
    private readonly Func<CancellationToken, Task<ChatSessionHost>>?         _hostFactory;
    private readonly TextReader                                              _input;
    private readonly TextWriter                                              _output;
    private readonly DisplaySecretRedactor                                   _redactor;
    private readonly Func<string, CancellationToken, Task<ChatSessionHost>>? _resumeFactory;

    private readonly string? _sessionStoreDirectory;

    private string? _pendingSessionCommand;

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
        Host          = host;
        _input        = input;
        _output       = output;
        _cancellation = cancellation;
        _redactor     = new DisplaySecretRedactor(knownSecrets);
    }

    /// <summary>
    ///     <para>建立可选择会话的入口；工厂须先预检规则再创建宿主，本入口接管初始及后续宿主。</para>
    ///     <para>
    ///         Creates a session-selection entry; the factory checks rules before creation, and this entry owns initial and
    ///         subsequent hosts.
    ///     </para>
    /// </summary>
    public ChatSessionConsole(Func<CancellationToken, Task<ChatSessionHost>> hostFactory, TextReader input,
                              TextWriter output, ChatCancellation cancellation,
                              IReadOnlyDictionary<string, string>? knownSecrets = null,
                              Func<string, CancellationToken, Task<ChatSessionHost>>? resumeFactory = null,
                              string? sessionStoreDirectory = null, ChatSessionHost? initialHost = null)
    {
        ArgumentNullException.ThrowIfNull(hostFactory);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(cancellation);
        _hostFactory           = hostFactory;
        _input                 = input;
        _output                = output;
        _cancellation          = cancellation;
        _redactor              = new DisplaySecretRedactor(knownSecrets);
        _resumeFactory         = resumeFactory;
        _sessionStoreDirectory = sessionStoreDirectory;
        Host                   = initialHost;
    }

    /// <summary>
    ///     <para>本控制台服务的会话。</para>
    ///     <para>The session served by this console.</para>
    /// </summary>
    public ChatSession Session => Host?.Session ??
                                  throw new
                                      InvalidOperationException("No session has been selected or created yet.");

    internal ChatSessionHost? Host { get; private set; }

    /// <summary>
    ///     <para>会话内 /models 命令处理器；为空时该入口不可用。两条输入路径共用，运行中仅查询。</para>
    ///     <para>
    ///         The in-session /models handler; the entry is unavailable when null. Both input paths share it, and only
    ///         queries run during a run.
    ///     </para>
    /// </summary>
    internal ChatModelSelection? ModelSelection { get; set; }

    /// <summary>
    ///     <para>仅由宿主在空闲边界处理工作区信任，不产生模型回合。</para>
    ///     <para>Handles workspace trust only at an idle host boundary without creating a model turn.</para>
    /// </summary>
    internal Func<CancellationToken, Task>? WorkspaceTrustHandler { get; set; }

    /// <summary>
    ///     <para>释放由工厂入口接管的当前宿主。</para>
    ///     <para>Disposes the current host owned by the factory-based entry.</para>
    /// </summary>
    public void Dispose()
    {
        if (_hostFactory is not null) Host?.Dispose();
    }

    /// <summary>
    ///     <para>查询已创建的会话，不触发工厂。</para>
    ///     <para>Queries the created session without invoking the factory.</para>
    /// </summary>
    public bool TryGetSession(out ChatSession? session)
    {
        session = Host?.Session;
        return session is not null;
    }

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
        if (_input is ChatInputRouter)
            await
                _output.WriteLineAsync("During a run, text adds an amendment. Controls: /pause /resume /cancel /work. Approval replies stay separate.");
        if (_resumeFactory is not null)
        {
            await
                _output.WriteLineAsync("Sessions: /new, /sessions, /switch <session-id>. During a run, switching waits for cancellation and cleanup.");
            if (Host is null) await HandleSessionCommandAsync("/sessions", cancellationToken);
            else await WriteUnappliedAmendmentsAsync();
        }

        if (ModelSelection is not null)
            await
                _output.WriteLineAsync("Models: /models to switch model or reasoning effort when idle; /models list anytime.");

        await PromptInspectionIfNeededAsync(cancellationToken);

        while (!_cancellation.IsSessionCancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Interlocked.Exchange(ref _pendingSessionCommand, null) is { } pending)
            {
                await HandleSessionCommandAsync(pending, cancellationToken);
                await PromptInspectionIfNeededAsync(cancellationToken);
            }

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

            if (IsSessionCommand(userInput))
            {
                await HandleSessionCommandAsync(userInput, cancellationToken);
                continue;
            }

            if (ChatModelSelection.IsModelsCommand(userInput))
            {
                await HandleModelsCommandAsync(userInput, false, cancellationToken);
                continue;
            }

            if (userInput is "/pause" or "/resume" or "/cancel" or "/work")
            {
                await HandleRunInputAsync(userInput).ConfigureAwait(false);
                continue;
            }

            if (userInput == "/trust" && WorkspaceTrustHandler is not null)
            {
                try
                {
                    await WorkspaceTrustHandler(_cancellation.SessionToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when
                    (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    await
                        _output.WriteLineAsync(TerminalDisplay
                                                  .Format($"Workspace trust update failed; access may have been restricted: {ex.Message}",
                                                          _redactor));
                }

                continue;
            }

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
    ///         首发工厂或发送前校验失败（含配置与凭据错误）同样报告后返回，空白态保持可重试。
    ///         最终回答已由事件流展示，不在摘要中重印。
    ///     </para>
    ///     <para>
    ///         Submissions rejected for argument errors or active-run conflicts are reported and return;
    ///         the active-turn cancellation scope ends regardless of submission outcome.
    ///         First-send factory or pre-send validation failures (including configuration and credential errors)
    ///         are likewise reported and return, leaving the blank state retryable.
    ///         The summary never reprints the final answer already shown by the event stream.
    ///     </para>
    /// </remarks>
    private async Task SubmitTurnAsync(string userInput, CancellationToken cancellationToken)
    {
        var        turnNumber = (Host?.Session.Turns.Count ?? 0) + 1;
        var        turnToken  = _cancellation.BeginTurn(cancellationToken);
        SessionRun run;
        try
        {
            try
            {
                if (Host is null)
                {
                    Host = await _hostFactory!(turnToken).ConfigureAwait(false);
                    await _output.WriteLineAsync($"session    : {Host.Session.Id}").ConfigureAwait(false);
                }

                if (ModelSelection is not null)
                    await ModelSelection.ValidateBeforeSendAsync(Host, turnToken).ConfigureAwait(false);
                var pendingRun = Host.SubmitTurnAsync(userInput, turnToken);
                if (_input is ChatInputRouter router) router.SetActiveHandler(HandleRunInputAsync);
                try
                {
                    run = await pendingRun.ConfigureAwait(false);
                }
                finally
                {
                    if (_input is ChatInputRouter completedRouter)
                        await completedRouter.ClearActiveHandlerAsync().ConfigureAwait(false);
                }
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
            catch (Exception ex) when (ex is SessionStoreException or IOException or InvalidDataException
                                          or UnauthorizedAccessException or ConfigException)
            {
                // 空白草稿的凭据迟至首发工厂才解析；缺凭据等配置失败在此报告并保持空白态可重试。
                await _output.WriteLineAsync(TerminalDisplay.Format($"cannot start a turn: {ex.Message}", _redactor));
                return;
            }
            catch (OperationCanceledException) when (Host is null)
            {
                await _output.WriteLineAsync("First submission cancelled before a session was created.");
                return;
            }
        }
        finally
        {
            _cancellation.EndTurn();
        }

        await WriteTurnFooterAsync(turnNumber, run.Result!);
        var unapplied =
            Host.Session.Amendments.Count(item => item.RunId == run.Id && item.CanonicalMessageIndex is null);
        if (unapplied > 0)
            await
                _output.WriteLineAsync($"{unapplied} saved amendment(s) were not applied; inspect them and submit a new turn to continue.");
        // 保存不可靠必须在所属回合立即告知用户，不能以运行终态代替保存证据。
        if (Host.PersistenceWarning is { Length: > 0 } warning)
            await _output.WriteLineAsync(TerminalDisplay.Format($"warning: this turn was not reliably saved: {warning}",
                                                                _redactor));

        // 新中断仅提示一次；确认前副作用已被阻断，只读分析不受影响。
        if (Volatile.Read(ref _pendingSessionCommand) is null)
            await PromptInspectionIfNeededAsync(cancellationToken);

        await _output.WriteLineAsync();
    }

    private async Task HandleRunInputAsync(string? line)
    {
        if (line is null)
        {
            Host?.CancelRun();
            return;
        }

        var text = line.Trim();
        if (text.Length == 0) return;
        if (IsSessionCommand(text))
        {
            await HandleSessionCommandAsync(text, _cancellation.SessionToken, true).ConfigureAwait(false);
            return;
        }

        if (ChatModelSelection.IsModelsCommand(text))
        {
            await HandleModelsCommandAsync(text, true, _cancellation.SessionToken).ConfigureAwait(false);
            return;
        }

        if (Volatile.Read(ref _pendingSessionCommand) is not null)
        {
            await _output.WriteLineAsync("Session change pending; wait for the next input prompt.");
            return;
        }

        try
        {
            var message = text switch
            {
                "/pause" => Host?.RequestPause() == true
                    ? "Pause requested; the current operation finishes at a safe boundary."
                    : "No active run.",
                "/resume" => Host?.ResumeRun() == true ? "Resume requested." : "No active run.",
                "/cancel" => Host?.CancelRun() == true
                    ? "Cancellation requested; waiting for cleanup."
                    : "No active run.",
                "/work" => Host is null
                    ? "No session yet."
                    : $"Work-item statuses are claims, not verified outcomes. Unapplied amendments: {Host.Session.Amendments.Count(item => item.CanonicalMessageIndex is null)}.\n" +
                      string.Join('\n',
                                  Host.Session.WorkItems.Select(item =>
                                                                    $"{item.Id} [{item.Status}] {item.DisplayTitle}")),
                _ => string.Empty
            };
            if (message.Length == 0)
            {
                if (Host is null) throw new InvalidOperationException("No active session.");
                var accepted = await Host.QueueAmendmentAsync(line, _cancellation.SessionToken).ConfigureAwait(false);
                message = $"Amendment {accepted.Id} saved and queued; it applies at the next safe boundary.";
            }

            await _output.WriteLineAsync(TerminalDisplay.Format(message)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException
                                      or OperationCanceledException)
        {
            await _output.WriteLineAsync(TerminalDisplay.Format($"Input was not accepted: {ex.Message}", _redactor))
                         .ConfigureAwait(false);
        }
    }

    private static bool IsSessionCommand(string input) =>
        input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is "/new" or "/sessions"
         or "/switch";

    /// <summary>
    ///     <para>处理 /models 命令；无会话或处理器未接线时明确提示，异常经脱敏报告且不影响会话。</para>
    ///     <para>
    ///         Handles /models commands; missing sessions or wiring are reported clearly, and failures are redacted without
    ///         affecting the session.
    ///     </para>
    /// </summary>
    private async Task HandleModelsCommandAsync(string input, bool duringRun, CancellationToken cancellationToken)
    {
        if (ModelSelection is null)
        {
            await _output.WriteLineAsync("Model selection is unavailable in this console.");
            return;
        }

        try
        {
            await ModelSelection.HandleAsync(Host, input, duringRun, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsSessionCancelled ||
                                                 cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException
                                      or IOException or UnauthorizedAccessException or ConfigException)
        {
            await _output.WriteLineAsync(TerminalDisplay.Format($"Model selection failed: {ex.Message}", _redactor));
        }
    }

    /// <summary>
    ///     <para>
    ///         在宿主边界管理会话；运行中只排队切换，恢复失败保留当前宿主。
    ///         <c>/new</c> 进入未发送空白态：释放当前宿主但不调用工厂，不创建会话、写入端或项目，
    ///         首次普通输入才建立真实会话。
    ///     </para>
    ///     <para>
    ///         Manages sessions at host boundaries; queues switches during runs and keeps the current host on restore
    ///         failure. <c>/new</c> enters the unsent blank state: it releases the current host without invoking the
    ///         factory, creates no session, writer, or project, and the first ordinary input establishes the real
    ///         session.
    ///     </para>
    /// </summary>
    private async Task HandleSessionCommandAsync(
        string input, CancellationToken cancellationToken, bool duringRun = false)
    {
        if (_hostFactory is null || _resumeFactory is null || _sessionStoreDirectory is null)
        {
            await _output.WriteLineAsync("Session navigation is unavailable in this console.");
            return;
        }

        var parts = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if ((parts[0] is "/new" or "/sessions" && parts.Length != 1) ||
            (parts[0] == "/switch" && (parts.Length != 2 || !SessionStorePaths.IsValidSessionId(parts[1]))))
        {
            await _output.WriteLineAsync("Usage: /new | /sessions | /switch <session-id> (32 hex characters).");
            return;
        }

        try
        {
            switch (parts[0])
            {
                case "/sessions" :
                {
                    var entries = await SessionStoreReader.ListAsync(_sessionStoreDirectory, cancellationToken);
                    await _output.WriteLineAsync("Sessions (* current):");
                    if (entries.Count == 0)
                        await _output.WriteLineAsync("  (no stored sessions; type a request to create one)");
                    foreach (var entry in entries)
                    {
                        var marker = entry.Id == Host?.Session.Id ? "*" : " ";
                        var detail = entry.Session is { } session
                            ? $"updated {session.UpdatedUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss}  turns {session.Turns.Count}  {session.Model?.ModelId}  {session.WorkspaceRoot}"
                            : $"damaged: {entry.Error}";
                        await _output.WriteLineAsync(TerminalDisplay.Format($"{marker} {entry.Id}  {detail}",
                                                                            _redactor));
                    }

                    return;
                }
                case "/switch" when string.Equals(parts[1], Host?.Session.Id, StringComparison.OrdinalIgnoreCase) :
                    await _output.WriteLineAsync($"Already in session {Host!.Session.Id}.");
                    return;
            }

            if (duringRun)
            {
                if (Interlocked.CompareExchange(ref _pendingSessionCommand, input, null) is not null)
                {
                    await _output.WriteLineAsync("A session change is already pending; wait for cleanup.");
                    return;
                }

                Host?.CancelRun();
                await _output.WriteLineAsync("Session change queued; waiting for the current turn to finish cleanup.");
                return;
            }

            if (Host?.PersistenceWarning is { Length: > 0 })
            {
                await
                    _output.WriteLineAsync("Cannot leave this session: its latest changes were not reliably saved. Restart after inspecting the session log.");
                return;
            }

            if (parts[0] == "/new")
            {
                // 显式新建只进入未发送空白态：不调用工厂，不创建会话、写入端或项目；
                // 保存警告守卫已在上方拒绝到达此处，当前宿主在此释放且历史保持可恢复。
                var abandoned = Host;
                Host = null;
                abandoned?.Dispose();
                _promptedInspectionNote  = null;
                _promptedInspectionRunId = null;
                await _output.WriteLineAsync("new session: blank; the first message creates and saves it.");
                return;
            }

            // 目标持锁并恢复成功后才释放当前宿主，失败不得丢弃当前上下文。
            var next     = await _resumeFactory(parts[1].ToLowerInvariant(), cancellationToken).ConfigureAwait(false);
            var previous = Host;
            Host = next;
            previous?.Dispose();
            _promptedInspectionNote  = null;
            _promptedInspectionRunId = null;
            await _output.WriteLineAsync($"session    : {next.Session.Id} ({next.Session.Turns.Count} turns)");
            await WriteUnappliedAmendmentsAsync();
            await PromptInspectionIfNeededAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (_cancellation.IsSessionCancelled ||
                                                 cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SessionStoreException or IOException or InvalidDataException
                                      or InvalidOperationException or UnauthorizedAccessException or ArgumentException
                                      or ConfigException)
        {
            await _output.WriteLineAsync(TerminalDisplay.Format($"Session command failed: {ex.Message}", _redactor));
        }
    }

    private async Task WriteUnappliedAmendmentsAsync()
    {
        var unapplied = Host?.Session.Amendments.Count(item => item.CanonicalMessageIndex is null) ?? 0;
        if (unapplied > 0)
            await
                _output.WriteLineAsync($"{unapplied} saved amendment(s) remain unapplied; inspect with session show and resubmit to continue.");
    }

    private async Task PromptInspectionIfNeededAsync(CancellationToken cancellationToken)
    {
        if (Host is not { InspectionPending: true })
        {
            _promptedInspectionNote  = null;
            _promptedInspectionRunId = null;
            return;
        }

        // 跟踪最近的未知副作用运行而非最近回合：只读续聊不重复提示，新中断即使仍在阻断也须再提示。
        var runId = Session.Turns.SelectMany(turn => turn.Runs)
                           .LastOrDefault(run => run.Result?.ToolCalls?.Any(call => call is
                            {
                                SideEffects: true, State: ToolCallExecutionState.UnknownResult
                            }) == true)?.Id;
        var note = Host.InspectionPendingNote;
        if (note == _promptedInspectionNote && runId == _promptedInspectionRunId) return;

        _promptedInspectionNote  = note;
        _promptedInspectionRunId = runId;
        if (!await InspectionPrompt.ConfirmAsync(Host, _input, _output, _redactor, _cancellation.SessionToken)) return;

        await Host.AcknowledgeInspectionAsync(cancellationToken);
        _promptedInspectionNote  = null;
        _promptedInspectionRunId = null;
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
        if (result.Budget is { } budget)
            summary.Append($" [requests: {budget.ModelRequests}, budget tokens: {budget.AccountedTokens:N0}" +
                           $"{(budget.HasEstimatedTokens ? " estimated" : " reported")}, elapsed: {budget.ElapsedMilliseconds} ms]");

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
            case AgentStatus.BudgetLimitReached :
                summary.Append($" — budget limit reached: {result.Budget?.LimitReason ?? "unknown"}.");
                break;
        }

        await _output.WriteLineAsync(TerminalDisplay.Format(summary.ToString(), _redactor));
    }

    private static string StatusText(AgentStatus status) => status switch
    {
        AgentStatus.Completed          => "completed",
        AgentStatus.Cancelled          => "cancelled",
        AgentStatus.Failed             => "failed",
        AgentStatus.StepLimitReached   => "step limit reached",
        AgentStatus.BudgetLimitReached => "budget limit reached",
        _                              => status.ToString().ToLowerInvariant()
    };
}

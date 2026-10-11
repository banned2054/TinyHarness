using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Session;

namespace TinyHarness.Cli.Services;

    /// <summary>
    ///     run 命令的宿主事件展示（M12.2）：assistant 文本增量流式打印，工具以"开始 → 终态"
    ///     单行呈现，交互式审批结论、usage、压缩与错误各占一行。所有打印文本先做已知密钥
    ///     脱敏（增量经跨片段流式脱敏），再转义控制字符，避免不可见内容误导。文本流之外的
    ///     行首会先补换行，保证事件行不被流式文本截断；审批请求先关掉未闭合的流式行，避免
    ///     审批提示与回答文本拼行；终态事件收尾未闭合的行并冲刷全部脱敏缓冲，命令摘要随后
    ///     正常输出。审批交互界面仍由 <see cref="ConsoleApprovalProvider" /> 绘制，
    ///     <see cref="HostEventKind.ApprovalRequested" /> 与 <see cref="HostEventKind.ToolPrepared" />
    ///     在此不重复显示；非交互的规则允许/拒绝也不打印，避免只读工具刷屏。
    ///     The run command's host-event display (M12.2): assistant text deltas stream
    ///     directly, tools render as one "started → finished" line, and interactive approval
    ///     resolutions, usage, compaction and errors each get a line. Every printed text is
    ///     first redacted for known secrets (deltas through cross-fragment streaming
    ///     redaction) and then has control characters escaped, so invisible content cannot
    ///     mislead. A newline is emitted before a line event when streamed text left the
    ///     line open; an approval request closes the open streamed line first so the
    ///     approval prompt never merges into the answer text; the terminal event closes a
    ///     trailing open line and drains the whole redaction buffer so the command summary
    ///     starts clean. The interactive approval UI stays with
    ///     <see cref="ConsoleApprovalProvider" />, so
    ///     <see cref="HostEventKind.ApprovalRequested" /> and
    ///     <see cref="HostEventKind.ToolPrepared" /> are not duplicated here; non-interactive
    ///     rule allows/denies print nothing to keep read-only tools quiet.
    /// </summary>
internal sealed class ConsoleHostEventSink : IHostEventSink
{
    private readonly DisplaySecretRedactor _redactor;

    private readonly TextWriter            _output;

    private bool _lineOpen;

    private bool _toolLineOpen;

    public ConsoleHostEventSink(IReadOnlyDictionary<string, string>? knownSecrets = null, TextWriter? output = null)
    {
        _redactor = new DisplaySecretRedactor(knownSecrets);
        _output   = output ?? Console.Out;
    }

    /// <summary>
    ///     事件在运行内串行到达（契约：发布方内联等待），展示状态无需加锁。文本增量流之间
    ///     必有其他事件，该边界冲刷脱敏缓冲的安全部分，把可能的密钥前缀留给下一条流拼接
    ///     匹配；终态事件冲刷全部剩余缓冲。
    ///     Events arrive serially within a run (the contract: the publisher awaits inline),
    ///     so the display state needs no locking. Another event always separates two delta
    ///     streams: that boundary flushes the redactor's safe part and holds a possible
    ///     secret prefix for matching against the next stream; the terminal event flushes
    ///     everything.
    /// </summary>
    public async ValueTask PublishAsync(HostEvent @event, CancellationToken cancellationToken)
    {
        switch (@event.Kind)
        {
            case HostEventKind.AssistantTextDelta when @event.Payload is string delta :
                await WriteDeltaAsync(delta, cancellationToken).ConfigureAwait(false);
                break;

            case HostEventKind.ToolStarted when @event.Payload is ToolPreparation started :
                await CloseOpenLineAsync(cancellationToken).ConfigureAwait(false);
                await _output.WriteAsync(TerminalDisplay.Format($"tool: {started.Summary}", _redactor).AsMemory(),
                                         cancellationToken)
                             .ConfigureAwait(false);
                _lineOpen     = true;
                _toolLineOpen = true;
                break;

            case HostEventKind.ToolFinished when @event.Payload is ToolExecutionOutcome outcome :
                await WriteToolFinishedAsync(outcome, cancellationToken).ConfigureAwait(false);
                break;

            case HostEventKind.ApprovalRequested :
                // The approval provider draws its own prompt; retire the streamed line
                // first so the prompt never merges into the answer text.
                await CloseOpenLineAsync(cancellationToken).ConfigureAwait(false);
                break;

            case HostEventKind.ApprovalResolved when @event.Payload is ApprovalResolution resolution &&
                                                     resolution.Decision == PermissionDecision.Ask :
                await WriteLineAsync($"approval: {resolution.Outcome}", cancellationToken).ConfigureAwait(false);
                break;

            case HostEventKind.ModelUsage when @event.Payload is ModelUsage usage &&
                                               (usage.InputTokens is not null || usage.OutputTokens is not null) :
                var summaryNote = usage.SummaryRequest ? " (summary request)" : string.Empty;
                var inputTokens = (usage.InputTokens ?? 0).ToString("N0");
                var outputTokens = (usage.OutputTokens ?? 0).ToString("N0");
                await WriteLineAsync($"usage: {inputTokens} in / {outputTokens} out tokens{summaryNote}",
                                     cancellationToken)
                             .ConfigureAwait(false);
                break;

            case HostEventKind.CompactionApplied when @event.Payload is ContextChange change :
                await WriteLineAsync($"Context: {change.BeforeTokens:N0} -> " +
                                     $"{change.AfterTokens:N0} tokens after compaction",
                                     cancellationToken)
                             .ConfigureAwait(false);
                break;

            case HostEventKind.RunError when @event.Payload is string error :
                await WriteLineAsync($"error: {error}", cancellationToken).ConfigureAwait(false);
                break;

            case HostEventKind.RunFinished :
                // The command prints the final summary itself; the sink only closes a
                // trailing streamed line so that summary starts at column zero. The
                // stream is over: flush everything, including a held-back prefix.
                await CloseOpenLineAsync(cancellationToken, endOfStream : true).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    ///     流式打印一个文本增量：脱敏后转义控制字符（保留换行与制表符）并写入。
    ///     Streams one text delta: redact, escape control characters (keeping newlines and
    ///     tabs) and write.
    /// </summary>
    private async Task WriteDeltaAsync(string delta, CancellationToken cancellationToken)
    {
        // Push already replaced the secrets; Format skips a redundant redaction scan.
        var text = TerminalDisplay.Format(_redactor.Push(delta));
        if (text.Length == 0) return;

        await _output.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
        _lineOpen = true;
    }

    /// <summary>以终态后缀完成工具行；行已被其他事件关闭时整行重写。Finishes the tool line with its terminal suffix; rewrites the whole line when another event closed it.</summary>
    private async Task WriteToolFinishedAsync(ToolExecutionOutcome outcome, CancellationToken cancellationToken)
    {
        var status = outcome.Result.TimedOut ? "timed out" : outcome.Result.Succeeded ? "ok" : "failed";
        var suffix = $" -> {status}";
        if (outcome.Result.ExitCode is { } exitCode) suffix += $" (exit {exitCode})";

        if (outcome.Result.OutputTruncated) suffix += " (output truncated)";

        if (_toolLineOpen)
        {
            await _output.WriteLineAsync(suffix.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await CloseOpenLineAsync(cancellationToken).ConfigureAwait(false);
            await _output.WriteLineAsync(TerminalDisplay.Format($"tool: {outcome.Preparation.Summary}{suffix}",
                                                                _redactor)
                                           .AsMemory(),
                                         cancellationToken)
                         .ConfigureAwait(false);
        }

        _lineOpen     = false;
        _toolLineOpen = false;
    }

    /// <summary>
    ///     输出一行前补换行关闭流式文本行，然后写入事件行。
    ///     Closes an open streamed line with a newline, then writes the event line.
    /// </summary>
    private async Task WriteLineAsync(string text, CancellationToken cancellationToken)
    {
        await CloseOpenLineAsync(cancellationToken).ConfigureAwait(false);
        await _output.WriteLineAsync(TerminalDisplay.Format(text, _redactor).AsMemory(), cancellationToken)
                     .ConfigureAwait(false);
    }

    /// <summary>
    ///     关闭未闭合的流式行：先输出脱敏缓冲的可显示部分，再补换行。中途边界只输出安全
    ///     前缀——流尚未结束，尾部仍可能是跨流拆分密钥的前缀，留给下一条流拼接匹配；终态
    ///     时流已结束，全量冲刷剩余缓冲。
    ///     Closes an open streamed line: first emit the redactor's displayable part, then
    ///     the newline. A mid-stream boundary emits only the safe prefix — the stream has
    ///     not ended, so the tail may still be the prefix of a secret split across streams
    ///     and stays for matching against the next one; at the end of the stream the whole
    ///     remaining buffer is flushed.
    /// </summary>
    private async Task CloseOpenLineAsync(CancellationToken cancellationToken, bool endOfStream = false)
    {
        _toolLineOpen = false;
        var tail = endOfStream ? _redactor.Flush() : _redactor.FlushSafe();
        if (tail.Length > 0)
        {
            await _output.WriteAsync(tail.AsMemory(), cancellationToken).ConfigureAwait(false);
            _lineOpen = true;
        }

        if (!_lineOpen) return;

        await _output.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);
        _lineOpen = false;
    }
}

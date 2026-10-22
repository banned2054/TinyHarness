using TinyHarness.Cli.Models;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Session;

namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>run 和 chat 的宿主事件展示：等待耗时、可选思考内容、流式回答与工具生命周期。</para>
///     <para>
///         Host-event display for run and chat: elapsed waiting time, optional reasoning, streamed answers and tool
///         lifecycle.
///     </para>
/// </summary>
/// <remarks>
///     <para>文本跨片段脱敏后转义控制字符；其他输出和审批前收尾当前行。审批界面由 ConsoleApprovalProvider 绘制。</para>
///     <para>
///         Text is redacted across fragments before escaping controls; lines close before other output and approvals.
///         ConsoleApprovalProvider draws prompts.
///     </para>
/// </remarks>
internal sealed partial class ConsoleHostEventSink(
    IReadOnlyDictionary<string, string>? knownSecrets     = null,
    TextWriter?                          output           = null,
    ReasoningDisplayMode                 reasoningDisplay = ReasoningDisplayMode.Status,
    bool?                                animateThinking  = null,
    TimeProvider?                        timeProvider     = null) : IHostEventSink, IAsyncDisposable
{
    private readonly TextWriter            _output   = output ?? Console.Out;
    private readonly DisplaySecretRedactor _redactor = new(knownSecrets);

    private bool _lineOpen;

    private bool _toolLineOpen;

    /// <summary>
    ///     <para>串行输出宿主事件和动画；文本在展示前跨片段脱敏，终态冲刷剩余缓冲。</para>
    ///     <para>Serializes host events and animation; redacts text across fragments and drains the buffer on completion.</para>
    /// </summary>
    public async ValueTask PublishAsync(HostEvent @event, CancellationToken cancellationToken)
    {
        await _displayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_animationTask is { IsFaulted: true }) await _animationTask.ConfigureAwait(false);
            await PublishCoreAsync(@event, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _displayGate.Release();
        }
    }

    private async ValueTask PublishCoreAsync(HostEvent @event, CancellationToken cancellationToken)
    {
        if (@event.Kind == HostEventKind.ModelRequestStarted && @event.Payload is ModelRequestProgress startedRequest)
        {
            await StopThinkingAsync(cancellationToken).ConfigureAwait(false);
            await CloseReasoningAsync(cancellationToken).ConfigureAwait(false);
            _hadReasoningContent = false;
            _answerLabelWritten  = false;
            _requestTextStarted  = false;
            await ShowRequestStatusAsync("等待中", startedRequest.ElapsedMilliseconds, cancellationToken)
               .ConfigureAwait(false);
            return;
        }

        if (@event.Kind is HostEventKind.ModelRequestActivity or HostEventKind.ModelRequestWaiting &&
            @event.Payload is ModelRequestProgress progress)
        {
            if (progress.Phase == ModelRequestPhase.ReceivingText)
                await StopThinkingAsync(cancellationToken).ConfigureAwait(false);
            else if (!_reasoningContentOpen && !_requestTextStarted)
                await ShowRequestStatusAsync(progress.Phase == ModelRequestPhase.ReceivingReasoning ? "思考中" : "等待中",
                                             progress.ElapsedMilliseconds, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (@event.Kind is HostEventKind.ModelUsage or HostEventKind.HistoryItemCompleted) return;

        if (@event.Kind == HostEventKind.AssistantReasoningDelta)
        {
            if (IncludeReasoningText && @event.Payload is string { Length: > 0 } reasoning)
            {
                await StopThinkingAsync(cancellationToken).ConfigureAwait(false);
                if (!_reasoningContentOpen)
                {
                    await WriteLineAsync("思考：", cancellationToken).ConfigureAwait(false);
                    _reasoningContentOpen = true;
                    _hadReasoningContent  = true;
                }

                await WriteDeltaAsync(reasoning, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await StopThinkingAsync(cancellationToken).ConfigureAwait(false);
        await CloseReasoningAsync(cancellationToken).ConfigureAwait(false);

        switch (@event.Kind)
        {
            case HostEventKind.ModelRequestFinished when @event.Payload is ModelRequestProgress finishedRequest :
                if (finishedRequest.PartialText)
                    await WriteLineAsync("回答未完成。", cancellationToken).ConfigureAwait(false);
                break;

            case HostEventKind.AssistantTextDelta when @event.Payload is string delta :
                _requestTextStarted = true;
                if (_hadReasoningContent && !_answerLabelWritten)
                {
                    await WriteLineAsync("回答：", cancellationToken).ConfigureAwait(false);
                    _answerLabelWritten = true;
                }

                await WriteDeltaAsync(delta, cancellationToken).ConfigureAwait(false);
                break;

            case HostEventKind.ToolStarted when @event.Payload is ToolPreparation started :
                await CloseOpenLineAsync(cancellationToken).ConfigureAwait(false);
                await _output.WriteAsync(TerminalDisplay.Format($"tool: {started.Summary}", _redactor).AsMemory(),
                                         cancellationToken).ConfigureAwait(false);
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

            case HostEventKind.ApprovalResolved when @event.Payload is
                ApprovalResolution { Decision: PermissionDecision.Ask } resolution :
                await WriteLineAsync($"approval: {resolution.Outcome}", cancellationToken).ConfigureAwait(false);
                break;

            case HostEventKind.CompactionApplied when @event.Payload is ContextChange change :
                await WriteLineAsync($"Context: {change.BeforeTokens:N0} -> " +
                                     $"{change.AfterTokens:N0} tokens after compaction",
                                     cancellationToken).ConfigureAwait(false);
                break;

            case HostEventKind.CompactionFailed when @event.Payload is CompactionFailureReason reason :
                await WriteLineAsync($"Context compaction not applied: {reason}", cancellationToken)
                   .ConfigureAwait(false);
                break;

            case HostEventKind.ProjectInstructionsChanged when @event.Payload is ProjectInstructionsChanged rules :
                await WriteLineAsync($"project instructions: {rules.DocumentCount} document(s), " +
                                     $"revision {rules.Revision} ({rules.Reason})", cancellationToken)
                   .ConfigureAwait(false);
                foreach (var source in rules.Sources ?? [])
                    await WriteLineAsync($"  source: {source}", cancellationToken).ConfigureAwait(false);
                break;

            case HostEventKind.RunError when @event.Payload is string error :
                await WriteLineAsync($"error: {error}", cancellationToken).ConfigureAwait(false);
                break;

            case HostEventKind.RunControlChanged when @event.Payload is string control :
                await WriteLineAsync($"run control: {control}", cancellationToken).ConfigureAwait(false);
                break;

            case HostEventKind.RunFinished :
                // The command prints the final summary itself; the sink only closes a
                // trailing streamed line so that summary starts at column zero. The
                // stream is over: flush everything, including a held-back prefix.
                await CloseOpenLineAsync(cancellationToken, true).ConfigureAwait(false);
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

    /// <summary>
    ///     以终态后缀完成工具行；行已被其他事件关闭时整行重写。Finishes the tool line with its terminal suffix; rewrites the whole line when
    ///     another event closed it.
    /// </summary>
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
                                                                _redactor).AsMemory(), cancellationToken)
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

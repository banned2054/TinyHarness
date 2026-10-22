using System.Diagnostics;
using System.Runtime.CompilerServices;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;

namespace TinyHarness.Core.Services.Agent;

public sealed partial class AgentLoop
{
    /// <summary>
    ///     <para>消费模型流并记录请求状态和 usage；仅返回有明确终止信息的可用回答或工具轮。</para>
    ///     <para>Consumes a model stream and records progress and usage; returns only usable responses with a terminal reason.</para>
    /// </summary>
    /// <remarks>
    ///     <para>校验先于历史追加和工具准备；失败保留已确认工具及预算，不自动重试请求。</para>
    ///     <para>Validation precedes history append and tool preparation; failure retains confirmed tools and budget without retrying.</para>
    /// </remarks>
    /// <param name="publishTextDeltas">
    ///     <para>普通回答发布正文增量；压缩仅发布状态，不向宿主展示摘要或推理。</para>
    ///     <para>Regular responses publish text deltas; compaction publishes progress without summary or reasoning text.</para>
    /// </param>
    private async Task<ChatMessage> RequestAssistantMessageAsync(
        ChatCompletionRequest request, CancellationToken cancellationToken,
        int estimatedPromptTokens, bool publishTextDeltas)
    {
        var       accumulator = new StreamAccumulator();
        var       ended       = false;
        using var charge      = _budget!.BeginRequest(estimatedPromptTokens);
        var requestNumber = _budget.Snapshot.ModelRequests;
        var elapsed       = Stopwatch.StartNew();
        var outcome       = "failed";
        var streamedText  = false;
        try
        {
            await PublishEventAsync(HostEventKind.ModelRequestStarted,
                                    new ModelRequestProgress(requestNumber, !publishTextDeltas, 0), cancellationToken)
               .ConfigureAwait(false);
            await foreach (var value in ReadModelStreamAsync(request, requestNumber, !publishTextDeltas,
                                                             elapsed, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                charge.Append(value);
                accumulator.Append(value);
                if (value.Kind == ChatStreamEventKind.End) ended = true;
                if (publishTextDeltas && value.Kind == ChatStreamEventKind.ContentDelta)
                {
                    streamedText |= !string.IsNullOrWhiteSpace(value.ContentDelta);
                    await PublishEventAsync(HostEventKind.AssistantTextDelta, value.ContentDelta, cancellationToken)
                       .ConfigureAwait(false);
                }
            }

            accumulator.Finish();
            if (accumulator.InputTokens is < 0 || accumulator.OutputTokens is < 0)
                throw new InvalidDataException("Model usage cannot contain negative token counts.");

            // 无效回答也保留已报告的计费事实；缺失 usage 不补为零。
            if (accumulator.InputTokens is not null ||
                accumulator.OutputTokens is not null ||
                accumulator.FinishReason is not null)
            {
                if (_recorder is not null)
                    await _recorder.RecordModelUsageAsync(accumulator.InputTokens, accumulator.OutputTokens,
                                                          accumulator.FinishReason, cancellationToken)
                                   .ConfigureAwait(false);
                await PublishEventAsync(HostEventKind.ModelUsage,
                                        new ModelUsage(accumulator.InputTokens, accumulator.OutputTokens,
                                                       accumulator.FinishReason, !publishTextDeltas), cancellationToken)
                   .ConfigureAwait(false);
                if (accumulator.InputTokens is { } inputTokens)
                    _reportedInputTokens = (_reportedInputTokens ?? 0) + inputTokens;
                if (accumulator.OutputTokens is { } outputTokens)
                    _reportedOutputTokens = (_reportedOutputTokens ?? 0) + outputTokens;
            }

            charge.Complete(accumulator.InputTokens, accumulator.OutputTokens);
            if (accumulator.InputTokens is { } actualInputTokens)
                _context.RecordModelUsage(estimatedPromptTokens, actualInputTokens);

            ValidateModelCompletion(accumulator, ended);
            outcome = "completed";
            return ChatMessage.Assistant(accumulator.Content, accumulator.ToolCalls, accumulator.Reasoning);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = _budget.DeadlineReached ? "budget limit reached" : "cancelled";
            throw;
        }
        catch (RunBudgetExceededException)
        {
            outcome = "budget limit reached";
            throw;
        }
        finally
        {
            await PublishEventAsync(HostEventKind.ModelRequestFinished,
                                    new ModelRequestProgress(requestNumber, !publishTextDeltas, elapsed.ElapsedMilliseconds,
                                                             outcome, streamedText && outcome != "completed"),
                                    CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     <para>串行读取模型流，等待期间每十秒发布状态；同一时刻只有一次流推进和一次计时等待。</para>
    ///     <para>Reads the model stream serially with ten-second wait notices; only one stream move and timer wait are pending.</para>
    /// </summary>
    /// <remarks>
    ///     <para>计时器不并发调用事件接收方；取消和释放仍等待当前流推进结束，不遗留后台读取。</para>
    ///     <para>The timer never publishes concurrently; cancellation and disposal await the pending stream move without orphaned reads.</para>
    /// </remarks>
    private async IAsyncEnumerable<ChatStreamEvent> ReadModelStreamAsync(
        ChatCompletionRequest request, int requestNumber, bool summaryRequest, Stopwatch elapsed,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = _model.CompleteAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        Task<bool>? tick = null;
        while (true)
        {
            var next = stream.MoveNextAsync();
            bool hasNext;
            if (_events is null || next.IsCompletedSuccessfully)
            {
                hasNext = await next.ConfigureAwait(false);
            }
            else
            {
                var pending = next.AsTask();
                while (!pending.IsCompleted)
                {
                    tick ??= timer.WaitForNextTickAsync().AsTask();
                    await Task.WhenAny(pending, tick).ConfigureAwait(false);
                    if (pending.IsCompleted) break;
                    await tick.ConfigureAwait(false);
                    tick = null;
                    // 展示取消不能中断当前 MoveNext 的回收；请求令牌仍传给模型流。
                    await PublishEventAsync(HostEventKind.ModelRequestWaiting,
                                            new ModelRequestProgress(requestNumber, summaryRequest,
                                                                     elapsed.ElapsedMilliseconds), CancellationToken.None)
                       .ConfigureAwait(false);
                }
                hasNext = await pending.ConfigureAwait(false);
            }
            if (!hasNext) yield break;

            yield return stream.Current;
        }
    }

    /// <summary>
    ///     <para>拒绝缺失或矛盾的终止信息、截断响应及仅含推理的空回答，不启动该轮工具。</para>
    ///     <para>Rejects missing or conflicting termination, truncation and reasoning-only empty answers before tools start.</para>
    /// </summary>
    private static void ValidateModelCompletion(StreamAccumulator response, bool ended)
    {
        if (!ended || string.IsNullOrWhiteSpace(response.FinishReason))
            throw new InvalidDataException("Model response is incomplete: a stream boundary or finish reason is missing; no tools from this response were started.");

        var hasTools       = response.ToolCalls.Count > 0;
        var expectedReason = hasTools ? "tool_calls" : "stop";
        if (!string.Equals(response.FinishReason, expectedReason, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Model response has an incomplete, unsupported or inconsistent finish reason; no tools from this response were started.");

        if (!hasTools && string.IsNullOrWhiteSpace(response.Content))
            throw new EmptyModelResponseException();
    }

    /// <summary>
    ///     <para>区分空回答与协议截断，使压缩继续保留 EmptySummary 的诊断契约。</para>
    ///     <para>Distinguishes empty output from protocol truncation so compaction retains its EmptySummary diagnostic.</para>
    /// </summary>
    private sealed class EmptyModelResponseException() : Exception(
        "Model response contained no usable answer or tool calls; reasoning and usage alone do not complete a turn.");
}

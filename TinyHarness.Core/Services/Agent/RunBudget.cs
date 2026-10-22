using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Services.Context;

namespace TinyHarness.Core.Services.Agent;

/// <summary>
///     <para>在唯一执行路径中累计预算，墙钟取消复用调用方的清理路径。</para>
///     <para>Accounts budgets on the single execution path; deadline cancellation reuses caller cleanup.</para>
/// </summary>
internal sealed class RunBudget : IDisposable
{
    private readonly CancellationTokenSource? _deadline;
    private readonly TimeProvider             _clock;
    private readonly long                     _started;
    private readonly RunBudgetLimits          _limits;
    private readonly CancellationTokenSource  _linked;

    private bool    _estimated;
    private string? _reason;
    private int     _requests;
    private long    _tokens;
    private int     _tools;

    internal RunBudget(RunBudgetLimits limits, CancellationToken cancellationToken, TimeProvider? timeProvider = null)
    {
        limits.Validate();
        _limits  = limits;
        _clock   = timeProvider ?? TimeProvider.System;
        _started = _clock.GetTimestamp();
        _deadline = limits.MaxWallClockSeconds > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(limits.MaxWallClockSeconds), _clock)
            : null;
        _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                                                                  _deadline?.Token ?? CancellationToken.None);
    }

    internal CancellationToken Token           => _linked.Token;
    internal bool              DeadlineReached => _deadline?.IsCancellationRequested == true;

    internal RunBudgetSnapshot Snapshot => new()
    {
        ModelRequests       = _requests,
        ToolExecutions      = _tools,
        AccountedTokens     = _tokens,
        HasEstimatedTokens  = _estimated,
        ElapsedMilliseconds = (long)_clock.GetElapsedTime(_started).TotalMilliseconds,
        LimitReason         = _reason ?? (DeadlineReached ? "wall-clock" : null)
    };

    public void Dispose()
    {
        _linked.Dispose();
        _deadline?.Dispose();
    }

    internal void Check()
    {
        if (DeadlineReached) Exceeded("wall-clock");
        Token.ThrowIfCancellationRequested();
        if (_limits.MaxTotalTokens > 0 && _tokens >= _limits.MaxTotalTokens) Exceeded("tokens");
    }

    internal RequestCharge BeginRequest(int estimatedInputTokens)
    {
        Check();
        if (_requests >= _limits.MaxModelRequests) Exceeded("model-requests");
        if (_limits.MaxTotalTokens > 0 && estimatedInputTokens >= _limits.MaxTotalTokens - _tokens) Exceeded("tokens");
        _requests++;
        _tokens += estimatedInputTokens;
        return new RequestCharge(this, estimatedInputTokens);
    }

    internal void CheckTool()
    {
        Check();
        if (_tools >= _limits.MaxToolExecutions) Exceeded("tool-executions");
    }

    internal void ToolStarted() => _tools++;

    private void Exceeded(string reason)
    {
        _reason ??= reason;
        throw new RunBudgetExceededException(_reason);
    }

    /// <summary>
    ///     <para>为单次请求累计流估算，完成时仅将有 usage 的方向替换成报告值。</para>
    ///     <para>Accumulates streaming estimates for one request and replaces only directions with reported usage.</para>
    /// </summary>
    internal sealed class RequestCharge(RunBudget owner, int estimatedInputTokens) : IDisposable
    {
        private long _estimatedOutput;
        private bool _finished;

        public void Dispose()
        {
            if (!_finished) owner._estimated = true;
        }

        internal void Append(ChatStreamEvent value)
        {
            // 逐片段向上取整可以高估；opaque 不代表可解码 token，只作有界载荷估算。
            var added = (long)TokenEstimator.EstimateText(value.ContentDelta)
                      + TokenEstimator.EstimateText(value.ToolCallArgumentsDelta)
                      + TokenEstimator.EstimateText(value.ToolCallFunctionName   ?? string.Empty)
                      + TokenEstimator.EstimateText(value.ReasoningDelta         ?? string.Empty)
                      + TokenEstimator.EstimateText(value.ReasoningProtectedData ?? string.Empty);
            _estimatedOutput += added;
            owner._tokens    += added;
            owner.Check();
        }

        internal void Complete(int? input, int? output)
        {
            if (input is < 0 || output is < 0)
                throw new InvalidDataException("Model usage cannot contain negative token counts.");
            if (input is { } measuredInput) owner._tokens   += measuredInput  - (long)estimatedInputTokens;
            if (output is { } measuredOutput) owner._tokens += measuredOutput - _estimatedOutput;
            owner._estimated |= input is null || output is null;
            _finished        =  true;
            owner.Check();
        }
    }
}

internal sealed class RunBudgetExceededException(string reason) : Exception($"Run budget reached: {reason}.");

using TinyHarness.Cli.Models;

namespace TinyHarness.Cli.Services;

internal sealed partial class ConsoleHostEventSink
{
    private readonly SemaphoreSlim           _displayGate = new(1, 1);
    private readonly CancellationTokenSource _animationCancellation = new();
    private readonly bool                   _animateThinking = animateThinking ?? (output is null && !Console.IsOutputRedirected);
    private readonly TimeProvider           _clock = timeProvider ?? TimeProvider.System;

    private Task?  _animationTask;
    private bool   _thinking;
    private bool   _reasoningContentOpen;
    private bool   _hadReasoningContent;
    private bool   _answerLabelWritten;
    private bool   _requestTextStarted;
    private int    _dots;
    private int    _disposed;
    private string _statusLabel = "等待中";
    private long   _statusElapsedMilliseconds;
    private long   _statusTimestamp;
    private int    _statusColumns;

    public bool IncludeReasoningText => reasoningDisplay == ReasoningDisplayMode.Content;

    /// <summary>
    ///     <para>显示当前请求的等待或思考耗时；重定向输出仅在状态变化时写一行。</para>
    ///     <para>
    ///         Shows elapsed waiting or reasoning time for the current request; redirected output writes only on state
    ///         changes.
    ///     </para>
    /// </summary>
    private async Task ShowRequestStatusAsync(
        string label, long elapsedMilliseconds, CancellationToken cancellationToken)
    {
        _statusElapsedMilliseconds = elapsedMilliseconds;
        _statusTimestamp           = _clock.GetTimestamp();
        var changed = !_thinking || _statusLabel != label;
        _statusLabel = label;
        if (!changed) return;
        if (!_thinking) await CloseOpenLineAsync(cancellationToken).ConfigureAwait(false);
        _thinking = true;
        _dots     = 1;
        if (!_animateThinking)
        {
            await _output.WriteLineAsync($"{label}... {elapsedMilliseconds / 1000}s".AsMemory(), cancellationToken)
                         .ConfigureAwait(false);
            return;
        }

        await WriteThinkingFrameAsync(cancellationToken).ConfigureAwait(false);
        _animationTask ??= AnimateThinkingAsync();
    }

    /// <summary>
    ///     <para>在其他输出和审批前清除动画行，避免覆盖正文或交互提示。</para>
    ///     <para>Clears the animation before other output and approvals so it cannot overwrite text or prompts.</para>
    /// </summary>
    private async Task StopThinkingAsync(CancellationToken cancellationToken)
    {
        if (!_thinking) return;
        if (_animateThinking)
            await _output.WriteAsync(($"\r{new string(' ', _statusColumns)}\r").AsMemory(), cancellationToken)
                         .ConfigureAwait(false);
        _thinking      = false;
        _statusColumns = 0;
    }

    private async Task WriteThinkingFrameAsync(CancellationToken cancellationToken)
    {
        var milliseconds = _statusElapsedMilliseconds + (long)_clock.GetElapsedTime(_statusTimestamp).TotalMilliseconds;
        var seconds      = (milliseconds / 1000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        // 两种状态标题均为三个汉字，按六列计算，补空格清除旧帧的尾部。
        var columns      = 11 + seconds.Length;
        var padding      = new string(' ', Math.Max(0, _statusColumns - columns));
        await _output.WriteAsync(($"\r{_statusLabel}{new string('.', _dots)}{new string(' ', 3 - _dots)} {seconds}s{padding}").AsMemory(),
                                 cancellationToken).ConfigureAwait(false);
        _statusColumns = columns;
    }

    private async Task CloseReasoningAsync(CancellationToken cancellationToken)
    {
        if (!_reasoningContentOpen) return;
        await CloseOpenLineAsync(cancellationToken).ConfigureAwait(false);
        _reasoningContentOpen = false;
    }

    /// <summary>
    ///     <para>动画独立于 SDK 增量推进；所有写入共用事件输出锁，取消后等待任务回收。</para>
    ///     <para>Animates independently of SDK deltas; all writes share the event lock and cancellation joins the task.</para>
    /// </summary>
    private async Task AnimateThinkingAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500), _clock);
        var       token = _animationCancellation.Token;
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await _displayGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (!_thinking) continue;
                    _dots = _dots % 3 + 1;
                    await WriteThinkingFrameAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    _displayGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 释放展示器时正常停止计时，不保留后台写入。
        }
    }

    /// <summary>
    ///     <para>停止并回收动画，再收尾输出；不释放调用方提供的 TextWriter。</para>
    ///     <para>Stops and joins animation before closing output; does not dispose the caller-owned TextWriter.</para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _animationCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_animationTask is not null) await _animationTask.ConfigureAwait(false);
            await StopThinkingAsync(CancellationToken.None).ConfigureAwait(false);
            await CloseOpenLineAsync(CancellationToken.None, true).ConfigureAwait(false);
        }
        finally
        {
            _animationCancellation.Dispose();
            _displayGate.Dispose();
        }
    }
}

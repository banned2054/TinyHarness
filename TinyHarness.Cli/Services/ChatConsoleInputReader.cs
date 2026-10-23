namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>区分 Windows 交互输入被控制键中断后的空读取与真正的 EOF；不接管底层读取器。</para>
///     <para>
///         Distinguishes empty reads interrupted by control keys from genuine EOF in Windows interactive input without
///         owning the inner reader.
///     </para>
/// </summary>
/// <remarks>
///     <para>重定向输入不做恢复。空读取最多等待 250 毫秒关联异步控制键回调，只有该次读取期间发生过中断才重读。</para>
///     <para>
///         Redirected input is never recovered. Empty reads wait up to 250 ms for an asynchronous control-key callback
///         and retry only when an interrupt occurred during that read.
///     </para>
/// </remarks>
internal sealed class ChatConsoleInputReader(
    TextReader inner, ChatCancellation cancellation, bool recoverConsoleInterrupts) : TextReader
{
    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var version = cancellation.InputInterruptVersion;
            var line    = await inner.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is not null || !recoverConsoleInterrupts || cancellation.IsSessionCancelled) return line;

            try
            {
                // Windows 可能先完成空读取，再调度 CancelKeyPress；真实 EOF 则有界返回。
                await cancellation.WaitForInputInterruptAsync(version)
                                  .WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }

            if (cancellation.IsSessionCancelled) return null;
        }
    }

    public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();
}

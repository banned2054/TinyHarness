using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Tests;

/// <summary>
/// A scripted process-execution backend used to observe the contract between
/// ShellTool and IProcessExecutionBackend without launching real processes.
/// </summary>
internal sealed class FakeProcessExecutionBackend : IProcessExecutionBackend
{
    /// <summary>
    /// 依次推入 stdout 捕获器的块；末尾自动追加结束流空块。
    /// Chunks pushed into the stdout capture in order; a trailing end-of-stream
    /// empty chunk is appended automatically.
    /// </summary>
    public IReadOnlyList<string> StdoutChunks { get; init; } = [];

    /// <summary>
    /// 依次推入 stderr 捕获器的块；末尾自动追加结束流空块。
    /// Chunks pushed into the stderr capture in order; a trailing end-of-stream
    /// empty chunk is appended automatically.
    /// </summary>
    public IReadOnlyList<string> StderrChunks { get; init; } = [];

    public int?                     ExitCode       { get; init; }
    public bool                     TimedOut       { get; init; }
    public ProcessExecutionFailure? Failure        { get; init; }

    /// <summary>
    /// 设置后在推送任何块之前原样抛出，用于模拟用户取消或后端异常。
    /// When set, thrown verbatim before any chunk is pushed, to simulate user
    /// cancellation or a backend error.
    /// </summary>
    public Exception? ThrowOnExecute { get; init; }

    public PreparedProcessExecution? ReceivedExecution { get; private set; }

    public async Task<ProcessExecutionResult> ExecuteAsync(PreparedProcessExecution execution,
                                                           IProcessOutputCapture     stdoutCapture,
                                                           IProcessOutputCapture     stderrCapture,
                                                           CancellationToken         cancellationToken)
    {
        ReceivedExecution = execution;
        if (ThrowOnExecute is not null)
        {
            throw ThrowOnExecute;
        }

        await PushAsync(stdoutCapture, StdoutChunks, cancellationToken).ConfigureAwait(false);
        await PushAsync(stderrCapture, StderrChunks, cancellationToken).ConfigureAwait(false);
        return new ProcessExecutionResult(ExitCode, TimedOut, Failure);
    }

    private static async Task PushAsync(IProcessOutputCapture capture, IReadOnlyList<string> chunks,
                                        CancellationToken cancellationToken)
    {
        foreach (var chunk in chunks)
        {
            await capture.AppendAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await capture.AppendAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);
    }
}

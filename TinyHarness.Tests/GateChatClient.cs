using System.Runtime.CompilerServices;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions;

namespace TinyHarness.Tests;

/// <summary>
///     门控模型 client：每个请求先报告"已开始"，然后挂起在门上直到 <see cref="Open" /> 或
///     取消令牌触发；门打开后返回预置文本响应。用于测试并发提交拒绝与运行期取消。
///     A gate-controlled model client: every request first signals "started", then
///     suspends on the gate until <see cref="Open" /> or the cancellation token fires;
///     once open it returns the prepared text response. Used to test concurrent-submit
///     rejection and mid-run cancellation.
/// </summary>
internal sealed class GateChatClient : IChatCompletionClient
{
    private readonly TaskCompletionSource           _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IReadOnlyList<ChatStreamEvent> _response;
    private readonly TaskCompletionSource           _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GateChatClient(string responseText = "done")
    {
        _response = FakeChatClient.Text(responseText);
    }

    public int Requests { get; private set; }

    public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
        ChatCompletionRequest                      request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Requests++;
        _started.TrySetResult();
        await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in _response)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    /// <summary>等待某次模型请求真正开始。Waits until a model request has actually started.</summary>
    public Task WaitStartedAsync(CancellationToken cancellationToken)
    {
        return _started.Task.WaitAsync(cancellationToken);
    }

    public void Open()
    {
        _gate.TrySetResult();
    }
}

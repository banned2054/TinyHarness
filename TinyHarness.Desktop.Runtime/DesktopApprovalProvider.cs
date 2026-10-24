using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Permissions;

namespace TinyHarness.Desktop.Runtime;

/// <summary>
/// <para>一个会话中等待人工裁决的准备计划；标识只在本次等待有效。</para>
/// <para>A prepared invocation awaiting a human decision in one session; its identifier is valid only for this wait.</para>
/// </summary>
public sealed record DesktopApproval(string Id, string ToolName, string Summary, string Arguments,
                                     IReadOnlyList<string> TargetPaths, string? ExecutionPolicy);

/// <summary>
/// <para>将 Core 审批等待交给桌面，取消或过期的裁决不能批准下一次调用。</para>
/// <para>Bridges Core approval waits to the desktop; cancelled or stale decisions cannot approve a later invocation.</para>
/// </summary>
public sealed class DesktopApprovalProvider(Func<DesktopApproval?, ValueTask> changed,
                                           Func<string, string> redact) : IApprovalProvider
{
    private readonly object _sync = new();
    private (DesktopApproval Request, TaskCompletionSource<ApprovalAction> Completion)? _pending;

    /// <summary>
    /// <para>只裁决身份匹配且仍待决的审批，返回是否接受。</para>
    /// <para>Resolves only a matching, still-pending approval and returns whether it was accepted.</para>
    /// </summary>
    public bool Respond(string id, ApprovalAction action)
    {
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        lock (_sync)
            return _pending is { } pending && pending.Request.Id == id && pending.Completion.TrySetResult(action);
    }

    /// <summary>
    /// <para>发布脱敏审批并等待裁决；取消时撤销该请求，不产生默认允许。</para>
    /// <para>Publishes a redacted approval and awaits a decision; cancellation withdraws it without a default allow.</para>
    /// </summary>
    public async Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new DesktopApproval(Guid.NewGuid().ToString("N"), redact(preparation.ToolName),
                                           redact(preparation.Summary), redact(preparation.Arguments.ToJsonString()),
                                           preparation.TargetPaths.Select(redact).ToArray(),
                                           preparation.ExecutionPolicy is { } policy ? redact(policy) : null);
        var completion = new TaskCompletionSource<ApprovalAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (_pending is not null) throw new InvalidOperationException("This session already has a pending approval.");
            _pending = (request, completion);
        }

        try
        {
            await changed(request).ConfigureAwait(false);
            var action = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return action;
        }
        finally
        {
            lock (_sync) _pending = null;
            await changed(null).ConfigureAwait(false);
        }
    }
}

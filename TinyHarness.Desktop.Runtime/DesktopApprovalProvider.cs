using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Services.Permissions;

namespace TinyHarness.Desktop.Runtime;

/// <summary>
///     <para>
///         一个会话中等待人工裁决的审批请求；标识只在本次等待有效。
///     </para>
///     <para>
///         One approval request awaiting a human decision in one session; its identifier is valid only for this
///         wait.
///     </para>
/// </summary>
public sealed record DesktopApproval(
    string                    Id,
    string                    ToolName,
    string                    Summary,
    string                    Arguments,
    IReadOnlyList<string>     TargetPaths,
    string?                   ExecutionPolicy,
    PermissionLevel           Level,
    IReadOnlyList<GrantScope> AllowedScopes,
    string?                   ReviewerNote = null,
    AiReviewResult?           Review       = null,
    IReadOnlyList<string>?    DisplayNotes = null);

/// <summary>
///     <para>将 Core 审批等待交给桌面；取消或过期的裁决不能批准下一次调用。</para>
///     <para>Bridges Core approval waits to the desktop; cancelled or stale decisions cannot approve a later invocation.</para>
/// </summary>
public sealed class DesktopApprovalProvider(
    Func<DesktopApproval?, ValueTask> changed,
    Func<string, string>              redact) : IApprovalProvider
{
    private readonly object                                                                          _sync = new();
    private          (DesktopApproval Request, TaskCompletionSource<ApprovalResolution> Completion)? _pending;

    /// <summary>
    ///     <para>发布脱敏审批并等待裁决；取消时撤销该请求，不产生默认允许。</para>
    ///     <para>Publishes a redacted approval and awaits a decision; cancellation withdraws it without a default allow.</para>
    /// </summary>
    public Task<ApprovalResolution> PromptAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var pending = new DesktopApproval(
                                          Guid.NewGuid().ToString("N"),
                                          redact(request.Preparation.ToolName),
                                          redact(request.Preparation.Summary),
                                          redact(request.Preparation.Arguments.ToJsonString()),
                                          request.Preparation.TargetPaths.Select(redact).ToArray(),
                                          request.Preparation.ExecutionPolicy is { } policy ? redact(policy) : null,
                                          request.Level,
                                          request.AllowedScopes,
                                          request.ReviewerNote is { } note ? redact(note) : null,
                                          request.Review,
                                          request.DisplayNotes?.Select(redact).ToArray());
        var completion =
            new TaskCompletionSource<ApprovalResolution>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (_pending is not null)
                throw new InvalidOperationException("This session already has a pending approval.");
            _pending = (pending, completion);
        }

        return AwaitAsync(pending, completion, cancellationToken);
    }

    /// <summary>
    ///     <para>等待裁决并在结束后撤销等待；取消使请求失效，不产生默认允许。</para>
    ///     <para>
    ///         Awaits the decision and withdraws the wait afterwards; cancellation voids the request without a default
    ///         allow.
    ///     </para>
    /// </summary>
    private async Task<ApprovalResolution> AwaitAsync(
        DesktopApproval   pending, TaskCompletionSource<ApprovalResolution> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            await changed(pending).ConfigureAwait(false);
            var resolution = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return resolution;
        }
        finally
        {
            lock (_sync)
            {
                _pending = null;
            }

            await changed(null).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     <para>只裁决身份匹配且仍待决的审批；过期标识一律不接受。</para>
    ///     <para>Resolves only a matching, still-pending approval; stale identifiers are always rejected.</para>
    /// </summary>
    public bool Respond(string id, ApprovalResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        lock (_sync)
        {
            return _pending is { } pending && pending.Request.Id == id && pending.Completion.TrySetResult(resolution);
        }
    }
}

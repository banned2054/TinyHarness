using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Permissions;

namespace TinyHarness.Core.Services.Agent;

public sealed partial class AgentLoop
{
    /// <summary>
    ///     <para>把实际权限作为本次请求的宿主事实注入，不改写持久化对话。</para>
    ///     <para>Injects effective permissions as host facts for this request without rewriting persisted conversation.</para>
    /// </summary>
    private IReadOnlyList<ChatMessage> PermissionModelView()
    {
        var messages = _context.BuildModelView();
        if (_permissions?.SelectedApprovalMode is null) return messages;
        var notice = ChatMessage.System(
                                        $"当前审批模式：{_permissions.ApprovalMode.Label()}。{_permissions.ApprovalMode.Description()} " +
                                        "沙箱基线操作（工作区读写、读根、沙箱 Shell）自动执行；其余操作需审批。" +
                                        "权限机制只决定工具审批，不扩大用户的任务授权。项目外路径请明确提交；" +
                                        "shell 的 executionScope=host 会以 TinyHarness 进程身份、无 OS 沙箱隔离地在宿主执行，始终需要单次审批，审批拒绝后不得绕过。" +
                                        "workspace 命令对受保护路径（如 .git）报 Permission denied 等沙箱边界错误时，不要原样重试：" +
                                        "先确认此前步骤没有留下不确定的副作用；仅当用户已明确授权对应操作时，才改用 executionScope=host 重新提交该命令并等待审批；" +
                                        "用户已拒绝、撤销或收窄授权的操作不得再次申请，被拒绝的同一操作不会再次询问。");
        return messages.Count == 0 ? [notice] : [messages[0], notice, .. messages.Skip(1)];
    }

    /// <summary>
    ///     <para>
    ///         计算并记录调用的授权结论：引擎求值、授权复用、按模式路由审批、拒绝记忆与不可用处置。
    ///     </para>
    ///     <para>
    ///         Decides and records invocation authorization: engine evaluation, grant reuse, mode-routed approval,
    ///         denial memory and unavailable handling.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         引擎 Allow 时原子消费匹配的单次授权；批准范围是 SingleOperation 时不存储授权（只覆盖本次尝试），
    ///         Turn 与 Session 授权入登记表，并绑定一次性派发凭据供执行前复核。
    ///         拒绝（人工或 AI）写入拒绝记忆；通道不可用保持未执行、不转人工、不写拒绝记忆。
    ///         任务取消仍终止运行；审批作废（用户补充）返回 Granted 由补充延后整轮。
    ///     </para>
    ///     <para>
    ///         On engine Allow the matching single-operation grant is consumed atomically; an approved
    ///         SingleOperation scope stores nothing (it covers exactly this attempt), while Turn and Session grants
    ///         enter the registry and bind a single-dispatch credential for execution-time revalidation.
    ///         Denials (human or AI) feed denial memory; an unavailable channel leaves the operation unexecuted
    ///         without human fallback and without denial memory. Task cancellation still ends the run; an approval
    ///         obsoleted by a user amendment returns Granted so the amendment defers the whole round.
    ///     </para>
    /// </remarks>
    private async Task<AuthorizationOutcome> AuthorizeAsync(
        ToolPreparation preparation, Dictionary<string, ToolCallRecord> records, CancellationToken cancellationToken)
    {
        var revision   = _permissions!.AuthorizationRevision;
        var evaluation = _permissions!.Evaluate(preparation);
        switch (evaluation.Decision)
        {
            case PermissionDecision.Allow :
            {
                var grant = evaluation.Level == PermissionLevel.Level1 ? null : _permissions.ConsumeGrant(preparation);
                if (evaluation.Level != PermissionLevel.Level1 && grant is null)
                    return await UnavailableAsync(preparation, evaluation, records,
                                                  "The matching grant is no longer available.", cancellationToken)
                       .ConfigureAwait(false);
                var dispatch = _permissions.CreateDispatchAuthorization(preparation, revision,
                                                                        grant?.Scope == GrantScope.SingleOperation
                                                                            ? null
                                                                            : grant?.GrantId,
                                                                        grant?.Scope == GrantScope.SingleOperation,
                                                                        grant?.ExpiresUtc);
                var approval = grant is { } spent
                    ? $"allow ({spent.Scope} grant {spent.GrantId})"
                    : "allow";
                records[preparation.CallId].Approval = approval;
                await RecordPermissionAsync(preparation, evaluation, approval, cancellationToken).ConfigureAwait(false);
                await PublishResolutionAsync(new ApprovalResolution(ApprovalResolutionKind.Granted, grant?.Scope, null),
                                             cancellationToken).ConfigureAwait(false);
                return new AuthorizationOutcome(true, string.Empty, dispatch);
            }

            case PermissionDecision.Deny :
            {
                var reason = $"{evaluation.Reason} Denied: {preparation.Summary}";
                records[preparation.CallId].Approval = reason;
                await RecordPermissionAsync(preparation, evaluation, reason, cancellationToken).ConfigureAwait(false);
                await PublishResolutionAsync(ApprovalResolution.Denied(evaluation.Reason), cancellationToken)
                   .ConfigureAwait(false);
                return AuthorizationOutcome.Denied(reason);
            }

            default :
            {
                var approvalProvider = _approver ??
                                       throw new
                                           InvalidOperationException("An approval provider is required when the permission engine returns Ask.");
                var request = new ApprovalRequest(preparation, evaluation.Level,
                                                  GrantScopeOptions.AllowedForLevel(evaluation.Level), null, null);
                await PublishEventAsync(HostEventKind.ApprovalRequested, request, cancellationToken)
                   .ConfigureAwait(false);
                var amendmentToken = _control?.AmendmentToken ?? CancellationToken.None;
                using var approvalToken =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, amendmentToken);
                ApprovalResolution resolution;
                try
                {
                    resolution = await approvalProvider.PromptAsync(request, approvalToken.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (amendmentToken.IsCancellationRequested &&
                                                         !cancellationToken.IsCancellationRequested)
                {
                    await PublishResolutionAsync(
                                                 ApprovalResolution.Unavailable("approval obsolete: user amendment"),
                                                 cancellationToken)
                       .ConfigureAwait(false);
                    return AuthorizationOutcome.Granted;
                }

                switch (resolution.Kind)
                {
                    case ApprovalResolutionKind.Granted :
                    {
                        var scope = resolution.Scope ?? GrantScope.SingleOperation;
                        if (!request.AllowedScopes.Contains(scope))
                            return await UnavailableAsync(preparation, evaluation, records,
                                                          $"The approval returned scope {scope} outside the allowed set.",
                                                          cancellationToken).ConfigureAwait(false);
                        if (_permissions.AuthorizationRevision != revision)
                            return await UnavailableAsync(preparation, evaluation, records,
                                                          "Authorization changed while approval was pending.",
                                                          cancellationToken).ConfigureAwait(false);
                        string? grantId = null;
                        string  approval;
                        switch (scope)
                        {
                            // 单次批准只覆盖当前调用，不存为授权；存入登记表会误批后续相同调用。
                            case GrantScope.SingleOperation :
                                approval = "allow (single operation)";
                                break;
                            case GrantScope.Turn :
                                grantId = _permissions.Grants
                                                      .Add(GrantScope.Turn, preparation, evaluation.MissingCapabilities
                                                              .FirstOrDefault(m => m is "additional-path-write" or
                                                                                  "additional-path-read"
                                                                               or "metadata-write")).GrantId;
                                approval = $"allow (turn grant {grantId})";
                                break;
                            default :
                                grantId = _permissions.Grants
                                                      .Add(GrantScope.Session, preparation, evaluation
                                                              .MissingCapabilities
                                                              .FirstOrDefault(m => m is "additional-path-write" or
                                                                                  "additional-path-read"
                                                                               or "metadata-write")).GrantId;
                                approval = $"allow (session grant {grantId})";
                                break;
                        }

                        var dispatch =
                            _permissions.CreateDispatchAuthorization(preparation, revision, grantId,
                                                                     scope == GrantScope.SingleOperation);
                        records[preparation.CallId].Approval = approval;
                        await RecordPermissionAsync(preparation, evaluation, approval, cancellationToken)
                           .ConfigureAwait(false);
                        await PublishResolutionAsync(ApprovalResolution.Granted(scope), cancellationToken)
                           .ConfigureAwait(false);
                        return new AuthorizationOutcome(true, string.Empty, dispatch);
                    }

                    case ApprovalResolutionKind.Denied :
                    {
                        // 拒绝写入拒绝记忆：同一操作不再弹卡、不再询问，也绝不转人工。
                        _permissions.RecordDenial(preparation);
                        var reason =
                            $"Permission denied by the approver: {resolution.Reason ?? preparation.Summary}";
                        records[preparation.CallId].Approval = reason;
                        await RecordPermissionAsync(preparation, evaluation, reason, cancellationToken)
                           .ConfigureAwait(false);
                        await PublishResolutionAsync(resolution, cancellationToken).ConfigureAwait(false);
                        return AuthorizationOutcome.Denied(reason);
                    }

                    default :
                        return await UnavailableAsync(preparation, evaluation, records,
                                                      resolution.Reason ?? "The approval channel is unavailable.",
                                                      cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    ///     <para>记录通道不可用：操作保持未执行，不转人工、不写拒绝记忆，原因如实回传。</para>
    ///     <para>
    ///         Records an unavailable channel: the operation stays unexecuted without human fallback or denial
    ///         memory, and the reason returns verbatim.
    ///     </para>
    /// </summary>
    private async Task<AuthorizationOutcome> UnavailableAsync(
        ToolPreparation preparation, PermissionEvaluation evaluation, Dictionary<string, ToolCallRecord> records,
        string          detail,      CancellationToken    cancellationToken)
    {
        var reason = $"Approval unavailable; the operation was not executed: {detail}";
        records[preparation.CallId].Approval = reason;
        await RecordPermissionAsync(preparation, evaluation, reason, cancellationToken).ConfigureAwait(false);
        await PublishResolutionAsync(ApprovalResolution.Unavailable(detail), cancellationToken).ConfigureAwait(false);
        return AuthorizationOutcome.Denied(reason);
    }

    /// <summary>
    ///     <para>
    ///         发布审批结论事件；与审计记录一一对应，宿主据此呈现每次权限结论（含规则自动
    ///         允许/拒绝），事件通道只读，不反向影响执行。
    ///     </para>
    ///     <para>
    ///         Publishes the approval-resolution event, one per audit-recorded decision, so
    ///         hosts can render every outcome (rule-based allows and denies included); the
    ///         event channel stays read-only and never influences execution.
    ///     </para>
    /// </summary>
    private async ValueTask PublishResolutionAsync(ApprovalResolution resolution, CancellationToken cancellationToken)
    {
        await PublishEventAsync(HostEventKind.ApprovalResolved, resolution, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordPermissionAsync(
        ToolPreparation   preparation, PermissionEvaluation evaluation, string outcome,
        CancellationToken cancellationToken)
    {
        if (_recorder is not null)
            await _recorder.RecordPermissionAsync(preparation, evaluation.Decision, outcome, cancellationToken)
                           .ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>
    ///         权限阶段的内部结果：是否放行、回传模型的拒绝原因，以及一次性派发凭据。
    ///     </para>
    ///     <para>
    ///         The internal authorization result: the allow flag, a model-facing denial reason and a single-dispatch
    ///         credential.
    ///     </para>
    /// </summary>
    private sealed record AuthorizationOutcome(
        bool                                    Allowed,
        string                                  Reason,
        PermissionEngine.DispatchAuthorization? Dispatch = null)
    {
        public static AuthorizationOutcome Granted { get; } = new(true, string.Empty);

        /// <summary>
        ///     <para>创建带说明的拒绝结果。</para>
        ///     <para>Creates a denied outcome with an explanatory reason.</para>
        /// </summary>
        public static AuthorizationOutcome Denied(string reason) => new(false, reason);
    }
}

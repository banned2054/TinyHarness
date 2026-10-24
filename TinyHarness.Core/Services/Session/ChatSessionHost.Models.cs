using System.Text.Json;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Core.Services.Session;

public sealed partial class ChatSessionHost
{
    /// <summary>
    ///     <para>
    ///         在空闲边界可靠保存并切换模型请求配置；不发送请求，不改写既有对话正文与权限。
    ///     </para>
    ///     <para>
    ///         Reliably saves and switches request configuration while idle, without requests or changes to
    ///         existing conversation content or permissions.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         候选绑定准备期间归调用方；宿主确认接受（含 no-op 短路）后归宿主，替换或关闭时释放。
    ///         拒绝、取消或提交失败以异常返回时所有权不变，仍由调用方释放一次；已落盘的切换不因旧客户端
    ///         释放失败被报告为未提交，也不回滚或重复释放。未绑定宿主（恢复后待重选目标）通过本方法首个候选建立绑定。
    ///     </para>
    ///     <para>
    ///         Candidate bindings belong to the caller until the host accepts them (including the no-op
    ///         short-circuit); the host then owns the binding and releases it on replacement or close. Rejections,
    ///         cancellation, or failed commits return by exception and leave disposal with the caller, exactly once;
    ///         a committed switch is never reported uncommitted because releasing the old client failed — the result
    ///         is neither rolled back nor released twice. An unbound host (a restored session awaiting target
    ///         re-selection) establishes its first binding through this method.
    ///     </para>
    ///     <para>
    ///         候选携带可信配置派生的事实：目标身份、上下文、强度、凭据路由、profile 提示与强度能力快照；
    ///         选择记录据此固化前一修订、选择时间与策略版本。no-op 比较覆盖影响请求和历史解释的全部策略信息，
    ///         不因等价新客户端实例产生修订；冗余候选客户端由宿主释放一次。
    ///     </para>
    ///     <para>
    ///         Candidates carry facts derived from trusted configuration: target identity, context, effort, credential
    ///         route, profile hint, and the effort capability snapshot; the selection record materializes the previous
    ///         revision, selection time, and policy versions from it. The no-op comparison covers every policy input
    ///         that affects requests or history interpretation and never creates a revision merely because an
    ///         equivalent client instance was rebuilt; the redundant candidate client is released once by the host.
    ///     </para>
    ///     <para>
    ///         持久化会话在上下文为空时首次提交选择，会先确立与系统提示一致的 system 消息再一并提交，
    ///         使日志满足恢复契约；不伪造用户消息、回合或运行。无写入端时不确立，选择仅作为内存草稿。
    ///     </para>
    ///     <para>
    ///         When a persisted session commits its first selection on an empty context, the system message matching
    ///         the system prompt is established and committed together, making the log satisfy the resume contract;
    ///         no user message, turn, or run is fabricated. Without a writer nothing is established and the
    ///         selection stays an in-memory draft.
    ///     </para>
    /// </remarks>
    public async Task<SessionModelSelection> ChangeModelAsync(
        SessionModelBinding candidate, CancellationToken cancellationToken,
        long? expectedRevision = null, bool resetReasoning = false)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate.ReasoningEffort is { } effort &&
            candidate.ReasoningCapabilities is { } capabilities &&
            !capabilities.Contains(effort))
            throw new
                ArgumentException("The candidate's reasoning effort is outside its declared capability snapshot; the selection was not changed.",
                                  nameof(candidate));
        EnsureModelOperationsAllowed();
        if (Interlocked.CompareExchange(ref _activeRuns, 2, 0) != 0)
            throw new InvalidOperationException("Model selection requires an idle session.");
        try
        {
            var current = Session.ModelSelection!;
            if (expectedRevision is { } expected && expected != current.Revision)
                throw new
                    InvalidOperationException("The model selection changed while the candidate was being prepared.");
            var boundary = _context.Messages.Count;
            if (boundary > 0 && ChatHistoryInvariants.LastCompleteBoundary(_context.Messages) != boundary)
                throw new InvalidOperationException("Model selection requires a complete conversation boundary.");

            // 空会话首次可靠提交选择前确立 system 上下文，使仅含选择的日志满足恢复契约。
            var establishingContext = boundary == 0 && _sessionWriter is not null;
            if (establishingContext) boundary = 1;

            var newEpoch = resetReasoning ||
                           current.Model != candidate.Model ||
                           !string.Equals(current.CredentialRoute, candidate.CredentialRoute, StringComparison.Ordinal);
            var selection = new SessionModelSelection
            {
                Revision                   = checked(current.Revision + 1),
                PreviousRevision           = current.Revision,
                SelectedUtc                = DateTimeOffset.UtcNow,
                Model                      = candidate.Model,
                ProfileHint                = candidate.ProfileHint,
                CredentialRoute            = candidate.CredentialRoute,
                Context                    = candidate.Context,
                ReasoningEffort            = candidate.ReasoningEffort,
                ReasoningCapabilities      = candidate.ReasoningCapabilities?.ToArray(),
                ReasoningMappingVersion    = ReasoningEffortMappings.CurrentVersion,
                ProjectionVersion          = SessionModelSelection.CurrentProjectionVersion,
                MessageBoundary            = boundary,
                ContinuationEpoch          = checked(current.ContinuationEpoch + (newEpoch ? 1 : 0)),
                ReasoningStartMessageIndex = newEpoch ? boundary : current.ReasoningStartMessageIndex
            };
            selection.Validate();

            // 无变化短路只对已可靠保存且已按 /2 契约记录的选择生效；仅内存种子、未持久化或旧 /1 记录
            // 仍须通过一次提交固化，等价新客户端实例不构成修订。冗余候选的客户端归宿主释放一次。
            var selectionDurable = _sessionWriter is null ||
                                   _sessionWriter.PersistedModelSelectionCount >= Session.ModelSelections.Count;
            if (Volatile.Read(ref _binding) is not null && selectionDurable && !newEpoch &&
                PolicyEquals(current, selection))
            {
                ReleaseCandidateAfterNoOp(candidate);
                return current;
            }

            if (_sessionWriter is not null)
                try
                {
                    if (establishingContext) _context.Append(ChatMessage.System(_systemPrompt));
                    var record = new SessionLogRecord(string.Empty, "model-selection",
                                                      JsonSerializer.SerializeToElement(selection,
                                                          SessionLogJsonContext.Default.SessionModelSelection));
                    await _sessionWriter.CommitControlAsync(Session, _context, [record], cancellationToken)
                                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    PersistenceWarning = "Model selection was not reliably saved: " + ex.Message;
                    throw;
                }

            Session.RecordModelSelection(selection);
            var previous = _binding;
            _binding = candidate;
            _options = _options with
            {
                Model           = candidate.Model.ModelId,
                Context         = candidate.Context,
                ReasoningEffort = candidate.ReasoningEffort
            };
            _context.BindModel(selection);
            PersistenceWarning = null;

            // 提交已确认且切换仅在空闲门内发生：旧绑定此刻无运行引用，立即释放；
            // 同一客户端的借用更替（仅改强度）移交所有权，不释放在用实例；释放失败单独报告。
            if (previous is not null && !previous.TryTransferClientOwnershipTo(candidate))
                ReportRelease(() => previous.Dispose(),
                              "Releasing the superseded model client failed after a committed switch: ");
            return selection;
        }
        finally
        {
            Interlocked.Exchange(ref _activeRuns, 0);
        }
    }

    /// <summary>
    ///     <para>仅调整下一次运行的强度并保留当前凭据路由与连续性；活动运行或过时选择拒绝修改。</para>
    ///     <para>Changes only the next run's effort while keeping the current credential route and continuity, rejecting active runs and stale selections.</para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         省略 <paramref name="declaredCapabilities" /> 时沿用上一选择记录的能力快照；组合层知道当前目录时应传入
    ///         当前声明，使快照反映选择时的事实。未绑定宿主没有可复用客户端，先通过完整候选建立绑定。
    ///     </para>
    ///     <para>
    ///         Omitting <paramref name="declaredCapabilities" /> carries over the last recorded snapshot; callers that
    ///         know the catalog should pass the current declaration so the snapshot reflects selection-time facts.
    ///         An unbound host has no reusable client and must establish a binding through a full candidate first.
    ///     </para>
    /// </remarks>
    public Task<SessionModelSelection> ChangeReasoningEffortAsync(
        ModelReasoningEffort? reasoningEffort, CancellationToken cancellationToken,
        IReadOnlyList<ModelReasoningEffort>? declaredCapabilities = null)
    {
        var current = Session.ModelSelection!;
        var binding = BoundBindingOrThrow();
        return ChangeModelAsync(new SessionModelBinding(current.Model, binding.Client, current.Context,
                                                        reasoningEffort, current.CredentialRoute, current.ProfileHint,
                                                        declaredCapabilities ?? current.ReasoningCapabilities,
                                                        ownsClient           : false),
                                cancellationToken,
                                expectedRevision : current.Revision);
    }

    /// <summary>
    ///     <para>
    ///         核对当前客户端的凭据路由与最近记录的选择路由；不一致时通过正常选择事务采纳当前路由，
    ///         可靠追加新修订并开启新 epoch，隔离旧路由产生的 opaque 推理。
    ///     </para>
    ///     <para>
    ///         Reconciles the current client's credential route against the latest recorded selection; on mismatch
    ///         it adopts the current route through the normal selection transaction, reliably appending a new
    ///         revision and opening a new epoch that isolates opaque reasoning from the earlier route.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         供恢复入口在接管会话后、首次发送请求前调用：历史选择继续记录原路由，不被改写；
    ///         当前路由与记录一致且已可靠保存时不产生新修订，也不重置连续性。
    ///         没有路由记录的历史（null）采纳任何具体路由同样按路由变化处理，保守隔离旧推理。
    ///         提交失败按选择保存失败语义抛出，调用方不得在失败后继续发送请求。
    ///         旧 /1 记录重复同路由时按 /2 追加固化记录（携带策略版本与时间），不视为 no-op。
    ///     </para>
    ///     <para>
    ///         Intended for resume entry points after taking over a session and before the first request:
    ///         historical selections keep their original route and are never rewritten; an identical,
    ///         already-saved route produces no new revision and keeps continuity. History without a recorded
    ///         route (null) adopting any concrete route is also treated as a change, isolating earlier reasoning
    ///         conservatively. A failed commit throws with selection-save semantics; callers must not send
    ///         requests after the failure. A legacy /1 record repeating the same route appends a /2
    ///         materialization record (with policy versions and time) instead of short-circuiting as a no-op.
    ///     </para>
    /// </remarks>
    public Task<SessionModelSelection> EnsureCredentialRouteAsync(
        string credentialRoute, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialRoute);
        var current = Session.ModelSelection!;
        var binding = BoundBindingOrThrow();
        return ChangeModelAsync(new SessionModelBinding(current.Model, binding.Client, current.Context,
                                                        current.ReasoningEffort, credentialRoute, current.ProfileHint,
                                                        current.ReasoningCapabilities,
                                                        ownsClient           : false),
                                cancellationToken,
                                expectedRevision : current.Revision);
    }

    private SessionModelBinding BoundBindingOrThrow() =>
        Volatile.Read(ref _binding) ??
        throw new
            InvalidOperationException("The session has no model binding yet; apply a candidate model selection with a client before reusing it.");

    /// <summary>
    ///     <para>比较影响请求与历史解释的全部策略信息；profile 提示仅影响展示，不参与 no-op 判定。</para>
    ///     <para>Compares every policy input affecting requests and history interpretation; the profile hint affects display only and never decides a no-op.</para>
    /// </summary>
    private static bool PolicyEquals(SessionModelSelection current, SessionModelSelection candidate) =>
        current.Schema                   == candidate.Schema &&
        current.Model                    == candidate.Model &&
        current.Context                  == candidate.Context &&
        current.ReasoningEffort          == candidate.ReasoningEffort &&
        string.Equals(current.CredentialRoute, candidate.CredentialRoute, StringComparison.Ordinal) &&
        CapabilitiesEqual(current.ReasoningCapabilities, candidate.ReasoningCapabilities) &&
        string.Equals(current.ReasoningMappingVersion, candidate.ReasoningMappingVersion, StringComparison.Ordinal) &&
        string.Equals(current.ProjectionVersion, candidate.ProjectionVersion, StringComparison.Ordinal);

    /// <summary>未声明（null）与空集合语义不同，不视为相等。Undeclared (null) and an empty declaration differ and never compare equal.</summary>
    private static bool CapabilitiesEqual(ModelReasoningEffort[]? left, ModelReasoningEffort[]? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.Length == right.Length && left.SequenceEqual(right);
    }

    /// <summary>
    ///     <para>no-op 采纳调用但未启用候选：借用当前客户端的候选只结束生命周期，自有客户端的候选释放一次。</para>
    ///     <para>Drops an unadopted candidate after an accepted no-op: a candidate borrowing the current client only ends its lifetime, an owning candidate releases its client once.</para>
    /// </summary>
    private void ReleaseCandidateAfterNoOp(SessionModelBinding candidate)
    {
        var current = Volatile.Read(ref _binding)!;
        if (ReferenceEquals(current.Client, candidate.Client))
        {
            candidate.MarkReleasedWithoutDisposal();
            return;
        }

        ReportRelease(candidate.Dispose, "Releasing the redundant candidate model client failed: ");
    }

    private void ReportRelease(Action release, string prefix)
    {
        try
        {
            release();
        }
        catch (Exception ex)
        {
            // 已提交结果不因释放失败改变；不重试、不回滚、不重复释放。
            BindingReleaseWarning = prefix + ex.Message;
        }
    }

    private void EnsureModelOperationsAllowed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _activeRuns) == -1, this);
        if (_sessionWriter is { IsHealthy: false })
            throw new InvalidOperationException("The session journal is unhealthy; reopen it before continuing.");
    }
}

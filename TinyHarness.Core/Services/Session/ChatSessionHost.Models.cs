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
    ///         客户端始终由调用方拥有和释放；须保证新旧客户端在相关运行结束前有效，且为输出配置适当脱敏。
    ///         调用方显式提供目标窗口和服务支持的强度；null 强度表示服务默认，null 上下文禁用预算。
    ///     </para>
    ///     <para>
    ///         Callers own and dispose clients, keep them alive through their runs, and configure appropriate output
    ///         redaction. Callers supply target context limits and supported effort; null effort uses the service
    ///         default and null context disables budgeting.
    ///     </para>
    ///     <para>
    ///         <paramref name="credentialRoute" /> 标识产生本选择的凭据路由（profile 与密钥引用），不含密钥值；
    ///         模型身份、路由或显式重置任一变化即开启新 epoch，旧区间的 opaque 推理不再进入请求视图。
    ///         null 路由表示未跟踪；未跟踪选择采纳任何具体路由同样视为路由变化，保守隔离旧推理。
    ///     </para>
    ///     <para>
    ///         <paramref name="credentialRoute" /> identifies the credential route that produced this selection
    ///         (profile and key reference), never the secret value; a change in model identity, route, or an explicit
    ///         reset opens a new epoch, and opaque reasoning from earlier intervals stops entering request views.
    ///         A null route means untracked; an untracked selection adopting any concrete route also counts as a
    ///         route change, isolating earlier reasoning conservatively.
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
        SessionModelIdentity model, IChatCompletionClient modelClient, ContextOptions? contextOptions,
        ModelReasoningEffort? reasoningEffort, CancellationToken cancellationToken,
        string? credentialRoute = null, bool resetReasoning = false, long? expectedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(modelClient);
        if (credentialRoute is not null && string.IsNullOrWhiteSpace(credentialRoute))
            throw new ArgumentException("The credential route must be a non-empty string when provided.",
                                        nameof(credentialRoute));
        cancellationToken.ThrowIfCancellationRequested();
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
                           current.Model != model ||
                           !string.Equals(current.CredentialRoute, credentialRoute, StringComparison.Ordinal);
            var selection = new SessionModelSelection
            {
                Revision                   = checked(current.Revision + 1),
                Model                      = model,
                CredentialRoute            = credentialRoute,
                Context                    = contextOptions,
                ReasoningEffort            = reasoningEffort,
                MessageBoundary            = boundary,
                ContinuationEpoch          = checked(current.ContinuationEpoch + (newEpoch ? 1 : 0)),
                ReasoningStartMessageIndex = newEpoch ? boundary : current.ReasoningStartMessageIndex
            };
            selection.Validate();

            // 无变化短路只对已可靠保存的选择生效；仅在内存初始化的选择（如首次提交前的种子）必须先完成首次持久化。
            var selectionDurable = _sessionWriter is null ||
                                   _sessionWriter.PersistedModelSelectionCount >= Session.ModelSelections.Count;
            if (selectionDurable &&
                !newEpoch &&
                current.Model == model &&
                current.Context == contextOptions &&
                current.ReasoningEffort == reasoningEffort &&
                string.Equals(current.CredentialRoute, credentialRoute, StringComparison.Ordinal) &&
                ReferenceEquals(_modelClient, modelClient))
                return current;

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
            _modelClient = modelClient;
            _options = _options with
            {
                Model           = model.ModelId,
                Context         = contextOptions,
                ReasoningEffort = reasoningEffort
            };
            _context.BindModel(selection);
            PersistenceWarning = null;
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
    public Task<SessionModelSelection> ChangeReasoningEffortAsync(
        ModelReasoningEffort? reasoningEffort, CancellationToken cancellationToken)
    {
        var current = Session.ModelSelection!;
        return ChangeModelAsync(current.Model, _modelClient, current.Context, reasoningEffort, cancellationToken,
                                credentialRoute : current.CredentialRoute,
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
    ///     </para>
    ///     <para>
    ///         Intended for resume entry points after taking over a session and before the first request:
    ///         historical selections keep their original route and are never rewritten; an identical,
    ///         already-saved route produces no new revision and keeps continuity. History without a recorded
    ///         route (null) adopting any concrete route is also treated as a change, isolating earlier reasoning
    ///         conservatively. A failed commit throws with selection-save semantics; callers must not send
    ///         requests after the failure.
    ///     </para>
    /// </remarks>
    public Task<SessionModelSelection> EnsureCredentialRouteAsync(
        string credentialRoute, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialRoute);
        var current = Session.ModelSelection!;
        return ChangeModelAsync(current.Model, _modelClient, current.Context, current.ReasoningEffort,
                                cancellationToken, credentialRoute : credentialRoute,
                                expectedRevision : current.Revision);
    }

    private void EnsureModelOperationsAllowed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _activeRuns) == -1, this);
        if (_sessionWriter is { IsHealthy: false })
            throw new InvalidOperationException("The session journal is unhealthy; reopen it before continuing.");
    }
}

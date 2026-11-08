using System.Text.Json;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Core.Services.Session;

public sealed partial class ChatSessionHost
{
    /// <summary>
    ///     <para>
    ///         组合授权指纹守卫的共用检查点（发送入口与每次工具派发前）：已闩死立即返回提示不再重算；
    ///         否则取当前指纹，取值失败（异常或 null）或与组合期基线不一致时，撤销全部活动授权
    ///         （递增授权修订，使已签发的一次性派发凭据失效）并置会话失效闩。未注入守卫时不检查。
    ///     </para>
    ///     <para>
    ///         The shared guard checkpoint (send entry and before every tool dispatch): a latched session
    ///         returns the notice immediately without recomputation; otherwise the current fingerprint is
    ///         taken, and a failed evaluation (exception or null) or divergence from the composition
    ///         baseline revokes every active grant (bumping the authorization revision so issued
    ///         single-dispatch credentials expire) and latches the session invalid. No check without an
    ///         injected guard.
    ///     </para>
    /// </summary>
    /// <returns>
    ///     <para>阻断提示；null 表示无需阻断。</para>
    ///     <para>The blocking notice, or null when dispatch may proceed.</para>
    /// </returns>
    private string? CheckCompositionGuard()
    {
        if (_compositionGuard is null) return null;
        if (Volatile.Read(ref _compositionInvalidated) != 0) return CompositionInvalidatedNotice;

        string? current;
        try
        {
            current = _compositionGuard.CurrentFingerprint();
        }
        catch (Exception)
        {
            // 取值器读取、解析或校验失败一律按失配处理，绝不放行旧快照。
            current = null;
        }

        if (current is null ||
            !string.Equals(current, _compositionGuard.CompositionFingerprint, StringComparison.Ordinal))
        {
            // 可信授权配置漂移：撤销 Grant 与一次性派发凭据；已启动进程沿用既有取消与回收机制。
            _permissions?.RevokeGrants();
            Volatile.Write(ref _compositionInvalidated, 1);
            return CompositionInvalidatedNotice;
        }

        return null;
    }

    /// <summary>
    ///     <para>空闲时可靠保存审批模式选择（/2），再切换执行策略；不发送模型请求。</para>
    ///     <para>
    ///         Durably saves an idle session's approval-mode selection (/2) and then switches the execution
    ///         policy; no model calls.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         切换撤销全部活动授权（引擎完成），拒绝记忆与中断检查保留；修订延续既有选择链。
    ///         提交成功才更新引擎；提交失败保持原模式并抛出。
    ///         迁移待确认时拒绝切换，包括相同模式；必须通过 ConfirmPermissionMigrationAsync
    ///         原子写入迁移记录与 /2 选择后才能切换。
    ///     </para>
    ///     <para>
    ///         Switching revokes every active grant (in the engine) while denial memory and the inspection block
    ///         persist; the revision continues the selection chain. The engine updates only after a reliable
    ///         commit; a failed commit keeps the original mode and throws.
    ///         Pending migration rejects changes, including the same mode; ConfirmPermissionMigrationAsync must
    ///         atomically write the migration record and /2 selection before mode changes are allowed.
    ///     </para>
    /// </remarks>
    public async Task<SessionApprovalMode> ChangeApprovalModeAsync(
        SessionApprovalMode mode, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        cancellationToken.ThrowIfCancellationRequested();
        EnsureModelOperationsAllowed();
        if (Interlocked.CompareExchange(ref _activeRuns, 2, 0) != 0)
            throw new InvalidOperationException("请先停止当前运行，再切换权限。");
        try
        {
            if (Session.PermissionMigrationPending)
                throw new InvalidOperationException("请先确认该会话的权限迁移，再切换审批模式。");
            if (Session.PermissionSelectionV2?.ApprovalMode == mode) return mode;

            // /1 与 /2 共用一条连续修订链；旧 /1 锚点在过渡层删除前参与取最大值。
            var revision = checked(Math.Max(Session.PermissionSelection?.Revision   ?? 0,
                                            Session.PermissionSelectionV2?.Revision ?? 0) + 1);
            var selection = new SessionPermissionSelectionV2 { Revision = revision, ApprovalMode = mode };
            if (_sessionWriter is not null)
            {
                if (_context.Messages.Count == 0) _context.Append(ChatMessage.System(_systemPrompt));
                var record = new SessionLogRecord(string.Empty, "permission-selection",
                                                  JsonSerializer.SerializeToElement(selection,
                                                      PermissionPersistenceJsonContext.Default
                                                         .SessionPermissionSelectionV2));
                await _sessionWriter.CommitControlAsync(Session, _context, [record], cancellationToken)
                                    .ConfigureAwait(false);
            }

            // 提交成功才镜像到会话状态：后续运行由 ComposeRunLoop 从会话事实重新应用审批模式。
            Session.PermissionSelectionV2 = selection;
            _permissions?.SetApprovalMode(mode);
            await DrainIdleGrantEventsAsync(cancellationToken).ConfigureAwait(false);
            return mode;
        }
        finally
        {
            Interlocked.Exchange(ref _activeRuns, 0);
        }
    }

    /// <summary>
    ///     <para>确认旧模式迁移：原子写入 migration 记录与 /2 审批模式选择，并切换执行策略。</para>
    ///     <para>
    ///         Confirms the legacy-mode migration: atomically writes the migration record and the /2
    ///         approval-mode selection, then switches the execution policy.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         恢复判定为 MigrationPending 的会话在确认前禁止执行模型工具；确认写入可靠完成后
    ///         才解除门禁并更新引擎，失败保持未迁移状态。旧模式名仅作历史展示，无运行时效力。
    ///     </para>
    ///     <para>
    ///         A session whose recovery verdict is MigrationPending cannot execute model tools until confirmed;
    ///         the gate and engine update only after a reliable commit, and a failure leaves
    ///         the session unmigrated. The legacy mode name is for history display only, with no runtime effect.
    ///     </para>
    /// </remarks>
    public async Task<SessionApprovalMode> ConfirmPermissionMigrationAsync(
        SessionApprovalMode mode, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        cancellationToken.ThrowIfCancellationRequested();
        EnsureModelOperationsAllowed();
        if (Interlocked.CompareExchange(ref _activeRuns, 2, 0) != 0)
            throw new InvalidOperationException("请先停止当前运行，再完成权限迁移。");
        try
        {
            // 与模式切换共用空闲锁，避免并发确认写入两条迁移记录。
            if (Session.PermissionMigration is not null)
                throw new InvalidOperationException("该会话已完成权限迁移。");
            if (!Session.PermissionMigrationPending)
                throw new InvalidOperationException("该会话无需确认权限迁移。");

            // /1 与 /2 共用一条连续修订链；迁移记录与其伴随的 /2 选择共享同一修订号。
            var revision = checked(Math.Max(Session.PermissionSelection?.Revision   ?? 0,
                                            Session.PermissionSelectionV2?.Revision ?? 0) + 1);
            var selection = new SessionPermissionSelectionV2 { Revision = revision, ApprovalMode = mode };
            var migration = new PermissionMigrationRecord
            {
                Revision     = revision,
                CompletedUtc = DateTimeOffset.UtcNow,
                PreviousMode = Session.PermissionSelection?.ModeName,
                ApprovalMode = mode
            };
            migration.Validate();
            if (_sessionWriter is not null)
            {
                if (_context.Messages.Count == 0) _context.Append(ChatMessage.System(_systemPrompt));
                var records = new[]
                {
                    new SessionLogRecord(string.Empty, "permission-migration",
                                         JsonSerializer.SerializeToElement(migration,
                                                                           PermissionPersistenceJsonContext.Default
                                                                              .PermissionMigrationRecord)),
                    new SessionLogRecord(string.Empty, "permission-selection",
                                         JsonSerializer.SerializeToElement(selection,
                                                                           PermissionPersistenceJsonContext.Default
                                                                              .SessionPermissionSelectionV2))
                };
                await _sessionWriter.CommitControlAsync(Session, _context, records, cancellationToken)
                                    .ConfigureAwait(false);
            }

            // 提交成功才解除迁移门禁；它不占用非只读阻断槽位，失败保持原状态。
            Session.PermissionMigrationPending = false;
            Session.PermissionSelectionV2      = selection;
            Session.PermissionMigration        = migration;
            _permissions?.SetApprovalMode(mode);
            // 可信授权变更：迁移确认整段解除拒绝记忆，authorization-changed 事件随授权审计落盘。
            if (_permissions?.ReleaseAllDenials() > 0)
                await AppendGrantEventsToLogAsync(
                                                  [
                                                      GrantEventAudit
                                                         .AuthorizationChanged("permission migration confirmed",
                                                                               DateTimeOffset.UtcNow,
                                                                               RedactForGrantEvent)
                                                  ],
                                                  cancellationToken).ConfigureAwait(false);
            await DrainIdleGrantEventsAsync(cancellationToken).ConfigureAwait(false);
            return mode;
        }
        finally
        {
            Interlocked.Exchange(ref _activeRuns, 0);
        }
    }

    /// <summary>
    ///     <para>
    ///         运行终态 drain：把本轮求值、消费与 Turn 到期的授权事件经活动 recorder 落盘。
    ///         无 recorder 或无事件时不执行操作；调用方负责把失败转为运行警告。
    ///     </para>
    ///     <para>
    ///         The run-end drain: this run's evaluation, consumption, and turn-expiry grant events are persisted
    ///         through the active recorder. No-ops without a recorder or events; the caller turns failures into a
    ///         run warning.
    ///     </para>
    /// </summary>
    private async Task DrainRunGrantEventsAsync(CancellationToken cancellationToken)
    {
        var events = _permissions?.Grants.DrainEvents();
        if (events is not { Count: > 0 } || _activeRecorder is null) return;
        await _activeRecorder.RecordGrantEventsAsync(
                                                     events.Select(grantEvent =>
                                                                       GrantEventAudit.FromEvent(grantEvent,
                                                                           static path => path)).ToArray(),
                                                     cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>
    ///         空闲边界 drain：模式切换等宿主操作产生的撤销与到期事件直接写入会话日志；
    ///         无 writer 或无事件时不执行操作。
    ///     </para>
    ///     <para>
    ///         The idle-boundary drain: revocations and expiries from host operations such as mode switches are
    ///         written straight to the session log. No-ops without a writer or events.
    ///     </para>
    /// </summary>
    private async Task DrainIdleGrantEventsAsync(CancellationToken cancellationToken)
    {
        var events = _permissions?.Grants.DrainEvents();
        if (events is not { Count: > 0 }) return;
        await AppendGrantEventsToLogAsync(
                                          events.Select(grantEvent =>
                                                            GrantEventAudit.FromEvent(grantEvent, RedactForGrantEvent))
                                                .ToArray(),
                                          cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把授权事件记录按 permission-grant-event/1 控制记录批量追加；无 writer 时忽略。</summary>
    private async Task AppendGrantEventsToLogAsync(IReadOnlyList<PermissionGrantEventRecord> records,
                                                   CancellationToken                         cancellationToken)
    {
        if (_sessionWriter is null || records.Count == 0) return;
        var journal = records.Select(grantEvent => new SessionLogRecord(string.Empty, "permission-grant-event",
                                                                        JsonSerializer.SerializeToElement(grantEvent,
                                                                            PermissionPersistenceJsonContext
                                                                               .Default
                                                                               .PermissionGrantEventRecord)))
                             .ToArray();
        await _sessionWriter.AppendRecordsAsync(journal, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>会话日志脱敏边界：宿主持有的已知密钥参与授权事件目标与原因脱敏。</summary>
    private string RedactForGrantEvent(string value) => _eventRedactor?.Redact(value) ?? value;
}

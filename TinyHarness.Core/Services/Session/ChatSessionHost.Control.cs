using System.Text.Json;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Core.Services.Session;

public sealed partial class ChatSessionHost
{
    private SessionRunControl? _activeControl;

    /// <summary>
    ///     <para>是否已请求暂停；真正到达暂停边界由 RunControlChanged 事件确认。</para>
    ///     <para>Whether pause is requested; RunControlChanged confirms arrival at a paused boundary.</para>
    /// </summary>
    public bool PauseRequested => Volatile.Read(ref _activeControl)?.IsPaused == true;

    /// <summary>
    ///     <para>请求当前运行在下一模型或工具边界暂停；不打断正在执行的工具。</para>
    ///     <para>Requests a pause at the next model or tool boundary without interrupting an executing tool.</para>
    /// </summary>
    public bool RequestPause() => Volatile.Read(ref _activeControl)?.Pause() == true;

    /// <summary>
    ///     <para>继续当前暂停的运行；保留原审批和隔离检查。</para>
    ///     <para>Resumes the paused run while retaining normal approval and isolation checks.</para>
    /// </summary>
    public bool ResumeRun() => Volatile.Read(ref _activeControl)?.Resume() == true;

    /// <summary>
    ///     <para>取消当前运行并复用既有清理；此方法不表示进程已清理完毕。</para>
    ///     <para>Cancels the active run using existing cleanup; returning does not confirm process cleanup.</para>
    /// </summary>
    public bool CancelRun() => Volatile.Read(ref _activeControl)?.Cancel() == true;

    /// <summary>
    ///     <para>可靠记录运行中的补充原话，再送入有界队列；尚未生效的输入不会在重启时自动发送。</para>
    ///     <para>Durably records an amendment before bounded enqueue; unapplied input is never automatically sent on restart.</para>
    /// </summary>
    public async Task<RunAmendment> QueueAmendmentAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var control = Volatile.Read(ref _activeControl)
                   ?? throw new InvalidOperationException("No active run; submit a new turn.");
        var amendment = new RunAmendment
        {
            Id           = Guid.NewGuid().ToString("N"),
            TurnId       = control.Run.TurnId,
            RunId        = control.Run.Id,
            Text         = text,
            DisplayText  = text
        };
        amendment.Validate();
        await control.EnqueueAsync(amendment, async () =>
        {
            if (_sessionWriter is not null)
            {
                var history = new SessionHistoryItem
                {
                    Id   = string.Empty, SessionId = Session.Id, TurnId = amendment.TurnId, RunId = amendment.RunId,
                    Kind = SessionHistoryKind.User, Text = amendment.DisplayText
                };
                var appended = await _sessionWriter.AppendRecordsAsync([
                    AmendmentRecord(amendment),
                    new SessionLogRecord(string.Empty, "history", JsonSerializer.SerializeToElement(history,
                                             SessionHistoryJsonContext.Default.SessionHistoryItem))
                ], cancellationToken).ConfigureAwait(false);
                control.RecordHistory(
                    appended[^1].Payload.Deserialize(SessionHistoryJsonContext.Default.SessionHistoryItem)!);
            }

            Session.RecordAmendment(amendment);
        }, cancellationToken).ConfigureAwait(false);
        return amendment;
    }

    private SessionRunControl CreateRunControl(SessionRun run, CancellationToken cancellationToken) =>
        new(run, ApplyAmendmentAsync, cancellationToken);

    private static SessionLogRecord AmendmentRecord(RunAmendment amendment) =>
        new(string.Empty, "run-amendment",
            JsonSerializer.SerializeToElement(amendment, SessionLogJsonContext.Default.RunAmendment));

    private async Task ApplyAmendmentAsync(RunAmendment amendment, CancellationToken cancellationToken)
    {
        ChatHistoryInvariants.EnsureCompleteToolGroups(_context.Messages);
        var index   = _context.Messages.Count;
        var applied = amendment with { CanonicalMessageIndex = index };
        _context.Append(ChatMessage.User(amendment.Text));
        try
        {
            if (_sessionWriter is not null)
                await _sessionWriter
                     .CommitControlAsync(Session, _context, [AmendmentRecord(applied)], cancellationToken)
                     .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _context.TruncateToMessageCount(index);
            PersistenceWarning = ex.Message;
            throw new
                InvalidOperationException("The amendment was not reliably applied; no further work was dispatched.",
                                          ex);
        }

        Session.RecordAmendment(applied);
        _permissions?.ClearTemporaryAllows();
    }

    private ConversationContext CompleteContextSnapshot()
    {
        var boundary = ChatHistoryInvariants.LastCompleteBoundary(_context.Messages);
        if (boundary < 0) throw new InvalidDataException("No complete conversation boundary is available.");
        var position = _context.Position;
        var snapshot = new ConversationContext(_context.CheckpointOptions);
        snapshot.Restore(_context.Messages.Take(boundary).ToArray(), _context.State, position.HasState,
                         position.FoldStartMessageIndex, position.FoldEndMessageIndex,
                         _context.CheckpointModelSelectionRevision);
        snapshot.RestoreProjectInstructions(RetainedInstructionRecords(boundary));
        snapshot.BindModel(Session.ModelSelection!);
        return snapshot;
    }
}

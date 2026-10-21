using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Session;

namespace TinyHarness.Core.Services.Agent;

public sealed partial class AgentLoop
{
    private readonly SessionRunControl? _control;

    private async Task WaitForControlAsync(CancellationToken cancellationToken)
    {
        if (_control is null) return;
        await PublishControlHistoryAsync(cancellationToken).ConfigureAwait(false);
        await _control.WaitAsync(state => PublishEventAsync(HostEventKind.RunControlChanged, state, cancellationToken),
                                 cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishControlHistoryAsync(CancellationToken cancellationToken)
    {
        if (_control is null) return;
        foreach (var item in _control.DrainHistory())
            await PublishEventAsync(HostEventKind.HistoryItemCompleted, item, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ApplyAmendmentsAsync(CancellationToken cancellationToken)
    {
        if (_control is null) return false;
        var pending = _control.Drain();
        foreach (var amendment in pending)
        {
            await _control.ApplyAsync(amendment, cancellationToken).ConfigureAwait(false);
            await PublishEventAsync(HostEventKind.RunControlChanged, "amendment-applied", cancellationToken)
               .ConfigureAwait(false);
        }

        return pending.Count > 0;
    }

    private async Task DeferForAmendmentAsync(
        IReadOnlyList<RoundCall> calls, Dictionary<string, ToolCallRecord> records, CancellationToken cancellationToken)
    {
        await RecordDeferredCallsAsync(calls, records,
                                       "Not started because a user amendment invalidated this plan; replan and request approval again.",
                                       cancellationToken).ConfigureAwait(false);
        await ApplyAmendmentsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordDeferredCallsAsync(
        IReadOnlyList<RoundCall> calls, Dictionary<string, ToolCallRecord> records,
        string deferredReason, CancellationToken cancellationToken)
    {
        foreach (var item in calls)
        {
            var record = records[item.Call.Id];
            var denied = record.Approval?.StartsWith("Permission denied", StringComparison.Ordinal)          == true ||
                         record.Approval?.StartsWith("Blocked pending inspection", StringComparison.Ordinal) == true;
            var reason = denied ? record.Approval! : deferredReason;
            _context.Append(ChatMessage.Tool(item.Call.FunctionName, item.Call.Id, reason));
            if (denied) continue;
            record.NotStartedReason = reason;
            if (_recorder is not null)
                await _recorder.RecordNotStartedAsync(record.Snapshot(), cancellationToken).ConfigureAwait(false);
        }
    }
}

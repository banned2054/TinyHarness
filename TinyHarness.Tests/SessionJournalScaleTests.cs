using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;

namespace TinyHarness.Tests;

public class SessionJournalScaleTests
{
    private const int    MessageLength              = 256;
    private const int    AppendCount                = 100;
    private const double AppendP95LimitMs           = 100;
    private const double RecoveryLimitMs            = 5_000;
    private const long   RecoveryAllocationLimit    = 256L * 1024 * 1024;
    private const double HistoryPageLimitMs         = 2_000;
    private const long   HistoryPageAllocationLimit = 64L * 1024 * 1024;

    private static readonly DateTimeOffset FixedTime = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task AppendAndRecovery_MeetsLockedScaleBudgets()
    {
        var warmup     = await RunScaleAsync(1);
        var scale100   = await RunScaleAsync(100);
        var scale1000  = await RunScaleAsync(1_000);
        var scale10000 = await RunScaleAsync(10_000);
        var scales     = new[] { scale100, scale1000, scale10000 };
        var report = new JournalScaleReport(DateTimeOffset.UtcNow,
                                            CreateEnvironment(),
                                            scales,
                                            warmup.MedianAppendAllocatedBytes,
                                            "The 200-item first page is measured at scale 10000; assertions enforce the locked 2s and 64MiB budgets.");
        await WriteReportAsync(report);

        Assert.That(scales.All(scale => scale.AppendP95Milliseconds <= AppendP95LimitMs), Is.True,
                    "append p95 exceeded 100 ms");
        Assert.That(scale10000.MedianAppendAllocatedBytes,
                    Is.LessThanOrEqualTo(scale100.MedianAppendAllocatedBytes * 4),
                    "10000-message allocation exceeded 4x the 100-message allocation");
        Assert.That(scales.Max(scale =>
            scale.Appends.Max(item => item.FileBytes) - scales.Min(scale => scale.Appends.Min(item => item.FileBytes))),
                    Is.LessThanOrEqualTo(4),
                    "append bytes depended on history size beyond sequence digits");
        Assert.That(scale10000.RecoveryMilliseconds, Is.LessThanOrEqualTo(RecoveryLimitMs),
                    "10000-message recovery exceeded 5 seconds");
        Assert.That(scale10000.RecoveryAllocatedBytes, Is.LessThanOrEqualTo(RecoveryAllocationLimit),
                    "10000-message recovery allocation exceeded 256 MiB");
        var history = scale10000.HistoryPage;
        Assert.That(history, Is.Not.Null, "10000-scale history page was not measured");
        Assert.That(history!.ItemCount, Is.EqualTo(200), "history first page did not return 200 items");
        Assert.That(history.ValidItems, Is.True, "history first page items did not match the locked workload");
        Assert.That(history.PageMilliseconds, Is.LessThanOrEqualTo(HistoryPageLimitMs),
                    "history first page exceeded 2 seconds");
        Assert.That(history.PageAllocatedBytes, Is.LessThanOrEqualTo(HistoryPageAllocationLimit),
                    "history first page allocation exceeded 64 MiB");
    }

    private static async Task<JournalScaleResult> RunScaleAsync(int scale, bool measureAppends = true)
    {
        using var dir = new TestTempDir();
        var store = Path.Combine(dir.Root, "sessions");
        var sessionId = Guid.NewGuid().ToString("N");
        var model = new SessionModelIdentity("scale-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions);
        var session = new ChatSession(sessionId, dir.Root, model, FixedTime);
        var context = new ConversationContext();
        var systemPrompt = Message(0);
        var measurements = new List<JournalMeasurement>();

        using (var writer = new SessionStoreWriter(store, sessionId))
        {
            context.Append(ChatMessage.System(systemPrompt));
            await writer.CommitAsync(session, context, CancellationToken.None);

            for (var i = 1; i < scale; i++)
                await AppendTurnAsync(session, context, writer, i);

            for (var i = 0; i < AppendCount; i++)
            {
                var index           = scale + i;
                var beforeBytes     = new FileInfo(writer.SnapshotPath).Length;
                var beforeAllocated = GC.GetTotalAllocatedBytes(true);
                var started         = Stopwatch.GetTimestamp();
                await AppendTurnAsync(session, context, writer, index);
                var elapsed   = Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds;
                var allocated = GC.GetTotalAllocatedBytes(true)          - beforeAllocated;
                var fileBytes = new FileInfo(writer.SnapshotPath).Length - beforeBytes;
                if (measureAppends)
                    measurements.Add(new JournalMeasurement(index, elapsed, allocated, fileBytes));
            }

            var checkpointMessages = context.Messages.ToArray();
            context.Restore(checkpointMessages, new CompactionState { Summary = "scale checkpoint" },
                            true, 1, 2);
            var checkpointBeforeBytes     = new FileInfo(writer.SnapshotPath).Length;
            var checkpointBeforeAllocated = GC.GetTotalAllocatedBytes(true);
            var checkpointStarted         = Stopwatch.GetTimestamp();
            await writer.CommitAsync(session, context, CancellationToken.None);
            var checkpoint = new JournalMeasurement(writer.CommitSequence,
                                                    Stopwatch.GetElapsedTime(checkpointStarted,
                                                                             Stopwatch.GetTimestamp())
                                                             .TotalMilliseconds,
                                                    GC.GetTotalAllocatedBytes(true) - checkpointBeforeAllocated,
                                                    new FileInfo(writer.SnapshotPath).Length - checkpointBeforeBytes);
            var finalBytes = new FileInfo(writer.SnapshotPath).Length;

            JournalHistoryPageResult? historyPage = null;
            if (scale == 10_000)
            {
                var turnId  = session.Turns[^1].Id;
                var runId   = session.Turns[^1].Runs[^1].Id;
                var records = new List<SessionLogRecord>(10_000);
                for (var i = 0; i < 10_000; i++)
                {
                    var item = new SessionHistoryItem
                    {
                        Id        = string.Empty,
                        SessionId = sessionId,
                        TurnId    = turnId,
                        RunId     = runId,
                        Kind      = SessionHistoryKind.Process,
                        Text      = Message(i)
                    };
                    records.Add(new SessionLogRecord(string.Empty, "history",
                                                     JsonSerializer.SerializeToElement(
                                                              item,
                                                              SessionHistoryJsonContext.Default.SessionHistoryItem)));
                }

                var batches = new List<IReadOnlyList<SessionLogRecord>>();
                for (var offset = 0; offset < records.Count; offset += 4_000)
                    batches.Add(records.Skip(offset).Take(4_000).ToArray());

                var appendBatchCount      = 0;
                var appendedItemCount     = 0;
                var beforeAppendBytes     = new FileInfo(writer.SnapshotPath).Length;
                var beforeAppendAllocated = GC.GetTotalAllocatedBytes(true);
                var appendStarted         = Stopwatch.GetTimestamp();
                foreach (var batch in batches)
                {
                    var normalized = await writer.AppendRecordsAsync(batch, CancellationToken.None);
                    appendBatchCount++;
                    appendedItemCount += normalized.Count;
                }

                var historyAppendMilliseconds =
                    Stopwatch.GetElapsedTime(appendStarted, Stopwatch.GetTimestamp()).TotalMilliseconds;
                var historyAppendAllocated = GC.GetTotalAllocatedBytes(true)          - beforeAppendAllocated;
                var historyAppendFileBytes = new FileInfo(writer.SnapshotPath).Length - beforeAppendBytes;

                var beforePageAllocated = GC.GetTotalAllocatedBytes(true);
                var pageStarted         = Stopwatch.GetTimestamp();
                var page = await new SessionHistoryReader().ReadPageAsync(store, sessionId, 200, null,
                                                                          CancellationToken.None);
                var historyPageMilliseconds =
                    Stopwatch.GetElapsedTime(pageStarted, Stopwatch.GetTimestamp()).TotalMilliseconds;
                var historyPageAllocated = GC.GetTotalAllocatedBytes(true) - beforePageAllocated;
                historyPage = new JournalHistoryPageResult(historyAppendMilliseconds,
                                                           historyAppendAllocated,
                                                           historyAppendFileBytes,
                                                           appendBatchCount,
                                                           appendedItemCount,
                                                           page.Items.Count,
                                                           historyPageMilliseconds,
                                                           historyPageAllocated,
                                                           page.BytesRead,
                                                           page.UpperSequence,
                                                           page.NextCursor is not null,
                                                           page.Warning,
                                                           page.Items.All(item =>
                    item.Kind ==
                    SessionHistoryKind.Process &&
                    item.Text?.Length == MessageLength &&
                    item.Id.Length    > 0 &&
                    item.Sequence     > 0 &&
                    item.TurnId       == turnId &&
                    item.RunId        == runId));
            }

            var beforeRecoveryAllocated = GC.GetTotalAllocatedBytes(true);
            var recoveryStarted         = Stopwatch.GetTimestamp();
            var snapshot                = await SessionStoreReader.ReadAsync(store, sessionId, CancellationToken.None);
            var restoration             = SessionResume.Restore(snapshot!, dir.Root, model, systemPrompt);
            var recoveryMilliseconds =
                Stopwatch.GetElapsedTime(recoveryStarted, Stopwatch.GetTimestamp()).TotalMilliseconds;
            var recoveryAllocated = GC.GetTotalAllocatedBytes(true) - beforeRecoveryAllocated;

            return new JournalScaleResult(scale,
                                          sessionId,
                                          finalBytes,
                                          measurements,
                                          Percentile95(measurements),
                                          Median(measurements.Select(item => item.AllocatedBytes)),
                                          checkpoint,
                                          recoveryMilliseconds,
                                          recoveryAllocated,
                                          restoration.Context.Messages.Count,
                                          restoration.Session.Turns.Count,
                                          restoration.Session.Turns.Sum(turn => turn.Runs.Count),
                                          historyPage);
        }
    }

    private static async Task AppendTurnAsync(
        ChatSession session, ConversationContext context, SessionStoreWriter writer, int index)
    {
        var turn = new SessionTurn($"turn-{index:D10}", Message(index), FixedTime);
        var run  = new SessionRun($"run-{index:D10}", session.Id, turn.Id, FixedTime);
        session.AddTurn(turn);
        turn.AddRun(run);
        run.Finish(new AgentResult { Status = AgentStatus.Completed }, FixedTime);
        context.Append(ChatMessage.User(Message(index)));
        await writer.CommitAsync(session, context, CancellationToken.None);
    }

    private static string Message(int index) => new('x', MessageLength);

    private static double Percentile95(IReadOnlyList<JournalMeasurement> values)
    {
        var ordered = values.Select(item => item.Milliseconds).Order().ToArray();
        return ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1];
    }

    private static long Median(IEnumerable<long> values)
    {
        var ordered = values.Order().ToArray();
        return ordered[ordered.Length / 2];
    }

    private static JournalEnvironment CreateEnvironment() => new(Environment.OSVersion.VersionString,
                                                                 RuntimeInformation.ProcessArchitecture.ToString(),
                                                                 "10.0.401",
                                                                 RuntimeInformation.FrameworkDescription,
                                                                 Environment
                                                                    .GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ??
                                                                 "unknown",
                                                                 Environment.ProcessorCount,
                                                                 null,
                                                                 null,
                                                                 "AllocatedBytes covers the whole process, including other threads.");

    private static async Task WriteReportAsync(JournalScaleReport report)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "TinyHarness.slnx")))
            current = current.Parent;
        var root = current?.FullName ?? Directory.GetCurrentDirectory();
        var path = Path.Combine(root, "artifacts", "session-journal-scale-results.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, report, SessionJournalScaleJsonContext.Default.JournalScaleReport);
    }
}

internal sealed record JournalMeasurement(long MessageIndex, double Milliseconds, long AllocatedBytes, long FileBytes);

internal sealed record JournalScaleResult(
    int                               Scale,
    string                            SessionId,
    long                              FinalLogBytes,
    IReadOnlyList<JournalMeasurement> Appends,
    double                            AppendP95Milliseconds,
    long                              MedianAppendAllocatedBytes,
    JournalMeasurement                Checkpoint,
    double                            RecoveryMilliseconds,
    long                              RecoveryAllocatedBytes,
    int                               RecoveredMessageCount,
    int                               RecoveredTurnCount,
    int                               RecoveredRunCount,
    JournalHistoryPageResult?         HistoryPage);

internal sealed record JournalHistoryPageResult(
    double  AppendMilliseconds,
    long    AppendAllocatedBytes,
    long    AppendFileBytes,
    int     AppendBatchCount,
    int     AppendedItemCount,
    int     ItemCount,
    double  PageMilliseconds,
    long    PageAllocatedBytes,
    long    BytesRead,
    long    UpperSequence,
    bool    HasNextCursor,
    string? Warning,
    bool    ValidItems);

internal sealed record JournalEnvironment(
    string  Os,
    string  Architecture,
    string  ProvidedSdkBaseline,
    string  RuntimeDescription,
    string  CpuIdentifier,
    int     LogicalProcessorCount,
    string? CpuModelName,
    long?   RamBytes,
    string  AllocationScope);

internal sealed record JournalScaleReport(
    DateTimeOffset                    CreatedUtc,
    JournalEnvironment                Environment,
    IReadOnlyList<JournalScaleResult> Scales,
    long                              WarmupAppendAllocatedBytes,
    string                            HistoryPageStatus);

[JsonSerializable(typeof(JournalScaleReport))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class SessionJournalScaleJsonContext : JsonSerializerContext;

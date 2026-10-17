using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Tests;

/// <summary>
///     <para>验证新日志历史扫描、固定上界分页、资源预算与损坏诊断。</para>
///     <para>Verifies new-log history scanning, fixed-ceiling paging, resource budgets, and corruption diagnostics.</para>
/// </summary>
public sealed class SessionHistoryPagingTests
{
    /// <summary>
    ///     <para>验证千条历史跨页不重不漏，读取期间追加不改变原分页上界。</para>
    ///     <para>
    ///         Verifies that a thousand history items page without gaps or duplicates and that appends do not change the
    ///         original ceiling.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ReadPage_PreservesFixedUpperBoundAcrossAppendAndSameBatchBoundaries()
    {
        using var dir       = new TestTempDir();
        var       sessionId = Guid.NewGuid().ToString("N");
        using var writer    = await CreateWriterAsync(dir.Root, sessionId);
        await AppendHistoryAsync(writer, sessionId, Enumerable.Range(0, 1000).Select(i => $"item-{i:D4}").ToArray());

        var reader = new SessionHistoryReader();
        var first  = await reader.ReadPageAsync(dir.Root, sessionId, 100, null, CancellationToken.None);
        Assert.That(first.Items, Has.Count.EqualTo(100));
        Assert.That(first.UpperSequence, Is.EqualTo(2));
        TestAssert.NotNull(first.NextCursor);

        await AppendHistoryAsync(writer, sessionId, ["appended-after-first-page"]);
        var allOriginal = first.Items.ToList();
        var cursor      = first.NextCursor;
        while (cursor is not null)
        {
            var page = await reader.ReadPageAsync(dir.Root, sessionId, 100, cursor, CancellationToken.None);
            Assert.That(page.UpperSequence, Is.EqualTo(2));
            allOriginal.AddRange(page.Items);
            cursor = page.NextCursor;
        }

        Assert.That(allOriginal, Has.Count.EqualTo(1000));
        Assert.That(allOriginal.Select(item => item.Text),
                    Is.EqualTo(Enumerable.Range(0, 1000).Select(i => $"item-{i:D4}")));
        Assert.That(allOriginal.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(1000));
        Assert.That(allOriginal.Any(item => item.Text == "appended-after-first-page"), Is.False);
    }

    /// <summary>
    ///     <para>验证跨会话游标和非法页大小均被拒绝。</para>
    ///     <para>Verifies that cross-session cursors and invalid page limits are rejected.</para>
    /// </summary>
    [Test]
    public async Task ReadPage_RejectsCursorForAnotherSessionAndLimitOutsideRange()
    {
        using var dir          = new TestTempDir();
        var       firstId      = Guid.NewGuid().ToString("N");
        var       secondId     = Guid.NewGuid().ToString("N");
        using var firstWriter  = await CreateWriterAsync(dir.Root, firstId);
        using var secondWriter = await CreateWriterAsync(dir.Root, secondId);
        await AppendHistoryAsync(firstWriter, firstId, ["first", "second"]);
        await AppendHistoryAsync(secondWriter, secondId, ["other-first", "other-second"]);
        var reader = new SessionHistoryReader();
        var page   = await reader.ReadPageAsync(dir.Root, firstId, 1, null, CancellationToken.None);

        var crossSession =
            await TestAssert.ThrowsAsync<SessionStoreException>(() => reader.ReadPageAsync(dir.Root, secondId, 1,
                                                                    page.NextCursor!, CancellationToken.None));
        TestAssert.Contains("different session", crossSession.Message, StringComparison.Ordinal);
        var forgedCursor = page.NextCursor! with { LastRecordIndex = int.MaxValue };
        var invalidPosition = await TestAssert.ThrowsAsync<SessionStoreException>(() =>
            reader.ReadPageAsync(dir.Root, firstId, 1, forgedCursor, CancellationToken.None));
        TestAssert.Contains("position", invalidPosition.Message, StringComparison.Ordinal);

        await TestAssert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadPageAsync(dir.Root, firstId, 201,
                                                                      null, CancellationToken.None));
        await TestAssert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadPageAsync(dir.Root, firstId, 0, null,
                                                                      CancellationToken.None));
    }

    /// <summary>
    ///     <para>验证尾部半行只报告可靠前缀，严格 scanner 默认拒绝且不截断文件。</para>
    ///     <para>
    ///         Verifies that a trailing partial line reports only the reliable prefix, strict scanning rejects it, and the
    ///         file is not truncated.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ReadPage_ReportsIncompleteTailAndStrictScannerRejectsIt()
    {
        using var dir       = new TestTempDir();
        var       sessionId = Guid.NewGuid().ToString("N");
        var       writer    = await CreateWriterAsync(dir.Root, sessionId);
        await AppendHistoryAsync(writer, sessionId, ["complete"]);
        await AppendHistoryAsync(writer, sessionId, ["partial"]);
        var path = writer.SnapshotPath;
        writer.Dispose();

        var original = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, original[..^1]);
        var truncatedLength = new FileInfo(path).Length;
        var reader =
            await new SessionHistoryReader().ReadPageAsync(dir.Root, sessionId, 20, null, CancellationToken.None);

        Assert.That(reader.Items.Select(item => item.Text), Is.EqualTo(["complete"]));
        Assert.That(reader.UpperSequence, Is.EqualTo(2));
        Assert.That(reader.ReliablePrefixSequence, Is.EqualTo(2));
        TestAssert.NotNull(reader.Warning);
        Assert.That(new FileInfo(path).Length, Is.EqualTo(truncatedLength));

        var strictError =
            await TestAssert.ThrowsAsync<SessionStoreException>(() => SessionLogScanner.ScanAsync(path, sessionId, null,
                                                                    null, false,
                                                                    static (_, _) => ValueTask.CompletedTask,
                                                                    CancellationToken.None));
        TestAssert.Contains("incomplete line", strictError.Message, StringComparison.Ordinal);
        Assert.That(new FileInfo(path).Length, Is.EqualTo(truncatedLength));
    }

    /// <summary>
    ///     <para>验证仅有尾部半行且没有完整批次时明确拒绝读取。</para>
    ///     <para>Verifies that a trailing partial line without any complete batch is explicitly rejected.</para>
    /// </summary>
    [Test]
    public async Task ReadPage_RejectsLogWithoutCommittedBatch()
    {
        using var dir       = new TestTempDir();
        var       sessionId = Guid.NewGuid().ToString("N");
        var       path      = Path.Combine(dir.Root, sessionId + SessionStoreWriter.LogFileSuffix);
        await File.WriteAllTextAsync(path, "{\"format\":\"partial");

        var error =
            await TestAssert.ThrowsAsync<SessionStoreException>(() =>
                                                                    new SessionHistoryReader().ReadPageAsync(dir.Root,
                                                                        sessionId, 10, null,
                                                                        CancellationToken.None));
        TestAssert.Contains("no committed history batch", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <para>验证重复批次、序号断裂和未知必需记录均使历史读取失败。</para>
    ///     <para>Verifies that duplicate batches, sequence gaps, and unknown required records fail history reads.</para>
    /// </summary>
    [Test]
    public async Task ReadPage_RejectsDuplicateSequenceGapAndUnknownKind()
    {
        using var duplicateDir = new TestTempDir();
        var       duplicateId  = Guid.NewGuid().ToString("N");
        string    duplicatePath;
        using (var writer = await CreateWriterAsync(duplicateDir.Root, duplicateId))
        {
            await AppendHistoryAsync(writer, duplicateId, ["one", "two"]);
            await AppendHistoryAsync(writer, duplicateId, ["three"]);
            duplicatePath = writer.SnapshotPath;
        }

        var duplicateLines = await File.ReadAllLinesAsync(duplicatePath);
        duplicateLines[2] = duplicateLines[1];
        await File.WriteAllLinesAsync(duplicatePath, duplicateLines);
        var duplicateError = await TestAssert.ThrowsAsync<SessionStoreException>(() =>
            new SessionHistoryReader().ReadPageAsync(duplicateDir.Root, duplicateId, 10, null,
                                                     CancellationToken.None));
        TestAssert.Contains("out-of-order", duplicateError.Message, StringComparison.Ordinal);

        using var gapDir = new TestTempDir();
        var       gapId  = Guid.NewGuid().ToString("N");
        string    gapPath;
        using (var writer = await CreateWriterAsync(gapDir.Root, gapId))
        {
            await AppendHistoryAsync(writer, gapId, ["one"]);
            await AppendHistoryAsync(writer, gapId, ["two"]);
            gapPath = writer.SnapshotPath;
        }

        var gapLines = await File.ReadAllLinesAsync(gapPath);
        var second   = JsonNode.Parse(gapLines[2])!.AsObject();
        second["sequence"] = 4;
        second["batchId"]  = $"{gapId}:00000000000000000004";
        gapLines[2]        = second.ToJsonString();
        await File.WriteAllLinesAsync(gapPath, gapLines);
        var gapError =
            await TestAssert.ThrowsAsync<SessionStoreException>(() =>
                                                                    new SessionHistoryReader()
                                                                       .ReadPageAsync(gapDir.Root, gapId, 10, null,
                                                                            CancellationToken.None));
        TestAssert.Contains("out-of-order", gapError.Message, StringComparison.Ordinal);

        using var unknownDir = new TestTempDir();
        var       unknownId  = Guid.NewGuid().ToString("N");
        string    unknownPath;
        using (var writer = await CreateWriterAsync(unknownDir.Root, unknownId))
        {
            await AppendHistoryAsync(writer, unknownId, ["one"]);
            unknownPath = writer.SnapshotPath;
        }

        var unknownLines = await File.ReadAllLinesAsync(unknownPath);
        var unknownBatch = JsonNode.Parse(unknownLines[1])!.AsObject();
        unknownBatch["records"]![0]!["kind"] = "future-required";
        unknownLines[1]                      = unknownBatch.ToJsonString();
        await File.WriteAllLinesAsync(unknownPath, unknownLines);
        var unknownError =
            await TestAssert.ThrowsAsync<SessionStoreException>(() =>
                                                                    new SessionHistoryReader()
                                                                       .ReadPageAsync(unknownDir.Root, unknownId, 10,
                                                                            null, CancellationToken.None));
        TestAssert.Contains("unknown required record kind", unknownError.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <para>验证页内序列化字节预算和条目数上限分别停止分页并返回游标。</para>
    ///     <para>Verifies that serialized page-byte and item-count budgets stop paging with a continuation cursor.</para>
    /// </summary>
    [Test]
    public async Task ReadPage_RespectsSerializedUtf8BudgetAndItemLimitWithoutModelOrToolCalls()
    {
        using var dir       = new TestTempDir();
        var       sessionId = Guid.NewGuid().ToString("N");
        using var writer    = await CreateWriterAsync(dir.Root, sessionId);
        await AppendHistoryAsync(writer, sessionId, [new string('a', 5 * 1024 * 1024)]);
        await AppendHistoryAsync(writer, sessionId, [new string('b', 5 * 1024 * 1024)]);

        var reader = new SessionHistoryReader();
        var first  = await reader.ReadPageAsync(dir.Root, sessionId, 20, null, CancellationToken.None);
        Assert.That(first.Items, Has.Count.EqualTo(1));
        Assert.That(JsonSerializer
                   .SerializeToUtf8Bytes(first.Items[0], SessionHistoryJsonContext.Default.SessionHistoryItem).Length,
                    Is.LessThanOrEqualTo(SessionHistoryReader.MaxPageBytes));
        TestAssert.NotNull(first.NextCursor);
        TestAssert.NotNull(first.Warning);
        var second = await reader.ReadPageAsync(dir.Root, sessionId, 20, first.NextCursor, CancellationToken.None);
        Assert.That(second.Items, Has.Count.EqualTo(1));
        Assert.That(second.Items[0].Text, Does.StartWith("b"));
        Assert.That(second.NextCursor, Is.Null);

        var       oversizedSessionId = Guid.NewGuid().ToString("N");
        using var oversizedWriter    = await CreateWriterAsync(dir.Root, oversizedSessionId);
        var       before             = new FileInfo(oversizedWriter.SnapshotPath).Length;
        var batchError =
            await TestAssert.ThrowsAsync<SessionStoreException>(() => AppendHistoryAsync(oversizedWriter,
                                                                    oversizedSessionId,
                                                                    [new string('x', 9 * 1024 * 1024)]));
        TestAssert.Contains("exceeds", batchError.Message, StringComparison.Ordinal);
        Assert.That(oversizedWriter.CommitSequence, Is.EqualTo(1));
        Assert.That(new FileInfo(oversizedWriter.SnapshotPath).Length, Is.EqualTo(before));

        var       hundred        = Enumerable.Range(0, 100).Select(i => $"small-{i}").ToArray();
        var       countSessionId = Guid.NewGuid().ToString("N");
        using var countWriter    = await CreateWriterAsync(dir.Root, countSessionId);
        await AppendHistoryAsync(countWriter, countSessionId, hundred);
        var countPage = await reader.ReadPageAsync(dir.Root, countSessionId, 10, null, CancellationToken.None);
        Assert.That(countPage.Items, Has.Count.EqualTo(10));
        TestAssert.NotNull(countPage.NextCursor);
        Assert.That(countPage.Items.All(item => item.Text?.StartsWith("small-", StringComparison.Ordinal) == true),
                    Is.True);
    }

    /// <summary>
    ///     <para>验证跨批次触发字节预算后封页，后续小条目不会越过未返回的大条目。</para>
    ///     <para>
    ///         Verifies that reaching the byte budget seals the page across batches, so later small items cannot skip a
    ///         deferred large item.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ReadPage_SealsByteBudgetAcrossBatchesWithoutSkippingHistory()
    {
        using var dir       = new TestTempDir();
        var       sessionId = Guid.NewGuid().ToString("N");
        using var writer    = await CreateWriterAsync(dir.Root, sessionId);
        var texts = new[]
        {
            new string('a', 5 * 1024 * 1024),
            new string('b', 5 * 1024 * 1024),
            "small-after-b",
            new string('d', 4 * 1024 * 1024),
            "small-after-d"
        };
        foreach (var text in texts)
        {
            var before = new FileInfo(writer.SnapshotPath).Length;
            await AppendHistoryAsync(writer, sessionId, [text]);
            Assert.That(new FileInfo(writer.SnapshotPath).Length - before,
                        Is.LessThan(SessionStoreWriter.MaxSnapshotBytes));
        }

        var reader = new SessionHistoryReader();
        var first  = await reader.ReadPageAsync(dir.Root, sessionId, 20, null, CancellationToken.None);
        Assert.That(first.Items, Has.Count.EqualTo(1));
        TestAssert.NotNull(first.Warning);
        TestAssert.NotNull(first.NextCursor);
        Assert.That(first.NextCursor!.LastSequence, Is.EqualTo(first.Items[0].Sequence));

        var collected  = first.Items.ToList();
        var pageCounts = new List<int> { first.Items.Count };
        var cursor     = first.NextCursor;
        while (cursor is not null)
        {
            Assert.That(pageCounts.Count, Is.LessThan(texts.Length));
            var page = await reader.ReadPageAsync(dir.Root, sessionId, 20, cursor, CancellationToken.None);
            Assert.That(page.Items, Is.Not.Empty);
            Assert.That(page.UpperSequence, Is.EqualTo(first.UpperSequence));
            Assert.That(page.Items.Sum(item => JsonSerializer
                                              .SerializeToUtf8Bytes(item,
                                                                    SessionHistoryJsonContext.Default
                                                                       .SessionHistoryItem).Length),
                        Is.LessThanOrEqualTo(SessionHistoryReader.MaxPageBytes));
            collected.AddRange(page.Items);
            pageCounts.Add(page.Items.Count);
            cursor = page.NextCursor;
        }

        Assert.That(pageCounts, Is.EqualTo([1, 2, 2]));
        Assert.That(collected.Select(item => item.Text), Is.EqualTo(texts));
        Assert.That(collected.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count(),
                    Is.EqualTo(texts.Length));
    }

    /// <summary>
    ///     <para>创建仅用于确定性日志测试的真实追加 writer 并提交身份批次。</para>
    ///     <para>Creates a real append writer for deterministic log tests and commits its identity batch.</para>
    /// </summary>
    /// <param name="directory">
    ///     <para>临时日志目录。</para>
    ///     <para>The temporary log directory.</para>
    /// </param>
    /// <param name="sessionId">
    ///     <para>会话 ID。</para>
    ///     <para>The session ID.</para>
    /// </param>
    /// <returns>
    ///     <para>已写入身份批次的 writer。</para>
    ///     <para>The writer with its identity batch committed.</para>
    /// </returns>
    private static async Task<SessionStoreWriter> CreateWriterAsync(string directory, string sessionId)
    {
        var session = new ChatSession(sessionId, directory,
                                      new SessionModelIdentity("test-model", "https://example.invalid/v1",
                                                               ChatApiKind.ChatCompletions));
        var writer = new SessionStoreWriter(directory, sessionId);
        await writer.CommitAsync(session, new ConversationContext(), CancellationToken.None);
        return writer;
    }

    /// <summary>
    ///     <para>通过生产 writer 追加指定文本的历史条目。</para>
    ///     <para>Appends history items with the supplied text through the production writer.</para>
    /// </summary>
    /// <param name="writer">
    ///     <para>目标 writer。</para>
    ///     <para>The target writer.</para>
    /// </param>
    /// <param name="sessionId">
    ///     <para>会话 ID。</para>
    ///     <para>The session ID.</para>
    /// </param>
    /// <param name="texts">
    ///     <para>历史正文。</para>
    ///     <para>The history texts.</para>
    /// </param>
    /// <returns>
    ///     <para>完成追加后返回。</para>
    ///     <para>Completes after the append.</para>
    /// </returns>
    private static async Task AppendHistoryAsync(SessionStoreWriter    writer, string sessionId,
                                                 IReadOnlyList<string> texts)
    {
        var records = texts.Select(text => new SessionLogRecord("", "history",
                                                                JsonSerializer.SerializeToElement(new SessionHistoryItem
                                                                    {
                                                                        Id        = "unbound",
                                                                        SessionId = sessionId,
                                                                        TurnId    = "test-turn",
                                                                        Kind      = SessionHistoryKind.User,
                                                                        Text      = text
                                                                    },
                                                                    SessionHistoryJsonContext.Default
                                                                       .SessionHistoryItem)))
                           .ToArray();
        await writer.AppendRecordsAsync(records, CancellationToken.None);
    }
}

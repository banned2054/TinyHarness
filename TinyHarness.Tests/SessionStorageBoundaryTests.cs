using System.Text.Json;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         会话列表摘要的冻结边界：标题、活动时间与回合数分别来自哪些 JSONL 事实。纯标题批次只改标题与提交时间，
///         不推进对话活动时间、不增加回合数；待重试标记不会清掉已保存名称；无标题事件时按首回合首行、
///         尚无回合时按会话 ID 前缀回退。
///     </para>
///     <para>
///         Frozen list-summary boundaries: which JSONL facts title, activity time, and turn count come from. A
///         title-only batch changes the title and the commit time only, never advancing conversation activity or the
///         turn count; a pending marker never clears a saved name; without a title event the fallback is the first
///         turn's first line, or the session-id prefix while no turn exists.
///     </para>
/// </summary>
public class SessionStorageBoundaryTests
{
    private static readonly string SessionId = new('a', 32);

    /// <summary>构造只带序号属性的批次：摘要投影只消费 <c>WrittenUtc</c> 与记录列表。</summary>
    private static SessionLogBatch Batch(DateTimeOffset writtenUtc, params SessionLogRecord[] records) => new()
    {
        SessionId = SessionId, WrittenUtc = writtenUtc, Records = records
    };

    private static SessionLogRecord IdentityRecord(DateTimeOffset createdUtc) => new(
        string.Empty, "identity",
        JsonSerializer.SerializeToElement(
            new SessionLogIdentity(SessionId, @"c:\work\alpha",
                                   new SessionModelIdentity("offline", "https://offline.invalid/v1",
                                                            ChatApiKind.ChatCompletions), createdUtc),
            SessionLogJsonContext.Default.SessionLogIdentity));

    private static SessionLogRecord TurnRecord(string turnId, string userInput, DateTimeOffset createdUtc) => new(
        string.Empty, "turn",
        JsonSerializer.SerializeToElement(new SessionLogTurn(turnId, userInput, createdUtc),
                                          SessionLogJsonContext.Default.SessionLogTurn));

    private static SessionLogRecord TitleRecord(string? name, bool pending) => new(
        string.Empty, "session-title",
        JsonSerializer.SerializeToElement(new SessionTitle { Name = name, Pending = pending },
                                          SessionLogJsonContext.Default.SessionTitle));

    [Test]
    public void TitleOnlyBatch_ChangesTitleAndCommitTimeButNotActivityOrTurnCount()
    {
        var created  = DateTimeOffset.FromUnixTimeMilliseconds(1_000_000);
        var activity = created.AddMinutes(1);
        var titled   = created.AddMinutes(2);

        var projection = new SessionSummaryBuilder.Projection(SessionId);
        projection.Apply(Batch(created, IdentityRecord(created)));
        projection.Apply(Batch(activity, TurnRecord("turn-1", "第一条输入", activity)));
        projection.Apply(Batch(titled, TitleRecord("生成的名称", false)));

        var summary = projection.ToSummary(3, 300);
        Assert.Multiple(() =>
        {
            Assert.That(summary.Title, Is.EqualTo("生成的名称"));
            Assert.That(summary.TurnCount, Is.EqualTo(1), "标题事件不是回合。");
            Assert.That(summary.LastActivityUtc, Is.EqualTo(activity), "标题更新不得推进对话活动时间。");
            Assert.That(summary.UpdatedUtc, Is.EqualTo(titled), "提交时间仍随每个已提交批次推进。");
        });
    }

    [Test]
    public void PendingTitleRecordAfterSavedName_KeepsTheSavedName()
    {
        var created  = DateTimeOffset.FromUnixTimeMilliseconds(1_000_000);
        var activity = created.AddMinutes(1);

        var projection = new SessionSummaryBuilder.Projection(SessionId);
        projection.Apply(Batch(created, IdentityRecord(created)));
        projection.Apply(Batch(activity, TurnRecord("turn-1", "第一条输入", activity)));
        projection.Apply(Batch(activity.AddMinutes(1), TitleRecord("生成的名称", false)));
        // 重试标记（name 为 null）在摘要投影里不覆盖已保存名称；完整恢复则以最后一条记录为准。
        projection.Apply(Batch(activity.AddMinutes(2), TitleRecord(null, true)));

        var summary = projection.ToSummary(4, 400);
        Assert.That(summary.Title, Is.EqualTo("生成的名称"));
        Assert.That(summary.LastActivityUtc, Is.EqualTo(activity));
        Assert.That(summary.TurnCount, Is.EqualTo(1));
    }

    [Test]
    public void WithoutTitleEvent_FallsBackToFirstTurnLineThenToSessionIdPrefix()
    {
        var created = DateTimeOffset.FromUnixTimeMilliseconds(1_000_000);

        var beforeTurn = new SessionSummaryBuilder.Projection(SessionId);
        beforeTurn.Apply(Batch(created, IdentityRecord(created)));
        Assert.That(beforeTurn.ToSummary(1, 100).Title, Is.EqualTo(SessionId[..8]));

        var afterTurn = new SessionSummaryBuilder.Projection(SessionId);
        afterTurn.Apply(Batch(created, IdentityRecord(created)));
        afterTurn.Apply(Batch(created.AddMinutes(1),
                              TurnRecord("turn-1", "第一条输入\n其他内容", created.AddMinutes(1))));
        var summary = afterTurn.ToSummary(2, 200);
        Assert.That(summary.Title, Is.EqualTo("第一条输入"));
        Assert.That(summary.TurnCount, Is.EqualTo(1));
    }
}

using NUnit.Framework;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Services.Permissions;

namespace TinyHarness.Desktop.Tests;

/// <summary>
///     <para>
///         直测 <see cref="ApprovalContextBuilder" /> 的窗口规则：条数与字符预算、自最新向前保留、
///         空窗口保留首条、仅在确有丢弃时附加 SystemNotice 窗口说明。
///     </para>
///     <para>
///         Directly tests the window rules of <see cref="ApprovalContextBuilder" />: entry and character budgets,
///         newest-first retention, unconditional first candidate on an empty window, and the SystemNotice window
///         notice only when something was actually dropped.
///     </para>
/// </summary>
public class DesktopReviewWindowTests
{
    // 镜像 ApprovalContextBuilder 的公共常量。
    private const int MaxEntries    = ApprovalContextBuilder.MaxEntries;
    private const int MaxCharacters = ApprovalContextBuilder.MaxCharacters;

    [Test]
    public void EmptyInput_YieldsEmptyWindowWithoutNotice() =>
        Assert.That(ApprovalContextBuilder.Build([]).Items, Is.Empty);

    [Test]
    public void EntriesWithinLimits_KeepAllInAscendingOrderWithoutNotice()
    {
        var items = Ascending(Enumerable.Range(0, MaxEntries).Select(index => $"t{index}").ToArray());

        var kept = ApprovalContextBuilder.Build(items).Items;

        Assert.That(kept.Select(item => item.Text).ToArray(),
                    Is.EqualTo(items.Select(item => item.Text).ToArray()));
        Assert.That(kept, Has.None.Matches<ApprovalContextItem>(item =>
                                                                    item is
                                                                    {
                                                                        Source: ApprovalInputSource.SystemNotice
                                                                    }));
    }

    [Test]
    public void ExactlyMaxCharacters_KeepsBothEntriesWithoutNotice()
    {
        var items = Ascending(new string('a', MaxCharacters / 2), new string('b', MaxCharacters / 2));

        var kept = ApprovalContextBuilder.Build(items).Items;

        Assert.That(kept.Select(item => item.Text).ToArray(), Is.EqualTo(new[] { items[0].Text, items[1].Text }));
        Assert.That(kept, Has.None.Matches<ApprovalContextItem>(item =>
                                                                    item is
                                                                    {
                                                                        Source: ApprovalInputSource.SystemNotice
                                                                    }));
    }

    [Test]
    public void EntryLimitExceeded_KeepsNewestThirtyTwoAndAppendsNotice()
    {
        var items = Ascending(Enumerable.Range(0, MaxEntries + 3).Select(index => $"t{index}").ToArray());

        var kept = ApprovalContextBuilder.Build(items).Items;

        Assert.That(kept, Has.Count.EqualTo(MaxEntries + 1));
        Assert.That(kept[0].Text, Is.EqualTo("t3"));
        Assert.That(kept[MaxEntries - 1].Text, Is.EqualTo($"t{MaxEntries + 2}"));
        Assert.That(kept[MaxEntries].Source, Is.EqualTo(ApprovalInputSource.SystemNotice));
        Assert.That(kept[MaxEntries].RecordId, Is.EqualTo(ApprovalContextBuilder.WindowNoticeRecordId));
        Assert.That(kept[MaxEntries].Text, Does.Contain($"保留的 {MaxEntries} 条"));
        Assert.That(kept[MaxEntries].Text, Does.Contain("另有 3 条更早记录未包含在内"));
    }

    [Test]
    public void CharacterBudgetExceeded_DropsOldestEntriesAndAppendsNotice()
    {
        var items = Ascending(new string('a', 15_000), new string('b', 10_000), new string('c', 10_000));

        var kept = ApprovalContextBuilder.Build(items).Items;

        Assert.That(kept, Has.Count.EqualTo(3));
        Assert.That(kept[0].Text, Is.EqualTo(items[1].Text));
        Assert.That(kept[1].Text, Is.EqualTo(items[2].Text));
        Assert.That(kept[2].Source, Is.EqualTo(ApprovalInputSource.SystemNotice));
        Assert.That(kept[2].Text, Does.Contain("另有 1 条更早记录未包含在内"));
    }

    [Test]
    public void FirstCandidateOverBudget_IsKeptWithoutNotice()
    {
        var items = Ascending(new string('x', MaxCharacters + 10_000));

        var kept = ApprovalContextBuilder.Build(items).Items;

        Assert.That(kept.Select(item => item.Text).ToArray(), Is.EqualTo(new[] { items[0].Text }));
        Assert.That(kept, Has.None.Matches<ApprovalContextItem>(item =>
                                                                    item is
                                                                    {
                                                                        Source: ApprovalInputSource.SystemNotice
                                                                    }));
    }

    private static ApprovalContextEntry[] Ascending(params string[] texts)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return texts.Select((text, index) => new ApprovalContextEntry(start.AddMinutes(index),
                                                                      ApprovalInputSource.UserUtterance,
                                                                      $"turn-{index + 1}", text)).ToArray();
    }
}

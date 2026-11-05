using NUnit.Framework;
using TinyHarness.Desktop.Runtime;

namespace TinyHarness.Desktop.Tests;

/// <summary>
///     <para>
///         直测 DesktopSessionFactory.SelectReviewInstructions 的窗口规则：条数与字符预算、
///         自最新向前保留、空窗口保留首条、仅在确有丢弃时追加〔窗口说明〕。
///     </para>
///     <para>
///         Directly tests the window rules of DesktopSessionFactory.SelectReviewInstructions through IVT: entry
///         and character budgets, newest-first retention, unconditional first candidate on an empty window, and
///         the 〔窗口说明〕 notice only when something was actually dropped.
///     </para>
/// </summary>
public class DesktopReviewWindowTests
{
    // 镜像 DesktopSessionFactory 的私有常量 MaxReviewInstructionEntries 与 MaxReviewInstructionCharacters。
    private const int MaxEntries    = 32;
    private const int MaxCharacters = 30_000;

    [Test]
    public void EmptyInput_YieldsEmptyWindowWithoutNotice() =>
        Assert.That(DesktopSessionFactory.SelectReviewInstructions([]), Is.Empty);

    [Test]
    public void EntriesWithinLimits_KeepAllInAscendingOrderWithoutNotice()
    {
        var items = Ascending(Enumerable.Range(0, MaxEntries).Select(index => $"t{index}").ToArray());

        var kept = DesktopSessionFactory.SelectReviewInstructions(items);

        Assert.That(kept, Is.EqualTo(items.Select(item => item.Text).ToArray()));
        Assert.That(kept, Has.None.StartsWith("〔窗口说明〕"));
    }

    [Test]
    public void ExactlyMaxCharacters_KeepsBothEntriesWithoutNotice()
    {
        var items = Ascending(new string('a', MaxCharacters / 2), new string('b', MaxCharacters / 2));

        var kept = DesktopSessionFactory.SelectReviewInstructions(items);

        Assert.That(kept, Is.EqualTo(new[] { items[0].Text, items[1].Text }));
        Assert.That(kept, Has.None.StartsWith("〔窗口说明〕"));
    }

    [Test]
    public void EntryLimitExceeded_KeepsNewestThirtyTwoAndAppendsNotice()
    {
        var items = Ascending(Enumerable.Range(0, MaxEntries + 3).Select(index => $"t{index}").ToArray());

        var kept = DesktopSessionFactory.SelectReviewInstructions(items);

        Assert.That(kept, Has.Count.EqualTo(MaxEntries + 1));
        Assert.That(kept[0], Is.EqualTo("t3"));
        Assert.That(kept[MaxEntries - 1], Is.EqualTo($"t{MaxEntries + 2}"));
        Assert.That(kept[MaxEntries], Does.StartWith("〔窗口说明〕"));
        Assert.That(kept[MaxEntries], Does.Contain($"最近保留的 {MaxEntries} 条"));
        Assert.That(kept[MaxEntries], Does.Contain("另有 3 条更早记录未包含在内"));
    }

    [Test]
    public void CharacterBudgetExceeded_DropsOldestEntriesAndAppendsNotice()
    {
        var items = Ascending(new string('a', 15_000), new string('b', 10_000), new string('c', 10_000));

        var kept = DesktopSessionFactory.SelectReviewInstructions(items);

        Assert.That(kept, Has.Count.EqualTo(3));
        Assert.That(kept[0], Is.EqualTo(items[1].Text));
        Assert.That(kept[1], Is.EqualTo(items[2].Text));
        Assert.That(kept[2], Does.StartWith("〔窗口说明〕").And.Contains("另有 1 条更早记录未包含在内"));
    }

    [Test]
    public void FirstCandidateOverBudget_IsKeptWithoutNotice()
    {
        var items = Ascending(new string('x', MaxCharacters + 10_000));

        var kept = DesktopSessionFactory.SelectReviewInstructions(items);

        Assert.That(kept, Is.EqualTo(new[] { items[0].Text }));
        Assert.That(kept, Has.None.StartsWith("〔窗口说明〕"));
    }

    private static (DateTimeOffset Time, string Text)[] Ascending(params string[] texts)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return texts.Select((text, index) => (Time : start.AddMinutes(index), Text : text)).ToArray();
    }
}

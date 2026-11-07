using TinyHarness.Core.Models.Permissions;

namespace TinyHarness.Tests;

/// <summary>
///     <para>核对旧七模式的历史标签及未知值回退，防止数字名称被解释为当前审批模式。</para>
///     <para>
///         Verifies historical labels for the seven legacy modes and fallback for unknown values,
///         preventing numeric names from being interpreted as current approval modes.
///     </para>
/// </summary>
public class LegacyPermissionDisplayTests
{
    [TestCase("ConfirmEveryAction", "逐次确认")]
    [TestCase("ReadOnly", "只读，可申请")]
    [TestCase("ProjectWrite", "项目内读写")]
    [TestCase("FullAccess", "完全开放")]
    [TestCase("ReviewEveryAction", "逐项 AI 审核")]
    [TestCase("ReadOnlyAutoReview", "只读，AI 审批")]
    [TestCase("ProjectWriteAutoReview", "项目内读写，AI 审批")]
    public void LegacyLabel_KnownModePreservesHistoricalLabel(string legacyModeName, string expectedLabel)
    {
        Assert.That(LegacyPermissionSelectionMapper.LegacyLabel(legacyModeName), Is.EqualTo(expectedLabel));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    [TestCase("UnknownMode")]
    [TestCase("HumanApproval")]
    [TestCase("AIAutoReview")]
    [TestCase("0")]
    [TestCase("1")]
    [TestCase("2")]
    [TestCase("3")]
    [TestCase("4")]
    [TestCase("5")]
    [TestCase("6")]
    [TestCase("-1")]
    public void LegacyLabel_UnknownOrNumericNameFallsBack(string? legacyModeName)
    {
        Assert.That(LegacyPermissionSelectionMapper.LegacyLabel(legacyModeName), Is.EqualTo("旧版模式"));
    }
}

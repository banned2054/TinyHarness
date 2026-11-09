using System.Text.Json;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Services.Persistence;

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

    /// <summary>
    ///     <para>
    ///         旧日志的选择记录只有 <c>mode</c> 字段：解析必须绑定到 <see cref="LegacyPermissionSelection.Mode" />，
    ///         写回也必须保持同一个 wire 名，避免再次改名让已落盘记录解析失败。
    ///     </para>
    ///     <para>
    ///         Legacy selection records only carry a <c>mode</c> field: parsing must bind it to
    ///         <see cref="LegacyPermissionSelection.Mode" /> and writing must keep that same wire name, so another
    ///         rename cannot silently break persisted logs.
    ///     </para>
    /// </summary>
    [Test]
    public void HistoricalPermissionSelection_UsesModeWireNameInBothDirections()
    {
        // 与真实会话日志同形的 /1 载荷（字段名固定为 mode）。
        using var document = JsonDocument.Parse(
            """{"schema":"tinyharness.permission-selection/1","revision":1,"mode":"ProjectWriteAutoReview"}""");
        var selection =
            document.RootElement.Deserialize(PermissionPersistenceJsonContext.Default.LegacyPermissionSelection);
        Assert.That(selection, Is.Not.Null);
        selection!.Validate();
        Assert.That(selection.Mode, Is.EqualTo("ProjectWriteAutoReview"));

        var written = JsonSerializer.Serialize(selection,
                                               PermissionPersistenceJsonContext.Default.LegacyPermissionSelection);
        Assert.Multiple(() =>
        {
            Assert.That(written, Does.Contain("\"mode\":"));
            Assert.That(written, Does.Not.Contain("modeName"));
        });
    }
}

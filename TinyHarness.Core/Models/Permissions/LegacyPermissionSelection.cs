namespace TinyHarness.Core.Models.Permissions;

/// <summary>
///     <para>
///         旧七模式选择记录（协议 tinyharness.permission-selection/1）的最小解析模型：
///         仅用于迁移判定（hasLegacySelection）与历史展示，<see cref="Mode" /> 保留旧模式原名，
///         经 <see cref="LegacyPermissionSelectionMapper" /> 映射为审批模式；对运行时授权没有任何效力。
///     </para>
///     <para>
///         The minimal parsing model for the legacy seven-preset selection record (schema
///         tinyharness.permission-selection/1): used only for migration detection (hasLegacySelection) and history
///         display. <see cref="Mode" /> keeps the original legacy mode name, mapped to an approval mode through
///         <see cref="LegacyPermissionSelectionMapper" />; it has no runtime authorization effect.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         历史记录写出的 JSON 字段是 <c>mode</c>，由 camelCase 命名策略从 <see cref="Mode" /> 推导；
///         改名会直接让已落盘的旧日志解析失败，wire 名必须保持为 <c>mode</c>。
///     </para>
///     <para>
///         Persisted legacy records use the JSON field <c>mode</c>, derived from <see cref="Mode" /> by the
///         camelCase naming policy; renaming it makes already-written logs fail to parse, so the wire name must
///         stay <c>mode</c>.
///     </para>
/// </remarks>
public sealed record LegacyPermissionSelection
{
    public const string LegacySchema = "tinyharness.permission-selection/1";

    /// <summary>已知旧模式名集合；解析与迁移映射的共同边界。The known legacy mode names; shared boundary for parsing and mapping.</summary>
    public static readonly IReadOnlyList<string> KnownLegacyModeNames =
    [
        "ConfirmEveryAction", "ReadOnly", "ProjectWrite", "FullAccess",
        "ReviewEveryAction", "ReadOnlyAutoReview", "ProjectWriteAutoReview"
    ];

    public string Schema   { get; init; } = LegacySchema;
    public long   Revision { get; init; }
    public string Mode     { get; init; } = string.Empty;

    /// <summary>
    ///     <para>拒绝未知协议、无效修订与未知旧模式名；解析仅限已知名称，不做授权解释。</para>
    ///     <para>Rejects unknown schemas, invalid revisions and unknown legacy names; parsing stays within known names.</para>
    /// </summary>
    public void Validate()
    {
        if (Schema != LegacySchema || Revision < 1 ||
            !KnownLegacyModeNames.Contains(Mode, StringComparer.Ordinal))
            throw new InvalidDataException("Invalid legacy session permission selection.");
    }
}

/// <summary>
///     <para>
///         旧七模式选择到双审批模式的最小迁移映射；仅供迁移确认与历史展示，
///         对运行时授权没有任何效力。
///     </para>
///     <para>
///         The minimal migration mapping from legacy seven-preset selections to the two approval modes;
///         used only for migration confirmation and history display, with no runtime authorization effect.
///     </para>
/// </summary>
public static class LegacyPermissionSelectionMapper
{
    /// <summary>
    ///     <para>仅返回旧模式的历史展示标签；空名称或未知名称显示为“旧版模式”，不产生授权。</para>
    ///     <para>
    ///         Returns only the historical display label for a legacy mode; null, empty or unknown names display
    ///         as “旧版模式” and confer no authorization.
    ///     </para>
    /// </summary>
    public static string LegacyLabel(string? legacyModeName) => legacyModeName?.Trim() switch
    {
        "ConfirmEveryAction"     => "逐次确认",
        "ReadOnly"               => "只读，可申请",
        "ProjectWrite"           => "项目内读写",
        "FullAccess"             => "完全开放",
        "ReviewEveryAction"      => "逐项 AI 审核",
        "ReadOnlyAutoReview"     => "只读，AI 审批",
        "ProjectWriteAutoReview" => "项目内读写，AI 审批",
        _                        => "旧版模式"
    };

    /// <summary>
    ///     <para>
    ///         把旧模式名映射为审批模式：三个 AI 审核预设映射为 AIAutoReview，其余（含完全开放）
    ///         一律保守映射为 HumanApproval；未知名称返回 null。
    ///     </para>
    ///     <para>
    ///         Maps a legacy mode name to an approval mode: the three AI-review presets map to AIAutoReview and
    ///         everything else (FullAccess included) maps conservatively to HumanApproval; unknown names return
    ///         null.
    ///     </para>
    /// </summary>
    public static SessionApprovalMode? FromLegacyName(string? legacyModeName) => legacyModeName?.Trim() switch
    {
        "ReviewEveryAction" or "ReadOnlyAutoReview" or "ProjectWriteAutoReview"
            => SessionApprovalMode.AIAutoReview,
        "ConfirmEveryAction" or "ReadOnly" or "ProjectWrite" or "FullAccess"
            => SessionApprovalMode.HumanApproval,
        _ => null
    };
}

using TinyHarness.Core.Models.Permissions;

namespace TinyHarness.Core.Models.Configuration;

/// <summary>
///     <para>
///         可信用户层的权限设置。当前形状只区分审批模式（defaultApprovalMode）与独立审批模型；
///         仍携带 defaultMode/composerModes 的旧配置按迁移待确认态读取，确认前不拒载其余字段。
///     </para>
///     <para>
///         Trusted user-level permission settings. The current shape carries only the default approval mode
///         and the independent reviewer model; legacy configs still holding defaultMode/composerModes load as
///         pending-migration state without rejecting their other fields.
///     </para>
/// </summary>
public sealed record PermissionSettings
{
    /// <summary>
    ///     <para>
    ///         旧七模式默认值；仅在配置仍处于旧形状（迁移待确认或显式编辑旧形状）时非空。
    ///         运行时授权不再读取它，仅供迁移映射与界面过渡展示。
    ///     </para>
    ///     <para>
    ///         The legacy seven-preset default; non-null only while the config is still in the legacy shape
    ///         (pending migration or an explicit legacy edit). Runtime authorization never reads it; it feeds
    ///         the migration mapping and transitional UI display only.
    ///     </para>
    /// </summary>
    public string? LegacyDefaultMode { get; init; }

    /// <summary>
    ///     <para>旧 Composer 菜单可见集；与 <see cref="LegacyDefaultMode" /> 同生同灭。</para>
    ///     <para>
    ///         The legacy composer-menu visibility set; lives and dies together with <see cref="DefaultMode" />.
    ///     </para>
    /// </summary>
    public IReadOnlyList<string>? LegacyComposerModes { get; init; }

    /// <summary>
    ///     <para>当前形状的默认审批模式；缺省为人工审批。</para>
    ///     <para>The current-shape default approval mode; defaults to human approval.</para>
    /// </summary>
    public SessionApprovalMode DefaultApprovalMode { get; init; } = SessionApprovalMode.HumanApproval;

    public bool    UseCurrentModel { get; init; } = true;
    public string? ReviewerProfile { get; init; }
    public string? ReviewerModel   { get; init; }

    /// <summary>
    ///     <para>
    ///         检测到旧 defaultMode/composerModes 且迁移尚未确认；为 true 时配置其余字段照常可用。
    ///     </para>
    ///     <para>
    ///         Legacy defaultMode/composerModes were detected and migration is not yet confirmed; other config
    ///         fields stay fully usable while this is true.
    ///     </para>
    /// </summary>
    public bool PendingMigration { get; init; }

    /// <summary>
    ///     <para>
    ///         校验两种形状：当前形状要求已定义的审批模式；旧形状沿用"菜单互异且含默认值"规则；
    ///         指定审批模型时必须同时提供供应商和模型。
    ///     </para>
    ///     <para>
    ///         Validates both shapes: the current shape requires a defined approval mode; the legacy shape keeps
    ///         the "distinct visible modes including the default" rule; a fixed reviewer requires both provider
    ///         and model.
    ///     </para>
    /// </summary>
    public void Validate()
    {
        if (!Enum.IsDefined(DefaultApprovalMode))
            throw new InvalidDataException("Permission settings require a defined default approval mode.");
        if (PendingMigration && LegacyDefaultMode is null)
            throw
                new InvalidDataException("A pending-migration permission setting must carry its legacy default mode.");
        if (LegacyDefaultMode is { } legacyDefault)
            if (LegacyPermissionSelectionMapper.FromLegacyName(legacyDefault) is null ||
                LegacyComposerModes is null || LegacyComposerModes.Count == 0 ||
                LegacyComposerModes.Any(mode => LegacyPermissionSelectionMapper.FromLegacyName(mode) is null) ||
                LegacyComposerModes.Distinct().Count() != LegacyComposerModes.Count ||
                !LegacyComposerModes.Contains(legacyDefault, StringComparer.Ordinal))
                throw new
                    InvalidDataException("Legacy permission settings require distinct visible modes including the default.");

        if (!UseCurrentModel &&
            (string.IsNullOrWhiteSpace(ReviewerProfile) || string.IsNullOrWhiteSpace(ReviewerModel)))
            throw new InvalidDataException("Select a provider and model for AI approval.");
    }
}

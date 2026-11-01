using TinyHarness.Core.Models.Permissions;

namespace TinyHarness.Core.Models.Configuration;

/// <summary>
///     <para>可信用户层的权限菜单、默认模式与审批模型选择。</para>
///     <para>Trusted user-level permission menu, default mode, and reviewer model selection.</para>
/// </summary>
public sealed record PermissionSettings
{
    public SessionPermissionMode                DefaultMode     { get; init; } = SessionPermissionMode.ReadOnly;
    public IReadOnlyList<SessionPermissionMode> ComposerModes   { get; init; } = SessionPermissionModes.All;
    public bool                                 UseCurrentModel { get; init; } = true;
    public string?                              ReviewerProfile { get; init; }
    public string?                              ReviewerModel   { get; init; }

    /// <summary>
    ///     <para>校验菜单和默认值一致；指定审批模型时必须同时提供供应商和模型。</para>
    ///     <para>Validates menu/default consistency and requires both provider and model for a fixed reviewer.</para>
    /// </summary>
    public void Validate()
    {
        if (!Enum.IsDefined(DefaultMode) || ComposerModes is null || ComposerModes.Count == 0 ||
            ComposerModes.Any(mode => !Enum.IsDefined(mode)) ||
            ComposerModes.Distinct().Count() != ComposerModes.Count || !ComposerModes.Contains(DefaultMode))
            throw new InvalidDataException("Permission settings require distinct visible modes including the default.");
        if (!UseCurrentModel &&
            (string.IsNullOrWhiteSpace(ReviewerProfile) || string.IsNullOrWhiteSpace(ReviewerModel)))
            throw new InvalidDataException("Select a provider and model for AI approval.");
    }
}

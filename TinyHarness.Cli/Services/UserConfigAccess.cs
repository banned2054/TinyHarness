using TinyHarness.Cli.Commands;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.Configuration;

namespace TinyHarness.Cli.Services;

/// <summary>
/// 管理命令共享的用户配置访问：加载、查找 profile、更新与保存。
///
/// Shared user-config access for management commands: load, find profiles, update, and save.
/// </summary>
internal static class UserConfigAccess
{
    /// <summary>
    /// 加载用户配置，同时返回文件是否存在。
    /// Loads the user config and reports whether the file exists.
    /// </summary>
    public static async Task<(UserConfig Config, bool Exists)> LoadAsync(CommandContext    context,
                                                                         CancellationToken cancellationToken)
    {
        var path   = context.ResolveUserConfigPath();
        var exists = File.Exists(path);
        var config = await UserConfigStore.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        return (config, exists);
    }

    /// <summary>
    /// 保存用户配置并输出文件路径。
    /// Saves the user config and reports the file path.
    /// </summary>
    public static async Task SaveAsync(CommandContext context, UserConfig config, CancellationToken cancellationToken)
    {
        var path = context.ResolveUserConfigPath();
        await UserConfigStore.SaveAsync(path, config, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按名称查找 profile。
    /// Finds a profile by name.
    /// </summary>
    public static UserProfile? FindProfile(UserConfig config, string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : config.Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// 返回默认 profile；未选择时返回 <see langword="null"/>。
    /// Returns the default profile; <see langword="null"/> when none is selected.
    /// </summary>
    public static UserProfile? DefaultProfile(UserConfig config) => FindProfile(config, config.DefaultProfile);

    /// <summary>
    /// 解析命令的目标 profile：显式 --provider 名称或默认 profile；失败时给出可操作的错误。
    ///
    /// Resolves the target profile of a command: an explicit --provider name or the default profile; failures produce actionable errors.
    /// </summary>
    public static async Task<(UserConfig Config, UserProfile Profile)> RequireProfileAsync(
        CommandContext context, string? providerName, CancellationToken cancellationToken)
    {
        var (config, exists) = await LoadAsync(context, cancellationToken).ConfigureAwait(false);
        if (!exists || config.Profiles.Count == 0)
        {
            throw new
                InvalidOperationException("No provider profiles are configured yet. Run 'tinyharness init' or 'tinyharness provider add <name>'.");
        }

        if (!string.IsNullOrWhiteSpace(providerName))
        {
            var named = FindProfile(config, providerName);
            if (named is null)
            {
                throw new
                    InvalidOperationException($"Profile '{providerName}' was not found. Known profiles: {string.Join(", ", config.Profiles.Select(p => p.Name))}.");
            }

            return (config, named);
        }

        var profile = DefaultProfile(config);
        if (profile is null)
        {
            throw new
                InvalidOperationException("No default profile is selected. Run 'tinyharness provider use <name>' or pass --provider <name>.");
        }

        return (config, profile);
    }

    /// <summary>
    /// 替换同名 profile 或追加新 profile，保持原有顺序。
    /// Replaces the profile with the same name or appends a new one, preserving order.
    /// </summary>
    public static UserConfig UpsertProfile(UserConfig config, UserProfile profile)
    {
        var profiles = new List<UserProfile>(config.Profiles);
        var index    = profiles.FindIndex(p => string.Equals(p.Name, profile.Name, StringComparison.Ordinal));
        if (index >= 0)
        {
            profiles[index] = profile;
        }
        else
        {
            profiles.Add(profile);
        }

        return config with { Profiles = profiles };
    }

    /// <summary>
    /// 描述 profile 的 API key 来源，用于列表显示；不包含密钥值。
    /// Describes a profile's API key source for listings; never includes the secret value.
    /// </summary>
    public static string DescribeKeySource(UserProfile profile) =>
        !string.IsNullOrWhiteSpace(profile.ApiKeyCredentialTarget)
            ? $"credential store '{profile.ApiKeyCredentialTarget}'"
            : !string.IsNullOrWhiteSpace(profile.ApiKeyEnvironmentVariable)
                ? $"env '{profile.ApiKeyEnvironmentVariable}'"
                : "not configured";
}

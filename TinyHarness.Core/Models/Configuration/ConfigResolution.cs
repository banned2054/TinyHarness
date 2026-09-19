namespace TinyHarness.Core.Models.Configuration;

/// <summary>
/// 配置查找来源。显式 `--config` 与当前目录项目文件直接来自文件；用户配置来自平台用户配置目录；
/// 都不存在时使用内置默认值。
///
/// Where the effective configuration came from. An explicit `--config` and the current-directory project
/// file are read directly; the user config lives in the platform user config directory; built-in defaults
/// apply when no file exists.
/// </summary>
public enum ConfigSourceKind
{
    /// <summary>未找到任何配置文件，全部使用内置默认值。No config file found; built-in defaults only.</summary>
    Defaults,

    /// <summary>未指定 --config 时自动发现的当前目录 tinyharness.json。The auto-discovered current-directory tinyharness.json when --config is absent.</summary>
    ProjectFile,

    /// <summary>通过 --config 显式指定的配置文件。The file explicitly selected through --config.</summary>
    ExplicitFile,

    /// <summary>用户配置目录中的 user-config.json。The user-config.json in the user config directory.</summary>
    UserConfig,
}

/// <summary>
/// 一次配置解析的完整结果：生效配置、来源与查找路径，供 CLI 展示与诊断。
///
/// The complete result of one config resolution: effective config, source, and lookup paths, for CLI display and diagnostics.
/// </summary>
public sealed record ConfigResolution
{
    public TinyHarnessConfig Config { get; init; } = new();

    public ConfigSourceKind Source { get; init; }

    /// <summary>实际读取的配置文件绝对路径；来源为 Defaults 时为 null。Absolute path of the file actually read; null for Defaults.</summary>
    public string? SourcePath { get; init; }

    /// <summary>用户配置中选中的 profile 名称；非用户配置来源时为 null。The profile selected in the user config; null for other sources.</summary>
    public string? ProfileName { get; init; }

    /// <summary>用户配置中选中的模型 ID；未选择时为 null。The model id selected in the user config; null when none.</summary>
    public string? ModelName { get; init; }

    /// <summary>用户配置文件绝对路径（无论是否存在）。Absolute user config path, whether or not the file exists.</summary>
    public required string UserConfigPath { get; init; }

    public bool UserConfigExists { get; init; }

    /// <summary>当前目录项目配置文件的绝对路径。Absolute path of the current-directory project config file.</summary>
    public required string ProjectConfigPath { get; init; }

    public bool ProjectConfigExists { get; init; }
}

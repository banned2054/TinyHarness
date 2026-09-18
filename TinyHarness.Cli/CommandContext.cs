using TinyHarness.Core.Configuration;
using TinyHarness.Core.Runtime;

namespace TinyHarness.Cli;

/// <summary>
/// 管理命令的共享依赖：终端 I/O、凭据存储与可选的用户配置路径覆盖（测试与便携部署使用）。
///
/// Shared dependencies for management commands: console I/O, the credential store, and an optional user config
/// path override (for tests and portable setups).
/// </summary>
internal sealed class CommandContext
{
    public required ICliConsole Io { get; init; }

    public required ICredentialStore Credentials { get; init; }

    /// <summary>用户配置文件路径；null 表示使用固定默认位置。User config path; null means the fixed default location.</summary>
    public string? UserConfigPath { get; init; }

    /// <summary>
    /// 项目配置与相对路径解析所使用的工作目录；null 表示进程当前目录。
    /// Working directory used for project-config and relative-path resolution; null means the process current directory.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// 解析实际使用的用户配置路径。
    /// Resolves the user config path actually in effect.
    /// </summary>
    public string ResolveUserConfigPath() =>
        string.IsNullOrWhiteSpace(UserConfigPath)
            ? UserConfigStore.DefaultFilePath()
            : Path.GetFullPath(UserConfigPath);

    /// <summary>
    /// 返回规范化的命令工作目录。
    /// Returns the normalized command working directory.
    /// </summary>
    public string ResolveWorkingDirectory() =>
        string.IsNullOrWhiteSpace(WorkingDirectory)
            ? Environment.CurrentDirectory
            : Path.GetFullPath(WorkingDirectory);
}

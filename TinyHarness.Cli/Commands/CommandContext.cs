using TinyHarness.Cli.Services;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Cli.Commands;

/// <summary>
///     <para>管理命令的共享依赖：终端 I/O、凭据存储、可选的用户配置路径与应用基准目录覆盖。</para>
///     <para>
///         Shared dependencies for management commands: console I/O, the credential store, and optional
///         user config path and application base directory overrides.
///     </para>
/// </summary>
internal sealed class CommandContext
{
    public required ICliConsole Io { get; init; }

    public required ICredentialStore Credentials { get; init; }

    /// <summary>
    ///     <para>用户配置文件路径；null 表示使用固定默认位置。</para>
    ///     <para>User config path; null means the fixed default location.</para>
    /// </summary>
    public string? UserConfigPath { get; init; }

    /// <summary>
    ///     <para>命令级工作目录（如沙箱 provision 计划）；null 表示进程当前目录。配置解析不使用该目录。</para>
    ///     <para>
    ///         Command-level working directory (for example the sandbox provision plan); null means the process
    ///         current directory. Config resolution never uses this directory.
    ///     </para>
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    ///     <para>应用基准目录（用于定位随包沙箱组件）；null 表示使用 <see cref="AppContext.BaseDirectory" />。</para>
    ///     <para>
    ///         Application base directory used to locate bundled sandbox components; null means
    ///         <see cref="AppContext.BaseDirectory" />.
    ///     </para>
    /// </summary>
    public string? AppBaseDirectory { get; init; }

    /// <summary>
    ///     <para>解析实际使用的用户配置路径。</para>
    ///     <para>Resolves the user config path actually in effect.</para>
    /// </summary>
    public string ResolveUserConfigPath() => string.IsNullOrWhiteSpace(UserConfigPath)
        ? UserConfigStore.DefaultFilePath()
        : Path.GetFullPath(UserConfigPath);

    /// <summary>
    ///     <para>返回规范化的命令工作目录。</para>
    ///     <para>Returns the normalized command working directory.</para>
    /// </summary>
    public string ResolveWorkingDirectory() => string.IsNullOrWhiteSpace(WorkingDirectory)
        ? Environment.CurrentDirectory
        : Path.GetFullPath(WorkingDirectory);

    /// <summary>
    ///     <para>返回规范化的应用基准目录。</para>
    ///     <para>Returns the normalized application base directory.</para>
    /// </summary>
    public string ResolveAppBaseDirectory() =>
        WindowsSandboxComponentResolver.NormalizeAppBaseDirectory(AppBaseDirectory);
}

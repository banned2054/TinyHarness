using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Models.Configuration;

/// <summary>
///     <para>
///         Windows 沙箱的可信用户设置。只存放在用户配置（user-config.json）的 settings.windowsSandbox；
///         任何 tinyharness.json 配置文件、模型请求和命令规则都不能设置或覆盖这些字段。enabled 缺省为
///         false：未显式启用时保持宿主执行并明示"无 OS 隔离"；显式启用后组件缺失、路径非法或平台不符
///         都拒绝执行，绝不自动回退宿主。外部网络始终阻断；TCP 环回仅能由可信用户设置显式启用。
///     </para>
///     <para>
///         Trusted user settings for the Windows sandbox. They live only in the user
///         config's settings.windowsSandbox; any tinyharness.json config file,
///         model requests, and command rules cannot set or override them. enabled
///         defaults to false: without an explicit opt-in the harness keeps host
///         execution and states "no OS isolation"; once enabled, missing components,
///         invalid paths, or a wrong platform refuse to execute instead of silently
///         falling back. External networking is always blocked; TCP loopback requires
///         an explicit opt-in in trusted user settings.
///     </para>
/// </summary>
public sealed record WindowsSandboxSettings
{
    /// <summary>
    ///     <para>显式启用 Windows 沙箱执行。缺省 false。</para>
    ///     <para>Explicitly enables Windows sandbox execution. Defaults to false.</para>
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    ///     <para>显式允许 TCP 环回，包括宿主本地服务；使用独立账户和 home，外部网络及 UDP 仍阻断。</para>
    ///     <para>
    ///         Explicitly allows TCP loopback, including host services, using separate accounts and home
    ///         while blocking external networking and UDP.
    ///     </para>
    /// </summary>
    public bool AllowTcpLoopback { get; init; }

    /// <summary>
    ///     <para>codex-windows-sandbox-setup.exe 的绝对路径（每命令 refresh 使用）。</para>
    ///     <para>Absolute path of codex-windows-sandbox-setup.exe (used for the per-command refresh).</para>
    /// </summary>
    public string SetupExecutablePath { get; init; } = string.Empty;

    /// <summary>
    ///     <para>codex-command-runner.exe 的绝对路径。</para>
    ///     <para>Absolute path of codex-command-runner.exe.</para>
    /// </summary>
    public string RunnerExecutablePath { get; init; } = string.Empty;

    /// <summary>
    ///     <para>
    ///         sandbox home（codex_home）：cap_sid、.sandbox、.sandbox-secrets、.sandbox-bin 所在目录。
    ///         留空时按网络模式使用独立固定默认 home，与 Codex 发布版独立；显式设置必须是绝对路径。
    ///     </para>
    ///     <para>
    ///         The sandbox home (codex_home) holds cap_sid, .sandbox, .sandbox-secrets and .sandbox-bin.
    ///         A blank value selects the mode's separate default home, independent of the Codex release;
    ///         an explicit value must be absolute.
    ///     </para>
    /// </summary>
    public string SandboxHome { get; init; } = string.Empty;

    /// <summary>
    ///     <para>固定隔离策略种类；缺省 workspace-write（工作区可写、元数据目录只读、断网）。</para>
    ///     <para>
    ///         The fixed isolation policy kind; defaults to workspace-write (workspace
    ///         writable, metadata directories read-only, network restricted).
    ///     </para>
    /// </summary>
    public SandboxPolicyKind Policy { get; init; } = SandboxPolicyKind.WorkspaceWrite;

    /// <summary>
    ///     <para>
    ///         目标命令的临时写根（TEMP/TMP/USERPROFILE 重定向目标）。缺省 &lt;sandboxHome&gt;\tmp。
    ///         必须是绝对路径，且不得位于工作区写根内。
    ///     </para>
    ///     <para>
    ///         The temp write root for target commands (TEMP/TMP/USERPROFILE redirect
    ///         target). Defaults to &lt;sandboxHome&gt;\tmp. It must be absolute and must not
    ///         sit inside a workspace write root.
    ///     </para>
    /// </summary>
    public string? SandboxTempRoot { get; init; }

    /// <summary>
    ///     <para>
    ///         额外写根（绝对路径）。read-only 策略下这是声明"必要临时写根"的唯一机制；
    ///         workspace-write 下叠加在工作区写根之上。它们授予真实文件系统写入，只应来自可信设置。
    ///     </para>
    ///     <para>
    ///         Additional write roots (absolute paths). Under the read-only policy this
    ///         is the only mechanism to declare necessary temp write roots; under
    ///         workspace-write they add to the workspace root. They grant real
    ///         filesystem writes and must only come from trusted settings.
    ///     </para>
    /// </summary>
    public IReadOnlyList<string> AdditionalWriteRoots { get; init; } = [];

    /// <summary>
    ///     <para>
    ///         附加到目标命令环境的名值对（如工具链缓存重定向 NUGET_PACKAGES、CARGO_HOME）。
    ///         来自可信设置；已知 secret 键始终在最后被移除。
    ///     </para>
    ///     <para>
    ///         Name/value pairs appended to the target command environment (e.g.
    ///         toolchain cache redirects such as NUGET_PACKAGES or CARGO_HOME). They
    ///         come from trusted settings; known secret keys are always removed last.
    ///     </para>
    /// </summary>
    public IReadOnlyDictionary<string, string>? ExtraEnvironment { get; init; }
}

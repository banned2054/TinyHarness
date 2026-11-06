using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Models.Configuration;

/// <summary>
///     <para>
///         Windows 沙箱的可信用户设置。只存放在用户配置（user-config.json）的 settings.windowsSandbox；
///         任何 tinyharness.json 配置文件、模型请求和命令规则都不能设置或覆盖这些字段。enabled 缺省为
///         false：未显式启用时保持宿主执行并明示"无 OS 隔离"；显式启用后组件缺失、路径非法或平台不符
///         都拒绝执行，绝不自动回退宿主。网络策略缺省 restricted（外部网络阻断，命令可经逐次审批临时
///         在线执行）；常设在线（enabled）与 TCP 环回都必须由可信用户设置显式选择。
///     </para>
///     <para>
///         Trusted user settings for the Windows sandbox. They live only in the user
///         config's settings.windowsSandbox; any tinyharness.json config file,
///         model requests, and command rules cannot set or override them. enabled
///         defaults to false: without an explicit opt-in the harness keeps host
///         execution and states "no OS isolation"; once enabled, missing components,
///         invalid paths, or a wrong platform refuse to execute instead of silently
///         falling back. The network policy defaults to restricted (external
///         networking blocked, per-command approval may still grant online
///         execution); standing online access and TCP loopback both require an
///         explicit opt-in in trusted user settings.
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
    ///         必须是绝对路径，且不得位于工作区写根内，也不得覆盖凭据目录
    ///         （&lt;sandboxHome&gt;\.sandbox-secrets）或落入其中——任一重叠都拒绝执行（fail closed）。
    ///     </para>
    ///     <para>
    ///         The temp write root for target commands (TEMP/TMP/USERPROFILE redirect
    ///         target). Defaults to &lt;sandboxHome&gt;\tmp. It must be absolute, must not
    ///         sit inside a workspace write root, and must never cover the credentials
    ///         directory (&lt;sandboxHome&gt;\.sandbox-secrets) or lie inside it — any overlap
    ///         refuses execution (fail closed).
    ///     </para>
    /// </summary>
    public string? SandboxTempRoot { get; init; }

    /// <summary>
    ///     <para>
    ///         额外写根（绝对路径）。read-only 策略下这是声明"必要临时写根"的唯一机制；
    ///         workspace-write 下叠加在工作区写根之上。它们授予真实文件系统写入，只应来自可信设置；
    ///         不得覆盖凭据目录（&lt;sandboxHome&gt;\.sandbox-secrets）或落入其中——任一方向重叠
    ///         （包含/等于/位于其内）都拒绝执行（fail closed）。
    ///     </para>
    ///     <para>
    ///         Additional write roots (absolute paths). Under the read-only policy this
    ///         is the only mechanism to declare necessary temp write roots; under
    ///         workspace-write they add to the workspace root. They grant real
    ///         filesystem writes and must only come from trusted settings; a root that
    ///         covers the credentials directory (&lt;sandboxHome&gt;\.sandbox-secrets) or lies
    ///         inside it is refused — overlap in either direction (containing, equal
    ///         to, or inside) fails closed.
    ///     </para>
    /// </summary>
    public IReadOnlyList<string> AdditionalWriteRoots { get; init; } = [];

    /// <summary>
    ///     <para>
    ///         可信只读根（绝对路径）。它们进入 setup payload 的 read_roots，由沙箱账户组以 OS 级
    ///         RX ACL 访问；不得覆盖凭据目录（&lt;sandboxHome&gt;\.sandbox-secrets）或落入其中——
    ///         任一方向重叠（包含/等于/位于其内）都拒绝执行（fail closed），严于"不得包含或等于
    ///         sandbox home"。
    ///     </para>
    ///     <para>
    ///         Trusted read-only roots (absolute paths). They enter the setup
    ///         payload's read_roots and are exposed to the sandbox account group
    ///         through OS-level RX ACLs; a root that covers the credentials directory
    ///         (&lt;sandboxHome&gt;\.sandbox-secrets) or lies inside it is refused — overlap
    ///         in either direction (containing, equal to, or inside) fails closed,
    ///         stricter than "must not contain or equal the sandbox home".
    ///     </para>
    /// </summary>
    public IReadOnlyList<string> AdditionalReadRoots { get; init; } = [];

    /// <summary>
    ///     <para>
    ///         沙箱命令的常设网络策略。缺省 Restricted：外部网络阻断，单个命令仍可经逐次审批临时
    ///         在线执行。Enabled 表示所有沙箱命令以在线账户执行，不再逐次审批。
    ///     </para>
    ///     <para>
    ///         The standing network policy for sandboxed commands. Restricted (the
    ///         default) blocks external networking while a single command may still
    ///         be granted online execution through per-command approval; Enabled runs
    ///         every sandboxed command under the online account without per-command
    ///         approval.
    ///     </para>
    /// </summary>
    public SandboxNetworkPolicy NetworkPolicy { get; init; } = SandboxNetworkPolicy.Restricted;

    /// <summary>
    ///     <para>
    ///         沙箱子进程的代理环境（HTTP_PROXY 等），独立于应用自身 HTTP 路由；仅在生效网络在线时
    ///         注入。代理地址绝不进入日志或显示串，代理认证凭据不进入子进程环境。
    ///     </para>
    ///     <para>
    ///         The proxy environment (HTTP_PROXY and friends) of sandboxed child
    ///         processes, independent of the app's own HTTP route; it is injected only
    ///         while the effective network is online. Proxy addresses never reach logs
    ///         or display strings, and proxy credentials never enter the child
    ///         environment.
    ///     </para>
    /// </summary>
    public SandboxProxyPolicy Proxy { get; init; } = new();

    /// <summary>
    ///     <para>
    ///         附加到目标命令环境的名值对（如工具链缓存重定向 NUGET_PACKAGES、CARGO_HOME）。
    ///         来自可信设置；已知 secret 键始终在最后被移除。这是可信显式配置，优先级最高：
    ///         可为受限子进程显式注入代理键（不推荐）——"受限 env 无代理键"的不变量指默认
    ///         派生路径（代理仅在生效网络在线时由后端注入），而非可信显式覆盖。
    ///     </para>
    ///     <para>
    ///         Name/value pairs appended to the target command environment (e.g.
    ///         toolchain cache redirects such as NUGET_PACKAGES or CARGO_HOME). They
    ///         come from trusted settings; known secret keys are always removed last.
    ///         This is trusted explicit configuration with the highest priority: it may
    ///         explicitly inject proxy keys into restricted child processes (not
    ///         recommended) — the "restricted env carries no proxy keys" invariant
    ///         describes the default derived path (proxy keys are injected by the
    ///         backend only while the effective network is online), not a trusted
    ///         explicit override.
    ///     </para>
    /// </summary>
    public IReadOnlyDictionary<string, string>? ExtraEnvironment { get; init; }
}

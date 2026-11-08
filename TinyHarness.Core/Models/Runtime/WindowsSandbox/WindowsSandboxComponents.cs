namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         Windows 沙箱组件与 home 的可信描述，来自独立用户/宿主设置，项目配置不能覆盖。
///         严格模式 setup v5，TCP 环回模式 v6；IPC 均为 v6，版本或账户模式不符时拒绝执行。
///         授权执行生成 v7（专用授权账户对与授权组）在 marker/凭据校验中作为新部署代接受。
///     </para>
///     <para>
///         Trusted sandbox components and home from separate user/host settings, never project overrides.
///         Strict mode requires setup v5 and TCP loopback v6; both use IPC v6 and reject mismatched
///         versions or account modes. The authorized-execution generation v7 (the dedicated
///         authorization account pair and group) is accepted as the new deployment generation by the
///         marker and credential checks.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         面向 TinyHarness 独立命名空间 fork；二进制文件名、DPAPI 和 IPC 保持原契约，
///         机器账户/规则与 Codex 分离，两个网络模式也分用账户和默认 home。
///         仍带旧 Codex 用户名的 marker/凭据一律拒绝复用。
///     </para>
///     <para>
///         Targets the TinyHarness namespace fork with the existing binary names, DPAPI and IPC contract.
///         Machine accounts/rules are separate from Codex, and the two network modes use separate
///         accounts and default homes. Markers or credentials naming legacy Codex accounts are rejected.
///     </para>
/// </remarks>
public sealed record WindowsSandboxComponents
{
    public const byte SetupVersion            = 5;
    public const byte TcpLoopbackSetupVersion = 6;

    /// <summary>
    ///     <para>
    ///         授权执行生成（fork SETUP_VERSION 7）：provisioning 写入专用授权账户对与授权组，
    ///         payload v7 携带 authorized_read_paths；普通执行 payload 仍为 v5/v6，旧 helper 兼容。
    ///     </para>
    ///     <para>
    ///         The authorized-execution generation (fork SETUP_VERSION 7): provisioning writes the
    ///         dedicated authorization account pair and authorization group, and a v7 payload carries
    ///         authorized_read_paths; normal-execution payloads stay at v5/v6 for old-helper
    ///         compatibility.
    ///     </para>
    /// </summary>
    public const byte AuthorizedSetupVersion = 7;

    public const byte   IpcVersion                 = 6;
    public const string DefaultOfflineUsername     = "TinyHarnessOffline";
    public const string DefaultOnlineUsername      = "TinyHarnessOnline";
    public const string TcpLoopbackOfflineUsername = "TinyHarnessTcpOff";
    public const string TcpLoopbackOnlineUsername  = "TinyHarnessTcpOn";

    /// <summary>
    ///     <para>
    ///         授权执行专用账户的缺省名（helper 协议 v7 provisioning 创建，与 Rust branding 常量同源）。
    ///         账户对只加入授权组、绝不加入沙箱组：授权组承载“全量 deny − 获批读”面，普通沙箱组的
    ///         全量 deny 不会压制获批读；普通执行绝不使用这两个账户。
    ///     </para>
    ///     <para>
    ///         The default dedicated authorization account names (created by helper protocol v7
    ///         provisioning, same source as the Rust branding constants). The pair joins only the
    ///         authorization group and never the sandbox group: the authorization group carries the
    ///         "full deny minus approved reads" face, so the sandbox group's full deny never
    ///         suppresses an approved read; ordinary executions never use these accounts.
    ///     </para>
    /// </summary>
    public const string DefaultAuthorizedOfflineUsername = "TinyHarnessAuthOff";

    public const string DefaultAuthorizedOnlineUsername = "TinyHarnessAuthOn";

    /// <summary>
    ///     <para>
    ///         旧 Codex 发布版的沙箱账户名；marker/凭据仍使用这些名字时必须拒绝复用并重新
    ///         provisioning，绝不能与 Codex 安装争用同一套账户/密码。
    ///     </para>
    ///     <para>
    ///         The sandbox account names of the legacy Codex release; markers or
    ///         credentials still using them must be refused and re-provisioned
    ///         instead of fighting with a Codex install over the same accounts.
    ///     </para>
    /// </summary>
    public const string LegacyCodexOfflineUsername = "CodexSandboxOffline";

    public const     string  LegacyCodexOnlineUsername = "CodexSandboxOnline";
    private readonly string? _authorizedOfflineUsername;
    private readonly string? _authorizedOnlineUsername;

    private readonly string? _offlineUsername;
    private readonly string? _onlineUsername;

    public bool AllowTcpLoopback     { get; init; }
    public byte RequiredSetupVersion => AllowTcpLoopback ? TcpLoopbackSetupVersion : SetupVersion;

    public required string SetupExecutablePath  { get; init; }
    public required string RunnerExecutablePath { get; init; }

    /// <summary>
    ///     <para>sandbox home（codex_home）：cap_sid、.sandbox、.sandbox-secrets、.sandbox-bin 所在目录。</para>
    ///     <para>
    ///         The sandbox home (codex_home): the directory holding cap_sid, .sandbox,
    ///         .sandbox-secrets, and .sandbox-bin.
    ///     </para>
    /// </summary>
    public required string SandboxHome { get; init; }

    public string OfflineUsername
    {
        get => _offlineUsername ?? (AllowTcpLoopback ? TcpLoopbackOfflineUsername : DefaultOfflineUsername);
        init => _offlineUsername = value;
    }

    public string OnlineUsername
    {
        get => _onlineUsername ?? (AllowTcpLoopback ? TcpLoopbackOnlineUsername : DefaultOnlineUsername);
        init => _onlineUsername = value;
    }

    /// <summary>
    ///     <para>授权执行专用离线账户名；缺省为 <see cref="DefaultAuthorizedOfflineUsername" />，测试可注入覆盖。</para>
    ///     <para>
    ///         The authorization offline account name; defaults to
    ///         <see cref="DefaultAuthorizedOfflineUsername" /> and accepts a test-injected override.
    ///     </para>
    /// </summary>
    public string AuthorizedOfflineUsername
    {
        get => _authorizedOfflineUsername ?? DefaultAuthorizedOfflineUsername;
        init => _authorizedOfflineUsername = value;
    }

    /// <summary>
    ///     <para>授权执行专用在线账户名；缺省为 <see cref="DefaultAuthorizedOnlineUsername" />，测试可注入覆盖。</para>
    ///     <para>
    ///         The authorization online account name; defaults to
    ///         <see cref="DefaultAuthorizedOnlineUsername" /> and accepts a test-injected override.
    ///     </para>
    /// </summary>
    public string AuthorizedOnlineUsername
    {
        get => _authorizedOnlineUsername ?? DefaultAuthorizedOnlineUsername;
        init => _authorizedOnlineUsername = value;
    }

    /// <summary>
    ///     <para>
    ///         独立固定的默认 sandbox home（每用户 %LOCALAPPDATA%\tinyharness\windows-sandbox-home）。
    ///         home 持有的是机器绑定状态（DPAPI LocalMachine 凭据、本机账户 SID），因此放 Local 而非
    ///         Roaming；它与 Codex 发布版的 home 完全独立。
    ///     </para>
    ///     <para>
    ///         The independent fixed default sandbox home (per-user
    ///         %LOCALAPPDATA%\tinyharness\windows-sandbox-home). The home holds
    ///         machine-bound state (DPAPI LocalMachine credentials, machine account
    ///         SIDs), so it lives under Local rather than Roaming; it is fully
    ///         independent of the Codex release's home.
    ///     </para>
    /// </summary>
    public static string DefaultSandboxHome =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tinyharness",
                     "windows-sandbox-home");

    public static string DefaultTcpLoopbackSandboxHome =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tinyharness",
                     "windows-sandbox-tcp-loopback-home");

    public string SandboxDirectory  => Path.Combine(SandboxHome, ".sandbox");
    public string MarkerPath        => Path.Combine(SandboxDirectory, "setup_marker.json");
    public string SetupErrorPath    => Path.Combine(SandboxDirectory, "setup_error.json");
    public string UsersFilePath     => Path.Combine(CredentialsDirectory, "sandbox_users.json");
    public string CapabilitySidPath => Path.Combine(SandboxHome, "cap_sid");

    /// <summary>
    ///     <para>
    ///         凭据目录（&lt;home&gt;\.sandbox-secrets）：DPAPI 凭据与账户映射所在，provisioning
    ///         对沙箱组永久 DENY。
    ///     </para>
    ///     <para>
    ///         The credentials directory (&lt;home&gt;\.sandbox-secrets): the DPAPI credentials and account map,
    ///         permanently denied to the sandbox group by provisioning.
    ///     </para>
    /// </summary>
    public string CredentialsDirectory => Path.Combine(SandboxHome, ".sandbox-secrets");

    /// <summary>
    ///     <para>
    ///         执行组件目录（&lt;home&gt;\.sandbox-bin）：沙箱链路执行组件所在，provisioning 锁内。
    ///     </para>
    ///     <para>
    ///         The execution components directory (&lt;home&gt;\.sandbox-bin): the sandbox chain's executables,
    ///         inside the provisioning lock.
    ///     </para>
    /// </summary>
    public string SandboxBinDirectory => Path.Combine(SandboxHome, ".sandbox-bin");

    /// <summary>
    ///     <para>
    ///         home 下受权限系统保护的敏感子路径（凭据目录、deny 簿记目录、授权映射表与执行组件
    ///         目录），供两端组合点构建应用层 C1 分类集。默认 tmp/tools/nuget-cache 运行时根物理
    ///         位于 home 下但刻意不在其中——整 home 入 C1 会把运行时根恒判保护类。组合期完整性
    ///         保护分两层：常设写根校验仍以整 home 为准，配置运行时根校验以本子路径集为准（运行
    ///         时根位于 home 内是预期布局）。
    ///     </para>
    ///     <para>
    ///         The permission-system-protected sensitive subpaths under the home (the credentials directory,
    ///         the deny bookkeeping directory, the authorization mapping table, and the execution components
    ///         directory) for the composition sites to build the application-layer C1 classification set. The
    ///         default tmp/tools/nuget-cache runtime roots physically sit under the home but are deliberately
    ///         absent — putting the whole home into C1 would classify every runtime root as protected.
    ///         Composition-time integrity protection has two layers: the standing write-root check still
    ///         keys on the whole home, while the configured runtime-root check keys on these subpaths
    ///         (runtime roots living under the home are the expected layout).
    ///     </para>
    /// </summary>
    public IReadOnlyList<string> ProtectedHomeSubpaths =>
        [CredentialsDirectory, SandboxDirectory, CapabilitySidPath, SandboxBinDirectory];

    /// <summary>
    ///     <para>解析 home：空白时使用所选网络模式的独立默认 home；显式值必须为绝对路径，否则拒绝。</para>
    ///     <para>
    ///         Resolves home: blank selects the network mode's separate default home;
    ///         explicit values must be absolute paths or are rejected.
    ///     </para>
    /// </summary>
    public static string ResolveSandboxHome(string? configuredSandboxHome, bool allowTcpLoopback = false)
    {
        if (string.IsNullOrWhiteSpace(configuredSandboxHome))
            return allowTcpLoopback ? DefaultTcpLoopbackSandboxHome : DefaultSandboxHome;

        return Path.IsPathFullyQualified(configuredSandboxHome)
            ? configuredSandboxHome.Trim()
            : throw new
                InvalidOperationException($"The trusted Windows sandbox setting 'sandboxHome' must be a fully qualified absolute path: '{configuredSandboxHome}'.");
    }

    /// <summary>
    ///     <para>是否为旧 Codex 发布版的沙箱账户名（SAM 名大小写不敏感比较）。</para>
    ///     <para>
    ///         Whether this is a legacy Codex-release sandbox account name
    ///         (case-insensitive SAM-name comparison).
    ///     </para>
    /// </summary>
    public static bool IsLegacyCodexAccountName(string? username) =>
        string.Equals(username, LegacyCodexOfflineUsername, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(username, LegacyCodexOnlineUsername, StringComparison.OrdinalIgnoreCase);
}

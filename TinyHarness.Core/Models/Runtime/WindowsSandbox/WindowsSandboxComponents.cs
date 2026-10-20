namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         Windows 沙箱组件与 home 的可信描述，来自独立用户/宿主设置，项目配置不能覆盖。
///         严格模式 setup v5，TCP 环回模式 v6；IPC 均为 v6，版本或账户模式不符时拒绝执行。
///     </para>
///     <para>
///         Trusted sandbox components and home from separate user/host settings, never project overrides.
///         Strict mode requires setup v5 and TCP loopback v6; both use IPC v6 and reject mismatched
///         versions or account modes.
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
    public const byte   SetupVersion               = 5;
    public const byte   TcpLoopbackSetupVersion    = 6;
    public const byte   IpcVersion                 = 6;
    public const string DefaultOfflineUsername     = "TinyHarnessOffline";
    public const string DefaultOnlineUsername      = "TinyHarnessOnline";
    public const string TcpLoopbackOfflineUsername = "TinyHarnessTcpOff";
    public const string TcpLoopbackOnlineUsername  = "TinyHarnessTcpOn";

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

    public const string LegacyCodexOnlineUsername = "CodexSandboxOnline";

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
    public string UsersFilePath     => Path.Combine(SandboxHome, ".sandbox-secrets", "sandbox_users.json");
    public string CapabilitySidPath => Path.Combine(SandboxHome, "cap_sid");

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

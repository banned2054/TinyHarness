namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     Windows 沙箱组件与 home 的可信描述：setup/runner 绝对路径、sandbox home、账户名和
///     硬版本（SETUP_VERSION=5、IPC_PROTOCOL_VERSION=6）。来源是独立可信的用户/宿主设置，
///     不进入可被目标项目覆盖的普通配置链；版本不匹配时拒绝执行。本 build 面向沙箱的
///     TinyHarness 独立命名空间 fork：setup/runner 二进制文件名、payload 字段名、DPAPI 与
///     IPC 协议不变，但机器级命名（账户、组、防火墙规则、WFP 对象、桌面名前缀）改为
///     TinyHarness 专属，仍带旧 Codex 用户名的 marker/凭据一律拒绝复用。
///     Trusted description of the Windows sandbox components and home: absolute
///     setup/runner paths, the sandbox home, account names, and the pinned hard
///     versions (SETUP_VERSION=5, IPC_PROTOCOL_VERSION=6). It comes from separate
///     trusted user/host settings, never the target project's configuration
///     chain; version mismatches refuse to execute. This build targets the
///     TinyHarness independent-namespace fork of the sandbox: the setup/runner
///     binary file names, payload field names, DPAPI, and IPC protocol are
///     unchanged, but the machine-wide names (accounts, group, firewall rules,
///     WFP objects, desktop-name prefix) are TinyHarness-owned, and markers or
///     credentials still carrying the legacy Codex usernames are never reused.
/// </summary>
public sealed record WindowsSandboxComponents
{
    public const byte   SetupVersion           = 5;
    public const byte   IpcVersion             = 6;
    public const string DefaultOfflineUsername = "TinyHarnessOffline";
    public const string DefaultOnlineUsername  = "TinyHarnessOnline";

    /// <summary>
    ///     旧 Codex 发布版的沙箱账户名；marker/凭据仍使用这些名字时必须拒绝复用并重新
    ///     provisioning，绝不能与 Codex 安装争用同一套账户/密码。
    ///     The sandbox account names of the legacy Codex release; markers or
    ///     credentials still using them must be refused and re-provisioned
    ///     instead of fighting with a Codex install over the same accounts.
    /// </summary>
    public const string LegacyCodexOfflineUsername = "CodexSandboxOffline";
    public const string LegacyCodexOnlineUsername  = "CodexSandboxOnline";

    public required string SetupExecutablePath { get; init; }

    public required string RunnerExecutablePath { get; init; }

    /// <summary>
    ///     sandbox home（codex_home）：cap_sid、.sandbox、.sandbox-secrets、.sandbox-bin 所在目录。
    ///     The sandbox home (codex_home): the directory holding cap_sid, .sandbox,
    ///     .sandbox-secrets, and .sandbox-bin.
    /// </summary>
    public required string SandboxHome { get; init; }

    public string OfflineUsername { get; init; } = DefaultOfflineUsername;

    public string OnlineUsername { get; init; } = DefaultOnlineUsername;

    /// <summary>
    ///     独立固定的默认 sandbox home（每用户 %LOCALAPPDATA%\tinyharness\windows-sandbox-home）。
    ///     home 持有的是机器绑定状态（DPAPI LocalMachine 凭据、本机账户 SID），因此放 Local 而非
    ///     Roaming；它与 Codex 发布版的 home 完全独立。
    ///     The independent fixed default sandbox home (per-user
    ///     %LOCALAPPDATA%\tinyharness\windows-sandbox-home). The home holds
    ///     machine-bound state (DPAPI LocalMachine credentials, machine account
    ///     SIDs), so it lives under Local rather than Roaming; it is fully
    ///     independent of the Codex release's home.
    /// </summary>
    public static string DefaultSandboxHome => Path.Combine(
                                                              Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                              "tinyharness", "windows-sandbox-home");

    /// <summary>
    ///     解析 sandbox home 设置：空白时使用独立固定默认 home；显式设置时必须是绝对路径
    ///     （否则抛带上下文异常，fail closed，不回退）。
    ///     Resolves the sandbox home setting: blank yields the independent fixed
    ///     default home; an explicit value must be an absolute path (otherwise a
    ///     contextual exception is thrown — fail closed, no fallback).
    /// </summary>
    public static string ResolveSandboxHome(string? configuredSandboxHome)
    {
        if (string.IsNullOrWhiteSpace(configuredSandboxHome)) return DefaultSandboxHome;

        return Path.IsPathFullyQualified(configuredSandboxHome)
            ? configuredSandboxHome.Trim()
            : throw new InvalidOperationException(
                                                 $"The trusted Windows sandbox setting 'sandboxHome' must be a fully qualified absolute path: '{configuredSandboxHome}'.");
    }

    /// <summary>
    ///     是否为旧 Codex 发布版的沙箱账户名（SAM 名大小写不敏感比较）。
    ///     Whether this is a legacy Codex-release sandbox account name
    ///     (case-insensitive SAM-name comparison).
    /// </summary>
    public static bool IsLegacyCodexAccountName(string? username)
    {
        return string.Equals(username, LegacyCodexOfflineUsername, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(username, LegacyCodexOnlineUsername, StringComparison.OrdinalIgnoreCase);
    }

    public string SandboxDirectory => Path.Combine(SandboxHome, ".sandbox");

    public string MarkerPath => Path.Combine(SandboxDirectory, "setup_marker.json");

    public string SetupErrorPath => Path.Combine(SandboxDirectory, "setup_error.json");

    public string UsersFilePath => Path.Combine(SandboxHome, ".sandbox-secrets", "sandbox_users.json");

    public string CapabilitySidPath => Path.Combine(SandboxHome, "cap_sid");
}

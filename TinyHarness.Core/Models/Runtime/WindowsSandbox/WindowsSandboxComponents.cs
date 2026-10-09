namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
/// Windows 沙箱组件与 home 的可信描述：setup/runner 绝对路径、sandbox home、账户名和
/// 硬版本（SETUP_VERSION=5、IPC_PROTOCOL_VERSION=6）。来源是独立可信的用户/宿主设置，
/// 不进入可被目标项目覆盖的普通配置链；版本不匹配时拒绝执行。
///
/// Trusted description of the Windows sandbox components and home: absolute
/// setup/runner paths, the sandbox home, account names, and the pinned hard
/// versions (SETUP_VERSION=5, IPC_PROTOCOL_VERSION=6). It comes from separate
/// trusted user/host settings, never the target project's configuration
/// chain; version mismatches refuse to execute.
/// </summary>
public sealed record WindowsSandboxComponents
{
    public const byte SetupVersion    = 5;
    public const byte IpcVersion      = 6;
    public const string DefaultOfflineUsername = "CodexSandboxOffline";
    public const string DefaultOnlineUsername  = "CodexSandboxOnline";

    public required string SetupExecutablePath { get; init; }

    public required string RunnerExecutablePath { get; init; }

    /// <summary>
    /// sandbox home（codex_home）：cap_sid、.sandbox、.sandbox-secrets、.sandbox-bin 所在目录。
    /// The sandbox home (codex_home): the directory holding cap_sid, .sandbox,
    /// .sandbox-secrets, and .sandbox-bin.
    /// </summary>
    public required string SandboxHome { get; init; }

    public string OfflineUsername { get; init; } = DefaultOfflineUsername;

    public string OnlineUsername { get; init; } = DefaultOnlineUsername;

    public string SandboxDirectory => Path.Combine(SandboxHome, ".sandbox");

    public string MarkerPath => Path.Combine(SandboxDirectory, "setup_marker.json");

    public string SetupErrorPath => Path.Combine(SandboxDirectory, "setup_error.json");

    public string UsersFilePath => Path.Combine(SandboxHome, ".sandbox-secrets", "sandbox_users.json");

    public string CapabilitySidPath => Path.Combine(SandboxHome, "cap_sid");
}

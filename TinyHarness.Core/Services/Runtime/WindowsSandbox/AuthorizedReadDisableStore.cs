using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         C2 授权执行禁用状态的成因分类：区分“ACL 恢复失败（进程已确认退出，可通过后续成功恢复刷新自动解禁）”
///         与“进程/整树退出未知（旧授权进程可能仍存活，不可仅凭 ACL 刷新解禁）”。
///     </para>
///     <para>
///         The cause category of a persisted C2 disable state: distinguishes "ACL restore failed (prior process
///         confirmed exited; auto-clearable by a later successful restore refresh)" from "process/whole-tree exit
///         uncertain (a prior authorized process may still be alive; cannot be cleared by ACL refresh alone)".
///     </para>
/// </summary>
public enum AuthorizedReadDisableKind
{
    /// <summary>未知、旧版或损坏的状态文件：按保守策略拒绝仅凭 ACL 刷新自动解禁。</summary>
    Unknown = 0,

    /// <summary>ACL 恢复刷新失败（进程已确认退出）：允许在授权窗口内经恢复刷新成功清除。</summary>
    RestoreFailed = 1,

    /// <summary>进程或整树退出未知：不可仅凭 ACL 刷新解禁，须等系统重启（BootId 变化）或显式重置。</summary>
    ReclamationUncertain = 2,

    /// <summary>
    ///     <para>
    ///         打开授权窗口前预写的守卫状态（WAL）：在窗口锁外遇到时可能属于正常并发持有者，应排队等锁；
    ///         持窗口锁后若仍存在，则证明前持有者异常崩溃且退出未知，按与 <see cref="ReclamationUncertain" /> 相同的
    ///         不可自动解禁边界处理。
    ///     </para>
    ///     <para>
    ///         Pre-window write-ahead guard state (WAL): outside the window lock it may belong to a healthy
    ///         concurrent holder so callers queue on the lock; if still present once the lock is held, it proves
    ///         a prior holder crashed with the window open and enforces the same non-auto-recoverable boundary as
    ///         <see cref="ReclamationUncertain" />.
    ///     </para>
    /// </summary>
    WindowOpen = 3
}

/// <summary>
///     <para>
///         单条持久化禁用记录的约束快照（原因、成因分类、开机周期标识、产生禁用的源 <c>sandboxHome</c> 及
///         其 64 位卷序列号与 128 位文件 ID）。
///     </para>
///     <para>
///         One persisted disable record's constraint snapshot (reason, cause kind, boot identifier, the source
///         <c>sandboxHome</c> that created it, and its 64-bit volume serial number and 128-bit file identifier).
///     </para>
/// </summary>
public sealed record AuthorizedReadDisableEntry(
    string                    Reason,
    AuthorizedReadDisableKind Kind,
    int?                      BootId,
    string?                   SourceSandboxHome,
    ulong?                    SourceHomeVolumeSerial = null,
    string?                   SourceHomeFileId       = null);

/// <summary>
///     <para>
///         已读取的授权执行禁用状态快照（含禁用标志、汇总原因、最严格类别、开机周期标识、源 home 与
///         128 位目录身份及全部活动约束条目 <see cref="Entries" />）。双读合并时保留每一份记录的独立约束，
///         只有全部条目都满足解禁条件才允许恢复。
///     </para>
///     <para>
///         A read snapshot of the authorized-execution disable state (flag, summary reason, strictest kind, boot
///         identifier, source home and 128-bit directory identity, and every active constraint entry in
///         <see cref="Entries" />). Dual-read merging preserves every record's independent constraints so
///         recovery is allowed only when every entry satisfies its own clearance rules.
///     </para>
/// </summary>
public sealed record AuthorizedReadDisableState(
    bool                                       Disabled,
    string?                                    Reason,
    AuthorizedReadDisableKind                  Kind,
    int?                                       BootId,
    string?                                    SourceSandboxHome      = null,
    IReadOnlyList<AuthorizedReadDisableEntry>? Entries                = null,
    ulong?                                     SourceHomeVolumeSerial = null,
    string?                                    SourceHomeFileId       = null)
{
    public static readonly AuthorizedReadDisableState NotDisabled =
        new(false, null, AuthorizedReadDisableKind.Unknown, null, null, [], null, null);

    public IReadOnlyList<AuthorizedReadDisableEntry> ActiveEntries =>
        Entries is { Count: > 0 }
            ? Entries
            : Disabled
                ? [new AuthorizedReadDisableEntry(Reason ?? "the persisted disable state carries no reason",
                                                  Kind, BootId, SourceSandboxHome,
                                                  SourceHomeVolumeSerial, SourceHomeFileId)]
                : [];

    public static AuthorizedReadDisableState DisabledState(
        string                    reason,
        AuthorizedReadDisableKind kind,
        int?                      bootId                 = null,
        string?                   sourceSandboxHome      = null,
        ulong?                    sourceHomeVolumeSerial = null,
        string?                   sourceHomeFileId       = null)
    {
        var entry = new AuthorizedReadDisableEntry(
            reason, kind, bootId, sourceSandboxHome, sourceHomeVolumeSerial, sourceHomeFileId);
        return new AuthorizedReadDisableState(
            true, reason, kind, bootId, sourceSandboxHome, [entry],
            sourceHomeVolumeSerial, sourceHomeFileId);
    }

    /// <summary>
    ///     <para>仅当旧版按 home 存放的状态文件自身贡献了禁用记录时保存其规范路径；不进入序列化。</para>
    ///     <para>
    ///         The canonical path of the legacy per-home state file, populated only when that legacy file
    ///         itself contributed a disabled entry; excluded from serialization.
    ///     </para>
    /// </summary>
    internal string? LegacyStatePath { get; init; }
}

/// <summary>
///     <para>
///         单次授权窗口在开启时锁定的物理源 <c>sandboxHome</c> 身份（规范最终路径及 Windows 64 位卷序列号 /
///         128 位文件 ID），用于把窗口前守卫、后端授权刷新、退出恢复刷新与失败禁用落盘绑定到同一源目录。
///     </para>
///     <para>
///         The physical originating <c>sandboxHome</c> identity captured when an authorization window opens
///         (canonical final path plus Windows 64-bit volume serial and 128-bit file ID), binding the
///         pre-window guard, backend authorized refresh, exit restore refresh, and disable persistence to the
///         exact same originating directory.
///     </para>
/// </summary>
internal sealed record AuthorizedReadWindowHome(
    string  CanonicalPath,
    ulong?  VolumeSerialNumber,
    string? FileIdHex);

/// <summary>
///     <para>
///         经 <see cref="AsyncLocal{T}" /> 向当前授权执行链路内的后端预检传递已锁定的窗口源目录身份，
///         不改变公开 <see cref="TinyHarness.Core.Services.Runtime.IProcessExecutionBackend" /> 签名。
///     </para>
///     <para>
///         Flows the locked window home identity through an <see cref="AsyncLocal{T}" /> to the inner
///         backend preflight during an authorized execution without altering the public
///         <see cref="TinyHarness.Core.Services.Runtime.IProcessExecutionBackend" /> contract.
///     </para>
/// </summary>
internal static class AuthorizedReadWindowContext
{
    private static readonly AsyncLocal<AuthorizedReadWindowHome?> CurrentHome = new();

    internal static AuthorizedReadWindowHome? Current => CurrentHome.Value;

    internal static IDisposable Enter(AuthorizedReadWindowHome windowHome)
    {
        ArgumentNullException.ThrowIfNull(windowHome);
        var previous = CurrentHome.Value;
        CurrentHome.Value = windowHome;
        return new Scope(previous);
    }

    private sealed class Scope(AuthorizedReadWindowHome? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) CurrentHome.Value = previous;
        }
    }
}

/// <summary>
///     <para>
///         授权执行禁用状态的持久化口：恢复 refresh 失败或进程回收结果未知时写入，后续授权执行
///         在入口及持窗口锁后读取并阻断；仅在满足对应解禁条件并经恢复刷新成功后清除。实现必须写入真实用户
///         可写、沙箱组不可写的受保护位置，防止沙箱内命令自解禁。
///     </para>
///     <para>
///         The persistence port for the authorized-execution disable state: written when a restore
///         refresh fails or process reclamation is unknown, read at entry and again after acquiring the
///         window lock to block later authorized executions, and cleared only once the matching recovery
///         condition and restore refresh succeed. Implementations must write to a location the real user can
///         write while the sandbox group cannot, so sandboxed commands can never un-disable themselves.
///     </para>
/// </summary>
public interface IAuthorizedReadDisableStore
{
    /// <summary>读取当前禁用状态；未禁用返回 false。 Reads the current disable state; false when not disabled.</summary>
    Task<(bool Disabled, string? Reason)> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     <para>读取包含成因分类与 BootId 的完整禁用状态；未重写时按保守 <see cref="AuthorizedReadDisableKind.Unknown" /> 返回。</para>
    ///     <para>
    ///         Reads the full disable state including cause kind and BootId; defaults conservatively to
    ///         <see cref="AuthorizedReadDisableKind.Unknown" /> when not overridden.
    ///     </para>
    /// </summary>
    async Task<AuthorizedReadDisableState> ReadStateAsync(CancellationToken cancellationToken)
    {
        var (disabled, reason) = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return disabled
            ? AuthorizedReadDisableState.DisabledState(
                reason ?? "the persisted disable state carries no reason", AuthorizedReadDisableKind.Unknown)
            : AuthorizedReadDisableState.NotDisabled;
    }

    internal Task<AuthorizedReadDisableState> ReadStateAsync(
        AuthorizedReadWindowHome? windowHome, CancellationToken cancellationToken) =>
        ReadStateAsync(cancellationToken);

    /// <summary>以给定原因持久化禁用状态（缺省为 ACL 恢复失败类别）。 Persists the disable state with the given reason (defaulting to restore-failed).</summary>
    Task DisableAsync(string reason, CancellationToken cancellationToken);

    /// <summary>
    ///     <para>按明确成因分类与可选 BootId 持久化禁用状态。</para>
    ///     <para>Persists the disable state with an explicit cause kind and optional BootId.</para>
    /// </summary>
    Task DisableAsync(
        string reason, AuthorizedReadDisableKind kind, int? bootId, CancellationToken cancellationToken) =>
        DisableAsync(reason, cancellationToken);

    internal Task DisableAsync(
        string                    reason,
        AuthorizedReadDisableKind kind,
        int?                      bootId,
        AuthorizedReadWindowHome? originatingHome,
        CancellationToken         cancellationToken) =>
        DisableAsync(reason, kind, bootId, cancellationToken);

    /// <summary>清除禁用状态（仅在满足解禁条件且恢复刷新成功后调用）。 Clears the disable state (only after the recovery condition and restore refresh succeed).</summary>
    Task ClearAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     <para>仅清除共享守卫文件（不触碰任何按 home 存放的旧版状态文件）。</para>
    ///     <para>Clears only the shared guard file without touching any per-home legacy state file.</para>
    /// </summary>
    internal Task ClearSharedGuardAsync(CancellationToken cancellationToken) =>
        ClearAsync(cancellationToken);

    /// <summary>
    ///     <para>清除共享状态文件，并仅在传入已核验的旧版状态文件路径时一并清除该文件。</para>
    ///     <para>
    ///         Clears the shared state file and, only when a verified legacy state path is provided, clears
    ///         that exact legacy file without deriving paths from a mutable home alias.
    ///     </para>
    /// </summary>
    internal Task ClearRecoveredStateAsync(
        string? verifiedLegacyStatePath, CancellationToken cancellationToken) =>
        ClearAsync(cancellationToken);
}

/// <summary>
///     <para>
///         <see cref="IAuthorizedReadDisableStore" /> 的文件实现：默认写入同一授权组共享的受保护目录
///         （<see cref="DefaultSharedStateDirectory" />），覆盖标准模式、TCP 环回模式与自定义 home，
///         同时兼容双读旧版每 home 凭据目录下的 <c>authorized_read_state.json</c> 并取最严格状态。
///         仅明确返回 <see cref="FileNotFoundException" />/<see cref="DirectoryNotFoundException" /> 才视为未禁用；
///         权限拒绝、I/O 故障、JSON 损坏或未明确断言禁用一律按 <see cref="AuthorizedReadDisableKind.Unknown" />
///         fail-closed。
///     </para>
///     <para>
///         The file-backed implementation of <see cref="IAuthorizedReadDisableStore" />: by default writes to
///         the shared protected directory (<see cref="DefaultSharedStateDirectory" />) for the authorization
///         group across standard mode, TCP loopback mode, and custom homes, while also reading any legacy
///         per-home <c>authorized_read_state.json</c> under the credentials directory and taking the stricter
///         state. Only an explicit <see cref="FileNotFoundException" /> or <see cref="DirectoryNotFoundException" />
///         means not disabled; access denials, I/O errors, corrupt JSON, or missing <c>disabled=true</c> fail
///         closed as <see cref="AuthorizedReadDisableKind.Unknown" />.
///     </para>
/// </summary>
public sealed class AuthorizedReadDisableFile : IAuthorizedReadDisableStore
{
    private readonly WindowsSandboxComponents _components;
    private readonly string                   _stateDirectory;

    public AuthorizedReadDisableFile(WindowsSandboxComponents components, string? sharedStateDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(components);
        _components     = components;
        _stateDirectory = string.IsNullOrWhiteSpace(sharedStateDirectory)
            ? DefaultSharedStateDirectory
            : sharedStateDirectory;
    }

    /// <summary>
    ///     <para>
    ///         同授权组共享禁用状态的默认目录（当前用户 <c>%LOCALAPPDATA%\TinyHarness\authorized-read-state</c>）。
    ///         部署范围限定为同一 Windows 用户下的全部 home（标准模式、TCP 环回模式与自定义 home）；
    ///         同机其他 Windows 用户拥有独立的 <c>LocalAppData</c>，跨用户机器级共享须由提权 provisioning
    ///         另行建立并授权公共目录后迁移。
    ///     </para>
    ///     <para>
    ///         The default directory shared across homes using the same authorization group
    ///         (<c>%LOCALAPPDATA%\TinyHarness\authorized-read-state</c> for the current user). The scope is all
    ///         homes under the same Windows user (standard mode, TCP loopback mode, and custom homes); other
    ///         Windows users on the same machine have separate <c>LocalAppData</c> roots, so cross-user machine
    ///         sharing requires elevated provisioning of a shared machine directory and migration.
    ///     </para>
    /// </summary>
    public static string DefaultSharedStateDirectory
    {
        get
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir)) baseDir = Path.GetTempPath();
            return Path.Combine(baseDir, "TinyHarness", "authorized-read-state");
        }
    }

    /// <summary>当前生效的共享状态文件路径。 The active shared state file path.</summary>
    public string StatePath => Path.Combine(_stateDirectory, "authorized_read_state.json");

    /// <summary>旧版按 home 存放的凭据目录状态路径（兼容双读与清理）。 The legacy per-home state path kept for dual-read and cleanup.</summary>
    internal string LegacyHomeStatePath => Path.Combine(_components.CredentialsDirectory, "authorized_read_state.json");

    private bool HasDistinctLegacyPath =>
        !string.Equals(Path.GetFullPath(StatePath), Path.GetFullPath(LegacyHomeStatePath),
                       StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<(bool Disabled, string? Reason)> ReadAsync(CancellationToken cancellationToken)
    {
        var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
        return (state.Disabled, state.Reason);
    }

    /// <inheritdoc />
    public Task<AuthorizedReadDisableState> ReadStateAsync(CancellationToken cancellationToken) =>
        ReadStateCoreAsync(windowHome: null, cancellationToken);

    Task<AuthorizedReadDisableState> IAuthorizedReadDisableStore.ReadStateAsync(
        AuthorizedReadWindowHome? windowHome, CancellationToken cancellationToken) =>
        ReadStateCoreAsync(windowHome, cancellationToken);

    private async Task<AuthorizedReadDisableState> ReadStateCoreAsync(
        AuthorizedReadWindowHome? windowHome, CancellationToken cancellationToken)
    {
        var primary = await ReadSingleFileAsync(StatePath, defaultIdentity: null, defaultSourceSandboxHome: null,
                                                cancellationToken)
                         .ConfigureAwait(false);
        string?                                                        defaultLegacyHome;
        (string FinalPath, ulong VolumeSerialNumber, string FileIdHex)? defaultLegacyIdentity;
        string                                                         legacyStatePath;
        if (windowHome is not null)
        {
            // 持窗口锁后已锁定物理源目录：直接由 windowHome 推导旧版状态路径与默认来源身份，不再重解别名。
            defaultLegacyHome = windowHome.CanonicalPath;
            defaultLegacyIdentity = windowHome.VolumeSerialNumber is { } vol &&
                                    !string.IsNullOrWhiteSpace(windowHome.FileIdHex)
                ? (windowHome.CanonicalPath, vol, windowHome.FileIdHex.Trim())
                : null;
            legacyStatePath = Path.Combine(windowHome.CanonicalPath, ".sandbox-secrets", "authorized_read_state.json");
        }
        else
        {
            // 优先从已捕获的规范物理路径推导旧版状态文件路径，避免在读阶段经可变别名误触其他 home。
            defaultLegacyIdentity = TryCaptureCanonicalHomeIdentity(_components.SandboxHome);
            defaultLegacyHome = defaultLegacyIdentity?.FinalPath
                             ?? TryResolveCanonicalHomePath(_components.SandboxHome, allowMissing: true);
            legacyStatePath = !string.IsNullOrWhiteSpace(defaultLegacyHome)
                ? Path.Combine(defaultLegacyHome, ".sandbox-secrets", "authorized_read_state.json")
                : LegacyHomeStatePath;
        }

        if (string.Equals(Path.GetFullPath(StatePath), Path.GetFullPath(legacyStatePath),
                          StringComparison.OrdinalIgnoreCase))
            return primary;

        // 旧版状态文件位于当前 home 的凭据目录下，仅当缺少 source_sandbox_home 时才以当前 home 作为默认来源身份。
        var legacy = await ReadSingleFileAsync(
                         legacyStatePath,
                         defaultIdentity: defaultLegacyIdentity,
                         defaultSourceSandboxHome: defaultLegacyHome,
                         cancellationToken)
                        .ConfigureAwait(false);
        if (legacy.Disabled) legacy = legacy with { LegacyStatePath = legacyStatePath };
        return MergeStates(primary, legacy);
    }

    /// <inheritdoc />
    public Task DisableAsync(string reason, CancellationToken cancellationToken) =>
        DisableAsync(reason, AuthorizedReadDisableKind.RestoreFailed, null, cancellationToken);

    /// <inheritdoc />
    public Task DisableAsync(
        string reason, AuthorizedReadDisableKind kind, int? bootId, CancellationToken cancellationToken) =>
        DisableCoreAsync(reason, kind, bootId, originatingHome: null, cancellationToken);

    Task IAuthorizedReadDisableStore.DisableAsync(
        string                    reason,
        AuthorizedReadDisableKind kind,
        int?                      bootId,
        AuthorizedReadWindowHome? originatingHome,
        CancellationToken         cancellationToken) =>
        DisableCoreAsync(reason, kind, bootId, originatingHome, cancellationToken);

    private async Task DisableCoreAsync(
        string                    reason,
        AuthorizedReadDisableKind kind,
        int?                      bootId,
        AuthorizedReadWindowHome? originatingHome,
        CancellationToken         cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        string? sourceHome;
        ulong?  sourceVolume;
        string? sourceFileId;
        if (originatingHome is not null)
        {
            // 已在开窗时捕获的源目录身份直接落盘，绝不按可能已改指的别名重新解析。
            sourceHome   = TrimStoredTrailingSeparators(originatingHome.CanonicalPath);
            sourceVolume = originatingHome.VolumeSerialNumber;
            sourceFileId = string.IsNullOrWhiteSpace(originatingHome.FileIdHex)
                ? null
                : originatingHome.FileIdHex.Trim();
        }
        else
        {
            var captured = TryCaptureCanonicalHomeIdentity(_components.SandboxHome);
            sourceHome   = captured?.FinalPath
                        ?? TryResolveCanonicalHomePath(_components.SandboxHome, allowMissing: true);
            sourceVolume = captured?.VolumeSerialNumber;
            sourceFileId = captured?.FileIdHex;
        }

        var state = new SandboxAuthorizedReadState
        {
            Disabled      = true,
            Reason        = reason,
            DisabledAtUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Kind = kind switch
            {
                AuthorizedReadDisableKind.WindowOpen           => SandboxAuthorizedReadState.WindowOpenKind,
                AuthorizedReadDisableKind.RestoreFailed        => SandboxAuthorizedReadState.RestoreFailedKind,
                AuthorizedReadDisableKind.ReclamationUncertain => SandboxAuthorizedReadState.ReclamationUncertainKind,
                _                                              => null
            },
            BootId                 = bootId,
            SourceSandboxHome      = sourceHome,
            SourceHomeVolumeSerial = sourceVolume,
            SourceHomeFileId       = sourceFileId
        };
        Directory.CreateDirectory(_stateDirectory);
        var json = JsonSerializer.Serialize(state, WindowsSandboxJsonContext.Default.SandboxAuthorizedReadState);
        // 先写同目录临时文件再原子覆盖，避免并发读取看到半截 JSON；Windows 优先 POSIX 语义替换，
        // 防止锁外读端持有的 FileShare.Delete 句柄阻塞 MoveFileExW，并对瞬态共享冲突做有界重试。
        var tempPath = Path.Combine(_stateDirectory, $"authorized_read_state.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
            await ReplaceStateFileAsync(tempPath, StatePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteTempFile(tempPath);
        }
    }

    private static readonly int[] ReplaceRetryDelaysMs = [15, 30, 60, 100, 150];

    private static async Task ReplaceStateFileAsync(
        string tempPath, string destinationPath, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(tempPath, destinationPath, overwrite: true);
            return;
        }

        var usePosixRename = true;
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (usePosixRename)
            {
                if (SandboxRuntimeTempSlotNative.TryReplaceFilePosix(tempPath, destinationPath, out var win32Error))
                    return;

                if (win32Error is SandboxRuntimeTempSlotNative.ErrorInvalidFunction or
                    SandboxRuntimeTempSlotNative.ErrorNotSupported or
                    SandboxRuntimeTempSlotNative.ErrorInvalidParameter)
                {
                    // 卷/文件系统不支持 POSIX 重命名时降级到标准覆盖移动，并沿用同一有界重试循环。
                    usePosixRename = false;
                }
                else if (IsTransientReplaceWin32Error(win32Error) && attempt < ReplaceRetryDelaysMs.Length)
                {
                    await Task.Delay(ReplaceRetryDelaysMs[attempt], cancellationToken).ConfigureAwait(false);
                    continue;
                }
                else
                {
                    throw new IOException(
                        $"Cannot atomically replace the authorized-read disable state file '{destinationPath}' " +
                        $"(Win32 error {win32Error}).",
                        new Win32Exception(win32Error));
                }
            }

            try
            {
                File.Move(tempPath, destinationPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (IsTransientReplaceException(ex) && attempt < ReplaceRetryDelaysMs.Length)
            {
                await Task.Delay(ReplaceRetryDelaysMs[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsTransientReplaceWin32Error(int win32Error) =>
        win32Error is SandboxRuntimeTempSlotNative.ErrorSharingViolation or
            SandboxRuntimeTempSlotNative.ErrorLockViolation or
            SandboxRuntimeTempSlotNative.ErrorAccessDenied;

    [SupportedOSPlatform("windows")]
    private static bool IsTransientReplaceException(Exception ex)
    {
        if (ex is UnauthorizedAccessException) return true;
        if (ex is not IOException io) return false;
        var win32Error = io.HResult & 0xFFFF;
        return IsTransientReplaceWin32Error(win32Error);
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteStateFile(StatePath);
        if (HasDistinctLegacyPath) DeleteStateFile(LegacyHomeStatePath);
        return Task.CompletedTask;
    }

    Task IAuthorizedReadDisableStore.ClearSharedGuardAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteStateFile(StatePath);
        return Task.CompletedTask;
    }

    Task IAuthorizedReadDisableStore.ClearRecoveredStateAsync(
        string? verifiedLegacyStatePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteStateFile(StatePath);
        if (!string.IsNullOrWhiteSpace(verifiedLegacyStatePath) &&
            !string.Equals(Path.GetFullPath(verifiedLegacyStatePath), Path.GetFullPath(StatePath),
                           StringComparison.OrdinalIgnoreCase))
            DeleteStateFile(verifiedLegacyStatePath);

        return Task.CompletedTask;
    }

    private static async Task<AuthorizedReadDisableState> ReadSingleFileAsync(
        string                                             path,
        (string FinalPath, ulong VolumeSerialNumber, string FileIdHex)? defaultIdentity,
        string?                                            defaultSourceSandboxHome,
        CancellationToken                                  cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // 不使用 File.Exists 预判（会把权限拒绝/I/O 故障静默判为缺失），也不用 File.ReadAllTextAsync
            // （默认 FileShare.Read 不含 Delete，会在窗口锁外阻塞持锁者的 File.Delete / File.Move 守卫清理）。
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader  = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var       content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var state = JsonSerializer.Deserialize(content, WindowsSandboxJsonContext.Default.SandboxAuthorizedReadState) ??
                        throw new JsonException("The persisted disable state is null.");
            // 已存在的状态必须明确断言禁用；false 或 null 同样按损坏状态处理，读取不得解除阻断。
            if (!state.Disabled)
                throw new JsonException("The persisted disable state does not assert disabled=true.");

            var kind = state.Kind switch
            {
                SandboxAuthorizedReadState.WindowOpenKind           => AuthorizedReadDisableKind.WindowOpen,
                SandboxAuthorizedReadState.RestoreFailedKind        => AuthorizedReadDisableKind.RestoreFailed,
                SandboxAuthorizedReadState.ReclamationUncertainKind => AuthorizedReadDisableKind.ReclamationUncertain,
                _                                                   => AuthorizedReadDisableKind.Unknown
            };
            // 读取已持久化的来源路径时绝不再做二次磁盘链接解析，原样保留已存规范路径与 128 位目录身份。
            string? sourceHome;
            ulong?  sourceVolume;
            string? sourceFileId;
            if (!string.IsNullOrWhiteSpace(state.SourceSandboxHome))
            {
                sourceHome   = TrimStoredTrailingSeparators(state.SourceSandboxHome);
                sourceVolume = state.SourceHomeVolumeSerial;
                sourceFileId = string.IsNullOrWhiteSpace(state.SourceHomeFileId)
                    ? null
                    : state.SourceHomeFileId.Trim();
            }
            else if (kind != AuthorizedReadDisableKind.Unknown)
            {
                sourceHome   = defaultIdentity?.FinalPath ?? defaultSourceSandboxHome;
                sourceVolume = defaultIdentity?.VolumeSerialNumber;
                sourceFileId = defaultIdentity?.FileIdHex;
            }
            else
            {
                sourceHome   = null;
                sourceVolume = null;
                sourceFileId = null;
            }

            return AuthorizedReadDisableState.DisabledState(
                state.Reason ?? "the persisted disable state carries no reason", kind, state.BootId, sourceHome,
                sourceVolume, sourceFileId);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return AuthorizedReadDisableState.NotDisabled;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 状态文件不可读按保守 Unknown 禁用处理（fail closed），不能仅凭 ACL 刷新自动解禁。
            return AuthorizedReadDisableState.DisabledState(
                $"the disable state at '{path}' is unreadable ({ex.GetType().Name})",
                AuthorizedReadDisableKind.Unknown);
        }
    }

    internal static AuthorizedReadDisableState MergeStates(
        AuthorizedReadDisableState primary, AuthorizedReadDisableState legacy)
    {
        if (!primary.Disabled) return legacy;
        if (!legacy.Disabled) return primary;

        // 保留每一份活动记录的独立退出与源 home 约束（合取语义），绝不按静态类别丢弃另一份记录的 BootId 或源 home。
        static int Severity(AuthorizedReadDisableKind kind) => kind switch
        {
            AuthorizedReadDisableKind.Unknown              => 4,
            AuthorizedReadDisableKind.ReclamationUncertain => 3,
            AuthorizedReadDisableKind.WindowOpen           => 2,
            AuthorizedReadDisableKind.RestoreFailed        => 1,
            _                                              => 4
        };

        var mergedEntries = new List<AuthorizedReadDisableEntry>(
            primary.ActiveEntries.Count + legacy.ActiveEntries.Count);
        mergedEntries.AddRange(primary.ActiveEntries);
        mergedEntries.AddRange(legacy.ActiveEntries);

        var strictestKind = Severity(primary.Kind) >= Severity(legacy.Kind) ? primary.Kind : legacy.Kind;
        var mergedBootId  = primary.BootId == legacy.BootId ? primary.BootId : null;
        var mergedHome = string.Equals(primary.SourceSandboxHome, legacy.SourceSandboxHome,
                                       StringComparison.OrdinalIgnoreCase)
            ? primary.SourceSandboxHome
            : null;
        var mergedVolume = mergedHome is not null && primary.SourceHomeVolumeSerial == legacy.SourceHomeVolumeSerial
            ? primary.SourceHomeVolumeSerial
            : null;
        var mergedFileId = mergedHome is not null &&
                           string.Equals(primary.SourceHomeFileId, legacy.SourceHomeFileId,
                                         StringComparison.OrdinalIgnoreCase)
            ? primary.SourceHomeFileId
            : null;
        return new AuthorizedReadDisableState(
            true, $"{primary.Reason}; {legacy.Reason}", strictestKind, mergedBootId, mergedHome, mergedEntries,
            mergedVolume, mergedFileId)
        {
            LegacyStatePath = legacy.LegacyStatePath
        };
    }

    /// <summary>
    ///     <para>
    ///         在 Windows 上跟随 <c>sandboxHome</c> 自身的链接打开单个目录句柄，并从同一句柄原子捕获规范
    ///         物理最终路径、64 位卷序列号与 128 位文件 ID（32 位十六进制），消除路径级 TOCTOU；非
    ///         Windows 或捕获失败返回 null。
    ///     </para>
    ///     <para>
    ///         Opens a single directory handle on Windows following any link on <c>sandboxHome</c> itself and
    ///         atomically captures the canonical physical final path, 64-bit volume serial number, and 128-bit
    ///         file ID (32-char hex) from that same handle to eliminate path-level TOCTOU; returns null on
    ///         non-Windows or on any capture failure.
    ///     </para>
    /// </summary>
    internal static (string FinalPath, ulong VolumeSerialNumber, string FileIdHex)? TryCaptureCanonicalHomeIdentity(
        string sandboxHome)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(sandboxHome)) return null;

        try
        {
            var fullHome = NormalizeHomePath(sandboxHome);
            using var handle = SandboxRuntimeTempSlotNative.OpenDirectory(
                fullHome,
                SandboxRuntimeTempSlotNative.FileReadAttributes | SandboxRuntimeTempSlotNative.Synchronize,
                SandboxRuntimeTempSlotNative.FileShareAll,
                "sandbox home",
                openReparsePoint: false);
            SandboxRuntimeTempSlotNative.RequireDirectory(handle, fullHome, out _);
            var finalPath = SandboxFinalPathResolver.TryReadFinalPath(handle);
            if (string.IsNullOrWhiteSpace(finalPath)) return null;

            var normalizedFinal = TrimStoredTrailingSeparators(finalPath);
            var identity        = SandboxRuntimeTempSlotIdentity.Capture(handle, normalizedFinal);
            return (normalizedFinal,
                    identity.VolumeSerialNumber,
                    identity.FileId.ToString("x32", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidDataException or IOException
                                      or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static string TrimStoredTrailingSeparators(string path) =>
        path.Length > 3 ? path.TrimEnd('\\', '/') : Path.TrimEndingDirectorySeparator(path);

    /// <summary>
    ///     <para>
    ///         在 Windows 上以含 <c>FILE_LIST_DIRECTORY</c> 且不含 <c>FILE_SHARE_DELETE</c> 的句柄钉住已验证物理
    ///         <c>sandboxHome</c>，并在同一句柄上核对规范最终路径与 64 位卷序列号 / 128 位文件 ID 与期望窗口
    ///         身份（及可选禁用记录集合）一致；失败返回 null 并输出原因。
    ///     </para>
    ///     <para>
    ///         Pins a verified physical <c>sandboxHome</c> on Windows with a handle carrying
    ///         <c>FILE_LIST_DIRECTORY</c> without <c>FILE_SHARE_DELETE</c> and verifies on that same handle
    ///         that its canonical final path, 64-bit volume serial, and 128-bit file ID match the expected
    ///         window home (and optional disable entries); returns null with a reason on mismatch or failure.
    ///     </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static SafeFileHandle? TryPinVerifiedWindowHome(
        AuthorizedReadWindowHome                   expectedHome,
        string                                     phaseLabel,
        out string?                                blockReason,
        IReadOnlyList<AuthorizedReadDisableEntry>? additionalEntries = null)
    {
        ArgumentNullException.ThrowIfNull(expectedHome);
        SafeFileHandle? handle = null;
        try
        {
            // 仅 FILE_READ_ATTRIBUTES 不参与内核 IoCheckShareAccess 共享拒绝，必须带 FILE_LIST_DIRECTORY
            // 才能真正在刷新期间阻断对源目录的删除/重命名与祖先改名。
            handle = SandboxRuntimeTempSlotNative.OpenDirectory(
                expectedHome.CanonicalPath,
                SandboxRuntimeTempSlotNative.FileListDirectory  |
                SandboxRuntimeTempSlotNative.FileReadAttributes |
                SandboxRuntimeTempSlotNative.Synchronize,
                SandboxRuntimeTempSlotNative.FileShareReadWrite,
                $"verified {phaseLabel} sandbox home",
                openReparsePoint: false);
            SandboxRuntimeTempSlotNative.RequireDirectory(handle, expectedHome.CanonicalPath, out _);
            var finalPath = SandboxFinalPathResolver.TryReadFinalPath(handle);
            if (string.IsNullOrWhiteSpace(finalPath))
            {
                handle.Dispose();
                blockReason = $"The pinned {phaseLabel} sandbox home '{expectedHome.CanonicalPath}' could not resolve its final path.";
                return null;
            }

            var normalizedFinal = TrimStoredTrailingSeparators(finalPath);
            var identity        = SandboxRuntimeTempSlotIdentity.Capture(handle, normalizedFinal);
            var fileIdHex       = identity.FileId.ToString("x32", CultureInfo.InvariantCulture);

            if (!string.Equals(normalizedFinal, expectedHome.CanonicalPath, StringComparison.OrdinalIgnoreCase))
            {
                handle.Dispose();
                blockReason = $"The pinned {phaseLabel} sandbox home '{expectedHome.CanonicalPath}' resolved to '{normalizedFinal}' " +
                              "instead of the verified originating home; refusing refresh.";
                return null;
            }

            if (expectedHome.VolumeSerialNumber is not { } expectedVolume ||
                string.IsNullOrWhiteSpace(expectedHome.FileIdHex) ||
                expectedVolume != identity.VolumeSerialNumber ||
                !string.Equals(expectedHome.FileIdHex.Trim(), fileIdHex, StringComparison.OrdinalIgnoreCase))
            {
                handle.Dispose();
                blockReason = $"The pinned {phaseLabel} sandbox home '{expectedHome.CanonicalPath}' " +
                              $"(volume 0x{identity.VolumeSerialNumber:x16}, file 0x{fileIdHex}) no longer matches " +
                              "the captured originating directory identity; refusing refresh.";
                return null;
            }

            if (additionalEntries is not null)
                foreach (var entry in additionalEntries)
                    if (entry.SourceHomeVolumeSerial is not { } sourceVolume ||
                        string.IsNullOrWhiteSpace(entry.SourceHomeFileId) ||
                        sourceVolume != identity.VolumeSerialNumber ||
                        !string.Equals(entry.SourceHomeFileId.Trim(), fileIdHex, StringComparison.OrdinalIgnoreCase))
                    {
                        handle.Dispose();
                        blockReason = $"The pinned {phaseLabel} sandbox home '{expectedHome.CanonicalPath}' " +
                                      $"(volume 0x{identity.VolumeSerialNumber:x16}, file 0x{fileIdHex}) no longer matches " +
                                      "the persisted originating directory identity; refusing refresh.";
                        return null;
                    }

            blockReason = null;
            return handle;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidDataException or IOException
                                      or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            handle?.Dispose();
            blockReason = $"The verified {phaseLabel} sandbox home '{expectedHome.CanonicalPath}' could not be pinned before " +
                          $"refreshing ({ex.GetType().Name}): {ex.Message}";
            return null;
        }
    }

    /// <summary>
    ///     <para>
    ///         刷新返回后、解除守卫或继续执行前仍在门内复核 payload、钉住句柄及新捕获身份均指向同一个
    ///         <paramref name="expectedHome" />；任一失配返回 false 并输出失败原因。
    ///     </para>
    ///     <para>
    ///         Verifies after refresh returns (while still holding the gate and pin handle) that the payload,
    ///         the pinned directory handle, and a fresh identity capture all still match
    ///         <paramref name="expectedHome" />; returns false with a failure reason on any mismatch.
    ///     </para>
    /// </summary>
    internal static bool VerifyPostRefreshWindowHome(
        AuthorizedReadWindowHome                                                  expectedHome,
        SafeFileHandle?                                                           pinnedHomeHandle,
        SandboxSetupPayload                                                       payload,
        Func<string, (string FinalPath, ulong VolumeSerialNumber, string FileIdHex)?> captureHomeIdentity,
        string                                                                    phaseLabel,
        out string?                                                               blockReason)
    {
        ArgumentNullException.ThrowIfNull(expectedHome);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(captureHomeIdentity);

        if (!string.Equals(TrimStoredTrailingSeparators(payload.SandboxHome), expectedHome.CanonicalPath,
                           StringComparison.OrdinalIgnoreCase))
        {
            blockReason = $"The {phaseLabel} refresh payload targeted '{payload.SandboxHome}' instead of the verified " +
                          $"originating sandbox home '{expectedHome.CanonicalPath}'; failing closed.";
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            var currentLexical = TryResolveCanonicalHomePath(expectedHome.CanonicalPath, allowMissing: false);
            if (!string.Equals(currentLexical, expectedHome.CanonicalPath, StringComparison.OrdinalIgnoreCase))
            {
                blockReason = $"The {phaseLabel} sandbox home '{expectedHome.CanonicalPath}' resolved to '{currentLexical}' " +
                              "after refresh; failing closed.";
                return false;
            }

            blockReason = null;
            return true;
        }

        if (pinnedHomeHandle is null || pinnedHomeHandle.IsInvalid)
        {
            blockReason = $"The {phaseLabel} refresh lost its pin handle for '{expectedHome.CanonicalPath}', so completion " +
                          "on the originating sandbox home cannot be confirmed; failing closed.";
            return false;
        }

        var pinnedFinal = SandboxFinalPathResolver.TryReadFinalPath(pinnedHomeHandle);
        if (string.IsNullOrWhiteSpace(pinnedFinal) ||
            !string.Equals(TrimStoredTrailingSeparators(pinnedFinal), expectedHome.CanonicalPath,
                           StringComparison.OrdinalIgnoreCase))
        {
            blockReason = $"The pinned {phaseLabel} sandbox home '{expectedHome.CanonicalPath}' moved or became unresolvable " +
                          "during refresh; failing closed.";
            return false;
        }

        var currentIdentity = captureHomeIdentity(expectedHome.CanonicalPath);
        if (currentIdentity is null ||
            expectedHome.VolumeSerialNumber is not { } expectedVolume ||
            string.IsNullOrWhiteSpace(expectedHome.FileIdHex) ||
            !string.Equals(currentIdentity.Value.FinalPath, expectedHome.CanonicalPath,
                           StringComparison.OrdinalIgnoreCase) ||
            currentIdentity.Value.VolumeSerialNumber != expectedVolume ||
            !string.Equals(currentIdentity.Value.FileIdHex, expectedHome.FileIdHex.Trim(),
                           StringComparison.OrdinalIgnoreCase))
        {
            blockReason = $"The {phaseLabel} sandbox home '{expectedHome.CanonicalPath}' could not be re-verified to its " +
                          "originating 128-bit directory identity after refresh; failing closed.";
            return false;
        }

        blockReason = null;
        return true;
    }

    /// <summary>
    ///     <para>
    ///         将 <c>sandboxHome</c> 规范化为物理最终路径（Windows 上展开 junction 与 8.3 短名）；无法确认
    ///         物理最终路径时在 Windows 上返回 null（绝不回落纯词法路径），非 Windows 环境回落词法规范路径。
    ///     </para>
    ///     <para>
    ///         Resolves <c>sandboxHome</c> to its canonical physical final path (expanding junctions and 8.3
    ///         short names on Windows); returns null on Windows when physical resolution fails instead of falling
    ///         back to a lexical path, while non-Windows environments fall back to lexical normalization.
    ///     </para>
    /// </summary>
    internal static string? TryResolveCanonicalHomePath(string sandboxHome, bool allowMissing = false)
    {
        if (string.IsNullOrWhiteSpace(sandboxHome)) return null;

        string fullPath;
        try
        {
            fullPath = NormalizeHomePath(sandboxHome);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!OperatingSystem.IsWindows()) return fullPath;

        var resolved = SandboxFinalPathResolver.TryGetFinalDirectoryPath(fullPath);
        if (resolved is null && allowMissing)
            resolved = GlobalDenyReadRefreshGate.TryResolveCanonicalPathAllowMissing(fullPath);

        return resolved is null ? null : Path.TrimEndingDirectorySeparator(resolved);
    }

    internal static string NormalizeHomePath(string sandboxHome) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(sandboxHome));

    private static void DeleteStateFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
        }
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException
                                      or UnauthorizedAccessException)
        {
        }
    }
}

using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         外部提权写入者（setup.exe）的进程身份：PID 与 64 位 UTC 文件创建时间戳，用于在调用方 CLI
///         提前退出后跨进程确认实际写入者是否仍在运行（并防 PID 复用误判）。
///     </para>
///     <para>
///         Process identity of an external elevated writer (setup.exe): PID and 64-bit UTC file-time
///         creation timestamp, used after an early CLI exit to verify across processes whether the
///         actual writer is still running while guarding against PID reuse.
///     </para>
/// </summary>
public sealed record SandboxElevatedProcessIdentity(
    int   ProcessId,
    long? CreationTimeUtcFileTime)
{
    /// <summary>
    ///     <para>从已启动的进程句柄捕获 PID 与 64 位 UTC 创建时间戳。</para>
    ///     <para>Captures the PID and 64-bit UTC creation timestamp from a started process handle.</para>
    /// </summary>
    public static SandboxElevatedProcessIdentity? TryCapture(Process process) =>
        SandboxProvisionLeaseStore.TryCaptureProcessIdentity(process);
}

/// <summary>
///     <para>持久化提权租约的磁盘记录（JSON source-gen 序列化）。</para>
///     <para>On-disk record for a durable provisioning lease (source-generated JSON).</para>
/// </summary>
internal sealed record SandboxProvisionLeaseRecord
{
    public required string HomeKey { get; init; }

    public int? BootId { get; init; }

    public long? RecordedTickCount64 { get; init; }

    public int? OwnerProcessId { get; init; }

    public long? OwnerCreationTimeUtcFileTime { get; init; }

    public int? HelperProcessId { get; init; }

    public long? HelperCreationTimeUtcFileTime { get; init; }

    public string? SetupExecutablePath { get; init; }

    public required string RecordedAtUtc { get; init; }
}

internal enum SandboxProvisionLeaseStatus
{
    Clear,
    Active,
    OrphanedPreLaunch,
    Corrupted
}

/// <summary>
///     <para>
///         按 home 键控的跨进程持久化提权租约存储：当 `sandbox provision` 启动提权 helper（可能跨越
///         CLI 超时/取消退出甚至崩溃继续修改 ACL）时，记录实际写入者的进程身份（PID + 创建时间）。
///         其他进程取得同 home 互斥体（含放弃互斥体）前必须经本存储复核：写入者仍存活或无法确认退出
///         时拒绝放行；一旦 OS 确认写入者已退出（或 PID 已被新进程复用），自动清理过期租约并放行。
///     </para>
///     <para>
///         Home-keyed durable cross-process provisioning lease store: when `sandbox provision` launches
///         an elevated helper that may keep mutating ACLs after the CLI times out, cancels, or crashes,
///         the actual writer's process identity (PID + creation time) is persisted. Peer processes
///         acquiring the same home mutex (including an abandoned mutex) must check this store before
///         proceeding: protection stays active while the writer is alive or unconfirmed, and clears
///         automatically once the OS confirms the writer has exited (or the PID has been reused).
///     </para>
/// </summary>
internal static partial class SandboxProvisionLeaseStore
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize                    = 0x00100000;
    private const uint WaitObject0                    = 0x00000000;
    private const uint StillActive                    = 259;
    private const int  ErrorAccessDenied              = 5;
    private const int  ErrorInvalidParameter          = 87;
    private const int  SystemProcessInformationClass  = 5;
    private const int  StatusSuccess                  = 0;
    private const int  StatusInfoLengthMismatch       = unchecked((int)0xC0000004);
    private const int  ReadRetryCount                 = 5;
    private const int  ReadRetryDelayMs               = 20;

    /// <summary>
    ///     <para>返回默认跨进程租约目录（CLI 与 Desktop 共享当前用户的 LocalApplicationData 目录）。</para>
    ///     <para>Returns the default cross-process lease directory shared by CLI and Desktop under LocalApplicationData.</para>
    /// </summary>
    internal static string ResolveDefaultLeaseDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
            baseDir = Path.GetTempPath();
        return Path.Combine(baseDir, "TinyHarness", "sandbox-provision-leases");
    }

    internal static string ResolveLeasePath(string homeKey, string? leaseDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeKey);
        var dir = string.IsNullOrWhiteSpace(leaseDirectory) ? ResolveDefaultLeaseDirectory() : leaseDirectory;
        return Path.Combine(dir, $"provision-lease-{homeKey}.json");
    }

    /// <summary>
    ///     <para>从已启动的进程句柄捕获进程 ID 与 64 位 UTC 创建时间戳。</para>
    ///     <para>Captures the process ID and 64-bit UTC creation timestamp from a started process handle.</para>
    /// </summary>
    internal static SandboxElevatedProcessIdentity? TryCaptureProcessIdentity(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            var pid = process.Id;
            if (!OperatingSystem.IsWindows())
                return new SandboxElevatedProcessIdentity(pid, null);

            var createTime = TryGetProcessCreationTime(process.SafeHandle);
            return new SandboxElevatedProcessIdentity(pid, createTime);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    ///     <para>原子写入（临时文件替换）按 home 键控的持久化提权租约。</para>
    ///     <para>Atomically persists (via temporary-file replacement) the home-keyed provisioning lease.</para>
    /// </summary>
    internal static void WriteLease(
        string                          homeKey,
        SandboxElevatedProcessIdentity? writerProcess,
        string?                         setupExecutablePath,
        string?                         leaseDirectory      = null,
        SandboxElevatedProcessIdentity? ownerProcess        = null,
        int?                            bootId              = null,
        long?                           recordedTickCount64 = null)
    {
        var path      = ResolveLeasePath(homeKey, leaseDirectory);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var effectiveOwner = ownerProcess ?? TryCaptureCurrentProcessIdentity();
        var record = new SandboxProvisionLeaseRecord
        {
            HomeKey                       = homeKey,
            BootId                        = bootId              ?? TryReadSystemBootId(),
            RecordedTickCount64           = recordedTickCount64 ?? Environment.TickCount64,
            OwnerProcessId                = effectiveOwner?.ProcessId,
            OwnerCreationTimeUtcFileTime  = effectiveOwner?.CreationTimeUtcFileTime,
            HelperProcessId               = writerProcess?.ProcessId,
            HelperCreationTimeUtcFileTime = writerProcess?.CreationTimeUtcFileTime,
            SetupExecutablePath           = setupExecutablePath,
            RecordedAtUtc                 = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",
                                                                     CultureInfo.InvariantCulture)
        };
        var json     = JsonSerializer.Serialize(record, WindowsSandboxJsonContext.Default.SandboxProvisionLeaseRecord);
        var tempPath = Path.Combine(directory, $"provision-lease-{homeKey}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    /// <summary>
    ///     <para>尝试删除指定 homeKey 的租约文件；缺失或瞬时清理失败不抛出。</para>
    ///     <para>Best-effort deletion of the lease file for the given homeKey; absence or transient failures do not throw.</para>
    /// </summary>
    internal static void TryDeleteLease(string homeKey, string? leaseDirectory = null)
    {
        try
        {
            TryDeleteFile(ResolveLeasePath(homeKey, leaseDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    /// <summary>
    ///     <para>仅用于测试资源清理：按 sandboxHome 计算 homeKey 并删除对应租约文件。</para>
    ///     <para>Test-cleanup helper: computes the homeKey for a sandboxHome and deletes its lease file if present.</para>
    /// </summary>
    internal static void TryDeleteLeaseForHome(string sandboxHome, string? leaseDirectory = null)
    {
        try
        {
            var homeKey = GlobalDenyReadRefreshGate.HomeKeyForHome(sandboxHome, allowMissingHome : true);
            TryDeleteLease(homeKey, leaseDirectory);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    ///     <para>
    ///         在持有 home 互斥体的前提下评估持久化租约：无租约返回 Clear；若写入者已由 OS 确认退出
    ///         则自动删除过期租约并返回 Clear；若写入者仍存活或无法确认退出（含未知 PID 租约）返回
    ///         Active；文件损坏或不可读返回 Corrupted（由门 fail closed 拒绝）。
    ///     </para>
    ///     <para>
    ///         Evaluates the durable lease while holding the home mutex: returns Clear if absent, deletes
    ///         the lease and returns Clear once the OS confirms the writer has exited, returns Active while
    ///         the writer is still alive or unconfirmed (including unknown-PID leases), and returns
    ///         Corrupted when the lease file is unreadable or malformed so the gate fails closed.
    ///     </para>
    /// </summary>
    internal static SandboxProvisionLeaseStatus EvaluateAndCleanStaleLease(
        string homeKey, string? leaseDirectory = null)
    {
        var path = ResolveLeasePath(homeKey, leaseDirectory);
        string? content;
        try
        {
            content = ReadLeaseContentOrNull(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SandboxDiagnosticLog.Event("provision-lease", $"read-failed type={ex.GetType().Name}");
            return SandboxProvisionLeaseStatus.Corrupted;
        }

        if (content is null) return SandboxProvisionLeaseStatus.Clear;

        SandboxProvisionLeaseRecord? record;
        try
        {
            record = JsonSerializer.Deserialize(content, WindowsSandboxJsonContext.Default.SandboxProvisionLeaseRecord);
        }
        catch (JsonException)
        {
            SandboxDiagnosticLog.Event("provision-lease", "corrupted-json");
            return SandboxProvisionLeaseStatus.Corrupted;
        }

        if (record is null || !string.Equals(record.HomeKey, homeKey, StringComparison.Ordinal))
        {
            SandboxDiagnosticLog.Event("provision-lease", "invalid-record");
            return SandboxProvisionLeaseStatus.Corrupted;
        }

        // 已确认系统经历了重启：上一开机周期的所有 UAC 请求（AppInfo/consent.exe）与提权 helper
        // 必然已结束，可以安全清理遗留租约并放行。
        if (IsConfirmedFromPreviousBoot(record))
        {
            TryDeleteFile(path);
            SandboxDiagnosticLog.Event("provision-lease", "stale-lease-cleared previous-boot");
            return SandboxProvisionLeaseStatus.Clear;
        }

        // 尚无写入者 PID 的预启动租约：Windows 提权进程由 AppInfo 服务在用户同意后异步创建，
        // 同一开机周期内进程快照无法确认等待中的 UAC 请求已经结束。
        // 发起方仍存活时按正常等待（Active）；发起方已退出时绝不提前清除，返回 OrphanedPreLaunch
        // 由门立即 fail-closed 报错并给出明确租约路径与恢复指引。
        if (record.HelperProcessId is not { } pid || pid <= 0)
        {
            if (record.OwnerProcessId is { } ownerPid && ownerPid > 0 &&
                !IsProcessConfirmedExited(ownerPid, record.OwnerCreationTimeUtcFileTime))
                return SandboxProvisionLeaseStatus.Active;

            SandboxDiagnosticLog.Event("provision-lease", "orphaned-prelaunch-lease same-boot");
            return SandboxProvisionLeaseStatus.OrphanedPreLaunch;
        }

        if (!IsProcessConfirmedExited(pid, record.HelperCreationTimeUtcFileTime))
            return SandboxProvisionLeaseStatus.Active;

        // OS 已确认实际写入者退出（或 PID 已被新进程复用）：删除过期租约并放行。
        TryDeleteFile(path);
        SandboxDiagnosticLog.Event("provision-lease", "stale-lease-cleared writer-exited");
        return SandboxProvisionLeaseStatus.Clear;
    }

    internal static bool IsConfirmedFromPreviousBoot(SandboxProvisionLeaseRecord record)
    {
        if (record.BootId is { } recordedBootId &&
            TryReadSystemBootId() is { } currentBootId &&
            currentBootId != recordedBootId)
            return true;

        return record.RecordedTickCount64 is { } recordedTick &&
               Environment.TickCount64 < recordedTick;
    }

    internal static int? TryReadSystemBootId()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters",
                writable : false);
            return key?.GetValue("BootId") switch
            {
                int intValue => intValue,
                _            => null
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    ///     <para>
    ///         查询目标进程是否已由操作系统确认退出：优先以 <c>SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION</c>
    ///         打开句柄，核对 <c>lpCreationTime</c> 防 PID 复用并以 <c>WaitForSingleObject(handle, 0)</c>
    ///         作为明确定义的退出信号（不读取运行中未定义的 <c>lpExitTime</c>）；若跨完整性级别导致
    ///         <c>SYNCHRONIZE</c> 被拒，回落至 <c>PROCESS_QUERY_LIMITED_INFORMATION + GetExitCodeProcess</c>
    ///         （遇 <c>STILL_ACTIVE</c> 或跨账户 <c>ERROR_ACCESS_DENIED</c> 均回落
    ///         <c>NtQuerySystemInformation(SystemProcessInformation)</c>）。任何查询异常均 fail-closed 返回 false。
    ///     </para>
    ///     <para>
    ///         Determines whether the OS confirms the target process has exited: first opens with
    ///         <c>SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION</c>, checks <c>lpCreationTime</c> against
    ///         PID reuse, and uses <c>WaitForSingleObject(handle, 0)</c> as the well-defined exit signal
    ///         (never reading <c>lpExitTime</c>, which is undefined while running); if <c>SYNCHRONIZE</c>
    ///         is denied across integrity levels, falls back to <c>PROCESS_QUERY_LIMITED_INFORMATION</c> +
    ///         <c>GetExitCodeProcess</c> (with <c>STILL_ACTIVE</c> and cross-account <c>ERROR_ACCESS_DENIED</c>
    ///         disambiguated via <c>NtQuerySystemInformation(SystemProcessInformation)</c>). Any unexpected
    ///         query failure fails closed and returns false.
    ///     </para>
    /// </summary>
    internal static bool IsProcessConfirmedExited(int processId, long? expectedCreationTimeUtcFileTime)
    {
        if (!OperatingSystem.IsWindows()) return false;

        using var syncHandle = OpenProcess(Synchronize | ProcessQueryLimitedInformation, false, (uint)processId);
        if (!syncHandle.IsInvalid)
        {
            if (!GetProcessTimes(syncHandle, out var creationTime, out _, out _, out _))
                return false;

            if (expectedCreationTimeUtcFileTime is { } expected && creationTime != expected)
                return true;

            var waitResult = WaitForSingleObject(syncHandle, 0);
            return waitResult == WaitObject0;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == ErrorInvalidParameter)
            return true;

        // 跨完整性级别（中等完整性查询高完整性 UAC 进程）可能拒绝 SYNCHRONIZE 但允许 PROCESS_QUERY_LIMITED_INFORMATION。
        if (error == ErrorAccessDenied)
        {
            using var queryHandle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
            if (!queryHandle.IsInvalid)
            {
                if (!GetProcessTimes(queryHandle, out var creationTime, out _, out _, out _))
                    return false;

                if (expectedCreationTimeUtcFileTime is { } expected && creationTime != expected)
                    return true;

                if (!GetExitCodeProcess(queryHandle, out var exitCode))
                    return false;

                if (exitCode != StillActive)
                    return true;

                // STILL_ACTIVE (259) 可能是进程仍在运行，也可能是进程以退出码 259 退出；经内核活动进程表消歧。
                return TryCheckExitedViaSystemProcessInformation(processId, expectedCreationTimeUtcFileTime) == true;
            }

            error = Marshal.GetLastPInvokeError();
            if (error == ErrorInvalidParameter)
                return true;
        }

        // 跨账户 UAC 提权进程可能拒绝所有 OpenProcess：改走内核进程快照核对 (PID, CreateTime)。
        if (error == ErrorAccessDenied || error != 0)
            return TryCheckExitedViaSystemProcessInformation(processId, expectedCreationTimeUtcFileTime) == true;

        return false;
    }

    internal static bool? TryCheckExitedViaSystemProcessInformation(
        int processId, long? expectedCreationTimeUtcFileTime)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var bufferSize = 256 * 1024;
        const int maxBufferSize = 32 * 1024 * 1024;
        while (bufferSize <= maxBufferSize)
        {
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                var status = NtQuerySystemInformation(SystemProcessInformationClass, buffer, (uint)bufferSize,
                                                      out var returnLength);
                if (status == StatusInfoLengthMismatch)
                {
                    var nextSize = returnLength > (uint)bufferSize && returnLength <= maxBufferSize
                        ? (int)returnLength + 64 * 1024
                        : bufferSize * 2;
                    bufferSize = Math.Max(nextSize, bufferSize * 2);
                    continue;
                }

                if (status != StatusSuccess) return null;

                // SYSTEM_PROCESS_INFORMATION 固定布局：NextEntryOffset=+0 (uint), CreateTime=+32 (long),
                // UniqueProcessId=+80 (x64) / +68 (x86)。
                var pidOffset = IntPtr.Size == 8 ? 80 : 68;
                var offset    = 0;
                while (true)
                {
                    if (offset < 0 || offset + pidOffset + IntPtr.Size > bufferSize)
                        return null;

                    var entryPtr   = IntPtr.Add(buffer, offset);
                    var entryPid   = Marshal.ReadIntPtr(entryPtr, pidOffset).ToInt64();
                    var createTime = Marshal.ReadInt64(entryPtr, 32);
                    if (entryPid == processId)
                    {
                        // PID 仍在运行：若记录了创建时间且与当前进程不同，则说明原进程已退出、PID 已被复用。
                        if (expectedCreationTimeUtcFileTime is { } expected && createTime != expected)
                            return true;
                        return false;
                    }

                    var nextOffset = Marshal.ReadInt32(entryPtr, 0);
                    if (nextOffset == 0) break;
                    if (nextOffset < 0) return null;
                    offset += nextOffset;
                }

                // 完整扫描当前系统全部活动进程且未找到匹配项：确认原写入者已退出。
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    private static SandboxElevatedProcessIdentity? TryCaptureCurrentProcessIdentity()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            return TryCaptureProcessIdentity(current);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? ReadLeaseContentOrNull(string path)
    {
        for (var attempt = 1;; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                  FileShare.Read | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (attempt < ReadRetryCount &&
                                       ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(ReadRetryDelayMs);
            }
        }
    }

    private static long? TryGetProcessCreationTime(SafeProcessHandle handle)
    {
        if (handle.IsInvalid) return null;
        return GetProcessTimes(handle, out var creationTime, out _, out _, out _) ? creationTime : null;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(
        uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessTimes", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(
        SafeProcessHandle process,
        out long          creationTime,
        out long          exitTime,
        out long          kernelTime,
        out long          userTime);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", EntryPoint = "GetExitCodeProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [LibraryImport("ntdll.dll", EntryPoint = "NtQuerySystemInformation")]
    private static partial int NtQuerySystemInformation(
        int    systemInformationClass,
        IntPtr systemInformation,
        uint   systemInformationLength,
        out uint returnLength);
}

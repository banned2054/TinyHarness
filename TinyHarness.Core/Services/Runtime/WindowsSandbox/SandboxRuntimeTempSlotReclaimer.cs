using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.Versioning;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>回收器的显式上界：枚举深度、条目数、瞬态冲突重试次数，以及每次重试的等待间隔。</para>
///     <para>
///         The reclaimer's explicit bounds: enumeration depth, entry count, transient-conflict retries, and
///         the delay between attempts.
///     </para>
/// </summary>
internal sealed record SandboxRuntimeTempSlotReclaimOptions
{
    internal static readonly SandboxRuntimeTempSlotReclaimOptions Default = new();

    internal int      MaximumDepth   { get; init; } = 64;
    internal int      MaximumEntries { get; init; } = 100_000;
    internal int      MaximumRetries { get; init; } = 4;
    internal TimeSpan RetryDelay     { get; init; } = TimeSpan.FromMilliseconds(50);

    internal void Validate()
    {
        if (MaximumDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumDepth), MaximumDepth,
                                                  "MaximumDepth must be non-negative.");
        if (MaximumEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumEntries), MaximumEntries,
                                                  "MaximumEntries must be positive.");
        if (MaximumRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumRetries), MaximumRetries,
                                                  "MaximumRetries must be non-negative.");
        if (RetryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RetryDelay), RetryDelay,
                                                  "RetryDelay must be non-negative.");
    }
}

/// <summary>
///     <para>一条保守残留：清理期间未删除的路径与原因，直接进入交付结果，绝不静默吞掉。</para>
///     <para>
///         One conservative leftover: a path that was not deleted during cleanup plus the reason, carried into
///         the result instead of being swallowed.
///     </para>
/// </summary>
internal sealed record SandboxRuntimeTempSlotReclaimIssue(string Path, string Reason);

/// <summary>
///     <para>
///         回收结果：是否真正移除槽位目录、删除计数与全部残留原因。仅当 <see cref="Reclaimed" /> 为 true 时
///         才能把槽位复用。
///     </para>
///     <para>
///         The reclamation result: whether the slot directory was actually removed, deletion counts, and every
///         leftover reason. The slot may only be reused when <see cref="Reclaimed" /> is true.
///     </para>
/// </summary>
internal sealed record SandboxRuntimeTempSlotReclaimResult(
    string                                            SlotPath,
    bool                                              Reclaimed,
    int                                               DeletedEntries,
    int                                               DeletedDirectories,
    IReadOnlyList<SandboxRuntimeTempSlotReclaimIssue> Issues);

/// <summary>
///     <para>
///         基于 <see cref="SafeFileHandle" /> 的保守槽位回收器：只认领"整棵进程树已退出且卷/文件 ID 匹配"的
///         退役槽位，所有枚举与删除都以已校验的目录句柄为基准，重解析点只解除链接本身，硬链接与只读项保留
///         并如实报告。
///     </para>
///     <para>
///         A conservative <see cref="SafeFileHandle" />-bound slot reclaimer: it only claims retired slots whose
///         whole process tree has exited and whose volume/file identity still matches; every enumeration and
///         deletion is anchored on the validated directory handle, reparse points lose only their link, and
///         hard-linked or read-only entries are retained and reported.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         保守策略的三条硬约束：① 重解析点用 <c>FILE_OPEN_REPARSE_POINT</c> 打开后只删除链接本身，绝不递归
///         进入目标；② 只读属性绝不清除，因为清除会通过硬链接修改槽位外部的共享文件；③ 硬链接（链接数大于 1）
///         不删除，只报告。普通子项仅申请最小权限（<c>DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE</c>），
///         仅当确认为非重解析点目录时再通过同句柄重开升级为目录列举权限。任何残留都进入结果，调用方
///         据此保持槽位保留态而不是假装回收成功。
///     </para>
///     <para>
///         Three hard constraints of the conservative policy: (1) reparse points are opened with
///         <c>FILE_OPEN_REPARSE_POINT</c> and only the link itself is deleted, never the target; (2) the
///         read-only attribute is never cleared, because clearing it can modify shared files outside the slot
///         through hard links; (3) hard-linked entries (link count above one) are reported, not deleted. Child
///         entries are opened with minimal rights (<c>DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE</c>) and only
///         upgraded via a same-handle reopen to list access once confirmed to be real non-reparse directories.
///         Every leftover reaches the result so callers keep the slot reserved instead of pretending success.
///     </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class SandboxRuntimeTempSlotReclaimer
{
    private const uint MinimalChildAccess = SandboxRuntimeTempSlotNative.DeleteAccess       |
                                            SandboxRuntimeTempSlotNative.FileReadAttributes |
                                            SandboxRuntimeTempSlotNative.Synchronize;

    // 重开目录仅申请列举与属性读，不重复申请 DELETE；外层 child 句柄全程不共享 FILE_SHARE_DELETE，
    // 从而在整个递归清理期间锁定子目录位置，禁止并发进程将其移出退役槽位。
    private const uint DirectoryEnumerateAccess = SandboxRuntimeTempSlotNative.FileListDirectory  |
                                                  SandboxRuntimeTempSlotNative.FileReadAttributes |
                                                  SandboxRuntimeTempSlotNative.Synchronize;

    /// <summary>
    ///     <para>
    ///         回收退役槽位；整树退出未确认、选项非法、身份不匹配或拒绝访问等前置条件失败时同步或按任务抛出，
    ///         清理期间的残留进入返回值。
    ///     </para>
    ///     <para>
    ///         Reclaims a retired slot; unmet preconditions (unconfirmed tree exit, invalid options, identity
    ///         mismatch, access denial) throw, while leftovers during cleanup are returned in the result.
    ///     </para>
    /// </summary>
    /// <param name="slot">
    ///     <para>初始化时返回的槽位（目录、身份与 DACL 模板）。</para>
    ///     <para>The slot returned by initialization (directory, identity, and DACL template).</para>
    /// </param>
    /// <param name="wholeTreeExitConfirmed">
    ///     <para>外层 Job 已确认 <c>ActiveProcesses == 0</c> 的证据；false 时拒绝回收。</para>
    ///     <para>Evidence that the outer job reported <c>ActiveProcesses == 0</c>; false refuses reclamation.</para>
    /// </param>
    /// <param name="options">
    ///     <para>上界选项；null 使用 <see cref="SandboxRuntimeTempSlotReclaimOptions.Default" />。</para>
    ///     <para>Bound options; null uses <see cref="SandboxRuntimeTempSlotReclaimOptions.Default" />.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消令牌；在条目间、枚举中与重试等待中生效，已标记删除的条目仍会在句柄关闭时删除。</para>
    ///     <para>
    ///         Cancellation token, honored between entries, during enumeration, and during retry delays; entries
    ///         already marked for deletion are still removed when their handles close.
    ///     </para>
    /// </param>
    internal static Task<SandboxRuntimeTempSlotReclaimResult> ReclaimAsync(
        SandboxRuntimeTempSlot                slot,
        bool                                  wholeTreeExitConfirmed,
        SandboxRuntimeTempSlotReclaimOptions? options,
        CancellationToken                     cancellationToken) =>
        ReclaimAsync(slot, wholeTreeExitConfirmed, options, cancellationToken, isPathMissing: null);

    internal static Task<SandboxRuntimeTempSlotReclaimResult> ReclaimAsync(
        SandboxRuntimeTempSlot                slot,
        bool                                  wholeTreeExitConfirmed,
        SandboxRuntimeTempSlotReclaimOptions? options,
        CancellationToken                     cancellationToken,
        Func<string, bool>?                   isPathMissing)
    {
        SandboxRuntimeTempSlotNative.EnsureWindows();
        ArgumentNullException.ThrowIfNull(slot);
        if (!wholeTreeExitConfirmed)
            throw new InvalidOperationException(
                $"Refusing to reclaim the runtime temp slot '{slot.Directory}': the whole process tree exit is not " +
                "confirmed. The slot stays reserved so a surviving descendant cannot write into a reclaimed or " +
                "re-provisioned directory.");

        var effectiveOptions = options ?? SandboxRuntimeTempSlotReclaimOptions.Default;
        effectiveOptions.Validate();
        return Task.Run(() => Reclaim(slot, effectiveOptions, cancellationToken, isPathMissing), cancellationToken);
    }

    private static SandboxRuntimeTempSlotReclaimResult Reclaim(
        SandboxRuntimeTempSlot               slot,
        SandboxRuntimeTempSlotReclaimOptions options,
        CancellationToken                    cancellationToken,
        Func<string, bool>?                  isPathMissing)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var walk    = new Walk(options, cancellationToken);
        var deleted = false;
        // 不共享删除：持有期间槽位目录既不能被替换也不能被别处删除。
        using (var handle = SandboxRuntimeTempSlotNative.OpenDirectory(
                   slot.Directory,
                   SandboxRuntimeTempSlotNative.ReadControl       | SandboxRuntimeTempSlotNative.FileReadAttributes |
                   SandboxRuntimeTempSlotNative.Synchronize       | SandboxRuntimeTempSlotNative.FileListDirectory  |
                   SandboxRuntimeTempSlotNative.DeleteAccess,
                   SandboxRuntimeTempSlotNative.FileShareReadWrite, "retired runtime temp slot"))
        {
            SandboxRuntimeTempSlotNative.RequireDirectory(handle, slot.Directory, out _);
            var identity = SandboxRuntimeTempSlotIdentity.Capture(handle, slot.Directory);
            if (!slot.Identity.Matches(identity))
                throw new SandboxRuntimeTempSlotException(
                    slot.Directory,
                    ["identity: the retired slot directory was replaced " +
                     $"(expected {slot.Identity.FormatForDiagnostics()}, " +
                     $"actual {identity.FormatForDiagnostics()})"]);

            walk.CleanDirectory(handle, slot.Directory, 0);
            if (walk.Issues.Count > 0)
                walk.AddIssue(slot.Directory,
                              "the slot directory was retained because conservative cleanup left entries behind");
            else
                deleted = walk.TryDelete(handle, slot.Directory, isDirectory: true);
        }

        var missing = false;
        if (walk.Issues.Count == 0 && deleted)
        {
            try
            {
                missing = isPathMissing is null
                    ? SandboxRuntimeTempSlotNative.IsPathMissing(slot.Directory)
                    : isPathMissing(slot.Directory);
                if (!missing)
                    walk.AddIssue(slot.Directory, "the slot directory still exists after delete-on-close");
            }
            catch (Win32Exception ex)
            {
                // 删除确认遇 Win32 错误（例如 delete-pending 的 ERROR_ACCESS_DENIED）记入残留诊断而非抛出，保持槽位保留态。
                walk.AddIssue(slot.Directory,
                              $"removal verification failed after delete-on-close (Win32 error {ex.NativeErrorCode}; possible delete-pending)");
            }
        }

        return new SandboxRuntimeTempSlotReclaimResult(slot.Directory, missing, walk.DeletedEntries,
                                                       walk.DeletedDirectories, walk.Issues.ToArray());
    }

    private static bool IsTransientOpenError(int win32Error) =>
        win32Error is SandboxRuntimeTempSlotNative.ErrorSharingViolation or
            SandboxRuntimeTempSlotNative.ErrorLockViolation;

    private static bool IsTransientDeleteError(int win32Error, bool isDirectory) =>
        win32Error is SandboxRuntimeTempSlotNative.ErrorSharingViolation or
            SandboxRuntimeTempSlotNative.ErrorLockViolation ||
        (isDirectory && win32Error == SandboxRuntimeTempSlotNative.ErrorDirectoryNotEmpty);

    private sealed class Walk(SandboxRuntimeTempSlotReclaimOptions options, CancellationToken cancellationToken)
    {
        private int _enumeratedEntries;

        internal int DeletedEntries { get; private set; }

        internal int DeletedDirectories { get; private set; }

        internal List<SandboxRuntimeTempSlotReclaimIssue> Issues { get; } = [];

        internal void AddIssue(string path, string reason) =>
            Issues.Add(new SandboxRuntimeTempSlotReclaimIssue(path, reason));

        /// <summary>
        ///     <para>递归清理一个目录句柄的直接子项；枚举与遍历共用同一条目上界，越界或超深时立即停止并记录原因。</para>
        ///     <para>
        ///         Recursively cleans the direct children of one directory handle; enumeration and traversal share one
        ///         global entry budget, stopping immediately and recording the reason once depth or entry bounds are
        ///         exceeded.
        ///     </para>
        /// </summary>
        internal void CleanDirectory(SafeFileHandle directory, string directoryPath, int depth)
        {
            if (depth > options.MaximumDepth)
            {
                AddIssue(directoryPath, $"the enumeration depth bound ({options.MaximumDepth}) was exceeded");
                return;
            }

            List<string> names;
            bool         truncated;
            try
            {
                // 共享计数传入 EnumerateChildNames 实时累加：即使后续批次读取抛错，也保留此前已消耗的条目配额。
                (names, truncated) = SandboxRuntimeTempSlotNative.EnumerateChildNames(
                    directory, directoryPath, options.MaximumEntries, ref _enumeratedEntries, cancellationToken);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidDataException)
            {
                AddIssue(directoryPath, ex.Message);
                return;
            }

            foreach (var name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var childPath = Path.Combine(directoryPath, name);
                var child     = OpenChildWithRetry(directory, name, childPath);
                if (child is null) continue;

                using (child)
                {
                    if (!SandboxRuntimeTempSlotNative.TryGetFileInformation(child, out var information,
                                                                            out var identityError))
                    {
                        AddIssue(childPath, $"cannot identify the entry (Win32 error {identityError})");
                        continue;
                    }

                    var attributes = information.FileAttributes;
                    if ((attributes & SandboxRuntimeTempSlotNative.AttributeReparsePoint) != 0)
                    {
                        // 只解除链接本身：句柄以 OPEN_REPARSE_POINT 打开，目标目录不会被枚举或删除。
                        var isDirLink = (attributes & SandboxRuntimeTempSlotNative.AttributeDirectory) != 0;
                        TryDelete(child, childPath, isDirectory: isDirLink);
                        continue;
                    }

                    if ((attributes & SandboxRuntimeTempSlotNative.AttributeDirectory) != 0)
                    {
                        var issuesBefore = Issues.Count;
                        // 外层 child 句柄持有 DELETE 且不共享 FILE_SHARE_DELETE（钉死目录位置）；listHandle 仅申请
                        // 列举权限，shareMode 带 FileShareAll 仅用于兼容本线程已持有的 child 句柄，不向外部放开移动。
                        using (var listHandle = SandboxRuntimeTempSlotNative.TryReOpenHandle(
                                   child, DirectoryEnumerateAccess, SandboxRuntimeTempSlotNative.FileShareAll,
                                   out var reopenError))
                        {
                            if (listHandle is null)
                            {
                                AddIssue(childPath,
                                         $"cannot reopen directory for enumeration (Win32 error {reopenError})");
                                continue;
                            }

                            CleanDirectory(listHandle, childPath, depth + 1);
                        }

                        if (Issues.Count != issuesBefore) continue;
                        TryDelete(child, childPath, isDirectory: true);
                        continue;
                    }

                    if ((attributes & SandboxRuntimeTempSlotNative.AttributeReadOnly) != 0)
                    {
                        AddIssue(childPath,
                                 "a read-only file was retained; clearing the read-only attribute could modify a " +
                                 "file shared outside the slot through a hard link");
                        continue;
                    }

                    if (information.NumberOfLinks > 1)
                    {
                        AddIssue(childPath,
                                 $"a hard-linked file was retained ({information.NumberOfLinks} links); unlinking a " +
                                 "shared name is left to an explicit decision");
                        continue;
                    }

                    TryDelete(child, childPath, isDirectory: false);
                }
            }

            if (truncated)
                AddIssue(directoryPath, $"the entry-count bound ({options.MaximumEntries}) was exceeded");
        }

        internal bool TryDelete(SafeFileHandle handle, string path, bool isDirectory)
        {
            for (var attempt = 0;; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SandboxRuntimeTempSlotNative.TryMarkDeleteOnClose(handle, out var error))
                {
                    if (isDirectory) DeletedDirectories++;
                    else             DeletedEntries++;
                    return true;
                }

                if (!IsTransientDeleteError(error, isDirectory) || attempt >= options.MaximumRetries)
                {
                    var retries = IsTransientDeleteError(error, isDirectory)
                        ? $" after {attempt} retries"
                        : string.Empty;
                    AddIssue(path, $"delete-on-close failed{retries} (Win32 error {error})");
                    return false;
                }

                WaitRetryDelay();
            }
        }

        private SafeFileHandle? OpenChildWithRetry(SafeFileHandle directory, string name, string childPath)
        {
            for (var attempt = 0;; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var child = SandboxRuntimeTempSlotNative.OpenChild(directory, name, MinimalChildAccess, out var error);
                if (child is not null) return child;
                if (!IsTransientOpenError(error) || attempt >= options.MaximumRetries)
                {
                    var retries = IsTransientOpenError(error) ? $" after {attempt} retries" : string.Empty;
                    AddIssue(childPath, $"cannot open the entry{retries} (Win32 error {error})");
                    return null;
                }

                WaitRetryDelay();
            }
        }

        private void WaitRetryDelay()
        {
            if (options.RetryDelay <= TimeSpan.Zero)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            if (cancellationToken.WaitHandle.WaitOne(options.RetryDelay))
                cancellationToken.ThrowIfCancellationRequested();
        }
    }
}

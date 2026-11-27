using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         槽位初始化回滚未完成异常：当新建槽位后的步骤失败，且自动 delete-on-close 标记失败或在关闭新建句柄
///         后目录仍未从命名空间移除（含待删除 delete-pending 残留）时抛出，保留原始失败异常
///         （<see cref="Exception.InnerException" />）、回滚错误码与已捕获的新建槽位最终路径及目录身份，供
///         租约层或回收器安全认领残留目录。
///     </para>
///     <para>
///         Thrown when slot initialization fails after directory creation and automatic delete-on-close rollback
///         either fails to mark or leaves the directory present/delete-pending after closing the handle:
///         preserves the original failure (<see cref="Exception.InnerException" />), the rollback error code,
///         and the captured final path and directory identity so the lease store or reclaimer can claim the
///         leftover directory safely.
///     </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SandboxRuntimeTempSlotRollbackException : InvalidOperationException
{
    internal SandboxRuntimeTempSlotRollbackException(
        string                          slotPath,
        SandboxRuntimeTempSlotIdentity? leftoverIdentity,
        int                             rollbackWin32Error,
        bool                            deletePendingOrRemaining,
        Exception                       initializationException)
        : base(BuildMessage(slotPath, leftoverIdentity, rollbackWin32Error, deletePendingOrRemaining,
                            initializationException),
               initializationException)
    {
        SlotPath                 = slotPath;
        LeftoverIdentity         = leftoverIdentity;
        RollbackWin32Error       = rollbackWin32Error;
        DeletePendingOrRemaining = deletePendingOrRemaining;
    }

    /// <summary>
    ///     <para>未能确认移除的槽位最终目录路径。</para>
    ///     <para>The resolved final path of the slot directory whose removal could not be confirmed.</para>
    /// </summary>
    internal string SlotPath { get; }

    /// <summary>
    ///     <para>新建目录的卷与文件标识快照（若已捕获），可供后续安全回收。</para>
    ///     <para>The captured volume/file identity snapshot of the newly created directory, if available, for safe later reclamation.</para>
    /// </summary>
    internal SandboxRuntimeTempSlotIdentity? LeftoverIdentity { get; }

    /// <summary>
    ///     <para>回滚标记或移除复核失败时的 Win32 错误码；目录仍存在但无 Win32 错误时为 0。</para>
    ///     <para>The Win32 error code from rollback marking or removal verification, or 0 when the directory simply remained present.</para>
    /// </summary>
    internal int RollbackWin32Error { get; }

    /// <summary>
    ///     <para>是否已成功标记删除但在关闭新建句柄后目录仍存在或处于待删除（delete-pending）状态。</para>
    ///     <para>Whether deletion was marked successfully yet the directory remained present or delete-pending after closing the handle.</para>
    /// </summary>
    internal bool DeletePendingOrRemaining { get; }

    private static string BuildMessage(
        string slotPath, SandboxRuntimeTempSlotIdentity? leftoverIdentity, int rollbackWin32Error,
        bool deletePendingOrRemaining, Exception initializationException)
    {
        var identityText = leftoverIdentity is null
            ? "identity unavailable"
            : leftoverIdentity.FormatForDiagnostics();
        var detail = deletePendingOrRemaining
            ? rollbackWin32Error == 0
                ? "the directory still existed after closing the handle (possible delete-pending leftover)"
                : $"removal verification failed after closing the handle (Win32 error {rollbackWin32Error}; possible delete-pending)"
            : $"automatic delete-on-close marking failed (Win32 error {rollbackWin32Error})";
        return $"Initialization of runtime temp slot '{slotPath}' failed ({initializationException.Message}), and " +
               $"rollback could not confirm removal: {detail} ({identityText}).";
    }
}

/// <summary>
///     <para>
///         槽位初始化器：先验证模板并打开可信 <c>sandboxHome</c> 起点目录（兼容 <c>sandboxHome</c> 自身的
///         junction 别名），从已打开的 <c>home</c> 句柄取得规范最终路径作为所有返回路径与回滚记录的基准；再沿
///         已校验的受管目录句柄链（<c>runtime-tmp-v2</c> → <c>slots</c> → <c>slot-XX</c>，全程拒绝重解析点）
///         原子创建全新槽位目录并直接取得其句柄，写入受保护显式 DACL 并固定 owner，随后在同一句柄上执行契约
///         校验。创建后任一步失败时通过该新建句柄标记删除、关闭句柄并确认目录已从命名空间移除；未能确认移除
///         （含 delete-pending 残留）时保留原异常、错误码与最终路径/目录身份。
///     </para>
///     <para>
///         The slot initializer: validates the template and opens the trusted <c>sandboxHome</c> directory
///         (following any junction alias on <c>sandboxHome</c> itself), reads the canonical final path from that
///         opened handle so all returned and rollback paths remain valid even if an alias changes later, then
///         walks a validated managed handle chain (<c>runtime-tmp-v2</c> → <c>slots</c> → <c>slot-XX</c>,
///         refusing reparse points throughout) to atomically create a brand-new slot directory and obtain its
///         handle directly, writes the protected explicit DACL and pins the owner, and validates the contract on
///         that same handle. Any failure after creation marks the new empty slot for deletion, closes the
///         handle, and verifies removal; if removal cannot be confirmed (including delete-pending leftovers),
///         preserves the original exception, error code, and resolved directory identity.
///     </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SandboxRuntimeTempSlotInitializer
{
    private const uint ContainerInitAccess = SandboxRuntimeTempSlotNative.ReadControl        |
                                             SandboxRuntimeTempSlotNative.WriteDac           |
                                             SandboxRuntimeTempSlotNative.FileReadAttributes |
                                             SandboxRuntimeTempSlotNative.FileTraverse       |
                                             SandboxRuntimeTempSlotNative.Synchronize;

    private const uint SlotInitAccess = ContainerInitAccess | SandboxRuntimeTempSlotNative.DeleteAccess;

    /// <summary>
    ///     <para>
    ///         在已校验的父容器句柄下为指定索引原子创建全新槽位目录并返回其基于真实最终路径的身份快照；槽位
    ///         目录已存在（尚未确认回收或被提前占位）、受管目录被替换为重解析点、DACL 无法写入、owner 无法
    ///         固定或契约校验失败时均 fail-closed 抛出。若新建目录之后发生失败，自动通过新建句柄标记删除、
    ///         关闭句柄并核实目录已移除；未能确认移除（含 delete-pending）则抛出
    ///         <see cref="SandboxRuntimeTempSlotRollbackException" /> 保留原异常、错误码与目录身份。
    ///     </para>
    ///     <para>
    ///         Atomically creates a brand-new slot directory under the validated parent container handle and returns
    ///         its identity snapshot anchored on the resolved final path; fails closed if the slot already exists, a
    ///         managed directory was replaced by a reparse point, the DACL cannot be written, the owner cannot be
    ///         pinned, or the contract check fails. Any failure after creation marks the new empty directory for
    ///         deletion, closes the handle, and verifies removal; if removal cannot be confirmed (including
    ///         delete-pending), throws <see cref="SandboxRuntimeTempSlotRollbackException" /> carrying the original
    ///         failure, error code, and resolved directory identity.
    ///     </para>
    /// </summary>
    internal static SandboxRuntimeTempSlot Initialize(
        string                        sandboxHome,
        int                           slotIndex,
        SandboxRuntimeTempSlotAclSpec spec,
        CancellationToken             cancellationToken = default) =>
        Initialize(sandboxHome, slotIndex, spec, cancellationToken, afterSlotCreated: null,
                   tryMarkDeleteOnClose: null, verifyPathMissing: null);

    internal static SandboxRuntimeTempSlot Initialize(
        string                                             sandboxHome,
        int                                                slotIndex,
        SandboxRuntimeTempSlotAclSpec                      spec,
        CancellationToken                                  cancellationToken,
        Action<SafeFileHandle>?                            afterSlotCreated,
        Func<SafeFileHandle, (bool Succeeded, int Error)>? tryMarkDeleteOnClose,
        Func<string, (bool Missing, int Error)>?           verifyPathMissing)
    {
        SandboxRuntimeTempSlotNative.EnsureWindows();
        ArgumentNullException.ThrowIfNull(spec);
        cancellationToken.ThrowIfCancellationRequested();

        // 在触碰任何目录或 ACL 之前先校验路径参数与模板。
        _ = SandboxRuntimeTempSlotLayout.ContainerDirectory(sandboxHome);
        var slotName      = SandboxRuntimeTempSlotLayout.SlotName(slotIndex);
        var containerSpec = spec.ToContainerSpec();
        var fullHome      = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sandboxHome));

        // home 属于 provisioning 的产物：这里只在其下建受管容器，缺失即视为未就绪。
        if (!Directory.Exists(fullHome))
            throw new DirectoryNotFoundException(
                $"The sandbox home '{sandboxHome}' does not exist; refusing to create runtime temp slots before provisioning.");

        // 兼容 sandboxHome 自身作为 junction 别名：跟随 home 链接打开后，直接从该已打开句柄读取规范最终路径，
        // 使返回的槽位路径、身份与回滚记录脱离易变别名，即使别名后续被撤销或改指仍可准确校验与回收。
        using var homeHandle = SandboxRuntimeTempSlotNative.OpenDirectory(
            fullHome,
            SandboxRuntimeTempSlotNative.FileTraverse | SandboxRuntimeTempSlotNative.FileReadAttributes |
            SandboxRuntimeTempSlotNative.Synchronize,
            SandboxRuntimeTempSlotNative.FileShareAll, "sandbox home", openReparsePoint: false);
        SandboxRuntimeTempSlotNative.RequireDirectory(homeHandle, fullHome, out _);

        var resolvedHome = SandboxFinalPathResolver.TryReadFinalPath(homeHandle);
        if (string.IsNullOrWhiteSpace(resolvedHome))
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                                     $"Failed to resolve the canonical final path of sandbox home '{sandboxHome}'.");

        var container    = SandboxRuntimeTempSlotLayout.ContainerDirectory(resolvedHome);
        var slots        = SandboxRuntimeTempSlotLayout.SlotsDirectory(resolvedHome);
        var slotPath     = SandboxRuntimeTempSlotLayout.SlotDirectory(resolvedHome, slotIndex);
        var writablePath = SandboxRuntimeTempSlotLayout.WritableDirectory(resolvedHome, slotIndex);

        using var containerHandle = OpenAndInitializeContainer(
            homeHandle, SandboxRuntimeTempSlotLayout.ContainerDirectoryName, container, containerSpec);
        cancellationToken.ThrowIfCancellationRequested();

        using var slotsHandle = OpenAndInitializeContainer(
            containerHandle, SandboxRuntimeTempSlotLayout.SlotsDirectoryName, slots, containerSpec);
        cancellationToken.ThrowIfCancellationRequested();

        // 在已校验且锁定的 slots 父句柄下原子 FILE_CREATE 新槽位根（创建瞬间即带受保护不可写 DACL，
        // 杜绝创建与设权之间的继承窗口），再在其句柄下原子创建带受保护写 DACL 的沙箱可写子目录 work。
        var slotRootDacl = SandboxRuntimeTempSlotAcl.BuildContainerDacl(containerSpec);
        var slotWorkDacl = SandboxRuntimeTempSlotAcl.BuildSlotDacl(spec);
        var slotHandle = SandboxRuntimeTempSlotNative.CreateNewChildDirectory(
            slotsHandle, slotName, SlotInitAccess, SandboxRuntimeTempSlotNative.FileShareReadWrite,
            slotPath, "runtime temp slot", slotRootDacl);
        SafeFileHandle?                 workHandle      = null;
        SandboxRuntimeTempSlotIdentity? createdIdentity = null;
        try
        {
            SandboxRuntimeTempSlotNative.RequireDirectory(slotHandle, slotPath, out _);
            createdIdentity = SandboxRuntimeTempSlotIdentity.Capture(slotHandle, slotPath);
            cancellationToken.ThrowIfCancellationRequested();
            afterSlotCreated?.Invoke(slotHandle);

            // 槽位根自身采用与容器一致的不可写受保护 DACL（DENY WriteBits + 最小穿越），沙箱无法删根、改权或原地设重解析点。
            SandboxRuntimeTempSlotAcl.ApplyContainerHandle(slotHandle, slotPath, containerSpec);
            SandboxRuntimeTempSlotContract.ValidateContainerHandle(slotHandle, slotPath, containerSpec);

            workHandle = SandboxRuntimeTempSlotNative.CreateNewChildDirectory(
                slotHandle, SandboxRuntimeTempSlotLayout.WritableDirectoryName, SlotInitAccess,
                SandboxRuntimeTempSlotNative.FileShareReadWrite, writablePath, "runtime temp slot writable directory",
                slotWorkDacl);
            SandboxRuntimeTempSlotNative.RequireDirectory(workHandle, writablePath, out _);
            var writableIdentity = SandboxRuntimeTempSlotIdentity.Capture(workHandle, writablePath);
            cancellationToken.ThrowIfCancellationRequested();

            SandboxRuntimeTempSlotAcl.ApplySlotHandle(workHandle, writablePath, spec);
            SandboxRuntimeTempSlotContract.ValidateWritableHandle(workHandle, writableIdentity, spec);
            workHandle.Dispose();
            workHandle = null;
            slotHandle.Dispose();
            return new SandboxRuntimeTempSlot(slotPath, createdIdentity, writablePath, writableIdentity, spec);
        }
        catch (Exception ex)
        {
            if (createdIdentity is null &&
                SandboxRuntimeTempSlotNative.TryGetFileIdInformation(slotHandle, out var fallbackInfo, out _))
                createdIdentity = SandboxRuntimeTempSlotIdentity.FromFileIdInformation(slotPath, fallbackInfo);

            // 若已创建 work 子目录，必须先删除并关闭 work 句柄，否则父目录 slot-XX 非空无法 delete-on-close。
            var workRollbackError = 0;
            if (workHandle is not null)
            {
                var (workMarked, workError) = MarkDeleteOnClose(workHandle);
                workHandle.Dispose();
                if (!workMarked) workRollbackError = workError;
            }

            var (marked, markError) = workRollbackError != 0
                ? (false, workRollbackError)
                : tryMarkDeleteOnClose is null
                    ? MarkDeleteOnClose(slotHandle)
                    : tryMarkDeleteOnClose(slotHandle);
            // 必须先关闭新建句柄，delete-on-close 才会生效，随后才能核实目录是否真正从命名空间消失。
            slotHandle.Dispose();

            if (!marked)
                throw new SandboxRuntimeTempSlotRollbackException(
                    slotPath, createdIdentity, markError, deletePendingOrRemaining: false, ex);

            var (missing, verifyError) = verifyPathMissing is null
                ? VerifyRemoved(slotPath)
                : verifyPathMissing(slotPath);
            if (!missing)
                throw new SandboxRuntimeTempSlotRollbackException(
                    slotPath, createdIdentity, verifyError, deletePendingOrRemaining: true, ex);

            throw;
        }
    }

    /// <summary>
    ///     <para>
    ///         以当前进程用户、SYSTEM、Administrators、共享沙箱组、实际沙箱账户 SID 与本槽位 capability SID
    ///         构造模板；沙箱账户 SID 为必填项，在写入任何 DACL 前即拒绝账户 SID 混入共享写主体或宽泛身份。
    ///     </para>
    ///     <para>
    ///         Builds a template from the current process user, SYSTEM, Administrators, shared sandbox groups,
    ///         actual sandbox account SIDs, and this slot's capability SID; sandbox account SIDs are required so
    ///         mixing an account SID into shared write principals or broad identities is refused before any DACL
    ///         is written.
    ///     </para>
    /// </summary>
    internal static SandboxRuntimeTempSlotAclSpec CreateHostTemplate(
        IReadOnlyList<SecurityIdentifier> sandboxSharedSids,
        IReadOnlyList<SecurityIdentifier> sandboxAccountSids,
        SecurityIdentifier                slotCapabilitySid)
    {
        SandboxRuntimeTempSlotNative.EnsureWindows();
        using var identity = WindowsIdentity.GetCurrent();
        var hostUser = identity.User ?? throw new InvalidOperationException("The current process has no user SID.");
        var management = new[]
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        };
        return SandboxRuntimeTempSlotAclSpec.Create(
            hostUser, management, sandboxSharedSids, sandboxAccountSids, slotCapabilitySid);
    }

    private static (bool Succeeded, int Error) MarkDeleteOnClose(SafeFileHandle handle)
    {
        var succeeded = SandboxRuntimeTempSlotNative.TryMarkDeleteOnClose(handle, out var error);
        return (succeeded, error);
    }

    private static (bool Missing, int Error) VerifyRemoved(string path)
    {
        try
        {
            return (SandboxRuntimeTempSlotNative.IsPathMissing(path), 0);
        }
        catch (Win32Exception ex)
        {
            // 例如 STATUS_DELETE_PENDING 经 GetFileAttributesW 常表现为 ERROR_ACCESS_DENIED (5)。
            return (false, ex.NativeErrorCode);
        }
    }

    internal static SafeFileHandle OpenAndInitializeContainer(
        SafeFileHandle                                     parentHandle,
        string                                             childName,
        string                                             fullPath,
        SandboxRuntimeTempContainerAclSpec                 spec,
        Action<SafeFileHandle, bool>?                      afterOpened          = null,
        Func<SafeFileHandle, (bool Succeeded, int Error)>? tryMarkDeleteOnClose = null,
        Func<string, (bool Missing, int Error)>?           verifyPathMissing    = null)
    {
        var initialProtectedDacl = SandboxRuntimeTempSlotAcl.BuildContainerDacl(spec);
        SafeFileHandle? handle;
        bool            created;
        try
        {
            // 先以 FILE_CREATE + ContainerInitAccess + FileShareAll 原子新建（不提前占用 DELETE，避免并发
            // 打开者因 FileShareReadWrite 命中共享冲突）；若目标已存在则改走无 DeleteAccess 的只读打开路径。
            handle = SandboxRuntimeTempSlotNative.CreateNewChildDirectory(
                parentHandle, childName, ContainerInitAccess, SandboxRuntimeTempSlotNative.FileShareAll,
                fullPath, "runtime temp slot container", initialProtectedDacl);
            created = true;
        }
        catch (IOException)
        {
            created = false;
            handle = SandboxRuntimeTempSlotNative.OpenChild(
                         parentHandle, childName, ContainerInitAccess, out var openError,
                         SandboxRuntimeTempSlotNative.FileShareReadWrite)
                  ?? throw new Win32Exception(openError, $"Cannot open runtime temp slot container '{fullPath}'.");
        }

        try
        {
            SandboxRuntimeTempSlotNative.RequireDirectory(handle, fullPath, out _);
            afterOpened?.Invoke(handle, created);
            // 仅对本次原子新建的容器补齐/核对 owner 与 DACL；已存在的容器绝不静默覆写修复，直接只读校验，不符即 fail-closed。
            if (created) SandboxRuntimeTempSlotAcl.ApplyContainerHandle(handle, fullPath, spec);
            SandboxRuntimeTempSlotContract.ValidateContainerHandle(handle, fullPath, spec);
            return handle;
        }
        catch (Exception ex)
        {
            if (!created)
            {
                handle.Dispose();
                throw;
            }

            // 仅当无并发 FileShareReadWrite 持有者时，同句柄重开 DeleteAccess 才能成功并标记 delete-on-close；
            // 若并发打开者尚存，重开会被 ERROR_SHARING_VIOLATION 拦截，从而避免 POSIX 删除把目录名摘除。
            var (marked, markError) = MarkCreatedContainerDeleteOnClose(handle, tryMarkDeleteOnClose);
            // 必须先关闭容器句柄，delete-on-close 才会生效，随后才能核实目录是否已从命名空间移除。
            handle.Dispose();

            if (!marked)
                throw new InvalidOperationException(
                    $"Initialization of runtime temp container '{fullPath}' failed ({ex.Message}), and " +
                    $"rollback could not mark the newly created directory for deletion (Win32 error {markError}).",
                    ex);

            var (missing, verifyError) = verifyPathMissing is null
                ? VerifyRemoved(fullPath)
                : verifyPathMissing(fullPath);
            if (!missing)
                throw new InvalidOperationException(
                    $"Initialization of runtime temp container '{fullPath}' failed ({ex.Message}), and " +
                    $"rollback could not confirm removal of the newly created directory (Win32 error {verifyError}).",
                    ex);

            throw;
        }
    }

    private static (bool Succeeded, int Error) MarkCreatedContainerDeleteOnClose(
        SafeFileHandle                                     handle,
        Func<SafeFileHandle, (bool Succeeded, int Error)>? tryMarkDeleteOnClose)
    {
        using var deleteHandle = SandboxRuntimeTempSlotNative.TryReOpenHandle(
            handle, SandboxRuntimeTempSlotNative.DeleteAccess | SandboxRuntimeTempSlotNative.Synchronize,
            SandboxRuntimeTempSlotNative.FileShareAll, out var reopenError);
        if (deleteHandle is null) return (false, reopenError);

        return tryMarkDeleteOnClose is null
            ? MarkDeleteOnClose(deleteHandle)
            : tryMarkDeleteOnClose(deleteHandle);
    }
}

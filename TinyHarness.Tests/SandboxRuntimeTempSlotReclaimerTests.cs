using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         离线验证句柄绑定保守回收：整树退出与身份前提、非法选项拒绝、重解析点只解除链接、无读权限文件删除、
///         硬链接与只读项保留并报告、深度/条目上界、瞬态共享冲突的有界重试与取消。
///     </para>
///     <para>
///         Offline validation of the handle-bound conservative reclaimer: whole-tree-exit and identity
///         preconditions, invalid-option refusal, reparse points losing only their link, deleting write-only files,
///         hard-linked and read-only entries retained and reported, depth/entry bounds, bounded retries of
///         transient sharing conflicts, and cancellation.
///     </para>
/// </summary>
[SupportedOSPlatform("windows")]
public class SandboxRuntimeTempSlotReclaimerTests
{
    [Test]
    public async Task Reclaim_RemovesTreeAndSlotRoot()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();
        var       slot = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        File.WriteAllText(Path.Combine(slot.WritableDirectory, "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(slot.WritableDirectory, "nested", "deeper"));
        File.WriteAllText(Path.Combine(slot.WritableDirectory, "nested", "deeper", "b.txt"), "b");

        var result = await ReclaimAsync(slot, wholeTreeExitConfirmed: true);

        Assert.That(result.Reclaimed, Is.True, string.Join("; ", result.Issues));
        Assert.That(result.Issues, Is.Empty);
        Assert.That(result.DeletedEntries, Is.EqualTo(2));
        Assert.That(Directory.Exists(slot.Directory), Is.False);
    }

    [Test]
    public async Task Reclaim_RefusesWhenProcessTreeExitIsUnconfirmed()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();
        var       slot = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        File.WriteAllText(Path.Combine(slot.WritableDirectory, "a.txt"), "a");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReclaimAsync(slot, wholeTreeExitConfirmed: false));
        TestAssert.Contains("not", error!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.That(Directory.Exists(slot.Directory), Is.True, "an unconfirmed slot must stay reserved");
        Assert.That(File.Exists(Path.Combine(slot.WritableDirectory, "a.txt")), Is.True);
    }

    [Test]
    public void Reclaim_RejectsInvalidOptions()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();
        var       slot = SandboxRuntimeTempSlotTestSupport.Initialize(home);

        Assert.Throws<ArgumentOutOfRangeException>(() => SandboxRuntimeTempSlotReclaimer.ReclaimAsync(
            slot, true, new SandboxRuntimeTempSlotReclaimOptions { MaximumDepth = -1 }, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => SandboxRuntimeTempSlotReclaimer.ReclaimAsync(
            slot, true, new SandboxRuntimeTempSlotReclaimOptions { MaximumEntries = 0 }, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => SandboxRuntimeTempSlotReclaimer.ReclaimAsync(
            slot, true, new SandboxRuntimeTempSlotReclaimOptions { MaximumRetries = -1 }, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => SandboxRuntimeTempSlotReclaimer.ReclaimAsync(
            slot, true, new SandboxRuntimeTempSlotReclaimOptions { RetryDelay = TimeSpan.FromMilliseconds(-1) },
            CancellationToken.None));
    }

    [Test]
    public async Task Reclaim_RefusesReplacedSlotDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home  = new TestTempDir();
        var       first = SandboxRuntimeTempSlotTestSupport.Initialize(
            home, 0, SandboxRuntimeTempSlotTestSupport.SlotCapabilitySid);
        var       other = SandboxRuntimeTempSlotTestSupport.Initialize(
            home, 1, SandboxRuntimeTempSlotTestSupport.SecondSlotCapabilitySid);
        File.WriteAllText(Path.Combine(first.Directory, "a.txt"), "a");

        var error = await Assert.ThrowsAsync<SandboxRuntimeTempSlotException>(
            () => ReclaimAsync(first with { Identity = other.Identity }, wholeTreeExitConfirmed: true));
        TestAssert.Contains("was replaced", error!.Message, StringComparison.Ordinal);
        Assert.That(Directory.Exists(first.Directory), Is.True);
    }

    [Test]
    public async Task Reclaim_UnlinksReparsePointWithoutFollowingTarget()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home    = new TestTempDir();
        var       slot    = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        var       outside = Path.Combine(home.Root, "outside");
        Directory.CreateDirectory(outside);
        var keptFile = Path.Combine(outside, "keep.txt");
        File.WriteAllText(keptFile, "keep");
        var link = Path.Combine(slot.WritableDirectory, "link");
        if (!SandboxRuntimeTempSlotTestSupport.TryCreateJunction(link, outside))
            Assert.Ignore("Directory junctions are unavailable on this machine.");

        var result = await ReclaimAsync(slot, wholeTreeExitConfirmed: true);

        Assert.That(result.Reclaimed, Is.True, string.Join("; ", result.Issues));
        Assert.That(result.Issues, Is.Empty);
        Assert.That(File.Exists(keptFile), Is.True, "the junction target must never be followed or deleted");
        Assert.That(Directory.Exists(slot.Directory), Is.False);
    }

    [Test]
    public async Task Reclaim_SucceedsWhenWritableChildTurnedIntoJunctionWithoutTouchingTarget()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home    = new TestTempDir();
        var       slot    = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        var       outside = Path.Combine(home.Root, "outside-target");
        Directory.CreateDirectory(outside);
        var keptFile = Path.Combine(outside, "keep.txt");
        File.WriteAllText(keptFile, "keep");

        // 模拟运行期可写子目录（slot-XX\work）被原地改为指向外部目录的重解析点：
        // 由于槽位根（slot-XX）自身不可写，回收器锚定 slot-XX 并把 work 作为子重解析点仅解除链接，绝不泄漏槽位。
        Directory.Delete(slot.WritableDirectory);
        if (!SandboxRuntimeTempSlotTestSupport.TryCreateJunction(slot.WritableDirectory, outside))
            Assert.Ignore("Directory junctions are unavailable on this machine.");

        // 校验器必须拒绝可写子目录已被重解析点替换的槽位，而回收器仍可安全回收该槽位。
        Assert.Throws<SandboxRuntimeTempSlotException>(() => SandboxRuntimeTempSlotContract.ValidateSlot(slot));
        var result = await ReclaimAsync(slot, wholeTreeExitConfirmed: true);

        Assert.That(result.Reclaimed, Is.True, string.Join("; ", result.Issues));
        Assert.That(result.Issues, Is.Empty);
        Assert.That(File.Exists(keptFile), Is.True, "the junction target must remain untouched");
        Assert.That(Directory.Exists(slot.Directory), Is.False);
    }

    [Test]
    public async Task Reclaim_DeletesFileWithoutReadDataPermission()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home      = new TestTempDir();
        var       slot      = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        var       writeOnly = Path.Combine(slot.WritableDirectory, "write-only.txt");
        File.WriteAllText(writeOnly, "payload");

        // 移除当前宿主的 ReadData，仅保留 Delete / ReadAttributes / Synchronize，验证回收器不申请多余读权限。
        var info     = new FileInfo(writeOnly);
        var security = info.GetAccessControl(AccessControlSections.Access);
        var hostSid  = new SecurityIdentifier(SandboxRuntimeTempSlotTestSupport.HostUserSid());
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(
            hostSid,
            FileSystemRights.Delete | FileSystemRights.ReadAttributes | FileSystemRights.Synchronize |
            FileSystemRights.ChangePermissions,
            AccessControlType.Allow));
        info.SetAccessControl(security);

        var result = await ReclaimAsync(slot, wholeTreeExitConfirmed: true);

        Assert.That(result.Reclaimed, Is.True, string.Join("; ", result.Issues));
        Assert.That(result.Issues, Is.Empty);
        Assert.That(File.Exists(writeOnly), Is.False);
    }

    [Test]
    public async Task Reclaim_RetainsReadOnlyAndHardLinkedFilesWithReasons()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home         = new TestTempDir();
        var       slot         = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        var       readOnlyPath = Path.Combine(slot.WritableDirectory, "readonly.txt");
        File.WriteAllText(readOnlyPath, "read-only");
        File.SetAttributes(readOnlyPath, FileAttributes.ReadOnly);
        var sharedPath = Path.Combine(home.Root, "shared.txt");
        File.WriteAllText(sharedPath, "shared");
        var hardLinkPath = Path.Combine(slot.WritableDirectory, "hardlink.txt");
        var hasHardLink  = SandboxRuntimeTempSlotTestSupport.TryCreateHardLink(hardLinkPath, sharedPath);

        try
        {
            var result = await ReclaimAsync(slot, wholeTreeExitConfirmed: true);

            Assert.That(result.Reclaimed, Is.False);
            Assert.That(result.Issues.Any(issue => issue.Path == readOnlyPath &&
                                                   issue.Reason.Contains("read-only", StringComparison.Ordinal)),
                        Is.True);
            Assert.That(File.Exists(readOnlyPath), Is.True);
            Assert.That((File.GetAttributes(readOnlyPath) & FileAttributes.ReadOnly) != 0, Is.True,
                        "the read-only attribute must never be cleared");
            Assert.That(File.ReadAllText(sharedPath), Is.EqualTo("shared"),
                        "a file shared through a hard link outside the slot must stay untouched");
            if (hasHardLink)
                Assert.That(result.Issues.Any(issue => issue.Path == hardLinkPath &&
                                                       issue.Reason.Contains("hard-linked", StringComparison.Ordinal)),
                            Is.True);
        }
        finally
        {
            // 测试资源清理：只读属性由测试自己清除，回收器不会这么做。
            if (File.Exists(readOnlyPath)) File.SetAttributes(readOnlyPath, FileAttributes.Normal);
        }
    }

    [Test]
    public async Task Reclaim_EnforcesDepthBound()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();
        var       slot = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        var       deep = Path.Combine(slot.WritableDirectory, "a", "b", "c", "d");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "x.txt"), "x");

        var result = await ReclaimAsync(slot, wholeTreeExitConfirmed: true,
                                        new SandboxRuntimeTempSlotReclaimOptions { MaximumDepth = 1 });

        Assert.That(result.Reclaimed, Is.False);
        Assert.That(result.Issues.Any(issue => issue.Reason.Contains("depth", StringComparison.Ordinal)), Is.True,
                    string.Join("; ", result.Issues));
        Assert.That(Directory.Exists(deep), Is.True);

        var cleanup = await ReclaimAsync(slot, wholeTreeExitConfirmed: true);
        Assert.That(cleanup.Reclaimed, Is.True);
    }

    [Test]
    public async Task Reclaim_EnforcesEntryBound()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();
        var       slot = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        for (var index = 0; index < 5; index++)
            File.WriteAllText(Path.Combine(slot.WritableDirectory, $"file-{index}.txt"), index.ToString());

        var result = await ReclaimAsync(slot, wholeTreeExitConfirmed: true,
                                        new SandboxRuntimeTempSlotReclaimOptions { MaximumEntries = 2 });

        Assert.That(result.Reclaimed, Is.False);
        Assert.That(result.Issues.Any(issue => issue.Reason.Contains("entry-count", StringComparison.Ordinal)),
                    Is.True);

        var cleanup = await ReclaimAsync(slot, wholeTreeExitConfirmed: true);
        Assert.That(cleanup.Reclaimed, Is.True);
    }

    [Test]
    public async Task Reclaim_EnforcesSharedEntryBoundAcrossNestedDirectoryEnumeration()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();
        var       slot = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        for (var dirIndex = 0; dirIndex < 3; dirIndex++)
        {
            var sub = Path.Combine(slot.WritableDirectory, $"dir-{dirIndex}");
            Directory.CreateDirectory(sub);
            for (var fileIndex = 0; fileIndex < 3; fileIndex++)
                File.WriteAllText(Path.Combine(sub, $"f-{fileIndex}.txt"), "x");
        }

        // 整树共 1 (work) + 3 (dir) + 9 (file) = 13 项；MaximumEntries=4 时枚举与遍历共用同一预算，不会按深度倍增。
        var result = await ReclaimAsync(slot, wholeTreeExitConfirmed: true,
                                        new SandboxRuntimeTempSlotReclaimOptions { MaximumEntries = 4 });

        Assert.Multiple(() =>
        {
            Assert.That(result.Reclaimed, Is.False);
            Assert.That(result.Issues.Any(issue => issue.Reason.Contains("entry-count", StringComparison.Ordinal)),
                        Is.True, string.Join("; ", result.Issues));
            Assert.That(result.DeletedEntries + result.DeletedDirectories, Is.LessThanOrEqualTo(4));
        });

        var cleanup = await ReclaimAsync(slot, wholeTreeExitConfirmed: true);
        Assert.That(cleanup.Reclaimed, Is.True);
    }

    [Test]
    public async Task Reclaim_ReturnsDiagnosticIssueInsteadOfThrowingWhenDeleteConfirmationFails()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();
        var       slot = SandboxRuntimeTempSlotTestSupport.Initialize(home);

        var result = await SandboxRuntimeTempSlotReclaimer.ReclaimAsync(
            slot, wholeTreeExitConfirmed: true, options: null, CancellationToken.None,
            isPathMissing: _ => throw new System.ComponentModel.Win32Exception(5, "Access denied (delete-pending)"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Reclaimed, Is.False);
            Assert.That(result.Issues.Any(issue => issue.Path == slot.Directory &&
                                                   issue.Reason.Contains("Win32 error 5", StringComparison.Ordinal)),
                        Is.True, string.Join("; ", result.Issues));
        });
    }

    [Test]
    public async Task Reclaim_ReportsTransientSharingConflictWithinBounds()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();
        var       slot = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        var       busy = Path.Combine(slot.WritableDirectory, "busy.txt");
        File.WriteAllText(busy, "busy");

        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await ReclaimAsync(slot, wholeTreeExitConfirmed: true,
                                            new SandboxRuntimeTempSlotReclaimOptions
                                            {
                                                MaximumRetries = 1,
                                                RetryDelay     = TimeSpan.FromMilliseconds(10)
                                            });
            Assert.That(result.Reclaimed, Is.False);
            Assert.That(result.Issues.Any(issue => issue.Path == busy &&
                                                   issue.Reason.Contains("32", StringComparison.Ordinal)), Is.True);
        }

        var afterRelease = await ReclaimAsync(slot, wholeTreeExitConfirmed: true);
        Assert.That(afterRelease.Reclaimed, Is.True);
    }

    [Test]
    public async Task Reclaim_HonorsCancellationBeforeStartAndDuringRetryDelay()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home = new TestTempDir();
        var       slot = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        var       busy = Path.Combine(slot.WritableDirectory, "busy.txt");
        File.WriteAllText(busy, "busy");

        using (var preCancelled = new CancellationTokenSource())
        {
            preCancelled.Cancel();
            await Assert.CatchAsync<OperationCanceledException>(
                () => SandboxRuntimeTempSlotReclaimer.ReclaimAsync(slot, true, null, preCancelled.Token));
        }

        Assert.That(Directory.Exists(slot.Directory), Is.True);

        // 重试等待期间取消必须立即中断，而不是死等长 RetryDelay。
        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var duringRetry = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            await Assert.CatchAsync<OperationCanceledException>(() => SandboxRuntimeTempSlotReclaimer.ReclaimAsync(
                slot, true,
                new SandboxRuntimeTempSlotReclaimOptions
                {
                    MaximumRetries = 5,
                    RetryDelay     = TimeSpan.FromSeconds(10)
                },
                duringRetry.Token));
        }

        var cleanup = await ReclaimAsync(slot, wholeTreeExitConfirmed: true);
        Assert.That(cleanup.Reclaimed, Is.True);
    }

    [Test]
    public void OpenChild_PinsSubdirectoryLocationAgainstConcurrentMove()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home  = new TestTempDir();
        var       slot  = SandboxRuntimeTempSlotTestSupport.Initialize(home);
        var       child = Path.Combine(slot.Directory, "pinned");
        var       moved = Path.Combine(home.Root, "escaped");
        Directory.CreateDirectory(child);

        using var rootHandle = SandboxRuntimeTempSlotNative.OpenDirectory(
            slot.Directory,
            SandboxRuntimeTempSlotNative.FileListDirectory | SandboxRuntimeTempSlotNative.FileReadAttributes |
            SandboxRuntimeTempSlotNative.Synchronize,
            SandboxRuntimeTempSlotNative.FileShareReadWrite, "slot root");
        using var childHandle = SandboxRuntimeTempSlotNative.OpenChild(
            rootHandle, "pinned",
            SandboxRuntimeTempSlotNative.DeleteAccess | SandboxRuntimeTempSlotNative.FileReadAttributes |
            SandboxRuntimeTempSlotNative.Synchronize,
            out var openError);

        Assert.That(childHandle, Is.Not.Null, $"OpenChild failed (Win32 error {openError}).");
        // 默认不共享 FILE_SHARE_DELETE：句柄持有期间任何并发方都无法将子目录移出槽位。
        Assert.Throws<IOException>(() => Directory.Move(child, moved));
        Assert.That(Directory.Exists(child), Is.True);
        Assert.That(Directory.Exists(moved), Is.False);
    }

    private static Task<SandboxRuntimeTempSlotReclaimResult> ReclaimAsync(
        SandboxRuntimeTempSlot                slot,
        bool                                  wholeTreeExitConfirmed,
        SandboxRuntimeTempSlotReclaimOptions? options = null) =>
        SandboxRuntimeTempSlotReclaimer.ReclaimAsync(slot, wholeTreeExitConfirmed, options, CancellationToken.None);
}

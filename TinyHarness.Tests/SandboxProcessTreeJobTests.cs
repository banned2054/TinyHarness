using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         离线验证宿主外层 Job：真实 <c>CREATE_SUSPENDED</c> 挂载再恢复、后台后代在根进程退出后仍占用 Job、
///         限时等待清空、严格模式超时或取消时终止整树并报告不确定性、查询失败 fail-closed，以及挂载或恢复
///         失败清理不泄漏进程。
///     </para>
///     <para>
///         Offline validation of the host outer job: real <c>CREATE_SUSPENDED</c> attach-then-resume, a
///         background descendant keeping the job occupied after the root exits, bounded drain waits, strict-mode
///         whole-tree termination on timeout or cancellation, fail-closed queries, and failure-path cleanup that
///         never leaks a suspended process.
///     </para>
/// </summary>
[SupportedOSPlatform("windows")]
public class SandboxProcessTreeJobTests
{
    private const uint CreateNoWindow  = 0x0800_0000;
    private const uint CreateSuspended = 0x0000_0004;

    private static string WaitForPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                                      "waitfor.exe");

    private static string ComSpec => Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

    [Test]
    public async Task Job_TracksAssignedProcessUntilItExits()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var job   = SandboxProcessTreeJob.Create();
        using var child = StartWaitFor(UniqueSignal("job"), seconds: 30);
        try
        {
            Assert.That(job.TryAssign(child.SafeHandle), Is.True);
            Assert.That(job.ActiveProcesses, Is.GreaterThanOrEqualTo(1));

            child.Kill();
            Assert.That(child.WaitForExit(15_000), Is.True);
            Assert.That(await job.WaitForEmptyAsync(TimeSpan.FromSeconds(15), CancellationToken.None), Is.True);
            Assert.That(job.ActiveProcesses, Is.Zero);
        }
        finally
        {
            TryTerminateJob(job);
        }
    }

    [Test]
    public async Task AttachSuspendedProcess_AttachesBeforeResumeAndKeepsBackgroundDescendantInJob()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var scripts = new TestTempDir();
        using var job     = SandboxProcessTreeJob.Create();
        var       signal  = UniqueSignal("desc");
        var       script  = Path.Combine(scripts.Root, "spawn-descendant.cmd");
        File.WriteAllText(
            script,
            "@echo off\r\n" +
            $"start /b \"\" \"{WaitForPath}\" /t 20 {signal}\r\n" +
            "exit /b 0\r\n");

        // 以真实 CREATE_SUSPENDED 启动 cmd.exe，先挂入外层 Job 再恢复主线程，彻底消除子进程逃逸竞态。
        var (processHandle, threadHandle, processId) = StartSuspended($"\"{ComSpec}\" /c \"{script}\"");
        try
        {
            SandboxProcessTreeJob.AttachSuspendedProcess(processHandle, threadHandle, job, processId, "probe");
            using (processHandle)
            {
                using var root = Process.GetProcessById((int)processId);
                Assert.That(root.WaitForExit(15_000), Is.True,
                            "the shell root must exit while its descendant survives");
            }

            // 根进程退出后后代仍在同一 Job 内：ActiveProcesses 保持非零，槽位因此不得提前回收。
            Assert.That(await job.WaitForEmptyAsync(TimeSpan.FromMilliseconds(500), CancellationToken.None), Is.False);
            Assert.That(job.ActiveProcesses, Is.GreaterThanOrEqualTo(1));

            // 严格模式：限期等待超时后终止外层 Job 并再次确认整树退出。
            var drainedNaturally = await SandboxProcessTreeExit.ConfirmWholeTreeExitAsync(
                job, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(20), CancellationToken.None);
            Assert.That(drainedNaturally, Is.False);
            Assert.That(job.ActiveProcesses, Is.Zero);
        }
        finally
        {
            TryTerminateJob(job);
        }
    }

    [Test]
    public async Task Job_ConfirmsNaturalDrainWithoutTermination()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var job   = SandboxProcessTreeJob.Create();
        using var child = StartWaitFor(UniqueSignal("short"), seconds: 1);
        try
        {
            Assert.That(job.TryAssign(child.SafeHandle), Is.True);

            var drainedNaturally = await SandboxProcessTreeExit.ConfirmWholeTreeExitAsync(
                job, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.That(drainedNaturally, Is.True);
        }
        finally
        {
            TryTerminateJob(job);
        }
    }

    [Test]
    public async Task Job_ConfirmWholeTreeExitTerminatesAndReportsUncertaintyWhenCancelled()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var job   = SandboxProcessTreeJob.Create();
        using var child = StartWaitFor(UniqueSignal("cancel"), seconds: 30);
        try
        {
            Assert.That(job.TryAssign(child.SafeHandle), Is.True);
            using var source = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            var error = await Assert.ThrowsAsync<ToolExecutionUncertainException>(
                () => SandboxProcessTreeExit.ConfirmWholeTreeExitAsync(
                    job, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), source.Token));
            TestAssert.Contains("cancelled", error!.Message, StringComparison.OrdinalIgnoreCase);
            Assert.That(error.InnerException, Is.InstanceOf<OperationCanceledException>());
            Assert.That(await job.WaitForEmptyAsync(TimeSpan.FromSeconds(15), CancellationToken.None), Is.True,
                        "cancellation during whole-tree confirmation must still terminate the outer job");
        }
        finally
        {
            TryTerminateJob(job);
        }
    }

    [Test]
    public void Job_ActiveProcessQueryFailsClosedAfterDispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        var job = SandboxProcessTreeJob.Create();
        job.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = job.ActiveProcesses);
    }

    [Test]
    public async Task Job_ConfirmWholeTreeExitReportsUncertaintyWhenJobIsUnusable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var job = SandboxProcessTreeJob.Create();
        job.Dispose();

        var error = await Assert.ThrowsAsync<ToolExecutionUncertainException>(
            () => SandboxProcessTreeExit.ConfirmWholeTreeExitAsync(
                job, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), CancellationToken.None));
        TestAssert.Contains("could not be confirmed", error!.Message, StringComparison.Ordinal);
    }

    [Test]
    public void AttachSuspendedProcess_TerminatesProcessWhenJobAssignmentFails()
    {
        if (!OperatingSystem.IsWindows()) return;
        var job = SandboxProcessTreeJob.Create();
        job.Dispose();
        var (processHandle, threadHandle, processId) = StartSuspended($"\"{WaitForPath}\" /t 30 {UniqueSignal("assign")}");
        using var observer = Process.GetProcessById((int)processId);

        Exception? failure = null;
        try
        {
            SandboxProcessTreeJob.AttachSuspendedProcess(processHandle, threadHandle, job, processId, "probe");
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        Assert.That(failure, Is.Not.Null);
        Assert.That(failure, Is.InstanceOf<InvalidOperationException>().Or.InstanceOf<Win32Exception>());
        Assert.That(observer.WaitForExit(15_000), Is.True,
                    "an attach failure must terminate the suspended process instead of leaking it");
    }

    [Test]
    public void AttachSuspendedProcess_TerminatesProcessWhenResumeFails()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var job = SandboxProcessTreeJob.Create();
        var (processHandle, realThread, processId) = StartSuspended($"\"{WaitForPath}\" /t 30 {UniqueSignal("resume")}");
        realThread.Dispose();
        using var observer      = Process.GetProcessById((int)processId);
        using var invalidThread = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);

        var error = Assert.Throws<Win32Exception>(
            () => SandboxProcessTreeJob.AttachSuspendedProcess(processHandle, invalidThread, job, processId, "probe"));
        TestAssert.Contains("Failed to resume", error!.Message, StringComparison.Ordinal);
        Assert.That(observer.WaitForExit(15_000), Is.True,
                    "a resume failure must terminate the suspended process instead of leaking it");
    }

    [Test]
    public async Task ConfirmFailedLaunchTreeExit_DrainsLiveProcessAndAllowsOriginalErrorToRethrow()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var job   = SandboxProcessTreeJob.Create();
        using var child = StartWaitFor(UniqueSignal("lnch"), seconds: 30);
        try
        {
            Assert.That(job.TryAssign(child.SafeHandle), Is.True);
            Assert.That(job.ActiveProcesses, Is.GreaterThanOrEqualTo(1));

            await WindowsSandboxRunnerLauncher.ConfirmFailedLaunchTreeExitAsync(
                job, new TimeoutException("pipe connect timeout"), TimeSpan.FromSeconds(15));

            Assert.That(job.ActiveProcesses, Is.Zero);
        }
        finally
        {
            TryTerminateJob(job);
        }
    }

    [Test]
    public async Task ConfirmFailedLaunchTreeExit_ThrowsUncertainWhenOuterJobCannotConfirmExit()
    {
        if (!OperatingSystem.IsWindows()) return;
        var job = SandboxProcessTreeJob.Create();
        job.Dispose();

        var error = await Assert.ThrowsAsync<ToolExecutionUncertainException>(
            () => WindowsSandboxRunnerLauncher.ConfirmFailedLaunchTreeExitAsync(
                job, new TimeoutException("pipe connect timeout"), TimeSpan.FromMilliseconds(50)));

        TestAssert.Contains("pipe connect timeout", error!.Message, StringComparison.Ordinal);
        TestAssert.Contains("whole-tree exit could not be confirmed", error.Message, StringComparison.Ordinal);
        Assert.That(error.InnerException, Is.InstanceOf<AggregateException>());
    }

    [Test]
    public async Task VerticalLoop_OuterJobWholeTreeExitEvidenceGatesSameSlotReclamation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home    = new TestTempDir();
        using var scripts = new TestTempDir();
        using var job     = SandboxProcessTreeJob.Create();
        var       slot    = SandboxRuntimeTempSlotTestSupport.Initialize(home, 0);
        SandboxRuntimeTempSlotContract.ValidateSlot(slot);

        var markerFile = Path.Combine(slot.WritableDirectory, "from-tree.txt");
        var signal     = UniqueSignal("vert");
        var script     = Path.Combine(scripts.Root, "write-and-spawn.cmd");
        File.WriteAllText(
            script,
            "@echo off\r\n" +
            $"echo payload>\"{markerFile}\"\r\n" +
            $"start /b \"\" \"{WaitForPath}\" /t 20 {signal}\r\n" +
            "exit /b 0\r\n");

        // 垂直闭环：槽位初始化 → CREATE_SUSPENDED 挂外层 Job 写槽位并留后台后代 → 后代未退前拒回收 → 整树确认退出后回收同一槽位并重建。
        var (processHandle, threadHandle, processId) = StartSuspended($"\"{ComSpec}\" /c \"{script}\"");
        try
        {
            SandboxProcessTreeJob.AttachSuspendedProcess(processHandle, threadHandle, job, processId, "probe");
            using (processHandle)
            {
                using var root = Process.GetProcessById((int)processId);
                Assert.That(root.WaitForExit(15_000), Is.True);
            }

            Assert.That(File.Exists(markerFile), Is.True);
            Assert.That(await job.WaitForEmptyAsync(TimeSpan.FromMilliseconds(500), CancellationToken.None), Is.False);
            Assert.That(job.ActiveProcesses, Is.GreaterThanOrEqualTo(1));

            // 整树退出证据不足：回收同一槽位必须被拒绝，目录与文件原样保留。
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => SandboxRuntimeTempSlotReclaimer.ReclaimAsync(
                    slot, wholeTreeExitConfirmed: job.ActiveProcesses == 0, options: null, CancellationToken.None));
            Assert.That(Directory.Exists(slot.Directory), Is.True);

            // 确认整树退出（ActiveProcesses == 0）后，同一槽位可安全回收并允许同索引重建。
            var drainedNaturally = await SandboxProcessTreeExit.ConfirmWholeTreeExitAsync(
                job, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(20), CancellationToken.None);
            Assert.That(drainedNaturally, Is.False);
            Assert.That(job.ActiveProcesses, Is.Zero);

            var reclaimed = await SandboxRuntimeTempSlotReclaimer.ReclaimAsync(
                slot, wholeTreeExitConfirmed: job.ActiveProcesses == 0, options: null, CancellationToken.None);
            Assert.That(reclaimed.Reclaimed, Is.True, string.Join("; ", reclaimed.Issues));
            Assert.That(reclaimed.DeletedEntries, Is.EqualTo(1));
            Assert.That(Directory.Exists(slot.Directory), Is.False);

            var recreated = SandboxRuntimeTempSlotTestSupport.Initialize(home, 0);
            SandboxRuntimeTempSlotContract.ValidateSlot(recreated);
            Assert.That(Directory.Exists(recreated.WritableDirectory), Is.True);
        }
        finally
        {
            TryTerminateJob(job);
        }
    }

    private static string UniqueSignal(string prefix) => $"TH{prefix}{Guid.NewGuid():N}"[..24];

    private static Process StartWaitFor(string name, int seconds)
    {
        var startInfo = new ProcessStartInfo(WaitForPath) { UseShellExecute = false, CreateNoWindow = true };
        startInfo.ArgumentList.Add("/t");
        startInfo.ArgumentList.Add(seconds.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(name);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start waitfor.exe.");
    }

    private static (SafeProcessHandle Process, SafeFileHandle Thread, uint ProcessId) StartSuspended(string commandLine)
    {
        var startupInfo = new StartupInfoW { Size = (uint)Marshal.SizeOf<StartupInfoW>() };
        var processInfo = default(ProcessInformation);
        if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                            CreateNoWindow | CreateSuspended, IntPtr.Zero, null, ref startupInfo, ref processInfo))
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                                     $"Failed to launch suspended test process: {commandLine}");

        return (new SafeProcessHandle(processInfo.ProcessHandle, ownsHandle: true),
                new SafeFileHandle(processInfo.ThreadHandle, ownsHandle: true),
                processInfo.ProcessId);
    }

    private static void TryTerminateJob(SandboxProcessTreeJob job)
    {
        try
        {
            job.Terminate();
        }
        catch (Exception ex) when (ex is Win32Exception or ObjectDisposedException)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoW
    {
        public uint   Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint   X;
        public uint   Y;
        public uint   XSize;
        public uint   YSize;
        public uint   XCountChars;
        public uint   YCountChars;
        public uint   FillAttribute;
        public uint   Flags;
        public ushort ShowWindow;
        public ushort Reserved2Bytes;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public uint   ProcessId;
        public uint   ThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string?                applicationName,
        string                 commandLine,
        IntPtr                 processAttributes,
        IntPtr                 threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint                   creationFlags,
        IntPtr                 environment,
        string?                currentDirectory,
        ref StartupInfoW       startupInfo,
        ref ProcessInformation processInformation);
}

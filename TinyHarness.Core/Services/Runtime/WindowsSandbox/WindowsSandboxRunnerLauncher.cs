using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     启动一次命令的 runner 进程：创建仅沙箱账户可访问的管道对，以沙箱账户
///     CreateProcessWithLogonW 启动 runner，等待双管道连接并校验客户端 PID 与 runner PID
///     一致（防管道抢连）。
///     Launches the per-command runner process: creates the pipe pair accessible
///     only to the sandbox account, starts the runner via CreateProcessWithLogonW
///     under that account, waits for both pipe connections, and verifies the
///     client PID equals the runner PID (guarding against pipe squatting).
/// </summary>
public interface ISandboxRunnerLauncher
{
    Task<SandboxRunnerConnection> LaunchAsync(RunnerLaunchRequest request, CancellationToken cancellationToken);
}

/// <summary>
///     <para>
///         runner 启动请求：runner 路径、沙箱账户（含明文密码，仅用于本次登录）、命令工作目录，
///         以及是否以 LOGON_WITH_PROFILE 加载账户 profile。
///     </para>
///     <para>
///         The runner launch request: runner path, the sandbox account (its plaintext
///         password used only for this logon), the command working directory, and
///         whether the account profile is loaded via LOGON_WITH_PROFILE.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         LoadUserProfile 仅应在生效在线时为 true：profile 加载使 LSASS 登录会话具备
///         schannel 客户端凭据，缺失时沙箱内 TLS 全部以 SEC_E_NO_CREDENTIALS 失败；
///         账户 profile 目录（C:\Users\&lt;account&gt;）随之成为该独立账户的可写面，属有意
///         为之的安全权衡，仅 online 路径启用；offline 保持不加载，不扩大可写面。
///     </para>
///     <para>
///         LoadUserProfile should be true only while the effective network is online:
///         loading the profile gives the LSASS logon session schannel client credentials —
///         without it every TLS handshake in the sandbox fails with SEC_E_NO_CREDENTIALS.
///         The account profile directory (C:\Users\&lt;account&gt;) thereby becomes writable
///         surface for that standalone account, a deliberate security tradeoff enabled only
///         on the online path; offline keeps profile loading off, leaving that surface closed.
///     </para>
/// </remarks>
public sealed record RunnerLaunchRequest(
    string                RunnerExecutablePath,
    WindowsSandboxAccount Account,
    string                WorkingDirectory,
    bool                  LoadUserProfile = false)
{
    /// <summary>
    ///     <para>
    ///         外层 Job 的进程树策略；默认 <see cref="SandboxProcessTreeMode.Disabled" /> 保持既有启动路径不变，
    ///         由调用方按普通 Shell 与授权执行语义显式选择。
    ///     </para>
    ///     <para>
    ///         The outer-job process-tree policy; the default <see cref="SandboxProcessTreeMode.Disabled" /> keeps
    ///         the existing launch path unchanged, and callers select it explicitly for ordinary shell versus
    ///         authorized execution semantics.
    ///     </para>
    /// </summary>
    internal SandboxProcessTreeMode ProcessTreeMode { get; init; } = SandboxProcessTreeMode.Disabled;
}

/// <summary>
///     一条 runner 命令连接：Downstream 为父→runner（spawn/terminate），Upstream 为
///     runner→父（ready/output/exit）；WaitForExitAsync/Kill 表达 runner 进程 OS 状态。
///     runner 的 OS 退出码不是命令结果，不在这里暴露为业务退出码。
///     One runner command connection: Downstream carries parent→runner frames
///     (spawn/terminate) and Upstream runner→parent frames (ready/output/exit);
///     WaitForExitAsync/Kill expose the runner process's OS state. The runner's
///     OS exit code is not the command result and is never surfaced as one here.
/// </summary>
public abstract class SandboxRunnerConnection(Stream downstream, Stream upstream) : IDisposable
{
    public Stream Downstream { get; } = downstream;

    public Stream Upstream { get; } = upstream;

    public abstract void Dispose();

    /// <summary>
    ///     限时等待 runner 进程退出；超时返回 false，不杀进程；句柄等待失败（WAIT_FAILED）
    ///     抛 Win32Exception。
    ///     Waits for the runner process to exit within the deadline; returns false
    ///     on timeout without killing; a failed handle wait (WAIT_FAILED) throws
    ///     Win32Exception.
    /// </summary>
    public abstract Task<bool> WaitForExitAsync(TimeSpan timeout);

    /// <summary>
    ///     终止 runner 进程；语义为 TerminateProcess，不保证完整回收后代。
    ///     Terminates the runner process; the semantics are TerminateProcess and
    ///     full descendant reclamation is not guaranteed.
    /// </summary>
    public abstract void Kill();

    /// <summary>
    ///     <para>
    ///         外层 Job 追踪器；未启用外层 Job 时为 null。普通模式应在释放连接前调用
    ///         <see cref="DetachProcessTree" />，让后台后代继续存活并由独立追踪器负责清空判定。
    ///     </para>
    ///     <para>
    ///         The outer-job tracker, or null when no outer job is enabled. Ordinary mode should call
    ///         <see cref="DetachProcessTree" /> before disposing the connection so background descendants keep
    ///         running while a dedicated tracker owns the drain decision.
    ///     </para>
    /// </summary>
    internal virtual SandboxProcessTreeJob? ProcessTree => null;

    /// <summary>
    ///     <para>移交外层 Job 的所有权并返回它；移交后连接不再释放该句柄。</para>
    ///     <para>
    ///         Transfers ownership of the outer job and returns it; after the transfer the connection no longer
    ///         disposes that handle.
    ///     </para>
    /// </summary>
    internal virtual SandboxProcessTreeJob? DetachProcessTree() => null;
}

/// <summary>
///     <see cref="ISandboxRunnerLauncher" /> 的 Windows 实现（命名管道 + CreateProcessWithLogonW）。
///     The Windows implementation of <see cref="ISandboxRunnerLauncher" /> (named
///     pipes plus CreateProcessWithLogonW).
/// </summary>
public sealed partial class WindowsSandboxRunnerLauncher(TimeSpan? connectTimeout = null) : ISandboxRunnerLauncher
{
    private const uint CreateNoWindow              = 0x0800_0000;
    private const uint CreateSuspended             = 0x0000_0004;
    private const uint CreateUnicodeEnvironment    = 0x0000_0400;
    private const uint LogonWithProfile            = 0x0000_0001; // LOGON_WITH_PROFILE
    private const uint StartfForceOffFeedback      = 0x0000_0020;
    private const uint ErrorModeFailCriticalErrors = 0x0000_0001;
    private const uint ErrorModeNoGpFaultErrorBox  = 0x0000_0002;
    private const uint WaitFailed                  = 0xFFFF_FFFF; // WAIT_FAILED

    /// <summary>
    ///     与库一致的管道连接等待期限。
    ///     The pipe connection deadline, matching the library.
    /// </summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly TimeSpan _connectTimeout = connectTimeout ?? DefaultConnectTimeout;

    public async Task<SandboxRunnerConnection> LaunchAsync(
        RunnerLaunchRequest request, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw
                new PlatformNotSupportedException("The Windows sandbox runner launcher is available on Windows only.");

        ArgumentNullException.ThrowIfNull(request);

        var nonce       = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var pipeInName  = $"codex-runner-{nonce}-in";
        var pipeOutName = $"codex-runner-{nonce}-out";

        var pipeSecurity = new PipeSecurity();
        // The sandbox account only reads and writes the pipes; the parent
        // keeps full rights implicitly through ownership, so WRITE_DAC and
        // DELETE are not granted to the sandboxed side.
        pipeSecurity.AddAccessRule(new PipeAccessRule(request.Account.AccountSid,
                                                      PipeAccessRights.ReadWrite, AccessControlType.Allow));

        NamedPipeServerStream?   downstream  = null;
        NamedPipeServerStream?   upstream    = null;
        RunnerProcessConnection? connection  = null;
        SandboxProcessTreeJob?   processTree = null;
        try
        {
            // 外层 Job 在启动前创建：挂载失败时 runner 仍在挂起态，不会留下未被跟踪的子进程。
            if (request.ProcessTreeMode != SandboxProcessTreeMode.Disabled) processTree = SandboxProcessTreeJob.Create();
            // parent writes into -in (Out direction) and reads from -out (In).
            downstream = NamedPipeServerStreamAcl.Create(pipeInName, PipeDirection.Out, 1,
                                                         PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                                                         64 * 1024, 64 * 1024, pipeSecurity);
            upstream = NamedPipeServerStreamAcl.Create(pipeOutName, PipeDirection.In, 1,
                                                       PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                                                       64 * 1024, 64 * 1024, pipeSecurity);

            var process = StartRunnerProcess(request, pipeInName, pipeOutName, processTree);
            try
            {
                await WaitForConnectionAndVerifyAsync(downstream, upstream, process.ProcessId,
                                                      cancellationToken).ConfigureAwait(false);
            }
            catch (Exception launchFailure)
            {
                // The failed launch must not depend on the runner exiting on
                // its own (it may hold the pipes); terminate it best-effort
                // before dropping the handles.
                process.Terminate();
                process.Dispose();
                if (processTree is not null)
                    await ConfirmFailedLaunchTreeExitAsync(
                            processTree, launchFailure, SandboxProcessTreeExit.DefaultTerminationGrace)
                       .ConfigureAwait(false);

                throw;
            }

            connection   = new RunnerProcessConnection(downstream, upstream, process, processTree);
            processTree  = null;
            downstream   = null;
            upstream     = null;
            return connection;
        }
        catch (Exception)
        {
            connection?.Dispose();
            downstream?.Dispose();
            upstream?.Dispose();
            processTree?.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     <para>
    ///         启动失败收尾：runner 已恢复但管道握手或 PID 校验失败时，在释放不带 <c>KILL_ON_JOB_CLOSE</c> 的
    ///         外层 Job 句柄前，以独立有界期限终止并确认 <c>ActiveProcesses == 0</c>；无法确认时抛出
    ///         <see cref="ToolExecutionUncertainException" />，防止调用方把退出未知误当普通启动失败。
    ///     </para>
    ///     <para>
    ///         Failed-launch teardown: when the runner was resumed yet pipe connection or PID verification fails,
    ///         terminates and confirms <c>ActiveProcesses == 0</c> within an independent bounded deadline before
    ///         releasing the outer job handle (which omits <c>KILL_ON_JOB_CLOSE</c>); throws
    ///         <see cref="ToolExecutionUncertainException" /> if whole-tree exit cannot be confirmed so callers
    ///         never mistake unknown exit for an ordinary launch failure.
    ///     </para>
    /// </summary>
    internal static async Task ConfirmFailedLaunchTreeExitAsync(
        SandboxProcessTreeJob processTree, Exception launchFailure, TimeSpan terminationGrace)
    {
        ArgumentNullException.ThrowIfNull(processTree);
        ArgumentNullException.ThrowIfNull(launchFailure);
        try
        {
            await SandboxProcessTreeExit
                 .ConfirmWholeTreeExitAsync(processTree, TimeSpan.Zero, terminationGrace, CancellationToken.None)
                 .ConfigureAwait(false);
        }
        catch (ToolExecutionUncertainException uncertain)
        {
            throw new ToolExecutionUncertainException(
                $"Sandbox runner launch failed ({launchFailure.Message}), and whole-tree exit could not be " +
                $"confirmed before releasing the outer job handle: {uncertain.Message}",
                new AggregateException(launchFailure, uncertain));
        }
    }

    private static RunnerProcessHandle StartRunnerProcess(
        RunnerLaunchRequest request, string pipeInName, string pipeOutName, SandboxProcessTreeJob? processTree)
    {
        var commandLine = BuildRunnerCommandLine(request.RunnerExecutablePath,
                                                 $@"\\.\pipe\{pipeInName}", $@"\\.\pipe\{pipeOutName}");

        var previousErrorMode = SetErrorMode(ErrorModeFailCriticalErrors | ErrorModeNoGpFaultErrorBox);
        try
        {
            // Mirrors the library's runner_client.rs: a fully zeroed
            // STARTUPINFOW with only cb and STARTF_FORCEOFFFEEDBACK set (no
            // lpDesktop — the runner opens the private desktop for the child
            // itself, from spawn_request.private_desktop_name).
            var startupInfo = new StartupInfoW
            {
                Size  = (uint)Marshal.SizeOf<StartupInfoW>(),
                Flags = StartfForceOffFeedback
            };
            var processInfo = default(ProcessInformation);
            var password    = request.Account.Password;
            // 启用外层 Job 时以 CREATE_SUSPENDED 启动：先挂入 Job 再恢复主线程，runner 无法在被跟踪前派生逃逸子进程。
            var creationFlags = CreateNoWindow | CreateUnicodeEnvironment |
                                (processTree is null ? 0 : CreateSuspended);
            // LOGON_WITH_PROFILE 仅在请求加载 profile 时传入：登录会话须加载账户 profile
            // 才具备 schannel 客户端凭据；offline 路径保持不加载。
            var created = CreateProcessWithLogonW(request.Account.Username,
                                                  ".",
                                                  password,
                                                  request.LoadUserProfile ? LogonWithProfile : 0,
                                                  null,
                                                  commandLine,
                                                  creationFlags,
                                                  IntPtr.Zero, // inherit the caller environment; runner reads none
                                                  request.WorkingDirectory,
                                                  ref startupInfo,
                                                  ref processInfo);
            if (!created)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                                         $"Failed to launch the sandbox runner as '{request.Account.Username}' " +
                                         "(the sandbox account or its credentials may be stale; rerun provisioning from the management entry point).");

            var processHandle = new SafeProcessHandle(processInfo.ProcessHandle, ownsHandle: true);
            var threadHandle  = new SafeFileHandle(processInfo.ThreadHandle, ownsHandle: true);
            if (processTree is null)
                threadHandle.Dispose();
            else
                // 挂入外层 Job 后恢复主线程；任一步失败都会终止挂起进程、释放两个句柄并抛出带上下文的异常。
                SandboxProcessTreeJob.AttachSuspendedProcess(processHandle, threadHandle, processTree,
                                                             processInfo.ProcessId, request.Account.Username);
            return new RunnerProcessHandle(processHandle, processInfo.ProcessId);
        }
        finally
        {
            SetErrorMode(previousErrorMode);
        }
    }

    private async Task WaitForConnectionAndVerifyAsync(NamedPipeServerStream downstream,
                                                       NamedPipeServerStream upstream, uint runnerProcessId,
                                                       CancellationToken     cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(_connectTimeout);
        using var linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await downstream.WaitForConnectionAsync(linkedSource.Token).ConfigureAwait(false);
            await upstream.WaitForConnectionAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            throw new
                TimeoutException($"The sandbox runner did not connect its pipes within {(int)_connectTimeout.TotalSeconds}s.");
        }

        VerifyClientProcess(downstream, runnerProcessId, "pipe-in");
        VerifyClientProcess(upstream, runnerProcessId, "pipe-out");
    }

    private static void VerifyClientProcess(Stream pipe, uint runnerProcessId, string pipeName)
    {
        if (pipe is not NamedPipeServerStream serverStream ||
            !GetNamedPipeClientProcessId(serverStream.SafePipeHandle, out var clientProcessId))
            throw new
                InvalidOperationException($"Failed to identify the client of the sandbox runner {pipeName} (Win32 error {Marshal.GetLastWin32Error()}).");

        if (clientProcessId != runnerProcessId)
            throw new
                InvalidOperationException($"The sandbox runner {pipeName} client PID {clientProcessId} did not match the runner PID {runnerProcessId}.");
    }

    /// <summary>
    ///     runner 命令行：CRT 引号规则——空串或含空白/引号才加引号，引号前反斜杠翻倍加一，串尾
    ///     反斜杠翻倍。管道名传完整 \\.\pipe\ 前缀。
    ///     The runner command line using CRT quoting rules: quote only empty
    ///     strings or ones with whitespace/quotes, double backslashes before a
    ///     quote, and double trailing backslashes. Pipe names carry the full
    ///     \\.\pipe\ prefix.
    /// </summary>
    internal static string BuildRunnerCommandLine(string runnerExecutablePath, string pipeInName, string pipeOutName) =>
        string.Join(' ', QuoteArgument(runnerExecutablePath), QuoteArgument($"--pipe-in={pipeInName}"),
                    QuoteArgument($"--pipe-out={pipeOutName}"));

    internal static string QuoteArgument(string value)
    {
        var needsQuotes = value.Length == 0    ||
                          value.Contains(' ')  || value.Contains('\t') ||
                          value.Contains('\r') || value.Contains('\n') || value.Contains('"');
        if (!needsQuotes) return value;

        var quoted = new StringBuilder(value.Length + 2);
        quoted.Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\' :
                    backslashes++;
                    continue;
                case '"' :
                    quoted.Append('\\', backslashes * 2 + 1);
                    backslashes = 0;
                    quoted.Append('"');
                    continue;
            }

            quoted.Append('\\', backslashes);
            backslashes = 0;
            quoted.Append(character);
        }

        quoted.Append('\\', backslashes * 2);
        quoted.Append('"');
        return quoted.ToString();
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessWithLogonW", SetLastError = true,
                   StringMarshalling = StringMarshalling.Utf16)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessWithLogonW(
        string? userName,         string?          domain,      string password, uint logonFlags,
        string? applicationName,  string           commandLine, uint creationFlags, IntPtr environment,
        string? currentDirectory, ref StartupInfoW startupInfo, ref ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", EntryPoint = "GetNamedPipeClientProcessId", SetLastError = true)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe,
                                                            out uint       clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", EntryPoint = "SetErrorMode")]
    private static partial uint SetErrorMode(uint mode);

    private sealed class RunnerProcessHandle(SafeProcessHandle processHandle, uint processId) : IDisposable
    {
        public SafeProcessHandle ProcessHandle { get; } = processHandle;

        public uint ProcessId { get; } = processId;

        public void Dispose()
        {
            ProcessHandle.Dispose();
        }

        /// <summary>
        ///     尽力终止 runner 进程（TerminateProcess）；已退出或调用失败均忽略返回值。
        ///     Best-effort TerminateProcess on the runner; an already-exited or
        ///     failing call has its return value ignored.
        /// </summary>
        public void Terminate()
        {
            _ = TerminateProcess(ProcessHandle, 1);
        }
    }

    private sealed class RunnerProcessConnection(
        Stream                 downstream,
        Stream                 upstream,
        RunnerProcessHandle    process,
        SandboxProcessTreeJob? processTree) : SandboxRunnerConnection(downstream, upstream)
    {
        private SandboxProcessTreeJob? _processTree = processTree;

        public uint RunnerProcessId => process.ProcessId;

        internal override SandboxProcessTreeJob? ProcessTree => _processTree;

        internal override SandboxProcessTreeJob? DetachProcessTree()
        {
            var tree     = _processTree;
            _processTree = null;
            return tree;
        }

        public override Task<bool> WaitForExitAsync(TimeSpan timeout)
        {
            return Task.Run(() =>
            {
                var waited = WaitForSingleObject(process.ProcessHandle,
                                                 (uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1));
                if (waited == WaitFailed)
                    // A failed wait means the handle is unusable; failing
                    // closed beats reporting "still running" forever.
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                                             $"Waiting on the sandbox runner process {process.ProcessId} handle failed.");

                return waited == 0; // WAIT_OBJECT_0
            });
        }

        public override void Kill()
        {
            if (!TerminateProcess(process.ProcessHandle, 1))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                                         $"Failed to terminate the sandbox runner process {process.ProcessId}.");
        }

        public override void Dispose()
        {
            Downstream.Dispose();
            Upstream.Dispose();
            process.Dispose();
            _processTree?.Dispose();
            _processTree = null;
        }
    }

    /// <summary>
    ///     原生 STARTUPINFOW 的完整布局（x64 下 104 字节）。此前只声明了 7 个"用到的"字段，
    ///     Size 与偏移都不符：CreateProcessWithLogonW 按真实偏移读取（dwFlags 在偏移 56、
    ///     hStd* 在 72 之后），会读到结构体外与错误位置的值。字段顺序与类型对齐 C 定义，
    ///     由 Sequential 布局自动复现原生对齐。
    ///     The full native STARTUPINFOW layout (104 bytes on x64). The earlier
    ///     7-field "only what we use" shape had the wrong size AND offsets:
    ///     CreateProcessWithLogonW reads at the real offsets (dwFlags at 56,
    ///     hStd* past 72), pulling values from beyond the struct or the wrong
    ///     slots. Field order and types match the C definition so Sequential
    ///     layout reproduces the native alignment.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoW
    {
        public uint   Size;           // cb
        public IntPtr Reserved;       // lpReserved
        public IntPtr Desktop;        // lpDesktop
        public IntPtr Title;          // lpTitle
        public uint   X;              // dwX
        public uint   Y;              // dwY
        public uint   XSize;          // dwXSize
        public uint   YSize;          // dwYSize
        public uint   XCountChars;    // dwXCountChars
        public uint   YCountChars;    // dwYCountChars
        public uint   FillAttribute;  // dwFillAttribute
        public uint   Flags;          // dwFlags
        public ushort ShowWindow;     // wShowWindow
        public ushort Reserved2Bytes; // cbReserved2
        public IntPtr Reserved2;      // lpReserved2
        public IntPtr StandardInput;  // hStdInput
        public IntPtr StandardOutput; // hStdOutput
        public IntPtr StandardError;  // hStdError
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public uint   ProcessId;
        public uint   ThreadId;
    }
}

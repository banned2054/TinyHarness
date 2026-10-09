using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
/// 启动一次命令的 runner 进程：创建仅沙箱账户可访问的管道对，以沙箱账户
/// CreateProcessWithLogonW 启动 runner，等待双管道连接并校验客户端 PID 与 runner PID
/// 一致（防管道抢连）。
///
/// Launches the per-command runner process: creates the pipe pair accessible
/// only to the sandbox account, starts the runner via CreateProcessWithLogonW
/// under that account, waits for both pipe connections, and verifies the
/// client PID equals the runner PID (guarding against pipe squatting).
/// </summary>
public interface ISandboxRunnerLauncher
{
    Task<SandboxRunnerConnection> LaunchAsync(RunnerLaunchRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// runner 启动请求：runner 路径、沙箱账户（含明文密码，仅用于本次登录）与命令工作目录。
///
/// The runner launch request: runner path, the sandbox account (its plaintext
/// password used only for this logon), and the command working directory.
/// </summary>
public sealed record RunnerLaunchRequest(
    string               RunnerExecutablePath,
    WindowsSandboxAccount Account,
    string               WorkingDirectory);

/// <summary>
/// 一条 runner 命令连接：Downstream 为父→runner（spawn/terminate），Upstream 为
/// runner→父（ready/output/exit）；WaitForExitAsync/Kill 表达 runner 进程 OS 状态。
/// runner 的 OS 退出码不是命令结果，不在这里暴露为业务退出码。
///
/// One runner command connection: Downstream carries parent→runner frames
/// (spawn/terminate) and Upstream runner→parent frames (ready/output/exit);
/// WaitForExitAsync/Kill expose the runner process's OS state. The runner's
/// OS exit code is not the command result and is never surfaced as one here.
/// </summary>
public abstract class SandboxRunnerConnection(Stream downstream, Stream upstream) : IDisposable
{
    public Stream Downstream { get; } = downstream;

    public Stream Upstream { get; } = upstream;

    /// <summary>
    /// 限时等待 runner 进程退出；超时返回 false，不杀进程；句柄等待失败（WAIT_FAILED）
    /// 抛 Win32Exception。
    /// Waits for the runner process to exit within the deadline; returns false
    /// on timeout without killing; a failed handle wait (WAIT_FAILED) throws
    /// Win32Exception.
    /// </summary>
    public abstract Task<bool> WaitForExitAsync(TimeSpan timeout);

    /// <summary>
    /// 终止 runner 进程；语义为 TerminateProcess，不保证完整回收后代。
    /// Terminates the runner process; the semantics are TerminateProcess and
    /// full descendant reclamation is not guaranteed.
    /// </summary>
    public abstract void Kill();

    public abstract void Dispose();
}

/// <summary>
/// <see cref="ISandboxRunnerLauncher"/> 的 Windows 实现（命名管道 + CreateProcessWithLogonW）。
///
/// The Windows implementation of <see cref="ISandboxRunnerLauncher"/> (named
/// pipes plus CreateProcessWithLogonW).
/// </summary>
public sealed partial class WindowsSandboxRunnerLauncher(TimeSpan? connectTimeout = null) : ISandboxRunnerLauncher
{
    /// <summary>
    /// 与库一致的管道连接等待期限。
    /// The pipe connection deadline, matching the library.
    /// </summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly TimeSpan _connectTimeout = connectTimeout ?? DefaultConnectTimeout;

    public async Task<SandboxRunnerConnection> LaunchAsync(RunnerLaunchRequest request,
                                                           CancellationToken  cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Windows sandbox runner launcher is available on Windows only.");
        }

        ArgumentNullException.ThrowIfNull(request);

        var nonce     = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var pipeInName  = $"codex-runner-{nonce}-in";
        var pipeOutName = $"codex-runner-{nonce}-out";

        var pipeSecurity = new PipeSecurity();
        // The sandbox account only reads and writes the pipes; the parent
        // keeps full rights implicitly through ownership, so WRITE_DAC and
        // DELETE are not granted to the sandboxed side.
        pipeSecurity.AddAccessRule(new PipeAccessRule(request.Account.AccountSid,
                                                      PipeAccessRights.ReadWrite, AccessControlType.Allow));

        NamedPipeServerStream? downstream = null;
        NamedPipeServerStream? upstream   = null;
        RunnerProcessConnection?  connection = null;
        try
        {
            // parent writes into -in (Out direction) and reads from -out (In).
            downstream = NamedPipeServerStreamAcl.Create(pipeInName, PipeDirection.Out, 1,
                                                         PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                                                         64 * 1024, 64 * 1024, pipeSecurity);
            upstream = NamedPipeServerStreamAcl.Create(pipeOutName, PipeDirection.In, 1,
                                                       PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                                                       64 * 1024, 64 * 1024, pipeSecurity);

            var process = StartRunnerProcess(request, pipeInName, pipeOutName);
            try
            {
                await WaitForConnectionAndVerifyAsync(downstream, upstream, process.ProcessId,
                                                       cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The failed launch must not depend on the runner exiting on
                // its own (it may hold the pipes); terminate it best-effort
                // before dropping the handles.
                process.Terminate();
                process.Dispose();
                throw;
            }

            connection = new RunnerProcessConnection(downstream, upstream, process);
            downstream = null;
            upstream   = null;
            return connection;
        }
        catch (Exception)
        {
            connection?.Dispose();
            downstream?.Dispose();
            upstream?.Dispose();
            throw;
        }
    }

    private static RunnerProcessHandle StartRunnerProcess(RunnerLaunchRequest request, string pipeInName,
                                                          string pipeOutName)
    {
        var commandLine = BuildRunnerCommandLine(request.RunnerExecutablePath,
                                                 $@"\\.\pipe\{pipeInName}", $@"\\.\pipe\{pipeOutName}");

        var previousErrorMode = SetErrorMode(ErrorModeFailCriticalErrors | ErrorModeNoGpFaultErrorBox);
        try
        {
            var startupInfo = new StartupInfoW
            {
                Size      = (uint)Marshal.SizeOf<StartupInfoW>(),
                Flags     = StartfForceOffFeedback,
            };
            var processInfo = default(ProcessInformation);
            var password    = request.Account.Password;
            var created     = CreateProcessWithLogonW(
                                                    request.Account.Username,
                                                    ".",
                                                    password,
                                                    0, // LOGON_WITH_PROFILE off: legacy runtime, no profile load
                                                    null,
                                                    commandLine,
                                                    CreateNoWindow | CreateUnicodeEnvironment,
                                                    IntPtr.Zero, // inherit the caller environment; runner reads none
                                                    request.WorkingDirectory,
                                                    ref startupInfo,
                                                    ref processInfo);
            if (!created)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                                         $"Failed to launch the sandbox runner as '{request.Account.Username}' " +
                                         "(the sandbox account or its credentials may be stale; rerun provisioning from the management entry point).");
            }

            CloseHandle(processInfo.ThreadHandle);
            return new RunnerProcessHandle(processInfo.ProcessHandle, processInfo.ProcessId);
        }
        finally
        {
            SetErrorMode(previousErrorMode);
        }
    }

    private async Task WaitForConnectionAndVerifyAsync(NamedPipeServerStream downstream,
                                                       NamedPipeServerStream upstream, uint runnerProcessId,
                                                       CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(_connectTimeout);
        using var linkedSource  = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await downstream.WaitForConnectionAsync(linkedSource.Token).ConfigureAwait(false);
            await upstream.WaitForConnectionAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException(
                                      $"The sandbox runner did not connect its pipes within {(int)_connectTimeout.TotalSeconds}s.");
        }

        VerifyClientProcess(downstream, runnerProcessId, pipeName : "pipe-in");
        VerifyClientProcess(upstream, runnerProcessId, pipeName : "pipe-out");
    }

    private static void VerifyClientProcess(Stream pipe, uint runnerProcessId, string pipeName)
    {
        if (pipe is not NamedPipeServerStream serverStream ||
            !GetNamedPipeClientProcessId(serverStream.SafePipeHandle, out var clientProcessId))
        {
            throw new InvalidOperationException(
                                          $"Failed to identify the client of the sandbox runner {pipeName} (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        if (clientProcessId != runnerProcessId)
        {
            throw new InvalidOperationException(
                                          $"The sandbox runner {pipeName} client PID {clientProcessId} did not match the runner PID {runnerProcessId}.");
        }
    }

    /// <summary>
    /// runner 命令行：CRT 引号规则——空串或含空白/引号才加引号，引号前反斜杠翻倍加一，串尾
    /// 反斜杠翻倍。管道名传完整 \\.\pipe\ 前缀。
    ///
    /// The runner command line using CRT quoting rules: quote only empty
    /// strings or ones with whitespace/quotes, double backslashes before a
    /// quote, and double trailing backslashes. Pipe names carry the full
    /// \\.\pipe\ prefix.
    /// </summary>
    internal static string BuildRunnerCommandLine(string runnerExecutablePath, string pipeInName, string pipeOutName)
        => string.Join(' ',
                       QuoteArgument(runnerExecutablePath),
                       QuoteArgument($"--pipe-in={pipeInName}"),
                       QuoteArgument($"--pipe-out={pipeOutName}"));

    internal static string QuoteArgument(string value)
    {
        var needsQuotes = value.Length == 0 ||
                          value.Contains(' ')  || value.Contains('\t') ||
                          value.Contains('\r') || value.Contains('\n') || value.Contains('"');
        if (!needsQuotes)
        {
            return value;
        }

        var quoted = new StringBuilder(value.Length + 2);
        quoted.Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', (backslashes * 2) + 1);
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

    private const uint CreateNoWindow            = 0x0800_0000;
    private const uint CreateUnicodeEnvironment  = 0x0000_0400;
    private const uint StartfForceOffFeedback    = 0x0000_0020;
    private const uint ErrorModeFailCriticalErrors = 0x0000_0001;
    private const uint ErrorModeNoGpFaultErrorBox = 0x0000_0002;
    private const uint WaitFailed                  = 0xFFFF_FFFF; // WAIT_FAILED

    private sealed class RunnerProcessHandle(IntPtr processHandle, uint processId) : IDisposable
    {
        public IntPtr ProcessHandle { get; } = processHandle;

        public uint ProcessId { get; } = processId;

        /// <summary>
        /// 尽力终止 runner 进程（TerminateProcess）；已退出或调用失败均忽略返回值。
        ///
        /// Best-effort TerminateProcess on the runner; an already-exited or
        /// failing call has its return value ignored.
        /// </summary>
        public void Terminate()
            => _ = TerminateProcess(ProcessHandle, 1);

        public void Dispose() => CloseHandle(ProcessHandle);
    }

    private sealed class RunnerProcessConnection(
        Stream downstream, Stream upstream, RunnerProcessHandle process)
        : SandboxRunnerConnection(downstream, upstream)
    {
        public uint RunnerProcessId => process.ProcessId;

        public override Task<bool> WaitForExitAsync(TimeSpan timeout)
            => Task.Run(() =>
            {
                var waited = WaitForSingleObject(process.ProcessHandle,
                                                 (uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1));
                if (waited == WaitFailed)
                {
                    // A failed wait means the handle is unusable; failing
                    // closed beats reporting "still running" forever.
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                                             $"Waiting on the sandbox runner process {process.ProcessId} handle failed.");
                }

                return waited == 0; // WAIT_OBJECT_0
            });

        public override void Kill()
        {
            if (!TerminateProcess(process.ProcessHandle, 1))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                                         $"Failed to terminate the sandbox runner process {process.ProcessId}.");
            }
        }

        public override void Dispose()
        {
            Downstream.Dispose();
            Upstream.Dispose();
            process.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoW
    {
        public uint   Size;
        public uint   Flags;
        public uint   ShowWindow;
        public uint   Reserved1;
        public IntPtr Reserved2;
        public IntPtr Reserved3;
        public IntPtr Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public uint   ProcessId;
        public uint   ThreadId;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessWithLogonW", SetLastError = true,
                   StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessWithLogonW(
        string? userName, string? domain, string password, uint logonFlags,
        string? applicationName, string commandLine, uint creationFlags, IntPtr environment,
        string? currentDirectory, ref StartupInfoW startupInfo, ref ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", EntryPoint = "GetNamedPipeClientProcessId", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
                                                            out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(IntPtr handle, uint exitCode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", EntryPoint = "SetErrorMode")]
    private static partial uint SetErrorMode(uint mode);
}

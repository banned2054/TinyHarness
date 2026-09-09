using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TinyHarness.Core.Runtime;

/// <summary>
/// Starts a Windows process suspended, assigns it to a kill-on-close Job Object,
/// then resumes its primary thread. Descendants therefore enter the Job before
/// user code can run, including commands whose root process exits immediately.
/// </summary>
internal static class WindowsJobProcess
{
    private const uint CreateNoWindow                = 0x08000000;
    private const uint CreateSuspended               = 0x00000004;
    private const uint CreateUnicodeEnvironment      = 0x00000400;
    private const uint StartfUseStdHandles           = 0x00000100;
    private const uint JobObjectLimitKillOnClose     = 0x00002000;
    private const int  ExtendedLimitInformationClass = 9;
    private const uint DuplicateSameAccess           = 0x00000002;

    public static RunningProcess Start(ProcessStartInfo startInfo, string? rawCmdCommand)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Job Objects are available only on Windows.");
        }

        SafeFileHandle? job           = null;
        SafeFileHandle? stdoutRead    = null;
        SafeFileHandle? stdoutWrite   = null;
        SafeFileHandle? stderrRead    = null;
        SafeFileHandle? stderrWrite   = null;
        SafeFileHandle? stdinChild    = null;
        SafeFileHandle? processHandle = null;
        SafeFileHandle? threadHandle  = null;
        Process?        process       = null;
        StreamReader?   stdout        = null;
        StreamReader?   stderr        = null;
        var             assignedToJob = false;

        try
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            ThrowIfInvalid(job, "create process Job Object");
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnClose,
                },
            };
            if (!SetInformationJobObject(job, ExtendedLimitInformationClass, ref limits,
                                         (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                throw LastWin32("configure process Job Object");
            }

            CreateRedirectPipe(out stdoutRead, out stdoutWrite, "stdout");
            CreateRedirectPipe(out stderrRead, out stderrWrite, "stderr");
            stdinChild = DuplicateStandardInput();

            var startupInfo = new StartupInfo
            {
                Size           = (uint)Marshal.SizeOf<StartupInfo>(),
                Flags          = StartfUseStdHandles,
                StandardInput  = stdinChild.DangerousGetHandle(),
                StandardOutput = stdoutWrite.DangerousGetHandle(),
                StandardError  = stderrWrite.DangerousGetHandle(),
            };
            var commandLine = new StringBuilder(BuildCommandLine(startInfo, rawCmdCommand));
            var environment = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(startInfo.Environment));
            try
            {
                if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, inheritHandles : true,
                                    CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment,
                                    environment, startInfo.WorkingDirectory, ref startupInfo,
                                    out var processInformation))
                {
                    throw LastWin32($"start executable '{startInfo.FileName}'");
                }

                processHandle = new SafeFileHandle(processInformation.Process, ownsHandle : true);
                threadHandle  = new SafeFileHandle(processInformation.Thread, ownsHandle : true);
                process       = Process.GetProcessById(checked((int)processInformation.ProcessId));
                // Force Process to open and retain its own handle while the native
                // process is guaranteed to exist in suspended state. Waiting and
                // ExitCode remain valid even if it exits immediately after resume.
                _ = process.SafeHandle;
            }
            finally
            {
                Marshal.FreeHGlobal(environment);
            }

            // The parent must not retain the child-side pipe handles, otherwise
            // EOF would never be observable after the Job exits.
            stdoutWrite.Dispose();
            stdoutWrite = null;
            stderrWrite.Dispose();
            stderrWrite = null;
            stdinChild.Dispose();
            stdinChild = null;

            if (!AssignProcessToJobObject(job, processHandle))
            {
                throw LastWin32($"assign process {process.Id} to its Job Object");
            }

            assignedToJob = true;
            var encoding = startInfo.StandardOutputEncoding ?? Console.OutputEncoding;
            // CreatePipe produces synchronous handles. FileStream must reflect
            // that mode; marking them overlapped would make ReadFile invalid.
            stdout = new StreamReader(new FileStream(stdoutRead, FileAccess.Read, 4096, isAsync : false),
                                      encoding, detectEncodingFromByteOrderMarks : true);
            stdoutRead = null;
            var errorEncoding = startInfo.StandardErrorEncoding ?? Console.OutputEncoding;
            stderr = new StreamReader(new FileStream(stderrRead, FileAccess.Read, 4096, isAsync : false),
                                      errorEncoding, detectEncodingFromByteOrderMarks : true);
            stderrRead = null;

            if (ResumeThread(threadHandle) == uint.MaxValue)
            {
                throw LastWin32($"resume process {process.Id}");
            }

            threadHandle.Dispose();
            threadHandle = null;
            processHandle.Dispose();
            processHandle = null;

            var lifetime = new WindowsJobLifetime(job);
            job = null;
            var running = new RunningProcess(process, stdout, stderr, lifetime.Terminate, lifetime);
            process = null;
            stdout  = null;
            stderr  = null;
            return running;
        }
        catch (Exception startException)
        {
            Exception? cleanupError = null;
            if (assignedToJob)
            {
                // Closing a configured Job is the authoritative tree cleanup.
                job?.Dispose();
            }
            else if (processHandle is { IsInvalid: false, IsClosed: false })
            {
                if (!TerminateProcess(processHandle, 1))
                {
                    cleanupError = LastWin32("terminate suspended process after launch failure");
                }
                else
                {
                    var waitResult = WaitForSingleObject(processHandle, 5_000);
                    if (waitResult == 258)
                    {
                        cleanupError = new TimeoutException(
                                                            "Timed out waiting for the suspended process to terminate after launch failure.");
                    }
                    else if (waitResult == uint.MaxValue)
                    {
                        cleanupError = LastWin32("wait for suspended process cleanup");
                    }
                }
            }

            if (cleanupError is not null)
            {
                throw new InvalidOperationException(
                                                    "Process launch failed and cleanup of the suspended process also failed.",
                                                    new AggregateException(startException, cleanupError));
            }

            throw;
        }
        finally
        {
            stdout?.Dispose();
            stderr?.Dispose();
            process?.Dispose();
            threadHandle?.Dispose();
            processHandle?.Dispose();
            stdinChild?.Dispose();
            stdoutWrite?.Dispose();
            stderrWrite?.Dispose();
            stdoutRead?.Dispose();
            stderrRead?.Dispose();
            job?.Dispose();
        }
    }

    private static string BuildCommandLine(ProcessStartInfo startInfo, string? rawCmdCommand)
    {
        var commandLine = new StringBuilder(QuoteWindowsArgument(startInfo.FileName));
        if (rawCmdCommand is not null)
        {
            commandLine.Append(" /d /s /c \"").Append(rawCmdCommand).Append('"');
            return commandLine.ToString();
        }

        foreach (var argument in startInfo.ArgumentList)
        {
            commandLine.Append(' ').Append(QuoteWindowsArgument(argument));
        }

        return commandLine.ToString();
    }

    private static string QuoteWindowsArgument(string argument)
    {
        if (argument.Length != 0 && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var quoted      = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            quoted.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }

        quoted.Append('\\', backslashes * 2).Append('"');
        return quoted.ToString();
    }

    private static string BuildEnvironmentBlock(IDictionary<string, string?> environment)
    {
        var block = new StringBuilder();
        foreach (var item in environment.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            block.Append(item.Key).Append('=').Append(item.Value).Append('\0');
        }

        return block.Append('\0').ToString();
    }

    private static void CreateRedirectPipe(out SafeFileHandle readHandle, out SafeFileHandle writeHandle,
                                           string             streamName)
    {
        var security = new SecurityAttributes
        {
            Length        = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true,
        };
        if (!CreatePipe(out readHandle, out writeHandle, ref security, 0))
        {
            throw LastWin32($"create redirected {streamName} pipe");
        }

        if (!SetHandleInformation(readHandle, 1, 0))
        {
            throw LastWin32($"make redirected {streamName} read handle private");
        }
    }

    private static SafeFileHandle DuplicateStandardInput()
    {
        var standardInput = GetStdHandle(-10);
        if (standardInput == IntPtr.Zero || standardInput == new IntPtr(-1))
        {
            return CreateInheritedNullInput();
        }

        var currentProcess = GetCurrentProcess();
        if (DuplicateHandle(currentProcess, standardInput, currentProcess, out var duplicate, 0,
                            inheritHandle : true, DuplicateSameAccess))
        {
            return duplicate;
        }

        return CreateInheritedNullInput();
    }

    private static SafeFileHandle CreateInheritedNullInput()
    {
        var security = new SecurityAttributes
        {
            Length        = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true,
        };
        var handle = CreateFileW("NUL", 0x80000000, 0x00000001 | 0x00000002, ref security,
                                 3, 0x00000080, IntPtr.Zero);
        ThrowIfInvalid(handle, "open NUL for child standard input");
        return handle;
    }

    private static void ThrowIfInvalid(SafeFileHandle handle, string operation)
    {
        if (handle.IsInvalid)
        {
            throw LastWin32(operation);
        }
    }

    private static Win32Exception LastWin32(string operation)
    {
        var error = Marshal.GetLastPInvokeError();
        return new Win32Exception(error, $"Failed to {operation}: {new Win32Exception(error).Message}");
    }

    private sealed class WindowsJobLifetime(SafeFileHandle job) : IDisposable
    {
        public void Terminate()
        {
            if (job.IsClosed || job.IsInvalid)
            {
                throw new
                    InvalidOperationException("Cannot terminate the process tree because its Job Object is closed.");
            }

            if (!TerminateJobObject(job, 1))
            {
                throw LastWin32("terminate process Job Object");
            }
        }

        public void Dispose() => job.Dispose();
    }

    internal sealed class RunningProcess(
        Process      process,
        StreamReader standardOutput,
        StreamReader standardError,
        Action       terminate,
        IDisposable  lifetime) : IDisposable
    {
        public Process      Process        { get; } = process;
        public StreamReader StandardOutput { get; } = standardOutput;
        public StreamReader StandardError  { get; } = standardError;

        public void Terminate() => terminate();

        public void Dispose()
        {
            lifetime.Dispose();
            StandardOutput.Dispose();
            StandardError.Dispose();
            Process.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int    Length;
        public IntPtr SecurityDescriptor;

        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
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
        public ushort Reserved2Count;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint   ProcessId;
        public uint   ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long    PerProcessUserTimeLimit;
        public long    PerJobUserTimeLimit;
        public uint    LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint    ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint    PriorityClass;
        public uint    SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters                     IoInfo;
        public UIntPtr                        ProcessMemoryLimit;
        public UIntPtr                        JobMemoryLimit;
        public UIntPtr                        PeakProcessMemoryUsed;
        public UIntPtr                        PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle                        job, int informationClass,
                                                       ref JobObjectExtendedLimitInformation information,
                                                       uint                                  informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? applicationName, StringBuilder commandLine,
                                              IntPtr processAttributes, IntPtr threadAttributes,
                                              [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
                                              uint creationFlags, IntPtr environment,
                                              string currentDirectory, ref StartupInfo startupInfo,
                                              out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle     readPipe,       out SafeFileHandle writePipe,
                                          ref SecurityAttributes pipeAttributes, uint               size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle,
                                               IntPtr targetProcess, out SafeFileHandle targetHandle,
                                               uint   desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
                                               uint   options);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
                                                     ref SecurityAttributes securityAttributes,
                                                     uint creationDisposition, uint flagsAndAttributes,
                                                     IntPtr templateFile);
}

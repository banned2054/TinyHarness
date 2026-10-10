using System.Security.Principal;
using System.Text;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     内存单向管道流：写入按块排队，读取可任意部分读取；关闭写端后读取干净 EOF，Dispose 唤醒
///     所有等待中的读取。用于在测试中模拟 runner 的双管道，不触碰 OS 资源。
///     An in-memory unidirectional pipe stream: writes enqueue chunks and reads
///     may consume any partial amount; closing the write side yields a clean EOF
///     while Dispose wakes pending readers. It simulates the runner's two pipes
///     in tests without touching OS resources.
/// </summary>
internal sealed class MemoryPipeStream : Stream
{
    private readonly Queue<byte[]> _chunks  = new();
    private readonly SemaphoreSlim _signal  = new(0);
    private          byte[]        _current = [];
    private          int           _currentOffset;
    private          bool          _writeClosed;

    public override bool CanRead  => !_writeClosed;
    public override bool CanSeek  => false;
    public override bool CanWrite => !_writeClosed;
    public override long Length   => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    ///     模拟对端关闭连接：不再接受写入；已缓冲的数据仍可被读端读完后见到干净 EOF（与真实
    ///     管道语义一致），等待中的读取立即被唤醒。
    ///     Simulates the peer closing the connection: no further writes are
    ///     accepted; already-buffered data stays readable until the reader sees a
    ///     clean EOF (matching real pipe semantics), and pending reads wake
    ///     immediately.
    /// </summary>
    public void DropConnection()
    {
        lock (_chunks)
        {
            _writeClosed = true;
        }

        _signal.Release();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        var copy = new byte[count];
        Array.Copy(buffer, offset, copy, 0, count);
        lock (_chunks)
        {
            if (_writeClosed) throw new IOException("The pipe is closed.");

            _chunks.Enqueue(copy);
        }

        _signal.Release();
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.ToArray(), 0, buffer.Length);
        return ValueTask.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (true)
        {
            lock (_chunks)
            {
                if (TryTakeChunk(buffer.AsSpan(offset, count), out var copied)) return copied;

                if (_writeClosed) return 0;
            }

            _signal.Wait();
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            lock (_chunks)
            {
                if (TryTakeChunk(buffer.Span, out var copied)) return copied;

                if (_writeClosed) return 0;
            }

            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryTakeChunk(Span<byte> destination, out int copied)
    {
        if (_currentOffset == _current.Length && _chunks.Count > 0)
        {
            _current       = _chunks.Dequeue();
            _currentOffset = 0;
        }

        var available = _current.Length - _currentOffset;
        if (available == 0)
        {
            copied = 0;
            return _writeClosed;
        }

        copied = Math.Min(destination.Length, available);
        _current.AsSpan(_currentOffset, copied).CopyTo(destination);
        _currentOffset += copied;
        return true;
    }

    public override void Close()
    {
        lock (_chunks)
        {
            _writeClosed = true;
        }

        _signal.Release();
        base.Close();
    }

    protected override void Dispose(bool disposing)
    {
        DropConnection();
        base.Dispose(disposing);
    }

    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
///     录制式输出捕获器：记录每个块与 Discard 调用，用于断言后端的输出推送与终态语义。
///     A recording output capture: records every chunk and Discard call so tests
///     can assert the backend's output pushing and end-state semantics.
/// </summary>
internal sealed class RecordingOutputCapture : IProcessOutputCapture
{
    private readonly StringBuilder _content = new();

    public List<string> Chunks    { get; } = [];
    public bool         Discarded { get; private set; }
    public bool         Completed { get; private set; }

    public string Content => _content.ToString();

    public ValueTask AppendAsync(ReadOnlyMemory<char> chunk, CancellationToken cancellationToken)
    {
        var text = new string(chunk.Span);
        Chunks.Add(text);
        if (!chunk.IsEmpty) _content.Append(text);

        return ValueTask.CompletedTask;
    }

    public CapturedProcessOutput Complete()
    {
        Completed = true;
        return new CapturedProcessOutput(_content.ToString(), false, _content.Length, null);
    }

    public void Discard()
    {
        Discarded = true;
    }
}

/// <summary>
///     Fake runner 启动器：用内存管道构造连接，把"runner 进程"表现为一个脚本化 body 任务；
///     Kill/Dispose 取消 body 并丢弃管道，模拟进程终止与断管道。
///     Fake runner launcher: builds connections from memory pipes and expresses
///     the "runner process" as a scripted body task; Kill/Dispose cancels the
///     body and drops the pipes, simulating process termination and pipe breaks.
/// </summary>
internal sealed class FakeSandboxRunnerLauncher : ISandboxRunnerLauncher
{
    /// <summary>
    ///     runner body：downstream 是父→runner 管道，upstream 是 runner→父管道；endToken 在
    ///     Kill/Dispose 时取消。
    ///     The runner body: downstream is the parent→runner pipe, upstream is the
    ///     runner→parent pipe; endToken cancels on Kill/Dispose.
    /// </summary>
    public required Func<Stream, Stream, CancellationToken, Task> RunnerBody { get; init; }

    /// <summary>
    ///     设置后 LaunchAsync 直接抛出该异常，用于模拟 runner 启动失败；OperationCanceledException
    ///     会被后端原样透传，其余异常被映射为 ProcessExecutionStartException。
    ///     When set, LaunchAsync throws it directly to simulate a failed launch;
    ///     OperationCanceledException passes through the backend untouched while
    ///     any other exception maps to ProcessExecutionStartException.
    /// </summary>
    public Exception? ThrowOnLaunch { get; init; }

    public RunnerLaunchRequest? LastRequest { get; private set; }

    public Task<SandboxRunnerConnection> LaunchAsync(RunnerLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ThrowOnLaunch is not null) throw ThrowOnLaunch;

        LastRequest = request;

        var downstream = new MemoryPipeStream();
        var upstream   = new MemoryPipeStream();
        var connection = new FakeRunnerConnection(downstream, upstream);
        connection.StartBody(RunnerBody);
        return Task.FromResult<SandboxRunnerConnection>(connection);
    }

    private sealed class FakeRunnerConnection(MemoryPipeStream downstream, MemoryPipeStream upstream)
        : SandboxRunnerConnection(downstream, upstream)
    {
        private readonly CancellationTokenSource _endSource = new();

        private readonly TaskCompletionSource<bool> _killSignal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Task _body = Task.CompletedTask;

        public void StartBody(Func<Stream, Stream, CancellationToken, Task> body)
        {
            _body = Task.Run(() => body(Downstream, Upstream, _endSource.Token));
        }

        /// <summary>
        ///     body 自然结束或 Kill 发出信号都算"进程已退出"；仅当二者都未发生且超时才返回
        ///     false。Task.WhenAny 不会因 body 故障/取消而抛出，异常结果按已退出处理。
        ///     A body that finished or a Kill signal both mean "the fake process
        ///     exited"; only when neither happens before the deadline does this
        ///     return false. Task.WhenAny never throws for a faulted/cancelled
        ///     body, and such an outcome still counts as exited.
        /// </summary>
        public override async Task<bool> WaitForExitAsync(TimeSpan timeout)
        {
            try
            {
                await Task.WhenAny(_body, _killSignal.Task).WaitAsync(timeout).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        public override void Kill()
        {
            _killSignal.TrySetResult(true);
            _endSource.Cancel();
            downstream.DropConnection();
            upstream.DropConnection();
        }

        public override void Dispose()
        {
            _killSignal.TrySetResult(true);
            _endSource.Cancel();
            downstream.Dispose();
            upstream.Dispose();
            _endSource.Dispose();
        }
    }
}

/// <summary>
///     记录 refresh payload 的 fake setup 调用器。
///     A fake setup invoker that records refresh payloads.
/// </summary>
internal sealed class FakeSetupInvoker : ISandboxSetupInvoker
{
    public List<SandboxSetupPayload> RefreshPayloads { get; } = [];
    public Exception?                ThrowOnRefresh  { get; set; }

    public Task RefreshAsync(SandboxSetupPayload payload, CancellationToken cancellationToken)
    {
        RefreshPayloads.Add(payload);
        if (ThrowOnRefresh is not null) throw ThrowOnRefresh;

        return Task.CompletedTask;
    }
}

/// <summary>
///     Fake 凭据源：返回固定账户（SID 用已知格式解析）。
///     A fake credential source returning a fixed account (SID parsed from a
///     well-formed string).
/// </summary>
internal sealed class FakeCredentialSource(string username) : ISandboxCredentialSource
{
    public FakeCredentialSource() : this("TinyHarnessOffline")
    {
    }

    public WindowsSandboxAccount Account { get; } = CreateAccount(username);

    public Task<WindowsSandboxAccount> LoadOfflineAccount(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Account);
    }

    private static WindowsSandboxAccount CreateAccount(string accountUsername)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        return new WindowsSandboxAccount(accountUsername, "test-password",
                                         new SecurityIdentifier(
                                                                "S-1-5-21-100-200-300-404"));
    }
}

/// <summary>
///     Fake 私有桌面工厂：不创建 OS 桌面，仅提供协议所需的名称。
///     A fake private desktop factory: no OS desktop is created, only the
///     protocol-visible name.
/// </summary>
internal sealed class FakeDesktopFactory : ISandboxDesktopFactory
{
    public const string DesktopName = "TinyHarnessDesktop-0123456789abcdef0123456789abcdef";

    public List<string> GrantedSids { get; } = [];

    public PrivateDesktop CreatePrivateDesktop(SecurityIdentifier sandboxAccountSid)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        GrantedSids.Add(sandboxAccountSid.Value);
        return new PrivateDesktop(DesktopName, IntPtr.Zero);
    }
}

/// <summary>
///     在临时目录中布置一个"已就绪"的沙箱 home：组件占位文件、有效 marker、cap_sid 表和
///     users 文件（密码为格式合法的占位 base64，不做 DPAPI 解密）。
///     Lays out a "ready" sandbox home inside a temp directory: placeholder
///     component files, a valid marker, a cap_sid table, and a users file (the
///     password is structurally valid placeholder base64, never DPAPI-decrypted).
/// </summary>
internal sealed class SandboxTestHome : IDisposable
{
    private readonly TestTempDir _directory = new();

    public SandboxTestHome()
    {
        var home      = _directory.Root;
        var setupExe  = _directory.WriteBytes("setup.exe", [0x4d, 0x5a]);
        var runnerExe = _directory.WriteBytes("runner.exe", [0x4d, 0x5a]);
        _directory.WriteFile(".sandbox/setup_marker.json",
                             """{"version":5,"offline_username":"TinyHarnessOffline","online_username":"TinyHarnessOnline","created_at":"2026-10-09T00:00:00Z","proxy_ports":[],"allow_local_binding":false,"read_roots":[],"write_roots":[]}""");
        _directory.WriteFile("cap_sid",
                             """{"workspace":"S-1-5-21-11-11-11-11","readonly":"S-1-5-21-22-22-22-22","workspace_by_cwd":{},"writable_root_by_path":{}}""");
        _directory.WriteFile(".sandbox-secrets/sandbox_users.json",
                             """{"version":5,"offline":{"username":"TinyHarnessOffline","password":"QUJDRA=="},"online":{"username":"TinyHarnessOnline","password":"QUJDRA=="}}""");
        Components = new WindowsSandboxComponents
        {
            SetupExecutablePath  = setupExe,
            RunnerExecutablePath = runnerExe,
            SandboxHome          = home
        };
    }

    public WindowsSandboxComponents Components { get; }

    public void Dispose()
    {
        _directory.Dispose();
    }
}

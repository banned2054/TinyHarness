using System.Text;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         使用内存管道与模拟 runner 验证 v6 协议的终态、错误、乱序、取消和回收边界，
///         以及冻结计划与清理后环境的传递；不启动真实进程或修改机器状态。
///     </para>
///     <para>
///         Uses in-memory pipes and a fake runner to verify v6 terminal, error, ordering, cancellation and reclamation
///         boundaries,
///         plus frozen-plan and cleaned-environment delivery; starts no real processes and changes no machine state.
///     </para>
/// </summary>
public class WindowsSandboxBackendTests
{
    private static readonly WindowsSandboxBackendLimits TestLimits = new()
    {
        RefreshTimeout          = TimeSpan.FromSeconds(5),
        SpawnReadyTimeout       = TimeSpan.FromSeconds(3),
        CommunicationGrace      = TimeSpan.FromMilliseconds(300),
        TerminationDrainTimeout = TimeSpan.FromMilliseconds(500),
        RunnerExitTimeout       = TimeSpan.FromMilliseconds(500),
        TerminationKillGrace    = TimeSpan.FromSeconds(1),
        TerminateWriteTimeout   = TimeSpan.FromSeconds(1)
    };

    private static PreparedProcessExecution Plan(string workingDirectory, int timeoutSeconds = 30) =>
        new("cmd.exe", ["/c", "whoami"], workingDirectory, timeoutSeconds, false,
            null);

    private static (WindowsSandboxBackend Backend, FakeSetupInvoker Setup, FakeDesktopFactory Desktops,
        FakeSandboxRunnerLauncher Launcher, SandboxTestHome Home) CreateBackend(
            Func<Stream, Stream, CancellationToken, Task> runnerBody,
            string?                                       workspaceRoot = null,
            SandboxPolicyKind                             kind          = SandboxPolicyKind.WorkspaceWrite,
            IReadOnlyDictionary<string, string>?          knownSecrets  = null,
            Exception?                                    refreshError  = null,
            WindowsSandboxComponents?                     components    = null,
            WindowsSandboxBackendLimits?                  limits        = null,
            Exception?                                    launchError   = null)
    {
        var home              = new SandboxTestHome();
        var workspace         = workspaceRoot ?? home.Components.SandboxHome; // 策略范围内的测试根。
        var targetEnvironment = new Dictionary<string, string> { ["TEMP"] = workspace };
        var policy            = SandboxPolicyResolver.Resolve(kind, workspace, targetEnvironment);
        var setup             = new FakeSetupInvoker { ThrowOnRefresh = refreshError };
        var desktops          = new FakeDesktopFactory();
        var launcher          = new FakeSandboxRunnerLauncher { RunnerBody = runnerBody, ThrowOnLaunch = launchError };
        var backend = new WindowsSandboxBackend(
                                                components ?? home.Components, policy, targetEnvironment,
                                                knownSecrets,
                                                setup, new FakeCredentialSource(),
                                                desktopFactory : desktops, runnerLauncher : launcher,
                                                limits : limits ?? TestLimits, allowNullDeviceAccess : _ => { });
        return (backend, setup, desktops, launcher, home);
    }

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));


    [Fact]
    public async Task Execute_NormalExitAssemblesStreamsAndResult()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (backend, setup, desktops, launcher, home) = CreateBackend(NormalRunner);
        using var homeScope = home;
        var       stdout    = new RecordingOutputCapture();
        var       stderr    = new RecordingOutputCapture();

        var result = await backend.ExecuteAsync(Plan(home.Components.SandboxHome), stdout, stderr,
                                                CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Null(result.Failure);
        Assert.Contains("stdout-hello 世界", stdout.Content);
        Assert.Contains("stderr-line", stderr.Content);
        Assert.DoesNotContain("stderr", stdout.Content, StringComparison.Ordinal);
        Assert.All([stdout, stderr], capture => Assert.Equal(string.Empty, capture.Chunks.Last()));
    }

    private static async Task NormalRunner(Stream downstream, Stream upstream, CancellationToken end)
    {
        var spawn = await RunnerFrameCodec.ReadFrameAsync(downstream, end);
        Assert.NotNull(spawn);
        await RunnerFrameCodec.WriteFrameAsync(upstream, "spawn_ready", new RunnerSpawnReady { ProcessId = 4321 },
                                               end);
        await RunnerFrameCodec.WriteFrameAsync(upstream, "output",
                                               new RunnerOutputPayload
                                               {
                                                   DataBase64 = Base64("stdout-hello 世界"),
                                                   Stream     = "stdout"
                                               }, end);
        await RunnerFrameCodec.WriteFrameAsync(upstream, "output",
                                               new RunnerOutputPayload
                                               {
                                                   DataBase64 = Base64("stderr-line"),
                                                   Stream     = "stderr"
                                               }, end);
        await RunnerFrameCodec.WriteFrameAsync(upstream, "exit",
                                               new RunnerExitPayload { ExitCode = 0, TimedOut = false }, end);
    }

    [Fact]
    public async Task Execute_TimeoutVerdictAndNatural192StayDistinct()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (timedOutBackend, _, _, _, timedOutHome) = CreateBackend(ScriptedRunner(new RunnerExitPayload
                                                                                  { ExitCode = 192, TimedOut = true }));
        using var timedOutScope = timedOutHome;
        var (naturalBackend, _, _, _, naturalHome) = CreateBackend(ScriptedRunner(new RunnerExitPayload
                                                                                { ExitCode = 192, TimedOut = false }));
        using var naturalScope = naturalHome;

        var timedOutResult = await timedOutBackend.ExecuteAsync(Plan(timedOutHome.Components.SandboxHome),
                                                                new RecordingOutputCapture(),
                                                                new RecordingOutputCapture(), CancellationToken.None);
        var naturalResult = await naturalBackend.ExecuteAsync(Plan(naturalHome.Components.SandboxHome),
                                                              new RecordingOutputCapture(),
                                                              new RecordingOutputCapture(), CancellationToken.None);

        Assert.Equal(192, timedOutResult.ExitCode);
        Assert.True(timedOutResult.TimedOut);
        Assert.Equal(192, naturalResult.ExitCode);
        Assert.False(naturalResult.TimedOut);
    }

    private static Func<Stream, Stream, CancellationToken, Task> ScriptedRunner(RunnerExitPayload exit)
    {
        return async (downstream, upstream, end) =>
        {
            await RunnerFrameCodec.ReadFrameAsync(downstream, end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "spawn_ready", new RunnerSpawnReady { ProcessId = 1 },
                                                   end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "exit", exit, end);
        };
    }

    [Fact]
    public async Task Execute_DecodesUtf8SplitAcrossOutputFrames()
    {
        if (!OperatingSystem.IsWindows()) return;

        // 将“世界”的 6 个 UTF-8 字节按 4/2 拆帧，验证解码器跨帧保留不完整序列。
        var bytes = Encoding.UTF8.GetBytes("世界");
        var (backend, _, _, _, home) = CreateBackend(async (downstream, upstream, end) =>
        {
            await RunnerFrameCodec.ReadFrameAsync(downstream, end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "spawn_ready", new RunnerSpawnReady { ProcessId = 1 },
                                                   end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "output",
                                                   new RunnerOutputPayload
                                                   {
                                                       DataBase64 = Convert.ToBase64String(bytes[..4]),
                                                       Stream     = "stdout"
                                                   }, end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "output",
                                                   new RunnerOutputPayload
                                                   {
                                                       DataBase64 = Convert.ToBase64String(bytes[4..]),
                                                       Stream     = "stdout"
                                                   }, end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "exit",
                                                   new RunnerExitPayload { ExitCode = 0, TimedOut = false }, end);
        });
        using var homeScope = home;

        var stdout = new RecordingOutputCapture();
        await backend.ExecuteAsync(Plan(home.Components.SandboxHome), stdout, new RecordingOutputCapture(),
                                   CancellationToken.None);
        Assert.Equal("世界", stdout.Content);
    }

    [Fact]
    public async Task Execute_LargeOutputSurvivesManyFrames()
    {
        if (!OperatingSystem.IsWindows()) return;

        const int frameCount = 64;
        const int frameSize  = 16 * 1024;
        var       payload    = new string('x', frameSize) + "\n";
        var (backend, _, _, _, home) = CreateBackend(async (downstream, upstream, end) =>
        {
            await RunnerFrameCodec.ReadFrameAsync(downstream, end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "spawn_ready", new RunnerSpawnReady { ProcessId = 1 },
                                                   end);
            for (var index = 0; index < frameCount; index++)
                await RunnerFrameCodec.WriteFrameAsync(upstream, "output",
                                                       new RunnerOutputPayload
                                                       {
                                                           DataBase64 = Base64(payload),
                                                           Stream     = "stdout"
                                                       }, end);

            await RunnerFrameCodec.WriteFrameAsync(upstream, "exit",
                                                   new RunnerExitPayload { ExitCode = 0, TimedOut = false }, end);
        });
        using var homeScope = home;

        var stdout = new RecordingOutputCapture();
        await backend.ExecuteAsync(Plan(home.Components.SandboxHome), stdout, new RecordingOutputCapture(),
                                   CancellationToken.None);
        Assert.Equal(frameCount * frameSize + frameCount, stdout.Content.Length);
    }


    [Fact]
    public async Task Execute_SpawnChildErrorThrowsStartException()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (backend, _, _, _, home) = CreateBackend(async (downstream, upstream, end) =>
        {
            await RunnerFrameCodec.ReadFrameAsync(downstream, end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "error",
                                                   new RunnerErrorPayload
                                                   {
                                                       Message          = "CreateProcess failed",
                                                       Stage            = "spawn_child",
                                                       WindowsErrorCode = 2
                                                   }, end);
        });
        using var homeScope = home;
        var       stdout    = new RecordingOutputCapture();

        var exception =
            await Assert.ThrowsAsync<ProcessExecutionStartException>(() =>
                                                                         backend.ExecuteAsync(Plan(home.Components
                                                                                              .SandboxHome), stdout,
                                                                                  new RecordingOutputCapture(),
                                                                                  CancellationToken.None));
        Assert.Contains("spawn_child", exception.Message);
        Assert.Contains("CreateProcess failed", exception.Message);
        Assert.Contains("Win32 error 2", exception.Message);
        Assert.True(stdout.Discarded);
    }

    [Fact]
    public async Task Execute_EofBeforeSpawnReadyThrowsStartException()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (backend, _, _, _, home) = CreateBackend(async (downstream, upstream, end) =>
        {
            await RunnerFrameCodec.ReadFrameAsync(downstream, end);
            await Task.Yield();
            // 不发送帧即退出，模拟握手前 runner 终止。
        });
        using var homeScope = home;

        await Assert.ThrowsAsync<ProcessExecutionStartException>(() =>
                                                                     backend.ExecuteAsync(Plan(home.Components
                                                                                          .SandboxHome),
                                                                              new RecordingOutputCapture(),
                                                                              new RecordingOutputCapture(),
                                                                              CancellationToken.None));
    }

    [Fact]
    public async Task Execute_EofBeforeExitThrowsUncertainExecution()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (backend, _, _, _, home) = CreateBackend(async (downstream, upstream, end) =>
        {
            await RunnerFrameCodec.ReadFrameAsync(downstream, end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "spawn_ready", new RunnerSpawnReady { ProcessId = 7 },
                                                   end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "output",
                                                   new RunnerOutputPayload
                                                   {
                                                       DataBase64 = Base64("partial"),
                                                       Stream     = "stdout"
                                                   }, end);
            await upstream.DisposeAsync(); // 无退出帧即关闭管道。
        });
        using var homeScope = home;
        var       stdout    = new RecordingOutputCapture();

        var exception = await Assert.ThrowsAsync<ToolExecutionUncertainException>(() =>
                     backend.ExecuteAsync(Plan(home.Components.SandboxHome), stdout,
                                          new RecordingOutputCapture(), CancellationToken.None));

        Assert.Contains("closed before the exit frame", exception.Message);
        Assert.Contains("partial", stdout.Content);
    }

    [Fact]
    public async Task Execute_MalformedOutputFrameThrowsUncertainExecution()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (backend, _, _, _, home) = CreateBackend(async (downstream, upstream, end) =>
        {
            await RunnerFrameCodec.ReadFrameAsync(downstream, end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "spawn_ready", new RunnerSpawnReady { ProcessId = 7 },
                                                   end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "output",
                                                   new RunnerOutputPayload
                                                   {
                                                       DataBase64 = "!!not-base64!!",
                                                       Stream     = "stdout"
                                                   }, end);
        });
        using var homeScope = home;

        var exception = await Assert.ThrowsAsync<ToolExecutionUncertainException>(() =>
                     backend.ExecuteAsync(Plan(home.Components.SandboxHome), new RecordingOutputCapture(),
                                          new RecordingOutputCapture(), CancellationToken.None));
        Assert.Contains("protocol failure", exception.Message);
    }


    [Fact]
    public async Task Execute_OutputBeforeSpawnReadyFailsTheHandshake()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (backend, _, _, _, home) = CreateBackend(async (downstream, upstream, end) =>
        {
            await RunnerFrameCodec.ReadFrameAsync(downstream, end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "output",
                                                   new RunnerOutputPayload
                                                   {
                                                       DataBase64 = Base64("early"),
                                                       Stream     = "stdout"
                                                   }, end);
        });
        using var homeScope = home;
        var       stdout    = new RecordingOutputCapture();

        var exception =
            await Assert.ThrowsAsync<ProcessExecutionStartException>(() =>
                                                                         backend.ExecuteAsync(Plan(home.Components
                                                                                              .SandboxHome), stdout,
                                                                                  new RecordingOutputCapture(),
                                                                                  CancellationToken.None));

        Assert.Contains("output before spawn_ready", exception.Message);
        Assert.True(stdout.Discarded);
    }

    [Fact]
    public async Task Execute_ErrorFrameAfterSpawnReadyThrowsUncertainExecution()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (backend, _, _, _, home) = CreateBackend(async (downstream, upstream, end) =>
        {
            await RunnerFrameCodec.ReadFrameAsync(downstream, end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "spawn_ready", new RunnerSpawnReady { ProcessId = 22 },
                                                   end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "error",
                                                   new RunnerErrorPayload
                                                   {
                                                       Message = "child vanished",
                                                       Stage   = "wait_child"
                                                   }, end);
        });
        using var homeScope = home;

        var exception = await Assert.ThrowsAsync<ToolExecutionUncertainException>(() =>
                     backend.ExecuteAsync(Plan(home.Components.SandboxHome), new RecordingOutputCapture(),
                                          new RecordingOutputCapture(), CancellationToken.None));

        Assert.Contains("protocol failure", exception.Message);
        Assert.Contains("error frame after spawn_ready", exception.Message);
    }


    [Fact]
    public async Task Execute_UserCancelSendsTerminateDiscardsOutputAndThrows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var terminateSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (backend, _, _, _, home) = CreateBackend(async (downstream, upstream, end) =>
        {
            await RunnerFrameCodec.ReadFrameAsync(downstream, end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "spawn_ready", new RunnerSpawnReady { ProcessId = 9 },
                                                   end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "output",
                                                   new RunnerOutputPayload
                                                   {
                                                       DataBase64 = Base64("partial"),
                                                       Stream     = "stdout"
                                                   }, end);
            while (true)
            {
                var frame = await RunnerFrameCodec.ReadFrameAsync(downstream, end);
                if (frame is null) return; // 连接关闭后停止读取。

                if (frame.Type != "terminate") continue;
                terminateSeen.TrySetResult(true);
                await RunnerFrameCodec.WriteFrameAsync(upstream, "exit",
                                                       new RunnerExitPayload { ExitCode = 1, TimedOut = false }, end);
                return;
            }
        });
        using var homeScope    = home;
        var       stdout       = new RecordingOutputCapture();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                                                                    backend.ExecuteAsync(Plan(home.Components
                                                                                         .SandboxHome, 60), stdout,
                                                                             new RecordingOutputCapture(),
                                                                             cancellation.Token));

        Assert.True(await terminateSeen.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(stdout.Discarded);
    }

    [Fact]
    public async Task Execute_UnresponsiveRunnerAfterTimeoutIsKilledAndReportsBackendTimeout()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (backend, _, _, _, home) = CreateBackend(async (downstream, upstream, end) =>
        {
            await RunnerFrameCodec.ReadFrameAsync(downstream, end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "spawn_ready", new RunnerSpawnReady { ProcessId = 11 },
                                                   end);
            await Task.Delay(Timeout.Infinite, end);
        });
        using var homeScope = home;
        var       stdout    = new RecordingOutputCapture();

        // 命令超时 1 秒并经过 300 毫秒宽限后，仍无退出帧；后端须回收 runner 并返回。
        var result = await backend.ExecuteAsync(Plan(home.Components.SandboxHome, 1), stdout,
                                                new RecordingOutputCapture(), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task Execute_TimeoutKillKeepsCapturedOutputAndReportsBackendTimeout()
    {
        if (!OperatingSystem.IsWindows()) return;

        // runner 输出后保持静默且不响应 terminate；用完成信号固定该边界，避免延时竞态。
        // 模拟 Kill 解除等待并确认进程退出。
        var silentReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (backend, _, _, _, home) = CreateBackend(async (downstream, upstream, end) =>
        {
            await RunnerFrameCodec.ReadFrameAsync(downstream, end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "spawn_ready", new RunnerSpawnReady { ProcessId = 21 },
                                                   end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "output",
                                                   new RunnerOutputPayload
                                                   {
                                                       DataBase64 = Base64("pre-timeout output"),
                                                       Stream     = "stdout"
                                                   }, end);
            silentReached.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, end);
        }, limits : TestLimits with
        {
            CommunicationGrace = TimeSpan.FromMilliseconds(200),
            TerminationDrainTimeout = TimeSpan.FromMilliseconds(200),
            RunnerExitTimeout = TimeSpan.FromMilliseconds(200),
            TerminationKillGrace = TimeSpan.FromMilliseconds(500),
            TerminateWriteTimeout = TimeSpan.FromMilliseconds(500)
        });
        using var homeScope = home;
        var       stdout    = new RecordingOutputCapture();

        var execution = backend.ExecuteAsync(Plan(home.Components.SandboxHome, 1), stdout,
                                             new RecordingOutputCapture(), CancellationToken.None);
        Assert.True(await silentReached.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.Null(result.Failure);
        Assert.Contains("pre-timeout output", stdout.Content);
    }

    [Fact]
    public async Task Execute_LateExitAfterTerminateIsReportedAsBackendTimeout()
    {
        if (!OperatingSystem.IsWindows()) return;

        // 看门狗超时后即使 runner 返回非超时退出帧，后端超时判定仍须生效。
        var (backend, _, _, _, home) = CreateBackend(async (downstream, upstream, end) =>
        {
            await RunnerFrameCodec.ReadFrameAsync(downstream, end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "spawn_ready", new RunnerSpawnReady { ProcessId = 12 },
                                                   end);
            // 保持静默直到看门狗发送 terminate。
            while (await RunnerFrameCodec.ReadFrameAsync(downstream, end) is { } frame)
                if (frame.Type == "terminate")
                    break;

            // 收到 terminate 后返回非超时退出帧。
            await RunnerFrameCodec.WriteFrameAsync(upstream, "exit",
                                                   new RunnerExitPayload { ExitCode = 1, TimedOut = false }, end);
        });
        using var homeScope = home;

        var result = await backend.ExecuteAsync(Plan(home.Components.SandboxHome, 1),
                                                new RecordingOutputCapture(), new RecordingOutputCapture(),
                                                CancellationToken.None);
        Assert.True(result.TimedOut);
        Assert.Equal(1, result.ExitCode);
    }


    [Fact]
    public async Task Execute_LauncherIOExceptionFailsClosedAsStartException()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (backend, _, _, _, home) = CreateBackend(NormalRunner,
                                                     launchError : new IOException("pipe creation denied"));
        using var homeScope = home;

        var exception =
            await Assert.ThrowsAsync<ProcessExecutionStartException>(() =>
                                                                         backend.ExecuteAsync(Plan(home.Components
                                                                                              .SandboxHome),
                                                                                  new RecordingOutputCapture(),
                                                                                  new RecordingOutputCapture(),
                                                                                  CancellationToken.None));

        Assert.Contains("Failed to launch the sandbox runner", exception.Message);
        Assert.Contains("pipe creation denied", exception.Message);
    }

    [Fact]
    public async Task Execute_LauncherCancellationPropagatesWithoutWrapping()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (backend, _, _, _, home) = CreateBackend(NormalRunner,
                                                     launchError : new OperationCanceledException());
        using var homeScope = home;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                                                                    backend.ExecuteAsync(Plan(home.Components
                                                                                         .SandboxHome),
                                                                             new RecordingOutputCapture(),
                                                                             new RecordingOutputCapture(),
                                                                             CancellationToken.None));
    }


    [Fact]
    public async Task Execute_SendsFrozenPlanPolicyAndCleanedEnvironment()
    {
        if (!OperatingSystem.IsWindows()) return;

        var spawnSeen = new TaskCompletionSource<RunnerSpawnRequest>(TaskCreationOptions
                                                                        .RunContinuationsAsynchronously);
        var (backend, setup, desktops, launcher, home) = CreateBackend(async (downstream, upstream, end) =>
                                                                       {
                                                                           var frame = await RunnerFrameCodec
                                                                              .ReadFrameAsync(downstream, end);
                                                                           spawnSeen.TrySetResult(RunnerFrameCodec
                                                                                       .ParsePayload<
                                                                                            RunnerSpawnRequest>(frame!));
                                                                           await RunnerFrameCodec
                                                                              .WriteFrameAsync(upstream, "spawn_ready",
                                                                                        new RunnerSpawnReady
                                                                                            { ProcessId = 13 }, end);
                                                                           await RunnerFrameCodec
                                                                              .WriteFrameAsync(upstream, "exit",
                                                                                        new RunnerExitPayload
                                                                                        {
                                                                                            ExitCode = 0,
                                                                                            TimedOut = false
                                                                                        },
                                                                                        end);
                                                                       },
                                                                       knownSecrets : new Dictionary<string, string>
                                                                           { ["TEST_SECRET"] = "secret-value" });
        using var homeScope = home;

        var originalSecret = Environment.GetEnvironmentVariable("TEST_SECRET");
        Environment.SetEnvironmentVariable("TEST_SECRET", "secret-value");
        try
        {
            var result = await backend.ExecuteAsync(Plan(home.Components.SandboxHome, 45),
                                                    new RecordingOutputCapture(), new RecordingOutputCapture(),
                                                    CancellationToken.None);
            Assert.Equal(0, result.ExitCode);

            var request = await spawnSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(["cmd.exe", "/c", "whoami"], request.Command);
            Assert.Equal(home.Components.SandboxHome, request.WorkingDirectory);
            Assert.Equal(45_000uL, request.TimeoutMilliseconds);
            Assert.False(request.Tty);
            Assert.False(request.StdinOpen);
            Assert.Equal(FakeDesktopFactory.DesktopName, request.PrivateDesktopName);
            Assert.Equal(home.Components.SandboxHome, request.WorkingDirectory);
            Assert.Equal([home.Components.SandboxHome], request.WorkspaceRoots);
            Assert.Equal(Path.Combine(home.Components.SandboxHome, ".sandbox"), request.SandboxDirectory);
            Assert.Equal(home.Components.SandboxHome, request.RealSandboxHome);
            Assert.Equal("restricted", request.PermissionProfile.Network);
            Assert.False(request.Environment.ContainsKey("TEST_SECRET"));
            // 启动环境必须仅含组装时冻结的 TEMP；不得继承额外宿主变量。
            Assert.Single(request.Environment);
            Assert.Equal(home.Components.SandboxHome, request.Environment["TEMP"]);
            Assert.NotEmpty(request.CapabilitySids);
            Assert.All(request.CapabilitySids, sid => Assert.Matches(@"^S-1-5-21-\d+-\d+-\d+-\d+$", sid));

            // 启动账户必须与创建桌面的账户一致。
            Assert.Equal("TinyHarnessOffline", launcher.LastRequest!.Account.Username);
            if (OperatingSystem.IsWindows())
                Assert.Equal(desktops.GrantedSids.Single(), launcher.LastRequest.Account.AccountSid.Value);

            Assert.Equal(home.Components.RunnerExecutablePath, launcher.LastRequest.RunnerExecutablePath);

            // 启动前仅刷新一次，并传递已解析的根路径。
            var refresh = Assert.Single(setup.RefreshPayloads);
            Assert.True(refresh.RefreshOnly);
            Assert.Equal(home.Components.SandboxHome, refresh.SandboxHome);
            Assert.Contains(home.Components.SandboxHome, refresh.WriteRoots);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_SECRET", originalSecret);
        }
    }

    [Fact]
    public async Task Execute_RejectsWorkingDirectoryOutsidePolicyWorkspace()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var outside = new TestTempDir();
        var (backend, _, _, _, home) = CreateBackend(NormalRunner);
        using var homeScope = home;

        await Assert.ThrowsAsync<ProcessExecutionStartException>(() => backend.ExecuteAsync(Plan(outside.Root),
                                                                          new RecordingOutputCapture(),
                                                                          new RecordingOutputCapture(),
                                                                          CancellationToken.None));
    }

    [Fact]
    public async Task Execute_FailsClosedWhenSandboxNotReady()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var dir = new TestTempDir();
        var components = new WindowsSandboxComponents
        {
            SetupExecutablePath  = Path.Combine(dir.Root, "missing-setup.exe"),
            RunnerExecutablePath = Path.Combine(dir.Root, "missing-runner.exe"),
            SandboxHome          = dir.Root
        };
        var (backend, _, _, launcher, home) = CreateBackend(NormalRunner, components : components);
        using var homeScope = home;

        var exception =
            await Assert.ThrowsAsync<ProcessExecutionStartException>(() =>
                                                                         backend.ExecuteAsync(Plan(home.Components
                                                                                              .SandboxHome),
                                                                                  new RecordingOutputCapture(),
                                                                                  new RecordingOutputCapture(),
                                                                                  CancellationToken.None));
        Assert.Contains("not ready", exception.Message);
        Assert.Null(launcher.LastRequest);
    }

    [Fact]
    public async Task Execute_FailsClosedWhenRefreshFails()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (backend, _, _, launcher, home) = CreateBackend(NormalRunner, refreshError :
                                                            new
                                                                InvalidOperationException("provisioning stale: helper_exit_1"));
        using var homeScope = home;

        var exception =
            await Assert.ThrowsAsync<ProcessExecutionStartException>(() =>
                                                                         backend.ExecuteAsync(Plan(home.Components
                                                                                              .SandboxHome),
                                                                                  new RecordingOutputCapture(),
                                                                                  new RecordingOutputCapture(),
                                                                                  CancellationToken.None));
        Assert.Contains("ACL refresh failed", exception.Message);
        Assert.Contains("provisioning stale", exception.Message);
        Assert.Null(launcher.LastRequest);
    }

    [Fact]
    public async Task Execute_HandshakeTimeoutKillsRunnerAndThrows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (backend, _, _, _, home) = CreateBackend(async (downstream, upstream, end) =>
                                                     {
                                                         await RunnerFrameCodec.ReadFrameAsync(downstream, end);
                                                         await Task.Delay(Timeout.Infinite, end);
                                                     },
                                                     limits : TestLimits with
                                                     {
                                                         SpawnReadyTimeout = TimeSpan.FromMilliseconds(300)
                                                     });
        using var homeScope = home;

        var exception =
            await Assert.ThrowsAsync<ProcessExecutionStartException>(() =>
                                                                         backend.ExecuteAsync(Plan(home.Components
                                                                                              .SandboxHome),
                                                                                  new RecordingOutputCapture(),
                                                                                  new RecordingOutputCapture(),
                                                                                  CancellationToken.None));
        Assert.Contains("spawn_ready", exception.Message);
    }
}

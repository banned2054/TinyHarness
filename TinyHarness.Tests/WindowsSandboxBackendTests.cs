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
            Func<Stream, Stream, CancellationToken, Task>       runnerBody,
            string?                                             workspaceRoot     = null,
            SandboxPolicyKind                                   kind              = SandboxPolicyKind.WorkspaceWrite,
            IReadOnlyDictionary<string, string>?                knownSecrets      = null,
            Exception?                                          refreshError      = null,
            WindowsSandboxComponents?                           components        = null,
            WindowsSandboxBackendLimits?                        limits            = null,
            Exception?                                          launchError       = null,
            Func<SandboxSetupPayload, CancellationToken, Task>? validateWriteAcls = null)
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
                                                limits : limits ?? TestLimits, allowNullDeviceAccess : _ => { },
                                                validateWriteAcls : validateWriteAcls ??
                                                                    ((_, _) => Task.CompletedTask));
        return (backend, setup, desktops, launcher, home);
    }

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Test]
    public async Task Execute_IncompleteChildInheritanceBlocksBeforeCredentialsAndRunner()
    {
        if (!OperatingSystem.IsWindows()) return;
        var validations = 0;
        var (backend, setup, desktops, launcher, home) = CreateBackend(NormalRunner,
                                                                       validateWriteAcls : (payload, _) =>
                                                                       {
                                                                           validations++;
                                                                           Assert.That(payload.RefreshOnly, Is.True);
                                                                           throw new
                                                                               InvalidOperationException("inheritance missing at src/PriceLab/obj");
                                                                       });
        using (home)
        {
            var error = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() => backend.ExecuteAsync(
                          Plan(home.Components.SandboxHome), new RecordingOutputCapture(), new RecordingOutputCapture(),
                          CancellationToken.None));
            Assert.That(validations, Is.EqualTo(1));
            Assert.That(error.Message, Does.Contain("src/PriceLab/obj"));
            Assert.That(launcher.LastRequest, Is.Null);
            Assert.That(setup.RefreshPayloads, Has.Count.EqualTo(1));
        }
    }


    [Test]
    public async Task Execute_NormalExitAssemblesStreamsAndResult()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (backend, setup, desktops, launcher, home) = CreateBackend(NormalRunner);
        using var homeScope = home;
        var       stdout    = new RecordingOutputCapture();
        var       stderr    = new RecordingOutputCapture();

        var result = await backend.ExecuteAsync(Plan(home.Components.SandboxHome), stdout, stderr,
                                                CancellationToken.None);

        Assert.That(result.ExitCode, Is.EqualTo(0));
        Assert.That(result.TimedOut, Is.False);
        Assert.That(result.Failure, Is.Null);
        Assert.That(stdout.Content, Does.Contain("stdout-hello 世界"));
        Assert.That(stderr.Content, Does.Contain("stderr-line"));
        TestAssert.DoesNotContain("stderr", stdout.Content, StringComparison.Ordinal);
        TestAssert.All([stdout, stderr], capture => Assert.That(capture.Chunks.Last(), Is.EqualTo(string.Empty)));
    }

    private static async Task NormalRunner(Stream downstream, Stream upstream, CancellationToken end)
    {
        var spawn = await RunnerFrameCodec.ReadFrameAsync(downstream, end);
        TestAssert.NotNull(spawn);
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

    [Test]
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

        Assert.That(timedOutResult.ExitCode, Is.EqualTo(192));
        Assert.That(timedOutResult.TimedOut, Is.True);
        Assert.That(naturalResult.ExitCode, Is.EqualTo(192));
        Assert.That(naturalResult.TimedOut, Is.False);
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

    [Test]
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
        Assert.That(stdout.Content, Is.EqualTo("世界"));
    }

    [Test]
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
        Assert.That(stdout.Content.Length, Is.EqualTo(frameCount * frameSize + frameCount));
    }


    [Test]
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
            await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() =>
                                                                             backend.ExecuteAsync(Plan(home.Components
                                                                                                  .SandboxHome), stdout,
                                                                                      new RecordingOutputCapture(),
                                                                                      CancellationToken.None));
        Assert.That(exception.Message, Does.Contain("spawn_child"));
        Assert.That(exception.Message, Does.Contain("CreateProcess failed"));
        Assert.That(exception.Message, Does.Contain("Win32 error 2"));
        Assert.That(stdout.Discarded, Is.True);
    }

    [Test]
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

        await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() =>
                                                                         backend.ExecuteAsync(Plan(home.Components
                                                                                              .SandboxHome),
                                                                                  new RecordingOutputCapture(),
                                                                                  new RecordingOutputCapture(),
                                                                                  CancellationToken.None));
    }

    [Test]
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

        var exception = await TestAssert.ThrowsAsync<ToolExecutionUncertainException>(() =>
                     backend.ExecuteAsync(Plan(home.Components.SandboxHome), stdout,
                                          new RecordingOutputCapture(), CancellationToken.None));

        Assert.That(exception.Message, Does.Contain("closed before the exit frame"));
        Assert.That(stdout.Content, Does.Contain("partial"));
    }

    [Test]
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

        var exception = await TestAssert.ThrowsAsync<ToolExecutionUncertainException>(() =>
                     backend.ExecuteAsync(Plan(home.Components.SandboxHome), new RecordingOutputCapture(),
                                          new RecordingOutputCapture(), CancellationToken.None));
        Assert.That(exception.Message, Does.Contain("protocol failure"));
    }


    [Test]
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
            await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() =>
                                                                             backend.ExecuteAsync(Plan(home.Components
                                                                                                  .SandboxHome), stdout,
                                                                                      new RecordingOutputCapture(),
                                                                                      CancellationToken.None));

        Assert.That(exception.Message, Does.Contain("output before spawn_ready"));
        Assert.That(stdout.Discarded, Is.True);
    }

    [Test]
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

        var exception = await TestAssert.ThrowsAsync<ToolExecutionUncertainException>(() =>
                     backend.ExecuteAsync(Plan(home.Components.SandboxHome), new RecordingOutputCapture(),
                                          new RecordingOutputCapture(), CancellationToken.None));

        Assert.That(exception.Message, Does.Contain("protocol failure"));
        Assert.That(exception.Message, Does.Contain("error frame after spawn_ready"));
    }


    [Test]
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

        await Assert.CatchAsync<OperationCanceledException>(() =>
                                                                backend.ExecuteAsync(Plan(home.Components
                                                                                     .SandboxHome, 60), stdout,
                                                                         new RecordingOutputCapture(),
                                                                         cancellation.Token));

        Assert.That(await terminateSeen.Task.WaitAsync(TimeSpan.FromSeconds(3)), Is.True);
        Assert.That(stdout.Discarded, Is.True);
    }

    [Test]
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

        Assert.That(result.TimedOut, Is.True);
        Assert.That(result.ExitCode, Is.Null);
        Assert.That(result.Failure, Is.Null);
    }

    [Test]
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
        Assert.That(await silentReached.Task.WaitAsync(TimeSpan.FromSeconds(3)), Is.True);
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(result.TimedOut, Is.True);
        Assert.That(result.ExitCode, Is.Null);
        Assert.That(result.Failure, Is.Null);
        Assert.That(stdout.Content, Does.Contain("pre-timeout output"));
    }

    [Test]
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
        Assert.That(result.TimedOut, Is.True);
        Assert.That(result.ExitCode, Is.EqualTo(1));
    }


    [Test]
    public async Task Execute_LauncherIOExceptionFailsClosedAsStartException()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (backend, _, _, _, home) = CreateBackend(NormalRunner,
                                                     launchError : new IOException("pipe creation denied"));
        using var homeScope = home;

        var exception =
            await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() =>
                                                                             backend.ExecuteAsync(Plan(home.Components
                                                                                                  .SandboxHome),
                                                                                      new RecordingOutputCapture(),
                                                                                      new RecordingOutputCapture(),
                                                                                      CancellationToken.None));

        Assert.That(exception.Message, Does.Contain("Failed to launch the sandbox runner"));
        Assert.That(exception.Message, Does.Contain("pipe creation denied"));
    }

    [Test]
    public async Task Execute_LauncherCancellationPropagatesWithoutWrapping()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (backend, _, _, _, home) = CreateBackend(NormalRunner,
                                                     launchError : new OperationCanceledException());
        using var homeScope = home;

        await Assert.CatchAsync<OperationCanceledException>(() =>
                                                                backend.ExecuteAsync(Plan(home.Components
                                                                                     .SandboxHome),
                                                                         new RecordingOutputCapture(),
                                                                         new RecordingOutputCapture(),
                                                                         CancellationToken.None));
    }


    [Test]
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
            Assert.That(result.ExitCode, Is.EqualTo(0));

            var request = await spawnSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(request.Command, Is.EqualTo(["cmd.exe", "/c", "whoami"]));
            Assert.That(request.WorkingDirectory, Is.EqualTo(home.Components.SandboxHome));
            Assert.That(request.TimeoutMilliseconds, Is.EqualTo(45_000uL));
            Assert.That(request.Tty, Is.False);
            Assert.That(request.StdinOpen, Is.False);
            Assert.That(request.PrivateDesktopName, Is.EqualTo(FakeDesktopFactory.DesktopName));
            Assert.That(request.WorkingDirectory, Is.EqualTo(home.Components.SandboxHome));
            Assert.That(request.WorkspaceRoots, Is.EqualTo([home.Components.SandboxHome]));
            Assert.That(request.SandboxDirectory, Is.EqualTo(Path.Combine(home.Components.SandboxHome, ".sandbox")));
            Assert.That(request.RealSandboxHome, Is.EqualTo(home.Components.SandboxHome));
            Assert.That(request.PermissionProfile.Network, Is.EqualTo("restricted"));
            Assert.That(request.Environment.ContainsKey("TEST_SECRET"), Is.False);
            // 启动环境必须仅含组装时冻结的 TEMP；不得继承额外宿主变量。
            TestAssert.Single(request.Environment);
            Assert.That(request.Environment["TEMP"], Is.EqualTo(home.Components.SandboxHome));
            Assert.That(request.CapabilitySids, Is.Not.Empty);
            TestAssert.All(request.CapabilitySids, sid => Assert.That(sid, Does.Match(@"^S-1-5-21-\d+-\d+-\d+-\d+$")));

            // 启动账户必须与创建桌面的账户一致。
            Assert.That(launcher.LastRequest!.Account.Username, Is.EqualTo("TinyHarnessOffline"));
            if (OperatingSystem.IsWindows())
                Assert.That(launcher.LastRequest.Account.AccountSid.Value, Is.EqualTo(desktops.GrantedSids.Single()));

            Assert.That(launcher.LastRequest.RunnerExecutablePath, Is.EqualTo(home.Components.RunnerExecutablePath));

            // 启动前仅刷新一次，并传递已解析的根路径。
            var refresh = TestAssert.Single(setup.RefreshPayloads);
            Assert.That(refresh.RefreshOnly, Is.True);
            Assert.That(refresh.SandboxHome, Is.EqualTo(home.Components.SandboxHome));
            Assert.That(refresh.WriteRoots, Does.Contain(home.Components.SandboxHome));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_SECRET", originalSecret);
        }
    }

    [Test]
    public async Task Execute_RejectsWorkingDirectoryOutsidePolicyWorkspace()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var outside = new TestTempDir();
        var (backend, _, _, _, home) = CreateBackend(NormalRunner);
        using var homeScope = home;

        await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() => backend.ExecuteAsync(Plan(outside.Root),
                                                                              new RecordingOutputCapture(),
                                                                              new RecordingOutputCapture(),
                                                                              CancellationToken.None));
    }

    [Test]
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
            await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() =>
                                                                             backend.ExecuteAsync(Plan(home.Components
                                                                                                  .SandboxHome),
                                                                                      new RecordingOutputCapture(),
                                                                                      new RecordingOutputCapture(),
                                                                                      CancellationToken.None));
        Assert.That(exception.Message, Does.Contain("not ready"));
        Assert.That(launcher.LastRequest, Is.Null);
    }

    [Test]
    public async Task Execute_FailsClosedWhenRefreshFails()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (backend, _, _, launcher, home) = CreateBackend(NormalRunner, refreshError :
                                                            new
                                                                InvalidOperationException("provisioning stale: helper_exit_1"));
        using var homeScope = home;

        var exception =
            await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() =>
                                                                             backend.ExecuteAsync(Plan(home.Components
                                                                                                  .SandboxHome),
                                                                                      new RecordingOutputCapture(),
                                                                                      new RecordingOutputCapture(),
                                                                                      CancellationToken.None));
        Assert.That(exception.Message, Does.Contain("ACL refresh failed"));
        Assert.That(exception.Message, Does.Contain("provisioning stale"));
        Assert.That(launcher.LastRequest, Is.Null);
    }

    [Test]
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
            await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() =>
                                                                             backend.ExecuteAsync(Plan(home.Components
                                                                                                  .SandboxHome),
                                                                                      new RecordingOutputCapture(),
                                                                                      new RecordingOutputCapture(),
                                                                                      CancellationToken.None));
        Assert.That(exception.Message, Does.Contain("spawn_ready"));
    }
}

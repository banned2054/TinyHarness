using System.Text;
using System.Text.Json;
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
        string?                                             workspaceRoot           = null,
        SandboxPolicyKind                                   kind                    = SandboxPolicyKind.WorkspaceWrite,
        IReadOnlyDictionary<string, string>?                knownSecrets            = null,
        Exception?                                          refreshError            = null,
        WindowsSandboxComponents?                           components              = null,
        WindowsSandboxBackendLimits?                        limits                  = null,
        Exception?                                          launchError             = null,
        Func<SandboxSetupPayload, CancellationToken, Task>? validateWriteAcls       = null,
        Func<SandboxSetupPayload, CancellationToken, Task>? validateReadRootReadiness = null,
        Func<WindowsSandboxComponents, SandboxIsolationPolicy, SandboxIsolationPolicy>? policyOverride = null,
        ISandboxDenyReadRefreshGate?                        denyReadRefreshGate     = null)
    {
        var home              = new SandboxTestHome();
        var workspace         = workspaceRoot ?? home.Components.SandboxHome; // 策略范围内的测试根。
        var targetEnvironment = new Dictionary<string, string> { ["TEMP"] = workspace };
        var policy            = SandboxPolicyResolver.Resolve(kind, workspace, targetEnvironment);
        if (policyOverride is not null) policy = policyOverride(home.Components, policy);
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
                                                                    ((_, _) => Task.CompletedTask),
                                                // 读根就绪门按用例注入：默认 stub（无真实 DACL 依赖），
                                                // 就绪门行为由 SandboxReadRootReadinessTests 单独覆盖。
                                                validateReadRootReadiness : validateReadRootReadiness ??
                                                                            ((_, _) => Task.CompletedTask),
                                                denyReadRefreshGate : denyReadRefreshGate);
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

#if DEBUG
    [Test]
    public async Task Execute_PreflightFailureDiagnosticLogsWhitelistedFieldsOnly()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var logs = new TestTempDir();
        SandboxDiagnosticLog.ResetForTests();
        SandboxDiagnosticLog.LogDirectoryOverrideForTests = logs.Root;
        SandboxDiagnosticLog.Initialize();
        try
        {
            var arbitraryHelperText = "arbitrary helper text from setup_error.json";
            var (backend, setup, desktops, launcher, home) = CreateBackend(NormalRunner,
                                                                           refreshError : new InvalidOperationException(
                                                                            arbitraryHelperText));
            using (home)
            {
                var error = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() => backend.ExecuteAsync(
                 Plan(home.Components.SandboxHome), new RecordingOutputCapture(),
                 new RecordingOutputCapture(), CancellationToken.None));
                Assert.That(error.Message, Does.Contain(arbitraryHelperText),
                            "The product exception message must stay unchanged.");
            }

            await SandboxDiagnosticLog.FlushForTestsAsync();
            var text = await File.ReadAllTextAsync(Directory.GetFiles(logs.Root, "sandbox-diagnostic-*.log").Single());
            Assert.That(text, Does.Contain("phase=refresh"));
            Assert.That(text, Does.Contain("type=InvalidOperationException"));
            Assert.That(text, Does.Not.Contain(arbitraryHelperText),
                        "Diagnostics must not copy arbitrary exception messages into the log.");
        }
        finally
        {
            SandboxDiagnosticLog.LogDirectoryOverrideForTests = null;
            SandboxDiagnosticLog.ResetForTests();
        }
    }
#endif

    [Test]
    public async Task Execute_StructuredAclFailureSurvivesPreflightWrappingWithoutLaunchingRunner()
    {
        if (!OperatingSystem.IsWindows()) return;
        WorkspaceAclProblem? problem = null;
        var (backend, setup, desktops, launcher, home) =
            CreateBackend(NormalRunner,
                          validateWriteAcls : (payload, _) =>
                          {
                              problem = new WorkspaceAclProblem(payload.WriteRoots[0],
                                                                Path.Combine(payload.WriteRoots[0], ".github"),
                                                                WorkspaceAclProblemCategory.MissingInheritedAccess,
                                                                [
                                                                    new WorkspaceAclMissingAce("S-1-5-21-1-2-3-1001",
                                                                        0x1301BF, 0, 0, 0)
                                                                ],
                                                                false, 5);
                              throw new WorkspaceAclPreparationException(problem);
                          });
        using (home)
        {
            var error = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() => backend.ExecuteAsync(
             Plan(home.Components.SandboxHome), new RecordingOutputCapture(), new RecordingOutputCapture(),
             CancellationToken.None));
            Assert.That(error.InnerException, Is.TypeOf<WorkspaceAclPreparationException>());
            Assert.That(((WorkspaceAclPreparationException)error.InnerException!).Problem, Is.SameAs(problem));
            Assert.That(error.Message, Does.Not.Contain("/trust"));
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
            {
                return backend.ExecuteAsync(Plan(home.Components.SandboxHome), stdout,
                                            new RecordingOutputCapture(), CancellationToken.None);
            });
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
        {
            return backend.ExecuteAsync(Plan(home.Components.SandboxHome), new RecordingOutputCapture(),
                                        new RecordingOutputCapture(), CancellationToken.None);
        });
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
            {
                return backend.ExecuteAsync(Plan(home.Components.SandboxHome), stdout,
                                            new RecordingOutputCapture(), CancellationToken.None);
            });

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
        {
            return backend.ExecuteAsync(Plan(home.Components.SandboxHome, 60), stdout,
                                        new RecordingOutputCapture(), cancellation.Token);
        });

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
            {
                return backend.ExecuteAsync(Plan(home.Components.SandboxHome), new RecordingOutputCapture(),
                                            new RecordingOutputCapture(), CancellationToken.None);
            });

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
        {
            return backend.ExecuteAsync(Plan(home.Components.SandboxHome), new RecordingOutputCapture(),
                                        new RecordingOutputCapture(), CancellationToken.None);
        });
    }


    [Test]
    public async Task Execute_SendsFrozenPlanPolicyAndCleanedEnvironment()
    {
        if (!OperatingSystem.IsWindows()) return;

        var spawnSeen =
            new TaskCompletionSource<RunnerSpawnRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (backend, setup, desktops, launcher, home) =
            CreateBackend(async (downstream, upstream, end) =>
                          {
                              var frame = await RunnerFrameCodec.ReadFrameAsync(downstream, end);
                              spawnSeen.TrySetResult(RunnerFrameCodec.ParsePayload<RunnerSpawnRequest>(frame!));
                              await RunnerFrameCodec.WriteFrameAsync(upstream, "spawn_ready",
                                                                     new RunnerSpawnReady { ProcessId = 13 }, end);
                              await RunnerFrameCodec.WriteFrameAsync(upstream, "exit",
                                                                     new RunnerExitPayload
                                                                     {
                                                                         ExitCode = 0,
                                                                         TimedOut = false
                                                                     },
                                                                     end);
                          },
                          knownSecrets : new Dictionary<string, string> { ["TEST_SECRET"] = "secret-value" });
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
            {
                return backend.ExecuteAsync(Plan(home.Components.SandboxHome), new RecordingOutputCapture(),
                                            new RecordingOutputCapture(), CancellationToken.None);
            });
        Assert.That(exception.Message, Does.Contain("not ready"));
        Assert.That(launcher.LastRequest, Is.Null);
    }

    [Test]
    public async Task Execute_FailsClosedWhenRefreshFails()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (backend, _, _, launcher, home) =
            CreateBackend(NormalRunner,
                          refreshError : new InvalidOperationException("provisioning stale: helper_exit_1"));
        using var homeScope = home;

        var exception =
            await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() =>
            {
                return backend.ExecuteAsync(Plan(home.Components.SandboxHome), new RecordingOutputCapture(),
                                            new RecordingOutputCapture(), CancellationToken.None);
            });
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
            {
                return backend.ExecuteAsync(Plan(home.Components.SandboxHome), new RecordingOutputCapture(),
                                            new RecordingOutputCapture(), CancellationToken.None);
            });
        Assert.That(exception.Message, Does.Contain("spawn_ready"));
    }

    [Test]
    public async Task Execute_FrozenGrantScope_MergesPayloadAndResolvesGrantSid()
    {
        if (!OperatingSystem.IsWindows()) return;

        var spawnSeen =
            new TaskCompletionSource<RunnerSpawnRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (backend, setup, _, _, home) =
            CreateBackend(async (downstream, upstream, end) =>
                          {
                              var frame = await RunnerFrameCodec.ReadFrameAsync(downstream, end);
                              spawnSeen.TrySetResult(RunnerFrameCodec.ParsePayload<RunnerSpawnRequest>(frame!));
                              await RunnerFrameCodec.WriteFrameAsync(upstream, "spawn_ready",
                                                                     new RunnerSpawnReady { ProcessId = 21 }, end);
                              await RunnerFrameCodec.WriteFrameAsync(upstream, "exit",
                                                                     new RunnerExitPayload
                                                                     {
                                                                         ExitCode = 0,
                                                                         TimedOut = false
                                                                     },
                                                                     end);
                          });
        using var homeScope = home;

        // grant 写根位于 home 之内（合法：凭据目录之外），基线 writeRoots 不含它。
        var grantRoot = Directory.CreateDirectory(Path.Combine(home.Components.SandboxHome,
                                                               "grant-root")).FullName;
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite,
                                                   home.Components.SandboxHome,
                                                   new Dictionary<string, string>
                                                   {
                                                       ["TEMP"] = home.Components.SandboxHome
                                                   });
        var scope = WindowsSandboxComposer.ResolveGrantScope(policy, home.Components,
                                                             null, [grantRoot], null);
        Assert.That(scope.WriteRoots, Does.Contain(grantRoot));

        var grantedPlan = Plan(home.Components.SandboxHome) with { GrantScope = scope };
        var result = await backend.ExecuteAsync(grantedPlan, new RecordingOutputCapture(),
                                                new RecordingOutputCapture(), CancellationToken.None);
        Assert.That(result.ExitCode, Is.EqualTo(0));

        // payload 合并：refresh 的 write_roots 必须含 grant 根（deny 基线不变）。
        var refresh = TestAssert.Single(setup.RefreshPayloads);
        Assert.That(refresh.WriteRoots, Does.Contain(grantRoot));
        Assert.That(refresh.WriteRoots, Does.Contain(home.Components.SandboxHome));

        // capability SID：grant 根获得独立 SID（基线 + grant 根各一个）。
        var request = await spawnSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(request.CapabilitySids, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Execute_EmptyGrantScope_KeepsBaselinePayloadAndSids()
    {
        if (!OperatingSystem.IsWindows()) return;

        var spawnSeen =
            new TaskCompletionSource<RunnerSpawnRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (backend, setup, _, _, home) =
            CreateBackend(async (downstream, upstream, end) =>
                          {
                              var frame = await RunnerFrameCodec.ReadFrameAsync(downstream, end);
                              spawnSeen.TrySetResult(RunnerFrameCodec.ParsePayload<RunnerSpawnRequest>(frame!));
                              await RunnerFrameCodec.WriteFrameAsync(upstream, "spawn_ready",
                                                                     new RunnerSpawnReady { ProcessId = 22 }, end);
                              await RunnerFrameCodec.WriteFrameAsync(upstream, "exit",
                                                                     new RunnerExitPayload
                                                                     {
                                                                         ExitCode = 0,
                                                                         TimedOut = false
                                                                     },
                                                                     end);
                          });
        using var homeScope = home;

        // 空 GrantScope（null）与基线执行完全一致：payload 无 grant 根、SID 只含基线根。
        var result = await backend.ExecuteAsync(Plan(home.Components.SandboxHome), new RecordingOutputCapture(),
                                                new RecordingOutputCapture(), CancellationToken.None);
        Assert.That(result.ExitCode, Is.EqualTo(0));

        var refresh = TestAssert.Single(setup.RefreshPayloads);
        Assert.That(refresh.WriteRoots, Does.Not.Contain(Path.Combine(home.Components.SandboxHome, "grant-root")));
        var request = await spawnSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(request.CapabilitySids, Has.Count.EqualTo(1));
    }

    // ---------------------------------------------------------------- 拒读簿记并集与后刷新校验

    private static string DenyBookkeepingJson(params (string Sid, string[] Paths)[] principals)
    {
        var principalsMap = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (sid, paths) in principals) principalsMap[sid] = paths;

        return JsonSerializer.Serialize(new { principals = principalsMap });
    }

    private static void WriteDenyBookkeeping(string home, string json) =>
        File.WriteAllText(Path.Combine(home, ".sandbox", "deny_read_acl_state.json"), json);

    [Test]
    public async Task Execute_RefreshPayloadFoldsDenyReadBookkeepingUnion()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var outside = new TestTempDir();
        var (backend, setup, _, _, home) = CreateBackend(NormalRunner,
                                                         policyOverride : (components, policy) => policy with
                                                         {
                                                             DenyReadPaths = [Path.Combine(components.SandboxHome,
                                                                                           ".ssh")]
                                                         });
        using var homeScope = home;
        var       sandboxHome = home.Components.SandboxHome;
        var       semantic    = Path.Combine(sandboxHome, ".ssh");
        var       pairable    = Path.Combine(sandboxHome, "legacy-scan-hit");
        var       unpaired    = Path.Combine(outside.Root, "unrelated-hit");
        WriteDenyBookkeeping(sandboxHome,
                             DenyBookkeepingJson(("S-1-5-21-9001", [pairable, sandboxHome, unpaired])));

        await backend.ExecuteAsync(Plan(sandboxHome), new RecordingOutputCapture(), new RecordingOutputCapture(),
                                   CancellationToken.None);

        // refresh payload 的 deny_read_paths = 冻结语义集 ∪ 可配对簿记条目（只增不减）：
        // 等于读根本身与脱离覆盖/兄弟的条目被过滤，读根集合不受并集影响。
        var refresh = TestAssert.Single(setup.RefreshPayloads);
        Assert.Multiple(() =>
        {
            Assert.That(refresh.DenyReadPaths, Does.Contain(semantic));
            Assert.That(refresh.DenyReadPaths, Does.Contain(pairable));
            Assert.That(refresh.DenyReadPaths, Does.Not.Contain(sandboxHome));
            Assert.That(refresh.DenyReadPaths, Does.Not.Contain(unpaired));
            Assert.That(refresh.ReadRoots, Does.Contain(sandboxHome));
        });
    }

    [Test]
    public async Task Execute_BookkeepingEntriesAddedWhileSessionFrozen_AreStillApplied()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (backend, setup, _, _, home) = CreateBackend(NormalRunner,
                                                         policyOverride : (components, policy) => policy with
                                                         {
                                                             DenyReadPaths = [Path.Combine(components.SandboxHome,
                                                                                           ".ssh")]
                                                         });
        using var homeScope  = home;
        var       sandboxHome = home.Components.SandboxHome;
        var       semantic    = Path.Combine(sandboxHome, ".ssh");
        var       lateEntry   = Path.Combine(sandboxHome, "late-scan-hit");

        // 第一条命令：会话冻结的语义集为唯一来源，无簿记文件=空集贡献。
        await backend.ExecuteAsync(Plan(sandboxHome), new RecordingOutputCapture(), new RecordingOutputCapture(),
                                   CancellationToken.None);
        Assert.That(TestAssert.Single(setup.RefreshPayloads).DenyReadPaths, Is.EqualTo([semantic]));

        // 会话冻结期间另一端（共享主体与 home 的 CLI）写入簿记：第二条命令 refresh 前并入，
        // 语义集永不收缩——只增不减正是交替 refresh 不撤另一端 deny 的收口点。
        WriteDenyBookkeeping(sandboxHome, DenyBookkeepingJson(("S-1-5-21-9001", [lateEntry])));
        await backend.ExecuteAsync(Plan(sandboxHome), new RecordingOutputCapture(), new RecordingOutputCapture(),
                                   CancellationToken.None);

        Assert.That(setup.RefreshPayloads, Has.Count.EqualTo(2));
        var second = setup.RefreshPayloads[1];
        Assert.Multiple(() =>
        {
            Assert.That(second.DenyReadPaths, Does.Contain(semantic));
            Assert.That(second.DenyReadPaths, Does.Contain(lateEntry));
            Assert.That(second.DenyReadPaths.Count, Is.GreaterThanOrEqualTo(setup.RefreshPayloads[0].DenyReadPaths.Count));
        });
    }

    [Test]
    public async Task Execute_PostRefreshValidationRunsWriteThenReadinessBeforeLaunch()
    {
        if (!OperatingSystem.IsWindows()) return;

        var trace = new List<string>();
        var (backend, setup, _, launcher, home) = CreateBackend(async (downstream, upstream, end) =>
        {
            trace.Add("launch");
            await NormalRunner(downstream, upstream, end);
        }, validateWriteAcls : (_, _) =>
        {
            trace.Add("write");
            return Task.CompletedTask;
        }, validateReadRootReadiness : (_, _) =>
        {
            trace.Add("readiness");
            return Task.CompletedTask;
        });
        using var homeScope = home;

        await backend.ExecuteAsync(Plan(home.Components.SandboxHome), new RecordingOutputCapture(),
                                   new RecordingOutputCapture(), CancellationToken.None);

        // 共享后刷新校验的固定顺序：refresh 成功 → 写 ACL 继承校验 → 组读根就绪门 → 启动。
        Assert.Multiple(() =>
        {
            Assert.That(trace, Is.EqualTo(["write", "readiness", "launch"]));
            Assert.That(setup.RefreshPayloads, Has.Count.EqualTo(1));
            Assert.That(launcher.LastRequest, Is.Not.Null);
        });
    }

    [Test]
    public async Task Execute_UnreadyReadRootsFailClosedBeforeLaunch()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (backend, setup, _, launcher, home) = CreateBackend(NormalRunner,
                                                                validateReadRootReadiness : (_, _) =>
                                                                    throw new ProcessExecutionStartException(
                                                                        "read-root readiness blocked by test stub",
                                                                        new TimeoutException("stub")));
        using var homeScope = home;

        var error = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() => backend.ExecuteAsync(
            Plan(home.Components.SandboxHome), new RecordingOutputCapture(), new RecordingOutputCapture(),
            CancellationToken.None));

        // 就绪门失败为启动期异常原样上抛（不叠加 refresh 前缀），命令保持未启动。
        Assert.Multiple(() =>
        {
            Assert.That(error.Message, Does.Contain("read-root readiness"));
            Assert.That(setup.RefreshPayloads, Has.Count.EqualTo(1));
            Assert.That(launcher.LastRequest, Is.Null);
        });
    }

    // ---------------------------------------------------------------- 拒读刷新门（跨进程串行化）

    /// <summary>共享 home 上模拟 helper 全量同步的簿记主体 SID。 The bookkeeping principal SID for the helper's wholesale sync on a shared home.</summary>
    private const string BookkeepingSid = "S-1-5-21-9001";

    private static string[] ReadDenyBookkeepingPaths(string home, string sid)
    {
        var path = Path.Combine(home, ".sandbox", "deny_read_acl_state.json");
        if (!File.Exists(path)) return []; // helper 的 load_state：文件缺失 = 空簿记（全新 home）。

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement
                       .GetProperty("principals")
                       .GetProperty(sid)
                       .EnumerateArray()
                       .Select(element => element.GetString()!)
                       .ToArray();
    }

    private static SandboxIsolationPolicy ResolveSharedHomePolicy(WindowsSandboxComponents components,
                                                                  params string[]          semanticDenyPaths)
    {
        var targetEnvironment = new Dictionary<string, string> { ["TEMP"] = components.SandboxHome };
        return SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, components.SandboxHome,
                                             targetEnvironment)
                   with { DenyReadPaths = semanticDenyPaths };
    }

    private static (WindowsSandboxBackend Backend, FakeSandboxRunnerLauncher Launcher) CreateSharedHomeBackend(
        WindowsSandboxComponents     components,
        SandboxIsolationPolicy       policy,
        ISandboxSetupInvoker         setup,
        WindowsSandboxBackendLimits? limits     = null,
        Func<Stream, Stream, CancellationToken, Task>? runnerBody = null,
        ISandboxDenyReadRefreshGate? denyReadRefreshGate = null)
    {
        var targetEnvironment = new Dictionary<string, string> { ["TEMP"] = components.SandboxHome };
        var launcher          = new FakeSandboxRunnerLauncher { RunnerBody = runnerBody ?? NormalRunner };
        var backend           = new WindowsSandboxBackend(
                                                components, policy, targetEnvironment, null, setup,
                                                new FakeCredentialSource(),
                                                desktopFactory : new FakeDesktopFactory(), runnerLauncher : launcher,
                                                limits : limits ?? TestLimits, allowNullDeviceAccess : _ => { },
                                                validateWriteAcls : (_, _) => Task.CompletedTask,
                                                validateReadRootReadiness : (_, _) => Task.CompletedTask,
                                                denyReadRefreshGate : denyReadRefreshGate);
        return (backend, launcher);
    }

    /// <summary>
    ///     模拟 helper ACL 层的 deny 面：门内 refresh 按主体语义施加/撤销，最终断言用。
    ///     The simulated helper ACL deny face: gate-held refreshes apply and revoke per the
    ///     helper's semantics, asserted at the end.
    /// </summary>
    private sealed class SimulatedDenyState(params string[] initialPaths)
    {
        // 门保证 [读簿记→合并→refresh] 跨进程串行化后，本状态只在门内变更；无须额外锁。
        private readonly HashSet<string> _paths = new(initialPaths, StringComparer.OrdinalIgnoreCase);

        public void Revoke(IEnumerable<string> paths)
        {
            foreach (var path in paths) _paths.Remove(path);
        }

        public void UnionWith(IEnumerable<string> paths) => _paths.UnionWith(paths);

        public string[] Snapshot() => [.. _paths];
    }

    /// <summary>
    ///     模拟 helper sync_persistent_principal_paths 的 fake invoker：load_state → applied=
    ///     payload.DenyReadPaths → 从模拟 deny 面撤销 previous 中不在 applied 的路径 →
    ///     principals[sid]=applied 写回真实簿记文件；钩子支持确定性交错。
    ///     A fake invoker simulating the helper's sync_persistent_principal_paths:
    ///     load_state → applied = payload.DenyReadPaths → revoke the previous paths missing
    ///     from applied on the simulated deny face → store principals[sid]=applied into the
    ///     real bookkeeping file; hooks support deterministic interleaving.
    /// </summary>
    private sealed class SimulatedHelperInvoker(
        string             sandboxHome,
        string             principalSid,
        SimulatedDenyState denyState,
        Func<Task>?        beforeApply = null,
        Action?            onEntered   = null,
        Action?            onCompleted = null) : ISandboxSetupInvoker
    {
        public List<SandboxSetupPayload> RefreshPayloads { get; } = [];

        public async Task RefreshAsync(SandboxSetupPayload payload, CancellationToken cancellationToken)
        {
            onEntered?.Invoke();
            if (beforeApply is not null) await beforeApply().ConfigureAwait(false);
            RefreshPayloads.Add(payload);

            // helper 语义：全量按主体同步——不在 payload 即撤销，簿记写回 applied 全集。
            var previous = ReadDenyBookkeepingPaths(sandboxHome, principalSid);
            var applied  = payload.DenyReadPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            denyState.Revoke(previous.Where(path => !applied.Contains(path)));
            denyState.UnionWith(applied);
            WriteDenyBookkeeping(sandboxHome,
                                 DenyBookkeepingJson((principalSid, payload.DenyReadPaths.ToArray())));
            onCompleted?.Invoke();
        }
    }

    [Test]
    public async Task Execute_DenyReadRefreshGateSerializesConcurrentRefreshesOnSharedHome()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home        = new SandboxTestHome();
        var       sandboxHome = home.Components.SandboxHome;
        var       oldEntry    = Path.Combine(sandboxHome, "shared-scan-hit");
        var       entryA      = Path.Combine(sandboxHome, "desktop-store");
        var       entryB      = Path.Combine(sandboxHome, "cli-d");
        var       denyState   = new SimulatedDenyState(oldEntry);
        WriteDenyBookkeeping(sandboxHome, DenyBookkeepingJson((BookkeepingSid, [oldEntry])));

        var aEntered   = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invokerA   = new SimulatedHelperInvoker(sandboxHome, BookkeepingSid, denyState,
                                                    onEntered : () => aEntered.TrySetResult(true),
                                                    onCompleted : () => aCompleted.TrySetResult(true));
        var invokerB   = new SimulatedHelperInvoker(sandboxHome, BookkeepingSid, denyState,
                                                    beforeApply : () => aCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var (backendA, _) = CreateSharedHomeBackend(home.Components,
                                                    ResolveSharedHomePolicy(home.Components, oldEntry, entryA),
                                                    invokerA);
        var (backendB, _) = CreateSharedHomeBackend(home.Components,
                                                    ResolveSharedHomePolicy(home.Components, oldEntry, entryB),
                                                    invokerB);

        // 确定性交错：A 先进入 invoker（已持门），B 随后启动并被门挡住；B 的 invoker 再阻塞到
        // A 的 invoker 完成。无门时 B 会在 A 持久化前读簿记，其旧 payload 将撤销 A 的条目。
        var taskA = backendA.ExecuteAsync(Plan(sandboxHome), new RecordingOutputCapture(), new RecordingOutputCapture(),
                                          CancellationToken.None);
        await aEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var taskB = backendB.ExecuteAsync(Plan(sandboxHome), new RecordingOutputCapture(), new RecordingOutputCapture(),
                                          CancellationToken.None);
        await Task.WhenAll(taskA, taskB);

        // B 的 refresh payload 在门内重读簿记，包含 A 刚持久化的条目；最终簿记与模拟 deny 面
        // 同时保留 old + A + B。
        var payloadB        = TestAssert.Single(invokerB.RefreshPayloads);
        var finalBookkeeping = ReadDenyBookkeepingPaths(sandboxHome, BookkeepingSid);
        Assert.Multiple(() =>
        {
            Assert.That(payloadB.DenyReadPaths, Does.Contain(entryA));
            Assert.That(payloadB.DenyReadPaths, Does.Contain(entryB));
            Assert.That(finalBookkeeping, Does.Contain(entryA));
            Assert.That(finalBookkeeping, Does.Contain(entryB));
            Assert.That(finalBookkeeping, Does.Contain(oldEntry));
            Assert.That(denyState.Snapshot(), Does.Contain(entryA));
            Assert.That(denyState.Snapshot(), Does.Contain(entryB));
            Assert.That(denyState.Snapshot(), Does.Contain(oldEntry));
        });
    }

    [Test]
    public async Task Execute_ConcurrentDenyReadRefreshesOnSharedHome_PreserveBothSidesEntries()
    {
        if (!OperatingSystem.IsWindows()) return;

        // 并发不变量：重复 3 轮、每轮新 home，两端并发 refresh 后簿记与模拟 deny 面都保留
        // 双方条目，两端命令都正常完成（不 fail-closed）。
        for (var round = 0; round < 3; round++)
        {
            using var home        = new SandboxTestHome();
            var       sandboxHome = home.Components.SandboxHome;
            var       entryA      = Path.Combine(sandboxHome, "desktop-store");
            var       entryB      = Path.Combine(sandboxHome, "cli-d");
            var       denyState   = new SimulatedDenyState();
            var       invokerA    = new SimulatedHelperInvoker(sandboxHome, BookkeepingSid, denyState);
            var       invokerB    = new SimulatedHelperInvoker(sandboxHome, BookkeepingSid, denyState);
            var (backendA, _) = CreateSharedHomeBackend(home.Components,
                                                        ResolveSharedHomePolicy(home.Components, entryA),
                                                        invokerA);
            var (backendB, _) = CreateSharedHomeBackend(home.Components,
                                                        ResolveSharedHomePolicy(home.Components, entryB),
                                                        invokerB);

            var results = await Task.WhenAll(
                backendA.ExecuteAsync(Plan(sandboxHome), new RecordingOutputCapture(), new RecordingOutputCapture(),
                                      CancellationToken.None),
                backendB.ExecuteAsync(Plan(sandboxHome), new RecordingOutputCapture(), new RecordingOutputCapture(),
                                      CancellationToken.None));

            var finalBookkeeping = ReadDenyBookkeepingPaths(sandboxHome, BookkeepingSid);
            Assert.Multiple(() =>
            {
                Assert.That(results[0].ExitCode, Is.EqualTo(0));
                Assert.That(results[1].ExitCode, Is.EqualTo(0));
                Assert.That(finalBookkeeping, Does.Contain(entryA));
                Assert.That(finalBookkeeping, Does.Contain(entryB));
                Assert.That(denyState.Snapshot(), Does.Contain(entryA));
                Assert.That(denyState.Snapshot(), Does.Contain(entryB));
            });
        }
    }

    [Test]
    public async Task Execute_ConcurrentFirstWorkspaceResolutionsOnSharedHome_AgreeOnOneSidAndPreserveExistingEntries()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home        = new SandboxTestHome();
        var       sandboxHome = home.Components.SandboxHome;
        // 预置既有映射：并发首次解析的整表写回不得把它覆盖丢失。
        SeedCapabilitySidTable(sandboxHome);

        var spawnA = new TaskCompletionSource<RunnerSpawnRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var spawnB = new TaskCompletionSource<RunnerSpawnRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invokerA = new SimulatedHelperInvoker(sandboxHome, BookkeepingSid, new SimulatedDenyState());
        var invokerB = new SimulatedHelperInvoker(sandboxHome, BookkeepingSid, new SimulatedDenyState());
        var (backendA, launcherA) = CreateSharedHomeBackend(home.Components,
                                                            ResolveSharedHomePolicy(home.Components), invokerA,
                                                            runnerBody : SpawnCapturingRunner(spawnA));
        var (backendB, launcherB) = CreateSharedHomeBackend(home.Components,
                                                            ResolveSharedHomePolicy(home.Components), invokerB,
                                                            runnerBody : SpawnCapturingRunner(spawnB));

        // 同根并发首次解析：两端 policy 与 cwd 完全一致，修复前各自 miss → 随机 SID → 整表写回，
        // 出现共享冲突或丢更新；修复后门内串行，后到者读到先到者写入的同一映射。
        var results = await Task.WhenAll(
            backendA.ExecuteAsync(Plan(sandboxHome), new RecordingOutputCapture(), new RecordingOutputCapture(),
                                  CancellationToken.None),
            backendB.ExecuteAsync(Plan(sandboxHome), new RecordingOutputCapture(), new RecordingOutputCapture(),
                                  CancellationToken.None));

        var sidA   = (await spawnA.Task.WaitAsync(TimeSpan.FromSeconds(10))).CapabilitySids[0];
        var sidB   = (await spawnB.Task.WaitAsync(TimeSpan.FromSeconds(10))).CapabilitySids[0];
        var table  = ReadCapabilitySidTable(sandboxHome);
        Assert.Multiple(() =>
        {
            Assert.That(results[0].ExitCode, Is.EqualTo(0));
            Assert.That(results[1].ExitCode, Is.EqualTo(0));
            Assert.That(launcherA.LastRequest, Is.Not.Null);
            Assert.That(launcherB.LastRequest, Is.Not.Null);
            // 同根收敛到同一 SID；表内该键与两侧 token 携带一致；预置映射保留。
            Assert.That(sidB, Is.EqualTo(sidA));
            Assert.That(table.WorkspaceByCwd[SandboxCapabilitySidStore.CanonicalRootKey(sandboxHome)],
                        Is.EqualTo(sidA));
            Assert.That(table.WritableRootByPath["c:/outside/pre-existing"], Is.EqualTo("S-1-5-21-33-33-33-33"));
        });
    }

    [Test]
    public async Task Execute_ConcurrentDistinctRootResolutionsOnSharedHome_PreserveBothSidesMappings()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home        = new SandboxTestHome();
        var       sandboxHome = home.Components.SandboxHome;
        SeedCapabilitySidTable(sandboxHome);

        var spawnA = new TaskCompletionSource<RunnerSpawnRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var spawnB = new TaskCompletionSource<RunnerSpawnRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invokerA = new SimulatedHelperInvoker(sandboxHome, BookkeepingSid, new SimulatedDenyState());
        var invokerB = new SimulatedHelperInvoker(sandboxHome, BookkeepingSid, new SimulatedDenyState());
        var (backendA, _) = CreateSharedHomeBackend(home.Components,
                                                    ResolveSharedHomePolicy(home.Components), invokerA,
                                                    runnerBody : SpawnCapturingRunner(spawnA));
        // B 从 home 的子目录运行：工作区写根 ≠ cwd，走 writable_root_by_path 表——两键异表，
        // 修复前整表写回互相覆盖或共享冲突；修复后门内串行，两个映射并存。
        var (backendB, _) = CreateSharedHomeBackend(home.Components,
                                                    ResolveSharedHomePolicy(home.Components), invokerB,
                                                    runnerBody : SpawnCapturingRunner(spawnB));

        var results = await Task.WhenAll(
            backendA.ExecuteAsync(Plan(sandboxHome), new RecordingOutputCapture(), new RecordingOutputCapture(),
                                  CancellationToken.None),
            backendB.ExecuteAsync(Plan(Path.Combine(sandboxHome, "cli-d")), new RecordingOutputCapture(),
                                  new RecordingOutputCapture(), CancellationToken.None));

        var sidA  = (await spawnA.Task.WaitAsync(TimeSpan.FromSeconds(10))).CapabilitySids[0];
        var sidB  = (await spawnB.Task.WaitAsync(TimeSpan.FromSeconds(10))).CapabilitySids[0];
        var table = ReadCapabilitySidTable(sandboxHome);
        Assert.Multiple(() =>
        {
            Assert.That(results[0].ExitCode, Is.EqualTo(0));
            Assert.That(results[1].ExitCode, Is.EqualTo(0));
            // 异根两端各得独立随机 SID（不同键），两侧映射都保留在各自表中；预置映射保留。
            Assert.That(sidB, Is.Not.EqualTo(sidA));
            Assert.That(table.WorkspaceByCwd[SandboxCapabilitySidStore.CanonicalRootKey(sandboxHome)],
                        Is.EqualTo(sidA));
            Assert.That(table.WritableRootByPath[SandboxCapabilitySidStore.CanonicalRootKey(sandboxHome)],
                        Is.EqualTo(sidB));
            Assert.That(table.WritableRootByPath["c:/outside/pre-existing"], Is.EqualTo("S-1-5-21-33-33-33-33"));
        });
    }

    [Test]
    public async Task Execute_ResolveCapabilitySidsHoldsHomeGateForResolutionOnly()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home        = new SandboxTestHome();
        var       sandboxHome = home.Components.SandboxHome;
        var       trace       = new List<string>();
        var       gate        = new ScriptedDenyReadGate(trace);
        var invoker = new SimulatedHelperInvoker(sandboxHome, BookkeepingSid, new SimulatedDenyState(),
                                                 onEntered   : () => trace.Add("refresh-entered"),
                                                 onCompleted : () => trace.Add("refresh-completed"));
        var launcher = new TracingRunnerLauncher(new FakeSandboxRunnerLauncher { RunnerBody = NormalRunner }, trace);
        var backend  = new WindowsSandboxBackend(
                                                home.Components, ResolveSharedHomePolicy(home.Components),
                                                new Dictionary<string, string> { ["TEMP"] = sandboxHome }, null,
                                                invoker, new FakeCredentialSource(),
                                                desktopFactory : new FakeDesktopFactory(), runnerLauncher : launcher,
                                                limits : TestLimits, allowNullDeviceAccess : _ => trace.Add("resolve-sids"),
                                                validateWriteAcls         : (_, _) => Task.CompletedTask,
                                                validateReadRootReadiness : (_, _) => Task.CompletedTask,
                                                denyReadRefreshGate       : gate);

        var result = await backend.ExecuteAsync(Plan(sandboxHome), new RecordingOutputCapture(),
                                                new RecordingOutputCapture(), CancellationToken.None);

        // 门序证明：refresh 在第一次持门窗口内；SID 解析（以 NUL ACE 回调为观测点）严格落在
        // 第二次 acquire/release 窗口内；runner 启动在门释放之后——解析绝不包住启动。
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            Assert.That(trace, Is.EqualTo(["deny-gate-acquire", "refresh-entered", "refresh-completed",
                                           "deny-gate-release", "deny-gate-acquire", "resolve-sids",
                                           "deny-gate-release", "launch"]));
            Assert.That(gate.AcquireCalls, Is.EqualTo(2));
            Assert.That(gate.LastHome, Is.EqualTo(sandboxHome));
        });
    }

    [Test]
    public async Task Execute_CapabilityResolutionGateTimeout_FailsClosedWithoutStartingCommand()
    {
        if (!OperatingSystem.IsWindows()) return;

        var gate  = new ScriptedDenyReadGate();
        var calls = 0;
        // 第一次获取（preflight refresh）成功放行，第二次（capability 解析）超时返回 null。
        gate.Script = () => ++calls == 1
            ? Task.FromResult<ISandboxDenyReadRefreshGateHandle?>(new ScriptedDenyGateHandle(gate))
            : Task.FromResult<ISandboxDenyReadRefreshGateHandle?>(null);
        var (backend, setup, _, launcher, home) =
            CreateBackend(NormalRunner, denyReadRefreshGate : gate);
        using var homeScope = home;

        var error = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() =>
            backend.ExecuteAsync(Plan(homeScope.Components.SandboxHome), new RecordingOutputCapture(),
                                 new RecordingOutputCapture(), CancellationToken.None));

        // preflight refresh 已完成（第一次获取成功），门在解析阶段限时失败：消息含门名与 home，
        // 命令未启动；门句柄仅经第一次获取的 Dispose 归还，第二次获取无句柄可释放。
        Assert.Multiple(() =>
        {
            Assert.That(setup.RefreshPayloads, Has.Count.EqualTo(1));
            TestAssert.Contains("deny-read refresh gate", error!.Message, StringComparison.Ordinal);
            TestAssert.Contains(homeScope.Components.SandboxHome, error.Message, StringComparison.Ordinal);
            TestAssert.Contains("capability SIDs", error.Message, StringComparison.Ordinal);
            Assert.That(launcher.LastRequest, Is.Null);
            Assert.That(gate.Trace, Is.EqualTo(["deny-gate-acquire", "deny-gate-release", "deny-gate-acquire"]));
            Assert.That(gate.LastHandle!.DisposeCalls, Is.EqualTo(1));
        });
    }

    /// <summary>把 launch 事件写入共享时序的转发启动器：其余行为委托内部 fake。 A forwarding launcher appending the launch event to the shared trace; everything else delegates to the inner fake.</summary>
    private sealed class TracingRunnerLauncher(ISandboxRunnerLauncher inner, List<string> trace) : ISandboxRunnerLauncher
    {
        public async Task<SandboxRunnerConnection> LaunchAsync(RunnerLaunchRequest request,
                                                               CancellationToken cancellationToken)
        {
            trace.Add("launch");
            return await inner.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>捕获 spawn_request 的 runner body 工厂：记录 token 携带的 capability SID 供断言。 A capturing runner-body factory: records the token-carried capability SIDs for assertions.</summary>
    private static Func<Stream, Stream, CancellationToken, Task> SpawnCapturingRunner(
        TaskCompletionSource<RunnerSpawnRequest> spawnSeen)
    {
        return async (downstream, upstream, end) =>
        {
            var frame = await RunnerFrameCodec.ReadFrameAsync(downstream, end);
            spawnSeen.TrySetResult(RunnerFrameCodec.ParsePayload<RunnerSpawnRequest>(frame!));
            await RunnerFrameCodec.WriteFrameAsync(upstream, "spawn_ready", new RunnerSpawnReady { ProcessId = 1 }, end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "exit",
                                                   new RunnerExitPayload { ExitCode = 0, TimedOut = false }, end);
        };
    }

    /// <summary>把 cap_sid 表预置为空映射外加一条既有 writable_root_by_path 映射。 Seeds the cap_sid table with empty maps plus one pre-existing writable_root_by_path mapping.</summary>
    private static void SeedCapabilitySidTable(string sandboxHome)
    {
        File.WriteAllText(Path.Combine(sandboxHome, "cap_sid"),
                          """{"workspace":"S-1-5-21-11-11-11-11","readonly":"S-1-5-21-22-22-22-22","workspace_by_cwd":{},"writable_root_by_path":{"c:/outside/pre-existing":"S-1-5-21-33-33-33-33"}}""");
    }

    /// <summary>解析 cap_sid 表的两张映射；键为存盘的规范化小写形态。 Parses both mappings of the cap_sid table; keys use the stored canonical lowercase form.</summary>
    private static (Dictionary<string, string> WorkspaceByCwd, Dictionary<string, string> WritableRootByPath)
        ReadCapabilitySidTable(string sandboxHome)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(sandboxHome, "cap_sid")));
        static Dictionary<string, string> ReadTable(JsonElement element) =>
            element.EnumerateObject()
                   .ToDictionary(property => property.Name, property => property.Value.GetString()!,
                                 StringComparer.Ordinal);
        return (ReadTable(document.RootElement.GetProperty("workspace_by_cwd")),
                ReadTable(document.RootElement.GetProperty("writable_root_by_path")));
    }

    [Test]
    public async Task Execute_DenyReadRefreshGateHeldByConcurrentRefresh_FailsClosedWithoutStartingCommand()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home        = new SandboxTestHome();
        var       sandboxHome = home.Components.SandboxHome;
        var       entryA      = Path.Combine(sandboxHome, "desktop-store");
        var       entryB      = Path.Combine(sandboxHome, "cli-d");
        var       denyState   = new SimulatedDenyState();
        var enteredA = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invokerA = new SimulatedHelperInvoker(sandboxHome, BookkeepingSid, denyState,
                                                  onEntered : () => enteredA.TrySetResult(true),
                                                  beforeApply : () => releaseA.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var invokerB = new SimulatedHelperInvoker(sandboxHome, BookkeepingSid, denyState);
        var (backendA, launcherA) = CreateSharedHomeBackend(home.Components,
                                                            ResolveSharedHomePolicy(home.Components, entryA),
                                                            invokerA);
        var (backendB, launcherB) = CreateSharedHomeBackend(home.Components,
                                                            ResolveSharedHomePolicy(home.Components, entryB),
                                                            invokerB,
                                                            limits : TestLimits with
                                                            {
                                                                DenyReadRefreshGateTimeout = TimeSpan.FromSeconds(1)
                                                            });

        // A 持门阻塞在 refresh 中；B 注入短门期限并发执行 → 门等待超时 fail-closed。
        var taskA = backendA.ExecuteAsync(Plan(sandboxHome), new RecordingOutputCapture(), new RecordingOutputCapture(),
                                          CancellationToken.None);
        await enteredA.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var error = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() =>
            backendB.ExecuteAsync(Plan(sandboxHome), new RecordingOutputCapture(), new RecordingOutputCapture(),
                                  CancellationToken.None));

        Assert.Multiple(() =>
        {
            TestAssert.Contains("deny-read refresh gate", error.Message, StringComparison.Ordinal);
            TestAssert.Contains(sandboxHome, error.Message, StringComparison.Ordinal);
            // B 的 invoker 未被调用：命令未启动（runner 同样未触达）。
            Assert.That(invokerB.RefreshPayloads, Is.Empty);
            Assert.That(launcherB.LastRequest, Is.Null);
        });

        // 放行 A 完成：门的串行化不阻塞先到者，A 正常结束。
        releaseA.TrySetResult(true);
        var result = await taskA.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(result.ExitCode, Is.EqualTo(0));
        Assert.That(launcherA.LastRequest, Is.Not.Null);
    }

    [Test]
    public async Task Execute_PreflightReadinessWaitsForGateDuringConcurrentCapSidWriteWindow()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home        = new SandboxTestHome();
        var       sandboxHome = home.Components.SandboxHome;
        var       capSidPath  = Path.Combine(sandboxHome, "cap_sid");
        // 撕裂表模拟门内整表 WriteAllText 的中间态：门外读会判成无效 JSON → "not ready" 误报。
        var completeTable = File.ReadAllText(capSidPath);
        const string tornTable = """{"workspace":"S-1-5-21-11""";

        // A 持同一隔离 home 的真实刷新门并控制 cap_sid 写入窗口；B 从正常 backend preflight 入口进入，
        // 其门是带进入信号的门装饰器——A 自己持门不经过它，进入信号只会由 B 置位。
        var gate  = new GlobalDenyReadRefreshGate();
        var heldA = await gate.TryAcquireAsync(sandboxHome, TimeSpan.FromSeconds(5), CancellationToken.None)
                              .WaitAsync(TimeSpan.FromSeconds(10));
        TestAssert.NotNull(heldA);
        var setup     = new FakeSetupInvoker();
        var gateProbe = new GateEntrySignalingDenyReadRefreshGate(gate);
        var (backendB, launcherB) = CreateSharedHomeBackend(home.Components,
                                                            ResolveSharedHomePolicy(home.Components), setup,
                                                            denyReadRefreshGate : gateProbe);
        Task<ProcessExecutionResult>? taskB   = null;
        string?                       failure = null;
        var                           failed  = false;
        try
        {
            File.WriteAllText(capSidPath, tornTable);
            taskB = backendB.ExecuteAsync(Plan(sandboxHome), new RecordingOutputCapture(),
                                          new RecordingOutputCapture(), CancellationToken.None);
            // 确定性信号：等到 B 实际进入门等待（撞上 A 持有的门）后才做短负等待；修复前 B 在门外
            // 立即读到撕裂表，进入门等待之前就以 "The Windows sandbox is not ready" 失败结束。
            failure = await gateProbe.ObserveGateWaitAsync(taskB, () => Assert.That(launcherB.LastRequest, Is.Null))
                                     .ConfigureAwait(false);
        }
        catch (Exception)
        {
            failed = true;
            throw;
        }
        finally
        {
            // A 写回完整表并释放门：B 随后在门内读到完整表并正常完成；收尾等待只观察，不覆盖原始失败。
            File.WriteAllText(capSidPath, completeTable);
            heldA?.Dispose();
            if (taskB is not null)
                failure = await gateProbe.DrainAfterReleaseAsync(taskB, failure, failure is not null || failed)
                                         .ConfigureAwait(false);
        }

        if (failure is not null)
            Assert.Fail("Preflight must wait for the deny-read refresh gate instead of reading the torn cap_sid " +
                        "outside the gate (pre-fix: the task faults with 'The Windows sandbox is not ready')." +
                        Environment.NewLine + failure);

        // B 在门内读到 A 写回的完整表后正常完成，全程无 "not ready" 异常。
        var result = await taskB!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            Assert.That(launcherB.LastRequest, Is.Not.Null);
            Assert.That(setup.RefreshPayloads, Has.Count.EqualTo(1));
        });
    }

    // ---------------------------------------------------------------- 拒读刷新门（退出未知的门保留）

    [Test]
    public async Task Execute_HelperExitUncertainDuringRefresh_HoldsGateUntilExitConfirmedWithoutStartingCommand()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new ScriptedDenyReadGate();
        var (backend, setup, _, launcher, home) =
            CreateBackend(NormalRunner,
                          refreshError : new SandboxRefreshExitUncertainException(
                              "The sandbox setup helper did not finish refreshing ACLs within 5s. The helper exit " +
                              "state is unknown; it may still be writing deny-read bookkeeping.",
                              pending.Task, null),
                          denyReadRefreshGate : gate);
        using var homeScope = home;

        var error = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() => backend.ExecuteAsync(
            Plan(home.Components.SandboxHome), new RecordingOutputCapture(), new RecordingOutputCapture(),
            CancellationToken.None));

        // 退出未知：超时语义经通用失败路径包装，命令未启动；门经确认释放被调而非立即归还
        // （Dispose 未被调），确认任务随异常移交门侧。
        var handle = TestAssert.NotNull(gate.LastHandle);
        Assert.Multiple(() =>
        {
            TestAssert.Contains("ACL refresh failed", error!.Message, StringComparison.Ordinal);
            TestAssert.Contains("exit state is unknown", error.Message, StringComparison.Ordinal);
            Assert.That(launcher.LastRequest, Is.Null);
            Assert.That(setup.RefreshPayloads, Has.Count.EqualTo(1));
            Assert.That(handle.ConfirmationTasks, Has.Count.EqualTo(1));
            Assert.That(handle.DisposeCalls, Is.EqualTo(0));
        });

        // 模拟确认完成后（确认任务完成 true），下一条命令可正常走门并完成。
        setup.ThrowOnRefresh = null;
        pending.TrySetResult(true);
        var result = await backend.ExecuteAsync(Plan(home.Components.SandboxHome), new RecordingOutputCapture(),
                                                new RecordingOutputCapture(), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            Assert.That(launcher.LastRequest, Is.Not.Null);
            // 第二条命令按新契约各持门两次：preflight refresh 与其后的 capability SID 解析。
            Assert.That(gate.AcquireCalls, Is.EqualTo(3));
            Assert.That(gate.Trace, Is.EqualTo(["deny-gate-acquire",
                                                "deny-gate-release-when-confirmed",
                                                "deny-gate-acquire",
                                                "deny-gate-release",
                                                "deny-gate-acquire",
                                                "deny-gate-release"]));
        });
    }

    [Test]
    public async Task Execute_HelperExitUncertainCancellationDuringRefresh_HoldsGateAndStaysACancellation()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new ScriptedDenyReadGate();
        var (backend, setup, _, launcher, home) =
            CreateBackend(NormalRunner,
                          refreshError : new SandboxRefreshExitUncertainCancellationException(
                              "The sandbox setup helper refresh was cancelled. The helper exit state is unknown; " +
                              "it may still be writing deny-read bookkeeping.",
                              CancellationToken.None, pending.Task),
                          denyReadRefreshGate : gate);
        using var homeScope = home;

        var error = await Assert.CatchAsync<OperationCanceledException>(() => backend.ExecuteAsync(
            Plan(home.Components.SandboxHome), new RecordingOutputCapture(), new RecordingOutputCapture(),
            CancellationToken.None));

        // 上层仍按取消处理（不确定取消子类原样上抛，不包装为启动失败）；门经确认释放被调，
        // Dispose 未被调，命令未启动。
        var handle = TestAssert.NotNull(gate.LastHandle);
        Assert.Multiple(() =>
        {
            Assert.That(error, Is.InstanceOf<SandboxRefreshExitUncertainCancellationException>());
            Assert.That(launcher.LastRequest, Is.Null);
            Assert.That(setup.RefreshPayloads, Has.Count.EqualTo(1));
            Assert.That(handle.ConfirmationTasks, Has.Count.EqualTo(1));
            Assert.That(handle.DisposeCalls, Is.EqualTo(0));
        });
        pending.TrySetResult(true);
    }
}
